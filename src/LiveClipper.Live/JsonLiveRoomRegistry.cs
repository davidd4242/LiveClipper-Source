using System.Text;
using System.Text.Json;

namespace LiveClipper.Live;

/// <summary>
/// 房间登记簿：把用户选择监控过的房间保存在本地 JSON 中。
/// Active 表示上次退出前仍在监控，启动时据此恢复；停止监控只置 Active=false，
/// 房间仍留在登记簿中，用户可在页面上再次启动或显式移除。
/// </summary>
public sealed class JsonLiveRoomRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, LiveRoomRegistration> _items = [];
    private bool _loaded;

    public JsonLiveRoomRegistry(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("登记簿路径不能为空", nameof(path));
        _path = System.IO.Path.GetFullPath(path);
    }

    public string FilePath => _path;

    /// <summary>是否已尝试加载过登记簿；损坏也算"已加载"，但会记下 LastError。</summary>
    public bool Loaded => _loaded;

    /// <summary>最近一次加载失败的原始错误；诊断端点/日志读取，成功时清空。</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset? LastErrorAt { get; private set; }

    public async Task<IReadOnlyList<LiveRoomRegistration>> ListAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnlockedAsync(token).ConfigureAwait(false);
            return _items.Values
                .OrderBy(item => item.RoomId)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetActiveAsync(long roomId, bool active, CancellationToken token = default, bool? autoRecord = null)
    {
        if (roomId <= 0) throw new ArgumentOutOfRangeException(nameof(roomId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnlockedAsync(token).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            _items[roomId] = _items.TryGetValue(roomId, out var existing)
                ? existing with { Active = active, UpdatedAt = now, AutoRecord = autoRecord ?? existing.AutoRecord }
                : new LiveRoomRegistration(roomId, active, now, now, AutoRecord: autoRecord ?? false);
            await SaveUnlockedAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(long roomId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnlockedAsync(token).ConfigureAwait(false);
            if (!_items.Remove(roomId)) return false;
            await SaveUnlockedAsync(token).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedUnlockedAsync(CancellationToken token)
    {
        if (_loaded) return;
        if (!File.Exists(_path))
        {
            _loaded = true;
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<RegistryDocument>(
                await File.ReadAllTextAsync(_path, token).ConfigureAwait(false),
                JsonOptions);
            foreach (var item in document?.Rooms ?? [])
            {
                if (item.RoomId > 0) _items[item.RoomId] = item;
            }
            _loaded = true;
        }
        catch (JsonException ex)
        {
            _loaded = true;
            LastError = ex.Message;
            LastErrorAt = DateTimeOffset.UtcNow;
            BackupCorruptFile(ex);
        }
    }

    /// <summary>
    /// 更新某个房间的恢复状态（失败次数/下次重试时间/最后一次错误）。
    /// 房间不在登记簿里时静默忽略，避免把已移除的房间又写回来。
    /// </summary>
    public async Task UpdateRecoveryAsync(
        long roomId,
        DateTimeOffset? attemptedAt,
        int failures,
        DateTimeOffset? nextRetryAt,
        string? lastError,
        CancellationToken token = default)
    {
        if (roomId <= 0) throw new ArgumentOutOfRangeException(nameof(roomId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnlockedAsync(token).ConfigureAwait(false);
            if (!_items.TryGetValue(roomId, out var existing)) return;
            _items[roomId] = existing with
            {
                LastRecoveryAttemptAt = attemptedAt ?? existing.LastRecoveryAttemptAt,
                RecoveryFailures = failures,
                NextRecoveryAt = nextRetryAt,
                LastRecoveryError = lastError,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await SaveUnlockedAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void BackupCorruptFile(JsonException error)
    {
        var backup = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        try
        {
            File.Copy(_path, backup, overwrite: false);
            Console.Error.WriteLine($"room registry quarantined invalid file {_path} -> {backup}: {error.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"room registry backup failed for {_path}: {ex.Message}");
        }
    }

    private async Task SaveUnlockedAsync(CancellationToken token)
    {
        var directory = System.IO.Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("登记簿路径没有目录");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new RegistryDocument
        {
            Version = 1,
            Rooms = _items.Values.OrderBy(item => item.RoomId).ToList()
        }, JsonOptions);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, json, Encoding.UTF8, token).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    private sealed class RegistryDocument
    {
        public int Version { get; set; } = 1;
        public List<LiveRoomRegistration> Rooms { get; set; } = [];
    }
}

/// <summary>一个已登记房间；Active 只表示“上次退出时仍在监控”，不是实时探测状态。</summary>
public sealed record LiveRoomRegistration(
    long RoomId,
    bool Active,
    DateTimeOffset AddedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRecoveryAttemptAt = null,
    int RecoveryFailures = 0,
    DateTimeOffset? NextRecoveryAt = null,
    string? LastRecoveryError = null,
    bool AutoRecord = false);
