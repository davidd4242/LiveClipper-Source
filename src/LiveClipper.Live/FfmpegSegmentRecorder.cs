using System.Diagnostics;
using System.Globalization;

namespace LiveClipper.Live;

public sealed record FfmpegRecorderOptions
{
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    /// <summary>B 站 CDN 用 HTTPS；部分 Windows FFmpeg 构建没有内置 CA，需要显式 cacert.pem。</summary>
    public string? CaBundlePath { get; init; }
    public string StorageRoot { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveClipper", "live");
    public double SegmentSeconds { get; init; } = 4;
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>恢复时估算未索引分片的时长；测试可注入。null 时用 ffprobe，失败退回 SegmentSeconds。</summary>
    public Func<string, CancellationToken, Task<double>>? DurationProbe { get; init; }
}

/// <summary>
/// 用 FFmpeg 把直播流切成独立 MPEG-TS 分片，并在分片稳定后通过 onSegment 交给
/// supervisor 落库。分片文件本身就是恢复依据，元数据由 supervisor 写 JSONL；
/// 这样进程崩溃后不会出现“文件存在但数据库不知道”的静默丢失。
/// </summary>
public sealed class FfmpegSegmentRecorder : ISegmentRecorder, ISegmentRecovery
{
    private readonly FfmpegRecorderOptions _options;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string? _caBundle;

    public FfmpegSegmentRecorder(FfmpegRecorderOptions? options = null)
    {
        _options = options ?? new FfmpegRecorderOptions();
        _ffmpeg = ResolveTool(_options.FfmpegPath, "ffmpeg.exe", "ffmpeg");
        _ffprobe = ResolveTool(_options.FfprobePath, "ffprobe.exe", "ffprobe");
        _caBundle = ResolveCaBundle(_options.CaBundlePath);

        if (_options.SegmentSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "分片时长必须至少 1 秒");
    }

    public string FfmpegPath => _ffmpeg;
    public string FfprobePath => _ffprobe;
    public string? CaBundlePath => _caBundle;

    public async Task RunAsync(
        CaptureContext context,
        Func<SegmentArtifact, CancellationToken, Task> onSegment,
        CancellationToken token)
    {
        var runDirectory = Path.Combine(
            _options.StorageRoot,
            "buffer",
            Sanitize(context.Session.Id),
            "run_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Directory.CreateDirectory(runDirectory);

        var process = StartFfmpeg(context, runDirectory);
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = new IngestState { Sequence = context.Session.LastSequence, LastEnd = TimeSpan.Zero };
        var lastFileChange = DateTimeOffset.UtcNow;
        var lastFileCount = 0;
        var canceled = false;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                var files = Directory.GetFiles(runDirectory, "segment_*.ts")
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();

                var completed = process.HasExited ? files : files.SkipLast(1).ToArray();
                foreach (var file in completed)
                {
                    var artifact = await IngestAsync(file, context, seen, state, token);
                    if (artifact is null) continue;
                    lastFileChange = DateTimeOffset.UtcNow;
                    await onSegment(artifact, token);
                }

                if (process.HasExited) break;

                if (files.Length != lastFileCount)
                {
                    lastFileCount = files.Length;
                    lastFileChange = DateTimeOffset.UtcNow;
                }

                if (DateTimeOffset.UtcNow - lastFileChange > _options.StallTimeout)
                {
                    throw new TimeoutException(
                        $"FFmpeg {_options.StallTimeout.TotalSeconds:0} 秒没有产生新分片，准备重连");
                }

                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            canceled = true;
        }
        finally
        {
            await StopFfmpegAsync(process, _options.StopTimeout);
            var stderr = await SafeReadAsync(stderrTask);
            _ = await SafeReadAsync(stdoutTask);

            // 停止时把 FFmpeg 已写完但尚未轮询到的分片补录，避免最后几秒丢元数据。
            try
            {
                var files = Directory.GetFiles(runDirectory, "segment_*.ts")
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                foreach (var file in files)
                {
                    var artifact = await IngestAsync(
                        file, context, seen, state, CancellationToken.None);
                    if (artifact is not null)
                        await onSegment(artifact, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                if (!canceled) throw new InvalidOperationException("收尾分片失败：" + ex.Message, ex);
            }

            var exitCode = TryGetExitCode(process);
            process.Dispose();

            if (!canceled && exitCode is not null and not 0)
                throw new InvalidOperationException(
                    $"FFmpeg 退出 ({exitCode}): {Tail(stderr, 2400)}");
        }
    }

    /// <summary>
    /// 进程崩溃后的恢复入口：扫描本会话媒体目录，返回“文件存在但索引里没有”的分片。
    /// 只读磁盘、不删除文件；同一会话重复调用是幂等的（已索引路径会被跳过）。
    /// </summary>
    public async Task<IReadOnlyList<SegmentArtifact>> RecoverOrphansAsync(
        string sessionId,
        IReadOnlySet<string> indexedPaths,
        int afterSequence,
        TimeSpan startOffset,
        CancellationToken token)
    {
        var sessionDirectory = Path.Combine(_options.StorageRoot, "buffer", Sanitize(sessionId));
        if (!Directory.Exists(sessionDirectory)) return Array.Empty<SegmentArtifact>();

        var indexed = indexedPaths
            .Select(path => Path.GetFullPath(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 分片目录形如 run_<unix-ms>，按名字排序即按录制轮次排序；同一轮内按文件名排序。
        var files = new List<string>();
        var runDirectories = Directory.EnumerateDirectories(sessionDirectory, "run_*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (var runDirectory in runDirectories)
        {
            foreach (var file in Directory.EnumerateFiles(runDirectory, "segment_*.ts")
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (!indexed.Contains(Path.GetFullPath(file))) files.Add(file);
            }
        }

        // 兼容旧布局：分片直接放在会话目录下（没有 run_* 子目录）。
        foreach (var file in Directory.EnumerateFiles(sessionDirectory, "segment_*.ts")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!indexed.Contains(Path.GetFullPath(file))) files.Add(file);
        }

        var recovered = new List<SegmentArtifact>(files.Count);
        var sequence = afterSequence;
        var end = startOffset;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0) continue;

            var durationSeconds = await ProbeRecoveredDurationAsync(file, token);
            if (durationSeconds <= 0) durationSeconds = _options.SegmentSeconds;
            var duration = TimeSpan.FromSeconds(durationSeconds);

            sequence++;
            recovered.Add(new SegmentArtifact(
                sessionId,
                sequence,
                end,
                duration,
                info.Length,
                file,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            end += duration;
        }

        return recovered;
    }

    private async Task<double> ProbeRecoveredDurationAsync(string path, CancellationToken token)
    {
        try
        {
            if (_options.DurationProbe is not null) return await _options.DurationProbe(path, token);
            return await ProbeDurationAsync(path, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // ffprobe 缺失或文件损坏时退回固定分片时长，恢复不能因此失败。
            return 0;
        }
    }

    /// <summary>
    /// G8：FFmpeg 输入侧全局参数（与 <c>-i</c> 同级）。抽成静态方法便于离线测试 HLS 参数，
    /// 也保证 flv 路径的既有参数不变。
    /// </summary>
    public static IReadOnlyList<string> BuildInputArguments(StreamSource stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-rw_timeout", "15000000"
        };
        if (stream.Uri.IsFile) return arguments;

        // HLS（m3u8 内引用 ts 分片）需要放开 demuxer 的协议/扩展名白名单，
        // 否则精简版 FFmpeg 会以 "Protocol 'segment' not on whitelist" 退出。
        if (stream.Format.Equals("hls", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(
            [
                "-protocol_whitelist", "file,http,https,tcp,tls,crypto",
                "-allowed_extensions", "ALL"
            ]);
        return arguments;
    }

    private Process StartFfmpeg(CaptureContext context, string runDirectory)
    {
        var arguments = new List<string>(BuildInputArguments(context.Stream));
        var isFile = context.Stream.Uri.IsFile;
        if (_caBundle is not null && !isFile)
        {
            arguments.Add("-ca_file");
            arguments.Add(_caBundle);
        }

        var target = new List<string>
        {
            "-i", InputTarget(context.Stream.Uri),
            "-map", "0",
            "-c", "copy",
            "-f", "segment",
            "-segment_time", _options.SegmentSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-segment_format", "mpegts",
            "-reset_timestamps", "1",
            Path.Combine(runDirectory, "segment_%05d.ts")
        };
        if (!isFile)
        {
            // 本地文件输入不需要 Referer/UA，部分 Windows 版 FFmpeg 也不接受这些协议选项。
            arguments.AddRange(["-headers", "Referer: https://live.bilibili.com/\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) LiveClipper/0.2\r\n"]);
        }

        arguments.AddRange(target);

        var info = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            return Process.Start(info)
                ?? throw new InvalidOperationException("无法启动 FFmpeg 进程");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"无法启动 FFmpeg（{_ffmpeg}）：{ex.Message}。可通过 LIVECLIPPER_FFMPEG 指定路径。", ex);
        }
    }

    /// <summary>
    /// 采集起点到现在的经过时间。优先使用共享 <see cref="CaptureContext.Clock"/>，
    /// 让分片 offset 与弹幕 offset 用同一个零点；缺失时钟时回退旧行为。
    /// </summary>
    public static TimeSpan ElapsedSinceCapture(CaptureContext context)
    {
        if (context.Clock is { } clock) return clock.Elapsed;
        var elapsed = DateTimeOffset.UtcNow - context.Session.StartedAt;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>
    /// 分片在成片时间线上的起点：取「上一分片结束」与「采集起点至今减去本片时长」的较大值。
    /// 与弹幕 offset 共用 <see cref="ElapsedSinceCapture"/>，所以两路基准一致。
    /// </summary>
    public static TimeSpan SegmentOffset(TimeSpan lastEnd, TimeSpan elapsed, TimeSpan duration)
    {
        var offsetSeconds = Math.Max(lastEnd.TotalSeconds, elapsed.TotalSeconds - duration.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(0, offsetSeconds));
    }

    /// <summary>
    /// FFmpeg 的输入目标。http(s) 流直接使用 URI；本地文件（离线测试/本地回放）
    /// 用本地路径，避免部分 Windows 版 FFmpeg 不接受 file:/// 形式。
    /// </summary>
    public static string InputTarget(Uri uri) => uri.IsFile ? uri.LocalPath : uri.ToString();

    private async Task<SegmentArtifact?> IngestAsync(
        string path,
        CaptureContext context,
        HashSet<string> seen,
        IngestState state,
        CancellationToken token)
    {
        if (!seen.Add(path)) return null;
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) return null;

        var durationSeconds = await ProbeDurationAsync(path, token);
        if (durationSeconds <= 0) durationSeconds = _options.SegmentSeconds;
        var duration = TimeSpan.FromSeconds(durationSeconds);

        var offset = SegmentOffset(state.LastEnd, ElapsedSinceCapture(context), duration);
        state.LastEnd = offset + duration;
        state.Sequence++;

        return new SegmentArtifact(
            context.Session.Id,
            state.Sequence,
            offset,
            duration,
            file.Length,
            path,
            DateTimeOffset.UtcNow);
    }

    private async Task<double> ProbeDurationAsync(string path, CancellationToken token)
    {
        var info = new ProcessStartInfo
        {
            FileName = _ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-v");
        info.ArgumentList.Add("error");
        info.ArgumentList.Add("-show_entries");
        info.ArgumentList.Add("format=duration");
        info.ArgumentList.Add("-of");
        info.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        info.ArgumentList.Add(path);

        using var process = Process.Start(info);
        if (process is null) return 0;
        var output = await process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 0;
    }

    private static async Task StopFfmpegAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("q");
                    await process.StandardInput.FlushAsync();
                }
                catch { }

                using var wait = new CancellationTokenSource(timeout);
                try { await process.WaitForExitAsync(wait.Token); }
                catch (OperationCanceledException) { }
            }

            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { }
                try { await process.WaitForExitAsync(); } catch { }
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出并释放，忽略。
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task; }
        catch { return ""; }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    private static string Tail(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[^maxLength..];

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private static string? ResolveCaBundle(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured)
                ? configured
                : throw new FileNotFoundException($"找不到配置的 CA 文件：{configured}");

        var environment = Environment.GetEnvironmentVariable("LIVECLIPPER_CA_BUNDLE");
        if (!string.IsNullOrWhiteSpace(environment) && File.Exists(environment))
            return environment;

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "certs", "cacert.pem"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveClipper", "certs", "cacert.pem"),
            @"C:\Program Files\Git\mingw64\etc\ssl\certs\ca-bundle.crt",
            @"C:\Program Files\Git\usr\ssl\certs\ca-bundle.crt"
        };

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        try
        {
            foreach (var pythonRoot in Directory.EnumerateDirectories(appData, "Python*"))
            {
                candidates.Add(Path.Combine(
                    pythonRoot, "site-packages", "certifi", "cacert.pem"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 没有 Python 目录时继续用其他候选。
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ResolveTool(string? configured, string fileName, string command)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return configured;
            throw new FileNotFoundException($"找不到配置的可执行文件：{configured}");
        }

        var environment = Environment.GetEnvironmentVariable(
            fileName.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase)
                ? "LIVECLIPPER_FFMPEG"
                : "LIVECLIPPER_FFPROBE");
        if (!string.IsNullOrWhiteSpace(environment) && File.Exists(environment))
            return environment;

        foreach (var root in CandidateRoots())
        {
            var path = Path.Combine(root, fileName);
            if (File.Exists(path)) return path;
            if (!Directory.Exists(root)) continue;

            try
            {
                var discovered = Directory
                    .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (discovered is not null) return discovered;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 某个候选目录不可读时继续找下一个。
            }
        }

        return command;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "LiveClipper", "runtime", "ffmpeg");
        yield return Path.Combine(local, "LiveClipper", "tools", "ffmpeg");
    }

    private sealed class IngestState
    {
        public int Sequence { get; set; }
        public TimeSpan LastEnd { get; set; }
    }
}
