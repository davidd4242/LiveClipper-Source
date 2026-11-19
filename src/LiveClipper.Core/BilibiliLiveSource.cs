using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed class BilibiliLiveSource : ILiveSource
{
    private readonly HttpClient _http = new();
    private string? _mixin;
    private string? _buvid;
    private DateTimeOffset _keyTime;
    private static readonly int[] MixinOrder = [46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13,37,48,7,16,24,55,40,61,26,17,0,1,60,51,30,4,22,25,54,21,56,59,6,63,57,62,11,36,20,34,44,52];
    private string _cookie="";
    public BilibiliLiveSource()
    {
        _http.Timeout=TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LiveClipper/0.1");
        _http.DefaultRequestHeaders.Referrer=new Uri("https://live.bilibili.com/");
    }
    /// <summary>登录 Cookie。匿名连接时 B 站会把弹幕用户名打码（"最***"），带 Cookie 后返回完整用户名。</summary>
    public string Cookie
    {
        get=>_cookie;
        set
        {
            _cookie=(value??"").Trim();
            _http.DefaultRequestHeaders.Remove("Cookie");
            if(_cookie.Length>0)_http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie",_cookie);
        }
    }
    /// <summary>从 Cookie 中解析出登录用户的 UID，弹幕鉴权需要它才能拿到完整用户名。</summary>
    public long SelfUid
    {
        get
        {
            foreach(var part in _cookie.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
                if(part.StartsWith("DedeUserID=",StringComparison.OrdinalIgnoreCase)&&long.TryParse(part[11..],out var uid))return uid;
            return 0;
        }
    }
    /// <summary>
    /// 用 /nav 接口校验当前 Cookie 是否真的处于登录状态；用于「自动识别 Cookie」后立即给出可信反馈。
    /// 未登录时接口返回 code=-101，这里按未登录处理而不是抛错。
    /// </summary>
    public async Task<(bool IsLogin,string UserName,long Uid)> VerifyCookieAsync(CancellationToken token)
    {
        var data=await GetDataAsync("https://api.bilibili.com/x/web-interface/nav",token);
        var login=data.TryGetProperty("isLogin",out var flag)&&flag.ValueKind==JsonValueKind.True;
        var name=login&&data.TryGetProperty("uname",out var uname)?uname.GetString()??"":"";
        var uid=login&&data.TryGetProperty("mid",out var mid)&&mid.TryGetInt64(out var value)?value:SelfUid;
        return (login,name,uid);
    }
    /// <summary>查询单个用户的昵称，用于补全被打码的弹幕用户名。</summary>
    public async Task<string> GetUserNameAsync(long uid,CancellationToken token)
    {
        if(uid<=0)throw new ArgumentOutOfRangeException(nameof(uid));
        var data=await GetDataAsync($"https://api.live.bilibili.com/live_user/v1/Master/info?uid={uid}",token);
        return data.GetProperty("info").GetProperty("uname").GetString()??"";
    }
    private async Task<JsonElement> GetDataAsync(string url,CancellationToken token)
    {
        using var response=await _http.GetAsync(url,token); response.EnsureSuccessStatusCode();
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var root=doc.RootElement;
        if(root.GetProperty("code").GetInt32()!=0 && !(url.EndsWith("/nav") && root.GetProperty("code").GetInt32()==-101)) throw new InvalidOperationException($"Bilibili API: {root.GetProperty("code").GetInt32()} {root.GetProperty("message").GetString()}");
        return root.GetProperty("data").Clone();
    }
    public async Task<RoomInfo> GetRoomInfoAsync(long roomId,CancellationToken token)
    {
        if(roomId<=0) throw new ArgumentOutOfRangeException(nameof(roomId));
        var init=await GetDataAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={roomId}",token);
        var canonical=init.GetProperty("room_id").GetInt64();
        var live=init.GetProperty("live_status").GetInt32()==1;
        string title=$"直播间 {roomId}",streamer="";
        try
        {
            var info=await GetDataAsync($"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={canonical}",token);
            title=info.GetProperty("title").GetString()??title;
            var uid=info.GetProperty("uid").GetInt64();
            var anchor=await GetDataAsync($"https://api.live.bilibili.com/live_user/v1/Master/info?uid={uid}",token);
            streamer=anchor.GetProperty("info").GetProperty("uname").GetString()??"";
        }
        catch { /* Metadata can be risk-controlled while recording still works. */ }
        return new(roomId,canonical,live,title,streamer);
    }
    public async Task<ResolvedLiveUser> ResolveUserAsync(long uid,CancellationToken token)
    {
        if(uid<=0)throw new ArgumentOutOfRangeException(nameof(uid));
        var data=await GetDataAsync($"https://api.live.bilibili.com/room/v1/Room/getRoomInfoOld?mid={uid}",token);
        if(data.GetProperty("roomStatus").GetInt32()!=1)throw new ArgumentException("该 B 站 UID 没有可用的直播间");
        var roomId=data.GetProperty("roomid").GetInt64();
        if(roomId<=0)throw new ArgumentException("未查询到直播间房号");
        var init=await GetDataAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={roomId}",token);
        if(init.GetProperty("uid").GetInt64()!=uid)throw new InvalidOperationException("直播间与 B 站 UID 不匹配");
        var canonical=init.GetProperty("room_id").GetInt64();
        var userName=$"UID {uid}";
        try
        {
            var master=await GetDataAsync($"https://api.live.bilibili.com/live_user/v1/Master/info?uid={uid}",token);
            userName=master.GetProperty("info").GetProperty("uname").GetString()??userName;
        }
        catch { /* The room is still resolvable when profile metadata is unavailable. */ }
        return new(uid,userName,roomId,canonical,init.GetProperty("live_status").GetInt32()==1,data.GetProperty("title").GetString()??$"直播间 {roomId}");
    }
    public async Task<StreamInfo> GetStreamAsync(long roomId,CancellationToken token)
    {
        var data=await GetDataAsync($"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id={roomId}&protocol=0,1&format=0,1,2&codec=0,1&qn=10000&platform=web&ptype=8",token);
        if(data.GetProperty("live_status").GetInt32()!=1) throw new InvalidOperationException("直播间尚未开播");
        var streams=data.GetProperty("playurl_info").GetProperty("playurl").GetProperty("stream");
        foreach(var stream in streams.EnumerateArray())
        foreach(var format in stream.GetProperty("format").EnumerateArray())
        {
            if(format.GetProperty("format_name").GetString()!="flv") continue;
            foreach(var codec in format.GetProperty("codec").EnumerateArray())
            {
                if(codec.GetProperty("codec_name").GetString()!="avc") continue;
                var info=codec.GetProperty("url_info")[0];
                return new(info.GetProperty("host").GetString()+codec.GetProperty("base_url").GetString()+info.GetProperty("extra").GetString(),"flv");
            }
        }
        throw new InvalidOperationException("直播接口没有返回可录制的 AVC/FLV 流");
    }
    public async Task<(long RoomId,string Token,string Host,int Port,string Buvid)> GetDanmakuServerAsync(long roomId,CancellationToken token)
    {
        var data=await GetDataAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={roomId}",token);
        var canonical=data.GetProperty("room_id").GetInt64();
        try
        {
            var direct=await GetDataAsync($"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id={canonical}&type=0",token);
            var parsed=ParseDanmaku(direct,canonical);return(parsed.Item1,parsed.Item2,parsed.Item3,parsed.Item4,_buvid??"");
        }
        catch(InvalidOperationException)
        {
            await LoadWbiAsync(token);
            var qs=Sign(new Dictionary<string,string>{{"id",canonical.ToString()},{"type","0"},{"web_location","444.8"}});
            var url="https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?"+qs;
            using var request=new HttpRequestMessage(HttpMethod.Get,url);
            if(!string.IsNullOrEmpty(_buvid)) request.Headers.Add("Cookie","buvid3="+_buvid);
            using var response=await _http.SendAsync(request,token);response.EnsureSuccessStatusCode();
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
            if(doc.RootElement.GetProperty("code").GetInt32()!=0) throw new InvalidOperationException("Bilibili 弹幕令牌获取失败："+doc.RootElement.GetProperty("message").GetString());
            var parsed=ParseDanmaku(doc.RootElement.GetProperty("data"),canonical);return(parsed.Item1,parsed.Item2,parsed.Item3,parsed.Item4,_buvid??"");
        }
    }
    private static (long,string,string,int) ParseDanmaku(JsonElement data,long room)
    {
        var host=data.GetProperty("host_list")[0];
        return (room,data.GetProperty("token").GetString()??"",host.GetProperty("host").GetString()??"",host.GetProperty("wss_port").GetInt32());
    }
    private async Task LoadWbiAsync(CancellationToken token)
    {
        if(_mixin is not null && DateTimeOffset.UtcNow-_keyTime<TimeSpan.FromHours(6))return;
        var nav=await GetDataAsync("https://api.bilibili.com/x/web-interface/nav",token);
        var img=Path.GetFileNameWithoutExtension(new Uri(nav.GetProperty("wbi_img").GetProperty("img_url").GetString()!).AbsolutePath);
        var sub=Path.GetFileNameWithoutExtension(new Uri(nav.GetProperty("wbi_img").GetProperty("sub_url").GetString()!).AbsolutePath);
        var source=img+sub;_mixin=new string(MixinOrder.Take(32).Select(i=>source[i]).ToArray());_keyTime=DateTimeOffset.UtcNow;
        try
        {
            var finger=await GetDataAsync("https://api.bilibili.com/x/frontend/finger/spi",token);
            _buvid=finger.GetProperty("b_3").GetString();
        }
        catch { _buvid=null; }
    }
    private string Sign(Dictionary<string,string> values)
    {
        values["wts"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var query=string.Join("&",values.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value)));
        var hash=Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query+_mixin))).ToLowerInvariant();
        return query+"&w_rid="+hash;
    }
}

public sealed class FakeLiveSource : ILiveSource
{
    public bool IsLive { get; set; }
    public Task<RoomInfo> GetRoomInfoAsync(long roomId,CancellationToken token) => Task.FromResult(new RoomInfo(roomId,roomId,IsLive,"测试直播","测试主播"));
    public Task<StreamInfo> GetStreamAsync(long roomId,CancellationToken token) => Task.FromResult(new StreamInfo("fake://stream","flv"));
}
