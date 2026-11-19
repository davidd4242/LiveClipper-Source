using LiveClipper.Live;

namespace LiveClipper.Tests;

/// <summary>
/// 直播弹幕只读查询：jsonl 读回（跳过半行）、按会话内偏移排序、尾部 limit 截取、
/// 未知会话返回 null。对应 /api/live/sessions/{id}/danmaku。
/// </summary>
public sealed class LiveDanmakuQueryTests : IDisposable
{
    private readonly string _root;

    public LiveDanmakuQueryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lc-live-danmaku-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言。
        }
    }

    private JsonLiveSessionStore Store() => new(_root);

    private static LiveSession Session(string id = "danmaku-session") => new(
        id,
        545068,
        LiveSessionState.Ended,
        new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero),
        EndedAt: new DateTimeOffset(2026, 10, 26, 2, 0, 18, TimeSpan.Zero),
        LastSequence: 3);

    private static DanmakuFrame Frame(string sessionId, int index, TimeSpan offset, string text) => new(
        $"dm-{index}",
        sessionId,
        offset,
        new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero) + offset,
        Uid: 100000 + index,
        User: $"观众{index}",
        Text: text);

    [Fact]
    public async Task LoadDanmakuAsync_SortsByOffsetAndSkipsCorruptLines()
    {
        var store = Store();
        var session = Session();
        await store.SaveSessionAsync(session, CancellationToken.None);
        await store.AppendDanmakuAsync(
            new[]
            {
                Frame(session.Id, 2, TimeSpan.FromSeconds(5), "第五条"),
                Frame(session.Id, 0, TimeSpan.FromSeconds(1), "第一条"),
                Frame(session.Id, 1, TimeSpan.FromSeconds(3), "第三条"),
            },
            CancellationToken.None);
        // 模拟崩溃留下的半行：读回时必须跳过，不能抛异常。
        await File.AppendAllTextAsync(
            store.Root + Path.DirectorySeparatorChar + session.Id + ".danmaku.jsonl",
            "{ not-json",
            CancellationToken.None);

        var frames = await store.LoadDanmakuAsync(session.Id, CancellationToken.None);

        Assert.Equal(new[] { "第一条", "第三条", "第五条" }, frames.Select(frame => frame.Text));
        // 行数统计是原始行数（含坏行 4 行）；语义读回只返回 3 条有效弹幕。
        Assert.Equal(4, await store.CountDanmakuAsync(session.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ListDanmakuAsync_MapsSafeFieldsAndTakesTailLimit()
    {
        var store = Store();
        var session = Session();
        await store.SaveSessionAsync(session, CancellationToken.None);
        var frames = Enumerable.Range(0, 5)
            .Select(index => Frame(session.Id, index, TimeSpan.FromSeconds(index), $"弹幕{index}"))
            .ToArray();
        await store.AppendDanmakuAsync(frames, CancellationToken.None);

        var events = await LiveSessionQueries.ListDanmakuAsync(store, session.Id, limit: 2, CancellationToken.None);

        Assert.NotNull(events);
        Assert.Equal(2, events!.Count);
        Assert.Equal(new[] { 3d, 4d }, events.Select(item => item.OffsetSeconds));
        Assert.Equal("弹幕4", events[1].Text);
        Assert.Equal("观众4", events[1].User);
        Assert.Equal(100004, events[1].Uid);
        Assert.Equal("danmaku", events[1].Kind);
        Assert.Equal(frames[4].SentAt, events[1].SentAt);
    }

    [Fact]
    public async Task ListDanmakuAsync_UnknownSessionReturnsNull()
    {
        var store = Store();
        var events = await LiveSessionQueries.ListDanmakuAsync(store, "missing-session", null, CancellationToken.None);
        Assert.Null(events);
    }

    [Fact]
    public async Task ListDanmakuAsync_EmptySessionReturnsEmptyArray()
    {
        var store = Store();
        var session = Session("empty-danmaku");
        await store.SaveSessionAsync(session, CancellationToken.None);

        var events = await LiveSessionQueries.ListDanmakuAsync(store, session.Id, null, CancellationToken.None);

        Assert.NotNull(events);
        Assert.Empty(events!);
    }
}
