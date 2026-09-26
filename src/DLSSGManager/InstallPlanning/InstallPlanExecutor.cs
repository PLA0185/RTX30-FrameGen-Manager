using System.IO;
using DLSSGManager.Compatibility;
using DLSSGManager.Providers;

namespace DLSSGManager.InstallPlanning;

/// <summary>What actually happened when a plan was executed.</summary>
public sealed record PlanExecutionResult(
    bool Ok,
    string Message,
    IReadOnlyList<string> Steps,
    string? ProxyUsed = null)
{
    /// <summary>
    /// Whether the provider actually put any file on disk, regardless of what <see cref="Ok"/> ended up being.
    ///
    /// <para>This exists because a failed install is not the same thing as an install that wrote nothing. The
    /// workflow used to set its own <c>filesWritten</c> flag only after a success: when the provider had already
    /// written files and the consistency check then failed, the workflow believed nothing had been written and
    /// skipped the rollback — leaving the game directory modified. The two facts are now reported separately.</para>
    /// </summary>
    public bool FilesWereWritten { get; init; }

    /// <summary>Whether the caller must restore: files went to disk and the run did not succeed.</summary>
    public bool RollbackRequired => FilesWereWritten && !Ok;

    public static PlanExecutionResult Refused(string message, IReadOnlyList<string> steps) =>
        new(false, message, steps);
}

/// <summary>
/// Executes an <see cref="InstallPlan"/> such that the plan's own choices decide what happens.
///
/// <para><b>Why this type exists.</b> The workflow used to build a plan and then install through a call that
/// worked everything out for itself — so the plan's proxy entry, install mode and ASI strategy were shown to
/// the user and then ignored. Two sources of truth for one decision is how a plan and reality drift apart,
/// and the drift is invisible: the install succeeds, just not the way it was described.</para>
///
/// <para><b>It refuses rather than degrades.</b> When a plan names a loader entry the provider cannot honour,
/// or requires a payload file that is not there, execution stops with a reason. Installing into some other
/// entry, or quietly turning an ASI install into a plain proxy install, would leave the user with something
/// other than what they approved — and they would have no way to tell.</para>
/// </summary>
public static class InstallPlanExecutor
{
    /// <param name="provider">Whose <c>Install</c> will do the work.</param>
    /// <param name="plan">The decisions. Read, never recomputed.</param>
    /// <param name="game">Target game. Its entry preference is set from the plan.</param>
    /// <param name="source">Payload folder the plan's file list is checked against.</param>
    /// <param name="allowProtected">Whether a protected (anti-cheat) game may be installed into.</param>
    public static PlanExecutionResult Execute(
        IPatchProvider provider,
        InstallPlan plan,
        GameEntry game,
        ModSource source,
        bool allowProtected = false)
    {
        var steps = new List<string>();

        if (!plan.CanExecute)
            return PlanExecutionResult.Refused($"计划状态为 {plan.Status}，不执行。", steps);

        // ── 1. Every file the plan requires must actually be in the payload. A plan that lists files the
        //       provider cannot supply describes an installation that cannot exist.
        //
        //       The proxy is the exception, and skipping it is not a loophole: it comes from the mod source rather
        //       than the payload folder, so requiring it there would refuse every correctly-planned install. Which
        //       proxy it is has already been decided above, and the payload check that follows covers the rest.
        var missing = new List<string>();

        // 这里刻意用**扫描**集合（`KnownProxyNames`）而不是可部署集合：本步问的是「这个名字是不是一个代理
        // 入口」，而 `winhttp.dll` 确实是一个代理入口（0.3.0 起不再部署，但旧安装里可能残留），它若出现在
        // 计划里也不该被要求「存在于 payload」—— 代理由 mod source 提供，不从 payload 取。
        //
        // **注意：这一步与下面第 4 步（校验代理）用的判据不同，那是有意的。** 第 4 步问「该到哪里找这个
        // 文件」，那里必须用可部署集合（`ProxyCandidates`），因为路径解析要按项目布局走（非 `version.dll`
        // 的入口在 `altnative/`）。本项目已经因为「同一个概念在两处用了不同集合」出现过四次真实缺陷 ——
        // 所以这两处的差异必须写在这里，而不是留给人猜。
        static bool IsProxyName(string name) =>
            ModSource.KnownProxyNames.Contains(Path.GetFileName(name), StringComparer.OrdinalIgnoreCase);

        foreach (var required in plan.FilesToDeploy)
        {
            if (string.IsNullOrWhiteSpace(required)) continue;
            if (IsProxyName(required)) continue;

            try
            {
                if (!File.Exists(Path.Combine(source.Root, required))) missing.Add(required);
            }
            catch
            {
                missing.Add(required);
            }
        }

        if (missing.Count > 0)
        {
            steps.Add($"payload 缺少：{string.Join("、", missing)}");
            return PlanExecutionResult.Refused(
                $"计划要求的 {missing.Count} 个文件不在 payload 中，已拒绝安装。", steps);
        }

        steps.Add("payload 已包含计划要求的全部文件（代理由 mod source 提供，不在其中）。");

        // ── 2. The chosen entry is what gets installed into — or nothing does.
        //
        // **这道守卫目前永不触发**：`plan.Mode` 在生产路径上恒为 `Unknown`（`ConfigurationRequest`
        // 有一个 `InstallMode` 参数，但两个界面构造点都不传它）。**方向是安全的** —— 恒不触发意味着
        // 它不会拒绝任何安装，而「计划没指定入口时必须拒绝」由下面 `SupportsProxyChoice` 那段守住。
        //
        // **刻意保留而不是删除**：一旦有人把 `InstallMode` 接上线（那是个合理的后续功能），
        // 这道守卫就是「DirectProxy 必须给出入口」的唯一检查点。删掉它，那个功能上线时会带着一个缺口。
        if (plan.Mode == InstallMode.DirectProxy && string.IsNullOrWhiteSpace(plan.ProxyChoice))
            return PlanExecutionResult.Refused("计划为 DirectProxy 却没有指定代理入口，已拒绝安装。", steps);

        if (!string.IsNullOrWhiteSpace(plan.ProxyChoice))
        {
            if (!provider.SupportsProxyChoice)
            {
                steps.Add($"{provider.Id} 未声明支持指定入口。");
                return PlanExecutionResult.Refused(
                    $"计划指定入口「{plan.ProxyChoice}」，但 {provider.Id} 无法按指定入口安装。" +
                    "静默改用其他入口会与实际安装不符，已拒绝。", steps);
            }

            game.PreferredProxy = plan.ProxyChoice!;
            steps.Add($"按计划使用入口「{plan.ProxyChoice}」。");
        }

        // ── 3. An ASI plan must not quietly become a DirectProxy install.
        if (!string.IsNullOrWhiteSpace(plan.AsiChoice))
        {
            if (!provider.SupportsAsiStrategy)
            {
                steps.Add($"{provider.Id} 未声明支持 ASI 策略。");
                return PlanExecutionResult.Refused(
                    $"计划要求 ASI 策略「{plan.AsiChoice}」，但 {provider.Id} 无法实现。" +
                    "降级为 DirectProxy 会改变安装形态，已拒绝。", steps);
            }

            steps.Add($"按计划使用 ASI 策略「{plan.AsiChoice}」。");
        }

        // ── 4. Every payload file must pass the provider's own verification, before anything is written.
        //       An Intact or unsigned file is acceptable — unsigned payloads are normal in this ecosystem —
        //       but a file whose signature no longer matches its contents is not, and neither is one the
        //       provider cannot classify. The distinction lives in the provider; this just refuses to skip it.
        foreach (var file in plan.FilesToDeploy)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;

            PackageVerification verification;
            try
            {
                // §17 P0：代理入口**不一定在 payload 根目录** —— 项目规定非 version.dll 的入口位于
                // altnative/，ModSource.ResolveDllPath 就是这条规定的实现，而 DeploymentService 用的正是它。
                // 这里曾经用 Path.Combine(source.Root, file) 扁平拼接，于是**任何非 version.dll 的入口**都被
                // 指到一个不存在的路径 → 「校验不过」→ 拒绝安装。
                //
                // 触发条件恰恰是工具最该帮上忙的场景：游戏目录里的 version.dll 被游戏自带或其它 mod 占用，
                // 扫描器按设计换用下一个空闲入口（winmm.dll / dinput8.dll …），然后装不上。
                //
                // 而被 901 项全绿掩盖的原因也很具体：替身 RecordingProvider.VerifyPackage 恒 Accepted 且
                // 不看文件是否存在，且测试的 payload 清单恰好只写 version.dll —— 输入里永远不会出现第二个入口名。
                //
                // 非代理文件（INI 等）本来就在根目录，不能套用 DllPath（那会把它解析到 altnative/）。
                var target = ModSource.ProxyCandidates.Contains(file, StringComparer.OrdinalIgnoreCase)
                    ? source.DllPath(file)
                    : Path.Combine(source.Root, file);

                verification = provider.VerifyPackage(target);
            }
            catch (Exception ex)
            {
                steps.Add($"校验「{file}」时出错：{ex.Message}");
                return PlanExecutionResult.Refused($"无法校验「{file}」，已拒绝安装。", steps);
            }

            if (!verification.Accepted)
            {
                steps.Add($"「{file}」未通过校验：{verification.Message}");
                return PlanExecutionResult.Refused($"payload 中的「{file}」未通过校验，已拒绝安装。", steps);
            }

            steps.Add($"「{file}」校验通过（{verification.Signature}）。");
        }

        // ── 5. Install, using the values the plan supplied.
        var install = provider.Install(game, source, allowProtected);
        steps.Add(install.Message);

        if (!install.Ok)
            // Install() 失败 ≠ 什么都没写 —— 但**也不等于写了**。
            //
            // 这里曾经无条件 `FilesWereWritten = true`（注释自陈「Assume files may be there」）。而 `Deploy`
            // 有多条**零写入**的早期失败路径：游戏正在运行、目录不可写、内核反作弊未授权、源目录无效、
            // 代理名无效。那些情况下调用方会拿**上一次**的部署记录去回滚 —— 把用户**原本正常的安装**整个
            // 卸载掉，报告却写「已回滚」。
            //
            // 「宁可多回滚一次」这条推理在这里恰恰是错的：多回滚一次删掉的是**用户能用的东西**，不是
            // 我们自己的半成品。现在由 `Deploy` 如实回答（`OpResult.FilesWritten`：只有进入写入事务之后
            // 才为 true），假设不再代替回答。
            return new PlanExecutionResult(false, install.Message, steps, plan.ProxyChoice)
            {
                FilesWereWritten = install.FilesWritten,
            };

        // Record which provider did this, so a later restore uses the same one. Without it the restore has to
        // guess, and guessing a provider is how the wrong files end up being removed.
        if (game.Deployment is not null)
            game.Deployment.ProviderId = plan.ProviderId;

        // ── 6. The plan decides which files may exist, so what was actually deployed is checked against it.
        //       A file the plan never named is not a detail: it means the plan and the deployment disagree about
        //       what this install is, and reporting success would quietly turn the plan into advice.
        var deployed = game.Deployment?.Files;

        if (deployed is null || deployed.Count == 0)
        {
            // Not a pass — and no longer reported as one. This comment used to say "not a pass" while the code
            // returned success. The project's promise is that the plan equals what was written, so being unable to
            // compare is a failure, not a note. Install() already returned Ok, so files may well be on disk, which is
            // why FilesWereWritten is set: the caller has to restore.
            steps.Add("部署记录为空，无法核对实际写入的文件是否与计划一致，已按失败处理。");

            return new PlanExecutionResult(false,
                "部署记录缺失，无法核对计划与实际写入是否一致，已按失败处理。", steps, plan.ProxyChoice)
            {
                FilesWereWritten = true,
            };
        }

        static string Leaf(string path) => Path.GetFileName(path.Replace('/', '\\'));

        // 用 FilesToDeploy 而不是 PlannedFiles 本身：两者由 InstallPlanner 的同一次 BuildPlannedFiles 调用
        // 产生（§15 的「同源派生」），必然一致；而测试手工构造的 plan 只填 FilesToDeploy，读派生视图对它们
        // 同样有效。**这个等价不是巧合，是 §15 那条结构性约束的直接结果。**
        var planned = plan.FilesToDeploy
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(Leaf)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 两个方向都要查。只看「部署了计划外的」会漏掉「计划里说了却没写」—— 后者同样违反
        // planned == deployed，而且它恰好是最难发现的那种：安装悄悄少写了东西，报告却一切正常。
        var unexpected = deployed.Where(f => !planned.Contains(Leaf(f.FileName)))
            .Select(f => f.FileName).ToList();

        var notDeployed = planned.Where(f => !deployed.Any(d => Leaf(d.FileName) == f)).ToList();

        if (unexpected.Count > 0 || notDeployed.Count > 0)
        {
            var detail = new List<string>();
            if (unexpected.Count > 0) detail.Add($"计划外的文件：{string.Join("、", unexpected)}");
            if (notDeployed.Count > 0) detail.Add($"计划中未部署的文件：{string.Join("、", notDeployed)}");

            var summary = string.Join("；", detail);
            steps.Add("部署结果与计划不一致 —— " + summary);

            return new PlanExecutionResult(false,
                $"部署的文件与计划不一致（{summary}），已按失败处理。", steps, plan.ProxyChoice)
            {
                FilesWereWritten = true,
            };
        }

        steps.Add($"部署结果与计划一致（{deployed.Count} 个文件）。");

        return new PlanExecutionResult(true, install.Message, steps, plan.ProxyChoice)
        {
            FilesWereWritten = true,
        };
    }
}
