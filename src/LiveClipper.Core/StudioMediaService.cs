using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public static partial class SubtitleTools
{
    [GeneratedRegex(@"(?m)^\s*(?:\d+\s*\r?\n)?\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})[^\r\n]*\r?\n([\s\S]*?)(?=\r?\n\s*\r?\n|\z)")]
    private static partial Regex CuePattern();
    private static double Seconds(string value)
    {
        var parts=value.Replace(',', '.').Split(':');
        return int.Parse(parts[0],CultureInfo.InvariantCulture)*3600+int.Parse(parts[1],CultureInfo.InvariantCulture)*60+double.Parse(parts[2],CultureInfo.InvariantCulture);
    }
    private static string Stamp(double value)
    {
        var ms=(long)Math.Round(Math.Max(0,value)*1000);return $"{ms/3600000:00}:{ms/60000%60:00}:{ms/1000%60:00},{ms%1000:000}";
    }
    public static TranscriptPreview ParseSrt(string source)
    {
        var segments=CuePattern().Matches(source.Replace("\r\n","\n"))
            .Select(x=>new TranscriptSegment(Seconds(x.Groups[1].Value),Seconds(x.Groups[2].Value),Regex.Replace(x.Groups[3].Value.Trim(),"<[^>]+>","")))
            .Where(x=>x.End>x.Start&&x.Text.Length>0).OrderBy(x=>x.Start).ToArray();
        return new("",segments,string.Join(Environment.NewLine,segments.Select(x=>x.Text)));
    }
    public static string ToSrt(IEnumerable<TranscriptSegment> segments,double offset=0)
    {
        var lines=segments.Where(x=>x.End>x.Start&&x.Text.Trim().Length>0).Select((x,i)=>$"{i+1}\n{Stamp(x.Start-offset)} --> {Stamp(x.End-offset)}\n{x.Text.Trim()}\n");
        return string.Join("\n",lines);
    }
    public static IReadOnlyList<TranscriptSegment> Clean(IEnumerable<TranscriptSegment> segments)
    {
        var result=new List<TranscriptSegment>();
        foreach(var item in segments)
        {
            var text=Regex.Replace(item.Text.Trim(),@"\s+"," ").Trim();
            if(Regex.IsMatch(text,@"^(嗯|呃|额|啊|唔|那个|就是)[，。！？!?,.\s]*$"))continue;
            text=Regex.Replace(text,@"^[，。！？!?,.\s]+|[，。！？!?,.\s]+$","");
            if(text.Length>0&&item.End>item.Start)result.Add(item with {Text=text});
        }
        return result;
    }
    public static string ToJson(TranscriptPreview preview)=>JsonSerializer.Serialize(new {language=preview.Language,segments=preview.Segments.Select(x=>new {start=x.Start,end=x.End,text=x.Text})});
}

public sealed record ImportResult(int Added,int Existing,int MatchedSubtitles,int MissingSubtitles,IReadOnlyList<string>? ClipIds=null);

public sealed class StudioMediaService(Store store,StudioStore studio,MediaTools media,string storageRoot)
{
    private static readonly HashSet<string> VideoExtensions=new(StringComparer.OrdinalIgnoreCase){".mp4",".mkv",".mov",".flv",".webm",".m4v",".ts"};
    private static readonly HashSet<string> SubtitleExtensions=new(StringComparer.OrdinalIgnoreCase){".srt",".vtt",".ass",".ssa"};
    public async Task<ImportResult> ImportAsync(string videoDirectory,string? subtitleDirectory,string outputDirectory,CancellationToken token)
    {
        if(!Directory.Exists(videoDirectory))throw new ArgumentException("录播文件夹不存在");
        if(!string.IsNullOrWhiteSpace(subtitleDirectory)&&!Directory.Exists(subtitleDirectory))throw new ArgumentException("字幕文件夹不存在");
        if(string.IsNullOrWhiteSpace(outputDirectory))throw new ArgumentException("请选择成片输出文件夹");
        Directory.CreateDirectory(outputDirectory);
        var subtitles=string.IsNullOrWhiteSpace(subtitleDirectory)?Array.Empty<string>():Directory.EnumerateFiles(subtitleDirectory,"*",SearchOption.AllDirectories).Where(x=>SubtitleExtensions.Contains(Path.GetExtension(x))).ToArray();
        var videoPaths=Directory.EnumerateFiles(videoDirectory,"*",SearchOption.AllDirectories).Where(x=>VideoExtensions.Contains(Path.GetExtension(x))).Take(2001).ToArray();
        if(videoPaths.Length>2000)throw new ArgumentException("单次最多导入 2000 个视频，请分批选择");
        return await ImportPathsAsync(videoPaths.Concat(subtitles).ToArray(),outputDirectory,token);
    }
    public async Task<ImportResult> ImportPathsAsync(IReadOnlyList<string> paths,string? outputDirectory,CancellationToken token)
    {
        outputDirectory=string.IsNullOrWhiteSpace(outputDirectory)?Path.Combine(storageRoot,"edited-clips"):Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var subtitles=paths.Where(x=>File.Exists(x)&&SubtitleExtensions.Contains(Path.GetExtension(x))).ToArray();
        var videoPaths=paths.Where(x=>File.Exists(x)&&VideoExtensions.Contains(Path.GetExtension(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(videoPaths.Length>2000)throw new ArgumentException("单次最多导入 2000 个视频");
        var existing=studio.LocalVideos().ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase);
        var added=0;var reused=0;var matched=0;var missing=0;
        var ids=new List<string>();
        foreach(var path in videoPaths)
        {
            token.ThrowIfCancellationRequested();
            var absolute=Path.GetFullPath(path);var title=Path.GetFileNameWithoutExtension(path);
            if(existing.TryGetValue(absolute,out var old))
            {
                studio.SaveLocalVideo(old with {OutputDirectory=Path.GetFullPath(outputDirectory)});reused++;
                ids.Add(old.ClipId);
                var matchedPath=MatchSubtitle(path,subtitles);
                if(matchedPath is not null){await AttachSubtitleAsync(store.Clips().First(x=>x.Id==old.ClipId),matchedPath,token);matched++;}
                continue;
            }
            var clip=new Clip(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),absolute,null,DateTimeOffset.UtcNow);
            store.SaveClip(clip);
            studio.SaveLocalVideo(new LocalVideo(clip.Id,absolute,title,Path.GetFullPath(outputDirectory),clip.CreatedAt));
            added++;
            ids.Add(clip.Id);
            var subtitle=MatchSubtitle(path,subtitles);
            if(subtitle is not null)
            {
                try{await AttachSubtitleAsync(clip,subtitle,token);matched++;}catch{missing++;}
            }
            else missing++;
        }
        return new(added,reused,matched,missing,ids);
    }
    public static string? MatchSubtitle(string video,IReadOnlyList<string> subtitles)
    {
        var name=Path.GetFileNameWithoutExtension(video);
        var exact=subtitles.FirstOrDefault(x=>Path.GetFileNameWithoutExtension(x).Equals(name,StringComparison.OrdinalIgnoreCase));if(exact is not null)return exact;
        static string Date(string value)=>Regex.Match(value,@"20\d{2}[-_.]?\d{2}[-_.]?\d{2}").Value.Replace("-","").Replace("_","").Replace(".","");
        var date=Date(name);if(date.Length==0)return null;
        var sameDate=subtitles.Where(x=>Date(Path.GetFileNameWithoutExtension(x))==date).ToArray();
        return sameDate.Length==1?sameDate[0]:null;
    }
    public async Task<(TranscriptPreview? Transcript,string? Error)> ImportSourceSrtIfAbsentAsync(Clip clip,string text,CancellationToken token)
    {
        var preview=SubtitleTools.ParseSrt(text);
        if(preview.Segments.Count==0 || preview.Segments.Any(s=>!double.IsFinite(s.Start)||!double.IsFinite(s.End)||s.Start<0||s.End<=s.Start))
            return (null,"invalid_source_srt");
        token.ThrowIfCancellationRequested();
        var directory=Path.Combine(storageRoot,"imports",clip.Id);Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"source-"+Guid.NewGuid().ToString("N")+".srt");
        var committed=false;
        try
        {
            await File.WriteAllTextAsync(path,SubtitleTools.ToSrt(preview.Segments),new UTF8Encoding(false),token);
            token.ThrowIfCancellationRequested();
            var error=store.InsertSourceTranscriptIfAbsent(clip,SubtitleTools.ToJson(preview),path);
            committed=error is null;
            return error is null?(preview,null):(null,error);
        }
        finally { if(!committed && File.Exists(path))File.Delete(path); }
    }
    public async Task<TranscriptPreview> AttachSubtitleAsync(Clip clip,string path,CancellationToken token)
    {
        if(!File.Exists(path)||!SubtitleExtensions.Contains(Path.GetExtension(path)))throw new ArgumentException("请选择 SRT、VTT 或 ASS 字幕文件");
        var directory=Path.Combine(storageRoot,"imports",clip.Id);Directory.CreateDirectory(directory);
        var srtPath=Path.Combine(directory,"transcript.srt");
        if(Path.GetExtension(path).Equals(".srt",StringComparison.OrdinalIgnoreCase))File.Copy(path,srtPath,true);
        else await media.RunAsync(media.Ffmpeg,["-hide_banner","-loglevel","error","-y","-i",path,srtPath],token);
        var preview=SubtitleTools.ParseSrt(await File.ReadAllTextAsync(srtPath,token));
        if(preview.Segments.Count==0)throw new InvalidOperationException("字幕文件中没有可读取的时间轴文字");
        store.SaveTranscript(clip.EventId,SubtitleTools.ToJson(preview),srtPath);
        return preview;
    }
    public async Task<TranscriptPreview> SaveTranscriptAsync(Clip clip,IReadOnlyList<TranscriptSegment> segments,string language,CancellationToken token)
    {
        if(segments.Count>50000||segments.Any(x=>!double.IsFinite(x.Start)||!double.IsFinite(x.End)||x.Start<0||x.End<=x.Start||x.Text.Length>1000))throw new ArgumentException("字幕时间或文字无效");
        var ordered=segments.OrderBy(x=>x.Start).ToArray();
        var preview=new TranscriptPreview(language,ordered,string.Join(Environment.NewLine,ordered.Select(x=>x.Text)));
        var directory=Path.Combine(storageRoot,"edited-transcripts",clip.Id);Directory.CreateDirectory(directory);
        var srtPath=Path.Combine(directory,"transcript.srt");
        await File.WriteAllTextAsync(srtPath,SubtitleTools.ToSrt(ordered),new UTF8Encoding(false),token);
        await File.WriteAllTextAsync(Path.Combine(directory,"transcript.json"),SubtitleTools.ToJson(preview),new UTF8Encoding(false),token);
        store.SaveTranscript(clip.EventId,SubtitleTools.ToJson(preview),srtPath);
        return preview;
    }
    public IReadOnlyList<StudioEvent> ExtractEvents(Clip clip)
    {
        var json=store.TranscriptJson(clip.EventId)??throw new InvalidOperationException("请先匹配字幕或转写视频");
        var segments=TranscriptExport.Parse(json).Segments;
        var result=new List<StudioEvent>();var group=new List<TranscriptSegment>();
        void Flush()
        {
            if(group.Count==0)return;
            var content=string.Join("",group.Select(x=>x.Text));
            if(content.Length>=8)
            {
                var title=content.Length<=28?content:content[..28]+"…";
                var item=new StudioEvent(Guid.NewGuid().ToString("N"),clip.Id,group[0].Start,group[^1].End,title,content.Length<=180?content:content[..180]+"…","","subtitle");
                studio.SaveEvent(item);result.Add(item);
            }
            group.Clear();
        }
        foreach(var segment in segments)
        {
            if(group.Count>0&&(segment.Start-group[^1].End>5||segment.End-group[0].Start>90))Flush();
            group.Add(segment);
        }
        Flush();return result;
    }
}
