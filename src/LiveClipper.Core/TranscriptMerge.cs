using System.Text.Encodings.Web;
using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core;

/// <summary>
/// 长录播切片转译后的合并：把每段的时间轴按分片起点平移，拼成一份完整的 JSON 与 SRT，
/// 保持与单段转译完全相同的字段结构（language + segments[start,end,text]）。
/// </summary>
public static class TranscriptMerge
{
    public sealed record Part(double OffsetSeconds,string Json);
    public sealed record Result(string Json,string Srt,int SegmentCount,string Language);

    public static Result Merge(IEnumerable<Part> parts)
    {
        var segments=new List<TranscriptSegment>();
        var language="";
        foreach(var part in parts)
        {
            if(string.IsNullOrWhiteSpace(part.Json))continue;
            TranscriptPreview preview;
            try{preview=TranscriptExport.Parse(part.Json);}catch(JsonException){continue;}catch(KeyNotFoundException){continue;}catch(InvalidOperationException){continue;}
            if(language.Length==0&&!string.IsNullOrWhiteSpace(preview.Language))language=preview.Language;
            foreach(var segment in preview.Segments)
                segments.Add(new TranscriptSegment(segment.Start+part.OffsetSeconds,segment.End+part.OffsetSeconds,segment.Text));
        }
        segments=segments.OrderBy(x=>x.Start).ToList();
        var json=JsonSerializer.Serialize(new{language,segments=segments.Select(x=>new{start=Math.Round(x.Start,3),end=Math.Round(x.End,3),text=x.Text})},
            new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        return new(json,ToSrt(segments),segments.Count,language);
    }

    public static string ToSrt(IReadOnlyList<TranscriptSegment> segments)
    {
        var text=new System.Text.StringBuilder();
        for(var i=0;i<segments.Count;i++)
            text.Append(i+1).Append('\n').Append(Stamp(segments[i].Start)).Append(" --> ").Append(Stamp(segments[i].End)).Append('\n').Append(segments[i].Text).Append("\n\n");
        return text.ToString();
    }

    /// <summary>SRT 时间戳：HH:MM:SS,mmm（超过 24 小时同样按小时累加）。</summary>
    public static string Stamp(double seconds)
    {
        var ms=Math.Max(0,(long)Math.Round(seconds*1000));
        var hours=ms/3600000;ms%=3600000;var minutes=ms/60000;ms%=60000;var secs=ms/1000;ms%=1000;
        return $"{hours:00}:{minutes:00}:{secs:00},{ms:000}";
    }
}
