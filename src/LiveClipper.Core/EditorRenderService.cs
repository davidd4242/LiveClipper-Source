using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed partial record TimelineItem(string Id,string ClipId,double Start,double End,string Kind="video",double Zoom=1,string Filter="none",string Motion="none",string? StickerAssetId=null,double StickerX=.72,double StickerY=.12,double StickerWidth=.22,string? SoundAssetId=null,double StickerRotation=0,double GapBefore=0,double Speed=1,int Track=1,double Rotation=0,double Opacity=1,double PositionX=0,double PositionY=0,bool Disabled=false,double VolumeDb=0,string? LinkGroup=null,bool AudioDetached=false,string? TitleText=null,double TitleSize=48,string TitleColor="#FFFFFF",double TitleX=.5,double TitleY=.82,double FadeIn=0,double FadeOut=0,[property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] ColorGrade? Grade=null,[property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] VolumeKeyframe[]? VolumeKeyframes=null,[property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] AudioEffectInstance[]? Effects=null,[property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? NestedSequenceId=null,[property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Dictionary<string,ParamKeyframe[]>? Keyframes=null,[property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] MaskInstance[]? Masks=null,[property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] double? SequenceStart=null);
/// <summary>
/// 音量包络关键帧（rubber band）：Time 与 TimelineItem.Start/End 使用同一坐标（源素材内秒，0..源时长），
/// Db 为绝对增益相对 0 dB 的偏移。渲染时点之间按 dB 线性插值、首点之前/末点之后保持端点值。
/// 前端 JSON 形状为 { "time": number, "db": number }。
/// </summary>
public sealed record VolumeKeyframe(double Time,double Db);
/// <summary>
/// 片段级音频效果实例。前端 JSON 形状与 docs/filmcraft-parity-next4.md §2.5 对齐：
/// { "id": string, "type": "compressor" | "noiseGate" | "parametric_eq", "enabled": bool, "params": { ... } }。
/// compressor / noiseGate 使用 camelCase 数值键；parametric_eq 使用引擎 35 个 snake_case 数值键。
/// 缺省值与越界参数按类型补齐/clamp；含非有限参数的整条效果丢弃。
/// </summary>
public sealed record AudioEffectInstance(string Id,string Type,bool Enabled=true,Dictionary<string,double>? Params=null);
/// <summary>
/// 变换参数关键帧（冻结模型，见 docs/keyframe-render-status.md 与 docs/filmcraft-parity-next6.md 方案 C）。
/// Time 是片段输出时间轴内的相对秒（0..ItemOutputDuration，受 speed 影响：输出秒 = 源秒 / speed），
/// Value 是该参数的业务值；点间线性插值、首点前/末点后保持端点。前端 JSON 形状为 { "time": number, "value": number }。
/// 参数白名单：scale / rotation / positionX / positionY / opacity。
/// </summary>
public sealed record ParamKeyframe(double Time,double Value);
/// <summary>
/// 静态蒙版 v1。坐标相对画面：X/Y 为 -0.5..0.5 的偏移，W/H 为 0..2 的比例；
/// Rotation 为角度，Feather 0..100，Opacity 0..1。null/缺省表示无蒙版，旧工程行为不变。
/// 前端 JSON 形状与 web/src/api/types.ts 的 MaskInstance 一致；v1 后端只持久化/归一化，渲染尚未启用。
/// </summary>
public sealed record MaskInstance(string Id,string Type,bool Enabled,double X,double Y,double W,double H,double Rotation,double Feather,double Opacity,[property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] MaskPoint[]? Points=null);

/// <summary>多边形蒙版顶点：0..1 归一化坐标（左上为原点），与前端 MaskInstance.points 一致。</summary>
public sealed record MaskPoint(double X,double Y);
/// <summary>
/// 剪辑点转场：挂在剪点右侧片段（ItemId）的入点上，横跨两个相邻片段。
/// duration 保存用户请求的时长（秒），实际生效时长由规格 §3 按两侧片段长度 clamp。
/// 同一数据模型由前端 web/src/api/types.ts 的 EditorTransition 写入，字段名保持一致。
/// </summary>
public sealed record EditorTransition(string Id,string ItemId,int Track,string Kind,double Duration,string Align,string Layout="overlap");
/// <summary>
/// 已归一化并通过合法性校验的转场。EffectiveDuration 是 clamp 后真正生效的重叠时长（转场带宽度），
/// Start 是对齐三态换算后转场带的起点（压缩后序列时间）。Shift 是该剪点让序列缩短的秒数：
/// center=d、start=d/2、end=3d/2，因为 start 会多取后段 d/2、end 会裁掉前段 d/2。
/// 规格见 docs/transitions-spec.md §4/§10。
/// </summary>
public sealed record TransitionCut(string Id,string PreviousItemId,string ItemId,int Track,string Kind,string Align,double RequestedDuration,double EffectiveDuration,double Start,double Shift=0,string Layout="overlap");
/// <summary>
/// 嵌套序列 v1（docs/filmcraft-parity-next5.md §4）：时间线片段可通过 TimelineItem.NestedSequenceId 引用它。
/// v1 只保存子序列自己的 items 与内部转场；渲染前 FlattenProject 会把它展开到主时间线（深度上限 4）。
/// Fps/Resolution 仅用于保存与前端回显，v1 渲染继承主序列设置。
/// </summary>
public sealed record NestedSequence(string Id,string Name,TimelineItem[] Timeline,EditorTransition[]? Transitions=null,double Fps=0,int Resolution=0);
/// <summary>片段调色参数（P1.6-b）：亮度/对比度/饱和度/色温。缺省或 null 表示不动画面。</summary>
public sealed record ColorGrade(double Brightness=0,double Contrast=1,double Saturation=1,double Temperature=0);
public sealed record EditorProject
{
    public double Start {get;init;}
    public double End {get;init;}
    public TimelineItem[]? Timeline {get;init;}
    /// <summary>剪辑点转场列表。null 或空数组表示没有转场，渲染保持原来的 concat 路径不变。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public EditorTransition[]? Transitions {get;init;}
    /// <summary>嵌套序列字典（键=序列 id）。null 表示没有嵌套序列，旧工程行为完全不变。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,NestedSequence>? NestedSequences {get;init;}
    public bool BurnSubtitles {get;init;}
    public int[]? SelectedSegments {get;init;}
    public bool VoiceOnly {get;init;}
    public string? MusicAssetId {get;init;}
    public string? EffectAssetId {get;init;}
    public double VoiceVolume {get;init;}=1;
    public double MusicVolume {get;init;}=.25;
    public double EffectVolume {get;init;}=.5;
    public double MasterVolume {get;init;}=1;
    public double EffectAt {get;init;}
    public string SubtitleFont {get;init;}="Microsoft YaHei";
    public int SubtitleSize {get;init;}=22;
    public string SubtitleColor {get;init;}="#FFFFFF";
    public string SubtitlePosition {get;init;}="bottom";
    public int Resolution {get;init;}=720;
    public int Fps {get;init;}=30;
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public int? CanvasWidth {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public int? CanvasHeight {get;init;}
    /// <summary>成片编码：h264/hevc/prores/dnxhr。null 表示 h264（旧工程 JSON 不变）。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? Format {get;init;}
    /// <summary>HEVC 色彩标记：hdr10/bt709；null 不增加标记，旧工程 JSON 不变。不执行 SDR→HDR 色调映射。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorSpace {get;init;}
    public int BitrateKbps {get;init;}
    public bool SentenceMode {get;init;}
    public bool DuckMusic {get;init;}=true;
    public double SubtitleOutline {get;init;}=1.5;
    public int SubtitleMargin {get;init;}=18;
    public Dictionary<string,TranscriptSegment[]>? SourceTranscripts {get;init;}
    /// <summary>用户字幕覆盖：键为源 clipId，值为该源秒级字幕；空数组=显式清空，缺省/null=沿用 store。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,TranscriptSegment[]>? CaptionOverrides {get;init;}
    /// <summary>自动剪气口之前的时间线快照；编辑器用它做「复原自动剪气口」。</summary>
    public TimelineItem[]? SilenceTrimBackup {get;init;}
    /// <summary>当前保存的时间线是否已经剪过气口（为真时上面的备份可以复原）。</summary>
    public bool SilenceTrimmed {get;init;}
    /// <summary>剪气口一共缩短了多少秒，用于界面提示。</summary>
    public double SilenceTrimmedSeconds {get;init;}
    /// <summary>只静音 V1 内嵌音频；false/缺省保持旧工程行为，不影响视频画面或 A1–A3。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
    public bool BaseAudioMuted {get;init;}
    /// <summary>静音的音频轨编号（A1=4、A2=5、A3=6）。</summary>
    public int[]? MutedTracks {get;init;}
    /// <summary>独奏的音频轨编号；非空时只混列出的轨道，同轨 solo 优先于 mute。</summary>
    public int[]? SoloedTracks {get;init;}
    /// <summary>隐藏的视频轨编号（V1=1、V2=2、V3=3），隐藏轨不参与成片画面。</summary>
    public int[]? HiddenTracks {get;init;}
    /// <summary>锁定的轨道编号（V1=1…V3=3、A1=4…A3=6）；锁定只限制编辑，不影响渲染。</summary>
    public int[]? LockedTracks {get;init;}
    /// <summary>时间线标记，按序列秒数保存。</summary>
    public double[]? Markers {get;init;}
    /// <summary>视频轨不透明度：索引 0/1/2 对应 V1/V2/V3。</summary>
    public double[]? TrackOpacities {get;init;}
    [JsonPropertyName("replicaExtension")]
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public ReplicaExtension? ReplicaExtension {get;init;}
    /// <summary>独立字幕轨（序列秒，见 TimelineCaption）。null=旧行为（源字幕动态映射）；[]=字幕轨显式为空，不补回源字幕；非空=唯一权威，渲染/SRT 侧车/烧录都只用它。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public TimelineCaption[]? TimelineCaptions {get;init;}
    /// <summary>受管理事件片段的显式清空标记（v1，见 TimelineEventSelectionClear）。null/缺省=无清空语义，空 timeline 照旧拒绝；非空=各源事件已全部取消勾选，允许 timeline=[]（空序列不可成片/导出，Queue 显式阻止）。</summary>
    [JsonPropertyName("clearedEventSelections")]
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public TimelineEventSelectionClear[]? ClearedEventSelections {get;init;}
    [JsonPropertyName("emptySequenceDuration")]
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public double? EmptySequenceDuration {get;init;}
}

public sealed class EditorRenderService(Store store,StudioStore studio,MediaTools media,string storageRoot,Func<string?>? renderWorkDirectory=null,Func<string?>? exportOutputDirectory=null)
{
    // Track-leading gaps share the sequence time domain, including append after another track.
    public const double MaxTimelineSeconds = 21600;
    private readonly CancellationTokenSource _stop=new();
    private readonly SemaphoreSlim _worker=new(1,1);
    private readonly List<Task> _tasks=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,CancellationTokenSource> _controls=new();
    // 串行化 Control 受理 / worker 进度 / 终态发布 / 控制项释放，避免 cancel 与 completed、progress、Dispose 互相覆盖。
    private readonly object _gate=new();
    public static TimelineItem[] Items(EditorProject plan,string primaryClipId)=>plan.Timeline??[new TimelineItem("source",primaryClipId,plan.Start,plan.End)];
    /// <summary>P1.6-b 导出格式矩阵：与 FilmCraft CLI 的 format 取值保持一致。</summary>
    public static readonly string[] SupportedFormats=["h264","hevc","prores","dnxhr",..EditorExportFormats.Ids];
    public static string NormalizeFormat(string? format)=>string.IsNullOrWhiteSpace(format)?"h264":format.Trim().ToLowerInvariant();
    /// <summary>成片容器：ProRes/DNxHR 必须进 .mov；R81 新增格式按描述表（图像序列为打包 .zip），其余走 .mp4。</summary>
    public static string OutputExtension(string? format)=>NormalizeFormat(format) switch
    {
        "prores" or "dnxhr"=>".mov",
        var other=>EditorExportFormats.Find(other)?.Extension??".mp4"
    };
    /// <summary>成片编码参数纯函数。非 HEVC 分支保留旧参数与顺序；尺寸/fps 必须是实际输出值。</summary>
    public static string[] VideoCodecArgs(string? format,int bitrateKbps,int width=1280,int height=720,double fps=30,string? colorSpace=null)
    {
        var rate=bitrateKbps>0?new[]{"-b:v",$"{bitrateKbps}k"}:Array.Empty<string>();
        return NormalizeFormat(format) switch
        {
            "h264"=>[..new[]{"-c:v","libx264","-preset","veryfast"},..(rate.Length>0?rate:new[]{"-crf","22"}),"-pix_fmt","yuv420p"],
            "hevc"=>HevcCodecArgs(rate,width,height,fps,colorSpace),
            "prores"=>["-c:v","prores_ks","-profile:v","3","-vendor","apl0","-pix_fmt","yuv422p10le"],
            "dnxhr" when colorSpace=="hdr10"=>["-c:v","dnxhd","-profile:v","dnxhr_hqx","-pix_fmt","yuv422p10le","-color_primaries","bt2020","-color_trc","smpte2084","-colorspace","bt2020nc"],
            "dnxhr"=>["-c:v","dnxhd","-profile:v","dnxhr_sq","-pix_fmt","yuv422p"],
            // R81：ProRes 其余档位 / DNxHR LB-HQX / MJPEG / WebM 走描述表（参数与 format-table.json 一致）。
            var other when EditorExportFormats.Find(other) is {Kind:ExportKind.VideoFile or ExportKind.AnimatedGif}=>EditorExportFormats.VideoArgsOrThrow(other),
            _=>throw new ArgumentException(EditorExportFormats.UnsupportedMessage),
        };
    }
    /// <summary>
    /// main tier 的最低 level（按画面采样数/每秒采样数，不按长宽朝向）：
    /// 3.1 ≤983040/33177600（720p30）；4 ≤2228224/66846720（1080p30）；
    /// 4.1 ≤2228224/133693440（1080p60）；5 ≤8912896/267386880（2160p30）；
    /// 5.1 ≤8912896/534773760（2160p60）。超出本导出器范围时拒绝。
    /// </summary>
    public static string HevcLevel(int width,int height,double fps)
    {
        if(width<=0||height<=0||!double.IsFinite(fps)||fps<=0)throw new ArgumentException("HEVC 输出尺寸或帧率无效");
        var samples=(double)width*height;var samplesPerSecond=samples*fps;
        foreach(var limit in new (string Level,double Picture,double PerSecond)[]{("3.1",983040,33177600),("4",2228224,66846720),("4.1",2228224,133693440),("5",8912896,267386880),("5.1",8912896,534773760)})
            if(samples<=limit.Picture&&samplesPerSecond<=limit.PerSecond&&width<=Math.Sqrt(limit.Picture*8)&&height<=Math.Sqrt(limit.Picture*8))return limit.Level;
        throw new ArgumentException("HEVC 输出超过 main tier level 5.1 支持范围");
    }
    private static string[] HevcCodecArgs(string[] rate,int width,int height,double fps,string? colorSpace)
    {
        if(colorSpace is not (null or "hdr10" or "bt709"))throw new ArgumentException("色彩空间只支持 hdr10|bt709 或 null");
        var hdr=colorSpace=="hdr10";
        var x265=$"level-idc={HevcLevel(width,height,fps)}:high-tier=0";
        // Pin encoder VUI too: frame metadata can override the outer FFmpeg color options.
        if(hdr)x265+=":colorprim=9:transfer=16:colormatrix=9:hdr10=1:hdr10-opt=1:repeat-headers=1";
        else if(colorSpace=="bt709")x265+=":colorprim=1:transfer=1:colormatrix=1";
        string[] color=hdr?["-color_primaries","bt2020","-color_trc","smpte2084","-colorspace","bt2020nc"]:
            colorSpace=="bt709"?["-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"]:[];
        return [..new[]{"-c:v","libx265","-preset","fast","-tag:v","hvc1"},..(rate.Length>0?rate:new[]{"-crf","24"}),"-pix_fmt",hdr?"yuv420p10le":"yuv420p","-x265-params",x265,..color];
    }
    internal static bool RequiresHighBitDepth(EditorProject plan)=>NormalizeFormat(plan.Format) switch
    {
        "prores"=>true,
        "hevc" or "dnxhr"=>plan.ColorSpace=="hdr10",
        var other=>EditorExportFormats.Find(other)?.HighBitDepth??false
    };
    internal static string[] IntermediateVideoCodecArgs(EditorProject plan,bool explicitPixelFormat=true)
    {
        // Lossless intermediates avoid repeated quantization; final codec/rate control stays independent.
        return ["-c:v","ffv1","-level","3","-slices","4","-slicecrc","1",
            ..(RequiresHighBitDepth(plan)&&explicitPixelFormat?new[]{"-pix_fmt","yuv420p10le"}:Array.Empty<string>()),
            ..(plan.ColorSpace=="hdr10"?new[]{"-color_primaries","bt2020","-color_trc","smpte2084","-colorspace","bt2020nc"}:Array.Empty<string>())];
    }
    internal static string[] IntermediateAudioCodecArgs()=>["-c:a","pcm_f32le","-ar","48000","-ac","2"];
    internal static string HighBitDepthFilterGraph(string graph)
    {
        graph=graph.Replace("format=yuv420p[","format=yuv420p10le[")
            .Replace("format=rgb24","format=gbrp16le")
            .Replace("format=rgba","format=gbrap16le")
            .Replace("format=gray[vmatte]","format=gbrp16le[vmatte]")
            .Replace("[vopacity][vopacityblack]blend","[vopacityblack]format=gbrp16le[hdrblack];[vopacity][hdrblack]blend")
            .Replace("overlay=x=","overlay=format=yuv420p10:x=")
            .Replace("drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill","lut=y=64:u=512:v=512");
        // The matte is a coverage map, not image luma; RGB planes keep black=0 and white=65535.
        return System.Text.RegularExpressions.Regex.Replace(graph,@"(\[[^\]]+\])(\[vmaskblack\])\[vmatte\]maskedmerge",
            "$1format=gbrp16le[hdrmasksource];$2format=gbrp16le[hdrmaskblack];[hdrmasksource][hdrmaskblack][vmatte]maskedmerge");
    }
    internal static void ConfigureIntermediateArgs(List<string> args,EditorProject plan)
    {
        // FFV1 and DNxHR inherit decoded-frame tags, not just output options. Apply to every input,
        // including lavfi canvases and overlay parts, rather than relying on a single source clip.
        if(plan.ColorSpace=="hdr10")
            for(var i=args.Count-2;i>=0;i--)
                if(args[i]=="-i")args.InsertRange(i,["-color_primaries","bt2020","-color_trc","smpte2084","-colorspace","bt2020nc"]);
        if(!RequiresHighBitDepth(plan))return;
        for(var i=0;i<args.Count-1;i++)
            if(args[i] is "-vf" or "-filter_complex")args[i+1]=HighBitDepthFilterGraph(args[i+1]);
    }
    /// <summary>调色 → ffmpeg 滤镜链。返回 null 表示参数等于默认值，不加滤镜。</summary>
    public static string? GradeFilter(ColorGrade? grade)
    {
        if(grade is null)return null;
        var parts=new List<string>();
        if(Math.Abs(grade.Brightness)>.001||Math.Abs(grade.Contrast-1)>.001||Math.Abs(grade.Saturation-1)>.001)
            parts.Add($"eq=brightness={N(Math.Clamp(grade.Brightness,-1,1))}:contrast={N(Math.Clamp(grade.Contrast,0,3))}:saturation={N(Math.Clamp(grade.Saturation,0,3))}");
        if(Math.Abs(grade.Temperature)>.001)
        {
            // 正值偏暖（抬红压蓝），负值偏冷；幅度收敛到 ±.12 与预设滤镜同一量级。
            var t=Math.Clamp(grade.Temperature,-1,1)*.12;
            parts.Add($"colorbalance=rs={N(t)}:bs={N(-t)}");
        }
        return parts.Count==0?null:string.Join(',',parts);
    }
    /// <summary>嵌套序列 v1 的渲染深度上限（docs/filmcraft-parity-next5.md §4 / 任务要求 4 层）。</summary>
    public const int MaxNestedDepth=4;
    /// <summary>时间线里是否存在被引用的嵌套序列片段；没有时所有旧路径保持原样。</summary>
    public static bool HasNestedSequences(EditorProject plan)=>Items(plan,"primary").Any(x=>!string.IsNullOrWhiteSpace(x.NestedSequenceId));
    /// <summary>
    /// 把嵌套序列展开成主时间线里的一段普通 items（序列嵌套 v1，docs/filmcraft-parity-next5.md §4）。
    /// 嵌套片段会被替换成子序列的具体片段：子片段 id 加「嵌套片段 id:」前缀以区分同一子序列的多次引用；
    /// 子序列内部转场随片段一起展开；父级时间线中引用嵌套片段本身的转场会被拒绝；
    /// 没有嵌套引用时原样返回 plan（无嵌套/旧工程路径完全不变）。
    /// </summary>
    public static EditorProject FlattenProject(EditorProject plan)
    {
        var items=Items(plan,"primary");
        if(!items.Any(x=>!string.IsNullOrWhiteSpace(x.NestedSequenceId)))return plan;
        if(items.Any(x=>x.SequenceStart is not null)||plan.NestedSequences?.Values.Any(s=>s.Timeline.Any(x=>x.SequenceStart is not null))==true)throw new ArgumentException("Nested sequences with absolute placement are not supported");
        var sequences=new Dictionary<string,NestedSequence>(StringComparer.Ordinal);
        if(plan.NestedSequences is {Count:>0} nested)
        {
            foreach(var pair in nested)
            {
                var key=pair.Key?.Trim();
                if(!string.IsNullOrEmpty(key))sequences[key!]=pair.Value;
                var own=pair.Value.Id.Trim();
                if(own.Length>0&&!sequences.ContainsKey(own))sequences[own]=pair.Value;
            }
        }
        var block=FlattenBlock(items,plan.Transitions,sequences,0,[]);
        return plan with {Timeline=block.Items,Transitions=block.Transitions.Length==0?null:block.Transitions,NestedSequences=null};
    }
    private sealed record FlattenedBlock(TimelineItem[] Items,EditorTransition[] Transitions);
    private static FlattenedBlock FlattenBlock(TimelineItem[] sourceItems,EditorTransition[]? sourceTransitions,IReadOnlyDictionary<string,NestedSequence> sequences,int depth,List<string> path)
    {
        // 父级时间线引用嵌套片段（把嵌套片段作为剪点右侧）或紧邻嵌套片段的转场都不支持：
        // 展开后嵌套片段会变成多个子片段，跨嵌套的剪点没有稳定语义（§4.3/§4.5）。
        var byItem=(sourceTransitions??[]).GroupBy(x=>x.ItemId).ToDictionary(g=>g.Key,g=>g.First());
        var previousTrackItems=new Dictionary<int,bool>();
        foreach(var item in sourceItems)
        {
            var nested=!string.IsNullOrWhiteSpace(item.NestedSequenceId);
            var track=TrackOf(item);
            if(byItem.ContainsKey(item.Id)&&(nested||(previousTrackItems.TryGetValue(track,out var previousNested)&&previousNested)))
                throw new ArgumentException($"嵌套序列暂不支持跨嵌套转场（片段 {item.Id}）");
            previousTrackItems[track]=nested;
        }
        var result=new List<TimelineItem>();var transitions=new List<EditorTransition>();
        if(sourceTransitions is {Length:>0})transitions.AddRange(sourceTransitions);
        foreach(var item in sourceItems)
        {
            var sequenceId=item.NestedSequenceId?.Trim();
            if(string.IsNullOrEmpty(sequenceId)){result.Add(item);continue;}
            if(!sequences.TryGetValue(sequenceId,out var sequence))
                throw new ArgumentException($"嵌套序列引用不存在：{sequenceId}");
            if(path.Contains(sequenceId,StringComparer.Ordinal))
                throw new ArgumentException($"嵌套序列存在循环引用：{string.Join(" → ",path.Append(sequenceId))}");
            if(depth+1>MaxNestedDepth)
                throw new ArgumentException($"嵌套层级不能超过 {MaxNestedDepth} 层");
            var childSource=sequence.Timeline??[];
            if(childSource.Length==0)
                throw new ArgumentException($"子序列为空（{sequenceId}）");
            var child=FlattenBlock(childSource,sequence.Transitions,sequences,depth+1,[..path,sequenceId]);
            var prefix=item.Id+":";
            var firstOnTrack=new HashSet<int>();
            foreach(var childItem in child.Items)
            {
                var track=MapNestedTrack(TrackOf(childItem),item.Track);
                var gap=childItem.GapBefore+(firstOnTrack.Add(track)?Math.Max(0,item.GapBefore):0);
                var id=string.IsNullOrWhiteSpace(childItem.Id)?$"{prefix}item{result.Count}":prefix+childItem.Id;
                result.Add(childItem with {Id=id,Track=track,GapBefore=gap,Disabled=childItem.Disabled||item.Disabled});
            }
            foreach(var transition in child.Transitions)
                transitions.Add(transition with
                {
                    Id=$"{prefix}{(string.IsNullOrWhiteSpace(transition.Id)?"transition":transition.Id)}",
                    ItemId=prefix+transition.ItemId,
                    Track=MapNestedTrack(transition.Track,item.Track)
                });
        }
        return new FlattenedBlock(result.ToArray(),transitions.ToArray());
    }
    /// <summary>
    /// 子序列内部轨道 → 主时间线轨道：视频轨相对嵌套片段所在视频轨平移（夹在 V1–V3，保留叠层关系与总时长），
    /// 音频轨相对 A1 平移（夹在 A1–A3）。与 §4.3 固定映射到 V1/A1 的偏差在 docs 中说明。
    /// </summary>
    public static int MapNestedTrack(int childTrack,int parentTrack)
    {
        childTrack=Math.Clamp(childTrack,1,6);
        if(childTrack>=4)
        {
            var audioBase=parentTrack>=4?Math.Clamp(parentTrack,4,6):4;
            return Math.Clamp(audioBase+childTrack-4,4,6);
        }
        var videoBase=parentTrack is >=1 and <=3?parentTrack:1;
        return Math.Clamp(videoBase+childTrack-1,1,3);
    }
    public static int TrackOf(TimelineItem item)=>Math.Clamp(item.Track,1,6);
    /// <summary>音频混音准入：有 solo 时只收 solo（同轨 solo 胜过 mute）；无 solo 时排除 mute。与视频 hide 无关。</summary>
    public static bool IncludeTrackAudio(int track,IReadOnlyCollection<int>? mutedTracks,IReadOnlyCollection<int>? soloedTracks)=>
        soloedTracks is{Count:>0}?soloedTracks.Contains(track):mutedTracks?.Contains(track)!=true;
    /// <summary>V1 内嵌音频单独静音；A1–A3 有独奏时仍不收 V1，不改变音频轨 solo-over-mute。</summary>
    public static bool IncludeBaseAudio(bool baseAudioMuted,IReadOnlyCollection<int>? mutedTracks,IReadOnlyCollection<int>? soloedTracks)=>
        !baseAudioMuted&&IncludeTrackAudio(1,mutedTracks,soloedTracks);
    public static double TrackOpacity(IReadOnlyList<double>? values,int track)=>track is >=1 and <=3&&values is {Count:3}?Math.Clamp(values[track-1],0,1):1;
    public static TimelineItem[] BaseItems(IEnumerable<TimelineItem> items)=>items.Where(x=>TrackOf(x)==1).ToArray();
    public static double TimelineDuration(IEnumerable<TimelineItem> items)=>SequenceDuration(items.ToArray(),[]);
    /// <summary>转场时长 clamp 使用的项目帧率；未指定（0）时与前端 project.ts 的 `project.fps || 30` 一致按 30fps 估算。</summary>
    public static double TransitionFps(EditorProject plan)=>plan.Fps>0?plan.Fps:30;
    /// <summary>浮点容差：用于 Math.Floor(maxDuration*gridFps + FrameEpsilon)，吸收 2/3 这类除不尽时间乘帧率产生的浮点误差，避免整帧边界被误向下取整。</summary>
    private const double FrameEpsilon=1e-6;
    /// <summary>转场最短生效时长：1 帧。与前端 transitionMinDuration 相同。</summary>
    public static double TransitionMinDuration(double fps)=>1/(double.IsFinite(fps)&&fps>0?fps:30);
    private static string TransitionKind(string? kind)=>EditorFx.TransitionMap.RequireKind(kind);
    private static string TransitionAlign(string? align)=>align is "center" or "start" or "end"?align:"center";
    private static string XfadeTransition(string kind)=>EditorFx.TransitionMap.Xfade(kind);
    /// <summary>
    /// 对齐三态的统一换算（规格 §10）：转场带 [Start, Start+d] 与剪点 C 的相对位置。
    /// center：带以 C 为中心 [-d/2,+d/2]；start：带起点落在 C [0,d]；end：带终点落在 C [-d,0]。
    /// </summary>
    public static (double Start,double End) TransitionSpan(string align,double effective)=>TransitionAlign(align) switch
    {
        "start"=>(0,effective),
        "end"=>(-effective,0),
        _=>(-effective/2,effective/2)
    };
    /// <summary>对齐三态让序列缩短的秒数 Δ=d−tailAdjust：center=d、start=d/2、end=3d/2（规格 §10）。</summary>
    public static double TransitionShift(string align,double effective)=>effective-TransitionTailAdjust(align,effective);
    /// <summary>对齐三态对前一片段可见尾部的调整：start 需多取 d/2（尾部 handle），end 裁掉 d/2，center 为 0。</summary>
    public static double TransitionTailAdjust(string align,double effective)=>TransitionAlign(align) switch{"start"=>effective/2,"end"=>-effective/2,_=>0};
    public static double TransitionTailAdjust(TransitionCut cut)=>cut.Layout=="preserve"?0:TransitionTailAdjust(cut.Align,cut.EffectiveDuration);
    /// <summary>转场剪点对序列时长的真实压缩量；兼容手工构造（Shift=0）的旧记录。</summary>
    public static double CutShift(TransitionCut cut)=>cut.Layout=="preserve"?0:cut.Shift>0?cut.Shift:TransitionShift(cut.Align,cut.EffectiveDuration);
    /// <summary>按工程解析转场；fps 使用 TransitionFps(plan)，与前端 normalizeProject 的换算保持一致。</summary>
    public static TransitionCut[] ResolveTransitions(EditorProject plan)=>ResolveTransitions(Items(plan,"primary"),plan.Transitions,TransitionFps(plan));
    /// <summary>渲染路径传入实际输出尺寸；先检查所有声明，再复用时间线归一化，避免过滤或重复项绕过尺寸限制。</summary>
    public static TransitionCut[] ResolveTransitions(TimelineItem[] items,EditorTransition[]? transitions,double fps,int width,int height,IReadOnlyDictionary<string,double>? clipDurations=null)
    {
        foreach(var transition in transitions??[])if(transition is not null)
            EditorFx.TransitionMap.RequireOutputSize(transition.Kind,width,height);
        return ResolveTransitions(items,transitions,fps,clipDurations);
    }

    /// <summary>
    /// 归一化并过滤非法转场（规格 §2/§3/§10）：轨道不符、找不到片段、剪点前有间隙、两侧片段被禁用、
    /// 或两侧片段太短放不下 1 帧的转场会被丢弃。未知 kind 拒绝；未知 align 按 center 处理。
    /// 同一剪点重复时以最后一条为准（与前端 normalizeTransitions 一致）。
    /// clipDurations（clipId→源时长）只在有 start 对齐时用于按前段尾部 handle 进一步 clamp；为 null 时乐观认为 handle 足够。
    /// </summary>
    public static TransitionCut[] ResolveTransitions(TimelineItem[] items,EditorTransition[]? transitions,double fps,IReadOnlyDictionary<string,double>? clipDurations=null)
    {
        ValidateSequencePositions(items);
        if(transitions is not {Length:>0})return [];
        var gridFps=double.IsFinite(fps)&&fps>0?fps:30;
        var byItem=new Dictionary<string,EditorTransition>();
        foreach(var transition in transitions)
        {
            if(transition is null)continue;
            var target=items.FirstOrDefault(x=>x.Id==transition.ItemId);
            EditorFx.TransitionMap.RequireTrack(transition.Kind,transition.Track,target is null?null:TrackOf(target));
            if(transition.Layout is not ("overlap" or "preserve"))throw new ArgumentException("Unknown transition layout");
            if(transition.Layout=="preserve"&&target is null)throw new ArgumentException("Preserve transition target missing");
            if(string.IsNullOrWhiteSpace(transition.ItemId)||target is null)continue;
            if(transition.Layout=="preserve"&&(TrackOf(target)!=1||target.GapBefore>0.001))throw new ArgumentException("Preserve transitions require adjacent V1 clips");
            if(target.SequenceStart is not null&&transition.Layout!="preserve")throw new ArgumentException("Absolute sequence positions require preserve transitions");
            byItem[transition.ItemId]=transition;
        }
        var cuts=new List<TransitionCut>();
        for(var track=1;track<=6;track++)
        {
            var ordered=items.Where(x=>TrackOf(x)==track).ToArray();
            var raw=0d;var shift=0d;
            for(var i=0;i<ordered.Length;i++)
            {
                var item=ordered[i];raw=item.SequenceStart??(raw+Math.Max(0,item.GapBefore));
                TransitionCut? cut=null;var cutShift=0d;
                if(i>0&&byItem.TryGetValue(item.Id,out var candidate)&&TransitionTrackMatches(candidate.Track,track))
                {
                    var previous=ordered[i-1];
                    if(item.SequenceStart is not null&&Math.Abs(previous.SequenceStart!.Value+ItemOutputDuration(previous)-item.SequenceStart.Value)>1e-7)throw new ArgumentException("Preserve transition requires an adjacent hard cut");
                    if(!EditorProjectGraphics.IsSourceFree(item)&&!EditorProjectGraphics.IsSourceFree(previous)&&!item.Disabled&&!previous.Disabled&&item.GapBefore<=0.001)
                    {
                        var align=TransitionAlign(candidate.Align);
                        var maxDuration=Math.Min(ItemOutputDuration(previous),ItemOutputDuration(item));
                        // 对齐三态的内容约束（规格 §10）：center 要求 d≤min(两侧输出时长)；
                        // end 还要保证转场带起点不越过前段起点（d≤2/3·前段时长）；
                        // start 还要保证前段尾部有 d/2 的 handle（仅当已知源时长时）。
                        if(candidate.Layout!="preserve"&&align=="end")maxDuration=Math.Min(maxDuration,ItemOutputDuration(previous)*2/3);
                        else if(candidate.Layout!="preserve"&&align=="start"&&clipDurations is not null&&clipDurations.TryGetValue(previous.ClipId,out var sourceDuration))
                        {
                            var handle=Math.Max(0,(sourceDuration-previous.End)/EffectiveSpeed(previous));
                            maxDuration=Math.Min(maxDuration,handle*2);
                        }
                        // 契约 §3.1：转场重叠必须落在整数帧上。上限先向下取整到整帧（再加微小容差吸收 2/3 等浮点误差），
                        // 保证取整后的重叠不会超过两侧片段能容纳的时长。
                        var maxFrames=Math.Floor(maxDuration*gridFps+FrameEpsilon);
                        if(maxFrames>=1)
                        {
                            var requested=double.IsFinite(candidate.Duration)?Math.Max(0,candidate.Duration):1;
                            // 取整规则：四舍五入、中点远离 0（MidpointRounding.AwayFromZero），不用银行家舍入。
                            // 原因：与前端 Math.round 对正数的行为一致（x.5 帧进位），且银行家舍入会让 0.5 帧→0 帧、
                            // 1.5 帧→2 帧、2.5 帧→2 帧这种奇偶抖动，跨端结果不稳定。
                            // 取整之后再 clamp 到 [1 帧, maxFrames]，所以取整绝不会掉到最短 1 帧以下，也不会超过上限。
                            var frames=Math.Clamp(Math.Round(requested*gridFps,MidpointRounding.AwayFromZero),1,maxFrames);
                            var overlap=frames/gridFps;
                            var delta=candidate.Layout=="preserve"?0:TransitionShift(align,overlap);
                            cut=new TransitionCut(
                                string.IsNullOrWhiteSpace(candidate.Id)?$"transition-{item.Id}":candidate.Id,
                                previous.Id,item.Id,track,TransitionKind(candidate.Kind),align,
                                requested,overlap,raw-shift-delta,delta,candidate.Layout);
                            cutShift=delta;
                        }
                    }
                }
                if(cut is not null){cuts.Add(cut);shift+=cutShift;}
                raw+=ItemOutputDuration(item);
            }
        }
        foreach(var tr in byItem.Values.Where(t=>t.Layout=="preserve"))
            if(!cuts.Any(c=>c.ItemId==tr.ItemId))throw new ArgumentException("Preserve transition needs two enabled source clips");
        foreach(var trackCuts in cuts.GroupBy(x=>x.Track))
        {
            var windows=trackCuts.Select(c=>(Cut:c,Start:c.Layout=="preserve"?c.Start-PreserveBefore(c,gridFps):c.Start)).OrderBy(x=>x.Start).ToArray();
            for(var i=1;i<windows.Length;i++)
                if((windows[i].Cut.Layout=="preserve"||windows[i-1].Cut.Layout=="preserve")&&windows[i].Start<windows[i-1].Start+windows[i-1].Cut.EffectiveDuration-1e-8)
                    throw new ArgumentException("Preserve transition windows must not overlap");
        }
        return cuts.OrderBy(x=>x.Track).ThenBy(x=>x.Start).ToArray();
    }
    /// <summary>轨道字段缺省（0）时采用片段所在轨道；显式写了 1–6 但和片段轨道不一致则视为非法。</summary>
    private static bool TransitionTrackMatches(int declared,int track)=>declared==0||declared==track;
    /// <summary>压缩后的序列起点（规格 §4/§10）：每轨按顺序累加输出时长，再减去累计的转场压缩量 Δ。</summary>
    public static void ValidateSequencePositions(TimelineItem[] items)
    {
        foreach(var group in items.GroupBy(TrackOf))
        {
            if(!group.Any(x=>x.SequenceStart is not null))continue;
            if(group.Key!=1)throw new ArgumentException("Absolute sequence placement currently requires V1");
            if(group.Any(x=>x.SequenceStart is null||!double.IsFinite(x.SequenceStart.Value)||x.SequenceStart.Value<0||x.SequenceStart.Value+ItemOutputDuration(x)>MaxTimelineSeconds||x.GapBefore!=0||x.NestedSequenceId is not null))
                throw new ArgumentException("Absolute sequence tracks require finite positions on every item, zero GapBefore and no nested sequence");
        }
    }
    public static Dictionary<string,double> SequenceStarts(TimelineItem[] items,IReadOnlyList<TransitionCut> cuts)
    {
        ValidateSequencePositions(items);
        var starts=new Dictionary<string,double>();
        for(var track=1;track<=6;track++)
        {
            var shifts=cuts.Where(x=>x.Track==track).ToDictionary(x=>x.ItemId,x=>CutShift(x));
            var raw=0d;var shift=0d;
            foreach(var item in items.Where(x=>TrackOf(x)==track))
            {
                raw+=Math.Max(0,item.GapBefore);
                var delta=shifts.TryGetValue(item.Id,out var value)?value:0;
                starts[item.Id]=item.SequenceStart??(raw-shift-delta);shift+=delta;raw+=ItemOutputDuration(item);
            }
        }
        return starts;
    }
    /// <summary>某条轨道压缩后的总时长：Σ(gapBefore+输出时长)−Σ转场压缩量 Δ。</summary>
    public static double TrackDuration(TimelineItem[] items,IReadOnlyList<TransitionCut> cuts,int track)
    {
        var trackItems=items.Where(x=>TrackOf(x)==track).ToArray();
        if(!trackItems.Any(x=>x.SequenceStart is not null))return trackItems.Sum(x=>Math.Max(0,x.GapBefore)+ItemOutputDuration(x))-cuts.Where(x=>x.Track==track).Sum(CutShift);
        var starts=SequenceStarts(items,cuts);
        return trackItems.Select(x=>starts[x.Id]+ItemOutputDuration(x)).DefaultIfEmpty(0).Max();
    }
    /// <summary>压缩后的序列总时长：各轨最大值（规格 §4）。它也是转场成片的输出时长。</summary>
    public static double SequenceDuration(TimelineItem[] items,IReadOnlyList<TransitionCut> cuts)
    {
        var duration=0d;
        for(var track=1;track<=6;track++)duration=Math.Max(duration,TrackDuration(items,cuts,track));
        return duration;
    }
    public static double SequenceDuration(EditorProject plan)
    {
        var items=Items(plan,"primary");return items.Length==0&&plan.ClearedEventSelections is {Length:>0}?plan.EmptySequenceDuration??0:SequenceDuration(items,ResolveTransitions(items,plan.Transitions,TransitionFps(plan)));
    }
    /// <summary>时间码 HH:MM:SS:FF，用于转场失败信息定位剪点。</summary>
    public static string Timecode(double seconds,double fps)
    {
        var rate=(int)Math.Round(double.IsFinite(fps)&&fps>0?fps:30);if(rate<1)rate=1;
        var totalFrames=(long)Math.Round(Math.Max(0,double.IsFinite(seconds)?seconds:0)*rate);
        var frames=totalFrames%rate;var totalSeconds=totalFrames/rate;
        return $"{totalSeconds/3600:00}:{totalSeconds/60%60:00}:{totalSeconds%60:00}:{frames:00}";
    }
    public static string CutLabel(TransitionCut cut,double fps)=>$"剪点 {Timecode(cut.Start,fps)}（片段 {cut.ItemId}）";
    // Native ASS input disables libass Unicode line breaking in auto mode.
    // Enable it explicitly so unspaced CJK paragraphs wrap inside dialogue margins.
    internal static string IndependentCaptionFilter(string ass)=>$"subtitles=filename='{FilterPath(ass)}':wrap_unicode=1";

    public static void ValidateCanvas(EditorProject plan)
    {
        if(plan.CanvasWidth is null && plan.CanvasHeight is null)return;
        if(plan.CanvasWidth is not (>=16 and <=16384) || plan.CanvasHeight is not (>=16 and <=16384) ||
            plan.CanvasWidth%2!=0 || plan.CanvasHeight%2!=0 || plan.Fps is not (24 or 25 or 30 or 50 or 60))
            throw new ArgumentException("Sequence canvas requires even dimensions from 16 to 16384 and a supported frame rate.");
    }

    public static void Validate(EditorProject plan)=>ValidateForSave(plan);

    /// <summary>All projects may persist an explicit empty timeline; legacy null still means a source clip.</summary>
    public static void ValidateForSave(EditorProject plan,bool independentProject=false)
    {
        var items=Items(plan,"primary");
        ValidateSequencePositions(items);
        foreach(var transition in plan.Transitions??[])if(transition is not null)
        {
            var target=items.FirstOrDefault(x=>x.Id==transition.ItemId);
            EditorFx.TransitionMap.RequireTrack(transition.Kind,transition.Track,target is null?null:TrackOf(target));
            if(transition.Layout is not ("overlap" or "preserve"))throw new ArgumentException("Unknown transition layout");
        }
        foreach(var item in items)EditorProjectGraphics.Validate(item);
        if(items.Any(x=>x.SequenceStart is not null)||plan.Transitions?.Any(t=>t.Layout=="preserve")==true)ResolveTransitions(items,plan.Transitions,TransitionFps(plan));
        var timelineDuration=TimelineDuration(items);
        // Managed-event clear metadata is validated even when an ordinary empty edit is saved.
        var clearedEventSelections=EditorProjectEventSelection.ValidateAndCollect(plan,items);
        if(items.Length==0&&clearedEventSelections.Count>0)timelineDuration=plan.EmptySequenceDuration??0;
        if(items.Length>200||items.Any(x=>string.IsNullOrWhiteSpace(x.Id)||(x.NestedSequenceId is null or ""&&!EditorProjectGraphics.IsSourceFree(x)&&string.IsNullOrWhiteSpace(x.ClipId))||!double.IsFinite(x.Start)||!double.IsFinite(x.End)||(x.NestedSequenceId is null or ""&&(x.Start<0||x.End<=x.Start))||!double.IsFinite(x.GapBefore)||x.GapBefore is <0 or >MaxTimelineSeconds||!double.IsFinite(x.Speed)||x.Speed is <0.25 or >4||x.Track is <1 or >6||!double.IsFinite(x.PositionX)||!double.IsFinite(x.PositionY)||Math.Abs(x.PositionX)>.5||Math.Abs(x.PositionY)>.5||!double.IsFinite(x.VolumeDb)||x.VolumeDb is < -60 or > 12)||items.Select(x=>x.Id).Distinct().Count()!=items.Length||timelineDuration>MaxTimelineSeconds)
            throw new ArgumentException("时间线需包含 0–200 个有效片段，轨道需在 V1–V3/A1–A3 之间，速度需在 0.25–4 倍之间，画面位置需在 -50%–50%，片段音量需在 -60–+12 dB，空隙须为非负且含空隙的总时长最多 6 小时");
        if(new[]{plan.VoiceVolume,plan.MusicVolume,plan.EffectVolume,plan.MasterVolume}.Any(x=>!double.IsFinite(x)||x is <0 or >2)||!double.IsFinite(plan.EffectAt)||plan.EffectAt<0||plan.EffectAt>timelineDuration)
            throw new ArgumentException("音量或音效位置无效");
        if((plan.MutedTracks??[]).Any(x=>x is <4 or >6)||(plan.SoloedTracks??[]).Any(x=>x is <4 or >6)||(plan.MutedTracks??[]).Distinct().Count()!=(plan.MutedTracks??[]).Length||(plan.SoloedTracks??[]).Distinct().Count()!=(plan.SoloedTracks??[]).Length)
            throw new ArgumentException("轨道静音/独奏参数无效");
        if((plan.HiddenTracks??[]).Any(x=>x is <1 or >3)||(plan.HiddenTracks??[]).Distinct().Count()!=(plan.HiddenTracks??[]).Length)
            throw new ArgumentException("轨道隐藏参数无效");
        if((plan.LockedTracks??[]).Any(x=>x is <1 or >6)||(plan.LockedTracks??[]).Distinct().Count()!=(plan.LockedTracks??[]).Length)
            throw new ArgumentException("轨道锁定参数无效");
        if(plan.TrackOpacities is {Length:>0}&&(plan.TrackOpacities.Length!=3||plan.TrackOpacities.Any(x=>!double.IsFinite(x)||x is <0 or >1)))
            throw new ArgumentException("视频轨不透明度无效");
        var markers=plan.Markers??[];
        if(markers.Any(x=>!double.IsFinite(x)||x<0||x>timelineDuration+0.001)||markers.Distinct().Count()!=markers.Length)
            throw new ArgumentException("时间线标记无效");
        ValidateCanvas(plan);
        if(plan.Resolution is not (0 or 480 or 720 or 1080 or 2160)||plan.Fps is not (0 or 24 or 25 or 30 or 50 or 60)||plan.BitrateKbps is <0 or >100000||plan.SubtitleSize is <10 or >80||plan.SubtitleColor is null||!System.Text.RegularExpressions.Regex.IsMatch(plan.SubtitleColor,"^#[A-Fa-f0-9]{6}$")||string.IsNullOrWhiteSpace(plan.SubtitleFont)||plan.SubtitleFont.Length>80||plan.SubtitlePosition is not ("top" or "middle" or "bottom")||!double.IsFinite(plan.SubtitleOutline)||plan.SubtitleOutline is <0 or >8||plan.SubtitleMargin is <0 or >200)
            throw new ArgumentException("视频输出或字幕样式参数无效");
        TimelineCaptionTools.Validate(plan.TimelineCaptions,timelineDuration);
        if(plan.Format is not null&&!SupportedFormats.Contains(NormalizeFormat(plan.Format)))
            throw new ArgumentException(EditorExportFormats.UnsupportedMessage);
        if(plan.ColorSpace is not (null or "hdr10" or "bt709"))throw new ArgumentException("色彩空间只支持 hdr10|bt709 或 null");
        if(items.Any(x=>!double.IsFinite(x.Zoom)||x.Zoom is <1 or >2||x.Filter is not ("none" or "warm" or "cool" or "mono" or "vivid")||x.Motion is not ("none" or "push" or "shake")||new[]{x.StickerX,x.StickerY,x.StickerWidth}.Any(v=>!double.IsFinite(v)||v is <0 or >1)||x.StickerWidth<.05||!double.IsFinite(x.StickerRotation)||x.StickerRotation is <-180 or >180||!double.IsFinite(x.Rotation)||x.Rotation is <-180 or >180||!double.IsFinite(x.Opacity)||x.Opacity is <0 or >1||x.Kind is not ("video" or "hook" or "insert" or "graphic")))throw new ArgumentException("精剪效果参数无效");
        if(items.Any(x=>(x.TitleText?.Length??0)>120||!double.IsFinite(x.TitleSize)||x.TitleSize is <12 or >160||(x.TitleText is not null&&!System.Text.RegularExpressions.Regex.IsMatch(x.TitleColor??string.Empty,"^#[A-Fa-f0-9]{6}$"))||!double.IsFinite(x.TitleX)||x.TitleX is <0 or >1||!double.IsFinite(x.TitleY)||x.TitleY is <0 or >1))
            throw new ArgumentException("标题文本、字号、颜色或位置无效");
        if(items.Any(x=>(x.NestedSequenceId is null or "")&&(!double.IsFinite(x.FadeIn)||!double.IsFinite(x.FadeOut)||x.FadeIn is <0 or >10||x.FadeOut is <0 or >10||x.FadeIn+x.FadeOut>ItemOutputDuration(x)+0.001)))
            throw new ArgumentException("淡入淡出时长无效");
        // 调色参数：非有限值或越界一律拒绝，避免把 NaN 写进 ffmpeg 表达式。
        if(items.Any(x=>x.Grade is{ } g&&(!double.IsFinite(g.Brightness)||!double.IsFinite(g.Contrast)||!double.IsFinite(g.Saturation)||!double.IsFinite(g.Temperature)||g.Brightness is < -1 or >1||g.Contrast is <0 or >3||g.Saturation is <0 or >3||g.Temperature is < -1 or >1)))
            throw new ArgumentException("调色参数无效");
        // 音量包络不参与合法性拒绝：非有限值/越界 dB/time 会被静默过滤或 clamp（与转场非法项丢弃的约定一致）。
        // 渲染与导出统一走 NormalizeVolumeKeyframes(item)，Validate 这里只做一次归一化调用确认不会抛异常。
        foreach(var item in items)NormalizeVolumeKeyframes(item);
        // 音频效果同理：未知类型/越界参数/非有限值静默丢弃或 clamp，旧工程 Effects=null 时行为完全不变。
        foreach(var item in items)NormalizeAudioEffects(item);
        // 变换参数关键帧与静态蒙版同样静默归一（未知键/非有限值/越界值丢弃或 clamp），不参与合法性拒绝。
        foreach(var item in items){NormalizeKeyframes(item);NormalizeMasks(item);}
    }
    public static TimelineItem[] SentenceTimeline(string clipId,IReadOnlyList<TranscriptSegment> segments,IReadOnlyCollection<int> selected,double start,double end)
    {
        var ranges=new List<TimelineItem>();var previous=-2;
        foreach(var (segment,index) in segments.Select((x,i)=>(x,i)))
        {
            if(!selected.Contains(index)||segment.End<=start||segment.Start>=end)continue;
            var a=Math.Max(start,segment.Start);var b=Math.Min(end,segment.End);
            if(index==previous+1&&ranges.Count>0&&a-ranges[^1].End<=.05)ranges[^1]=ranges[^1] with {End=Math.Max(b,ranges[^1].End)};
            else ranges.Add(new(Guid.NewGuid().ToString("N"),clipId,a,b));
            previous=index;
        }
        return ranges.ToArray();
    }
    /// <summary>字幕快照归一化：丢弃非有限值/空文本/end&lt;=start（start 先 clamp 到 &gt;=0），不重排、不改入参数组。</summary>
    public static TranscriptSegment[] NormalizeCaptions(IEnumerable<TranscriptSegment>? segments)
    {
        var result=new List<TranscriptSegment>();
        if(segments is null)return result.ToArray();
        foreach(var segment in segments)
        {
            if(segment is null||!double.IsFinite(segment.Start)||!double.IsFinite(segment.End))continue;
            var start=Math.Max(0,segment.Start);
            if(segment.End<=start||string.IsNullOrWhiteSpace(segment.Text))continue;
            result.Add(segment with{Start=start});
        }
        return result.ToArray();
    }
    /// <summary>显式覆盖判定：键存在（含空数组）即生效，own-key 语义，不回落到 store。</summary>
    public static bool HasCaptionOverride(EditorProject? plan,string clipId)=>
        plan?.CaptionOverrides is{ } overrides&&!string.IsNullOrEmpty(clipId)&&overrides.ContainsKey(clipId);
    /// <summary>某源生效字幕：override（归一化）→ 冻结的 SourceTranscripts → fallback。</summary>
    public static TranscriptSegment[] CaptionsFor(EditorProject? plan,string clipId,IReadOnlyList<TranscriptSegment>? fallback)
    {
        if(plan?.CaptionOverrides is{ } overrides&&overrides.TryGetValue(clipId,out var edited))return NormalizeCaptions(edited);
        if(plan?.SourceTranscripts is{ } frozen&&frozen.TryGetValue(clipId,out var snapshot))return snapshot.ToArray();
        return fallback is null?Array.Empty<TranscriptSegment>():fallback.ToArray();
    }
    /// <summary>深拷贝并归一化编辑态覆盖，供 Snapshot 冻结（之后改原数组不影响快照）。</summary>
    public static Dictionary<string,TranscriptSegment[]>? FreezeCaptionOverrides(EditorProject plan)=>
        plan.CaptionOverrides is null?null:plan.CaptionOverrides.ToDictionary(pair=>pair.Key,pair=>NormalizeCaptions(pair.Value),StringComparer.Ordinal);
    /// <summary>字幕映射：primarySelectionApplies=false（primary 有覆盖）时不再按 selectedSegments 过滤主源。</summary>
    public static IReadOnlyList<TranscriptSegment> MapSubtitles(TimelineItem[] items,Func<string,IReadOnlyList<TranscriptSegment>> source,string primaryClipId,int[]? selected,EditorTransition[]? transitions=null,double fps=30,bool primarySelectionApplies=true)
    {
        // 有转场时字幕按压缩后的序列起点定位（规格 §4）；无转场时保持原来的累计偏移算法，结果完全不变。
        var starts=transitions is {Length:>0}||items.Any(x=>x.SequenceStart is not null)?SequenceStarts(items,ResolveTransitions(items,transitions,fps)):null;
        var result=new List<TranscriptSegment>();var offset=0d;
        foreach(var item in items)
        {
            var itemStart=offset+=item.GapBefore;var speed=EffectiveSpeed(item);var length=(item.End-item.Start)/speed;
            if(starts is not null&&starts.TryGetValue(item.Id,out var compressed))itemStart=compressed;
            if(item.Disabled){if(starts is null)offset+=length;continue;}
            foreach(var (segment,index) in source(item.ClipId).Select((x,i)=>(x,i)))
            {
                if(primarySelectionApplies&&item.ClipId==primaryClipId&&item.Kind!="hook"&&selected is not null&&!selected.Contains(index))continue;
                var start=Math.Max(item.Start,segment.Start);var end=Math.Min(item.End,segment.End);
                if(end>start)result.Add(segment with {Start=itemStart+(start-item.Start)/speed,End=itemStart+(end-item.Start)/speed});
            }
            if(starts is null)offset+=length;
        }
        return result.OrderBy(x=>x.Start).ThenBy(x=>x.End).ToArray();
    }
    public static void ValidateForRender(EditorProject plan)
    {
        ValidateForSave(plan,independentProject:true);
        if(plan.Timeline is {Length:0})throw new ArgumentException("Timeline has no renderable segments; an empty project cannot be exported.");
    }

    public EditorRender Queue(Clip clip,EditorProject plan,int? revision=null,string? epoch=null)=>QueueCore(clip,null,plan,revision,epoch);
    public EditorRender Queue(EditorProjectIdentity identity,EditorProject plan,int? revision=null,string? epoch=null)=>QueueCore(null,identity,plan,revision,epoch);
    internal static void ValidateIndependentRender(EditorProjectIdentity identity,EditorProject plan)
    {
        PremiereExportService.ValidateIdentity(identity.Id,plan,identity);
        ValidateForRender(plan);
        foreach(var transition in plan.Transitions??[])
            EditorFx.TransitionMap.RequireOutputSize(transition.Kind,identity.Width,identity.Height);
        if(identity.Width%2!=0||identity.Height%2!=0)throw new ArgumentException("独立工程 MP4 画幅宽高必须为偶数");
        if(NormalizeFormat(plan.Format)!="h264"||plan.ColorSpace is not null)
            throw new ArgumentException("独立工程当前仅接入 H.264 SDR MP4 输出");
        if(plan.Timeline!.Any(x=>x.Kind!="video"||TrackOf(x)!=1||!string.IsNullOrEmpty(x.NestedSequenceId))||
            plan.VoiceOnly||plan.EffectAssetId is not null)
            throw new ArgumentException("独立工程当前仅支持 V1 视频及字幕输出；独立音轨、图形、嵌套和配乐尚未接入");
    }
    private EditorRender QueueCore(Clip? clip,EditorProjectIdentity? identity,EditorProject plan,int? revision,string? epoch)
    {
        var ownerId=identity?.Id??clip!.Id;
        if(identity is not null)ValidateIndependentRender(identity,plan);
        ValidateForRender(plan);
        // 空序列（受管理事件清空）可保存但不可成片：没有可渲染片段，明确拒绝而不是产出黑片。
        if(!media.Available())throw new InvalidOperationException("请先安装 FFmpeg");
        plan=SnapshotCore(ownerId,plan);
        var known=store.Clips().Select(x=>x.Id).ToHashSet();
        if(Items(plan,ownerId).Any(x=>!EditorProjectGraphics.IsSourceFree(x)&&!known.Contains(x.ClipId)))throw new ArgumentException("时间线中有不存在的源视频");
        var local=studio.LocalVideos().FirstOrDefault(x=>x.ClipId==ownerId);
        var outputDirectory=RenderStoragePaths.OutputRoot(storageRoot,local?.OutputDirectory,exportOutputDirectory?.Invoke());Directory.CreateDirectory(outputDirectory);
        var id=Guid.NewGuid().ToString("N");
        var highlightBase=clip is null?null:OutputNaming.HighlightBase(clip);
        // P1.6-b：成片扩展名跟着编码走（ProRes/DNxHR 只能进 .mov）。
        var extension=OutputExtension(plan.Format);
        var path=highlightBase is null
            ?Path.Combine(outputDirectory,$"LiveClipper-{id[..8]}{extension}")
            :OutputNaming.UniquePath(outputDirectory,$"{highlightBase}{OutputNaming.ClipSuffix}-{OutputNaming.Date(DateTimeOffset.UtcNow)}",extension,studio.Renders().Select(x=>x.OutputPath));
        var render=new EditorRender(id,clip?.Id??"",path,JsonSerializer.Serialize(plan),DateTimeOffset.UtcNow,revision,epoch,identity);
        studio.SaveRender(render);
        var job=new Job(id,"edit-render",clip?.EventId??"","pending",0,null);store.SaveJob(job);Schedule(job,render,clip);return render;
    }
    public EditorProject Snapshot(Clip primary,EditorProject plan)=>SnapshotCore(primary.Id,plan);
    private EditorProject SnapshotCore(string ownerId,EditorProject plan)
    {
        plan=FlattenProject(plan);
        var clips=store.Clips().ToDictionary(x=>x.Id);var sources=new Dictionary<string,TranscriptSegment[]>();
        foreach(var id in Items(plan,ownerId).Where(x=>!EditorProjectGraphics.IsSourceFree(x)).Select(x=>x.ClipId).Distinct())
        {
            if(!clips.TryGetValue(id,out var clip))throw new ArgumentException("时间线源视频不存在");
            if(plan.CaptionOverrides is{ } overrides&&overrides.TryGetValue(id,out var edited)){sources[id]=NormalizeCaptions(edited);continue;}
            // 冻结原始 segments：plan.SelectedSegments 存的是原始下标，归一化丢段会让筛选下标错位。
            // 无效段（end<=start/非有限值）在 MapSubtitles 里再丢弃。
            var json=store.TranscriptJson(clip.EventId);sources[id]=json is null?[]:TranscriptExport.Parse(json).Segments.ToArray();
        }
        return plan with {SourceTranscripts=sources,CaptionOverrides=FreezeCaptionOverrides(plan)};
    }
    private const string InterruptedPrefix="停止确认被重启中断";
    private const string InterruptedError=InterruptedPrefix+"：旧进程是否已停止、产物是否已清理均无法确认，旧产物已保留，请重新发起新导出。";
    // 仅在服务未持有 control 时把残留的 pausing/cancelling 标为可见失败；调用方须在 _gate 内。
    private bool MarkInterruptedIfOrphan(Job job)
    {
        if(job.Status is not ("pausing" or "cancelling")||_controls.ContainsKey(job.Id))return false;
        store.SaveJob(job with {Status="failed",Error=InterruptedError});return true;
    }
    public void ResumePending()
    {
        lock(_gate)foreach(var orphan in store.Jobs().Where(x=>x.Type=="edit-render"&&x.Status is "pausing" or "cancelling").ToArray())MarkInterruptedIfOrphan(orphan);
        var renders=studio.Renders();var clips=store.Clips();
        foreach(var job in store.PendingJobs("edit-render"))
        {
            var render=renders.FirstOrDefault(x=>x.Id==job.Id);var clip=render is null?null:clips.FirstOrDefault(x=>x.Id==render.ClipId);
            if(render is null||(clip is null&&render.ProjectIdentity is null))store.SaveJob(job with {Status="failed",Error="找不到待剪辑视频"});else Schedule(job,render,clip);
        }
    }
    public bool Control(string id,string action)
    {
        var render=studio.Renders().FirstOrDefault(x=>x.Id==id);var job=store.Jobs().FirstOrDefault(x=>x.Id==id);if(render is null||job is null)return false;
        lock(_gate)
        {
            job=store.Jobs().FirstOrDefault(x=>x.Id==id)??job;
            if(action is "pause" or "cancel")
            {
                // 受理即返回：状态先进 cancelling/pausing，终态 cancelled/paused 由 worker 在 RenderAsync 停止并清理后发布。
                var stopping=action=="pause"?"pausing":"cancelling";
                if(job.Status==stopping&&_controls.ContainsKey(id))return true;
                if(MarkInterruptedIfOrphan(job))throw new ArgumentException(InterruptedError);
                if(job.Status is not ("running" or "pending")&&!(action=="cancel"&&job.Status=="pausing"))throw new ArgumentException("该任务当前无法暂停或取消");
                // 无 control 说明渲染进程不归本服务持有：不伪造终态、不删文件，保留状态与产物。
                if(!_controls.TryGetValue(id,out var stop))throw new ArgumentException("任务未由当前服务持有，无法确认停止；状态与产物已保留");
                store.SaveJob(job with {Status=stopping});stop.Cancel();
                return true;
            }
            if(MarkInterruptedIfOrphan(job))throw new ArgumentException(InterruptedError);
            if(job.Status is "pausing" or "cancelling")throw new ArgumentException("任务正在停止，请稍后继续");
            if(job.Status=="failed"&&job.Error?.StartsWith(InterruptedPrefix,StringComparison.Ordinal)==true)throw new ArgumentException("停止确认曾被重启中断，旧进程可能仍占用原输出，请重新发起新导出");
            if(action is not ("resume" or "retry")||job.Status is not ("paused" or "failed" or "cancelled"))throw new ArgumentException("该任务当前无法继续");
            if(_controls.ContainsKey(id))throw new ArgumentException("任务正在停止，请稍后继续");
            var clip=store.Clips().FirstOrDefault(x=>x.Id==render.ClipId);
            if(clip is null&&render.ProjectIdentity is null)throw new ArgumentException("源视频不存在");
            store.SaveJob(job=job with {Status="pending",Progress=0,Error=null});Schedule(job,render,clip);return true;
        }
    }
    public async Task StopAsync(){_stop.Cancel();Task[] jobs;lock(_tasks)jobs=_tasks.ToArray();try{await Task.WhenAll(jobs);}catch(OperationCanceledException){}}
    private void Schedule(Job job,EditorRender render,Clip? clip)
    {
        var control=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);if(!_controls.TryAdd(job.Id,control)){control.Dispose();return;}
        var configuredWorkDirectory=renderWorkDirectory?.Invoke();
        var task=Task.Run(()=>RunAsync(job,render,clip,control,configuredWorkDirectory));lock(_tasks)_tasks.Add(task);
    }
    private static string N(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);
    private static double EffectiveSpeed(TimelineItem item)=>Math.Clamp(item.Speed,0.25,4);
    /// <summary>片段在成片时间线上的生效时长（源区间长度除以速度）；导出清单的 part duration 也用它。</summary>
    public static double ItemOutputDuration(TimelineItem item)=>Math.Max(.001,(item.End-item.Start)/EffectiveSpeed(item));
    private static string AudioFilter(TimelineItem item)
    {
        var filters=new List<string>();
        var gain=Math.Pow(10,Math.Clamp(item.VolumeDb,-60,12)/20);
        if(Math.Abs(gain-1)>.001)filters.Add($"volume={N(gain)}");
        var speed=EffectiveSpeed(item);if(Math.Abs(speed-1)>.0001)filters.Add(Atempo(speed));
        // 音频效果放在 atempo 之后，按输出时间轴处理；数组内按前端顺序串接。
        var effects=AudioEffectFilters(item);
        if(effects.Length>0)
        {
            // Output -ar resamples after filters; EQ needs 48 kHz before evaluating frequencies up to 22 kHz.
            if(NormalizeAudioEffects(item).Any(effect=>effect.Enabled&&effect.Type=="parametric_eq"&&(
                effect.Params!["hp_on"]!=0||effect.Params["lp_on"]!=0||effect.Params["master_gain"]!=0||
                ParametricEqBands.Any(band=>effect.Params[$"{band.Prefix}_on"]!=0&&effect.Params[$"{band.Prefix}_gain"]!=0))))
                filters.Add("aresample=48000");
            filters.Add(effects);
        }
        if(item.CatalogAudioFx is not null || item.CatalogAudioFxVersion is not null)
        {
            if(item.CatalogAudioFxVersion!=1 || item.CatalogAudioFx is null)
                throw new ArgumentException(EditorFx.AudioFxChain.InvalidVersion);
            if(item.Track is not (1 or 4 or 5 or 6))
                throw new ArgumentException(EditorFx.AudioFxChain.UnsupportedTrack);
            var catalogAudio=EditorFx.AudioFxChain.Build(item.CatalogAudioFx);
            if(catalogAudio.Length>0)
            {
                // Match replica Hz units at a stable rate; retain legacy host effects before this separate chain.
                filters.Add("aresample=48000");
                filters.Add(catalogAudio);
                // Keep the source channel matrix through all DSP. FFmpeg 9 can crash when
                // aeval c=same negotiates mono directly with the stereo encoder's auto-resampler.
                // An explicit output resampler separates that negotiation without an upstream upmix.
                if(catalogAudio.Contains("aeval=",StringComparison.Ordinal))filters.Add("aresample");
            }
        }
        // 包络在 atempo 之后用输出时间轴采样：前端 volumeKeyframes.time 就是片段输出时间轴内的相对秒
        // （0..ItemOutputDuration），与 ffmpeg volume=eval=frame 的 t 完全一一对应。
        var envelope=VolumeEnvelopeFilter(item);if(envelope.Length>0)filters.Add(envelope);
        var length=ItemOutputDuration(item);
        var fadeIn=Math.Clamp(item.FadeIn,0,length);
        var fadeOut=Math.Clamp(item.FadeOut,0,length);
        if(fadeIn>0)filters.Add($"afade=t=in:st=0:d={N(fadeIn)}");
        if(fadeOut>0)filters.Add($"afade=t=out:st={N(Math.Max(0,length-fadeOut))}:d={N(fadeOut)}");
        return string.Join(',',filters);
    }
    /// <summary>供测试与导出复用：片段音频滤镜链（volumeDb → atempo → 音频效果 → 包络 → fade）。</summary>
    public static string AudioFilterChain(TimelineItem item)=>AudioFilter(item);
    private static readonly (string Prefix,double Frequency,double Q,string Filter)[] ParametricEqBands=
    [
        ("low",100,.707,"lowshelf"),
        ("b1",200,1,"equalizer"),
        ("b2",500,1,"equalizer"),
        ("mid",1000,1,"equalizer"),
        ("b4",2000,1,"equalizer"),
        ("b5",5000,1,"equalizer"),
        ("high",8000,.707,"highshelf")
    ];
    /// <summary>归一化效果类型（接受大小写、连字符及既有别名），未知类型返回 null 并丢弃。</summary>
    public static string? AudioEffectType(string? type)=>(type??"").Trim().ToLowerInvariant() switch
    {
        "compressor" or "comp" =>"compressor",
        "noisegate" or "noise-gate" or "noise_gate" or "gate" =>"noiseGate",
        "parametric_eq" or "parametric-eq" or "parametriceq" =>"parametric_eq",
        _=>null
    };
    /// <summary>
    /// 归一化片段音频效果：丢弃未知类型、补齐 id、按类型 clamp 参数到 ffmpeg 安全区间、
    /// 参数含 NaN/Inf 的整条效果丢弃、最多保留 4 个。返回的结果带各类型的标准参数键，
    /// 关闭（Enabled=false）的效果保留在结果里但不生成滤镜。Validate 只调用它做静默归一，不拒绝工程。
    /// </summary>
    public static AudioEffectInstance[] NormalizeAudioEffects(IEnumerable<AudioEffectInstance>? effects)
    {
        if(effects is null)return [];
        var normalized=new List<AudioEffectInstance>();
        var index=0;
        foreach(var effect in effects)
        {
            index++;
            if(effect is null)continue;
            var type=AudioEffectType(effect.Type);if(type is null)continue;
            var id=string.IsNullOrWhiteSpace(effect.Id)?$"fx-{index}":effect.Id.Trim();
            var raw=effect.Params;
            var values=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
            if(raw is not null)foreach(var pair in raw)values[pair.Key]=pair.Value;
            var bad=false;
            double Read(string key,double fallback,double min,double max)
            {
                if(!values.TryGetValue(key,out var value)||!double.IsFinite(value))return fallback;
                return Math.Clamp(value,min,max);
            }
            foreach(var value in values.Values)if(!double.IsFinite(value))bad=true;
            if(bad)continue;
            Dictionary<string,double> parameters;
            if(type=="compressor")
            {
                parameters=new()
                {
                    ["thresholdDb"]=Read("thresholdDb",-20,-60,0),
                    ["ratio"]=Read("ratio",4,1,20),
                    ["attackMs"]=Read("attackMs",10,.1,2000),
                    ["releaseMs"]=Read("releaseMs",100,1,9000),
                    ["makeupDb"]=Read("makeupDb",0,0,24),
                    ["kneeDb"]=Read("kneeDb",6,1,8)
                };
            }
            else if(type=="parametric_eq")
            {
                double Discrete(string key,double fallback,double max)=>Math.Round(Read(key,fallback,0,max),MidpointRounding.AwayFromZero);
                parameters=new()
                {
                    ["master_gain"]=Read("master_gain",0,-30,30),
                    ["hp_on"]=Discrete("hp_on",0,1),
                    ["hp_freq"]=Read("hp_freq",30,10,20000),
                    ["hp_slope"]=Discrete("hp_slope",0,3)
                };
                foreach(var band in ParametricEqBands)
                {
                    parameters[$"{band.Prefix}_on"]=Discrete($"{band.Prefix}_on",1,1);
                    parameters[$"{band.Prefix}_freq"]=Read($"{band.Prefix}_freq",band.Frequency,20,20000);
                    parameters[$"{band.Prefix}_gain"]=Read($"{band.Prefix}_gain",0,-30,30);
                    parameters[$"{band.Prefix}_q"]=Read($"{band.Prefix}_q",band.Q,.1,30);
                }
                parameters["lp_on"]=Discrete("lp_on",0,1);
                parameters["lp_freq"]=Read("lp_freq",18000,20,22000);
                parameters["lp_slope"]=Discrete("lp_slope",0,3);
            }
            else
            {
                parameters=new()
                {
                    ["thresholdDb"]=Read("thresholdDb",-40,-80,0),
                    ["ratio"]=Read("ratio",2,1,20),
                    ["attackMs"]=Read("attackMs",5,.1,9000),
                    ["releaseMs"]=Read("releaseMs",100,1,9000),
                    ["rangeDb"]=Read("rangeDb",24,0,60)
                };
            }
            normalized.Add(effect with {Id=id,Type=type,Params=parameters});
            if(normalized.Count>=4)break;
        }
        return normalized.ToArray();
    }
    /// <summary>片段级归一化版本：无 Effects 时返回空数组（旧工程行为完全不变）。</summary>
    public static AudioEffectInstance[] NormalizeAudioEffects(TimelineItem item)=>NormalizeAudioEffects(item.Effects);
    private static double DbToLinear(double db)=>Math.Pow(10,db/20);
    /// <summary>
    /// 生成音频效果滤镜串（不含前后逗号）：acompressor / agate / parametric_eq，参数从归一化结果取。
    /// 关闭的效果与空效果列表返回空串，从而保证无效果时滤镜链与旧版逐字节一致。
    /// </summary>
    public static string AudioEffectFilters(TimelineItem item)
    {
        var effects=NormalizeAudioEffects(item);
        if(effects.Length==0)return "";
        var filters=new List<string>();
        foreach(var effect in effects)
        {
            if(!effect.Enabled)continue;
            var parameters=effect.Params??new Dictionary<string,double>();
            double Value(string key,double fallback)=>parameters.TryGetValue(key,out var value)?value:fallback;
            if(effect.Type=="compressor")
            {
                filters.Add($"acompressor=threshold={N(DbToLinear(Value("thresholdDb",-20)))}:ratio={N(Value("ratio",4))}:attack={N(Value("attackMs",10))}:release={N(Value("releaseMs",100))}:makeup={N(DbToLinear(Value("makeupDb",0)))}:knee={N(Value("kneeDb",6))}");
            }
            else if(effect.Type=="noiseGate")
            {
                var range=Math.Clamp(Math.Pow(10,-Value("rangeDb",24)/20),0,1);
                filters.Add($"agate=threshold={N(DbToLinear(Value("thresholdDb",-40)))}:ratio={N(Value("ratio",2))}:attack={N(Value("attackMs",5))}:release={N(Value("releaseMs",100))}:range={N(range)}");
            }
            else if(effect.Type=="parametric_eq")
            {
                void AddPass(string prefix,string filter,double frequency)
                {
                    if(Value($"{prefix}_on",0)==0)return;
                    var stages=(int)Value($"{prefix}_slope",0)+1;
                    // Fixed-Q cascades approximate slope choices, not the engine's per-stage Butterworth Q.
                    for(var stage=0;stage<stages;stage++)
                        filters.Add($"{filter}=f={N(Value($"{prefix}_freq",frequency))}:p=2:t=q:w=0.707107:r=f64");
                }
                AddPass("hp","highpass",30);
                AddPass("lp","lowpass",18000);
                foreach(var band in ParametricEqBands)
                {
                    var gain=Value($"{band.Prefix}_gain",0);
                    if(Value($"{band.Prefix}_on",1)==0||gain==0)continue;
                    filters.Add($"{band.Filter}=f={N(Value($"{band.Prefix}_freq",band.Frequency))}:t=q:w={N(Value($"{band.Prefix}_q",band.Q))}:g={N(gain)}:r=f64");
                }
                var master=Value("master_gain",0);
                if(master!=0)filters.Add($"volume={N(master)}dB:precision=double");
            }
        }
        return string.Join(',',filters);
    }
    /// <summary>
    /// 归一化音量包络点：过滤非有限值、clamp db 到 -60..12 dB、按 time 升序、同一 time 保留最后一条、
    /// 最多 64 个点；负 time clamp 到 0。time 与片段输出时间轴同一坐标（0..ItemOutputDuration）。
    /// </summary>
    public static VolumeKeyframe[] NormalizeVolumeKeyframes(IEnumerable<VolumeKeyframe>? points)
    {
        if(points is null)return [];
        var byTime=new SortedDictionary<double,VolumeKeyframe>();
        foreach(var point in points)
        {
            if(point is null||!double.IsFinite(point.Time)||!double.IsFinite(point.Db))continue;
            var time=Math.Round(Math.Max(0,point.Time),6);
            byTime[time]=new VolumeKeyframe(time,Math.Clamp(point.Db,-60,12));
        }
        return byTime.Values.Take(64).ToArray();
    }
    /// <summary>
    /// 片段级归一化：在结构归一化基础上把 time 限制在片段的输出时长内（超出末尾的点丢弃，
    /// 因为 volumeKeyframes 的定义域是 0..ItemOutputDuration）。无包络时返回空数组。
    /// </summary>
    public static VolumeKeyframe[] NormalizeVolumeKeyframes(TimelineItem item)
    {
        var points=NormalizeVolumeKeyframes(item.VolumeKeyframes);
        if(points.Length==0)return [];
        var duration=ItemOutputDuration(item);
        return points.Where(x=>x.Time<=duration+1e-6).Select(x=>x.Time>duration?new VolumeKeyframe(duration,x.Db):x).ToArray();
    }
    /// <summary>
    /// 按包络采样线性增益（不是 dB 值）：seconds 为片段输出时间轴内的相对秒；
    /// 点之间按 dB 线性插值，首点之前/末点之后保持端点值；无包络时返回 1。
    /// </summary>
    public static double VolumeGainAt(TimelineItem item,double seconds)
    {
        var points=NormalizeVolumeKeyframes(item);
        if(points.Length==0)return 1;
        var time=Math.Max(0,double.IsFinite(seconds)?seconds:0);
        double db;
        if(time<=points[0].Time)db=points[0].Db;
        else if(time>=points[^1].Time)db=points[^1].Db;
        else
        {
            var index=0;
            while(index<points.Length-2&&time>=points[index+1].Time)index++;
            var a=points[index];var b=points[index+1];
            var span=b.Time-a.Time;
            db=span<=1e-9?a.Db:a.Db+(b.Db-a.Db)*(time-a.Time)/span;
        }
        return Math.Pow(10,db/20);
    }
    /// <summary>
    /// 生成 ffmpeg 包络滤镜 `volume=volume='&lt;分段 if&gt;':eval=frame`。
    /// 表达式变量 t 是滤镜链当前时间；包络滤镜放在 atempo 之后，因此 t 就是片段输出相对秒。
    /// 分段内按 dB 线性插值（等价于对线性增益做指数插值），首点前/末点后取端点。
    /// </summary>
    public static string VolumeEnvelopeFilter(TimelineItem item)
    {
        var points=NormalizeVolumeKeyframes(item);
        if(points.Length==0)return "";
        var expression=N(Math.Pow(10,points[^1].Db/20));
        for(var i=points.Length-2;i>=0;i--)
        {
            var a=points[i];var b=points[i+1];
            var segment=Math.Abs(b.Db-a.Db)<1e-9
                ?N(Math.Pow(10,a.Db/20))
                :$"pow(10,({N(a.Db)}+({N(b.Db-a.Db)})*(t-{N(a.Time)})/{N(b.Time-a.Time)})/20)";
            expression=$"if(lt(t,{N(b.Time)}),{segment},{expression})";
        }
        expression=$"if(lt(t,{N(points[0].Time)}),{N(Math.Pow(10,points[0].Db/20))},{expression})";
        return $"volume=volume='{expression}':eval=frame";
    }
    /// <summary>可关键帧的变换参数白名单（冻结顺序，与 web/src/api/types.ts 的 ParamKeyframeProperty 一致）。</summary>
    public static readonly string[] ParamKeyframeProperties=["scale","rotation","positionX","positionY","opacity"];
    public const int MaxParamKeyframes=64;
    public const double ParamKeyframeMinGap=0.02;
    private static bool TryParamKeyframeRange(string prop,out double min,out double max)
    {
        switch(prop)
        {
            case "scale":min=.5;max=3;return true;
            case "rotation":min=-180;max=180;return true;
            case "positionX":case "positionY":min=-.5;max=.5;return true;
            case "opacity":min=0;max=1;return true;
            default:min=0;max=0;return false;
        }
    }
    /// <summary>把外部参数名归一化到白名单；未知/空白返回 null（未知键静默丢弃）。</summary>
    public static string? NormalizeParamKeyframeProperty(string? prop)
    {
        var value=prop?.Trim();
        return value is not null&&ParamKeyframeProperties.Contains(value,StringComparer.Ordinal)?value:null;
    }
    /// <summary>
    /// 归一化某参数的关键帧：过滤非有限值、time clamp 到 [0, span]（span 为该片段输出时长）、
    /// value clamp 到参数范围、按 time 升序、time 间隔 &lt;0.02s 视为同点保留后者、最多 64 点；
    /// 数值按 3 位小数取整（与前端 normalizeParamKeyframes 完全一致）。
    /// </summary>
    public static ParamKeyframe[] NormalizeParamKeyframes(string prop,IEnumerable<ParamKeyframe>? points,double span)
    {
        var canonical=NormalizeParamKeyframeProperty(prop);
        if(canonical is null||!TryParamKeyframeRange(canonical,out var min,out var max))return [];
        var maxTime=Math.Max(0,double.IsFinite(span)?span:0);
        var sorted=new List<ParamKeyframe>();
        foreach(var point in points??[])
        {
            if(point is null||!double.IsFinite(point.Time)||!double.IsFinite(point.Value))continue;
            var time=Math.Round(Math.Clamp(point.Time,0,maxTime),3);
            sorted.Add(new ParamKeyframe(time,Math.Round(Math.Clamp(point.Value,min,max),3)));
        }
        sorted.Sort((a,b)=>a.Time.CompareTo(b.Time));
        var kept=new List<ParamKeyframe>();
        foreach(var point in sorted)
        {
            if(kept.Count>0&&point.Time-kept[^1].Time<ParamKeyframeMinGap){kept[^1]=point;continue;}
            kept.Add(point);
        }
        return kept.Take(MaxParamKeyframes).ToArray();
    }
    /// <summary>片段级归一化关键帧字典：只保留归一化后非空的参数；无关键帧返回 null（旧工程 JSON 不变）。</summary>
    public static Dictionary<string,ParamKeyframe[]>? NormalizeKeyframes(TimelineItem item)
    {
        if(item.Keyframes is not {Count:>0})return null;
        var span=ItemOutputDuration(item);
        var result=new Dictionary<string,ParamKeyframe[]>(StringComparer.Ordinal);
        foreach(var prop in ParamKeyframeProperties)
        {
            if(!item.Keyframes.TryGetValue(prop,out var points))continue;
            var normalized=NormalizeParamKeyframes(prop,points,span);
            if(normalized.Length>0)result[prop]=normalized;
        }
        return result.Count>0?result:null;
    }
    /// <summary>片段某参数归一化后的关键帧数组（无关键帧为空数组）。</summary>
    public static ParamKeyframe[] ParamKeyframesFor(TimelineItem item,string prop)=>
        NormalizeParamKeyframes(prop,item.Keyframes is not null&&item.Keyframes.TryGetValue(prop,out var points)?points:null,ItemOutputDuration(item));
    /// <summary>参数在片段上的静态基准值（无关键帧时的回退）：scale←Zoom、rotation/positionX/Y/opacity←同名字段。</summary>
    public static double BaseParamValue(TimelineItem item,string prop)=>prop switch
    {
        "scale"=>double.IsFinite(item.Zoom)&&item.Zoom>0?item.Zoom:1,
        "rotation"=>double.IsFinite(item.Rotation)?item.Rotation:0,
        "positionX"=>double.IsFinite(item.PositionX)?item.PositionX:0,
        "positionY"=>double.IsFinite(item.PositionY)?item.PositionY:0,
        "opacity"=>double.IsFinite(item.Opacity)?item.Opacity:1,
        _=>0,
    };
    /// <summary>按关键帧采样参数值：线性插值，首点之前/末点之后保持端点；无点返回 fallback。</summary>
    public static double ParamValueAt(IReadOnlyList<ParamKeyframe> points,double localTime,double fallback)
    {
        if(points.Count==0)return fallback;
        var time=Math.Max(0,double.IsFinite(localTime)?localTime:0);
        if(time<=points[0].Time)return points[0].Value;
        if(time>=points[^1].Time)return points[^1].Value;
        var index=0;
        while(index<points.Count-2&&time>=points[index+1].Time)index++;
        var a=points[index];var b=points[index+1];
        var span=b.Time-a.Time;
        return span<=1e-9?b.Value:a.Value+(b.Value-a.Value)*(time-a.Time)/span;
    }
    /// <summary>片段某参数在输出时间轴 localTime 处的实际值：有关键帧走插值，否则用静态值。</summary>
    public static double ParamValueAtItem(TimelineItem item,string prop,double localTime)=>
        ParamValueAt(ParamKeyframesFor(item,prop),localTime,BaseParamValue(item,prop));
    public static bool HasParamKeyframes(TimelineItem item,string prop)=>ParamKeyframesFor(item,prop).Length>0;
    /// <summary>
    /// 生成参数关键帧的分段线性 ffmpeg 表达式（首尾 hold、段内线性）。
    /// 变量约定：scale 用 zoompan 的 on，位置/旋转用 n；滤镜位于 setpts 之前，因此用
    /// n ÷ (fps × speed) 换算成片段输出相对秒。未知参数/无关键帧会抛带片段 id 与参数名的异常。
    /// </summary>
    public static string ParamKeyframeExpression(TimelineItem item,string prop,string timeVariable,double frameRate,double speed=1)
    {
        if(NormalizeParamKeyframeProperty(prop) is null)
            throw new ArgumentException($"片段 {item.Id} 的参数 {prop} 不支持关键帧");
        var points=ParamKeyframesFor(item,prop);
        if(points.Length==0)
            throw new ArgumentException($"片段 {item.Id} 的参数 {prop} 没有可用的关键帧");
        var rate=double.IsFinite(frameRate)&&frameRate>0?frameRate:30;
        var factor=rate*(double.IsFinite(speed)&&speed>0?speed:1);
        var time=$"({timeVariable}/{N(factor)})";
        var expression=N(points[^1].Value);
        for(var i=points.Length-2;i>=0;i--)
        {
            var a=points[i];var b=points[i+1];
            var segment=Math.Abs(b.Value-a.Value)<1e-9
                ?N(a.Value)
                :$"({N(a.Value)}+({N(b.Value-a.Value)})*({time}-{N(a.Time)})/{N(b.Time-a.Time)})";
            expression=$"if(lt({time},{N(b.Time)}),{segment},{expression})";
        }
        return $"if(lt({time},{N(points[0].Time)}),{N(points[0].Value)},{expression})";
    }
    /// <summary>
    /// 归一化静态蒙版：type 非 ellipse 视为 rect、id 缺省补 mask-N、全部数值 clamp（x/y ±0.5、
    /// w/h 0.01..2、rotation ±180、feather 0..100、opacity 0..1）、最多 4 条；无有效蒙版返回 null。
    /// </summary>
    public static MaskInstance[]? NormalizeMasks(TimelineItem item)
    {
        if(item.Masks is not {Length:>0})return null;
        var result=new List<MaskInstance>();
        foreach(var mask in item.Masks.Take(4))
        {
            if(mask is null)continue;
            var type=mask.Type=="ellipse"?"ellipse":mask.Type=="polygon"?"polygon":"rect";
            var points=type=="polygon"?NormalizeMaskPoints(mask.Points):null;
            // 多边形至少 3 个有效顶点，否则整条丢弃（渲染端无法表达退化多边形）。
            if(type=="polygon"&&points is null)continue;
            result.Add(new MaskInstance(
                string.IsNullOrWhiteSpace(mask.Id)?$"mask-{result.Count+1}":mask.Id,
                type,mask.Enabled,
                ClampFinite(mask.X,-.5,.5,0),ClampFinite(mask.Y,-.5,.5,0),
                ClampFinite(mask.W,.01,2,.6),ClampFinite(mask.H,.01,2,.6),
                ClampFinite(mask.Rotation,-180,180,0),
                ClampFinite(mask.Feather,0,100,0),
                ClampFinite(mask.Opacity,0,1,1),points));
        }
        return result.Count>0?result.ToArray():null;
    }
    private static double ClampFinite(double value,double min,double max,double fallback)=>
        double.IsFinite(value)?Math.Clamp(value,min,max):fallback;

    /// <summary>
    /// 归一化多边形顶点：clamp 到 0..1、三位小数、去掉连续重复点，最多 24 个；有效顶点不足 3 个返回 null。
    /// </summary>
    public static MaskPoint[]? NormalizeMaskPoints(MaskPoint[]? points)
    {
        if(points is not {Length:>=3})return null;
        var result=new List<MaskPoint>(Math.Min(points.Length,24));
        foreach(var point in points)
        {
            if(point is null)continue;
            if(result.Count>=24)break;
            var x=Math.Round(ClampFinite(point.X,0,1,0),3);
            var y=Math.Round(ClampFinite(point.Y,0,1,0),3);
            if(result.Count>0)
            {
                var last=result[^1];
                if(Math.Abs(last.X-x)<1e-6&&Math.Abs(last.Y-y)<1e-6)continue;
            }
            result.Add(new MaskPoint(x,y));
        }
        if(result.Count>=3)
        {
            var first=result[0];var last=result[^1];
            if(Math.Abs(first.X-last.X)<1e-6&&Math.Abs(first.Y-last.Y)<1e-6)result.RemoveAt(result.Count-1);
        }
        return result.Count>=3?result.ToArray():null;
    }
    private static string Atempo(double speed)
    {
        var filters=new List<string>();var remaining=speed;
        while(remaining<.5-1e-9){filters.Add("atempo=0.5");remaining/=.5;}
        while(remaining>2+1e-9){filters.Add("atempo=2");remaining/=2;}
        filters.Add($"atempo={N(remaining)}");
        return string.Join(',',filters);
    }
    internal static string FilterPath(string path)=>Path.GetFullPath(path).Replace('\\','/').Replace(":","\\:").Replace("'","\\'").Replace("[","\\[").Replace("]","\\]");
    /// <summary>
    /// 起点对齐（start）需要前一片段在源素材 End 之后仍有 d/2 的尾部素材，因此存在 start 对齐时
    /// 先探测主轨相关源视频时长，用于按可用 handle clamp 转场时长（规格 §10）。
    /// 探测失败（文件缺失/stems 等）时返回 null，按乐观假设渲染，由渲染阶段给出明确错误。
    /// </summary>
    private async Task<Dictionary<string,double>?> ProbeTransitionClipDurationsAsync(TimelineItem[] items,EditorTransition[]? transitions,IReadOnlyDictionary<string,Clip> clips,CancellationToken token)
    {
        if(transitions is not {Length:>0}||!transitions.Any(x=>x is not null&&TransitionAlign(x.Align)=="start"))return null;
        var durations=new Dictionary<string,double>(StringComparer.Ordinal);
        foreach(var clipId in BaseItems(items).Select(x=>x.ClipId).Distinct(StringComparer.Ordinal))
        {
            if(!clips.TryGetValue(clipId,out var clip)||!File.Exists(clip.Path)||durations.ContainsKey(clipId))continue;
            try{durations[clipId]=await media.DurationAsync(clip.Path,token);}
            catch(Exception ex) when(ex is not OperationCanceledException){}
        }
        return durations.Count>0?durations:null;
    }
    public async Task<(string Video,IReadOnlyList<TranscriptSegment> Subtitles,double Length,double Fps,IReadOnlyDictionary<string,double>? Starts)> AssembleAsync(Clip primary,EditorProject plan,string work,CancellationToken token,Action<double>? progress=null)
        =>await AssembleCoreAsync(primary.Id,null,plan,work,token,progress);
    private async Task<(string Video,IReadOnlyList<TranscriptSegment> Subtitles,double Length,double Fps,IReadOnlyDictionary<string,double>? Starts)> AssembleCoreAsync(string ownerId,EditorProjectIdentity? identity,EditorProject plan,string work,CancellationToken token,Action<double>? progress=null)
    {
        plan=FlattenProject(plan);Validate(plan);Directory.CreateDirectory(work);var allItems=Items(plan,ownerId);var items=BaseItems(allItems);var clips=store.Clips().ToDictionary(x=>x.Id);
        var clipDurations=await ProbeTransitionClipDurationsAsync(allItems,plan.Transitions,clips,token);
        var graphicsOnly=allItems.All(EditorProjectGraphics.IsSourceFree);
        int width,height;double fps;
        if(identity is not null)
        {
            width=identity.Width;height=identity.Height;fps=identity.Fps;
        }
        else if(plan.CanvasWidth is {} canvasWidth && plan.CanvasHeight is {} canvasHeight)
        {
            width=canvasWidth;height=canvasHeight;fps=plan.Fps;
        }
        else if(graphicsOnly)
        {
            height=plan.Resolution==0?720:plan.Resolution;
            width=Math.Max(2,(int)Math.Round(height*16.0/9)/2*2);fps=plan.Fps==0?30:plan.Fps;
        }
        else
        {
            var primarySource=items.Length>0&&clips.TryGetValue(items[0].ClipId,out var firstSource)?firstSource:clips[ownerId];
            var info=await media.ProbeAsync(primarySource.Path,token);
            height=plan.Resolution==0?info.Height:Math.Min(info.Height,plan.Resolution);height=Math.Max(2,height/2*2);
            width=Math.Max(2,(int)Math.Round(info.Width*(double)height/info.Height)/2*2);fps=plan.Fps==0?Math.Clamp(info.Fps,1,60):plan.Fps;
        }
        // DNxHR（dnxhd 编码器）要求画面至少 256x120，更小的画布会在编码器内部失败并只留下 "-22 / Input dimensions
        // too small" 这类不可读报错；在这里、在跑任何 ffmpeg 之前提前用中文明确拒绝。
        EditorExportFormats.RequireDnxhrCanvas(plan.Format,width,height);
        var cuts=ResolveTransitions(allItems,plan.Transitions,TransitionFps(plan),width,height,clipDurations);var v1Cuts=cuts.Where(x=>x.Track==1).ToArray();var starts=cuts.Length==0&&!allItems.Any(x=>x.SequenceStart is not null)?null:SequenceStarts(allItems,cuts);
        var totalDuration=cuts.Length==0?TimelineDuration(allItems):SequenceDuration(allItems,cuts);
        var pieces=new List<string>();var pieceDurations=new List<double>();var pieceOfItem=new Dictionary<string,int>();
        for(var i=0;i<items.Length;i++)
        {
            token.ThrowIfCancellationRequested();var item=items[i];
            // 规格 §5(4)：落在转场区间里的那一侧淡化交给转场本身，避免和 xfade 叠成双重淡化；未邻接转场的淡入淡出完全保留。
            if(v1Cuts.Any(x=>x.ItemId==item.Id)&&item.FadeIn>0)item=item with {FadeIn=0};
            if(v1Cuts.Any(x=>x.PreviousItemId==item.Id)&&item.FadeOut>0)item=item with {FadeOut=0};
            if(item.GapBefore>0.001)
            {
                var gapPart=Path.Combine(work,$"gap-{i:000}.mkv");
                List<string> gapArgs=["-hide_banner","-loglevel","error","-y","-f","lavfi","-i",$"color=c=black:s={width}x{height}:r={N(fps)}:d={N(item.GapBefore)}","-f","lavfi","-i","anullsrc=r=48000:cl=stereo","-t",N(item.GapBefore),"-map","0:v:0","-map","1:a:0",..IntermediateVideoCodecArgs(plan),..IntermediateAudioCodecArgs(),gapPart];
                ConfigureIntermediateArgs(gapArgs,plan);
                await media.RunAsync(media.Ffmpeg,gapArgs,token);pieces.Add(gapPart);pieceDurations.Add(item.GapBefore);
            }
            if(!clips.TryGetValue(item.ClipId,out var source)||!File.Exists(source.Path))throw new InvalidOperationException("时间线源文件已移动或不存在");
            var duration=await media.DurationAsync(source.Path,token);if(item.End>duration+.05)throw new ArgumentException($"片段结束时间超过源视频长度 {duration:0.0} 秒");
            var sourceInfo=await media.ProbeAsync(source.Path,token);var part=Path.Combine(work,$"part-{i:000}.mkv");
            var speed=EffectiveSpeed(item);var sourceLength=item.End-item.Start;
            // 对齐三态会改变前一片段的可见尾部（规格 §10）：start 多取 d/2 秒 handle，end 少取 d/2 秒，center 不变。
            var outgoing=v1Cuts.FirstOrDefault(x=>x.PreviousItemId==item.Id);var tailAdjust=outgoing is null?0:TransitionTailAdjust(outgoing);
            if(tailAdjust>0)
            {
                var extra=Math.Min(Math.Max(0,duration-item.End),tailAdjust*speed);
                if(extra<tailAdjust*speed-1e-6)throw new ArgumentException($"起点对齐转场需要前一片段尾部 {N(tailAdjust)} 秒素材，但源视频剩余不足（{CutLabel(outgoing!,fps)}）");
                sourceLength+=extra;
            }
            else if(tailAdjust<0)sourceLength=Math.Max(.001,sourceLength+tailAdjust*speed);
            if(sourceLength<=.001)throw new ArgumentException($"片段太短，无法渲染转场（片段 {item.Id}）");
            var length=sourceLength/speed;
            if(item.Disabled)
            {
                List<string> hiddenArgs=["-hide_banner","-loglevel","error","-y","-f","lavfi","-i",$"color=c=black:s={width}x{height}:r={N(fps)}:d={N(length)}","-f","lavfi","-i","anullsrc=r=48000:cl=stereo","-t",N(length),"-map","0:v:0","-map","1:a:0",..IntermediateVideoCodecArgs(plan),..IntermediateAudioCodecArgs(),part];
                ConfigureIntermediateArgs(hiddenArgs,plan);
                await media.RunAsync(media.Ffmpeg,hiddenArgs,token);pieces.Add(part);pieceDurations.Add(length);pieceOfItem[item.Id]=pieces.Count-1;progress?.Invoke(.1+.65*(i+1)/items.Length);continue;
            }
            var baseOpacity=TrackOpacity(plan.TrackOpacities,1);
            var visualItem=baseOpacity<.999?item with {Opacity=item.Opacity*baseOpacity}:item;
            // Decode through the cut before trimming: input -ss can drop the PCM packet straddling Start.
            var args=new List<string>{"-hide_banner","-loglevel","error","-y","-t",N(item.Start+sourceLength),"-i",source.Path};var audioInput=0;var audioStart=item.Start;
            if(item.AudioDetached){args.AddRange(["-f","lavfi","-i","anullsrc=r=48000:cl=stereo"]);audioInput=1;audioStart=0;}
            else if(plan.VoiceOnly&&item.Kind!="insert")
            {
                var stems=studio.Stems(item.ClipId);if(stems is null||!File.Exists(stems.VocalsPath))throw new InvalidOperationException("时间线中有视频尚未完成人声分离");
                args.AddRange(["-t",N(item.Start+sourceLength),"-i",stems.VocalsPath]);audioInput=1;
            }
            else if(!sourceInfo.HasAudio){args.AddRange(["-f","lavfi","-i","anullsrc=r=48000:cl=stereo"]);audioInput=1;audioStart=0;}
            args.AddRange(["-map","0:v:0","-map",$"{audioInput}:a:0","-vf",$"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={N(fps)}","-t",N(length),..IntermediateVideoCodecArgs(plan),..IntermediateAudioCodecArgs(),part]);
            var audioFilter=TimelineAudio.TrimFilter(audioStart,sourceLength);
            var effects=AudioFilter(item);if(effects.Length>0)audioFilter+=","+effects;
            args.InsertRange(args.IndexOf("-c:a"),["-af",audioFilter]);
            var trimmedVisual=$"trim=start={N(item.Start)}:duration={N(sourceLength)},setpts=PTS-STARTPTS,"+VisualFilter(visualItem,width,height,fps,RequiresHighBitDepth(plan));
            var vf=args.IndexOf("-vf");args[vf+1]=trimmedVisual;
            // 静态蒙版（matte PNG + maskedmerge）与 V1 opacity（blend 到黑底，逐帧表达式）都属于 part 级效果；
            // 两者都没有时保持上面的 -vf 路径，旧工程滤镜链逐字节不变。
            var masks=NormalizeMasks(item);
            var opacityExpression=OpacityExpression(item,baseOpacity);
            var hasOpacityEffect=opacityExpression!="1";
            var sticker=string.IsNullOrWhiteSpace(item.StickerAssetId)?null:studio.Assets().FirstOrDefault(x=>x.Id==item.StickerAssetId&&x.Kind=="sticker")??throw new InvalidOperationException("贴纸素材不存在");
            if(sticker is not null&&!File.Exists(sticker.Path))throw new InvalidOperationException("贴纸文件已移动");
            if(!hasOpacityEffect&&masks is null&&sticker is null)
            {
                // 无 part 级效果：完全沿用旧路径（含旧贴纸分支的形状）。
            }
            else if(masks is null&&!hasOpacityEffect&&sticker is not null)
            {
                var inputIndex=audioInput==1?2:1;
                args.RemoveRange(vf,2);var map=args.IndexOf("-map");args[map+1]="[vout]";
                var inputAt=args.IndexOf("-map");args.InsertRange(inputAt,["-loop","1","-i",sticker.Path]);
                var stickerInfo=await media.ProbeAsync(sticker.Path,token);var stickerWidth=Math.Max(2,(int)(width*item.StickerWidth));var stickerHeight=(int)Math.Round(stickerWidth*(double)stickerInfo.Height/stickerInfo.Width);
                var angle=N(item.StickerRotation*Math.PI/180);
                args.InsertRange(args.Count-1,["-filter_complex",$"[0:v]{trimmedVisual}[base];[{inputIndex}:v]scale={stickerWidth}:{stickerHeight},format=rgba,rotate={angle}:ow=rotw({angle}):oh=roth({angle}):c=black@0[sticker];[base][sticker]overlay=x=W*{N(item.StickerX)}+{stickerWidth}/2-w/2:y=H*{N(item.StickerY)}+{stickerHeight}/2-h/2:shortest=1[vout]"]);
            }
            else
            {
                var mattePath=masks is null?null:await RenderMaskMatteAsync(item,work,i,width,height,token);
                var nextInput=audioInput==0?1:2;var stickerIndex=-1;var maskIndex=-1;
                if(sticker is not null){args.InsertRange(args.IndexOf("-map"),["-loop","1","-i",sticker.Path]);stickerIndex=nextInput++;}
                if(mattePath is not null){args.InsertRange(args.IndexOf("-map"),["-loop","1","-framerate",N(fps),"-i",mattePath]);maskIndex=nextInput++;}
                var vfRemove=args.IndexOf("-vf");args.RemoveRange(vfRemove,2);
                args[args.IndexOf("-map")+1]="[vout]";
                var filters=new List<string>{$"[0:v]{trimmedVisual}[vbase]"};
                var current="[vbase]";
                if(sticker is not null)
                {
                    var stickerInfo=await media.ProbeAsync(sticker.Path,token);
                    var stickerWidth=Math.Max(2,(int)(width*item.StickerWidth));
                    var stickerHeight=(int)Math.Round(stickerWidth*(double)stickerInfo.Height/stickerInfo.Width);
                    var angle=N(item.StickerRotation*Math.PI/180);
                    filters.Add($"[{stickerIndex}:v]scale={stickerWidth}:{stickerHeight},format=rgba,rotate={angle}:ow=rotw({angle}):oh=roth({angle}):c=black@0[vsticker]");
                    filters.Add($"{current}[vsticker]overlay=x=W*{N(item.StickerX)}+{stickerWidth}/2-w/2:y=H*{N(item.StickerY)}+{stickerHeight}/2-h/2:shortest=1[voverlay]");
                    current="[voverlay]";
                }
                if(maskIndex>=0)
                {
                    // SDR matte samples are 0..255 coverage on every plane, not gray image colors.
                    var matteFilter = RequiresHighBitDepth(plan) ? "format=gray"
                        : "scale=in_range=full:out_range=full,format=yuv420p,setrange=limited,geq=lum='lum(X,Y)':cb='lum(X/SW,Y/SH)':cr='lum(X/SW,Y/SH)'";
                    filters.Add($"[{maskIndex}:v]fps={N(fps)},{matteFilter}[vmatte]");
                    filters.Add($"color=c=black:s={width}x{height}:r={N(fps)}[vmaskblack]");
                    // maskedmerge 的白色蒙版区域取 bottom 输入：top=画面、bottom=黑底、matte 外部白 → 外部变黑、内部保留画面。
                    filters.Add($"{current}[vmaskblack][vmatte]maskedmerge[vmasked]");
                    current="[vmasked]";
                }
                if(hasOpacityEffect)
                {
                    var opacityPixelFormat = RequiresHighBitDepth(plan) ? "rgb24" : "yuv420p";
                    filters.Add($"{current}format={opacityPixelFormat}[vopacity]");
                    filters.Add($"color=c=black:s={width}x{height}:r={N(fps)}[vopacityblack]");
                    filters.Add($"[vopacity][vopacityblack]blend=all_mode=normal:all_expr='B+(A-B)*({opacityExpression})':shortest=1,format=yuv420p[vout]");
                }
                else
                {
                    filters.Add($"{current}format=yuv420p[vout]");
                }
                args.InsertRange(args.Count-1,["-filter_complex",string.Join(';',filters)]);
            }
            ConfigureIntermediateArgs(args,plan);
            await media.RunAsync(media.Ffmpeg,args,token);pieces.Add(part);pieceDurations.Add(length);pieceOfItem[item.Id]=pieces.Count-1;progress?.Invoke(.1+.65*(i+1)/items.Length);
        }
        string joined;
        if(items.Length==0)
        {
            joined=Path.Combine(work,"base.mkv");
            List<string> baseArgs=["-hide_banner","-loglevel","error","-y","-f","lavfi","-i",$"color=c=black:s={width}x{height}:r={N(fps)}:d={N(totalDuration)}","-f","lavfi","-i","anullsrc=r=48000:cl=stereo","-t",N(totalDuration),"-map","0:v:0","-map","1:a:0",..IntermediateVideoCodecArgs(plan),..IntermediateAudioCodecArgs(),joined];
            ConfigureIntermediateArgs(baseArgs,plan);
            await media.RunAsync(media.Ffmpeg,baseArgs,token);
        }
        else if(items.Any(x=>x.SequenceStart is not null))
        {
            joined=await JoinAbsoluteSequenceAsync(pieces,items,v1Cuts,totalDuration,width,height,fps,work,plan,token);
        }
        else if(v1Cuts.Length>0)
        {
            try{joined=await JoinWithTransitionsAsync(pieces,pieceDurations,v1Cuts,pieceOfItem,totalDuration,fps,work,plan,token);}
            catch(Exception ex) when(ex is not OperationCanceledException)
            {
                var labels=string.Join("、",v1Cuts.Take(3).Select(x=>CutLabel(x,fps)));
                if(v1Cuts.Length>3)labels+=$" 等 {v1Cuts.Length} 个剪点";
                throw new InvalidOperationException($"转场渲染失败：{labels}：{ex.Message}",ex);
            }
        }
        else
        {
            // FFV1 streams can carry different pixel formats/extradata (for example black gaps and RGB clips).
            // Decode each multi-part input independently; concat demuxer stream-copy would use the first header for all.
            if(pieces.Count==1){joined=Path.Combine(work,"joined.mkv");await media.ConcatAsync(pieces,joined,token);}
            else joined=await JoinWithTransitionsAsync(pieces,pieceDurations,[],pieceOfItem,totalDuration,fps,work,plan,token);
        }
        progress?.Invoke(.8);
        if(plan.BaseAudioMuted||allItems.Any(x=>TrackOf(x)>1)||(plan.HiddenTracks??[]).Length>0||(plan.SoloedTracks??[]).Length>0||items.Length==0)joined=await ComposeOverlaysAsync(joined,allItems,clips,width,height,fps,totalDuration,plan.BaseAudioMuted,plan.MutedTracks??[],plan.SoloedTracks??[],plan.HiddenTracks??[],plan.TrackOpacities??[],work,plan,token,progress,cuts,starts);
        IReadOnlyList<TranscriptSegment> Source(string id)=>plan.CaptionOverrides?.TryGetValue(id,out var edited)==true?NormalizeCaptions(edited):plan.SourceTranscripts?.TryGetValue(id,out var frozen)==true?frozen:store.TranscriptJson(clips[id].EventId) is{ } json?TranscriptExport.Parse(json).Segments:Array.Empty<TranscriptSegment>();
        return (joined,plan.TimelineCaptions is{ } independent?TimelineCaptionTools.ToSegments(independent):MapSubtitles(items,Source,ownerId,plan.SelectedSegments,plan.Transitions,TransitionFps(plan),identity is null&&!HasCaptionOverride(plan,ownerId)),totalDuration,fps,starts);
    }
    /// <summary>
    /// 按转场把主轨 part 串起来：没有转场的相邻 part 用 concat 过滤器拼接，有转场的剪点用
    /// xfade（视频）+ acrossfade（音频）重叠 EffectiveDuration 秒，最终长度等于 SequenceDuration。
    /// 规格见 docs/transitions-spec.md §5。
    /// </summary>
    public static double PreserveBefore(TransitionCut cut,double fps)=>cut.Align=="start"?0:cut.Align=="end"?cut.EffectiveDuration:Math.Floor(cut.EffectiveDuration*fps/2+1e-8)/fps;
    internal static int AbsoluteVideoEndFrame(TimelineItem item,double fps)
    {
        var start=(int)Math.Round(item.SequenceStart!.Value*fps,MidpointRounding.AwayFromZero);
        return Math.Max(start+1,(int)Math.Round((item.SequenceStart.Value+ItemOutputDuration(item))*fps,MidpointRounding.AwayFromZero));
    }
    internal static int AbsoluteVideoFrames(TimelineItem[] items,double totalDuration,double fps)
        =>Math.Max(Math.Max(1,(int)Math.Round(totalDuration*fps,MidpointRounding.AwayFromZero)),items.Select(x=>AbsoluteVideoEndFrame(x,fps)).DefaultIfEmpty(1).Max());
    internal static string AbsoluteSequenceFilter(TimelineItem[] items,IReadOnlyList<TransitionCut> cuts,double totalDuration,int width,int height,double fps,string pixelFormat)
    {
        var filters=new List<string>();
        var totalFrames=AbsoluteVideoFrames(items,totalDuration,fps);
        filters.Add($"color=c=black:s={width}x{height}:r={N(fps)}:d={N(totalFrames/fps)},settb=AVTB,format={pixelFormat}[absbase]");
        var video="[absbase]";
        // Reverse compositing makes the first array item win, matching native same-track selection.
        for(var i=items.Length-1;i>=0;i--)
        {
            var item=items[i];if(item.Disabled)continue;
            var start=item.SequenceStart!.Value;var end=start+ItemOutputDuration(item);
            var startFrame=(int)Math.Round(start*fps,MidpointRounding.AwayFromZero);var endFrame=AbsoluteVideoEndFrame(item,fps);
            filters.Add($"[{i}:v]fps={N(fps)},settb=AVTB,tpad=stop_mode=clone:stop_duration=1,trim=end_frame={endFrame-startFrame},setpts=round((N/{N(fps)}+{N(startFrame/fps)})/TB),format={pixelFormat}[absclip{i}]");
            filters.Add($"{video}[absclip{i}]overlay=eof_action=pass:repeatlast=0:format=auto:enable='gte(n,{startFrame})*lt(n,{endFrame})'[absv{i}]");video=$"[absv{i}]";
        }
        // Explicit preserve windows replace video only; all source bounds and the hard-cut audio stay intact.
        for(var n=0;n<cuts.Count;n++)
        {
            var cut=cuts[n];var a=Array.FindIndex(items,x=>x.Id==cut.PreviousItemId);var b=Array.FindIndex(items,x=>x.Id==cut.ItemId);
            var before=PreserveBefore(cut,fps);
            // Inputs are already CFR parts. Frame-index trims avoid losing the final source
            // frame when AVTB rounds e.g. 2/60 seconds down to 33333 microseconds.
            var spanFrames=Math.Max(1,(int)Math.Round(cut.EffectiveDuration*fps,MidpointRounding.AwayFromZero));
            var beforeFrames=(int)Math.Round(before*fps,MidpointRounding.AwayFromZero);
            var startFrame=(int)Math.Round(cut.Start*fps,MidpointRounding.AwayFromZero)-beforeFrames;
            var leftFrames=Math.Max(1,(int)Math.Round((items[a].SequenceStart!.Value+ItemOutputDuration(items[a]))*fps,MidpointRounding.AwayFromZero)-Math.Round(items[a].SequenceStart!.Value*fps,MidpointRounding.AwayFromZero));
            var leftOffsetFrames=Math.Max(0,leftFrames-Math.Max(beforeFrames,1));
            filters.Add($"[{a}:v]fps={N(fps)},settb=AVTB,trim=start_frame={leftOffsetFrames},setpts=N/({N(fps)}*TB),tpad=stop_mode=clone:stop={spanFrames},trim=end_frame={spanFrames},format={pixelFormat}[absta{n}]");
            filters.Add($"[{b}:v]fps={N(fps)},settb=AVTB,trim=end_frame={Math.Max(spanFrames-beforeFrames,1)},setpts=N/({N(fps)}*TB),tpad=start_mode=clone:start={beforeFrames},trim=end_frame={spanFrames},format={pixelFormat}[abstb{n}]");
            filters.Add(EditorFx.TransitionMap.Join(cut.Kind,$"[absta{n}]",$"[abstb{n}]",$"[abstm{n}]",0,cut.EffectiveDuration,fps,pixelFormat));
            filters.Add($"[abstm{n}]trim=end_frame={spanFrames},setpts=round((N/{N(fps)}+{N(startFrame/fps)})/TB)[abstw{n}]");
            filters.Add($"{video}[abstw{n}]overlay=eof_action=pass:repeatlast=0:format=auto:enable='gte(n,{startFrame})*lt(n,{startFrame+spanFrames})'[abstout{n}]");video=$"[abstout{n}]";
        }
        for(var i=0;i<items.Length;i++)
            filters.Add($"[{i}:a]apad,atrim=duration={N(ItemOutputDuration(items[i]))},asetpts=PTS-STARTPTS,adelay={(long)Math.Round(items[i].SequenceStart!.Value*48000,MidpointRounding.AwayFromZero)}S:all=1[absa{i}]");
        filters.Add($"{string.Concat(Enumerable.Range(0,items.Length).Select(i=>$"[absa{i}]"))}amix=inputs={items.Length}:duration=longest:dropout_transition=0:normalize=0,apad,asetpts=N/SR/TB,atrim=duration={N(totalDuration)}[aout]");
        filters.Add($"{video}trim=end_frame={totalFrames}[vout]");
        return string.Join(';',filters);
    }
    private async Task<string> JoinAbsoluteSequenceAsync(IReadOnlyList<string> pieces,TimelineItem[] items,IReadOnlyList<TransitionCut> cuts,double totalDuration,int width,int height,double fps,string work,EditorProject plan,CancellationToken token)
    {
        var joined=Path.Combine(work,"joined.mkv");var args=new List<string>{"-hide_banner","-loglevel","error","-y"};
        foreach(var piece in pieces)args.AddRange(["-i",piece]);
        var pixel=cuts.Any(c=>EditorFx.TransitionMap.BlendExpression(c.Kind) is not null)?"gbrp":RequiresHighBitDepth(plan)?"yuv420p10le":"yuv420p";
        args.AddRange(["-filter_complex",AbsoluteSequenceFilter(items,cuts,totalDuration,width,height,fps,pixel),"-map","[vout]","-map","[aout]","-t",N(Math.Max(totalDuration,AbsoluteVideoFrames(items,totalDuration,fps)/fps)),"-r",N(fps),..IntermediateVideoCodecArgs(plan),..IntermediateAudioCodecArgs(),joined]);
        ConfigureIntermediateArgs(args,plan);await media.RunAsync(media.Ffmpeg,args,token);return joined;
    }
    internal static string SerialSequenceFilter(int pieceCount,IReadOnlyList<double> durations,IReadOnlyList<TransitionCut> cuts,IReadOnlyDictionary<string,int> pieceOfItem,double fps,EditorProject plan)
    {
        if(pieceCount==0)throw new InvalidOperationException("主轨没有可转场拼接的片段");
        var cutAfter=new TransitionCut?[pieceCount];
        foreach(var cut in cuts)
        {
            if(!pieceOfItem.TryGetValue(cut.PreviousItemId,out var previous)||!pieceOfItem.TryGetValue(cut.ItemId,out var next))
                throw new InvalidOperationException($"转场两侧片段不在主轨渲染队列中（片段 {cut.ItemId}）");
            if(next!=previous+1)
                throw new InvalidOperationException($"转场两侧片段在渲染队列中不相邻（片段 {cut.ItemId}）");
            if(cutAfter[previous] is not null)
                throw new InvalidOperationException($"同一剪点出现多个转场（片段 {cut.ItemId}）");
            cutAfter[previous]=cut;
        }
        var filters=new List<string>();
        var transitionPixelFormat=cuts.Count==0&&!RequiresHighBitDepth(plan)?"bgra":cuts.Any(cut=>EditorFx.TransitionMap.BlendExpression(cut.Kind) is not null)?"gbrp":"yuv420p";
        for(var i=0;i<pieceCount;i++)
        {
            // 统一帧率/时间基，并补齐到标称时长：-ss/-t 编码出的 part 可能少一帧，xfade 需要稳定时长。
            var length=N(durations[i]);
            filters.Add($"[{i}:v]fps={N(fps)},settb=AVTB,tpad=stop_mode=clone:stop_duration={N(durations[i]+1)},trim=end={length},setpts=PTS-STARTPTS,setsar=1,format={transitionPixelFormat}[vin{i}]");
            filters.Add($"[{i}:a]apad,atrim=end={length},asetpts=PTS-STARTPTS[ain{i}]");
        }
        var video="[vin0]";var audio="[ain0]";var videoLength=durations[0];
        for(var i=1;i<pieceCount;i++)
        {
            var cut=cutAfter[i-1];
            if(cut?.Layout=="preserve")
            {
                var before=PreserveBefore(cut,fps);var after=cut.EffectiveDuration-before;
                filters.Add($"{video}tpad=stop_mode=clone:stop_duration={N(after)},trim=end={N(videoLength+after)}[vp{i}]");
                filters.Add($"[vin{i}]tpad=start_mode=clone:start_duration={N(before)},trim=end={N(durations[i]+before)},setpts=PTS-STARTPTS[vn{i}]");
                var pixel=transitionPixelFormat=="gbrp"?"gbrp":RequiresHighBitDepth(plan)?"yuv420p10le":"yuv420p";
                filters.Add(EditorFx.TransitionMap.Join(cut.Kind,$"[vp{i}]",$"[vn{i}]",$"[vx{i}]",videoLength-before,cut.EffectiveDuration,fps,pixel));
                filters.Add($"{audio}[ain{i}]concat=n=2:v=0:a=1[ax{i}]");
                video=$"[vx{i}]";audio=$"[ax{i}]";
            }
            else if(cut is not null)
            {
                var videoOffset=Math.Max(0,videoLength-cut.EffectiveDuration);
                var dipPixelFormat=transitionPixelFormat=="gbrp"?"gbrp":RequiresHighBitDepth(plan)?"yuv420p10le":"yuv420p";
                filters.Add(EditorFx.TransitionMap.Join(cut.Kind,video,$"[vin{i}]",$"[vx{i}]",videoOffset,cut.EffectiveDuration,fps,dipPixelFormat));
                filters.Add($"{audio}[ain{i}]acrossfade=d={N(cut.EffectiveDuration)}:c1=tri:c2=tri[ax{i}]");
                video=$"[vx{i}]";audio=$"[ax{i}]";
            }
            else
            {
                filters.Add($"{video}[vin{i}]concat=n=2:v=1:a=0[vc{i}]");
                filters.Add($"{audio}[ain{i}]concat=n=2:v=0:a=1[ac{i}]");
                video=$"[vc{i}]";audio=$"[ac{i}]";
            }
            videoLength+=durations[i]-(cut is {Layout:"overlap"}?cut.EffectiveDuration:0);
        }
        filters.Add($"{video}null[vout]");filters.Add($"{audio}apad[aout]");
        return string.Join(';',filters);
    }
    private async Task<string> JoinWithTransitionsAsync(IReadOnlyList<string> pieces,IReadOnlyList<double> durations,IReadOnlyList<TransitionCut> cuts,IReadOnlyDictionary<string,int> pieceOfItem,double totalDuration,double fps,string work,EditorProject plan,CancellationToken token)
    {
        var joined=Path.Combine(work,"joined.mkv");
        var args=new List<string>{"-hide_banner","-loglevel","error","-y"};
        foreach(var piece in pieces)args.AddRange(["-i",piece]);
        var filter=SerialSequenceFilter(pieces.Count,durations,cuts,pieceOfItem,fps,plan);
        args.AddRange(["-filter_complex",filter,"-map","[vout]","-map","[aout]","-t",N(totalDuration),"-r",N(fps),..IntermediateVideoCodecArgs(plan),..IntermediateAudioCodecArgs(),"-movflags","+faststart",joined]);
        ConfigureIntermediateArgs(args,plan);
        await media.RunAsync(media.Ffmpeg,args,token);
        return joined;
    }
    private async Task<string> ComposeOverlaysAsync(string basePath,TimelineItem[] allItems,Dictionary<string,Clip> clips,int width,int height,double fps,double totalDuration,bool baseAudioMuted,int[] mutedTracks,int[] soloedTracks,int[] hiddenTracks,double[] trackOpacities,string work,EditorProject plan,CancellationToken token,Action<double>? progress,IReadOnlyList<TransitionCut>? cuts=null,IReadOnlyDictionary<string,double>? starts=null)
    {
        var overlays=new List<(TimelineItem Item,double Start,double End,string Path)>();
        var graphics=new List<(TimelineItem Item,double Start,double End)>();
        foreach(var track in new[]{2,3})
        {
            if(hiddenTracks.Contains(track))continue;
            var cursor=0d;
            foreach(var item in allItems.Where(x=>TrackOf(x)==track))
            {
                token.ThrowIfCancellationRequested();
                cursor+=Math.Max(0,item.GapBefore);
                var length=(item.End-item.Start)/EffectiveSpeed(item);
                // 有转场时叠加层/贴纸/标题的位置用压缩后的序列起点（规格 §4/§5(5)）。
                var itemStart=starts is not null&&starts.TryGetValue(item.Id,out var compressed)?compressed:cursor;
                if(item.Disabled){cursor+=length;continue;}
                if(EditorProjectGraphics.IsSourceFree(item))
                {
                    graphics.Add((item,itemStart,itemStart+length));cursor+=length;continue;
                }
                if(!clips.TryGetValue(item.ClipId,out var source)||!File.Exists(source.Path))throw new InvalidOperationException("叠加轨源文件已移动或不存在");
                var path=Path.Combine(work,$"overlay-{overlays.Count:000}.mkv");
                await RenderOverlayPartAsync(source,item,path,width,height,fps,plan,token);
                overlays.Add((item,itemStart,itemStart+length,path));
                cursor+=length;
                progress?.Invoke(.95);
            }
        }
        var audioTracks=new List<(TimelineItem Item,double Start,double End,string Path)>();
        foreach(var track in new[]{4,5,6})
        {
            if(!IncludeTrackAudio(track,mutedTracks,soloedTracks))continue;
            var cursor=0d;
            foreach(var item in allItems.Where(x=>TrackOf(x)==track))
            {
                token.ThrowIfCancellationRequested();
                cursor+=Math.Max(0,item.GapBefore);
                var length=(item.End-item.Start)/EffectiveSpeed(item);
                var itemStart=starts is not null&&starts.TryGetValue(item.Id,out var compressed)?compressed:cursor;
                if(item.Disabled){cursor+=length;continue;}
                if(!clips.TryGetValue(item.ClipId,out var source)||!File.Exists(source.Path))throw new InvalidOperationException("音频轨源文件已移动或不存在");
                var path=Path.Combine(work,$"audio-{audioTracks.Count:000}.mka");
                await RenderAudioPartAsync(source,item,path,token);
                audioTracks.Add((item,itemStart,itemStart+length,path));
                cursor+=length;
                progress?.Invoke(.95);
            }
        }
        var composed=Path.Combine(work,"composed.mkv");
        var args=new List<string>{"-hide_banner","-loglevel","error","-y","-i",basePath};
        foreach(var overlay in overlays)args.AddRange(["-i",overlay.Path]);
        foreach(var audioTrack in audioTracks)args.AddRange(["-i",audioTrack.Path]);
        var filters=new List<string>();
        var baseItems=BaseItems(allItems);
        var baseLength=baseItems.Length==0?totalDuration:TrackDuration(baseItems,cuts??[],1);
        var pad=Math.Max(0,totalDuration-baseLength);
        var video="[0:v]";var audio="[0:a]";
        if(hiddenTracks.Contains(1))
        {
            filters.Add("[0:v]drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill[basehidden]");
            video="[basehidden]";
        }
        if(!IncludeBaseAudio(baseAudioMuted,mutedTracks,soloedTracks))
        {
            filters.Add("[0:a]volume=0[basemuted]");
            audio="[basemuted]";
        }
        if(pad>.001)
        {
            filters.Add($"[0:v]tpad=stop_mode=add:stop_duration={N(pad)}:color=black[basev]");
            filters.Add($"{audio}apad=pad_dur={N(pad)}[basea]");
            video="[basev]";audio="[basea]";
        }
        var mixes=new List<string>{audio};
        for(var index=0;index<overlays.Count;index++)
        {
            var input=index+1;var overlay=overlays[index];
            var overlayFilter=$"[{input}:v]setpts=PTS-STARTPTS+{N(overlay.Start)}/TB";
            var overlayOpacity=Math.Clamp(overlay.Item.Opacity*TrackOpacity(trackOpacities,TrackOf(overlay.Item)),0,1);
            if(overlayOpacity<.999)overlayFilter+=$",format=rgba,colorchannelmixer=aa={N(overlayOpacity)}";
            filters.Add($"{overlayFilter}[overlayv{index}]");
            var overlayX=(int)Math.Round(overlay.Item.PositionX*width);
            var overlayY=(int)Math.Round(overlay.Item.PositionY*height);
            filters.Add($"{video}[overlayv{index}]overlay=x={overlayX}:y={overlayY}:eof_action=pass[video{index}]");
            video=$"[video{index}]";
            var overlayTrack=TrackOf(overlay.Item);
            if(!overlay.Item.AudioDetached&&IncludeTrackAudio(overlayTrack,mutedTracks,soloedTracks))
            {
                var gain=Math.Pow(10,Math.Clamp(overlay.Item.VolumeDb,-60,12)/20);
                var gainFilter=Math.Abs(gain-1)>.001?$"volume={N(gain)},":"";
                filters.Add($"[{input}:a]{gainFilter}adelay={(int)Math.Round(overlay.Start*1000)}:all=1[overlaya{index}]");
                mixes.Add($"[overlaya{index}]");
            }
        }
        for(var index=0;index<audioTracks.Count;index++)
        {
            var input=overlays.Count+index+1;var audioTrack=audioTracks[index];
            filters.Add($"[{input}:a]adelay={(int)Math.Round(audioTrack.Start*1000)}:all=1[trackaudio{index}]");
            mixes.Add($"[trackaudio{index}]");
        }
        // Graphics draw into the joined frame, preserving underlying pixels and avoiding opaque intermediate clips.
        for(var index=0;index<graphics.Count;index++)
        {
            var graphic=graphics[index];
            var opacity=graphic.Item.Opacity*TrackOpacity(trackOpacities,3);
            // drawbox fill / drawtext 的 @alpha 只在 RGB 族像素格式里生效：直接画在 yuv420p 上时 0.5 的不透明度实测约 88% 不透明
            // （yuv 没有 alpha 分量，fill 只混合了色度）。图形链先转 rgba 绘制，再转回 yuv420p 交给后续叠加与编码，
            // rgba 往返带来的色度误差只有 1~2 级，远小于测试的像素容差。
            filters.Add($"{video}format=rgba,{EditorProjectGraphics.Build(graphic.Item,width,height,graphic.Start,graphic.End,opacity)},format=yuv420p[graphic{index}]");
            video=$"[graphic{index}]";
        }
        filters.Add(mixes.Count==1?$"{audio}anull[aout]":$"{string.Concat(mixes)}amix=inputs={mixes.Count}:duration=longest:dropout_transition=0:normalize=0[aout]");
        // 只有音频轨（没有 V2/V3 叠加、隐藏、补长）时 video 仍是 "[0:v]" 这个输入标签，
        // 不能作为 -filter_complex 的输出标签使用，直接 map 输入视频流。
        var videoMap=video=="[0:v]"?"0:v":video;
        args.AddRange(["-filter_complex",string.Join(';',filters),"-map",videoMap,"-map","[aout]","-t",N(totalDuration),..IntermediateVideoCodecArgs(plan),..IntermediateAudioCodecArgs(),composed]);
        ConfigureIntermediateArgs(args,plan);
        await media.RunAsync(media.Ffmpeg,args,token);
        return composed;
    }
    private async Task RenderOverlayPartAsync(Clip source,TimelineItem item,string path,int width,int height,double fps,EditorProject plan,CancellationToken token)
    {
        var duration=await media.DurationAsync(source.Path,token);if(item.End>duration+.05)throw new ArgumentException($"叠加片段结束时间超过源视频长度 {duration:0.0} 秒");
        var sourceInfo=await media.ProbeAsync(source.Path,token);var sourceLength=item.End-item.Start;var speed=EffectiveSpeed(item);var length=sourceLength/speed;
        var args=new List<string>{"-hide_banner","-loglevel","error","-y","-t",N(item.Start+sourceLength),"-i",source.Path};
        var audioInput=0;
        if(!sourceInfo.HasAudio){args.AddRange(["-f","lavfi","-i","anullsrc=r=48000:cl=stereo"]);audioInput=1;}
        var trimmedVisual=$"trim=start={N(item.Start)}:duration={N(sourceLength)},setpts=PTS-STARTPTS,"+VisualFilter(item,width,height,fps,RequiresHighBitDepth(plan));
        args.AddRange(["-map","0:v:0","-map",$"{audioInput}:a:0","-vf",trimmedVisual,"-t",N(length),..IntermediateVideoCodecArgs(plan),..IntermediateAudioCodecArgs(),path]);
        var audioFilter=TimelineAudio.TrimFilter(sourceInfo.HasAudio?item.Start:0,sourceLength);
        var effects=AudioFilter(item);if(effects.Length>0)audioFilter+=","+effects;
        args.InsertRange(args.IndexOf("-c:a"),["-af",audioFilter]);
        ConfigureIntermediateArgs(args,plan);
        await media.RunAsync(media.Ffmpeg,args,token);
    }
    private async Task RenderAudioPartAsync(Clip source,TimelineItem item,string path,CancellationToken token)
    {
        var duration=await media.DurationAsync(source.Path,token);if(item.End>duration+.05)throw new ArgumentException($"音频片段结束时间超过源视频长度 {duration:0.0} 秒");
        var sourceInfo=await media.ProbeAsync(source.Path,token);
        if(!sourceInfo.HasAudio)throw new InvalidOperationException("音频轨来源没有音轨");
        var sourceLength=item.End-item.Start;var speed=EffectiveSpeed(item);var length=sourceLength/speed;
        List<string> args=["-hide_banner","-loglevel","error","-y","-t",N(item.Start+sourceLength),"-i",source.Path,"-map","0:a:0","-vn",..IntermediateAudioCodecArgs(),"-t",N(length),path];
        var audioFilter=TimelineAudio.TrimFilter(item.Start,sourceLength);
        var effects=AudioFilter(item);if(effects.Length>0)audioFilter+=","+effects;
        args.InsertRange(args.IndexOf("-c:a"),["-af",audioFilter]);
        await media.RunAsync(media.Ffmpeg,args,token);
    }
    private static string EscapeDrawText(string text)=>text.Replace("\\","\\\\").Replace("'","\\'").Replace(":","\\:").Replace("%","\\%").Replace("\r"," ").Replace("\n"," ");
    /// <summary>
    /// 片段输出时间轴上的 opacity 增益表达式，供 ffmpeg blend 的 all_expr 逐帧求值（T 为输出相对秒）。
    /// 无 opacity 关键帧时返回常量（item.Opacity × multiplier）；有关键帧时按归一化后的关键帧做线性插值并在首尾保持，
    /// 与前端 project.ts 的 paramValueAt 完全一致。multiplier 用于叠加轨道不透明度（TrackOpacities）。
    /// 返回 "1" 表示无需合成（调用方保持旧滤镜链）。
    /// </summary>
    public static string OpacityExpression(TimelineItem item,double multiplier=1,string timeVariable="T")
    {
        multiplier=double.IsFinite(multiplier)?Math.Clamp(multiplier,0,1):1;
        var points=ParamKeyframesFor(item,"opacity");
        if(points.Length==0)
        {
            var value=Math.Clamp(item.Opacity*multiplier,0,1);
            return value>=.999?"1":N(value);
        }
        var expr=N(points[^1].Value);
        for(var i=points.Length-2;i>=0;i--)
        {
            var t0=N(points[i].Time);var t1=N(points[i+1].Time);
            var v0=N(points[i].Value);var v1=N(points[i+1].Value);
            var linear=$"({v0}+((({v1})-({v0}))/({t1}-{t0}))*({timeVariable}-{t0}))";
            expr=$"if(lt({timeVariable},{t1}),{linear},{expr})";
        }
        expr=$"if(lt({timeVariable},{N(points[0].Time)}),{N(points[0].Value)},{expr})";
        if(multiplier<.999)expr=$"{N(multiplier)}*({expr})";
        return expr;
    }
    /// <summary>
    /// 单个静态蒙版的 geq 亮度表达式：255 表示该像素被蒙版挡掉（变黑）、0 表示保留画面。
    /// rect 用旋转后的半宽/半高判定，ellipse 用旋转后的椭圆单位式判定；feather 由调用方追加 gblur。
    /// </summary>
    public static string MaskMatteExpression(MaskInstance mask,int width,int height)
    {
        var opacity=Math.Clamp(mask.Opacity,0,1);
        if(mask.Type=="polygon")
        {
            var points=mask.Points;
            // 无有效顶点：不挡画面（全部保留）。
            if(points is not {Length:>=3})return "0";
            // 奇偶规则（even-odd）等价于 |winding| 的奇偶，这里用 winding 求和是否为 0 判定内部。
            var winding=new System.Text.StringBuilder("0");
            for(var i=0;i<points.Length;i++)
            {
                var a=points[i];var b=points[(i+1)%points.Length];
                var ax=N(Math.Clamp(a.X,0,1)*width);var ay=N(Math.Clamp(a.Y,0,1)*height);
                var bx=N(Math.Clamp(b.X,0,1)*width);var by=N(Math.Clamp(b.Y,0,1)*height);
                var isLeft=$"(({bx}-{ax})*(Y-{ay})-(X-{ax})*({by}-{ay}))";
                var up=$"lte({ay},Y)*gt({by},Y)*gt({isLeft},0)";
                var down=$"lte({by},Y)*gt({ay},Y)*lt({isLeft},0)";
                winding.Append($"+({up})-({down})");
            }
            var inside=$"gt(abs({winding}),0.5)";
            return $"255*{N(opacity)}*(1-{inside})";
        }
        var x=Math.Clamp(mask.X,-.5,.5);var y=Math.Clamp(mask.Y,-.5,.5);
        var w=Math.Clamp(mask.W,.01,2);var h=Math.Clamp(mask.H,.01,2);
        var rotation=Math.Clamp(mask.Rotation,-180,180);
        var halfWidth=N(Math.Max(.5,w*width/2));var halfHeight=N(Math.Max(.5,h*height/2));
        var radians=rotation*Math.PI/180;var cos=N(Math.Cos(radians));var sin=N(Math.Sin(radians));
        var dx=$"(X-{N((.5+x)*width)})";var dy=$"(Y-{N((.5+y)*height)})";
        var xr=$"({dx}*{cos}+{dy}*{sin})";var yr=$"(-{dx}*{sin}+{dy}*{cos})";
        var insideRectOrEllipse=mask.Type=="ellipse"?$"lte(pow({xr}/{halfWidth},2)+pow({yr}/{halfHeight},2),1)":$"lt(abs({xr}),{halfWidth})*lt(abs({yr}),{halfHeight})";
        return $"255*{N(opacity)}*(1-{insideRectOrEllipse})";
    }
    /// <summary>
    /// 把片段上启用的静态蒙版合成为一张灰度 matte PNG（外部白=抠黑、内部黑=保留），返回文件路径；
    /// 没有启用蒙版时返回 null。多个蒙版按 visible=并集处理：matte 取各蒙版外部值的最小值（blend darken），
    /// 即只要落在任一蒙版内就保留画面。matte 只依赖片段与输出尺寸，不随帧变化。
    /// </summary>
    private async Task<string?> RenderMaskMatteAsync(TimelineItem item,string work,int index,int width,int height,CancellationToken token)
    {
        var masks=NormalizeMasks(item);var enabled=masks?.Where(x=>x.Enabled).ToArray();
        if(enabled is not {Length:>0})return null;
        var path=Path.Combine(work,$"mask-{index:000}.png");
        var graph=new StringBuilder();
        if(enabled.Length==1)
        {
            graph.Append("[0:v]format=gray,geq=lum='").Append(MaskMatteExpression(enabled[0],width,height)).Append('\'');
            if(enabled[0].Feather>0)graph.Append($",gblur=sigma={N(Math.Clamp(enabled[0].Feather,0,100))}");
            graph.Append(",format=gray[matte]");
        }
        else
        {
            graph.Append("[0:v]format=gray,split=").Append(enabled.Length.ToString(CultureInfo.InvariantCulture));
            for(var i=0;i<enabled.Length;i++)graph.Append($"[sm{i}]");
            for(var i=0;i<enabled.Length;i++)
            {
                graph.Append($";[sm{i}]geq=lum='{MaskMatteExpression(enabled[i],width,height)}'");
                if(enabled[i].Feather>0)graph.Append($",gblur=sigma={N(Math.Clamp(enabled[i].Feather,0,100))}");
                graph.Append($"[mk{i}]");
            }
            var current="[mk0]";
            for(var i=1;i<enabled.Length;i++)
            {
                graph.Append($";{current}[mk{i}]blend=all_mode=darken[mb{i}]");
                current=$"[mb{i}]";
            }
            graph.Append($";{current}format=gray[matte]");
        }
        var args=new List<string>{"-hide_banner","-loglevel","error","-y","-f","lavfi","-i",$"color=c=black:s={width}x{height}:d=1","-filter_complex",graph.ToString(),"-map","[matte]","-frames:v","1","-update","1",path};
        await media.RunAsync(media.Ffmpeg,args,token);
        return File.Exists(path)?path:null;
    }
    internal static string HighBitDepthEq(double brightness,double contrast,double saturation)=>
        $"geq=lum='clip((lum(X,Y)-512)*{N(contrast)}+512+{N(brightness)}*1023,0,1023)':cb='clip((cb(X,Y)-512)*{N(saturation)}+512,0,1023)':cr='clip((cr(X,Y)-512)*{N(saturation)}+512,0,1023)'";
    internal static string HighBitDepthZoom(string zoom)
    {
        // Sample a fixed-size frame: crop caches input dimensions and cannot center a per-frame scale.
        var z=$"max(1,({zoom}))";
        return $"geq=lum='lum((X-W/2)/{z}+W/2,(Y-H/2)/{z}+H/2)':cb='cb((X-W/2)/{z}+W/2,(Y-H/2)/{z}+H/2)':cr='cr((X-W/2)/{z}+W/2,(Y-H/2)/{z}+H/2)'";
    }
    public static string VisualFilter(TimelineItem item,int width,int height,double fps,bool highBitDepth=false)
    {
        var filters=new List<string>{$"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={N(fps)}"};
        if(highBitDepth)filters.Insert(0,"format=yuv420p10le");
        var track=TrackOf(item);
        var speed=EffectiveSpeed(item);
        var hasScale=track==1&&HasParamKeyframes(item,"scale");
        var hasRotation=track==1&&HasParamKeyframes(item,"rotation");
        var hasPositionX=track==1&&HasParamKeyframes(item,"positionX");
        var hasPositionY=track==1&&HasParamKeyframes(item,"positionY");
        var hasPosition=hasPositionX||hasPositionY;
        var hasTransform=hasScale||hasRotation||hasPosition;
        if(!hasTransform&&item.Motion=="push")filters.Add(highBitDepth
            ?HighBitDepthZoom($"1+{N(Math.Max(.12,item.Zoom-1))}*min(N/{N(Math.Max(1,(item.End-item.Start)*fps))},1)")
            :$"zoompan=z='1+{N(Math.Max(.12,item.Zoom-1))}*min(on/{N(Math.Max(1,(item.End-item.Start)*fps))},1)':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s={width}x{height}:fps={N(fps)}");
        else if(!hasTransform&&item.Motion=="shake")filters.Add($"crop=iw/1.12:ih/1.12:(iw-ow)/2+4*sin(t*35):(ih-oh)/2+4*cos(t*31),scale={width}:{height},setsar=1");
        else if(!hasScale&&item.Zoom>1)filters.Add($"crop=iw/{N(item.Zoom)}:ih/{N(item.Zoom)},scale={width}:{height},setsar=1");
        if(hasScale)
        {
            // 缩放关键帧：zoompan 的 z 表达式按帧求值（on 为输出帧号，÷(fps×speed) 换算成片段输出相对秒），
            // 输出尺寸固定为 width×height，时间基与 base 链的 fps 一致。
            var curve=ParamKeyframeExpression(item,"scale",highBitDepth?"N":"on",fps,speed);
            filters.Add(highBitDepth?HighBitDepthZoom(curve)
                :$"zoompan=z='{curve}':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s={width}x{height}:fps={N(fps)}");
        }
        if(hasPosition)
        {
            // 位置关键帧：先 pad 出双倍画布，再用按帧求值的 crop x/y 平移（黑边语义与静态 pad/crop 一致）。
            var px=hasPositionX?ParamKeyframeExpression(item,"positionX","n",fps,speed):N(item.PositionX);
            var py=hasPositionY?ParamKeyframeExpression(item,"positionY","n",fps,speed):N(item.PositionY);
            filters.Add($"pad={width*2}:{height*2}:{width/2}:{height/2}");
            filters.Add($"crop={width}:{height}:x='{width/2}-({px})*{width}':y='{height/2}-({py})*{height}'");
        }
        else if(track==1&&(Math.Abs(item.PositionX)>.001||Math.Abs(item.PositionY)>.001))
        {
            var dx=(int)Math.Round(item.PositionX*width);
            var dy=(int)Math.Round(item.PositionY*height);
            if(dx!=0)
                filters.Add(dx>0
                    ? $"pad={width+dx}:{height}:{dx}:0,crop={width}:{height}:0:0"
                    : $"crop={width+dx}:{height}:{-dx}:0,pad={width}:{height}:0:0");
            if(dy!=0)
                filters.Add(dy>0
                    ? $"pad={width}:{height+dy}:0:{dy},crop={width}:{height}:0:0"
                    : $"crop={width}:{height+dy}:0:{-dy},pad={width}:{height}:0:0");
        }
        var filter=item.Filter switch {"mono"=>"hue=s=0","warm"=>"colorbalance=rs=.08:bs=-.05","cool"=>"colorbalance=bs=.08:rs=-.04","vivid"=>highBitDepth?HighBitDepthEq(0,1.08,1.3):"eq=saturation=1.3:contrast=1.08",_=>null};if(filter is not null)filters.Add(filter);
        // eq and zoompan negotiate only 8-bit; high-depth renders use geq arithmetic/resampling.
        if(highBitDepth&&item.Grade is {} g)
        {
            if(Math.Abs(g.Brightness)>.001||Math.Abs(g.Contrast-1)>.001||Math.Abs(g.Saturation-1)>.001)
                filters.Add("format=yuv420p10le,"+HighBitDepthEq(Math.Clamp(g.Brightness,-1,1),Math.Clamp(g.Contrast,0,3),Math.Clamp(g.Saturation,0,3)));
            if(Math.Abs(g.Temperature)>.001)
            {
                var t=Math.Clamp(g.Temperature,-1,1)*.12;
                filters.Add($"colorbalance=rs={N(t)}:bs={N(-t)}");
            }
        }
        else if(GradeFilter(item.Grade) is{} grade)filters.Add(grade);
        if(hasRotation)
        {
            // 旋转关键帧：rotate 的 angle 表达式按帧求值，旋转中心与输出尺寸保持与静态路径一致。
            var curve=ParamKeyframeExpression(item,"rotation","n",fps,speed);
            filters.Add($"rotate='({curve})*PI/180':ow=iw:oh=ih:c=black");
        }
        else if(Math.Abs(item.Rotation)>.001)filters.Add($"rotate={N(item.Rotation*Math.PI/180)}:ow=iw:oh=ih:c=black");
        if(TrackOf(item)==3&&item.GraphicKind is not null)
            filters.Add(EditorProjectGraphics.Build(item,width,height,0,ItemOutputDuration(item)));
        else if(TrackOf(item)==3&&!string.IsNullOrWhiteSpace(item.TitleText))
        {
            var titleSize=Math.Clamp((int)Math.Round(item.TitleSize*height/720.0),12,200);
            filters.Add($"drawtext=fontfile='{EditorProjectGraphics.TitleFontFilterPath(item.TitleFont)}':text='{EscapeDrawText(item.TitleText.Trim())}':fontcolor={item.TitleColor??"#FFFFFF"}:fontsize={titleSize}:x=w*{N(item.TitleX)}-text_w/2:y=h*{N(item.TitleY)}-text_h/2:shadowcolor=black@0.65:shadowx=2:shadowy=2");
        }
        // V1 的 opacity 不在这里处理：colorchannelmixer 的 alpha 转回 yuv420p 时会被丢弃（且不支持逐帧求值），
        // 改由 part 级 blend 滤镜图按输出时间轴合成到黑底上，见 OpacityExpression / AssembleAsync。
        if(Math.Abs(speed-1)>.0001)filters.Add($"setpts=(PTS-STARTPTS)/{N(speed)}");
        var length=ItemOutputDuration(item);
        var alpha=TrackOf(item)>1?":alpha=1":"";
        var fadeIn=Math.Clamp(item.FadeIn,0,length);
        var fadeOut=Math.Clamp(item.FadeOut,0,length);
        if(fadeIn>0)filters.Add($"fade=t=in:st=0:d={N(fadeIn)}{alpha}");
        if(fadeOut>0)filters.Add($"fade=t=out:st={N(Math.Max(0,length-fadeOut))}:d={N(fadeOut)}{alpha}");
        if(item.CatalogFx is not null)
        {
            if(item.CatalogFxVersion!=1)throw new ArgumentException("catalog-fx-invalid-version");
            var catalogFx=LiveClipper.Core.EditorFx.VideoFxChain.Build(item.CatalogFx);
            if(TrackOf(item)>1&&item.CatalogFx.Any(effect=>effect.Enabled&&(effect.Id is "transform" or "rotate_3d" or "rounded_crop" or "mosaic" or "four_color_gradient" or "rgb_split" or "clone" or "lens_flare" or "circle" or "grid" or "checkerboard" or "ellipse" or "paint_bucket" or "alpha_adjust" or "track_matte" or "color_key" or "luma_key" or "logo_cutout" or "mirror" or "lens_distortion" or "twirl" or "wave_warp" or "spherize" or "warp_stabilizer" or "spacer" or "magnify" or "ultra_key" or "drop_shadow" or "long_shadow" or "grow" or "shrink" or "move" or "noise" or "basic_3d" or "turbulent_displace" or "spin" or "camera_shake" or "wiggle" or "wonder_glow" or "stroke"||effect.Id=="channel_blur"&&effect.Params.GetValueOrDefault("alpha",0)>0)))
                throw new ArgumentException("catalog-fx-alpha-overlay-unsupported");
            if(catalogFx.Length>0)filters.Add(catalogFx);
            // Matte only after the full FX chain; per-effect matting destroys later wrapped samples.
            if(TrackOf(item)==1&&item.CatalogFx.Any(effect=>effect.Enabled&&(effect.Id is "transform" or "offset" or "rotate_3d" or "rounded_crop" or "mosaic" or "four_color_gradient" or "rgb_split" or "clone" or "lens_flare" or "circle" or "grid" or "checkerboard" or "ellipse" or "paint_bucket" or "alpha_adjust" or "track_matte" or "color_key" or "luma_key" or "logo_cutout" or "mirror" or "lens_distortion" or "twirl" or "wave_warp" or "spherize" or "warp_stabilizer" or "spacer" or "magnify" or "ultra_key" or "drop_shadow" or "long_shadow" or "grow" or "shrink" or "move" or "noise" or "basic_3d" or "turbulent_displace" or "spin" or "camera_shake" or "wiggle" or "wonder_glow" or "stroke"||effect.Id=="channel_blur"&&effect.Params.GetValueOrDefault("alpha",0)>0)))
                filters.Add("premultiply=inplace=1,format=rgb24");
        }
        return string.Join(',',filters);
    }
    public async Task RenderAsync(Clip clip,EditorProject plan,string output,string work,CancellationToken token,Action<double>? progress=null)
        =>await RenderCoreAsync(clip.Id,null,plan,output,work,token,progress);
    public async Task RenderAsync(EditorProjectIdentity identity,EditorProject plan,string output,string work,CancellationToken token,Action<double>? progress=null)
    {
        ValidateIndependentRender(identity,plan);
        await RenderCoreAsync(identity.Id,identity,plan,output,work,token,progress);
    }
    private async Task RenderCoreAsync(string ownerId,EditorProjectIdentity? identity,EditorProject plan,string output,string work,CancellationToken token,Action<double>? progress)
    {
        plan=FlattenProject(plan);
        var assembled=await AssembleCoreAsync(ownerId,identity,plan,work,token,progress);
        await FinishAsync(ownerId,plan,assembled.Video,assembled.Subtitles,assembled.Length,assembled.Fps,assembled.Starts,output,work,token);
    }
    private async Task FinishAsync(string ownerId,EditorProject plan,string video,IReadOnlyList<TranscriptSegment> subtitles,double length,double fps,IReadOnlyDictionary<string,double>? starts,string output,string work,CancellationToken token)
    {
            // cuts/length/starts 统一由 AssembleAsync 用同一组参数计算（含 clipDurations 的 start 对齐 clamp），
            // 避免 Finish 阶段用不同 cuts 再算一次 -t 造成两处时长不一致。
            var projectItems=Items(plan,ownerId);
            var absoluteItems=BaseItems(projectItems);var absoluteVideo=absoluteItems.Any(x=>x.SequenceStart is not null);
            var outputLength=absoluteVideo?Math.Max(length,AbsoluteVideoFrames(absoluteItems,length,fps)/fps):length;
            var args=new List<string>{"-hide_banner","-loglevel","error","-y","-i",video};var next=1;int? musicInput=null,effectInput=null;
            string? videoFilter=null;
            if(plan.BurnSubtitles)
            {
                if(subtitles.Count==0)throw new InvalidOperationException("时间线中没有可烧录的字幕");
                if(plan.TimelineCaptions is{ } independentCaptions)
                {
                    // subtitles 滤镜直接作用于 assemble 输出 video，用它的真实宽高（ffprobe）做 ASS 参考换算；探测失败直接抛错。
                    var burnInfo=await media.ProbeAsync(video,token);
                    var ass=Path.Combine(work,"timeline.ass");await File.WriteAllTextAsync(ass,TimelineCaptionTools.ToAss(independentCaptions,burnInfo.Width,burnInfo.Height,fps),new UTF8Encoding(false),token);
                    videoFilter=IndependentCaptionFilter(ass);
                }
                else {
                var srt=Path.Combine(work,"timeline.srt");await File.WriteAllTextAsync(srt,SubtitleTools.ToSrt(subtitles),new UTF8Encoding(false),token);
                var color="&H00"+plan.SubtitleColor[5..7]+plan.SubtitleColor[3..5]+plan.SubtitleColor[1..3];
                var alignment=plan.SubtitlePosition switch {"top"=>8,"middle"=>5,_=>2};
                var font=System.Text.RegularExpressions.Regex.Replace(plan.SubtitleFont,@"[^\p{L}\p{N} _-]","");
                videoFilter=$"subtitles=filename='{FilterPath(srt)}':force_style='FontName={font},FontSize={plan.SubtitleSize},PrimaryColour={color},Outline={N(plan.SubtitleOutline)},Alignment={alignment},MarginV={plan.SubtitleMargin}'";
                }
            }
            // R81：新增格式按 ExportKind 分派。spec 为 null 时逐字节保持旧的 4 格式行为
            // （h264/hevc/prores/dnxhr 的滤镜、map、编码与封装参数完全不变）。
            var spec=EditorExportFormats.Find(plan.Format);
            var kind=spec?.Kind??ExportKind.VideoFile;
            var wantsAudio=spec is null||spec.HasAudio;
            var filters=new List<string>();var mix=new List<string>();
            if(wantsAudio)
            {
                var assets=studio.Assets();
            AudioAsset? Find(string? id,string kind){if(string.IsNullOrWhiteSpace(id))return null;var a=assets.FirstOrDefault(x=>x.Id==id&&x.Kind==kind);return a is not null&&File.Exists(a.Path)?a:throw new InvalidOperationException("找不到选择的音频素材");}
            var music=Find(plan.MusicAssetId,"music");var effect=Find(plan.EffectAssetId,"effect");
            if(music is not null){musicInput=next++;args.AddRange(["-stream_loop","-1","-i",music.Path]);}if(effect is not null){effectInput=next++;args.AddRange(["-i",effect.Path]);}
                filters.Add($"[0:a]volume={N(plan.VoiceVolume)}[voice]");mix.Add("[voice]");
                if(musicInput is not null)
                {
                    filters.Add($"[{musicInput}:a]volume={N(plan.MusicVolume)},atrim=duration={N(length)},afade=t=out:st={N(Math.Max(0,length-1))}:d=1[bgm]");
                    if(plan.DuckMusic){filters.Add("[voice]asplit=2[voice_mix][duck_key]");mix[0]="[voice_mix]";filters.Add("[bgm][duck_key]sidechaincompress=threshold=0.04:ratio=6:attack=20:release=250[ducked]");mix.Add("[ducked]");}else mix.Add("[bgm]");
                }
                if(effectInput is not null){filters.Add($"[{effectInput}:a]volume={N(plan.EffectVolume)},adelay={(int)Math.Round(plan.EffectAt*1000)}:all=1[sfx]");mix.Add("[sfx]");}
                var offset=0d;var soundIndex=0;
                foreach(var item in BaseItems(projectItems))
                {
                    if(starts is not null&&starts.TryGetValue(item.Id,out var itemStart))offset=itemStart;else offset+=item.GapBefore;
                    if(Find(item.SoundAssetId,"effect") is {} sound)
                    {
                        args.AddRange(["-i",sound.Path]);var label=$"clip_sfx{soundIndex++}";filters.Add($"[{next++}:a]volume={N(plan.EffectVolume)},adelay={(int)Math.Round(offset*1000)}:all=1[{label}]");mix.Add($"[{label}]");
                    }
                    if(starts is null)offset+=(item.End-item.Start)/EffectiveSpeed(item);
                }
                filters.Add(mix.Count==1?"[voice]anull[aout]":string.Concat(mix)+$"amix=inputs={mix.Count}:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95:latency=1[aout]");
                filters.Add($"[aout]volume={N(plan.MasterVolume)}[aout_master]");
            }
            if(absoluteVideo&&wantsAudio)
            {
                filters[^1]=filters[^1].Replace("[aout_master]",$",asetpts=N/SR/TB,atrim=duration={N(length)}[aout_master]");
            }
            var audioGraph=string.Join(';',filters);
            var target=output;string? frames=null;
            if(kind==ExportKind.AudioOnly)
            {
                // 纯音频：视频流只用于时间轴对齐（-vn 丢弃），时长取合并后的成片时长。
                args.AddRange(["-filter_complex",audioGraph,"-map","[aout_master]","-vn","-t",N(length)]);
                args.AddRange(spec!.AudioArgs);args.AddRange(spec.MuxArgs);
            }
            else if(kind==ExportKind.ImageSequence)
            {
                // 图像序列：先编码到临时帧目录，成功后再打包 zip 作为成片。
                frames=Path.Combine(work,"frames");Directory.CreateDirectory(frames);
                target=Path.Combine(frames,"frame%06d"+spec!.FrameExtension);
                if(videoFilter is not null)args.AddRange(["-vf",videoFilter]);
                args.AddRange(["-map","0:v:0","-an","-t",N(outputLength),"-r",N(fps)]);
                args.AddRange(spec.VideoArgs);
            }
            else if(kind==ExportKind.AnimatedGif)
            {
                // 动画 GIF：palettegen/paletteuse 两遍调色板；无音轨，帧率上限 15 控制体积。
                var prefix=videoFilter is null?string.Empty:videoFilter+",";
                args.AddRange(["-filter_complex",$"[0:v]{prefix}split[gifA][gifB];[gifA]palettegen=stats_mode=diff[gifPal];[gifB][gifPal]paletteuse=dither=bayer:bayer_scale=5[gifOut]","-map","[gifOut]","-an","-t",N(outputLength),"-r",N(Math.Min(fps,15)),"-fps_mode","cfr"]);
            }
            else
            {
                if(videoFilter is not null)args.AddRange(["-vf",videoFilter]);
                args.AddRange(["-filter_complex",audioGraph,"-map","0:v:0","-map","[aout_master]","-t",N(outputLength),"-r",N(fps),"-fps_mode","cfr"]);
                if(NormalizeFormat(plan.Format)=="hevc")
                {
                    var outputInfo=await media.ProbeAsync(video,token);
                    args.AddRange(VideoCodecArgs(plan.Format,plan.BitrateKbps,outputInfo.Width,outputInfo.Height,fps,plan.ColorSpace));
                }
                else args.AddRange(VideoCodecArgs(plan.Format,plan.BitrateKbps,colorSpace:plan.ColorSpace));
                args.AddRange(spec?.AudioArgs??["-c:a","aac","-b:a","192k"]);
                args.AddRange(spec?.MuxArgs??["-movflags","+faststart"]);
            }
            args.Add(target);
            await media.RunAsync(media.Ffmpeg,args,token);
            if(frames is not null)
            {
                if(Directory.GetFiles(frames).Length==0)throw new InvalidOperationException("FFmpeg 未生成图像序列");
                if(File.Exists(output))File.Delete(output);
                System.IO.Compression.ZipFile.CreateFromDirectory(frames,output,System.IO.Compression.CompressionLevel.Optimal,false);
                Directory.Delete(frames,true);
            }
            if(!File.Exists(output)||new FileInfo(output).Length==0)throw new InvalidOperationException(kind==ExportKind.AudioOnly?"FFmpeg 未生成音频":kind==ExportKind.ImageSequence?"FFmpeg 未生成图像序列压缩包":"FFmpeg 未生成视频");
            await File.WriteAllTextAsync(Path.ChangeExtension(output,".srt"),SubtitleTools.ToSrt(subtitles),new UTF8Encoding(false),token);
    }
    private async Task RunAsync(Job job,EditorRender render,Clip? clip,CancellationTokenSource control,string? configuredWorkDirectory)
    {
        var entered=false;var startedRender=false;RenderWorkScope? workScope=null;
        var stopped=false;var completed=false;string? failure=null;
        try
        {
            workScope=RenderWorkScope.Capture(storageRoot,configuredWorkDirectory,render.Id,render.OutputPath);
            await _worker.WaitAsync(control.Token);entered=true;
            lock(_gate){control.Token.ThrowIfCancellationRequested();store.SaveJob(job=job with {Status="running",Progress=.05,Error=null});}
            var plan=EditorProjectCodec.Deserialize(render.ProjectJson);Validate(plan);
            workScope.Create();startedRender=true;
            if(render.ProjectIdentity is { } identity)ValidateIndependentRender(identity,plan);
            await RenderCoreAsync(render.ProjectId,render.ProjectIdentity,plan,render.OutputPath,workScope.Work,control.Token,p=>{lock(_gate){if(!control.IsCancellationRequested)store.SaveJob(job with {Status="running",Progress=p});}});
            // 完成与取消在 _gate 内二选一：先受理的 cancel 生效；先发布的 completed 不会再被 Control 改写。
            lock(_gate){control.Token.ThrowIfCancellationRequested();store.SaveJob(job=job with {Status="completed",Progress=1});completed=true;}
        }
        catch(OperationCanceledException){stopped=true;}
        catch(Exception ex){if(control.IsCancellationRequested)stopped=true;else failure=ex.Message;}
        finally
        {
            string? cleanupError=null;
            try
            {
                // Settings may change while queued/running. Cleanup uses only this attempt's captured scope.
                if(stopped&&startedRender)workScope!.DeleteIncompleteOutput();
            }
            catch(Exception ex){cleanupError=ex.Message;}
            try
            {
                workScope?.Cleanup();
            }
            catch(Exception ex){cleanupError??=ex.Message;}
            try
            {
                lock(_gate)
                {
                    try
                    {
                        if(completed){if(cleanupError is not null)store.SaveJob(job with {Error="工作目录清理失败："+cleanupError});}
                        else if(cleanupError is not null)store.SaveJob(job with {Status="failed",Error=(failure is null?"":failure+"；")+"清理失败："+cleanupError});
                        else if(failure is not null)store.SaveJob(job with {Status="failed",Error=failure});
                        else if(stopped)
                        {
                            var current=store.Jobs().FirstOrDefault(x=>x.Id==job.Id);
                            store.SaveJob(job with {Status=current?.Status switch{"cancelling"=>"cancelled","pausing"=>"paused",_=>"pending"},Error=null});
                        }
                    }
                    finally{_controls.TryRemove(job.Id,out _);control.Dispose();}
                }
            }
            finally{if(entered)_worker.Release();}
        }
    }
}

