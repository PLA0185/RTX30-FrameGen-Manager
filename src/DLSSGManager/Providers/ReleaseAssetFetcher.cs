using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using DLSSGManager.Update;

namespace DLSSGManager.Providers;

/// <summary>Outcome of fetching and unpacking one release asset.</summary>
public sealed record AssetFetchResult(
    bool Ok,
    string Message,
    string? ExtractedDirectory,
    DigestCheck? Digest)
{
    public static AssetFetchResult Failed(string message) => new(false, message, null, null);
}

/// <summary>
/// Fetches a release asset and unpacks it into a mod-source folder.
///
/// Behind an interface so a provider can be exercised without a network, and so the security rules
/// below are the only place that touches HTTP.
/// </summary>
public interface IReleaseAssetFetcher
{
    Task<AssetFetchResult> FetchAndExtractAsync(
        string url, string destinationDirectory, string? expectedSha256, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// Real asset fetcher.
///
/// Three rules, each of which exists because of a specific way this can go wrong:
/// <list type="number">
/// <item><b>Every hop is checked, not just the first.</b> GitHub answers a release download with a
/// redirect to a CDN host, so validating only the URL we were handed would let the redirect decide where
/// the bytes actually come from. Redirects are followed manually, up to a small limit, re-checking the
/// host policy each time.</item>
/// <item><b>A published digest is verified before anything is unpacked.</b> The zip's bytes are checked
/// against the release's own <c>digest</c> field, using the shared parser so "no digest published" stays
/// distinguishable from "digest matched".</item>
/// <item><b>Extraction cannot write outside the target folder.</b> Archive entries are resolved to full
/// paths and rejected unless they stay under the destination — the same escaping-file-name problem the
/// deployment backups guard against, arriving through a different door.</item>
/// </list>
/// </summary>
public sealed class ReleaseAssetFetcher : IReleaseAssetFetcher
{
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;

    public ReleaseAssetFetcher(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RTX30-FrameGen-Manager");
    }

    public async Task<AssetFetchResult> FetchAndExtractAsync(
        string url, string destinationDirectory, string? expectedSha256, IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return AssetFetchResult.Failed("下载地址为空。");

        var staging = Path.Combine(Path.GetTempPath(), "dlssg_asset_" + Guid.NewGuid().ToString("N")[..8] + ".zip");

        try
        {
            using var response = await GetFollowingRedirectsAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var rejected = (int)response.StatusCode >= 300 && (int)response.StatusCode < 400;
                return AssetFetchResult.Failed(rejected
                    ? "下载被重定向到未被允许的地址（已按逐跳策略拒绝）。"
                    : $"下载失败：HTTP {(int)response.StatusCode}。");
            }

            progress?.Report("正在下载 Release 资产…");

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(staging))
            {
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            // Verify before unpacking: an unverified archive must never reach the filesystem.
            var digest = DigestParser.Compare(staging, expectedSha256);
            if (digest.State == DigestState.Mismatch)
                return new AssetFetchResult(false, "资产摘要与发布值不符，已丢弃。", null, digest);

            if (!SafeZip.TryExtract(staging, destinationDirectory, out var error))
                return new AssetFetchResult(false, error, null, digest);

            return new AssetFetchResult(true, "资产已下载并解包。", destinationDirectory, digest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AssetFetchResult.Failed($"下载或解包失败：{ex.Message}");
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { /* Temp cleanup is best effort. */ }
        }
    }

    /// <summary>
    /// Follows redirects by hand so each destination is checked against the host policy.
    ///
    /// A redirect to a host outside the allow-list is treated as a failure rather than followed: the
    /// point of the allow-list is to decide where bytes may come from, and a redirect is a decision about
    /// exactly that.
    /// </summary>
    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(string url, CancellationToken ct)
    {
        var current = new Uri(url);

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!ModFetcher.IsAllowedAddress(current))
                throw new InvalidOperationException($"下载地址被拒绝：{current.Host}");

            var response = await _http
                .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is < 300 or >= 400 || response.Headers.Location is null) return response;

            var location = response.Headers.Location;
            response.Dispose();
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new InvalidOperationException("重定向次数过多，已中止。");
    }
}

/// <summary>
/// Archive extraction that cannot write outside its destination.
///
/// Zip entries carry their own relative paths, and an entry named <c>..\..\something</c> would otherwise
/// be written wherever it points. Every entry is resolved to a full path first and rejected unless it
/// stays inside the target directory — checked before the entry is opened, so a rejected archive leaves
/// nothing behind.
/// </summary>
public static class SafeZip
{
    public static bool TryExtract(string zipPath, string destination, out string error)
    {
        error = "";

        try
        {
            var root = Path.GetFullPath(destination);
            Directory.CreateDirectory(root);

            using var archive = ZipFile.OpenRead(zipPath);

            foreach (var entry in archive.Entries)
            {
                // Directory entries end with a separator and carry no content.
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!IsInside(root, target))
                {
                    error = $"压缩包中的条目会写到目标目录之外，已拒绝：{entry.FullName}";
                    return false;
                }
            }

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                entry.ExtractToFile(target, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"解包失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>True when <paramref name="candidate"/> is the root itself or sits underneath it.</summary>
    public static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = Path.GetFullPath(candidate);

        if (string.Equals(normalized, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;

        return normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
