using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LiveClipper.Core;

/// <summary>
/// 独立字幕轨 cue（序列秒）。EditorProject.TimelineCaptions 为 null=沿用旧的“源字幕动态映射”；
/// 非 null（含 []）=该数组是字幕唯一权威，[] 表示字幕轨为空，不会再补回源字幕。
/// 样式字段为 null 时回落到工程级 SubtitleFont/Size/Color/Outline/Margin/Position。
/// JSON 为 camelCase（工程 Web 序列化默认）。
/// </summary>
public sealed record TimelineCaption
{
    public string Id {get;init;}="";
    public double Start {get;init;}
    public double End {get;init;}
    public string Text {get;init;}="";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Font {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? Size {get;init;}
    /// <summary>#RRGGBB。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Color {get;init;}
    /// <summary>字幕中心点，相对画面宽高 0–1；X/Y 必须同时给或同时缺省。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? PositionX {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? PositionY {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? Outline {get;init;}
    /// <summary>垂直边距；仅在未设置位置时生效。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? Margin {get;init;}
    // 以下仅为 web 映射/溯源，渲染不读取。
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? SourceClipId {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? SequenceItemId {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? SourceCueIndex {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? SourceStart {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? SourceEnd {get;init;}
    /// <summary>手工 cue（不随源字幕变更覆盖）。</summary>
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] public bool Manual {get;init;}
}

public static class TimelineCaptionTools
{
    public static void Validate(TimelineCaption[]? captions,double timelineDuration)
    {
        if(captions is null)return;
        var bad=captions.Length>5000||captions.Any(x=>x is null||string.IsNullOrWhiteSpace(x.Id)||x.Id.Length>120||string.IsNullOrWhiteSpace(x.Text)||x.Text.Length>2000
            ||!double.IsFinite(x.Start)||!double.IsFinite(x.End)||x.Start<0||x.End<=x.Start||x.End>timelineDuration+0.001
            ||(x.Font is not null&&(string.IsNullOrWhiteSpace(x.Font)||x.Font.Length>80))||(x.Size is {} s&&s is <10 or >80)
            ||(x.Color is not null&&!Regex.IsMatch(x.Color,"^#[A-Fa-f0-9]{6}$"))
            ||(x.PositionX is null)!=(x.PositionY is null)||(x.PositionX is {} px&&(!double.IsFinite(px)||px is <0 or >1))||(x.PositionY is {} py&&(!double.IsFinite(py)||py is <0 or >1))
            ||(x.Outline is {} o&&(!double.IsFinite(o)||o is <0 or >8))||(x.Margin is {} m&&m is <0 or >200)
            ||(x.SourceStart is {} ss&&!double.IsFinite(ss))||(x.SourceEnd is {} se&&!double.IsFinite(se)));
        if(bad||captions.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=captions.Length)
            throw new ArgumentException("独立字幕无效：需唯一 id、非空文本、有限且 end>start 的序列秒、字体/字号(10–80)/颜色(#RRGGBB)/位置(0–1，X/Y 成对)/描边(0–8)/边距(0–200)");
    }

    /// <summary>转为 SRT 侧车与“有无字幕”判断使用的段；按开始时间排序。</summary>
    public static IReadOnlyList<TranscriptSegment> ToSegments(TimelineCaption[] captions)=>
        captions.OrderBy(x=>x.Start).Select(x=>new TranscriptSegment(x.Start,x.End,x.Text)).ToArray();

    static string N(double v)=>v.ToString("0.######",CultureInfo.InvariantCulture);
    static string Bgr(string rgb)=>"&H00"+rgb[5..7]+rgb[3..5]+rgb[1..3]+"&";
    static string Time(double s){var cs=(long)Math.Round(Math.Max(0,s)*100);return $"{cs/360000}:{cs/6000%60:00}:{cs/100%60:00}.{cs%100:00}";}
    /// <summary>ASS 只有 1/100 秒；ffmpeg subtitles 滤镜把帧 pts 截断成毫秒后与 [start,end) 比较（半开）。</summary>
    public const double MaxFrameAlignedFps=60;
    const double FrameEpsilon=1e-6;
    /// <summary>
    /// 帧对齐：把序列秒换成“输出帧采样点”可表达的 ASS 时间。k=ceil(t*fps)=第一个 pts≥t 的帧，
    /// 取 floor(k/fps 到 1/100 秒再减 1ms 余量)：≤ 该帧 pts（该帧可见/开始帧不延后），
    /// 且 &gt; 前一帧 pts（前一帧不可见/结束帧起消失）。后者要求帧间隔 &gt; 11ms，即 fps&lt;90；故仅支持 fps≤60，其余回退旧四舍五入。
    /// FrameEpsilon 只吸收 n/fps 存成 double 的表示误差。
    /// </summary>
    static string FrameTime(double s,double fps)
    {
        var k=Math.Ceiling(Math.Max(0,s)*fps-FrameEpsilon);
        var cs=(long)Math.Max(0,Math.Floor(k/fps*100-0.1));
        return $"{cs/360000}:{cs/6000%60:00}:{cs/100%60:00}.{cs%100:00}";
    }
    static string Font(string? f)=>Regex.Replace(f??"","[^\\p{L}\\p{N} _-]","");
    static string EscapeText(string t)=>t.Replace("\\","＼").Replace("{","\\{").Replace("}","\\}").Replace("\r\n","\\N").Replace("\n","\\N").Replace("\r","\\N");

    // 独立字幕轨 canonical 语义 = 原生预览 fc-captions.js drawOnto：
    // 以 960 宽为参考坐标，实际画面宽 W 时一切长度量 ×(W/960)；cue.size 缺省 34、margin 缺省 36（参考 px）、
    // 底部居中，字体 Segoe UI 权重 600，白字，描边为 stroke 宽 max(3,size*.16)（一半在字外，即 \bord≈size*.08）。
    // 这里不使用 EditorProject.Subtitle*（那是旧 SRT 路径、PlayRes288 语义），预览目前也不读它们。
    public const int ReferenceWidth=960;
    const int DefaultSize=34,DefaultMargin=36;

    /// <summary>
    /// 生成 ASS（仅独立字幕轨）。PlayResX=960、PlayResY=round(960*高/宽)，所以 ASS 坐标即“960 宽参考 px”，
    /// libass 按 videoWidth/960 等比放大到实际烧录视频；\pos 用归一化坐标×(960, PlayResY)，\an5 以文字块中心对齐。
    /// videoWidth/videoHeight 必须是实际被 subtitles 滤镜烧录的那条视频的真实宽高。
    /// </summary>
    public static string ToAss(TimelineCaption[] captions,int videoWidth,int videoHeight,double? outputFps=null)
    {
        if(videoWidth<=0||videoHeight<=0)throw new ArgumentException("烧录字幕需要有效的视频宽高");
        // outputFps=实际烧录视频恒定帧率；null/非法/&gt;60 走旧的四舍五入到 1/100 秒（不声称能表达高帧率/VFR）。
        string T(double s)=>outputFps is {} f&&double.IsFinite(f)&&f>0&&f<=MaxFrameAlignedFps?FrameTime(s,f):Time(s);
        const int w=ReferenceWidth;
        var h=Math.Max(1,(int)Math.Round((double)w*videoHeight/videoWidth));
        var sb=new StringBuilder();
        sb.Append("[Script Info]\nScriptType: v4.00+\nWrapStyle: 0\nScaledBorderAndShadow: yes\nPlayResX: ").Append(w).Append("\nPlayResY: ").Append(h).Append("\n\n");
        sb.Append("[V4+ Styles]\nFormat: Name,Fontname,Fontsize,PrimaryColour,SecondaryColour,OutlineColour,BackColour,Bold,Italic,Underline,StrikeOut,ScaleX,ScaleY,Spacing,Angle,BorderStyle,Outline,Shadow,Alignment,MarginL,MarginR,MarginV,Encoding\n");
        // 描边色 rgba(0,0,0,.86) → ASS alpha 0x24；边距全在 Dialogue 里逐条写，避免 0=沿用样式的歧义。
        sb.Append($"Style: Default,Segoe UI,{DefaultSize},{Bgr("#FFFFFF")},&H000000FF,&H24000000,&H00000000,600,0,0,0,100,100,0,0,1,{N(DefaultSize*.08)},0,2,0,0,0,1\n\n");
        sb.Append("[Events]\nFormat: Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text\n");
        foreach(var c in captions.OrderBy(x=>x.Start))
        {
            var tags=new StringBuilder();
            if(c.PositionX is {} px&&c.PositionY is {} py)tags.Append($"\\an5\\pos({N(px*w)},{N(py*h)})");
            if(!string.IsNullOrWhiteSpace(c.Font)&&Font(c.Font) is {Length:>0} f)tags.Append("\\fn").Append(f);
            var size=c.Size ?? DefaultSize;
            if(c.Size is not null)tags.Append("\\fs").Append(size);
            if(c.Color is not null)tags.Append("\\c").Append(Bgr(c.Color));
            if(c.Outline is {} o)tags.Append("\\bord").Append(N(o));
            else if(c.Size is not null)tags.Append("\\bord").Append(N(size*.08));
            var margin=c.Margin ?? DefaultMargin;
            sb.Append($"Dialogue: 0,{T(c.Start)},{T(c.End)},Default,,{margin},{margin},{margin},,{(tags.Length>0?"{"+tags+"}":"")}{EscapeText(c.Text)}\n");
        }
        return sb.ToString();
    }
}
