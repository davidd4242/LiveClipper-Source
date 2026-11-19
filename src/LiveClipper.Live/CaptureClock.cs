namespace LiveClipper.Live;

/// <summary>
/// 采集时间基准。录制分片与弹幕共用同一个零点（流解析完成、真正开始采集的时刻），
/// 避免直接使用 <see cref="LiveSession.StartedAt"/>（早于流解析）导致两路 offset
/// 出现系统性漂移。可注入 <see cref="TimeProvider"/>，便于离线测试。
/// </summary>
public sealed class CaptureClock
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DateTimeOffset? _startedAt;

    public CaptureClock(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>零点墙钟时间；未锚定时为 null。</summary>
    public DateTimeOffset? StartedAt
    {
        get
        {
            lock (_gate) return _startedAt;
        }
    }

    /// <summary>锚定采集起点。重复调用保留第一次的锚点（幂等）。</summary>
    /// <returns>本次调用是否真正完成了锚定。</returns>
    public bool Start()
    {
        lock (_gate)
        {
            if (_startedAt is not null) return false;
            _startedAt = _time.GetUtcNow();
            return true;
        }
    }

    /// <summary>从锚点开始经过的时间；未锚定或时钟回拨时返回 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                if (_startedAt is not { } started) return TimeSpan.Zero;
                var elapsed = _time.GetUtcNow() - started;
                return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
            }
        }
    }
}
