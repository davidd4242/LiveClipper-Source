namespace LiveClipper.Live;

/// <summary>单次入队结果，让调用方区分“正常入队”和“为腾位置挤掉最旧一帧”。</summary>
public enum DanmakuEnqueueResult { Enqueued, DroppedOldest, Completed }

/// <summary>
/// G7：有界弹幕缓冲。取代 <see cref="System.Threading.Channels.BoundedChannelFullMode.DropOldest"/>
/// 的静默丢弃——队列满时挤掉最旧一帧，并把“发生过丢弃”作为返回值交给调用方计数。
/// 单生产者/单消费者：生产者 <see cref="TryEnqueue"/>，消费者 <see cref="DequeueAsync"/>。
/// </summary>
public sealed class DanmakuBuffer
{
    private readonly object _sync = new();
    private readonly Queue<DanmakuFrame> _items;
    private readonly SemaphoreSlim _signal = new(0);
    private bool _completed;
    private long _droppedCount;

    public DanmakuBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "弹幕缓冲容量必须为正");
        Capacity = capacity;
        _items = new Queue<DanmakuFrame>(capacity);
    }

    public int Capacity { get; }

    public int Count
    {
        get { lock (_sync) return _items.Count; }
    }

    /// <summary>因队列已满而被挤掉的最旧帧数量。</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>入队一帧；队列已满时先挤掉最旧帧，返回 <see cref="DanmakuEnqueueResult.DroppedOldest"/>。</summary>
    public DanmakuEnqueueResult TryEnqueue(DanmakuFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var dropped = false;
        lock (_sync)
        {
            if (_completed) return DanmakuEnqueueResult.Completed;
            if (_items.Count >= Capacity)
            {
                _items.Dequeue();
                dropped = true;
            }

            _items.Enqueue(frame);
        }

        if (dropped) Interlocked.Increment(ref _droppedCount);
        _signal.Release();
        return dropped ? DanmakuEnqueueResult.DroppedOldest : DanmakuEnqueueResult.Enqueued;
    }

    /// <summary>标记生产结束；消费者在队列排空后读到 null。</summary>
    public void Complete()
    {
        lock (_sync) _completed = true;
        _signal.Release();
    }

    /// <summary>异步出队；生产已结束且队列为空时返回 null（可重复调用，不会永久阻塞）。</summary>
    public async ValueTask<DanmakuFrame?> DequeueAsync(CancellationToken token)
    {
        while (true)
        {
            lock (_sync)
            {
                if (_items.Count > 0) return _items.Dequeue();
                if (_completed) return null;
            }

            await _signal.WaitAsync(token).ConfigureAwait(false);
        }
    }
}
