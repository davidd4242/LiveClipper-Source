using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core;

/// <summary>Explicit source analysis and derived result creation, never a first-clip surrogate.</summary>
public sealed class IndependentRoughCutService(Store store,StudioStore studio,string destinationRoot)
{
    public Clip Source(string projectId,string sourceId)
    {
        if(studio.GetIndependentProject(projectId) is null)throw new ArgumentException("目标不是独立工程");
        return store.Clips().FirstOrDefault(x=>x.Id==sourceId)??throw new ArgumentException("所选素材没有真实登记");
    }
    public EditorProject ResolveSnapshot(string projectId,string sourceId,EditorProject project)
    {
        var source=Source(projectId,sourceId);
        var identity=studio.GetIndependentProject(projectId)!.Identity;
        if(project.Fps!=identity.Fps)throw new ArgumentException("工程帧率与登记不一致");
        // Explicit current-project override wins, otherwise use the source's edited effective text.
        var effective=project.CaptionOverrides?.ContainsKey(sourceId)==true
            ?EffectiveTranscript.Resolve(project,sourceId,store.TranscriptJson(source.EventId))
            :EffectiveTranscript.FromProjectJson(studio.Project(sourceId),sourceId,store.TranscriptJson(source.EventId));
        return project with {CaptionOverrides=new(){[sourceId]=effective.Segments}};
    }
    public RoughCutPreview Preview(string projectId,string sourceId,EditorProject project,double duration)
        =>new RoughCutPreviewService(store,studio).Preview(Source(projectId,sourceId),ResolveSnapshot(projectId,sourceId,project),duration);

    public async Task<AiAnalysisResult> AnalyzeAsync(string projectId,string sourceId,EditorProject project,long revision,
        Func<Clip,EditorProject,CancellationToken,Task<AiAnalysisResult>> analyze,CancellationToken token=default)
    {
        var source=Source(projectId,sourceId);
        if(studio.GetIndependentProject(projectId)!.Document.Revision!=revision)throw new ProjectCopyConflictException("工程已变化，尚未请求分析");
        var snapshot=ResolveSnapshot(projectId,sourceId,project);
        var before=Preview(projectId,sourceId,project,0);
        var result=await analyze(source,snapshot,token);
        var current=Preview(projectId,sourceId,project,0);
        if(studio.GetIndependentProject(projectId)?.Document.Revision!=revision||before.TranscriptCacheKey!=current.TranscriptCacheKey||before.SourceSignature!=current.SourceSignature)
            throw new ProjectCopyConflictException("分析期间工程、源字幕或媒体已变化；候选/分析缓存可能已更新，时间线未改，请重新读取并核对");
        return result;
    }
    public RoughCutPreview Verify(string projectId,string sourceId,EditorProject project,double duration,string sourceSignature,string candidateHash,string transcriptCacheKey)
    {
        var current=Preview(projectId,sourceId,project,duration);
        if(current.SourceSignature!=sourceSignature||current.CandidateHash!=candidateHash||current.TranscriptCacheKey!=transcriptCacheKey)
            throw new ProjectCopyConflictException("源素材、字幕或候选已改变，请重新读取");
        return current;
    }
    public async Task<RoughCutCopyResult> CreateAsync(string projectId,string sourceId,string name,EditorProject snapshot,double duration,
        RoughCutSourceRange[] ranges,RoughCutSourceRange[] beforeRanges,string sourceSignature,string candidateHash,string transcriptCacheKey,
        long revision,CancellationToken token=default,int[]? sceneBoundaryIndexes=null,double transitionDurationSeconds=.8)
    {
        if(beforeRanges is null)throw new ArgumentException("缺少生成前选择，不能创建不可恢复的结果");
        var preview=Verify(projectId,sourceId,snapshot,duration,sourceSignature,candidateHash,transcriptCacheKey);
        var project=RoughCutCopyService.Build(sourceId,snapshot,preview.Transcript,duration,ranges,sceneBoundaryIndexes,transitionDurationSeconds)
            with {CaptionOverrides=new(){[sourceId]=preview.Transcript.ToArray()}};
        _=RoughCutCopyService.Build(sourceId,snapshot,preview.Transcript,duration,beforeRanges);
        if(ranges.Any(r=>!beforeRanges.Any(b=>r.Start>=b.Start&&r.End<=b.End)))throw new ArgumentException("结果区间须包含在生成前选择内");
        var restore=JsonSerializer.Serialize(new RoughCutRestoreSnapshot(2,snapshot.Fps,duration,beforeRanges,preview.Transcript,RoughCutCopyService.Fingerprint(project,sourceId)));
        var copy=await new ProjectCopyService(studio,destinationRoot).CopyAsync(projectId,name,project,revision,token,restore,
            generatedSourceOnly:true,validateBeforePublish:()=>Verify(projectId,sourceId,snapshot,duration,sourceSignature,candidateHash,transcriptCacheKey));
        var cuts=EditorRenderService.ResolveTransitions(project);var fps=EditorRenderService.TransitionFps(project);
        var boundaries=cuts.Select(c=>new RoughCutNaturalBoundary(Array.FindIndex(project.Timeline!,x=>x.Id==c.ItemId),(int)Math.Round(c.EffectiveDuration*fps),c.EffectiveDuration)).ToArray();
        return new(copy.ClipId,copy.Name,copy.Revision,new(boundaries,cuts.Sum(EditorRenderService.CutShift),project.End));
    }
}
