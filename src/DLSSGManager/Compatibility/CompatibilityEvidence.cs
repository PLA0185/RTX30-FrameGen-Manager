using System.IO;
using System.Text.Json;
using DLSSGManager.GameDetection;
using DLSSGManager.Orchestration;

namespace DLSSGManager.Compatibility;

/// <summary>Where a piece of compatibility evidence came from.</summary>
public enum EvidenceSource
{
    Unknown,

    /// <summary>The provider's own documentation.</summary>
    OfficialProviderReadme,

    /// <summary>A published release's notes or assets.</summary>
    GitHubRelease,

    /// <summary>An issue on the upstream tracker.</summary>
    GitHubIssue,

    /// <summary>A discussion thread.</summary>
    GitHubDiscussion,

    /// <summary>Someone else reproduced it, but not on this machine.</summary>
    CommunityReproduction,

    /// <summary>This machine, by the person running it.</summary>
    LocalUserTest,

    /// <summary>This machine, by whoever maintains the project.</summary>
    ProjectMaintainerTest,
}

/// <summary>What kind of evidence it is.</summary>
public enum EvidenceType
{
    Unknown,
    Documentation,
    ReleaseNotes,
    BugReport,
    Reproduction,
    TestResult,
}

/// <summary>
/// How strongly a record is supported.
///
/// Ordered from weakest to strongest, because the failure this ladder guards against is a documented
/// claim being presented as a tested one. Note the asymmetry between the middle rungs:
/// <see cref="Documented"/> and <see cref="CommunityReported"/> are statements about <i>other people's</i>
/// systems, and neither becomes a fact about this one by repetition.
/// </summary>
public enum ValidationLevel
{
    /// <summary>Nothing supports it.</summary>
    Unverified,

    /// <summary>Upstream documents it. Says nothing about whether it works here.</summary>
    Documented,

    /// <summary>Independent people reproduced it. Still not this machine.</summary>
    CommunityReported,

    /// <summary>Would need a real run to confirm; explicitly not claimed yet.</summary>
    PendingUserValidation,

    /// <summary>Observed working on a real machine by this project's own testing.</summary>
    ProjectVerified,
}

/// <summary>A citation: what was seen, where, and when.</summary>
public sealed record EvidenceRef(
    EvidenceSource Source = EvidenceSource.Unknown,
    EvidenceType Type = EvidenceType.Unknown,
    string Reference = "",
    DateTimeOffset? ObservedAt = null)
{
    /// <summary>
    /// True only for evidence produced on the machine the record is about.
    ///
    /// A README is a claim about someone else's setup, and a community reproduction is a claim about
    /// theirs. Neither is an observation of this one, which is why only these two sources count.
    /// </summary>
    public bool IsFirstHand =>
        Source is EvidenceSource.LocalUserTest or EvidenceSource.ProjectMaintainerTest;

    public bool IsPresent => Source != EvidenceSource.Unknown;
}

/// <summary>
/// Enforces the rule that "verified" means something narrower than "documented".
///
/// <para>This is a guard rather than a convention because the tempting shortcut — marking a record
/// verified because the upstream README says it works — is exactly how a compatibility database stops
/// being trustworthy. Every write goes through <see cref="Normalize"/>, and a claim that cannot be
/// supported is <b>downgraded with a reason</b> rather than accepted or silently dropped.</para>
/// </summary>
public static class ValidationGuard
{
    /// <summary>
    /// Whether a record may claim <see cref="ValidationLevel.ProjectVerified"/>.
    ///
    /// Requires first-hand evidence <b>and</b> a timestamp. An undated first-hand claim is not usable:
    /// drivers, providers and games all move, and a verification with no date cannot be re-checked.
    /// </summary>
    public static bool CanClaimProjectVerified(EvidenceRef evidence, DateTimeOffset? lastVerified) =>
        evidence.IsFirstHand && lastVerified is not null;

    /// <summary>
    /// Clamps a claimed level to what the evidence supports, and says what it did.
    ///
    /// The downgrade target is <see cref="ValidationLevel.PendingUserValidation"/> rather than
    /// <see cref="ValidationLevel.Unverified"/>: the intent to verify is real and worth keeping visible,
    /// it simply has not happened yet.
    /// </summary>
    public static (ValidationLevel Level, string Note) Normalize(
        EvidenceRef evidence, DateTimeOffset? lastVerified, ValidationLevel claimed)
    {
        if (claimed != ValidationLevel.ProjectVerified)
            return (claimed, "");

        if (CanClaimProjectVerified(evidence, lastVerified))
            return (ValidationLevel.ProjectVerified, "");

        var reason = !evidence.IsFirstHand
            ? $"证据来自 {evidence.Source}，不是本机实测，不能标为 Project Verified。"
            : "缺少验证日期，不能标为 Project Verified。";

        return (ValidationLevel.PendingUserValidation, $"已降级为 Pending User Validation：{reason}");
    }
}

/// <summary>
/// What was learned from installing one recipe into one game.
///
/// <para>Kept as a record of attempts rather than a boolean, because both halves matter: a recipe that
/// succeeded twice and failed once is a different proposition from one that has never been tried, and
/// the failure count is the part that stops a bad recipe from being recommended forever.</para>
/// </summary>
public sealed record RecipeMemoryEntry(
    string RecipeId,
    string Game,
    string ProviderId,
    string ProviderVersion = "",
    GraphicsApi Api = GraphicsApi.Unknown,
    StoreKind Store = StoreKind.Unknown,
    string ProxyAsi = "",
    InstallMode Mode = InstallMode.Unknown,
    SmoothMotionEvidence HighestEvidence = SmoothMotionEvidence.None,
    int Successes = 0,
    int Failures = 0,
    DateTimeOffset? LastAttempt = null,
    DateTimeOffset? LastSuccess = null,
    EvidenceRef Evidence = null!,
    ValidationLevel Validation = ValidationLevel.Unverified,
    string Note = "")
{
    public EvidenceRef EvidenceRef => Evidence ?? new EvidenceRef();

    /// <summary>Stable identity for merging attempts of the same recipe against the same game.</summary>
    public string Key => $"{RecipeId}@{Game}";

    /// <summary>
    /// Whether this combination has ever actually been seen to work, as opposed to merely attempted.
    ///
    /// Requires the top rung, which (per the orchestration's evidence ladder) requires corroborating
    /// signals — so a run that merely copied files never makes a recipe "known good".
    /// </summary>
    public bool IsKnownGood => HighestEvidence == SmoothMotionEvidence.Verified && Successes > 0 && Failures == 0;

    /// <summary>Merges a new attempt into this entry.</summary>
    public RecipeMemoryEntry WithAttempt(bool succeeded, SmoothMotionEvidence evidence, DateTimeOffset when,
        EvidenceRef evidenceRef, ValidationLevel claimed, string note)
    {
        // Evidence may be upgraded but never downgraded: a first-hand observation replaces a
        // documentation citation, while a documentation citation must not overwrite something that was
        // already observed on this machine. Without this rule the first citation a recipe ever received
        // would pin its validation level forever, no matter what was learned afterwards.
        var merged = !EvidenceRef.IsPresent || (evidenceRef.IsFirstHand && !EvidenceRef.IsFirstHand)
            ? evidenceRef
            : EvidenceRef;

        var lastVerified = succeeded && merged.IsFirstHand ? when : LastSuccess;
        var (level, downgradeNote) = ValidationGuard.Normalize(merged, lastVerified, claimed);

        return this with
        {
            Successes = Successes + (succeeded ? 1 : 0),
            Failures = Failures + (succeeded ? 0 : 1),
            LastAttempt = when,
            LastSuccess = succeeded ? when : LastSuccess,
            HighestEvidence = (SmoothMotionEvidence)Math.Max((int)HighestEvidence, (int)evidence),
            Evidence = merged,
            Validation = level,
            Note = string.IsNullOrWhiteSpace(downgradeNote) ? note : $"{note} {downgradeNote}".Trim(),
        };
    }
}

/// <summary>
/// Remembers which recipes worked, per game.
///
/// Persistence follows the same rules as the compatibility matrix: versioned, atomic, and tolerant of a
/// damaged file — a corrupt memory is worthless but must never stop the application from starting.
/// </summary>
public sealed class RecipeMemoryStore
{
    public const int SchemaVersion = 1;

    private readonly Dictionary<string, RecipeMemoryEntry> _entries = new();
    private readonly string _path;
    private readonly object _gate = new();

    public RecipeMemoryStore(string? path = null) => _path = path ?? DefaultPath;

    public static string DefaultPath => Path.Combine(AppPaths.Root, "recipe-memory.json");

    public int Count { get { lock (_gate) return _entries.Count; } }

    public IReadOnlyList<RecipeMemoryEntry> All
    {
        get { lock (_gate) return _entries.Values.ToList(); }
    }

    public RecipeMemoryEntry? Find(string recipeId, string game)
    {
        lock (_gate) return _entries.TryGetValue($"{recipeId}@{game}", out var found) ? found : null;
    }

    /// <summary>
    /// Records an attempt, merging it into whatever is already known about that recipe and game.
    ///
    /// The claimed validation level is clamped by <see cref="ValidationGuard"/> before it is stored, so
    /// an unsupportable "verified" claim cannot enter the database even if a caller sends one.
    /// </summary>
    public RecipeMemoryEntry Record(
        string recipeId, string game, string providerId, bool succeeded,
        SmoothMotionEvidence evidence, DateTimeOffset when, EvidenceRef evidenceRef,
        ValidationLevel claimed, string note = "",
        string providerVersion = "", GraphicsApi api = GraphicsApi.Unknown,
        StoreKind store = StoreKind.Unknown, string proxyAsi = "", InstallMode mode = InstallMode.Unknown)
    {
        lock (_gate)
        {
            var key = $"{recipeId}@{game}";
            var entry = _entries.TryGetValue(key, out var existing)
                ? existing
                : new RecipeMemoryEntry(recipeId, game, providerId, providerVersion, api, store, proxyAsi, mode,
                    SmoothMotionEvidence.None, 0, 0, null, null, evidenceRef, ValidationLevel.Unverified, "");

            var updated = entry with
            {
                ProviderVersion = string.IsNullOrWhiteSpace(providerVersion) ? entry.ProviderVersion : providerVersion,
                Api = api == GraphicsApi.Unknown ? entry.Api : api,
                Store = store == StoreKind.Unknown ? entry.Store : store,
                ProxyAsi = string.IsNullOrWhiteSpace(proxyAsi) ? entry.ProxyAsi : proxyAsi,
                Mode = mode == InstallMode.Unknown ? entry.Mode : mode,
            };

            updated = updated.WithAttempt(succeeded, evidence, when, evidenceRef, claimed, note);
            _entries[key] = updated;
            return updated;
        }
    }

    /// <summary>Recipes worth trying for a game, best first: known-good, then tried, then untried.</summary>
    public IReadOnlyList<RecipeMemoryEntry> RankFor(string game)
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(e => string.Equals(e.Game, game, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.IsKnownGood)
                .ThenByDescending(e => e.Successes)
                .ThenBy(e => e.Failures)
                .ToList();
        }
    }

    public void Persist()
    {
        try
        {
            List<RecipeMemoryEntry> snapshot;
            lock (_gate) snapshot = _entries.Values.ToList();

            var payload = new
            {
                schemaVersion = SchemaVersion,
                entries = snapshot.Select(e => new
                {
                    recipeId = e.RecipeId,
                    game = e.Game,
                    providerId = e.ProviderId,
                    providerVersion = e.ProviderVersion,
                    api = e.Api.ToString(),
                    store = e.Store.ToString(),
                    proxyAsi = e.ProxyAsi,
                    mode = e.Mode.ToString(),
                    highestEvidence = e.HighestEvidence.ToString(),
                    successes = e.Successes,
                    failures = e.Failures,
                    lastAttempt = e.LastAttempt?.ToString("O"),
                    lastSuccess = e.LastSuccess?.ToString("O"),
                    evidenceSource = e.EvidenceRef.Source.ToString(),
                    evidenceType = e.EvidenceRef.Type.ToString(),
                    evidenceReference = e.EvidenceRef.Reference,
                    validation = e.Validation.ToString(),
                    note = e.Note,
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
            // Memory is an optimisation; failing to write it must never fail an install.
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
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;

            var loaded = new Dictionary<string, RecipeMemoryEntry>();

            foreach (var item in entries.EnumerateArray())
            {
                var recipeId = Text(item, "recipeId");
                var game = Text(item, "game");
                if (string.IsNullOrWhiteSpace(recipeId) || string.IsNullOrWhiteSpace(game)) continue;

                var entry = new RecipeMemoryEntry(
                    RecipeId: recipeId,
                    Game: game,
                    ProviderId: Text(item, "providerId"),
                    ProviderVersion: Text(item, "providerVersion"),
                    Api: Parse(item, "api", GraphicsApi.Unknown),
                    Store: Parse(item, "store", StoreKind.Unknown),
                    ProxyAsi: Text(item, "proxyAsi"),
                    Mode: Parse(item, "mode", InstallMode.Unknown),
                    HighestEvidence: Parse(item, "highestEvidence", SmoothMotionEvidence.None),
                    Successes: Number(item, "successes"),
                    Failures: Number(item, "failures"),
                    LastAttempt: When(item, "lastAttempt"),
                    LastSuccess: When(item, "lastSuccess"),
                    Evidence: new EvidenceRef(
                        Parse(item, "evidenceSource", EvidenceSource.Unknown),
                        Parse(item, "evidenceType", EvidenceType.Unknown),
                        Text(item, "evidenceReference"),
                        null),
                    Validation: Parse(item, "validation", ValidationLevel.Unverified),
                    Note: Text(item, "note"));

                loaded[entry.Key] = entry;
            }

            lock (_gate)
            {
                _entries.Clear();
                foreach (var pair in loaded) _entries[pair.Key] = pair.Value;
            }
        }
        catch
        {
            lock (_gate) _entries.Clear();
        }

        static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";

        static int Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

        static DateTimeOffset? When(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;

        static T Parse<T>(JsonElement element, string name, T fallback) where T : struct, Enum =>
            Enum.TryParse<T>(Text(element, name), out var parsed) ? parsed : fallback;
    }
}
