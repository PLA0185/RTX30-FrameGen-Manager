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
