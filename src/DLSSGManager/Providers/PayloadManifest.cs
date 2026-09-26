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
    /// Makes one path segment safe. Provider ids and versions reach this from the network, so neither is trusted
    /// to be a single well-formed name — and a version string is not guaranteed to be a version number at all.
    /// </summary>
    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "_unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');

        return cleaned.Length == 0 ? "_unknown" : cleaned;
    }
}
