using DLSSGManager.GameDetection;
using DLSSGManager.Compatibility;
using DLSSGManager.InstallPlanning;
using DLSSGManager.Providers;
using DLSSGManager.Update;

namespace DLSSGManager.Orchestration;

/// <summary>Everything one configuration run needs, in the shape the interface can supply it.</summary>
public sealed record ConfigurationRequest(
    GameEntry Game,
    IPatchProvider Provider,
    string ProviderVersion,
    string? PayloadDirectory,
    string? GpuName = null,
    string? DriverVersion = null,
    StoreKind Store = StoreKind.Unknown,
    InstallMode InstallMode = InstallMode.Unknown,
    GraphicsApi UserApi = GraphicsApi.Unknown,
    string? UserRendererChoice = null,
    bool AllowProtected = false,
    bool HasKernelAntiCheat = false,
    bool UserConfirmedUnverified = false,

    /// <summary>
    /// Which NVIDIA Profile settings this run may write, or null for none.
    ///
    /// <para>The workflow turns these into typed writes through
    /// <see cref="SmoothMotionSettings.EnableWrites"/> — it never invents values, and a setting whose values are
    /// not established is dropped rather than written as a guess. Without this the interface could complete a
    /// run that installed files and left the driver untouched.</para>
    /// </summary>
    IReadOnlyList<NvidiaProfile.ProfileSetting>? ProfileSettings = null,

    /// <summary>
    /// Build the plan and stop, without writing anything. See <c>WorkflowRequest.PreviewOnly</c>.
    /// </summary>
    bool PreviewOnly = false);

/// <summary>The outcome, in the shape a window can display without reinterpreting it.</summary>
public sealed record ConfigurationOutcome(
    WorkflowOutcome Outcome,
    string Summary,
    IReadOnlyList<string> Details,
    InstallPlan? Plan,
    IReadOnlyList<string> ConfirmationReasons)
{
    public bool Succeeded => Outcome == WorkflowOutcome.Succeeded;

    public bool NeedsUserConfirmation => Outcome == WorkflowOutcome.NeedsConfirmation;
}

/// <summary>
/// Runs one game's configuration through the workflow.
///
/// <para>The window's job is to collect a few values and show the result. The sequence — detect, decide, plan,
/// download, install, configure, verify — belongs to the workflow service, and routing it through here rather
/// than a click handler is what makes it testable and keeps the product logic from existing only inside the
/// interface.</para>
///
/// <para><b>It does not swallow the outcome.</b> A blocked or confirmation-seeking run is returned as what it
/// is, with the reasons attached, so the caller can ask the user rather than guess on their behalf.</para>
/// </summary>
public sealed class GameConfigurationService
{
    private readonly SmoothMotionWorkflow _workflow;

    public GameConfigurationService(SmoothMotionWorkflow workflow) => _workflow = workflow;

    /// <summary>
    /// Builds the plan for one game without installing anything.
    ///
    /// <para>This is what lets a batch show the user what it is about to do — all of it, at once — rather than
    /// asking once per game, or proceeding on a confirmation that described none of the plans.</para>
    /// </summary>
    public Task<ConfigurationOutcome> PreviewAsync(
        ConfigurationRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ConfigureAsync(request with { PreviewOnly = true }, progress, ct);
    }

    /// <summary>
    /// Assembles the workflow request and runs it.
    ///
    /// <paramref name="progress"/> receives the workflow's own narration; nothing here reformats or
    /// embellishes it, because a progress line that disagrees with what the run is doing is worse than none.
    /// </summary>
    public async Task<ConfigurationOutcome> ConfigureAsync(
        ConfigurationRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workflowRequest = new WorkflowRequest(
            Game: request.Game,
            Provider: request.Provider,
            ProviderVersion: request.ProviderVersion,
            Policy: VersionPolicy.None,
            Channel: ReleaseChannel.Stable,
            PayloadDirectory: request.PayloadDirectory,
            UserRendererChoice: request.UserRendererChoice,
            AllowProtected: request.AllowProtected,
            HasKernelAntiCheat: request.HasKernelAntiCheat,
            UserApi: request.UserApi,
            UserConfirmedUnverified: request.UserConfirmedUnverified,
            ProfileSettings: request.ProfileSettings,
            PreviewOnly: request.PreviewOnly,
            GpuName: request.GpuName,
            DriverVersion: request.DriverVersion,
            Store: request.Store,
            InstallMode: request.InstallMode);

        var result = await _workflow.RunAsync(workflowRequest, progress, ct).ConfigureAwait(false);

        var details = new List<string>();
        foreach (var step in result.Steps)
            details.Add($"{(step.Ok ? "✓" : "×")} {step.Stage}：{step.Message}");

        foreach (var error in result.Errors)
            details.Add("! " + error);

        return new ConfigurationOutcome(
            result.Outcome,
            Summarize(result),
            details,
            result.Plan,
            result.Outcome == WorkflowOutcome.NeedsConfirmation
                ? result.Errors.ToList()
                : new List<string>());
    }

    /// <summary>
    /// One line for the log. Says what happened without implying more than the run established.
    /// </summary>
    private static string Summarize(WorkflowResult result) => result.Outcome switch
    {
        WorkflowOutcome.Succeeded => $"配置完成（证据等级 {result.Evidence}）—— 文件已安装不等于功能已生效。",
        WorkflowOutcome.NeedsConfirmation => "需要你确认后才能继续；在此之前没有写入任何内容。",
        WorkflowOutcome.Blocked => "计划被阻止，未写入任何内容。",
        _ => "配置失败，已回滚能回滚的部分。",
    };
}
