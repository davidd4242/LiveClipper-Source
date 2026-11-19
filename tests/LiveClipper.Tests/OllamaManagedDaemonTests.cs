using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class OllamaManagedDaemonTests
{
    [Fact]
    public async Task ConfirmationAndMissingProgramDoNotStartOrChangeConnection()
    {
        var install = new OllamaInstallationService();
        var service = new OllamaManagedDaemon(install);
        var root = Path.Combine(Path.GetTempPath(), "lc-missing-" + Guid.NewGuid().ToString("N"));
        var original = OllamaRuntime.BaseUri;
        Assert.Equal(400, (await service.StartAsync(root, false, default)).StatusCode);
        Assert.Equal("managed-program-not-valid", (await service.StartAsync(root, true, default)).Outcome);
        Assert.False(service.Snapshot().Owned);
        Assert.Equal(original, OllamaRuntime.BaseUri);
        Assert.False(Directory.Exists(root));
        using var lease = install.TryAcquireManagement();
        Assert.NotNull(lease);
    }
    [Fact]
    public async Task PrivateSelectionRequiresOwnedReadyRuntime()
    {
        var service = new OllamaManagedDaemon(new());
        Assert.Equal(400, (await service.SelectAsync(false, "private", default)).StatusCode);
        Assert.Equal(409, (await service.SelectAsync(true, "private", default)).StatusCode);
        Assert.Equal(400, (await service.SelectAsync(true, "unknown", default)).StatusCode);
        Assert.Equal(200, (await service.SelectAsync(true, "original", default)).StatusCode);
        Assert.Equal(200, (await service.StopAsync(true, default)).StatusCode);
        Assert.False(service.Snapshot().Selected);
    }
    [Fact]
    public async Task SharedLeaseBlocksInstallationAndOtherManagement()
    {
        var install = new OllamaInstallationService();
        using (var lease = install.TryAcquireManagement())
        {
            Assert.NotNull(lease);
            Assert.Null(install.TryAcquireManagement());
            var result = await install.StartAsync(Path.GetTempPath(), true, OllamaManagedInstallation.SupportedVersion);
            Assert.Equal("private-runtime-in-use", result.Outcome);
            var daemon = new OllamaManagedDaemon(install);
            Assert.Equal(409, (await daemon.StartAsync(Path.GetTempPath(), true, default)).StatusCode);
        }
        using var next = install.TryAcquireManagement();
        Assert.NotNull(next);
    }
    [Fact]
    public async Task UninstallRequiresVersionAndVerifiedProgramAndNeverCreatesDirectory()
    {
        var service = new OllamaManagedDaemon(new());
        var root = Path.Combine(Path.GetTempPath(), "lc-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(400, (await service.UninstallAsync(root, false, null, default)).StatusCode);
        Assert.Equal("version-mismatch", (await service.UninstallAsync(root, true, "wrong", default)).Outcome);
        Assert.Equal("managed-removal-plan-invalid", (await service.UninstallAsync(root, true, OllamaManagedInstallation.SupportedVersion, default)).Outcome);
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task RetirementMovesOnlyProgramAndRetainsModelsAndCache()
    {
        var storage = Path.Combine(Path.GetTempPath(), "lc-retire-" + Guid.NewGuid().ToString("N"));
        var root = OllamaInstallationService.ResolvePrivateRoot(storage);
        var program = Path.Combine(root, "program");
        Directory.CreateDirectory(program);
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        File.WriteAllText(Path.Combine(root, "models", "keep"), "model");
        File.WriteAllText(Path.Combine(root, "cache", "keep"), "cache");
        var bytes = System.Text.Encoding.UTF8.GetBytes("fixture-not-executed");
        File.WriteAllBytes(Path.Combine(program, "ollama.exe"), bytes);
        var manifest = new OllamaManagedManifest(1, OllamaManagedInstallation.ManagementIdentifier,
            OllamaManagedInstallation.SupportedVersion, OllamaManagedInstallation.SupportedPackageSha256,
            "ollama.exe", [new("ollama.exe", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)))]);
        File.WriteAllText(Path.Combine(program, "installation.json"), System.Text.Json.JsonSerializer.Serialize(manifest,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        try
        {
            var daemon = new OllamaManagedDaemon(new());
            var result = await daemon.UninstallAsync(storage, true, OllamaManagedInstallation.SupportedVersion, default);
            Assert.Equal("program-retired-recoverable", result.Outcome);
            Assert.False(Directory.Exists(program));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(result.Daemon.RetiredDirectory!, "ollama.exe")));
            Assert.Equal("model", File.ReadAllText(Path.Combine(root, "models", "keep")));
            Assert.Equal("cache", File.ReadAllText(Path.Combine(root, "cache", "keep")));
        }
        finally { Directory.Delete(storage, true); }
    }
}
