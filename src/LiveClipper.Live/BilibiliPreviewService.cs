using System.Net;

namespace LiveClipper.Live;

/// <summary>
/// 按需打开的 B 站直播预览流。只在用户点选房间时建立，不参与录制，
/// 也不把 B 站 CDN 地址暴露给浏览器；FLV 字节由本地后端转发。
/// </summary>
public sealed class BilibiliPreviewService
{
    private readonly BilibiliRoomProbe _probe;
    private readonly HttpClient _http;

    public BilibiliPreviewService(BilibiliRoomProbe probe)
    {
        _probe = probe;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LiveClipper/0.2");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");
    }

    /// <summary>房间不在直播时返回 null；上游不可达时抛 HttpRequestException。</summary>
    public async Task<BilibiliPreviewStream?> OpenAsync(long roomId, CancellationToken token)
    {
        if (roomId <= 0) throw new ArgumentOutOfRangeException(nameof(roomId));

        var room = await _probe.ProbeAsync(roomId, token).ConfigureAwait(false);
        if (room.Availability != RoomAvailability.Live) return null;

        var source = await _probe.ResolveAsync(room, token).ConfigureAwait(false);
        if (source is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, source.Uri);
        request.Headers.Referrer = new Uri("https://live.bilibili.com/");
        var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"B 站直播流返回 HTTP {status}");
        }

        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "video/x-flv";
        return new BilibiliPreviewStream(response, stream, contentType);
    }
}

public sealed class BilibiliPreviewStream : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;

    internal BilibiliPreviewStream(HttpResponseMessage response, Stream content, string contentType)
    {
        _response = response;
        Content = content;
        ContentType = contentType;
    }

    public Stream Content { get; }
    public string ContentType { get; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
    }
}
