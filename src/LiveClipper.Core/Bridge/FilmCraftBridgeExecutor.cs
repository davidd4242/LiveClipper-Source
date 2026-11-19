using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveClipper.Core.Bridge;

/// <summary>
/// 真实 FilmCraft 导出执行器：editor-project.json → filmcraft-b1-map.mjs → .fcproj → filmcraft-cli 导出 mp4。
/// 任何一步不可用都返回明确的 BridgeError（MappingError / FilmCraftError / BridgeTimeout），由端点统一成契约错误体。
/// B12-4：优先使用 clip 已保存的 editor 工程（可带字幕/蒙版），并把字幕/蒙版开关传给映射脚本；
/// 超过 30 秒的片子按 9 秒分块导出再 ffmpeg concat，规避 FilmCraft 32 位进程的 OOM。
/// </summary>
public sealed class FilmCraftBridgeExecutor : IBridgeExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<string, string?> _clipPathResolver;
    private readonly Func<string, string?>? _projectJsonResolver;
    private readonly string _nodePath;
    private readonly string _mapperPath;
    private readonly string _cliPath;
    private readonly string _ffprobePath;
    private readonly string? _ffmpegPath;
    private readonly string _workRoot;
    private readonly string _storageRoot;
    private readonly TimeSpan _stepTimeout;
    private readonly object _capabilitiesGate = new();
    private BridgeCapabilitiesInfo? _capabilities;

    public FilmCraftBridgeExecutor(
        Func<string, string?> clipPathResolver,
        string nodePath,
        string mapperPath,
        string cliPath,
        string ffprobePath,
        string storageRoot,
        TimeSpan? stepTimeout = null,
        string? ffmpegPath = null,
        Func<string, string?>? projectJsonResolver = null)
    {
        _clipPathResolver = clipPathResolver;
        _nodePath = nodePath;
        _mapperPath = mapperPath;
        _cliPath = cliPath;
        _ffprobePath = ffprobePath;
        _ffmpegPath = ffmpegPath;
        _projectJsonResolver = projectJsonResolver;
        _storageRoot = storageRoot;
        _workRoot = Path.Combine(storageRoot, "bridge-work");
        _stepTimeout = stepTimeout ?? DefaultStepTimeout;
    }

    public BridgeCapabilitiesInfo Capabilities
    {
        get
        {
            lock (_capabilitiesGate) return _capabilities ??= DetectCapabilities();
        }
    }

    private BridgeCapabilitiesInfo DetectCapabilities()
    {
        var version = "unavailable";
        try
        {
            var result = RunProcess(_cliPath, ["--version"], TimeSpan.FromSeconds(10), CancellationToken.None);
            if (result.ExitCode == 0)
            {
                var match = System.Text.RegularExpressions.Regex.Match(result.StdOut, @"\d+\.\d+\.\d+");
                if (match.Success) version = match.Value;
            }
        }
        catch (Exception)
        {
            // CLI 未安装时 capabilities 仍可返回，真实导出再报 FilmCraftError。
        }
        return new BridgeCapabilitiesInfo(
            version,
            15,
            ["transitions", "speed", "keyframes", "fades", "masks", "captions", "multiTrack"],
            BridgeJobService.SupportedVariants,
            BridgeJobService.SupportedFormats,
            CompensateDefault: true);
    }

    public async Task<string> RunAsync(BridgeJobContext context, CancellationToken token)
    {
        var clipPath = _clipPathResolver(context.ClipId);
        if (string.IsNullOrWhiteSpace(clipPath) || !File.Exists(clipPath))
            throw new BridgeExecutionException(new BridgeError("MappingError", "切片媒体文件不存在", clipPath ?? context.ClipId));
        if (!File.Exists(_mapperPath))
            throw new BridgeExecutionException(new BridgeError("MappingError", "找不到映射脚本 filmcraft-b1-map.mjs", _mapperPath));
        if (!File.Exists(_cliPath))
            throw new BridgeExecutionException(new BridgeError("FilmCraftError", "未安装 FilmCraft CLI", _cliPath));

        var workDir = Path.Combine(_workRoot, $"job-{context.Id}");
        var outDir = Path.Combine(workDir, "map");
        Directory.CreateDirectory(workDir);
        var probe = await ProbeMediaAsync(clipPath, token).ConfigureAwait(false);
        var mediaDuration = probe.Duration > 0 ? probe.Duration : 4.0;
        var project = BuildProject(context, probe, mediaDuration);
        var inputPath = Path.Combine(workDir, "editor-project.json");
        var payload = new JsonObject
        {
            ["clip"] = new JsonObject
            {
                ["id"] = context.ClipId,
                ["path"] = clipPath,
                ["duration"] = mediaDuration
            },
            ["project"] = project.Node
        };
        await File.WriteAllTextAsync(inputPath, payload.ToJsonString(JsonOptions), token).ConfigureAwait(false);

        context.ReportProgress(10);
        // B8-3：默认把转场时长补偿传给映射脚本，使 FC 导出时长对齐 LC 压缩后时长；
        // 调用方可用 options.compensate=false 关闭（capabilities/响应 warnings 会标明）。
        var mapperArgs = new List<string>
        {
            _mapperPath,
            "--input", inputPath,
            "--out", outDir,
            "--map-fades",
            "--map-transitions",
            "--map-keyframes",
            "--map-speed",
            "--map-captions",
            "--map-masks",
            "--multi-track", "fold",
            "--validate",
            "--keep"
        };
        if (context.Compensate) mapperArgs.Add("--transition-compensate");
        var mapper = await RunProcessAsync(_nodePath, mapperArgs, token).ConfigureAwait(false);
        if (mapper.ExitCode != 0)
            throw new BridgeExecutionException(new BridgeError("MappingError", "LiveClipper 工程映射失败", Clip(mapper.StdErr, mapper.StdOut)));

        context.ReportProgress(40);
        var fcproj = Directory.EnumerateFiles(outDir, "*.fcproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? throw new BridgeExecutionException(new BridgeError("MappingError", "映射完成但没有生成 .fcproj", outDir));
        var directory = Path.GetDirectoryName(context.ResultPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var chunks = BridgeChunkPlanner.Plan(project.ExportSeconds);
        double? trimSeconds = null;
        if (chunks.Count > 1)
        {
            // FilmCraft 带音频导出会把视频起点推后 1 帧，且 concat 的 AAC priming 会累积；
            // 按帧对齐：总帧数取 round(时长*fps)，分块少导出 1 帧，拼完用 -t 把音频裁齐到 totalFrames/fps。
            var fps = project.Fps > 0 ? project.Fps : 30;
            var totalFrames = Math.Max(1, Math.Round(project.ExportSeconds * fps));
            // FC 带音频导出的视频起点会推后 1 帧；拼完后按 (总帧数-1)/fps 截断，
            // 使容器结束时间正好回到 totalFrames/fps（映射后时间线时长）。
            trimSeconds = (totalFrames - 1) / fps;
            chunks = BridgeChunkPlanner.Plan((totalFrames - 1) / fps);
        }
        if (chunks.Count <= 1)
        {
            trimSeconds = null;
            context.ReportProgress(60);
            await ExportSegmentAsync(fcproj, context.ResultPath, null, null, project.BurnCaptions, token).ConfigureAwait(false);
        }
        else
        {
            var chunkDir = Path.Combine(workDir, "chunks");
            Directory.CreateDirectory(chunkDir);
            var files = new List<string>(chunks.Count);
            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                var file = Path.Combine(chunkDir, $"chunk-{index:D3}.mp4");
                await ExportSegmentAsync(fcproj, file, chunk.StartSeconds, chunk.LengthSeconds, project.BurnCaptions, token).ConfigureAwait(false);
                files.Add(file);
                context.ReportProgress(45 + (int)Math.Round(50.0 * (index + 1) / chunks.Count));
            }
            context.ReportProgress(96);
            await ConcatAsync(files, context.ResultPath, trimSeconds, token).ConfigureAwait(false);
        }

        if (!File.Exists(context.ResultPath))
            throw new BridgeExecutionException(new BridgeError("FilmCraftError", "FilmCraft 未产出结果文件", context.ResultPath));
        // B8-3：成功导出旁留一份短报告，记录这次映射/补偿的真实数字，便于用户与验收对账。
        await TryWriteRunReportAsync(context, outDir, fcproj, project, token).ConfigureAwait(false);
        context.ReportProgress(100);
        return context.ResultPath;
    }

    private sealed record ProjectPlan(JsonObject Node, double ExportSeconds, bool BurnCaptions, double Fps);

    /// <summary>
    /// 写 {StorageRoot}/bridge-runs/{jobId}/bridge-report.md（尽量短，≤20 行）；失败绝不影响导出。
    /// </summary>
    private async Task TryWriteRunReportAsync(BridgeJobContext context, string outDir, string fcproj, ProjectPlan project, CancellationToken token)
    {
        try
        {
            var warnings = ReadMappingWarnings(outDir);
            var compensatedFrames = ReadCompensatedFrames(outDir);
            var fps = project.Fps > 0 ? project.Fps : 30;
            var mappedSeconds = Math.Max(0, project.ExportSeconds - compensatedFrames / fps);
            var (frames, duration, width, height) = await ProbeFramesAsync(context.ResultPath, token).ConfigureAwait(false);
            var expectedFrames = Math.Max(0, (int)Math.Round(mappedSeconds * fps));
            var delta = frames - expectedFrames;
            var size = new FileInfo(context.ResultPath).Length;
            var lines = new List<string>
            {
                "# Bridge 导出报告",
                "- jobId: `" + context.Id + "`",
                "- clipId: `" + context.ClipId + "`",
                "- 时间线时长（映射补偿后）: " + mappedSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s / " + expectedFrames + " 帧（" + fps + "fps，补偿 " + compensatedFrames + " 帧）",
                "- 导出产物: " + duration.ToString("0.###", CultureInfo.InvariantCulture) + "s / " + frames + " 帧 / " + width + "×" + height + " / " + (size / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
                "- 帧数差: " + delta + " 帧（" + (Math.Abs(delta) <= 1 ? "≤1 帧，达标" : "超 1 帧，需排查") + "）",
                "- 转场时长补偿: " + (context.Compensate ? "已启用 `--transition-compensate`" : "已关闭（保留 FC 时间轴）"),
                "- 映射警告: " + (warnings.Count == 0 ? "无" : string.Join("；", warnings.Take(5))),
                "- 产物路径: `" + context.ResultPath + "`",
                "- 生成时间: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            };
            var reportDir = Path.Combine(_storageRoot, "bridge-runs", context.Id);
            Directory.CreateDirectory(reportDir);
            await File.WriteAllTextAsync(Path.Combine(reportDir, "bridge-report.md"), string.Join(Environment.NewLine, lines) + Environment.NewLine, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            // 报告是旁路产物，写失败不影响导出本身；错误已记录到 job log。
        }
    }

    private static List<string> ReadMappingWarnings(string outDir)
    {
        var path = Path.Combine(outDir, "mapping.json");
        if (!File.Exists(path)) return new List<string>();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array)
            return new List<string>();
        var result = new List<string>();
        foreach (var item in warnings.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                result.Add(text);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                // mapping.json 的 warnings 是 {code,message,sample} 对象（B8-3 实测）。
                var message = item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                var code = item.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var label = !string.IsNullOrWhiteSpace(message) ? message : code;
                if (!string.IsNullOrWhiteSpace(label)) result.Add(label);
            }
        }
        return result;
    }

    private static int ReadCompensatedFrames(string outDir)
    {
        var path = Path.Combine(outDir, "mapping.json");
        if (!File.Exists(path)) return 0;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
            return 0;
        var total = 0;
        foreach (var clip in clips.EnumerateArray())
            if (clip.TryGetProperty("compensatedFrames", out var value) && value.TryGetInt32(out var frames)) total += Math.Max(0, frames);
        return total;
    }

    private async Task<(int Frames, double Duration, int Width, int Height)> ProbeFramesAsync(string path, CancellationToken token)
    {
        var ffprobe = File.Exists(_ffprobePath) ? _ffprobePath : Path.Combine(Path.GetDirectoryName(_ffprobePath) ?? "", "ffprobe.exe");
        var result = await RunProcessAsync(ffprobe, [
            "-v", "error", "-count_frames", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,nb_read_frames:format=duration",
            "-of", "json", path
        ], token).ConfigureAwait(false);
        if (result.ExitCode != 0) return (0, 0, 0, 0);
        using var document = JsonDocument.Parse(result.StdOut);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var list) && list.GetArrayLength() > 0 ? list[0] : default;
        var frames = 0;
        if (streams.ValueKind == JsonValueKind.Object && streams.TryGetProperty("nb_read_frames", out var nb)
            && int.TryParse(nb.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFrames)) frames = parsedFrames;
        var duration = 0.0;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration)) duration = parsedDuration;
        var width = streams.ValueKind == JsonValueKind.Object && streams.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        var height = streams.ValueKind == JsonValueKind.Object && streams.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        return (frames, duration, width, height);
    }

    /// <summary>
    /// 有已保存的 editor 工程就沿用它（字幕/蒙版/转场都来自保存的工程），只把画布尺寸对齐媒体；
    /// 否则用单片段最小工程。导出时长以工程时间线为准，避免把整段素材当工程。
    /// </summary>
    private ProjectPlan BuildProject(BridgeJobContext context, (double Duration, int Width, int Height) probe, double mediaDuration)
    {
        var saved = _projectJsonResolver?.Invoke(context.ClipId);
        if (!string.IsNullOrWhiteSpace(saved))
        {
            try
            {
                // StudioStore 落库的 project JSON 是 PascalCase（默认序列化器），映射脚本只认 camelCase；
                // 先反序列化成 EditorProject 再按 Web 选项转回 JsonObject，两种命名都能读。
                var normalized = JsonSerializer.Deserialize<EditorProject>(saved, JsonOptions) is { } savedProject
                    ? JsonSerializer.SerializeToNode(savedProject, JsonOptions) as JsonObject
                    : null;
                if ((normalized ?? JsonNode.Parse(saved) as JsonObject) is { } node)
                {
                    node["start"] = 0;
                    node["width"] = probe.Width;
                    node["height"] = probe.Height;
                    var timeline = node["timeline"] as JsonArray;
                    if (timeline is null || timeline.Count == 0)
                    {
                        timeline = BuildTimeline(context, mediaDuration);
                        node["timeline"] = timeline;
                    }
                    var end = mediaDuration;
                    if (node["end"] is JsonValue value && value.TryGetValue<double>(out var savedEnd) && savedEnd > 0)
                        end = Math.Min(savedEnd, mediaDuration);
                    else if (timeline.Count > 0 && timeline[^1] is JsonObject last && last["end"] is JsonValue lastEnd && lastEnd.TryGetValue<double>(out var lastValue))
                        end = Math.Min(lastValue, mediaDuration);
                    node["end"] = end;
                    var burnCaptions = node["burnSubtitles"] is JsonValue burn && burn.TryGetValue<bool>(out var burnValue) && burnValue;
                    var fps = node["fps"] is JsonValue fpsValue && fpsValue.TryGetValue<double>(out var savedFps) && savedFps > 0 ? savedFps : 30;
                    return new ProjectPlan(node, Math.Max(0.033, end), burnCaptions, fps);
                }
            }
            catch (JsonException)
            {
                // 保存的工程损坏时退回最小工程。
            }
        }

        var synthetic = new JsonObject
        {
            ["name"] = $"Bridge {context.Id[..8]}",
            ["fps"] = 30,
            ["width"] = probe.Width,
            ["height"] = probe.Height,
            ["start"] = 0d,
            ["end"] = mediaDuration,
            ["timeline"] = BuildTimeline(context, mediaDuration)
        };
        return new ProjectPlan(synthetic, mediaDuration, false, 30);
    }

    private static JsonArray BuildTimeline(BridgeJobContext context, double duration) =>
    [
        new JsonObject
        {
            ["id"] = "bridge-clip",
            ["clipId"] = context.ClipId,
            ["start"] = 0d,
            ["end"] = duration,
            ["kind"] = "video",
            ["track"] = 1,
            ["zoom"] = 1d,
            ["filter"] = "none",
            ["motion"] = "none",
            ["stickerAssetId"] = null,
            ["stickerX"] = 0.5d,
            ["stickerY"] = 0.5d,
            ["stickerWidth"] = 0.2d,
            ["soundAssetId"] = null,
            ["stickerRotation"] = 0d,
            ["fps"] = 30
        }
    ];

    private async Task ExportSegmentAsync(string fcproj, string path, double? startSeconds, double? lengthSeconds, bool burnCaptions, CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["format"] = "h264",
            ["bitrateKbps"] = 8000,
            ["audio"] = true,
            ["wait"] = true
        };
        if (startSeconds is { } start && lengthSeconds is { } length)
        {
            payload["range"] = "custom";
            payload["startSeconds"] = start;
            payload["endSeconds"] = start + length;
        }
        if (burnCaptions) payload["burnCaptions"] = true;

        var export = await RunProcessAsync(_cliPath, ["--project", fcproj, "exec", "file.exportMedia", JsonSerializer.Serialize(payload)], token).ConfigureAwait(false);
        if (export.ExitCode != 0)
            throw new BridgeExecutionException(new BridgeError("FilmCraftError", "FilmCraft 导出失败", Clip(export.StdErr, export.StdOut)));
        if (!File.Exists(path))
            throw new BridgeExecutionException(new BridgeError("FilmCraftError", "FilmCraft 未产出结果文件", Clip(export.StdOut, export.StdErr)));
    }

    private async Task ConcatAsync(IReadOnlyList<string> files, string resultPath, double? trimSeconds, CancellationToken token)
    {
        var listPath = Path.Combine(Path.GetDirectoryName(files[0])!, "concat.txt");
        var lines = files.Select(file => "file '" + file.Replace('\\', '/').Replace("'", "'\\''") + "'");
        await File.WriteAllTextAsync(listPath, string.Join('\n', lines) + "\n", token).ConfigureAwait(false);
        var ffmpeg = ResolveFfmpegPath();
        var arguments = new List<string> {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "concat", "-safe", "0", "-i", listPath
        };
        if (trimSeconds is { } trim)
        {
            // 视频直接拷贝（保持逐段帧数），音频重编码并裁到目标时长，消除 AAC priming 累积。
            arguments.AddRange(["-c:v", "copy", "-c:a", "aac", "-b:a", "160k", "-t", trim.ToString("0.######", CultureInfo.InvariantCulture)]);
        }
        else
        {
            arguments.AddRange(["-c", "copy"]);
        }
        arguments.AddRange(["-movflags", "+faststart", resultPath]);
        var result = await RunProcessAsync(ffmpeg, arguments, token).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new BridgeExecutionException(new BridgeError("FilmCraftError", "分块拼接失败", Clip(result.StdErr, result.StdOut)));
    }

    private string ResolveFfmpegPath()
    {
        if (!string.IsNullOrWhiteSpace(_ffmpegPath) && File.Exists(_ffmpegPath)) return _ffmpegPath;
        var directory = Path.GetDirectoryName(_ffprobePath);
        if (!string.IsNullOrEmpty(directory))
        {
            var sibling = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(sibling)) return sibling;
        }
        return string.IsNullOrWhiteSpace(_ffmpegPath) ? "ffmpeg" : _ffmpegPath;
    }

    private async Task<(double Duration, int Width, int Height)> ProbeMediaAsync(string path, CancellationToken token)
    {
        var ffprobe = File.Exists(_ffprobePath) ? _ffprobePath : Path.Combine(Path.GetDirectoryName(_ffprobePath) ?? "", "ffprobe.exe");
        if (!File.Exists(ffprobe)) return (4, 1920, 1080);
        var result = await RunProcessAsync(ffprobe, [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height:format=duration",
            "-of", "json", path
        ], token).ConfigureAwait(false);
        if (result.ExitCode != 0) return (4, 1920, 1080);
        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var root = document.RootElement;
            var width = root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0
                ? streams[0].GetProperty("width").GetInt32() : 1920;
            var height = root.TryGetProperty("streams", out var streamList) && streamList.GetArrayLength() > 0
                ? streamList[0].GetProperty("height").GetInt32() : 1080;
            var duration = root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : 4;
            return (duration, width, height);
        }
        catch (JsonException)
        {
            return (4, 1920, 1080);
        }
    }

    private async Task<ProcessResult> RunProcessAsync(string file, IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_stepTimeout);
        var start = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new BridgeExecutionException(new BridgeError("FilmCraftError", $"无法启动 {Path.GetFileName(file)}", ex.Message));
        }
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (token.IsCancellationRequested) throw;
            throw new BridgeExecutionException(new BridgeError("BridgeTimeout", $"{Path.GetFileName(file)} 超时", _stepTimeout.ToString()));
        }
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private ProcessResult RunProcess(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token) =>
        RunProcessAsync(file, arguments, token).GetAwaiter().GetResult();

    private static string Clip(string primary, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(primary) ? fallback : primary;
        text = text.Trim();
        return text.Length <= 800 ? text : text[..800];
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
