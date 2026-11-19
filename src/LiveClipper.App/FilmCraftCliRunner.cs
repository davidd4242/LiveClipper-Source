using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LiveClipper.Tests")]

namespace LiveClipper.App;

public sealed record FilmCraftExportRequest(
    string ProjectPath,
    string OutputPath,
    double Start = 0,
    double End = 3,
    string Format = "h264",
    int TimeoutSec = 1800,
    bool BurnCaptions = false,
    // 只驱动导出后的输出校验（要求成品带音频流），不进 --settings JSON、不进 argv。
    bool ExpectAudio = false);

public sealed record FilmCraftExportResult(
    bool Ok,
    string? Code = null,
    string? Message = null,
    string? Output = null,
    long Bytes = 0,
    string? Stdout = null,
    string? Stderr = null,
    int? ExitCode = null,
    string? Error = null);

public sealed record FilmCraftFormat(string Name, string Extension, string CliFormat);

/// <summary>
/// 一次输出探测的结论。<paramref name="Available"/>=false 表示这台机器上探针本身跑不起来
/// （缺 ffprobe / 被拦截），不是"输出有问题"——调用方据此放行，不要当成失败证据。
/// </summary>
public sealed record OutputProbeResult(
    bool Available,
    bool Readable,
    bool HasVideo,
    bool HasAudio,
    double DurationSeconds,
    string? Detail = null)
{
    public static OutputProbeResult Unavailable(string? detail = null) => new(false, false, false, false, 0, detail);
}

/// <summary>
/// 导出成品的可注入校验口。exit 0 + 文件非 0 字节并不证明成品可用：容器可能被截断、
/// 只剩音轨、或时长为 0。测试注入假探针，生产用 <see cref="FfprobeOutputProbe"/>。
/// </summary>
public interface IOutputProbe
{
    Task<OutputProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// 默认实现：复用 <see cref="LiveClipper.Core.MediaTools"/> 已有的 ffprobe 查找（Ffmpeg 同目录 sibling，
/// 否则退回 PATH），不另造一套探测逻辑。用 CaptureAsync 而不是 RunAsync：坏容器下 ffprobe
/// 会非 0 退出，但仍要拿到它的输出才能判"不可读"；RunAsync 会抛异常，反而被当成探针不可用而放行。
/// </summary>
public sealed class FfprobeOutputProbe(LiveClipper.Core.MediaTools? media = null) : IOutputProbe
{
    private readonly LiveClipper.Core.MediaTools _media = media ?? new LiveClipper.Core.MediaTools();

    public async Task<OutputProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        string raw;
        try
        {
            raw = await _media.CaptureAsync(
                _media.Ffprobe,
                ["-v", "error", "-show_error", "-show_streams", "-show_format", "-print_format", "json", path],
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ffprobe 不存在/启动失败（Win32Exception 等）：探针不可用，不是输出有问题。
            return OutputProbeResult.Unavailable(Summarize(ex.Message));
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
            return new(true, false, false, false, 0, Summarize(raw));

        try
        {
            using var document = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = document.RootElement;
            // ffprobe can emit valid JSON even on failure; -show_error distinguishes corrupt media from a readable audio-only file.
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                return new(true, false, false, false, 0, Summarize(raw));
            var hasVideo = false;
            var hasAudio = false;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var codecType) && codecType.ValueKind == JsonValueKind.String)
                    {
                        var kind = codecType.GetString();
                        if (string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase)) hasVideo = true;
                        else if (string.Equals(kind, "audio", StringComparison.OrdinalIgnoreCase)) hasAudio = true;
                    }
                }
            }
            // 时长走 ReadDurationSeconds：format.duration -> max(stream.duration) -> tags DURATION，见该方法注释。
            var duration = ReadDurationSeconds(root);
            return new(true, true, hasVideo, hasAudio, duration, Summarize(raw));
        }
        catch (JsonException ex)
        {
            return new(true, false, false, false, 0, Summarize(ex.Message));
        }
    }

    /// <summary>
    /// 成品时长，按可信度依次回退：format.duration（大于 0 才算）→ streams[].duration 的最大值 →
    /// streams[].tags.DURATION 与 format.tags.DURATION 的最大值。Matroska/WebM 的 stream 上没有 duration 字段，
    /// 只有 tags.DURATION，所以最后一级不能省。"N/A"、缺字段、非正数都按 0 处理；全部为 0 时返回 0，
    /// 仍由 VerifyOutputAsync 判 output_zero_duration——回退只多认一种写法，不放宽判定。
    /// </summary>
    internal static double ReadDurationSeconds(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return 0;
        var hasFormat = root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object;
        if (hasFormat)
        {
            var formatSeconds = ReadSeconds(format);
            if (formatSeconds > 0) return formatSeconds;
        }

        var streams = root.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray()
            : Array.Empty<JsonElement>();
        var streamSeconds = 0d;
        foreach (var stream in streams) streamSeconds = Math.Max(streamSeconds, ReadSeconds(stream));
        if (streamSeconds > 0) return streamSeconds;

        var tagSeconds = hasFormat ? ReadTagDurationSeconds(format) : 0;
        foreach (var stream in streams) tagSeconds = Math.Max(tagSeconds, ReadTagDurationSeconds(stream));
        return tagSeconds;
    }

    private static double ReadTagDurationSeconds(JsonElement element)
    {
        if (!element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return 0;
        var seconds = 0d;
        foreach (var tag in tags.EnumerateObject())
        {
            if (tag.Value.ValueKind == JsonValueKind.String && string.Equals(tag.Name, "DURATION", StringComparison.OrdinalIgnoreCase))
                seconds = Math.Max(seconds, ParseClockSeconds(tag.Value.GetString()));
        }
        return seconds;
    }

    // Matroska 的 DURATION 标签形如 "00:00:01.021000000"：小数 9 位、小时可以 >= 24，都超出 TimeSpan.Parse 的范围，
    // 所以手拆三段按不变文化解析；格式不对（含 "N/A"）一律返回 0。
    private static double ParseClockSeconds(string? text)
    {
        var parts = text?.Trim().Split(':');
        if (parts is not { Length: 3 } ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            minutes >= 60 || seconds >= 60)
            return 0;
        var total = hours * 3600d + minutes * 60d + seconds;
        return double.IsFinite(total) && total > 0 ? total : 0;
    }

    private static double ReadSeconds(JsonElement element)
    {
        if (!element.TryGetProperty("duration", out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return double.IsFinite(number) && number > 0 ? number : 0;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return double.IsFinite(parsed) && parsed > 0 ? parsed : 0;
        return 0;
    }

    private static string? Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= 600 ? trimmed : trimmed[^600..];
    }
}

/// <summary>
/// P2a: run the real FilmCraft CLI as a short-lived child process.
/// The browser never chooses an executable; it can only pick a .fcproj and an output path.
/// </summary>
public static class FilmCraftCliRunner
{
    // FilmCraft's dnxhr format uses MOV; MXF has separate mxf-op1a/mxf-opatom formats.
    public static IReadOnlyList<FilmCraftFormat> FilmCraftFormats { get; } = Array.AsReadOnly<FilmCraftFormat>(
    [
        new("h264", ".mp4", "h264"),
        new("hevc", ".mp4", "hevc"),
        new("prores", ".mov", "prores"),
        new("dnxhr", ".mov", "dnxhr"),
    ]);

    private static readonly string[] MediaExtensions = [".mp4", ".mov", ".mxf", ".mkv", ".webm", ".avi", ".m4v", ".ts"];

    /// <summary>
    /// 解析 filmcraft-cli.exe 路径。走 <see cref="LiveClipper.Core.Bridge.FilmCraftLocator"/> 的公开 API，
    /// 因此导出路径和 /api/bridge 的安装探测共用同一套来源：显式覆盖路径、LIVECLIPPER_FILMCRAFT_CLI
    /// 环境变量、注册表 InstallLocation/App Paths、ProgramFiles(x86)、LocalAppData。
    /// 这里注入空版本探针：定位不该为每次导出多起一个 --version 子进程（最坏 10 秒停顿），
    /// 版本 pin 的用户可见判定留在 BridgeEndpoints 的安装检查里，版本不符不在导出路径上拦截。
    /// Locator 的 GUI-only 分支会返回 Found=true 但 CliPath 并不存在，所以这里必须自己 File.Exists 兜底。
    /// </summary>
    public static string? FindCli(string? overridePath = null)
    {
        var probe = LiveClipper.Core.Bridge.FilmCraftLocator.Locate(
            overridePath: overridePath,
            versionProbe: static (_, _) => null);
        var cli = probe.Install?.CliPath;
        return !string.IsNullOrWhiteSpace(cli) && File.Exists(cli) ? cli : null;
    }

    public static ProcessStartInfo CreateExportStartInfo(string cli, FilmCraftExportRequest request)
    {
        var format = FilmCraftFormats.FirstOrDefault(item => string.Equals(item.Name, request.Format, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Format must be h264/hevc/prores/dnxhr.", nameof(request));
        var psi = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "--project", Path.GetFullPath(request.ProjectPath),
            "export", Path.GetFullPath(request.OutputPath),
            "--format", format.CliFormat,
            "--range", "custom",
            "--start", request.Start.ToString(CultureInfo.InvariantCulture),
            "--end", request.End.ToString(CultureInfo.InvariantCulture),
            "--allow-offline",
            "--settings", JsonSerializer.Serialize(new { burnCaptions = request.BurnCaptions }),
        })
        {
            psi.ArgumentList.Add(argument);
        }
        return psi;
    }

    public static Task<FilmCraftExportResult> ExportAsync(
        FilmCraftExportRequest request,
        CancellationToken cancellationToken) => ExportAsync(request, cancellationToken, () =>
        {
            var cli = FindCli();
            return cli is null ? null : CreateExportStartInfo(cli, request);
        });

    // Tests replace only process discovery; validation, execution and output checks are shared.
    internal static async Task<FilmCraftExportResult> ExportAsync(
        FilmCraftExportRequest request,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo?> startInfoFactory,
        IOutputProbe? outputProbe = null)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectPath) || !File.Exists(request.ProjectPath))
            return new(false, "project_missing", $"Project file not found: {request.ProjectPath}");
        if (!string.Equals(Path.GetExtension(request.ProjectPath), ".fcproj", StringComparison.OrdinalIgnoreCase))
            return new(false, "project_extension", "Project must be a .fcproj file.");
        if (!double.IsFinite(request.Start) || !double.IsFinite(request.End) || request.Start < 0 || request.End <= request.Start)
            return new(false, "range_invalid", "Start/end must be finite seconds with 0 <= start < end.");
        var extension = Path.GetExtension(request.OutputPath);
        if (!FilmCraftFormats.Any(item => string.Equals(item.Extension, extension, StringComparison.OrdinalIgnoreCase)))
            return new(false, "output_extension", "Output must end with .mp4/.mov.");
        var format = FilmCraftFormats.FirstOrDefault(item => string.Equals(item.Name, request.Format, StringComparison.OrdinalIgnoreCase));
        if (format is null)
            return new(false, "format_invalid", "Format must be h264/hevc/prores/dnxhr.");
        if (!string.Equals(extension, format.Extension, StringComparison.OrdinalIgnoreCase))
            return new(false, "output_format_mismatch", $"{format.Name} 输出文件必须使用 {format.Extension} 扩展名。");

        var output = Path.GetFullPath(request.OutputPath);
        var outputDirectory = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
            return new(false, "output_dir_missing", $"Output directory does not exist: {outputDirectory}");

        var psi = startInfoFactory();
        if (psi is null)
            return FailExport("cli_missing", "未找到 FilmCraft CLI，请安装 FilmCraft 后重试导出。", null, null, null);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return FailExport("cli_start_failed", "FilmCraft CLI 启动失败。", null, null, null);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSec, 5, 7200));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch { /* already gone */ }
                var cancelled = cancellationToken.IsCancellationRequested;
                return FailExport(cancelled ? "cancelled" : "timeout",
                    cancelled ? "导出已取消。" : $"FilmCraft CLI 导出超时（{timeout.TotalSeconds:0} 秒），进程已终止。",
                    null, await stdoutTask, await stderrTask);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                return FailExport("cli_failed", $"FilmCraft CLI 导出失败（退出码 {process.ExitCode}）。", process.ExitCode, stdout, stderr);
            if (!File.Exists(output))
                return FailExport("output_missing", "FilmCraft CLI 已退出，但未生成输出文件。", process.ExitCode, stdout, stderr);
            var bytes = new FileInfo(output).Length;
            if (bytes <= 0)
                return FailExport("output_empty", "FilmCraft CLI 生成的输出文件为 0 字节。", process.ExitCode, stdout, stderr);
            var verdict = await VerifyOutputAsync(output, request, outputProbe ?? new FfprobeOutputProbe(), cancellationToken);
            if (verdict is not null)
                return FailExport(verdict.Code, verdict.Message, process.ExitCode, stdout, JoinDiagnostics(stderr, verdict.Detail));
            return new(true, Output: output, Bytes: bytes, Stdout: stdout, Stderr: stderr);
        }
        catch (Exception ex)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return FailExport("cli_error", $"FilmCraft CLI 执行异常：{ex.Message}", null, null, null);
        }
    }

    public static async Task<FilmCraftCutsResult> BuildCutsAsync(
        FilmCraftCutsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MediaPaths is null || request.MediaPaths.Length == 0)
            return new(false, "media_missing", "At least one media file is required.");
        foreach (var media in request.MediaPaths)
        {
            if (string.IsNullOrWhiteSpace(media))
                return new(false, "media_missing", "Media path is empty.");
            if (IsUncPath(media))
                return new(false, "media_path_unsupported", $"UNC media paths are not allowed: {media}");
            if (!MediaExtensions.Contains(Path.GetExtension(media), StringComparer.OrdinalIgnoreCase))
                return new(false, "media_extension", $"Media must end with {string.Join("/", MediaExtensions)}: {media}");
            if (!File.Exists(media))
                return new(false, "media_missing", $"Media file not found: {media}");
        }
        if (string.IsNullOrWhiteSpace(request.ProjectPath) ||
            !string.Equals(Path.GetExtension(request.ProjectPath), ".fcproj", StringComparison.OrdinalIgnoreCase))
            return new(false, "project_extension", "Project must be a .fcproj file.");
        if (IsUncPath(request.ProjectPath))
            return new(false, "project_path_unsupported", "UNC project paths are not allowed.");
        var project = Path.GetFullPath(request.ProjectPath);
        var projectDirectory = Path.GetDirectoryName(project);
        if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
            return new(false, "project_dir_missing", $"Project directory does not exist: {projectDirectory}");

        string? output = null;
        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            if (IsUncPath(request.OutputPath))
                return new(false, "output_path_unsupported", "UNC output paths are not allowed.");
            if (!string.Equals(Path.GetExtension(request.OutputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
                return new(false, "output_extension", "Output must end with .mp4.");
            output = Path.GetFullPath(request.OutputPath);
            var outputDirectory = Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
                return new(false, "output_dir_missing", $"Output directory does not exist: {outputDirectory}");
        }

        if (FindCli() is null)
            return new(false, "cli_missing", "filmcraft-cli.exe not found.");
        var script = Path.Combine(AppContext.BaseDirectory, "tools", "FilmCraftCli", "New-CutsProject.ps1");
        if (!File.Exists(script))
            return new(false, "builder_missing", $"Cuts builder script not found: {script}");

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return new(false, "powershell_missing", "Windows PowerShell not found.");

        string? mediaListPath = null;
        try
        {
            mediaListPath = Path.Combine(Path.GetTempPath(), $"fc-cuts-media-{Guid.NewGuid():N}.json");
            File.WriteAllText(mediaListPath, JsonSerializer.Serialize(request.MediaPaths.Select(Path.GetFullPath).ToArray()));
        }
        catch (Exception ex)
        {
            return new(false, "builder_error", ex.Message);
        }

        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
            "-MediaListFile", mediaListPath,
            "-OutputProject", project,
        })
        {
            psi.ArgumentList.Add(argument);
        }
        if (output is not null)
        {
            psi.ArgumentList.Add("-ExportPath");
            psi.ArgumentList.Add(output);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new(false, "builder_start_failed", "Could not start the cuts builder.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSec, 5, 7200));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (cancellationToken.IsCancellationRequested)
                    return new(false, "cancelled", "Cuts builder was cancelled.");
                return new(false, "timeout", $"Cuts builder exceeded {timeout.TotalSeconds:0}s.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                return FailCuts("builder_failed", $"Cuts builder exited with {process.ExitCode}.", process.ExitCode, stdout, stderr);
            if (!File.Exists(project))
                return FailCuts("project_missing", "Builder reported success but project was not created.", process.ExitCode, stdout, stderr);
            if (output is not null && !File.Exists(output))
                return FailCuts("output_missing", "Builder reported success but output was not created.", process.ExitCode, stdout, stderr);

            return new(true, Project: project, Output: output, Stdout: stdout, Stderr: stderr);
        }
        catch (Exception ex)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new(false, "builder_error", ex.Message);
        }
        finally
        {
            if (mediaListPath is not null)
                try { File.Delete(mediaListPath); } catch { /* best effort */ }
            CleanupTempProjects(projectDirectory, Path.GetFileNameWithoutExtension(project));
        }
    }

    private static void CleanupTempProjects(string directory, string stem)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, $".{stem}.tmp-*.fcproj"))
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// 失败响应契约（camelCase：error / stderr / exitCode，另有 code / message / stdout）：三者都非空。
    /// stderr 为空时退回 stdout，再退回 message；error 取 stderr 的最后一段非空文本（真实诊断），便于前端直接展示。
    /// </summary>
    private static (string Stderr, string Error) Diagnose(string message, string? stdout, string? stderr)
    {
        var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr!.Trim()
            : !string.IsNullOrWhiteSpace(stdout) ? stdout!.Trim()
            : message;
        var tail = detail.Length > 2000 ? detail[^2000..] : detail;
        return (detail, tail);
    }

    private sealed record OutputVerdict(string Code, string Message, string? Detail);

    /// <summary>
    /// exit 0 + 非 0 字节不足以判定导出成功，所以再加一层 ffprobe 级校验：容器可读、有视频流、
    /// 时长 > 0、请求了音频时有音频流。
    /// 取舍：探针跑不起来（这台机器没装 ffprobe / 启动被拦）时放行而不是拦截。真实 ffmpeg 常由
    /// FfmpegInstaller 装在 PATH 之外、路径只存在 LiveSettings.FfmpegPath 里，而 Runner 这一层拿不到它，
    /// 一刀切 fail-closed 会把现在能正常导出的机器全变成导出失败。容器能读但缺视频流/时长为 0/
    /// 要求音频却没音频 —— 这些是明确的坏成品证据，一律 fail-closed。
    /// </summary>
    private static async Task<OutputVerdict?> VerifyOutputAsync(
        string output,
        FilmCraftExportRequest request,
        IOutputProbe probe,
        CancellationToken cancellationToken)
    {
        OutputProbeResult result;
        try
        {
            result = await probe.ProbeAsync(output, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 取消发生在成品已经落盘之后：不改判已有结果，只是这一次没校验。
            return null;
        }
        catch (Exception)
        {
            return null;
        }

        if (!result.Available) return null;
        if (!result.Readable)
            return new("output_unreadable", "FilmCraft CLI 生成的输出文件无法被 ffprobe 读取（容器损坏或不完整）。", result.Detail);
        if (!result.HasVideo)
            return new("output_no_video", "FilmCraft CLI 生成的输出文件没有视频流。", result.Detail);
        if (!(result.DurationSeconds > 0))
            return new("output_zero_duration", "FilmCraft CLI 生成的输出文件时长为 0。", result.Detail);
        if (request.ExpectAudio && !result.HasAudio)
            return new("output_no_audio", "FilmCraft CLI 生成的输出文件缺少音频流（本次导出要求含音频）。", result.Detail);
        return null;
    }

    private static string? JoinDiagnostics(string? stderr, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return stderr;
        if (string.IsNullOrWhiteSpace(stderr)) return detail;
        return stderr + "\n" + detail;
    }

    private static FilmCraftExportResult FailExport(string code, string message, int? exitCode, string? stdout, string? stderr)
    {
        var (err, error) = Diagnose(message, stdout, stderr);
        return new(false, code, message, Stdout: stdout, Stderr: err, ExitCode: exitCode, Error: error);
    }

    private static FilmCraftCutsResult FailCuts(string code, string message, int exitCode, string? stdout, string? stderr)
    {
        var (err, error) = Diagnose(message, stdout, stderr);
        return new(false, code, message, Stdout: stdout, Stderr: err, ExitCode: exitCode, Error: error);
    }

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);
}

public sealed record FilmCraftCutsRequest(
    string[] MediaPaths,
    string ProjectPath,
    string? OutputPath = null,
    int TimeoutSec = 1800);

public sealed record FilmCraftCutsResult(
    bool Ok,
    string? Code = null,
    string? Message = null,
    string? Project = null,
    string? Output = null,
    string? Stdout = null,
    string? Stderr = null,
    int? ExitCode = null,
    string? Error = null);
