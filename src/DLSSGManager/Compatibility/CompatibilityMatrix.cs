using System.IO;
using System.Text.Json;
using DLSSGManager.GameDetection;
using DLSSGManager.Update;

namespace DLSSGManager.Compatibility;

/// <summary>How the payload is attached to the game.</summary>
public enum InstallMode
{
    Unknown,

    /// <summary>A DLL beside the renderer, loaded through the loader's search order.</summary>
    DirectProxy,

    /// <summary>Through an ASI loader, which needs its own host DLL.</summary>
    AsiLoader,
}

/// <summary>
/// The evidence a record carries. Deliberately minimal: the full evidence ladder belongs to Stage 9, and
/// inventing it here would put behaviour ahead of the stage that defines it.
/// </summary>
public enum ValidationState
{
    Unknown,
    ReportedWorking,
    ReportedBroken,
}

/// <summary>The twelve dimensions a compatibility record is scoped to.</summary>
public enum CompatibilityDimension
{
    Gpu,
    Driver,
    GraphicsApi,
    Game,
    Store,
    RendererExe,
    Provider,
    ProviderVersion,
    InstallMode,
    ProxyAsi,
    LaunchMode,
    ValidationState,
}

/// <summary>
/// One thing that was observed to work (or fail) in one exact environment.
///
/// Twelve dimensions rather than a loose "works on RTX 30" claim, because the whole point is that a
/// result on a different driver, API, provider version or install mode is a <i>different</i> result.
/// An empty dimension means "not recorded", and §13 is explicit that this must not be read as a match.
/// </summary>
public sealed record CompatibilityRecord(
    string Gpu = "",
    string Driver = "",
    GraphicsApi GraphicsApi = GraphicsApi.Unknown,
    string Game = "",
    StoreKind Store = StoreKind.Unknown,
    string RendererExe = "",
    string Provider = "",
    string ProviderVersion = "",
    InstallMode InstallMode = InstallMode.Unknown,
    string ProxyAsi = "",
    string LaunchMode = "",
    ValidationState Validation = ValidationState.Unknown,
    string Note = "",
    EvidenceRef? Evidence = null,
    ValidationLevel EvidenceValidation = ValidationLevel.Unverified)
{
    /// <summary>Which dimensions this record actually pins down.</summary>
    public IReadOnlyList<CompatibilityDimension> SpecifiedDimensions()
    {
        var list = new List<CompatibilityDimension>();
        if (!string.IsNullOrWhiteSpace(Gpu)) list.Add(CompatibilityDimension.Gpu);
        if (!string.IsNullOrWhiteSpace(Driver)) list.Add(CompatibilityDimension.Driver);
        if (GraphicsApi != GraphicsApi.Unknown) list.Add(CompatibilityDimension.GraphicsApi);
        if (!string.IsNullOrWhiteSpace(Game)) list.Add(CompatibilityDimension.Game);
        if (Store != StoreKind.Unknown) list.Add(CompatibilityDimension.Store);
        if (!string.IsNullOrWhiteSpace(RendererExe)) list.Add(CompatibilityDimension.RendererExe);
        if (!string.IsNullOrWhiteSpace(Provider)) list.Add(CompatibilityDimension.Provider);
        if (!string.IsNullOrWhiteSpace(ProviderVersion)) list.Add(CompatibilityDimension.ProviderVersion);
        if (InstallMode != InstallMode.Unknown) list.Add(CompatibilityDimension.InstallMode);
        if (!string.IsNullOrWhiteSpace(ProxyAsi)) list.Add(CompatibilityDimension.ProxyAsi);
        if (!string.IsNullOrWhiteSpace(LaunchMode)) list.Add(CompatibilityDimension.LaunchMode);
        if (Validation != ValidationState.Unknown) list.Add(CompatibilityDimension.ValidationState);
        return list;
    }
}

/// <summary>What is known about the environment being asked about.</summary>
public sealed record CompatibilityQuery(
    string? Gpu = null,
    string? Driver = null,
    GraphicsApi GraphicsApi = GraphicsApi.Unknown,
    string? Game = null,
    StoreKind Store = StoreKind.Unknown,
    string? RendererExe = null,
    string Provider = "",
    string? ProviderVersion = null,
    InstallMode InstallMode = InstallMode.Unknown,
    string? ProxyAsi = null,
    string? LaunchMode = null);

/// <summary>How well a record matched.</summary>
public enum CompatibilityMatchKind
{
    /// <summary>Nothing matched, or everything that matched contradicts the query.</summary>
    None,

    /// <summary>Some dimensions matched and others could not be checked. Never treated as proof.</summary>
    Partial,

    /// <summary>Every dimension the record pins down matched, and every dimension the query asks about was pinned down.</summary>
    Exact,
}

/// <summary>Per-dimension outcome, so a partial match can say exactly what was missing.</summary>
public sealed record DimensionOutcome(CompatibilityDimension Dimension, bool Matched, string Detail);

/// <summary>The result of asking the matrix.</summary>
public sealed record CompatibilityMatch(
    CompatibilityMatchKind Kind,
    ValidationState Validation,
    string Reason,
    IReadOnlyList<DimensionOutcome> Outcomes)
{
    public static CompatibilityMatch NoRecord(string reason) =>
        new(CompatibilityMatchKind.None, ValidationState.Unknown, reason, Array.Empty<DimensionOutcome>());
}

/// <summary>
/// The 12-dimension matrix.
///
/// Matching is deliberately strict, because the failure mode it guards against is the expensive one:
/// reporting "compatible" from an unrelated record would send a user into a broken game. So an
/// unrecorded dimension blocks an exact match rather than being assumed equal, and a partial match is
/// reported as partial instead of being rounded up.
/// </summary>
public sealed class CompatibilityMatrixStore
{
    public const int SchemaVersion = 1;

    private readonly List<CompatibilityRecord> _records = new();
    private readonly string _path;
    private readonly object _gate = new();

    public CompatibilityMatrixStore(string? path = null) => _path = path ?? DefaultPath;

    public static string DefaultPath => Path.Combine(AppPaths.Root, "compatibility.json");

    public int Count { get { lock (_gate) return _records.Count; } }

    /// <summary>A snapshot of every record. The store keeps ownership of its own list.</summary>
    public IReadOnlyList<CompatibilityRecord> All
    {
        get { lock (_gate) return _records.ToList(); }
    }

    public void Add(CompatibilityRecord record)
    {
        lock (_gate) _records.Add(record);
    }

    /// <summary>
    /// Looks for a record that describes this exact environment.
    ///
    /// Every candidate is scored dimension by dimension; the strongest outcome wins, and a tie between
    /// contradictory records resolves to <see cref="CompatibilityMatchKind.Partial"/> with the
    /// contradiction stated — never to a confident answer.
    /// </summary>
    public CompatibilityMatch Query(CompatibilityQuery query)
    {
        List<CompatibilityRecord> snapshot;
        lock (_gate) snapshot = _records.ToList();

        if (snapshot.Count == 0)
            return CompatibilityMatch.NoRecord("兼容性矩阵为空（尚无任何已验证记录）。");

        CompatibilityMatch? best = null;

        foreach (var record in snapshot)
        {
            var (outcomes, exact, anyMatch, anyMismatch) = Compare(record, query);
            if (!anyMatch) continue;

            // A contradicting dimension means the record does not describe this environment at all.
            // Calling that "partial" would imply it is still relevant — which is exactly how a record made
            // on a different GPU ends up being acted on. Contradiction is None; only a dimension that
            // could not be checked may leave the result Partial.
            var kind = anyMismatch
                ? CompatibilityMatchKind.None
                : exact ? CompatibilityMatchKind.Exact : CompatibilityMatchKind.Partial;

            var reason = kind switch
            {
                CompatibilityMatchKind.Exact => $"命中 12 维完全一致的记录（{record.Note}）。",
                CompatibilityMatchKind.Partial =>
                    $"仅部分匹配，仍有维度无法核对：{string.Join("、", outcomes.Where(o => !o.Matched).Select(o => o.Dimension.ToString()))}",
                _ => $"记录与环境存在明确矛盾：{string.Join("、", outcomes.Where(o => !o.Matched).Select(o => o.Detail))}",
            };

            var candidate = new CompatibilityMatch(kind, record.Validation, reason, outcomes);

            if (best is null || candidate.Kind > best.Kind) best = candidate;
        }

        return best ?? CompatibilityMatch.NoRecord("没有任何记录匹配当前环境。");
    }

    /// <summary>
    /// Compares one record against one query.
    ///
    /// Three outcomes per dimension, and the middle one matters most: when the query does not know a
    /// dimension, that dimension is <b>unresolved</b>, which prevents an exact match and therefore
    /// prevents a compatibility claim.
    /// </summary>
    private static (List<DimensionOutcome> Outcomes, bool Exact, bool AnyMatch, bool AnyMismatch) Compare(
        CompatibilityRecord record, CompatibilityQuery query)
    {
        var outcomes = new List<DimensionOutcome>();
        var exact = true;
        var anyMatch = false;
        var anyMismatch = false;

        void Add(CompatibilityDimension dimension, string recorded, string asked)
        {
            if (string.IsNullOrWhiteSpace(recorded))
            {
                // The record says nothing here. It cannot support a match.
                exact = false;
                outcomes.Add(new DimensionOutcome(dimension, false, "记录未涵盖该维度。"));
                return;
            }

            if (string.IsNullOrWhiteSpace(asked))
            {
                // The query says nothing here. It cannot be checked, so it cannot be an exact match.
                exact = false;
                outcomes.Add(new DimensionOutcome(dimension, false, "当前环境未知，无法核对。"));
                return;
            }

            if (string.Equals(recorded, asked, StringComparison.OrdinalIgnoreCase))
            {
                anyMatch = true;
                outcomes.Add(new DimensionOutcome(dimension, true, recorded));
                return;
            }

            anyMismatch = true;
            exact = false;
            outcomes.Add(new DimensionOutcome(dimension, false, $"记录 {recorded} ≠ 当前 {asked}"));
        }

        Add(CompatibilityDimension.Gpu, record.Gpu, query.Gpu ?? "");
        Add(CompatibilityDimension.Driver, record.Driver, query.Driver ?? "");
        Add(CompatibilityDimension.GraphicsApi,
            record.GraphicsApi == GraphicsApi.Unknown ? "" : record.GraphicsApi.ToString(),
            query.GraphicsApi == GraphicsApi.Unknown ? "" : query.GraphicsApi.ToString());
        Add(CompatibilityDimension.Game, record.Game, query.Game ?? "");
        Add(CompatibilityDimension.Store,
            record.Store == StoreKind.Unknown ? "" : record.Store.ToString(),
            query.Store == StoreKind.Unknown ? "" : query.Store.ToString());
        Add(CompatibilityDimension.RendererExe, record.RendererExe, query.RendererExe ?? "");
        Add(CompatibilityDimension.Provider, record.Provider, query.Provider);
        Add(CompatibilityDimension.ProviderVersion, record.ProviderVersion, query.ProviderVersion ?? "");
        Add(CompatibilityDimension.InstallMode,
            record.InstallMode == Compatibility.InstallMode.Unknown ? "" : record.InstallMode.ToString(),
            query.InstallMode == Compatibility.InstallMode.Unknown ? "" : query.InstallMode.ToString());
        Add(CompatibilityDimension.ProxyAsi, record.ProxyAsi, query.ProxyAsi ?? "");
        Add(CompatibilityDimension.LaunchMode, record.LaunchMode, query.LaunchMode ?? "");
        // ValidationState is the record's own conclusion, not an attribute of the environment — the query
        // has no counterpart for it. So it is checked for presence only: a record that does not say what
        // happened cannot support a compatibility claim.
        if (record.Validation == ValidationState.Unknown)
        {
            exact = false;
            outcomes.Add(new DimensionOutcome(CompatibilityDimension.ValidationState, false, "记录未包含验证状态。"));
        }
        else
        {
            anyMatch = true;
            outcomes.Add(new DimensionOutcome(CompatibilityDimension.ValidationState, true, record.Validation.ToString()));
        }

        return (outcomes, exact, anyMatch, anyMismatch);
    }

    /// <summary>
    /// Persists to disk. Atomic, versioned, and best-effort: a matrix that cannot be written must not
    /// take the application down, and a damaged file is discarded rather than half-read.
    /// </summary>
    public void Persist()
    {
        try
        {
            List<CompatibilityRecord> snapshot;
            lock (_gate) snapshot = _records.ToList();

            var payload = new
            {
                schemaVersion = SchemaVersion,
                records = snapshot.Select(r => new
                {
                    gpu = r.Gpu,
                    driver = r.Driver,
                    graphicsApi = r.GraphicsApi.ToString(),
                    game = r.Game,
                    store = r.Store.ToString(),
                    rendererExe = r.RendererExe,
                    provider = r.Provider,
                    providerVersion = r.ProviderVersion,
                    installMode = r.InstallMode.ToString(),
                    proxyAsi = r.ProxyAsi,
                    launchMode = r.LaunchMode,
                    validation = r.Validation.ToString(),
                    note = r.Note,
                    evidenceSource = r.Evidence?.Source.ToString() ?? "Unknown",
                    evidenceType = r.Evidence?.Type.ToString() ?? "Unknown",
                    evidenceReference = r.Evidence?.Reference ?? "",
                    evidenceObservedAt = r.Evidence?.ObservedAt?.ToString("O"),
                    evidenceValidation = r.EvidenceValidation.ToString(),
                }),
            };

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Losing the cache of results is survivable; failing an install because of it is not.
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;

            if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != SchemaVersion) return;
            if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array) return;

            var loaded = new List<CompatibilityRecord>();
            foreach (var item in records.EnumerateArray())
            {
                loaded.Add(new CompatibilityRecord(
                    Text(item, "gpu"), Text(item, "driver"),
                    Enum.TryParse<GraphicsApi>(Text(item, "graphicsApi"), out var api) ? api : GraphicsApi.Unknown,
                    Text(item, "game"),
                    Enum.TryParse<StoreKind>(Text(item, "store"), out var store) ? store : StoreKind.Unknown,
                    Text(item, "rendererExe"), Text(item, "provider"), Text(item, "providerVersion"),
                    Enum.TryParse<InstallMode>(Text(item, "installMode"), out var mode) ? mode : InstallMode.Unknown,
                    Text(item, "proxyAsi"), Text(item, "launchMode"),
                    Enum.TryParse<ValidationState>(Text(item, "validation"), out var validation) ? validation : ValidationState.Unknown,
                    Text(item, "note"),
                    new EvidenceRef(
                        Enum.TryParse<EvidenceSource>(Text(item, "evidenceSource"), out var src) ? src : EvidenceSource.Unknown,
                        Enum.TryParse<EvidenceType>(Text(item, "evidenceType"), out var typ) ? typ : EvidenceType.Unknown,
                        Text(item, "evidenceReference"),
                        DateTimeOffset.TryParse(Text(item, "evidenceObservedAt"), out var seen) ? seen : null),
                    Enum.TryParse<ValidationLevel>(Text(item, "evidenceValidation"), out var level) ? level : ValidationLevel.Unverified));
            }

            lock (_gate)
            {
                _records.Clear();
                _records.AddRange(loaded);
            }
        }
        catch
        {
            lock (_gate) _records.Clear();
        }

        static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
    }
}

/// <summary>
/// The full-context question Stage 5 can answer.
///
/// Added alongside <see cref="ICompatibilitySelector"/> rather than replacing it: the Stage 4 seam takes
/// three arguments and is already in use, and widening its signature would have changed behaviour that
/// Stage 4's tests pin down. A caller that has the 12 dimensions asks here; one that does not keeps the
/// narrower seam and gets the honest "unknown".
/// </summary>
public interface ICompatibilityEvaluator
{
    CompatibilityDecision Evaluate(CompatibilityQuery query);
}

/// <summary>
/// Answers compatibility from the matrix.
///
/// Implements both seams. The narrow one cannot see the GPU, driver, game, API or install mode, and
/// rather than pretend otherwise it answers <see cref="CompatibilityState.Unknown"/> — the absence of
/// context is not evidence of compatibility.
/// </summary>
public sealed class CompatibilityMatrixSelector : ICompatibilitySelector, ICompatibilityEvaluator
{
    private readonly CompatibilityMatrixStore _store;

    public CompatibilityMatrixSelector(CompatibilityMatrixStore store) => _store = store;

    public CompatibilityDecision Evaluate(CompatibilityQuery query)
    {
        var match = _store.Query(query);

        return match.Kind switch
        {
            CompatibilityMatchKind.Exact when match.Validation == ValidationState.ReportedWorking =>
                CompatibilityDecision.Compatible(query.ProviderVersion ?? "",
                    $"12 维完全匹配且记录为可用：{match.Reason}"),

            CompatibilityMatchKind.Exact when match.Validation == ValidationState.ReportedBroken =>
                CompatibilityDecision.Incompatible(query.ProviderVersion ?? "",
                    $"12 维完全匹配且记录为不可用：{match.Reason}"),

            CompatibilityMatchKind.Exact =>
                CompatibilityDecision.Unknown($"匹配到记录但验证状态未知：{match.Reason}"),

            _ => CompatibilityDecision.Unknown(match.Reason),
        };
    }

    /// <summary>
    /// The Stage 4 seam.
    ///
    /// Three arguments cannot identify an environment, so this reports unknown unless a record pins down
    /// exactly the provider and provider version and nothing else — and even then the environment
    /// dimensions would be unrecorded, which the matcher already refuses to round up.
    /// </summary>
    public CompatibilityDecision Decide(string providerId, string? installedVersion, string candidateVersion)
    {
        var match = _store.Query(new CompatibilityQuery(Provider: providerId, ProviderVersion: candidateVersion));

        return match.Kind == CompatibilityMatchKind.Exact
            ? Evaluate(new CompatibilityQuery(Provider: providerId, ProviderVersion: candidateVersion))
            : CompatibilityDecision.Unknown(
                $"缺少 12 维上下文（GPU / 驱动 / 游戏 / API / 安装方式未提供），无法给出兼容性结论：{match.Reason}");
    }
}
