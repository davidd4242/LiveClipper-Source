using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace LiveClipper.Desktop;

/// <summary>
/// Serves the built SPA and proxies /api/* to the still-running Kestrel service.
/// This is the transitional P0.5 host: static assets are already local; API remains
/// on 127.0.0.1 until the endpoint groups are moved in-process.
/// </summary>
internal sealed class LocalWebHost : IDisposable
{
    public const string AppOrigin = "https://app.local";

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".wasm"] = "application/wasm",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".txt"] = "text/plain; charset=utf-8"
    };

    private readonly CoreWebView2 _core;
    private readonly Uri _serverOrigin;
    private readonly HttpClient _proxy;
    private readonly string _webRoot;
    private readonly ConcurrentBag<Stream> _responseStreams = new();
    private bool _attached;
    private readonly Task<LivePreviewRelay> _previewRelay;
    private readonly CancellationTokenSource _proxyStop = new();
    private readonly object _responseGate = new();
    private readonly ConcurrentDictionary<ProxyResponseStream, byte> _proxyStreams = new();

    public LocalWebHost(CoreWebView2 core, HttpClient proxy, Uri serverOrigin)
    {
        _core = core;
        _serverOrigin = new Uri(serverOrigin.GetLeftPart(UriPartial.Authority));
        _proxy = proxy;
        _previewRelay = Task.Run(() => LivePreviewRelay.StartAsync(proxy));
        _webRoot = FindWebRoot() ?? throw new DirectoryNotFoundException(
            "找不到前端构建产物 web/dist 或 wwwroot。请先在 web 目录运行 npm.cmd run build。");
    }

    public string WebRoot => _webRoot;

    public void Attach()
    {
        if (_attached) return;
        _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += OnWebResourceRequested;
        _attached = true;
    }

    public void Dispose()
    {
        _ = _previewRelay.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); }, TaskScheduler.Default);
        if (_attached)
        {
            _core.WebResourceRequested -= OnWebResourceRequested;
            _attached = false;
        }
        // WebView2 keeps the streams we hand it alive after the view is gone, so the files stay
        // locked until this process exits; close them here, before the temp workspace is deleted.
        lock (_responseGate) {
            _proxyStop.Cancel();
            while (_responseStreams.TryTake(out var stream)) stream.Dispose();
            foreach (var stream in _proxyStreams.Keys) stream.Dispose();
        }
    }

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(uri.Host, "app.local", StringComparison.OrdinalIgnoreCase)) return;

        var deferral = e.GetDeferral();
        try
        {
            await _previewRelay;
            var path = uri.AbsolutePath;
            e.Response = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api", StringComparison.OrdinalIgnoreCase)
                ? await ProxyAsync(e.Request, uri)
                : BuildStaticResponse(e.Request, uri);
        }
        catch (Exception ex)
        {
            e.Response = TextResponse(500, "Internal Server Error", ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private CoreWebView2WebResourceResponse BuildSseResponse(CoreWebView2WebResourceRequest request, Uri uri)
    {
        var target = new UriBuilder(_serverOrigin)
        {
            Path = uri.AbsolutePath,
            Query = uri.Query.TrimStart('?')
        }.Uri;
        var stream = new SseBridgeStream(_proxy, target, GetHeader(request, "Cookie"), _serverOrigin.GetLeftPart(UriPartial.Authority));
        return _core.Environment.CreateWebResourceResponse(
            stream,
            200,
            "OK",
            "Content-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache, no-store\r\nX-Accel-Buffering: no\r\nConnection: keep-alive\r\n");
    }

    private CoreWebView2WebResourceResponse BuildStaticResponse(CoreWebView2WebResourceRequest request, Uri uri)
    {
        var path = ResolveFile(uri.AbsolutePath);
        if (path is null) return TextResponse(404, "Not Found", "Not found");

        var info = new FileInfo(path);
        if (!info.Exists) return TextResponse(404, "Not Found", "Not found");

        var total = info.Length;
        var rangeHeader = GetHeader(request, "Range");
        var range = ParseRange(rangeHeader, total);
        if (!string.IsNullOrWhiteSpace(rangeHeader) && range is null)
        {
            return _core.Environment.CreateWebResourceResponse(
                null,
                416,
                "Range Not Satisfiable",
                $"Content-Range: bytes */{total}\r\nContent-Length: 0\r\n");
        }

        var start = range?.Start ?? 0L;
        var end = range?.End ?? total - 1L;
        var length = total == 0 ? 0L : end - start + 1L;

        Stream stream;
        if (string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase) || length == 0)
        {
            stream = new MemoryStream(Array.Empty<byte>());
        }
        else
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            {
                Position = start
            };
            stream = range is null ? file : new RangeStream(file, length);
        }

        _responseStreams.Add(stream);
        var headers = new StringBuilder();
        headers.Append("Content-Type: ").Append(MimeFor(path)).Append("\r\n");
        headers.Append("Accept-Ranges: bytes\r\n");
        headers.Append("Cache-Control: no-cache\r\n");
        headers.Append("X-Content-Type-Options: nosniff\r\n");
        headers.Append("Referrer-Policy: no-referrer\r\n");
        headers.Append($"Content-Security-Policy: default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob: {_previewRelay.Result.Origin}; connect-src 'self' {_previewRelay.Result.Origin}; worker-src 'self' blob:; font-src 'self' data:; frame-ancestors 'self'; base-uri 'self'; form-action 'self'\r\n");
        headers.Append("Content-Length: ").Append(length).Append("\r\n");
        if (range is not null) headers.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(total).Append("\r\n");

        return _core.Environment.CreateWebResourceResponse(
            stream,
            range is null ? 200 : 206,
            range is null ? "OK" : "Partial Content",
            headers.ToString());
    }

    private static void Log(string message)
    {
        var path = Environment.GetEnvironmentVariable("LIVECLIPPER_PROXY_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.AppendAllText(path, $"{DateTime.Now:O} {message}\n"); } catch { }
    }

    private async Task<CoreWebView2WebResourceResponse> ProxyAsync(CoreWebView2WebResourceRequest request, Uri uri)
    {
        if (request.Method == "GET" && LivePreviewRelay.TryRoom(uri.AbsolutePath, out var room)) {
            var relay = await _previewRelay;
            return _core.Environment.CreateWebResourceResponse(new MemoryStream(), 307, "Temporary Redirect",
                "Location: " + relay.Url(room) + "\r\nCache-Control: no-store\r\nContent-Length: 0\r\n");
        }
        var target = new UriBuilder(_serverOrigin)
        {
            Path = uri.AbsolutePath,
            Query = uri.Query.TrimStart('?')
        }.Uri;

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);
        if (request.Content is not null && request.Method is not "GET" and not "HEAD")
        {
            using var body = new MemoryStream();
            await request.Content.CopyToAsync(body);
            message.Content = new ByteArrayContent(body.ToArray());
        }

        CopyRequestHeaders(request, message);
        message.Headers.TryAddWithoutValidation("Origin", _serverOrigin.GetLeftPart(UriPartial.Authority));

        var debugLog=Environment.GetEnvironmentVariable("LIVECLIPPER_PROXY_LOG");
        if(!string.IsNullOrWhiteSpace(debugLog))try{File.AppendAllText(debugLog,$"{DateTime.Now:O} start {request.Method} {uri}\n");}catch{}
        var response = await _proxy.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, _proxyStop.Token);
        if(!string.IsNullOrWhiteSpace(debugLog))try{File.AppendAllText(debugLog,$"{DateTime.Now:O} headers {(int)response.StatusCode} {response.Content.Headers.ContentType}\n");}catch{}
        try
        {
            var stream = await response.Content.ReadAsStreamAsync();
            stream = new ProxyPumpStream(stream);
            var headers = BuildResponseHeaders(response);
            if(!string.IsNullOrWhiteSpace(debugLog)&&response.Content.Headers.ContentType?.MediaType=="text/event-stream")try{File.AppendAllText(debugLog,$"{DateTime.Now:O} sse headers=[{headers.Replace("\r\n"," | ")}] len={response.Content.Headers.ContentLength}\n");}catch{}
            var owned = new ProxyResponseStream(stream, response, completed => _proxyStreams.TryRemove(completed, out _));
            lock (_responseGate) {
                if (_proxyStop.IsCancellationRequested) { owned.Dispose(); _proxyStop.Token.ThrowIfCancellationRequested(); }
                _proxyStreams.TryAdd(owned, 0);
            }
            return _core.Environment.CreateWebResourceResponse(
                owned,
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                headers);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void CopyRequestHeaders(CoreWebView2WebResourceRequest request, HttpRequestMessage message)
    {
        foreach (var name in new[]
                 {
                     "Accept", "Accept-Language", "Authorization", "Cache-Control", "Content-Type",
                     "Cookie", "If-Match", "If-Modified-Since", "If-None-Match", "If-Range",
                     "If-Unmodified-Since", "Range", "User-Agent", "X-Requested-With",
                     "X-Base-Revision", "X-Editor-Epoch", "X-Session-Id"
                 })
        {
            var value = GetHeader(request, name);
            if (string.IsNullOrEmpty(value)) continue;
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            else
                message.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static string BuildResponseHeaders(HttpResponseMessage response)
    {
        var headers = new StringBuilder();
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (IsHopByHop(header.Key) || header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var raw in header.Value)
            {
                var value = raw;
                if (header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))
                    value = RewriteLocation(value);
                if (header.Key.Equals("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase))
                    value = AppOrigin;
                headers.Append(header.Key).Append(": ").Append(value).Append("\r\n");
            }
        }
        if (response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            headers.Append("Connection: keep-alive\r\n");
        return headers.ToString();
    }

    private static bool IsHopByHop(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static string RewriteLocation(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var location)
            && location.Host is "127.0.0.1" or "localhost")
            return AppOrigin + location.PathAndQuery + location.Fragment;
        return value;
    }

    private static (long Start, long End)? ParseRange(string? value, long length)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return null;
        var spec = value[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffix) || suffix <= 0) return null;
            var start = Math.Max(0, length - suffix);
            return (start, length - 1);
        }

        if (!long.TryParse(startText, out var from) || from < 0 || from >= length) return null;
        var to = length - 1;
        if (endText.Length > 0 && (!long.TryParse(endText, out to) || to < from)) return null;
        return (from, Math.Min(to, length - 1));
    }

    private string? ResolveFile(string absolutePath)
    {
        var relative = Uri.UnescapeDataString(absolutePath).TrimStart('/');
        if (relative.Length == 0) relative = "index.html";
        if (relative.Contains('\0')) return null;

        var candidate = Path.GetFullPath(Path.Combine(_webRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(candidate)) return null;
        if (Directory.Exists(candidate)) candidate = Path.Combine(candidate, "index.html");
        if (File.Exists(candidate)) return candidate;

        if (!Path.HasExtension(relative))
        {
            var index = Path.Combine(_webRoot, "index.html");
            if (File.Exists(index)) return index;
        }
        return null;
    }

    private bool IsWithin(string candidate)
    {
        var root = _webRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string MimeFor(string path) =>
        MimeTypes.TryGetValue(Path.GetExtension(path), out var mime) ? mime : "application/octet-stream";

    private static string? GetHeader(CoreWebView2WebResourceRequest request, string name)
    {
        try
        {
            return request.Headers.Contains(name) ? request.Headers.GetHeader(name) : null;
        }
        catch
        {
            return null;
        }
    }

    private CoreWebView2WebResourceResponse TextResponse(int status, string reason, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        return _core.Environment.CreateWebResourceResponse(
            new MemoryStream(bytes),
            status,
            reason,
            $"Content-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n");
    }

    private static string? FindWebRoot()
    {
        var configured = Environment.GetEnvironmentVariable("LIVECLIPPER_WEB_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "index.html")))
            return Path.GetFullPath(configured);

        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                     Path.Combine(AppContext.BaseDirectory, "..", "server", "wwwroot")
                 })
        {
            if (File.Exists(Path.Combine(candidate, "index.html"))) return Path.GetFullPath(candidate);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "web", "dist");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private sealed class RangeStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public RangeStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0) return 0;
            var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_remaining <= 0) return Task.FromResult(0);
            return ReadAsyncCore(buffer, offset, (int)Math.Min(count, _remaining), cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return ValueTask.FromResult(0);
            return ReadAsyncMemory(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
        }

        private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await _inner.ReadAsync(buffer, offset, count, ct);
            _remaining -= read;
            return read;
        }

        private async ValueTask<int> ReadAsyncMemory(Memory<byte> buffer, CancellationToken ct)
        {
            var read = await _inner.ReadAsync(buffer, ct);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private int _prefixOffset;
        private readonly Stream _inner;

        public PrefixStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _prefixOffset);
                Array.Copy(_prefix, _prefixOffset, buffer, offset, n);
                _prefixOffset += n;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _prefixOffset);
                Array.Copy(_prefix, _prefixOffset, buffer, offset, n);
                _prefixOffset += n;
                return Task.FromResult(n);
            }
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var n = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
                _prefix.AsSpan(_prefixOffset, n).CopyTo(buffer.Span);
                _prefixOffset += n;
                return ValueTask.FromResult(n);
            }
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await _inner.DisposeAsync(); await base.DisposeAsync(); }
    }

    private sealed class SseBridgeStream : Stream
    {
        private readonly HttpClient _proxy;
        private readonly Uri _target;
        private readonly string? _cookie;
        private readonly string _origin;
        private readonly BlockingCollection<byte[]> _chunks = new(32);
        private readonly CancellationTokenSource _cts = new();
        private byte[]? _current;
        private int _offset;
        private Exception? _error;
        private bool _disposed;

        public SseBridgeStream(HttpClient proxy, Uri target, string? cookie, string origin)
        {
            _proxy = proxy;
            _target = target;
            _cookie = cookie;
            _origin = origin;
            var thread = new Thread(Pump) { IsBackground = true, Name = "sse-bridge" };
            thread.Start();
        }

        private void Pump()
        {
            Log("sse bridge start");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _target);
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                request.Headers.TryAddWithoutValidation("Origin", _origin);
                if (!string.IsNullOrEmpty(_cookie)) request.Headers.TryAddWithoutValidation("Cookie", _cookie);
                using var response = _proxy.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter().GetResult();
                Log($"sse bridge upstream {(int)response.StatusCode} {response.Content.Headers.ContentType}");
                if (!response.IsSuccessStatusCode)
                {
                    _error = new IOException($"SSE upstream returned {(int)response.StatusCode}");
                    return;
                }
                using var upstream = response.Content.ReadAsStream();
                var buffer = new byte[8192];
                while (!_cts.IsCancellationRequested)
                {
                    var n = upstream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    Log($"sse bridge read {n} [{Encoding.UTF8.GetString(buffer, 0, n).Replace("\n", "\\n").Replace("\r", "\\r")}]");
                    _chunks.Add(buffer.AsSpan(0, n).ToArray(), _cts.Token);

                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Log("sse bridge error " + error); _error = error; }
            finally { _chunks.CompleteAdding(); Log("sse bridge end"); }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Log("stream read call");
            while (true)
            {
                if (_current is not null && _offset < _current.Length)
                {
                    var n = Math.Min(count, _current.Length - _offset);
                    Array.Copy(_current, _offset, buffer, offset, n);
                    _offset += n;
                    return n;
                }
                _current = null;
                _offset = 0;
                if (_chunks.TryTake(out var next, Timeout.Infinite, _cts.Token))
                {
                    _current = next;
                    continue;
                }
                if (_error is not null) throw new IOException("SSE stream failed", _error);
                return 0;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => Read(buffer, offset, count), cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<int>(Task.Run(() =>
            {
                var temp = new byte[buffer.Length];
                var n = Read(temp, 0, temp.Length);
                temp.AsSpan(0, n).CopyTo(buffer.Span);
                return n;
            }, cancellationToken));
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _cts.Cancel();
                _chunks.Dispose();
                _cts.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Dispose();
            await base.DisposeAsync();
        }
    }

}
