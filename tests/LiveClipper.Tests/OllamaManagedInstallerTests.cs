using System.IO.Compression;
using System.Text.Json;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class OllamaManagedInstallerTests
{
    [Theory]
    [InlineData("relative-root")]
    [InlineData(".")]
    public async Task RelativePrivateRootIsRejected(string root)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OllamaManagedInstaller.InstallFromPackageAsync(root, Path.Combine(Path.GetTempPath(), "package.zip")));
    }

    [Fact]
    public async Task FilesystemRootIsRejected()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OllamaManagedInstaller.InstallFromPackageAsync(root, Path.Combine(Path.GetTempPath(), "package.zip")));
    }

    [Fact]
    public async Task RelativePackagePathIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OllamaManagedInstaller.InstallFromPackageAsync(NewRoot(), "official.zip"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingProgramIsNeverOverwrittenOrUpgraded(bool directory)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var program = Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName);
        var evidence = directory ? Path.Combine(program, "ollama.exe") : program;
        if (directory) Directory.CreateDirectory(program);
        File.WriteAllText(evidence, "existing unclaimed user file");
        var models = Path.Combine(root, OllamaManagedInstallation.ModelsDirectoryName);
        Directory.CreateDirectory(models);
        var model = Path.Combine(models, "keep.bin");
        File.WriteAllText(model, "keep model");
        var zip = WriteZip(root, [("ollama.exe", "not trusted")]);

        var result = await OllamaManagedInstaller.InstallFromPackageAsync(root, zip);

        Assert.Equal(OllamaManagedInstaller.Failed, result.Status);
        Assert.Contains("path-already-exists", result.Reason);
        Assert.Null(result.StagingDirectory);
        Assert.Equal("existing unclaimed user file", File.ReadAllText(evidence));
        Assert.Equal("keep model", File.ReadAllText(model));
        Assert.Empty(Directory.EnumerateDirectories(root, ".ollama-stage-*"));
    }

    [Fact]
    public async Task SmallFakeOfficialZipNeverPublishesAndRetainsItsStage()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("ollama.exe", "not the fixed official hash")]);
        var models = Path.Combine(root, OllamaManagedInstallation.ModelsDirectoryName);
        Directory.CreateDirectory(models);
        var model = Path.Combine(models, "keep.bin");
        File.WriteAllText(model, "user model");

        var result = await OllamaManagedInstaller.InstallFromPackageAsync(root, zip);

        Assert.Equal(OllamaManagedInstaller.Failed, result.Status);
        Assert.Equal("package-size-mismatch", result.Reason);
        Assert.Null(result.ExecutableRelativePath);
        Assert.False(Directory.Exists(result.ProgramDirectory));
        Assert.NotNull(result.StagingDirectory);
        Assert.True(Directory.Exists(result.StagingDirectory));
        Assert.Equal(root, Path.GetDirectoryName(result.StagingDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(result.StagingDirectory!, "program")));
        Assert.True(File.Exists(zip));
        Assert.Equal("user model", File.ReadAllText(model));
        Assert.Equal(OllamaManagedInstallation.NotInstalled, (await OllamaManagedInstallation.InspectAsync(root)).Status);
    }

    [Fact]
    public async Task SmallRealZipFailsTheFixedSha256CheckWithoutPublishing()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("ollama.exe", "wrong hash")]);
        await using var stream = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            OllamaManagedInstaller.VerifyPackageSha256Async(stream));

        Assert.Equal("package-sha256-mismatch", error.Message);
        Assert.False(Directory.Exists(Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName)));
        Assert.Empty(Directory.EnumerateDirectories(root, ".ollama-stage-*"));
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotPublishOrCreateStage()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("ollama.exe", "not trusted")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await OllamaManagedInstaller.InstallFromPackageAsync(root, zip, cancellation.Token);

        Assert.Equal(OllamaManagedInstaller.Cancelled, result.Status);
        Assert.Null(result.StagingDirectory);
        Assert.False(Directory.Exists(result.ProgramDirectory));
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task DirectoryCannotBeUsedAsPackage()
    {
        var root = NewRoot();
        var package = Path.Combine(root, "package.zip");
        Directory.CreateDirectory(package);

        var result = await OllamaManagedInstaller.InstallFromPackageAsync(root, package);

        Assert.Equal(OllamaManagedInstaller.Failed, result.Status);
        Assert.Contains("package-is-not-regular-file", result.Reason);
        Assert.False(Directory.Exists(result.ProgramDirectory));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("..\\outside.bin")]
    [InlineData("/outside.bin")]
    [InlineData("\\outside.bin")]
    [InlineData("C:\\outside.bin")]
    [InlineData("C:outside.bin")]
    [InlineData("folder/../../outside.bin")]
    [InlineData("folder//outside.bin")]
    [InlineData("folder/./outside.bin")]
    [InlineData("folder/ollama.exe:stream")]
    [InlineData("folder/trailing. ")]
    [InlineData("CON.exe")]
    [InlineData("models/model.bin")]
    [InlineData("folder/MODELS/model.bin")]
    [InlineData("installation.json")]
    [InlineData("folder/installation.json")]
    public async Task UnsafeZipPathsAreRejectedBeforeAnyEntryIsWritten(string entryPath)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("safe-first.bin", "safe"), (entryPath, "unsafe")]);
        var program = Path.Combine(root, "stage", "program");
        Directory.CreateDirectory(program);
        using var archive = ZipFile.OpenRead(zip);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OllamaManagedInstaller.ExtractIntoEmptyProgramAsync(archive, program));

        Assert.Empty(Directory.EnumerateFileSystemEntries(program));
        Assert.False(File.Exists(Path.Combine(root, "outside.bin")));
        Assert.False(File.Exists(Path.Combine(root, "stage", "outside.bin")));
    }

    [Theory]
    [InlineData(0xA000)] // Unix symbolic link.
    [InlineData(0x1000)] // Unix FIFO.
    [InlineData(0x6000)] // Unix block device.
    public async Task NonRegularUnixZipEntryIsRejected(int unixType)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("ollama.exe", "target")], unixType << 16);
        var program = Path.Combine(root, "stage", "program");
        Directory.CreateDirectory(program);
        using var archive = ZipFile.OpenRead(zip);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OllamaManagedInstaller.ExtractIntoEmptyProgramAsync(archive, program));
        Assert.Empty(Directory.EnumerateFileSystemEntries(program));
    }

    [Fact]
    public async Task ReparsePointZipEntryIsRejected()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("ollama.exe", "target")], (int)FileAttributes.ReparsePoint);
        var program = Path.Combine(root, "stage", "program");
        Directory.CreateDirectory(program);
        using var archive = ZipFile.OpenRead(zip);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OllamaManagedInstaller.ExtractIntoEmptyProgramAsync(archive, program));
        Assert.Empty(Directory.EnumerateFileSystemEntries(program));
    }

    [Fact]
    public async Task CaseAliasedZipPathsAreRejected()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("bin/ollama.exe", "first"), ("BIN/OLLAMA.EXE", "second")]);
        var program = Path.Combine(root, "stage", "program");
        Directory.CreateDirectory(program);
        using var archive = ZipFile.OpenRead(zip);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OllamaManagedInstaller.ExtractIntoEmptyProgramAsync(archive, program));
        Assert.Empty(Directory.EnumerateFileSystemEntries(program));
    }

    [Fact]
    public async Task ExistingExtractionContentsAreNeverOverwritten()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("ollama.exe", "replacement")]);
        var program = Path.Combine(root, "stage", "program");
        Directory.CreateDirectory(program);
        var existing = Path.Combine(program, "ollama.exe");
        File.WriteAllText(existing, "keep");
        using var archive = ZipFile.OpenRead(zip);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OllamaManagedInstaller.ExtractIntoEmptyProgramAsync(archive, program));
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public async Task ExtractionPreservesActualNestedLayoutWithoutPublishing()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("arbitrary/bin/ollama.exe", "executable bytes"), ("arbitrary/lib.dll", "library bytes")]);
        var program = Path.Combine(root, "stage", "program");
        Directory.CreateDirectory(program);
        using var archive = ZipFile.OpenRead(zip);

        await OllamaManagedInstaller.ExtractIntoEmptyProgramAsync(archive, program);

        Assert.Equal("executable bytes", File.ReadAllText(Path.Combine(program, "arbitrary", "bin", "ollama.exe")));
        Assert.Equal("library bytes", File.ReadAllText(Path.Combine(program, "arbitrary", "lib.dll")));
        Assert.False(File.Exists(Path.Combine(program, OllamaManagedInstallation.ManifestFileName)));
        Assert.False(Directory.Exists(Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName)));
        Assert.False(Directory.Exists(Path.Combine(root, OllamaManagedInstallation.ModelsDirectoryName)));
    }

    [Theory]
    [InlineData(false, "ollama-executable-missing")]
    [InlineData(true, "ollama-executable-ambiguous")]
    public async Task ManifestRequiresExactlyOneActualOllamaExecutable(bool multiple, string reason)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var entries = multiple
            ? new[] { ("one/ollama.exe", "one"), ("two/ollama.exe", "two") }
            : new[] { ("library.dll", "no executable") };
        var zip = WriteZip(root, entries);
        var program = Path.Combine(root, "stage", "program");
        Directory.CreateDirectory(program);
        using var archive = ZipFile.OpenRead(zip);
        await OllamaManagedInstaller.ExtractIntoEmptyProgramAsync(archive, program);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            OllamaManagedInstaller.CreateManifestAsync(program));

        Assert.Equal(reason, error.Message);
        Assert.False(File.Exists(Path.Combine(program, OllamaManagedInstallation.ManifestFileName)));
        Assert.False(Directory.Exists(Path.Combine(root, "program")));
    }

    [Fact]
    public async Task GeneratedManifestUsesExistingContractAndStableNestedPaths()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = WriteZip(root, [("layout/bin/ollama.exe", "executable"), ("layout/library.dll", "library")]);
        var stage = Path.Combine(root, "stage");
        var program = Path.Combine(stage, "program");
        Directory.CreateDirectory(program);
        using var archive = ZipFile.OpenRead(zip);
        await OllamaManagedInstaller.ExtractIntoEmptyProgramAsync(archive, program);

        // A manifest fixture is not a package-trust test and never invokes the publishing entry point.
        var manifest = await OllamaManagedInstaller.CreateManifestAsync(program);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(program, OllamaManagedInstallation.ManifestFileName), json);
        var identity = await OllamaManagedInstallation.InspectAsync(stage);

        Assert.Equal(OllamaManagedInstallation.ManifestFormatVersion, manifest.FormatVersion);
        Assert.Equal(OllamaManagedInstallation.ManagementIdentifier, manifest.ManagedBy);
        Assert.Equal(OllamaManagedInstallation.SupportedVersion, manifest.Version);
        Assert.Equal(OllamaManagedInstallation.SupportedPackageSha256, manifest.PackageSha256);
        Assert.Equal("layout/bin/ollama.exe", manifest.ExecutableRelativePath);
        Assert.Equal(new[] { "layout/bin/ollama.exe", "layout/library.dll" }, manifest.Files.Select(file => file.RelativePath));
        Assert.Contains("\"formatVersion\":1", json);
        Assert.Equal(OllamaManagedInstallation.ManagedValid, identity.Status);
        Assert.Equal(2, identity.VerifiedFiles.Count);
        Assert.False(Directory.Exists(Path.Combine(root, "program")));
        Assert.False(Directory.Exists(Path.Combine(root, "models")));
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "liveclipper-ollama-installer-" + Guid.NewGuid().ToString("N"));

    private static string WriteZip(string root, (string Path, string Content)[] entries, int? attributes = null)
    {
        var path = Path.Combine(root, "ollama-windows-amd64.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path);
            if (attributes is not null) entry.ExternalAttributes = attributes.Value;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }
        return path;
    }
}
