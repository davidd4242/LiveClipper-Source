using System.Globalization;
using System.Diagnostics;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed class VideoExportService(Store store,MediaTools media)
{
    private readonly SemaphoreSlim _worker=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private readonly List<Task> _tasks=[];
    public static bool ValidOptions(int bitrateKbps,int resolution,int fps)=>(bitrateKbps is 0 or 1500 or 2500 or 4000 or 6000 or 8000)
        && (resolution is 0 or 480 or 720 or 1080) && (fps is 0 or 24 or 30 or 60);
    /// <summary>
    /// 导出文件基名：高光素材用规范目录名（{主播}-{直播日期}-高光时刻[序号]，旧目录自动换算），
    /// 本地导入的录播用原文件名；两者都没有时退回内部 ID 前 8 位。
    /// </summary>
    public static string ExportBaseName(Clip clip)
        =>OutputNaming.HighlightBase(clip)??OutputNaming.SafeName(Path.GetFileNameWithoutExtension(clip.Path))??clip.Id[..8];
    public VideoExport Queue(Clip clip,int bitrateKbps,int resolution,int fps)
    {
        if(!ValidOptions(bitrateKbps,resolution,fps))throw new ArgumentException("视频导出参数无效");
        if(bitrateKbps==0&&resolution==0&&fps==0)throw new ArgumentException("原始画质可以直接下载 MP4");
        if(!media.Available())throw new InvalidOperationException("请先安装或设置 FFmpeg");
        var id=Guid.NewGuid().ToString("N");
        // 导出文件名与剪辑导出一致：{主播}-{直播日期}-高光时刻切片-{导出日期}.mp4；同目录重名时自动追加 -2、-3。
        var exportDirectory=Path.Combine(Path.GetDirectoryName(clip.Path)!,"exports");
        Directory.CreateDirectory(exportDirectory);
        var output=OutputNaming.UniquePath(exportDirectory,$"{ExportBaseName(clip)}{OutputNaming.ClipSuffix}-{OutputNaming.Date(DateTimeOffset.UtcNow)}",".mp4",store.VideoExports().Select(x=>x.OutputPath));
        var request=new VideoExport(id,clip.Id,clip.EventId,output,bitrateKbps,resolution,fps,DateTimeOffset.UtcNow);
        store.SaveVideoExport(request);
        var job=new Job(id,"video-export",clip.EventId,"pending",0,null);
        store.SaveJob(job);Schedule(job,request,clip);
        return request;
    }
    public void ResumePending()
    {
        var exports=store.VideoExports();var clips=store.Clips();
        foreach(var job in store.PendingJobs("video-export"))
        {
            var request=exports.FirstOrDefault(x=>x.Id==job.Id);
            var clip=request is null?null:clips.FirstOrDefault(x=>x.Id==request.ClipId);
            if(request is not null&&clip is not null)Schedule(job,request,clip);
            else store.SaveJob(job with {Status="failed",Error="找不到待导出的视频"});
        }
    }
    private void Schedule(Job job,VideoExport request,Clip clip)
    {
        var task=Task.Run(()=>RunAsync(job,request,clip));
        lock(_tasks)_tasks.Add(task);
    }
    public async Task StopAsync()
    {
        _stop.Cancel();Task[] tasks;lock(_tasks)tasks=_tasks.ToArray();
        try{await Task.WhenAll(tasks);}catch(OperationCanceledException){}
    }
    private async Task RunAsync(Job job,VideoExport request,Clip clip)
    {
        var entered=false;var completed=false;Process? process=null;
        try
        {
            await _worker.WaitAsync(_stop.Token);entered=true;
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            store.SaveJob(job=job with {Status="running",Progress=0.02,Error=null});
            var duration=await media.DurationAsync(clip.Path,_stop.Token);
            var args=new List<string>{"-hide_banner","-loglevel","error","-y","-i",clip.Path};
            if(request.Resolution>0)args.AddRange(["-vf",$"scale=-2:{request.Resolution}"]);
            if(request.Fps>0)args.AddRange(["-r",request.Fps.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["-c:v","libx264","-preset","veryfast"]);
            if(request.BitrateKbps>0)args.AddRange(["-b:v",$"{request.BitrateKbps}k"]);
            else args.AddRange(["-crf","23"]);
            args.AddRange(["-c:a","aac","-b:a","128k","-movflags","+faststart","-progress","pipe:1","-nostats",request.OutputPath]);
            process=MediaTools.Start(media.Ffmpeg,args);
            var errors=process.StandardError.ReadToEndAsync(_stop.Token);
            var lastProgress=0d;
            while(await process.StandardOutput.ReadLineAsync(_stop.Token) is { } line)
            {
                if(!line.StartsWith("out_time=",StringComparison.Ordinal))continue;
                if(!TimeSpan.TryParse(line[9..],CultureInfo.InvariantCulture,out var time))continue;
                var progress=Math.Clamp(time.TotalSeconds/Math.Max(1,duration),0,0.95);
                if(progress-lastProgress<0.02)continue;
                lastProgress=progress;store.SaveJob(job=job with {Status="running",Progress=progress});
            }
            await process.WaitForExitAsync(_stop.Token);
            var error=await errors;
            if(process.ExitCode!=0)throw new InvalidOperationException($"FFmpeg 导出失败：{error[^Math.Min(error.Length,800)..]}");
            if(!File.Exists(request.OutputPath)||new FileInfo(request.OutputPath).Length==0)throw new InvalidOperationException("FFmpeg 没有生成视频文件");
            store.SaveJob(job with {Status="completed",Progress=1,Error=null});
            completed=true;
        }
        catch(OperationCanceledException)
        {
            store.SaveJob(job with {Status="pending",Progress=0,Error=null});
        }
        catch(Exception ex)
        {
            store.SaveJob(job with {Status="failed",Error=ex.Message});
        }
        finally
        {
            if(process is not null)
            {
                try{if(!process.HasExited)process.Kill(true);}catch{}
                process.Dispose();
            }
            if(entered)_worker.Release();
            if(!completed&&File.Exists(request.OutputPath))try{File.Delete(request.OutputPath);}catch{}
        }
    }
}
