using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveClipper.Live;

/// <summary>
/// 最小但可恢复的本地存储：每个逻辑会话一个 JSON 清单，分片/弹幕各自追加
/// JSONL。写清单用临时文件 + 原子替换，避免进程在写一半时留下坏状态。
/// </summary>
public sealed class JsonLiveSessionStore : ILiveSessionStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonLiveSessionStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("存储目录不能为空", nameof(root));
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public Task<IReadOnlyList<LiveSession>> LoadOpenSessionsAsync(CancellationToken token) =>
        LoadSessionsAsync(openOnly: true, token);

    public Task<IReadOnlyList<LiveSession>> LoadAllSessionsAsync(CancellationToken token) =>
        LoadSessionsAsync(openOnly: false, token);

    private async Task<IReadOnlyList<LiveSession>> LoadSessionsAsync(bool openOnly, CancellationToken token)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            var result = new List<LiveSession>();
            if (!Directory.Exists(_root)) return result;
            var corrupt = new List<string>();
            foreach (var file in Directory.EnumerateFiles(_root, "*.session.json", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var session = JsonSerializer.Deserialize<LiveSession>(
                        await File.ReadAllTextAsync(file, token),
                        JsonOptions);
                    if (session is not null && (!openOnly || session.IsOpen)) result.Add(session);
                    else if (session is null) corrupt.Add(file);
                }
                catch (JsonException)
                {
                    // 坏清单不阻塞整场恢复；移入隔离区，防止下一次保存把它静默覆盖。
                    corrupt.Add(file);
                }
            }

            foreach (var file in corrupt) QuarantineFile(file);
            return result.OrderBy(x => x.StartedAt).ToArray();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<LiveSession?> GetSessionAsync(string id, CancellationToken token)
    {
        var path = SessionFile(id);
        if (!File.Exists(path)) return null;
        await _writeGate.WaitAsync(token);
        try
        {
            if (!File.Exists(path)) return null;
            var session = JsonSerializer.Deserialize<LiveSession>(
                await File.ReadAllTextAsync(path, token),
                JsonOptions);
            if (session is null) QuarantineFile(path);
            return session;
        }
        catch (JsonException)
        {
            QuarantineFile(path);
            return null;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<SegmentArtifact>> LoadSegmentsAsync(
        string sessionId,
        CancellationToken token)
    {
        var path = SessionFile(sessionId, ".segments.jsonl");
        if (!File.Exists(path)) return Array.Empty<SegmentArtifact>();

        await _writeGate.WaitAsync(token);
        try
        {
            if (!File.Exists(path)) return Array.Empty<SegmentArtifact>();
            var result = new List<SegmentArtifact>();
            foreach (var line in await File.ReadAllLinesAsync(path, token))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var artifact = JsonSerializer.Deserialize<SegmentArtifact>(line, LineOptions);
                    if (artifact is not null) result.Add(artifact);
                }
                catch (JsonException)
                {
                    // 崩溃可能留下半行；跳过坏行即可，不阻塞恢复，也不重写原文件。
                }
            }
            return result.OrderBy(item => item.Sequence).ToArray();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<DanmakuFrame>> LoadDanmakuAsync(
        string sessionId,
        CancellationToken token)
    {
        var path = SessionFile(sessionId, ".danmaku.jsonl");
        if (!File.Exists(path)) return Array.Empty<DanmakuFrame>();

        await _writeGate.WaitAsync(token);
        try
        {
            if (!File.Exists(path)) return Array.Empty<DanmakuFrame>();
            var result = new List<DanmakuFrame>();
            foreach (var line in await File.ReadAllLinesAsync(path, token))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var frame = JsonSerializer.Deserialize<DanmakuFrame>(line, LineOptions);
                    if (frame is not null) result.Add(frame);
                }
                catch (JsonException)
                {
                    // 崩溃可能留下半行；跳过坏行，弹幕是柔性数据，不阻塞恢复。
                }
            }
            return result
                .OrderBy(frame => frame.Offset)
                .ThenBy(frame => frame.SentAt)
                .ToArray();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SaveSessionAsync(LiveSession session, CancellationToken token)
    {
        var path = SessionFile(session.Id);
        var json = JsonSerializer.Serialize(session, JsonOptions);
        await _writeGate.WaitAsync(token);
        try
        {
            await WriteAtomicAsync(path, json, token);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task AppendSegmentAsync(SegmentArtifact segment, CancellationToken token)
    {
        var path = SessionFile(segment.SessionId, ".segments.jsonl");
        var line = JsonSerializer.Serialize(segment, LineOptions) + "\n";
        await AppendAsync(path, line, token);
    }

    public async Task AppendDanmakuAsync(IReadOnlyList<DanmakuFrame> frames, CancellationToken token)
    {
        if (frames.Count == 0) return;
        var sessionId = frames[0].SessionId;
        var builder = new StringBuilder(frames.Count * 160);
        foreach (var frame in frames)
        {
            if (!string.Equals(frame.SessionId, sessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("同一批弹幕不能属于不同会话");
            builder.Append(JsonSerializer.Serialize(frame, LineOptions)).Append('\n');
        }
        await AppendAsync(SessionFile(sessionId, ".danmaku.jsonl"), builder.ToString(), token);
    }

    public async Task DeleteSessionAsync(string id, CancellationToken token)
    {
        var files = new[]
        {
            SessionFile(id),
            SessionFile(id, ".segments.jsonl"),
            SessionFile(id, ".danmaku.jsonl")
        };
        await _writeGate.WaitAsync(token);
        try
        {
            foreach (var file in files)
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<long> CountDanmakuAsync(string sessionId, CancellationToken token)
    {
        var path = SessionFile(sessionId, ".danmaku.jsonl");
        if (!File.Exists(path)) return 0;
        long count = 0;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(token) is not null) count++;
        return count;
    }

    private async Task AppendAsync(string path, string content, CancellationToken token)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("路径没有目录");
        Directory.CreateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, Encoding.UTF8, token);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    /// <summary>坏清单隔离目录；诊断端点只读列出，不做清理。</summary>
    public string QuarantineDirectory => Path.Combine(_root, "quarantine");

    private void QuarantineFile(string path)
    {
        try
        {
            Directory.CreateDirectory(QuarantineDirectory);
            var target = Path.Combine(
                QuarantineDirectory,
                Path.GetFileName(path) + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"));
            File.Move(path, target, overwrite: true);
            Console.Error.WriteLine($"live session store quarantined invalid file {path} -> {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"live session store failed to quarantine {path}: {ex.Message}");
        }
    }

    private string SessionFile(string id, string suffix = ".session.json")
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("会话 ID 不能为空", nameof(id));
        if (!string.Equals(id, Path.GetFileName(id), StringComparison.Ordinal)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("会话 ID 含有文件名非法字符", nameof(id));
        if (id.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("会话 ID 不能包含 ..", nameof(id));
        if (suffix != ".session.json" && !suffix.StartsWith('.'))
            throw new ArgumentException("会话文件后缀非法", nameof(suffix));
        return Path.Combine(_root, id + suffix);
    }

    public void Dispose() => _writeGate.Dispose();
}
