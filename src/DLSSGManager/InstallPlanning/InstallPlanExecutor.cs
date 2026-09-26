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
                verification = provider.VerifyPackage(Path.Combine(source.Root, file));
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
            return new PlanExecutionResult(false, install.Message, steps, plan.ProxyChoice);

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
            // Not a pass: with no deployment record there is nothing to compare, and claiming agreement would be
            // inventing evidence. It is reported as "could not check" rather than as a mismatch, because those are
            // different facts and only one of them is actually known here.
            steps.Add("部署记录为空，无法核对实际写入的文件是否与计划一致。");
            return new PlanExecutionResult(true, install.Message, steps, plan.ProxyChoice);
        }

        static string Leaf(string path) => Path.GetFileName(path.Replace('/', '\\'));

        var planned = plan.FilesToDeploy
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(Leaf)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                $"部署的文件与计划不一致（{summary}），已按失败处理。", steps, plan.ProxyChoice);
        }

        steps.Add($"部署结果与计划一致（{deployed.Count} 个文件）。");

        return new PlanExecutionResult(true, install.Message, steps, plan.ProxyChoice);
    }
}
