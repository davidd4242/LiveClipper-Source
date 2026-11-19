using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiveClipper.Core;

// Success proves program-file identity only, not daemon ownership or uninstall authorization.
public sealed record OllamaManagedInstallResult(
    string Status,
    string Reason,
    string? StagingDirectory,
    string ProgramDirectory,
    string? ExecutableRelativePath);

public static class OllamaManagedInstaller
{
    public const string Installed = "installed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // privateRoot is an explicit caller-owned application directory, never a discovered installation.
    public static async Task<OllamaManagedInstallResult> InstallFromPackageAsync(
        string privateRoot, string zipPath, CancellationToken token = default)
    {
        var root = AbsoluteDirectory(privateRoot, nameof(privateRoot));
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        if (!Path.IsPathFullyQualified(zipPath))
            throw new ArgumentException("An absolute package path is required.", nameof(zipPath));
        var packagePath = Path.GetFullPath(zipPath);
        var program = Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName);
        string? stage = null;
        try
        {
            token.ThrowIfCancellationRequested();
            EnsureDirectoryChain(root, allowMissing: true);
            EnsureAbsent(program);
            EnsureRegularFileChain(packagePath);
            Directory.CreateDirectory(root);
            EnsureDirectoryChain(root);
            EnsureAbsent(program);

            stage = Path.Combine(root, ".ollama-stage-" + Guid.NewGuid().ToString("N"));
            EnsureAbsent(stage);
            Directory.CreateDirectory(stage);
            EnsureDirectoryChain(stage);
            var stagedProgram = Path.Combine(stage, OllamaManagedInstallation.ProgramDirectoryName);
            Directory.CreateDirectory(stagedProgram);

            // Keep this same handle through hashing and extraction. On Windows FileShare.Read denies
            // concurrent writers and delete/rename/replacement; extraction never reopens the path.
            await using (var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                EnsureRegularFileChain(packagePath);
                if (package.Length != OllamaManagedInstallation.SupportedPackageSize)
                    throw new InvalidDataException("package-size-mismatch");
                await VerifyPackageSha256Async(package, token);
                if (package.Length != OllamaManagedInstallation.SupportedPackageSize)
                    throw new InvalidDataException("package-size-changed");
                package.Position = 0;
                using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
                await ExtractIntoEmptyProgramAsync(archive, stagedProgram, token);
            }

            var manifest = await CreateManifestAsync(stagedProgram, token);
            var manifestPath = Path.Combine(stagedProgram, OllamaManagedInstallation.ManifestFileName);
            EnsureDirectoryChain(stagedProgram);
            await using (var output = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(output, manifest, ManifestJson, token);

            var identity = await OllamaManagedInstallation.InspectAsync(stage, token);
            if (identity.Status != OllamaManagedInstallation.ManagedValid)
                throw new InvalidDataException("staged-inspection-failed: " + identity.Reason);
            token.ThrowIfCancellationRequested();
            EnsureDirectoryChain(root);
            EnsureDirectoryChain(stagedProgram);
            EnsureAbsent(program);
            // Sibling paths on the same filesystem: rename only; never merge, overwrite, or copy.
            Directory.Move(stagedProgram, program);
            return new(Installed, "program-files-published", stage, program, manifest.ExecutableRelativePath);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(Cancelled, "cancelled", stage, program, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or CryptographicException or NotSupportedException or ArgumentException or OverflowException)
        {
            // The owned stage is deliberately retained, including partial files, as failure evidence.
            return new(Failed, error.Message, stage, program, null);
        }
    }

    internal static async Task VerifyPackageSha256Async(Stream package, CancellationToken token = default)
    {
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(package, token));
        if (!string.Equals(hash, OllamaManagedInstallation.SupportedPackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("package-sha256-mismatch");
    }

    // Kept separate so small real ZIPs can exercise extraction safety without bypassing package trust.
    // This helper does not create a manifest or publish an installation.
    internal static async Task ExtractIntoEmptyProgramAsync(ZipArchive archive, string program, CancellationToken token = default)
    {
        program = AbsoluteDirectory(program, nameof(program));
        EnsureDirectoryChain(program);
        if (Directory.EnumerateFileSystemEntries(program).Any())
            throw new InvalidDataException("extraction-directory-not-empty");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Path, bool Directory)>();
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var relative = directory ? entry.FullName[..^1] : entry.FullName;
            var parts = SafeParts(relative);
            var attributes = unchecked((uint)entry.ExternalAttributes);
            var unixType = (attributes >> 16) & 0xF000;
            if ((attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                (unixType != 0 && unixType != 0x8000 && unixType != 0x4000) ||
                (unixType == 0x4000 && !directory) || (unixType == 0x8000 && directory) ||
                ((attributes & (uint)FileAttributes.Directory) != 0 && !directory))
                throw new InvalidDataException("archive-entry-is-not-regular: " + entry.FullName);
            if (directory && entry.Length != 0)
                throw new InvalidDataException("archive-directory-has-data: " + entry.FullName);
            var path = Path.GetFullPath(Path.Combine(program, Path.Combine(parts)));
            if (!path.StartsWith(program + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("archive-path-escapes-stage: " + entry.FullName);
            if (!paths.Add(string.Join('/', parts)))
                throw new InvalidDataException("archive-duplicate-path: " + entry.FullName);
            entries.Add((entry, path, directory));
        }

        foreach (var item in entries)
        {
            token.ThrowIfCancellationRequested();
            EnsureDirectoryChain(program);
            var parent = item.Directory ? item.Path : Path.GetDirectoryName(item.Path)!;
            EnsureDirectoryChain(parent, allowMissing: true);
            Directory.CreateDirectory(parent);
            EnsureDirectoryChain(parent);
            if (item.Directory) continue;
            EnsureAbsent(item.Path);
            await using var source = item.Entry.Open();
            await using var target = new FileStream(item.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token)) != 0)
            {
                written = checked(written + count);
                if (written > item.Entry.Length) throw new InvalidDataException("archive-entry-length-exceeded");
                await target.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (written != item.Entry.Length) throw new InvalidDataException("archive-entry-length-mismatch");
        }
    }

    internal static async Task<OllamaManagedManifest> CreateManifestAsync(string program, CancellationToken token = default)
    {
        var files = new List<OllamaManagedFile>();
        var executables = new List<string>();
        var directories = new Stack<string>();
        directories.Push(program);
        while (directories.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            EnsureDirectoryChain(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new InvalidDataException("extracted-path-is-not-regular: " + path);
                var relative = Path.GetRelativePath(program, path).Replace(Path.DirectorySeparatorChar, '/');
                SafeParts(relative);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(path);
                    continue;
                }
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                files.Add(new(relative, hash));
                if (string.Equals(Path.GetFileName(path), "ollama.exe", StringComparison.OrdinalIgnoreCase))
                    executables.Add(relative);
            }
        }
        if (executables.Count != 1)
            throw new InvalidDataException(executables.Count == 0 ? "ollama-executable-missing" : "ollama-executable-ambiguous");
        return new(OllamaManagedInstallation.ManifestFormatVersion, OllamaManagedInstallation.ManagementIdentifier,
            OllamaManagedInstallation.SupportedVersion, OllamaManagedInstallation.SupportedPackageSha256,
            executables[0], files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
    }

    private static string[] SafeParts(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("unsafe-archive-path: " + relative);
        var parts = relative.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0];
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(character => character < 32) || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 ||
                string.Equals(part, OllamaManagedInstallation.ModelsDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, OllamaManagedInstallation.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException("unsafe-archive-path: " + relative);
        }
        return parts;
    }

    private static string AbsoluteDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute application-private directory is required.", parameterName);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), PathComparison))
            throw new ArgumentException("A filesystem root is not application-private.", parameterName);
        return fullPath;
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void EnsureAbsent(string path)
    {
        if (Attributes(path) is not null) throw new IOException("path-already-exists: " + path);
    }

    private static void EnsureDirectoryChain(string path, bool allowMissing = false)
    {
        var ancestors = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        foreach (var ancestor in ancestors)
        {
            var attributes = Attributes(ancestor);
            if (attributes is null && allowMissing) continue;
            if (attributes is null) throw new DirectoryNotFoundException(ancestor);
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0 ||
                (attributes.Value & FileAttributes.Directory) == 0)
                throw new IOException("unsafe-directory: " + ancestor);
        }
    }

    private static void EnsureRegularFileChain(string path)
    {
        EnsureDirectoryChain(Path.GetDirectoryName(path)!);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new IOException("package-is-not-regular-file: " + path);
    }
}
