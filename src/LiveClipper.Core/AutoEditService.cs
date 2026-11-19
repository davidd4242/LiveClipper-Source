using System.Collections.Concurrent;
using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core;

/// <summary>一段静音（气口）在源视频中的位置。</summary>
public sealed record SilenceRange(double Start,double End)
{
    public double Seconds=>Math.Max(0,End-Start);
}

/// <summary>转录完成后自动提炼 + AI 分析 + 剪气口的结果，供编辑器展示。</summary>
public sealed record AutoEditReport(
    string ClipId,string Status,string Source,string Title,string Summary,double Start,double End,
    int KeepCount,int SegmentCount,int EventCount,double TrimmedSeconds,
    IReadOnlyList<int> Keep,IReadOnlyList<int> Hooks,IReadOnlyList<string> Reasons,
    string? Error,DateTimeOffset UpdatedAt)
{
    public SourceSilenceCandidate[] SilenceCandidates {get;init;}=[];
}

/// <summary>用 ffmpeg silencedetect 找出句子之间的空白，并把它们从时间线里裁掉。</summary>
public sealed class SilenceTrimService(MediaTools media,Func<string>? pythonPath=null)
{
    // 同一文件同样的检测参数直接复用上次结果：全片解码是自动剪气口最耗时的一步，重复点击不应重复解码。
    private readonly ConcurrentDictionary<string,(string Signature,IReadOnlyList<SilenceRange> Ranges)> _cache=new();
    private readonly ConcurrentDictionary<string,double> _progress=new();
    /// <summary>某个视频的静音检测进度（0–1），编辑器在等待期间轮询它来显示百分比。</summary>
    public double ProgressOf(string clipId)=>_progress.TryGetValue(clipId,out var value)?value:0;

    public async Task<SourceSilencePreview> PlanAsync(string sourceId,string path,double noiseDb,double minSeconds,IReadOnlyList<TranscriptSegment> transcript,int fps,CancellationToken token)
    {
        var duration=await media.DurationAsync(path,token);
        return await new SourceSilencePreviewService(media,pythonPath).PreviewAsync(sourceId,path,0,duration,noiseDb,minSeconds,token,transcript,fps);
    }

    public async Task<IReadOnlyList<SilenceRange>> DetectAsync(string clipId,string path,double noiseDb,double minSeconds,CancellationToken token)
    {
        if(!double.IsFinite(noiseDb)||noiseDb is >0 or < -90)throw new ArgumentException("静音判定音量需在 -90dB 到 0dB 之间");
        if(!double.IsFinite(minSeconds)||minSeconds is <.2 or >30)throw new ArgumentException("最短气口时长需在 0.2–30 秒之间");
        var cacheKey=Path.GetFullPath(path);
        var signature=$"{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}|{noiseDb}|{minSeconds}";
        if(_cache.TryGetValue(cacheKey,out var cached)&&cached.Signature==signature)return cached.Ranges;
        var duration=await media.DurationAsync(path,token);
        if(clipId.Length>0)_progress[clipId]=0;
        IReadOnlyList<SilenceRange> ranges;
        try
        {
            // Both legacy and review paths use identical PTS-preserving measurement and stream restrictions.
            var measured=await new SourceSilencePreviewService(media).PreviewAsync(clipId,path,0,duration,noiseDb,minSeconds,token);
            ranges=measured.Silences.Select(x=>new SilenceRange(x.Start,x.End)).ToArray();
        }
        finally {if(clipId.Length>0)_progress.TryRemove(clipId,out _);}
        _cache[cacheKey]=(signature,ranges);
        return ranges;
    }

    /// <summary>从时间线片段中扣除静音区间；两端各保留 padding 秒的呼吸感，过碎的残片直接丢弃。</summary>
    public static TimelineItem[] Trim(IReadOnlyList<TimelineItem> items,IReadOnlyList<SilenceRange> silences,double padding=SilenceCutPlanner.Padding,double minKeepSeconds=0)
    {
        if(silences.Count==0)return items.ToArray();
        padding=Math.Clamp(double.IsFinite(padding)?padding:0,0,1);
        var ordered=silences.Where(x=>x.Seconds>0).OrderBy(x=>x.Start).ToArray();
        var result=new List<TimelineItem>();
        foreach(var item in items)
        {
            // 钩子与插入素材保持原样，避免把刻意保留的留白剪掉。
            if(item.Kind!="video"){result.Add(item);continue;}
            var pieces=new List<(double Start,double End)>{(item.Start,item.End)};
            foreach(var silence in ordered)
            {
                var cutStart=silence.Start+padding;var cutEnd=silence.End-padding;
                if(cutEnd-cutStart<=0)continue;
                var next=new List<(double Start,double End)>();
                foreach(var piece in pieces)
                {
                    if(cutEnd<=piece.Start||cutStart>=piece.End){next.Add(piece);continue;}
                    if(cutStart>piece.Start)next.Add((piece.Start,cutStart));
                    if(cutEnd<piece.End)next.Add((cutEnd,piece.End));
                }
                pieces=next;
            }
            var kept=pieces.Where(x=>x.End>x.Start).ToArray();
            // 全部被判为静音时保留原片段，宁可少剪也不要剪没了。
            if(kept.Length==0){result.Add(item);continue;}
            foreach(var (piece,index) in kept.Select((value,index)=>(value,index)))
                result.Add(item with {
                    Id=Guid.NewGuid().ToString("N"),
                    Start=piece.Start,
                    End=piece.End,
                    GapBefore=index==0?item.GapBefore:0,
                    FadeIn=index==0?item.FadeIn:0,
                    FadeOut=index==kept.Length-1?item.FadeOut:0,
                });
        }
        return result.Count==0?items.ToArray():result.ToArray();
    }

    public static double TrimmedSeconds(IReadOnlyList<TimelineItem> before,IReadOnlyList<TimelineItem> after)=>
        Math.Max(0,before.Sum(x=>x.End-x.Start)-after.Sum(x=>x.End-x.Start));
}

/// <summary>
/// 转录完成后的自动流程：提炼事件 → AI（或规则）逐句分析 → 预勾选保留句 → 未勾选句子在时间线中分割删除 → 自动剪气口。
/// 结果写入编辑器工程，前端检测到新版本后会自动同步。
/// </summary>
public sealed class AutoEditService(Store store,StudioStore studio,StudioMediaService mediaService,AiAnalysisService ai,EditPlanningService planning,SilenceTrimService silence,MediaTools media)
{
    private readonly SemaphoreSlim _worker=new(1,1);
    /// <summary>返回当前可用的 AI 连接；返回 null 表示走规则方案。</summary>
    public Func<AiConnection?>? ConnectionFactory {get;set;}
    public bool AutoAnalyze {get;set;}=true;
    public bool AutoTrimSilence {get;set;}=true;
    public double SilenceNoiseDb {get;set;}=SilenceCutPlanner.NoiseDb;
    public double SilenceMinSeconds {get;set;}=SilenceCutPlanner.MinSeconds;
    public double SilencePadding {get;set;}=SilenceCutPlanner.Padding;
    /// <summary>某个视频的自动方案已更新（参数为 clipId）。</summary>
    public event Action<string>? Changed;

    public AutoEditReport? Report(string clipId)
    {
        var json=studio.Document("auto-edit",clipId);
        return json is null?null:JsonSerializer.Deserialize<AutoEditReport>(json);
    }

    /// <summary>Generate a rules-only preview without writing projects, documents, events, jobs or history.</summary>
    public AutoEditProposal GeneratePlan(Clip clip,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        token.ThrowIfCancellationRequested();
        // Read the pair in one SELECT; do not bind/reset editor history just to preview a proposal.
        var baseline=EditorRevisionStore.Serialized(clip.Id,()=>studio.GetProjectRecord(clip.Id));
        var transcript=EffectiveTranscript.FromProjectJson(baseline?.Json,clip.Id,store.TranscriptJson(clip.EventId)).CanonicalJson;
        return AutoEditProposalPlanning.Generate(clip,baseline,EditorRevisionStore.EpochFor(studio),transcript,token);
    }

    /// <summary>Explicitly apply one proposal using the editor's lock, CAS and single undo checkpoint.</summary>
    public AutoEditProposalApplyResult ApplyProposal(Clip clip,AutoEditProposal proposal,string? sessionId=null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(proposal);
        return EditorRevisionStore.Serialized(studio,clip.Id,()=>
        {
            var saved=studio.GetProjectRecord(clip.Id);
            var revision=checked((int)(saved?.Revision??0));
            AutoEditProposalApplyResult Reject(string code,string error,bool conflict=true)=>new(false,conflict,code,revision,error);
            if(!string.Equals(clip.Id,proposal.ClipId,StringComparison.Ordinal))return Reject("proposal-clip-mismatch","方案不属于当前素材");
            if(!string.Equals(EditorRevisionStore.EpochFor(studio),proposal.Epoch,StringComparison.Ordinal))return Reject("stale-epoch","服务端已重启，请重新生成方案");
            if(revision!=proposal.BaseRevision)return Reject("stale-revision",$"工程已更新（当前 revision {revision}），请重新生成方案");
            if(!string.Equals(AutoEditProposalPlanning.Hash(saved?.Json),proposal.BaseHash,StringComparison.Ordinal))return Reject("stale-project-hash","工程基线 hash 不匹配，请重新生成方案");
            var transcript=EffectiveTranscript.FromProjectJson(saved?.Json,clip.Id,store.TranscriptJson(clip.EventId));
            if(!string.Equals(AutoEditProposalPlanning.Hash(transcript.CanonicalJson),proposal.TranscriptHash,StringComparison.Ordinal))return Reject("stale-transcript","字幕已更新，请重新生成方案");
            // A proposal is a standalone undo step; do not merge it into an active drag/edit group.
            try{EditorRevisionStore.EnsureSession(clip.Id,sessionId,history:true);}
            catch(EditorRevisionConflictException conflict){return Reject(conflict.Code,conflict.Message);}
            if(proposal.Timeline is not {Length:>0}||proposal.Timeline.Any(x=>x is null))return Reject("invalid-proposal","方案没有可应用的有效时间线",false);

            var timeline=proposal.Timeline.ToArray();
            EditorProject current;
            EditorProject next;
            try
            {
                current=AutoEditProposalPlanning.ReadProject(saved?.Json,timeline.Max(x=>x.End));
                // Only Timeline is replaced; Transitions/NestedSequences and every unrelated project field remain current.
                next=current with {Timeline=timeline};
                EditorRenderService.Validate(current);
                EditorRenderService.Validate(next);
                if(!AutoEditProposalPlanning.PreservesProtectedItems(current,clip.Id,timeline))return Reject("protected-timeline","方案改变了锁定片段、转场锚点或嵌套引用",false);
                var flattened=EditorRenderService.FlattenProject(next);
                EditorRenderService.Validate(flattened);
                var mediaIds=store.Clips().Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
                if(EditorRenderService.Items(flattened,clip.Id).Any(x=>!mediaIds.Contains(x.ClipId)))return Reject("missing-media","方案引用了不存在的媒体",false);
            }
            catch(ArgumentException invalid){return Reject("invalid-proposal",invalid.Message,false);}
            if(saved is not null&&AutoEditProposalPlanning.SameTimeline(EditorRenderService.Items(current,clip.Id),timeline))return Reject("no-changes","方案未改变时间线",false);
            var committed=EditorRevisionStore.Commit(studio,clip.Id,AutoEditProposalPlanning.InstallTimeline(saved?.Json,next),proposal.BaseRevision);
            if(!committed.Ok)return new AutoEditProposalApplyResult(false,true,"stale-revision",committed.Revision,"工程已被其他写入者修改，请重新生成方案");
            EditorRevisionStore.RecordWrite(clip.Id,saved?.Json??JsonSerializer.Serialize(current));
            EditorRevisionStore.TryAdvance(clip.Id,proposal.BaseRevision,out var appliedRevision);
            return new AutoEditProposalApplyResult(true,false,"applied",appliedRevision);
        });
    }

    public void Queue(Clip clip)=>_=Task.Run(()=>RunAsync(clip,CancellationToken.None));

    public async Task<AutoEditReport> RunAsync(Clip clip,CancellationToken token)
    {
        await _worker.WaitAsync(token);
        try{return await ExecuteAsync(clip,token);}
        finally{_worker.Release();}
    }

    private async Task<AutoEditReport> ExecuteAsync(Clip clip,CancellationToken token)
    {
        var savedProject=studio.Project(clip.Id);
        var originalTranscript=store.TranscriptJson(clip.EventId);
        var effective=EffectiveTranscript.FromProjectJson(savedProject,clip.Id,originalTranscript);
        IReadOnlyList<TranscriptSegment> segments=effective.Segments;
        if(segments.Count==0)return Save(new(clip.Id,"empty","none","","",0,0,0,0,0,0,[],[],[],"尚无可分析的字幕",DateTimeOffset.UtcNow));
        // 先写一条 running 报告，编辑器能立刻显示“正在自动提炼”。
        Save(new(clip.Id,"running","none","","",0,0,0,segments.Count,0,0,[],[],[],null,DateTimeOffset.UtcNow));
        var duration=await media.DurationAsync(clip.Path,token);
        // 取整段视频作为可选范围；探测时长失败时退回最后一句的结束时间。
        var end=duration>0?duration:segments[^1].End;
        if(!(end>0))return Save(new(clip.Id,"failed","none","","",0,0,0,segments.Count,0,0,[],[],[],"无法读取视频时长",DateTimeOffset.UtcNow));
        // Clamp the effective snapshot without changing its indices; planning consumes this same snapshot.
        segments=segments
            .Select(s=>s with {Start=Math.Clamp(s.Start,0,end),End=Math.Clamp(s.End,0,end)})
            .ToList();
        if(!segments.Any(s=>s.End>s.Start))return Save(new(clip.Id,"empty","none","","",0,0,0,0,0,0,[],[],[],"字幕不落在视频时长内",DateTimeOffset.UtcNow));

        var eventCount=0;
        try{eventCount=mediaService.ExtractEvents(clip).Count;}
        catch(Exception ex)when(ex is not OperationCanceledException){/* 提炼失败不影响后续剪辑建议 */}

        var analysisProject=AutoEditProposalPlanning.ReadProject(savedProject,end);
        if(analysisProject.Transitions is {Length:>0})
            return Save(new(clip.Id,"failed","none","","",0,0,0,segments.Count,eventCount,0,[],[],[],"当前工程已有手工转场，自动整段替换已停止以保留原编辑；请使用人工审核方案",DateTimeOffset.UtcNow));
        AiConnection? connection=null;
        if(AutoAnalyze)
        {
            try{connection=ConnectionFactory?.Invoke();}
            catch(Exception ex)when(ex is not OperationCanceledException)
            {
                return Save(new(clip.Id,"failed","none","","",0,0,0,segments.Count,eventCount,0,[],[],[],$"AI 配置错误：{ex.Message}",DateTimeOffset.UtcNow));
            }
        }
        if(AutoAnalyze&&connection is null)
            return Save(new(clip.Id,"failed","none","","",0,0,0,segments.Count,eventCount,0,[],[],[],"AI 未配置：已选择 AI 自动分析，但没有可用的模型连接；请配置模型，或关闭自动 AI 分析改用规则方案",DateTimeOffset.UtcNow));
        if(connection is not null)
        {
            try{var items=await ai.AnalyzeAsync(clip,connection,token,analysisProject);eventCount=Math.Max(eventCount,items.Events.Count);}
            catch(Exception ex)when(ex is not OperationCanceledException)
            {
                return Save(new(clip.Id,"failed","none","","",0,0,0,segments.Count,eventCount,0,[],[],[],$"AI 事件分析失败：{ex.Message}",DateTimeOffset.UtcNow));
            }
        }

        CutSuggestion suggestion;
        var error=(string?)null;
        try{suggestion=await planning.CutWithTranscriptAsync(clip,0,end,connection,token,effective);}
        catch(Exception ex)when(ex is not OperationCanceledException)
        {
            var stage=connection is null?"规则剪辑方案失败":"AI 剪辑方案失败";
            return Save(new(clip.Id,"failed","none","","",0,0,0,segments.Count,eventCount,0,[],[],[],$"{stage}：{ex.Message}",DateTimeOffset.UtcNow));
        }

        var keep=suggestion.Keep.Where(i=>i>=0&&i<segments.Count).Distinct().Order().ToArray();
        if(keep.Length==0)
            return Save(new(clip.Id,"failed","none","","",0,0,0,segments.Count,eventCount,0,[],[],[],"没有有效的保留方案：未保存也未应用时间线",DateTimeOffset.UtcNow));
        var timeline=EditorRenderService.SentenceTimeline(clip.Id,segments,keep,suggestion.Start,suggestion.End);
        var trimmed=0d;
        var trimBackup=(TimelineItem[]?)null;
        SourceSilenceCandidate[] silenceCandidates=[];
        if(AutoTrimSilence&&timeline.Length>0)
        {
            try
            {
                var measured=await silence.PlanAsync(clip.Id,clip.Path,SilenceNoiseDb,SilenceMinSeconds,segments,analysisProject?.Fps??30,token);
                var plan=measured.Silences;
                silenceCandidates=plan;
                var after=SilenceTrimService.Trim(timeline,SilenceCutPlanner.AutomaticCuts(plan),0);
                trimmed=SilenceTrimService.TrimmedSeconds(timeline,after);
                // 只有真的剪短了才留备份：编辑器里「复原自动剪气口」靠它还原。
                if(trimmed>0.01){trimBackup=timeline;timeline=after;}
            }
            catch(Exception ex)when(ex is not OperationCanceledException){error??=$"自动剪气口失败：{ex.Message}";}
        }

        // Keep the legacy persisted selection in original parsed-array coordinates. Do not turn ASR into a permanent override.
        var selected=keep;
        var hooks=suggestion.Hooks.ToArray();
        if(effective.Source=="original")
        {
            var originalIndices=TranscriptExport.Parse(originalTranscript!).Segments.Select((segment,index)=>(segment,index))
                .Where(x=>EditorRenderService.NormalizeCaptions([x.segment]).Length>0).Select(x=>x.index).ToArray();
            selected=keep.Select(index=>originalIndices[index]).ToArray();
            hooks=hooks.Select(index=>originalIndices[index]).ToArray();
        }
        // 读、改、校验、保存都在 EditorRevisionStore 的 clip 锁内完成，与 PUT/commands 串行；保存后 revision+1、旧工程进 undo、活动会话结束。
        try{EditorRevisionStore.SaveExternal(studio,clip.Id,"auto-edit",saved=>{
        if(EffectiveTranscript.FromProjectJson(saved,clip.Id,store.TranscriptJson(clip.EventId)).CacheKey!=effective.CacheKey)throw new ArgumentException("字幕已更新，请重新生成自动方案");
        var project=(saved is null?null:JsonSerializer.Deserialize<EditorProject>(saved))??new EditorProject();
        RequireNoManualTransitionsForReplacement(project);
        project=project with {Start=suggestion.Start,End=suggestion.End,SelectedSegments=selected,SentenceMode=true,Timeline=timeline,
            Transitions=Array.Empty<EditorTransition>(),NestedSequences=null,
            Markers=(project.Markers??Array.Empty<double>()).Where(m=>m>=suggestion.Start&&m<=suggestion.End).ToArray(),
            SilenceTrimBackup=trimBackup,SilenceTrimmed=trimBackup is not null,SilenceTrimmedSeconds=Math.Round(trimmed,3)};
        EditorRenderService.Validate(project);return JsonSerializer.Serialize(project);});}
        catch(ArgumentException ex){error??=ex.Message;}

        return Save(new(clip.Id,error is null?"ready":"partial",suggestion.Source,suggestion.Title,suggestion.Summary,suggestion.Start,suggestion.End,
            keep.Length,segments.Count,eventCount,Math.Round(trimmed,2),selected,hooks,suggestion.Reasons,error,DateTimeOffset.UtcNow){SilenceCandidates=silenceCandidates});
    }

    internal static void RequireNoManualTransitionsForReplacement(EditorProject project)
    {
        if(project.Transitions is {Length:>0})throw new ArgumentException("当前工程已有手工转场，不能由自动整段替换删除；原工程未改动");
    }

    private AutoEditReport Save(AutoEditReport report)
    {
        studio.SaveDocument("auto-edit",report.ClipId,JsonSerializer.Serialize(report));
        Changed?.Invoke(report.ClipId);
        return report;
    }
}
