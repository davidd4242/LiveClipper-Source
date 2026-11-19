using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveClipper.Core.Bridge;

/// <summary>桥接任务状态。JSON 里统一小写，避免全局 JsonStringEnumConverter 输出 PascalCase。</summary>
public static class BridgeJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record BridgeError(string Class, string Message, string? Detail = null);

/// <summary>执行层错误：Endpoints 统一转成 {error:{class,message,detail}}，并保留 HTTP 语义。</summary>
public sealed class BridgeExecutionException(BridgeError error) : Exception(error.Message)
{
    public BridgeError Error { get; } = error;
}

/// <summary>导出选项：variant/formats/jobs 等。jobs 只接受 1|2（契约测试 V2）。</summary>
/// <remarks>B8-3：compensate 默认 true（FC 时间轴按 LC 转场压缩语义补正），传 false 可关闭。</remarks>
public sealed record BridgeExportOptions(
    string? Variant = null,
    string? Format = null,
    int? Jobs = null,
    bool? HardwareEncoding = null,
    bool? Compensate = null);

public sealed record BridgeExportRequest(
    string? ClipId = null,
    JsonElement? Plan = null,
    BridgeExportOptions? Options = null,
    string? Variant = null,
    string? Format = null,
    int? Jobs = null);

public sealed record BridgeJobSnapshot(
    string Id,
    string ClipId,
    string Variant,
    string Format,
    string Status,
    int Progress,
    string? ResultPath,
    BridgeError? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<string> Warnings);

public sealed record BridgeCapabilitiesInfo(
    string FilmCraftVersion,
    int SchemaVersion,
    IReadOnlyList<string> Supports,
    IReadOnlyList<string> Variants,
    IReadOnlyList<string> Formats,
    bool CompensateDefault = true);

/// <summary>
/// 真正的导出执行委托。测试里用 fake，生产默认用 <see cref="FilmCraftBridgeExecutor"/>。
/// 返回最终 mp4 的绝对路径；失败抛 <see cref="BridgeExecutionException"/>。
/// </summary>
public interface IBridgeExecutor
{
    BridgeCapabilitiesInfo Capabilities { get; }
    Task<string> RunAsync(BridgeJobContext context, CancellationToken token);
}

public sealed class BridgeJobContext
{
    public required string Id { get; init; }
    public required string ClipId { get; init; }
    public required string Variant { get; init; }
    public required string Format { get; init; }
    public required string ResultPath { get; init; }
    public required Action<int> ReportProgress { get; init; }

    /// <summary>B8-3：是否把 --transition-compensate 传给映射脚本（默认 true）。</summary>
    public bool Compensate { get; init; } = true;
}

/// <summary>一次桥接任务的内存态；所有状态迁移都经过锁，供轮询线程安全读取。</summary>
public sealed class BridgeJob
{
    private readonly object _gate = new();
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private string _status = BridgeJobStatuses.Queued;
    private int _progress;
    private string? _resultPath;
    private BridgeError? _error;
    private bool _cancelRequested;

    public BridgeJob(string id, string clipId, string variant, string format, string resultPath, bool compensate = true)
    {
        Id = id;
        ClipId = clipId;
        Variant = variant;
        Format = format;
        ResultPath = resultPath;
        Compensate = compensate;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public string ClipId { get; }
    public string Variant { get; }
    public string Format { get; }
    public string ResultPath { get; }
    public DateTimeOffset CreatedAt { get; }
    /// <summary>B8-3：转场时长补偿（默认开启）；关闭时导出保留 FC 时间轴。</summary>
    public bool Compensate { get; }
    public CancellationTokenSource Cancellation { get; } = new();

    public bool IsActive
    {
        get { lock (_gate) return _status is BridgeJobStatuses.Queued or BridgeJobStatuses.Running; }
    }

    public bool IsCancelRequested
    {
        get { lock (_gate) return _cancelRequested; }
    }

    public void MarkRunning()
    {
        lock (_gate)
        {
            if (_status != BridgeJobStatuses.Queued) return;
            _status = BridgeJobStatuses.Running;
            _startedAt = DateTimeOffset.UtcNow;
        }
    }

    public void ReportProgress(int value)
    {
        lock (_gate)
        {
            if (_status is not (BridgeJobStatuses.Queued or BridgeJobStatuses.Running)) return;
            var clamped = Math.Clamp(value, 0, 100);
            if (clamped > _progress) _progress = clamped;
        }
    }

    public void Succeed(string path)
    {
        lock (_gate)
        {
            if (_status is not (BridgeJobStatuses.Queued or BridgeJobStatuses.Running)) return;
            _status = BridgeJobStatuses.Succeeded;
            _progress = 100;
            _resultPath = path;
            _finishedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(BridgeError error)
    {
        lock (_gate)
        {
            if (_status is not (BridgeJobStatuses.Queued or BridgeJobStatuses.Running)) return;
            _status = BridgeJobStatuses.Failed;
            _error = error;
            _finishedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>请求取消；终态返回 false（Endpoints 映射成 409 Conflict）。</summary>
    public bool RequestCancel()
    {
        lock (_gate)
        {
            if (_status is not (BridgeJobStatuses.Queued or BridgeJobStatuses.Running)) return false;
            _cancelRequested = true;
            _status = BridgeJobStatuses.Cancelled;
            _finishedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public BridgeJobSnapshot Snapshot()
    {
        lock (_gate)
        {
            var warnings = Compensate
                ? new[] { "已启用转场时长补偿（FC 时间轴按 LC 压缩语义补正；转场区间画面混合仍为 FC 几何语义）" }
                : new[] { "已关闭转场时长补偿（FC 保留时间轴，含转场时导出时长可能比 LC 压缩输出长）" };
            return new BridgeJobSnapshot(Id, ClipId, Variant, Format, _status, _progress, _resultPath, _error,
                CreatedAt, _startedAt, _finishedAt, warnings);
        }
    }
}
