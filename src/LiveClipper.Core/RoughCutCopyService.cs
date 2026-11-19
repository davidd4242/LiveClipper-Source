using LiveClipper.Domain;
using System.Text.Json;
namespace LiveClipper.Core;
public sealed record RoughCutRestoreSnapshot(int Version,int Fps,double SourceDuration,RoughCutSourceRange[] BeforeRanges,TranscriptSegment[] Transcript,string GeneratedFingerprint);
public sealed record RoughCutRestoreResult(int Revision,EditorProject Project);
public sealed record RoughCutNaturalBoundary(int BoundaryIndex,int Frames,double Duration);
public sealed record RoughCutNaturalTransitions(RoughCutNaturalBoundary[] Boundaries,double TotalShortening,double Duration);
public sealed record RoughCutCopyResult(string ClipId,string Name,long Revision,RoughCutNaturalTransitions NaturalTransitions);
public sealed record RoughCutSourceRange(double Start,double End);
/// <summary>New independent source-only sequence; never mutates the source project.</summary>
public sealed class RoughCutCopyService(Store store,StudioStore studio,string destinationRoot)
{
    public async Task<RoughCutCopyResult> CreateAsync(Clip clip,string name,EditorProject snapshot,double sourceDuration,RoughCutSourceRange[] ranges,string sourceSignature,string candidateHash,string transcriptCacheKey,long revision,CancellationToken token=default,RoughCutSourceRange[]? beforeRanges=null,int[]? sceneBoundaryIndexes=null,double transitionDurationSeconds=.8)
    {
        var preview=new RoughCutPreviewService(store,studio).Preview(clip,snapshot,sourceDuration);
        if(preview.SourceSignature!=sourceSignature||preview.CandidateHash!=candidateHash||preview.TranscriptCacheKey!=transcriptCacheKey)throw new ProjectCopyConflictException("源素材、字幕或候选已变化，请重新预览");
        var project=Build(clip.Id,snapshot,preview.Transcript,sourceDuration,ranges,sceneBoundaryIndexes,transitionDurationSeconds);
        var before=beforeRanges??ranges;
        _=Build(clip.Id,snapshot,preview.Transcript,sourceDuration,before);
        if(ranges.Any(r=>!before.Any(b=>r.Start>=b.Start&&r.End<=b.End)))throw new ArgumentException("保留片段必须位于生成前选定的源区间内");
        var restore=JsonSerializer.Serialize(new RoughCutRestoreSnapshot(1,snapshot.Fps,sourceDuration,before,preview.Transcript,Fingerprint(project,clip.Id)));
        var copy=await new ProjectCopyService(studio,destinationRoot).CopyAsync(clip.Id,name,project,revision,token,restore);
        var cuts=EditorRenderService.ResolveTransitions(project);var fps=EditorRenderService.TransitionFps(project);
        var boundaries=cuts.Select(c=>new RoughCutNaturalBoundary(Array.FindIndex(project.Timeline!,x=>x.Id==c.ItemId),(int)Math.Round(c.EffectiveDuration*fps),c.EffectiveDuration)).ToArray();
        return new(copy.ClipId,copy.Name,copy.Revision,new(boundaries,cuts.Sum(EditorRenderService.CutShift),project.End));
    }
    public RoughCutRestoreResult Restore(string clipId,int expectedRevision,string? sessionId=null)=>EditorRevisionStore.Serialized(studio,clipId,()=>
    {
        EditorRevisionStore.EnsureSession(clipId,sessionId,history:true);
        var record=studio.GetProjectRecord(clipId)??throw new ArgumentException("新工程不存在");
        if(record.Revision!=expectedRevision)throw new ProjectCopyConflictException("新工程已变化，请重新读取后恢复");
        var json=studio.Document("roughcut-restore",clipId)??throw new ArgumentException("此工程没有自动粗剪前快照");
        var state=JsonSerializer.Deserialize<RoughCutRestoreSnapshot>(json)??throw new ArgumentException("恢复快照无效");
        if(state.Version is not (1 or 2))throw new NotSupportedException("恢复快照版本暂不支持");
        var current=EditorProjectCodec.Deserialize(record.Json);
        var sourceId=clipId;
        if(state.Version==2)
        {
            if(studio.GetIndependentProject(clipId) is null)throw new ProjectCopyConflictException("恢复目标不是独立结果工程");
            var ids=(current.Timeline??[]).Select(x=>x.ClipId).Distinct().ToArray();
            if(ids.Length!=1)throw new ProjectCopyConflictException("结果工程已不再是生成的单源时间线，未覆盖");
            sourceId=ids[0];
        }
        if(Fingerprint(current,sourceId)!=state.GeneratedFingerprint)throw new ProjectCopyConflictException("新工程已包含生成后的编辑；为避免覆盖，请先另存副本，不能直接恢复自动粗剪前快照");
        var next=Build(sourceId,new EditorProject{Fps=state.Fps,CanvasWidth=current.CanvasWidth,CanvasHeight=current.CanvasHeight},state.Transcript,state.SourceDuration,state.BeforeRanges);
        if(state.Version==2)next=next with {CaptionOverrides=new(){[sourceId]=state.Transcript.ToArray()}};
        var commit=EditorRevisionStore.Commit(studio,clipId,JsonSerializer.Serialize(next),expectedRevision);
        if(!commit.Ok)throw new ProjectCopyConflictException("新工程已变化，恢复未写入");
        EditorRevisionStore.RecordWrite(clipId,record.Json);EditorRevisionStore.TryAdvance(clipId,expectedRevision,out var revision);
        return new RoughCutRestoreResult(revision,next);
    });
    internal static string Fingerprint(EditorProject project,string sourceId)
    {
        var normalized=StudioProjectMediaRefs.Remap(project,new Dictionary<string,string>{{sourceId,"source"}});
        var ids=(normalized.Timeline??[]).Select((x,i)=>(x.Id,Value:"item-"+i)).ToDictionary(x=>x.Id,x=>x.Value);
        normalized=normalized with {Transitions=normalized.Transitions?.Select((t,i)=>t with {Id="transition-"+i,ItemId=ids[t.ItemId]}).ToArray(),Timeline=normalized.Timeline?.Select(x=>x with {Id=ids[x.Id]}).ToArray(),TimelineCaptions=normalized.TimelineCaptions?.Select((c,i)=>c with {Id="caption-"+i,SequenceItemId=c.SequenceItemId is {} item&&ids.TryGetValue(item,out var stable)?stable:c.SequenceItemId}).ToArray()};
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized)))).ToLowerInvariant();
    }
    internal static EditorProject Build(string sourceId,EditorProject snapshot,TranscriptSegment[] transcript,double duration,RoughCutSourceRange[] ranges,int[]? sceneBoundaryIndexes=null,double transitionDurationSeconds=.8)
    {
        if(ranges.Length is <1 or >500)throw new ArgumentException("独立粗剪需要1至500个保留片段");
        if(ranges.Any(r=>!double.IsFinite(r.Start)||!double.IsFinite(r.End)||r.Start<0||r.End<=r.Start||r.End>duration))throw new ArgumentException("保留区间超出源素材或为空");
        var items=ranges.Select(r=>new TimelineItem(Guid.NewGuid().ToString("N"),sourceId,r.Start,r.End)).ToArray();
        // AI rough cuts are hard cuts only. Keep legacy request fields accepted, but never
        // turn scene boundaries into transitions. Existing source projects are not mutated.
        EditorTransition[] transitions=[];
        TransitionCut[] cuts=[];
        var starts=EditorRenderService.SequenceStarts(items,cuts);
        var captions=new List<TimelineCaption>();

        foreach(var item in items)
        {
            var offset=starts[item.Id];
            for(var i=0;i<transcript.Length;i++)
            {
                var cue=transcript[i];var start=Math.Max(item.Start,cue.Start);var end=Math.Min(item.End,cue.End);
                if(end<=start)continue;
                captions.Add(new(){Id=Guid.NewGuid().ToString("N"),Start=offset+start-item.Start,End=offset+end-item.Start,Text=cue.Text,SequenceItemId=item.Id,SourceCueIndex=i,SourceStart=start,SourceEnd=end});
            }

        }
        var result=new EditorProject{Start=0,End=EditorRenderService.SequenceDuration(items,cuts),Transitions=transitions.Length==0?null:transitions,Timeline=items,Fps=snapshot.Fps,CanvasWidth=snapshot.CanvasWidth,CanvasHeight=snapshot.CanvasHeight,Resolution=0,SourceTranscripts=new(){[sourceId]=transcript.ToArray()},TimelineCaptions=captions.ToArray()};
        EditorRenderService.Validate(result);return result;
    }
}
