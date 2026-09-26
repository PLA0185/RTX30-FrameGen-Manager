namespace DLSSGManager.Update;

/// <summary>
/// Which stream a release belongs to.
///
/// The core has to be able to express all three from the start, because "stable" and "prerelease" are
/// not interchangeable at the point of decision: a stable channel must never pick a prerelease by
/// accident, and an experimental build is something the user opts into explicitly.
/// </summary>
public enum ReleaseChannel
{
    Stable,
    Prerelease,
    Experimental,
}

/// <summary>Normalised outcome of a network attempt, so callers never branch on exception text.</summary>
public enum UpdateNetworkState
{
    Ok,

    /// <summary>No transport at all (DNS failure, connection refused, offline).</summary>
    Offline,

    Timeout,

    /// <summary>GitHub says the caller has exhausted its quota — distinct from being offline.</summary>
    RateLimited,

    /// <summary>403 without rate-limit headers: the request is not permitted.</summary>
    Forbidden,

    /// <summary>5xx from the API: transient, worth retrying later.</summary>
    ServerError,

    /// <summary>A response arrived but could not be understood.</summary>
    Malformed,

    Unknown,
}

/// <summary>Whether a candidate version may be recommended for this machine.</summary>
public enum CompatibilityState
{
    Compatible,
    Incompatible,

    /// <summary>
    /// Not established. This is the honest default in Stage 4: the compatibility matrix is Stage 5, so
    /// "no evidence of a problem" must not be reported as "compatible".
    /// </summary>
    Unknown,
}

/// <summary>How the user's version policy constrains the automatic target.</summary>
public enum PinState
{
    /// <summary>No constraint: the recommended target may move.</summary>
    NotPinned,

    /// <summary>Still reports newer versions, but an automatic target may not pass the pinned one.</summary>
    Pinned,

    /// <summary>Still checks and still reports, but no automatic target is chosen at all.</summary>
    Held,
}

/// <summary>The single state the interface renders. One value, no boolean soup.</summary>
public enum UpdateState
{
    UpToDate,
    UpdateAvailable,
    Pinned,
    Held,
    Unknown,
    ProviderUnavailable,
    RateLimited,
}

/// <summary>Outcome of checking a payload against a published digest.</summary>
public enum DigestState
{
    /// <summary>Computed bytes matched the published digest.</summary>
    Verified,

    /// <summary>Digest was published and the bytes did not match. Never installable.</summary>
    Mismatch,

    /// <summary>
    /// The distribution publishes no digest for this content (the git-tree case). Deliberately distinct
    /// from <see cref="Verified"/>: an absent digest is not a verification, and reporting it as one
    /// would overstate what is known.
    /// </summary>
    Unavailable,

    /// <summary>A digest was present but not in a form that could be parsed.</summary>
    Malformed,
}

/// <summary>One downloadable file attached to a release.</summary>
/// <param name="Id">Asset id — part of release identity, because the same tag can be re-uploaded.</param>
/// <param name="DigestSha256">Lower-case hex from the API's <c>digest</c> field, when present.</param>
public sealed record ReleaseAssetInfo(long Id, string Name, long Size, string? DigestSha256);

/// <summary>
/// A release as the API described it.
///
/// Carries the identity fields rather than just the tag, because a tag can be re-pointed at different
/// bytes: caching or comparing on <c>tag_name</c> alone would treat a swapped payload as unchanged.
/// </summary>
public sealed record ReleaseEntry(
    string Repository,
    long ReleaseId,
    string Tag,
    bool IsDraft,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt,
    string? Notes,
    IReadOnlyList<ReleaseAssetInfo> Assets)
{
    /// <summary>Digest of a named asset, or null when the release has no such asset or no digest.</summary>
    public string? DigestFor(string assetName) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))?.DigestSha256;

    /// <summary>
    /// Whether this release may be considered on the given channel.
    ///
    /// Drafts are excluded everywhere: an unpublished release is not a release. A stable channel also
    /// excludes prereleases; the prerelease channel takes both, since a prerelease channel that
    /// refused stable builds would be useless during a rollback.
    /// </summary>
    public bool IsCandidateFor(ReleaseChannel channel) => channel switch
    {
        ReleaseChannel.Stable => !IsDraft && !IsPrerelease,
        ReleaseChannel.Prerelease => !IsDraft,
        ReleaseChannel.Experimental => !IsDraft,
        _ => false,
    };
}

/// <summary>Identity of the bytes behind a version, as far as the source can prove.</summary>
public sealed record ReleaseIdentity(long ReleaseId, string Tag, long AssetId, string? DigestSha256);

/// <summary>Result of a digest check, carrying both sides so a log can show what disagreed.</summary>
public sealed record DigestCheck(DigestState State, string? Expected, string? Actual, string Message)
{
    public bool IsInstallable => State is DigestState.Verified or DigestState.Unavailable;
}

/// <summary>What the compatibility layer decided about one candidate.</summary>
public sealed record CompatibilityDecision(CompatibilityState State, string? Version, string Reason)
{
    public static CompatibilityDecision Unknown(string reason) => new(CompatibilityState.Unknown, null, reason);

    public static CompatibilityDecision Compatible(string version, string reason) =>
        new(CompatibilityState.Compatible, version, reason);

    public static CompatibilityDecision Incompatible(string version, string reason) =>
        new(CompatibilityState.Incompatible, version, reason);
}

/// <summary>
/// Decides whether a candidate version is safe to recommend.
///
/// A seam rather than an implementation: the compatibility matrix (Stage 5) plugs in here. Stage 4
/// ships only the "unknown" implementation, so nothing in this stage can invent a compatibility claim.
/// </summary>
public interface ICompatibilitySelector
{
    CompatibilityDecision Decide(string providerId, string? installedVersion, string candidateVersion);
}

/// <summary>
/// The Stage 4 selector: everything is <see cref="CompatibilityState.Unknown"/>.
///
/// This is the whole point — <c>LatestAvailable</c> and <c>LatestCompatible</c> stay separate values,
/// and "we have not checked" never becomes "it is fine".
/// </summary>
public sealed class UnknownCompatibilitySelector : ICompatibilitySelector
{
    public CompatibilityDecision Decide(string providerId, string? installedVersion, string candidateVersion) =>
        CompatibilityDecision.Unknown("尚无兼容性证据（兼容性矩阵属 Stage 5）");
}

/// <summary>The user's version constraint for one update target.</summary>
public sealed record VersionPolicy(PinState State, string? PinnedVersion)
{
    public static VersionPolicy None { get; } = new(PinState.NotPinned, null);

    public static VersionPolicy Pin(string version) => new(PinState.Pinned, version);

    public static VersionPolicy Hold() => new(PinState.Held, null);
}

/// <summary>
/// A provider's own rule for turning a release into a version label.
///
/// Separate from <see cref="Providers.IPatchProvider"/> on purpose: providers that already existed
/// keep working untouched, and a provider that needs its own tag semantics implements this alongside.
/// Nothing here assumes semver — the method returns whatever label the provider recognises.
/// </summary>
public interface IReleaseVersionResolver
{
    /// <summary>The version a release represents, or null when this release carries none.</summary>
    string? ResolveVersion(ReleaseEntry release);
}

/// <summary>
/// Everything an update check concluded, in one testable value.
///
/// The three version fields are deliberately separate: <see cref="LatestAvailable"/> is what upstream
/// has, <see cref="LatestCompatible"/> is what this machine may safely run (null when unproven), and
/// <see cref="RecommendedVersion"/> is what the policy allows acting on.
/// </summary>
public sealed record UpdateCheckResult(
    string TargetId,
    string? CurrentVersion,
    string? LatestAvailable,
    string? LatestCompatible,
    string? RecommendedVersion,
    string? PinnedVersion,
    bool HoldUpdates,
    bool UpdateAvailable,
    CompatibilityState Compatibility,
    Providers.ProviderHealth? ProviderHealth,
    ReleaseChannel Channel,
    string? ReleaseNotes,
    DateTimeOffset CheckedAt,
    bool FromCache,
    bool StaleCache,
    UpdateState State,
    UpdateNetworkState Network,
    string Reason)
{
    /// <summary>A result that could not be established, preserving why.</summary>
    public static UpdateCheckResult Unknown(
        string targetId, string? currentVersion, ReleaseChannel channel, DateTimeOffset checkedAt,
        UpdateState state, UpdateNetworkState network, string reason) =>
        new(targetId, currentVersion, null, null, null, null, false, false,
            CompatibilityState.Unknown, null, channel, null, checkedAt, false, false, state, network, reason);
}

/// <summary>The user-facing summary of a check. One state word plus one reason.</summary>
public sealed record UpdateNotification(UpdateState State, string Title, string Detail);
