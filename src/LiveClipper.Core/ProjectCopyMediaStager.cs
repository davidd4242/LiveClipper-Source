using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace LiveClipper.Core;

/// <summary>Stages independent media copies only; does not register or publish a project.</summary>
public static class ProjectCopyMediaStager
{
    public sealed record Source(string ClipId, string MediaPath, string? SrtPath = null);
    public sealed record StagedFile(string Path, long Bytes, string Sha256);
    public sealed record StagedClip(string ClipId, StagedFile Media, StagedFile? Srt);
    public sealed record Result(string StagingDirectory, IReadOnlyDictionary<string, StagedClip> Clips);

    public sealed class StagingException(string stagingDirectory, Exception innerException)
        : IOException($"Media staging failed; retained staging path: {stagingDirectory}", innerException)
    {
        public string StagingDirectory { get; } = stagingDirectory;
    }

    public sealed class StagingCanceledException(string stagingDirectory, OperationCanceledException innerException,
        CancellationToken cancellationToken)
        : OperationCanceledException($"Media staging canceled; retained staging path: {stagingDirectory}",
            innerException, cancellationToken)
    {
        public string StagingDirectory { get; } = stagingDirectory;
    }

    /// <summary>
    /// The caller owns the returned directory, including partial files on failure. No cleanup is performed.
    /// Validation/root setup precedes allocation; after a staging path is chosen, failures carry that exact path.
    /// Attribute/ancestor checks reject observed reparse points, not all external filesystem TOCTOU races.
    /// </summary>
    public static async Task<Result> StageAsync(string destinationRoot, IReadOnlyList<Source> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var root = AbsolutePath(destinationRoot, nameof(destinationRoot));
        if (PathEquals(root, Path.GetPathRoot(root)!))
            throw new ArgumentException("Destination root cannot be a volume root.", nameof(destinationRoot));
        var snapshot = sources.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < snapshot.Length; i++)
        {
            var source = snapshot[i] ?? throw new ArgumentException("Source cannot be null.", nameof(sources));
            if (string.IsNullOrWhiteSpace(source.ClipId) || !ids.Add(source.ClipId))
                throw new ArgumentException("Source clip IDs must be nonempty and unique.", nameof(sources));
            snapshot[i] = source with
            {
                MediaPath = AbsolutePath(source.MediaPath, nameof(sources)),
                SrtPath = source.SrtPath is null ? null : AbsolutePath(source.SrtPath, nameof(sources))
            };
        }

        RejectReparseAncestors(root);
        Directory.CreateDirectory(root);
        RejectReparseAncestors(root);
        string staging;
        do { staging = ChildPath(root, "project-copy-" + Guid.NewGuid().ToString("N")); }
        while (File.Exists(staging) || Directory.Exists(staging));

        try
        {
            // CreateDirectory has no exclusive-create contract; external directory replacement remains a TOCTOU risk.
            Directory.CreateDirectory(staging);
            RejectReparseAncestors(staging);
            cancellationToken.ThrowIfCancellationRequested();
            var clips = new Dictionary<string, StagedClip>(StringComparer.Ordinal);
            foreach (var source in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string clipId;
                do { clipId = Guid.NewGuid().ToString("N"); } while (!ids.Add(clipId));
                var clipDirectory = ChildPath(staging, clipId);
                if (File.Exists(clipDirectory) || Directory.Exists(clipDirectory))
                    throw new IOException($"Staging clip path already exists: {clipDirectory}");
                Directory.CreateDirectory(clipDirectory);
                RejectReparseAncestors(clipDirectory);
                var mediaPath = ChildPath(clipDirectory, "media" + SafeExtension(source.MediaPath));
                var media = await CopyAsync(source.MediaPath, mediaPath, cancellationToken).ConfigureAwait(false);
                StagedFile? srt = null;
                if (source.SrtPath is { } srtSource)
                {
                    var srtPath = ChildPath(clipDirectory, "subtitles" + SafeExtension(srtSource));
                    srt = await CopyAsync(srtSource, srtPath, cancellationToken).ConfigureAwait(false);
                }
                clips.Add(source.ClipId, new(clipId, media, srt));
            }
            RejectReparseAncestors(staging);
            cancellationToken.ThrowIfCancellationRequested();
            return new(staging, new ReadOnlyDictionary<string, StagedClip>(clips));
        }
        catch (OperationCanceledException ex)
        {
            throw new StagingCanceledException(staging, ex, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new StagingException(staging, ex);
        }
    }

    private static async Task<StagedFile> CopyAsync(string sourcePath, string destinationPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectReparseAncestors(sourcePath);
        var attributes = File.GetAttributes(sourcePath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new IOException($"Source must be an ordinary file: {sourcePath}");
        // FileShare.Read denies write/delete sharing on Windows for the lifetime of this read handle.
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!source.CanSeek) throw new IOException($"Source must be seekable: {sourcePath}");
        RejectReparseAncestors(sourcePath);
        var expectedLength = source.Length;
        RejectReparseAncestors(destinationPath);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            copied = checked(copied + count);
        }
        await destination.FlushAsync(token).ConfigureAwait(false);
        if (copied != expectedLength || source.Length != expectedLength || destination.Length != expectedLength)
            throw new IOException($"Source or copied length changed: {sourcePath}");
        var sourceHash = hash.GetHashAndReset();
        destination.Position = 0;
        var actualHash = await SHA256.HashDataAsync(destination, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, actualHash))
            throw new IOException($"Copied SHA256 does not match source: {sourcePath}");
        RejectReparseAncestors(sourcePath);
        RejectReparseAncestors(destinationPath);
        token.ThrowIfCancellationRequested();
        return new(destinationPath, copied, Convert.ToHexString(actualHash));
    }

    private static string AbsolutePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An explicit absolute path is required.", parameterName);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Device paths are not accepted.", parameterName);
        var pathRoot = Path.GetPathRoot(path)!;
        var tail = path[pathRoot.Length..];
        var segments = tail.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."
            || (OperatingSystem.IsWindows() && (segment.Contains(':') || segment.EndsWith('.') || segment.EndsWith(' ')))))
            throw new ArgumentException("Path traversal or ambiguous path segments are not accepted.", parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string SafeExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || extension.Contains(Path.DirectorySeparatorChar) || extension.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Invalid source extension.", nameof(path));
        return extension;
    }

    private static string ChildPath(string parent, string name)
    {
        var child = Path.GetFullPath(Path.Combine(parent, name));
        var relative = Path.GetRelativePath(parent, child);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || PathEquals(parent, child))
            throw new IOException($"Staging path escaped its parent: {child}");
        return child;
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse paths are not accepted: {current}");
        }
    }
}
