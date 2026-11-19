namespace LiveClipper.Live;

/// <summary>G6：多房间自动恢复的退避策略。默认 5s 起步、每次翻倍、上限 5 分钟。</summary>
public sealed record LiveRecoveryOptions
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);
    public double BackoffFactor { get; init; } = 2;
}

/// <summary>一次恢复尝试的可观测记录；失败时带上下一次重试的延迟。</summary>
public sealed record LiveRecoveryAttempt(
    long RoomId,
    DateTimeOffset AttemptedAt,
    bool Succeeded,
    string? Error,
    int Failures,
    TimeSpan? NextDelay);

/// <summary>某个房间当前的恢复状态；诊断端点与会话列表都可读。</summary>
public sealed record LiveRecoveryState(
    long RoomId,
    int Failures,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    bool Succeeded);

/// <summary>一轮恢复扫描的汇总。</summary>
public sealed record LiveRecoveryRun(
    DateTimeOffset CheckedAt,
    int Attempted,
    int Succeeded,
    int Failed,
    int Skipped,
    IReadOnlyList<LiveRecoveryAttempt> Attempts,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 把登记簿里仍标记为 Active 的房间按退避策略逐步恢复。
/// 单个房间失败只记入自己的状态，不阻塞其他房间；失败原因和下次重试时间会写回
/// <see cref="JsonLiveRoomRegistry"/>，重启后仍然可见。
/// </summary>
public sealed class LiveRoomRecoveryService
{
    /// <summary>周期扫描间隔；到期与否由每个房间的 NextAttemptAt 决定。</summary>
    public static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(5);

    public const int MaxHistory = 100;

    private readonly JsonLiveRoomRegistry _registry;
    private readonly LiveRecoveryOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<long, LiveRecoveryState> _states = [];
    private readonly List<LiveRecoveryAttempt> _history = [];

    public LiveRoomRecoveryService(
        JsonLiveRoomRegistry registry,
        LiveRecoveryOptions? options = null,
        TimeProvider? time = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? new LiveRecoveryOptions();
        if (_options.InitialDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "InitialDelay 必须为正");
        if (_options.MaxDelay < _options.InitialDelay) throw new ArgumentOutOfRangeException(nameof(options), "MaxDelay 不能小于 InitialDelay");
        if (_options.BackoffFactor < 1) throw new ArgumentOutOfRangeException(nameof(options), "BackoffFactor 不能小于 1");
        _time = time ?? TimeProvider.System;
    }

    public LiveRecoveryOptions Options => _options;

    public IReadOnlyList<LiveRecoveryState> Snapshot()
    {
        lock (_sync)
        {
            return _states.Values
                .OrderBy(item => item.RoomId)
                .Select(item => item with { })
                .ToArray();
        }
    }

    public IReadOnlyList<LiveRecoveryAttempt> History
    {
        get
        {
            lock (_sync) return _history.ToArray();
        }
    }

    /// <summary>手动启动/停止房间后清掉旧的退避状态（内存与登记簿都清），避免跨重启等待。</summary>
    public async Task ResetAsync(long roomId, CancellationToken token = default)
    {
        lock (_sync)
        {
            _states.Remove(roomId);
            _history.RemoveAll(item => item.RoomId == roomId);
        }

        try
        {
            await _registry.UpdateRecoveryAsync(roomId, null, 0, null, null, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 登记簿写失败不影响内存里的重置；诊断仍能看到旧记录。
            Console.Error.WriteLine($"live room {roomId} recovery reset failed: {ex.Message}");
        }
    }

    /// <summary>扫描一次登记簿：只尝试已经到期的 Active 房间。</summary>
    public async Task<LiveRecoveryRun> RunOnceAsync(
        Func<long, CancellationToken, Task> startRoom,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(startRoom);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var checkedAt = _time.GetUtcNow();
            var registrations = await _registry.ListAsync(token).ConfigureAwait(false);
            var attempts = new List<LiveRecoveryAttempt>();
            var warnings = new List<string>();
            var attempted = 0;
            var succeeded = 0;
            var failed = 0;
            var skipped = 0;

            foreach (var registration in registrations
                .Where(item => item.Active)
                .OrderBy(item => item.RoomId))
            {
                token.ThrowIfCancellationRequested();
                var state = GetOrCreateState(registration);
                // G9：重复扫描幂等——不重复登记（UpdateRecoveryAsync 只更新已有记录），
                // 已处于退避期的房间跳过，成功房间的下一次调用交给 manager.StartAsync 的幂等实现。
                if (state.NextAttemptAt is { } nextAttemptAt && nextAttemptAt > checkedAt)
                {
                    skipped++;
                    continue;
                }

                attempted++;
                var attemptedAt = _time.GetUtcNow();
                try
                {
                    await startRoom(registration.RoomId, token).ConfigureAwait(false);
                    succeeded++;
                    var next = new LiveRecoveryState(registration.RoomId, 0, attemptedAt, null, null, true);
                    lock (_sync) _states[registration.RoomId] = next;
                    attempts.Add(new LiveRecoveryAttempt(registration.RoomId, attemptedAt, true, null, 0, null));
                    await PersistStateSafeAsync(registration.RoomId, attemptedAt, 0, null, null, warnings, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    var failures = state.Failures + 1;
                    var delay = ComputeDelay(failures, _options);
                    var nextAttempt = attemptedAt + delay;
                    var next = new LiveRecoveryState(
                        registration.RoomId,
                        failures,
                        attemptedAt,
                        ex.Message,
                        nextAttempt,
                        false);
                    lock (_sync) _states[registration.RoomId] = next;
                    attempts.Add(new LiveRecoveryAttempt(
                        registration.RoomId,
                        attemptedAt,
                        false,
                        ex.Message,
                        failures,
                        delay));
                    await PersistStateSafeAsync(
                            registration.RoomId,
                            attemptedAt,
                            failures,
                            nextAttempt,
                            ex.Message,
                            warnings,
                            token)
                        .ConfigureAwait(false);
                }
            }

            lock (_sync)
            {
                _history.AddRange(attempts);
                if (_history.Count > MaxHistory)
                    _history.RemoveRange(0, _history.Count - MaxHistory);
            }

            return new LiveRecoveryRun(
                checkedAt,
                attempted,
                succeeded,
                failed,
                skipped,
                attempts.ToArray(),
                warnings.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>周期恢复循环；取消 token 后安静退出。首次启动由调用方立即 RunOnce。</summary>
    public async Task RunLoopAsync(
        Func<long, CancellationToken, Task> startRoom,
        TimeSpan? interval = null,
        CancellationToken token = default)
    {
        var period = interval is { } custom && custom > TimeSpan.Zero ? custom : LoopInterval;
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await RunOnceAsync(startRoom, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("live recovery loop failed: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 应用退出。
        }
    }

    public static TimeSpan ComputeDelay(int failures, LiveRecoveryOptions? options = null)
    {
        if (failures <= 0) return TimeSpan.Zero;
        var target = options ?? new LiveRecoveryOptions();
        var seconds = target.InitialDelay.TotalSeconds * Math.Pow(target.BackoffFactor, failures - 1);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds >= target.MaxDelay.TotalSeconds)
            return target.MaxDelay;
        return TimeSpan.FromSeconds(seconds);
    }

    private LiveRecoveryState GetOrCreateState(LiveRoomRegistration registration)
    {
        lock (_sync)
        {
            if (_states.TryGetValue(registration.RoomId, out var existing)) return existing;
            var derived = new LiveRecoveryState(
                registration.RoomId,
                registration.RecoveryFailures,
                registration.LastRecoveryAttemptAt,
                registration.LastRecoveryError,
                registration.NextRecoveryAt,
                false);
            _states[registration.RoomId] = derived;
            return derived;
        }
    }

    private async Task PersistStateSafeAsync(
        long roomId,
        DateTimeOffset attemptedAt,
        int failures,
        DateTimeOffset? nextAttemptAt,
        string? error,
        List<string> warnings,
        CancellationToken token)
    {
        try
        {
            await _registry.UpdateRecoveryAsync(
                    roomId,
                    attemptedAt,
                    failures,
                    nextAttemptAt,
                    error,
                    token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"live room {roomId} 恢复状态落盘失败：{ex.Message}");
        }
    }
}
