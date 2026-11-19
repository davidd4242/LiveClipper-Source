using System.Globalization;
using System.Runtime.CompilerServices;
using LiveClipper.Core;
using LiveClipper.Live;

namespace LiveClipper.Tests;

/// <summary>
/// 直播底座第二阶段：G2 离线系统测试（录制器/弹幕/清理器/supervisor 停止幂等）、
/// G3 统一采集时间基准（录制与弹幕共用 CaptureClock，漂移 &lt; 50ms）、
/// G5 历史会话查询的映射/过滤/分页逻辑。
/// </summary>
public sealed class LiveFoundationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public LiveFoundationTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RecordingToggle_DefaultMonitors_StopsWithoutDeleting_AndDoesNotRestart()
    {
        var root = NewTempRoot("recording-toggle");
        try
        {
            var store = new JsonLiveSessionStore(root);
            await using var manager = new LiveRoomManager(id => NewSupervisor(store, new SilentRecorder(), new EmptyDanmakuFeed(), id));
            await manager.StartAsync(4001);
            await manager.StartAsync(4002);
            await WaitUntilAsync(() => manager.Snapshots().All(s => s.Room is not null), "monitor probes");
            Assert.All(manager.Snapshots(), s => { Assert.True(s.Running); Assert.False(s.AutoRecord); Assert.False(s.Capturing); });
            Assert.Empty((await LiveSessionQueries.ListSessionsAsync(store)).Sessions);
            await manager.SetRecordingAsync(4001, true);
            await WaitUntilAsync(() => manager.Snapshots().Single(s => s.RoomId == 4001).Capturing, "recording starts");
            var sessionId = manager.Snapshots().Single(s => s.RoomId == 4001).Session!.Id;
            await manager.SetRecordingAsync(4001, false);
            await manager.SetRecordingAsync(4001, false);
            await Task.Delay(150);
            var stopped = manager.Snapshots().Single(s => s.RoomId == 4001);
            Assert.True(stopped.Running);
            Assert.False(stopped.AutoRecord);
            Assert.False(stopped.Capturing);
            Assert.Equal(LiveSessionState.Ended, (await store.GetSessionAsync(sessionId, CancellationToken.None))!.State);
            Assert.Single((await LiveSessionQueries.ListSessionsAsync(store)).Sessions);
            Assert.False(manager.Snapshots().Single(s => s.RoomId == 4002).AutoRecord);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task RoomRegistry_PersistsExplicitRecordingOff()
    {
        var root = NewTempRoot("recording-registry");
        try
        {
            var path = Path.Combine(root, "rooms.json");
            var registry = new JsonLiveRoomRegistry(path);
            await registry.SetActiveAsync(4001, true, autoRecord: true);
            await registry.SetActiveAsync(4001, true, autoRecord: false);
            var restored = Assert.Single(await new JsonLiveRoomRegistry(path).ListAsync());
            Assert.True(restored.Active);
            Assert.False(restored.AutoRecord);
        }
        finally { TryDelete(root); }
    }


    // ---------------- G3：采集时钟 ----------------

    [Fact]
    public void CaptureClock_StartsOnceAndReportsElapsedFromAnchoredZero()
    {
        var anchor = new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(anchor);
        var clock = new CaptureClock(time);

        Assert.Null(clock.StartedAt);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        Assert.True(clock.Start());
        Assert.False(clock.Start()); // 幂等：重复 Start 不重锚
        Assert.Equal(anchor, clock.StartedAt);

        time.Advance(TimeSpan.FromMilliseconds(1250));
        Assert.Equal(TimeSpan.FromMilliseconds(1250), clock.Elapsed);

        // 时钟回拨时不能出现负 offset。
        time.Set(anchor - TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Fact]
    public void DanmakuAndSegmentOffsets_ShareOneCaptureClock_WithNoWallClockDrift()
    {
        var resolveAt = new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(resolveAt);
        var clock = new CaptureClock(time);
        clock.Start();

        // 会话在流解析前 2 秒创建。旧实现以 StartedAt 为基准，弹幕会比录制多出 2 秒系统性漂移。
        var session = Session("clock-session", startedAt: resolveAt - TimeSpan.FromSeconds(2));
        var context = new CaptureContext(
            session,
            new StreamSource(new Uri("http://127.0.0.1:9/fake-live.m3u8"), "hls"),
            clock);

        time.Advance(TimeSpan.FromSeconds(1));
        var feed = new BilibiliDanmakuFeed(new BilibiliRoomProbe());
        var frame = feed.ParseLine(DanmuJson, context);
        Assert.NotNull(frame);
        Assert.Equal(TimeSpan.FromSeconds(1), frame!.Offset); // 不是 3 秒

        var elapsed = FfmpegSegmentRecorder.ElapsedSinceCapture(context);
        Assert.Equal(TimeSpan.FromSeconds(1), elapsed);
        var segmentOffset = FfmpegSegmentRecorder.SegmentOffset(
            TimeSpan.Zero, elapsed, TimeSpan.FromSeconds(0.5));
        var segmentEnd = segmentOffset + TimeSpan.FromSeconds(0.5);
        var drift = Math.Abs((segmentEnd - frame.Offset).TotalMilliseconds);
        Assert.True(drift < 50, $"弹幕与分片 offset 应同基准，实际漂移 {drift:0.###}ms");
    }

    [Fact]
    public void DanmakuParse_CountsBadLinesAndIgnoresUnknownCommands()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var clock = new CaptureClock(time);
        clock.Start();
        var context = new CaptureContext(NewSession("parse"), FixedStream(), clock);
        var feed = new BilibiliDanmakuFeed(new BilibiliRoomProbe());

        // 坏行：只计数，不抛出。
        Assert.Null(feed.ParseLine("{\"cmd\":", context));
        Assert.Equal(1, feed.DroppedCount);
        Assert.Equal(0, feed.AcceptedCount);

        // 与本系统无关的子命令：忽略计数。
        Assert.Null(feed.ParseLine("{\"cmd\":\"ONLINE_RANK_V2\",\"data\":{}}", context));
        Assert.Equal(1, feed.IgnoredCount);

        var danmaku = feed.ParseLine(DanmuJson, context);
        Assert.NotNull(danmaku);
        Assert.Equal(12345, danmaku!.Uid);
        Assert.Equal("测试观众", danmaku.User);
        Assert.Equal("测试弹幕", danmaku.Text);
        Assert.Equal("danmaku", danmaku.Kind);
        Assert.Equal(1, feed.AcceptedCount);

        var gift = feed.ParseLine(
            "{\"cmd\":\"SEND_GIFT\",\"data\":{\"uid\":7,\"uname\":\"送礼人\",\"giftName\":\"辣条\",\"num\":3}}",
            context);
        Assert.NotNull(gift);
        Assert.Equal("辣条 x3", gift!.Text);
        Assert.Equal("gift", gift.Kind);
        Assert.Equal(2, feed.AcceptedCount);

        // 合法 JSON 但缺字段：仍算坏行，不能把异常抛到连接循环。
        Assert.Null(feed.ParseLine("{\"cmd\":\"DANMU_MSG\"}", context));
        Assert.Equal(2, feed.DroppedCount);
        Assert.Equal(1, feed.IgnoredCount);
    }

    // ---------------- G2：FFmpeg 分片录制器 ----------------

    [Fact]
    public async Task Recorder_ProducesOrderedSegmentsWithProbedDurations()
    {
        var ffmpeg = TestFfmpeg();
        var ffprobe = ffmpeg is null ? null : TestFfprobe(ffmpeg);
        if (ffmpeg is null || ffprobe is null)
        {
            _output.WriteLine("[skip] 未找到 ffmpeg 或 ffprobe，录制分片时长探测测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg，并确保同目录存在 ffprobe）");
            return;
        }

        var root = NewTempRoot("recorder");
        try
        {
            Directory.CreateDirectory(root);
            var source = await CreateTestSource(ffmpeg, root, seconds: 5);
            var recorder = new FfmpegSegmentRecorder(new FfmpegRecorderOptions
            {
                FfmpegPath = ffmpeg,
                FfprobePath = ffprobe,
                StorageRoot = Path.Combine(root, "media"),
                SegmentSeconds = 2,
                StallTimeout = TimeSpan.FromSeconds(20),
                StopTimeout = TimeSpan.FromSeconds(5)
            });

            var artifacts = await RecordAsync(recorder, source, "rec-normal");

            Assert.True(artifacts.Count >= 2, $"本地 5 秒素材应至少切出两个分片，实际 {artifacts.Count}");
            for (var i = 0; i < artifacts.Count; i++)
            {
                Assert.Equal(i + 1, artifacts[i].Sequence);
                Assert.Equal($"segment_{i:00000}.ts", Path.GetFileName(artifacts[i].Path));
                Assert.True(File.Exists(artifacts[i].Path));
                var runDirectory = Path.GetFileName(Path.GetDirectoryName(artifacts[i].Path));
                Assert.StartsWith("run_", runDirectory, StringComparison.Ordinal);
            }
            Assert.InRange(artifacts[0].Offset.TotalSeconds, 0, 0.5);
            for (var i = 1; i < artifacts.Count; i++)
                Assert.True(artifacts[i].Offset >= artifacts[i - 1].Offset, "分片 offset 必须单调递增");
            Assert.All(artifacts.Take(artifacts.Count - 1),
                item => Assert.InRange(item.Duration.TotalSeconds, 1.5, 2.4));
            Assert.All(artifacts, item => Assert.True(item.Bytes > 0, "分片不应为空"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Recorder_FallsBackToSegmentSecondsWhenDurationProbeFails()
    {
        var ffmpeg = TestFfmpeg();
        if (ffmpeg is null)
        {
            _output.WriteLine("[skip] 未找到 ffmpeg，分片时长回退测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");
            return;
        }

        var root = NewTempRoot("recorder-fallback");
        try
        {
            Directory.CreateDirectory(root);
            var source = await CreateTestSource(ffmpeg, root, seconds: 5);
            // 用 cmd.exe 冒充 ffprobe：探测必然失败，分片时长回退到 SegmentSeconds。
            var recorder = new FfmpegSegmentRecorder(new FfmpegRecorderOptions
            {
                FfmpegPath = ffmpeg,
                FfprobePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                StorageRoot = Path.Combine(root, "media"),
                SegmentSeconds = 2,
                StallTimeout = TimeSpan.FromSeconds(20),
                StopTimeout = TimeSpan.FromSeconds(5)
            });

            var artifacts = await RecordAsync(recorder, source, "rec-fallback");

            Assert.True(artifacts.Count >= 2, $"应至少切出两个分片，实际 {artifacts.Count}");
            Assert.All(artifacts, item => Assert.Equal(2d, item.Duration.TotalSeconds, 3));
            Assert.Equal(TimeSpan.Zero, artifacts[0].Offset);
            Assert.Equal(TimeSpan.FromSeconds(2), artifacts[1].Offset);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------------- G2：磁盘清理器边界 ----------------

    [Fact]
    public async Task Janitor_DeletesExpiredEndedSessionsButKeepsActiveAndOpenOnes()
    {
        var root = NewTempRoot("janitor");
        try
        {
            var buffer = Path.Combine(root, "buffer");
            using var store = new JsonLiveSessionStore(Path.Combine(root, "store"));
            var now = new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero);
            var time = new ManualTimeProvider(now);

            var expired = Session("expired", LiveSessionState.Ended, now.AddDays(-10), now.AddDays(-10).AddMinutes(30));
            var recent = Session("recent", LiveSessionState.Ended, now.AddHours(-2), now.AddMinutes(-60));
            var open = Session("open", LiveSessionState.Recording, now.AddDays(-2));
            foreach (var session in new[] { expired, recent, open })
                await store.SaveSessionAsync(session, CancellationToken.None);

            foreach (var id in new[] { "expired", "recent", "open", "active-orphan" })
            {
                var directory = Path.Combine(buffer, id);
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(
                    Path.Combine(directory, "segment_00000.ts"), new byte[512], CancellationToken.None);
            }

            var active = new HashSet<string>(StringComparer.Ordinal) { "active-orphan" };
            var janitor = new LiveStorageJanitor(
                store,
                new LiveStorageOptions
                {
                    BufferRoot = buffer,
                    Retention = TimeSpan.FromDays(3),
                    MaxTotalBytes = 1L << 40,
                    MinFreeBytes = 0,
                    DeleteMetadata = true
                },
                () => active,
                time);

            var inspect = await janitor.InspectAsync(CancellationToken.None);
            Assert.False(inspect.Enforced);
            Assert.Equal(0, inspect.DeletedSessions);
            Assert.Equal(4, inspect.SessionDirectories);
            Assert.Equal(2, inspect.ProtectedSessions); // 进行中会话 + 活动会话
            Assert.Equal(1, inspect.ExpiredSessions);
            Assert.True(Directory.Exists(Path.Combine(buffer, "expired")), "Inspect 不能删除目录");

            var enforced = await janitor.EnforceAsync(CancellationToken.None);
            Assert.True(enforced.Enforced);
            Assert.Equal(1, enforced.DeletedSessions);
            Assert.Equal(1, enforced.ExpiredSessions);
            Assert.Empty(enforced.Warnings);
            Assert.False(Directory.Exists(Path.Combine(buffer, "expired")));
            Assert.True(Directory.Exists(Path.Combine(buffer, "recent")));
            Assert.True(Directory.Exists(Path.Combine(buffer, "open")));
            Assert.True(Directory.Exists(Path.Combine(buffer, "active-orphan")));
            Assert.Null(await store.GetSessionAsync("expired", CancellationToken.None));
            Assert.NotNull(await store.GetSessionAsync("recent", CancellationToken.None));

            var second = await janitor.EnforceAsync(CancellationToken.None);
            Assert.Equal(0, second.DeletedSessions);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------------- G2：supervisor 停止幂等 ----------------

    [Fact]
    public async Task Supervisor_StopIsIdempotentAndFinalizesSessionOnce()
    {
        var root = NewTempRoot("supervisor-stop");
        try
        {
            using var store = new JsonLiveSessionStore(root);
            await using var supervisor = NewSupervisor(store, new SilentRecorder(), new EmptyDanmakuFeed());

            await supervisor.StopAsync(); // 从未启动时停止必须安全
            Assert.False(supervisor.Snapshot.Running);

            await supervisor.StartAsync(autoRecord:true);
            await WaitUntilAsync(() => supervisor.Snapshot.Capturing, "supervisor 进入采集");
            var sessionId = supervisor.Snapshot.Session?.Id;
            Assert.NotNull(sessionId);

            await supervisor.StopAsync();
            await supervisor.StopAsync();

            var snapshot = supervisor.Snapshot;
            Assert.False(snapshot.Running);
            Assert.False(snapshot.Capturing);
            var saved = await store.GetSessionAsync(sessionId!, CancellationToken.None);
            Assert.NotNull(saved);
            Assert.Equal(LiveSessionState.Ended, saved!.State);
            Assert.NotNull(saved.EndedAt);
            Assert.Single(Directory.GetFiles(root, "*.session.json"));

            await supervisor.DisposeAsync();
            await supervisor.DisposeAsync(); // Dispose 也必须幂等
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------------- G5：历史会话查询映射 ----------------

    [Fact]
    public async Task SessionQueries_ListFiltersPagesAndMapsSafeFields()
    {
        var root = NewTempRoot("queries");
        try
        {
            using var store = new JsonLiveSessionStore(root);
            var now = new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero);
            var ended = Session("ended", LiveSessionState.Ended, now.AddHours(-1), now.AddMinutes(-30),
                lastSequence: 2, recoveredSegments: 1, roomId: 11);
            var open = Session("open", LiveSessionState.Recording, now.AddHours(-2), roomId: 11);
            var older = Session("older", LiveSessionState.Ended, now.AddHours(-3), now.AddHours(-2).AddMinutes(-30),
                lastSequence: 1, roomId: 22);
            foreach (var session in new[] { ended, open, older })
                await store.SaveSessionAsync(session, CancellationToken.None);

            await store.AppendSegmentAsync(
                Segment("ended", 1, 0, 2, Path.Combine(root, "secret", "a.ts")), CancellationToken.None);
            await store.AppendSegmentAsync(
                Segment("ended", 2, 2, 2, Path.Combine(root, "secret", "b.ts")), CancellationToken.None);
            await store.AppendDanmakuAsync(
            [
                Danmaku("ended", 1, 1), Danmaku("ended", 2, 2), Danmaku("ended", 3, 3)
            ], CancellationToken.None);

            var (sessions, total) = await LiveSessionQueries.ListSessionsAsync(store);
            Assert.Equal(3, total);
            Assert.Equal(new[] { "ended", "open", "older" }, sessions.Select(item => item.Id).ToArray());
            var summary = sessions[0];
            Assert.Equal("Ended", summary.State);
            Assert.Equal(11, summary.RoomId);
            Assert.Equal(2, summary.Segments);
            Assert.Equal(3, summary.DanmakuCount);
            Assert.Equal(1, summary.RecoveredSegments);
            Assert.Equal(2, summary.LastSequence);
            Assert.InRange(summary.DurationSeconds, 1799, 1801);
            Assert.InRange(summary.SegmentDurationSeconds, 3.999, 4.001);

            var (roomOne, roomTotal) = await LiveSessionQueries.ListSessionsAsync(store, roomId: 11);
            Assert.Equal(2, roomTotal);
            Assert.All(roomOne, item => Assert.Equal(11, item.RoomId));

            var (page, pageTotal) = await LiveSessionQueries.ListSessionsAsync(store, limit: 2, offset: 1);
            Assert.Equal(3, pageTotal);
            Assert.Equal(new[] { "open", "older" }, page.Select(item => item.Id).ToArray());

            Assert.Equal(200, LiveSessionQueries.ClampLimit(9999));
            Assert.Equal(1, LiveSessionQueries.ClampLimit(0));
            Assert.Equal(0, LiveSessionQueries.ClampOffset(-3));

            var detail = await LiveSessionQueries.GetSessionAsync(store, "ended");
            Assert.NotNull(detail);
            Assert.Equal(new[] { 1, 2 }, detail!.Segments.Select(item => item.Sequence).ToArray());
            Assert.Equal(0, detail.Segments[0].OffsetSeconds);
            Assert.Equal(2, detail.Segments[1].OffsetSeconds);
            Assert.Equal(2, detail.Segments[0].DurationSeconds);
            Assert.All(detail.Segments, item => Assert.Equal(Path.GetFileName(item.File), item.File));
            Assert.False(detail.Segments[0].File.Contains(root, StringComparison.OrdinalIgnoreCase),
                "分片视图不能泄漏服务器绝对路径");

            Assert.Null(await LiveSessionQueries.GetSessionAsync(store, "missing"));
            Assert.Null(await LiveSessionQueries.GetSessionAsync(store, " "));
            Assert.Equal(3, await store.CountDanmakuAsync("ended", CancellationToken.None));
            Assert.Equal(0, await store.CountDanmakuAsync("open", CancellationToken.None));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------------- 套件内工具 ----------------

    private const string DanmuJson =
        "{\"cmd\":\"DANMU_MSG\",\"info\":[[0,1,25,16777215,0,0,0,\"\",0],\"测试弹幕\",[12345,\"测试观众\"]]}";

    private static LiveSession NewSession(string id, LiveSessionState state = LiveSessionState.Recording) =>
        new(id, 1, state, DateTimeOffset.UtcNow);

    private static LiveSession Session(
        string id,
        LiveSessionState state = LiveSessionState.Recording,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null,
        int lastSequence = 0,
        int recoveredSegments = 0,
        long roomId = 1) =>
        new(id, roomId, state, startedAt ?? DateTimeOffset.UtcNow, endedAt,
            LastSequence: lastSequence, RecoveredSegments: recoveredSegments);

    private static StreamSource FixedStream() =>
        new(new Uri("http://127.0.0.1:9/fake-live.m3u8"), "hls");

    private static SegmentArtifact Segment(
        string sessionId,
        int sequence,
        double offsetSeconds,
        double durationSeconds,
        string? path = null) =>
        new(sessionId, sequence, TimeSpan.FromSeconds(offsetSeconds), TimeSpan.FromSeconds(durationSeconds),
            1024L * sequence, path ?? Path.Combine(Path.GetTempPath(), $"live-seg-{sequence}.ts"),
            DateTimeOffset.UtcNow);

    private static DanmakuFrame Danmaku(string sessionId, int index, double offsetSeconds) =>
        new($"dm-{index}", sessionId, TimeSpan.FromSeconds(offsetSeconds), DateTimeOffset.UtcNow,
            100 + index, "观众", $"弹幕 {index}");

    private static LiveSessionSupervisor NewSupervisor(
        JsonLiveSessionStore store,
        ISegmentRecorder recorder,
        IDanmakuFeed feed,
        long roomId = 4001) =>
        new(roomId,
            new ScriptedProbe(() => new LiveRoom(roomId, RoomAvailability.Live, "测试直播间", "测试主播",
                DateTimeOffset.UtcNow)),
            new FixedResolver(),
            recorder,
            feed,
            store,
            FastOptions());

    private static LiveSupervisorOptions FastOptions() => new()
    {
        ProbeInterval = TimeSpan.FromMilliseconds(25),
        RetryInterval = TimeSpan.FromMilliseconds(25),
        DanmakuBatchSize = 4,
        DanmakuFlushInterval = TimeSpan.FromMilliseconds(15),
        DanmakuQueueCapacity = 64
    };

    private static string NewTempRoot(string scenario) =>
        Path.Combine(Path.GetTempPath(), "liveclipper-tests", scenario + "-" + Guid.NewGuid().ToString("N"));

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

    private static string? TestFfmpeg()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment)) return fromEnvironment;
        var bundled = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : null;
    }

    private static string? TestFfprobe(string ffmpeg)
    {
        var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (File.Exists(sibling)) return sibling;
        var bundled = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveClipper", "tools", "ffmpeg", "bin", "ffprobe.exe");
        return File.Exists(bundled) ? bundled : null;
    }

    private static async Task<string> CreateTestSource(string ffmpeg, string root, int seconds)
    {
        var path = Path.Combine(root, "source.mp4");
        var tools = new MediaTools(ffmpeg);
        await tools.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", seconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-g", "25", "-sc_threshold", "0", // 每秒一个关键帧，segment muxer 才能按 2 秒切分
            "-c:a", "aac", "-b:a", "96k",
            path
        ], CancellationToken.None);
        return path;
    }

    private static async Task<List<SegmentArtifact>> RecordAsync(
        FfmpegSegmentRecorder recorder,
        string source,
        string sessionId)
    {
        var context = new CaptureContext(Session(sessionId), new StreamSource(new Uri(source), "file"));
        var artifacts = new List<SegmentArtifact>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await recorder.RunAsync(context, (artifact, _) =>
        {
            lock (artifacts) artifacts.Add(artifact);
            return Task.CompletedTask;
        }, timeout.Token);
        return artifacts;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
        public void Set(DateTimeOffset value) => _now = value;
    }

    private sealed class ScriptedProbe(Func<LiveRoom> factory) : ILiveRoomProbe
    {
        public Task<LiveRoom> ProbeAsync(long roomId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(factory());
        }
    }

    private sealed class FixedResolver : IStreamSourceResolver
    {
        public Task<StreamSource?> ResolveAsync(LiveRoom room, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<StreamSource?>(FixedStream());
        }
    }

    private sealed class SilentRecorder : ISegmentRecorder
    {
        public Task RunAsync(
            CaptureContext context,
            Func<SegmentArtifact, CancellationToken, Task> onSegment,
            CancellationToken token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
    }

    private sealed class EmptyDanmakuFeed : IDanmakuFeed
    {
        public async IAsyncEnumerable<DanmakuFrame> ReadAsync(
            CaptureContext context,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield break;
        }
    }
}
