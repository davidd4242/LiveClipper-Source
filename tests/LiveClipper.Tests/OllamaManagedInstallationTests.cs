using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class OllamaManagedInstallationTests
{
    [Fact]
    public async Task MissingManifestDoesNotClaimAnExecutable()
    {
        var root = NewRoot();
        var program = Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName);
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "ollama.exe"), "unclaimed executable");

        var result = await OllamaManagedInstallation.InspectAsync(root);

        Assert.Equal(OllamaManagedInstallation.NotInstalled, result.Status);
        Assert.Equal("manifest-missing", result.Reason);
        Assert.Null(result.Version);
        Assert.Null(result.Executable);
        Assert.Null(result.UninstallPreviewDirectory);
        Assert.Empty(result.VerifiedFiles);
    }

    [Fact]
    public async Task ValidManifestProvesOnlyDeclaredFilesAndKeepsModelsOutsideProgram()
    {
        var root = NewRoot();
        var manifest = WriteInstallation(root);
        var models = Path.Combine(root, OllamaManagedInstallation.ModelsDirectoryName);
        Directory.CreateDirectory(models);
        var model = Path.Combine(models, "keep-model.bin");
        File.WriteAllText(model, "model data");

        var result = await OllamaManagedInstallation.InspectAsync(root);

        var program = Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName);
        Assert.Equal(OllamaManagedInstallation.ManagedValid, result.Status);
        Assert.Equal("managed-file-identity-verified", result.Reason);
        Assert.Equal(OllamaManagedInstallation.SupportedVersion, result.Version);
        Assert.Equal(Path.Combine(program, "bin", "ollama.exe"), result.Executable);
        Assert.Equal(program, result.UninstallPreviewDirectory);
        Assert.Equal(2, result.VerifiedFiles.Count);
        Assert.Contains(result.Executable!, result.VerifiedFiles);
        Assert.Contains(Path.Combine(program, "library.dll"), result.VerifiedFiles);
        Assert.DoesNotContain(model, result.VerifiedFiles);
        Assert.False(models.StartsWith(program + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("model data", File.ReadAllText(model));
        Assert.Equal(manifest.PackageSha256, OllamaManagedInstallation.SupportedPackageSha256);
    }

    [Fact]
    public async Task HashMismatchDoesNotExposeAnUninstallDirectory()
    {
        var root = NewRoot();
        WriteInstallation(root);
        File.WriteAllText(Path.Combine(root, "program", "bin", "ollama.exe"), "modified");

        var result = await OllamaManagedInstallation.InspectAsync(root);

        AssertInvalid(result, "file-sha256-mismatch");
    }

    [Theory]
    [InlineData("../models/model.bin")]
    [InlineData("..\\models\\model.bin")]
    [InlineData("C:\\external\\ollama.exe")]
    [InlineData("/external/ollama.exe")]
    public async Task EscapingOrAbsoluteDeclaredPathIsInvalid(string relativePath)
    {
        var root = NewRoot();
        var manifest = WriteInstallation(root);
        WriteManifest(root, manifest with { Files = [new(relativePath, Hash("external"))] });

        var result = await OllamaManagedInstallation.InspectAsync(root);

        AssertInvalid(result, "unsafe-file-path");
    }

    [Theory]
    [InlineData("../external/ollama.exe")]
    [InlineData("C:\\external\\ollama.exe")]
    public async Task UnsafeExecutableDeclarationIsInvalid(string relativePath)
    {
        var root = NewRoot();
        var manifest = WriteInstallation(root);
        WriteManifest(root, manifest with { ExecutableRelativePath = relativePath });

        AssertInvalid(await OllamaManagedInstallation.InspectAsync(root), "unsafe-executable-path");
    }

    [Theory]
    [InlineData("External.Ollama", "0.35.0", "wrong-management-identifier")]
    [InlineData("LiveClipper.Ollama", "0.34.0", "unsupported-version")]
    public async Task WrongProductOrVersionIsInvalid(string managedBy, string version, string reason)
    {
        var root = NewRoot();
        var manifest = WriteInstallation(root);
        WriteManifest(root, manifest with { ManagedBy = managedBy, Version = version });

        AssertInvalid(await OllamaManagedInstallation.InspectAsync(root), reason);
    }

    [Fact]
    public async Task MalformedManifestIsInvalidAndModelsAreNotCreated()
    {
        var root = NewRoot();
        var program = Path.Combine(root, "program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, OllamaManagedInstallation.ManifestFileName), "{broken");

        AssertInvalid(await OllamaManagedInstallation.InspectAsync(root), "manifest-json-invalid");
        Assert.False(Directory.Exists(Path.Combine(root, OllamaManagedInstallation.ModelsDirectoryName)));
    }

    [Fact]
    public async Task ManifestCannotClaimSiblingModelDirectory()
    {
        var root = NewRoot();
        var manifest = WriteInstallation(root);
        var models = Path.Combine(root, OllamaManagedInstallation.ModelsDirectoryName);
        Directory.CreateDirectory(models);
        var model = Path.Combine(models, "keep.bin");
        File.WriteAllText(model, "keep");
        WriteManifest(root, manifest with { Files = [new("../models/keep.bin", Hash("keep"))] });

        AssertInvalid(await OllamaManagedInstallation.InspectAsync(root), "unsafe-file-path");
        Assert.Equal("keep", File.ReadAllText(model));
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "liveclipper-ollama-identity-" + Guid.NewGuid().ToString("N"));

    private static OllamaManagedManifest WriteInstallation(string root)
    {
        var program = Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName);
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
        WriteManifest(root, manifest);
        return manifest;
    }

    private static void WriteManifest(string root, OllamaManagedManifest manifest) =>
        File.WriteAllText(Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName, OllamaManagedInstallation.ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static void AssertInvalid(OllamaManagedFileIdentity result, string reason)
    {
        Assert.Equal(OllamaManagedInstallation.ManagedInvalid, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Version);
        Assert.Null(result.Executable);
        Assert.Null(result.UninstallPreviewDirectory);
        Assert.Empty(result.VerifiedFiles);
    }
}
