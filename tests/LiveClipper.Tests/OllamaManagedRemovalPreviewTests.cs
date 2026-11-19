using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class OllamaManagedRemovalPreviewTests
{
    [Fact]
    public async Task MissingInstallationReturnsNoCandidatesAndCreatesNothing()
    {
        var root = NewRoot();

        var result = await OllamaManagedRemovalPreview.PreviewAsync(root);

        Assert.Equal(OllamaManagedInstallation.NotInstalled, result.Status);
        Assert.Equal("manifest-missing", result.Reason);
        AssertNoCandidates(result);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task UnclaimedProgramWithoutManifestIsNotACandidate()
    {
        var root = NewRoot();
        var program = Path.Combine(root, "program");
        Directory.CreateDirectory(program);
        var executable = Path.Combine(program, "ollama.exe");
        File.WriteAllText(executable, "unclaimed");

        var result = await OllamaManagedRemovalPreview.PreviewAsync(root);

        Assert.Equal(OllamaManagedInstallation.NotInstalled, result.Status);
        AssertNoCandidates(result);
        Assert.Equal("unclaimed", File.ReadAllText(executable));
    }

    [Fact]
    public async Task InvalidManifestIsBlockedWithoutCandidates()
    {
        var root = NewRoot();
        var program = Path.Combine(root, "program");
        Directory.CreateDirectory(program);
        var path = Path.Combine(program, "installation.json");
        File.WriteAllText(path, "{broken");

        var result = await OllamaManagedRemovalPreview.PreviewAsync(root);

        Assert.Equal(OllamaManagedRemovalPreview.Blocked, result.Status);
        Assert.Equal("manifest-json-invalid", result.Reason);
        AssertNoCandidates(result);
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public async Task ModifiedDeclaredFileIsBlockedByExistingInspection()
    {
        var root = NewRoot();
        WriteInstallation(root);
        var executable = Path.Combine(root, "program", "bin", "ollama.exe");
        File.WriteAllText(executable, "changed");

        var result = await OllamaManagedRemovalPreview.PreviewAsync(root);

        Assert.Equal(OllamaManagedRemovalPreview.Blocked, result.Status);
        Assert.Equal("file-sha256-mismatch", result.Reason);
        AssertNoCandidates(result);
        Assert.Equal("changed", File.ReadAllText(executable));
    }

    [Theory]
    [InlineData("extra.txt")]
    [InlineData("bin/extra.txt")]
    public async Task UndeclaredFileBlocksTheEntirePlan(string relativePath)
    {
        var root = NewRoot();
        WriteInstallation(root);
        var extra = Path.Combine(root, "program", relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(extra, "unknown user file");

        var result = await OllamaManagedRemovalPreview.PreviewAsync(root);

        Assert.Equal(OllamaManagedRemovalPreview.Blocked, result.Status);
        Assert.Equal("undeclared-program-file: " + relativePath, result.Reason);
        AssertNoCandidates(result);
        Assert.Equal("unknown user file", File.ReadAllText(extra));
    }

    [Fact]
    public async Task UndeclaredEmptyDirectoryIsConservativelyBlocked()
    {
        var root = NewRoot();
        WriteInstallation(root);
        var extra = Path.Combine(root, "program", "unknown");
        Directory.CreateDirectory(extra);

        var result = await OllamaManagedRemovalPreview.PreviewAsync(root);

        Assert.Equal(OllamaManagedRemovalPreview.Blocked, result.Status);
        Assert.Equal("undeclared-program-directory: unknown", result.Reason);
        AssertNoCandidates(result);
        Assert.True(Directory.Exists(extra));
    }

    [Fact]
    public async Task ValidPlanListsOnlyExactFilesIncludingManifestButNeverAuthorizesRemoval()
    {
        var root = NewRoot();
        WriteInstallation(root);
        var program = Path.Combine(root, "program");
        var manifestPath = Path.Combine(program, "installation.json");
        var manifestBefore = File.ReadAllText(manifestPath);
        var executableSize = new FileInfo(Path.Combine(program, "bin", "ollama.exe")).Length;
        var librarySize = new FileInfo(Path.Combine(program, "library.dll")).Length;
        var manifestSize = new FileInfo(manifestPath).Length;

        var result = await OllamaManagedRemovalPreview.PreviewAsync(root);

        Assert.Equal(OllamaManagedRemovalPreview.PreviewOnly, result.Status);
        Assert.True(result.PlanValid);
        Assert.True(result.PreviewOnly);
        Assert.False(result.CanUninstall);
        Assert.Contains("runtime-and-transaction-gates-not-checked", result.Reason);
        Assert.Contains("Process ownership", result.UncoveredSafetyChecks);
        Assert.Contains("mutual exclusion", result.UncoveredSafetyChecks);
        Assert.Contains("not transactional", result.UncoveredSafetyChecks);
        Assert.Equal("installation.json", result.ManifestRelativePath);
        Assert.Collection(result.Files,
            file => Assert.Equal(new OllamaManagedRemovalFile("bin/ollama.exe", executableSize), file),
            file => Assert.Equal(new OllamaManagedRemovalFile("installation.json", manifestSize), file),
            file => Assert.Equal(new OllamaManagedRemovalFile("library.dll", librarySize), file));
        Assert.Equal(executableSize + librarySize + manifestSize, result.TotalBytes);
        Assert.Equal(manifestBefore, File.ReadAllText(manifestPath));
        Assert.Equal("private executable", File.ReadAllText(Path.Combine(program, "bin", "ollama.exe")));
        Assert.Equal("private library", File.ReadAllText(Path.Combine(program, "library.dll")));
    }

    [Fact]
    public async Task SiblingModelsAndCacheAreExcludedEvenWithExclusivelyLockedData()
    {
        var root = NewRoot();
        WriteInstallation(root);
        var models = Path.Combine(root, "models");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(cache);
        var model = Path.Combine(models, "keep-model.bin");
        var cached = Path.Combine(cache, "keep-cache.bin");
        File.WriteAllText(model, "model data");
        File.WriteAllText(cached, "cache data");
        OllamaManagedRemovalPlan result;

        using (var modelLock = new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var cacheLock = new FileStream(cached, FileMode.Open, FileAccess.Read, FileShare.None))
            result = await OllamaManagedRemovalPreview.PreviewAsync(root);

        Assert.True(result.PlanValid);
        Assert.False(result.CanUninstall);
        Assert.Equal(3, result.Files.Count);
        Assert.DoesNotContain(result.Files, file => file.RelativePath.Contains("models", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Files, file => file.RelativePath.Contains("cache", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("model data", File.ReadAllText(model));
        Assert.Equal("cache data", File.ReadAllText(cached));
    }

    [Theory]
    [InlineData("relative-root")]
    [InlineData(".")]
    public async Task RelativePrivateRootIsRejected(string root)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => OllamaManagedRemovalPreview.PreviewAsync(root));
    }

    [Fact]
    public async Task FilesystemRootIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OllamaManagedRemovalPreview.PreviewAsync(Path.GetPathRoot(Path.GetTempPath())!));
    }

    [Fact]
    public async Task CancellationPropagatesWithoutCreatingAnything()
    {
        var root = NewRoot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OllamaManagedRemovalPreview.PreviewAsync(root, cancellation.Token));

        Assert.False(Directory.Exists(root));
    }

    private static void AssertNoCandidates(OllamaManagedRemovalPlan result)
    {
        Assert.True(result.PreviewOnly);
        Assert.False(result.CanUninstall);
        Assert.False(result.PlanValid);
        Assert.Null(result.ManifestRelativePath);
        Assert.Empty(result.Files);
        Assert.Equal(0, result.TotalBytes);
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "liveclipper-ollama-removal-preview-" + Guid.NewGuid().ToString("N"));

    private static void WriteInstallation(string root)
    {
        var program = Path.Combine(root, "program");
        Directory.CreateDirectory(Path.Combine(program, "bin"));
        File.WriteAllBytes(Path.Combine(program, "bin", "ollama.exe"), Encoding.UTF8.GetBytes("private executable"));
        File.WriteAllBytes(Path.Combine(program, "library.dll"), Encoding.UTF8.GetBytes("private library"));
        var manifest = new OllamaManagedManifest(
            OllamaManagedInstallation.ManifestFormatVersion,
            OllamaManagedInstallation.ManagementIdentifier,
            OllamaManagedInstallation.SupportedVersion,
            OllamaManagedInstallation.SupportedPackageSha256,
            "bin/ollama.exe",
            [new("bin/ollama.exe", Hash("private executable")), new("library.dll", Hash("private library"))]);
        File.WriteAllText(Path.Combine(program, OllamaManagedInstallation.ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}