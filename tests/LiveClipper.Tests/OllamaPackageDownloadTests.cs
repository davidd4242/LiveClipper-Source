using System.Security.Cryptography;
using System.Text;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class OllamaPackageDownloadTests
{
    [Theory]
    [InlineData("")]
    [InlineData("relative-private-root")]
    public async Task RequiresAnExplicitAbsolutePrivateRoot(string root)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => OllamaPackageDownload.DownloadAsync(root));
    }

    [Fact]
    public async Task RejectsFilesystemRootWithoutCreatingCache()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        await Assert.ThrowsAsync<ArgumentException>(() => OllamaPackageDownload.DownloadAsync(root));
    }

    [Fact]
    public async Task InvalidExistingCacheReportsActualLengthAndHashAndPreservesFiles()
    {
        var root = NewRoot();
        var cache = Path.Combine(root, OllamaPackageDownload.CacheDirectoryName);
        Directory.CreateDirectory(cache);
        var finalPath = Path.Combine(cache, OllamaPackageDownload.CachedPackageFileName);
        var bytes = Encoding.UTF8.GetBytes("not the official Ollama package");
        File.WriteAllBytes(finalPath, bytes);
        var program = Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName);
        var models = Path.Combine(root, OllamaManagedInstallation.ModelsDirectoryName);
        Directory.CreateDirectory(program);
        Directory.CreateDirectory(models);
        var executable = Path.Combine(program, "keep.exe");
        var model = Path.Combine(models, "keep.bin");
        File.WriteAllText(executable, "existing program");
        File.WriteAllText(model, "existing model");
        var events = new List<OllamaPackageDownloadProgress>();

        var error = await Assert.ThrowsAsync<OllamaPackageDownloadException>(() =>
            OllamaPackageDownload.DownloadAsync(root, new InlineProgress(events.Add)));

        Assert.Equal("existing-cache-invalid", error.Reason);
        Assert.Equal(finalPath, error.RetainedPath);
        Assert.Equal((long)bytes.Length, error.ActualSize);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), error.ActualSha256);
        Assert.NotEqual(OllamaManagedInstallation.SupportedPackageSha256.ToUpperInvariant(), error.ActualSha256);
        Assert.Equal(bytes, File.ReadAllBytes(finalPath));
        Assert.Equal("existing program", File.ReadAllText(executable));
        Assert.Equal("existing model", File.ReadAllText(model));
        Assert.Equal(new[] { finalPath }, Directory.GetFiles(cache));
        var checking = Assert.Single(events);
        Assert.Equal(OllamaPackageDownload.CheckingCache, checking.Stage);
        Assert.Equal(0L, checking.BytesReceived);
        Assert.Equal(OllamaManagedInstallation.SupportedPackageSize, checking.TotalBytes);
        Assert.Equal(finalPath, checking.FilePath);
    }

    [Fact]
    public async Task PreCanceledOperationDoesNotCreatePrivateRoot()
    {
        var root = NewRoot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var events = new List<OllamaPackageDownloadProgress>();

        var error = await Assert.ThrowsAsync<OllamaPackageDownloadCanceledException>(() =>
            OllamaPackageDownload.DownloadAsync(root, new InlineProgress(events.Add), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(error.RetainedPath);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Empty(events);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CancellationAtPartialCreationRetainsUniqueFilesWithoutSendingHttp()
    {
        var root = NewRoot();
        var cache = Path.Combine(root, OllamaPackageDownload.CacheDirectoryName);
        Directory.CreateDirectory(cache);
        var previousPartial = Path.Combine(cache, "previous.partial");
        File.WriteAllText(previousPartial, "previous interrupted download");
        var retained = new List<string>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            var events = new List<OllamaPackageDownloadProgress>();
            var progress = new InlineProgress(value =>
            {
                events.Add(value);
                cancellation.Cancel();
            });

            var error = await Assert.ThrowsAsync<OllamaPackageDownloadCanceledException>(() =>
                OllamaPackageDownload.DownloadAsync(root, progress, cancellation.Token));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            var download = Assert.Single(events);
            Assert.Equal(OllamaPackageDownload.Downloading, download.Stage);
            Assert.Equal(0L, download.BytesReceived);
            Assert.Equal(OllamaManagedInstallation.SupportedPackageSize, download.TotalBytes);
            Assert.Equal(error.RetainedPath, download.FilePath);
            Assert.Equal(cache, Path.GetDirectoryName(download.FilePath));
            Assert.EndsWith(".partial", download.FilePath);
            Assert.Equal(0L, new FileInfo(download.FilePath).Length);
            retained.Add(download.FilePath);
        }

        Assert.NotEqual(retained[0], retained[1]);
        Assert.Equal("previous interrupted download", File.ReadAllText(previousPartial));
        Assert.Equal(3, Directory.GetFiles(cache).Length);
        Assert.False(File.Exists(Path.Combine(cache, OllamaPackageDownload.CachedPackageFileName)));
        Assert.False(Directory.Exists(Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName)));
        Assert.False(Directory.Exists(Path.Combine(root, OllamaManagedInstallation.ModelsDirectoryName)));
    }

    [Fact]
    public async Task CancellationBeforeCacheHashPreservesExistingFile()
    {
        var root = NewRoot();
        var cache = Path.Combine(root, OllamaPackageDownload.CacheDirectoryName);
        Directory.CreateDirectory(cache);
        var finalPath = Path.Combine(cache, OllamaPackageDownload.CachedPackageFileName);
        File.WriteAllText(finalPath, "keep invalid cache");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancellation.Cancel());

        var error = await Assert.ThrowsAsync<OllamaPackageDownloadCanceledException>(() =>
            OllamaPackageDownload.DownloadAsync(root, progress, cancellation.Token));

        Assert.Equal(finalPath, error.RetainedPath);
        Assert.Equal("keep invalid cache", File.ReadAllText(finalPath));
        Assert.Equal(new[] { finalPath }, Directory.GetFiles(cache));
    }

    [Fact]
    public async Task DirectoryAtFinalCachePathIsNotOverwritten()
    {
        var root = NewRoot();
        var finalPath = Path.Combine(root, OllamaPackageDownload.CacheDirectoryName,
            OllamaPackageDownload.CachedPackageFileName);
        Directory.CreateDirectory(finalPath);
        var sentinel = Path.Combine(finalPath, "keep.txt");
        File.WriteAllText(sentinel, "keep");

        var error = await Assert.ThrowsAsync<OllamaPackageDownloadException>(() =>
            OllamaPackageDownload.DownloadAsync(root));

        Assert.Equal("cache-path-is-not-regular-file", error.Reason);
        Assert.Equal(finalPath, error.RetainedPath);
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "liveclipper-ollama-download-" + Guid.NewGuid().ToString("N"));

    private sealed class InlineProgress(Action<OllamaPackageDownloadProgress> report) : IProgress<OllamaPackageDownloadProgress>
    {
        public void Report(OllamaPackageDownloadProgress value) => report(value);
    }
}
