using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed class HighlightService(Store store,MediaTools media,string storageRoot,int preRoll=200,int postRoll=300,int mergeGap=90,int danmakuLimit=1000,IClock? clock=null)
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly IClock _clock=clock??new SystemClock();
    public int PreRollSeconds {get;set;}=preRoll;
    public int PostRollSeconds {get;set;}=postRoll;
    /// <summary>手动高光默认前 200 秒 + 后 200 秒（与自动切片同一口径，旧默认 300/120 由设置迁移处理）。</summary>
    public int ManualPreRollSeconds {get;set;}=200;
    public int ManualPostRollSeconds {get;set;}=200;
    public int DanmakuRecordLimit {get;set;}=danmakuLimit;
    public event Action<HighlightEvent>? Changed;
    public async Task<HighlightEvent> MarkAsync(RecordingSession session,string source,double score=1,CancellationToken token=default)
    {
        var now=Math.Max(0,(_clock.UtcNow-session.StartedAt).TotalSeconds);
        return await MarkAtAsync(session,source,now,score,token,source=="manual"?ManualPreRollSeconds:null,source=="manual"?ManualPostRollSeconds:null);
    }
    public async Task<HighlightEvent> MarkAtAsync(RecordingSession session,string source,double time,double score=1,CancellationToken token=default,int? preRollOverride=null,int? postRollOverride=null)
    {
        HighlightEvent? notification=null;
        await _gate.WaitAsync(token);
        try
        {
            var start=Math.Max(0,time-(preRollOverride??PreRollSeconds));var end=time+(postRollOverride??PostRollSeconds);
            // 弹幕高光有独立的延展/收尾阶段：已收尾的旧事件不再并入，避免把新爆发算进上一段。
            var overlap=store.Highlights(session.Id).FirstOrDefault(x=>x.State==HighlightState.Collecting && x.Source==source
                && (source!="danmaku"||!x.Closed||start<=x.End) && start<=x.End+mergeGap && end>=x.Start-mergeGap);
            // 弹幕高光的结束点由 DanmakuHeat 驱动（ExtendDanmakuAsync / CloseDanmakuAsync），这里只落触发点。
            var ev=overlap is null ? new HighlightEvent(Guid.NewGuid().ToString("N"),session.Id,source,time,start,end,HighlightState.Collecting,score)
                : overlap with { End=source=="danmaku"&&!overlap.Closed?overlap.End:Math.Max(overlap.End,end),Closed=false,Score=Math.Max(overlap.Score,score) };
            store.SaveHighlight(ev);
            await store.LockRangeAsync(session.Id,ev.Start,time+0.001,token);
            notification=ev;
            return ev;
        }
        finally {_gate.Release();if(notification is not null)Changed?.Invoke(notification);}
    }
    /// <summary>
    /// 高能持续期间把弹幕高光的结束点往后推。只有未收尾的弹幕高光可以延展；
    /// 已收尾事件返回 null，由调用方等待物化或另起一条。
    /// </summary>
    public async Task<HighlightEvent?> ExtendDanmakuAsync(RecordingSession session,string eventId,double end,CancellationToken token=default)
    {
        HighlightEvent? notification=null;
        await _gate.WaitAsync(token);
        try
        {
            var ev=store.Highlights(session.Id).FirstOrDefault(x=>x.Id==eventId && x.State==HighlightState.Collecting && x.Source=="danmaku");
            if(ev is null||ev.Closed)return null;
            if(end<=ev.End+.2)return ev;
            var updated=ev with { End=end };store.SaveHighlight(updated);
            await store.LockRangeAsync(session.Id,updated.Start,end,token);
            notification=updated;
            return updated;
        }
        finally {_gate.Release();if(notification is not null)Changed?.Invoke(notification);}
    }
    /// <summary>
    /// 热度回落到释放线以下（或强制超时）时收尾：结束点 = 收尾时刻 + 后录秒数，
    /// 之后才允许物化。若分片已经覆盖收尾点会立即物化，否则等 OnSegmentAsync。
    /// </summary>
    public async Task<HighlightEvent?> CloseDanmakuAsync(RecordingSession session,string eventId,double closeAt,int? tailSeconds=null,CancellationToken token=default)
    {
        HighlightEvent? closed;
        await _gate.WaitAsync(token);
        try
        {
            var ev=store.Highlights(session.Id).FirstOrDefault(x=>x.Id==eventId && x.State==HighlightState.Collecting && x.Source=="danmaku");
            if(ev is null)return null;
            var end=Math.Max(ev.End,closeAt+(tailSeconds??PostRollSeconds));
            closed=ev with { End=end,Closed=true };store.SaveHighlight(closed);
            await store.LockRangeAsync(session.Id,closed.Start,end,token);
        }
        finally {_gate.Release();}
        Changed?.Invoke(closed);
        var last=store.LastSegment(session.Id);
        if(last is not null&&last.End>=closed.End)await MaterializeAsync(closed,token);
        return closed;
    }
    public async Task OnSegmentAsync(MediaSegment segment,CancellationToken token=default)
    {
        foreach(var ev in store.Highlights(segment.SessionId).Where(x=>x.State==HighlightState.Collecting && segment.End>x.Start))
        {
            if(segment.Start<ev.End)await store.LockRangeAsync(segment.SessionId,segment.Start,segment.End,token);
            // 弹幕高光要等热度回落收尾后才物化；否则持续高热会边录边切出一堆半截片段。
            if(segment.End>=ev.End&&(ev.Source!="danmaku"||ev.Closed))await MaterializeAsync(ev,token);
        }
    }
    /// <summary>
    /// 收尾一场录制里所有 Collecting 高光。
    /// allowPartial=false 用于正常关窗：只收尾分片已覆盖到结束点的事件，未覆盖的留在 Collecting，等下次启动恢复。
    /// allowPartial=true 用于崩溃/重启恢复：弹幕事件即使没等到 Closed 也强制收尾；覆盖不足时截到最后一个分片并标 Partial。
    /// </summary>
    public async Task FinalizePendingAsync(string sessionId,bool allowPartial,CancellationToken token=default)
    {
        var last=store.LastSegment(sessionId);
        foreach(var ev in store.Highlights(sessionId).Where(x=>x.State==HighlightState.Collecting).ToArray())
        {
            var canClose=ev.Source!="danmaku"||ev.Closed||allowPartial;
            if(!canClose)continue;
            // RequestedEnd 是用户最初请求的终点；End 可能已经被 partial 截短。
            // 判断“完整”必须对原始终点，否则二次恢复会把 partial 误判成完整。
            // 迁移 6 时代已经有 Partial 行，但当时的原始请求终点没有持久化。
            // 这些行的 End 可能已被截短；不能把 End 当成原始终点再清掉 Partial。
            var legacyPartial=ev.Partial&&ev.RequestedEnd<=0;
            var requestedEnd=ev.RequestedEnd>0?ev.RequestedEnd:ev.End;
            if(last is not null&&last.End>=requestedEnd)
            {
                var complete=ev with {End=requestedEnd,RequestedEnd=legacyPartial?0:requestedEnd,Closed=true,Partial=legacyPartial};
                store.SaveHighlight(complete);
                await MaterializeAsync(complete,requestedEnd,token);
                continue;
            }
            if(!allowPartial)continue;
            if(last is null||last.End<=ev.Start)
            {
                // 没有可用分片也不能抹掉已经发生过的 partial 事实；否则二次恢复
                // 会把“只覆盖到一半”的事件误报成完整/普通失败。
                var failed=ev with {State=HighlightState.Failed,Closed=true,RequestedEnd=legacyPartial?0:requestedEnd,Partial=ev.Partial};
                store.SaveHighlight(failed);Changed?.Invoke(failed);
                continue;
            }
            var partial=ev with {End=Math.Min(requestedEnd,last.End),RequestedEnd=legacyPartial?0:requestedEnd,Closed=true,Partial=true};
            store.SaveHighlight(partial);
            await MaterializeAsync(partial,requestedEnd,token);
        }
    }
    public Task MaterializeAsync(HighlightEvent ev,CancellationToken token=default)
        =>MaterializeAsync(ev,ev.End,token);

    private async Task MaterializeAsync(HighlightEvent ev,double requestedEnd,CancellationToken token)
    {
        Job? job=null;
        await _gate.WaitAsync(token);
        try
        {
            var current=store.Highlights(ev.SessionId).FirstOrDefault(x=>x.Id==ev.Id);
            if(current is null||current.State!=HighlightState.Collecting)return;
            ev=current with {State=HighlightState.Materializing};store.SaveHighlight(ev);
            job=store.PendingJob("materialize",ev.Id)
                ??new Job(Guid.NewGuid().ToString("N"),"materialize",ev.Id,"pending",0,null);
            store.SaveJob(job=job with {Status="running",Progress=0.1});
        }
        finally {_gate.Release();}
        Changed?.Invoke(ev);
        try
        {
            await store.LockRangeAsync(ev.SessionId,ev.Start,ev.End,token);
            var segments=await store.GetRangeAsync(ev.SessionId,ev.Start,ev.End,token);
            if(segments.Count==0)throw new InvalidOperationException("高光时间段没有可用的录制分片");
            store.SaveJob((job!) with {Status="running",Progress=0.3});
            var session=store.Sessions().FirstOrDefault(x=>x.Id==ev.SessionId);
            var dayFolder=DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var directory=Path.Combine(storageRoot,"clips",dayFolder,HighlightFolderName(session,ev,dayFolder));
            Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,Path.GetFileName(directory)+".mp4");
            // 先落一份含事件 ID 的元数据：失败重试时靠它认回自己上次的目录，不会再顺延编号。
            await File.WriteAllTextAsync(Path.Combine(directory,"metadata.json"),JsonSerializer.Serialize(new { ev.Id,ev.SessionId,ev.Source,ev.TriggerTime,ev.Start,ev.End,ev.Score,partial=ev.Partial,requestedEnd }),token);
            await media.ConcatAsync(segments.Select(s=>s.Path),path,token);
            store.SaveJob((job!) with {Status="running",Progress=0.8});
            var messages=store.DanmakuForHighlight(ev.SessionId,ev.Start,ev.End,ev.TriggerTime,DanmakuRecordLimit);
            await File.WriteAllLinesAsync(Path.Combine(directory,"danmaku.jsonl"),messages.Select(x=>JsonSerializer.Serialize(x)),token);
            var availableEnd=segments.Max(s=>s.End);
            var metadata=new { ev.Id,ev.SessionId,ev.Source,ev.TriggerTime,ev.Start,ev.End,ev.Score,partial=ev.Partial,requestedEnd,availableEnd,segments=segments.Select(s=>new{s.Sequence,s.Start,s.End,s.Path}) };
            await File.WriteAllTextAsync(Path.Combine(directory,"metadata.json"),JsonSerializer.Serialize(metadata,new JsonSerializerOptions{WriteIndented=true}),token);
            store.SaveClip(new Clip(Guid.NewGuid().ToString("N"),ev.Id,path,null,DateTimeOffset.UtcNow));
            ev=ev with {State=HighlightState.Ready};store.SaveHighlight(ev);
            store.ReleaseFinishedPins(ev.SessionId,ev.Start,ev.End);
            store.SaveJob((job!) with {Status="completed",Progress=1});
            Changed?.Invoke(ev);
        }
        catch(Exception ex)
        {
            ev=ev with {State=HighlightState.Failed};store.SaveHighlight(ev);Changed?.Invoke(ev);
            if(job is not null)store.SaveJob(job with {Status="failed",Error=ex.Message});
            throw;
        }
    }
    public async Task RetryAsync(string eventId,CancellationToken token=default)
    {
        var ev=store.Highlights().FirstOrDefault(x=>x.Id==eventId)??throw new InvalidOperationException("找不到高光事件");
        if(ev.State!=HighlightState.Failed)throw new InvalidOperationException("仅失败的高光可重试");
        var legacyPartial=ev.Partial&&ev.RequestedEnd<=0;
        var requestedEnd=ev.RequestedEnd>0?ev.RequestedEnd:ev.End;
        var last=store.LastSegment(ev.SessionId);
        if(last is null||last.End<=ev.Start)throw new InvalidOperationException("没有可用于重试的录制分片");
        var end=Math.Min(requestedEnd,last.End);
        var retry=ev with {State=HighlightState.Collecting,End=end,RequestedEnd=legacyPartial?0:requestedEnd,Partial=legacyPartial||end<requestedEnd};
        store.SaveHighlight(retry);
        await MaterializeAsync(retry,requestedEnd,token);
    }

    /// <summary>目录名分配串行化：两个高光同时物化时避免抢同一个名字。</summary>
    private static readonly SemaphoreSlim NameGate=new(1,1);
    /// <summary>
    /// 高光目录命名：主播名-直播日期-高光时刻（如 小明-01-10-2026-高光时刻）。
    /// 同一天同一主播输出多条时，从第二条起在末尾追加 1、2、3…；失败重试复用自己上次的目录（metadata 里记录了事件 ID）。
    /// </summary>
    private string HighlightFolderName(RecordingSession? session,HighlightEvent ev,string dayFolder)
    {
        var room=session is null?null:store.Room(session.RoomId);
        var who=OutputNaming.SafeName(room?.Streamer)??OutputNaming.SafeName(room?.Title)??"未知主播";
        var prefix=OutputNaming.HighlightPrefix(who,session?.StartedAt??DateTimeOffset.UtcNow);
        var root=Path.Combine(storageRoot,"clips",dayFolder);
        NameGate.Wait();
        try
        {
            if(Directory.Exists(root))
                foreach(var taken in Directory.EnumerateDirectories(root,prefix+"*"))
                {
                    try{if(File.ReadAllText(Path.Combine(taken,"metadata.json")).Contains("\""+ev.Id+"\""))return Path.GetFileName(taken);}catch(IOException){}catch(UnauthorizedAccessException){}
                }
            var name=prefix;var index=0;
            while(Directory.Exists(Path.Combine(root,name))&&index<999)name=prefix+(++index);
            return name;
        }
        finally{NameGate.Release();}
    }
}

/// <summary>弹幕高能触发参数：先采集固定前置基准，再按“触发线 / 释放线”两档阈值决定开切与收尾。</summary>
public sealed class DanmakuHeatOptions
{
    /// <summary>收尾后的冷却秒数；期间不重新起切，避免一场长热点被切成很多刀。</summary>
    public double CooldownSeconds {get;set;}=60;
    /// <summary>开播后用于建立弹幕基准的秒数；用户规格为前 10 分钟，窗口内不自动触发。</summary>
    public double BaselineSeconds {get;set;}=600;
    /// <summary>基准建立后，用于计算当前弹幕速率的滑动比较窗口秒数。</summary>
    public double ComparisonSeconds {get;set;}=120;
    /// <summary>触发倍率：当前窗口速率高于基准的该倍率即开始自动切片（用户规格 140%）。</summary>
    public double BaselineRatio {get;set;}=1.4;
    /// <summary>释放倍率：持续高能时把结束点不断后推，直到速率低于基准的该倍率才收尾（用户规格 120%）。</summary>
    public double ReleaseRatio {get;set;}=1.2;
    /// <summary>单条自动切片允许延展的最长秒数；超时强制收尾，避免超长热点无限后移结束点。</summary>
    public double MaxExtensionSeconds {get;set;}=1800;
    /// <summary>
    /// 释放确认秒数（A2）：速率跌破释放线后需要连续保持这么久才真正收尾；期间反弹回升则继续延展。
    /// 设为 0 表示恢复“跌破即收尾”的旧行为。默认 200 秒，与后录 200 秒对齐。
    /// </summary>
    public double ReleaseSeconds {get;set;}=200;
    /// <summary>
    /// 基准重锚间隔（A3）：基准建立后若超过该时长都没有触发自动切片，就用最近 <see cref="BaselineSeconds"/> 的
    /// 弹幕重新计算基准线，避免开场安静/中途换节目后基准永远停留在过去。默认 600 秒（10 分钟）。
    /// </summary>
    public double RebaseSeconds {get;set;}=600;
    public DanmakuHeatOptions Clamp()
    {
        var baseline=Math.Clamp(double.IsFinite(BaselineSeconds)?BaselineSeconds:600,60,3600);
        var comparison=Math.Clamp(double.IsFinite(ComparisonSeconds)?ComparisonSeconds:120,10,600);
        var trigger=Math.Clamp(double.IsFinite(BaselineRatio)?BaselineRatio:1.4,1.05,3);
        var release=Math.Clamp(double.IsFinite(ReleaseRatio)?ReleaseRatio:1.2,1.0,trigger-0.01);
        return new()
        {
            CooldownSeconds=Math.Clamp(double.IsFinite(CooldownSeconds)?CooldownSeconds:60,10,900),
            BaselineSeconds=baseline,
            ComparisonSeconds=comparison,
            BaselineRatio=trigger,
            ReleaseRatio=release,
            MaxExtensionSeconds=Math.Clamp(double.IsFinite(MaxExtensionSeconds)?MaxExtensionSeconds:1800,60,7200),
            ReleaseSeconds=Math.Clamp(double.IsFinite(ReleaseSeconds)?ReleaseSeconds:200,0,1800),
            RebaseSeconds=Math.Clamp(double.IsFinite(RebaseSeconds)?RebaseSeconds:600,60,3600)
        };
    }
}

public enum HeatSignalKind { None, Trigger, Extend, Close }

/// <summary>DanmakuHeat 每一步的状态转移：Trigger 起切、Extend 持续后推结束点、Close 收尾。</summary>
public readonly record struct HeatSignal(HeatSignalKind Kind,double At=0);

/// <summary>
/// 弹幕高能量检测（用户口径）：
/// 1. 开播后前 BaselineSeconds（默认 10 分钟）的弹幕速率作为本场初始基准，此期间不自动切片；
/// 2. 基准建立后再看 ComparisonSeconds（默认 2 分钟）的完整滑动窗口；
/// 3. 窗口速率高于基准的 BaselineRatio（默认 140%）时触发自动切片（前 200 秒 + 后 200 秒）；
/// 4. 触发后只要速率仍高于 ReleaseRatio（默认 120%）就把结束点持续向后推；
/// 5. 速率跌破释放线后还要连续保持 ReleaseSeconds（默认 200 秒）才收尾，期间回升则继续延展（A2）；
///    收尾结束点 = 收尾时刻 + 后录秒数；
/// 6. 基准建立后若连续 RebaseSeconds（默认 10 分钟）没有触发，就按最近 BaselineSeconds 的弹幕重锚基准（A3）；
/// 7. 每场会话由 LiveCoordinator 调 Reset 清空基准/比较窗/冷却，跨场次不串热度（A3）；
///    收尾后进入 CooldownSeconds 冷却，避免同一场热点被切成很多刀。
/// </summary>
public sealed class DanmakuHeat(DanmakuHeatOptions? options=null)
{
    /// <summary>样本太少的基准不可靠：前 10 分钟至少要有 5 条弹幕才允许自动触发。</summary>
    private const int MinBaselineSamples=5;
    // 基准只需要固定计数，不需要长期保留前 10 分钟的每一条弹幕。
    private int _baselineCount;
    private bool _baselineClosed;
    private readonly Queue<DanmakuEvent> _current=new();
    private bool _active;
    private double _activeStartedAt;
    private double _notBefore=double.NegativeInfinity;
    // A2：本次回落确认从什么时候开始；回升后置回 NaN，重新计时。
    private double _releaseBelowAt=double.NaN;
    // A3：最近一次基准锚点（初始基准建立或重锚）的会话时间；NaN 表示还没建立。
    private double _lastRebaseAt=double.NaN;
    private int _rebaseCount;
    private readonly HashSet<string> _countedHighlights=new();
    private readonly Dictionary<int,int> _initialBuckets=new();
    private double _baselineRate=double.NaN;
    private double _belowBaselineAt=double.NaN;
    private double _lastRebaseAttemptAt=double.NegativeInfinity;
    private readonly Queue<double> _triggerTimes=new();
    private readonly HashSet<string> _seenIds=new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder=new();
    public string RebaseReason {get;private set;}="尚未重评";
    public double? LastRebaseTime=>_rebaseCount==0?null:_lastRebaseAt;
    public int RecentTriggerCount=>_triggerTimes.Count;
    public double BaselinePercent=>Baseline>0?MessageRate/Baseline*100:0;

    // A3：按秒聚合的最近消息量，只用于重锚基准，不保留每条弹幕。
    private readonly Queue<(long Second,int Count)> _secondCounts=new();
    private long _bucketSecond=-1;
    private int _bucketCount;
    public DanmakuHeatOptions Options {get;private set;}=(options??new()).Clamp();
    public double MessageRate {get;private set;}
    public double Baseline {get;private set;}
    public double TriggerLine {get;private set;}
    public double ReleaseLine {get;private set;}
    public double Score {get;private set;}
    /// <summary>当前是否处在一条弹幕自动切片的“高热延展”阶段。</summary>
    public bool Active=>_active;
    /// <summary>是否仍在采集基准窗口。</summary>
    public bool InBaseline=>!_baselineClosed;
    /// <summary>监控是在直播中途才加入的（没有看到开播瞬间），基准只能从加入时刻估算（A3）。</summary>
    public bool BaselineIncomplete {get;private set;}
    /// <summary>当前用于计算 Baseline 的样本数；重锚后会变成最近一个基准窗口内的样本数。</summary>
    public int BaselineSamples=>_baselineCount;
    /// <summary>本场已经按最近窗口重锚过多少次基准（A3）。</summary>
    public int RebaseCount=>_rebaseCount;
    /// <summary>最近一次状态转移的时间；没有发生过返回 -1。</summary>
    public double LastSignalTime {get;private set;}=-1;
    /// <summary>当前界面展示的触发状态与阈值说明。</summary>
    public string Reason {get;private set;}="";

    public void Configure(DanmakuHeatOptions value)
    {
        Options=(value??new()).Clamp();
        // 配置改变后按新窗口重新计算；已关闭的旧基准不能继续污染新策略。
        _baselineCount=0;_baselineClosed=false;_current.Clear();
        _active=false;_activeStartedAt=0;_notBefore=double.NegativeInfinity;
        _releaseBelowAt=double.NaN;_lastRebaseAt=double.NaN;_rebaseCount=0;
        _baselineRate=double.NaN;_belowBaselineAt=double.NaN;_lastRebaseAttemptAt=double.NegativeInfinity;
        _triggerTimes.Clear();_countedHighlights.Clear();_initialBuckets.Clear();_seenIds.Clear();_seenOrder.Clear();RebaseReason="尚未重评";
        _secondCounts.Clear();_bucketSecond=-1;_bucketCount=0;
        BaselineIncomplete=false;
        LastSignalTime=-1;
    }

    /// <summary>
    /// 新一场会话开始时清空全部跨场次状态（A3）：基准样本、比较窗口、触发/延展、回落确认与冷却都不再携带。
    /// <paramref name="anchoredAtStreamStart"/> 为 false 表示监控是在直播中途才加入的，基准只能从加入时刻起估算，
    /// 此时 <see cref="BaselineIncomplete"/> 为 true，用于提示“中途加入”的口径差异。
    /// </summary>
    public void Reset(bool anchoredAtStreamStart)
    {
        _baselineCount=0;_baselineClosed=false;_current.Clear();
        _active=false;_activeStartedAt=0;_notBefore=double.NegativeInfinity;
        _releaseBelowAt=double.NaN;_lastRebaseAt=double.NaN;_rebaseCount=0;
        _baselineRate=double.NaN;_belowBaselineAt=double.NaN;_lastRebaseAttemptAt=double.NegativeInfinity;
        _triggerTimes.Clear();_countedHighlights.Clear();_initialBuckets.Clear();_seenIds.Clear();_seenOrder.Clear();RebaseReason="尚未重评";
        _secondCounts.Clear();_bucketSecond=-1;_bucketCount=0;
        LastSignalTime=-1;
        MessageRate=0;Baseline=0;TriggerLine=0;ReleaseLine=0;Score=0;
        BaselineIncomplete=!anchoredAtStreamStart;
        Reason=anchoredAtStreamStart
            ?"等待开播后采集前 600 秒弹幕基准"
            :"监控中途加入：基准将从监控开始的时刻起估算，口径与“开播起算”略有差异";
    }

    public HeatSignal Add(DanmakuEvent ev)
    {
        if(ev.Type!="danmaku")return new(HeatSignalKind.None);
        if(!_seenIds.Add(ev.Id))return new(HeatSignalKind.None);
        _seenOrder.Enqueue(ev.Id);while(_seenOrder.Count>10000)_seenIds.Remove(_seenOrder.Dequeue());
        var now=ev.SessionTime;
        if(!_baselineClosed)
        {
            if(now<=Options.BaselineSeconds){_baselineCount++;var bucket=Math.Min((int)(now/30),(int)Math.Ceiling(Options.BaselineSeconds/30)-1);_initialBuckets[bucket]=_initialBuckets.GetValueOrDefault(bucket)+1;}
            if(now>=Options.BaselineSeconds)_baselineClosed=true;
        }
        CountSecond(now);
        _current.Enqueue(ev);
        return Evaluate(now,true);
    }

    /// <summary>没有新弹幕时按分片节拍推进状态：持续高热会继续后推结束点，热度下滑会触发收尾。</summary>
    public HeatSignal Advance(double sessionTime)=>Evaluate(sessionTime,false);

    private HeatSignal Evaluate(double now,bool hasCurrentMessage)
    {
        if(!_baselineClosed&&now>=Options.BaselineSeconds)_baselineClosed=true;
        if(double.IsNaN(_lastRebaseAt)&&_baselineClosed)_lastRebaseAt=now;
        while(_current.Count>0&&now-_current.Peek().SessionTime>Options.ComparisonSeconds)_current.Dequeue();
        PruneSecondCounts(now);
        var cooling=now<_notBefore;
        var ready=_baselineClosed&&_baselineCount>=MinBaselineSamples&&now>=Options.BaselineSeconds+Options.ComparisonSeconds;
        if(_baselineClosed&&double.IsNaN(_baselineRate))_baselineRate=InitialBaselineRate();
        Baseline=double.IsNaN(_baselineRate)?_baselineCount/Options.BaselineSeconds:_baselineRate;
        if(_baselineClosed&&_baselineCount>=MinBaselineSamples)Baseline=Math.Max(.01,Baseline);
        MessageRate=_current.Count/Options.ComparisonSeconds;
        while(_triggerTimes.Count>0&&_triggerTimes.Peek()<=now-1200)_triggerTimes.Dequeue();
        if(_baselineClosed)
        {
            if(MessageRate>=Baseline)_belowBaselineAt=double.NaN;
            else if(double.IsNaN(_belowBaselineAt))_belowBaselineAt=now;
        }
        if(_baselineClosed)TryDynamicRebase(now);
        var stable=Math.Max(.01,Baseline);
        TriggerLine=stable*Options.BaselineRatio;
        ReleaseLine=stable*Options.ReleaseRatio;
        Score=Math.Clamp(MessageRate/Math.Max(.0001,TriggerLine),0,1);
        var rebaseNote=_rebaseCount>0?$"；基准已按最近300秒渐进重评 {_rebaseCount} 次":"";
        if(_active)
        {
            var heatElapsed=now-_activeStartedAt;
            if(heatElapsed>=Options.MaxExtensionSeconds)
            {
                Close(now);
                Reason=$"高能已持续 {Options.MaxExtensionSeconds:0} 秒上限，强制收尾并保留后录；近 {Options.ComparisonSeconds:0} 秒 {MessageRate:0.00}/s，释放线 {ReleaseLine:0.00}/s";
                return new(HeatSignalKind.Close,now);
            }
            if(MessageRate>ReleaseLine)
            {
                // A2：回落确认期间回升，重新开始计时，结束点继续后推。
                _releaseBelowAt=double.NaN;
                Reason=$"自动切片进行中：近 {Options.ComparisonSeconds:0} 秒 {MessageRate:0.00}/s 仍高于释放线 {ReleaseLine:0.00}/s，结束点持续后推（已 {heatElapsed:0} 秒）";
                return new(HeatSignalKind.Extend,now);
            }
            if(double.IsNaN(_releaseBelowAt))_releaseBelowAt=now;
            var held=now-_releaseBelowAt;
            if(held>=Options.ReleaseSeconds)
            {
                Close(now);
                Reason=$"高能回落已确认：近 {Options.ComparisonSeconds:0} 秒 {MessageRate:0.00}/s ≤ 释放线 {ReleaseLine:0.00}/s，且连续 {held:0}/{Options.ReleaseSeconds:0} 秒未回升，收尾并保留后录";
                return new(HeatSignalKind.Close,now);
            }
            Reason=$"高能回落确认中：近 {Options.ComparisonSeconds:0} 秒 {MessageRate:0.00}/s ≤ 释放线 {ReleaseLine:0.00}/s，已保持 {held:0}/{Options.ReleaseSeconds:0} 秒；确认前继续保留后录";
            return new(HeatSignalKind.Extend,now);
        }
        if(ready&&hasCurrentMessage&&!cooling&&MessageRate>TriggerLine)
        {
            _active=true;_activeStartedAt=now;LastSignalTime=now;_releaseBelowAt=double.NaN;
            
            Score=Math.Clamp(MessageRate/Math.Max(.0001,TriggerLine),0,1);
            Reason=$"高光已触发；近 {Options.ComparisonSeconds:0} 秒 {MessageRate:0.00}/s；当前100%基准 {Baseline:0.00}/s，{Options.BaselineRatio*100:0}%触发线 {TriggerLine:0.00}/s；{RebaseReason}";
            return new(HeatSignalKind.Trigger,now);
        }
        Reason=now<Options.BaselineSeconds
            ?$"正在采集前 {Options.BaselineSeconds:0} 秒弹幕基准（{_baselineCount} 条）{(BaselineIncomplete?"（监控中途加入，基准从加入时刻起算）":"")}，此期间不自动切片"
            :!_baselineClosed||_baselineCount<MinBaselineSamples
                ?$"基准样本不足（{_baselineCount} 条，至少 {MinBaselineSamples} 条），暂不自动切片"
            :now<Options.BaselineSeconds+Options.ComparisonSeconds
                ?$"基准已建立，等待 {Options.ComparisonSeconds:0} 秒完整比较窗口"
            :cooling
                ?$"冷却中（还需 {Math.Max(0,_notBefore-now):0} 秒）；近 {Options.ComparisonSeconds:0} 秒 {MessageRate:0.00}/s"
            :$"近 {Options.ComparisonSeconds:0} 秒 {MessageRate:0.00}/s；基准 {stable:0.00}/s；触发线 {Options.BaselineRatio*100:0}%（{TriggerLine:0.00}/s）、释放线 {Options.ReleaseRatio*100:0}%（{ReleaseLine:0.00}/s）{rebaseNote}";
        return new(HeatSignalKind.None);
    }

    // Called only after persistence returns the actual automatic event identity.
    public void RegisterHighlight(string eventId,string source,double now)
    {
        if(source!="danmaku"||!_countedHighlights.Add(eventId))return;
        while(_triggerTimes.Count>0&&_triggerTimes.Peek()<=now-1200)_triggerTimes.Dequeue();
        _triggerTimes.Enqueue(now);TryDynamicRebase(now);
        Score=Math.Clamp(MessageRate/Math.Max(.0001,TriggerLine),0,1);
        Reason=$"独立自动高光已登记；当前100%基准 {Baseline:0.000}/s，{Options.BaselineRatio*100:0}%触发线 {TriggerLine:0.000}/s；{RebaseReason}";
    }

    private double InitialBaselineRate()
    {
        var windows=(int)Math.Ceiling(Options.BaselineSeconds/30);
        if(windows<10)return _baselineCount/Options.BaselineSeconds;
        var reference=Enumerable.Range(2,windows-2).Select(i=>_initialBuckets.GetValueOrDefault(i)).Order().ToArray();
        // Sparse rooms do not provide enough evidence to distinguish greetings from normal traffic.
        if(reference.Count(x=>x>0)<reference.Length/2)return _baselineCount/Options.BaselineSeconds;
        var median=(reference[(reference.Length-1)/2]+reference[reference.Length/2])/2d;
        var effective=(double)_baselineCount;
        for(var i=0;i<2;i++)
        {
            var count=_initialBuckets.GetValueOrDefault(i);
            if(count>Math.Max(median*3,median+5))effective-=count-median;
        }
        return effective/Options.BaselineSeconds;
    }

    private void TryDynamicRebase(double now)
    {
        var quiet=!double.IsNaN(_belowBaselineAt)&&now-_belowBaselineAt>600;
        var frequent=_triggerTimes.Count>=3;
        var recoverSparse=_baselineCount<MinBaselineSamples&&now>=Options.BaselineSeconds+300;
        if((!quiet&&!frequent&&!recoverSparse)||now-_lastRebaseAttemptAt<300)return;
        _lastRebaseAttemptAt=now;
        var reason=recoverSparse?"初始样本不足，重新检查300秒样本":frequent?"1200秒内第3个独立自动高光":"连续超过600秒未达到100%基准";
        var recent=CountRecent(now,300);
        if(now<300||recent<MinBaselineSamples)
        {
            RebaseReason=$"{reason}；最近300秒仅{recent}条，保留原基准（至少5条）";
            return;
        }
        var measured=recent/300d;
        _baselineRate=recoverSparse?measured:Baseline*.75+measured*.25;Baseline=_baselineRate;
        _baselineCount=recent;_lastRebaseAt=now;_rebaseCount++;
        _triggerTimes.Clear();_belowBaselineAt=MessageRate<Baseline?now:double.NaN;
        TriggerLine=Math.Max(.01,Baseline)*Options.BaselineRatio;
        ReleaseLine=Math.Max(.01,Baseline)*Options.ReleaseRatio;
        RebaseReason=$"{reason}；最近300秒{recent}条，{(recoverSparse?"首次有效基准":"旧基准75% + 新样本25%")} = {Baseline:0.000}/s";
    }

    /// <summary>把最近弹幕按秒聚合成桶；桶只用于重锚基准，不参与比较窗口。</summary>
    private void CountSecond(double now)
    {
        var second=(long)Math.Floor(now);
        if(second==_bucketSecond){_bucketCount++;return;}
        if(_bucketSecond>=0)_secondCounts.Enqueue((_bucketSecond,_bucketCount));
        _bucketSecond=second;_bucketCount=1;
    }

    private void PruneSecondCounts(double now)
    {
        var cutoff=now-Math.Max(600,Options.BaselineSeconds);
        while(_secondCounts.Count>0&&_secondCounts.Peek().Second<=cutoff)_secondCounts.Dequeue();
    }

    private int CountRecent(double now,double windowSeconds)
    {
        var cutoff=now-windowSeconds;
        var total=_bucketSecond>cutoff?_bucketCount:0;
        foreach(var (second,count) in _secondCounts)if(second>cutoff)total+=count;
        return total;
    }

    private void Close(double now)
    {
        _active=false;_releaseBelowAt=double.NaN;_notBefore=now+Options.CooldownSeconds;
        // 刚收尾的热点不应该立刻被当成新基准；下次重锚从收尾时刻重新计时。
        LastSignalTime=now;
    }
}
