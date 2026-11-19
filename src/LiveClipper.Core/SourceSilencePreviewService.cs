using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace LiveClipper.Core;

public sealed record SourceSilenceCandidate(string Id,double Start,double End,double CutStart=0,double CutEnd=0,bool AutoEligible=false,string[]? RiskCodes=null,CaptionVoiceConflict[]? CaptionConflicts=null);
public sealed record CaptionVoiceConflict(double Start,double End,double VoiceSeconds,double CoverageRatio,double ReviewThreshold=.5);
public sealed record SourceSilencePreview(string SourceId,string SourceSignature,double Duration,double Start,double End,double NoiseDb,double MinSeconds,SourceSilenceCandidate[] Silences,string[] Warnings,string TranscriptCacheKey="",double Padding=SilenceCutPlanner.Padding,string PolicyVersion=SilenceCutPlanner.Version,string EvidenceProvider="energy-only-fallback");
/// <summary>Read-only source-time silence measurement. No timeline mutation or short-piece deletion.</summary>
public sealed class SourceSilencePreviewService(MediaTools media,Func<string>? pythonPath=null,string? vadScript=null)
{
    public static string Signature(string path)
    {
        var f=new FileInfo(path);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"))).ToLowerInvariant();
    }
    public async Task<SourceSilencePreview> PreviewAsync(string sourceId,string path,double start,double end,double noiseDb,double minSeconds,CancellationToken token=default,IReadOnlyList<TranscriptSegment>? transcript=null,int fps=30,string transcriptCacheKey="")
    {
        token.ThrowIfCancellationRequested();
        if(!double.IsFinite(start)||!double.IsFinite(end)||start<0||end<=start)throw new ArgumentException("请选择有效的源素材起止时间");
        if(!double.IsFinite(noiseDb)||noiseDb< -90||noiseDb>0)throw new ArgumentException("静音阈值须在 -90 至 0 dB 之间");
        if(!double.IsFinite(minSeconds)||minSeconds<.2||minSeconds>30)throw new ArgumentException("长停顿阈值须在 0.2 至 30 秒之间");
        var signature=Signature(path);
        using var doc=JsonDocument.Parse(await media.RunAsync(media.Ffprobe,["-v","error","-show_streams","-show_format","-of","json",path],token));
        var format=doc.RootElement.GetProperty("format");
        var duration=double.Parse(format.GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
        var origin=format.TryGetProperty("start_time",out var st)?double.Parse(st.GetString()!,CultureInfo.InvariantCulture):0;
        if(Math.Abs(origin)>.000001)throw new NotSupportedException("暂不支持非零容器起点素材的停顿预览");
        if(end>duration+.001)throw new ArgumentException("选择范围超出素材时长");
        var audio=doc.RootElement.GetProperty("streams").EnumerateArray().Where(s=>s.GetProperty("codec_type").GetString()=="audio").ToArray();
        if(audio.Length==0)throw new NotSupportedException("素材没有音轨，无法检测停顿");
        if(audio.Length>1)throw new NotSupportedException("素材包含多个音频流，请先指定单一对白音源");
        string? vadFailure=null;
        if(pythonPath is not null)
        {
            try
            {
                var evidence=await new VoiceActivityPauseService(media,pythonPath(),vadScript).DetectAsync(path,start,Math.Min(end,duration),token);
                if(signature!=Signature(path))throw new InvalidOperationException("检测期间源素材已变化");
                var planned=SilenceCutPlanner.PlanVoice(evidence,transcript,fps,minSeconds);
                return new(sourceId,signature,duration,start,Math.Min(end,duration),noiseDb,minSeconds,planned,
                    evidence.Warnings.Concat(new[]{"本地人声活动检测，不调用在线模型；音量阈值不参与VAD判断。",
                    "字幕仅辅助保护句边界；弱语音、缺少前后语音或大时间戳缺口保留。VAD不是语义或听感保证。"}).ToArray(),
                    transcriptCacheKey,SilenceCutPlanner.Padding,SilenceCutPlanner.Version,VoiceActivityPauseService.Provider);
            }
            catch(OperationCanceledException){throw;}
            catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or NotSupportedException)
            {vadFailure=ex.Message;}
        }
        else vadFailure="未配置本地VAD Python";
        var filter=FormattableString.Invariant($"silencedetect=noise={noiseDb}dB:d={minSeconds}");
        // Preserve input PTS: delayed audio must not be moved to time zero.
        var log=await media.RunAsync(media.Ffmpeg,["-hide_banner","-nostats","-copyts","-i",path,"-map","0:a:0","-vn","-af",filter,"-f","null","-"],token);
        if(signature!=Signature(path))throw new InvalidOperationException("检测期间源素材已变化，请重新预览");
        var ranges=SilenceCutPlanner.Plan(Parse(log,start,Math.Min(end,duration),minSeconds).Select(x=>new SilenceRange(x.Start,x.End)),transcript,fps)
            .Select(x=>x with {AutoEligible=false,RiskCodes=(x.RiskCodes??[]).Append("vad-unavailable").ToArray()}).ToArray();
        var warnings=new List<string>{"静音只代表低音量，不代表无意义停顿；请试听并保留有意停顿。","仅返回候选，不会修改或保存工程。"};
        if(audio[0].TryGetProperty("start_time",out var a)&&double.TryParse(a.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var audioStart)&&audioStart>0)
            warnings.Add("音频存在延迟起点；检测保留源时间戳，未将起点前无音轨区域标记为静音。");
        return new(sourceId,signature,duration,start,Math.Min(end,duration),noiseDb,minSeconds,ranges,warnings.Append("本地VAD不可用，当前仅音量检测回退，禁止自动删除："+vadFailure).Append("字幕仅是句级保护区，不能证明逐字边界；无字幕、覆盖不足或低音量与字幕冲突的候选默认保留，音乐环境不强行产生切点。").ToArray(),transcriptCacheKey);
    }
    internal static SourceSilenceCandidate[] Parse(string log,double start,double end,double minSeconds)
    {
        var result=new List<SourceSilenceCandidate>();double? open=null;
        foreach(Match m in Regex.Matches(log,@"silence_(start|end):\s*(-?[0-9.]+)"))
        {
            var t=double.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture);
            if(m.Groups[1].Value=="start"){open=t;continue;}
            if(open is {} s){Add(s,t);open=null;}
        }
        if(open is {} tail)Add(tail,end);
        return result.ToArray();
        void Add(double s,double e)
        {
            s=Math.Max(start,s);e=Math.Min(end,e);
            if(e-s+1e-6<minSeconds)return;
            result.Add(new(FormattableString.Invariant($"silence-{s:R}-{e:R}"),s,e));
        }
    }
}
