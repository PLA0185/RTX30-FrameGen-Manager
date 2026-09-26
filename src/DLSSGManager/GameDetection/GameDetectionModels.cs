using System.IO;

namespace DLSSGManager.GameDetection;

/// <summary>
/// How strong the evidence behind a conclusion is.
///
/// One shared ladder, because §9.2 and §9.3 order their sources the same way: a verified record beats
/// anything observed live, observation beats a guess from file names, and a user's own choice is the
/// fallback when nothing else can decide. The numeric value is the strength, so comparisons read as
/// "is this at least as good as …".
///
/// Note what is deliberately <b>not</b> here: there is no "filename looks like Shipping" level that
/// equals verification. Static heuristics live at <see cref="StaticHeuristic"/> and can never be
/// promoted to <see cref="VerifiedDatabase"/>.
/// </summary>
public enum EvidenceLevel
{
    Unknown = 0,

    /// <summary>Whatever the user said when asked. Honoured as fact, but it is not machine evidence.</summary>
    UserConfirmation = 1,

    /// <summary>Naming, layout, neighbouring files. Suggests; never proves.</summary>
    StaticHeuristic = 2,

    /// <summary>Seen in a running process. Applies only to the session it was observed in.</summary>
    RuntimeDetection = 3,

    /// <summary>Read out of a store's own metadata (appmanifest, launch configuration).</summary>
    StoreManifest = 4,

    /// <summary>A record that already matched this exact environment.</summary>
    VerifiedDatabase = 5,
}

/// <summary>What role an executable plays. The whole point is that these are not interchangeable.</summary>
public enum ProcessRole
{
    Unknown,

    /// <summary>Menu / store front-end the user clicks first.</summary>
    Launcher,

    /// <summary>The game's own process, when it is not the one rendering.</summary>
    Game,

    /// <summary>The process that loads the graphics API — where a proxy has to sit.</summary>
    Renderer,

    /// <summary>Spawned by the launcher or the renderer.</summary>
    Child,

    /// <summary>Store front-end wrapper (Steam/Epic overlay process around the game).</summary>
    StoreWrapper,
}

/// <summary>One executable and why it was considered.</summary>
public sealed record ExecutableEvidence(string Path, ProcessRole Role, EvidenceLevel Level, string Reason);

/// <summary>
/// Which executable the proxy belongs beside, with the evidence that led there.
///
/// Never a bare path: the caller has to be able to show the user <i>why</i> this file was chosen, and
/// to tell "a store told us" apart from "the name looked right".
/// </summary>
public sealed record RendererDetection(
    string? RendererExe,
    EvidenceLevel Level,
    string Reason,
    IReadOnlyList<ExecutableEvidence> Candidates)
{
    /// <summary>Nothing found, or nothing better than a guess.</summary>
    public bool IsUnknown => RendererExe is null || Level < EvidenceLevel.StaticHeuristic;

    /// <summary>Machine evidence strong enough to identify the renderer without asking anyone.</summary>
    public bool HasSufficientEvidence => RendererExe is not null && Level >= EvidenceLevel.RuntimeDetection;

    /// <summary>
    /// Whether a plan may act on this without asking first.
    ///
    /// Static heuristics are excluded: a file name is a suggestion, never permission to modify a game
    /// folder unseen. A user's own choice is included, because they have already answered the question —
    /// asking again would be re-deriving with a guess what they stated outright.
    /// </summary>
    public bool CanPlanWithoutAsking =>
        RendererExe is not null && Level is not (EvidenceLevel.Unknown or EvidenceLevel.StaticHeuristic);
}

/// <summary>Graphics API a game renders with. Only the three the manager can act on, plus unknown.</summary>
public enum GraphicsApi
{
    Unknown,
    Dx11,
    Dx12,
    Vulkan,
}

/// <summary>One piece of API evidence, kept even when a stronger source overrules it.</summary>
public sealed record GraphicsApiEvidence(GraphicsApi Api, EvidenceLevel Level, string Source, string Reason);

/// <summary>
/// The API decision plus every piece of evidence behind it.
///
/// The losing evidence is retained on purpose: a static DX11 reading that a runtime observation
/// overruled is exactly the kind of conflict worth showing, and silently dropping it would hide that
/// the two sources disagreed.
/// </summary>
public sealed record GraphicsApiDetection(
    GraphicsApi Api,
    EvidenceLevel Level,
    string Reason,
    IReadOnlyList<GraphicsApiEvidence> Evidence)
{
    /// <summary>True when sources disagree. Retained, never averaged away.</summary>
    public bool HasConflict => Evidence.Select(e => e.Api).Where(a => a != GraphicsApi.Unknown).Distinct().Count() > 1;

    /// <summary>An API is only actionable when something better than a guess established it.</summary>
    public bool CanAutoSelect => Api != GraphicsApi.Unknown && Level >= EvidenceLevel.StaticHeuristic;

    public static GraphicsApiDetection Unknown(string reason) =>
        new(GraphicsApi.Unknown, EvidenceLevel.Unknown, reason, Array.Empty<GraphicsApiEvidence>());
}

/// <summary>How far store support goes. Unimplemented stores say so rather than pretending.</summary>
public enum StoreKind
{
    Unknown,
    Steam,
    Manual,
    Epic,
    GamePass,
}

/// <summary>
/// Whether a store path actually works.
///
/// Epic and Game Pass are listed because the model has to be able to name them, but the stage rule is
/// explicit: no guessing a store from a folder name. Until a reliable manifest source is wired up they
/// stay <see cref="NotImplemented"/>, which the interface can then state honestly.
/// </summary>
public sealed record StoreSupport(StoreKind Store, bool CanEnumerate, string Reason)
{
    public static StoreSupport Supported(StoreKind store, string reason) => new(store, true, reason);

    public static StoreSupport NotImplemented(StoreKind store, string reason) => new(store, false, reason);
}

/// <summary>Engine families the layout heuristics can recognise.</summary>
public enum EngineKind
{
    Unknown,
    Unreal,
    Unity,
    Other,
}

/// <summary>Executable architecture, as read from the PE header.</summary>
public enum ArchitectureKind
{
    Unknown,
    X64,
    X86,
    Arm64,
}

/// <summary>
/// Finds the executable a proxy belongs beside, by evidence level.
///
/// The previous implementation answered this with a single heuristic (name blacklist + largest file).
/// That answer is kept, but it is now labelled for what it is, and it can no longer be mistaken for a
/// verified result.
/// </summary>
public static class RendererDetector
{
    /// <summary>Folders whose names say nothing about the game; used when naming the evidence.</summary>
    private static readonly string[] RendererFolderHints = { "binaries", "win64", "win32", "bin", "shipping" };

    /// <summary>
    /// Decides which executable renders, strongest evidence first.
    ///
    /// <para><b>A user's explicit choice short-circuits everything.</b> §9.2 lists user confirmation
    /// last, and that is where it belongs in the <i>asking</i> order — but once the user has answered,
    /// re-deriving the answer from file names would be overruling them with a guess. So the choice is
    /// honoured as fact and marked for what it is.</para>
    /// </summary>
    /// <param name="renderDir">Directory the proxy would be installed into.</param>
    /// <param name="verifiedRenderer">Renderer from a record that already matched this environment.</param>
    /// <param name="observedRenderer">Renderer seen in a running process for this game.</param>
    /// <param name="userChoice">Explicit user selection, if any.</param>
    public static RendererDetection Detect(
        string renderDir,
        string? verifiedRenderer = null,
        string? observedRenderer = null,
        string? userChoice = null)
    {
        var candidates = new List<ExecutableEvidence>();

        if (!string.IsNullOrWhiteSpace(userChoice) && File.Exists(userChoice))
        {
            candidates.Add(new ExecutableEvidence(userChoice!, ProcessRole.Renderer, EvidenceLevel.UserConfirmation,
                "用户显式指定。"));
            return new RendererDetection(userChoice, EvidenceLevel.UserConfirmation, "用户显式指定该可执行文件。", candidates);
        }

        if (!string.IsNullOrWhiteSpace(verifiedRenderer) && File.Exists(verifiedRenderer))
        {
            candidates.Add(new ExecutableEvidence(verifiedRenderer!, ProcessRole.Renderer, EvidenceLevel.VerifiedDatabase,
                "命中已验证记录。"));
            return new RendererDetection(verifiedRenderer, EvidenceLevel.VerifiedDatabase,
                "命中已验证记录（同一游戏 / 商店 / 启动方式）。", candidates);
        }

        if (!string.IsNullOrWhiteSpace(observedRenderer) && File.Exists(observedRenderer))
        {
            candidates.Add(new ExecutableEvidence(observedRenderer!, ProcessRole.Renderer, EvidenceLevel.RuntimeDetection,
                "在运行中的进程里观察到。"));
            return new RendererDetection(observedRenderer, EvidenceLevel.RuntimeDetection,
                "在用户正常启动游戏的进程中观察到该渲染进程。", candidates);
        }

        if (!Directory.Exists(renderDir))
            return new RendererDetection(null, EvidenceLevel.Unknown, "渲染目录不存在。", candidates);

        // Static fallback: everything on disk that could be the renderer, ranked, then labelled as a
        // guess. This is the level the old implementation stopped at.
        foreach (var exe in EnumerateExecutables(renderDir))
        {
            var role = ClassifyByStaticHint(exe, renderDir, out var reason);
            candidates.Add(new ExecutableEvidence(exe, role, EvidenceLevel.StaticHeuristic, reason));
        }

        var best = candidates
            .Where(c => c.Role == ProcessRole.Renderer)
            .OrderByDescending(c => ScoreStatic(c.Path, renderDir))
            .FirstOrDefault();

        if (best is null)
        {
            return new RendererDetection(null, EvidenceLevel.Unknown,
                "没有任何可执行文件具备渲染进程的静态特征，需要用户确认。", candidates);
        }

        return new RendererDetection(best.Path, EvidenceLevel.StaticHeuristic,
            "仅依据静态特征（文件名与目录结构）推断，需用户确认后才能用于自动安装。", candidates);
    }

    /// <summary>Every .exe in the folder, with obvious non-game executables dropped.</summary>
    public static IEnumerable<string> EnumerateExecutables(string renderDir)
    {
        try
        {
            return new DirectoryInfo(renderDir).GetFiles("*.exe").Select(f => f.FullName).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Labels an executable from its name alone.
    ///
    /// Names like <c>*-Win64-Shipping.exe</c> are <b>hints</b>: they raise a candidate's score and are
    /// reported as static evidence, but nothing here promotes them to a verified renderer.
    /// </summary>
    public static ProcessRole ClassifyByStaticHint(string exePath, string renderDir, out string reason)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);

        foreach (var noise in NoiseNames)
        {
            if (name.Contains(noise, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"名称含 {noise}，判为非游戏进程。";
                return ProcessRole.Launcher;
            }
        }

        if (name.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Win64", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Win32", StringComparison.OrdinalIgnoreCase))
        {
            reason = "名称呈 Unreal 打包特征（Shipping / Win64）。";
            return ProcessRole.Renderer;
        }

        // The folder name appearing in the executable name is the same signal the old picker used.
        var folder = Path.GetFileName(renderDir.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(folder) && name.Contains(folder, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"名称与所在目录（{folder}）一致。";
            return ProcessRole.Renderer;
        }

        if (RendererFolderHints.Any(h => exePath.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "位于 Binaries / Win64 等渲染目录结构中。";
            return ProcessRole.Renderer;
        }

        reason = "无足够的静态特征。";
        return ProcessRole.Unknown;
    }

    private static int ScoreStatic(string exePath, string renderDir)
    {
        var score = 0;
        var name = Path.GetFileNameWithoutExtension(exePath);

        if (name.Contains("Shipping", StringComparison.OrdinalIgnoreCase)) score += 40;
        if (exePath.Contains("Win64", StringComparison.OrdinalIgnoreCase)) score += 20;
        if (exePath.Contains("Binaries", StringComparison.OrdinalIgnoreCase)) score += 15;

        var folder = Path.GetFileName(renderDir.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(folder) && name.Contains(folder, StringComparison.OrdinalIgnoreCase)) score += 10;

        try { score += (int)Math.Min(9, new FileInfo(exePath).Length / (1024 * 1024)); }
        catch { /* Size is only a tie-breaker. */ }

        return score;
    }

    private static readonly string[] NoiseNames =
    {
        "unitycrashhandler", "crashhandler", "crashreport", "launcher", "unins", "setup",
        "vcredist", "dxsetup", "easyanticheat", "battlestri", "battleye", "be_service",
        "notification_helper", "installer", "updater", "report",
    };
}

/// <summary>
/// Decides which graphics API a game renders with, in the order §9.3 fixes.
///
/// The order is not a preference — it is the rule. A verified record outranks a live observation, which
/// outranks a static reading, which outranks asking the user. Every source is kept on the result so a
/// disagreement stays visible instead of being averaged into a confident answer.
/// </summary>
public static class GraphicsApiDetector
{
    /// <summary>Module names that reveal the API when present beside the renderer.</summary>
    private static readonly (string Module, GraphicsApi Api)[] NeighbourModules =
    {
        ("d3d12.dll", GraphicsApi.Dx12),
        ("d3d11.dll", GraphicsApi.Dx11),
        ("vulkan-1.dll", GraphicsApi.Vulkan),
    };

    public static GraphicsApiDetection Detect(
        string renderDir,
        GraphicsApi verifiedApi = GraphicsApi.Unknown,
        GraphicsApi runtimeApi = GraphicsApi.Unknown,
        GraphicsApi staticApi = GraphicsApi.Unknown,
        GraphicsApi userApi = GraphicsApi.Unknown)
    {
        var evidence = new List<GraphicsApiEvidence>();

        if (verifiedApi != GraphicsApi.Unknown)
            evidence.Add(new GraphicsApiEvidence(verifiedApi, EvidenceLevel.VerifiedDatabase, "verified-database", "命中已验证记录。"));

        if (runtimeApi != GraphicsApi.Unknown)
            evidence.Add(new GraphicsApiEvidence(runtimeApi, EvidenceLevel.RuntimeDetection, "runtime-modules", "游戏运行时加载了该 API 的模块。"));

        if (staticApi != GraphicsApi.Unknown)
            evidence.Add(new GraphicsApiEvidence(staticApi, EvidenceLevel.StaticHeuristic, "static", "静态特征（邻近 DLL / 导入表 / 引擎结构）。"));

        if (userApi != GraphicsApi.Unknown)
            evidence.Add(new GraphicsApiEvidence(userApi, EvidenceLevel.UserConfirmation, "user", "用户显式指定。"));

        // Strict order. The first non-unknown wins; the rest stay on the record.
        foreach (var level in new[]
                 {
                     EvidenceLevel.VerifiedDatabase, EvidenceLevel.RuntimeDetection,
                     EvidenceLevel.StaticHeuristic, EvidenceLevel.UserConfirmation,
                 })
        {
            var winner = evidence.FirstOrDefault(e => e.Level == level);
            if (winner is null) continue;

            var conflict = evidence.Any(e => e.Api != winner.Api && e.Api != GraphicsApi.Unknown);
            var reason = conflict
                ? $"按优先级取 {winner.Source} 的结论（存在冲突证据，已保留）。"
                : $"依据 {winner.Source}。";

            return new GraphicsApiDetection(winner.Api, level, reason, evidence);
        }

        return new GraphicsApiDetection(GraphicsApi.Unknown, EvidenceLevel.Unknown,
            "没有任何足够证据判定图形 API，必须由用户确认。", evidence);
    }

    /// <summary>
    /// Static reading from neighbouring modules.
    ///
    /// A <c>d3d12.dll</c> next to the renderer is a <b>static hint</b>, not proof: it is reported at
    /// <see cref="EvidenceLevel.StaticHeuristic"/> and can be overruled by a runtime observation.
    /// Treating its mere presence as verified DX12 is exactly what §10 forbids.
    /// </summary>
    public static GraphicsApi DetectFromNeighbourModules(string renderDir)
    {
        if (!Directory.Exists(renderDir)) return GraphicsApi.Unknown;

        var found = GraphicsApi.Unknown;
        foreach (var (module, api) in NeighbourModules)
        {
            try
            {
                if (!File.Exists(Path.Combine(renderDir, module))) continue;

                // Vulkan is decisive when present; otherwise the first DirectX module wins, and d3d12
                // is preferred over d3d11 when both ship.
                if (api == GraphicsApi.Vulkan) return GraphicsApi.Vulkan;
                if (found == GraphicsApi.Unknown || api == GraphicsApi.Dx12) found = api;
            }
            catch
            {
                // An unreadable directory is not evidence of anything.
            }
        }

        return found;
    }
}
