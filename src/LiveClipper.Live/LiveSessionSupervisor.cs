using System.Threading.Channels;

namespace LiveClipper.Live;

/// <summary>
/// 单直播间的最小直播底座。职责边界：
/// 探测房间 → 新建逻辑会话 → 同时跑分片录制和弹幕读取 → 失败后同一会话重连
/// → 下播/停止时收尾。网络失败不再让旧任务和心跳互相覆盖。
/// </summary>
public sealed class LiveSessionSupervisor : IAsyncDisposable
{
    private readonly long _roomId;
    private readonly ILiveRoomProbe _probe;
    private readonly IStreamSourceResolver _resolver;
    private readonly ISegmentRecorder _recorder;
    private readonly IDanmakuFeed _danmaku;
    private readonly ILiveSessionStore _store;
    private readonly LiveSupervisorOptions _options;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _segmentGate = new(1, 1);

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    private LiveRoom? _room;
    private LiveSession? _session;
    private string? _lastError;
    private string? _resumeFrom;
    private bool _running;
    private bool _autoRecord;
    private bool _disposed;
    private int _segmentCount;
    private long _danmakuCount;
    private int _recoveredSessions;
    private int _recoveredSegments;
    private int _resumeSequence;

    public LiveSessionSupervisor(
        long roomId,
        ILiveRoomProbe probe,
        IStreamSourceResolver resolver,
        ISegmentRecorder recorder,
        IDanmakuFeed danmaku,
        ILiveSessionStore store,
        LiveSupervisorOptions? options = null,
        TimeProvider? time = null)
    {
        if (roomId <= 0) throw new ArgumentOutOfRangeException(nameof(roomId));
        _roomId = roomId;
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _danmaku = danmaku ?? throw new ArgumentNullException(nameof(danmaku));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new LiveSupervisorOptions();
        _time = time ?? TimeProvider.System;

        if (_options.DanmakuBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "弹幕批量大小必须为正");
        if (_options.DanmakuQueueCapacity < _options.DanmakuBatchSize)
            throw new ArgumentOutOfRangeException(nameof(options), "弹幕队列容量不能小于批量大小");
    }

    public event Action<LiveStatus>? StatusChanged;

    public LiveStatus Snapshot
    {
        get
        {
            lock (_gate)
            {
                var feedMetrics = _danmaku as IDanmakuFeedMetrics;
                return new LiveStatus(
                    _roomId,
                    _room?.Availability ?? RoomAvailability.Unknown,
                    _running && _loopTask is { IsCompleted: false },
                    _captureTask is { IsCompleted: false },
                    _room,
                    _session,
                    _lastError,
                    _segmentCount,
                    _danmakuCount,
                    _session?.RestartCount ?? 0,
                    _recoveredSessions,
                    _time.GetUtcNow(),
                    _recoveredSegments,
                    feedMetrics?.QueueDroppedCount ?? 0,
                    feedMetrics?.QueueCapacity ?? 0,
                    _autoRecord);
            }
        }
    }

    public Task StartAsync(CancellationToken token = default, bool autoRecord = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopTask is { IsCompleted: false }) return Task.CompletedTask;

            _loopCts?.Dispose();
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _autoRecord = autoRecord;
            _running = true;
            _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
        }
        Publish();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _loopCts;
            loop = _loopTask;
        }

        cts?.Cancel();
        if (loop is not null)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
        }

        // RunLoopAsync 的 finally 已经做收尾；这里再调一次是幂等的，覆盖“循环已退出
        // 但最后一次捕获任务还没被外部等待”的边界。
        await EndSessionAsync("服务停止", CancellationToken.None);

        lock (_gate)
        {
            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
            _running = false;
            _captureCts?.Dispose();
            _captureCts = null;
            _captureTask = null;
        }
        Publish();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            await RecoverAsync(token);
            var failed = false;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var room = await _probe.ProbeAsync(_roomId, token);
                    lock (_gate) { _room = room; }

                    if (room.Availability == RoomAvailability.Live && _autoRecord)
                    {
                        if (!IsCaptureActive())
                        {
                            var started = await StartCaptureAsync(room, token);
                            failed = !started;
                        }
                        else
                        {
                            failed = false;
                        }
                    }
                    else if (room.Availability == RoomAvailability.Offline)
                    {
                        if (HasSession) await EndSessionAsync("直播已结束", token);
                        failed = false;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetError(ex.Message);
                    failed = true;
                }

                Publish();
                try
                {
                    await Task.Delay(failed ? _options.RetryInterval : _options.ProbeInterval, _time, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            try { await EndSessionAsync("服务停止", CancellationToken.None); }
            catch (Exception ex) { SetError(ex.Message); }
            lock (_gate) { _running = false; }
            Publish();
        }
    }

    private async Task<bool> StartCaptureAsync(LiveRoom room, CancellationToken token)
    {
        LiveSession session;
        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return true;
            if (_session is null) _session = CreateSessionLocked();
            if (_session.State is LiveSessionState.Ended or LiveSessionState.Interrupted or LiveSessionState.Failed)
                _session = CreateSessionLocked();

            _session = _session with { State = LiveSessionState.Starting, LastError = null };
            session = _session;
        }

        await _store.SaveSessionAsync(session, token);

        StreamSource? stream;
        try
        {
            stream = await _resolver.ResolveAsync(room, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkReconnectingAsync(ex.Message, token);
            return false;
        }

        if (stream is null)
        {
            await MarkReconnectingAsync("没有取到可用的直播流地址", token);
            return false;
        }

        lock (_gate)
        {
            if (_session is null || _session.Id != session.Id)
                return false;
            _session = _session with { State = LiveSessionState.Recording, LastError = null };
            session = _session;
        }
        await _store.SaveSessionAsync(session, token);

        lock (_gate)
        {
            if (_session is null || _session.Id != session.Id || token.IsCancellationRequested)
                return false;

            _captureCts?.Dispose();
            _captureCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var captureToken = _captureCts.Token;
            _captureTask = Task.Run(() => CaptureAsync(session, stream, captureToken), captureToken);
        }

        Publish();
        return true;
    }

    private async Task CaptureAsync(LiveSession session, StreamSource stream, CancellationToken token)
    {
        // 流解析完成后才锚定采集零点：录制分片与弹幕都从这一时刻算 offset，
        // 不再各自用会话创建时间（早于解析）当基准，避免两路时间线漂移。
        var clock = new CaptureClock(_time);
        clock.Start();
        var context = new CaptureContext(session, stream, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var recorderTask = _recorder.RunAsync(context, OnSegmentAsync, linked.Token);
            var danmakuTask = PumpDanmakuAsync(context, linked.Token);

            // 不能直接 WhenAll：任何一路故障时，另一路可能仍在无限读取，WhenAll
            // 会一直等下去，重连逻辑就永远不触发。先等首个结束，再取消另一路。
            await Task.WhenAny(recorderTask, danmakuTask);
            linked.Cancel();
            try { await Task.WhenAll(recorderTask, danmakuTask); } catch { }

            if (token.IsCancellationRequested) return;

            if (recorderTask.IsFaulted) await recorderTask;
            if (danmakuTask.IsFaulted) await danmakuTask;

            await MarkReconnectingAsync("录制或弹幕任务自行结束，准备重连", token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                await MarkReconnectingAsync(ex.Message, token);
        }
    }

    private async Task OnSegmentAsync(SegmentArtifact segment, CancellationToken token)
    {
        LiveSession? updated;
        await _segmentGate.WaitAsync(token);
        try
        {
            await _store.AppendSegmentAsync(segment, token);
            lock (_gate)
            {
                if (_session is null || _session.Id != segment.SessionId) return;
                if (segment.Sequence <= _session.LastSequence) return;
                updated = _session with { LastSequence = segment.Sequence };
                _session = updated;
            }
            Interlocked.Increment(ref _segmentCount);
            await _store.SaveSessionAsync(updated, token);
        }
        finally
        {
            _segmentGate.Release();
        }
        Publish();
    }

    private async Task PumpDanmakuAsync(CaptureContext context, CancellationToken token)
    {
        var channel = Channel.CreateBounded<DanmakuFrame>(new BoundedChannelOptions(_options.DanmakuQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var producer = ProduceAsync();
        var consumer = ConsumeAsync();
        try
        {
            await Task.WhenAll(producer, consumer);
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        async Task ProduceAsync()
        {
            try
            {
                await foreach (var frame in _danmaku.ReadAsync(context, token))
                    await channel.Writer.WriteAsync(frame, token);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }

        async Task ConsumeAsync()
        {
            var batch = new List<DanmakuFrame>(_options.DanmakuBatchSize);
            try
            {
                while (await channel.Reader.WaitToReadAsync(token))
                {
                    while (batch.Count < _options.DanmakuBatchSize && channel.Reader.TryRead(out var frame))
                        batch.Add(frame);

                    if (batch.Count >= _options.DanmakuBatchSize)
                    {
                        await FlushAsync();
                    }
                    else if (batch.Count > 0)
                    {
                        await Task.Delay(_options.DanmakuFlushInterval, _time, token);
                        if (batch.Count > 0) await FlushAsync();
                    }
                }

                if (batch.Count > 0) await FlushAsync();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 停止前尽可能把已读到的消息保住，不把半批丢掉。
                if (batch.Count > 0)
                {
                    try { await FlushAsync(CancellationToken.None); } catch { }
                }
                throw;
            }

            async Task FlushAsync(CancellationToken flushToken = default)
            {
                if (batch.Count == 0) return;
                var items = batch.ToArray();
                batch.Clear();
                await _store.AppendDanmakuAsync(items, flushToken);
                Interlocked.Add(ref _danmakuCount, items.Length);
                Publish();
            }
        }
    }

    private async Task RecoverAsync(CancellationToken token)
    {
        var open = await _store.LoadOpenSessionsAsync(token);
        if (open.Count == 0) return;

        var recovered = 0;
        var recoveredSegments = 0;
        string? latest = null;
        var latestSequence = 0;
        foreach (var session in open)
        {
            token.ThrowIfCancellationRequested();
            var interrupted = session with
            {
                State = LiveSessionState.Interrupted,
                EndedAt = _time.GetUtcNow(),
                LastError = "服务重启，旧会话已中断"
            };

            // 崩溃时 FFmpeg 可能已经写完分片、但 supervisor 还没把元数据落库。
            // 分片文件就是恢复依据：扫描未索引文件补录，并把 LastSequence 修正为最大值。
            if (_recorder is ISegmentRecovery recovery)
            {
                try
                {
                    var indexed = await _store.LoadSegmentsAsync(session.Id, token);
                    var afterSequence = indexed.Count == 0 ? 0 : indexed.Max(item => item.Sequence);
                    var startOffset = indexed.Count == 0
                        ? TimeSpan.Zero
                        : indexed.Max(item => item.Offset + item.Duration);
                    var orphans = await recovery.RecoverOrphansAsync(
                        session.Id,
                        indexed.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        afterSequence,
                        startOffset,
                        token);
                    foreach (var orphan in orphans)
                    {
                        await _store.AppendSegmentAsync(orphan, token);
                        recoveredSegments++;
                    }

                    var lastSequence = afterSequence + orphans.Count;
                    if (lastSequence > interrupted.LastSequence)
                        interrupted = interrupted with { LastSequence = lastSequence };
                    if (orphans.Count > 0)
                        interrupted = interrupted with
                        {
                            RecoveredSegments = session.RecoveredSegments + orphans.Count
                        };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 恢复失败不能阻塞整个房间：保留旧状态并记录原因，交给下一轮探测/诊断。
                    var reason = "分片索引恢复失败：" + ex.Message;
                    interrupted = interrupted with
                    {
                        LastError = interrupted.LastError is null
                            ? reason
                            : interrupted.LastError + "；" + reason
                    };
                }
            }

            await _store.SaveSessionAsync(interrupted, token);
            recovered++;
            latest = session.Id;
            latestSequence = interrupted.LastSequence;
        }

        lock (_gate)
        {
            _recoveredSessions = recovered;
            _recoveredSegments = recoveredSegments;
            _resumeFrom = latest;
            _resumeSequence = latestSequence;
        }
        Publish();
    }

    private async Task MarkReconnectingAsync(string message, CancellationToken token)
    {
        LiveSession? updated = null;
        lock (_gate)
        {
            if (_session is null || _session.State is LiveSessionState.Ended or LiveSessionState.Interrupted)
                return;
            updated = _session with
            {
                State = LiveSessionState.Reconnecting,
                RestartCount = _session.RestartCount + 1,
                LastError = message
            };
            _session = updated;
            _lastError = message;
        }

        if (updated is not null) await _store.SaveSessionAsync(updated, token);
        Publish();
    }

    private async Task EndSessionAsync(string reason, CancellationToken token)
    {
        CancellationTokenSource? captureCts;
        Task? captureTask;
        LiveSession? session;
        lock (_gate)
        {
            captureCts = _captureCts;
            captureTask = _captureTask;
            session = _session;
        }

        captureCts?.Cancel();
        if (captureTask is not null)
        {
            try { await captureTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetError(ex.Message); }
        }

        if (session is null)
        {
            lock (_gate) { _captureCts = null; _captureTask = null; }
            return;
        }

        lock (_gate)
        {
            if (_session is null
                || _session.Id != session.Id
                || _session.State is LiveSessionState.Ended
                    or LiveSessionState.Interrupted
                    or LiveSessionState.Failed)
            {
                _captureCts = null;
                _captureTask = null;
                return;
            }
            _session = _session with { State = LiveSessionState.Finalizing };
            session = _session;
        }

        try
        {
            await _store.SaveSessionAsync(session, token);
            lock (_gate)
            {
                _session = _session is null
                    ? null
                    : _session with
                    {
                        State = LiveSessionState.Ended,
                        EndedAt = _time.GetUtcNow(),
                        LastError = reason == "直播已结束" ? null : _lastError
                    };
                session = _session;
                _captureCts?.Dispose();
                _captureCts = null;
                _captureTask = null;
            }
            if (session is not null) await _store.SaveSessionAsync(session, token);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            lock (_gate)
            {
                if (_session is not null)
                    _session = _session with { State = LiveSessionState.Failed, LastError = ex.Message };
            }
        }

        lock (_gate) { _lastError = null; }
        Publish();
    }

    private LiveSession CreateSessionLocked()
    {
        Interlocked.Exchange(ref _segmentCount, 0);
        Interlocked.Exchange(ref _danmakuCount, 0);
        var resumedFrom = _resumeFrom;
        var lastSequence = _resumeSequence;
        _resumeFrom = null;
        _resumeSequence = 0;
        return new LiveSession(
            Guid.NewGuid().ToString("N"),
            _roomId,
            LiveSessionState.Starting,
            _time.GetUtcNow(),
            ResumedFrom: resumedFrom,
            LastSequence: lastSequence);
    }

    private bool IsCaptureActive()
    {
        lock (_gate) return _captureTask is { IsCompleted: false };
    }

    private bool HasSession
    {
        get { lock (_gate) return _session is not null; }
    }

    private void SetError(string message)
    {
        lock (_gate) { _lastError = message; }
        Publish();
    }

    private void Publish()
    {
        var snapshot = Snapshot;
        try { StatusChanged?.Invoke(snapshot); }
        catch { /* 观察者异常不能打断录制 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _segmentGate.Dispose();
        lock (_gate) { _captureCts?.Dispose(); _loopCts?.Dispose(); }
    }
}
