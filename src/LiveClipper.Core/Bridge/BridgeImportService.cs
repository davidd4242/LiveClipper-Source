using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core.Bridge;

/// <summary>
/// POST /api/bridge/import 的请求体：fcprojPath 必填；clipMapping 为可选的「fcproj 媒体路径 → LiveClipper clipId」覆盖表，
/// 未提供的条目会先回退到 targetClipId（UI 的目标切片下拉），再按绝对路径或文件名自动匹配现有切片。
/// </summary>
public sealed record BridgeImportRequest(
    string? FcprojPath = null,
    Dictionary<string, string>? ClipMapping = null,
    string? TargetClipId = null);

/// <summary>导入成功的摘要；projectId 是保存 editor project 的主 clipId（与前端 editor 路由一致）。</summary>
public sealed record BridgeImportSummary(
    string ProjectId,
    string TargetClipId,
    int TimelineItems,
    double DurationSeconds,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, int> Ignored,
    IReadOnlyDictionary<string, string> ResolvedMedia);

/// <summary>导入失败分类：InvalidFormat/UnmappedMedia/ValidationError → 400，UnsupportedSchema/MissingSequence/InvalidProject → 422。</summary>
public sealed class BridgeImportException(string errorClass, string message, string? detail = null) : Exception(message)
{
    public string Class { get; } = errorClass;
    public string? Detail { get; } = detail;
}

/// <summary>
/// FilmCraft 工程 → LC editor project 的落库服务：解析 .fcproj、把媒体路径映射到现有切片、校验后写入目标切片的 editor project。
/// 纯 Core 实现（可单测），端点层只做 HTTP 包装与错误码映射。
/// </summary>
internal sealed class BridgeImportService
{
    private readonly Func<IReadOnlyList<Clip>> _clips;
    private readonly Func<string, string?> _project;
    private readonly Action<string, string> _saveProject;

    public BridgeImportService(
        Func<IReadOnlyList<Clip>> clips,
        Func<string, string?> project,
        Action<string, string> saveProject)
    {
        _clips = clips;
        _project = project;
        _saveProject = saveProject;
    }

    public BridgeImportSummary Import(BridgeImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FcprojPath))
            throw new BridgeImportException("ValidationError", "缺少 fcprojPath", "请求体需要 { fcprojPath, clipMapping? }");
        string path;
        try
        {
            path = Path.GetFullPath(request.FcprojPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new BridgeImportException("ValidationError", "fcprojPath 不是合法路径", ex.Message);
        }
        if (!File.Exists(path))
            throw new BridgeImportException("ValidationError", "找不到 .fcproj 文件", path);

        var clips = _clips();
        var clipById = new Dictionary<string, Clip>(StringComparer.Ordinal);
        foreach (var clip in clips) clipById[clip.Id] = clip;
        var mapping = NormalizeMapping(request.ClipMapping);
        var targetClipId = string.IsNullOrWhiteSpace(request.TargetClipId) ? null : request.TargetClipId.Trim();
        if (targetClipId is not null && !clipById.ContainsKey(targetClipId))
            throw new BridgeImportException("ValidationError", "目标切片不存在", targetClipId);

        string? Resolve(string mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return null;
            if (mapping.TryGetValue(NormalizeKey(mediaPath), out var mapped))
                return clipById.ContainsKey(mapped) ? mapped : null;
            if (targetClipId is not null) return targetClipId;
            foreach (var clip in clips)
            {
                if (SamePath(clip.Path, mediaPath)) return clip.Id;
                if (string.Equals(FileName(clip.Path), FileName(mediaPath), StringComparison.OrdinalIgnoreCase)) return clip.Id;
            }
            return null;
        }

        FcprojImportResult imported;
        try
        {
            imported = FcprojImporter.ImportFile(path, new FcprojImportOptions { ClipIdByMediaPath = Resolve });
        }
        catch (FcprojImportException ex)
        {
            throw new BridgeImportException(ex.Class, ex.Message, ex.Class);
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var media in imported.MediaPaths)
        {
            var clipId = Resolve(media);
            if (clipId is null) missing.Add(media);
            else resolved[media] = clipId;
        }
        if (missing.Count > 0)
            throw new BridgeImportException(
                "UnmappedMedia",
                $"有 {missing.Count} 个媒体路径没有对应的本地切片",
                string.Join("\n", missing));

        var project = NormalizeForEditor(imported.Project, out var normalizeWarnings);
        var warnings = imported.Warnings.Concat(normalizeWarnings).ToList();
        var target = ResolveTargetClip(project, clipById, resolved);
        if (_project(target) is not null)
            warnings.Add("已覆盖目标切片原有的 editor project（旧版本已备份为 project revision）");
        try
        {
            EditorRenderService.Validate(project);
        }
        catch (ArgumentException ex)
        {
            throw new BridgeImportException("InvalidProject", "导入的工程未通过编辑器校验", ex.Message);
        }

        _saveProject(target, JsonSerializer.Serialize(project));
        var timelineItems = project.Timeline?.Length ?? 0;
        // 摘要里的时长与编辑器时间线一致：LC 的转场会在输出时间线上压缩重叠，不能直接用 project.End（源区间末端）。
        var timeline = project.Timeline ?? [];
        var cuts = timeline.Length == 0 ? [] : EditorRenderService.ResolveTransitions(timeline, project.Transitions, project.Fps);
        var outputDuration = timeline.Length == 0
            ? 0
            : (cuts.Length == 0 ? EditorRenderService.TimelineDuration(timeline) : EditorRenderService.SequenceDuration(timeline, cuts));
        var transitionCompression = cuts.Sum(EditorRenderService.CutShift);
        if (transitionCompression > 0.05)
            warnings.Add($"转场在输出时间线上压缩了 {Math.Round(transitionCompression, 3)}s：时间线 {Math.Round(outputDuration, 3)}s（源区间末端 {Math.Round(project.End, 3)}s）");
        return new BridgeImportSummary(
            ProjectId: target,
            TargetClipId: target,
            TimelineItems: timelineItems,
            DurationSeconds: Math.Round(outputDuration, 3),
            Warnings: warnings.ToArray(),
            Ignored: imported.Ignored,
            ResolvedMedia: resolved);
    }

    /// <summary>LC 编辑器只接受 0.25–4× 速度与 1–2× 缩放；越界时钳制并计入 warning，不静默改数据。</summary>
    private static EditorProject NormalizeForEditor(EditorProject project, out List<string> warnings)
    {
        warnings = [];
        if (project.Timeline is not { Length: > 0 }) return project;
        var normalized = new TimelineItem[project.Timeline.Length];
        for (var index = 0; index < project.Timeline.Length; index++)
        {
            var item = project.Timeline[index];
            var speed = item.Speed;
            var end = item.End;
            if (!double.IsFinite(speed) || speed is < 0.25 or > 4)
            {
                var clamped = double.IsFinite(speed) ? Math.Clamp(speed, 0.25, 4) : 1;
                var exported = end - item.Start;
                end = item.Start + (speed > 0 && double.IsFinite(exported) ? exported * clamped / speed : exported);
                warnings.Add($"片段 {item.Id} 速度 {speed:0.##}× 超出 0.25–4×，已钳制为 {clamped:0.##}×");
                speed = clamped;
            }
            var zoom = item.Zoom;
            if (!double.IsFinite(zoom) || zoom is < 1 or > 2)
            {
                var clamped = double.IsFinite(zoom) ? Math.Clamp(zoom, 1, 2) : 1;
                warnings.Add($"片段 {item.Id} 缩放 {zoom:0.##}× 超出 LC 的 1–2×，已钳制为 {clamped:0.##}×");
                zoom = clamped;
            }
            normalized[index] = item with { Speed = speed, Zoom = zoom, End = end };
        }
        return project with { Timeline = normalized };
    }

    private string ResolveTargetClip(EditorProject project, IReadOnlyDictionary<string, Clip> clipById, IReadOnlyDictionary<string, string> resolved)
    {
        var ordered = project.Timeline ?? [];
        var primary = ordered.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ClipId) && clipById.ContainsKey(item.ClipId))?.ClipId
            ?? resolved.Values.FirstOrDefault(clipById.ContainsKey);
        if (string.IsNullOrWhiteSpace(primary))
            throw new BridgeImportException("UnmappedMedia", "工程中没有可落地到本地切片的片段", "请先用 /api/studio/import-paths 导入媒体，或提供 clipMapping");
        var existing = _project(primary);
        if (existing is null)
        {
            // 首次导入：不额外建记录，空工程由前端按主 clip 打开；这里仅记录一次可读日志。
        }
        return primary;
    }

    private static Dictionary<string, string> NormalizeMapping(Dictionary<string, string>? mapping)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mapping is null) return normalized;
        foreach (var pair in mapping)
        {
            var key = NormalizeKey(pair.Key);
            if (key.Length == 0 || string.IsNullOrWhiteSpace(pair.Value)) continue;
            normalized[key] = pair.Value.Trim();
        }
        return normalized;
    }

    private static string NormalizeKey(string value) =>
        OperatingSystem.IsWindows()
            ? value.Trim().Replace('/', '\\').ToLowerInvariant()
            : value.Trim().ToLowerInvariant();

    private static string FileName(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        var index = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            var a = Path.GetFullPath(left);
            var b = Path.GetFullPath(right);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
