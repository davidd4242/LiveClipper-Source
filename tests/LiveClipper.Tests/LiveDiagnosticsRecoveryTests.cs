using System.Text.Json;
using LiveClipper.Live;

namespace LiveClipper.Tests;

/// <summary>
/// task-12：G4 诊断端点（LiveDiagnosticsService）与 G6 恢复退避（LiveRoomRecoveryService）。
/// 全部离线：临时目录 + 手动 TimeProvider，不访问网络、不启动 FFmpeg。
/// </summary>
public sealed class LiveDiagnosticsRecoveryTests
{
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lc-diag-" + Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private static readonly DateTimeOffset Anchor = new(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);

    private static LiveSession NewSession(string id, long roomId = 100) =>
        new(id, roomId, LiveSessionState.Recording, Anchor);

    private static async Task<JsonLiveRoomRegistry> NewRegistryAsync(TempRoot root, params long[] activeRoomIds)
    {
        var registry = new JsonLiveRoomRegistry(System.IO.Path.Combine(root.Path, "rooms.json"));
        foreach (var roomId in activeRoomIds) await registry.SetActiveAsync(roomId, true, CancellationToken.None);
        return registry;
    }

    // ---------- G6：退避 ----------

    [Fact]
    public async Task RecoveryFailureIncreasesDelay_AndPersistsToRegistry()
    {
        using var root = new TempRoot();
        var registry = await NewRegistryAsync(root, 100);
        var time = new ManualTimeProvider(Anchor);
        var service = new LiveRoomRecoveryService(
            registry,
            new LiveRecoveryOptions { InitialDelay = TimeSpan.FromSeconds(5), MaxDelay = TimeSpan.FromSeconds(60) },
            time);

        Task Start(long id, CancellationToken token) => throw new InvalidOperationException("boom");

        var first = await service.RunOnceAsync(Start, CancellationToken.None);
        Assert.Equal(1, first.Attempted);
        Assert.Equal(1, first.Failed);
        Assert.Equal(0, first.Succeeded);

        // 1) 未到期的房间不重复尝试。
        time.Advance(TimeSpan.FromSeconds(4));
        var skipped = await service.RunOnceAsync(Start, CancellationToken.None);
        Assert.Equal(1, skipped.Skipped);
        Assert.Equal(0, skipped.Attempted);

        // 2) 到期后失败次数累加，延迟翻倍到 10s。
        time.Advance(TimeSpan.FromSeconds(1));
        var second = await service.RunOnceAsync(Start, CancellationToken.None);
        Assert.Equal(1, second.Failed);
        var state = Assert.Single(service.Snapshot());
        Assert.Equal(2, state.Failures);
        Assert.Equal(Anchor.AddSeconds(15), state.NextAttemptAt);

        // 3) 状态已写回登记簿，进程重启后仍可诊断（G4 可读）。
        var persisted = Assert.Single(await registry.ListAsync(CancellationToken.None));
        Assert.Equal(2, persisted.RecoveryFailures);
        Assert.Contains("boom", persisted.LastRecoveryError ?? "");
        Assert.Equal(Anchor.AddSeconds(15), persisted.NextRecoveryAt);

        // 4) 恢复成功后清零，不再排下一次重试。
        time.Advance(TimeSpan.FromSeconds(10));
        var ok = await service.RunOnceAsync((_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, ok.Succeeded);
        var recovered = Assert.Single(service.Snapshot());
        Assert.True(recovered.Succeeded);
        Assert.Equal(0, recovered.Failures);
        Assert.Null(recovered.NextAttemptAt);

        var persistedOk = Assert.Single(await registry.ListAsync(CancellationToken.None));
        Assert.Equal(0, persistedOk.RecoveryFailures);
        Assert.Null(persistedOk.LastRecoveryError);
    }

    [Fact]
    public async Task RecoveryIsolatesRooms_AndCapsDelay()
    {
        using var root = new TempRoot();
        var registry = await NewRegistryAsync(root, 200, 201);
        var time = new ManualTimeProvider(Anchor);
        var service = new LiveRoomRecoveryService(
            registry,
            new LiveRecoveryOptions { InitialDelay = TimeSpan.FromSeconds(5), MaxDelay = TimeSpan.FromSeconds(12) },
            time);

        Task Start(long roomId, CancellationToken token) =>
            roomId == 200 ? throw new InvalidOperationException("room-200 down") : Task.CompletedTask;

        var first = await service.RunOnceAsync(Start, CancellationToken.None);
        Assert.Equal(2, first.Attempted);
        Assert.Equal(1, first.Failed);
        Assert.Equal(1, first.Succeeded);
        Assert.Equal(2, service.Snapshot().Count);

        // 失败房间按 5→10→12（封顶）递增；另一房间成功不受影响。
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 3; i++)
        {
            var state = Assert.Single(service.Snapshot(), item => item.RoomId == 200);
            Assert.NotNull(state.NextAttemptAt);
            delays.Add(state.NextAttemptAt!.Value - time.GetUtcNow());
            time.Advance(delays[^1]);
            await service.RunOnceAsync(Start, CancellationToken.None);
        }

        Assert.Equal(TimeSpan.FromSeconds(5), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(12), delays[2]);
        var healthy = Assert.Single(service.Snapshot(), item => item.RoomId == 201);
        Assert.True(healthy.Succeeded);
    }

    [Fact]
    public async Task RecoveryResetClearsBackoff_AndHistoryIsCapped()
    {
        using var root = new TempRoot();
        var registry = await NewRegistryAsync(root, 300);
        var time = new ManualTimeProvider(Anchor);
        var service = new LiveRoomRecoveryService(
            registry,
            new LiveRecoveryOptions { InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(2) },
            time);

        // 失败一次后 ResetAsync：内存与登记簿的退避都被清掉，下一次立即尝试。
        await service.RunOnceAsync((_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);
        Assert.Single(service.Snapshot());
        await service.ResetAsync(300, CancellationToken.None);
        Assert.Empty(service.Snapshot());
        var persistedReset = Assert.Single(await registry.ListAsync(CancellationToken.None));
        Assert.Equal(0, persistedReset.RecoveryFailures);
        Assert.Null(persistedReset.NextRecoveryAt);
        var retried = await service.RunOnceAsync((_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, retried.Attempted);
        Assert.Equal(1, retried.Succeeded);

        // 成功房间每轮都会尝试；历史只保留最近 MaxHistory 条。
        for (var i = 0; i < LiveRoomRecoveryService.MaxHistory + 5; i++)
            await service.RunOnceAsync((_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(LiveRoomRecoveryService.MaxHistory, service.History.Count);
    }

    [Fact]
    public async Task RecoveryRespectsMaxRooms_AndBacksOffOverLimitRoom()
    {
        using var root = new TempRoot();
        var registry = await NewRegistryAsync(root, 500, 501);
        var time = new ManualTimeProvider(Anchor);
        var service = new LiveRoomRecoveryService(
            registry,
            new LiveRecoveryOptions { InitialDelay = TimeSpan.FromSeconds(5), MaxDelay = TimeSpan.FromSeconds(60) },
            time);

        var active = new HashSet<long>();
        const int maxRooms = 1;
        Task Start(long roomId, CancellationToken token)
        {
            if (active.Count >= maxRooms)
                throw new InvalidOperationException($"最多同时监控 {maxRooms} 个直播间");
            active.Add(roomId);
            return Task.CompletedTask;
        }

        var run = await service.RunOnceAsync(Start, CancellationToken.None);

        Assert.Equal(2, run.Attempted);
        Assert.Equal(1, run.Succeeded);
        Assert.Equal(1, run.Failed);
        Assert.Single(active);
        Assert.Equal(500, active.Single()); // 登记簿按 RoomId 升序尝试，500 先占位

        var overLimit = Assert.Single(service.Snapshot(), item => item.RoomId == 501);
        Assert.False(overLimit.Succeeded);
        Assert.Equal(1, overLimit.Failures);
        Assert.Contains("最多同时监控", overLimit.LastError ?? "");
        Assert.Equal(Anchor.AddSeconds(5), overLimit.NextAttemptAt);

        var persisted = (await registry.ListAsync(CancellationToken.None)).Single(item => item.RoomId == 501);
        Assert.Equal(1, persisted.RecoveryFailures);
        Assert.Equal(Anchor.AddSeconds(5), persisted.NextRecoveryAt);
    }

    // ---------- G4：诊断 ----------

    [Fact]
    public async Task DiagnosticsQuarantinesCorruptSessionManifest()
    {
        using var root = new TempRoot();
        var store = new JsonLiveSessionStore(root.Path);
        await store.SaveSessionAsync(NewSession("sess-good"), CancellationToken.None);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(root.Path, "sess-bad.session.json"),
            "{ not json",
            CancellationToken.None);

        var service = new LiveDiagnosticsService(store);
        var before = await service.InspectAsync(CancellationToken.None);

        Assert.Equal(2, before.Store.SessionFiles);
        Assert.Equal(1, before.Store.CorruptFiles);
        Assert.Equal(1, before.Store.OpenSessions);
        var issue = Assert.Single(before.Store.Issues);
        Assert.Equal("corrupt-session", issue.Kind);
        Assert.Contains("sess-bad", issue.Path);
        Assert.False(System.IO.Path.IsPathRooted(issue.Path));
        Assert.Equal((long)"{ not json".Length, issue.SizeBytes);
        Assert.Equal(".", before.Root);
        Assert.Equal(0, before.Store.QuarantinedFiles);

        // 诊断本身只读；真正读取会话的路径才会把坏清单移入隔离区。
        await store.LoadAllSessionsAsync(CancellationToken.None);
        Assert.True(Directory.Exists(store.QuarantineDirectory));
        Assert.Single(Directory.GetFiles(store.QuarantineDirectory));

        var after = await service.InspectAsync(CancellationToken.None);
        Assert.Equal(1, after.Store.SessionFiles);
        Assert.Equal(0, after.Store.CorruptFiles);
        Assert.Equal(1, after.Store.QuarantinedFiles);
        Assert.Equal(1, after.Store.OpenSessions);
    }

    [Fact]
    public async Task DiagnosticsReportsCorruptSegmentLines_ButLoadSkipsThem()
    {
        using var root = new TempRoot();
        var store = new JsonLiveSessionStore(root.Path);
        await store.SaveSessionAsync(NewSession("sess-seg"), CancellationToken.None);
        await store.AppendSegmentAsync(
            new SegmentArtifact("sess-seg", 1, TimeSpan.Zero, TimeSpan.FromSeconds(4), 4096, "/media/a.ts", Anchor),
            CancellationToken.None);
        await store.AppendSegmentAsync(
            new SegmentArtifact("sess-seg", 2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), 4096, "/media/b.ts", Anchor),
            CancellationToken.None);
        await File.AppendAllTextAsync(
            System.IO.Path.Combine(root.Path, "sess-seg.segments.jsonl"),
            "this is not json\n",
            CancellationToken.None);

        var report = await new LiveDiagnosticsService(store).InspectAsync(CancellationToken.None);
        var issue = Assert.Single(report.Store.Issues);
        Assert.Equal("corrupt-segment-line", issue.Kind);
        Assert.Equal("sess-seg.segments.jsonl", issue.Path);
        Assert.Equal(3, issue.Line);
        Assert.NotNull(issue.SizeBytes);

        var segments = await store.LoadSegmentsAsync("sess-seg", CancellationToken.None);
        Assert.Equal(2, segments.Count);
        Assert.Equal(new[] { 1, 2 }, segments.Select(item => item.Sequence).ToArray());
    }

    [Fact]
    public async Task DiagnosticsCountsOrphanMediaDirectories()
    {
        using var root = new TempRoot();
        var store = new JsonLiveSessionStore(root.Path);
        await store.SaveSessionAsync(NewSession("sess-known"), CancellationToken.None);

        var bufferRoot = System.IO.Path.Combine(root.Path, "media", "buffer");
        Directory.CreateDirectory(System.IO.Path.Combine(bufferRoot, "sess-known", "run_1"));
        Directory.CreateDirectory(System.IO.Path.Combine(bufferRoot, "sess-orphan", "run_9"));
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(bufferRoot, "sess-orphan", "run_9", "segment_00000.ts"),
            new byte[1024],
            CancellationToken.None);

        var janitor = new LiveStorageJanitor(store, new LiveStorageOptions { BufferRoot = bufferRoot });
        var report = await new LiveDiagnosticsService(store, storage: janitor).InspectAsync(CancellationToken.None);

        Assert.NotNull(report.Storage);
        var orphan = Assert.Single(report.OrphanMediaDirectories);
        Assert.Contains("sess-orphan", orphan.RelativePath);
        Assert.False(System.IO.Path.IsPathRooted(orphan.RelativePath));
        Assert.Equal(1, orphan.Files);
        Assert.Equal(1024L, orphan.Bytes);
        Assert.Contains(report.Warnings, warning => warning.Contains("没有对应会话清单"));
    }

    [Fact]
    public async Task DiagnosticsSurfacesRegistryCorruptionWithBackup()
    {
        using var root = new TempRoot();
        var registryPath = System.IO.Path.Combine(root.Path, "rooms.json");
        await File.WriteAllTextAsync(registryPath, "{ broken", CancellationToken.None);

        var registry = new JsonLiveRoomRegistry(registryPath);
        var roomList = await registry.ListAsync(CancellationToken.None);
        Assert.Empty(roomList);
        Assert.True(registry.Loaded);
        Assert.NotNull(registry.LastError);
        var backups = Directory.GetFiles(root.Path, "rooms.json.corrupt-*");
        Assert.Single(backups);
        Assert.Contains(".corrupt-", backups[0]);

        var diagnostics = new LiveDiagnosticsService(new JsonLiveSessionStore(root.Path), registry);
        var report = await diagnostics.InspectAsync(CancellationToken.None);
        Assert.NotNull(report.Registry.LastError);
        Assert.Single(report.Registry.CorruptBackups);
        Assert.Equal("rooms.json", report.Registry.Path);
        Assert.False(System.IO.Path.IsPathRooted(report.Registry.Path));
        Assert.Contains(report.Warnings, warning => warning.Contains("房间登记簿损坏"));
    }

    [Fact]
    public async Task DiagnosticsIncludesRecoveryStateAndWarnings()
    {
        using var root = new TempRoot();
        var registry = await NewRegistryAsync(root, 400);
        var time = new ManualTimeProvider(Anchor);
        var recovery = new LiveRoomRecoveryService(registry, null, time);
        await recovery.RunOnceAsync((_, _) => throw new InvalidOperationException("room-400 down"), CancellationToken.None);

        var store = new JsonLiveSessionStore(root.Path);
        var diagnostics = new LiveDiagnosticsService(
            store,
            registry: registry,
            recovery: recovery);
        var report = await diagnostics.InspectAsync(CancellationToken.None);

        var state = Assert.Single(report.Recovery);
        Assert.Equal(400, state.RoomId);
        Assert.Contains("room-400 down", state.LastError ?? "");
        Assert.Contains(report.Warnings, warning => warning.Contains("恢复失败"));
        Assert.True(report.CheckedAt > DateTimeOffset.MinValue);

        // 快照 JSON 必须可序列化（诊断端点直接返回它）。
        var json = JsonSerializer.Serialize(report);
        Assert.Contains("RoomId", json);
        Assert.Contains("CheckedAt", json);
        // 安全字段要求：响应里不能出现本机绝对路径。
        Assert.DoesNotContain(root.Path, json);
    }
}
