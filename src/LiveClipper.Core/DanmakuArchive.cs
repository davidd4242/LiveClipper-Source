using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace LiveClipper.Core;

public sealed record DanmakuPoint(double Time,string Text);

/// <summary>
/// B 站弹幕 XML（官方录制机/录播工具的侧车文件）离线分析：
/// 解析弹幕点 → 按窗口聚合热度 → 生成不依赖 LLM 的本地切片候选。
/// </summary>
public static class DanmakuArchive
{
    private static readonly string[] ReactionWords=["哈哈","笑","太强","离谱","绷不住","卧槽","来了","翻盘","爆了","泪目","名场面","离大谱","绝了","awsl","寄了","上大分","下饭"];
    /// <summary>找到视频同目录、同名的弹幕 XML 侧车。</summary>
    public static string? FindSidecar(string videoPath)
    {
        var candidate=Path.ChangeExtension(videoPath,".xml");
        return File.Exists(candidate)?candidate:null;
    }

    /// <summary>解析 `<d p="time,...">内容</d>` 结构。解析前拒绝 DOCTYPE/ENTITY，且禁用 DTD 与外部实体。</summary>
    public static IReadOnlyList<DanmakuPoint> Parse(string path)
    {
        var content=File.ReadAllText(path);
        if(content.AsSpan(0,Math.Min(content.Length,4096)).IndexOf("!DOCTYPE",StringComparison.OrdinalIgnoreCase)>=0
            ||content.AsSpan(0,Math.Min(content.Length,4096)).IndexOf("!ENTITY",StringComparison.OrdinalIgnoreCase)>=0)
            throw new InvalidOperationException("弹幕 XML 包含 DOCTYPE/ENTITY 声明，已拒绝解析");
        var points=new List<DanmakuPoint>();
        var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,IgnoreComments=true,IgnoreWhitespace=true};
        using var reader=XmlReader.Create(path,settings);
        // ReadElementContentAsString 会把读取器推进到下一个节点，这里手动控制前进，避免跳过相邻的 <d>。
        if(!reader.Read())return points;
        while(reader.ReadState==ReadState.Interactive)
        {
            if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="d")
            {
                var attribute=reader.GetAttribute("p");
                var text=reader.ReadElementContentAsString().Trim();
                if(!string.IsNullOrWhiteSpace(attribute)&&text.Length>0)
                {
                    var first=attribute.Split(',')[0];
                    if(double.TryParse(first,NumberStyles.Float,CultureInfo.InvariantCulture,out var time)&&time>=0)
                        points.Add(new(time,Regex.Replace(text,@"\s+","")));
                }
                continue;
            }
            if(!reader.Read())break;
        }
        return points.OrderBy(x=>x.Time).ToArray();
    }

    /// <summary>把弹幕/反应词命中聚合成固定秒数的窗口，score = log1p(条数)+log1p(权重)。</summary>
    public static IReadOnlyList<DanmakuWindow> Aggregate(IReadOnlyList<DanmakuPoint> points,double windowSeconds=60)
    {
        var windows=new List<DanmakuWindow>();
        DanmakuWindow? open=null;
        foreach(var point in points)
        {
            var index=Math.Floor(point.Time/windowSeconds);
            if(open is null||Math.Floor(open.Start/windowSeconds)!=index)
            {
                open=new DanmakuWindow(index*windowSeconds,Math.Min(index*windowSeconds+windowSeconds,double.MaxValue),[],0);
                windows.Add(open);
            }
            // record 是不可变的：with 会生成新实例，必须同步替换列表末尾的元素。
            open=open with {Points=[..open.Points,point],Weight=open.Weight+1+ReactionBonus(point.Text)};
            windows[^1]=open;
        }
        return windows;
    }
    /// <summary>反应词命中数；每命中一个词加 1 分权重。</summary>
    public static int ReactionBonus(string text)=>ReactionWords.Count(word=>text.Contains(word,StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从弹幕窗口和字幕反应词里挑出本地候选：取前 limit 个互不相邻的热窗口，
    /// 每个扩展成 center±30 秒的候选片段，confidence=min(0.99, score/10)。
    /// </summary>
    public static IReadOnlyList<CandidateEvent> LocalCandidates(
        IReadOnlyList<DanmakuWindow>? danmaku,IReadOnlyList<TranscriptSegment>? segments,
        double duration,double windowSeconds=60,int limit=8)
    {
        var scored=new List<(double Start,double End,double Score,string Origin)>();
        if(danmaku is not null)
            foreach(var window in danmaku.Where(x=>x.Points.Count>=3))
                scored.Add((window.Start,window.End,Math.Log(1+window.Points.Count)+Math.Log(1+window.Weight),"danmaku"));
        if(segments is not null)
        {
            var hits=new List<DanmakuPoint>();
            foreach(var segment in segments)
            {
                var bonus=ReactionBonus(segment.Text);
                if(bonus>0)hits.Add(new((segment.Start+segment.End)/2,segment.Text));
            }
            foreach(var window in Aggregate(hits,windowSeconds).Where(x=>x.Points.Count>=3))
                scored.Add((window.Start,window.End,Math.Log(1+window.Points.Count)+Math.Log(1+window.Weight),"reaction"));
        }
        var result=new List<CandidateEvent>();
        foreach(var (start,end,score,origin) in scored.OrderByDescending(x=>x.Score))
        {
            // 相邻（重叠）窗口只保留最热的一个，避免候选扎堆。
            if(result.Any(x=>start<x.End&&end>x.Start))continue;
            var center=(start+end)/2;
            var candidateStart=Math.Max(0,center-30);
            var candidateEnd=Math.Min(duration>0?duration:double.MaxValue,center+30);
            if(candidateEnd-candidateStart<10)continue;
            var excerpt=origin=="danmaku"
                ?string.Join(" / ",AggregateSide(start,end,danmaku!).Take(4))
                :string.Join(" / ",segments!.Where(x=>x.End>start&&x.Start<end&&ReactionBonus(x.Text)>0).Select(x=>x.Text).Take(4));
            result.Add(new(candidateStart,candidateEnd,excerpt.Length>40?excerpt[..40]+"…":excerpt,"弹幕密集段，建议结合上下文确认内容","",Math.Min(.99,score/10),origin));
            if(result.Count>=limit)break;
        }
        return result;
    }
    private static IEnumerable<string> AggregateSide(double start,double end,IReadOnlyList<DanmakuWindow> danmaku)=>
        danmaku.Where(x=>x.End>start&&x.Start<end).SelectMany(x=>x.Points).OrderBy(x=>x.Time).Select(x=>x.Text);
}

public sealed record DanmakuWindow(double Start,double End,IReadOnlyList<DanmakuPoint> Points,double Weight);
