using System.Collections.Concurrent;

namespace LiveClipper.Core.Bridge;

/// <summary>
/// 桥接任务注册表 + 执行队列（并发上限 2）。供 /api/bridge/* 端点使用，也可在测试里直接驱动。
/// </summary>
public sealed class BridgeJobService : IDisposable
{
    public const int MaxConcurrentJobs = 2;
    public static readonly IReadOnlyList<string> SupportedVariants = ["preserve", "fold"];
    public static readonly IReadOnlyList<string> SupportedFormats = ["h264", "mp4"];

    private readonly ConcurrentDictionary<string, BridgeJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _enqueueGate = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentJobs, MaxConcurrentJobs);
    private readonly IBridgeExecutor _executor;
    private readonly Func<string, bool>? _clipExists;
    private readonly string _resultRoot;
    private readonly TimeSpan _jobTimeout;
    private int _disposed;

    public BridgeJobService(IBridgeExecutor executor, string storageRoot, Func<string, bool>? clipExists = null, TimeSpan? jobTimeout = null)
    {
        _executor = executor;
        _clipExists = clipExists;
        _resultRoot = Path.Combine(storageRoot, "bridge-clips");
        _jobTimeout = jobTimeout ?? TimeSpan.FromMinutes(30);
        Directory.CreateDirectory(_resultRoot);
    }

    public BridgeCapabilitiesInfo Capabilities => _executor.Capabilities;

    public (BridgeJobSnapshot? Job, BridgeError? Error, int StatusCode) Enqueue(BridgeExportRequest request)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return (null, new BridgeError("Internal", "桥接服务已停止", null), 500);

        var clipId = request.ClipId?.Trim() ?? string.Empty;
        if (clipId.Length == 0)
            return (null, new BridgeError("ValidationError", "clipId 不能为空", "clipId"), 400);

        var variant = string.IsNullOrWhiteSpace(request.Options?.Variant ?? request.Variant)
            ? "preserve"
            : (request.Options?.Variant ?? request.Variant)!.Trim().ToLowerInvariant();
        if (!SupportedVariants.Contains(variant))
            return (null, new BridgeError("ValidationError", "variant 只支持 preserve|fold", variant), 400);

        var format = string.IsNullOrWhiteSpace(request.Options?.Format ?? request.Format)
            ? "h264"
            : (request.Options?.Format ?? request.Format)!.Trim().ToLowerInvariant();
        if (!SupportedFormats.Contains(format))
            return (null, new BridgeError("ValidationError", "format 只支持 h264|mp4", format), 400);

        var jobs = request.Options?.Jobs ?? request.Jobs;
        if (jobs is not (null or 1 or 2))
            return (null, new BridgeError("ValidationError", "jobs 只支持 1|2", jobs?.ToString()), 400);

        // B8-3：转场时长补偿默认开启，可显式 options.compensate=false 关闭（契约见 capabilities.export.compensate）。
        var compensate = request.Options?.Compensate ?? true;

        if (_clipExists is not null && !_clipExists(clipId))
            return (null, new BridgeError("NotFound", "切片不存在", clipId), 404);

        lock (_enqueueGate)
        {
            var active = _jobs.Values.Where(job => job.IsActive).ToArray();
            if (active.Length >= MaxConcurrentJobs)
                return (null, new BridgeError("Conflict", "并发上限 2，稍后重试", $"active={active.Length}"), 409);
            if (active.Any(job => string.Equals(job.ClipId, clipId, StringComparison.Ordinal)))
                return (null, new BridgeError("Conflict", "同一切片已有进行中的导出任务", clipId), 409);

            var id = Guid.NewGuid().ToString("N");
            var job = new BridgeJob(id, clipId, variant, format, Path.Combine(_resultRoot, $"{id}.mp4"), compensate);
            _jobs[id] = job;
            _ = Task.Run(() => RunAsync(job));
            return (job.Snapshot(), null, 202);
        }
    }

    public (BridgeJobSnapshot? Job, BridgeError? Error, int StatusCode) Get(string id)
    {
        if (!BridgeEndpointFormatting.IsJobId(id))
            return (null, new BridgeError("ValidationError", "jobId 必须是 32 位小写 hex", id), 400);
        return _jobs.TryGetValue(id, out var job)
            ? (job.Snapshot(), null, 200)
            : (null, new BridgeError("NotFound", "任务不存在", id), 404);
    }

    public IReadOnlyList<BridgeJobSnapshot> List(int limit = 50) =>
        _jobs.Values
            .OrderByDescending(job => job.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(job => job.Snapshot())
            .ToArray();

    public (BridgeJobSnapshot? Job, BridgeError? Error, int StatusCode) Cancel(string id)
    {
        if (!BridgeEndpointFormatting.IsJobId(id))
            return (null, new BridgeError("ValidationError", "jobId 必须是 32 位小写 hex", id), 400);
        if (!_jobs.TryGetValue(id, out var job))
            return (null, new BridgeError("NotFound", "任务不存在", id), 404);
        if (!job.RequestCancel())
            return (null, new BridgeError("Conflict", "任务已结束，无法取消", job.Snapshot().Status), 409);
        try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return (job.Snapshot(), null, 200);
    }

    private async Task RunAsync(BridgeJob job)
    {
        await _slots.WaitAsync().ConfigureAwait(false);
        try
        {
            if (job.IsCancelRequested) return;
            job.MarkRunning();
            using var timeout = new CancellationTokenSource(_jobTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, job.Cancellation.Token);
            try
            {
                var path = await _executor.RunAsync(new BridgeJobContext
                {
                    Id = job.Id,
                    ClipId = job.ClipId,
                    Variant = job.Variant,
                    Format = job.Format,
                    ResultPath = job.ResultPath,
                    ReportProgress = job.ReportProgress,
                    Compensate = job.Compensate
                }, linked.Token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    job.Fail(new BridgeError("MappingError", "导出完成但结果文件不存在", path));
                else
                    job.Succeed(path);
            }
            catch (OperationCanceledException) when (job.IsCancelRequested)
            {
                // RequestCancel 已把状态置为 cancelled。
            }
            catch (OperationCanceledException)
            {
                job.Fail(new BridgeError("BridgeTimeout", "导出超时", _jobTimeout.ToString()));
            }
            catch (BridgeExecutionException ex)
            {
                job.Fail(ex.Error);
            }
            catch (Exception ex)
            {
                job.Fail(new BridgeError("FilmCraftError", "导出失败", ex.Message));
            }
        }
        finally
        {
            WriteJobLog(job);
            _slots.Release();
        }
    }

    /// <summary>
    /// 每个任务在结果目录写一个 {jobId}.log：失败保留 class/message/detail 便于归因（B12-3），
    /// 成功与取消写一行 summary；日志写入失败不影响任务状态。
    /// </summary>
    private void WriteJobLog(BridgeJob job)
    {
        try
        {
            var snapshot = job.Snapshot();
            var stamp = DateTimeOffset.UtcNow.ToString("O");
            string? text = snapshot.Status switch
            {
                BridgeJobStatuses.Succeeded =>
                    $"{stamp} succeeded clip={snapshot.ClipId} variant={snapshot.Variant} format={snapshot.Format} progress={snapshot.Progress} result={snapshot.ResultPath}{Environment.NewLine}",
                BridgeJobStatuses.Failed =>
                    $"{stamp} failed clip={snapshot.ClipId} variant={snapshot.Variant} format={snapshot.Format} class={snapshot.Error?.Class ?? "Unknown"}{Environment.NewLine}message={snapshot.Error?.Message}{Environment.NewLine}detail={snapshot.Error?.Detail}{Environment.NewLine}",
                BridgeJobStatuses.Cancelled =>
                    $"{stamp} cancelled clip={snapshot.ClipId} variant={snapshot.Variant} format={snapshot.Format}{Environment.NewLine}",
                _ => null
            };
            if (text is null) return;
            File.WriteAllText(Path.Combine(_resultRoot, $"{job.Id}.log"), text);
            BridgeLogRotation.Prune(_resultRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写入失败不改变任务结果。
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var job in _jobs.Values)
        {
            if (!job.IsActive) continue;
            job.RequestCancel();
            try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        _slots.Dispose();
    }
}

public static class BridgeEndpointFormatting
{
    public static bool IsJobId(string? id) =>
        id is { Length: 32 } && id.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
