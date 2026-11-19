using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

[assembly: InternalsVisibleTo("LiveClipper.Tests")]

namespace LiveClipper.Core.Bridge;

/// <summary>
/// fcproj 导入失败分类：调用方按 Class 决定 HTTP 错误码（InvalidFormat/UnsupportedSchema → 400，MissingSequence/InvalidProject → 422）。
/// </summary>
public static class FcprojImportErrorClass
{
    public const string InvalidFormat = "InvalidFormat";
    public const string UnsupportedSchema = "UnsupportedSchema";
    public const string MissingSequence = "MissingSequence";
    public const string InvalidProject = "InvalidProject";
}

/// <summary>
/// 带明确错误分类的 fcproj 导入异常；不支持的结构应以该异常显式失败，而不是静默丢数据。
/// </summary>
public sealed class FcprojImportException(string errorClass,string message) : Exception(message)
{
    public string Class { get; } = errorClass;
}

/// <summary>
/// 导入选项：媒体路径 → LiveClipper clipId 的解析器（往返时由调用方把 fcproj 媒体路径映射到新库里的 clipId）。
/// </summary>
public sealed class FcprojImportOptions
{
    public Func<string,string?>? ClipIdByMediaPath { get; init; }
    /// <summary>LiveClipper 当前支持 6 条视频轨，超出部分计入 Ignored 而不是强行并轨。</summary>
    public int MaxTracks { get; init; } = 6;
}

/// <summary>
/// fcproj 导入结果：除 EditorProject 外，还携带无法映射的结构计数与警告，做到「不支持的项不静默丢」。
/// </summary>
public sealed class FcprojImportResult
{
    public EditorProject Project { get; init; } = new();
    public string SequenceName { get; init; } = "";
    public int SchemaVersion { get; init; }
    public int Fps { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>按类别统计的未映射结构数量；键始终存在（0 表示没有遇到）。</summary>
    public IReadOnlyDictionary<string,int> Ignored { get; init; } = new Dictionary<string,int>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>主序列里成功映射的视频片段媒体路径（按时间线顺序，去重由调用方决定）。</summary>
    public IReadOnlyList<string> MediaPaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 历史 .fcproj → EditorProject 转换，仅供内部诊断和兼容回归使用。
/// 生产工程交接方向固定为宿主 → .fcproj；应用层不得调用此转换器。
/// 保留映射代码供 W7 清理，不用于读取或验证导出的渲染输入。
/// </summary>
internal static class FcprojImporter
{
    public const double TicksPerSecond = 254016000000.0;
    public const int MaxSupportedSchemaVersion = FcprojExportService.SchemaVersion;

    private static readonly Dictionary<string,string> TransitionKinds = new(StringComparer.Ordinal) {
        ["cross_dissolve"] = "cross-dissolve",
        ["dip_to_black"] = "dip-to-black",
        ["dip_to_white"] = "dip-to-white",
    };

    private static readonly Dictionary<string,string> TransitionAligns = new(StringComparer.Ordinal) {
        ["CenterAtCut"] = "center",
        ["StartAtCut"] = "start",
        ["EndAtCut"] = "end",
    };

    private static readonly string[] IgnoredCategories = [
        "extraSequences","mediaItems","videoTracksBeyondLimit","mutedVideoTracks","audioTracks","audioClips",
        "captionTracks","captionCues","masks","keyframes","effects","blendModes","reversedClips","reversedTransitions",
        "frameHold","markers","orphanTransitions","unknownTransitions","unknownAligns","clipsWithoutMedia","clipsMissingId",
    ];

    private static readonly int[] SupportedFps = [0,24,25,30,50,60];

    public static FcprojImportResult ImportFile(string path,FcprojImportOptions? options=null) =>
        Import(File.ReadAllText(path),options);

    public static FcprojImportResult Import(string json,FcprojImportOptions? options=null)
    {
        options ??= new FcprojImportOptions();
        var ignored = IgnoredCategories.ToDictionary(x=>x,_=>0,StringComparer.Ordinal);
        var warnings = new List<string>();

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new FcprojImportException(FcprojImportErrorClass.InvalidFormat,"fcproj 根节点不是 JSON 对象");
        }
        catch(JsonException ex)
        {
            throw new FcprojImportException(FcprojImportErrorClass.InvalidFormat,$"fcproj JSON 解析失败：{ex.Message}");
        }

        var format = Str(root["format"]);
        if(!string.Equals(format,"filmcraft.project",StringComparison.Ordinal))
            throw new FcprojImportException(FcprojImportErrorClass.InvalidFormat,$"不支持的 fcproj 格式：{format ?? "(缺少 format)"}，期望 filmcraft.project");
        var schema = Int(root["schema_version"])
            ?? throw new FcprojImportException(FcprojImportErrorClass.InvalidFormat,"缺少 schema_version");
        if(schema<=0)
            throw new FcprojImportException(FcprojImportErrorClass.InvalidFormat,$"非法 schema_version：{schema}");
        if(schema>MaxSupportedSchemaVersion)
            throw new FcprojImportException(FcprojImportErrorClass.UnsupportedSchema,$"fcproj schema v{schema} 高于当前支持的 v{MaxSupportedSchemaVersion}");

        var projectNode = Obj(root["project"])
            ?? throw new FcprojImportException(FcprojImportErrorClass.InvalidProject,"缺少 project 对象");
        var items = Obj(projectNode["items"])
            ?? throw new FcprojImportException(FcprojImportErrorClass.InvalidProject,"缺少 project.items");

        var sequenceKeys = items.Where(pair => Obj(Obj(Obj(pair.Value)?["kind"])?["Sequence"]) is not null)
            .Select(pair=>pair.Key).ToList();
        if(sequenceKeys.Count==0)
            throw new FcprojImportException(FcprojImportErrorClass.MissingSequence,"fcproj 中没有可识别的主序列（kind.Sequence）");
        if(sequenceKeys.Count>1) ignored["extraSequences"] += sequenceKeys.Count-1;

        var sequenceKey = SelectSequenceKey(root,projectNode,sequenceKeys);
        var sequenceItem = Obj(items[sequenceKey])!;
        var sequence = Obj(Obj(sequenceItem["kind"])!["Sequence"])!;
        var sequenceName = Str(sequenceItem["name"]) ?? Str(sequenceItem["label"]) ?? "";

        var settings = Obj(sequence["settings"]) ?? new JsonObject();
        var width = (int)Math.Round(Double(settings["width"]) ?? 0);
        var height = (int)Math.Round(Double(settings["height"]) ?? 0);
        var fps = ResolveFps(settings);
        if(!SupportedFps.Contains(fps)) warnings.Add($"序列帧率 {fps}fps 不在 LiveClipper 支持的集合内，渲染按实际素材帧率处理");

        var timeline = new List<TimelineItem>();
        var transitions = new List<EditorTransition>();
        var mediaPaths = new List<string>();
        var itemIdByFcClipId = new Dictionary<string,string>(StringComparer.Ordinal);
        var clipOrdinal = 0;
        var effectStats = new ClipEffectStats();

        var videoTracks = Arr(sequence["video_tracks"]) ?? new JsonArray();
        for(var trackIndex=0;trackIndex<videoTracks.Count;trackIndex++)
        {
            var track = Obj(videoTracks[trackIndex]);
            if(track is null) continue;
            var trackNumber = trackIndex+1;
            var clips = Arr(track["items"]) ?? new JsonArray();
            if(trackNumber>options.MaxTracks)
            {
                ignored["videoTracksBeyondLimit"] += clips.Count;
                continue;
            }
            if(Bool(track["muted"])==true) ignored["mutedVideoTracks"]++;

            foreach(var clipNode in clips)
            {
                var clip = Obj(clipNode);
                if(clip is null) continue;
                var fcClipId = KeyOf(clip["id"]);
                if(fcClipId is null) { ignored["clipsMissingId"]++; continue; }
                var mediaPath = ResolveMediaPath(items,clip["item"]);
                if(string.IsNullOrWhiteSpace(mediaPath)) { ignored["clipsWithoutMedia"]++; continue; }
                mediaPaths.Add(mediaPath);

                var clipId = options.ClipIdByMediaPath?.Invoke(mediaPath);
                if(string.IsNullOrWhiteSpace(clipId))
                {
                    clipId = mediaPath;
                    warnings.Add($"媒体路径未映射到 clipId，回退为绝对路径：{mediaPath}");
                }

                var sourceSeconds = TicksToSeconds(clip["source_in"]);
                var durationSeconds = TicksToSeconds(clip["duration"]);
                var speed = Double(clip["speed"]) ?? 1;
                if(speed<=0) { speed = 1; warnings.Add($"片段 {fcClipId} speed={Str(clip["speed"]) ?? "0"} 非法，按 1 处理"); }
                if(Bool(clip["reverse"])==true) ignored["reversedClips"]++;
                if(clip["frame_hold"] is not null) ignored["frameHold"]++;
                ignored["markers"] += Arr(clip["markers"])?.Count ?? 0;

                var outputDuration = durationSeconds * speed;
                var effects = ReadEffects(clip,width,height,sourceSeconds,outputDuration,speed,effectStats,ignored,warnings);
                var zoom = effects.Zoom;
                var rotation = effects.Rotation;
                var positionX = effects.PositionX;
                var positionY = effects.PositionY;
                var opacity = effects.Opacity;
                var volumeDb = Double(clip["gain_db"]) ?? 0;
                var disabled = Bool(clip["enabled"])==false;
                var itemId = $"fc-{++clipOrdinal}";
                itemIdByFcClipId[fcClipId] = itemId;

                timeline.Add(new TimelineItem(
                    Id: itemId,
                    ClipId: clipId,
                    Start: sourceSeconds,
                    End: sourceSeconds + durationSeconds * speed,
                    Zoom: zoom,
                    Speed: speed,
                    Track: trackNumber,
                    Rotation: rotation,
                    Opacity: opacity,
                    PositionX: positionX,
                    PositionY: positionY,
                    Disabled: disabled,
                    Masks: effects.Masks,
                    Keyframes: effects.Keyframes,
                    VolumeDb: volumeDb));
            }

            foreach(var transitionNode in Arr(track["transitions"]) ?? new JsonArray())
            {
                var transition = Obj(transitionNode);
                if(transition is null) continue;
                var transitionId = KeyOf(transition["id"]) ?? "?";
                var effectName = TransitionEffectName(transition["effect"]);
                if(effectName is null || !TransitionKinds.TryGetValue(effectName,out var kind))
                {
                    ignored["unknownTransitions"]++;
                    warnings.Add($"转场 {transitionId} 的 effect={effectName ?? "(缺少)"} 暂不支持，已跳过");
                    continue;
                }
                var alignRaw = Str(transition["align"]);
                if(alignRaw is null || !TransitionAligns.TryGetValue(alignRaw,out var align))
                {
                    ignored["unknownAligns"]++;
                    warnings.Add($"转场 {transitionId} 的 align={alignRaw ?? "(缺少)"} 暂不支持，已跳过");
                    continue;
                }
                if(Bool(transition["reverse"])==true) ignored["reversedTransitions"]++;
                var toKey = KeyOf(transition["to"]);
                if(toKey is null || !itemIdByFcClipId.TryGetValue(toKey,out var toItemId))
                {
                    ignored["orphanTransitions"]++;
                    continue;
                }
                transitions.Add(new EditorTransition(
                    Id: $"fc-transition-{transitionId}",
                    ItemId: toItemId,
                    Track: trackNumber,
                    Kind: kind,
                    Duration: TicksToSeconds(transition["duration"]),
                    Align: align));
            }
        }

        var audioTracks = Arr(sequence["audio_tracks"]) ?? new JsonArray();
        ignored["audioTracks"] += audioTracks.Count;
        foreach(var audioTrack in audioTracks) ignored["audioClips"] += Arr(Obj(audioTrack)?["items"])?.Count ?? 0;

        var captionTracks = Arr(sequence["caption_tracks"]) ?? new JsonArray();
        ignored["captionTracks"] += captionTracks.Count;
        foreach(var captionTrack in captionTracks) ignored["captionCues"] += Arr(Obj(captionTrack)?["captions"])?.Count ?? 0;
        if(captionTracks.Count>0) warnings.Add($"fcproj 含 {captionTracks.Count} 条字幕轨，LiveClipper 字幕来自转写数据，已忽略");

        if(effectStats.MasksImported>0) warnings.Add($"已导入 {effectStats.MasksImported} 个蒙版");
        if(effectStats.MasksUnsupported>0) warnings.Add($"{effectStats.MasksUnsupported} 个蒙版暂不支持（inverted/非矩形椭圆），已跳过");
        if(effectStats.MasksTruncated>0) warnings.Add($"单片蒙版超过 LiveClipper 上限 4 条，已截断 {effectStats.MasksTruncated} 条");
        if(effectStats.MasksRounded>0) warnings.Add($"{effectStats.MasksRounded} 个圆角蒙版暂不支持圆角，已按直角矩形导入");
        if(effectStats.KeyframesImported>0) warnings.Add($"已导入 {effectStats.KeyframesImported} 条关键帧");
        if(effectStats.KeyframesDegraded>0) warnings.Add($"{effectStats.KeyframesDegraded} 条关键帧插值不是 Linear，已降级为线性插值");

        var ordered = timeline.OrderBy(x=>x.Track).ThenBy(x=>x.Start).ToArray();
        var editorProject = new EditorProject {
            Start = 0,
            End = ordered.Length>0 ? ordered.Max(x=>x.End) : 0,
            Timeline = ordered,
            Transitions = transitions.Count>0 ? transitions.ToArray() : null,
            Resolution = 0,
            Fps = fps,
        };

        return new FcprojImportResult {
            Project = editorProject,
            SequenceName = sequenceName,
            SchemaVersion = schema,
            Fps = fps,
            Width = width,
            Height = height,
            Ignored = ignored.Where(pair=>pair.Value>0).ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.Ordinal),
            Warnings = warnings,
            MediaPaths = mediaPaths,
        };
    }

    private static string SelectSequenceKey(JsonObject root,JsonObject project,IReadOnlyList<string> sequenceKeys)
    {
        var active = root["view"] is JsonObject view ? KeyOf(view["active_sequence"]) : null;
        if(active is not null && sequenceKeys.Contains(active)) return active;
        if(project["root"] is JsonObject rootNode && rootNode["children"] is JsonArray children)
            foreach(var child in children)
            {
                var itemKey = KeyOf(Obj(child)?["Item"]);
                if(itemKey is not null && sequenceKeys.Contains(itemKey)) return itemKey;
            }
        return sequenceKeys[0];
    }

    private static int ResolveFps(JsonObject settings)
    {
        if(Obj(settings["frame_rate"]) is { } frameRate)
        {
            var num = Double(frameRate["num"]);
            var den = Double(frameRate["den"]);
            if(num>0 && den>0)
            {
                var fps = (int)Math.Round(num.Value/den.Value);
                if(fps>0) return fps;
            }
        }
        var raw = Double(settings["fps"]);
        return raw is >0 ? (int)Math.Round(raw.Value) : 30;
    }

    private static string? ResolveMediaPath(JsonObject items,JsonNode? mediaItemRef)
    {
        var mediaKey = KeyOf(mediaItemRef);
        if(mediaKey is null) return null;
        var mediaItem = Obj(items[mediaKey]);
        var media = Obj(Obj(Obj(mediaItem?["kind"])?["Media"])?["media"]);
        if(media is null) return null;
        var file = media["File"];
        var path = file is JsonObject fileObj ? Str(fileObj["path"]) : Str(file);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private sealed class ClipEffectStats
    {
        public int MasksImported;
        public int MasksUnsupported;
        public int MasksTruncated;
        public int MasksRounded;
        public int KeyframesImported;
        public int KeyframesDegraded;
    }

    private sealed record ClipEffects(
        double Zoom,double Rotation,double PositionX,double PositionY,double Opacity,
        MaskInstance[]? Masks,Dictionary<string,ParamKeyframe[]>? Keyframes);

    /// <summary>
    /// 解析 clip 的 motion/opacity 效果：静态变换 + 关键帧 + 蒙版。
    /// 关键帧时间是「源素材 ticks」：source_tick = source_in + round(round(output_rel*fps)*speed/fps)；
    /// 反解时按 (tick/TPS - source_in)/speed 还原输出相对秒，再交给 EditorRenderService 归一化（clamp/取整/去重/≤64）。
    /// </summary>
    private static ClipEffects ReadEffects(JsonObject clip,int width,int height,double sourceInSeconds,double outputDuration,double speed,ClipEffectStats stats,Dictionary<string,int> ignored,List<string> warnings)
    {
        double zoom = 1,rotation = 0,positionX = 0,positionY = 0,opacity = 1;
        var masks = new List<MaskInstance>();
        var keyframes = new Dictionary<string,List<ParamKeyframe>>(StringComparer.Ordinal);
        var fcClipId = KeyOf(clip["id"]) ?? "?";
        foreach(var effectNode in Arr(clip["effects"]) ?? new JsonArray())
        {
            var effect = Obj(effectNode);
            var effectId = effect is null ? null : Str(effect["effect"]);
            if(effect is null || effectId is null) continue;
            var parameters = Obj(effect["params"]) ?? new JsonObject();
            if(effectId=="motion")
            {
                var scaleParam = Obj(parameters["scale"]);
                var scale = Double(Obj(scaleParam?["value"])?["Float"]);
                if(scale is not null) zoom = Math.Clamp(scale.Value/100.0,0.1,10);
                var rotationParam = Obj(parameters["rotation"]);
                var rotationValue = Double(Obj(rotationParam?["value"])?["Float"]);
                if(rotationValue is not null) rotation = Math.Clamp(rotationValue.Value,-180,180);
                var positionParam = Obj(parameters["position"]);
                var positionValue = Obj(Obj(positionParam?["value"])?["Vec2"]);
                if(positionValue is not null && width>0 && height>0)
                {
                    var x = Double(positionValue["x"]) ?? width/2.0;
                    var y = Double(positionValue["y"]) ?? height/2.0;
                    positionX = Math.Clamp((x-width/2.0)/width,-0.5,0.5);
                    positionY = Math.Clamp((y-height/2.0)/height,-0.5,0.5);
                }
                ReadParamKeyframes(scaleParam,"scale",sourceInSeconds,outputDuration,speed,stats,keyframes,
                    value => Double(Obj(value)?["Float"]) is { } raw ? raw/100.0 : null);
                ReadParamKeyframes(rotationParam,"rotation",sourceInSeconds,outputDuration,speed,stats,keyframes,
                    value => Double(Obj(value)?["Float"]));
                ReadParamKeyframes(positionParam,"positionX",sourceInSeconds,outputDuration,speed,stats,keyframes,
                    value => Vec2Component(value,"x") is { } px && width>0 ? Math.Clamp((px-width/2.0)/width,-0.5,0.5) : null);
                ReadParamKeyframes(positionParam,"positionY",sourceInSeconds,outputDuration,speed,stats,keyframes,
                    value => Vec2Component(value,"y") is { } py && height>0 ? Math.Clamp((py-height/2.0)/height,-0.5,0.5) : null);
                continue;
            }
            if(effectId=="opacity")
            {
                var opacityParam = Obj(parameters["opacity"]);
                var value = Double(Obj(opacityParam?["value"])?["Float"]);
                if(value is not null) opacity = Math.Clamp(value.Value/100.0,0,1);
                ReadParamKeyframes(opacityParam,"opacity",sourceInSeconds,outputDuration,speed,stats,keyframes,
                    node => Double(Obj(node)?["Float"]) is { } raw ? raw/100.0 : null);
                var blend = Int(Obj(Obj(parameters["blend"])?["value"])?["Choice"]);
                if(blend is >0) ignored["blendModes"]++;
                ReadMasks(effect,width,height,zoom,rotation,positionX,positionY,masks,stats,warnings,fcClipId);
                continue;
            }
            ignored["effects"]++;
        }
        return new ClipEffects(
            zoom,rotation,positionX,positionY,opacity,
            masks.Count>0 ? masks.ToArray() : null,
            keyframes.Count>0 ? keyframes.ToDictionary(pair=>pair.Key,pair=>pair.Value.ToArray(),StringComparer.Ordinal) : null);
    }

    private static void ReadParamKeyframes(JsonObject? param,string prop,double sourceInSeconds,double outputDuration,double speed,ClipEffectStats stats,Dictionary<string,List<ParamKeyframe>> target,Func<JsonNode?,double?> convert)
    {
        var points = Arr(param?["keyframes"]);
        if(points is null || points.Count==0) return;
        var raw = new List<ParamKeyframe>(points.Count);
        foreach(var node in points)
        {
            var keyframe = Obj(node);
            if(keyframe is null) continue;
            var tick = Double(keyframe["time"]);
            if(tick is null) continue;
            var value = convert(keyframe["value"]);
            if(value is null) continue;
            var outputTime = (tick.Value/TicksPerSecond - sourceInSeconds)/(speed>0 ? speed : 1);
            var interp = Str(keyframe["interp"]);
            if(interp is not null && !string.Equals(interp,"Linear",StringComparison.OrdinalIgnoreCase)) stats.KeyframesDegraded++;
            raw.Add(new ParamKeyframe(outputTime,value.Value));
        }
        var normalized = EditorRenderService.NormalizeParamKeyframes(prop,raw,outputDuration);
        if(normalized.Length==0) return;
        stats.KeyframesImported += normalized.Length;
        target[prop] = [.. normalized];
    }

    private static double? Vec2Component(JsonNode? valueNode,string axis)
    {
        var vec = Obj(Obj(valueNode)?["Vec2"]);
        return vec is null ? null : Double(vec[axis]);
    }

    /// <summary>
    /// 蒙版映射：FilmCraft 侧只有 opacity effect 的 masks[]（4 顶点贝塞尔 Path + feather/opacity），
    /// 因此按 path 反推形状：切向≈0 → rect；切向与半径垂直 → ellipse；其余按圆角矩形降级为 rect。
    /// path 坐标在源素材像素域：逆变换 p_source → canvas：canvas = position + R(rotation)·(zoom·(p - anchor))，
    /// anchor 取序列中心（与 filmcraft-b1-map.mjs 的 maskPathToSource 互逆；序列与素材同尺寸时无损）。
    /// </summary>
    private static void ReadMasks(JsonObject opacityEffect,int width,int height,double zoom,double rotation,double positionX,double positionY,List<MaskInstance> masks,ClipEffectStats stats,List<string> warnings,string clipId)
    {
        var maskArr = Arr(opacityEffect["masks"]);
        if(maskArr is null || maskArr.Count==0) return;
        foreach(var node in maskArr)
        {
            if(masks.Count>=4) { stats.MasksTruncated++; continue; }
            var mask = Obj(node);
            if(mask is null) continue;
            if(Bool(mask["inverted"])==true)
            {
                stats.MasksUnsupported++;
                warnings.Add($"片段 {clipId} 的蒙版 inverted 暂不支持，已跳过");
                continue;
            }
            var id = MaskIdFromName(Str(mask["name"])) ?? $"mask-{masks.Count+1}";
            var feather = Double(Obj(Obj(mask["feather"])?["value"])?["Float"]) ?? 0;
            var opacity = (Double(Obj(Obj(mask["opacity"])?["value"])?["Float"]) ?? 100)/100.0;
            var instance = MaskFromPath(mask,width,height,zoom,rotation,positionX,positionY,id,feather,opacity,stats)
                ?? MaskFromLegacyShape(mask,width,height,id,feather,opacity);
            if(instance is null) { stats.MasksUnsupported++; continue; }
            masks.Add(instance);
            stats.MasksImported++;
        }
    }

    private static MaskInstance? MaskFromPath(JsonObject mask,int width,int height,double zoom,double rotation,double positionX,double positionY,string id,double feather,double opacity,ClipEffectStats stats)
    {
        var pathNode = Obj(Obj(Obj(mask["path"])?["value"])?["Path"]);
        var vertices = Arr(pathNode?["vertices"]);
        if(vertices is null || vertices.Count<3 || width<=0 || height<=0) return null;
        var source = new List<(double X,double Y,double TX,double TY)>(vertices.Count);
        foreach(var node in vertices)
        {
            var vertex = Obj(node);
            var point = Obj(vertex?["p"]);
            if(point is null) return null;
            var x = Double(point["x"]); var y = Double(point["y"]);
            if(x is null || y is null) return null;
            var tangent = Obj(vertex?["t_out"]);
            source.Add((x.Value,y.Value,Double(tangent?["x"]) ?? 0,Double(tangent?["y"]) ?? 0));
        }
        var rad = rotation*Math.PI/180.0;
        var cos = Math.Cos(rad); var sin = Math.Sin(rad);
        var canvas = source.Select(vertex => {
            var dx = (vertex.X-width/2.0)*zoom;
            var dy = (vertex.Y-height/2.0)*zoom;
            return (X: width/2.0 + positionX*width + dx*cos - dy*sin,
                    Y: height/2.0 + positionY*height + dx*sin + dy*cos,
                    TX: vertex.TX, TY: vertex.TY);
        }).ToList();
        var cx = canvas.Average(p=>p.X);
        var cy = canvas.Average(p=>p.Y);
        var tangentMean = source.Average(vertex=>Math.Sqrt(vertex.TX*vertex.TX + vertex.TY*vertex.TY));
        var perpendicular = 0.0;
        foreach(var (point,vertex) in canvas.Zip(source))
        {
            var radius = Math.Sqrt(Math.Pow(point.X-cx,2)+Math.Pow(point.Y-cy,2));
            var tangentLength = Math.Sqrt(vertex.TX*vertex.TX + vertex.TY*vertex.TY);
            if(radius<=1e-6 || tangentLength<=1e-6) continue;
            var dot = ((point.X-cx)/radius)*vertex.TX/tangentLength + ((point.Y-cy)/radius)*vertex.TY/tangentLength;
            perpendicular += Math.Abs(dot);
        }
        perpendicular /= Math.Max(1,canvas.Count);

        var type = "rect";
        if(tangentMean>0.5)
        {
            if(perpendicular<0.3) type = "ellipse";
            else stats.MasksRounded++;
        }
        double w,h,angle;
        if(type=="ellipse" && canvas.Count>=4)
        {
            var ux = canvas[1].X-canvas[3].X; var uy = canvas[1].Y-canvas[3].Y;
            var vx = canvas[2].X-canvas[0].X; var vy = canvas[2].Y-canvas[0].Y;
            w = Math.Sqrt(ux*ux+uy*uy)/width;
            h = Math.Sqrt(vx*vx+vy*vy)/height;
            angle = Math.Atan2(uy,ux)*180.0/Math.PI;
        }
        else
        {
            var ux = canvas[1].X-canvas[0].X; var uy = canvas[1].Y-canvas[0].Y;
            var vx = canvas[3].X-canvas[0].X; var vy = canvas[3].Y-canvas[0].Y;
            w = Math.Sqrt(ux*ux+uy*uy)/width;
            h = Math.Sqrt(vx*vx+vy*vy)/height;
            angle = Math.Atan2(uy,ux)*180.0/Math.PI;
        }
        return new MaskInstance(
            id,type,true,
            Clamp((cx-width/2.0)/width,-.5,.5,0),
            Clamp((cy-height/2.0)/height,-.5,.5,0),
            Clamp(w,.01,2,.6),Clamp(h,.01,2,.6),
            Clamp(angle,-180,180,0),
            Clamp(feather,0,100,0),
            Clamp(opacity,0,1,1));
    }

    private static MaskInstance? MaskFromLegacyShape(JsonObject mask,int width,int height,string id,double feather,double opacity)
    {
        var typeRaw = Str(mask["type"]);
        if(typeRaw is null) return null;
        var type = typeRaw.Equals("ellipse",StringComparison.OrdinalIgnoreCase) ? "ellipse" : typeRaw.Equals("rect",StringComparison.OrdinalIgnoreCase) ? "rect" : null;
        if(type is null) return null;
        var center = Obj(mask["center"]);
        var size = Obj(mask["size"]);
        var x = center is not null && Double(center["x"]) is { } centerX && width>0 ? (centerX-width/2.0)/width : Double(mask["x"]) ?? 0;
        var y = center is not null && Double(center["y"]) is { } centerY && height>0 ? (centerY-height/2.0)/height : Double(mask["y"]) ?? 0;
        var w = size is not null && Double(size["w"]) is { } sizeW && width>0 ? sizeW/width : Double(mask["w"]) ?? .6;
        var h = size is not null && Double(size["h"]) is { } sizeH && height>0 ? sizeH/height : Double(mask["h"]) ?? .6;
        var rotationValue = Double(mask["rotation"]) ?? 0;
        return new MaskInstance(id,type,Bool(mask["enabled"])!=false,
            Clamp(x,-.5,.5,0),Clamp(y,-.5,.5,0),Clamp(w,.01,2,.6),Clamp(h,.01,2,.6),
            Clamp(rotationValue,-180,180,0),Clamp(feather,0,100,0),Clamp(opacity,0,1,1));
    }

    private static string? MaskIdFromName(string? name)
    {
        if(string.IsNullOrWhiteSpace(name)) return null;
        var open = name.IndexOf('(');
        var close = name.LastIndexOf(')');
        if(open<0 || close<=open+1) return null;
        var id = name[(open+1)..close].Trim();
        return id.Length==0 ? null : id;
    }

    private static double Clamp(double value,double min,double max,double fallback)=>
        double.IsFinite(value) ? Math.Clamp(value,min,max) : fallback;

    private static string? TransitionEffectName(JsonNode? effectNode)
    {
        if(effectNode is JsonObject effectObject) return Str(effectObject["effect"]);
        return Str(effectNode);
    }

    private static double TicksToSeconds(JsonNode? ticks) => (Double(ticks) ?? 0)/TicksPerSecond;

    private static double? Double(JsonNode? node)
    {
        if(node is not JsonValue value) return null;
        if(value.TryGetValue<double>(out var number)) return number;
        if(value.TryGetValue<long>(out var longValue)) return longValue;
        if(value.TryGetValue<string>(out var text) && double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var parsed)) return parsed;
        return null;
    }

    private static int? Int(JsonNode? node)
    {
        var value = Double(node);
        return value is null ? null : (int)Math.Round(value.Value);
    }

    private static bool? Bool(JsonNode? node)
    {
        if(node is not JsonValue value) return null;
        if(value.TryGetValue<bool>(out var flag)) return flag;
        if(value.TryGetValue<string>(out var text)) return bool.TryParse(text,out var parsed) ? parsed : null;
        return null;
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject? Obj(JsonNode? node) => node as JsonObject;

    private static JsonArray? Arr(JsonNode? node) => node as JsonArray;

    /// <summary>JSON 数字/字符串 id 统一转字符串键；用于 items 字典和 clip/transition 引用。</summary>
    private static string? KeyOf(JsonNode? node)
    {
        if(node is not JsonValue value) return null;
        if(value.TryGetValue<long>(out var longValue)) return longValue.ToString(CultureInfo.InvariantCulture);
        if(value.TryGetValue<int>(out var intValue)) return intValue.ToString(CultureInfo.InvariantCulture);
        if(value.TryGetValue<double>(out var doubleValue) && doubleValue==Math.Floor(doubleValue)) return ((long)doubleValue).ToString(CultureInfo.InvariantCulture);
        if(value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) return text;
        return null;
    }
}
