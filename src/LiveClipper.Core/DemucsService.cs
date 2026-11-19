using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed class DemucsService(Store store,StudioStore studio,LiveSettings settings)
{
    private readonly SemaphoreSlim _worker=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private readonly List<Task> _tasks=[];
    public void Queue(Clip clip)
    {
        if(studio.Stems(clip.Id) is { } stems&&File.Exists(stems.VocalsPath))return;
        if(store.Jobs().Any(x=>x.Type=="separate"&&x.EventId==clip.EventId&&x.Status is "pending" or "running"))throw new InvalidOperationException("这个视频正在分离人声");
        var job=new Job(Guid.NewGuid().ToString("N"),"separate",clip.EventId,"pending",0,null);
        store.SaveJob(job);Schedule(job,clip);
    }
    public void ResumePending()
    {
        foreach(var job in store.PendingJobs("separate"))
        {
            var clip=store.Clips().FirstOrDefault(x=>x.EventId==job.EventId);
            if(clip is null)store.SaveJob(job with {Status="failed",Error="找不到源视频"});
            else Schedule(job,clip);
        }
    }
    public async Task StopAsync()
    {
        _stop.Cancel();Task[] jobs;lock(_tasks)jobs=_tasks.ToArray();try{await Task.WhenAll(jobs);}catch(OperationCanceledException){}
    }
    private void Schedule(Job job,Clip clip)
    {
        var task=Task.Run(()=>RunAsync(job,clip));lock(_tasks)_tasks.Add(task);
    }
    private async Task RunAsync(Job job,Clip clip)
    {
        var entered=false;
        try
        {
            await _worker.WaitAsync(_stop.Token);entered=true;
            store.SaveJob(job=job with {Status="running",Progress=.1,Error=null});
            var output=Path.Combine(settings.StorageRoot,"stems",clip.Id);Directory.CreateDirectory(output);
            var media=new MediaTools();
            await media.RunAsync(settings.PythonPath,["-m","demucs","--two-stems=vocals","-n","htdemucs","-o",output,clip.Path],_stop.Token);
            var stemDirectory=Path.Combine(output,"htdemucs",Path.GetFileNameWithoutExtension(clip.Path));
            var voice=Path.Combine(stemDirectory,"vocals.wav");var instrumental=Path.Combine(stemDirectory,"no_vocals.wav");
            if(!File.Exists(voice)||!File.Exists(instrumental))throw new InvalidOperationException("Demucs 没有生成预期的人声与伴奏文件");
            studio.SaveStems(new AudioStems(clip.Id,voice,instrumental));
            store.SaveJob(job with {Status="completed",Progress=1});
        }
        catch(OperationCanceledException){store.SaveJob(job with {Status="pending",Error=null});}
        catch(Exception ex){store.SaveJob(job with {Status="failed",Error=ex.Message+"。请确认所选 Python 已安装 demucs 与 torch"});}
        finally{if(entered)_worker.Release();}
    }
}
