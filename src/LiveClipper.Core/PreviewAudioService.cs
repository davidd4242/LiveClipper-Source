using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace LiveClipper.Core;
/// <summary>Timestamp-preserving browser PCM proxy. Source media is never modified.</summary>
public sealed class PreviewAudioService(MediaTools media)
{
    private readonly ConcurrentDictionary<string,SemaphoreSlim> gates=new(StringComparer.OrdinalIgnoreCase);
    public async Task<string> GetAsync(string source,string workRoot,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var file=new FileInfo(source);
        if(!file.Exists)throw new FileNotFoundException("源素材不存在",source);
        var signature=$"pcm-preview-v2|{TimelineAudio.PreserveTimestampsFilter}|{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
        var root=Path.Combine(workRoot,"preview-audio");Directory.CreateDirectory(root);
        var output=Path.Combine(root,key+".wav");
        var gate=gates.GetOrAdd(output,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(token);
        try
        {
            if(File.Exists(output))return output;
            var info=await media.ProbeAsync(source,token);
            if(!info.HasAudio)throw new NotSupportedException("源素材没有音轨");
            var duration=await media.DurationAsync(source,token);token.ThrowIfCancellationRequested();
            if(!double.IsFinite(duration)||duration<=0)throw new NotSupportedException("无法读取源素材时长");
            var partial=Path.Combine(root,key+"-"+Guid.NewGuid().ToString("N")+".partial.wav");
            try
            {
                // Default demux timestamp origin is the container start; do not reset individual audio PTS.
                var filter=TimelineAudio.PreserveTimestampsFilter+",apad,atrim=duration="+duration.ToString("R",CultureInfo.InvariantCulture);
                await media.RunAsync(media.Ffmpeg,["-hide_banner","-nostats","-n","-i",source,"-map","0:a:0","-vn","-af",filter,"-ar","48000","-ac","2","-c:a","pcm_s16le","-f","wav",partial],token);
                var after=new FileInfo(source);
                if(after.Length!=file.Length||after.LastWriteTimeUtc!=file.LastWriteTimeUtc)throw new IOException("生成预览期间源素材已变化，请重试");
                token.ThrowIfCancellationRequested();File.Move(partial,output);return output;
            }
            finally{if(File.Exists(partial))File.Delete(partial);}
        }
        finally{gate.Release();}
    }
}
