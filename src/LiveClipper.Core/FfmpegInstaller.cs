using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace LiveClipper.Core;

public sealed record FfmpegDownloadStatus(string State,int Progress,string? Error,string? Path);

public sealed class FfmpegInstaller(string installRoot,Func<string,Task> onInstalled)
{
    private const string ArchiveUrl="https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private readonly HttpClient _http=new(){Timeout=Timeout.InfiniteTimeSpan};
    private readonly object _gate=new();
    private FfmpegDownloadStatus _status=new("idle",0,null,null);
    public FfmpegDownloadStatus Status {get {lock(_gate)return _status;}}
    private void Set(string state,int progress,string? error=null,string? path=null)
    {lock(_gate)_status=new(state,progress,error,path);}
    public bool Start()
    {
        lock(_gate)
        {
            if(_status.State is "downloading" or "verifying" or "installing")return false;
            _status=new("downloading",0,null,null);
        }
        _=Task.Run(DownloadAsync);
        return true;
    }
    private async Task DownloadAsync()
    {
        var temp=Path.Combine(installRoot,"downloads",Guid.NewGuid().ToString("N"));
        var archivePath=Path.Combine(temp,"ffmpeg.zip");
        string? candidate=null;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(20));
            var token=timeout.Token;
            Directory.CreateDirectory(temp);
            using var response=await _http.GetAsync(ArchiveUrl,HttpCompletionOption.ResponseHeadersRead,token);
            response.EnsureSuccessStatusCode();
            var total=response.Content.Headers.ContentLength;
            await using(var source=await response.Content.ReadAsStreamAsync(token))
            await using(var target=new FileStream(archivePath,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
            {
                var buffer=new byte[81920];long received=0;int count;
                while((count=await source.ReadAsync(buffer,token))>0)
                {
                    await target.WriteAsync(buffer.AsMemory(0,count),token);received+=count;
                    if(total is >0)Set("downloading",Math.Clamp((int)(received*90/total.Value),0,90));
                }
            }
            Set("verifying",91);
            var hashText=await _http.GetStringAsync(ArchiveUrl+".sha256",token);
            var expected=Regex.Match(hashText,@"\b[a-fA-F0-9]{64}\b").Value;
            if(expected.Length!=64)throw new InvalidDataException("下载源没有提供有效 SHA-256 校验值");
            await using(var stream=File.OpenRead(archivePath))
            {
                var actual=Convert.ToHexString(await SHA256.HashDataAsync(stream,token));
                if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("FFmpeg 下载校验失败，请重试");
            }
            Set("installing",95);
            candidate=Path.Combine(installRoot,"tools","ffmpeg",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(candidate);
            using(var zip=ZipFile.OpenRead(archivePath))
            {
                foreach(var name in new[]{"ffmpeg.exe","ffprobe.exe"})
                {
                    var entry=zip.Entries.SingleOrDefault(x=>x.FullName.Replace('\\','/').EndsWith("/bin/"+name,StringComparison.OrdinalIgnoreCase))
                        ??throw new InvalidDataException($"安装包中缺少 {name}");
                    entry.ExtractToFile(Path.Combine(candidate,name));
                }
            }
            var path=Path.Combine(candidate,"ffmpeg.exe");
            if(!new MediaTools(path).Available())throw new InvalidDataException("下载的 FFmpeg 无法运行");
            await onInstalled(path);
            Set("completed",100,null,path);
        }
        catch(Exception ex)
        {
            Set("failed",0,ex.Message);
            if(candidate is not null)
            {
                foreach(var name in new[]{"ffmpeg.exe","ffprobe.exe"})
                {var file=Path.Combine(candidate,name);if(File.Exists(file))try{File.Delete(file);}catch{}}
                try{Directory.Delete(candidate);}catch{}
            }
        }
        finally
        {
            if(File.Exists(archivePath))try{File.Delete(archivePath);}catch{}
            try{Directory.Delete(temp);}catch{}
        }
    }
}
