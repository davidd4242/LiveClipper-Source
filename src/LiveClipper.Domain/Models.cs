namespace LiveClipper.Domain;

public enum RecordingState { Offline, Connecting, Recording, Reconnecting, Finalizing, Ended }
public enum HighlightState { Collecting, Materializing, Ready, Failed }

public sealed record RoomInfo(long RoomId, long CanonicalId, bool IsLive, string Title, string Streamer);
public sealed record ResolvedLiveUser(long Uid,string UserName,long RoomId,long CanonicalRoomId,bool IsLive,string Title);
public sealed record StreamInfo(string Url, string Format);
public sealed record RecordingSession(string Id, long RoomId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, RecordingState State);
public sealed record MediaSegment(string Id, string SessionId, int Sequence, double Start, double End, string Path, bool Locked);
public sealed record HighlightEvent(string Id, string SessionId, string Source, double TriggerTime, double Start, double End, HighlightState State, double Score)
{
    /// <summary>
    /// 弹幕自动切片是否已经收尾。弹幕高光在持续高热期间会把结束点不断往后推，
    /// 只有热度回落到释放线以下（或强制收尾）才置为 true，之后才允许物化。
    /// 手动/其他来源没有这个阶段，默认 false 不影响旧行为。
    /// </summary>
    public bool Closed { get; init; }

    /// <summary>
    /// 只覆盖到可用分片末尾时为 true；不是生命周期状态。
    /// 正常关窗不会产生 partial；崩溃/重启恢复时按已有分片收尾才会置位。
    /// </summary>
    public bool Partial { get; init; }

    /// <summary>
    /// 用户最初请求的结束点（秒）。partial 收尾会把 End 截到最后一个分片，
    /// 但恢复/重试必须拿这个原始终点判断是否已经完整，避免二次恢复把 partial 误判成完整。
    /// 0 表示旧数据没有记录，按 End 处理。
    /// </summary>
    public double RequestedEnd { get; init; }
}
public sealed record DanmakuEvent(string Id, string SessionId, double SessionTime, DateTimeOffset Timestamp, string Type, long Uid, string UserName, string Text, double Weight);
public sealed record Clip(string Id, string EventId, string Path, string? SrtPath, DateTimeOffset CreatedAt);
public sealed record VideoExport(string Id,string ClipId,string EventId,string OutputPath,int BitrateKbps,int Resolution,int Fps,DateTimeOffset CreatedAt);
public sealed record Job(string Id, string Type, string EventId, string Status, double Progress, string? Error, DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null, int RetryCount = 0, string? Message = null);

public interface ILiveSource
{
    Task<RoomInfo> GetRoomInfoAsync(long roomId, CancellationToken token);
    Task<StreamInfo> GetStreamAsync(long roomId, CancellationToken token);
}

public interface IRecorder
{
    Task StartAsync(RecordingSession session, StreamInfo stream, CancellationToken token);
    Task StopAsync();
}

public interface ISegmentBuffer
{
    Task AddAsync(MediaSegment segment, CancellationToken token = default);
    Task<IReadOnlyList<MediaSegment>> GetRangeAsync(string sessionId, double start, double end, CancellationToken token = default);
    Task LockRangeAsync(string sessionId, double start, double end, CancellationToken token = default);
    Task<int> CleanAsync(string sessionId, double latestTime, double bufferSeconds, CancellationToken token = default);
}
public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow; }
public interface IDanmakuClient
{
    event Func<DanmakuEvent,Task>? Received;
    event Action<string>? Error;
    Task RunAsync(RecordingSession session,CancellationToken token);
}

public static class RecordingTransitions
{
    public static RecordingState Move(RecordingState state, RecordingState next) => (state, next) switch
    {
        (RecordingState.Offline, RecordingState.Connecting) or
        (RecordingState.Connecting, RecordingState.Recording) or
        (RecordingState.Connecting, RecordingState.Reconnecting) or
        (RecordingState.Connecting, RecordingState.Offline) or
        (RecordingState.Recording, RecordingState.Reconnecting) or
        (RecordingState.Reconnecting, RecordingState.Recording) or
        (RecordingState.Reconnecting, RecordingState.Finalizing) or
        (RecordingState.Recording, RecordingState.Finalizing) or
        (RecordingState.Finalizing, RecordingState.Ended) => next,
        _ => throw new InvalidOperationException($"Invalid recording transition: {state} → {next}")
    };
}
