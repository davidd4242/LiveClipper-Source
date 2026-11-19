using System.Runtime.CompilerServices;
using LiveClipper.Live;

namespace LiveClipper.Tests;

/// <summary>
/// 直播底座恢复链路测试：崩溃后“分片文件存在但索引没有”必须能被补录，
/// 且重复恢复幂等；已结束会话不重新开账；LastSequence 跨会话单调。
/// </summary>
public class LiveRecoveryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LoadSegmentsAsync_RoundTripsOrderedAndSkipsCorruptLines()
    {
        var root = NewTempRoot("store");
        try
        {
            using var store = new JsonLiveSessionStore(root);
            await store.SaveSessionAsync(NewSession("s1", LiveSessionState.Recording), CancellationToken.None);
            await store.AppendSegmentAsync(Segment("s1", 2, 4, 8), CancellationToken.None);
            await store.AppendSegmentAsync(Segment("s1", 1, 0, 4), CancellationToken.None);

            // 模拟崩溃时写了一半的行：恢复必须跳过它而不是整场失败。
            await File.AppendAllTextAsync(Path.Combine(root, "s1.segments.jsonl"), "{\"SessionId\":\"s1\",\"Sequence\":3\n");

            var loaded = await store.LoadSegmentsAsync("s1", CancellationToken.None);

            Assert.Equal(new[] { 1, 2 }, loaded.Select(item => item.Sequence).ToArray());
            Assert.Equal(TimeSpan.Zero, loaded[0].Offset);
            Assert.Equal(TimeSpan.FromSeconds(4), loaded[1].Offset);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task LoadSegmentsAsync_MissingFileReturnsEmpty()
    {
        var root = NewTempRoot("store-missing");
        try
        {
            using var store = new JsonLiveSessionStore(root);
            Assert.Empty(await store.LoadSegmentsAsync("no-such-session", CancellationToken.None));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RecoverOrphansAsync_FindsOnlyUnindexedSegmentsInOrder()
    {
        var root = NewTempRoot("media");
        try
        {
            var sessionDir = Path.Combine(root, "buffer", "s1");
            var run1 = Path.Combine(sessionDir, "run_1700000000000");
            var run2 = Path.Combine(sessionDir, "run_1700000001000");
            Directory.CreateDirectory(run1);
            Directory.CreateDirectory(run2);
            var indexed = WriteSegment(run1, "segment_00000.ts", 100);
            WriteSegment(run2, "segment_00000.ts", 200);
            WriteSegment(run2, "segment_00001.ts", 300);
            File.WriteAllBytes(Path.Combine(run2, "segment_00002.ts"), Array.Empty<byte>()); // 空文件必须忽略

            var recorder = NewRecorder(root, _ => 4.0);

            var recovered = await recorder.RecoverOrphansAsync(
                "s1",
                new HashSet<string> { indexed },
                afterSequence: 1,
                startOffset: TimeSpan.FromSeconds(4),
                CancellationToken.None);

            Assert.Equal(new[] { 2, 3 }, recovered.Select(item => item.Sequence).ToArray());
            Assert.Equal(new[] { 4.0, 8.0 }, recovered.Select(item => item.Offset.TotalSeconds).ToArray());
            Assert.All(recovered, item => Assert.Equal(4.0, item.Duration.TotalSeconds));
            Assert.Equal(new long[] { 200, 300 }, recovered.Select(item => item.Bytes).ToArray());
            Assert.DoesNotContain(recovered, item => item.Path == indexed);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RecoverOrphansAsync_MissingSessionDirectoryReturnsEmpty()
    {
        var root = NewTempRoot("media-empty");
        try
        {
            var recorder = NewRecorder(root, _ => 4.0);
            var recovered = await recorder.RecoverOrphansAsync(
                "no-such-session",
                new HashSet<string>(),
                0,
                TimeSpan.Zero,
                CancellationToken.None);
            Assert.Empty(recovered);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RecoverOrphansAsync_ProbeFailureFallsBackToSegmentSeconds()
    {
        var root = NewTempRoot("media-probe-fail");
        try
        {
            var sessionDir = Path.Combine(root, "buffer", "s1", "run_1700000000000");
            Directory.CreateDirectory(sessionDir);
            WriteSegment(sessionDir, "segment_00000.ts", 10);

            var recorder = NewRecorder(root, _ => throw new InvalidOperationException("ffprobe 不可用"));

            var recovered = await recorder.RecoverOrphansAsync(
                "s1",
                new HashSet<string>(),
                0,
                TimeSpan.Zero,
                CancellationToken.None);

            var artifact = Assert.Single(recovered);
            Assert.Equal(4.0, artifact.Duration.TotalSeconds); // SegmentSeconds 默认 4
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Supervisor_Recovery_ReconcilesOrphansAndPatchesLastSequence()
    {
        var storeRoot = NewTempRoot("store-recover");
        var mediaRoot = NewTempRoot("media-recover");
        try
        {
            using var store = new JsonLiveSessionStore(storeRoot);
            var old = new LiveSession(
                "old-session",
                1004,
                LiveSessionState.Recording,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                LastSequence: 1);
            await store.SaveSessionAsync(old, CancellationToken.None);

            var run1 = Path.Combine(mediaRoot, "buffer", "old-session", "run_1700000000000");
            var run2 = Path.Combine(mediaRoot, "buffer", "old-session", "run_1700000001000");
            Directory.CreateDirectory(run1);
            Directory.CreateDirectory(run2);
            var indexedPath = WriteSegment(run1, "segment_00000.ts", 4096);
            WriteSegment(run2, "segment_00000.ts", 4096);
            WriteSegment(run2, "segment_00001.ts", 8192);
            await store.AppendSegmentAsync(
                new SegmentArtifact("old-session", 1, TimeSpan.Zero, TimeSpan.FromSeconds(4), 4096, indexedPath, DateTimeOffset.UtcNow),
                CancellationToken.None);

            var recorder = NewRecorder(mediaRoot, _ => 4.0);
            await using var supervisor = new LiveSessionSupervisor(
                1004,
                new ScriptedProbe(_ => OfflineRoom(1004)),
                new FixedResolver(),
                recorder,
                new EmptyDanmakuFeed(),
                store,
                FastOptions());

            await supervisor.StartAsync(autoRecord:true);
            await WaitUntilAsync(
                () => supervisor.Snapshot.RecoveredSessions == 1 && supervisor.Snapshot.RecoveredSegments == 2,
                "恢复应补录 2 个未索引分片");
            await supervisor.StopAsync();

            var recovered = await store.LoadSegmentsAsync("old-session", CancellationToken.None);
            Assert.Equal(new[] { 1, 2, 3 }, recovered.Select(item => item.Sequence).ToArray());
            Assert.Equal(new[] { 0.0, 4.0, 8.0 }, recovered.Select(item => item.Offset.TotalSeconds).ToArray());
            Assert.Equal(new long[] { 4096, 4096, 8192 }, recovered.Select(item => item.Bytes).ToArray());

            var patched = await store.GetSessionAsync("old-session", CancellationToken.None);
            Assert.NotNull(patched);
            Assert.Equal(LiveSessionState.Interrupted, patched!.State);
            Assert.Equal(3, patched.LastSequence);
            Assert.Contains("服务重启", patched.LastError);

            // 第二次恢复必须幂等：会话已不是 open，分片索引也不再增长。
            await using var second = new LiveSessionSupervisor(
                1004,
                new ScriptedProbe(_ => OfflineRoom(1004)),
                new FixedResolver(),
                recorder,
                new EmptyDanmakuFeed(),
                store,
                FastOptions());
            await second.StartAsync(autoRecord:true);
            await Task.Delay(200);
            Assert.Equal(0, second.Snapshot.RecoveredSessions);
            await second.StopAsync();
            Assert.Equal(3, (await store.LoadSegmentsAsync("old-session", CancellationToken.None)).Count);
        }
        finally
        {
            TryDelete(storeRoot);
            TryDelete(mediaRoot);
        }
    }

    [Fact]
    public async Task Supervisor_Recovery_IgnoresEndedSessions()
    {
        var storeRoot = NewTempRoot("store-ended");
        var mediaRoot = NewTempRoot("media-ended");
        try
        {
            using var store = new JsonLiveSessionStore(storeRoot);
            var ended = new LiveSession(
                "ended-session",
                1005,
                LiveSessionState.Ended,
                DateTimeOffset.UtcNow.AddHours(-2),
                EndedAt: DateTimeOffset.UtcNow.AddHours(-1),
                LastSequence: 2);
            await store.SaveSessionAsync(ended, CancellationToken.None);
            var run = Path.Combine(mediaRoot, "buffer", "ended-session", "run_1700000000000");
            Directory.CreateDirectory(run);
            WriteSegment(run, "segment_00000.ts", 2048);
            await store.AppendSegmentAsync(
                new SegmentArtifact("ended-session", 1, TimeSpan.Zero, TimeSpan.FromSeconds(4), 1024, Path.Combine(run, "segment_00000.ts"), DateTimeOffset.UtcNow),
                CancellationToken.None);

            var recorder = NewRecorder(mediaRoot, _ => 4.0);
            await using var supervisor = new LiveSessionSupervisor(
                1005,
                new ScriptedProbe(_ => OfflineRoom(1005)),
                new FixedResolver(),
                recorder,
                new EmptyDanmakuFeed(),
                store,
                FastOptions());

            await supervisor.StartAsync(autoRecord:true);
            await Task.Delay(200);
            await supervisor.StopAsync();

            Assert.Equal(0, supervisor.Snapshot.RecoveredSessions);
            Assert.Equal(0, supervisor.Snapshot.RecoveredSegments);
            Assert.Single(await store.LoadSegmentsAsync("ended-session", CancellationToken.None));
            Assert.Equal(LiveSessionState.Ended, (await store.GetSessionAsync("ended-session", CancellationToken.None))!.State);
        }
        finally
        {
            TryDelete(storeRoot);
            TryDelete(mediaRoot);
        }
    }

    [Fact]
    public async Task Supervisor_NewSessionInheritsLastSequenceFromInterruptedSession()
    {
        var storeRoot = NewTempRoot("store-inherit");
        try
        {
            using var store = new JsonLiveSessionStore(storeRoot);
            var old = new LiveSession(
                "old-session",
                1006,
                LiveSessionState.Recording,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                LastSequence: 7);
            await store.SaveSessionAsync(old, CancellationToken.None);

            var probe = new ScriptedProbe(_ => LiveRoom(1006));
            await using var supervisor = new LiveSessionSupervisor(
                1006,
                probe,
                new FixedResolver(),
                new SilentRecorder(),
                new EmptyDanmakuFeed(),
                store,
                FastOptions());

            await supervisor.StartAsync(autoRecord:true);
            string? newSessionId = null;
            await WaitUntilAsync(
                () =>
                {
                    var session = supervisor.Snapshot.Session;
                    if (session is null || session.ResumedFrom != "old-session") return false;
                    newSessionId = session.Id;
                    return session.LastSequence == 7;
                },
                "新会话应继承旧会话的 LastSequence");
            await supervisor.StopAsync();

            var persisted = await store.GetSessionAsync(newSessionId!, CancellationToken.None);
            Assert.NotNull(persisted);
            Assert.Equal(7, persisted!.LastSequence);
            Assert.Equal("old-session", persisted.ResumedFrom);
        }
        finally
        {
            TryDelete(storeRoot);
        }
    }

    private static FfmpegSegmentRecorder NewRecorder(
        string storageRoot,
        Func<string, double> durationProbe)
    {
        // 构造器只校验“配置的可执行文件存在”，测试注入探针后不会真正启动进程。
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return new FfmpegSegmentRecorder(new FfmpegRecorderOptions
        {
            FfmpegPath = executable,
            FfprobePath = executable,
            StorageRoot = storageRoot,
            SegmentSeconds = 4,
            DurationProbe = (path, _) => Task.FromResult(durationProbe(path))
        });
    }

    private static string WriteSegment(string directory, string name, int bytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static LiveSession NewSession(string id, LiveSessionState state) =>
        new(id, 1000, state, DateTimeOffset.UtcNow.AddMinutes(-1));

    private static SegmentArtifact Segment(string sessionId, int sequence, double offsetSeconds, double durationSeconds) =>
        new(
            sessionId,
            sequence,
            TimeSpan.FromSeconds(offsetSeconds),
            TimeSpan.FromSeconds(durationSeconds),
            1024,
            Path.Combine(Path.GetTempPath(), $"{sessionId}-{sequence}.ts"),
            DateTimeOffset.UtcNow);

    private static LiveRoom LiveRoom(long roomId = 1001) =>
        new(roomId, RoomAvailability.Live, "测试直播间", "测试主播", DateTimeOffset.UtcNow);

    private static LiveRoom OfflineRoom(long roomId) =>
        new(roomId, RoomAvailability.Offline, "测试直播间", "测试主播", DateTimeOffset.UtcNow);

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

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string what)
    {
        var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("等待超时：" + what);
    }

    private sealed class ScriptedProbe(Func<int, LiveRoom> factory) : ILiveRoomProbe
    {
        private int _count;

        public Task<LiveRoom> ProbeAsync(long roomId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var count = Interlocked.Increment(ref _count);
            return Task.FromResult(factory(count));
        }
    }

    private sealed class FixedResolver : IStreamSourceResolver
    {
        public Task<StreamSource?> ResolveAsync(LiveRoom room, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<StreamSource?>(
                new StreamSource(new Uri("http://127.0.0.1:9/fake-live.m3u8"), "flv"));
        }
    }

    private sealed class SilentRecorder : ISegmentRecorder
    {
        public async Task RunAsync(
            CaptureContext context,
            Func<SegmentArtifact, CancellationToken, Task> onSegment,
            CancellationToken token)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        }
    }

    private sealed class EmptyDanmakuFeed : IDanmakuFeed
    {
        public async IAsyncEnumerable<DanmakuFrame> ReadAsync(
            CaptureContext context,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            yield break;
        }
    }
}
