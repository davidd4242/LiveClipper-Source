using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using LiveClipper.Live;

namespace LiveClipper.Tests;

/// <summary>
/// task-13：G7 弹幕队列丢弃可观测、G8 HLS 回退、G9 Stop 幂等与恢复重复扫描。
/// 全部离线：临时目录 + 假 HTTP handler，不访问网络、不启动 FFmpeg。
/// </summary>
public sealed class LiveEdgePolicyTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    // ---------- G7：弹幕缓冲与指标 ----------

    [Fact]
    public void DanmakuBuffer_DropsOldestAndCounts()
    {
        var buffer = new DanmakuBuffer(3);
        for (var index = 0; index < 5; index++)
        {
            var result = buffer.TryEnqueue(Frame(index));
            Assert.Equal(index >= 3 ? DanmakuEnqueueResult.DroppedOldest : DanmakuEnqueueResult.Enqueued, result);
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(3, buffer.Capacity);
        Assert.Equal(2, buffer.DroppedCount);

        buffer.Complete();
        Assert.Equal(DanmakuEnqueueResult.Completed, buffer.TryEnqueue(Frame(99)));
    }

    [Fact]
    public async Task DanmakuBuffer_DrainKeepsNewestFrames_ThenNull()
    {
        var buffer = new DanmakuBuffer(2);
        buffer.TryEnqueue(Frame(1));
        buffer.TryEnqueue(Frame(2));
        buffer.TryEnqueue(Frame(3)); // 挤掉 frame 1
        buffer.Complete();

        Assert.Equal("dm-2", (await buffer.DequeueAsync(CancellationToken.None))!.Id);
        Assert.Equal("dm-3", (await buffer.DequeueAsync(CancellationToken.None))!.Id);
        Assert.Null(await buffer.DequeueAsync(CancellationToken.None));
        Assert.Equal(1, buffer.DroppedCount);
    }

    [Fact]
    public async Task SupervisorSnapshot_ReportsFeedQueueCapacityAndDroppedFrames()
    {
        var root = NewRoot("metrics");
        try
        {
            var store = new JsonLiveSessionStore(root);
            var feed = new MetricsFeed(queueCapacity: 16, dropped: 7);
            var supervisor = new LiveSessionSupervisor(
                5001, new LiveProbe(), new HlsResolver(), new SilentRecorder(), feed, store, FastOptions());
            try
            {
                await supervisor.StartAsync(autoRecord:true);
                await WaitUntilAsync(() => supervisor.Snapshot.Capturing, "开始采集");

                var snapshot = supervisor.Snapshot;
                Assert.Equal(7, snapshot.DanmakuQueueDropped);
                Assert.Equal(16, snapshot.DanmakuQueueCapacity);
            }
            finally
            {
                await supervisor.DisposeAsync();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------- G8：HLS 回退与录制参数 ----------

    [Fact]
    public async Task ProbeResolveFallsBackToHlsWhenOnlyHlsAdvertised()
    {
        var probe = ProbeWithJson(PlayInfoJson(flv: false, hls: true));
        var source = await probe.ResolveAsync(Room(), CancellationToken.None);

        Assert.NotNull(source);
        Assert.Equal("hls", source!.Format);
        Assert.Equal("https://cn-hls.example.com/live-bvc/hls/stream.m3u8?expires=2", source.Uri.ToString());
    }

    [Fact]
    public async Task ProbeResolvePrefersFlvWhenBothAdvertised()
    {
        var probe = ProbeWithJson(PlayInfoJson(flv: true, hls: true));
        var source = await probe.ResolveAsync(Room(), CancellationToken.None);

        Assert.NotNull(source);
        Assert.Equal("flv", source!.Format);
        Assert.Equal("https://cn-live.example.com/live-bvc/flv/stream.flv?expires=1", source.Uri.ToString());
    }

    [Fact]
    public async Task ProbeResolveReturnsNullWhenNoPlayableStream()
    {
        var probe = ProbeWithJson("""
            {"code":0,"message":"0","data":{"live_status":1,"playurl_info":null}}
            """);
        Assert.Null(await probe.ResolveAsync(Room(), CancellationToken.None));
    }

    [Fact]
    public void RecorderInputArguments_HlsEnablesProtocolWhitelist_FlvDoesNot()
    {
        var hls = FfmpegSegmentRecorder.BuildInputArguments(
            new StreamSource(new Uri("https://example.com/live.m3u8"), "hls"));
        Assert.Contains("-protocol_whitelist", hls);
        Assert.Contains(hls, item => item.Contains("http", StringComparison.Ordinal));
        Assert.Contains("-allowed_extensions", hls);

        var flv = FfmpegSegmentRecorder.BuildInputArguments(
            new StreamSource(new Uri("https://example.com/live.flv"), "flv"));
        Assert.DoesNotContain("-protocol_whitelist", flv);
        Assert.Equal("-hide_banner", flv[0]);

        var file = FfmpegSegmentRecorder.BuildInputArguments(
            new StreamSource(new Uri("file:///C:/tmp/source.mp4"), "hls"));
        Assert.DoesNotContain("-protocol_whitelist", file);
    }

    [Fact]
    public async Task SupervisorSetsReadableErrorWhenResolverReturnsNull()
    {
        var root = NewRoot("no-stream");
        try
        {
            var store = new JsonLiveSessionStore(root);
            var supervisor = new LiveSessionSupervisor(
                5002, new LiveProbe(), new NullResolver(), new SilentRecorder(), new SilentDanmakuFeed(),
                store, FastOptions());
            try
            {
                await supervisor.StartAsync(autoRecord:true);
                await WaitUntilAsync(
                    () => supervisor.Snapshot.LastError?.Contains("没有取到可用的直播流地址") == true,
                    "可读的取流失败原因");
                Assert.Contains("没有取到可用的直播流地址", supervisor.Snapshot.LastError);
            }
            finally
            {
                await supervisor.DisposeAsync();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------- G9：Stop 幂等与恢复重复扫描 ----------

    [Fact]
    public async Task ManagerStopIsIdempotent()
    {
        var root = NewRoot("manager-stop");
        try
        {
            var store = new JsonLiveSessionStore(root);
            var manager = new LiveRoomManager(roomId => new LiveSessionSupervisor(
                roomId, new LiveProbe(), new HlsResolver(), new SilentRecorder(), new SilentDanmakuFeed(),
                store, FastOptions()));
            try
            {
                await manager.StartAsync(6001);
                Assert.True(await manager.StopAsync(6001));
                Assert.False(await manager.StopAsync(6001)); // 重复调用不抛异常
                Assert.False(await manager.StopAsync(9999)); // 未知房间也只是 false
                await manager.StopAllAsync();
                Assert.Equal(0, manager.Count);
            }
            finally
            {
                await manager.DisposeAsync();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RecoveryRepeatedScan_IsIdempotent_AndRegistryKeepsOneEntry()
    {
        var root = NewRoot("recovery-skip");
        try
        {
            var registry = new JsonLiveRoomRegistry(Path.Combine(root, "rooms.json"));
            await registry.SetActiveAsync(7001, true, CancellationToken.None);
            var service = new LiveRoomRecoveryService(registry);
            var calls = 0;
            Task Start(long roomId, CancellationToken token)
            {
                calls++;
                return Task.CompletedTask;
            }

            var first = await service.RunOnceAsync(Start);
            Assert.Equal(1, first.Attempted);
            Assert.Equal(1, first.Succeeded);
            Assert.Equal(1, calls);

            // 重复扫描幂等：房间再次被尝试（manager.StartAsync 本身幂等），但登记簿不重复、不异常。
            var second = await service.RunOnceAsync(Start);
            Assert.Equal(1, second.Attempted);
            Assert.Equal(1, second.Succeeded);
            Assert.Equal(2, calls);

            var registrations = await registry.ListAsync(CancellationToken.None);
            var single = Assert.Single(registrations);
            Assert.Equal(7001, single.RoomId);
            Assert.Equal(0, single.RecoveryFailures);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RegistrySetActiveTwice_KeepsSingleRegistration()
    {
        var root = NewRoot("registry-idempotent");
        try
        {
            var registry = new JsonLiveRoomRegistry(Path.Combine(root, "rooms.json"));
            await registry.SetActiveAsync(8001, true, CancellationToken.None);
            await registry.SetActiveAsync(8001, true, CancellationToken.None);

            var single = Assert.Single(await registry.ListAsync(CancellationToken.None));
            Assert.Equal(8001, single.RoomId);
            Assert.True(single.Active);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task UidEndpointDistinguishesAbsentRoomInvalidInputAndNetworkFailure()
    {
        async Task<int?> Status(long uid,Exception? error=null)
        {
            var result=await LiveClipper.App.LiveEndpoints.ResolveUidAsync(uid,(_,_)=>error is null
                ?Task.FromResult(new LiveClipper.Domain.ResolvedLiveUser(uid,"name",12,34,true,"title"))
                :Task.FromException<LiveClipper.Domain.ResolvedLiveUser>(error),CancellationToken.None);
            return ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode;
        }
        Assert.Equal(200,await Status(1));Assert.Equal(400,await Status(0));
        Assert.Equal(404,await Status(1,new ArgumentException("No live room")));
        Assert.Equal(502,await Status(1,new HttpRequestException("Network failed")));
        Assert.Equal(502,await Status(1,new TaskCanceledException("Timeout")));
    }

    [Fact]
    public async Task RoomProbeExposesCanonicalWithoutChangingRegistrationIdentity()
    {
        var probe=ProbeWithJson("""{"code":0,"data":{"room_id":22637261,"live_status":1}}""");
        var shortRoom=await probe.ProbeAsync(7734200,CancellationToken.None);
        var longRoom=await probe.ProbeAsync(22637261,CancellationToken.None);
        Assert.Equal(7734200,shortRoom.RoomId);
        Assert.Equal(22637261,longRoom.RoomId);
        Assert.Equal(22637261,shortRoom.CanonicalRoomId);
        Assert.Equal(shortRoom.CanonicalRoomId,longRoom.CanonicalRoomId);
        Assert.Equal(RoomAvailability.Live,shortRoom.Availability);
        // Optional metadata keeps existing constructor callers and recording keys intact.
        var legacy=new LiveRoom(12,RoomAvailability.Offline);
        Assert.Null(legacy.CanonicalRoomId);Assert.Equal(12,legacy.RoomId);
    }

    // ---------- helpers ----------

    private static LiveRoom Room() =>
        new(6006, RoomAvailability.Live, "测试直播间", "测试主播", DateTimeOffset.UtcNow);

    private static StreamSource FixedStream() =>
        new(new Uri("http://127.0.0.1:9/fake-live.m3u8"), "hls");

    private static DanmakuFrame Frame(int index) =>
        new($"dm-{index}", "s1", TimeSpan.Zero, DateTimeOffset.UtcNow, index + 1, "观众", $"弹幕 {index}");

    private static string PlayInfoJson(bool flv, bool hls)
    {
        var formats = new List<string>();
        if (flv)
            formats.Add("""
                {"protocol_name":"http_stream","format":[{"format_name":"flv","codec":[{"codec_name":"avc","base_url":"/live-bvc/flv/stream.flv?expires=1","url_info":[{"host":"https://cn-live.example.com","extra":""}]}]}]}
                """);
        if (hls)
            formats.Add("""
                {"protocol_name":"http_hls","format":[{"format_name":"ts","codec":[{"codec_name":"avc","base_url":"/live-bvc/hls/stream.m3u8?expires=2","url_info":[{"host":"https://cn-hls.example.com","extra":""}]}]}]}
                """);
        return "{\"code\":0,\"message\":\"0\",\"data\":{\"live_status\":1,\"playurl_info\":{\"playurl\":{\"stream\":["
               + string.Join(",", formats)
               + "]}}}}";
    }

    private static BilibiliRoomProbe ProbeWithJson(string json) =>
        new(http: new HttpClient(new StubHandler(json)));

    private static LiveSupervisorOptions FastOptions() => new()
    {
        ProbeInterval = TimeSpan.FromMilliseconds(20),
        RetryInterval = TimeSpan.FromMilliseconds(20),
        DanmakuBatchSize = 4,
        DanmakuFlushInterval = TimeSpan.FromMilliseconds(10),
        DanmakuQueueCapacity = 64
    };

    private static string NewRoot(string scenario) =>
        Path.Combine(Path.GetTempPath(), "live-edge-tests", scenario + "-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string what)
    {
        var deadline = Environment.TickCount64 + (long)WaitTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"等待超时：{what}");
    }

    // ---------- fakes ----------

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class LiveProbe : ILiveRoomProbe
    {
        public Task<LiveRoom> ProbeAsync(long roomId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new LiveRoom(
                roomId, RoomAvailability.Live, "测试直播间", "测试主播", DateTimeOffset.UtcNow));
        }
    }

    private sealed class HlsResolver : IStreamSourceResolver
    {
        public Task<StreamSource?> ResolveAsync(LiveRoom room, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<StreamSource?>(FixedStream());
        }
    }

    private sealed class NullResolver : IStreamSourceResolver
    {
        public Task<StreamSource?> ResolveAsync(LiveRoom room, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<StreamSource?>(null);
        }
    }

    private sealed class SilentRecorder : ISegmentRecorder
    {
        public Task RunAsync(
            CaptureContext context,
            Func<SegmentArtifact, CancellationToken, Task> onSegment,
            CancellationToken token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
    }

    private sealed class SilentDanmakuFeed : IDanmakuFeed
    {
        public async IAsyncEnumerable<DanmakuFrame> ReadAsync(
            CaptureContext context,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield break;
        }
    }

    private sealed class MetricsFeed(int queueCapacity, long dropped) : IDanmakuFeed, IDanmakuFeedMetrics
    {
        public int QueueCapacity => queueCapacity;
        public long QueueDroppedCount => dropped;

        public async IAsyncEnumerable<DanmakuFrame> ReadAsync(
            CaptureContext context,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield break;
        }
    }
}
