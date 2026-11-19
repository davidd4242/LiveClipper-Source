namespace LiveClipper.Live;

/// <summary>直播间的当前可见状态。Unknown 只用于首次探测失败。</summary>
public enum RoomAvailability { Unknown, Offline, Live }

/// <summary>
/// 一次逻辑录制会话。网络重连不会新建会话；进程重启后旧会话会被标记为
/// Interrupted，新会话通过 ResumedFrom 串起来。
/// </summary>
public enum LiveSessionState { Starting, Recording, Reconnecting, Finalizing, Ended, Interrupted, Failed }

public sealed record LiveRoom(
    long RoomId,
    RoomAvailability Availability,
    string Title = "",
    string Streamer = "",
    DateTimeOffset CheckedAt = default,
    long? CanonicalRoomId = null);

public sealed record StreamSource(
    Uri Uri,
    string Format = "hls",
    DateTimeOffset? ExpiresAt = null);

/// <summary>持久化的逻辑会话；LastSequence 让恢复后的分片序号保持单调。</summary>
public sealed record LiveSession(
    string Id,
    long RoomId,
    LiveSessionState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null,
    string? ResumedFrom = null,
    int RestartCount = 0,
    int LastSequence = 0,
    string? LastError = null,
    int RecoveredSegments = 0)
{
    public bool IsOpen => State is LiveSessionState.Starting
        or LiveSessionState.Recording
        or LiveSessionState.Reconnecting
        or LiveSessionState.Finalizing;
}

public sealed record SegmentArtifact(
    string SessionId,
    int Sequence,
    TimeSpan Offset,
    TimeSpan Duration,
    long Bytes,
    string Path,
    DateTimeOffset FinalizedAt);

public sealed record DanmakuFrame(
    string Id,
    string SessionId,
    TimeSpan Offset,
    DateTimeOffset SentAt,
    long Uid,
    string User,
    string Text,
    string Kind = "danmaku");

/// <summary>
/// 一次采集尝试的上下文。Clock 由 supervisor 在流解析完成后、启动录制器与弹幕前锚定，
/// 两路共享同一个零点；为 null 时回退旧行为（以 Session.StartedAt 为基准）。
/// </summary>
public sealed record CaptureContext(LiveSession Session, StreamSource Stream, CaptureClock? Clock = null);

/// <summary>给界面/状态端点用的只读快照，不包含可变对象。</summary>
public sealed record LiveStatus(
    long RoomId,
    RoomAvailability Availability,
    bool Running,
    bool Capturing,
    LiveRoom? Room,
    LiveSession? Session,
    string? LastError,
    int SegmentCount,
    long DanmakuCount,
    int RestartCount,
    int RecoveredSessions,
    DateTimeOffset UpdatedAt,
    int RecoveredSegments = 0,
    /// <summary>G7：本次采集因接收队列已满而丢弃的最旧帧数（无指标能力时为 0）。</summary>
    long DanmakuQueueDropped = 0,
    /// <summary>G7：弹幕接收队列容量（无指标能力时为 0）。</summary>
    int DanmakuQueueCapacity = 0,
    bool AutoRecord = false);

public interface ILiveRoomProbe
{
    Task<LiveRoom> ProbeAsync(long roomId, CancellationToken token);
}

public interface IStreamSourceResolver
{
    Task<StreamSource?> ResolveAsync(LiveRoom room, CancellationToken token);
}

public interface ISegmentRecorder
{
    /// <summary>
    /// 持续录制直到取消。每完成一个可安全读取的分片就调用一次 onSegment；
    /// 不允许在 onSegment 之外偷偷保存路径，否则恢复时会丢元数据。
    /// </summary>
    Task RunAsync(
        CaptureContext context,
        Func<SegmentArtifact, CancellationToken, Task> onSegment,
        CancellationToken token);
}

/// <summary>
/// 可选能力：进程重启后，从媒体目录扫出“已落盘但没有索引”的分片，交给 supervisor 补录。
/// 与 <see cref="ISegmentRecorder"/> 分开，记录器不必强制实现。
/// </summary>
public interface ISegmentRecovery
{
    /// <param name="indexedPaths">已经写入分片索引的路径，恢复时必须跳过。</param>
    /// <param name="afterSequence">已索引分片的最大序号，恢复分片从它之后续接。</param>
    /// <param name="startOffset">已索引分片的结束时间，恢复分片从它之后续接。</param>
    Task<IReadOnlyList<SegmentArtifact>> RecoverOrphansAsync(
        string sessionId,
        IReadOnlySet<string> indexedPaths,
        int afterSequence,
        TimeSpan startOffset,
        CancellationToken token);
}

public interface IDanmakuFeed
{
    /// <summary>持续读取直到取消；读到的帧由 supervisor 批量落库。</summary>
    IAsyncEnumerable<DanmakuFrame> ReadAsync(CaptureContext context, CancellationToken token);
}

/// <summary>
/// 可选能力（G7）：弹幕接收队列的容量与“因满丢弃”计数。supervisor/诊断端点据此
/// 暴露丢帧事实，而不是让 DropOldest 完全静默。未实现该接口的弹幕源按 0 处理。
/// </summary>
public interface IDanmakuFeedMetrics
{
    int QueueCapacity { get; }
    long QueueDroppedCount { get; }
}

public interface ILiveSessionStore
{
    Task<IReadOnlyList<LiveSession>> LoadOpenSessionsAsync(CancellationToken token);
    Task<IReadOnlyList<LiveSession>> LoadAllSessionsAsync(CancellationToken token);
    Task<LiveSession?> GetSessionAsync(string id, CancellationToken token);
    /// <summary>读回某场会话已落库的分片索引；坏行跳过，不阻塞恢复。</summary>
    Task<IReadOnlyList<SegmentArtifact>> LoadSegmentsAsync(string sessionId, CancellationToken token);
    Task SaveSessionAsync(LiveSession session, CancellationToken token);
    Task AppendSegmentAsync(SegmentArtifact segment, CancellationToken token);
    Task AppendDanmakuAsync(IReadOnlyList<DanmakuFrame> frames, CancellationToken token);
    /// <summary>删除一场已结束会话的清单、分片索引和弹幕；不会删除媒体文件。</summary>
    Task DeleteSessionAsync(string id, CancellationToken token);

    /// <summary>统计某场会话已落库的弹幕行数。默认实现返回 0，存储实现可覆盖。</summary>
    Task<long> CountDanmakuAsync(string sessionId, CancellationToken token) => Task.FromResult(0L);

    /// <summary>读回某场会话已落库的弹幕；坏行跳过，默认实现返回空，存储实现可覆盖。</summary>
    Task<IReadOnlyList<DanmakuFrame>> LoadDanmakuAsync(string sessionId, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<DanmakuFrame>>(Array.Empty<DanmakuFrame>());
}

public sealed record LiveSupervisorOptions
{
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int DanmakuBatchSize { get; init; } = 32;
    public TimeSpan DanmakuFlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int DanmakuQueueCapacity { get; init; } = 4096;
}
