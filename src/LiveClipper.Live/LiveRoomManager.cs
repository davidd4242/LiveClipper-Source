namespace LiveClipper.Live;

public sealed record LiveRoomManagerOptions
{
    public int MaxRooms { get; init; } = 8;
}

/// <summary>
/// 多房间生命周期管理：一房一个 supervisor，互不共享可变捕获状态。
/// 管理器只负责启动/停止/索引；每个 supervisor 仍独立负责自己的会话与恢复。
/// </summary>
public sealed class LiveRoomManager : IAsyncDisposable
{
    private readonly Func<long, LiveSessionSupervisor> _factory;
    private readonly int _maxRooms;
    private readonly Dictionary<long, LiveSessionSupervisor> _rooms = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public LiveRoomManager(
        Func<long, LiveSessionSupervisor> factory,
        LiveRoomManagerOptions? options = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _maxRooms = options?.MaxRooms ?? 8;
        if (_maxRooms <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxRooms 必须为正");
    }

    public int MaxRooms => _maxRooms;

    public int Count
    {
        get
        {
            lock (_rooms) return _rooms.Count;
        }
    }

    public IReadOnlyList<LiveStatus> Snapshots()
    {
        lock (_rooms)
        {
            return _rooms.Values
                .Select(supervisor => supervisor.Snapshot)
                .OrderBy(status => status.RoomId)
                .ToArray();
        }
    }

    public bool Contains(long roomId)
    {
        lock (_rooms) return _rooms.ContainsKey(roomId);
    }

    public async Task<LiveStatus> StartAsync(long roomId, CancellationToken token = default, bool autoRecord = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (roomId <= 0) throw new ArgumentOutOfRangeException(nameof(roomId));

        await _gate.WaitAsync(token).ConfigureAwait(false);
        LiveSessionSupervisor? created = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_rooms)
            {
                if (_rooms.TryGetValue(roomId, out var existing))
                    return existing.Snapshot;
                if (_rooms.Count >= _maxRooms)
                    throw new InvalidOperationException($"最多同时监控 {_maxRooms} 个直播间");
            }

            created = _factory(roomId);
            await created.StartAsync(token, autoRecord).ConfigureAwait(false);
            lock (_rooms)
            {
                if (_rooms.TryGetValue(roomId, out var raced))
                {
                    created = null; // 由并发启动的一方持有；本次创建的实例在 finally 释放
                    return raced.Snapshot;
                }
                _rooms[roomId] = created;
                var started = created;
                created = null;
                return started.Snapshot;
            }
        }
        finally
        {
            if (created is not null)
                await created.DisposeAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }

    public async Task<LiveStatus> SetRecordingAsync(long roomId, bool enabled, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            LiveSessionSupervisor previous;
            lock (_rooms)
            {
                if (!_rooms.TryGetValue(roomId, out previous!)) throw new InvalidOperationException("房间尚未开始监控");
                if (previous.Snapshot.AutoRecord == enabled) return previous.Snapshot;
            }
            var next = _factory(roomId);
            await previous.StopAsync().ConfigureAwait(false);
            await previous.DisposeAsync().ConfigureAwait(false);
            lock (_rooms) _rooms[roomId] = next;
            await next.StartAsync(CancellationToken.None, enabled).ConfigureAwait(false);
            return next.Snapshot;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> StopAsync(long roomId)
    {
        LiveSessionSupervisor? removed;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_rooms)
            {
                if (!_rooms.Remove(roomId, out removed)) return false;
            }
        }
        finally
        {
            _gate.Release();
        }

        await removed.StopAsync().ConfigureAwait(false);
        await removed.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async Task StopAllAsync()
    {
        LiveSessionSupervisor[] all;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_rooms)
            {
                all = _rooms.Values.ToArray();
                _rooms.Clear();
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var supervisor in all)
        {
            try { await supervisor.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"live room {supervisor.Snapshot.RoomId} stop failed: {ex.Message}"); }
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAllAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
