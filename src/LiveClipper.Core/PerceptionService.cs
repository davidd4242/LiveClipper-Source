using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveClipper.Core;

// R50-B perception primitives: shot cuts (ffmpeg select='gt(scene,θ)',showinfo) and
// a short-term loudness envelope (ffmpeg ebur128 momentary M: values).
// Everything here is read-only: it never writes a timeline, project or any file.
// Cut times are only suggestions; applying them belongs to the command layer.

/// <summary>Source frame rate as an exact rational, with the ffprobe field it came from.</summary>
public sealed record PerceptionFrameRate(long Numerator,long Denominator,string Source)
{
    public double Value=>(double)Numerator/Denominator;
}

/// <summary>Video/audio stream facts read from ffprobe JSON stdout.</summary>
public sealed record PerceptionStreamInfo(bool HasVideo,bool HasAudio,PerceptionFrameRate? FrameRate,double VideoStartTime,double? Duration);

/// <summary>
/// One detected cut. <c>RawTime</c> is the showinfo pts_time (minus the video start time);
/// <c>Time</c> is that value snapped to the source frame grid; <c>Adjusted</c> says whether
/// snapping moved it and <c>AdjustedBy</c> is the signed snap amount in seconds.
/// </summary>
public sealed record ShotCut(double Time,long Frame,double RawTime,bool Adjusted,double AdjustedBy);

/// <summary>One ebur128 momentary (400 ms) measurement. <c>Lufs</c> is null for silence (gated) or -inf/nan.</summary>
public sealed record MomentaryLoudness(double T,double? Lufs);

/// <summary>Parsed ebur128 log. Momentary peak is LUFS; sample peak is dBFS (PCM). They are different units.</summary>
public sealed record EbuR128Log(IReadOnlyList<MomentaryLoudness> Samples,double? IntegratedLufs,double? SamplePeakDbfs);

/// <summary>One envelope window [T, End). <c>Lufs</c> is the energy mean; <c>MaxLufs</c> the loudest momentary value inside.</summary>
public sealed record LoudnessEnvelopePoint(double T,double End,double? Lufs,double? MaxLufs,int Samples);

public sealed record ShotDetectionResult(double Threshold,PerceptionFrameRate FrameRate,IReadOnlyList<ShotCut> Cuts);

public sealed record LoudnessResult(double Window,bool HasAudio,IReadOnlyList<LoudnessEnvelopePoint> Envelope,double? MomentaryPeakLufs,double? IntegratedLufs,double? SamplePeakDbfs);

// runner executes one tool invocation and returns (exit code, stdout, stderr). Production uses RunProcessAsync
// (MediaTools.Start); R51-B PerceptionEndpointsTests passes canned ffprobe output to reach the "no usable fps" 502 path.
public sealed class PerceptionService(MediaTools media,Func<string,IReadOnlyList<string>,CancellationToken,Task<(int Exit,string Stdout,string Stderr)>> runner)
{
    public PerceptionService(MediaTools tools):this(tools,RunProcessAsync){}

    /// <summary>select=gt(scene,θ) can only fire for 0 &lt; θ &lt; 1 (scene score lives in [0,1]).</summary>
    public const double MinThresholdExclusive=0,MaxThresholdExclusive=1;
    /// <summary>ebur128 emits a momentary value every 100 ms, so shorter windows would be empty.</summary>
    public const double MinWindowSeconds=0.1,MaxWindowSeconds=600;
    /// <summary>BS.1770 absolute gate. Momentary values at or below it are silence, reported as null.</summary>
    public const double SilenceGateLufs=-70;
    private const double SnapEpsilon=1e-9;

    // ---------- validation (pure) ----------

    /// <summary>Returns null when the threshold is usable, otherwise a message that echoes the received value.</summary>
    public static string? ValidateThreshold(double? threshold)
    {
        if(threshold is null)return "缺少 threshold（必填，0<θ<1，不提供默认值）";
        var value=threshold.Value;
        if(!double.IsFinite(value))return $"threshold 必须是有限数，收到 {Echo(value)}";
        if(value<=MinThresholdExclusive||value>=MaxThresholdExclusive)return $"threshold 必须满足 0<θ<1，收到 {Echo(value)}";
        return null;
    }

    /// <summary>Returns null when the window is usable, otherwise a message that echoes the received value.</summary>
    public static string? ValidateWindow(double? window)
    {
        if(window is null)return "缺少 window（必填，单位秒，0.1≤window≤600，不提供默认值）";
        var value=window.Value;
        if(!double.IsFinite(value))return $"window 必须是有限数，收到 {Echo(value)}";
        if(value<MinWindowSeconds||value>MaxWindowSeconds)return $"window 必须满足 0.1≤window≤600 秒，收到 {Echo(value)}";
        return null;
    }

    /// <summary>Parses a query/body string with the invariant culture. Null/blank → null; garbage → NaN so validation reports it.</summary>
    public static double? ParseNumber(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))return null;
        return double.TryParse(raw.Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)?value:double.NaN;
    }

    private static string Echo(double value)=>value.ToString("R",CultureInfo.InvariantCulture);

    // ---------- ffprobe JSON (pure) ----------

    /// <summary>
    /// Reads ffprobe <c>-show_entries stream=index,codec_type,avg_frame_rate,r_frame_rate,time_base,start_time:format=duration -of json</c>.
    /// Frame rate: avg_frame_rate, else r_frame_rate (provenance kept). No usable rate → null, never a guessed 30.
    /// </summary>
    public static PerceptionStreamInfo ParseProbeJson(string json)
    {
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        bool hasVideo=false,hasAudio=false;PerceptionFrameRate? rate=null;double start=0;
        if(root.TryGetProperty("streams",out var streams)&&streams.ValueKind==JsonValueKind.Array)
        {
            foreach(var stream in streams.EnumerateArray())
            {
                var type=stream.TryGetProperty("codec_type",out var t)?t.GetString():null;
                if(type=="audio")hasAudio=true;
                if(type!="video"||hasVideo)continue;
                hasVideo=true;
                rate=ParseRational(Text(stream,"avg_frame_rate"),"avg_frame_rate")??ParseRational(Text(stream,"r_frame_rate"),"r_frame_rate");
                if(double.TryParse(Text(stream,"start_time"),NumberStyles.Float,CultureInfo.InvariantCulture,out var s)&&double.IsFinite(s))start=s;
            }
        }
        double? duration=null;
        if(root.TryGetProperty("format",out var format)&&double.TryParse(Text(format,"duration"),NumberStyles.Float,CultureInfo.InvariantCulture,out var d)&&double.IsFinite(d)&&d>0)duration=d;
        return new PerceptionStreamInfo(hasVideo,hasAudio,rate,start,duration);
    }

    private static string? Text(JsonElement element,string name)=>element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;

    /// <summary>"30000/1001" → rational; "0/0", "N/A", non-positive → null.</summary>
    public static PerceptionFrameRate? ParseRational(string? text,string source)
    {
        if(string.IsNullOrWhiteSpace(text))return null;
        var parts=text.Split('/');
        if(parts.Length==1&&double.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out var whole)&&whole>0&&whole==Math.Floor(whole)&&whole<long.MaxValue)
            return new PerceptionFrameRate((long)whole,1,source);
        if(parts.Length!=2)return null;
        if(!long.TryParse(parts[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out var num)||!long.TryParse(parts[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out var den))return null;
        if(num<=0||den<=0)return null;
        return new PerceptionFrameRate(num,den,source);
    }

    // ---------- shot cuts (pure) ----------

    private static readonly Regex ShowInfoFrame=new(@"\[Parsed_showinfo_\d+ @ [^\]]*\]\s*n:\s*\d+\s+pts:\s*-?\d+\s+pts_time:\s*(\S+)",RegexOptions.Compiled|RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts the pts_time of every frame showinfo printed after select='gt(scene,θ)'.
    /// Config lines ("config in time_base") and non-numeric pts_time (NOPTS) are skipped.
    /// </summary>
    public static IReadOnlyList<double> ParseShowInfoTimes(string log)
    {
        var times=new List<double>();
        foreach(var line in log.Split('\n'))
        {
            var match=ShowInfoFrame.Match(line);
            if(!match.Success)continue;
            if(double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var t)&&double.IsFinite(t))times.Add(t);
        }
        return times;
    }

    /// <summary>Snaps one time to the nearest frame of <paramref name="rate"/>; <c>Adjusted</c> is true when the time moved.</summary>
    public static ShotCut SnapToFrameGrid(double rawTime,PerceptionFrameRate rate)
    {
        if(!double.IsFinite(rawTime))throw new ArgumentOutOfRangeException(nameof(rawTime),"时间必须是有限数");
        var frame=(long)Math.Round(rawTime*rate.Numerator/rate.Denominator,MidpointRounding.AwayFromZero);
        if(frame<0)frame=0;
        var snapped=(double)frame*rate.Denominator/rate.Numerator;
        var delta=snapped-rawTime;
        var adjusted=Math.Abs(delta)>SnapEpsilon;
        return new ShotCut(snapped,frame,rawTime,adjusted,adjusted?delta:0);
    }

    /// <summary>
    /// Parses showinfo output into frame-snapped cuts, relative to <paramref name="videoStartTime"/>.
    /// Cuts that snap to the same frame are kept once; result is sorted by time.
    /// </summary>
    public static IReadOnlyList<ShotCut> ParseShotCuts(string log,PerceptionFrameRate rate,double videoStartTime=0)
    {
        ArgumentNullException.ThrowIfNull(rate);
        if(!double.IsFinite(videoStartTime))videoStartTime=0;
        var seen=new HashSet<long>();var cuts=new List<ShotCut>();
        foreach(var pts in ParseShowInfoTimes(log))
        {
            var cut=SnapToFrameGrid(pts-videoStartTime,rate);
            if(seen.Add(cut.Frame))cuts.Add(cut);
        }
        cuts.Sort((a,b)=>a.Time.CompareTo(b.Time));
        return cuts;
    }

    // ---------- loudness (pure) ----------

    private static readonly Regex EburFrame=new(@"\[Parsed_ebur128_\d+ @ [^\]]*\]\s*t:\s*(\S+)\s.*?\sM:\s*(-?inf|nan|[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)",RegexOptions.Compiled|RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
    private static readonly Regex EburSpk=new(@"\sSPK:\s*(-?inf|nan|[-+]?\d+(?:\.\d+)?)\s*dBFS",RegexOptions.Compiled|RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
    private static readonly Regex SummaryValue=new(@"^\s*(I|Peak):\s*(-?inf|nan|[-+]?\d+(?:\.\d+)?)\s*(LUFS|dBFS)",RegexOptions.Compiled|RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);

    /// <summary>Momentary samples only (see <see cref="ParseEbuR128"/> for summary values).</summary>
    public static IReadOnlyList<MomentaryLoudness> ParseMomentaryLoudness(string log)=>ParseEbuR128(log).Samples;

    /// <summary>
    /// Parses <c>ebur128=peak=sample:framelog=info</c> stderr. Momentary values at or below -70 LUFS,
    /// -inf or nan become null (silence/no measurement), never a substituted number.
    /// Integrated loudness at the gate (-70.0 on silence) is null too. Sample peak comes from the
    /// Summary "Peak:" line, else the largest per-frame SPK; -inf stays null.
    /// </summary>
    public static EbuR128Log ParseEbuR128(string log)
    {
        var samples=new List<MomentaryLoudness>();
        double? frameSpk=null,summaryPeak=null,integrated=null;
        bool inSummary=false,inPeakSection=false,summaryPeakSeen=false;
        foreach(var raw in log.Split('\n'))
        {
            var line=raw.TrimEnd('\r');
            if(line.Contains("Parsed_ebur128",StringComparison.Ordinal)&&line.Contains("Summary:",StringComparison.Ordinal)){inSummary=true;continue;}
            if(!inSummary)
            {
                var match=EburFrame.Match(line);
                if(!match.Success)continue;
                if(!double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var t)||!double.IsFinite(t))continue;
                samples.Add(new MomentaryLoudness(t,Gated(ParseLevel(match.Groups[2].Value))));
                var spk=EburSpk.Match(line);
                if(spk.Success&&ParseLevel(spk.Groups[1].Value) is double s&&double.IsFinite(s))frameSpk=frameSpk is null?s:Math.Max(frameSpk.Value,s);
                continue;
            }
            var trimmed=line.Trim();
            // Section headers ("Integrated loudness:", "Loudness range:", "Sample peak:", "True peak:") end with ':'.
            if(trimmed.EndsWith(':')){inPeakSection=trimmed.Equals("Sample peak:",StringComparison.OrdinalIgnoreCase);continue;}
            var value=SummaryValue.Match(line);
            if(!value.Success)continue;
            var level=ParseLevel(value.Groups[2].Value);
            if(value.Groups[1].Value.Equals("I",StringComparison.OrdinalIgnoreCase)&&integrated is null)integrated=Gated(level);
            else if(value.Groups[1].Value.Equals("Peak",StringComparison.OrdinalIgnoreCase)&&inPeakSection){summaryPeakSeen=true;summaryPeak=level is double p&&double.IsFinite(p)?p:null;}
        }
        return new EbuR128Log(samples,integrated,summaryPeakSeen?summaryPeak:frameSpk);
    }

    private static double? ParseLevel(string text)
    {
        if(text.Equals("-inf",StringComparison.OrdinalIgnoreCase))return double.NegativeInfinity;
        if(text.Equals("inf",StringComparison.OrdinalIgnoreCase))return double.PositiveInfinity;
        if(text.Equals("nan",StringComparison.OrdinalIgnoreCase))return null;
        return double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)?value:null;
    }

    private static double? Gated(double? lufs)=>lufs is double v&&double.IsFinite(v)&&v>SilenceGateLufs?v:null;

    /// <summary>
    /// Buckets momentary samples into [k·w, (k+1)·w) windows and averages them in linear energy
    /// (10^(L/10)); silent (null) samples contribute zero energy. A window with no audible sample
    /// is null. The last window ends at the last sample. Throws on an invalid window.
    /// </summary>
    public static IReadOnlyList<LoudnessEnvelopePoint> AggregateEnvelope(IReadOnlyList<MomentaryLoudness> samples,double windowSec)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if(ValidateWindow(windowSec) is string error)throw new ArgumentOutOfRangeException(nameof(windowSec),error);
        var usable=samples.Where(s=>double.IsFinite(s.T)&&s.T>=0).ToList();
        if(usable.Count==0)return [];
        var buckets=(int)Math.Floor(usable.Max(s=>s.T)/windowSec)+1;
        var energy=new double[buckets];var counts=new int[buckets];var audible=new int[buckets];var max=new double?[buckets];
        foreach(var s in usable)
        {
            var k=Math.Min(buckets-1,(int)Math.Floor(s.T/windowSec));
            counts[k]++;
            if(s.Lufs is not double l)continue;
            audible[k]++;energy[k]+=Math.Pow(10,l/10);
            max[k]=max[k] is null?l:Math.Max(max[k]!.Value,l);
        }
        var points=new List<LoudnessEnvelopePoint>(buckets);
        for(var k=0;k<buckets;k++)
        {
            double? lufs=audible[k]==0?null:Math.Round(10*Math.Log10(energy[k]/counts[k]),2);
            points.Add(new LoudnessEnvelopePoint(Math.Round(k*windowSec,6),Math.Round((k+1)*windowSec,6),lufs,max[k],counts[k]));
        }
        return points;
    }

    /// <summary>Loudest momentary value in LUFS (not a sample peak); null when everything is silent.</summary>
    public static double? MomentaryPeak(IReadOnlyList<MomentaryLoudness> samples)
    {
        double? peak=null;
        foreach(var s in samples)if(s.Lufs is double l)peak=peak is null?l:Math.Max(peak.Value,l);
        return peak;
    }

    // ---------- process runner (reuses MediaTools paths + Start) ----------

    public async Task<PerceptionStreamInfo> ProbeAsync(string path,CancellationToken token)
    {
        var (exit,stdout,stderr)=await RunAsync(media.Ffprobe,["-v","error","-show_entries","stream=index,codec_type,avg_frame_rate,r_frame_rate,time_base,start_time:format=duration","-of","json",path],token);
        if(exit!=0)throw new InvalidOperationException($"ffprobe 失败 ({exit}): {Tail(stderr)}");
        try{return ParseProbeJson(stdout);}
        catch(JsonException ex){throw new InvalidOperationException($"ffprobe 输出无法解析: {ex.Message}");}
    }

    public async Task<ShotDetectionResult> DetectShotsAsync(string path,double threshold,CancellationToken token)
    {
        if(ValidateThreshold(threshold) is string error)throw new ArgumentOutOfRangeException(nameof(threshold),error);
        var info=await ProbeAsync(path,token);
        if(!info.HasVideo)throw new InvalidOperationException("源文件没有视频流，无法检测镜头切点");
        if(info.FrameRate is null)throw new InvalidOperationException("ffprobe 没有给出可用的帧率（avg_frame_rate 与 r_frame_rate 均不可用），无法吸附到帧栅格");
        var filter=$"select='gt(scene,{threshold.ToString("R",CultureInfo.InvariantCulture)})',showinfo";
        // -copyts (R51-B): keep demuxer timestamps so showinfo pts_time shares ffprobe's start_time clock. Without it the
        // CLI already subtracts the format start_time and ParseShotCuts would subtract the video start a second time
        // (hard-cut.mp4 remuxed to MPEG-TS with +10 s offset: real cut 5.0 s reported as pts 5.0 → 5.0-11.4 → clamped to frame 0).
        var (exit,_,stderr)=await RunAsync(media.Ffmpeg,["-hide_banner","-nostdin","-nostats","-loglevel","info","-copyts","-i",path,"-map","0:v:0","-an","-sn","-dn","-vf",filter,"-f","null","-"],token);
        if(exit!=0)throw new InvalidOperationException($"ffmpeg 镜头检测失败 ({exit}): {Tail(stderr)}");
        return new ShotDetectionResult(threshold,info.FrameRate,ParseShotCuts(stderr,info.FrameRate,info.VideoStartTime));
    }

    public async Task<LoudnessResult> MeasureLoudnessAsync(string path,double window,CancellationToken token)
    {
        if(ValidateWindow(window) is string error)throw new ArgumentOutOfRangeException(nameof(window),error);
        var info=await ProbeAsync(path,token);
        if(!info.HasAudio)return new LoudnessResult(window,false,[],null,null,null);
        var (exit,_,stderr)=await RunAsync(media.Ffmpeg,["-hide_banner","-nostdin","-nostats","-loglevel","info","-i",path,"-map","0:a:0","-vn","-sn","-dn","-af","ebur128=peak=sample:framelog=info","-f","null","-"],token);
        if(exit!=0)throw new InvalidOperationException($"ffmpeg 响度测量失败 ({exit}): {Tail(stderr)}");
        var log=ParseEbuR128(stderr);
        return new LoudnessResult(window,true,AggregateEnvelope(log.Samples,window),MomentaryPeak(log.Samples),log.IntegratedLufs,log.SamplePeakDbfs);
    }

    private Task<(int Exit,string Stdout,string Stderr)> RunAsync(string executable,IReadOnlyList<string> args,CancellationToken token)=>runner(executable,args,token);

    /// <summary>Starts the tool via <see cref="MediaTools.Start"/>, drains stdout/stderr concurrently, kills it on cancellation.</summary>
    private static async Task<(int Exit,string Stdout,string Stderr)> RunProcessAsync(string executable,IReadOnlyList<string> args,CancellationToken token)
    {
        using var process=MediaTools.Start(executable,args);
        var stdout=process.StandardOutput.ReadToEndAsync(token);
        var stderr=process.StandardError.ReadToEndAsync(token);
        try{await process.WaitForExitAsync(token);}
        catch(OperationCanceledException){try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}throw;}
        return (process.ExitCode,await stdout,await stderr);
    }

    private static string Tail(string text)=>text.Length<=1200?text:text[^1200..];
}
