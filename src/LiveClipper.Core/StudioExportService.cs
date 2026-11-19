using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveClipper.Domain;

namespace LiveClipper.Core;

/// <summary>
/// 导出清单里的单个 part。start 是该片段在成片时间线上的真实起点（已计入 speed 与剪辑点转场造成的压缩），
/// duration 是它实际生效的输出时长（源区间除以 speed）。字段名保持小写，ai-worker/export_draft.py 直接读取。
/// </summary>
public sealed record ExportPart(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sourceClipId")] string SourceClipId,
    [property: JsonPropertyName("sourceStart")] double SourceStart,
    [property: JsonPropertyName("sourceEnd")] double SourceEnd,
    /// <summary>草稿 v2：与渲染器滤镜近似对应的剪映滤镜列表；无滤镜时为 null（不写字段）。</summary>
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("filters")] ExportFilter[]? Filters=null,
    /// <summary>草稿 v2：淡入淡出与运镜动画；三者都为空时为 null（不写字段）。</summary>
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("animation")] ExportAnimation? Animation=null);

/// <summary>草稿 v2：片段滤镜。kind 是 LiveClipper 的滤镜名（warm/cool/mono/vivid），由 export_draft.py 映射成剪映滤镜。</summary>
public sealed record ExportFilter(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("intensity")] double Intensity=100);
/// <summary>草稿 v2：片段动画。fadeIn/fadeOut 单位秒；motion 为 none/push/shake，由导出脚本映射成剪映入场动画。</summary>
public sealed record ExportAnimation(
    [property: JsonPropertyName("fadeIn")] double FadeIn,
    [property: JsonPropertyName("fadeOut")] double FadeOut,
    [property: JsonPropertyName("motion")] string Motion="none");
/// <summary>草稿 v2：文字样式。font 是渲染器字体名（如 Microsoft YaHei）；size 是草稿字号（已从 ASS 字号换算）；color 为 #RRGGBB；outline 是描边宽度（ASS 单位）；position 为 top/middle/bottom。</summary>
public sealed record ExportTextStyle(
    [property: JsonPropertyName("font")] string Font,
    [property: JsonPropertyName("size")] double Size,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("outline")] double Outline,
    [property: JsonPropertyName("position")] string Position);
/// <summary>草稿 v2：一条可编辑文字。start/duration 秒（成片时间线）；transformX/transformY 是剪映归一化坐标（-1..1）。source 为 subtitle（字幕句）或 title（片内标题），导出脚本据此放到「字幕」/「标题」两条轨道。</summary>
public sealed record ExportText(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("transformX")] double TransformX,
    [property: JsonPropertyName("transformY")] double TransformY,
    [property: JsonPropertyName("style")] ExportTextStyle Style,
    [property: JsonPropertyName("source")] string Source="subtitle");

public sealed class StudioExportService(Store store,StudioStore studio,EditorRenderService renderer,MediaTools media,LiveSettings settings)
{
    private readonly CancellationTokenSource _stop=new();
    private readonly SemaphoreSlim _worker=new(1,1);
    private readonly List<Task> _tasks=[];
    private string Root=>Path.Combine(string.IsNullOrWhiteSpace(settings.ExportOutputDirectory)?settings.StorageRoot:settings.ExportOutputDirectory,"studio-exports");
    /// <summary>
    /// 生成清单的 parts 与成片总时长：起点取压缩后的序列时间（<see cref="EditorRenderService.SequenceStarts"/>），
    /// 时长取计入速度后的生效时长；有转场时总时长用 <see cref="EditorRenderService.SequenceDuration"/>（扣除重叠）。
    /// 与渲染器内部的 part 排布公式一致，保证导出包里的偏移与成片对得上。
    /// part 文件名必须对应渲染器真实产出的文件：V1 → part-NNN.mkv，叠加轨 V2/V3 → overlay-NNN.mkv，音频轨 A1–A3 → audio-NNN.mka；
    /// 渲染器跳过的隐藏/静音/独奏外轨道、禁用片段不会产出文件，也不进清单；source-free 图形片段不产 overlay 文件，编号也不占用。
    /// </summary>
    public static (ExportPart[] Parts,double Duration) BuildManifest(EditorProject plan,string primaryClipId)
    {
        var allItems=EditorRenderService.Items(plan,primaryClipId);
        var cuts=EditorRenderService.ResolveTransitions(allItems,plan.Transitions,EditorRenderService.TransitionFps(plan));
        var starts=EditorRenderService.SequenceStarts(allItems,cuts);
        var duration=cuts.Length==0?EditorRenderService.TimelineDuration(allItems):EditorRenderService.SequenceDuration(allItems,cuts);
        var parts=new List<ExportPart>();
        foreach(var item in EditorRenderService.BaseItems(allItems))
            parts.Add(new ExportPart($"part-{parts.Count:000}.mkv",starts[item.Id],EditorRenderService.ItemOutputDuration(item),item.Kind,item.ClipId,item.Start,item.End,BuildFilters(item),BuildAnimation(item)));
        var hidden=plan.HiddenTracks??[];
        var muted=plan.MutedTracks??[];
        var soloed=plan.SoloedTracks??[];
        var overlayIndex=0;
        foreach(var track in new[]{2,3})
        {
            if(hidden.Contains(track))continue;
            foreach(var item in allItems.Where(x=>EditorRenderService.TrackOf(x)==track))
            {
                if(item.Disabled||EditorProjectGraphics.IsSourceFree(item))continue;
                parts.Add(new ExportPart($"overlay-{overlayIndex++:000}.mkv",starts[item.Id],EditorRenderService.ItemOutputDuration(item),item.Kind,item.ClipId,item.Start,item.End));
            }
        }
        var audioIndex=0;
        foreach(var track in new[]{4,5,6})
        {
            if(!EditorRenderService.IncludeTrackAudio(track,muted,soloed))continue;
            foreach(var item in allItems.Where(x=>EditorRenderService.TrackOf(x)==track))
            {
                if(item.Disabled)continue;
                parts.Add(new ExportPart($"audio-{audioIndex++:000}.mka",starts[item.Id],EditorRenderService.ItemOutputDuration(item),item.Kind,item.ClipId,item.Start,item.End));
            }
        }
        return (parts.ToArray(),duration);
    }

    /// <summary>草稿 v2：把渲染器的调色滤镜映射成剪映可编辑滤镜（暖黄/冷调 CCD/高清黑白/鲜艳增色）。</summary>
    public static ExportFilter[]? BuildFilters(TimelineItem item)=>item.Filter switch
    {
        "warm"=>[new ExportFilter("warm")],
        "cool"=>[new ExportFilter("cool")],
        "mono"=>[new ExportFilter("mono")],
        "vivid"=>[new ExportFilter("vivid")],
        _=>null
    };

    /// <summary>草稿 v2：淡入淡出（秒）与运镜映射成剪映片段动画；三项都为空时不写字段。</summary>
    public static ExportAnimation? BuildAnimation(TimelineItem item)
    {
        var fadeIn=double.IsFinite(item.FadeIn)?Math.Max(0,item.FadeIn):0;
        var fadeOut=double.IsFinite(item.FadeOut)?Math.Max(0,item.FadeOut):0;
        var motion=item.Motion is "push" or "shake"?item.Motion:"none";
        return fadeIn<=0&&fadeOut<=0&&motion=="none"
            ?null
            :new ExportAnimation(Math.Round(fadeIn,3),Math.Round(fadeOut,3),motion);
    }

    /// <summary>草稿 v2：字幕样式。剪映草稿字号按 ASS 字号的经验比例换算（副标题 ×0.5），颜色统一成 #RRGGBB，描边沿用 ASS 单位。</summary>
    public static ExportTextStyle SubtitleStyle(EditorProject plan)=>new(
        plan.SubtitleFont,
        Math.Clamp(Math.Round(plan.SubtitleSize*.5,1),1,40),
        NormalizeColor(plan.SubtitleColor)??"#FFFFFF",
        Math.Clamp(plan.SubtitleOutline,0,8),
        plan.SubtitlePosition is "top" or "middle"?plan.SubtitlePosition:"bottom");

    /// <summary>
    /// 草稿 v2：成片时间线上的可编辑文字。字幕句用工程的字体/字号/颜色/描边/位置；
    /// V1 片段的标题用片段自己的字号/颜色与 TitleX/TitleY 换算成剪映归一化坐标。
    /// </summary>
    public static ExportText[] BuildTexts(EditorProject plan,string primaryClipId,IReadOnlyList<TranscriptSegment> subtitles)
    {
        var texts=new List<ExportText>();
        var subtitleStyle=SubtitleStyle(plan);
        var subtitleY=SubtitleTransformY(plan.SubtitlePosition);
        foreach(var segment in subtitles)
        {
            var start=Math.Max(0,segment.Start);
            var duration=segment.End-segment.Start;
            if(string.IsNullOrWhiteSpace(segment.Text)||duration<=.01)continue;
            texts.Add(new ExportText(segment.Text.Trim(),Math.Round(start,3),Math.Round(duration,3),0,subtitleY,subtitleStyle,"subtitle"));
        }
        var allItems=EditorRenderService.Items(plan,primaryClipId);
        var cuts=EditorRenderService.ResolveTransitions(allItems,plan.Transitions,EditorRenderService.TransitionFps(plan));
        var starts=EditorRenderService.SequenceStarts(allItems,cuts);
        foreach(var item in EditorRenderService.BaseItems(allItems))
        {
            var text=item.TitleText?.Trim();
            if(string.IsNullOrEmpty(text))continue;
            var titleStyle=new ExportTextStyle(
                plan.SubtitleFont,
                Math.Clamp(Math.Round(item.TitleSize*.25,1),1,40),
                NormalizeColor(item.TitleColor)??"#FFFFFF",
                0,
                plan.SubtitlePosition is "top" or "middle"?plan.SubtitlePosition:"bottom");
            texts.Add(new ExportText(text,Math.Round(starts[item.Id],3),Math.Round(EditorRenderService.ItemOutputDuration(item),3),Math.Round((item.TitleX-.5)*2,4),Math.Round((.5-item.TitleY)*2,4),titleStyle,"title"));
        }
        return texts.ToArray();
    }

    private static double SubtitleTransformY(string position)=>position switch{"top"=>.72,"middle"=>0,_=>-.72};
    private static string? NormalizeColor(string? value)=>value is not null&&System.Text.RegularExpressions.Regex.IsMatch(value,"^#[A-Fa-f0-9]{6}$")?value.ToUpperInvariant():null;

    public StudioOutput Queue(Clip clip,EditorProject plan,string kind)
    {
        if(kind is not ("package" or "draft"))throw new ArgumentException("导出格式无效");EditorRenderService.Validate(plan);
        // 空序列（受管理事件清空）可保存但不可导出：没有可渲染片段，明确拒绝而不是打包黑片。
        if(plan.Timeline is { Length:0 })throw new ArgumentException("时间线没有任何可渲染片段：受管理事件已全部取消勾选，不能导出空成片");
        plan=renderer.Snapshot(clip,plan);
        var id=Guid.NewGuid().ToString("N");var outputRoot=Root;Directory.CreateDirectory(outputRoot);
        var work=Path.Combine(RenderStoragePaths.WorkRoot(settings.StorageRoot,settings.RenderWorkDirectory),"studio-exports",id);
        var highlightBase=OutputNaming.HighlightBase(clip);
        var outputPath=kind=="draft"
            ?(highlightBase is null
                ?Path.Combine(outputRoot,$"LiveClipper-draft-{id[..8]}.zip")
                :OutputNaming.UniquePath(outputRoot,$"{highlightBase}{OutputNaming.DraftSuffix}-{OutputNaming.Date(DateTimeOffset.UtcNow)}",".zip",studio.Outputs().Select(x=>x.OutputPath)))
            :Path.Combine(outputRoot,id+".zip");
        var output=new StudioOutput(id,clip.Id,kind,outputPath,JsonSerializer.Serialize(plan),DateTimeOffset.UtcNow);studio.SaveOutput(output);
        var job=new Job(id,"studio-"+kind,clip.EventId,"pending",0,null);store.SaveJob(job);Schedule(output,clip,job,work);return output;
    }
    public void ResumePending()
    {
        foreach(var output in studio.Outputs())
        {
            var job=store.Jobs().FirstOrDefault(x=>x.Id==output.Id);if(job?.Status!="pending")continue;
            var clip=store.Clips().FirstOrDefault(x=>x.Id==output.ClipId);if(clip is null)store.SaveJob(job with {Status="failed",Error="找不到源视频"});else Schedule(output,clip,job);
        }
    }
    private void Schedule(StudioOutput output,Clip clip,Job job,string? work=null)
    {
        // Historical jobs retain their recorded output location; only newly queued jobs capture new settings.
        var capturedWork=work??Path.Combine(Path.GetDirectoryName(output.OutputPath)!,output.Id);
        var task=Task.Run(()=>RunAsync(output,clip,job,capturedWork));lock(_tasks)_tasks.Add(task);
    }
    public async Task StopAsync(){_stop.Cancel();Task[] tasks;lock(_tasks)tasks=_tasks.ToArray();try{await Task.WhenAll(tasks);}catch(OperationCanceledException){}}
    private async Task RunAsync(StudioOutput output,Clip clip,Job job,string work)
    {
        var entered=false;var token=_stop.Token;
        try
        {
            await _worker.WaitAsync(token);entered=true;store.SaveJob(job=job with {Status="running",Progress=.03,Error=null});
            var plan=JsonSerializer.Deserialize<EditorProject>(output.ProjectJson)??throw new InvalidOperationException("剪辑方案无效");
            if(output.Kind=="draft")
            {
                // 草稿 v1 只写 V1 主轨（part-*.mkv 同源）；叠加轨/音频轨片段没有可编辑草稿映射，提前给出可执行提示。
                var draftTracks=EditorRenderService.Items(plan,clip.Id).Select(EditorRenderService.TrackOf).Where(track=>track!=1).Distinct().OrderBy(track=>track).ToArray();
                if(draftTracks.Length>0)
                {
                    static string Label(int track)=>track<=3?$"V{track}":$"A{track-3}";
                    throw new InvalidOperationException($"剪映草稿 v1 仅支持 V1 主轨，检测到 {string.Join("、",draftTracks.Select(Label))} 轨片段：请先删除或禁用这些片段，或改用素材包导出");
                }
            }
            var assembled=await renderer.AssembleAsync(clip,plan,work,token,p=>store.SaveJob(job with {Progress=p}));
            await File.WriteAllTextAsync(Path.Combine(work,"subtitles.srt"),SubtitleTools.ToSrt(assembled.Subtitles),new UTF8Encoding(false),token);
            var items=EditorRenderService.Items(plan,clip.Id);var info=await media.ProbeAsync(assembled.Video,token);var (parts,duration)=BuildManifest(plan,clip.Id);
            var assets=studio.Assets();var assetIds=items.SelectMany(x=>new[]{x.StickerAssetId,x.SoundAssetId}).Concat([plan.MusicAssetId,plan.EffectAssetId]).Where(x=>x is not null).Distinct().ToArray();
            var audioPaths=new Dictionary<string,string>();var credits=new StringBuilder();
            foreach(var id in assetIds)
            {
                var asset=assets.FirstOrDefault(x=>x.Id==id);if(asset is null||!File.Exists(asset.Path))throw new InvalidOperationException("导出所需素材已移动");
                var name="asset-"+asset.Id+Path.GetExtension(asset.Path);File.Copy(asset.Path,Path.Combine(work,name),true);audioPaths[asset.Id]=name;
                var attribution=studio.Document("audio-attribution",asset.Id);if(attribution is not null){var item=JsonSerializer.Deserialize<OnlineAudio>(attribution);credits.AppendLine(item?.Attribution).AppendLine(item?.LandingUrl).AppendLine(item?.LicenseUrl).AppendLine();}
            }
            var draftName=output.Kind=="draft"&&Path.GetFileNameWithoutExtension(output.OutputPath).Contains(OutputNaming.DraftSuffix)
                ?Path.GetFileNameWithoutExtension(output.OutputPath)
                :null;
            var manifest=new {schemaVersion=1,draftVersion=2,application="LiveClipper",draftName,width=info.Width,height=info.Height,fps=info.Fps,duration,parts,texts=BuildTexts(plan,clip.Id,assembled.Subtitles),assets=audioPaths,project=plan};
            await File.WriteAllTextAsync(Path.Combine(work,"project.json"),JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}),token);
            await File.WriteAllTextAsync(Path.Combine(work,"ATTRIBUTION.txt"),credits.Length==0?"本工程未使用需要署名的线上音频。":credits.ToString(),token);
            var cover=studio.Document("cover-file",clip.Id);if(cover is not null&&File.Exists(cover))File.Copy(cover,Path.Combine(work,"cover.png"),true);
            if(output.Kind=="draft")
            {
                var script=Path.Combine(AppContext.BaseDirectory,"ai-worker","export_draft.py");if(!File.Exists(script))throw new InvalidOperationException("找不到剪映草稿导出脚本");
                await media.RunAsync(settings.PythonPath,[script,"--manifest",Path.Combine(work,"project.json")],token);
            }
            var draftFolderName=draftName??"JianyingDraft";
            await File.WriteAllTextAsync(Path.Combine(work,"README.txt"),output.Kind=="draft"
                ?$"解压全部文件并保留在固定位置。若移动到其他目录，先右键 relocate-draft.ps1，选择‘使用 PowerShell 运行’，更新素材路径；然后将 {draftFolderName} 文件夹复制到剪映设置中显示的草稿目录。不要删除解压目录中的 part-* 与 asset-* 文件。草稿 v2 只写入 V1 主轨画面（叠加轨与音频轨片段请先删除或禁用）；相邻片段之间的转场、片段滤镜、运镜（入场动画）、淡入淡出、字幕与片内标题的字体/字号/颜色/描边/位置都会导出为可编辑的剪映素材。贴纸与亮度/对比度/饱和度等基础调节没有对应的剪映基础调节项，仍烘焙到视频片段。草稿内音频为可编辑基础音量，成片试听中的配乐自动压低与限幅效果需在剪映里重新设置。建议先备份再在剪映中打开。"
                :"part-000.mkv 等文件按时间线顺序排列（中间产物为 FFV1 视频 + PCM 音频的 Matroska 容器）；subtitles.srt 是拼接后时间轴。asset-* 为配乐、音效、贴纸。project.json 保存原始源范围、音量和效果设置。将视频片段、字幕与音频导入剪映等软件可继续编辑。署名素材信息见 ATTRIBUTION.txt。",token);
            store.SaveJob(job with {Progress=.9});if(File.Exists(output.OutputPath))File.Delete(output.OutputPath);
            ZipFile.CreateFromDirectory(work,output.OutputPath,CompressionLevel.Fastest,false);store.SaveJob(job with {Status="completed",Progress=1});
        }
        catch(OperationCanceledException){store.SaveJob(job with {Status="pending",Error=null});}
        catch(Exception ex){store.SaveJob(job with {Status="failed",Error=ex.Message});}
        finally{if(entered)_worker.Release();}
    }
}
