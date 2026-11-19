using System.Collections.Concurrent;
using System.Text.Json;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed class LiveSettings
{
    public long RoomId {get;set;}
    /// <summary>需要同时监控的直播间；为空时沿用 <see cref="RoomId"/>。</summary>
    public List<long> RoomIds {get;set;}=[];
    public long BilibiliUid {get;set;}
    /// <summary>B 站登录 Cookie（含 SESSDATA）。填写后弹幕用户名不会被打码。</summary>
    public string BilibiliCookie {get;set;}="";
    public string StorageRoot {get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper");
    public string? OllamaRuntimeBaseDirectory {get;set;}
    public string RecordingDirectory {get;set;}="";
    public List<long> AutoRecordingRoomIds {get;set;}=[];
    public List<long> HighlightCacheRoomIds {get;set;}=[];
    [System.Text.Json.Serialization.JsonIgnore]
    public string RecordingRoot=>string.IsNullOrWhiteSpace(RecordingDirectory)?StorageRoot:RenderStoragePaths.NormalizeDirectory(RecordingDirectory);
    public string RenderWorkDirectory {get;set;}="";
    public string ExportOutputDirectory {get;set;}="";
    public string FfmpegPath {get;set;}="ffmpeg";
    public string PythonPath {get;set;}="python";
    public string WhisperModel {get;set;}="large-v3-turbo";
    public bool AutoTranscribe {get;set;}=false;
    public bool AutoHighlight {get;set;}=true;
    /// <summary>自动切片灵敏度 0.5（保守）–2（敏感）。</summary>
    public double AutoHighlightSensitivity {get;set;}=1;
    /// <summary>自动切片触发分数阈值 0.2–0.95。</summary>
    public double AutoHighlightThreshold {get;set;}=.6;
    /// <summary>两次自动切片之间的冷却秒数。</summary>
    public double AutoHighlightCooldownSeconds {get;set;}=60;
    /// <summary>开播后用于建立弹幕基准的前置窗口；用户规格为前 10 分钟，窗口内不自动切片。</summary>
    public double AutoHighlightBaselineSeconds {get;set;}=600;
    /// <summary>基准建立后用于比较的当前弹幕滑动窗口。</summary>
    public double AutoHighlightComparisonSeconds {get;set;}=120;
    /// <summary>触发倍率：当前窗口速率超过基准的该倍率即开始自动切片；1.4 表示 140%。</summary>
    public double AutoHighlightBaselineRatio {get;set;}=1.4;
    /// <summary>释放倍率：持续高热时不断后推结束点，速率跌破该倍率后收尾；1.2 表示 120%。</summary>
    public double AutoHighlightReleaseRatio {get;set;}=1.2;
    /// <summary>单条自动切片允许延展的最长秒数，超时强制收尾。</summary>
    public double AutoHighlightMaxExtensionSeconds {get;set;}=1800;
    /// <summary>释放确认秒数（A2）：速率跌破释放线后需连续保持这么久才收尾；期间回升则继续延展。默认 200 秒。</summary>
    public double AutoHighlightReleaseSeconds {get;set;}=200;
    /// <summary>基准重锚间隔（A3）：基准建立后超过该时长没有触发，就用最近基准窗口的弹幕重算基准。默认 600 秒。</summary>
    public double AutoHighlightRebaseSeconds {get;set;}=600;
    /// <summary>转录完成后自动提炼事件、AI 分析并预勾选保留句。</summary>
    public bool AutoAnalyze {get;set;}=true;
    /// <summary>自动剪掉句子之间的气口（静音）。</summary>
    public bool AutoTrimSilence {get;set;}=true;
    public int HighlightPreRollSeconds {get;set;}=200;
    public int HighlightPostRollSeconds {get;set;}=200;
    /// <summary>设置结构版本；v2 把自动切片旧默认（300s/120%/300s）迁移到新口径（600s/140%/200s），v3 把手动高光旧默认（前 300s/后 120s）迁移到（前 200s/后 200s）。</summary>
    public int SettingsVersion {get;set;}=0;
    /// <summary>手动高光前录；口径与自动切片一致：前 200 秒 + 后 200 秒。</summary>
    public int ManualPreRollSeconds {get;set;}=200;
    public int ManualPostRollSeconds {get;set;}=200;
    public int DanmakuRecordLimit {get;set;}=1000;
    public string AiProvider {get;set;}="ollama";
    public string AiEndpoint {get;set;}="https://api.openai.com/v1";
    /// <summary>本地 Ollama 默认推荐 qwen2.5:7b-instruct（可换 3B/1.5B）；在线 provider 使用所选云端模型。</summary>
    public string AiModel {get;set;}="qwen2.5:7b-instruct";
    /// <summary>兼容 API 的密钥。设置文件里保存 DPAPI 密文（dpapi: 前缀），任何接口都不返回明文或密文。</summary>
    public string AiApiKey {get;set;}="";
    public string LocalOutputRoot {get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","exports");
    /// <summary>是否允许按需下载本地音乐模型及其 Python 运行依赖。</summary>
    public bool MusicModelDownloadAllowed {get;set;}=false;
    /// <summary>创建音乐窗口默认选择的本地模型。</summary>
    public string MusicModelId {get;set;}="facebook/musicgen-small";
    /// <summary>音乐生成后端：local（本地模型）或 custom-http（自定义 HTTP 同步 WAV 协议）。</summary>
    public string MusicGenerationProvider {get;set;}="local";
    public string MusicApiUrl {get;set;}="";
    public string MusicApiModel {get;set;}="";
    /// <summary>none 或 bearer。</summary>
    public string MusicApiAuthMode {get;set;}="none";
    /// <summary>自定义音乐 API 的密钥；设置文件里保存 DPAPI 密文，任何接口都不返回明文或密文。</summary>
    public string MusicApiKey {get;set;}="";
    /// <summary>退出软件时自动清理本次直播的缓存（回放缓存、弹幕记录、直播间与 UID 数据）。</summary>
    public bool CleanupOnExit {get;set;}=true;
}

/// <summary>单个直播间的监控快照，供状态接口使用。</summary>
public sealed record RoomMonitorState(
    long RoomId,string Title,string Streamer,bool IsLive,bool Monitoring,
    string? SessionId,RecordingState? State,DateTimeOffset? StartedAt,
    double MessageRate,double Baseline,double HeatScore,
    long DanmakuPackets,bool DanmakuAuthorized,string HeatReason,string? Error,string? DanmakuError,
    double BufferSeconds=0,bool AutoRecord=false,bool CacheActive=false,string CaptureMode="monitor-only",bool CacheEnabled=false,
    double HeatBaselinePercent=0,string HeatRebaseReason="",double? HeatRebaseAt=null,int HeatRecentTriggers=0);

/// <summary>一个直播间的录制 + 弹幕 + 高光管线。多个实例可以并行运行。</summary>
internal sealed class RoomMonitor
{
    private readonly Store _store;
    private readonly ILiveSource _source;
    private readonly SegmentRecorder _recorder;
    private readonly HighlightService _highlights;
    private readonly BilibiliDanmakuClient _danmaku;
    private readonly DanmakuHeat _heat;
    private readonly SemaphoreSlim _heatGate=new(1,1);
    private readonly object _notificationGate=new();
    private bool _heatTransitionActive;
    private readonly List<HighlightEvent> _pendingHeatNotifications=[];
    private volatile HeatView _heatView=new(0,0,0,"",0,0,false,true,0,"",null,0);
    private sealed record HeatView(double Rate,double Baseline,double Score,string Reason,double Trigger,double Release,bool Active,bool InBaseline,double Percent,string RebaseReason,double? RebaseAt,int Recent);
    private readonly LiveSettings _settings;
    private readonly Action _changed;
    private readonly Action<HighlightEvent> _highlightReady;
    private readonly Func<DanmakuEvent,DanmakuEvent> _decorate;
    private CancellationTokenSource? _monitorStop;
    private CancellationTokenSource? _danmakuStop;
    private Task? _monitorTask;
    private Task? _danmakuTask;
    private RecordingSession? _session;
    private RoomInfo? _room;
    /// <summary>当前正在延展的弹幕自动切片事件；收尾后置空。</summary>
    private string? _activeAutoClipId;
    /// <summary>是否观察到过本场次开播前的离线状态；用于区分“开播起算基准”与“中途加入估算”（A3）。</summary>
    private bool _observedOffline;

    public long RoomId {get;}
    public HighlightService Highlights=>_highlights;
    public SegmentRecorder Recorder=>_recorder;
    public RecordingSession? Session=>_session;
    public RoomInfo? Room=>_room;
    private readonly bool _cacheEnabled;
    public bool CacheEnabled {get;}
    public bool AutoRecord {get;}
    public bool Monitoring=>_monitorTask is {IsCompleted:false};
    public double MessageRate=>_heatView.Rate;
    public double Baseline=>_heatView.Baseline;
    public double HeatScore=>_heatView.Score;
    public long DanmakuPackets=>_danmaku.PacketCount;
    public bool DanmakuAuthorized=>_danmaku.Authorized;
    public string? LastCommand=>_danmaku.LastCommand;
    public string HeatReason=>_heatView.Reason;
    public double HeatTriggerLine=>_heatView.Trigger;
    public double HeatReleaseLine=>_heatView.Release;
    public bool HeatActive=>_heatView.Active;
    public bool HeatInBaseline=>_heatView.InBaseline;

    internal static DanmakuHeatOptions HeatOptions(LiveSettings settings)=>new()
    {
        CooldownSeconds=settings.AutoHighlightCooldownSeconds,
        BaselineSeconds=settings.AutoHighlightBaselineSeconds,
        ComparisonSeconds=settings.AutoHighlightComparisonSeconds,
        BaselineRatio=settings.AutoHighlightBaselineRatio,
        ReleaseRatio=settings.AutoHighlightReleaseRatio,
        MaxExtensionSeconds=settings.AutoHighlightMaxExtensionSeconds,
        ReleaseSeconds=settings.AutoHighlightReleaseSeconds,
        RebaseSeconds=settings.AutoHighlightRebaseSeconds
    };
    public void ApplyHeatOptions()
    {
        _heatGate.Wait();
        try { _heat.Configure(HeatOptions(_settings));
        _activeAutoClipId=null;PublishHeat(); } finally { _heatGate.Release(); }
        // 策略切换后旧事件不再延展，交给本轮结束时的 FinalizePending 收尾。

    }
    public string? Error {get;private set;}
    public string? DanmakuError {get;private set;}

    public RoomMonitor(long roomId,Store store,MediaTools media,BilibiliLiveSource source,ILiveSource? roomSource,LiveSettings settings,Action changed,Action<HighlightEvent> highlightReady,Func<DanmakuEvent,DanmakuEvent> decorate)
    {
        AutoRecord=settings.AutoRecordingRoomIds.Contains(roomId);
        CacheEnabled=settings.HighlightCacheRoomIds.Contains(roomId);
        _cacheEnabled=CacheEnabled&&!AutoRecord&&!string.IsNullOrWhiteSpace(settings.RecordingDirectory)&&media.Available();
        RoomId=roomId;_store=store;_source=roomSource??source;_settings=settings;_changed=changed;_highlightReady=highlightReady;_decorate=decorate;
        _heat=new DanmakuHeat(HeatOptions(settings));
        _highlights=new HighlightService(store,media,settings.RecordingRoot,settings.HighlightPreRollSeconds,settings.HighlightPostRollSeconds,danmakuLimit:settings.DanmakuRecordLimit)
        {
            ManualPreRollSeconds=settings.ManualPreRollSeconds,
            ManualPostRollSeconds=settings.ManualPostRollSeconds
        };
        _highlights.Changed+=ev=>
        {
            lock(_notificationGate) { if(_heatTransitionActive){_pendingHeatNotifications.Add(ev);return;} }
            _highlightReady(ev);_changed();
        };
        _recorder=new SegmentRecorder(store,media,AutoRecord?settings.RecordingRoot:Path.Combine(settings.RecordingRoot,"highlight-cache"))
        {
            RetainAllSegments=AutoRecord,
            OwnedCacheRoot=_cacheEnabled?Path.Combine(settings.RecordingRoot,"highlight-cache"):null,
            BufferSeconds=Math.Max(120,Math.Max(settings.HighlightPreRollSeconds,settings.ManualPreRollSeconds)+60)
        };
        _recorder.SegmentReady=async segment=>
        {
            try
            {
                // 没有新弹幕时也要按分片节拍推进热度：持续高热继续后推结束点，热度跌破释放线后收尾。
                await RunHeatAsync(async heat=>await ApplyHeatSignalAsync(heat.Advance(segment.End),segment.End));
                await _highlights.OnSegmentAsync(segment);
            }
            catch(Exception ex){Error=ex.Message;}
            _changed();
        };
        _recorder.Error+=message=>{Error=message;_changed();};
        _danmaku=new BilibiliDanmakuClient(source);
        _danmaku.Received+=OnDanmakuAsync;
        _danmaku.Error+=message=>{DanmakuError=message;_changed();};
    }

    public RoomMonitorState Snapshot()
    {
        var view=_heatView;
        return new(RoomId,_room?.Title??"",_room?.Streamer??"",_room?.IsLive??false,Monitoring,
        _session?.Id,_session?.State,_session?.StartedAt,view.Rate,view.Baseline,view.Score,DanmakuPackets,DanmakuAuthorized,view.Reason,Error,DanmakuError,
        _session is null?0:_store.BufferSeconds(_session.Id),AutoRecord,_cacheEnabled&&_recorder.IsRunning,AutoRecord?"full-recording":_cacheEnabled?"highlight-cache":"monitor-only",CacheEnabled,view.Percent,view.RebaseReason,view.RebaseAt,view.Recent);
    }

    public void Start()
    {
        if(Monitoring)return;
        Error=null;DanmakuError=null;
        _monitorStop=new CancellationTokenSource();
        _monitorTask=MonitorAsync(_monitorStop.Token);
        _changed();
    }

    public async Task StopAsync()
    {
        if(_monitorStop is null)return;
        _monitorStop.Cancel();
        if(_monitorTask is not null)try{await _monitorTask;}catch(OperationCanceledException){}
        _monitorStop.Dispose();_monitorStop=null;_monitorTask=null;
        _changed();
    }

    public Task<HighlightEvent> MarkAsync(CancellationToken token)
    {
        if(_session?.State!=RecordingState.Recording)throw new InvalidOperationException("当前没有正在录制的直播");
        return _highlights.MarkAsync(_session,"manual",1,token);
    }

    public void ApplyHighlightDurations(){_highlights.PreRollSeconds=_settings.HighlightPreRollSeconds;_highlights.PostRollSeconds=_settings.HighlightPostRollSeconds;_recorder.RetainAllSegments=AutoRecord;_recorder.BufferSeconds=Math.Max(120,Math.Max(_settings.HighlightPreRollSeconds,_settings.ManualPreRollSeconds)+60);}
    public void ApplyManualDurations(){_highlights.ManualPreRollSeconds=_settings.ManualPreRollSeconds;_highlights.ManualPostRollSeconds=_settings.ManualPostRollSeconds;_recorder.RetainAllSegments=AutoRecord;_recorder.BufferSeconds=Math.Max(120,Math.Max(_settings.HighlightPreRollSeconds,_settings.ManualPreRollSeconds)+60);}
    public void ApplyDanmakuLimit()=>_highlights.DanmakuRecordLimit=_settings.DanmakuRecordLimit;

    internal async Task RunHeatAsync(Func<DanmakuHeat,Task> transition)
    {
        await _heatGate.WaitAsync().ConfigureAwait(false);
        lock(_notificationGate)_heatTransitionActive=true;
        HighlightEvent[] notifications=[];
        try { await transition(_heat).ConfigureAwait(false); }
        finally
        {
            PublishHeat();
            lock(_notificationGate){_heatTransitionActive=false;notifications=_pendingHeatNotifications.ToArray();_pendingHeatNotifications.Clear();}
            _heatGate.Release();
            foreach(var ev in notifications){_highlightReady(ev);_changed();}
        }
    }
    private void PublishHeat()=>_heatView=new(_heat.MessageRate,_heat.Baseline,_heat.Score,_heat.Reason,
        _heat.TriggerLine,_heat.ReleaseLine,_heat.Active,_heat.InBaseline,_heat.BaselinePercent,
        _heat.RebaseReason,_heat.LastRebaseTime,_heat.RecentTriggerCount);

    private async Task OnDanmakuAsync(DanmakuEvent raw)
    {
        var ev=_decorate(raw);
        _store.SaveDanmaku(ev);
        await RunHeatAsync(async heat=>await ApplyHeatSignalAsync(heat.Add(ev),ev.SessionTime));
        _changed();
    }

    /// <summary>把 DanmakuHeat 的状态转移落到高光事件：起切 / 后推结束点 / 收尾。</summary>
    private async Task ApplyHeatSignalAsync(HeatSignal signal,double sessionTime)
    {
        if(!_settings.AutoHighlight||_session is not {State:RecordingState.Recording})return;
        switch(signal.Kind)
        {
            case HeatSignalKind.Trigger:
            {
                var ev=await _highlights.MarkAtAsync(_session,"danmaku",sessionTime,_heat.Score);
                _activeAutoClipId=ev.Id;
                _heat.RegisterHighlight(ev.Id,ev.Source,sessionTime);
                break;
            }
            case HeatSignalKind.Extend:
            {
                if(_activeAutoClipId is {Length:>0} id)
                    await _highlights.ExtendDanmakuAsync(_session,id,sessionTime+_settings.HighlightPostRollSeconds);
                break;
            }
            case HeatSignalKind.Close:
            {
                if(_activeAutoClipId is {Length:>0} id)
                    await _highlights.CloseDanmakuAsync(_session,id,sessionTime);
                _activeAutoClipId=null;
                break;
            }
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            while(!token.IsCancellationRequested)
            {
                try
                {
                    _room=await _source.GetRoomInfoAsync(RoomId,token);_store.SaveRoom(_room);_changed();
                    if(_room.IsLive&&(AutoRecord||_cacheEnabled))
                    {
                        if(_session is null)
                        {
                            _session=new RecordingSession(Guid.NewGuid().ToString("N"),RoomId,DateTimeOffset.UtcNow,null,RecordingState.Connecting);
                            // A3：每场会话从零采集自己的弹幕基准；上一场的触发/回落确认/冷却不跨场次携带。
                            // 只有监控先看到离线、再看到开播才算“开播起算”；否则基准是从中途加入时刻估算的。
                            await RunHeatAsync(heat=>{heat.Reset(_observedOffline);return Task.CompletedTask;});
                            _store.SaveSession(_session);_changed();
                        }
                        if(!_recorder.IsRunning&&!_recorder.CacheCapacityExceeded)
                        {
                            if(_session.State==RecordingState.Recording)SetState(RecordingState.Reconnecting);
                            var stream=await _source.GetStreamAsync(_room.CanonicalId,token);
                            await _recorder.StartAsync(_session,stream,token);
                            SetState(RecordingState.Recording);
                            _danmakuStop?.Cancel();
                            _danmakuStop=new CancellationTokenSource();
                            _danmakuTask=_danmaku.RunAsync(_session,_danmakuStop.Token);
                        }
                    }
                    else
                    {
                        if(!_room.IsLive)_observedOffline=true;
                        if(_session is not null)await EndSessionAsync();
                        else await RunHeatAsync(heat=>{heat.Reset(true);return Task.CompletedTask;});
                    }
                    if(!_recorder.CacheCapacityExceeded)Error=null;
                }
                catch(OperationCanceledException) { break; }
                catch(Exception ex)
                {
                    Error=ex.Message;_changed();
                    if(_session is {State:RecordingState.Connecting})SetState(RecordingState.Reconnecting);
                    else if(_session is {State:RecordingState.Recording} && !_recorder.IsRunning)SetState(RecordingState.Reconnecting);
                }
                await Task.Delay(TimeSpan.FromSeconds(10),token);
            }
        }
        catch(OperationCanceledException) { }
        finally
        {
            if(_session is not null)await EndSessionAsync();
        }
    }

    private void SetState(RecordingState state)
    {
        // 重连可能发生在首次取流还没完成的时候，非法跃迁直接忽略而不是让监控任务崩掉。
        if(_session is null||_session.State==state)return;
        RecordingState next;
        try{next=RecordingTransitions.Move(_session.State,state);}
        catch(InvalidOperationException){return;}
        _session=_session with {State=next};
        _store.SaveSession(_session);_changed();
    }

    private async Task EndSessionAsync()
    {
        if(_session is null)return;
        if(_session.State==RecordingState.Connecting)SetState(RecordingState.Reconnecting);
        SetState(RecordingState.Finalizing);
        _danmakuStop?.Cancel();
        if(_danmakuTask is not null)try{await _danmakuTask;}catch{}
        await _recorder.StopAsync();
        _activeAutoClipId=null;
        try{await _highlights.FinalizePendingAsync(_session.Id,allowPartial:!AutoRecord);}catch(Exception ex){Error=ex.Message;}
        // A3：会话结束即清空本场热度状态；下一场重新采集基准，不影响已有的切片物化。
        await RunHeatAsync(heat=>{heat.Reset(_room?.IsLive!=true);_activeAutoClipId=null;return Task.CompletedTask;});
        _observedOffline=_room?.IsLive!=true;
        if(_cacheEnabled&&_recorder.OwnedCacheRoot is {} cacheRoot)
            await _store.CleanAsync(_session.Id,double.MaxValue,0,CancellationToken.None,cacheRoot,includeTail:true);
        _session=_session with {State=RecordingState.Ended,EndedAt=DateTimeOffset.UtcNow};_store.SaveSession(_session);_changed();
        _session=null;
    }
}

public sealed class LiveCoordinator
{
    /// <summary>同时监控的直播间上限，避免带宽和磁盘被瞬间打满。</summary>
    public const int MaxRooms=8;
    private readonly Store _store;
    private readonly MediaTools _media;
    private readonly BilibiliLiveSource _source;
    private readonly ILiveSource _roomSource;
    private readonly TranscriptionService _transcription;
    private readonly BilibiliUserNameResolver _names;
    private readonly ConcurrentDictionary<long,RoomMonitor> _monitors=new();
    private readonly List<long> _order=[];
    public LiveSettings Settings {get;}
    public MediaTools Media=>_media;
    public TranscriptionService Transcription=>_transcription;
    public string? Error {get;private set;}
    public string? DanmakuError=>Primary?.DanmakuError;
    public event Action? Updated;

    private RoomMonitor? Primary
    {
        get
        {
            lock(_order)
            {
                foreach(var id in _order)if(_monitors.TryGetValue(id,out var monitor))return monitor;
            }
            return null;
        }
    }

    public IReadOnlyList<RoomMonitorState> Rooms
    {
        get
        {
            lock(_order)return _order.Where(_monitors.ContainsKey).Select(id=>_monitors[id].Snapshot()).ToArray();
        }
    }

    public RecordingSession? Session=>Primary?.Session;
    public RoomInfo? Room=>Primary?.Room;
    public bool Monitoring=>_monitors.Values.Any(x=>x.Monitoring);
    public bool BufferUnlimited=>Primary?.Recorder.RetainAllSegments??false;
    public double BufferLimitSeconds=>Primary?.Recorder.BufferSeconds??Math.Max(1200,Math.Max(Settings.HighlightPreRollSeconds,Settings.ManualPreRollSeconds)+60);
    public double MessageRate=>_monitors.Values.Sum(x=>x.MessageRate);
    public double Baseline=>Primary?.Baseline??0;
    public double HeatScore=>_monitors.Values.Select(x=>x.HeatScore).DefaultIfEmpty(0).Max();
    public long DanmakuPackets=>_monitors.Values.Sum(x=>x.DanmakuPackets);
    public string? LastDanmakuCommand=>Primary?.LastCommand;
    public bool DanmakuAuthorized=>_monitors.Values.Any(x=>x.DanmakuAuthorized);
    /// <summary>所有正在录制的会话（多直播间时可能有多个）。</summary>
    public IReadOnlyList<RecordingSession> ActiveSessions=>_monitors.Values.Select(x=>x.Session).OfType<RecordingSession>().ToArray();

    public LiveCoordinator(Store store,LiveSettings settings,ILiveSource? roomSource=null)
    {
        Settings=settings;_store=store;_source=new BilibiliLiveSource{Cookie=settings.BilibiliCookie};_roomSource=roomSource??_source;
        _media=new MediaTools(settings.FfmpegPath);
        _names=new BilibiliUserNameResolver(store,()=>Updated?.Invoke()){Lookup=_source.GetUserNameAsync};
        _transcription=new TranscriptionService(store,settings,_media);
        _transcription.ResumePending();
    }

    /// <summary>更新 B 站 Cookie。带 Cookie 的弹幕连接不会把用户名打码。</summary>
    public void SetBilibiliCookie(string cookie)
    {
        Settings.BilibiliCookie=(cookie??"").Trim();
        _source.Cookie=Settings.BilibiliCookie;
        Updated?.Invoke();
    }

    /// <summary>为历史弹幕补全被打码的用户名，返回本次发起查询的用户数。</summary>
    public int BackfillUserNames(int max=200)=>_names.BackfillHistory(max);

    private RoomMonitor Create(long roomId)=>new(roomId,_store,_media,_source,_roomSource,Settings,
        ()=>Updated?.Invoke(),
        ev=>
        {
            if(ev.State==HighlightState.Ready && Settings.AutoTranscribe)
            {
                var clip=_store.Clips().FirstOrDefault(x=>x.EventId==ev.Id);
                if(clip is not null)_transcription.Queue(clip);
            }
        },
        _names.Decorate);

    /// <summary>开始监控一个直播间；已有的直播间会继续监控（支持多开）。</summary>
    public void Start(long roomId,bool autoRecord=false,bool cacheEnabled=true)
    {
        if(roomId<=0)throw new ArgumentOutOfRangeException(nameof(roomId));
        if((autoRecord||cacheEnabled)&&string.IsNullOrWhiteSpace(Settings.RecordingDirectory))throw new ArgumentException("请先设置录制目录，再开启自动录播。");
        lock(_order)
        {
            if(_monitors.TryGetValue(roomId,out var running)&&running.Monitoring)throw new InvalidOperationException("这个直播间已经在监控中");
            if(!_monitors.ContainsKey(roomId)&&_monitors.Count>=MaxRooms)throw new InvalidOperationException($"最多同时监控 {MaxRooms} 个直播间，请先停止其中一个");
            Settings.HighlightCacheRoomIds.Remove(roomId);
            if(cacheEnabled)Settings.HighlightCacheRoomIds.Add(roomId);
            Settings.AutoRecordingRoomIds.Remove(roomId);
            if(autoRecord)Settings.AutoRecordingRoomIds.Add(roomId);
            var monitor=_monitors.GetOrAdd(roomId,Create);
            if(!_order.Contains(roomId))_order.Add(roomId);
            if(_order.Count==1)Settings.RoomId=roomId;
            if(!Settings.RoomIds.Contains(roomId))Settings.RoomIds.Add(roomId);
            Error=null;
            monitor.Start();
        }
        Updated?.Invoke();
    }

    /// <summary>
    /// 重启后恢复上次还在监控的直播间。没有 FFmpeg 或没有记录时什么都不做，
    /// 单个房间失败（已下架、房号无效）也只会跳过它，不影响其他房间。
    /// </summary>
    public async Task SetAutoRecordAsync(long roomId,bool enabled)
    {
        if(!_monitors.ContainsKey(roomId))throw new ArgumentException("直播间尚未监控。");
        if(enabled&&string.IsNullOrWhiteSpace(Settings.RecordingDirectory))throw new ArgumentException("请先设置录制目录，再开启自动录播。");
        if(enabled&&!_media.Available())throw new InvalidOperationException("自动录播需要 FFmpeg。");
        // Await old recorder finalization before replacing its immutable output root/mode.
        var cacheEnabled=Settings.HighlightCacheRoomIds.Contains(roomId);
        await StopAsync(roomId);
        Start(roomId,enabled,cacheEnabled);
    }

    public async Task SetCacheEnabledAsync(long roomId,bool enabled)
    {
        if(!_monitors.ContainsKey(roomId))throw new ArgumentException("直播间尚未监控。");
        if(enabled&&string.IsNullOrWhiteSpace(Settings.RecordingDirectory))throw new ArgumentException("请先设置 F 盘缓存/录制目录。");
        if(enabled&&!_media.Available())throw new InvalidOperationException("高光缓存需要 FFmpeg。");
        var full=Settings.AutoRecordingRoomIds.Contains(roomId);
        await StopAsync(roomId);Start(roomId,full,enabled);
    }

    public int ResumeMonitors()
    {
        var restored=0;
        foreach(var roomId in Settings.RoomIds.ToArray())
        {
            try{Start(roomId,Settings.AutoRecordingRoomIds.Contains(roomId)&&!string.IsNullOrWhiteSpace(Settings.RecordingDirectory),Settings.HighlightCacheRoomIds.Contains(roomId)&&!string.IsNullOrWhiteSpace(Settings.RecordingDirectory));restored++;}
            catch(InvalidOperationException){/* 已在监控或超出上限 */}
            catch(ArgumentOutOfRangeException){Settings.RoomIds.Remove(roomId);}
        }
        return restored;
    }

    /// <summary>停止指定直播间；不传房间号时停止全部。</summary>
    public async Task StopAsync(long? roomId=null)
    {
        RoomMonitor[] targets;
        lock(_order)
        {
            targets=roomId is null
                ? _order.Where(_monitors.ContainsKey).Select(id=>_monitors[id]).ToArray()
                : _monitors.TryGetValue(roomId.Value,out var one)?[one]:[];
        }
        foreach(var monitor in targets)await monitor.StopAsync();
        lock(_order)
        {
            foreach(var monitor in targets)
            {
                _monitors.TryRemove(monitor.RoomId,out _);
                _order.Remove(monitor.RoomId);
                Settings.RoomIds.Remove(monitor.RoomId);
            }
        }
        Updated?.Invoke();
    }

    public async Task ShutdownAsync()
    {
        await StopAsync();
        await _transcription.StopAsync();
    }

    /// <summary>标记高光；多直播间时可指定房间，默认取第一个正在录制的房间。</summary>
    public async Task<HighlightEvent> MarkAsync(CancellationToken token=default,long? roomId=null)
    {
        var monitor=roomId is not null
            ? (_monitors.TryGetValue(roomId.Value,out var one)?one:throw new InvalidOperationException("这个直播间没有在监控"))
            : _monitors.Values.FirstOrDefault(x=>x.Session is {State:RecordingState.Recording})
              ??throw new InvalidOperationException("当前没有正在录制的直播");
        return await monitor.MarkAsync(token);
    }

    public void QueueTranscription(string eventId)=>QueueTranscriptionReceipt(eventId);

    public TranscriptionReceipt QueueTranscriptionReceipt(string eventId)
    {
        var clip=_store.Clips().FirstOrDefault(x=>x.EventId==eventId)??throw new InvalidOperationException("找不到高光视频");
        if(_store.Jobs().Any(x=>x.EventId==eventId&&x.Type=="transcribe"&&x.Status is "pending" or "running"))throw new InvalidOperationException("该高光正在转写");
        var receipt=_transcription.QueueReceipt(clip);Updated?.Invoke();
        return receipt;
    }

    /// <summary>中断该高光正在进行的转译并重新排队（界面上的「重新转译」按钮）。</summary>
    public void RestartTranscription(string eventId)=>RestartTranscriptionReceipt(eventId);

    public TranscriptionReceipt RestartTranscriptionReceipt(string eventId)
    {
        var clip=_store.Clips().FirstOrDefault(x=>x.EventId==eventId)??throw new InvalidOperationException("找不到高光视频");
        var receipt=_transcription.RestartReceipt(clip);Updated?.Invoke();
        return receipt;
    }
    public void SetFfmpegPath(string path)
    {
        Settings.FfmpegPath=path;
        _media.SetFfmpegPath(path);
        Updated?.Invoke();
    }
    public void SetHighlightDurations(int preRollSeconds,int postRollSeconds)
    {
        if(preRollSeconds is <0 or >1800 || postRollSeconds is <0 or >1800 || preRollSeconds+postRollSeconds<1)
            throw new ArgumentException("高光前后时长需为 0–1800 秒，且总时长至少 1 秒");
        Settings.HighlightPreRollSeconds=preRollSeconds;
        Settings.HighlightPostRollSeconds=postRollSeconds;
        foreach(var monitor in _monitors.Values)monitor.ApplyHighlightDurations();
        Updated?.Invoke();
    }
    public void SetManualHighlightDurations(int preRollSeconds,int postRollSeconds)
    {
        if(preRollSeconds is <0 or >1800 || postRollSeconds is <0 or >1800 || preRollSeconds+postRollSeconds<1)
            throw new ArgumentException("手动高光前后时长需为 0–1800 秒，且总时长至少 1 秒");
        Settings.ManualPreRollSeconds=preRollSeconds;Settings.ManualPostRollSeconds=postRollSeconds;
        foreach(var monitor in _monitors.Values)monitor.ApplyManualDurations();
        Updated?.Invoke();
    }
    /// <summary>调整自动切片的触发灵敏度、阈值与冷却时间。</summary>
    public void SetAutoHighlightTuning(double sensitivity,double threshold,double cooldownSeconds)
    {
        if(!double.IsFinite(sensitivity)||sensitivity is <.5 or >2)throw new ArgumentException("自动切片灵敏度需在 0.5–2 之间");
        if(!double.IsFinite(threshold)||threshold is <.2 or >.95)throw new ArgumentException("自动切片触发阈值需在 0.2–0.95 之间");
        if(!double.IsFinite(cooldownSeconds)||cooldownSeconds is <10 or >900)throw new ArgumentException("自动切片冷却时间需在 10–900 秒之间");
        Settings.AutoHighlightSensitivity=sensitivity;Settings.AutoHighlightThreshold=threshold;Settings.AutoHighlightCooldownSeconds=cooldownSeconds;
        foreach(var monitor in _monitors.Values)monitor.ApplyHeatOptions();
        Updated?.Invoke();
    }
    /// <summary>按“前 10 分钟基准 + 140% 触发 / 120% 释放 + 后续延展”配置弹幕自动切片。</summary>
    public void SetDanmakuHeatPolicy(double cooldownSeconds,double baselineSeconds,double comparisonSeconds,double baselineRatio,double releaseRatio,double maxExtensionSeconds)
    {
        if(!double.IsFinite(cooldownSeconds)||cooldownSeconds is <10 or >900)throw new ArgumentException("自动切片冷却时间需在 10–900 秒之间");
        if(!double.IsFinite(baselineSeconds)||baselineSeconds is <60 or >3600)throw new ArgumentException("弹幕基准窗口需在 60–3600 秒之间");
        if(!double.IsFinite(comparisonSeconds)||comparisonSeconds is <10 or >600)throw new ArgumentException("弹幕比较窗口需在 10–600 秒之间");
        if(!double.IsFinite(baselineRatio)||baselineRatio is <1.05 or >3)throw new ArgumentException("弹幕触发倍率需在 1.05–3 之间");
        if(!double.IsFinite(releaseRatio)||releaseRatio is <1.0 or >2.99)throw new ArgumentException("弹幕释放倍率需在 1.0–3 之间");
        if(releaseRatio>baselineRatio-.01)throw new ArgumentException("弹幕释放倍率必须低于触发倍率");
        if(!double.IsFinite(maxExtensionSeconds)||maxExtensionSeconds is <60 or >7200)throw new ArgumentException("自动切片最长延展需在 60–7200 秒之间");
        Settings.AutoHighlightCooldownSeconds=cooldownSeconds;Settings.AutoHighlightBaselineSeconds=baselineSeconds;
        Settings.AutoHighlightComparisonSeconds=comparisonSeconds;Settings.AutoHighlightBaselineRatio=baselineRatio;
        Settings.AutoHighlightReleaseRatio=releaseRatio;Settings.AutoHighlightMaxExtensionSeconds=maxExtensionSeconds;
        foreach(var monitor in _monitors.Values)monitor.ApplyHeatOptions();
        Updated?.Invoke();
    }
    public string HeatReason=>Primary?.HeatReason??"";
    public double HeatTriggerLine=>Primary?.HeatTriggerLine??0;
    public double HeatReleaseLine=>Primary?.HeatReleaseLine??0;
    public bool HeatActive=>Primary?.HeatActive??false;
    public bool HeatInBaseline=>Primary?.HeatInBaseline??false;
    public void SetAutoHighlightEnabled(bool enabled)
    {
        Settings.AutoHighlight=enabled;
        Updated?.Invoke();
    }
    public void SetDanmakuRecordLimit(int limit)
    {
        if(limit is <1000 or >10000)throw new ArgumentException("单次高光弹幕记录条数需在 1000–10000 之间");
        Settings.DanmakuRecordLimit=limit;
        foreach(var monitor in _monitors.Values)monitor.ApplyDanmakuLimit();
        Updated?.Invoke();
    }
    public async Task RecoverHighlightsAsync()
    {
        if(string.IsNullOrWhiteSpace(Settings.RecordingDirectory))return;
        var helper=new HighlightService(_store,_media,Settings.RecordingRoot,Settings.HighlightPreRollSeconds,Settings.HighlightPostRollSeconds,danmakuLimit:Settings.DanmakuRecordLimit);
        foreach(var sessionId in _store.Highlights().Where(x=>x.State==HighlightState.Collecting).Select(x=>x.SessionId).Distinct())
            try {await helper.FinalizePendingAsync(sessionId,allowPartial:true);}catch(Exception ex){Error=ex.Message;}
    }
    public Task RetryHighlightAsync(string eventId,CancellationToken token=default)
    {
        if(string.IsNullOrWhiteSpace(Settings.RecordingDirectory))throw new ArgumentException("请先设置高光/录制目录，再重试高光生成。");
        var helper=new HighlightService(_store,_media,Settings.RecordingRoot,Settings.HighlightPreRollSeconds,Settings.HighlightPostRollSeconds,danmakuLimit:Settings.DanmakuRecordLimit);
        return helper.RetryAsync(eventId,token);
    }
}

/// <summary>A9 P0：设置读写的纯函数入口，Web 层与单测共用，避免 clamp 逻辑散落。</summary>
public static class LiveSettingsMutations
{
    public static void ApplyAutoClipDurations(LiveSettings settings,int preRoll,int postRoll,int manualPreRoll,int manualPostRoll,int danmakuLimit)
    {
        settings.HighlightPreRollSeconds=Math.Clamp(preRoll,0,1800);
        settings.HighlightPostRollSeconds=Math.Clamp(postRoll,0,1800);
        settings.ManualPreRollSeconds=Math.Clamp(manualPreRoll,0,1800);
        settings.ManualPostRollSeconds=Math.Clamp(manualPostRoll,0,1800);
        settings.DanmakuRecordLimit=Math.Clamp(danmakuLimit,1000,10000);
    }

    public static void ApplyReleasePolicy(LiveSettings settings,double releaseSeconds,double rebaseSeconds)
    {
        settings.AutoHighlightReleaseSeconds=Math.Clamp(double.IsFinite(releaseSeconds)?releaseSeconds:200,10,1800);
        settings.AutoHighlightRebaseSeconds=Math.Clamp(double.IsFinite(rebaseSeconds)?rebaseSeconds:600,60,3600);
    }

    /// <summary>
    /// A9-2：POST /api/live/auto-clip 与设置页共用同一份 Settings；把策略端点写入的
    /// 前/后录、手动前/后录、弹幕记录、释放确认与基准重锚一次性落盘。
    /// </summary>
    public static void ApplyLivePolicy(LiveSettings settings,int preRoll,int postRoll,int manualPreRoll,int manualPostRoll,int danmakuLimit,double releaseSeconds,double rebaseSeconds)
    {
        ApplyAutoClipDurations(settings,preRoll,postRoll,manualPreRoll,manualPostRoll,danmakuLimit);
        ApplyReleasePolicy(settings,releaseSeconds,rebaseSeconds);
    }

    // ===== UI4-B：PATCH /api/settings 局部合并（task-149）=====
    // legacy 全量 POST 与 React PATCH 共用同一份可写字段集合与同一份显式范围校验，避免再次漂移。

    /// <summary>深拷贝一份设置（PATCH 在副本上合并，校验失败时不污染运行期对象）。</summary>
    public static LiveSettings Clone(LiveSettings settings)=>
        JsonSerializer.Deserialize<LiveSettings>(JsonSerializer.Serialize(settings))??new LiveSettings();

    /// <summary>可写字段集合（与 POST /api/settings 一一对应，28 项；roomIds/sensitivity/threshold/music*/aiApiKey/settingsVersion 不在其中）。</summary>
    public static readonly IReadOnlyList<string> WritableFields=
    [
        "roomId","bilibiliUid","bilibiliCookie","storageRoot","recordingDirectory","renderWorkDirectory","exportOutputDirectory","ffmpegPath","pythonPath","whisperModel",
        "autoTranscribe","autoHighlight","autoAnalyze","autoTrimSilence","cleanupOnExit",
        "autoHighlightCooldownSeconds","autoHighlightBaselineSeconds","autoHighlightComparisonSeconds",
        "autoHighlightBaselineRatio","autoHighlightReleaseRatio","autoHighlightMaxExtensionSeconds",
        "autoHighlightReleaseSeconds","autoHighlightRebaseSeconds",
        "highlightPreRollSeconds","highlightPostRollSeconds","manualPreRollSeconds","manualPostRollSeconds",
        "danmakuRecordLimit","aiProvider","aiEndpoint","aiModel","localOutputRoot"
    ];

    /// <summary>全量 POST 与 PATCH 合并后共用的显式范围校验；返回非法字段名（camelCase）。</summary>
    public static List<string> ValidateForFullSave(LiveSettings settings)
    {
        var fields=new List<string>();
        if(settings.RoomId<0)fields.Add("roomId");
        if(settings.BilibiliUid<0)fields.Add("bilibiliUid");
        if(string.IsNullOrWhiteSpace(settings.StorageRoot))fields.Add("storageRoot");
        if(!RenderStoragePaths.IsValidDirectory(settings.RecordingDirectory))fields.Add("recordingDirectory");
        if(!RenderStoragePaths.IsValidDirectory(settings.RenderWorkDirectory))fields.Add("renderWorkDirectory");
        if(!RenderStoragePaths.IsValidDirectory(settings.ExportOutputDirectory))fields.Add("exportOutputDirectory");
        if(settings.HighlightPreRollSeconds is <0 or >1800)fields.Add("highlightPreRollSeconds");
        if(settings.HighlightPostRollSeconds is <0 or >1800)fields.Add("highlightPostRollSeconds");
        if(settings.HighlightPreRollSeconds+settings.HighlightPostRollSeconds<1)fields.Add("highlightPreRollSeconds+highlightPostRollSeconds");
        if(settings.ManualPreRollSeconds is <0 or >1800)fields.Add("manualPreRollSeconds");
        if(settings.ManualPostRollSeconds is <0 or >1800)fields.Add("manualPostRollSeconds");
        if(settings.ManualPreRollSeconds+settings.ManualPostRollSeconds<1)fields.Add("manualPreRollSeconds+manualPostRollSeconds");
        if(settings.DanmakuRecordLimit is <1000 or >10000)fields.Add("danmakuRecordLimit");
        if(settings.AutoHighlightReleaseSeconds is <10 or >1800)fields.Add("autoHighlightReleaseSeconds");
        if(settings.AutoHighlightRebaseSeconds is <60 or >3600)fields.Add("autoHighlightRebaseSeconds");
        return fields;
    }

    /// <summary>
    /// 把 PATCH 请求体（只含想改的键）合并到 current 的副本上；只写出现的键，未知键忽略。
    /// 返回 false 时 invalidFields 为非法字段；merged 不用于落盘（调用方不得持久化）。
    /// </summary>
    public static bool TryApplyPatch(LiveSettings current,JsonElement patch,out LiveSettings merged,out List<string> invalidFields,out List<string> changedFields)
    {
        merged=Clone(current);
        invalidFields=[];
        changedFields=[];
        if(patch.ValueKind!=JsonValueKind.Object){invalidFields.Add("body");return false;}
        foreach(var property in patch.EnumerateObject())
        {
            TryApplyPatchValue(merged,property,out var applied,out var typeError);
            if(typeError){invalidFields.Add(property.Name);continue;}
            if(applied && Changed(current,merged,property.Name))changedFields.Add(property.Name);
        }
        if(invalidFields.Count>0)return false;
        var rangeErrors=ValidateForFullSave(merged);
        if(rangeErrors.Count>0){invalidFields.AddRange(rangeErrors);return false;}
        // 工具路径 / 转写模型被 PATCH 改动时不得为空（全量 POST 语义不变，不在 ValidateForFullSave 里加）。
        foreach(var field in changedFields)
        {
            var blank=field switch
            {
                "ffmpegPath"=>string.IsNullOrWhiteSpace(merged.FfmpegPath),
                "pythonPath"=>string.IsNullOrWhiteSpace(merged.PythonPath),
                "whisperModel"=>string.IsNullOrWhiteSpace(merged.WhisperModel),
                _=>false
            };
            if(blank)invalidFields.Add(field);
        }
        if(invalidFields.Count>0)return false;
        return true;
    }

    static bool Changed(LiveSettings current,LiveSettings merged,string field)
    {
        object? Value(LiveSettings s,string name)=>name switch
        {
            "roomId"=>s.RoomId,"bilibiliUid"=>s.BilibiliUid,"bilibiliCookie"=>s.BilibiliCookie,
            "recordingDirectory"=>s.RecordingDirectory,
            "renderWorkDirectory"=>s.RenderWorkDirectory,"exportOutputDirectory"=>s.ExportOutputDirectory,
            "storageRoot"=>s.StorageRoot,"ffmpegPath"=>s.FfmpegPath,"pythonPath"=>s.PythonPath,"whisperModel"=>s.WhisperModel,
            "autoTranscribe"=>s.AutoTranscribe,"autoHighlight"=>s.AutoHighlight,"autoAnalyze"=>s.AutoAnalyze,
            "autoTrimSilence"=>s.AutoTrimSilence,"cleanupOnExit"=>s.CleanupOnExit,
            "autoHighlightCooldownSeconds"=>s.AutoHighlightCooldownSeconds,
            "autoHighlightBaselineSeconds"=>s.AutoHighlightBaselineSeconds,
            "autoHighlightComparisonSeconds"=>s.AutoHighlightComparisonSeconds,
            "autoHighlightBaselineRatio"=>s.AutoHighlightBaselineRatio,
            "autoHighlightReleaseRatio"=>s.AutoHighlightReleaseRatio,
            "autoHighlightMaxExtensionSeconds"=>s.AutoHighlightMaxExtensionSeconds,
            "autoHighlightReleaseSeconds"=>s.AutoHighlightReleaseSeconds,
            "autoHighlightRebaseSeconds"=>s.AutoHighlightRebaseSeconds,
            "highlightPreRollSeconds"=>s.HighlightPreRollSeconds,"highlightPostRollSeconds"=>s.HighlightPostRollSeconds,
            "manualPreRollSeconds"=>s.ManualPreRollSeconds,"manualPostRollSeconds"=>s.ManualPostRollSeconds,
            "danmakuRecordLimit"=>s.DanmakuRecordLimit,"aiProvider"=>s.AiProvider,"aiEndpoint"=>s.AiEndpoint,
            "aiModel"=>s.AiModel,"localOutputRoot"=>s.LocalOutputRoot,_=>null
        };
        return !Equals(Value(current,field),Value(merged,field));
    }

    /// <summary>单个字段解析：类型严格校验（数字/布尔/字符串）；未出现或不可写字段返回 applied=false。</summary>
    static bool TryApplyPatchValue(LiveSettings target,JsonProperty property,out bool applied,out bool typeError)
    {
        applied=false;typeError=false;
        var value=property.Value;
        // 请求体键名大小写不敏感；规范化为目标字段的 camelCase 名称后再匹配。
        var name=WritableFields.FirstOrDefault(field=>string.Equals(field,property.Name,StringComparison.OrdinalIgnoreCase));
        if(name is null)return true;
        switch(name)
        {
            case "roomId":
            case "bilibiliUid":
                if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt64(out var id)){typeError=true;return true;}
                if(name=="roomId")target.RoomId=id; else target.BilibiliUid=id;
                break;
            case "bilibiliCookie": if(!TryString(value,out var cookie)){typeError=true;return true;} target.BilibiliCookie=cookie;break;
            case "storageRoot": if(!TryString(value,out var storage)){typeError=true;return true;} target.StorageRoot=storage;break;
            case "recordingDirectory": if(!TryString(value,out var recording)){typeError=true;return true;} target.RecordingDirectory=recording;break;
            case "renderWorkDirectory": if(!TryString(value,out var renderWork)){typeError=true;return true;} target.RenderWorkDirectory=renderWork;break;
            case "exportOutputDirectory": if(!TryString(value,out var exportOutput)){typeError=true;return true;} target.ExportOutputDirectory=exportOutput;break;
            case "ffmpegPath": if(!TryString(value,out var ffmpeg)){typeError=true;return true;} target.FfmpegPath=ffmpeg;break;
            case "pythonPath": if(!TryString(value,out var python)){typeError=true;return true;} target.PythonPath=python;break;
            case "whisperModel": if(!TryString(value,out var whisper)){typeError=true;return true;} target.WhisperModel=whisper;break;
            case "aiProvider": if(!TryString(value,out var provider)){typeError=true;return true;} target.AiProvider=provider;break;
            case "aiEndpoint": if(!TryString(value,out var endpoint)){typeError=true;return true;} target.AiEndpoint=endpoint;break;
            case "aiModel": if(!TryString(value,out var model)){typeError=true;return true;} target.AiModel=model;break;
            case "localOutputRoot": if(!TryString(value,out var output)){typeError=true;return true;} target.LocalOutputRoot=output;break;
            case "autoTranscribe": if(!TryBool(value,out var autoTranscribe)){typeError=true;return true;} target.AutoTranscribe=autoTranscribe;break;
            case "autoHighlight": if(!TryBool(value,out var autoHighlight)){typeError=true;return true;} target.AutoHighlight=autoHighlight;break;
            case "autoAnalyze": if(!TryBool(value,out var autoAnalyze)){typeError=true;return true;} target.AutoAnalyze=autoAnalyze;break;
            case "autoTrimSilence": if(!TryBool(value,out var trimSilence)){typeError=true;return true;} target.AutoTrimSilence=trimSilence;break;
            case "cleanupOnExit": if(!TryBool(value,out var cleanup)){typeError=true;return true;} target.CleanupOnExit=cleanup;break;
            case "autoHighlightCooldownSeconds": if(!TryDouble(value,out var cooldown)){typeError=true;return true;} target.AutoHighlightCooldownSeconds=cooldown;break;
            case "autoHighlightBaselineSeconds": if(!TryDouble(value,out var baseline)){typeError=true;return true;} target.AutoHighlightBaselineSeconds=baseline;break;
            case "autoHighlightComparisonSeconds": if(!TryDouble(value,out var comparison)){typeError=true;return true;} target.AutoHighlightComparisonSeconds=comparison;break;
            case "autoHighlightBaselineRatio": if(!TryDouble(value,out var ratio)){typeError=true;return true;} target.AutoHighlightBaselineRatio=ratio;break;
            case "autoHighlightReleaseRatio": if(!TryDouble(value,out var releaseRatio)){typeError=true;return true;} target.AutoHighlightReleaseRatio=releaseRatio;break;
            case "autoHighlightMaxExtensionSeconds": if(!TryDouble(value,out var extension)){typeError=true;return true;} target.AutoHighlightMaxExtensionSeconds=extension;break;
            case "autoHighlightReleaseSeconds": if(!TryDouble(value,out var releaseSeconds)){typeError=true;return true;} target.AutoHighlightReleaseSeconds=releaseSeconds;break;
            case "autoHighlightRebaseSeconds": if(!TryDouble(value,out var rebase)){typeError=true;return true;} target.AutoHighlightRebaseSeconds=rebase;break;
            case "highlightPreRollSeconds":
            case "highlightPostRollSeconds":
            case "manualPreRollSeconds":
            case "manualPostRollSeconds":
            case "danmakuRecordLimit":
                if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt32(out var number)){typeError=true;return true;}
                switch(name)
                {
                    case "highlightPreRollSeconds":target.HighlightPreRollSeconds=number;break;
                    case "highlightPostRollSeconds":target.HighlightPostRollSeconds=number;break;
                    case "manualPreRollSeconds":target.ManualPreRollSeconds=number;break;
                    case "manualPostRollSeconds":target.ManualPostRollSeconds=number;break;
                    default:target.DanmakuRecordLimit=number;break;
                }
                break;
            default: return true; // 不可写字段（如 roomIds）：忽略
        }
        applied=true;
        return true;
    }

    static bool TryString(JsonElement value,out string text)
    {
        text=value.ValueKind==JsonValueKind.String?(value.GetString()??""):"";
        return value.ValueKind==JsonValueKind.String;
    }
    static bool TryBool(JsonElement value,out bool flag)
    {
        flag=value.ValueKind==JsonValueKind.True;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
    static bool TryDouble(JsonElement value,out double number)
    {
        number=0;
        return value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out number);
    }
}

/// <summary>A9 P0-4：把 AI 测试连接异常映射为前端可展示的分类（ok/model_not_found/network/timeout/invalid）。</summary>
public static class AiProbeClassifier
{
    public static string Classify(Exception exception)=>exception switch
    {
        OperationCanceledException=>"timeout",
        HttpRequestException=>"network",
        ArgumentException or UriFormatException or InvalidOperationException or JsonException=>"invalid",
        _=>"unknown"
    };
}

