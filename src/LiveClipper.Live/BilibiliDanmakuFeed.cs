using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LiveClipper.Live;

/// <summary>
/// B 站直播弹幕 WebSocket 适配器。内部使用有界 <see cref="DanmakuBuffer"/> 把网络读取和 supervisor 的
/// 批量落库解耦；连接断开后自动重连，但不会因为弹幕断开影响录像主链路。
/// G7：队列满时挤掉最旧一帧并计数，不再静默 DropOldest。
/// </summary>
public sealed class BilibiliDanmakuFeed(
    BilibiliRoomProbe probe,
    TimeSpan? reconnectDelay = null,
    int queueCapacity = 2048) : IDanmakuFeed, IDanmakuFeedMetrics
{
    private readonly int _queueCapacity = queueCapacity > 0
        ? queueCapacity
        : throw new ArgumentOutOfRangeException(nameof(queueCapacity), "弹幕队列容量必须为正");
    private readonly TimeSpan _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(5);
    private long _acceptedCount;
    private long _droppedCount;
    private long _ignoredCount;
    private long _queueDroppedTotal;
    private long _queueDroppedCurrent;

    public string? LastError { get; private set; }
    public event Action<string>? Error;

    /// <summary>G7：接收队列容量（WebSocket 读取与 supervisor 消费之间）。</summary>
    public int QueueCapacity => _queueCapacity;

    /// <summary>G7：因接收队列已满而被挤掉的最旧帧累计数（含当前采集）。</summary>
    public long QueueDroppedCount =>
        Interlocked.Read(ref _queueDroppedTotal) + Interlocked.Read(ref _queueDroppedCurrent);

    /// <summary>成功解析并下发的弹幕数（含礼物/SC/舰长）。</summary>
    public long AcceptedCount => Interlocked.Read(ref _acceptedCount);

    /// <summary>无法解析的行（坏 JSON / 缺字段）数量；坏行只丢弃，不中断连接。</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>能解析但与本系统无关的子命令数量（如 ONLINE_RANK 等）。</summary>
    public long IgnoredCount => Interlocked.Read(ref _ignoredCount);

    public async IAsyncEnumerable<DanmakuFrame> ReadAsync(
        CaptureContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        // 每次采集新建缓冲：跨会话残留的旧帧不能混进新会话的 offset 时间线。
        var buffer = new DanmakuBuffer(_queueCapacity);
        Interlocked.Exchange(ref _queueDroppedCurrent, 0);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var producer = ProduceAsync(context, buffer, linked.Token);
        try
        {
            while (true)
            {
                var frame = await buffer.DequeueAsync(token).ConfigureAwait(false);
                if (frame is null) break;
                yield return frame;
            }
        }
        finally
        {
            linked.Cancel();
            try { await producer; }
            catch (OperationCanceledException) { }
            catch (Exception) { /* 弹幕失败不能遮盖录制主链路的异常 */ }

            // 当前采集的丢弃计数归档为累计值：下一次采集重置 current 后仍能看到历史。
            var current = Interlocked.Exchange(ref _queueDroppedCurrent, 0);
            if (current != 0) Interlocked.Add(ref _queueDroppedTotal, current);
        }
    }

    private async Task ProduceAsync(
        CaptureContext context,
        DanmakuBuffer buffer,
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ReadConnectionAsync(context, buffer, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 网络/风控错误由下一轮重连处理；supervisor 仍可继续录制。
                    LastError = ex.Message;
                    Error?.Invoke(ex.Message);
                }

                try { await Task.Delay(_reconnectDelay, token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            buffer.Complete();
        }
    }

    private async Task ReadConnectionAsync(
        CaptureContext context,
        DanmakuBuffer buffer,
        CancellationToken token)
    {
        var server = await probe.GetDanmakuServerAsync(context.Session.RoomId, token);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
        if (!string.IsNullOrWhiteSpace(probe.Cookie))
            socket.Options.SetRequestHeader("Cookie", probe.Cookie);

        await socket.ConnectAsync(new Uri($"wss://{server.Host}:{server.Port}/sub"), token);

        var auth = JsonSerializer.Serialize(new
        {
            uid = probe.SelfUid,
            roomid = server.RoomId,
            protover = 3,
            buvid = server.Buvid,
            platform = "web",
            type = 2,
            key = server.Token
        });
        await SendAsync(socket, Packet(7, Encoding.UTF8.GetBytes(auth)), token);

        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var heartbeat = HeartbeatAsync(socket, heartbeatStop.Token);
        try
        {
            var receiveBuffer = new byte[1024 * 1024];
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult? result;
                do
                {
                    result = await socket.ReceiveAsync(receiveBuffer, token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (message.Length + result.Count > 8 * 1024 * 1024)
                        throw new InvalidOperationException("弹幕包超过 8MB 上限");
                    message.Write(receiveBuffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result?.MessageType == WebSocketMessageType.Close) break;
                await ProcessPacketAsync(message.ToArray(), context, buffer, token);
            }
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { } catch (Exception) { }
        }
    }

    private static async Task HeartbeatAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(token) && socket.State == WebSocketState.Open)
            await SendAsync(socket, Packet(2, Encoding.UTF8.GetBytes("[object Object]")), token);
    }

    private static async Task SendAsync(ClientWebSocket socket, byte[] payload, CancellationToken token)
    {
        await socket.SendAsync(payload, WebSocketMessageType.Binary, true, token);
    }

    private static byte[] Packet(int operation, byte[] body)
    {
        var bytes = new byte[16 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), bytes.Length);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), operation);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12, 4), 1);
        body.CopyTo(bytes, 16);
        return bytes;
    }

    private async Task ProcessPacketAsync(
        byte[] bytes,
        CaptureContext context,
        DanmakuBuffer buffer,
        CancellationToken token)
    {
        var offset = 0;
        while (offset + 16 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            var header = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset + 4, 2));
            var version = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset + 6, 2));
            var operation = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 8, 4));
            if (length < header || header < 16 || offset + length > bytes.Length) break;

            var body = bytes.AsSpan(offset + header, length - header).ToArray();
            offset += length;

            if (operation == 8)
            {
                using var auth = JsonDocument.Parse(body);
                var code = auth.RootElement.GetProperty("code").GetInt32();
                if (code != 0)
                    throw new InvalidOperationException($"弹幕鉴权失败：{code}");
                continue;
            }

            if (operation != 5) continue;

            if (version is 2 or 3)
            {
                using var input = new MemoryStream(body);
                using Stream decompressed = version == 3
                    ? new BrotliStream(input, CompressionMode.Decompress)
                    : new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                await decompressed.CopyToAsync(output, token);
                await ProcessPacketAsync(output.ToArray(), context, buffer, token);
                continue;
            }

            try
            {
                var frame = ParseLine(Encoding.UTF8.GetString(body), context);
                if (frame is not null
                    && buffer.TryEnqueue(frame) == DanmakuEnqueueResult.DroppedOldest)
                    Interlocked.Increment(ref _queueDroppedCurrent);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // 忽略无法识别的子命令，不影响后续弹幕。
                Interlocked.Increment(ref _droppedCount);
            }
        }
    }

    /// <summary>
    /// 解析一行（已解压的）弹幕 JSON。坏行只增加 <see cref="DroppedCount"/> 并返回 null，
    /// 与本系统无关的子命令只增加 <see cref="IgnoredCount"/>；两条路径都不会抛异常。
    /// 公开给离线测试使用。
    /// </summary>
    public DanmakuFrame? ParseLine(string json, CaptureContext context)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var frame = ParseFrame(document.RootElement, context);
            if (frame is null)
            {
                Interlocked.Increment(ref _ignoredCount);
                return null;
            }

            Interlocked.Increment(ref _acceptedCount);
            return frame;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Interlocked.Increment(ref _droppedCount);
            return null;
        }
    }

    /// <summary>采集时间基准：有共享时钟时以时钟零点为准，否则回退旧会话锚点。</summary>
    public static TimeSpan ElapsedSinceCapture(CaptureContext context)
    {
        if (context.Clock is { } clock) return clock.Elapsed;
        var elapsed = DateTimeOffset.UtcNow - context.Session.StartedAt;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private DanmakuFrame? ParseFrame(JsonElement root, CaptureContext context)
    {
        var cmd = root.GetProperty("cmd").GetString() ?? "";
        var now = DateTimeOffset.UtcNow;
        var offset = ElapsedSinceCapture(context);
        var id = Guid.NewGuid().ToString("N");

        if (cmd.StartsWith("DANMU_MSG", StringComparison.Ordinal))
        {
            var info = root.GetProperty("info");
            var user = info[2];
            var uid = user[0].GetInt64();
            var name = BestName(info, user);
            return new DanmakuFrame(id, context.Session.Id, offset, now, uid, name,
                info[1].GetString() ?? "", "danmaku");
        }

        if (cmd == "SEND_GIFT")
        {
            var data = root.GetProperty("data");
            return new DanmakuFrame(id, context.Session.Id, offset, now,
                TryLong(data, "uid"), TryString(data, "uname"),
                TryString(data, "giftName") + " x" + TryLong(data, "num"),
                "gift");
        }

        if (cmd == "SUPER_CHAT_MESSAGE")
        {
            var data = root.GetProperty("data");
            return new DanmakuFrame(id, context.Session.Id, offset, now,
                TryLong(data, "uid"), TryString(data, "user", "uname"),
                TryString(data, "message"), "superchat");
        }

        if (cmd.StartsWith("GUARD_BUY", StringComparison.Ordinal))
        {
            var data = root.GetProperty("data");
            return new DanmakuFrame(id, context.Session.Id, offset, now,
                TryLong(data, "uid"), TryString(data, "username"),
                "购买" + TryString(data, "gift_name"), "guard");
        }

        return null;
    }

    private static string BestName(JsonElement info, JsonElement user)
    {
        var masked = user.ValueKind == JsonValueKind.Array && user.GetArrayLength() > 1
            ? user[1].GetString() ?? ""
            : "";
        try
        {
            var extra = info[0][15].GetProperty("user").GetProperty("base").GetProperty("name").GetString() ?? "";
            if (extra.Length > 0 && !extra.Contains('*')) return extra;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or KeyNotFoundException
            or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // 旧协议没有 info[0][15]。
        }
        return masked;
    }

    private static string TryString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "";

    private static string TryString(JsonElement element, string parent, string name)
    {
        return element.TryGetProperty(parent, out var node) ? TryString(node, name) : "";
    }

    private static long TryLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
