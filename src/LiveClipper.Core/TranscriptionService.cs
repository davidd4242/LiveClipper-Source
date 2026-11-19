using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LiveClipper.Domain;

namespace LiveClipper.Core;

/// <summary>转译进程长时间没有任何进展（进度与输出都停住）时抛出，由上层决定自动重跑。</summary>
public sealed class TranscriptionStalledException(string message):Exception(message);

/// <summary>入队结果：Started 表示新建并调度，不代表 worker 已开始执行。</summary>
public sealed record TranscriptionReceipt(string JobId,string Disposition,bool Started)
{
    public const string Queued="queued";
    public const string AlreadyPending="already-pending";
}

/// <summary>
/// 本地 faster-whisper 转译管线。三件事：
/// 1) 长录播（默认超过 30 分钟）先用 FFmpeg 切成 30 分钟一段的音频，逐段转译后合并成一份 JSON/SRT；
/// 2) 运行中持续写入进度、耗时与当前分段，界面据此显示实时耗时；
/// 3) 进度长时间不动时自动重跑（默认重试 2 次），用户也可以用「重新转译」随时中断重来。
/// </summary>
public sealed class TranscriptionService(Store store,LiveSettings settings,MediaTools media)
{
    /// <summary>超过该时长（秒）的录播先切片再转译，默认 30 分钟。</summary>
    public const double DefaultChunkSeconds=1800;
    /// <summary>进度长时间没有变化的判定阈值（秒），默认 15 分钟。</summary>
    public const double DefaultStallSeconds=900;
    private const int MaxAutoRetries=2;
    private readonly SemaphoreSlim _worker=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private readonly List<Task> _tasks=[];
    private readonly ConcurrentDictionary<string,CancellationTokenSource> _running=new(StringComparer.Ordinal);
    /// <summary>某个视频的转录文本已写入数据库，可用于自动同步编辑器与后续自动分析。</summary>
    public event Action<Clip>? Completed;

    /// <summary>切片阈值（秒）；可用 LIVECLIPPER_TRANSCRIBE_CHUNK_SECONDS 覆盖，便于测试与小文件验证。</summary>
    public static double ChunkSeconds=>EnvSeconds("LIVECLIPPER_TRANSCRIBE_CHUNK_SECONDS",DefaultChunkSeconds);
    /// <summary>无进度阈值（秒）；可用 LIVECLIPPER_TRANSCRIBE_STALL_SECONDS 覆盖。</summary>
    public static double StallSeconds=>EnvSeconds("LIVECLIPPER_TRANSCRIBE_STALL_SECONDS",DefaultStallSeconds);
    private static double EnvSeconds(string name,double fallback)
    {
        var value=Environment.GetEnvironmentVariable(name);
        return double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var parsed)&&parsed>0?parsed:fallback;
    }

    private static Job NewJob(Clip clip,int attempt)=>new(Guid.NewGuid().ToString("N"),"transcribe",clip.EventId,"pending",0,null,RetryCount:attempt);

    public void Queue(Clip clip)=>QueueReceipt(clip);

    public TranscriptionReceipt QueueReceipt(Clip clip)
    {
        var job=NewJob(clip,0);
        Schedule(job,clip);
        return new(job.Id,TranscriptionReceipt.Queued,true);
    }

    // Coordinates restart with worker entry/exit; Queue/ResumePending keep their existing boundaries.
    private readonly object _queueGate=new();

    /// <summary>中断该视频正在进行的转译并重新排队；仅有 pending 时沿用原任务。</summary>
    public void Restart(Clip clip)=>RestartReceipt(clip);

    public TranscriptionReceipt RestartReceipt(Clip clip)
    {
        lock(_queueGate)
        {
            var cancelled=false;
            if(_running.TryGetValue(clip.EventId,out var running)&&!running.IsCancellationRequested){cancelled=true;try{running.Cancel();}catch(ObjectDisposedException){}}
            // 取消正在执行的任务必须补排；没有取消时沿用真实 pending 行，不再次调度。
            var pending=store.PendingJobs("transcribe").FirstOrDefault(x=>x.EventId==clip.EventId);
            if(!cancelled&&pending is not null)return new(pending.Id,TranscriptionReceipt.AlreadyPending,false);
            return QueueReceipt(clip);
        }
    }

    private sealed record OutputRoots(string Results,string Work);
    private void Schedule(Job job,Clip clip,OutputRoots? roots=null)
    {
        roots??=new OutputRoots(string.IsNullOrWhiteSpace(settings.ExportOutputDirectory)?Path.GetFullPath(settings.StorageRoot):RenderStoragePaths.NormalizeDirectory(settings.ExportOutputDirectory),RenderStoragePaths.WorkRoot(settings.StorageRoot,settings.RenderWorkDirectory));
        var captured=roots;
        lock(_tasks)
        {
            store.SaveJob(job);
            _tasks.Add(Task.Run(()=>RunAsync(job,clip,captured)));
        }
    }

    public void ResumePending()
    {
        foreach(var job in store.PendingJobs("transcribe"))
        {
            var clip=store.Clips().FirstOrDefault(x=>x.EventId==job.EventId);
            if(clip is not null)Schedule(job,clip);
        }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();Task[] tasks;
        while(true)
        {
            lock(_tasks)tasks=_tasks.ToArray();
            // A stalled parent can register a retry before settling; drain it even if another task faults.
            try {await Task.WhenAll(tasks);}catch(Exception) { }
            lock(_tasks)if(tasks.Length==_tasks.Count)break;
        }
        try {await Task.WhenAll(tasks);}catch(OperationCanceledException) { }
    }

    private async Task RunAsync(Job job,Clip clip,OutputRoots roots)
    {
        var entered=false;string? work=null;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            await _worker.WaitAsync(linked.Token);entered=true;
            lock(_queueGate)
            {
                linked.Token.ThrowIfCancellationRequested();
                // Semaphore waiters are pending, not running; restart must reuse their saved row.
                _running[clip.EventId]=linked;
                store.SaveJob(job=job with{Status="running",Progress=.05,Message=null});
            }
            var destination=Path.Combine(roots.Results,"transcripts",clip.Id);
            work=Path.Combine(roots.Work,"transcription-audio",job.Id);Directory.CreateDirectory(work);
            var output=Path.Combine(work,"result");Directory.CreateDirectory(output);
            var summary=await TranscribeAsync(clip,output,work,job,linked.Token);
            var srt=Path.Combine(output,"transcript.srt");var json=Path.Combine(output,"transcript.json");
            if(!File.Exists(srt)||!File.Exists(json))throw new InvalidOperationException("转录脚本没有生成 SRT 和 JSON");
            store.SaveJob(job=job with{Progress=.97,Message=summary});
            var transcript=StampMediaTimebase(await File.ReadAllTextAsync(json,linked.Token));
            await File.WriteAllTextAsync(json,transcript,linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            PublishTranscript(clip.EventId,transcript,output,destination,work);
            store.SaveJob(job with{Status="completed",Progress=1,Message=summary});
            try{Completed?.Invoke(clip);}catch(Exception ex){store.SaveJob(job with{Status="completed",Progress=1,Error="转录后自动处理失败："+ex.Message});}
        }
        catch(OperationCanceledException)
        {
            // 软件退出时保持 pending（下次启动续跑）；用户主动中断则标记 interrupted，避免与新任务重复排队。
            var resumable=_stop.IsCancellationRequested;
            store.SaveJob(job with{Status=resumable?"pending":"interrupted",Message=resumable?"软件退出，下次启动继续转译":"已中断，重新排队中"});
        }
        catch(TranscriptionStalledException ex)
        {
            if(job.RetryCount<MaxAutoRetries)
            {
                store.SaveJob(job with{Status="retried",Message=$"进度停在 {Math.Round(job.Progress*100)}% 超过 {FormatSeconds(StallSeconds)}，已自动重新排队（第 {job.RetryCount+1} 次重试）"});
                await Task.Delay(TimeSpan.FromSeconds(3));
                Schedule(NewJob(clip,job.RetryCount+1),clip,roots);
            }
            else store.SaveJob(job with{Status="failed",Error=$"转译长时间没有进度（{ex.Message}），已自动重试 {job.RetryCount} 次仍未完成。可点击「重新转译」再次尝试。"});
        }
        catch(Exception ex){store.SaveJob(job with{Status="failed",Error=ex.Message});}
        finally
        {
            lock(_queueGate)
            {
                if(_running.TryGetValue(clip.EventId,out var current)&&ReferenceEquals(current,linked))_running.TryRemove(clip.EventId,out _);
            }
            if(work is not null)TryDeleteDirectory(work);
            if(entered)_worker.Release();
        }
    }

    /// <summary>转写主体：短视频直接整段转译；长录播先切片，逐段转译后再合并成一份字幕。</summary>
    private async Task<string?> TranscribeAsync(Clip clip,string output,string work,Job job,CancellationToken token)
    {
        var script=Environment.GetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT") is { Length: > 0 } overrideScript?overrideScript:Path.Combine(AppContext.BaseDirectory,"ai-worker","transcribe.py");
        void Record(double overall,string? note)=>store.SaveJob(job with{Progress=Math.Clamp(.05+.9*overall,.05,.95),Message=note});
        var duration=await SafeDurationAsync(clip.Path,token);
        var chunkSeconds=ChunkSeconds;
        if(duration<=chunkSeconds+1)
        {
            if(duration>0)store.SaveJob(job with{Message=$"整段转译（时长 {FormatSeconds(duration)}）"});
            var normalized=Path.Combine(work,"source-timeline.wav");
            await PrepareInputAsync(clip.Path,normalized,duration,token);
            await RunWorkerAsync(script,normalized,output,v=>Record(v,null),token);
            return duration>0?$"转译完成（时长 {FormatSeconds(duration)}）":"转译完成";
        }
        store.SaveJob(job with{Message=$"录播时长 {FormatSeconds(duration)}，正在按 {FormatSeconds(chunkSeconds)} 切片…"});
        var chunks=await SplitAsync(clip.Path,work,chunkSeconds,token);
        var parts=new List<TranscriptMerge.Part>();
        var offset=0d;
        for(var i=0;i<chunks.Count;i++)
        {
            var index=i;
            var chunkDuration=await SafeDurationAsync(chunks[i],token);
            store.SaveJob(job with{Message=$"分段 {i+1}/{chunks.Count} 转译中",Progress=Math.Clamp(.05+.9*index/chunks.Count,.05,.95)});
            var chunkOutput=Path.Combine(work,"chunk-results",Path.GetFileNameWithoutExtension(chunks[i]));
            await RunWorkerAsync(script,chunks[i],chunkOutput,v=>Record((index+v)/chunks.Count,$"分段 {index+1}/{chunks.Count} 转译中"),token);
            var json=Path.Combine(chunkOutput,"transcript.json");
            if(!File.Exists(json))throw new InvalidOperationException($"第 {i+1}/{chunks.Count} 段没有生成转译结果");
            parts.Add(new TranscriptMerge.Part(offset,await File.ReadAllTextAsync(json,token)));
            offset+=chunkDuration>0?chunkDuration:chunkSeconds;
        }
        var merged=TranscriptMerge.Merge(parts);
        await File.WriteAllTextAsync(Path.Combine(output,"transcript.json"),merged.Json,token);
        await File.WriteAllTextAsync(Path.Combine(output,"transcript.srt"),merged.Srt,token);
        TryDeleteDirectory(Path.Combine(work,"chunks"));
        return $"长录播已按 {FormatSeconds(chunkSeconds)} 切片 {chunks.Count} 段转译，合并为 {merged.SegmentCount} 条字幕";
    }

    /// <summary>把录播切成等长音频分段；分片是临时产物，成功合并后会删除。</summary>
    internal async Task<IReadOnlyList<string>> SplitAsync(string input,string output,double chunkSeconds,CancellationToken token)
    {
        var directory=Path.Combine(output,"chunks");Directory.CreateDirectory(directory);
        foreach(var stale in Directory.GetFiles(directory,"chunk_*.wav"))TryDeleteFile(stale);
        var pattern=Path.Combine(directory,"chunk_%03d.wav");
        // 直接解码成 Whisper 需要的 16 kHz 单声道 PCM，切片的同时完成重采样。
        var duration=await SafeDurationAsync(input,token);
        var args=AudioInputArguments(input,duration).Concat(new[]{"-f","segment","-segment_time",chunkSeconds.ToString("0.###",CultureInfo.InvariantCulture),"-reset_timestamps","1",pattern});
        await media.RunAsync(media.Ffmpeg,args,token);
        var chunks=Directory.GetFiles(directory,"chunk_*.wav").OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        if(chunks.Length==0)throw new InvalidOperationException("长录播切片失败：没有生成任何音频分段");
        return chunks;
    }

    private void PublishTranscript(string eventId,string transcript,string staged,string destination,string work)
    {
        var existed=Directory.Exists(destination);Directory.CreateDirectory(destination);
        var installed=new List<string>();var backups=new List<(string Original,string Backup)>();
        try
        {
            foreach(var name in new[]{"transcript.json","transcript.srt"})
            {
                var target=Path.Combine(destination,name);
                if(File.Exists(target))
                {
                    var backup=Path.Combine(work,"previous-"+name);File.Move(target,backup);backups.Add((target,backup));
                }
                File.Move(Path.Combine(staged,name),target);installed.Add(target);
            }
            store.SaveTranscript(eventId,transcript,Path.Combine(destination,"transcript.srt"));
        }
        catch
        {
            foreach(var path in installed)File.Delete(path);
            foreach(var (original,backup) in backups)File.Move(backup,original);
            if(!existed&&!Directory.EnumerateFileSystemEntries(destination).Any())Directory.Delete(destination);
            throw;
        }
    }

    internal static string StampMediaTimebase(string json)
    {
        var node=JsonNode.Parse(json)?.AsObject()??throw new InvalidOperationException("转录结果不是JSON对象");
        node["audioTimebase"]="media-pts-v1";
        return node.ToJsonString();
    }

    internal Task<string> PrepareInputAsync(string input,string output,double duration,CancellationToken token)=>
        media.RunAsync(media.Ffmpeg,AudioInputArguments(input,duration).Concat(new[]{"-f","wav",output}),token);
    internal static string[] AudioInputArguments(string input,double duration)
    {
        var filter=TimelineAudio.PreserveTimestampsFilter;
        if(double.IsFinite(duration)&&duration>0)filter+=",apad,atrim=duration="+duration.ToString("R",CultureInfo.InvariantCulture);
        return ["-hide_banner","-loglevel","error","-y","-i",input,"-map","0:a:0","-vn","-af",filter,"-ac","1","-ar","16000","-c:a","pcm_s16le"];
    }

    /// <summary>
    /// 跑一次 transcribe.py：逐行读 stderr 更新进度，同时看住「既没有进度也没有输出」的卡死情况。
    /// </summary>
    private async Task RunWorkerAsync(string script,string input,string output,Action<double> onProgress,CancellationToken token)
    {
        var errors=new StringBuilder();
        var lastActivity=DateTimeOffset.UtcNow.UtcTicks;
        using var process=MediaTools.Start(settings.PythonPath,[script,input,output,"--model",settings.WhisperModel]);
        var stderr=Task.Run(async()=>{
            while(await process.StandardError.ReadLineAsync(token) is { } line)
            {
                Interlocked.Exchange(ref lastActivity,DateTimeOffset.UtcNow.UtcTicks);
                if(line.StartsWith("PROGRESS ",StringComparison.Ordinal)
                    &&double.TryParse(line.AsSpan(9),NumberStyles.Float,CultureInfo.InvariantCulture,out var value))
                    onProgress(Math.Clamp(value,0,.999));
                else lock(errors){if(errors.Length<4000)errors.AppendLine(line);}
            }
        },token);
        var stdout=process.StandardOutput.ReadToEndAsync(token);
        var exit=process.WaitForExitAsync(token);
        var limit=TimeSpan.FromSeconds(StallSeconds);
        try
        {
            while(!exit.IsCompleted)
            {
                var finished=await Task.WhenAny(exit,Task.Delay(TimeSpan.FromSeconds(5),token));
                if(finished==exit)break;
                var idle=DateTimeOffset.UtcNow-new DateTimeOffset(Interlocked.Read(ref lastActivity),TimeSpan.Zero);
                if(idle>limit)
                {
                    try{process.Kill(true);}catch(Exception){}
                    throw new TranscriptionStalledException($"{FormatSeconds(idle.TotalSeconds)}没有新的进度");
                }
            }
            await exit;
        }
        catch(OperationCanceledException)
        {
            try{if(!process.HasExited)process.Kill(true);}catch(Exception){}
            throw;
        }
        finally
        {
            try{await Task.WhenAll(stderr,stdout);}catch(OperationCanceledException){}
        }
        if(process.ExitCode!=0)throw new InvalidOperationException("转写脚本失败："+Tail(errors.ToString(),800));
    }

    private async Task<double> SafeDurationAsync(string path,CancellationToken token)
    {
        try{return await media.DurationAsync(path,token);}
        catch(OperationCanceledException){throw;}
        catch{return 0;}
    }

    private static string Tail(string text,int length)=>text.Length<=length?text:text[^length..];
    private static void TryDeleteFile(string path)
    {
        try{if(File.Exists(path)){File.SetAttributes(path,FileAttributes.Normal);File.Delete(path);}}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private static void TryDeleteDirectory(string path)
    {
        try{if(Directory.Exists(path))Directory.Delete(path,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    /// <summary>把秒数写成界面上的中文时长（30 分钟 / 2 小时 5 分）。</summary>
    public static string FormatSeconds(double seconds)
    {
        var total=Math.Max(0,(int)Math.Round(seconds));
        if(total<60)return $"{total} 秒";
        if(total<3600)return total%60==0?$"{total/60} 分钟":$"{total/60} 分 {total%60} 秒";
        return total%3600==0?$"{total/3600} 小时":$"{total/3600} 小时 {total%3600/60} 分";
    }
}
