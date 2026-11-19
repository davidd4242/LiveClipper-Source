using System.Text;
using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed record JianyingExportResult(string Directory,string DraftPath,string ManifestPath,string[] Warnings);
public sealed record JianyingMedia(string Id,string Path,string? StagedPath,string? AudioPath=null,string? AudioStagedPath=null);
public sealed record JianyingSegment(string Id,string SourceId,int Track,long SourceStartUs,long DurationUs,long TargetStartUs,double Volume,bool VideoEnabled,bool AudioEnabled)
{
    public long SourceDurationUs {get;init;}=DurationUs;
}
public sealed record JianyingCaption(long StartUs,long DurationUs,string Text);
public sealed record JianyingManifest(int SchemaVersion,string Name,int Width,int Height,int Fps,JianyingMedia[] Media,JianyingSegment[] Segments,JianyingCaption[] Captions);

/// <summary>Independent original-media, editable draft profile for installed pyJianYingDraft 0.3.0.</summary>
public sealed class JianyingExportService(Store store,MediaTools media,string pythonPath,string scriptPath,string outputRoot)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    public async Task<JianyingExportResult> ExportAsync(string clipId,string name,EditorProject project,CancellationToken token=default,EditorProjectIdentity? projectIdentity=null)
    {
        name=name.Trim();if(name.Length is 0 or >100)throw new ArgumentException("草稿名称须为 1–100 个字符");
        project=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(project))!;
        PremiereExportService.ValidateIdentity(clipId,project,projectIdentity);
        Validate(project);
        if(!File.Exists(scriptPath))throw new InvalidOperationException("缺少独立剪映 v3 导出脚本");
        var items=EditorRenderService.Items(project,clipId);if(items.Length==0)throw new ArgumentException("时间线为空，不能导出草稿");
        var clips=store.Clips().ToDictionary(c=>c.Id,StringComparer.Ordinal);
        var ids=items.Select(i=>i.ClipId).Distinct(StringComparer.Ordinal).ToArray();
        foreach(var id in ids)if(!clips.TryGetValue(id,out var clip)||!File.Exists(clip.Path))throw new ArgumentException("源素材不存在："+id);
        var width=1280;var height=720;
        if(projectIdentity is not null){width=projectIdentity.Width;height=projectIdentity.Height;}
        else if(project.CanvasWidth is {} cw && project.CanvasHeight is {} ch){width=cw;height=ch;}
        else if(project.ReplicaExtension?.Document.TryGetProperty("sequence",out var sequence)==true&&sequence.TryGetProperty("width",out var w)&&sequence.TryGetProperty("height",out var h))
        {width=w.GetInt32();height=h.GetInt32();}
        else if(items.FirstOrDefault(i=>i.Track<=3) is {} video)
        {
            var info=await media.ProbeAsync(clips[video.ClipId].Path,token);
            height=project.Resolution==0?info.Height:Math.Min(info.Height,project.Resolution);height=Math.Max(2,height/2*2);
            width=Math.Max(2,(int)Math.Round(info.Width*(double)height/info.Height)/2*2);
        }
        if(width<=0||height<=0)throw new ArgumentException("草稿画布尺寸无效");
        IReadOnlyList<TranscriptSegment> Source(string id)
        {
            if(project.CaptionOverrides?.TryGetValue(id,out var edited)==true)return EditorRenderService.NormalizeCaptions(edited);
            if(project.SourceTranscripts?.TryGetValue(id,out var frozen)==true)return frozen;
            var json=store.TranscriptJson(clips[id].EventId);return json is null?[]:TranscriptExport.Parse(json).Segments;
        }
        var captions=project.TimelineCaptions is {} independent?TimelineCaptionTools.ToSegments(independent):EditorRenderService.MapSubtitles(items.Where(i=>i.Track==1).ToArray(),Source,clipId,project.SelectedSegments,null,project.Fps,projectIdentity is null&&!EditorRenderService.HasCaptionOverride(project,clipId));
        var root=Path.GetFullPath(outputRoot);Directory.CreateDirectory(root);
        var idName=Guid.NewGuid().ToString("N");var staging=Path.Combine(root,".partial-"+idName);var final=Path.Combine(root,idName);Directory.CreateDirectory(staging);
        var copied=await ProjectCopyMediaStager.StageAsync(staging,ids.Select(id=>new ProjectCopyMediaStager.Source(id,clips[id].Path)).ToArray(),token);
        Directory.Move(copied.StagingDirectory,Path.Combine(staging,"media"));
        var sources=ids.Select(id=>
        {
            var relative=Path.Combine("media",Path.GetRelativePath(copied.StagingDirectory,copied.Clips[id].Media.Path));
            return new JianyingMedia(id,Path.Combine(final,relative),Path.Combine(staging,relative));
        }).ToArray();
        for(var index=0;index<sources.Length;index++)
        {
            var source=sources[index];if(!items.Any(i=>i.ClipId==source.Id&&i.Track>=4))continue;
            var json=await media.RunAsync(media.Ffprobe,["-v","error","-show_streams","-of","json",source.StagedPath!],token);
            using var info=JsonDocument.Parse(json);var streams=info.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var audio=streams.Where(s=>s.GetProperty("codec_type").GetString()=="audio").ToArray();
            if(audio.Length!=1)throw new NotSupportedException("剪映 v3 独立音轨要求源素材恰有一个音频流");
            static double Start(JsonElement stream)=>stream.TryGetProperty("start_time",out var value)?double.Parse(value.GetString()!,System.Globalization.CultureInfo.InvariantCulture):0;
            if(Math.Abs(Start(audio[0]))>.000001)throw new NotSupportedException("剪映 v3 独立音轨暂不支持非零音频起点，不能静默归零或缩短源范围");
            if(!streams.Any(s=>s.GetProperty("codec_type").GetString()=="video"))continue;
            var audioStaged=Path.Combine(Path.GetDirectoryName(source.StagedPath!)!,"audio.mka");
            await media.RunAsync(media.Ffmpeg,["-hide_banner","-y","-copyts","-i",source.StagedPath!,"-map","0:a:0","-vn","-c:a","copy","-avoid_negative_ts","disabled",audioStaged],token);
            var afterJson=await media.RunAsync(media.Ffprobe,["-v","error","-show_streams","-of","json",audioStaged],token);
            using var after=JsonDocument.Parse(afterJson);var extracted=after.RootElement.GetProperty("streams").EnumerateArray().Single();
            if(Math.Abs(Start(audio[0])-Start(extracted))>.001)throw new NotSupportedException("提取音频流的起点发生变化，未发布草稿");
            sources[index]=source with {AudioStagedPath=audioStaged,AudioPath=Path.Combine(final,Path.GetRelativePath(staging,audioStaged))};
        }
        var manifest=CreateManifest(project,clipId,name,width,height,sources,captions);
        var manifestPath=Path.Combine(staging,"manifest.json");
        await File.WriteAllTextAsync(manifestPath,JsonSerializer.Serialize(manifest,Json),new UTF8Encoding(false),token);
        await media.RunAsync(pythonPath,["-B",scriptPath,"--manifest",manifestPath,"--draft-directory",Path.Combine(staging,"draft")],token);
        if(!File.Exists(Path.Combine(staging,"draft","draft_content.json"))||!File.Exists(Path.Combine(staging,"draft","draft_meta_info.json")))throw new InvalidOperationException("剪映候选草稿文件不完整，未发布完成目录");
        // Final manifest contains only published paths, not stale staging paths.
        manifest=manifest with {Media=sources.Select(s=>s with {StagedPath=null,AudioStagedPath=null}).ToArray()};
        await File.WriteAllTextAsync(manifestPath,JsonSerializer.Serialize(manifest,Json),new UTF8Encoding(false),token);
        string[] warnings=["这是 pyJianYingDraft 0.3.0 格式的候选草稿，尚未在当前剪映版本实机验证。","原素材按字节复制；视频源用于独立音轨时另提取原音频流（不重编码）。VFR及音视频起点不做归零转换，需实机核对同步和剪点。","字幕为可编辑文本，重叠字幕拆分轨道；原字幕样式不保证一致。","草稿使用导出目录中的绝对素材路径，移动后须重新连接素材；不会自动写入剪映草稿目录。"];
        await File.WriteAllTextAsync(Path.Combine(staging,"README.txt"),"LiveClipper 剪映原素材可编辑草稿 v3\n生成 profile：pyJianYingDraft==0.3.0，draft_content.json + draft_meta_info.json。\n不生成或伪造 draft_info.json；不代表兼容最新版剪映。\n"+string.Join("\n",warnings)+"\n请完整保留本目录，再在目标剪映版本完成打开、剪点/同步核对、编辑、保存、重开、导出验收。\n原工程未修改；没有把整条时间线烘焙成成片。",new UTF8Encoding(false),token);
        token.ThrowIfCancellationRequested();Directory.Move(staging,final);
        return new(final,Path.Combine(final,"draft"),Path.Combine(final,"manifest.json"),warnings);
    }
    internal static void Validate(EditorProject p)
    {
        PremiereExportService.ValidateBasics(p,"剪映 v3");
        if(p.HiddenTracks is {Length:>0}||p.Timeline?.Any(i=>i.Disabled)==true)throw new NotSupportedException("剪映 v3 首版暂不支持隐藏视频轨或禁用片段，不能静默移除这些编辑内容");
    }
    internal static long Micros(double seconds)=>(long)Math.Round((decimal)seconds*1000000m,0,MidpointRounding.AwayFromZero);
    internal static JianyingManifest CreateManifest(EditorProject p,string clipId,string name,int width,int height,JianyingMedia[] sources,IReadOnlyList<TranscriptSegment> captions)
    {
        Validate(p);var items=EditorRenderService.Items(p,clipId);var starts=EditorRenderService.SequenceStarts(items,[]);
        var segments=items.Select(i=>new JianyingSegment(i.Id,i.ClipId,i.Track,Micros(i.Start),Micros(starts[i.Id]+(i.End-i.Start))-Micros(starts[i.Id]),Micros(starts[i.Id]),p.MasterVolume,true,
            !(i.Track<=3&&i.AudioDetached)&&!(i.Track==1&&p.BaseAudioMuted)&&EditorRenderService.IncludeTrackAudio(i.Track,p.MutedTracks,p.SoloedTracks)){SourceDurationUs=Micros(i.End)-Micros(i.Start)}).ToArray();
        return new(3,name,width,height,p.Fps,sources,segments,captions.Select(c=>new JianyingCaption(Micros(c.Start),Micros(c.End)-Micros(c.Start),c.Text)).ToArray());
    }
}
