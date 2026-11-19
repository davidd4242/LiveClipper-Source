namespace LiveClipper.Core;

/// <summary>
/// 媒体注册表引用完整性闸门：工程 PUT/commands 的「预检 + 提交」持读锁（可并发），
/// 删除 Clip/会话持写锁。需同时清理工程的本地删除先取工程 Gate，再取写锁；禁止本闸门 → 工程 Gate。
/// </summary>
public static class MediaRegistryGate
{
    private static readonly ReaderWriterLockSlim Gate = new(LockRecursionPolicy.SupportsRecursion);

    public static IDisposable EnterRead() { Gate.EnterReadLock(); return new Releaser(false); }
    public static IDisposable EnterWrite() { Gate.EnterWriteLock(); return new Releaser(true); }

    private sealed class Releaser(bool write) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            if (write) Gate.ExitWriteLock(); else Gate.ExitReadLock();
        }
    }
}
