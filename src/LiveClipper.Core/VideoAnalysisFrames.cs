using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LiveClipper.Core;

/// <summary>One sampled still. TimeSeconds is the requested seek time on the source timeline (not a frame-exact timestamp).</summary>
public sealed record VideoAnalysisFrame(double TimeSeconds,string MimeType,byte[] Data);

/// <summary>
/// Extracts a small, bounded set of JPEG stills from one video via ffmpeg (image2pipe, nothing written to disk).
/// These are sampled pictures only: not frame-by-frame analysis, no audio, no transcription, no model judgement.
/// The caller supplies a path resolved from a real clip; this class never scans directories.
/// </summary>
public sealed class VideoAnalysisFrameSampler(MediaTools media)
{
    public const int MaxFrames=12;
    public const int MaxLongEdge=768;
    public const int MaxFrameBytes=512*1024;
    public const int MaxTotalBytes=5*1024*1024;
    /// <summary>Samples are spaced at least this far apart, so very short clips yield fewer frames, never duplicate times.</summary>
    public const double MinSpacingSeconds=0.5;
    private static readonly TimeSpan FrameTimeout=TimeSpan.FromSeconds(30);
    private const int StderrKeepChars=1200;

    /// <summary>
    /// count=min(12,max(1,floor(duration/0.5))); time_i=(i+0.5)*duration/count (segment midpoints, rounded to ms),
    /// so no sample sits on t=0 or at the end where ffmpeg may return nothing. Each frame is one `-ss t -i` run
    /// (input seek, decoded forward to t); precision is bounded by container seek + decode, not guaranteed frame-exact.
    /// Output keeps aspect ratio, long edge ≤768, never upscaled; portrait is not stretched.
    /// Any frame failing (ffmpeg non-zero, timeout, no bytes, invalid JPEG, size cap) throws; partial results are never returned.
    /// </summary>
    public async Task<IReadOnlyList<VideoAnalysisFrame>> SampleAsync(string videoPath,double durationSeconds,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(videoPath))throw new ArgumentException("视频路径为空。",nameof(videoPath));
        if(!double.IsFinite(durationSeconds)||durationSeconds<=0)throw new ArgumentOutOfRangeException(nameof(durationSeconds),"视频时长必须是有限正数。");
        if(!File.Exists(videoPath))throw new FileNotFoundException("视频文件不存在。",videoPath);
        token.ThrowIfCancellationRequested();

        var count=(int)Math.Min(MaxFrames,Math.Max(1,Math.Floor(durationSeconds/MinSpacingSeconds)));
        var frames=new List<VideoAnalysisFrame>(count);
        long total=0;
        for(var i=0;i<count;i++)
        {
            var time=Math.Round((i+0.5)*durationSeconds/count,3,MidpointRounding.AwayFromZero);
            var data=await ExtractOneAsync(videoPath,time,token);
            total+=data.Length;
            if(total>MaxTotalBytes)throw new InvalidOperationException($"抽帧总字节超过上限 {MaxTotalBytes}。");
            frames.Add(new VideoAnalysisFrame(time,"image/jpeg",data));
        }
        if(frames.Count==0)throw new InvalidOperationException("未抽到任何画面。");
        return frames;
    }

    private async Task<byte[]> ExtractOneAsync(string videoPath,double time,CancellationToken token)
    {
        string[] args=
        [
            "-hide_banner","-loglevel","error","-nostdin",
            "-ss",time.ToString("0.###",CultureInfo.InvariantCulture),
            "-i",videoPath,
            "-map","0:v:0","-frames:v","1","-an","-sn","-dn",
            "-vf",$"scale='min({MaxLongEdge},iw)':'min({MaxLongEdge},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2",
            "-c:v","mjpeg","-pix_fmt","yuvj420p","-q:v","5",
            "-f","image2pipe","pipe:1"
        ];
        using var p=MediaTools.Start(media.Ffmpeg,args);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(FrameTimeout);
        var image=new MemoryStream();
        var stderrText=new StringBuilder();
        var exceeded=false;
        // Both pipes are drained concurrently so neither can block ffmpeg.
        var outTask=Task.Run(async()=>
        {
            var buffer=new byte[64*1024];
            int n;
            while((n=await p.StandardOutput.BaseStream.ReadAsync(buffer,timeout.Token))>0)
            {
                if(image.Length+n>MaxFrameBytes){exceeded=true;KillTree(p);return;}
                image.Write(buffer,0,n);
            }
        });
        var errTask=Task.Run(async()=>
        {
            var buffer=new char[4096];
            int n;
            while((n=await p.StandardError.ReadAsync(buffer.AsMemory(),timeout.Token))>0)
                if(stderrText.Length<StderrKeepChars)stderrText.Append(buffer,0,Math.Min(n,StderrKeepChars-stderrText.Length));
        });
        try
        {
            await Task.WhenAll(outTask,errTask);
            await p.WaitForExitAsync(timeout.Token);
        }
        catch(OperationCanceledException)
        {
            KillTree(p);
            await p.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outTask,errTask).ContinueWith(_=>{},CancellationToken.None);
            if(token.IsCancellationRequested)throw;
            throw new TimeoutException($"ffmpeg 抽帧超时（{FrameTimeout.TotalSeconds:0}s），t={time:0.###}s。");
        }
        if(exceeded)
        {
            await p.WaitForExitAsync(CancellationToken.None);
            throw new InvalidOperationException($"t={time:0.###}s 的画面超过单张上限 {MaxFrameBytes} 字节。");
        }
        if(p.ExitCode!=0)throw new InvalidOperationException($"ffmpeg 抽帧失败 ({p.ExitCode})，t={time:0.###}s: {stderrText}");
        var data=image.ToArray();
        if(data.Length<4||data[0]!=0xFF||data[1]!=0xD8||data[^2]!=0xFF||data[^1]!=0xD9)
            throw new InvalidOperationException($"t={time:0.###}s 没有得到有效 JPEG（{data.Length} 字节）。{stderrText}");
        return data;
    }

    private static void KillTree(Process p)
    {
        try { if(!p.HasExited)p.Kill(true); } catch(InvalidOperationException) { }
    }
}
