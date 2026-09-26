using System.IO;
using DLSSGManager.Compatibility;
using DLSSGManager.GameDetection;
using DLSSGManager.Update;

namespace DLSSGManager.InstallPlanning;

/// <summary>
/// How a proxy is expected to be arranged in the game folder.
///
/// There is deliberately no "best" value. The three real strategies are <see cref="SafeSingle"/>
/// (one hot-path entry), <see cref="UtilitySet"/> (several non-hot-path entries) and
/// <see cref="KnownRecipe"/> (a documented combination) — and whether they are equivalent is still
/// <c>Unresolved / Needs Validation</c> per the proxy design, so the planner records which one a plan
/// uses without claiming one is superior.
/// </summary>
public enum ProxyStrategy
{
    Unknown,
    SafeSingle,
    UtilitySet,
    KnownRecipe,
}

/// <summary>Who owns a DLL already sitting in a hot-path entry name.</summary>
public enum ProxyOwnership
{
    Unknown,

    /// <summary>Written by this manager, per the deployment record.</summary>
    OwnedByThisTool,

    /// <summary>Recognised as belonging to the game itself.</summary>
    KnownGameFile,

    /// <summary>Recognised as another mod this project already knows about.</summary>
    KnownCompatibleMod,
}

/// <summary>One loader entry name and what occupies it.</summary>
public sealed record ProxySlot(string FileName, string Path, bool Exists, ProxyOwnership Ownership, string Reason)
{
    /// <summary>Only a free slot may be taken without further evidence.</summary>
    public bool IsFree => !Exists;
}

/// <summary>The full occupancy picture for one game folder.</summary>
public sealed record ProxyConflictReport(
    IReadOnlyList<ProxySlot> Slots,
    IReadOnlyList<string> SafeCandidates,
    IReadOnlyList<ProxySlot> Conflicts)
{
    public bool HasConflict => Conflicts.Count > 0;

    public bool HasSafeSlot => SafeCandidates.Count > 0;
}

/// <summary>
/// Works out which loader entry names are free and which are occupied.
///
/// Occupancy is not a boolean: a file we wrote, a file the game shipped, and an unrecognised file are
/// three different situations with three different correct responses. Unknown occupancy is never
/// treated as free — overwriting something unidentified is the one mistake this scan exists to prevent.
/// </summary>
public static class ProxyConflictScanner
{
    /// <param name="game">Game whose folder is inspected.</param>
    /// <param name="isKnownCompatibleMod">
    /// Optional recogniser for other known mods. Left as a hook rather than a guess: without it, an
    /// unidentified DLL stays <see cref="ProxyOwnership.Unknown"/>.
    /// </param>
    public static ProxyConflictReport Scan(GameEntry game, Func<string, bool>? isKnownCompatibleMod = null)
    {
        var slots = new List<ProxySlot>();
        var safe = new List<string>();
        var conflicts = new List<ProxySlot>();

        if (game is null || string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
            return new ProxyConflictReport(slots, safe, conflicts);

        var ourNames = OurRecordedNames(game);

        foreach (var name in ModSource.KnownProxyNames)
        {
            var path = Path.Combine(game.RenderDir, name);

            bool exists;
            try { exists = File.Exists(path); }
            catch { exists = false; }

            if (!exists)
            {
                var free = new ProxySlot(name, path, false, ProxyOwnership.Unknown, "入口未被占用。");
                slots.Add(free);
                safe.Add(name);
                continue;
            }

            ProxyOwnership ownership;
            string reason;

            if (ourNames.Contains(name))
            {
                ownership = ProxyOwnership.OwnedByThisTool;
                reason = "由本工具部署（见部署记录）。";
            }
            else if (isKnownCompatibleMod is not null && isKnownCompatibleMod(path))
            {
                ownership = ProxyOwnership.KnownCompatibleMod;
                reason = "识别为已知兼容 Mod。";
            }
            else
            {
                ownership = ProxyOwnership.Unknown;
                reason = "存在同名文件但无法确认归属，不得覆盖。";
            }

            var occupied = new ProxySlot(name, path, true, ownership, reason);
            slots.Add(occupied);
            conflicts.Add(occupied);
        }

        return new ProxyConflictReport(slots, safe, conflicts);
    }

    /// <summary>Entry names this manager itself put there, according to the deployment record.</summary>
    private static HashSet<string> OurRecordedNames(GameEntry game)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deployment = game.Deployment;
        if (deployment is null) return names;

        if (!string.IsNullOrWhiteSpace(deployment.ProxyName)) names.Add(deployment.ProxyName);

        // Older records may name several entries; anything the record mentions counts as ours.
        foreach (var recorded in RecordedFiles(deployment))
        {
            if (ModSource.KnownProxyNames.Contains(recorded, StringComparer.OrdinalIgnoreCase)) names.Add(recorded);
        }

        return names;
    }

    private static IEnumerable<string> RecordedFiles(DeploymentInfo deployment)
    {
        try
        {
            return deployment.Backups is null
                ? Array.Empty<string>()
                : deployment.Backups.Select(b => b.FileName);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// A documented way to install one provider into one kind of game.
///
/// The field list is the compatibility contract: a recipe only applies when the store, API, renderer
/// pattern, provider and provider-version range all line up. Everything else — files, proxy strategy,
/// install mode, ASI strategy, profile changes, launch arguments, validation and rollback steps — is
/// carried so the planner never has to invent it.
/// </summary>
public sealed record InstallRecipe(
    string Id,
    string Game,
    StoreKind Store,
    GraphicsApi GraphicsApi,
    string RendererExePattern,
    string ProviderId,
    string ProviderVersionRange,
    IReadOnlyList<string> RequiredFiles,
    ProxyStrategy ProxyStrategy,
    InstallMode Mode,
    string AsiStrategy,
    IReadOnlyList<string> NvidiaProfileChanges,
    IReadOnlyList<string> LaunchArguments,
    IReadOnlyList<string> ValidationSteps,
    IReadOnlyList<string> RollbackSteps,
    string Note = "")
{
    /// <summary>
    /// Whether this recipe may be applied.
    ///
    /// An unset field on either side is not a wildcard: a recipe that does not pin the API cannot be
    /// applied to a game whose API is unknown, and a game whose API is unknown cannot be matched by a
    /// recipe that pins one. Guessing here would apply a documented procedure to an undocumented case.
    /// </summary>
    public bool AppliesTo(GraphicsApi api, StoreKind store, string? rendererExe, string providerId, string? providerVersion)
    {
        if (GraphicsApi != GraphicsApi.Unknown && GraphicsApi != api) return false;
        if (Store != StoreKind.Unknown && Store != store) return false;
        if (!string.Equals(ProviderId, providerId, StringComparison.OrdinalIgnoreCase)) return false;

        if (!string.IsNullOrWhiteSpace(ProviderVersionRange) && !string.IsNullOrWhiteSpace(providerVersion) &&
            !VersionInRange(providerVersion!, ProviderVersionRange))
            return false;

        if (!string.IsNullOrWhiteSpace(RendererExePattern) && !string.IsNullOrWhiteSpace(rendererExe))
        {
            var pattern = RendererExePattern.Replace("*", "");
            if (pattern.Length > 0 && !Path.GetFileName(rendererExe!).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return api != GraphicsApi.Unknown && !string.IsNullOrWhiteSpace(providerVersion);
    }

    /// <summary>Accepts "0.3.0-0.3.9", "0.3.x" or an exact version; anything else is not a range.</summary>
    private static bool VersionInRange(string version, string range)
    {
        if (range.Contains('x', StringComparison.OrdinalIgnoreCase))
        {
            var prefix = range.Split('x')[0].TrimEnd('.');
            return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        var parts = range.Split('-', 2);
        if (parts.Length == 2)
        {
            var low = ReleaseVersion.Compare(version, parts[0].Trim());
            var high = ReleaseVersion.Compare(version, parts[1].Trim());
            return low is VersionOrder.Greater or VersionOrder.Equal && high is VersionOrder.Less or VersionOrder.Equal;
        }

        return ReleaseVersion.Compare(version, range.Trim()) == VersionOrder.Equal;
    }
}

/// <summary>Where a plan ended up.</summary>
public enum PlanStatus
{
    Unknown,

    /// <summary>Every precondition is satisfied; the plan may be carried out.</summary>
    Ready,

    /// <summary>Not blocked, but something must be confirmed first.</summary>
    NeedsConfirmation,

    /// <summary>A precondition failed. The plan must not be carried out.</summary>
    Blocked,
}

/// <summary>Everything the planner is allowed to look at. Assembled by the caller, never fetched inside.</summary>
public sealed record InstallPlanInput(
    GameEntry Game,
    RendererDetection Renderer,
    GraphicsApiDetection Api,
    string ProviderId,
    string? ProviderVersion,
    IReadOnlyList<string> ProviderPayloadFiles,
    ProxyConflictReport ProxyConflicts,
    CompatibilityDecision Compatibility,
    InstallRecipe? Recipe,
    bool HasKernelAntiCheat,
    bool AllowProtected,
    InstallMode RequestedMode = InstallMode.Unknown);

/// <summary>
/// The plan itself: what would be done, what would be changed, and what must be true beforehand.
///
/// Holds no file handles and performs no writes — it is a value the caller can inspect, show and refuse.
/// </summary>
public sealed record InstallPlan(
    PlanStatus Status,
    string? TargetRendererExe,
    GraphicsApi Api,
    string ProviderId,
    string? ProviderVersion,
    InstallMode Mode,
    ProxyStrategy ProxyStrategy,
    IReadOnlyList<string> FilesToDeploy,
    string? ProxyChoice,
    string? AsiChoice,
    IReadOnlyList<string> NvidiaProfileRequirements,
    IReadOnlyList<string> LaunchArguments,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    CompatibilityDecision Compatibility,
    IReadOnlyList<string> RollbackRequirements)
{
    public bool CanExecute => Status == PlanStatus.Ready;
}

/// <summary>
/// Turns detection results into a plan.
///
/// Pure and side-effect free by construction: it receives everything it needs, reads only to check
/// whether a slot is free, and never writes. That is what makes the rules below testable without a game
/// folder, and what keeps "compute a plan" from ever turning into "change the user's installation".
/// </summary>
public static class InstallPlanner
{
    public static InstallPlan Plan(InstallPlanInput input)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var profile = new List<string>();
        var launch = new List<string>();
        var rollback = new List<string>();

        // 1. Kernel anti-cheat is a hard stop unless the user explicitly accepted it.
        if (input.HasKernelAntiCheat && !input.AllowProtected)
            blockers.Add("检测到内核级反作弊；未获显式授权前不得安装。");

        // 2. Graphics API. §9.3 is explicit that an unknown API must produce an error rather than a
        //    default choice, so this returns Blocked before anything else can be proposed.
        if (input.Api.Api == GraphicsApi.Unknown)
        {
            blockers.Add("UnknownApi：无法判定图形 API，安装计划不得默认选择其中一个。");
            return Blocked(input, blockers, warnings, profile, launch, rollback);
        }

        if (input.Api.HasConflict)
            warnings.Add($"图形 API 证据存在冲突，已按优先级取 {input.Api.Api}：{input.Api.Reason}");

        // 3. Renderer executable. Unknown is a stop for automatic installation, not a guess.
        if (input.Renderer.IsUnknown || string.IsNullOrWhiteSpace(input.Renderer.RendererExe))
        {
            warnings.Add($"渲染 EXE 尚未确定：{input.Renderer.Reason}");
        }
        else if (!input.Renderer.CanPlanWithoutAsking)
        {
            warnings.Add($"渲染 EXE 仅有静态证据（{input.Renderer.Level}），需要用户确认：{input.Renderer.Reason}");
        }

        // 4. Proxy occupancy. Only a free entry may be taken; an unrecognised file is never overwritten,
        //    and "back it up first" is not an acceptable substitute.
        var proxyChoice = input.ProxyConflicts.SafeCandidates.FirstOrDefault();

        if (proxyChoice is null)
            blockers.Add("所有热路径代理入口都被占用；不得覆盖来源不明的 DLL。");
        else if (input.ProxyConflicts.HasConflict)
            warnings.Add($"以下入口已被占用，本计划不使用：{string.Join("、", input.ProxyConflicts.Conflicts.Select(c => c.FileName))}");

        // 5. Compatibility. Only an exact, working record clears the plan; everything else means the
        //    user decides, which is what NeedsConfirmation expresses.
        var compatible = input.Compatibility.State == CompatibilityState.Compatible;
        if (!compatible)
            warnings.Add($"兼容性为 {input.Compatibility.State}：{input.Compatibility.Reason}");

        if (input.Compatibility.State == CompatibilityState.Incompatible)
            blockers.Add($"该组合已被记录为不兼容：{input.Compatibility.Reason}");

        // 6. Recipe-driven extras.
        var mode = input.Recipe?.Mode ?? input.RequestedMode;
        var strategy = input.Recipe?.ProxyStrategy ?? ProxyStrategy.Unknown;
        string? asi = null;

        if (input.Recipe is not null)
        {
            profile.AddRange(input.Recipe.NvidiaProfileChanges);
            launch.AddRange(input.Recipe.LaunchArguments);
            rollback.AddRange(input.Recipe.RollbackSteps);

            if (input.Recipe.Mode == InstallMode.AsiLoader)
            {
                asi = input.Recipe.AsiStrategy;
                if (string.IsNullOrWhiteSpace(asi))
                    warnings.Add("该配方要求 ASI 加载方式，但未给出 ASI 策略。");
            }
        }
        else
        {
            warnings.Add("没有适用的安装配方；计划仅包含代理部署，未包含 NVIDIA Profile 与启动参数。");
            rollback.Add("移除本次部署的代理文件与 INI，并恢复被覆盖的同名文件。");
        }

        // 7. A profile change is never implicit. The provider must say it writes one, and Stage 5 does not
        //    write profiles at all — so a requirement here is reported, not acted on.
        if (profile.Count > 0)
            warnings.Add($"该配方要求修改 NVIDIA Profile（{profile.Count} 项）；本阶段不执行 Profile 写入。");

        var status = Determine(input, blockers, compatibilityIsExact: compatible);

        return new InstallPlan(
            Status: status,
            TargetRendererExe: input.Renderer.RendererExe,
            Api: input.Api.Api,
            ProviderId: input.ProviderId,
            ProviderVersion: input.ProviderVersion,
            Mode: mode,
            ProxyStrategy: strategy,
            FilesToDeploy: input.ProviderPayloadFiles,
            ProxyChoice: proxyChoice,
            AsiChoice: asi,
            NvidiaProfileRequirements: profile,
            LaunchArguments: launch,
            Warnings: warnings,
            Blockers: blockers,
            Compatibility: input.Compatibility,
            RollbackRequirements: rollback);
    }

    /// <summary>
    /// Ready requires all three: nothing blocking, a renderer good enough to act on, and an exact
    /// compatibility record. Anything less is a question for the user.
    /// </summary>
    private static PlanStatus Determine(InstallPlanInput input, List<string> blockers, bool compatibilityIsExact)
    {
        if (blockers.Count > 0) return PlanStatus.Blocked;
        if (!input.Renderer.CanPlanWithoutAsking) return PlanStatus.NeedsConfirmation;
        if (input.ProxyConflicts.SafeCandidates.Count == 0) return PlanStatus.Blocked;
        if (!compatibilityIsExact) return PlanStatus.NeedsConfirmation;
        return PlanStatus.Ready;
    }

    private static InstallPlan Blocked(
        InstallPlanInput input, List<string> blockers, List<string> warnings,
        List<string> profile, List<string> launch, List<string> rollback) =>
        new(
            Status: PlanStatus.Blocked,
            TargetRendererExe: input.Renderer.RendererExe,
            Api: input.Api.Api,
            ProviderId: input.ProviderId,
            ProviderVersion: input.ProviderVersion,
            Mode: InstallMode.Unknown,
            ProxyStrategy: ProxyStrategy.Unknown,
            FilesToDeploy: Array.Empty<string>(),
            ProxyChoice: null,
            AsiChoice: null,
            NvidiaProfileRequirements: profile,
            LaunchArguments: launch,
            Warnings: warnings,
            Blockers: blockers,
            Compatibility: input.Compatibility,
            RollbackRequirements: rollback);
}
