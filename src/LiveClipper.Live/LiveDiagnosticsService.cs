using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveClipper.Live;

/// <summary>
/// 一条诊断问题。Kind 是稳定的机器可读分类（corrupt-session / corrupt-segment-line /
/// unreadable-session / unreadable-segments），Path 相对会话存储根目录，SizeBytes 是坏文件
/// 的字节数（读不到时为 null）。诊断端点只返回相对路径，不泄露本机绝对路径。
/// </summary>
public sealed record LiveStoreIssue(string Kind, string Path, int? Line, long? SizeBytes, string Message);

/// <summary>孤儿媒体目录：相对路径 + 文件数 + 字节数；不返回原始绝对路径。</summary>
public sealed record LiveOrphanMediaDirectory(string RelativePath, int Files, long Bytes);

public sealed record LiveStoreDiagnostics(
    string Root,
    int SessionFiles,
    int OpenSessions,
    int CorruptFiles,
    int QuarantinedFiles,
    IReadOnlyList<string> QuarantineFiles,
    IReadOnlyList<LiveStoreIssue> Issues);

public sealed record LiveRoomRegistryDiagnostics(
    string Path,
    bool Loaded,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    IReadOnlyList<string> CorruptBackups);

/// <summary>G7：某个直播房间的弹幕接收队列指标；只来自运行时快照，不落盘。</summary>
public sealed record LiveDanmakuDropDiagnostics(long RoomId, long QueueDropped, int QueueCapacity);

/// <summary>
/// 诊断报告。所有路径字段（Root / Store.Root / Issue.Path / Registry.Path /
/// OrphanMediaDirectories[].RelativePath）都相对会话存储根目录（Root="."），
/// 即只暴露安全字段：相对路径、大小、原因码。
/// </summary>
public sealed record LiveDiagnosticsReport(
    DateTimeOffset CheckedAt,
    string Root,
    LiveStoreDiagnostics Store,
    LiveRoomRegistryDiagnostics Registry,
    LiveStorageReport? Storage,
    IReadOnlyList<LiveRecoveryState> Recovery,
    IReadOnlyList<LiveOrphanMediaDirectory> OrphanMediaDirectories,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<LiveDanmakuDropDiagnostics> DanmakuDropped);

/// <summary>
/// G4：只读诊断端点背后的采集器。会话清单、分片索引、登记簿、隔离区、孤儿媒体目录
/// 各自单独容错，单个文件坏掉不会让整个报告失败。恢复状态来自 G6 服务。
/// </summary>
public sealed class LiveDiagnosticsService
{
    private const int MaxIssues = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly JsonLiveSessionStore _store;
    private readonly JsonLiveRoomRegistry? _registry;
    private readonly LiveStorageJanitor? _storage;
    private readonly LiveRoomRecoveryService? _recovery;
    private readonly Func<IReadOnlyList<LiveStatus>>? _rooms;

    public LiveDiagnosticsService(
        JsonLiveSessionStore store,
        JsonLiveRoomRegistry? registry = null,
        LiveStorageJanitor? storage = null,
        LiveRoomRecoveryService? recovery = null,
        Func<IReadOnlyList<LiveStatus>>? rooms = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry;
        _storage = storage;
        _recovery = recovery;
        _rooms = rooms;
    }

    public async Task<LiveDiagnosticsReport> InspectAsync(CancellationToken token = default)
    {
        var root = _store.Root;
        var issues = new List<LiveStoreIssue>();
        var warnings = new List<string>();
        var sessionIds = new List<string>();
        var sessionFiles = 0;
        var openSessions = 0;
        var corruptFiles = 0;

        if (Directory.Exists(root))
        {
            foreach (var file in Directory
                .EnumerateFiles(root, "*.session.json", SearchOption.TopDirectoryOnly)
                .OrderBy(item => item, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                sessionFiles++;
                var relative = RelativePath(root, file);
                var size = SizeOf(file);
                try
                {
                    var session = JsonSerializer.Deserialize<LiveSession>(
                        await File.ReadAllTextAsync(file, token).ConfigureAwait(false),
                        JsonOptions);
                    if (session is null)
                    {
                        corruptFiles++;
                        AddIssue(issues, new LiveStoreIssue("corrupt-session", relative, null, size, "清单反序列化结果为 null"));
                        continue;
                    }

                    sessionIds.Add(session.Id);
                    if (session.IsOpen) openSessions++;
                }
                catch (JsonException ex)
                {
                    corruptFiles++;
                    AddIssue(issues, new LiveStoreIssue("corrupt-session", relative, null, size, ex.Message));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddIssue(issues, new LiveStoreIssue("unreadable-session", relative, null, size, ex.Message));
                }
            }

            foreach (var sessionId in sessionIds)
            {
                token.ThrowIfCancellationRequested();
                await ScanSegmentsAsync(root, sessionId, issues, token).ConfigureAwait(false);
            }
        }

        var quarantineFiles = ListQuarantine(root);
        var registry = DescribeRegistry(root);
        if (registry.LastError is not null)
            warnings.Add($"房间登记簿损坏：{registry.LastError}（备份：{registry.CorruptBackups.Count} 份）");
        if (corruptFiles > 0)
            warnings.Add($"{corruptFiles} 个会话清单损坏，已移入隔离区等待人工确认");

        var recovery = (_recovery?.Snapshot() ?? Array.Empty<LiveRecoveryState>())
            .Select(item => item.LastError is null
                ? item
                : item with { LastError = Sanitize(item.LastError, root, _storage?.BufferRoot) })
            .ToArray();
        foreach (var state in recovery.Where(item => item.LastError is not null))
            warnings.Add($"房间 {state.RoomId} 恢复失败：{state.LastError}（下次 {state.NextAttemptAt:O}）");

        LiveStorageReport? storage = null;
        if (_storage is not null)
        {
            try
            {
                storage = await _storage.InspectAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"存储统计失败：{Sanitize(ex.Message, root, _storage.BufferRoot)}");
            }

            if (storage is not null && storage.Warnings.Count > 0)
            {
                storage = storage with
                {
                    Warnings = storage.Warnings
                        .Select(item => Sanitize(item, root, _storage.BufferRoot))
                        .ToArray()
                };
            }
        }

        var orphans = _storage is null
            ? Array.Empty<LiveOrphanMediaDirectory>()
            : ListOrphanMediaDirectories(_storage.BufferRoot, sessionIds, root);
        if (orphans.Count > 0)
            warnings.Add($"{orphans.Count} 个媒体目录没有对应会话清单（可能是崩溃遗留）");

        // G7：弹幕接收队列的容量/丢弃计数只存在于运行时 supervisor，不落盘；
        // 这里通过注入的房间快照把"丢了多少"暴露到诊断端点。
        var danmakuDropped = (_rooms?.Invoke() ?? Array.Empty<LiveStatus>())
            .Where(status => status.DanmakuQueueCapacity > 0)
            .Select(status => new LiveDanmakuDropDiagnostics(
                status.RoomId,
                status.DanmakuQueueDropped,
                status.DanmakuQueueCapacity))
            .OrderBy(item => item.RoomId)
            .ToArray();
        foreach (var room in danmakuDropped.Where(item => item.QueueDropped > 0))
            warnings.Add($"房间 {room.RoomId} 弹幕队列已丢弃 {room.QueueDropped} 帧（容量 {room.QueueCapacity}）");

        return new LiveDiagnosticsReport(
            DateTimeOffset.UtcNow,
            ".",
            new LiveStoreDiagnostics(
                ".",
                sessionFiles,
                openSessions,
                corruptFiles,
                quarantineFiles.Count,
                quarantineFiles,
                issues.ToArray()),
            registry,
            storage,
            recovery,
            orphans,
            warnings.ToArray(),
            danmakuDropped);
    }

    private static async Task ScanSegmentsAsync(
        string root,
        string sessionId,
        List<LiveStoreIssue> issues,
        CancellationToken token)
    {
        if (issues.Count >= MaxIssues) return;
        var path = Path.Combine(root, sessionId + ".segments.jsonl");
        if (!File.Exists(path)) return;
        var relative = RelativePath(root, path);
        var size = SizeOf(path);
        try
        {
            var lines = await File.ReadAllLinesAsync(path, token).ConfigureAwait(false);
            for (var index = 0; index < lines.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index])) continue;
                try
                {
                    _ = JsonSerializer.Deserialize<SegmentArtifact>(lines[index], JsonOptions);
                }
                catch (JsonException ex)
                {
                    AddIssue(issues, new LiveStoreIssue("corrupt-segment-line", relative, index + 1, size, ex.Message));
                    if (issues.Count >= MaxIssues) return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddIssue(issues, new LiveStoreIssue("unreadable-segments", relative, null, size, ex.Message));
        }
    }

    private LiveRoomRegistryDiagnostics DescribeRegistry(string root)
    {
        if (_registry is null)
            return new LiveRoomRegistryDiagnostics("", false, null, null, Array.Empty<string>());

        var path = _registry.FilePath;
        var directory = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileName(path);
        var backups = Directory.Exists(directory)
            ? Directory
                .EnumerateFiles(directory, fileName + ".corrupt-*", SearchOption.TopDirectoryOnly)
                .Select(item => Path.GetFileName(item))
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        return new LiveRoomRegistryDiagnostics(
            RelativePath(root, path),
            _registry.Loaded,
            _registry.LastError is null ? null : Sanitize(_registry.LastError, root),
            _registry.LastErrorAt,
            backups);
    }

    private static IReadOnlyList<string> ListQuarantine(string root)
    {
        var directory = Path.Combine(root, "quarantine");
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(item => Path.GetFileName(item))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<LiveOrphanMediaDirectory> ListOrphanMediaDirectories(
        string bufferRoot,
        IReadOnlyList<string> sessionIds,
        string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(bufferRoot) || !Directory.Exists(bufferRoot))
            return Array.Empty<LiveOrphanMediaDirectory>();

        var known = sessionIds.ToHashSet(StringComparer.Ordinal);
        var orphans = new List<LiveOrphanMediaDirectory>();
        foreach (var directory in Directory.EnumerateDirectories(bufferRoot))
        {
            var name = Path.GetFileName(directory);
            if (known.Contains(name)) continue;
            var files = 0;
            long bytes = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    files++;
                    try { bytes += new FileInfo(file).Length; }
                    catch { /* 读不到大小不影响诊断 */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录被占用/权限不足时仍报告它，只是文件数可能不完整。
            }

            orphans.Add(new LiveOrphanMediaDirectory(RelativePath(baseDirectory, directory), files, bytes));
        }

        return orphans
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>把绝对路径换成相对会话存储根目录的路径；不同卷等无法相对化时退化为文件名。</summary>
    private static string RelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".") return ".";
        if (Path.IsPathRooted(relative)) return Path.GetFileName(path);
        return relative.Replace('\\', '/');
    }

    private static long? SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    /// <summary>错误文本里可能带绝对路径（例如 IOException.Message），替换成相对路径再返回。</summary>
    private static string Sanitize(string message, string baseDirectory, params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalized.Length == 0) continue;
            message = message.Replace(normalized, RelativePath(baseDirectory, normalized), StringComparison.OrdinalIgnoreCase);
        }

        return message;
    }

    private static void AddIssue(List<LiveStoreIssue> issues, LiveStoreIssue issue)
    {
        if (issues.Count < MaxIssues) issues.Add(issue);
    }
}
