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
/// What a piece of evidence can actually establish.
///
/// <para>This distinction is the whole point of the ladder. Copying a file proves bytes reached a folder and
/// nothing more; the game loading the proxy proves it was engaged; a frame-generation signal reaches what the
/// game renders. Treating them as interchangeable is exactly how "we installed it" becomes "it works".</para>
/// </summary>
public enum SignalKind
{
    /// <summary>
    /// Proves only that bytes were written. Present after <i>every</i> successful install, which is why it can
    /// never be one of the two signals that establish verification.
    /// </summary>
    Installation,

    /// <summary>The game or the driver actually engaged the feature.</summary>
    RuntimeOrDriver,

    /// <summary>Reaches what the game renders: frames being generated.</summary>
    FrameGeneration,
}

/// <summary>
/// One piece of evidence about whether the feature is working.
///
/// <paramref name="Kind"/> says what it can establish; <paramref name="Source"/> and
/// <paramref name="ObservedAt"/> say where it came from and when. A verification record without provenance
/// cannot be re-checked or aged out, and an observation without a time cannot expire.
/// </summary>
public sealed record VerificationSignal(
    string Name,
    bool Present,
    string Detail,
    SignalKind Kind,
    string Source = "",
    DateTimeOffset? ObservedAt = null)
{
    /// <summary>
    /// Only frame-generation evidence is strong. Runtime and installation evidence are real observations, but
    /// neither of them reaches the thing being claimed.
    /// </summary>
    public bool Strong => Kind == SignalKind.FrameGeneration;
}

/// <summary>The evidence collected about one installation, and the rung it supports.</summary>
public sealed record VerificationReport(
    SmoothMotionEvidence Level,
    IReadOnlyList<VerificationSignal> Signals,
    string Reason)
{
    /// <summary>
    /// Derives the rung from the signals.
    ///
    /// <para><b>Verification needs two different kinds of evidence, and neither substitutes for the other:</b>
    /// a frame-generation signal (something that reaches what the game actually renders), and a
    /// runtime/driver corroboration (the feature was actually engaged).</para>
    ///
    /// <para><b>Installation evidence is excluded from both on purpose.</b> It is present after every
    /// successful install, so counting it towards the two would let "a DLL was copied and debug bars appeared"
    /// pass as verified — the file being there says nothing about whether the game ever loaded it, and the
    /// whole reason this ladder exists is to stop a rung from being reported as the one above it.</para>
    /// </summary>
    public static VerificationReport FromSignals(IReadOnlyList<VerificationSignal> signals)
    {
        var present = signals.Where(s => s.Present).ToList();

        var frames = present.FirstOrDefault(s => s.Kind == SignalKind.FrameGeneration);
        var runtime = present.FirstOrDefault(s => s.Kind == SignalKind.RuntimeOrDriver);

        if (frames is not null && runtime is not null)
        {
            return new VerificationReport(SmoothMotionEvidence.Verified, signals,
                $"生成帧证据「{frames.Name}」与运行时/驱动佐证「{runtime.Name}」同时成立。");
        }

        if (present.Any(s => s.Name == SignalNames.ProfileApplied))
        {
            return new VerificationReport(SmoothMotionEvidence.Applied, signals,
                frames is null
                    ? "驱动已接受并保存了设置，但尚无生成帧的证据。"
                    : $"已观察到「{frames.Name}」，但缺少运行时或驱动侧的佐证，不足以判定已生效。");
        }

        if (present.Any(s => s.Name == SignalNames.ProfileRequested))
            return new VerificationReport(SmoothMotionEvidence.Requested, signals, "已请求驱动启用，尚未确认已保存。");

        if (present.Any(s => s.Name == SignalNames.ProxyLoaded))
            return new VerificationReport(SmoothMotionEvidence.Loaded, signals, "游戏进程已加载代理，但尚未验证生成帧。");

        if (present.Any(s => s.Name == SignalNames.FilesInstalled))
            return new VerificationReport(SmoothMotionEvidence.Installed, signals, "文件已部署，仅此而已——尚未证明被加载或生效。");

        return new VerificationReport(SmoothMotionEvidence.None, signals, "没有任何证据。");
    }

    /// <summary>
    /// True when every present signal carries provenance. Verification without a source and a time is a claim
    /// nobody can check later, which is the state this project treats as unverified.
    /// </summary>
    public bool HasTraceableProvenance =>
        Signals.Where(s => s.Present).All(s => s.Source.Length > 0 && s.ObservedAt is not null);
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

    /// <summary>
    /// Set only after the user has been shown the warnings and chosen to continue anyway.
    ///
    /// <para>This is <b>consent, not evidence</b>: it lets a run proceed with unknown compatibility, and it
    /// must never be used to change what the compatibility state says. Writing it back as "Compatible" would
    /// turn "the user accepted the risk" into "this combination was verified".</para>
    /// </summary>
    bool UserConfirmedUnverified = false,

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

    /// <summary>
    /// Optional recipe memory. Null is a legal state and not the same as "nothing to remember yet": a caller
    /// that has not supplied a store simply is not keeping one.
    /// </summary>
    private readonly RecipeMemoryStore? _recipes;

    public SmoothMotionWorkflow(
        IWorkflowDetector detector,
        NvidiaProfileService profile,
        CompatibilityMatrixStore matrix,
        RecipeMemoryStore? recipes = null)
    {
        _detector = detector;
        _profile = profile;
        _matrix = matrix;
        _recipes = recipes;
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

        // A user-specified API is used, because the person running the game knows more than a heuristic — but it
        // may not silently overrule evidence that reaches runtime detection. A disagreement becomes a question
        // for the user rather than a coin toss inside the code.
        var detectedApi = _detector.DetectApi(request.Game);
        var apiChoice = ResolveApi(request.UserApi, detectedApi);

        steps.Add(new WorkflowStep("检测图形 API", apiChoice.Api != GraphicsApi.Unknown, apiChoice.Reason));

        if (apiChoice.Conflicts) errors.Add(apiChoice.Reason);

        var conflicts = _detector.ScanProxyConflicts(request.Game);
        steps.Add(new WorkflowStep("扫描代理入口", true,
            $"空闲 {conflicts.SafeCandidates.Count} 个，占用 {conflicts.Conflicts.Count} 个。"));

        // ---- 2. provider version ----
        // Resolved before compatibility, the recipe and the plan, because all three are keyed on it. A plan
        // built from one version while being checked against another is a plan whose verification means
        // nothing — and the interface can easily supply neither, which is how this went unnoticed.
        var resolvedVersion = await ResolveProviderVersionAsync(request, progress, ct).ConfigureAwait(false);

        steps.Add(new WorkflowStep("解析 Provider 版本", resolvedVersion.Length > 0,
            resolvedVersion.Length > 0
                ? $"使用版本 {resolvedVersion}。"
                : "未能确定版本；后续按「未知版本」判定，不做猜测。"));

        // The payload folder is derived from the provider and the version just resolved, so two providers — or two
        // versions of one provider — cannot share one folder. A caller may still name a folder explicitly, which is
        // what the tests do; the derived path is what the interface relies on.
        var payloadDirectory = string.IsNullOrWhiteSpace(request.PayloadDirectory)
            ? PayloadPaths.For(request.Provider.Id, resolvedVersion)
            : request.PayloadDirectory;

        // ---- 3. compatibility ----
        var environment = new CompatibilityQuery(
            Gpu: request.GpuName,
            Driver: request.DriverVersion,
            GraphicsApi: apiChoice.Api,
            Game: request.Game.Name,
            Store: request.Store,
            RendererExe: renderer.RendererExe is null ? null : System.IO.Path.GetFileName(renderer.RendererExe),
            Provider: request.Provider.Id,
            ProviderVersion: resolvedVersion,
            InstallMode: request.InstallMode,
            LaunchMode: request.LaunchMode);

        var compatibility = _matrix.Query(environment);
        var decision = Decide(compatibility, resolvedVersion);

        steps.Add(new WorkflowStep("查询兼容性", true,
            $"{compatibility.Kind} / {compatibility.Validation}"));

        // ---- 3. plan ----
        // Prefer the payload's real contents when the provider has already scanned them. The hard-coded name is
        // only a fallback for the first run, where the download has not happened yet — and the executor checks
        // the plan against the payload afterwards precisely because a fallback is a guess.
        var manifest = payloadDirectory is null
            ? null
            : request.Provider.ManifestOf(payloadDirectory);

        var payloadFiles = manifest is not null
            ? manifest.FileNames
            : payloadDirectory is null
                ? new List<string>()
                : new List<string> { ModSource.IniName };

        InstallPlanInput MakeInput(CompatibilityDecision d) => new(
            Game: request.Game,
            Renderer: renderer,
            // The plan receives the API actually in play. The evidence behind the detector's own reading is kept
            // as-is: it records what was observed, and rewriting it to match the decision would destroy the very
            // disagreement the user is being asked about.
            Api: detectedApi with { Api = apiChoice.Api, Reason = apiChoice.Reason },
            ProviderId: request.Provider.Id,
            ProviderVersion: resolvedVersion,
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
        decision = Decide(recheck, resolvedVersion);

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

        var needsConfirmation = plan.Status == PlanStatus.NeedsConfirmation || apiChoice.Conflicts;

        if (needsConfirmation && !request.UserConfirmedUnverified)
        {
            errors.AddRange(plan.Warnings);

            steps.Add(new WorkflowStep("等待用户确认", true,
                "兼容性未知或存在 API 冲突：需要用户明确确认后才会继续，本步骤未写入任何内容。"));

            return Finish(WorkflowOutcome.NeedsConfirmation, SmoothMotionEvidence.None, steps, plan, request,
                errors, filesWritten: false, profileWritten: false, journal: profileJournal);
        }

        if (needsConfirmation)
        {
            // Consent, not evidence. The approval makes the plan executable and changes nothing it claims:
            // the status stays NeedsConfirmation, and the compatibility state keeps saying whatever it said.
            // Recording a confirmation as "Compatible" would turn "the user accepted the risk" into "checked".
            var before = plan.Status;

            if (plan.Status == PlanStatus.NeedsConfirmation)
                plan = plan with { UserApprovedUnverified = true };

            steps.Add(new WorkflowStep("用户确认继续", true,
                $"用户已知情确认在兼容性为 {plan.Compatibility.State} 的情况下继续" +
                $"（计划状态保持 {before}，该状态不作为已验证）。"));
        }

        // From here on something is actually written, so every failure path must undo what it did.
        var filesWritten = false;
        var profileWritten = false;

        try
        {
            // ---- 4. payload ----
            if (string.IsNullOrWhiteSpace(payloadDirectory))
            {
                errors.Add("没有可用的本地 payload 目录。");
                return Finish(WorkflowOutcome.Failed, SmoothMotionEvidence.None, steps, plan, request, errors,
                    filesWritten, profileWritten, journal: profileJournal);
            }

            var download = await request.Provider.DownloadAsync(payloadDirectory, progress, ct)
                .ConfigureAwait(false);
            steps.Add(new WorkflowStep("获取 payload", download.Ok, download.Message));

            if (!download.Ok)
            {
                errors.Add(download.Message);
                return Finish(WorkflowOutcome.Failed, SmoothMotionEvidence.None, steps, plan, request, errors,
                    filesWritten, profileWritten, journal: profileJournal);
            }

            // The plan was built before the payload existed, so its file list may have come from the fallback.
            // Now that the bytes are on disk, compare the two: a plan that names files the payload does not
            // contain describes an installation that cannot happen.
            var downloaded = request.Provider.ManifestOf(payloadDirectory);
            if (downloaded is not null)
            {
                var absent = plan.FilesToDeploy
                    .Where(f => !string.IsNullOrWhiteSpace(f) && !downloaded.Contains(f))
                    .ToList();

                steps.Add(new WorkflowStep("核对 payload 清单", absent.Count == 0,
                    $"payload 实际含 {downloaded.Files.Count} 个文件。" +
                    (absent.Count == 0 ? "" : "缺少：" + string.Join("、", absent))));

                if (absent.Count > 0)
                {
                    errors.Add($"payload 中缺少计划要求的文件：{string.Join("、", absent)}");
                    return Finish(WorkflowOutcome.Failed, SmoothMotionEvidence.None, steps, plan, request, errors,
                        filesWritten, profileWritten, journal: profileJournal);
                }
            }

            // ---- 5. install the patch (files before driver) ----
            // The plan decides what gets installed: its entry choice, its file list, and its ASI strategy. The
            // executor refuses rather than substituting some other arrangement the user never approved.
            var source = new ModSource(payloadDirectory);
            var execution = InstallPlanExecutor.Execute(
                request.Provider, plan, request.Game, source, request.AllowProtected);

            steps.Add(new WorkflowStep("安装补丁", execution.Ok,
                execution.Ok ? execution.Message : string.Join("；", execution.Steps)));

            if (!execution.Ok)
            {
                errors.Add(execution.Message);
                foreach (var detail in execution.Steps) errors.Add(detail);

                return Finish(WorkflowOutcome.Failed, SmoothMotionEvidence.None, steps, plan, request, errors,
                    filesWritten, profileWritten, journal: profileJournal);
            }

            filesWritten = true;

            // ---- 6. driver settings ----
            if (request.ProfileSettings is { Count: > 0 })
            {
                // Values come from the setting definitions, never from a blanket 1: the API bitmask is derived
                // from the API actually in play (the user's choice when given, otherwise the detected one), and
                // a setting whose values are not established is not written at all. An unknown API therefore
                // yields the master switch alone — never a guessed bitmask.
                var writes = SmoothMotionSettings.EnableWrites(apiChoice.Api)
                    .Where(w => request.ProfileSettings.Any(s => s.Id == w.Setting.Id))
                    .ToList();

                var apply = writes.Count > 0
                    ? _profile.Apply(request.Game.Name, writes)
                    : new ProfileApplyResult(true, "计划中的设置都没有可安全写入的值，已跳过。",
                        new ProfileJournal(request.Game.Name), Array.Empty<string>());

                profileJournal = apply.Journal;

                // The read-back already happened inside Apply, while the session was still open. Doing it here
                // would be reading a closed session, and calling the inevitable failure "not confirmed" would be
                // indistinguishable from a genuine mismatch.
                var readBack = apply.Ok && apply.ReadBackConfirmed;
                profileWritten = readBack;

                steps.Add(new WorkflowStep("配置 NVIDIA Profile", apply.Ok, apply.Message));

                if (apply.Ok)
                {
                    steps.Add(new WorkflowStep("读回驱动设置", readBack,
                        readBack
                            ? "读回值与写入值一致。"
                            : "无法确认读回值（驱动未读回、或不具备读取能力）—— 不按「已生效」处理。"));
                }

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
            var signals = CollectSignals(request, profileWritten, DateTimeOffset.Now);
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

    /// <summary>
    /// Picks the graphics API to plan with, and reports whether the user's choice contradicts an observation
    /// strong enough to argue with it.
    ///
    /// <para>A user-specified API wins over a static guess — the person running the game knows more than a
    /// filename heuristic. It does <b>not</b> silently win over runtime evidence: a disagreement at that rung
    /// is exactly the kind of question the user should answer, so it is surfaced rather than resolved.</para>
    /// </summary>
    private static (GraphicsApi Api, string Reason, bool Conflicts) ResolveApi(
        GraphicsApi userApi, GraphicsApiDetection detected)
    {
        if (userApi == GraphicsApi.Unknown)
            return (detected.Api, detected.Reason, false);

        var strong = detected.Level >= EvidenceLevel.RuntimeDetection && detected.Api != GraphicsApi.Unknown;
        var conflicts = strong && detected.Api != userApi;

        if (conflicts)
        {
            return (userApi,
                $"用户指定 {userApi}，但运行时检测到 {detected.Api}（证据等级 {detected.Level}）："
                + "两者冲突，需要用户确认后才会继续。",
                true);
        }

        return (userApi,
            $"采用用户指定的 {userApi}（检测结果为 {detected.Api}，证据等级 {detected.Level}）。",
            false);
    }

    /// <summary>
    /// Records what this run learned, so a later attempt can prefer what worked and avoid what did not.
    ///
    /// <para><b>The claimed level is deliberately not <c>ProjectVerified</c>.</b> That level requires first-hand
    /// evidence carrying a verification date (see <see cref="ValidationGuard.CanClaimProjectVerified"/>), and a
    /// workflow run cannot supply it — only the person watching the game can say frames appeared. What a run can
    /// honestly contribute is an observation, so that is what gets stored, and the guard is free to downgrade it
    /// further.</para>
    ///
    /// <para>Everything here is best-effort. Failing to remember must never fail a run that already worked.</para>
    /// </summary>
    private void RecordOutcome(
        WorkflowRequest request, VerificationReport verification, bool succeeded, string providerVersion)
    {
        if (_recipes is null) return;

        try
        {
            var when = DateTimeOffset.Now;

            _recipes.Record(
                recipeId: request.Recipe?.Id ?? $"{request.Provider.Id}@{request.Game.Name}",
                game: request.Game.Name,
                providerId: request.Provider.Id,
                succeeded: succeeded,
                evidence: verification.Level,
                when: when,
                evidenceRef: new EvidenceRef(
                    Source: EvidenceSource.LocalUserTest,
                    Type: EvidenceType.TestResult,
                    Reference: verification.Reason,
                    ObservedAt: when),
                claimed: ValidationLevel.PendingUserValidation,
                note: verification.Reason,
                providerVersion: providerVersion,
                api: request.UserApi,
                store: request.Store,
                proxyAsi: request.Game.PreferredProxy,
                mode: request.InstallMode);
        }
        catch
        {
            // Remembering is a convenience; failing to remember must not fail a run that worked.
        }
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

        // Recorded here rather than on the success path alone, so a failed attempt is remembered too: knowing
        // that a combination did not work is exactly what stops the next attempt from repeating it.
        RecordOutcome(request, report, succeeded: outcome == WorkflowOutcome.Succeeded,
            providerVersion: plan.ProviderVersion ?? "");

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

    /// <summary>
    /// Determines the version everything downstream is matched against.
    ///
    /// <para><b>Order matters.</b> Compatibility, the recipe and the plan are all keyed on the version, and a
    /// plan built from one version while being checked against another is a plan whose verification means
    /// nothing. So the version is resolved first — from what the caller already knows, and otherwise from the
    /// provider itself.</para>
    ///
    /// <para><b>An unresolved version stays unknown.</b> Substituting a plausible one would let the run match
    /// records it has no business matching, which is worse than admitting the version is not known. A failure to
    /// reach the provider is not fatal either: it degrades matching, it does not make installing impossible.</para>
    /// </summary>
    private async Task<string> ResolveProviderVersionAsync(
        WorkflowRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.ProviderVersion))
            return request.ProviderVersion!;

        try
        {
            progress?.Report("正在解析 Provider 版本…");

            var latest = await request.Provider
                .CheckLatestAsync(forceRefresh: false, ct)
                .ConfigureAwait(false);

            return latest?.Version ?? "";
        }
        catch (Exception ex)
        {
            progress?.Report($"版本解析失败：{ex.Message}");
            return "";
        }
    }

    private static IReadOnlyList<VerificationSignal> CollectSignals(
        WorkflowRequest request, bool profileApplied, DateTimeOffset observedAt) =>
        new[]
        {
            // Installation: present after every successful install, so it never counts towards verification.
            new VerificationSignal(SignalNames.FilesInstalled, true,
                "代理与 INI 已写入渲染目录。",
                SignalKind.Installation, Source: "文件系统", ObservedAt: observedAt),

            new VerificationSignal(SignalNames.ProxyLoaded, request.ProxyLoadedInGame,
                request.ProxyLoadedInGame ? "游戏进程模块中观察到代理。" : "尚未观察到游戏加载代理。",
                SignalKind.RuntimeOrDriver, Source: "游戏进程模块", ObservedAt: observedAt),

            new VerificationSignal(SignalNames.ProfileRequested, request.ProfileSettings is { Count: > 0 },
                "已向驱动提交设置。",
                SignalKind.RuntimeOrDriver, Source: "DRS 会话", ObservedAt: observedAt),

            new VerificationSignal(SignalNames.ProfileApplied, profileApplied,
                profileApplied
                    ? "驱动已保存设置，且读回值与写入值一致。"
                    : "驱动未确认保存，或读回值与写入值不一致。",
                SignalKind.RuntimeOrDriver, Source: "DRS 读回", ObservedAt: observedAt),

            new VerificationSignal(SignalNames.DebugBars, request.ObservedDebugBars,
                request.ObservedDebugBars ? "用户确认看到 Debug Bars。" : "未确认 Debug Bars。",
                SignalKind.FrameGeneration, Source: "用户观察（Debug Bars）", ObservedAt: observedAt),

            new VerificationSignal(SignalNames.PatchLog, request.ObservedPatchLog,
                request.ObservedPatchLog ? "补丁日志显示正在生成帧。" : "补丁日志无生成帧记录。",
                SignalKind.FrameGeneration, Source: "补丁日志", ObservedAt: observedAt),
        };
}
