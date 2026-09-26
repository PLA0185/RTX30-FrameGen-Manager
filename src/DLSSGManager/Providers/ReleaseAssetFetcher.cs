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
/// Archive extraction that cannot write outside its destination, cannot exhaust the disk, and leaves the
/// destination unchanged when it fails.
///
/// <para>Four rules, each of which exists because of a specific way this goes wrong:</para>
/// <list type="number">
/// <item><b>Zip slip.</b> Entries carry their own relative paths, and an entry named <c>..\..\something</c>
/// would otherwise be written wherever it points. Every entry is resolved to a full path and rejected unless
/// it stays inside the destination — checked before anything is opened.</item>
/// <item><b>Size limits.</b> An archive that expands to a hundred gigabytes fills the disk while it is being
/// unpacked. Entry count, per-file size, total expanded size and compression ratio are all bounded, so a bomb
/// becomes a refusal rather than a full drive.</item>
/// <item><b>Staging.</b> Everything is unpacked into a private folder first and moved into place only after
/// the whole archive succeeded, so a failure never leaves a half-extracted payload where the installer would
/// find it.</item>
/// <item><b>Cleanup.</b> The staging folder is removed in a <c>finally</c>, on the failure path as well.</item>
/// </list>
/// </summary>
public static class SafeZip
{
    /// <summary>More files than any real payload contains; beyond this the archive is not our shape.</summary>
    public const int MaxEntries = 2048;

    /// <summary>512 MiB for one file: the largest legitimate payload file is a DLL of a few tens of MB.</summary>
    public const long MaxSingleFileBytes = 512L * 1024 * 1024;

    /// <summary>2 GiB expanded in total.</summary>
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 200:1. Real payload zips compress their DLLs at roughly 2–4:1, so a ratio in the hundreds means the
    /// archive is mostly repetition — which is what a bomb looks like.
    /// </summary>
    public const int MaxCompressionRatio = 200;

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="destination"/> through a staging directory.
    ///
    /// <para>The two size ceilings are parameters whose defaults are the production constants. A limit that can only
    /// be reached by spending real time and disk is a limit no test can cover cheaply — and one that reaches 512 MiB
    /// is worse than that: a test that tries it can derail the whole suite. Callers keep passing three arguments and
    /// get the real values; tests pass small ones.</para>
    /// </summary>
    public static bool TryExtract(string zipPath, string destination, out string error,
        long maxSingleFileBytes = MaxSingleFileBytes, long maxTotalBytes = MaxTotalBytes)
    {
        error = "";
        string? staging = null;

        try
        {
            var root = Path.GetFullPath(destination);
            Directory.CreateDirectory(root);

            using var archive = ZipFile.OpenRead(zipPath);

            // ── 1. Everything is checked before a single byte is written.
            var rejection = Validate(archive, root, maxSingleFileBytes, maxTotalBytes);
            if (rejection is not null)
            {
                error = rejection;
                return false;
            }

            // ── 2. Unpack privately, so nothing partial ever appears at a final path.
            staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var staged = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                entry.ExtractToFile(staged, overwrite: true);
            }

            // ── 3. Move into place only now that the whole archive unpacked.
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(staging, file);
                var target = Path.GetFullPath(Path.Combine(root, relative));

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"解包失败：{ex.Message}";
            return false;
        }
        finally
        {
            if (staging is not null)
            {
                try
                {
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                }
                catch
                {
                    // Best effort: an empty staging folder left behind is not worth failing a good extract over.
                }
            }
        }
    }

    /// <summary>
    /// Checks an archive against the path and size rules. Returns null when it may be extracted, otherwise the
    /// reason it may not.
    /// </summary>
    private static string? Validate(ZipArchive archive, string root,
        long maxSingleFileBytes, long maxTotalBytes)
    {
        if (archive.Entries.Count > MaxEntries)
            return $"压缩包条目过多（{archive.Entries.Count} > {MaxEntries}），已拒绝。";

        long total = 0;

        foreach (var entry in archive.Entries)
        {
            // Directory entries end with a separator and carry no content.
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!IsInside(root, target))
                return $"压缩包中的条目会写到目标目录之外，已拒绝：{entry.FullName}";

            if (entry.Length > maxSingleFileBytes)
                return $"压缩包中的单个文件过大，已拒绝：{entry.FullName}（{entry.Length} 字节）";

            total += entry.Length;
            if (total > maxTotalBytes)
                return $"压缩包解压后总大小超过上限，已拒绝（累计 {total} 字节）。";

            if (entry.CompressedLength > 0)
            {
                var ratio = entry.Length / Math.Max(1, entry.CompressedLength);
                if (ratio > MaxCompressionRatio)
                    return $"压缩包压缩比异常，已拒绝：{entry.FullName}（约 {ratio}:1）";
            }
        }

        return null;
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
