namespace DLSSGManager.Update;

/// <summary>
/// Compatibility rule for the manager's own updates.
///
/// This is not a loophole in "never assume newest equals compatible". A patch provider's new version
/// has to match a game, a driver and a graphics API — which is exactly why Stage 4 refuses to guess
/// there. A new build of this manager has no such counterpart: the release pipeline fixes the runtime
/// it targets, so the newest stable build is compatible by construction, and saying so is a reasoned
/// position rather than an assumption.
///
/// Patch providers still go through <see cref="UnknownCompatibilitySelector"/>.
/// </summary>
public sealed class SelfHostCompatibilitySelector : ICompatibilitySelector
{
    public CompatibilityDecision Decide(string providerId, string? installedVersion, string candidateVersion) =>
        CompatibilityDecision.Compatible(candidateVersion,
            "管理器自身的更新不涉及第三方环境匹配；目标运行时由发布流程固定。");
}

/// <summary>
/// Update checking for the manager itself.
///
/// A separate service, not a mode of <see cref="PatchUpdateService"/>: the two decide about different
/// objects, and the command forbids collapsing them behind one <c>if (self)</c>. They share the release
/// client, the cache and the request path — never a policy.
///
/// Per the stage scope this stops at discovery, parsing, caching, comparison and decision. Replacing
/// the running executable needs an independent updater (Stage 13), so the seam exists but is not
/// implemented, and the class says so rather than pretending otherwise.
///
/// The update source is a published GitHub Release, never the main branch: a pushed commit is not a
/// client update, and treating it as one would hand users unreviewed code.
/// </summary>
public sealed class SelfUpdateService
{
    /// <summary>The repository this manager is published from.</summary>
    public const string Repository = "PLA0185/RTX30-FrameGen-Manager";

    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(30);

    public const string CacheKeyPrefix = "self:";

    private readonly IGitHubReleaseClient _client;
    private readonly ReleaseCache _cache;
    private readonly ICompatibilitySelector _compatibility;
    private readonly Func<DateTimeOffset> _clock;

    public SelfUpdateService(
        IGitHubReleaseClient client,
        ReleaseCache cache,
        string? currentVersion = null,
        ICompatibilitySelector? compatibility = null,
        Func<DateTimeOffset>? clock = null)
    {
        _client = client;
        _cache = cache;
        _compatibility = compatibility ?? new SelfHostCompatibilitySelector();
        _clock = clock ?? (() => DateTimeOffset.Now);

        // AppVersion.Label carries the "v" prefix the badge shows; comparisons want the bare number.
        CurrentVersion = (currentVersion ?? AppVersion.Label).TrimStart('v', 'V');
    }

    /// <summary>The running build's version, without the display prefix.</summary>
    public string CurrentVersion { get; }

    /// <summary>
    /// False in Stage 4 by design: replacing a running executable needs the independent updater that a
    /// later stage builds. Nothing here writes to the installation directory.
    /// </summary>
    public bool SupportsSelfReplace => false;

    /// <summary>Asks GitHub what the newest build is and decides whether it applies to this machine.</summary>
    public async Task<UpdateCheckResult> CheckAsync(ReleaseChannel channel, bool forceRefresh, CancellationToken ct)
    {
        var now = _clock();

        // The channel is part of the key: a stable check and a prerelease check are different questions
        // and must never be served from one another's answer.
        var cacheKey = CacheKeyPrefix + channel.ToString().ToLowerInvariant();

        var releases = new List<ReleaseEntry>();
        var fromCache = false;
        var stale = false;

        if (_cache.TryGet(cacheKey, DefaultCacheTtl, forceRefresh, out var cached, out var expired))
        {
            releases = cached.Releases.ToList();
            fromCache = true;
            stale = expired;
        }

        if (releases.Count == 0 || stale || forceRefresh)
        {
            var fetched = await _client.FetchReleasesAsync(Repository, ct).ConfigureAwait(false);

            if (fetched.Ok)
            {
                releases = fetched.Releases.ToList();
                fromCache = false;
                stale = false;
                _cache.Put(cacheKey, releases);
            }
            else if (releases.Count == 0)
            {
                var state = fetched.State switch
                {
                    UpdateNetworkState.RateLimited => UpdateState.RateLimited,
                    UpdateNetworkState.Offline or UpdateNetworkState.Timeout or UpdateNetworkState.ServerError
                        => UpdateState.ProviderUnavailable,
                    _ => UpdateState.Unknown,
                };

                return UpdateCheckResult.Unknown("self", CurrentVersion, channel, now, state, fetched.State, fetched.Reason);
            }
            else
            {
                stale = true;
            }
        }

        // Drafts never reach here; stable excludes prereleases unless the caller asked for that channel.
        var candidates = releases
            .Where(r => r.IsCandidateFor(channel))
            .Select(r => r.Tag.TrimStart('v', 'V'))
            .Where(v => ReleaseVersion.TryParseNumeric(v, out _))
            .ToList();

        if (candidates.Count == 0)
        {
            return UpdateCheckResult.Unknown("self", CurrentVersion, channel, now,
                UpdateState.Unknown, UpdateNetworkState.Ok,
                "本仓库尚无正式 Release 可供比较（推送提交不等于客户端更新）。");
        }

        var latest = candidates.Aggregate((a, b) => ReleaseVersion.Compare(a, b) == VersionOrder.Greater ? a : b);
        var hasNewer = ReleaseVersion.IsNewer(latest, CurrentVersion);

        var decision = _compatibility.Decide("self", CurrentVersion, latest);
        var compatible = decision.State == CompatibilityState.Compatible ? decision.Version ?? latest : null;

        var recommended = compatible is not null && ReleaseVersion.IsNewer(compatible, CurrentVersion) ? compatible : null;

        return new UpdateCheckResult(
            TargetId: "self",
            CurrentVersion: CurrentVersion,
            LatestAvailable: latest,
            LatestCompatible: compatible,
            RecommendedVersion: recommended,
            PinnedVersion: null,
            HoldUpdates: false,
            UpdateAvailable: hasNewer,
            Compatibility: decision.State,
            ProviderHealth: null,
            Channel: channel,
            ReleaseNotes: null,
            CheckedAt: now,
            FromCache: fromCache,
            StaleCache: stale,
            State: hasNewer ? UpdateState.UpdateAvailable : UpdateState.UpToDate,
            Network: UpdateNetworkState.Ok,
            Reason: hasNewer ? $"上游最新版本 {latest}。" : "已是最新版本。");
    }

    /// <summary>The state word the interface shows for a self-update check.</summary>
    public static UpdateNotification Notify(UpdateCheckResult result) => result.State switch
    {
        UpdateState.UpToDate => new UpdateNotification(result.State, "已是最新", $"当前版本 v{result.CurrentVersion}。"),
        UpdateState.UpdateAvailable => new UpdateNotification(result.State, "有新版本",
            $"管理器可更新到 v{result.RecommendedVersion ?? result.LatestAvailable}。"),
        UpdateState.RateLimited => new UpdateNotification(result.State, "GitHub 配额受限", result.Reason),
        UpdateState.ProviderUnavailable => new UpdateNotification(result.State, "更新源不可用", result.Reason),
        _ => new UpdateNotification(result.State, "状态未知", result.Reason),
    };
}
