namespace LiveClipper.Live;

/// <summary>
/// 一场直播会话的只读摘要。字段全部是安全可公开的基础类型，不含服务器绝对路径。
/// </summary>
public sealed record LiveSessionSummary(
    string Id,
    long RoomId,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int LastSequence,
    int RecoveredSegments,
    int Segments,
    long DanmakuCount,
    string? ResumedFrom,
    int RestartCount,
    double DurationSeconds,
    double SegmentDurationSeconds);

/// <summary>单个分片的只读视图；<see cref="File"/> 只有文件名，不暴露目录。</summary>
public sealed record LiveSegmentView(
    int Sequence,
    double OffsetSeconds,
    double DurationSeconds,
    long Bytes,
    string File,
    DateTimeOffset FinalizedAt);

/// <summary>
/// 一条弹幕的只读视图：会话内偏移（秒）、发送时间、发送者与内容；
/// 不含会话 id 之外的任何内部字段，也不暴露服务器路径。
/// </summary>
public sealed record LiveDanmakuView(
    string Id,
    double OffsetSeconds,
    DateTimeOffset SentAt,
    long Uid,
    string User,
    string Text,
    string Kind);

/// <summary>某场会话的详情：摘要 + 分片列表。</summary>
public sealed record LiveSessionDetail(
    LiveSessionSummary Session,
    IReadOnlyList<LiveSegmentView> Segments);

/// <summary>
/// 历史会话查询的纯映射逻辑，放在 Live 项目里方便离线单测；
/// HTTP 端点只做参数校验与结果包装。
/// </summary>
public static class LiveSessionQueries
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>按开始时间倒序列出会话；可选按房间过滤，支持 limit/offset 分页。</summary>
    public static async Task<(IReadOnlyList<LiveSessionSummary> Sessions, int Total)> ListSessionsAsync(
        ILiveSessionStore store,
        long? roomId = null,
        int? limit = null,
        int? offset = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var take = ClampLimit(limit);
        var skip = ClampOffset(offset);

        var all = await store.LoadAllSessionsAsync(token).ConfigureAwait(false);
        var filtered = all
            .Where(session => roomId is null || session.RoomId == roomId)
            .OrderByDescending(session => session.StartedAt)
            .ThenBy(session => session.Id, StringComparer.Ordinal)
            .ToArray();

        var page = new List<LiveSessionSummary>(Math.Min(take, filtered.Length));
        foreach (var session in filtered.Skip(skip).Take(take))
        {
            var segments = await store.LoadSegmentsAsync(session.Id, token).ConfigureAwait(false);
            var danmaku = await store.CountDanmakuAsync(session.Id, token).ConfigureAwait(false);
            page.Add(MapSession(session, segments, danmaku));
        }

        return (page, filtered.Length);
    }

    /// <summary>读取一场会话及其分片；会话不存在时返回 null。</summary>
    public static async Task<LiveSessionDetail?> GetSessionAsync(
        ILiveSessionStore store,
        string sessionId,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var session = await store.GetSessionAsync(sessionId, token).ConfigureAwait(false);
        if (session is null) return null;

        var segments = await store.LoadSegmentsAsync(sessionId, token).ConfigureAwait(false);
        var danmaku = await store.CountDanmakuAsync(sessionId, token).ConfigureAwait(false);
        return new LiveSessionDetail(
            MapSession(session, segments, danmaku),
            segments.OrderBy(segment => segment.Sequence).Select(MapSegment).ToArray());
    }

    public static LiveSessionSummary MapSession(
        LiveSession session,
        IReadOnlyList<SegmentArtifact> segments,
        long danmakuCount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(segments);

        var segmentDuration = segments.Sum(segment => segment.Duration.TotalSeconds);
        var end = session.EndedAt ?? session.StartedAt + TimeSpan.FromSeconds(segmentDuration);
        var duration = Math.Max(0, (end - session.StartedAt).TotalSeconds);
        return new LiveSessionSummary(
            session.Id,
            session.RoomId,
            session.State.ToString(),
            session.StartedAt,
            session.EndedAt,
            session.LastSequence,
            session.RecoveredSegments,
            segments.Count,
            danmakuCount,
            session.ResumedFrom,
            session.RestartCount,
            Math.Round(duration, 3),
            Math.Round(segmentDuration, 3));
    }

    public static LiveSegmentView MapSegment(SegmentArtifact segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new LiveSegmentView(
            segment.Sequence,
            Math.Round(segment.Offset.TotalSeconds, 3),
            Math.Round(segment.Duration.TotalSeconds, 3),
            segment.Bytes,
            Path.GetFileName(segment.Path),
            segment.FinalizedAt);
    }

    /// <summary>
    /// 读取一场会话的弹幕（按会话内偏移升序取最后 limit 条）；会话不存在时返回 null。
    /// 只暴露安全字段，不返回服务器绝对路径。
    /// </summary>
    public static async Task<IReadOnlyList<LiveDanmakuView>?> ListDanmakuAsync(
        ILiveSessionStore store,
        string sessionId,
        int? limit = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var session = await store.GetSessionAsync(sessionId, token).ConfigureAwait(false);
        if (session is null) return null;

        var frames = await store.LoadDanmakuAsync(sessionId, token).ConfigureAwait(false);
        var take = Math.Clamp(limit ?? DefaultDanmakuLimit, 1, MaxDanmakuLimit);
        return frames
            .OrderBy(frame => frame.Offset)
            .ThenBy(frame => frame.SentAt)
            .TakeLast(take)
            .Select(MapDanmaku)
            .ToArray();
    }

    public static LiveDanmakuView MapDanmaku(DanmakuFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new LiveDanmakuView(
            frame.Id,
            Math.Round(frame.Offset.TotalSeconds, 3),
            frame.SentAt,
            frame.Uid,
            frame.User,
            frame.Text,
            frame.Kind);
    }

    public const int DefaultDanmakuLimit = 200;
    public const int MaxDanmakuLimit = 2000;

    public static int ClampLimit(int? limit) =>
        Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    public static int ClampOffset(int? offset) => Math.Max(0, offset ?? 0);
}
