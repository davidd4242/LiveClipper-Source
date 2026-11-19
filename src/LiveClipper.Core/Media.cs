using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed record VideoInfo(int Width,int Height,double Fps,bool HasAudio);

public sealed class MediaTools(string? ffmpegPath=null)
{
    private static readonly string ProbeName="ffprobe"+(OperatingSystem.IsWindows()?".exe":"");
    private string _probe="";
    public string Ffmpeg { get; private set; } = string.IsNullOrWhiteSpace(ffmpegPath)?"ffmpeg":ffmpegPath;
    public void SetFfmpegPath(string path){Ffmpeg=path;_probe="";}
    /// <summary>
    /// ffprobe 的路径。用户可能只配置了 ffmpeg 的绝对路径（且所在目录不在 PATH 里），
    /// 这时同名目录下通常有 ffprobe；找不到就退回 PATH 上的 ffprobe，避免直接抛 Win32Exception。
    /// </summary>
    public string Ffprobe
    {
        get
        {
            if(_probe.Length>0)return _probe;
            var directory=Path.GetDirectoryName(Ffmpeg);
            if(string.IsNullOrEmpty(directory))return _probe=ProbeName;
            var sibling=Path.Combine(directory,ProbeName);
            return _probe=File.Exists(sibling)?sibling:ProbeName;
        }
    }
    public bool Available()
    {
        try { using var p=Process.Start(new ProcessStartInfo(Ffmpeg,"-version"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true});return p?.WaitForExit(3000)==true && p.ExitCode==0; } catch{return false;}
    }
    public static Process Start(string executable,IEnumerable<string> args)
    {
        var info=new ProcessStartInfo(executable){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        return Process.Start(info)??throw new InvalidOperationException($"无法启动 {executable}");
    }
    internal const int FilterScriptThreshold=24000;

    internal FilterScriptArguments PrepareFilterScripts(string executable,IEnumerable<string> args,string? temporaryRoot=null)
    {
        var prepared=new FilterScriptArguments(args.ToArray());
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        if(!string.Equals(executable,Ffmpeg,comparison)||WindowsCommandLineLength(executable,prepared.Arguments)<=FilterScriptThreshold)
            return prepared;
        try
        {
            for(var i=0;i<prepared.Arguments.Length-1;i++)
            {
                var option=prepared.Arguments[i];
                if(option is not ("-vf" or "-af" or "-filter_complex" or "-filter")
                    && !option.StartsWith("-filter:",StringComparison.Ordinal))continue;
                if(prepared.DirectoryPath is null)
                {
                    prepared.DirectoryPath=Path.Combine(temporaryRoot??Path.GetTempPath(),"liveclipper-filters-"+Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(prepared.DirectoryPath);
                }
                var path=Path.Combine(prepared.DirectoryPath,$"filter-{i}.txt");
                // -/ loads the option value verbatim; the old *_script options were removed in FFmpeg 9.
                File.WriteAllText(path,prepared.Arguments[i+1],new System.Text.UTF8Encoding(false));
                prepared.Arguments[i]="-/"+option[1..];
                prepared.Arguments[++i]=path;
            }
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    internal static long WindowsCommandLineLength(string executable,IEnumerable<string> args)
    {
        long length=executable.Length+3L; // Quoted executable and terminating NUL.
        foreach(var arg in args)
        {
            length++;
            if(arg.Length>0&&!arg.Any(c=>char.IsWhiteSpace(c)||c=='"'))
            {
                length+=arg.Length;
                continue;
            }
            length+=2;
            var slashes=0;
            foreach(var c in arg)
            {
                if(c=='\\'){slashes++;continue;}
                length+=c=='"'?slashes*2L+2:slashes+1L;
                slashes=0;
            }
            length+=slashes*2L;
        }
        return length;
    }

    internal sealed class FilterScriptArguments(string[] arguments) : IDisposable
    {
        internal string[] Arguments {get;}=arguments;
        internal string? DirectoryPath {get;set;}
        public void Dispose()
        {
            if(DirectoryPath is not { } directory)return;
            try { Directory.Delete(directory,true); }
            catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }
    }

    public async Task<string> RunAsync(string executable,IEnumerable<string> args,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var prepared=PrepareFilterScripts(executable,args);
        using var p=Start(executable,prepared.Arguments);
        var stdout=p.StandardOutput.ReadToEndAsync(token);var stderr=p.StandardError.ReadToEndAsync(token);
        try { await p.WaitForExitAsync(token); } catch(OperationCanceledException) { if(!p.HasExited)p.Kill(true);await p.WaitForExitAsync(CancellationToken.None);throw; }
        var output=(await stdout)+"\n"+(await stderr);
        if(p.ExitCode!=0)throw new InvalidOperationException($"{Path.GetFileName(executable)} 失败 ({p.ExitCode}): {output[^Math.Min(output.Length,1200)..]}");
        return output;
    }
    /// <summary>只关心输出文本、不关心退出码的调用（例如 `ffmpeg -i file` 用来读元信息）。</summary>
    public async Task<string> CaptureAsync(string executable,IEnumerable<string> args,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var prepared=PrepareFilterScripts(executable,args);
        using var p=Start(executable,prepared.Arguments);
        var stdout=p.StandardOutput.ReadToEndAsync(token);var stderr=p.StandardError.ReadToEndAsync(token);
        try { await p.WaitForExitAsync(token); } catch(OperationCanceledException) { if(!p.HasExited)p.Kill(true);await p.WaitForExitAsync(CancellationToken.None);throw; }
        return (await stdout)+"\n"+(await stderr);
    }
    private static readonly System.Text.RegularExpressions.Regex DurationPattern=
        new(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)",System.Text.RegularExpressions.RegexOptions.Compiled);
    public async Task<double> DurationAsync(string path,CancellationToken token)
    {
        try
        {
            var output=await RunAsync(Ffprobe,["-v","error","-show_entries","format=duration","-of","default=noprint_wrappers=1:nokey=1",path],token);
            if(double.TryParse(output.Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&value>0)return value;
        }
        catch { }
        // ffprobe 不可用时退回 ffmpeg 自报的时长（绿色版/精简版常缺 ffprobe）。
        // `ffmpeg -i` 不带输出会以非 0 退出，但元信息已经打印出来，所以用 CaptureAsync。
        try
        {
            var output=await CaptureAsync(Ffmpeg,["-hide_banner","-i",path],token);
            if(DurationPattern.Match(output) is {Success:true} match
                && double.TryParse(match.Groups[3].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var seconds))
                return int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture)*3600
                    +int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture)*60+seconds;
        }
        catch { }
        return 4;
    }
    public async Task<VideoInfo> ProbeAsync(string path,CancellationToken token)
    {
        var output=await RunAsync(Ffprobe,["-v","error","-show_streams","-of","json",path],token);
        using var doc=JsonDocument.Parse(output);
        var streams=doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.FirstOrDefault(x=>x.GetProperty("codec_type").GetString()=="video");
        if(video.ValueKind==JsonValueKind.Undefined)throw new InvalidOperationException("文件没有视频画面");
        var rate=video.TryGetProperty("avg_frame_rate",out var r)?r.GetString()?.Split('/'):null;
        var fps=rate?.Length==2&&double.TryParse(rate[0],CultureInfo.InvariantCulture,out var num)&&double.TryParse(rate[1],CultureInfo.InvariantCulture,out var den)&&den>0?num/den:30;
        return new(video.GetProperty("width").GetInt32(),video.GetProperty("height").GetInt32(),fps,streams.Any(x=>x.GetProperty("codec_type").GetString()=="audio"));
    }
    public async Task ConcatAsync(IEnumerable<string> files,string output,CancellationToken token)
    {
        var paths=files.ToArray();if(paths.Length==0)throw new InvalidOperationException("所选时间段没有录制分片");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var listPath=Path.Combine(Path.GetDirectoryName(output)!,"concat.txt");
        await File.WriteAllLinesAsync(listPath,paths.Select(x=>"file '"+Path.GetFullPath(x).Replace("\\","/").Replace("'","'\\''")+"'"),token);
        try
        {
            await RunAsync(Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","concat","-safe","0","-i",listPath,"-c","copy","-movflags","+faststart",output],token);
            if(!File.Exists(output)||new FileInfo(output).Length==0)throw new InvalidOperationException("FFmpeg 未生成有效 MP4");
        }
        finally { File.Delete(listPath); }
    }
}

public sealed class SegmentRecorder(Store store,MediaTools media,string storageRoot,double bufferSeconds=1200) : IRecorder
{
    /// <summary>直播回放缓存默认完整保留；仅显式关闭时才按 BufferSeconds 清理。</summary>
    public bool RetainAllSegments {get;set;}=true;
    public string? OwnedCacheRoot {get;set;}
    public bool CacheCapacityExceeded {get;private set;}
    public const long CacheSessionLimitBytes=2L*1024*1024*1024;
    public const long CacheTotalLimitBytes=10L*1024*1024*1024;
    public const long CacheMinimumFreeBytes=2L*1024*1024*1024;
    public double BufferSeconds {get;set;}=bufferSeconds;
    private Process? _process;
    private Task? _monitor;
    private CancellationTokenSource? _stop;
    public Func<MediaSegment,Task>? SegmentReady;
    public event Action<string>? Error;
    public async Task StartAsync(RecordingSession session,StreamInfo stream,CancellationToken token)
    {
        await StopAsync();
        CacheCapacityExceeded=false;
        if(!RetainAllSegments)
        {
            if(string.IsNullOrWhiteSpace(OwnedCacheRoot))throw new InvalidOperationException("高光缓存需要独立目录");
            HighlightCachePaths.Create(OwnedCacheRoot,session.Id);
            CheckActiveCacheCapacity(OwnedCacheRoot,session.Id);
        }
        var directory=Path.Combine(RetainAllSegments?storageRoot:OwnedCacheRoot!,"buffer",session.Id,"run_"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());Directory.CreateDirectory(directory);
        _stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        var args=new[]{"-hide_banner","-loglevel","warning","-rw_timeout","15000000","-headers","Referer: https://live.bilibili.com/\r\nUser-Agent: Mozilla/5.0\r\n","-i",stream.Url,"-map","0","-c","copy","-f","segment","-segment_time","4","-segment_format","mpegts","-reset_timestamps","1",Path.Combine(directory,"segment_%09d.ts")};
        var info=new ProcessStartInfo(media.Ffmpeg){RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        _process=Process.Start(info)??throw new InvalidOperationException("无法启动 FFmpeg");
        var errors=_process.StandardError.ReadToEndAsync(_stop.Token);
        _monitor=MonitorAsync(session,directory,errors,_stop.Token);
    }
    private async Task MonitorAsync(RecordingSession session,string directory,Task<string> errors,CancellationToken token)
    {
        var seen=new HashSet<string>();var last=store.LastSegment(session.Id);var sequence=last?.Sequence??0;var lastEnd=last?.End??0d;var lastProgress=DateTimeOffset.UtcNow;
        async Task IngestAsync(string path,CancellationToken ct)
        {
            if(!seen.Add(path)||new FileInfo(path).Length==0)return;
            var duration=await media.DurationAsync(path,ct);
            var start=Math.Max(lastEnd,(DateTimeOffset.UtcNow-session.StartedAt).TotalSeconds-duration);
            var end=start+duration;
            var segment=new MediaSegment(Guid.NewGuid().ToString("N"),session.Id,++sequence,start,end,path,false);
            await store.AddAsync(segment,ct);lastEnd=end;lastProgress=DateTimeOffset.UtcNow;
            if(SegmentReady is not null)await SegmentReady(segment);
            if(!RetainAllSegments)await store.CleanAsync(session.Id,lastEnd,BufferSeconds,ct,OwnedCacheRoot);
        }
        try
        {
            while(!token.IsCancellationRequested)
            {
                var paths=Directory.GetFiles(directory,"segment_*.ts").OrderBy(x=>x,StringComparer.Ordinal).ToArray();
                // FFmpeg is still writing the newest file. Only ingest completed files.
                foreach(var path in paths.SkipLast(1))await IngestAsync(path,token);
                if(!RetainAllSegments)CheckActiveCacheCapacity(OwnedCacheRoot!,session.Id);
                if(_process?.HasExited==true)break;
                if(DateTimeOffset.UtcNow-lastProgress>TimeSpan.FromSeconds(30))
                {
                    Error?.Invoke("录制分片 30 秒无更新，正在重连");
                    try{_process?.Kill(true);}catch{};
                    break;
                }
                await Task.Delay(1000,token);
            }
            foreach(var path in Directory.GetFiles(directory,"segment_*.ts").OrderBy(x=>x,StringComparer.Ordinal))await IngestAsync(path,CancellationToken.None);
            if(!token.IsCancellationRequested && _process?.ExitCode is int code && code!=0)
            {
                var message=await errors;Error?.Invoke($"FFmpeg 退出 ({code}): {message[^Math.Min(message.Length,500)..]}");
            }
        }
        catch(OperationCanceledException)
        {
            foreach(var path in Directory.GetFiles(directory,"segment_*.ts").OrderBy(x=>x,StringComparer.Ordinal))
                try {await IngestAsync(path,CancellationToken.None);}catch(Exception ex){Error?.Invoke(ex.Message);}
        }
        catch(Exception ex)
        {
            // A failed cache monitor must not leave FFmpeg writing unbounded files.
            try { if(_process is {HasExited:false}) { _process.Kill(true); await _process.WaitForExitAsync(); } } catch { }
            Error?.Invoke(ex.Message);
        }
    }
    public async Task StopAsync()
    {
        if(_stop is null)return;
        if(_process is {HasExited:false})
        {
            try {await _process.StandardInput.WriteLineAsync("q");await _process.StandardInput.FlushAsync();}
            catch { }
            try {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));await _process.WaitForExitAsync(timeout.Token);}
            catch {try{_process.Kill(true);}catch{}}
        }
        _stop.Cancel();
        if(_monitor is not null)await _monitor;
        _process?.Dispose();_process=null;_stop.Dispose();_stop=null;
    }
    private void CheckActiveCacheCapacity(string root,string sessionId)
    {
        try { CheckCacheCapacity(root,sessionId); }
        catch(InvalidOperationException) { CacheCapacityExceeded=true; throw; }
    }
    public static void CheckCacheCapacity(string root,string sessionId)
    {
        var full=Path.GetFullPath(root);
        var free=new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace;
        var options=new EnumerationOptions { RecurseSubdirectories=true, AttributesToSkip=FileAttributes.ReparsePoint, IgnoreInaccessible=false };
        long total=0,sessionBytes=0;
        var sessionRoot=Path.GetFullPath(Path.Combine(full,"buffer",sessionId))+Path.DirectorySeparatorChar;
        foreach(var path in Directory.EnumerateFiles(full,"*",options))
        {
            var size=new FileInfo(path).Length;total+=size;
            if(Path.GetFullPath(path).StartsWith(sessionRoot,StringComparison.OrdinalIgnoreCase))sessionBytes+=size;
        }
        EnsureCacheBudget(free,sessionBytes,total);
    }
    public static void EnsureCacheBudget(long freeBytes,long sessionBytes,long totalBytes)
    {
        if(freeBytes<CacheMinimumFreeBytes||sessionBytes>=CacheSessionLimitBytes||totalBytes>=CacheTotalLimitBytes)
            throw new InvalidOperationException("高光缓存容量保护：剩余空间不足2GiB、单房缓存达到2GiB或总缓存达到10GiB，已暂停缓存；不会删除锁定素材或旧录播");
    }
    public bool IsRunning => _process is {HasExited:false};
}
