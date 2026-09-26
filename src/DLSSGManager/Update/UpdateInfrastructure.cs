using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DLSSGManager.Update;

/// <summary>What a release lookup produced, errors included and already normalised.</summary>
public sealed record ReleaseFetchResult(
    UpdateNetworkState State,
    IReadOnlyList<ReleaseEntry> Releases,
    string Reason,
    DateTimeOffset? RateLimitResetAt)
{
    public bool Ok => State == UpdateNetworkState.Ok;

    public static ReleaseFetchResult Failed(UpdateNetworkState state, string reason) =>
        new(state, Array.Empty<ReleaseEntry>(), reason, null);
}

/// <summary>
/// Reads releases from a repository.
///
/// Behind an interface so every Stage 4 test can run offline against a fake, and so the real
/// implementation is the only place that knows about HTTP, status codes and JSON.
/// </summary>
public interface IGitHubReleaseClient
{
    Task<ReleaseFetchResult> FetchReleasesAsync(string repository, CancellationToken ct);
}

/// <summary>
/// Parses a GitHub <c>digest</c> value.
///
/// Kept as its own function because the git-tree case has to end up as
/// <see cref="DigestState.Unavailable"/> rather than being folded into "verified" or "mismatch".
/// </summary>
public static class DigestParser
{
    /// <summary>Extracts the lower-case hex from <c>sha256:...</c>, or null when absent/unusable.</summary>
    public static string? TryParseSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;

        var text = digest.Trim();
        var colon = text.IndexOf(':');
        if (colon < 0) return null;

        var algorithm = text[..colon].Trim();
        if (!string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase)) return null;

        var hex = text[(colon + 1)..].Trim();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return null;

        return hex.ToLowerInvariant();
    }

    /// <summary>
    /// Compares the bytes on disk with a published digest. <paramref name="published"/> being null means
    /// the source published nothing, which is <see cref="DigestState.Unavailable"/> — not a pass.
    /// </summary>
    public static DigestCheck Compare(string path, string? published)
    {
        if (string.IsNullOrWhiteSpace(published))
            return new DigestCheck(DigestState.Unavailable, null, null,
                "来源未提供发布摘要，无法做内容级校验（按未验证处理，不视为已通过）。");

        var expected = TryParseSha256(published);
        if (expected is null)
            return new DigestCheck(DigestState.Malformed, published, null, $"发布摘要格式无法解析：{published}");

        string actual;
        try
        {
            actual = DeploymentService.Sha256(path);
        }
        catch (Exception ex)
        {
            return new DigestCheck(DigestState.Malformed, expected, null, $"无法计算文件摘要：{ex.Message}");
        }

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            ? new DigestCheck(DigestState.Verified, expected, actual, "摘要与发布值一致。")
            : new DigestCheck(DigestState.Mismatch, expected, actual, "摘要与发布值不符，拒绝进入安装流程。");
    }
}

/// <summary>
/// Real release client.
///
/// Two Phase 0 rules are encoded here rather than in callers: the list is requested with
/// <c>per_page=100</c> (a smaller page silently truncates), and the response order is never treated as
/// version order — the entries are handed back exactly as received, for the provider to resolve.
/// </summary>
public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;

    public GitHubReleaseClient(HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? CreateDefaultClient();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RTX30-FrameGen-Manager");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<ReleaseFetchResult> FetchReleasesAsync(string repository, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return ReleaseFetchResult.Failed(UpdateNetworkState.Malformed, "仓库名称为空。");

        var url = $"https://api.github.com/repos/{repository}/releases?per_page=100";

        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return NormalizeFailure(response);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var releases = ParseReleases(json, repository);

            return releases.Count == 0
                ? new ReleaseFetchResult(UpdateNetworkState.Ok, releases, "该仓库没有任何 Release。", null)
                : new ReleaseFetchResult(UpdateNetworkState.Ok, releases, $"读取到 {releases.Count} 个 Release。", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return ReleaseFetchResult.Failed(UpdateNetworkState.Timeout, "请求超时。");
        }
        catch (HttpRequestException ex)
        {
            return ReleaseFetchResult.Failed(UpdateNetworkState.Offline, $"网络不可用：{ex.Message}");
        }
        catch (JsonException ex)
        {
            return ReleaseFetchResult.Failed(UpdateNetworkState.Malformed, $"响应不是有效 JSON：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ReleaseFetchResult.Failed(UpdateNetworkState.Unknown, $"请求失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Maps a non-success response onto a state. Rate limiting is read from the headers GitHub
    /// documents, which is what makes it distinguishable from a plain 403 rather than a guess at text.
    /// </summary>
    private ReleaseFetchResult NormalizeFailure(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;

        var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var r) ? r.FirstOrDefault() : null;
        var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var s) ? s.FirstOrDefault() : null;
        var retryAfter = response.Headers.TryGetValues("Retry-After", out var t) ? t.FirstOrDefault() : null;

        DateTimeOffset? resetAt = null;
        if (long.TryParse(reset, out var unix))
            resetAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        else if (int.TryParse(retryAfter, out var seconds))
            resetAt = _clock().AddSeconds(seconds);

        var exhausted = string.Equals(remaining?.Trim(), "0", StringComparison.Ordinal);

        if (status == 429 || (status == 403 && (exhausted || resetAt is not null)))
        {
            var detail = resetAt is null ? "" : $"，配额重置时间 {resetAt:u}";
            return new ReleaseFetchResult(UpdateNetworkState.RateLimited, Array.Empty<ReleaseEntry>(),
                $"GitHub 配额已用尽（HTTP {status}{detail}）。", resetAt);
        }

        if (status == 403)
            return ReleaseFetchResult.Failed(UpdateNetworkState.Forbidden, "GitHub 拒绝了该请求（HTTP 403）。");

        if (status >= 500)
            return ReleaseFetchResult.Failed(UpdateNetworkState.ServerError, $"GitHub 服务端错误（HTTP {status}）。");

        if (response.StatusCode == HttpStatusCode.NotFound)
            return ReleaseFetchResult.Failed(UpdateNetworkState.Forbidden, "仓库或接口不存在（HTTP 404）。");

        return ReleaseFetchResult.Failed(UpdateNetworkState.Unknown, $"GitHub 返回 HTTP {status}。");
    }

    /// <summary>Reads the fields the update layer needs; unknown fields are ignored.</summary>
    public static IReadOnlyList<ReleaseEntry> ParseReleases(string json, string repository)
    {
        var result = new List<ReleaseEntry>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var tag = element.TryGetProperty("tag_name", out var tagProperty) ? tagProperty.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var id = element.TryGetProperty("id", out var idProperty) && idProperty.TryGetInt64(out var releaseId)
                ? releaseId
                : 0;

            var draft = element.TryGetProperty("draft", out var draftProperty) && draftProperty.GetBoolean();
            var prerelease = element.TryGetProperty("prerelease", out var preProperty) && preProperty.GetBoolean();

            DateTimeOffset? published = null;
            if (element.TryGetProperty("published_at", out var publishedProperty) &&
                publishedProperty.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(publishedProperty.GetString(), out var parsed))
            {
                published = parsed;
            }

            var notes = element.TryGetProperty("body", out var bodyProperty) ? bodyProperty.GetString() : null;

            var assets = new List<ReleaseAssetInfo>();
            if (element.TryGetProperty("assets", out var assetsProperty) && assetsProperty.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProperty.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var assetId = asset.TryGetProperty("id", out var assetIdProperty) && assetIdProperty.TryGetInt64(out var aid)
                        ? aid : 0;
                    var size = asset.TryGetProperty("size", out var sizeProperty) && sizeProperty.TryGetInt64(out var sz)
                        ? sz : 0;
                    var digest = asset.TryGetProperty("digest", out var digestProperty) && digestProperty.ValueKind == JsonValueKind.String
                        ? DigestParser.TryParseSha256(digestProperty.GetString())
                        : null;

                    assets.Add(new ReleaseAssetInfo(assetId, name, size, digest));
                }
            }

            result.Add(new ReleaseEntry(repository, id, tag, draft, prerelease, published, notes, assets));
        }

        return result;
    }
}

/// <summary>
/// Collapses concurrent identical requests into one backend call.
///
/// Exists because three places can ask for the same provider at the same moment — the automatic check,
/// a manual refresh and a health probe — and spending three GitHub API calls on one answer is exactly
/// how a rate limit gets hit.
///
/// The shared call is deliberately not tied to any caller's token: one caller walking away must not
/// cancel the work the others are still waiting for.
/// </summary>
public sealed class RequestDeduplicator<TKey, TResult> where TKey : notnull
{
    private readonly Dictionary<TKey, Task<TResult>> _inflight = new();
    private readonly object _gate = new();

    /// <summary>How many times the factory was actually started. Used by tests to prove coalescing.</summary>
    public int BackendCalls { get; private set; }

    public async Task<TResult> RunAsync(TKey key, Func<CancellationToken, Task<TResult>> factory, CancellationToken ct)
    {
        Task<TResult> shared;

        lock (_gate)
        {
            if (!_inflight.TryGetValue(key, out var existing))
            {
                BackendCalls++;
                existing = Task.Run(() => factory(CancellationToken.None), CancellationToken.None);
                _inflight[key] = existing;

                // Detach the entry once it settles so a later request starts a fresh call.
                _ = existing.ContinueWith(
                    _ => { lock (_gate) _inflight.Remove(key); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            shared = existing;
        }

        // WaitAsync applies this caller's token to its own wait only.
        return await shared.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Number of calls currently in flight. Exposed for tests.</summary>
    public int InFlightCount
    {
        get { lock (_gate) return _inflight.Count; }
    }
}

/// <summary>A cache entry plus the identity it was stored under.</summary>
public sealed record CachedReleases(
    string Key,
    DateTimeOffset FetchedAt,
    IReadOnlyList<ReleaseEntry> Releases,
    IReadOnlyList<ReleaseIdentity> Identities);

/// <summary>
/// Disk-backed release cache.
///
/// Deliberately only an accelerator and an offline fallback — never the source of truth. Corrupt or
/// unreadable content is discarded rather than thrown, so a damaged file can never stop the manager
/// from starting.
///
/// Persisted with a schema version from day one, so a later format change can migrate instead of
/// guessing. No token or authorization header is ever written.
/// </summary>
public sealed class ReleaseCache
{
    /// <summary>Bumped whenever the stored shape changes.</summary>
    public const int SchemaVersion = 1;

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedReleases> _memory = new();

    public ReleaseCache(string path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The file this cache lives in. Under the app's data folder, never the source tree.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.Root, "release-cache.json");

    /// <summary>
    /// Looks a key up. Returns false when there is nothing usable; <paramref name="stale"/> is true when
    /// an entry exists but is past its TTL (still useful as an offline fallback).
    /// </summary>
    public bool TryGet(string key, TimeSpan ttl, bool forceRefresh, out CachedReleases entry, out bool stale)
    {
        entry = null!;
        stale = false;

        if (forceRefresh) return false;

        lock (_gate)
        {
            if (!_memory.TryGetValue(key, out var found)) return false;

            entry = found;
            stale = _clock() - found.FetchedAt > ttl;
            return true;
        }
    }

    public void Put(string key, IReadOnlyList<ReleaseEntry> releases)
    {
        var entry = new CachedReleases(
            key,
            _clock(),
            releases,
            releases.Select(r => new ReleaseIdentity(
                r.ReleaseId, r.Tag, r.Assets.FirstOrDefault()?.Id ?? 0, r.Assets.FirstOrDefault()?.DigestSha256)).ToList());

        lock (_gate) _memory[key] = entry;
        Persist();
    }

    /// <summary>
    /// True when a cached entry no longer describes the same bytes. A tag alone cannot decide this:
    /// the same tag can be re-uploaded with a different asset id or digest.
    /// </summary>
    public static bool IsStaleIdentity(CachedReleases cached, IReadOnlyList<ReleaseEntry> fresh)
    {
        if (fresh.Count == 0) return cached.Releases.Count > 0;

        foreach (var candidate in fresh)
        {
            var previous = cached.Identities.FirstOrDefault(i => i.ReleaseId == candidate.ReleaseId);
            if (previous is null) return true;

            var assetId = candidate.Assets.FirstOrDefault()?.Id ?? 0;
            var digest = candidate.Assets.FirstOrDefault()?.DigestSha256;

            if (previous.AssetId != assetId) return true;
            if (!string.Equals(previous.DigestSha256, digest, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Loads from disk. Any problem yields an empty cache rather than an exception.</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;

            if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != SchemaVersion)
                return; // A different shape is not an error; it is simply not usable.

            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;

            lock (_gate)
            {
                foreach (var item in entries.EnumerateArray())
                {
                    var key = item.TryGetProperty("key", out var keyProperty) ? keyProperty.GetString() : null;
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    var fetched = item.TryGetProperty("fetchedAt", out var fetchedProperty) &&
                                  DateTimeOffset.TryParse(fetchedProperty.GetString(), out var when)
                        ? when : _clock();

                    var releases = new List<ReleaseEntry>();
                    if (item.TryGetProperty("releases", out var releaseArray) && releaseArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var release in releaseArray.EnumerateArray())
                        {
                            var tag = release.TryGetProperty("tag", out var tagProperty) ? tagProperty.GetString() : null;
                            if (string.IsNullOrWhiteSpace(tag)) continue;

                            var id = release.TryGetProperty("releaseId", out var idProperty) && idProperty.TryGetInt64(out var rid) ? rid : 0;
                            var draft = release.TryGetProperty("draft", out var d) && d.GetBoolean();
                            var pre = release.TryGetProperty("prerelease", out var p) && p.GetBoolean();

                            var assets = new List<ReleaseAssetInfo>();
                            if (release.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var asset in assetArray.EnumerateArray())
                                {
                                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                                    if (string.IsNullOrWhiteSpace(name)) continue;

                                    var aid = asset.TryGetProperty("id", out var a) && a.TryGetInt64(out var parsedAid) ? parsedAid : 0;
                                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var parsedSize) ? parsedSize : 0;
                                    var digest = asset.TryGetProperty("digestSha256", out var dg) ? dg.GetString() : null;

                                    assets.Add(new ReleaseAssetInfo(aid, name, size, digest));
                                }
                            }

                            releases.Add(new ReleaseEntry(key, id, tag, draft, pre, null, null, assets));
                        }
                    }

                    _memory[key] = new CachedReleases(key, fetched, releases,
                        releases.Select(r => new ReleaseIdentity(r.ReleaseId, r.Tag,
                            r.Assets.FirstOrDefault()?.Id ?? 0, r.Assets.FirstOrDefault()?.DigestSha256)).ToList());
                }
            }
        }
        catch
        {
            // A damaged cache is worthless but harmless: continue with an empty one.
            lock (_gate) _memory.Clear();
        }
    }

    /// <summary>Writes atomically, so a crash mid-write cannot leave a half file behind.</summary>
    public void Persist()
    {
        try
        {
            List<CachedReleases> snapshot;
            lock (_gate) snapshot = _memory.Values.ToList();

            var payload = new
            {
                schemaVersion = SchemaVersion,
                entries = snapshot.Select(e => new
                {
                    key = e.Key,
                    fetchedAt = e.FetchedAt.ToString("O"),
                    releases = e.Releases.Select(r => new
                    {
                        releaseId = r.ReleaseId,
                        tag = r.Tag,
                        draft = r.IsDraft,
                        prerelease = r.IsPrerelease,
                        assets = r.Assets.Select(a => new { id = a.Id, name = a.Name, size = a.Size, digestSha256 = a.DigestSha256 }),
                    }),
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
            // Caching is best-effort; failing to persist must never surface as an error.
        }
    }
}
