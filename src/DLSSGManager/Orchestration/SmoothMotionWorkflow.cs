using DLSSGManager.Compatibility;
using DLSSGManager.GameDetection;
using DLSSGManager.InstallPlanning;
using DLSSGManager.NvidiaProfile;
using DLSSGManager.Providers;
using DLSSGManager.Update;

namespace DLSSGManager.Orchestration;

/// <summary>
/// How far the installation is actually proven to have got.
///
/// These are not synonyms and the numbers are the strength, because the failure this ladder exists to
/// prevent is reporting the bottom rung as the top one. Copying a DLL proves the file was written and
/// nothing else: the game may never load it, the driver may never be told to use it, and frames may
/// never be generated. Each rung needs its own evidence.
/// </summary>
public enum SmoothMotionEvidence
{
    /// <summary>Nothing was done.</summary>
    None = 0,

    /// <summary>The payload is on disk beside the renderer.</summary>
    Installed = 1,

    /// <summary>The game process actually loaded the proxy.</summary>
    Loaded = 2,

    /// <summary>The driver was asked to enable the feature (profile written).</summary>
    Requested = 3,

    /// <summary>The driver accepted and persisted the request.</summary>
    Applied = 4,

    /// <summary>Independent signals agree that frames are being generated.</summary>
    Verified = 5,
}

/// <summary>How the run ended.</summary>
public enum WorkflowOutcome
{
    Succeeded,

    /// <summary>Not attempted: the plan requires a human decision first.</summary>
    NeedsConfirmation,

    /// <summary>Not attempted: a precondition failed.</summary>
    Blocked,

    /// <summary>Attempted and failed; anything already done was rolled back.</summary>
    Failed,
}

/// <summary>One step of the run, kept so a failure can be explained rather than summarised away.</summary>
public sealed record WorkflowStep(string Stage, bool Ok, string Message);

/// <summary>
/// One piece of evidence about whether the feature is working.
///
/// <paramref name="Strong"/> marks evidence that reaches the driver's own behaviour (the debug bars the
/// community uses, or a log showing frames being generated). File presence and module loading are real
/// observations but they are not that, which is why they are weak.
/// </summary>
public sealed record VerificationSignal(string Name, bool Present, string Detail, bool Strong);

/// <summary>The evidence collected about one installation, and the rung it supports.</summary>
public sealed record VerificationReport(
    SmoothMotionEvidence Level,
    IReadOnlyList<VerificationSignal> Signals,
    string Reason)
{
    /// <summary>
    /// Derives the rung from the signals.
    ///
    /// <para><b>Verified requires corroboration.</b> At least two independent signals must be present and
    /// at least one of them must be strong. A single signal — however strong it looks on its own — is not
    /// enough, because the project's rule is that no single observation may declare the feature verified.
    /// This is why the file-copied case can never reach the top rung: one weak signal cannot.</para>
    /// </summary>
    public static VerificationReport FromSignals(IReadOnlyList<VerificationSignal> signals)
    {
        var present = signals.Where(s => s.Present).ToList();
        var strong = present.Count(s => s.Strong);

        if (present.Count >= 2 && strong >= 1)
        {
            return new VerificationReport(SmoothMotionEvidence.Verified, signals,
                $"多信号交叉确认（{present.Count} 项存在，其中强证据 {strong} 项）。");
        }

        if (present.Any(s => s.Name == SignalNames.ProfileApplied))
            return new VerificationReport(SmoothMotionEvidence.Applied, signals, "驱动已接受并保存了设置，但尚无生成帧的外部证据。");

        if (present.Any(s => s.Name == SignalNames.ProfileRequested))
            return new VerificationReport(SmoothMotionEvidence.Requested, signals, "已请求驱动启用，尚未确认已保存。");

        if (present.Any(s => s.Name == SignalNames.ProxyLoaded))
            return new VerificationReport(SmoothMotionEvidence.Loaded, signals, "游戏进程已加载代理，但尚未验证生成帧。");

        if (present.Any(s => s.Name == SignalNames.FilesInstalled))
            return new VerificationReport(SmoothMotionEvidence.Installed, signals, "文件已部署，仅此而已——尚未证明被加载或生效。");

        return new VerificationReport(SmoothMotionEvidence.None, signals, "没有任何证据。");
    }
}

/// <summary>Signal names, shared so the report and its tests cannot drift apart.</summary>
public static class SignalNames
{
    public const string FilesInstalled = "文件已部署";
    public const string ProxyLoaded = "游戏已加载代理";
    public const string ProfileRequested = "已请求驱动启用";
    public const string ProfileApplied = "驱动已保存设置";
    public const string DebugBars = "Debug Bars 观察";
    public const string PatchLog = "补丁日志显示生成帧";
}

/// <summary>
/// The detection steps the workflow needs, behind an interface.
///
/// The detectors themselves are static and read the filesystem, so without this seam the workflow could
/// only be tested against a real game folder.
/// </summary>
public interface IWorkflowDetector
{
    RendererDetection DetectRenderer(GameEntry game, string? userChoice);

    GraphicsApiDetection DetectApi(GameEntry game);

    /// <summary>Proxy/ASI occupancy for the game folder.</summary>
    ProxyConflictReport ScanProxyConflicts(GameEntry game);
}

/// <summary>Everything one run needs. Assembled by the caller; the workflow fetches nothing itself.</summary>
public sealed record WorkflowRequest(
    GameEntry Game,
    IPatchProvider Provider,
    string? ProviderVersion,
    VersionPolicy Policy,
    ReleaseChannel Channel,
    string? PayloadDirectory,
    string? UserRendererChoice = null,
    InstallRecipe? Recipe = null,
    bool AllowProtected = false,
    bool HasKernelAntiCheat = false,
    GraphicsApi UserApi = GraphicsApi.Unknown,
    bool ObservedDebugBars = false,
    bool ObservedPatchLog = false,
    bool ProxyLoadedInGame = false,
    IReadOnlyList<NvidiaProfile.ProfileSetting>? ProfileSettings = null,
    string? GpuName = null,
    string? DriverVersion = null,
    StoreKind Store = StoreKind.Unknown,
    string? LaunchMode = null,
    InstallMode InstallMode = InstallMode.Unknown);

/// <summary>The whole run's outcome, including what was rolled back and why it failed.</summary>
public sealed record WorkflowResult(
    WorkflowOutcome Outcome,
    SmoothMotionEvidence Evidence,
    IReadOnlyList<WorkflowStep> Steps,
    InstallPlan? Plan,
    VerificationReport Verification,
    IReadOnlyList<string> Errors,
    bool FilesRolledBack,
    bool ProfileRolledBack)
{
    public bool Succeeded => Outcome == WorkflowOutcome.Succeeded;
}

/// <summary>
/// Runs the whole "make this game work" sequence in one place.
///
/// <para>Deliberately a service and not a window method. The sequence — detect, decide, plan, install,
/// configure, verify — is the product's core, and putting it in a click handler would make it untestable
/// and impossible to reuse for batch operations or the command line.</para>
///
/// <para><b>Ordering is the safety property.</b> Nothing is written until a plan says
/// <see cref="PlanStatus.Ready"/>; the filesystem is touched before the driver, so a failure cannot leave
/// a profile enabled for a game whose proxy was never installed; and on any failure the steps already
/// taken are undone in reverse.</para>
/// </summary>
public sealed class SmoothMotionWorkflow
{
    private readonly IWorkflowDetector _detector;
    private readonly NvidiaProfileService _profile;
    private readonly CompatibilityMatrixStore _matrix;

    public SmoothMotionWorkflow(IWorkflowDetector detector, NvidiaProfileService profile, CompatibilityMatrixStore matrix)
    {
        _detector = detector;
        _profile = profile;
        _matrix = matrix;
    }

    /// <summary>Runs the sequence and reports what actually happened.</summary>
    public async Task<WorkflowResult> RunAsync(WorkflowRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var steps = new List<WorkflowStep>();
        var errors = new List<string>();

        // Honour cancellation before touching anything, so a cancelled run is a cancellation rather than a
        // quiet early return that looks like a decision.
        ct.ThrowIfCancellationRequested();

        // Declared before any work so every early return can hand it to the rollback path.
        // The journal is the rollback's input and carries the profile name, so a rollback can open its own
        // session long after this run closed its own.
        ProfileJournal? profileJournal = null;

        // ---- 1. detection ----
        progress?.Report("检测渲染 EXE 与图形 API…");
        var renderer = _detector.DetectRenderer(request.Game, request.UserRendererChoice);
        steps.Add(new WorkflowStep("检测渲染 EXE", true, renderer.Reason));

        var api = _detector.DetectApi(request.Game);
        steps.Add(new WorkflowStep("检测图形 API", api.Api != GraphicsApi.Unknown, api.Reason));

        var conflicts = _detector.ScanProxyConflicts(request.Game);
        steps.Add(new WorkflowStep("扫描代理入口", true,
            $"空闲 {conflicts.SafeCandidates.Count} 个，占用 {conflicts.Conflicts.Count} 个。"));

        // ---- 2. compatibility ----
        var environment = new CompatibilityQuery(
            Gpu: request.GpuName,
            Driver: request.DriverVersion,
            GraphicsApi: api.Api,
            Game: request.Game.Name,
            Store: request.Store,
            RendererExe: renderer.RendererExe is null ? null : System.IO.Path.GetFileName(renderer.RendererExe),
            Provider: request.Provider.Id,
            ProviderVersion: request.ProviderVersion,
            InstallMode: request.InstallMode,
            LaunchMode: request.LaunchMode);

        var compatibility = _matrix.Query(environment);
        var decision = Decide(compatibility, request.ProviderVersion);

        steps.Add(new WorkflowStep("查询兼容性", true,
            $"{compatibility.Kind} / {compatibility.Validation}"));

        // ---- 3. plan ----
        var payloadFiles = request.PayloadDirectory is null
            ? new List<string>()
            : new List<string> { ModSource.IniName };

        InstallPlanInput MakeInput(CompatibilityDecision d) => new(
            Game: request.Game,
            Renderer: renderer,
            Api: api,
            ProviderId: request.Provider.Id,
            ProviderVersion: request.ProviderVersion,
            ProviderPayloadFiles: payloadFiles,
            ProxyConflicts: conflicts,
            Compatibility: d,
            Recipe: request.Recipe,
            HasKernelAntiCheat: request.HasKernelAntiCheat,
            AllowProtected: request.AllowProtected,
            RequestedMode: request.InstallMode);

        // Pass 1 exists only to choose the install mode and the proxy entry. Compatibility cannot be fully
        // checked until those two are known, and they come from a plan — so a plan is built, its choices
        // complete the query, and only the second plan's status is acted on. Acting on the first would
        // deadlock: an incomplete context yields NeedsConfirmation, which would skip the very re-check
        // that could have made it exact.
        var provisional = InstallPlanner.Plan(MakeInput(decision));

        var fullQuery = environment with { InstallMode = provisional.Mode, ProxyAsi = provisional.ProxyChoice };
        var recheck = _matrix.Query(fullQuery);
        compatibility = recheck;
        decision = Decide(recheck, request.ProviderVersion);

        steps.Add(new WorkflowStep("复核兼容性（含安装方式）",
            recheck.Kind == CompatibilityMatchKind.Exact, $"{recheck.Kind} / {recheck.Validation}"));

        var plan = InstallPlanner.Plan(MakeInput(decision));

        steps.Add(new WorkflowStep("生成安装计划", plan.Status == PlanStatus.Ready, $"计划状态 {plan.Status}。"));

        if (plan.Status == PlanStatus.Blocked)
        {
            errors.AddRange(plan.Blockers);
            return Finish(WorkflowOutcome.Blocked, SmoothMotionEvidence.None, steps, plan, request,
                errors, filesWritten: false, profileWritten: false, journal: profileJournal);
        }

        if (plan.Status == PlanStatus.NeedsConfirmation)
        {
            errors.AddRange(plan.Warnings);
            return Finish(WorkflowOutcome.NeedsConfirmation, SmoothMotionEvidence.None, steps, plan, request,
                errors, filesWritten: false, profileWritten: false, journal: profileJournal);
        }

        // From here on something is actually written, so every failure path must undo what it did.
        var filesWritten = false;
        var profileWritten = false;

        try
        {
            // ---- 4. payload ----
            if (string.IsNullOrWhiteSpace(request.PayloadDirectory))
            {
                errors.Add("没有可用的本地 payload 目录。");
                return Finish(WorkflowOutcome.Failed, SmoothMotionEvidence.None, steps, plan, request, errors,
                    filesWritten, profileWritten, journal: profileJournal);
            }

            var download = await request.Provider.DownloadAsync(request.PayloadDirectory!, progress, ct)
                .ConfigureAwait(false);
            steps.Add(new WorkflowStep("获取 payload", download.Ok, download.Message));

            if (!download.Ok)
            {
                errors.Add(download.Message);
                return Finish(WorkflowOutcome.Failed, SmoothMotionEvidence.None, steps, plan, request, errors,
                    filesWritten, profileWritten, journal: profileJournal);
            }

            // ---- 5. install the patch (files before driver) ----
            var source = new ModSource(request.PayloadDirectory!);
            var install = request.Provider.Install(request.Game, source, request.AllowProtected);
            steps.Add(new WorkflowStep("安装补丁", install.Ok, install.Message));

            if (!install.Ok)
            {
                errors.Add(install.Message);
                return Finish(WorkflowOutcome.Failed, SmoothMotionEvidence.None, steps, plan, request, errors,
                    filesWritten, profileWritten, journal: profileJournal);
            }

            filesWritten = true;

            // ---- 6. driver settings ----
            if (request.ProfileSettings is { Count: > 0 })
            {
                // Values come from the setting definitions, never from a blanket 1: the API bitmask is derived
                // from the API actually in play, and a setting whose values are not established is not written
                // at all. An unknown API therefore yields the master switch alone — never a guessed bitmask.
                var writes = SmoothMotionSettings.EnableWrites(request.UserApi)
                    .Where(w => request.ProfileSettings.Any(s => s.Id == w.Setting.Id))
                    .ToList();

                var apply = writes.Count > 0
                    ? _profile.Apply(request.Game.Name, writes)
                    : new ProfileApplyResult(true, "计划中的设置都没有可安全写入的值，已跳过。",
                        new ProfileJournal(request.Game.Name), Array.Empty<string>());

                profileJournal = apply.Journal;
                profileWritten = apply.Ok;

                steps.Add(new WorkflowStep("配置 NVIDIA Profile", apply.Ok, apply.Message));

                if (!apply.Ok)
                {
                    errors.Add(apply.Message);
                    foreach (var note in apply.Notes) errors.Add(note);

                    return Finish(WorkflowOutcome.Failed, SmoothMotionEvidence.Installed, steps, plan, request,
                        errors, filesWritten, profileWritten, journal: profileJournal);
                }
            }
            else
            {
                steps.Add(new WorkflowStep("配置 NVIDIA Profile", true, "本次计划没有 Profile 要求，已跳过。"));
            }

            // ---- 7. verification ----
            var signals = CollectSignals(request, profileWritten);
            var verification = VerificationReport.FromSignals(signals);
            steps.Add(new WorkflowStep("运行验证", true, verification.Reason));

            return Finish(WorkflowOutcome.Succeeded, verification.Level, steps, plan, request, errors,
                filesWritten: false, profileWritten: false, verification);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"编排过程中异常：{ex.Message}");
            return Finish(WorkflowOutcome.Failed, SmoothMotionEvidence.None, steps, plan, request, errors,
                filesWritten, profileWritten, journal: profileJournal);
        }
    }

    /// <summary>
    /// Undoes what was done, in reverse order, and reports honestly.
    ///
    /// The driver is rolled back before the files: a profile left enabled for a game whose proxy has just
    /// been removed is the more surprising of the two half-states for a user to discover.
    /// </summary>
    /// <summary>Turns a matrix answer into a decision. Partial and absent matches stay unknown.</summary>
    private static CompatibilityDecision Decide(CompatibilityMatch match, string? version)
    {
        if (match.Kind != CompatibilityMatchKind.Exact) return CompatibilityDecision.Unknown(match.Reason);

        return match.Validation switch
        {
            ValidationState.ReportedWorking => CompatibilityDecision.Compatible(version ?? "", match.Reason),
            ValidationState.ReportedBroken => CompatibilityDecision.Incompatible(version ?? "", match.Reason),
            _ => CompatibilityDecision.Unknown(match.Reason),
        };
    }

    private WorkflowResult Finish(
        WorkflowOutcome outcome,
        SmoothMotionEvidence evidence,
        List<WorkflowStep> steps,
        InstallPlan plan,
        WorkflowRequest request,
        List<string> errors,
        bool filesWritten,
        bool profileWritten,
        VerificationReport? verification = null,
        ProfileJournal? journal = null)
    {
        var report = verification ?? VerificationReport.FromSignals(Array.Empty<VerificationSignal>());

        if (outcome != WorkflowOutcome.Failed)
            return new WorkflowResult(outcome, evidence, steps, plan, report, errors,
                FilesRolledBack: false, ProfileRolledBack: false);

        var filesRolledBack = false;
        var profileRolledBack = false;

        // Driver first, then files: a profile left enabled for a game whose proxy has just been removed is
        // the more surprising of the two half-states for a user to find.
        if (journal is { Count: > 0 })
        {
            var rollback = _profile.Rollback(journal);
            profileRolledBack = rollback.Ok;
            steps.Add(new WorkflowStep("回滚 NVIDIA Profile", rollback.Ok, rollback.Message));
            if (!rollback.Ok) errors.Add(rollback.Message);
        }

        if (filesWritten)
        {
            var restore = request.Provider.Restore(request.Game, removeLogs: false);
            filesRolledBack = restore.Ok;
            steps.Add(new WorkflowStep("回滚文件部署", restore.Ok, restore.Message));
            if (!restore.Ok) errors.Add(restore.Message);
        }

        return new WorkflowResult(outcome, evidence, steps, plan, report, errors, filesRolledBack, profileRolledBack);
    }

    private static IReadOnlyList<VerificationSignal> CollectSignals(WorkflowRequest request, bool profileApplied) =>
        new[]
        {
            new VerificationSignal(SignalNames.FilesInstalled, true, "代理与 INI 已写入渲染目录。", Strong: false),
            new VerificationSignal(SignalNames.ProxyLoaded, request.ProxyLoadedInGame,
                request.ProxyLoadedInGame ? "游戏进程模块中观察到代理。" : "尚未观察到游戏加载代理。", Strong: false),
            new VerificationSignal(SignalNames.ProfileRequested, request.ProfileSettings is { Count: > 0 },
                "已向驱动提交设置。", Strong: false),
            new VerificationSignal(SignalNames.ProfileApplied, profileApplied, "驱动已保存设置。", Strong: false),
            new VerificationSignal(SignalNames.DebugBars, request.ObservedDebugBars,
                request.ObservedDebugBars ? "用户确认看到 Debug Bars。" : "未确认 Debug Bars。", Strong: true),
            new VerificationSignal(SignalNames.PatchLog, request.ObservedPatchLog,
                request.ObservedPatchLog ? "补丁日志显示正在生成帧。" : "补丁日志无生成帧记录。", Strong: true),
        };
}
