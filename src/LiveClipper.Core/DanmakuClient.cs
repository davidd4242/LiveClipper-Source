using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed class BilibiliDanmakuClient(BilibiliLiveSource source):IDanmakuClient
{
    public event Func<DanmakuEvent,Task>? Received;
    public event Action<string>? Error;
    public long PacketCount {get;private set;}
    public string? LastCommand {get;private set;}
    public bool Authorized {get;private set;}
    public async Task RunAsync(RecordingSession session,CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try
            {
                var server=await source.GetDanmakuServerAsync(session.RoomId,token);
                using var socket=new ClientWebSocket();
                socket.Options.SetRequestHeader("Origin","https://live.bilibili.com");
                if(!string.IsNullOrEmpty(source.Cookie))socket.Options.SetRequestHeader("Cookie",source.Cookie);
                await socket.ConnectAsync(new Uri($"wss://{server.Host}:{server.Port}/sub"),token);
                // 带上登录 UID 后 B 站才会下发完整用户名；匿名连接（uid=0）会被打码。
                var auth=JsonSerializer.Serialize(new {uid=source.SelfUid,roomid=server.RoomId,protover=3,buvid=server.Buvid,platform="web",type=2,key=server.Token});
                await socket.SendAsync(Packet(7,Encoding.UTF8.GetBytes(auth)),WebSocketMessageType.Binary,true,token);
                using var heartbeat=new PeriodicTimer(TimeSpan.FromSeconds(30));
                var heartTask=Task.Run(async()=>
                {
                    while(await heartbeat.WaitForNextTickAsync(token) && socket.State==WebSocketState.Open)
                        await socket.SendAsync(Packet(2,Encoding.UTF8.GetBytes("[object Object]")),WebSocketMessageType.Binary,true,token);
                },token);
                var buffer=new byte[1024*1024];
                while(socket.State==WebSocketState.Open&&!token.IsCancellationRequested)
                {
                    using var ms=new MemoryStream();WebSocketReceiveResult? result;
                    do { result=await socket.ReceiveAsync(buffer,token);if(result.MessageType==WebSocketMessageType.Close)break;ms.Write(buffer,0,result.Count); }while(!result.EndOfMessage);
                    if(result?.MessageType==WebSocketMessageType.Close)break;
                    PacketCount++;
                    await ProcessPacketAsync(ms.ToArray(),session);
                }
            }
            catch(OperationCanceledException) { break; }
            catch(Exception ex) { Error?.Invoke(ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(10),token); } catch(OperationCanceledException) { break; }
        }
    }
    private static byte[] Packet(int operation,byte[] body)
    {
        var bytes=new byte[16+body.Length];BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0,4),bytes.Length);BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(4,2),16);BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(6,2),1);BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8,4),operation);BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12,4),1);body.CopyTo(bytes,16);return bytes;
    }
    public async Task ProcessPacketAsync(byte[] bytes,RecordingSession session)
    {
        var offset=0;
        while(offset+16<=bytes.Length)
        {
            var length=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset,4));var header=BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset+4,2));
            var version=BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset+6,2));var operation=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset+8,4));
            if(length<header||header<16||offset+length>bytes.Length)break;
            var body=bytes.AsSpan(offset+header,length-header).ToArray();offset+=length;
            if(operation==8)
            {
                try {using var auth=JsonDocument.Parse(body);var code=auth.RootElement.GetProperty("code").GetInt32();Authorized=code==0;if(code!=0)throw new InvalidOperationException($"弹幕鉴权失败：{code}");}
                catch(JsonException ex) {Error?.Invoke("弹幕鉴权响应无效："+ex.Message);}
                continue;
            }
            if(operation!=5)continue;
            if(version is 2 or 3)
            {
                using var input=new MemoryStream(body);
                using Stream decompressed=version==3?new BrotliStream(input,CompressionMode.Decompress):new ZLibStream(input,CompressionMode.Decompress);
                using var output=new MemoryStream();await decompressed.CopyToAsync(output);await ProcessPacketAsync(output.ToArray(),session);continue;
            }
            DanmakuEvent? ev=null;
            try
            {
                using var doc=JsonDocument.Parse(body);var root=doc.RootElement;
                var cmd=root.GetProperty("cmd").GetString()??"";
                LastCommand=cmd;
                var now=DateTimeOffset.UtcNow;var time=Math.Max(0,(now-session.StartedAt).TotalSeconds);

                if(cmd.StartsWith("DANMU_MSG"))
                {
                    var info=root.GetProperty("info");var user=info[2];
                    ev=new(Guid.NewGuid().ToString("N"),session.Id,time,now,"danmaku",user[0].GetInt64(),BestName(info,user),info[1].GetString()??"",1);
                }
                else if(cmd=="SEND_GIFT")
                {
                    var d=root.GetProperty("data");ev=new(Guid.NewGuid().ToString("N"),session.Id,time,now,"gift",TryLong(d,"uid"),TryString(d,"uname"),TryString(d,"giftName"),1);
                }
                else if(cmd=="SUPER_CHAT_MESSAGE")
                {
                    var d=root.GetProperty("data");ev=new(Guid.NewGuid().ToString("N"),session.Id,time,now,"superchat",TryLong(d,"uid"),"",TryString(d,"message"),1);
                }
                else if(cmd.StartsWith("GUARD_BUY"))
                {
                    var d=root.GetProperty("data");ev=new(Guid.NewGuid().ToString("N"),session.Id,time,now,"guard",TryLong(d,"uid"),TryString(d,"username"),"购买舰长",1);
                }

            }
            catch(JsonException) { }
            catch(KeyNotFoundException) { }
            catch(InvalidOperationException) { }
            if(ev is not null&&Received is not null)
            {
                try { await Received(ev); }
                catch(Exception ex) { Error?.Invoke("弹幕业务处理失败："+ex.Message);throw; }
            }
        }
    }
    /// <summary>用户名是否被 B 站打码（例如 "最***"）。</summary>
    public static bool IsMasked(string name)=>name.Contains('*');
    /// <summary>
    /// info[2][1] 在匿名连接下会被打码；新协议在 info[0][15].user.base.name 里同时下发完整用户名，
    /// 这里优先取未打码的那一个。
    /// </summary>
    private static string BestName(JsonElement info,JsonElement user)
    {
        var masked=user.ValueKind==JsonValueKind.Array&&user.GetArrayLength()>1?user[1].GetString()??"":"";
        try
        {
            var extra=info[0][15].GetProperty("user").GetProperty("base").GetProperty("name").GetString()??"";
            if(extra.Length>0&&!IsMasked(extra))return extra;
        }
        catch(Exception ex)when(ex is IndexOutOfRangeException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException){/* 老协议没有这个字段 */}
        return masked;
    }
    private static string TryString(JsonElement x,string name)=>x.TryGetProperty(name,out var v)?v.ToString():"";
    private static long TryLong(JsonElement x,string name)=>x.TryGetProperty(name,out var v)&&v.TryGetInt64(out var n)?n:0;
}
