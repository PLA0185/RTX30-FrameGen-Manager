using System.IO;
using System.Security.Cryptography;

namespace DLSSGManager.Providers;

/// <summary>One file in a payload, identified by content rather than by name alone.</summary>
public sealed record PayloadFile(string RelativePath, string Sha256, long Size);

/// <summary>
/// What a downloaded payload actually contains.
///
/// <para>Produced by <b>scanning the extracted folder</b>, not by reading the archive's own listing: what
/// matters is what is now on disk. An entry that produced no file must not appear here, and a file the
/// archive did not mention must not be invisible.</para>
///
/// <para>This is where a plan's file list should come from. A caller-supplied list of expected names
/// describes what somebody hoped the payload would contain; the manifest describes what it does contain, and
/// the gap between those two is exactly where an installation goes wrong quietly.</para>
/// </summary>
public sealed record PayloadManifest(IReadOnlyList<PayloadFile> Files)
{
    public static PayloadManifest Empty { get; } = new(Array.Empty<PayloadFile>());

    /// <summary>Relative paths of everything in the payload, in scan order.</summary>
    public IReadOnlyList<string> FileNames => Files.Select(f => f.RelativePath).ToList();

    public bool Contains(string relativePath) => Find(relativePath) is not null;

    public PayloadFile? Find(string relativePath) =>
        Files.FirstOrDefault(f => string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>Builds a manifest by walking an extracted payload folder.</summary>
public static class PayloadScanner
{
    /// <summary>Refuses to describe an absurdly large tree — the same reason the archive limits exist.</summary>
    public const int MaxFiles = 1024;

    public static PayloadManifest Scan(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return PayloadManifest.Empty;

        var root = Path.GetFullPath(directory);
        var files = new List<PayloadFile>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (files.Count >= MaxFiles) break;

                var full = Path.GetFullPath(path);

                // Defensive: the scanner walks a folder the extractor already confined, but a symlink or a
                // junction could still point outside, and describing a file we would never install is useless.
                if (!SafeZip.IsInside(root, full)) continue;

                var info = new FileInfo(full);
                files.Add(new PayloadFile(
                    Path.GetRelativePath(root, full),
                    Hash(full),
                    info.Exists ? info.Length : 0));
            }
        }
        catch
        {
            // An unreadable tree yields what was read so far; the caller decides whether that is enough.
        }

        return new PayloadManifest(files);
    }

    private static string Hash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// Where a provider's downloaded payload lives.
///
/// <para><b>Keyed on provider and version together.</b> A payload only means something together with the version
/// it came from: sharing one folder across versions makes "which version is this?" unanswerable, and both the
/// plan and its manifest depend on that answer. It also means switching providers cannot leave one provider's
/// files sitting where another will look for its own.</para>
/// </summary>
public static class PayloadPaths
{
    /// <summary>Root under the application's data folder — not a game folder, and not the source folder.</summary>
    public static string Root => Path.Combine(AppPaths.Root, "payloads");

    /// <summary>The final folder for one provider and version.</summary>
    public static string For(string providerId, string version) =>
        Path.Combine(Root, Segment(providerId), Segment(version));

    /// <summary>
    /// A staging folder <b>beside</b> the target, so the swap into place stays on one volume.
    ///
    /// <para>A payload is downloaded here first and moved into place only after it verifies. Moving across
    /// volumes degrades to copy-then-delete, which is exactly the non-atomic behaviour this avoids.</para>
    /// </summary>
    public static string Staging(string providerId, string version) =>
        For(providerId, version) + ".staging-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The other version folders for this provider, newest first, so a caller can clean up old payloads without
    /// touching the one in use. Staging folders are excluded: they belong to a download in progress.
    /// </summary>
    public static IReadOnlyList<string> OtherVersions(string providerId, string keepVersion)
    {
        var parent = Path.Combine(Root, Segment(providerId));

        if (!Directory.Exists(parent)) return Array.Empty<string>();

        var keep = Segment(keepVersion);

        return Directory.EnumerateDirectories(parent)
            .Where(d => !Path.GetFileName(d).Contains(".staging-", StringComparison.OrdinalIgnoreCase))
            .Where(d => !string.Equals(Path.GetFileName(d), keep, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>
    /// Deletes version folders for a provider that nothing refers to any more, and returns what it removed.
    ///
    /// <para><paramref name="keepVersion"/> and every entry of <paramref name="referenced"/> survive. One provider's
    /// payload can back the deployments of several games, and each records its own version — deleting a version that
    /// is still referenced would break those deployments. So this refuses to guess: it removes only what the caller
    /// has said is unused, which is why it takes the referenced set rather than deciding for itself.</para>
    ///
    /// <para>Staging folders are never touched: they belong to a download in progress. A folder that cannot be
    /// removed is skipped rather than allowed to abort the rest — cleanup is best-effort, and the next run sees it.</para>
    /// </summary>
    public static IReadOnlyList<string> RemoveUnreferenced(
        string providerId, string keepVersion, IEnumerable<string> referenced)
    {
        var keep = new HashSet<string>(referenced.Select(Segment), StringComparer.OrdinalIgnoreCase);

        var removed = new List<string>();

        foreach (var folder in OtherVersions(providerId, keepVersion))
        {
            if (keep.Contains(Path.GetFileName(folder))) continue;

            try
            {
                Directory.Delete(folder, recursive: true);
                removed.Add(folder);
            }
            catch (IOException)
            {
                // Locked or in use; leave it for the next cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours to remove; leave it rather than failing the caller's operation over it.
            }
        }

        return removed;
    }

    /// <summary>
    /// Makes one path segment safe. Provider ids and versions reach this from the network, so neither is trusted
    /// to be a single well-formed name — and a version string is not guaranteed to be a version number at all.
    /// </summary>
    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "_unknown";

        // **本地化占位符不是版本名 —— 两道防线，缺一不可。**
        //
        // ① **来源**（根因，已修）：`ModSource.Version` 读不到标签时返回**空串**而不是 `Loc.T(...)`
        //    ⇒ 新写入的数据干净，两种语言收敛到同一个 `_unknown`。
        // ② **这里**（兜底，本次补上）：**旧记录里已经存着占位符**。修复只影响新写入 ——
        //    英文环境装过的用户，`DeploymentInfo.ModVersion` 里就是 `"Unknown"`；它非空白，
        //    `GetInstalledVersion` 原样返回，于是**仍然**会落到 `payloads/mfg-smooth/Unknown`。
        //    **纯 ASCII 的 `"Unknown"` 在形状上完全正常**，只有值匹配才拦得住（Pass D 报出）。
        //
        // **值匹配的名单从语言表取，而不是硬编码** —— 加一种语言就自动覆盖一种，
        // 而硬编码的名单会在那时静默漏掉。这正是「形状判据」当初想解决的问题，只是它解决不了 ASCII。
        foreach (var language in Languages.All)
        {
            if (string.Equals(value, Strings.For(language)["ModSource.UnknownVersion"],
                    StringComparison.OrdinalIgnoreCase))
                return "_unknown";
        }

        // 形状判据作为**第三道防线**：任何别处混进来的本地化文本（含非 ASCII 的）仍然挡得住。
        if (value.Any(c => c > 127)) return "_unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');

        return cleaned.Length == 0 ? "_unknown" : cleaned;
    }
}
