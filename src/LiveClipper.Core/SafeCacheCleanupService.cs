using System.Text.RegularExpressions;
namespace LiveClipper.Core;
public sealed record CacheCleanupScan(string Token,long Bytes,int Count,string[] Excluded);
public sealed record CacheCleanupResult(long DeletedBytes,int DeletedCount,int SkippedCount);
public sealed class SafeCacheCleanupService(string dataRoot,Func<IReadOnlyDictionary<string,string>> sources)
{
    private sealed record Item(string Path,long Length,DateTime Modified);
    private readonly object gate=new();
    private string? token;
    private Item[] pending=[];
    private DateTime expires;
    private static readonly byte[] Png=[137,80,78,71,13,10,26,10];
    private string Root=>Path.GetFullPath(Path.Combine(dataRoot,"waveforms"));
    private static bool Plain(string path) {
        for(var dir=new DirectoryInfo(path);dir is not null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)return false;
        return true;
    }
    public CacheCleanupScan Scan() {
        lock(gate) {
            var known=sources();var protectedPaths=known.Values.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items=new List<Item>();
            if(Plain(Root)&&Directory.Exists(Root)) foreach(var path in Directory.EnumerateFiles(Root,"*.png",SearchOption.TopDirectoryOnly)) {
                var match=Regex.Match(Path.GetFileName(path),@"^([a-fA-F0-9]{32})-(\d{3,4})x(\d{2,3})\.png$");
                if(!match.Success||!known.ContainsKey(match.Groups[1].Value)||protectedPaths.Contains(path))continue;
                var info=new FileInfo(path);
                if((info.Attributes&FileAttributes.ReparsePoint)!=0||info.LastWriteTimeUtc>DateTime.UtcNow.AddHours(-1))continue;
                try { using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);
                    var magic=new byte[8];if(file.Read(magic)!=8||!magic.SequenceEqual(Png))continue;
                    items.Add(new(path,file.Length,info.LastWriteTimeUtc));
                } catch(IOException) { }
            }
            token=Guid.NewGuid().ToString("N");pending=items.ToArray();expires=DateTime.UtcNow.AddMinutes(5);
            return new(token,items.Sum(x=>x.Length),items.Count,["正在录制和高光缓冲","工程、素材、录播、字幕和历史备份","浏览器 WebView 缓存及预览音频缓存","模型、渲染工作目录及未知旧文件","最近一小时生成或正在占用的波形"]);
        }
    }
    public CacheCleanupResult Clean(string confirmationToken) {
        lock(gate) {
            if(token!=confirmationToken||DateTime.UtcNow>expires)throw new InvalidOperationException("清理清单已失效，请重新扫描确认");
            token=null;long bytes=0;var count=0;var skipped=0;
            var protectedPaths=sources().Values.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var item in pending) {
                try {
                    if(!Plain(Root)||protectedPaths.Contains(item.Path)) {skipped++;continue;}
                    var info=new FileInfo(item.Path);
                    if(!info.Exists||(info.Attributes&FileAttributes.ReparsePoint)!=0||info.Length!=item.Length||info.LastWriteTimeUtc!=item.Modified) {skipped++;continue;}
                    using(var file=new FileStream(item.Path,FileMode.Open,FileAccess.Read,FileShare.None)) { if(file.Length!=item.Length) {skipped++;continue;} }
                    File.Delete(item.Path);bytes+=item.Length;count++;
                } catch(IOException) {skipped++;} catch(UnauthorizedAccessException) {skipped++;}
            }
            pending=[];return new(bytes,count,skipped);
        }
    }
}
