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
        var missing = new List<string>();
        foreach (var required in plan.FilesToDeploy)
        {
            if (string.IsNullOrWhiteSpace(required)) continue;

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

        steps.Add($"payload 已包含计划要求的 {plan.FilesToDeploy.Count} 个文件。");

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

        // ── 4. Install, using the values the plan supplied.
        var install = provider.Install(game, source, allowProtected);
        steps.Add(install.Message);

        return new PlanExecutionResult(install.Ok, install.Message, steps, plan.ProxyChoice);
    }
}
