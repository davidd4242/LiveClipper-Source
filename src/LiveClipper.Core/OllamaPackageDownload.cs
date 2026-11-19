using System.Net;
using System.Security.Cryptography;

namespace LiveClipper.Core;

public sealed record OllamaPackageDownloadProgress(
    string Stage,
    long BytesReceived,
    long TotalBytes,
    string FilePath);

public sealed class OllamaPackageDownloadException : IOException
{
    public string Reason { get; }
    public string? RetainedPath { get; }
    public long? ActualSize { get; }
    public string? ActualSha256 { get; }

    internal OllamaPackageDownloadException(string reason, string? retainedPath,
        long? actualSize = null, string? actualSha256 = null, Exception? innerException = null)
        : base($"Ollama package download failed ({reason}). Retained file: {retainedPath ?? "none"}.", innerException)
    {
        Reason = reason;
        RetainedPath = retainedPath;
        ActualSize = actualSize;
        ActualSha256 = actualSha256;
    }
}

public sealed class OllamaPackageDownloadCanceledException : OperationCanceledException
{
    public string? RetainedPath { get; }

    internal OllamaPackageDownloadCanceledException(string? retainedPath, Exception innerException, CancellationToken token)
        : base($"Ollama package download canceled. Retained file: {retainedPath ?? "none"}.", innerException, token)
    {
        RetainedPath = retainedPath;
    }
}

public static class OllamaPackageDownload
{
    public const string CacheDirectoryName = "cache";
    public const string CachedPackageFileName = "ollama-windows-amd64-" + OllamaManagedInstallation.SupportedVersion + ".zip";
    public const string CheckingCache = "checking-cache";
    public const string Downloading = "downloading";
    public const string Verifying = "verifying";
    public const string Verified = "verified";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // The caller chooses a private application root; this method does not discover installations or install files.
    public static async Task<string> DownloadAsync(string privateRoot,
        IProgress<OllamaPackageDownloadProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateRoot);
        if (!Path.IsPathFullyQualified(privateRoot))
            throw new ArgumentException("An absolute application-private root is required.", nameof(privateRoot));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(privateRoot));
        if (string.Equals(root, Path.GetPathRoot(root), PathComparison))
            throw new ArgumentException("A filesystem root is not an application-private root.", nameof(privateRoot));
        var cache = Path.Combine(root, CacheDirectoryName);
        var finalPath = Path.Combine(cache, CachedPackageFileName);
        string? retainedPath = null;

        try
        {
            token.ThrowIfCancellationRequested();
            EnsureSafeDirectories(cache);
            if (Attributes(finalPath) is { } existingAttributes)
            {
                retainedPath = finalPath;
                RequireRegularFile(finalPath, existingAttributes);
                Report(progress, CheckingCache, 0, finalPath);
                token.ThrowIfCancellationRequested();
                await VerifyFileAsync(finalPath, "existing-cache-invalid", token);
                token.ThrowIfCancellationRequested();
                Report(progress, Verified, OllamaManagedInstallation.SupportedPackageSize, finalPath);
                token.ThrowIfCancellationRequested();
                return finalPath;
            }

            token.ThrowIfCancellationRequested();
            var partialPath = Path.Combine(cache, CachedPackageFileName + "." + Guid.NewGuid().ToString("N") + ".partial");
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                retainedPath = partialPath;
                Report(progress, Downloading, 0, partialPath);
                token.ThrowIfCancellationRequested();
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false,
                    UseDefaultCredentials = false,
                    Credentials = null,
                    PreAuthenticate = false,
                    AutomaticDecompression = DecompressionMethods.None
                };
                using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                using var response = await OpenResponseAsync(client, token);
                if (response.Content.Headers.ContentEncoding.Count != 0)
                    throw new OllamaPackageDownloadException("unexpected-content-encoding", partialPath);
                if (response.Content.Headers.ContentLength is { } contentLength &&
                    contentLength != OllamaManagedInstallation.SupportedPackageSize)
                    throw new OllamaPackageDownloadException("response-length-mismatch", partialPath, contentLength);

                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[131072];
                long received = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), token);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    received += read;
                    Report(progress, Downloading, received, partialPath);
                    token.ThrowIfCancellationRequested();
                    if (received > OllamaManagedInstallation.SupportedPackageSize)
                        throw new OllamaPackageDownloadException("download-too-large", partialPath, received);
                }
                await output.FlushAsync(token);
            }

            Report(progress, Verifying, new FileInfo(partialPath).Length, partialPath);
            token.ThrowIfCancellationRequested();
            await VerifyFileAsync(partialPath, "download-integrity-mismatch", token);
            token.ThrowIfCancellationRequested();
            EnsureSafeDirectories(cache);
            File.Move(partialPath, finalPath, overwrite: false);
            retainedPath = finalPath;
            Report(progress, Verified, OllamaManagedInstallation.SupportedPackageSize, finalPath);
            token.ThrowIfCancellationRequested();
            return finalPath;
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            throw new OllamaPackageDownloadCanceledException(retainedPath, ex, token);
        }
        catch (OllamaPackageDownloadException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            throw new OllamaPackageDownloadException("download-io-or-http-error", retainedPath, innerException: ex);
        }
    }

    private static async Task VerifyFileAsync(string path, string reason, CancellationToken token)
    {
        RequireRegularFile(path, File.GetAttributes(path));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (length != OllamaManagedInstallation.SupportedPackageSize ||
            !string.Equals(hash, OllamaManagedInstallation.SupportedPackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new OllamaPackageDownloadException(reason, path, length, hash);
    }

    private static async Task<HttpResponseMessage> OpenResponseAsync(HttpClient client, CancellationToken token)
    {
        var uri = new Uri(OllamaManagedInstallation.SupportedPackageUrl);
        for (var redirects = 0; ; redirects++)
        {
            RequireTrustedUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                using (response)
                {
                    if (redirects >= 5 || response.Headers.Location is not { } location)
                        throw new HttpRequestException("Missing location or too many Ollama package redirects.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                }
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Unexpected Ollama package HTTP status: {(int)status}.", null, status);
            }
            return response;
        }
    }

    private static void RequireTrustedUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new HttpRequestException("Untrusted Ollama package redirect rejected.");
    }

    private static void EnsureSafeDirectories(string cache)
    {
        var ancestors = new Stack<string>();
        for (string? current = cache; current is not null; current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        foreach (var path in ancestors)
        {
            var attributes = Attributes(path);
            if (attributes is null)
            {
                Directory.CreateDirectory(path);
                attributes = File.GetAttributes(path);
            }
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0 ||
                (attributes.Value & FileAttributes.Directory) == 0)
                throw new IOException("Ollama cache ancestors must be directories, not reparse points.");
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void RequireRegularFile(string path, FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new OllamaPackageDownloadException("cache-path-is-not-regular-file", path);
    }

    private static void Report(IProgress<OllamaPackageDownloadProgress>? progress, string stage, long received, string path) =>
        progress?.Report(new(stage, received, OllamaManagedInstallation.SupportedPackageSize, path));
}
