using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveClipper.Live;

/// <summary>
/// 真实 B 站房间探测与直播流解析。只依赖公开 HTTP API，不读取旧 Core 项目；
/// Cookie 可选，但弹幕完整用户名和部分风控场景需要登录态。
/// </summary>
public sealed class BilibiliRoomProbe : ILiveRoomProbe, IStreamSourceResolver
{
    private static readonly int[] MixinOrder =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5,
        49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24,
        55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63,
        57, 62, 11, 36, 20, 34, 44, 52
    ];

    private readonly HttpClient _http;
    private readonly string? _cookie;
    private string? _mixin;
    private DateTimeOffset _keyTime;
    private string? _buvid;

    public BilibiliRoomProbe(string? cookie = null, HttpClient? http = null)
    {
        _cookie = string.IsNullOrWhiteSpace(cookie) ? null : cookie.Trim();
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LiveClipper/0.2");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");
        if (_cookie is not null)
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", _cookie);
    }

    internal string? Cookie => _cookie;

    public long SelfUid
    {
        get
        {
            if (_cookie is null) return 0;
            foreach (var part in _cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("DedeUserID=", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(part[11..], out var uid)) return uid;
            }
            return 0;
        }
    }

    /// <summary>把主播 UID 解析成当前直播间号；没有直播间时返回 0。</summary>
    public async Task<long> ResolveRoomIdByUidAsync(long uid, CancellationToken token)
    {
        if (uid <= 0) throw new ArgumentOutOfRangeException(nameof(uid));
        var data = await GetDataAsync(
            $"https://api.live.bilibili.com/room/v1/Room/getRoomInfoOld?mid={uid}",
            token,
            allowNotLoggedIn: true);
        return data.TryGetProperty("roomid", out var roomId) ? roomId.GetInt64() : 0;
    }

    public async Task<LiveRoom> ProbeAsync(long roomId, CancellationToken token)
    {
        if (roomId <= 0) throw new ArgumentOutOfRangeException(nameof(roomId));

        var init = await GetDataAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={roomId}", token);
        var canonical = init.GetProperty("room_id").GetInt64();
        var live = init.GetProperty("live_status").GetInt32() == 1;
        var title = $"直播间 {roomId}";
        var streamer = "";

        try
        {
            var info = await GetDataAsync($"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={canonical}", token);
            title = info.GetProperty("title").GetString() ?? title;
            var uid = info.GetProperty("uid").GetInt64();
            var anchor = await GetDataAsync($"https://api.live.bilibili.com/live_user/v1/Master/info?uid={uid}", token);
            streamer = anchor.GetProperty("info").GetProperty("uname").GetString() ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 标题/主播名可能被风控，但房间在线状态仍然有效。
        }

        return new LiveRoom(
            roomId,
            live ? RoomAvailability.Live : RoomAvailability.Offline,
            title,
            streamer,
            DateTimeOffset.UtcNow,
            CanonicalRoomId: canonical);
    }

    public async Task<StreamSource?> ResolveAsync(LiveRoom room, CancellationToken token)
    {
        var data = await GetDataAsync(
            $"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id={room.RoomId}&protocol=0,1&format=0,1,2&codec=0,1&qn=10000&platform=web&ptype=8",
            token);
        if (data.GetProperty("live_status").GetInt32() != 1) return null;
        if (!data.TryGetProperty("playurl_info", out var playurlInfo)
            || playurlInfo.ValueKind == JsonValueKind.Null) return null;

        var streams = playurlInfo.GetProperty("playurl").GetProperty("stream");
        // 1) 优先 FLV：低延迟、切片稳定，是旧路径的默认选择。
        if (TryResolveFormat(streams, "flv", "flv", out var flv)) return flv;
        // 2) G8 回退 HLS：部分房间只下发 ts（m3u8）流；此前直接返回 null 会让 supervisor
        //    一直报“没有取到可用的直播流地址”并反复重连。
        if (TryResolveFormat(streams, "ts", "hls", out var hls)) return hls;
        return null;
    }

    private static bool TryResolveFormat(
        JsonElement streams,
        string formatName,
        string sourceFormat,
        out StreamSource? source)
    {
        foreach (var stream in streams.EnumerateArray())
        {
            foreach (var format in stream.GetProperty("format").EnumerateArray())
            {
                if (format.GetProperty("format_name").GetString() != formatName) continue;
                foreach (var codec in format.GetProperty("codec").EnumerateArray())
                {
                    if (codec.GetProperty("codec_name").GetString() != "avc") continue;
                    var urls = codec.GetProperty("url_info");
                    if (urls.GetArrayLength() == 0) continue;
                    var info = urls[0];
                    var uri = new Uri(
                        info.GetProperty("host").GetString()
                        + codec.GetProperty("base_url").GetString()
                        + info.GetProperty("extra").GetString());
                    source = new StreamSource(uri, sourceFormat);
                    return true;
                }
            }
        }

        source = null;
        return false;
    }

    internal async Task<DanmakuServerInfo> GetDanmakuServerAsync(long roomId, CancellationToken token)
    {
        var init = await GetDataAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={roomId}", token);
        var canonical = init.GetProperty("room_id").GetInt64();

        try
        {
            var direct = await GetDataAsync(
                $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id={canonical}&type=0",
                token,
                allowWbiFailure: true);
            return ParseDanmakuServer(direct, canonical) with { Buvid = _buvid };
        }
        catch (InvalidOperationException)
        {
            await LoadWbiAsync(token);
            var query = Sign(new Dictionary<string, string>
            {
                ["id"] = canonical.ToString(CultureInfo.InvariantCulture),
                ["type"] = "0",
                ["web_location"] = "444.8"
            });
            var data = await GetDataAsync(
                "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?" + query,
                token);
            return ParseDanmakuServer(data, canonical) with { Buvid = _buvid };
        }

        static DanmakuServerInfo ParseDanmakuServer(JsonElement data, long room)
        {
            var host = data.GetProperty("host_list")[0];
            return new DanmakuServerInfo(
                room,
                data.GetProperty("token").GetString() ?? "",
                host.GetProperty("host").GetString() ?? "",
                host.GetProperty("wss_port").GetInt32());
        }
    }

    private async Task<JsonElement> GetDataAsync(
        string url,
        CancellationToken token,
        bool allowWbiFailure = false,
        bool allowNotLoggedIn = false)
    {
        using var response = await _http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var root = document.RootElement;
        var code = root.GetProperty("code").GetInt32();
        if (code != 0 && !(allowWbiFailure && code == -352) && !(allowNotLoggedIn && code == -101))
            throw new InvalidOperationException(
                $"Bilibili API {code}: {root.GetProperty("message").GetString()}");
        if (code != 0 && allowWbiFailure)
            throw new InvalidOperationException("Bilibili API 需要 WBI 签名");
        return root.GetProperty("data").Clone();
    }

    private async Task LoadWbiAsync(CancellationToken token)
    {
        if (_mixin is not null && DateTimeOffset.UtcNow - _keyTime < TimeSpan.FromHours(6)) return;

        var nav = await GetDataAsync("https://api.bilibili.com/x/web-interface/nav", token, allowNotLoggedIn: true);
        var image = Path.GetFileNameWithoutExtension(new Uri(nav.GetProperty("wbi_img").GetProperty("img_url").GetString()!).AbsolutePath);
        var sub = Path.GetFileNameWithoutExtension(new Uri(nav.GetProperty("wbi_img").GetProperty("sub_url").GetString()!).AbsolutePath);
        var source = image + sub;
        _mixin = new string(MixinOrder.Take(32).Select(index => source[index]).ToArray());
        _keyTime = DateTimeOffset.UtcNow;

        try
        {
            var finger = await GetDataAsync("https://api.bilibili.com/x/frontend/finger/spi", token);
            _buvid = finger.GetProperty("b_3").GetString();
            if (!string.IsNullOrWhiteSpace(_buvid))
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"buvid3={_buvid}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _buvid = null;
        }
    }

    private string Sign(Dictionary<string, string> values)
    {
        if (_mixin is null) throw new InvalidOperationException("WBI mixin 未初始化");
        values["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var query = string.Join(
            "&",
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + _mixin))).ToLowerInvariant();
        return query + "&w_rid=" + hash;
    }
}

internal sealed record DanmakuServerInfo(long RoomId, string Token, string Host, int Port, string? Buvid = null);
