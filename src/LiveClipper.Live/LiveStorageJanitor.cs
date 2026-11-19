namespace LiveClipper.Live;

/// <summary>
/// 直播缓冲区的保留策略。只清理媒体目录，不碰仍可恢复的开放会话；
/// 已结束或没有清单的孤儿目录才会在超过保留期/配额时删除。
/// </summary>
public sealed record LiveStorageOptions
{
    public string BufferRoot { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveClipper", "live", "buffer");
    /// <summary>0 表示不限制总字节数。</summary>
    public long MaxTotalBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    /// <summary>0 表示不检查剩余空间。</summary>
    public long MinFreeBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    /// <summary>0 或负值表示不按时间清理。</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(3);
    public bool DeleteMetadata { get; init; } = true;
}

public sealed record LiveStorageReport(
    DateTimeOffset CheckedAt,
    bool Enforced,
    long TotalBytes,
    long MetadataBytes,
    long FreeBytes,
    long MaxTotalBytes,
    long MinFreeBytes,
    int SessionDirectories,
    int ProtectedSessions,
    int ExpiredSessions,
    int DeletedSessions,
    long DeletedBytes,
    bool QuotaExceeded,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 直播分片磁盘清理器。判定顺序是：保留活动会话 → 删除超过保留期的已结束会话
/// → 仍超配额时从最旧的已结束会话开始删。删除媒体后同时删清单/索引，避免留
/// 下指向不存在分片的元数据。
/// </summary>
public sealed class LiveStorageJanitor
{
    private readonly ILiveSessionStore _store;
    private readonly LiveStorageOptions _options;
    private readonly Func<IReadOnlySet<string>> _activeSessionIds;
    private readonly string _bufferRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;

    public LiveStorageJanitor(
        ILiveSessionStore store,
        LiveStorageOptions? options = null,
        Func<IReadOnlySet<string>>? activeSessionIds = null,
        TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new LiveStorageOptions();
        if (_options.MaxTotalBytes < 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalBytes 不能为负");
        if (_options.MinFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(options), "MinFreeBytes 不能为负");
        if (string.IsNullOrWhiteSpace(_options.BufferRoot)) throw new ArgumentException("BufferRoot 不能为空", nameof(options));
        _activeSessionIds = activeSessionIds ?? (static () => new HashSet<string>(StringComparer.Ordinal));
        _bufferRoot = Path.GetFullPath(_options.BufferRoot);
        Directory.CreateDirectory(_bufferRoot);
        _time = time ?? TimeProvider.System;
    }

    public string BufferRoot => _bufferRoot;

    /// <summary>清理器持有的会话存储；只读端点用它列出历史会话，避免再复制一份存储实例。</summary>
    public ILiveSessionStore Store => _store;

    public Task<LiveStorageReport> InspectAsync(CancellationToken token = default) =>
        RunAsync(enforce: false, token);

    public Task<LiveStorageReport> EnforceAsync(CancellationToken token = default) =>
        RunAsync(enforce: true, token);

    private async Task<LiveStorageReport> RunAsync(bool enforce, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            var sessions = await _store.LoadAllSessionsAsync(token);
            var sessionById = sessions.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var active = _activeSessionIds();
            var entries = Scan(sessionById, active, now, token);
            var metadataBytes = SumMetadataBytes(token);
            var initialTotal = entries.Sum(item => item.Bytes);
            var initialFree = GetFreeBytes();
            var initialQuotaExceeded = IsOverQuota(initialTotal, initialFree);
            var expired = entries
                .Where(item => item.Expired)
                .OrderBy(item => item.AgeAt)
                .ToArray();
            var warnings = new List<string>();
            var deletedSessions = 0;
            var deletedBytes = 0L;

            if (enforce)
            {
                var candidates = expired.ToList();
                if (initialQuotaExceeded)
                {
                    // 已结束/孤儿会话按结束时间从旧到新补足配额；活动会话永远不在这里。
                    foreach (var candidate in entries
                        .Where(item => !item.Protected && !item.Expired)
                        .OrderBy(item => item.AgeAt))
                    {
                        candidates.Add(candidate);
                    }
                }

                var totalBytes = initialTotal;
                var freeBytes = initialFree;
                foreach (var entry in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (!initialQuotaExceeded && !entry.Expired) break;
                    if (initialQuotaExceeded && !entry.Expired
                        && totalBytes <= _options.MaxTotalBytes
                        && (!NeedsFreeSpaceCheck || freeBytes >= _options.MinFreeBytes))
                    {
                        break;
                    }

                    try
                    {
                        if (Directory.Exists(entry.Path))
                            Directory.Delete(entry.Path, recursive: true);
                        if (_options.DeleteMetadata && entry.Session is not null)
                            await _store.DeleteSessionAsync(entry.Session.Id, token);

                        deletedSessions++;
                        deletedBytes += entry.Bytes;
                        totalBytes -= entry.Bytes;
                        freeBytes += entry.Bytes;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                    {
                        warnings.Add($"无法删除 {entry.Id}: {ex.Message}");
                    }
                }
            }

            var remaining = Scan(sessionById, active, now, token);
            var remainingMetadata = SumMetadataBytes(token);
            var remainingFree = GetFreeBytes();
            var remainingTotal = remaining.Sum(item => item.Bytes);
            var quotaExceeded = IsOverQuota(remainingTotal, remainingFree);

            return new LiveStorageReport(
                now,
                enforce,
                remainingTotal,
                remainingMetadata,
                remainingFree,
                _options.MaxTotalBytes,
                _options.MinFreeBytes,
                remaining.Count,
                remaining.Count(item => item.Protected),
                expired.Length,
                deletedSessions,
                deletedBytes,
                quotaExceeded,
                warnings.Take(20).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<StorageEntry> Scan(
        IReadOnlyDictionary<string, LiveSession> sessions,
        IReadOnlySet<string> active,
        DateTimeOffset now,
        CancellationToken token)
    {
        var result = new List<StorageEntry>();
        if (!Directory.Exists(_bufferRoot)) return result;

        foreach (var directory in Directory.EnumerateDirectories(_bufferRoot))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            sessions.TryGetValue(name, out var session);
            var lastWrite = Directory.GetLastWriteTimeUtc(directory);
            var ageAt = session?.EndedAt ?? lastWrite;
            var isOpen = session?.IsOpen == true;
            var isActive = active.Contains(name);
            // 开放会话即使当前未被 manager 装载，也先保护；它可能等待恢复。
            var isProtected = isActive || isOpen;
            var ended = session is { IsOpen: false } || session is null;
            var expired = !isProtected
                && ended
                && _options.Retention > TimeSpan.Zero
                && now - ageAt > _options.Retention;

            result.Add(new StorageEntry(
                name,
                directory,
                SumDirectoryBytes(directory),
                new DateTimeOffset(lastWrite, TimeSpan.Zero),
                isProtected,
                session,
                expired));
        }

        return result;
    }

    private bool IsOverQuota(long totalBytes, long freeBytes) =>
        (_options.MaxTotalBytes > 0 && totalBytes > _options.MaxTotalBytes)
        || (NeedsFreeSpaceCheck && freeBytes < _options.MinFreeBytes);

    private bool NeedsFreeSpaceCheck => _options.MinFreeBytes > 0;

    private long SumDirectoryBytes(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try { return new FileInfo(path).Length; }
                    catch (IOException) { return 0; }
                    catch (UnauthorizedAccessException) { return 0; }
                });
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private long SumMetadataBytes(CancellationToken token)
    {
        if (_store is not JsonLiveSessionStore jsonStore || !Directory.Exists(jsonStore.Root))
            return 0;

        try
        {
            return Directory.EnumerateFiles(jsonStore.Root, "*.session.json", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(jsonStore.Root, "*.segments.jsonl", SearchOption.TopDirectoryOnly))
                .Concat(Directory.EnumerateFiles(jsonStore.Root, "*.danmaku.jsonl", SearchOption.TopDirectoryOnly))
                .Sum(path =>
                {
                    token.ThrowIfCancellationRequested();
                    try { return new FileInfo(path).Length; }
                    catch (IOException) { return 0; }
                });
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private long GetFreeBytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(_bufferRoot)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed record StorageEntry(
        string Id,
        string Path,
        long Bytes,
        DateTimeOffset LastWrite,
        bool Protected,
        LiveSession? Session,
        bool Expired)
    {
        public DateTimeOffset AgeAt => Session?.EndedAt ?? LastWrite;
    }
}
