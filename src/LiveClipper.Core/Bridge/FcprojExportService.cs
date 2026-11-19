using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveClipper.Core.Bridge;

/// <summary>
/// 把宿主编辑器工程单向导出为 FilmCraft 渲染输入；产物不作为宿主工程的数据源。
/// 复用映射脚本；没有保存工程时使用单片段最小工程。
/// 产物落在 {StorageRoot}/bridge-roundtrip/{clipId}/{timestamp}/。
/// </summary>
public sealed class FcprojExportService
{
    // FilmCraft 0.13.0 crates/format/src/lib.rs:55,62-66,398.
    // This is the native render format, separate from the preview's filmcraft-project/version=1.
    public const string ProjectFormat = "filmcraft.project";
    public const int SchemaVersion = 15;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<string, string?> _clipPathResolver;
    private readonly Func<string, string?>? _projectJsonResolver;
    private readonly Func<string, EditorProject, EditorProject>? _snapshot;
    private readonly string _nodePath;
    private readonly string _mapperPath;
    private readonly string _ffprobePath;
    private readonly string _storageRoot;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>TTL 保留策略：同一 clipId 下最多保留 20 份临时工程。</summary>
    public const int RetentionCount = 20;

    /// <summary>TTL 保留策略：超过 7 天的临时工程即使未满 20 份也清理（与 20 份取严）。</summary>
    public static readonly TimeSpan RetentionAge = TimeSpan.FromDays(7);

    public FcprojExportService(
        Func<string, string?> clipPathResolver,
        string nodePath,
        string mapperPath,
        string ffprobePath,
        string storageRoot,
        Func<string, string?>? projectJsonResolver = null,
        Func<DateTimeOffset>? now = null,
        Func<string, EditorProject, EditorProject>? snapshot = null)
    {
        _clipPathResolver = clipPathResolver;
        _nodePath = nodePath;
        _mapperPath = mapperPath;
        _ffprobePath = ffprobePath;
        _storageRoot = storageRoot;
        _projectJsonResolver = projectJsonResolver;
        _snapshot = snapshot;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string StorageRoot => _storageRoot;

    public sealed record FcprojExportResult(
        string ClipId,
        string FcprojPath,
        string WorkDir,
        string EditorProjectPath,
        IReadOnlyList<string> Warnings);

    /// <param name="plan">编辑器即时工程（可选）：优先于已保存工程；落盘前校验并快照，非法返回 ValidationError。</param>
    public FcprojExportResult Export(string clipId, string? variant, CancellationToken token, EditorProject? plan = null)
    {
        if (string.IsNullOrWhiteSpace(clipId))
            throw new BridgeExecutionException(new BridgeError("NotFound", "缺少 clipId", clipId));
        var clipPath = _clipPathResolver(clipId);
        if (string.IsNullOrWhiteSpace(clipPath) || !File.Exists(clipPath))
            throw new BridgeExecutionException(new BridgeError("NotFound", "切片不存在或媒体文件缺失", clipPath ?? clipId));
        if (!File.Exists(_mapperPath))
            throw new BridgeExecutionException(new BridgeError("MappingError", "找不到映射脚本 filmcraft-b1-map.mjs", _mapperPath));

        // 即时工程先校验：非法一律 ValidationError（端点 422），不静默回落到防抖窗口里的旧工程。
        if (plan is not null)
        {
            try { EditorRenderService.Validate(plan); }
            catch (ArgumentException ex)
            {
                throw new BridgeExecutionException(new BridgeError("ValidationError", "编辑器工程校验失败", ex.Message));
            }
        }

        var now = _now();
        var workDir = CreateWorkDir(_storageRoot, clipId, now);
        // 生成新工程时顺手清理旧工程（最近 20 份 / 7 天，二者取严）。失败不影响本次生成。
        PruneWorkDirs(_storageRoot, clipId, now);
        var outDir = Path.Combine(workDir, "map");
        Directory.CreateDirectory(outDir);
        try
        {
            var probe = ProbeMedia(clipPath, token);
            var mediaDuration = probe.Duration > 0 ? probe.Duration : 4.0;
            // 序列尺寸取 V1 轨道首段源，与 EditorRenderService.AssembleAsync 的 items[0] 口径一致（导出与预览同源）。
            // 首段解析不到真实文件 / ffprobe 不可用时回退主切片尺寸；时长仍取主切片，避免改变序列时长语义。
            var width = probe.Width;
            var height = probe.Height;
            if (plan is not null)
            {
                try
                {
                    var sizeItems = EditorRenderService.BaseItems(EditorRenderService.Items(EditorRenderService.FlattenProject(plan), clipId));
                    var sizeClipId = sizeItems.FirstOrDefault()?.ClipId;
                    if (!string.IsNullOrWhiteSpace(sizeClipId) && sizeClipId != clipId)
                    {
                        var sizedPath = _clipPathResolver(sizeClipId);
                        if (!string.IsNullOrWhiteSpace(sizedPath) && File.Exists(sizedPath)
                            && TryProbeMedia(sizedPath, token, out var sizedProbe)
                            && sizedProbe.Width > 0 && sizedProbe.Height > 0)
                        {
                            width = sizedProbe.Width;
                            height = sizedProbe.Height;
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // 嵌套序列超深/循环引用时保持主切片尺寸，导出本身的校验会给出明确错误。
                }
            }
            var payload = BuildDocument(clipId, clipPath, plan, _projectJsonResolver?.Invoke(clipId), mediaDuration, width, height, _clipPathResolver, _snapshot);
            var editorProjectPath = Path.Combine(workDir, "editor-project.json");
            File.WriteAllText(editorProjectPath, payload.ToJsonString(JsonOptions));

            var mapper = RunProcess(_nodePath, [
                _mapperPath,
                "--input", editorProjectPath,
                "--out", outDir,
                "--map-fades",
                "--map-keyframes",
                "--map-speed",
                "--map-captions",
                "--map-masks",
                "--multi-track", "host",
                "--validate",
                "--keep",
            ], token);
            if (mapper.ExitCode != 0)
                throw new BridgeExecutionException(new BridgeError("MappingError", "生成 FilmCraft 工程失败", Clip(mapper.StdErr, mapper.StdOut)));

            var fcprojPath = Directory.EnumerateFiles(outDir, "*.fcproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(fcprojPath) || !File.Exists(fcprojPath))
                throw new BridgeExecutionException(new BridgeError("MappingError", "映射完成但没有生成 .fcproj", outDir));
            fcprojPath = Path.GetFullPath(fcprojPath);
            if (!IsUnderBridgeRoundtrip(_storageRoot, fcprojPath))
                throw new BridgeExecutionException(new BridgeError("MappingError", "生成的 .fcproj 不在 bridge-roundtrip 目录下", fcprojPath));

            ValidateRenderInput(File.ReadAllText(fcprojPath));
            var warnings = ReadWarnings(Path.Combine(outDir, "mapping.json"));
            var metaPath = Path.Combine(workDir, "meta.json");
            var meta = new JsonObject
            {
                ["clipId"] = clipId,
                ["variant"] = variant ?? "default",
                ["createdAt"] = _now().ToString("O", CultureInfo.InvariantCulture),
                ["fcprojPath"] = fcprojPath,
                ["editorProjectPath"] = editorProjectPath,
                ["mediaPath"] = clipPath,
                ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
            };
            File.WriteAllText(metaPath, meta.ToJsonString(JsonOptions));
            return new FcprojExportResult(clipId, fcprojPath, workDir, editorProjectPath, warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeExecutionException(new BridgeError("MappingError", "写入临时工程失败", ex.Message));
        }
    }

    /// <summary>只读检查原生渲染输入；完整字段和语义由 mapper 的 CLI --validate 验证。</summary>
    internal static void ValidateRenderInput(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format)
                || format.ValueKind != JsonValueKind.String
                || format.GetString() != ProjectFormat
                || !root.TryGetProperty("schema_version", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var version)
                || version != SchemaVersion)
            {
                throw new JsonException($"Expected {ProjectFormat} schema_version={SchemaVersion}.");
            }

            // Required by crates/format/src/lib.rs:126 and crates/project/src/lib.rs:1233-1239.
            if (!root.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object
                || !project.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Expected project.items object.");
            }
        }
        catch (JsonException ex)
        {
            throw new BridgeExecutionException(new BridgeError("MappingError", "FilmCraft 渲染输入契约不匹配", ex.Message));
        }
    }

    public static string SanitizeClipId(string clipId)
    {
        var safe = new string(clipId.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
        if (safe.Length == 0) safe = "clip";
        return safe.Length <= 80 ? safe : safe[..80];
    }

    public static string BridgeRoundtripRoot(string storageRoot) =>
        Path.GetFullPath(Path.Combine(storageRoot, "bridge-roundtrip"));

    public static string BuildWorkDir(string storageRoot, string clipId, DateTimeOffset timestamp)
    {
        var safe = SanitizeClipId(clipId);
        return Path.Combine(BridgeRoundtripRoot(storageRoot), safe, timestamp.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
    }

    public static string CreateWorkDir(string storageRoot, string clipId, DateTimeOffset timestamp)
    {
        var baseDir = BuildWorkDir(storageRoot, clipId, timestamp);
        var candidate = baseDir;
        for (var index = 1; index < 100 && Directory.Exists(candidate); index++)
            candidate = baseDir + "-" + index.ToString(CultureInfo.InvariantCulture);
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    /// <summary>
    /// 清理某 clipId 下的旧临时工程：保留最新 <see cref="RetentionCount"/> 份且不超过 <see cref="RetentionAge"/>，
    /// 两个条件取严（超过 20 份或超过 7 天的都删除）。返回被删除的目录，删除失败静默跳过。
    /// </summary>
    public static IReadOnlyList<string> PruneWorkDirs(string storageRoot, string clipId, DateTimeOffset now)
    {
        var clipDir = Path.Combine(BridgeRoundtripRoot(storageRoot), SanitizeClipId(clipId));
        if (!Directory.Exists(clipDir)) return Array.Empty<string>();

        var entries = new List<(string Path, DateTimeOffset CreatedAt)>();
        foreach (var dir in Directory.EnumerateDirectories(clipDir))
        {
            var name = Path.GetFileName(dir);
            var createdAt = TryParseWorkDirStamp(name)
                ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);
            entries.Add((dir, createdAt));
        }

        entries.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        var removed = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            var expired = now - entries[index].CreatedAt > RetentionAge;
            var overflow = index >= RetentionCount;
            if (!expired && !overflow) continue;
            try
            {
                Directory.Delete(entries[index].Path, recursive: true);
                removed.Add(entries[index].Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理是尽力而为：目录被占用时保留，下次生成再试。
            }
        }
        return removed;
    }

    private static DateTimeOffset? TryParseWorkDirStamp(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // 目录名形如 yyyyMMdd-HHmmss-fff，冲突时追加 -1 / -2。
        var parts = name.Split('-');
        if (parts.Length < 3 || parts[0].Length != 8 || parts[1].Length != 6 || parts[2].Length < 3) return null;
        var stamp = string.Concat(parts[0], parts[1], parts[2][..3]);
        return DateTimeOffset.TryParseExact(stamp, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    public static bool IsUnderBridgeRoundtrip(string storageRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate)) return false;
        try
        {
            var root = BridgeRoundtripRoot(storageRoot);
            var full = Path.GetFullPath(candidate);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 生成映射输入：优先即时 plan，其次已保存工程，最后单片段最小工程。
    /// clips 数组为时间线上每个源提供真实媒体路径；未知源显式 NotFound，绝不把别的切片映射成主媒体。
    /// </summary>
    public static JsonObject BuildDocument(
        string clipId,
        string clipPath,
        EditorProject? plan,
        string? savedProjectJson,
        double mediaDuration,
        int width,
        int height,
        Func<string, string?> clipPathResolver,
        Func<string, EditorProject, EditorProject>? snapshot = null)
    {
        if (string.IsNullOrWhiteSpace(clipId))
            throw new BridgeExecutionException(new BridgeError("NotFound", "缺少 clipId", clipId));

        var freeze = snapshot ?? ((_, project) => project);
        JsonObject project;
        if (plan is not null)
        {
            project = ProjectNode(Freeze(clipId, plan, freeze), mediaDuration, width, height, clipId);
        }
        else
        {
            var saved = ParseProject(savedProjectJson);
            if (saved is not null)
            {
                project = ProjectNode(Freeze(clipId, saved, freeze), mediaDuration, width, height, clipId);
            }
            else
            {
                project = new JsonObject
                {
                    ["name"] = "FilmCraft " + SanitizeClipId(clipId),
                    ["fps"] = 30,
                    ["width"] = width,
                    ["height"] = height,
                    ["start"] = 0d,
                    ["end"] = mediaDuration,
                    ["timeline"] = BuildTimeline(clipId, 0d, mediaDuration),
                };
            }
        }

        var clips = new JsonArray();
        foreach (var id in TimelineSources(project, clipId))
        {
            var mediaPath = string.Equals(id, clipId, StringComparison.Ordinal) ? clipPath : clipPathResolver(id);
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                throw new BridgeExecutionException(new BridgeError("NotFound", $"时间线源视频不存在或媒体文件缺失（{id}）", mediaPath ?? id));
            clips.Add(new JsonObject { ["id"] = id, ["path"] = mediaPath });
        }

        return new JsonObject
        {
            ["clip"] = new JsonObject { ["id"] = clipId, ["path"] = clipPath, ["duration"] = mediaDuration },
            ["clips"] = clips,
            ["project"] = project,
        };
    }

    private static EditorProject? ParseProject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<EditorProject>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>快照失败即工程不可用：按 ValidationError 上报，绝不拿未冻结的工程继续映射。</summary>
    private static EditorProject Freeze(string clipId, EditorProject project, Func<string, EditorProject, EditorProject> freeze)
    {
        try { return freeze(clipId, project); }
        catch (ArgumentException ex)
        {
            throw new BridgeExecutionException(new BridgeError("ValidationError", "编辑器工程快照失败", ex.Message));
        }
    }

    /// <summary>工程节点：保留已有裁剪起止（clamp 到媒体时长）；缺省/非法时才退回 0..duration。</summary>
    private static JsonObject ProjectNode(EditorProject project, double mediaDuration, int width, int height, string clipId)
    {
        var node = JsonSerializer.SerializeToNode(project, JsonOptions) as JsonObject ?? new JsonObject();
        var start = double.IsFinite(project.Start) && project.Start >= 0 && project.Start < mediaDuration ? project.Start : 0d;
        var end = double.IsFinite(project.End) && project.End > start ? Math.Min(project.End, mediaDuration) : mediaDuration;
        if (end <= start) { start = 0d; end = mediaDuration; }
        node["start"] = start;
        node["end"] = end;
        var sourceWidth = width > 0 ? width : 1920;
        var sourceHeight = height > 0 ? height : 1080;
        var targetHeight = project.Resolution == 0 ? sourceHeight : Math.Min(sourceHeight, project.Resolution);
        targetHeight = Math.Max(2, targetHeight / 2 * 2);
        node["width"] = Math.Max(2, (int)Math.Round(sourceWidth * (double)targetHeight / sourceHeight) / 2 * 2);
        node["height"] = targetHeight;
        // 与 EditorRenderService.Items 的旧工程语义一致：没有时间线就按 start/end 单片段裁剪，而不是整片。
        if (node["timeline"] is not JsonArray { Count: > 0 }) node["timeline"] = BuildTimeline(clipId, start, end);
        return node;
    }

    private static List<string> TimelineSources(JsonObject project, string clipId)
    {
        var ids = new List<string>();
        if (project["timeline"] is JsonArray timeline)
        {
            foreach (var item in timeline)
            {
                if (item is not JsonObject entry) continue;
                if (entry["clipId"]?.GetValueKind() != JsonValueKind.String) continue;
                var id = entry["clipId"]!.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
            }
        }
        if (ids.Count == 0) ids.Add(clipId);
        return ids;
    }


    private static JsonArray BuildTimeline(string clipId, double start, double end) =>
    [
        new JsonObject
        {
            ["id"] = "fcproj-clip",
            ["clipId"] = clipId,
            ["start"] = start,
            ["end"] = end,
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
        },
    ];

    private (double Duration, int Width, int Height) ProbeMedia(string path, CancellationToken token)
        => TryProbeMedia(path, token, out var info) ? info : (4, 1920, 1080);

    /// <summary>ffprobe 成功解析才返回 true；失败时哨兵值不会冒充真实尺寸。</summary>
    private bool TryProbeMedia(string path, CancellationToken token, out (double Duration, int Width, int Height) info)
    {
        info = default;
        var ffprobe = File.Exists(_ffprobePath) ? _ffprobePath : Path.Combine(Path.GetDirectoryName(_ffprobePath) ?? "", "ffprobe.exe");
        if (!File.Exists(ffprobe)) return false;
        var result = RunProcess(ffprobe, [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height:format=duration",
            "-of", "json", path,
        ], token);
        if (result.ExitCode != 0) return false;
        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var root = document.RootElement;
            var width = root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0
                ? streams[0].GetProperty("width").GetInt32() : 1920;
            var height = root.TryGetProperty("streams", out var streamList) && streamList.GetArrayLength() > 0
                ? streamList[0].GetProperty("height").GetInt32() : 1080;
            var duration = root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var durationNode)
                && double.TryParse(durationNode.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : 4;
            info = (duration, width, height);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<string> ReadWarnings(string mappingPath)
    {
        var warnings = new List<string>();
        if (!File.Exists(mappingPath)) return warnings;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(mappingPath)) is { } node) CollectWarnings(node, warnings);
        }
        catch (JsonException)
        {
            // mapping.json 只是调试副产物，解析失败不影响主流程。
        }
        return warnings;
    }

    private static void CollectWarnings(JsonNode node, List<string> warnings)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    if (pair.Key.Equals("warnings", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonArray array)
                    {
                        foreach (var item in array)
                        {
                            var text = item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : item?.ToJsonString();
                            if (!string.IsNullOrWhiteSpace(text) && warnings.Count < 20) warnings.Add(text);
                        }
                    }
                    else if (pair.Value is not null)
                    {
                        CollectWarnings(pair.Value, warnings);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    if (item is not null) CollectWarnings(item, warnings);
                break;
        }
    }

    private ProcessResult RunProcess(string file, IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(StepTimeout);
        var start = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BridgeExecutionException(new BridgeError("FilmCraftError", $"无法启动 {Path.GetFileName(file)}", ex.Message));
        }
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new BridgeExecutionException(new BridgeError("BridgeTimeout", $"{Path.GetFileName(file)} 超时", StepTimeout.ToString()));
        }
        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string Clip(string primary, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(primary) ? fallback : primary;
        text = text.Trim();
        return text.Length <= 800 ? text : text[..800];
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
