using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveClipper.Core;

public sealed record OllamaManagedFile(string RelativePath, string Sha256);

public sealed record OllamaManagedManifest(
    int FormatVersion,
    string ManagedBy,
    string Version,
    string PackageSha256,
    string ExecutableRelativePath,
    IReadOnlyList<OllamaManagedFile> Files);

// This is a file-identity snapshot, not daemon ownership or permission to delete a directory recursively.
public sealed record OllamaManagedFileIdentity(
    string Status,
    string Reason,
    string? Version,
    string? Executable,
    string? UninstallPreviewDirectory,
    IReadOnlyList<string> VerifiedFiles);

public static class OllamaManagedInstallation
{
    public const string NotInstalled = "not-installed";
    public const string ManagedValid = "managed-valid";
    public const string ManagedInvalid = "managed-invalid";
    public const int ManifestFormatVersion = 1;
    public const string ManagementIdentifier = "LiveClipper.Ollama";
    public const string SupportedVersion = "0.35.0";
    public const string SupportedPackageUrl = "https://github.com/ollama/ollama/releases/download/v0.35.0/ollama-windows-amd64.zip";
    public const long SupportedPackageSize = 1461196158;
    public const string SupportedPackageSha256 = "d6f7d3dd4f5d013553a78c1e78b2521fcf41d43dd2863e4596cdc046fe6036db";
    public const string ProgramDirectoryName = "program";
    public const string ModelsDirectoryName = "models";
    public const string ManifestFileName = "installation.json";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // The caller supplies an application-private root, never a discovered external Ollama location.
    public static async Task<OllamaManagedFileIdentity> InspectAsync(string privateRoot, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateRoot);
        if (!Path.IsPathFullyQualified(privateRoot))
            throw new ArgumentException("An absolute application-private root is required.", nameof(privateRoot));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(privateRoot));
        if (string.Equals(root, Path.GetPathRoot(root), PathComparison))
            throw new ArgumentException("A filesystem root is not an application-private root.", nameof(privateRoot));
        var program = Path.Combine(root, ProgramDirectoryName);
        var manifestPath = Path.Combine(program, ManifestFileName);

        OllamaManagedFileIdentity Invalid(string reason) => new(ManagedInvalid, reason, null, null, null, Array.Empty<string>());
        OllamaManagedFileIdentity Missing() => new(NotInstalled, "manifest-missing", null, null, null, Array.Empty<string>());
        try
        {
            token.ThrowIfCancellationRequested();
            var ancestors = new Stack<string>();
            for (string? current = root; current is not null; current = Path.GetDirectoryName(current))
                ancestors.Push(current);
            foreach (var directory in ancestors.Append(program))
            {
                var attributes = Attributes(directory);
                if (attributes is null) return Missing();
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0) return Invalid("directory-is-reparse-point");
                if ((attributes.Value & FileAttributes.Directory) == 0) return Invalid("program-path-is-not-directory");
            }
            var manifestAttributes = Attributes(manifestPath);
            if (manifestAttributes is null) return Missing();
            if (!IsRegularFile(manifestAttributes.Value)) return Invalid("manifest-is-not-regular-file");

            await using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var manifest = await JsonSerializer.DeserializeAsync<OllamaManagedManifest>(manifestStream, ManifestJson, token);
            if (manifest is null) return Invalid("manifest-empty");
            if (manifest.FormatVersion != ManifestFormatVersion) return Invalid("unsupported-manifest-format");
            if (!string.Equals(manifest.ManagedBy, ManagementIdentifier, StringComparison.Ordinal)) return Invalid("wrong-management-identifier");
            if (!string.Equals(manifest.Version, SupportedVersion, StringComparison.Ordinal)) return Invalid("unsupported-version");
            if (!string.Equals(manifest.PackageSha256, SupportedPackageSha256, StringComparison.OrdinalIgnoreCase)) return Invalid("unsupported-package-sha256");
            if (manifest.Files is null || manifest.Files.Count == 0) return Invalid("manifest-files-missing");
            if (!TryResolveFile(program, manifest.ExecutableRelativePath, out var executable)) return Invalid("unsafe-executable-path");
            if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) return Invalid("executable-is-not-exe");

            var verified = new List<string>();
            var declaredPaths = new HashSet<string>(PathComparer);
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                if (file is null || !TryResolveFile(program, file.RelativePath, out var path)) return Invalid("unsafe-file-path");
                if (string.Equals(path, manifestPath, PathComparison)) return Invalid("manifest-cannot-hash-itself");
                if (!declaredPaths.Add(path)) return Invalid("duplicate-file-path");
                if (!IsSha256(file.Sha256)) return Invalid("invalid-file-sha256");
                var relative = Path.GetRelativePath(program, path);
                var current = program;
                var parts = relative.Split(Path.DirectorySeparatorChar);
                for (var index = 0; index < parts.Length; index++)
                {
                    current = Path.Combine(current, parts[index]);
                    var attributes = Attributes(current);
                    if (attributes is null) return Invalid("declared-file-missing");
                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0) return Invalid("declared-path-is-reparse-point");
                    if (index < parts.Length - 1 && (attributes.Value & FileAttributes.Directory) == 0)
                        return Invalid("declared-parent-is-not-directory");
                    if (index == parts.Length - 1 && !IsRegularFile(attributes.Value)) return Invalid("declared-file-is-not-regular-file");
                }
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase)) return Invalid("file-sha256-mismatch");
                verified.Add(path);
            }
            if (!declaredPaths.Contains(executable)) return Invalid("executable-not-declared");
            return new(ManagedValid, "managed-file-identity-verified", manifest.Version, executable, program, verified.AsReadOnly());
        }
        catch (JsonException) { return Invalid("manifest-json-invalid"); }
        catch (IOException) { return Invalid("installation-io-error"); }
        catch (UnauthorizedAccessException) { return Invalid("installation-access-denied"); }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool IsRegularFile(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;

    private static bool IsSha256(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static bool TryResolveFile(string program, string? relativePath, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        // Treat both separators as separators on every OS; reject drive/ADS syntax and traversal rather than normalizing it away.
        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
            part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny([':', '\0', '*', '?', '"', '<', '>', '|']) >= 0)) return false;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(program, Path.Combine(parts)));
            return fullPath.StartsWith(program + Path.DirectorySeparatorChar, PathComparison);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
