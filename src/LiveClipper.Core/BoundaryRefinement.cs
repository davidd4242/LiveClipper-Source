using System.Globalization;
using LiveClipper.Domain;

namespace LiveClipper.Core;

/// <summary>
/// 切片边界的两段式细化（参考 Bilibili-Auto-Clipping）：
/// 先用音频 RMS 峰值把起止点向外拉到情绪最高点，再按字幕句末标点/语句间隔对齐并留 padding。
/// </summary>
public sealed class AudioPeakService(MediaTools media)
{
    private readonly Dictionary<string,(string Signature,double[] Windows)> _cache=new();
    private const double WindowSeconds=.5;

    /// <summary>按 0.5 秒窗口计算全片归一化 RMS（0–1），结果按文件签名缓存。流式解码，不落地中间文件。</summary>
    public async Task<IReadOnlyList<double>> LoadWindowsAsync(string path,CancellationToken token)
    {
        var signature=$"{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}";
        lock(_cache)
        {
            if(_cache.TryGetValue(path,out var hit)&&hit.Signature==signature)return hit.Windows;
        }
        var samplesPerWindow=(int)(16000*WindowSeconds);
        var windows=new List<double>();
        double sum=0;int count=0;
        using var process=MediaTools.Start(media.Ffmpeg,["-hide_banner","-loglevel","error","-i",path,"-map","0:a:0","-ac","1","-ar","16000","-f","s16le","-"]);
        var buffer=new byte[64*1024];
        await using var stdout=process.StandardOutput.BaseStream;
        int read;
        while((read=await stdout.ReadAsync(buffer,token))>0)
            for(var offset=0;offset+1<read;offset+=2)
            {
                // 16 位小端 PCM，归一化到 -1..1。
                var sample=BitConverter.ToInt16(buffer,offset)/32768d;
                sum+=sample*sample;
                if(++count>=samplesPerWindow)
                {
                    windows.Add(Math.Sqrt(sum/count));sum=0;count=0;
                }
            }
        if(count>0)windows.Add(Math.Sqrt(sum/count));
        try{await process.WaitForExitAsync(token);}catch(OperationCanceledException){if(!process.HasExited)process.Kill(true);throw;}
        if(process.ExitCode!=0)throw new InvalidOperationException("音频峰值分析失败");
        var peak=windows.Count>0?windows.Max():0d;
        var normalized=peak>0?windows.Select(x=>x/peak).ToArray():[];
        lock(_cache){_cache[path]=(signature,normalized);}
        return normalized;
    }

    /// <summary>
    /// 在候选起止点附近搜索最强音频峰：起点在 [-8s,+2s]、终点在 [-2s,+8s] 内找峰值窗口，
    /// 峰值归一化低于 floor 时忽略；命中后把边界向外扩 1 秒。
    /// </summary>
    public (double Start,double End) Refine(double start,double end,IReadOnlyList<double> windows,double duration,double search=8,double floor=.2)
    {
        if(windows.Count==0)return (start,end);
        double At(double second)=>windows[Math.Clamp((int)(second/WindowSeconds),0,windows.Count-1)];
        double Strongest(double from,double to)
        {
            from=Math.Max(0,from);to=Math.Max(0,to);
            var best=(Second:-1d,Value:0d);
            for(var second=from;second<Math.Min(to,windows.Count*WindowSeconds);second+=WindowSeconds)
                if(At(second)>best.Value)best=(second,At(second));
            return best.Value>=floor?best.Second:-1;
        }
        var refinedStart=start;
        var peakStart=Strongest(start-search,start+2);
        if(peakStart>=0)refinedStart=Math.Max(0,Math.Min(start,peakStart-1));
        var refinedEnd=end;
        var peakEnd=Strongest(end-2,end+search);
        if(peakEnd>=0)refinedEnd=Math.Min(duration>0?duration:double.MaxValue,Math.Max(end,peakEnd+1+WindowSeconds));
        return (refinedStart,refinedEnd<refinedStart+1?refinedStart+1:refinedEnd);
    }
}

/// <summary>语音边界对齐：沿字幕行向外扩展到句末标点、语句间隔或上限，最后留 padding。</summary>
public static class SpeechBoundaryAlign
{
    private const string SentenceEnd=".!?…。！？";
    public static (double Start,double End) Align(
        double start,double end,IReadOnlyList<TranscriptSegment> segments,
        double maxExtraBefore=8,double maxExtraAfter=10,double connectedGap=1.2,double padBefore=.35,double padAfter=.65)
    {
        if(segments.Count==0)return (start,end);
        var pairs=segments.Select((x,i)=>(Segment:x,Index:i)).Where(x=>x.Segment.End>start&&x.Segment.Start<end).ToList();
        if(pairs.Count==0)return (start,end);
        var first=pairs[0].Segment;var last=pairs[^1].Segment;
        var indexFirst=pairs[0].Index;var indexLast=pairs[^1].Index;
        // 向前扩展：与上一句间隔小于 connectedGap 就并入，直到上一句以句末标点收尾或超出上限。
        var refinedStart=first.Start;
        for(var i=indexFirst-1;i>=0&&refinedStart-segments[i].End<=connectedGap&&first.Start-segments[i].Start<=maxExtraBefore;i--)
        {
            refinedStart=segments[i].Start;
            if(segments[i].Text.TrimEnd().Length>0&&SentenceEnd.Contains(segments[i].Text.TrimEnd()[^1]))break;
        }
        var refinedEnd=last.End;
        for(var i=indexLast+1;i<segments.Count&&segments[i].Start-last.End<=connectedGap&&segments[i].End-last.End<=maxExtraAfter;i++)
        {
            refinedEnd=segments[i].End;
            if(segments[i].Text.TrimEnd().Length>0&&SentenceEnd.Contains(segments[i].Text.TrimEnd()[^1]))break;
        }
        return (Math.Max(0,refinedStart-padBefore),refinedEnd+padAfter);
    }
}
