using DLSSGManager.Providers;

namespace DLSSGManager.Update;

/// <summary>
/// Compares two version labels without assuming a scheme.
///
/// Phase 0 established that the managed projects do not share one: MFG's tags carry no version at all.
/// A global <c>new Version(tag)</c> would throw, and guessing an order for an unparseable label would
/// be worse than admitting ignorance — so the numeric case is handled and everything else is
/// <see cref="VersionOrder.Unordered"/>, which callers must treat as "no ordering established".
/// </summary>
public enum VersionOrder
{
    Less,
    Equal,
    Greater,

    /// <summary>No ordering could be established. Never treated as any of the other three.</summary>
    Unordered,
}

/// <summary>Numeric-dotted comparison, with an honest answer when it does not apply.</summary>
public static class ReleaseVersion
{
    /// <summary>True when the label is purely dotted numbers, e.g. <c>0.3.5</c> or <c>1.2</c>.</summary>
    public static bool TryParseNumeric(string? version, out int[] parts)
    {
        parts = Array.Empty<int>();

        if (string.IsNullOrWhiteSpace(version)) return false;

        var text = version.Trim().TrimStart('v', 'V');
        var fields = text.Split('.');
        if (fields.Length == 0) return false;

        var parsed = new int[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!int.TryParse(fields[i], out parsed[i])) return false;
        }

        parts = parsed;
        return true;
    }

    /// <summary>
    /// Orders two labels. Returns <see cref="VersionOrder.Unordered"/> unless both sides are numeric:
    /// ordering a label like <c>smfix</c> against <c>0.3.5</c> is not a comparison this layer can make.
    /// </summary>
    public static VersionOrder Compare(string? left, string? right)
    {
        if (!TryParseNumeric(left, out var a) || !TryParseNumeric(right, out var b))
            return VersionOrder.Unordered;

        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x < y ? VersionOrder.Less : VersionOrder.Greater;
        }

        return VersionOrder.Equal;
    }

    /// <summary>Strictly newer. Unordered pairs are never "newer".</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        Compare(candidate, current) == VersionOrder.Greater;
}

/// <summary>
/// Update checking for third-party patches.
///
/// Split from <see cref="SelfUpdateService"/> by design rather than by a flag: the two share the
/// release client, cache and deduplication, but they decide about different objects and must not share
/// install or rollback logic. This class never installs anything — it resolves versions and hands the
/// work to the provider, which delegates to the Stage 2 deployment path.
/// </summary>
public sealed class PatchUpdateService
{
    /// <summary>How long a cached release list is considered fresh for automatic checks.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(30);

    public const string CacheKeyPrefix = "patch:";

    private readonly ProviderRegistry _registry;
    private readonly IGitHubReleaseClient _client;
    private readonly ReleaseCache _cache;
    private readonly ICompatibilitySelector _compatibility;
    private readonly Func<DateTimeOffset> _clock;

    public PatchUpdateService(
        ProviderRegistry registry,
        IGitHubReleaseClient client,
        ReleaseCache cache,
        ICompatibilitySelector? compatibility = null,
        Func<DateTimeOffset>? clock = null)
    {
        _registry = registry;
        _client = client;
        _cache = cache;
        _compatibility = compatibility ?? new UnknownCompatibilitySelector();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Resolves what is installed, what upstream has, what this machine may run, and what the user's
    /// policy actually allows acting on.
    ///
    /// Never throws for an expected failure: an unknown provider, an unreachable API or a rate limit all
    /// come back as a result carrying the reason, because a caller drawing a status line must not have
    /// to catch.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(
        string providerId,
        GameEntry? game,
        VersionPolicy policy,
        ReleaseChannel channel,
        bool forceRefresh,
        CancellationToken ct)
    {
        var now = _clock();

        var provider = _registry.Get(providerId);
        if (provider is null)
        {
            // No fallback to another provider: being asked about X and answered about Y would be worse
            // than being told X is unknown.
            return UpdateCheckResult.Unknown(providerId, null, channel, now,
                UpdateState.Unknown, UpdateNetworkState.Unknown, $"未注册的 Provider：{providerId}");
        }

        var health = _registry.GetHealth(providerId);

        // No game context means nothing is installed for anyone: the question "what is installed" only
        // has an answer per game, and the provider's signature says so.
        var current = game is null ? null : provider.GetInstalledVersion(game);

        if (!health.IsUsable && health.State == ProviderHealthState.Broken)
            return UpdateCheckResult.Unknown(providerId, current, channel, now,
                UpdateState.ProviderUnavailable, UpdateNetworkState.Unknown, health.Reason);

        var repository = provider.Metadata.UpstreamRepository;
        var cacheKey = CacheKeyPrefix + providerId;
        var releases = new List<ReleaseEntry>();
        var fromCache = false;
        var staleCache = false;
        var network = UpdateNetworkState.Ok;
        var reason = "";

        if (_cache.TryGet(cacheKey, DefaultCacheTtl, forceRefresh, out var cached, out var stale))
        {
            releases = cached.Releases.ToList();
            fromCache = true;
            staleCache = stale;

            if (!stale)
                network = UpdateNetworkState.Ok;
        }

        if (releases.Count == 0 || staleCache || forceRefresh)
        {
            var fetched = await _client.FetchReleasesAsync(repository, ct).ConfigureAwait(false);
            network = fetched.State;

            if (fetched.Ok)
            {
                releases = fetched.Releases.ToList();
                fromCache = false;
                staleCache = false;
                _cache.Put(cacheKey, releases);
            }
            else
            {
                reason = fetched.Reason;

                // A cached list is still worth reporting when the API is unreachable; say so instead of
                // pretending there is no answer.
                if (releases.Count > 0)
                {
                    staleCache = true;
                    reason += "（显示上次缓存的结果）";
                }
                else
                {
                    var state = fetched.State switch
                    {
                        UpdateNetworkState.RateLimited => UpdateState.RateLimited,
                        UpdateNetworkState.Offline or UpdateNetworkState.Timeout or UpdateNetworkState.ServerError
                            => UpdateState.ProviderUnavailable,
                        _ => UpdateState.Unknown,
                    };

                    return UpdateCheckResult.Unknown(providerId, current, channel, now, state, network, reason);
                }
            }
        }

        // Version discovery. A provider that knows its own tag semantics answers directly; otherwise the
        // tag is used only when it is actually numeric. Releases are never assumed to be pre-sorted.
        var candidates = new List<string>();
        foreach (var release in releases.Where(r => r.IsCandidateFor(channel)))
        {
            if (provider is IReleaseVersionResolver resolver)
            {
                var resolved = resolver.ResolveVersion(release);
                if (!string.IsNullOrWhiteSpace(resolved)) candidates.Add(resolved!);
                continue;
            }

            if (ReleaseVersion.TryParseNumeric(release.Tag.TrimStart('v', 'V'), out _))
                candidates.Add(release.Tag.TrimStart('v', 'V'));
        }

        if (candidates.Count == 0)
        {
            // The git-tree case: releases may exist with no usable tag, or none at all. Fall back to the
            // provider's own probe rather than calling it broken.
            var probed = await SafeProbeAsync(provider, ct).ConfigureAwait(false);

            return probed is null
                ? UpdateCheckResult.Unknown(providerId, current, channel, now,
                    UpdateState.Unknown, network, reason.Length > 0 ? reason : "无法确定上游版本。")
                : Build(providerId, current, probed, health, channel, now, fromCache, staleCache, policy, network,
                        reason.Length > 0 ? reason : "版本来自 Provider 探测（该来源不提供可解析的 Release 标签）。");
        }

        var latestAvailable = candidates.Aggregate((a, b) => ReleaseVersion.Compare(a, b) == VersionOrder.Greater ? a : b);

        return Build(providerId, current, latestAvailable, health, channel, now, fromCache, staleCache, policy, network,
            reason.Length > 0 ? reason : $"读取到 {releases.Count} 个 Release。");
    }

    /// <summary>Delegates the download to the provider, which owns its own sources and verification.</summary>
    public Task<OpResult> DownloadAsync(string providerId, string destination, IProgress<string>? progress, CancellationToken ct)
    {
        var provider = _registry.Get(providerId);
        return provider is null
            ? Task.FromResult(Fail($"未注册的 Provider：{providerId}"))
            : provider.DownloadAsync(destination, progress, ct);

        static OpResult Fail(string message)
        {
            var r = new OpResult();
            r.Fail(message);
            return r;
        }
    }

    /// <summary>A probe failure is a network condition, not a fault of the provider object itself.</summary>
    private static async Task<string?> SafeProbeAsync(IPatchProvider provider, CancellationToken ct)
    {
        try
        {
            var release = await provider.CheckLatestAsync(forceRefresh: false, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(release?.Version) ? null : release!.Version;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the result, keeping the three version values distinct and applying the pin/hold policy.
    ///
    /// The compatibility selector gets the final word on what may be recommended: with the Stage 4
    /// selector everything is unknown, so <c>LatestCompatible</c> stays null and nothing is recommended
    /// — the honest answer while the compatibility matrix does not exist yet.
    /// </summary>
    private UpdateCheckResult Build(
        string providerId,
        string? current,
        string? latestAvailable,
        ProviderHealth health,
        ReleaseChannel channel,
        DateTimeOffset now,
        bool fromCache,
        bool staleCache,
        VersionPolicy policy,
        UpdateNetworkState network,
        string reason)
    {
        string? latestCompatible = null;
        var compatibility = CompatibilityState.Unknown;

        if (latestAvailable is not null)
        {
            var decision = _compatibility.Decide(providerId, current, latestAvailable);
            compatibility = decision.State;
            if (decision.State == CompatibilityState.Compatible) latestCompatible = decision.Version ?? latestAvailable;

            if (decision.State == CompatibilityState.Incompatible)
                reason += $"；最新版被判定为不兼容：{decision.Reason}";
        }

        var hasNewer = latestAvailable is not null &&
                       (current is null || ReleaseVersion.IsNewer(latestAvailable, current));

        // The recommendation is the newest *compatible* version the policy permits; never merely the
        // newest that exists.
        string? recommended = latestCompatible;

        if (recommended is not null && string.Equals(recommended, current, StringComparison.OrdinalIgnoreCase))
            recommended = null;

        if (policy.State == PinState.Pinned && policy.PinnedVersion is not null)
        {
            // A pin caps the automatic target; it does not hide the newer release.
            var beyondPin = recommended is not null && ReleaseVersion.IsNewer(recommended, policy.PinnedVersion);
            if (beyondPin) recommended = policy.PinnedVersion;

            if (recommended is not null && string.Equals(recommended, current, StringComparison.OrdinalIgnoreCase))
                recommended = null;
        }

        if (policy.State == PinState.Held)
            recommended = null;

        var state = policy.State switch
        {
            PinState.Held => UpdateState.Held,
            PinState.Pinned when hasNewer => UpdateState.Pinned,
            _ when hasNewer && recommended is not null => UpdateState.UpdateAvailable,
            _ when hasNewer => UpdateState.Unknown,
            _ => UpdateState.UpToDate,
        };

        if (hasNewer && recommended is null && policy.State == PinState.NotPinned)
            reason += "；新版本存在但无兼容性证据，因此不给出推荐目标。";

        return new UpdateCheckResult(
            TargetId: providerId,
            CurrentVersion: current,
            LatestAvailable: latestAvailable,
            LatestCompatible: latestCompatible,
            RecommendedVersion: recommended,
            PinnedVersion: policy.PinnedVersion,
            HoldUpdates: policy.State == PinState.Held,
            UpdateAvailable: hasNewer,
            Compatibility: compatibility,
            ProviderHealth: health,
            Channel: channel,
            ReleaseNotes: null,
            CheckedAt: now,
            FromCache: fromCache,
            StaleCache: staleCache,
            State: state,
            Network: network,
            Reason: reason);
    }

    /// <summary>Maps a result onto one state word plus a reason, for whatever renders it.</summary>
    public static UpdateNotification Notify(UpdateCheckResult result) => result.State switch
    {
        UpdateState.UpToDate => new UpdateNotification(result.State, "已是最新",
            $"当前版本 {result.CurrentVersion ?? "(未知)"} 已是最新。"),
        UpdateState.UpdateAvailable => new UpdateNotification(result.State, "有可用更新",
            $"可更新到 {result.RecommendedVersion ?? result.LatestAvailable}。"),
        UpdateState.Pinned => new UpdateNotification(result.State, "已固定版本",
            $"上游有 {result.LatestAvailable}，但自动目标固定在 {result.PinnedVersion}。"),
        UpdateState.Held => new UpdateNotification(result.State, "已暂停更新",
            $"上游有 {result.LatestAvailable}，但你已暂停自动更新。"),
        UpdateState.RateLimited => new UpdateNotification(result.State, "GitHub 配额受限", result.Reason),
        UpdateState.ProviderUnavailable => new UpdateNotification(result.State, "来源不可用", result.Reason),
        _ => new UpdateNotification(result.State, "状态未知", result.Reason),
    };
}
