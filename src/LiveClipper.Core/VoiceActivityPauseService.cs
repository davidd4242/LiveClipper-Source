using System.Text.Json;
using System.Globalization;
namespace LiveClipper.Core;

public sealed record VoiceGap(double Start,double End,string Reason="");
public sealed record VoiceActivityEvidence(string Version,double SourceStart,double SourceEnd,int SampleRate,
    SilenceRange[] Speech,SilenceRange[] Uncertain,VoiceGap[] Gaps,string[] Warnings);

public sealed class VoiceActivityPauseService(MediaTools media,string python,string? script=null)
{
    public const string Provider="silero-v6-local-v1";
    public async Task<VoiceActivityEvidence> DetectAsync(string path,double start,double end,CancellationToken token)
    {
        var worker=script??Path.Combine(AppContext.BaseDirectory,"ai-worker","vad_pause.py");
        if(!File.Exists(worker))throw new NotSupportedException("本地VAD脚本未安装");
        var parts=new List<VoiceActivityEvidence>();
        for(var at=start;at<end;at+=598)
        {
            var from=Math.Max(start,at-1);var to=Math.Min(end,at+599);
            var output=await media.RunAsync(python,["-B",worker,"--input",path,"--ffmpeg",media.Ffmpeg,"--ffprobe",media.Ffprobe,
                "--start",from.ToString("R",CultureInfo.InvariantCulture),"--end",to.ToString("R",CultureInfo.InvariantCulture)],token);
            var part=JsonSerializer.Deserialize<VoiceActivityEvidence>(output,new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ??throw new InvalidOperationException("本地VAD未返回证据");
            if(part.Version!=Provider||part.SampleRate!=16000||Math.Abs(part.SourceStart-from)>.001||Math.Abs(part.SourceEnd-to)>.001)
                throw new InvalidOperationException("本地VAD证据版本或源时间范围不匹配");
            if(part.Speech is null||part.Uncertain is null||part.Gaps is null||part.Warnings is null)
                throw new InvalidOperationException("本地VAD证据缺少区间字段");
            var intervals=part.Speech.Concat(part.Uncertain).Concat(part.Gaps.Select(x=>new SilenceRange(x.Start,x.End)));
            if(intervals.Any(x=>!double.IsFinite(x.Start)||!double.IsFinite(x.End)||x.End<x.Start||x.Start<from-.001||x.End>to+.001))
                throw new InvalidOperationException("本地VAD证据区间不在原源范围内");
            parts.Add(part);
        }
        return new(Provider,start,end,16000,Merge(parts.SelectMany(x=>x.Speech)),Merge(parts.SelectMany(x=>x.Uncertain)),
            parts.SelectMany(x=>x.Gaps).Distinct().ToArray(),parts.SelectMany(x=>x.Warnings).Distinct().ToArray());
    }
    internal static SilenceRange[] Merge(IEnumerable<SilenceRange> ranges)
    {
        var merged=new List<SilenceRange>();
        foreach(var r in ranges.OrderBy(x=>x.Start))
        {
            if(merged.Count>0&&r.Start<=merged[^1].End)merged[^1]=merged[^1] with {End=Math.Max(merged[^1].End,r.End)};
            else merged.Add(r);
        }
        return merged.ToArray();
    }
}
