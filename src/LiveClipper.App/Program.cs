using System.Text.Json;
using System.Text.Json.Serialization;
using LiveClipper.Core;
using LiveClipper.Core.Bridge;
using LiveClipper.Domain;
using LiveClipper.Live;
using LiveClipper.App;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

string? LaunchArgument(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
// 注意：桌面壳（LiveClipper.Desktop.exe）是 WPF 入口，Program.Main 只是被 InProcessApiHost 在窗口 OnLoaded 里
// 跑起来的"第二个宿主"，所以它拿到的 args 是 desktopHost 自己的命令行，永远不会带 --factory-reset-parent。
// 真正的"等父进程退出"逻辑在桌面壳自己那边（见 MainWindow 的恢复出厂设置重启链）；这里保留只是给
// 独立服务进程（Program 自己就是入口）用的。
if(int.TryParse(LaunchArgument("--factory-reset-parent"),out var resetParent))
    try{using var parent=Process.GetProcessById(resetParent);await parent.WaitForExitAsync();}catch(ArgumentException){}
var factoryResetId=LaunchArgument("--factory-reset-id");
var dataDirectory=LaunchArgument("--data-dir");
var bridgeFake=args.Contains("--bridge-fake")||Environment.GetEnvironmentVariable("LIVECLIPPER_BRIDGE_FAKE")=="1";
var desktopHost=Environment.GetEnvironmentVariable("LIVECLIPPER_DESKTOP_HOST")=="1";

// 单实例判定必须早于任何副作用（尤其是"删上一轮残留的恢复出厂设置标记"）：
// 桌面壳的 StartupUri 会在 base.OnStartup 里把窗口建出来跑到 OnLoaded，而进程内宿主要等 OnLoaded
// 里的 InProcessApiHost.TryStartAsync 才执行到这里，所以"窗口已经出现过"完全不能当作"本进程是第一实例"。
// 这里用 LiveClipper.Activate.<port> 命名事件做判定：构造期 TryOpenExisting 命中就说明已经有一个实例
// 在持有激活通道（即它已经把事件建好了），本进程立刻发信号并退出，一个文件都不碰。
var serverPort=Program.ChoosePort(Program.ResolvePort(Environment.GetEnvironmentVariable("LIVECLIPPER_PORT")),message=>Console.Error.WriteLine("[port] "+message));
// 单实例判定还有一层：Program.Main 在桌面壳里跑得比窗口晚，P2 可能在 P1 建好激活通道之前就走到这里。
// 私有重置标记已按 PID/token 隔离，但仍须在清理历史残留等副作用之前保留这层判据：
// server 互斥量（与下面 serverMutex 同名，用户会话级），避免只靠"探激活事件"的窗口。
// 这里只 Test 一次"是否已存在"，不持有它；P1 在进程生命周期内一直持有，P2 于是必然看到它已存在。
// 注意：同用户同名前缀的 mutex 是递归的，所以这一层只在"同一进程第一次"与"另一个进程"之间有效——
// 恰恰就是这里需要的语义。（独立服务进程模式下这条也成立，P1 同样持有它。）
var existingServerInstance=Program.ServerSingletonExists(serverPort);
var builder=WebApplication.CreateBuilder(args);
// 单实例激活通道必须在这里就建好：第一个实例从此刻到任何窗口出现之间都会持有 AutoReset 事件，
// 第二实例在任何时刻发信号都不会落空（旧实现把事件放到窗口 OnLoaded 才建，窗口创建→Loaded 这段是盲区）。
// 这一步与宿主构建是否成功无关，所以拿到 server mutex 之后、Build() 之前做；
// 失败（非 Windows / 创建异常）只记日志，窗口那段 TryOpenExisting 判定会退回兜底。
var activationCreated=Program.TryCreateActivationChannel(serverPort,out var activationSignal,out var activationRelease,out var activationFresh);
using var activationSignalHandle=activationSignal;
using var activationReleaseHandle=activationRelease;
AppDomain.CurrentDomain.ProcessExit+=(_,_)=>{try{activationReleaseHandle?.Set();}catch{};};
// 等待线程由 Main 启动（而不是窗口）：从这一刻起任何时刻到达的信号都会被接住。
// 窗口还没出现时信号只累计 pending，窗口 OnLoaded 时统一消费；进程退出靠上面的 release 事件让线程收敛。
if(activationCreated)Program.StartActivationPump(activationSignal!,null,activationRelease);
// 沿用单实例门控：激活通道就绪后才清理私有重置目录的历史残留。
// 共享实现还会验证 PID/token/时间，保留其他活动实例的有效标记；窗口只消费本次运行的标记。
// LIVECLIPPER_SERVER_ENTRY 由桌面入口设置；独立服务进程沿用自己的重启链，不参与这项清理。
if(Environment.GetEnvironmentVariable("LIVECLIPPER_SERVER_ENTRY")=="1")
{
    // 只有真的"独占了这个端口"的进程才有资格把上一轮的重置标记当垃圾清掉。判据两条，缺一不可：
    //   1) activationFresh：本进程把 LiveClipper.Activate.<port> 新建了出来（createdNew=true）。
    //      探空竞态里的 P2 走到这里时事件已被 P1 建好，拿到的是已存在的句柄，于是它不碰标记。
    //   2) !existingServerInstance：进来之前 Server 互斥量还不存在。
    // 两条都不依赖"窗口有没有出现过"，也不依赖"P1 的互斥量什么时候拿到"。
    if(activationFresh&&!existingServerInstance)Program.ClearFactoryResetMarker();
    else
    {
        try
        {
            if(Environment.GetEnvironmentVariable("LIVECLIPPER_ACTIVATION_TRACE")=="1")
                Console.Error.WriteLine("[reset] marker clear skipped: fresh="+activationFresh+" server-exists="+existingServerInstance+" pid="+Environment.ProcessId);
        }
        catch{}
    }
}
var serverUrl=$"http://127.0.0.1:{serverPort}";
using var serverMutex=new Mutex(true,$@"Local\LiveClipper.Server.{serverPort}",out var ownsServerMutex);
if(!ownsServerMutex)
{
    // 桌面壳进程内托管：通知已有的那个实例把窗口带到前台，然后安静退出（退出码 0），不弹错误框。
    // 注意这里拿到 !ownsServerMutex 的实际情形是「上一个持有者异常退出留下了 abandoned mutex」——
    // 同用户同名前缀的 mutex 是递归的，第二个终态实例在同线程上会拿到同一个 handle。
    // 启动期（P1 还没建激活事件）的第二实例由窗口构造期的 TryOpenExisting 和这里的 abandoned 语义共同兜底，
    // 但都不完备：P1 启动后 ~300ms 内的 P2 仍会闪一次窗（见 MainWindow.xaml.cs 构造函数注释）。
    if(desktopHost){Program.SignalFirstInstance(serverPort);Environment.Exit(0);}
    throw new InvalidOperationException("LiveClipper 服务已经在运行");
}
builder.Services.ConfigureHttpJsonOptions(options=>options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options=>options.MultipartBodyLengthLimit=16L*1024*1024*1024);
builder.WebHost.ConfigureKestrel(options=>options.Limits.MaxRequestBodySize=16L*1024*1024*1024);
builder.WebHost.UseUrls(serverUrl);
// --webroot 让隔离测试实例能指向真实的 web/dist：此前这个参数被忽略，测试一直在跑
// src/LiveClipper.App/wwwroot 里的旧副本（2026-10-27），门禁结论与当前源码无关。
var webRootArgument=LaunchArgument("--webroot");
if(!string.IsNullOrWhiteSpace(webRootArgument))
{
    var webRootPath=Path.GetFullPath(webRootArgument);
    if(Directory.Exists(webRootPath)) builder.WebHost.UseWebRoot(webRootPath);
    else Console.Error.WriteLine("[webroot] ignored, directory not found: "+webRootPath);
}
var configDirectory=Path.GetFullPath(Environment.GetEnvironmentVariable("LIVECLIPPER_CONFIG_DIR")??(string.IsNullOrWhiteSpace(dataDirectory)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper"):dataDirectory));
Directory.CreateDirectory(configDirectory);
// R51-Y1b：按数据目录的单实例锁（不同端口但同一数据目录的两个实例会互相踩设置与数据库）。
// 端口互斥量（上面 serverMutex）保留不变；这把锁在任何设置/数据库写入之前拿到，拿不到就失败关闭。
using var configRootMutex=StartupDurability.TryAcquireDataRootMutex(configDirectory)
    ??throw new InvalidOperationException($"另一个 LiveClipper 实例正在使用数据目录 {configDirectory}");
// 清理崩溃留下的 DurableFile 临时文件（只删带 .lc-tmp- 标记、超过 1 小时的 .tmp）。
StartupDurability.DeleteStaleTemps(configDirectory,StartupDurability.StaleTempAge,DateTime.UtcNow);
AiRequestDiagnostics.ConfigureFile(Path.Combine(configDirectory,"logs","ai-requests.jsonl"));
var configPath=Path.Combine(configDirectory,"settings.json");
// 解析失败不再静默回落默认值：坏文件先备份为 settings.json.corrupt-<时间戳>，原文件保留，警告写日志并经 /api/health 暴露；
// 本次启动期间的版本迁移不回写（settingsWritable=false），避免把坏文件悄悄覆盖成默认值。
var settingsLoad=StartupDurability.LoadSettings(configPath,()=>FactoryResetService.Defaults(configDirectory));
LiveSettings settings=settingsLoad.Settings;
var settingsWritable=settingsLoad.Writable;
var startupWarnings=new List<string>();
if(settingsLoad.Warning is string settingsWarning){startupWarnings.Add(settingsWarning);Console.Error.WriteLine("[settings] "+settingsWarning);}
// Program 与 MusicEndpoints 共用按配置路径的门；读取旧值、修改、序列化及原子发布都在同一事务内。
var indentedSettingsJson=new JsonSerializerOptions{WriteIndented=true};
void UpdateSettingsDurably(Action update,JsonSerializerOptions? options=null,bool persist=true)=>
    StartupDurability.UpdateSettings(configPath,settings,()=>{update();return true;},options,persist);
bool TryUpdateSettingsDurably(Func<bool> update)=>StartupDurability.UpdateSettings(configPath,settings,update,indentedSettingsJson);
Task UpdateSettingsDurablyAsync(Func<Task> update)=>StartupDurability.UpdateSettingsAsync(configPath,settings,async ()=>{await update();return true;},indentedSettingsJson);
void MigrateSettings(Action update)
{
    try{UpdateSettingsDurably(update,persist:settingsWritable);}
    catch(Exception ex){Console.Error.WriteLine("[settings] save failed: "+ex.Message);}
}
if(settings.FfmpegPath=="ffmpeg"&&Environment.GetEnvironmentVariable("LIVECLIPPER_FFMPEG") is string installedFfmpeg)settings.FfmpegPath=installedFfmpeg;
if(settings.PythonPath=="python"&&Environment.GetEnvironmentVariable("LIVECLIPPER_PYTHON") is string installedPython)settings.PythonPath=installedPython;
if(settings.SettingsVersion<2)MigrateSettings(()=>
{
    // 旧默认口径：基线 300s、触发 120%、后录 300s。整组仍是旧默认时迁移到用户要求的新口径。
    var legacyDefaults=Math.Abs(settings.AutoHighlightBaselineSeconds-300)<0.001
        &&Math.Abs(settings.AutoHighlightBaselineRatio-1.2)<0.001
        &&Math.Abs(settings.AutoHighlightComparisonSeconds-120)<0.001
        &&settings.HighlightPostRollSeconds==300;
    if(legacyDefaults)
    {
        settings.AutoHighlightBaselineSeconds=600;
        settings.AutoHighlightBaselineRatio=1.4;
        settings.AutoHighlightReleaseRatio=1.2;
        settings.AutoHighlightMaxExtensionSeconds=1800;
        settings.HighlightPostRollSeconds=200;
    }
    settings.SettingsVersion=2;
});
if(settings.SettingsVersion<3)MigrateSettings(()=>
{
    // 手动高光旧默认：前 300s、后 120s。整组仍是旧默认时迁移到与自动切片一致的 前 200s/后 200s。
    if(settings.ManualPreRollSeconds==300&&settings.ManualPostRollSeconds==120)
    {
        settings.ManualPreRollSeconds=200;
        settings.ManualPostRollSeconds=200;
    }
    settings.SettingsVersion=3;
});
if(settings.SettingsVersion<4)MigrateSettings(()=>
{
    // v4 引入 React 设置页的 AI 连接：补齐空字段，并给兼容 API 密钥留出 DPAPI 密文槽位（设置文件永不保存明文）。
    if(string.IsNullOrWhiteSpace(settings.AiProvider))settings.AiProvider="ollama";
    if(string.IsNullOrWhiteSpace(settings.AiEndpoint))settings.AiEndpoint="https://api.openai.com/v1";
    if(string.IsNullOrWhiteSpace(settings.AiModel))settings.AiModel="qwen2.5:7b-instruct";
    settings.AiApiKey??="";
    settings.SettingsVersion=4;
});
if(settings.FfmpegPath=="ffmpeg"&&Directory.Exists(Path.Combine(configDirectory,"tools","ffmpeg")))
    settings.FfmpegPath=Directory.EnumerateFiles(Path.Combine(configDirectory,"tools","ffmpeg"),"ffmpeg.exe",SearchOption.AllDirectories).FirstOrDefault()??"ffmpeg";
// --data-dir 用于隔离测试实例：设置文件、数据库与桥接产物都写入该目录，不污染本机正式数据。
if(!string.IsNullOrWhiteSpace(dataDirectory))settings.StorageRoot=Path.Combine(Path.GetFullPath(dataDirectory),"storage");
Directory.CreateDirectory(settings.StorageRoot);
// 存储目录可能不在配置目录下（用户自定义 StorageRoot）：再按存储目录拿一次同名规则的锁；同一路径不重复拿。
using var storageRootMutex=string.Equals(StartupDurability.NormalizeDataRoot(settings.StorageRoot),StartupDurability.NormalizeDataRoot(configDirectory),StringComparison.Ordinal)
    ?null
    :StartupDurability.TryAcquireDataRootMutex(settings.StorageRoot)??throw new InvalidOperationException($"另一个 LiveClipper 实例正在使用存储目录 {settings.StorageRoot}");
StartupDurability.DeleteStaleTemps(settings.StorageRoot,StartupDurability.StaleTempAge,DateTime.UtcNow);
// 失败关闭：数据库由更新版本迁移过（schema_migrations 版本 > StudioStore.SupportedSchemaVersion）时，
// 必须在 Store.Migrate()（它会切 WAL、跑迁移、写 RecoverJobs）之前拒绝启动，且不改动任何文件、不换 StorageRoot 兜底。
try{StartupDurability.EnsureDatabaseSupported(Path.Combine(settings.StorageRoot,"liveclipper.db"));}
catch(StorageSchemaTooNewException ex)
{
    Console.Error.WriteLine($"[storage] {StorageSchemaTooNewException.ErrorCode}: 数据库 {ex.DatabasePath} 的版本 {ex.SchemaVersion} 高于本程序支持的 {ex.SupportedVersion}，请安装新版 LiveClipper；已拒绝启动且未修改数据。");
    throw;
}
var store=new Store(Path.Combine(settings.StorageRoot,"liveclipper.db"));store.Migrate();
var studioStore=new StudioStore(store.DatabasePath);studioStore.Migrate();
// ===== B12-2 桥接端点：映射执行器可被 --bridge-fake 替换，契约测试不依赖真实 FilmCraft。
IBridgeExecutor bridgeExecutor;
if(bridgeFake)
{
    bridgeExecutor=new FakeBridgeExecutor();
}
else
{
    bridgeExecutor=new FilmCraftBridgeExecutor(
        clipId=>store.Clips().FirstOrDefault(clip=>clip.Id==clipId)?.Path,
        Environment.GetEnvironmentVariable("LIVECLIPPER_NODE")??"node",
        FindBridgeMapper(),
        Environment.GetEnvironmentVariable("LIVECLIPPER_FILMCRAFT_CLI")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"FilmCraft","filmcraft-cli.exe"),
        Environment.GetEnvironmentVariable("LIVECLIPPER_FFPROBE")??Path.Combine(Path.GetDirectoryName(settings.FfmpegPath)??"","ffprobe.exe"),
        settings.StorageRoot,
        null,
        Environment.GetEnvironmentVariable("LIVECLIPPER_FFMPEG")??(File.Exists(settings.FfmpegPath)?settings.FfmpegPath:Path.Combine(Path.GetDirectoryName(settings.FfmpegPath)??"","ffmpeg.exe")),
        clipId=>studioStore.Project(clipId));
}
var bridgeJobs=new BridgeJobService(bridgeExecutor,settings.StorageRoot,bridgeFake?null:clipId=>store.Clips().Any(clip=>clip.Id==clipId));
string FindBridgeMapper()
{
    foreach(var start in new[]{Environment.CurrentDirectory,AppContext.BaseDirectory})
    {
        var dir=new DirectoryInfo(Path.GetFullPath(start));
        for(var depth=0;dir is not null&&depth<8;depth++,dir=dir.Parent)
        {
            var candidate=Path.Combine(dir.FullName,"scripts","filmcraft-b1-map.mjs");
            if(File.Exists(candidate))return candidate;
        }
    }
    return Path.Combine(Environment.CurrentDirectory,"scripts","filmcraft-b1-map.mjs");
}
var coordinator=new LiveCoordinator(store,settings);
var liveStore=new JsonLiveSessionStore(Path.Combine(settings.StorageRoot,"live-core"));
var liveRegistry=new JsonLiveRoomRegistry(Path.Combine(settings.StorageRoot,"live-core","rooms.json"));
var liveManager=new LiveRoomManager(roomId =>
{
    // 每个房间独立持有 probe/recorder/feed，避免一个房间的取消影响另一个房间。
    var probe=new BilibiliRoomProbe(coordinator.Settings.BilibiliCookie);
    return new LiveSessionSupervisor(
        roomId,
        probe,
        probe,
        new FfmpegSegmentRecorder(new FfmpegRecorderOptions
        {
            FfmpegPath=settings.FfmpegPath,
            StorageRoot=Path.Combine(settings.RecordingRoot,"live-core","media"),
            SegmentSeconds=4,
            StallTimeout=TimeSpan.FromSeconds(30)
        }),
        new BilibiliDanmakuFeed(probe),
        liveStore);
});
var liveStorage=new LiveStorageJanitor(
    liveStore,
    new LiveStorageOptions
    {
        BufferRoot=Path.Combine(settings.RecordingRoot,"live-core","media","buffer"),
        MaxTotalBytes=ReadLongEnvironment("LIVECLIPPER_LIVE_MAX_MB",20L*1024)*1024*1024,
        MinFreeBytes=ReadLongEnvironment("LIVECLIPPER_LIVE_MIN_FREE_MB",2L*1024)*1024*1024,
        Retention=TimeSpan.FromDays(ReadLongEnvironment("LIVECLIPPER_LIVE_RETENTION_DAYS",3))
    },
    () => liveManager.Snapshots()
        .Select(status=>status.Session)
        .Where(session=>session is not null)
        .Select(session=>session!.Id)
        .ToHashSet(StringComparer.Ordinal));
var livePreview=new BilibiliPreviewService(new BilibiliRoomProbe(CookieProtector.Unprotect(settings.BilibiliCookie)));
var liveLookup=new BilibiliRoomProbe(CookieProtector.Unprotect(settings.BilibiliCookie));
var liveRecovery=new LiveRoomRecoveryService(liveRegistry);
var liveDiagnostics=new LiveDiagnosticsService(liveStore,liveRegistry,liveStorage,liveRecovery,()=>liveManager.Snapshots());
long ReadLongEnvironment(string name,long fallback)
{
    var raw=Environment.GetEnvironmentVariable(name);
    return long.TryParse(raw,out var value)&&value>=0?value:fallback;
}
// 设置文件里的 Cookie 可能是 DPAPI 密文（dpapi: 前缀）；运行期统一解密成明文，只有落盘前才重新加密。
coordinator.SetBilibiliCookie(CookieProtector.Unprotect(settings.BilibiliCookie));
await coordinator.RecoverHighlightsAsync();
// 上次退出时还在监控的直播间会自动恢复（多房间一起恢复）。
coordinator.ResumeMonitors();
var uidResolver=new BilibiliLiveSource();
var videoExporter=new VideoExportService(store,coordinator.Media);
videoExporter.ResumePending();
var studioMedia=new StudioMediaService(store,studioStore,coordinator.Media,settings.StorageRoot);
// 全片候选流水线：分块缓存/断点续跑、去重合并、最终排序、音频峰值与语音边界优化。
var candidatePipeline=new CandidatePipelineService(store,studioStore,coordinator.Media,new AudioPeakService(coordinator.Media));
var aiAnalysis=new AiAnalysisService(store,studioStore,candidatePipeline);
var audioLibrary=new AudioLibraryService(studioStore,settings.StorageRoot);audioLibrary.EnsureBuiltins();
var musicGeneration=new MusicGenerationService(settings,audioLibrary,studioStore,coordinator.Media);
var demucs=new DemucsService(store,studioStore,settings);demucs.ResumePending();
var editorRenderer=new EditorRenderService(store,studioStore,coordinator.Media,settings.StorageRoot,()=>settings.RenderWorkDirectory,()=>settings.ExportOutputDirectory);editorRenderer.ResumePending();
// C1 MVP：编辑器工程 → 临时 .fcproj（精修入口，仅生成/打开，不回写）。
// 需在 editorRenderer 之后构造：snapshot 回调 = 实际 store clip + editorRenderer.Snapshot。
var bridgeFcproj=new FcprojExportService(
    clipId=>store.Clips().FirstOrDefault(clip=>clip.Id==clipId)?.Path,
    Environment.GetEnvironmentVariable("LIVECLIPPER_NODE")??"node",
    FindBridgeMapper(),
    Environment.GetEnvironmentVariable("LIVECLIPPER_FFPROBE")??Path.Combine(Path.GetDirectoryName(settings.FfmpegPath)??"","ffprobe.exe"),
    settings.StorageRoot,
    clipId=>studioStore.Project(clipId),
    now:null,
    snapshot:(clipId,plan)=>editorRenderer.Snapshot(store.Clips().FirstOrDefault(clip=>clip.Id==clipId)??throw new ArgumentException($"切片不存在（{clipId}）"),plan));
var studioExports=new StudioExportService(store,studioStore,editorRenderer,coordinator.Media,settings);studioExports.ResumePending();
var silenceTrim=new SilenceTrimService(coordinator.Media,()=>settings.PythonPath);
// D6：转录完成仅通知；生成建议和应用编辑均须由用户手动触发。
// 保留显式自动方案入口的连接设置，不在转录完成时启动流程。
AiConnection? AutoConnection()
{
    var provider=NormalizeAiProvider(settings.AiProvider);
    var model=(settings.AiModel??"").Trim();
    if(model.Length==0)return null;
    if(provider=="ollama")return new AiConnection("ollama",settings.AiEndpoint,model,null);
    var endpoint=(settings.AiEndpoint??"").Trim();
    ValidateAiEndpoint(provider,endpoint);
    if(AiAnalysisService.NormalizeEndpoint(endpoint) is null)throw new ArgumentException("兼容 API 地址无效");
    // 凭据由 Resolver 按 provider+地址补全；缺 key 时由服务返回认证失败。
    return AiConnectionResolver.Resolve(new AiConnection("compatible",endpoint,model,null),settings);
}
var autoEdit=new AutoEditService(store,studioStore,studioMedia,aiAnalysis,new EditPlanningService(store,studioStore),silenceTrim,coordinator.Media)
{
    AutoAnalyze=settings.AutoAnalyze,
    AutoTrimSilence=settings.AutoTrimSilence,
    ConnectionFactory=AutoConnection
};
coordinator.Transcription.Completed+=clip=>
{
    if(TranscriptionCompletionPolicy.ShouldQueueAutoEdit(settings,clip))autoEdit.Queue(clip);
};
var ffmpegInstaller=new FfmpegInstaller(configDirectory,path=>
{
    UpdateSettingsDurably(()=>coordinator.SetFfmpegPath(path),indentedSettingsJson);
    return Task.CompletedTask;
});
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(coordinator);
builder.Services.AddSingleton(liveManager);
OllamaManagementEndpoints.RegisterServices(builder.Services);
var app=builder.Build();
// Reset publishes default settings through the same per-path gate as every other settings writer (fresh object, live settings untouched).
var factoryReset=new FactoryResetService(store.DatabasePath,configDirectory,null,defaults=>StartupDurability.UpdateSettings(configPath,defaults,()=>true,new JsonSerializerOptions{WriteIndented=true}));
string? resetToken=null;DateTimeOffset resetTokenExpires=default;var resetRequested=false;
var resetGate=new object();
// 最近一次 Cookie 的来源说明（浏览器名或「内置浏览器」），仅在运行期显示。
string? LastCookieSource=null;
app.Use(async (context,next)=>
{
    var origin=context.Request.Headers.Origin.ToString();
    var host=context.Request.Host.Host;
    var hostAllowed=host is "127.0.0.1" or "localhost" or "app.local";
    if(context.Request.Method is "POST" or "PUT" or "DELETE" && !hostAllowed)
    {
        context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new {error="禁止跨站访问本地控制接口"});return;
    }
    if(context.Request.Method is "POST" or "PUT" or "DELETE" && context.Request.Headers.Origin.Count>0 && origin!=serverUrl && origin!="https://app.local")
    {
        context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new {error="禁止跨站访问本地控制接口"});return;
    }
    if(resetRequested&&context.Request.Path.StartsWithSegments("/api")&&context.Request.Path!="/api/health")
    {context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new {error="正在恢复出厂设置，请等待软件重新载入"});return;}
    await next();
});
app.UseExceptionHandler(handler=>handler.Run(async context=>
{
    var ex=context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    // 调用方的错才是 400 invalid_request：参数校验的 ArgumentException、读不出请求体的 BadHttpRequestException（全限定，避开 Kestrel 的同名旧类型）；其余 500 internal_error。
    var invalidRequest=ex is ArgumentException or Microsoft.AspNetCore.Http.BadHttpRequestException;
    context.Response.StatusCode=invalidRequest?400:500;
    // 响应体是 {error,code} 而不是 RFC 7807 ProblemDetails，所以声明普通 JSON（与 WriteAsJsonAsync 的默认值一致）。
    context.Response.ContentType="application/json; charset=utf-8";
    await context.Response.WriteAsJsonAsync(new {error=ex?.Message??"未知错误",code=invalidRequest?"invalid_request":"internal_error"});
}));
// ===== 静态资源缓存策略（task-54）：发布后浏览器/WebView2 刷新即拿到新 HTML，hash 资源长缓存 =====
// - index.html / legacy.html（及 / → DefaultFiles 重写后的 index.html）：no-cache，允许 ETag 304 条件请求，刷新必回源校验
// - /assets/* 中带内容哈希的文件（vite 产物，如 index-DCBhDtih.js）与 *.map：immutable 一年，减少重复下载
// - 其它静态（png/字体/旧版根目录 js/css 等）：public, max-age=3600，兼顾图标可更新
// 说明：StaticFileOptions.OnPrepareResponse 在 WriteHeader 之前执行，Kestrel/WebView2/浏览器都按响应头生效。
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse=ctx=>
    {
        var path=ctx.Context.Request.Path.Value??"/";
        var name=path[(path.LastIndexOf('/')+1)..];
        // vite 产物形如 index-DCBhDtih.js / index-CLgAMncV.css / index-*.js.map：文件名末尾带 8 位左右内容哈希
        // 注意 hash 可能含连字符（如 index-D5JIaf-D.js），字符集需包含 '-'
        var hashed=System.Text.RegularExpressions.Regex.IsMatch(name,@"-[A-Za-z0-9_-]{6,}\.");
        if(path.StartsWith("/assets/",StringComparison.Ordinal)&&(hashed||name.EndsWith(".map",StringComparison.Ordinal)))
            ctx.Context.Response.Headers.CacheControl="public, max-age=31536000, immutable";
        else if(path=="/"||name.EndsWith(".html",StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl="no-cache, must-revalidate";
        else
            ctx.Context.Response.Headers.CacheControl="public, max-age=3600";
    }
});
// 版号统一来自 csproj 的程序集版本（当前 1.3.0），界面与诊断日志都以此为准。
var appVersion=(System.Reflection.Assembly.GetEntryAssembly()??System.Reflection.Assembly.GetExecutingAssembly()).GetName().Version?.ToString(3)??"0.0.0";
app.MapGet("/api/version",()=>Results.Ok(new {appVersion}));
app.MapGet("/api/updates/check", (CancellationToken ct) => ReleaseUpdateCheck.CheckAsync(appVersion, app.Configuration, ct));
app.MapGet("/api/status",async () =>
{
    var session=coordinator.Session;
    var segments=session is null?Array.Empty<MediaSegment>(): (await store.GetRangeAsync(session.Id,0,double.MaxValue)).ToArray();
    return Results.Ok(new {coordinator.Monitoring,coordinator.Room,coordinator.Session,coordinator.Error,coordinator.DanmakuError,coordinator.DanmakuPackets,coordinator.LastDanmakuCommand,coordinator.DanmakuAuthorized,coordinator.MessageRate,coordinator.Baseline,coordinator.HeatScore,
        rooms=coordinator.Rooms,maxRooms=LiveCoordinator.MaxRooms,heatReason=coordinator.HeatReason,autoHighlightCooldownSeconds=settings.AutoHighlightCooldownSeconds,
        autoHighlightBaselineSeconds=settings.AutoHighlightBaselineSeconds,autoHighlightComparisonSeconds=settings.AutoHighlightComparisonSeconds,autoHighlightBaselineRatio=settings.AutoHighlightBaselineRatio,
        autoHighlightEnabled=settings.AutoHighlight,autoHighlightReleaseRatio=settings.AutoHighlightReleaseRatio,autoHighlightMaxExtensionSeconds=settings.AutoHighlightMaxExtensionSeconds,autoHighlightReleaseSeconds=settings.AutoHighlightReleaseSeconds,autoHighlightRebaseSeconds=settings.AutoHighlightRebaseSeconds,
        highlightPreRollSeconds=settings.HighlightPreRollSeconds,highlightPostRollSeconds=settings.HighlightPostRollSeconds,
        heatTriggerLine=coordinator.HeatTriggerLine,heatReleaseLine=coordinator.HeatReleaseLine,heatActive=coordinator.HeatActive,heatInBaseline=coordinator.HeatInBaseline,
        ffmpegAvailable=new MediaTools(settings.FfmpegPath).Available(),segmentCount=segments.Length,bufferSeconds=segments.Length==0?0:segments[^1].End-segments[0].Start,bufferUnlimited=coordinator.BufferUnlimited,bufferLimitSeconds=coordinator.BufferUnlimited?null:(double?)coordinator.BufferLimitSeconds,danmakuRecordLimit=settings.DanmakuRecordLimit,manualPreRollSeconds=settings.ManualPreRollSeconds,manualPostRollSeconds=settings.ManualPostRollSeconds,appVersion});
});
// ===== 自动切片（弹幕热度）=====
object AutoClipPolicy()=>new
{
    enabled=settings.AutoHighlight,
    cooldownSeconds=settings.AutoHighlightCooldownSeconds,
    baselineSeconds=settings.AutoHighlightBaselineSeconds,
    comparisonSeconds=settings.AutoHighlightComparisonSeconds,
    triggerRatio=settings.AutoHighlightBaselineRatio,
    releaseRatio=settings.AutoHighlightReleaseRatio,
    preRollSeconds=settings.HighlightPreRollSeconds,
    postRollSeconds=settings.HighlightPostRollSeconds,
    maxExtensionSeconds=settings.AutoHighlightMaxExtensionSeconds,
    releaseSeconds=settings.AutoHighlightReleaseSeconds,
    rebaseSeconds=settings.AutoHighlightRebaseSeconds,
    manualPreRollSeconds=settings.ManualPreRollSeconds,
    manualPostRollSeconds=settings.ManualPostRollSeconds,
    danmakuRecordLimit=settings.DanmakuRecordLimit
};
app.MapGet("/api/live/auto-clip",()=>
{
    var clips=store.Clips().OrderByDescending(c=>c.CreatedAt).Select(c=>
    {
        var ev=store.Highlights().FirstOrDefault(h=>h.Id==c.EventId);
        return new {id=c.Id,eventId=c.EventId,path=c.Path,createdAt=c.CreatedAt,source=ev?.Source,triggerTime=ev?.TriggerTime,start=ev?.Start,end=ev?.End,state=ev?.State.ToString(),closed=ev?.Closed??false,partial=ev?.Partial??false,score=ev?.Score??0};
    }).Where(c=>c.source=="danmaku").Take(20).ToArray();
    return Results.Ok(new
    {
        enabled=settings.AutoHighlight,policy=AutoClipPolicy(),
        heat=new {messageRate=coordinator.MessageRate,baseline=coordinator.Baseline,triggerLine=coordinator.HeatTriggerLine,releaseLine=coordinator.HeatReleaseLine,active=coordinator.HeatActive,inBaseline=coordinator.HeatInBaseline,reason=coordinator.HeatReason},
        clips
    });
});
app.MapPost("/api/live/auto-clip",([FromBody] AutoClipPolicyRequest body)=>
{
    IResult? rejection=null;
    TryUpdateSettingsDurably(()=>
    {
    var cooldown=Math.Clamp(body.CooldownSeconds??settings.AutoHighlightCooldownSeconds,10,900);
    var baseline=Math.Clamp(body.BaselineSeconds??settings.AutoHighlightBaselineSeconds,60,3600);
    var comparison=Math.Clamp(body.ComparisonSeconds??settings.AutoHighlightComparisonSeconds,10,600);
    var trigger=Math.Clamp(body.TriggerRatio??settings.AutoHighlightBaselineRatio,1.05,3);
    var release=Math.Clamp(body.ReleaseRatio??settings.AutoHighlightReleaseRatio,1.0,trigger-.01);
    var maxExtension=Math.Clamp(body.MaxExtensionSeconds??settings.AutoHighlightMaxExtensionSeconds,60,7200);
    var pre=(int)Math.Clamp(body.PreRollSeconds??settings.HighlightPreRollSeconds,0,1800);
    var post=(int)Math.Clamp(body.PostRollSeconds??settings.HighlightPostRollSeconds,0,1800);
    if(pre+post<1){rejection=Results.BadRequest(new {error="前录与后录合计至少 1 秒"});return false;}
    // A9-2：manual/danmaku/release/rebase 也由本端点写回同一份 Settings，避免旧/新界面两套口径。
    var manualPre=(int)Math.Clamp(body.ManualPreRollSeconds??settings.ManualPreRollSeconds,0,1800);
    var manualPost=(int)Math.Clamp(body.ManualPostRollSeconds??settings.ManualPostRollSeconds,0,1800);
    if(manualPre+manualPost<1){rejection=Results.BadRequest(new {error="手动前录与后录合计至少 1 秒"});return false;}
    var danmakuLimit=(int)Math.Clamp(body.DanmakuRecordLimit??settings.DanmakuRecordLimit,1000,10000);
    var releaseSeconds=Math.Clamp(double.IsFinite(body.ReleaseSeconds??settings.AutoHighlightReleaseSeconds)?(body.ReleaseSeconds??settings.AutoHighlightReleaseSeconds):200,10,1800);
    var rebaseSeconds=Math.Clamp(double.IsFinite(body.RebaseSeconds??settings.AutoHighlightRebaseSeconds)?(body.RebaseSeconds??settings.AutoHighlightRebaseSeconds):600,60,3600);
    var enabled=body.Enabled??settings.AutoHighlight;
    coordinator.SetAutoHighlightEnabled(enabled);
    coordinator.SetHighlightDurations(pre,post);
    coordinator.SetManualHighlightDurations(manualPre,manualPost);
    coordinator.SetDanmakuRecordLimit(danmakuLimit);
    coordinator.SetDanmakuHeatPolicy(cooldown,baseline,comparison,trigger,release,maxExtension);
    settings.AutoHighlight=enabled;
    settings.HighlightPreRollSeconds=pre;settings.HighlightPostRollSeconds=post;
    LiveSettingsMutations.ApplyLivePolicy(settings,pre,post,manualPre,manualPost,danmakuLimit,releaseSeconds,rebaseSeconds);
    settings.AutoHighlightCooldownSeconds=cooldown;settings.AutoHighlightBaselineSeconds=baseline;
    settings.AutoHighlightComparisonSeconds=comparison;settings.AutoHighlightBaselineRatio=trigger;
    settings.AutoHighlightReleaseRatio=release;settings.AutoHighlightMaxExtensionSeconds=maxExtension;
    return true;
    });
    return rejection??Results.Ok(new {saved=true,policy=AutoClipPolicy()});
});
// 返回给界面时解密 Cookie，设置文件里保存的始终是 dpapi: 密文。
LiveSettings SnapshotSettings()
{
    var copy=JsonSerializer.Deserialize<LiveSettings>(JsonSerializer.Serialize(settings))??new LiveSettings();
    copy.BilibiliCookie=CookieProtector.Unprotect(copy.BilibiliCookie);
    // AI 密钥无论明文还是密文都不离开本机设置文件；界面只能通过 hasApiKey 知道是否已配置。
    copy.AiApiKey="";
    copy.MusicApiKey="";
    return copy;
}
app.MapGet("/api/settings",()=>Results.Ok(SnapshotSettings()));
app.MapPost("/api/filmcraft/cli/export",async (HttpRequest request,[FromBody] FilmCraftExportRequest body,CancellationToken token)=>
{
    var origin=request.Headers.Origin.ToString();
    if(!string.IsNullOrEmpty(origin)&&origin!=serverUrl&&origin!="https://app.local")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var result=await FilmCraftCliRunner.ExportAsync(body,token);
    return result.Ok?Results.Ok(result):Results.BadRequest(result);
});
var cutsGate=new SemaphoreSlim(1,1);
app.MapPost("/api/filmcraft/cli/cuts",async (HttpRequest request,[FromBody] FilmCraftCutsRequest body,CancellationToken token)=>
{
    var origin=request.Headers.Origin.ToString();
    if(!string.IsNullOrEmpty(origin)&&origin!=serverUrl&&origin!="https://app.local")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(!await cutsGate.WaitAsync(0))
        return Results.Json(new FilmCraftCutsResult(false,"busy","已有批量工程任务在运行"),statusCode:StatusCodes.Status409Conflict);
    try
    {
        var result=await FilmCraftCliRunner.BuildCutsAsync(body,token);
        return result.Ok?Results.Ok(result):Results.BadRequest(result);
    }
    finally
    {
        cutsGate.Release();
    }
});
app.MapGet("/api/settings/factory-reset",()=>
{
    factoryReset.ValidateTargets();lock(resetGate){resetToken=Guid.NewGuid().ToString("N");resetTokenExpires=DateTimeOffset.UtcNow.AddMinutes(5);return Results.Ok(new {token=resetToken,summary=factoryReset.Summary(store,studioStore)});}
});
app.MapPost("/api/settings/factory-reset",([FromBody] FactoryResetRequest request)=>
{
    lock(resetGate)
    {
        if(request.Confirmation!="RESET"||request.Token!=resetToken||resetToken is null||DateTimeOffset.UtcNow>resetTokenExpires)return Results.BadRequest(new {error="请重新打开恢复出厂设置窗口并确认"});
        if(ffmpegInstaller.Status.State is "downloading" or "verifying" or "installing"||aiAnalysis.DownloadState.State=="downloading"||musicGeneration.DownloadState.State=="downloading")return Results.Conflict(new {error="请等待工具或模型下载完成后再重设"});
        factoryReset.ValidateTargets();factoryResetId=resetToken;resetRequested=true;resetToken=null;
    }
    _=Task.Run(async()=>{await Task.Delay(400);await app.StopAsync();});
    return Results.Ok(new {resetting=true,operationId=factoryResetId});
});
app.MapGet("/api/resolve-uid/{uid:long}",(long uid,CancellationToken token)=>LiveEndpoints.ResolveUidAsync(uid,uidResolver.ResolveUserAsync,token));
app.MapGet("/api/ffmpeg/download",()=>Results.Ok(ffmpegInstaller.Status));
app.MapPost("/api/ffmpeg/download",()=>ffmpegInstaller.Start()?Results.Ok(ffmpegInstaller.Status):Results.Conflict(new {error="FFmpeg 已在下载中"}));
// ===== 设置读写：全量 POST（旧 UI，保持原语义）+ PATCH 局部合并（UI4-B，task-149）=====
// 两条路径共用 LiveSettingsMutations.WritableFields / ValidateForFullSave 与 ApplySettingsBody 映射，避免再漂移。
void ApplySettingsBody(LiveSettings body)
{
    coordinator.SetHighlightDurations(body.HighlightPreRollSeconds,body.HighlightPostRollSeconds);
    coordinator.SetManualHighlightDurations(body.ManualPreRollSeconds,body.ManualPostRollSeconds);
    coordinator.SetDanmakuRecordLimit(body.DanmakuRecordLimit);
    // A9 P0-1：五个字段此前只进 coordinator、未写回 settings，重启即丢；这里统一落盘。
    LiveSettingsMutations.ApplyAutoClipDurations(settings,body.HighlightPreRollSeconds,body.HighlightPostRollSeconds,body.ManualPreRollSeconds,body.ManualPostRollSeconds,body.DanmakuRecordLimit);
    // 弹幕高能参数按范围收敛，超出范围的输入直接夹住，不必让整份设置保存失败。
    var cooldown=Math.Clamp(double.IsFinite(body.AutoHighlightCooldownSeconds)?body.AutoHighlightCooldownSeconds:60,10,900);
    var baseline=Math.Clamp(double.IsFinite(body.AutoHighlightBaselineSeconds)?body.AutoHighlightBaselineSeconds:600,60,3600);
    var comparison=Math.Clamp(double.IsFinite(body.AutoHighlightComparisonSeconds)?body.AutoHighlightComparisonSeconds:120,10,600);
    var ratio=Math.Clamp(double.IsFinite(body.AutoHighlightBaselineRatio)?body.AutoHighlightBaselineRatio:1.4,1.05,3);
    var release=Math.Clamp(double.IsFinite(body.AutoHighlightReleaseRatio)?body.AutoHighlightReleaseRatio:1.2,1.0,ratio-.01);
    var maxExtension=Math.Clamp(double.IsFinite(body.AutoHighlightMaxExtensionSeconds)?body.AutoHighlightMaxExtensionSeconds:1800,60,7200);
    var releaseSeconds=Math.Clamp(double.IsFinite(body.AutoHighlightReleaseSeconds)?body.AutoHighlightReleaseSeconds:200,10,1800);
    var rebaseSeconds=Math.Clamp(double.IsFinite(body.AutoHighlightRebaseSeconds)?body.AutoHighlightRebaseSeconds:600,60,3600);
    coordinator.SetDanmakuHeatPolicy(cooldown,baseline,comparison,ratio,release,maxExtension);
    coordinator.SetBilibiliCookie(body.BilibiliCookie);
    settings.RoomId=body.RoomId;settings.BilibiliUid=body.BilibiliUid;settings.AutoHighlight=body.AutoHighlight;settings.AutoTranscribe=body.AutoTranscribe;
    settings.AutoAnalyze=body.AutoAnalyze;settings.AutoTrimSilence=body.AutoTrimSilence;
    // Cookie 归一后用 DPAPI 加密写入本机设置文件；运行期仍是明文，重启后启动代码会自动解密。
    settings.BilibiliCookie=CookieProtector.Protect(CookieProtector.Normalize(body.BilibiliCookie));
    settings.AutoHighlightCooldownSeconds=cooldown;
    settings.AutoHighlightBaselineSeconds=baseline;
    settings.AutoHighlightComparisonSeconds=comparison;
    settings.AutoHighlightBaselineRatio=ratio;
    settings.AutoHighlightReleaseRatio=release;
    LiveSettingsMutations.ApplyReleasePolicy(settings,releaseSeconds,rebaseSeconds);
    settings.AutoHighlightMaxExtensionSeconds=maxExtension;
    autoEdit.AutoAnalyze=body.AutoAnalyze;autoEdit.AutoTrimSilence=body.AutoTrimSilence;
    settings.FfmpegPath=body.FfmpegPath;settings.PythonPath=body.PythonPath;settings.WhisperModel=body.WhisperModel;
    settings.AiProvider=body.AiProvider;settings.AiEndpoint=body.AiEndpoint;settings.AiModel=body.AiModel;settings.LocalOutputRoot=body.LocalOutputRoot;
    settings.CleanupOnExit=body.CleanupOnExit;
    settings.StorageRoot=body.StorageRoot;
    settings.RecordingDirectory=RenderStoragePaths.NormalizeDirectory(body.RecordingDirectory);
    settings.RenderWorkDirectory=RenderStoragePaths.NormalizeDirectory(body.RenderWorkDirectory);
    settings.ExportOutputDirectory=RenderStoragePaths.NormalizeDirectory(body.ExportOutputDirectory);
}
// 旧 UI：全量覆盖语义保持不变（legacy 下线前仍需它）。
app.MapPost("/api/settings",([FromBody] LiveSettings body)=>
{
    if(body.RecordingDirectory!=settings.RecordingDirectory&&(coordinator.Rooms.Any(r=>r.AutoRecord||r.CaptureMode=="highlight-cache")||liveManager.Snapshots().Any(r=>r.AutoRecord)))
        return Results.Conflict(new {error="请先停止各房间监控与采集，再修改录制目录。"});
    var invalidFields=LiveSettingsMutations.ValidateForFullSave(body);
    if(invalidFields.Count>0)
        return ValidationError(invalidFields);
    UpdateSettingsDurably(()=>ApplySettingsBody(body),indentedSettingsJson);
    return Results.Ok(new {saved=true,restartRequired=true});
});
// UI4-B：局部合并——只写请求体出现的键，未出现保持原值；未知键忽略；类型/范围非法 400 且不落盘。
app.MapMethods("/api/settings",new[]{"PATCH"},async (HttpRequest request)=>
{
    JsonDocument document;
    try { document=await JsonDocument.ParseAsync(request.Body); }
    catch(JsonException) { return ValidationError(["body"]); }
    using(document)
    {
        List<string> invalidFields=[];
        List<string> changedFields=[];
        if(!TryUpdateSettingsDurably(()=>
        {
            // PATCH 的快照也必须在门内读取，不能先合并旧快照再等待写锁。
            if(!LiveSettingsMutations.TryApplyPatch(settings,document.RootElement,out var merged,out invalidFields,out changedFields))return false;
            // 副本里的 Cookie 是 dpapi: 密文，先解回明文，再交给共享映射重新加密落盘（避免二次加密）。
            merged.BilibiliCookie=CookieProtector.Unprotect(merged.BilibiliCookie);
            if(merged.RecordingDirectory!=settings.RecordingDirectory&&(coordinator.Rooms.Any(r=>r.AutoRecord||r.CaptureMode=="highlight-cache")||liveManager.Snapshots().Any(r=>r.AutoRecord)))
            {invalidFields.Add("recordingDirectory: 请先停止监控与采集");return false;}
            ApplySettingsBody(merged);
            return true;
        }))return ValidationError(invalidFields);
        string[] restartFields=["storageRoot","ffmpegPath","pythonPath","whisperModel"];
        var restartRequired=changedFields.Any(field=>restartFields.Contains(field));
        return Results.Ok(new {saved=true,restartRequired});
    }
});
static IResult ValidationError(IEnumerable<string> fields)=>
    Results.Json(new Dictionary<string,object>{{"error",new Dictionary<string,object>{{"class","Validation"},{"fields",fields.ToArray()}}}},statusCode:StatusCodes.Status400BadRequest);
// ===== AI 连接设置（React 设置页 /settings 使用；旧 UI 的全量 /api/settings 行为保持不变）=====
// 密钥只在设置文件里以 DPAPI 密文（dpapi: 前缀）保存，本组接口既不返回明文也不返回密文。
string NormalizeAiProvider(string? provider)=>string.Equals(provider?.Trim(),"compatible",StringComparison.OrdinalIgnoreCase)?"compatible":"ollama";
void ValidateAiEndpoint(string provider,string endpoint)
{
    if(provider!="compatible")return;
    if(string.IsNullOrWhiteSpace(endpoint))throw new ArgumentException("请填写兼容 API 地址");
    if(!Uri.TryCreate(endpoint.Trim(),UriKind.Absolute,out var uri)||uri.Scheme!="https"&&!(uri.IsLoopback&&uri.Scheme=="http"))
        throw new ArgumentException("云端 API 地址必须使用 HTTPS");
}
object AiSettingsSnapshot()=>new
{
    provider=NormalizeAiProvider(settings.AiProvider),
    endpoint=settings.AiEndpoint,
    model=settings.AiModel,
    hasApiKey=!string.IsNullOrWhiteSpace(settings.AiApiKey),
    ollamaEndpoint=OllamaRuntime.BaseUri.ToString(),
    defaultModel="qwen2.5:7b-instruct",
    settingsVersion=settings.SettingsVersion
};
app.MapGet("/api/settings/ai",()=>Results.Ok(AiSettingsSnapshot()));
app.MapPost("/api/settings/ai",([FromBody] AiSettingsRequest body)=>
{
    var provider=NormalizeAiProvider(body.Provider);
    var endpoint=(body.Endpoint??"").Trim();
    var model=(body.Model??"").Trim();
    ValidateAiEndpoint(provider,endpoint);
    var saved=TryUpdateSettingsDurably(()=>
    {
    if(string.IsNullOrWhiteSpace(model))model=settings.AiModel;
    if(string.IsNullOrWhiteSpace(model))return false;
    settings.AiProvider=provider;
    settings.AiEndpoint=endpoint;
    settings.AiModel=model;
    // ApiKey 为空表示“保持原密钥”；显式清空走 ClearApiKey，避免页面回显空值时误删。
    if(body.ClearApiKey==true)settings.AiApiKey="";
    else if(!string.IsNullOrWhiteSpace(body.ApiKey))settings.AiApiKey=CookieProtector.Protect(body.ApiKey.Trim());
    return true;
    });
    if(!saved)return Results.BadRequest(new {error="请选择或填写模型"});
    return Results.Ok(new {saved=true,ai=AiSettingsSnapshot()});
});
// ===== 音乐生成配置：local | custom-http（同步返回 WAV）。密钥同样只以 DPAPI 密文落盘，接口不回显。 =====
object MusicGenerationSnapshot()=>new
{
    provider=settings.MusicGenerationProvider,
    generationUrl=settings.MusicApiUrl,
    model=settings.MusicApiModel,
    authMode=settings.MusicApiAuthMode,
    hasApiKey=!string.IsNullOrWhiteSpace(settings.MusicApiKey)
};
app.MapGet("/api/settings/music-generation",()=>Results.Ok(MusicGenerationSnapshot()));
app.MapPost("/api/settings/music-generation",([FromBody] MusicGenerationSettingsRequest body)=>
{
    string? error=null;
    var saved=TryUpdateSettingsDurably(()=>
    {
        // 未给出的字段保持原值；空字符串是明确的清空。
        var provider=(body.Provider??settings.MusicGenerationProvider??"local").Trim().ToLowerInvariant();
        var url=(body.GenerationUrl??settings.MusicApiUrl??"").Trim();
        var model=(body.Model??settings.MusicApiModel??"").Trim();
        var authMode=(body.AuthMode??settings.MusicApiAuthMode??"none").Trim().ToLowerInvariant();
        var newKey=body.ApiKey?.Trim();
        if(provider!="local"&&provider!="custom-http"){error="音乐生成 provider 只能是 local 或 custom-http";return false;}
        if(authMode!="none"&&authMode!="bearer"){error="音乐 API 鉴权方式只能是 none 或 bearer";return false;}
        if(model.Length>200||model.Any(char.IsControl)){error="音乐模型名无效（最长 200 字符，不能含控制字符）";return false;}
        if(url.Length>0)
        {
            if(url.Contains('?')||url.Contains('#')){error="音乐 API 地址不能包含查询参数或片段";return false;}
            if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||string.IsNullOrEmpty(uri.Host)||uri.Scheme!="https"&&!(uri.IsLoopback&&uri.Scheme=="http")){error="音乐 API 地址必须是完整的 HTTPS 地址（本机回环可用 HTTP）";return false;}
            if(!string.IsNullOrEmpty(uri.UserInfo)){error="音乐 API 地址不能包含用户名或密码";return false;}
        }
        if(provider=="custom-http"&&(url.Length==0||model.Length==0)){error="自定义 HTTP 模式必须填写音乐 API 地址和模型名";return false;}
        if(body.ClearApiKey==true&&!string.IsNullOrWhiteSpace(newKey)){error="不能同时提供新密钥和清除密钥";return false;}
        if(!string.IsNullOrWhiteSpace(newKey))
        {
            if(authMode=="none"){error="鉴权方式为 none 时不能携带密钥";return false;}
            if(newKey.Length>4096||newKey.Any(char.IsControl)){error="音乐 API 密钥格式无效";return false;}
        }
        var hadKey=!string.IsNullOrWhiteSpace(settings.MusicApiKey);
        var endpointChanged=!string.Equals(url,settings.MusicApiUrl??"",StringComparison.Ordinal)||authMode!=settings.MusicApiAuthMode;
        if(hadKey&&endpointChanged&&body.ClearApiKey!=true&&string.IsNullOrWhiteSpace(newKey)){error="音乐 API 地址或鉴权方式已更改，请重新输入密钥或选择清除已保存的密钥（旧密钥不会发往新地址）";return false;}
        var keyAfter=body.ClearApiKey==true?"":!string.IsNullOrWhiteSpace(newKey)?CookieProtector.Protect(newKey):settings.MusicApiKey??"";
        if(provider=="custom-http"&&authMode=="bearer"&&string.IsNullOrWhiteSpace(keyAfter)){error="Bearer 鉴权必须提供密钥";return false;}
        settings.MusicGenerationProvider=provider;
        settings.MusicApiUrl=url;
        settings.MusicApiModel=model;
        settings.MusicApiAuthMode=authMode;
        settings.MusicApiKey=keyAfter;
        return true;
    });
    if(!saved)return Results.BadRequest(new {error=error??"音乐生成配置保存失败"});
    return Results.Ok(new {saved=true,music=MusicGenerationSnapshot()});
});
app.MapPost("/api/settings/ai/test",async ([FromBody] AiSettingsRequest? body,CancellationToken token)=>
{
    var provider=NormalizeAiProvider(body?.Provider??settings.AiProvider);
    var endpoint=(body?.Endpoint??settings.AiEndpoint??"").Trim();
    var model=(body?.Model??settings.AiModel??"").Trim();
    // 只在 provider 与规范化地址都匹配时复用已存密钥。
    string? apiKey=null;
    if(!string.IsNullOrWhiteSpace(body?.ApiKey))apiKey=body!.ApiKey!.Trim();
    else if(provider==NormalizeAiProvider(settings.AiProvider)
        &&AiAnalysisService.NormalizeEndpoint(endpoint) is {} normalizedEndpoint
        &&string.Equals(normalizedEndpoint,AiAnalysisService.NormalizeEndpoint(settings.AiEndpoint),StringComparison.Ordinal)
        &&!string.IsNullOrWhiteSpace(settings.AiApiKey))
    {
        try{apiKey=CookieProtector.Unprotect(settings.AiApiKey);}catch{apiKey=null;}
    }
    var watch=Stopwatch.StartNew();
    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
    timeout.CancelAfter(TimeSpan.FromSeconds(8));
    try
    {
        ValidateAiEndpoint(provider,endpoint);
        var models=await aiAnalysis.ModelsAsync(new AiConnection(provider,endpoint,model,apiKey),timeout.Token);
        watch.Stop();
        var modelAvailable=model.Length>0&&models.Contains(model,StringComparer.OrdinalIgnoreCase);
        return Results.Ok(new {ok=true,code=modelAvailable||model.Length==0?"ok":"model_not_found",models,elapsedMs=watch.ElapsedMilliseconds,modelAvailable,error=(string?)null});
    }
    catch(OperationCanceledException) when(!token.IsCancellationRequested)
    {
        watch.Stop();
        return Results.Ok(new {ok=false,code="timeout",error="连接超时（8 秒），请检查地址与网络",models=Array.Empty<string>(),elapsedMs=watch.ElapsedMilliseconds,modelAvailable=false});
    }
    catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or HttpRequestException or JsonException or UriFormatException)
    {
        watch.Stop();
        // 不回显底层 ex.Message（可能含 URL/主机）；只返回本服务自己构造的可读文案。
        var (failCode,failMessage)=ex switch
        {
            HttpRequestException {StatusCode: not null} http=>(http.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed?"invalid":"network",http.Message),
            HttpRequestException=>("network","无法连接到该地址，请检查地址、网络或证书"),
            JsonException or UriFormatException=>("invalid",ex is JsonException?ex.Message:"API 地址格式无效"),
            _=>("invalid",ex.Message)
        };
        return Results.Ok(new {ok=false,code=failCode,error=failMessage,models=Array.Empty<string>(),elapsedMs=watch.ElapsedMilliseconds,modelAvailable=false});
    }
});
// ===== Cookie 自动识别 =====
// 把 Cookie 归一化后写入运行期与设置文件（设置文件里始终是 DPAPI 密文）。
void ApplyBilibiliCookie(string cookie,string source)
{
    var normalized=CookieProtector.Normalize(cookie);
    if(!normalized.Contains("SESSDATA=",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Cookie 里没有 SESSDATA，登录信息不完整");
    UpdateSettingsDurably(()=>
    {
        coordinator.SetBilibiliCookie(normalized);
        settings.BilibiliCookie=CookieProtector.Protect(normalized);
    },indentedSettingsJson);
    LastCookieSource=source;
}

string CookieHint(string cookie)
{
    var parts=CookieProtector.Normalize(cookie).Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)
        .Select(x=>x.Split('=',2)[0]).Where(x=>x.Length>0).Take(6).ToArray();
    return parts.Length==0?"":string.Join("、",parts);
}
async Task<object> VerifyCookieAsync(string cookie,CancellationToken token)
{
    try
    {
        var source=new BilibiliLiveSource{Cookie=CookieProtector.Normalize(cookie)};
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var result=await source.VerifyCookieAsync(timeout.Token);
        return new {login=result.IsLogin,userName=result.UserName,uid=result.Uid};
    }
    catch(Exception ex){return new {login=false,error=ex.Message};}
}
// No ambient browser session access in the public source edition.
app.MapPost("/api/cookies/detect", () => Results.Ok(new { found = false, applied = false, sources = Array.Empty<object>(), summary = "公开源码版未启用浏览器登录检测。请使用软件内登录。" }));
// 软件内扫码登录（桌面外壳从 WebView2 里取到 Cookie 后调这里落地）。
app.MapPost("/api/cookies/import",async (CookieImportRequest body,CancellationToken token)=>
{
    if(string.IsNullOrWhiteSpace(body.Cookie))return Results.BadRequest(new {error="没有收到 Cookie"});
    var verify=await VerifyCookieAsync(body.Cookie,token);
    var source=string.IsNullOrWhiteSpace(body.Source)?"内置浏览器":body.Source!;
    try{ApplyBilibiliCookie(body.Cookie,source);}
    catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
    return Results.Ok(new {applied=true,source,keys=CookieHint(body.Cookie),verify});
});
app.MapGet("/api/cookies/status",()=>Results.Ok(new {configured=!string.IsNullOrWhiteSpace(coordinator.Settings.BilibiliCookie),source=LastCookieSource,uid=coordinator.Settings.BilibiliUid}));
app.MapPost("/api/start",(StartRequest request)=>
{
    if((request.AutoRecord||request.CacheEnabled)&&!coordinator.Media.Available())return Results.BadRequest(new {error="自动录播需要 FFmpeg。"});
    if((request.AutoRecord||request.CacheEnabled)&&string.IsNullOrWhiteSpace(settings.RecordingDirectory))return Results.BadRequest(new {error="请先设置录制目录，再开启自动录播。"});
    UpdateSettingsDurably(()=>
    {
        if(request.Uid is >0)settings.BilibiliUid=request.Uid.Value;
        coordinator.Start(request.RoomId,request.AutoRecord,request.CacheEnabled);
    },indentedSettingsJson);
    return Results.Ok(new {started=true});
});
app.MapPost("/api/rooms/{roomId:long}/recording",async(long roomId,RecordingModeRequest body)=>
{
    try
    {
        await UpdateSettingsDurablyAsync(()=>coordinator.SetAutoRecordAsync(roomId,body.Enabled));
        return Results.Ok(new {rooms=coordinator.Rooms});
    }
    catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
    catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
});
app.MapPost("/api/rooms/{roomId:long}/cache",async(long roomId,RecordingModeRequest body)=>
{
    try
    {
        await UpdateSettingsDurablyAsync(()=>coordinator.SetCacheEnabledAsync(roomId,body.Enabled));
        return Results.Ok(new {rooms=coordinator.Rooms});
    }
    catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
    catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
});
app.MapPost("/api/stop",async(long? roomId)=>
{
    // StopAsync 在 await 后修改监控列表；整个操作持有与同步端点相同的门。
    await UpdateSettingsDurablyAsync(()=>coordinator.StopAsync(roomId));
    return Results.Ok(new {stopped=true,rooms=coordinator.Rooms});
});
app.MapPost("/api/mark",async(long? roomId)=>Results.Ok(await coordinator.MarkAsync(default,roomId)));
// ===== 记录清理：单条删除与一键清空。文件只删软件数据目录内的内容，用户的原始视频与导出成片不动。 =====
app.MapDelete("/api/clips/{id}",(string id)=>
{
    var clip=store.Clips().FirstOrDefault(x=>x.Id==id)??throw new InvalidOperationException("切片不存在");
    store.DeleteClip(clip,settings.StorageRoot);
    return Results.Ok(new {deleted=true});
});
app.MapDelete("/api/sessions/{id}",(string id)=>
{
    var session=store.Sessions().FirstOrDefault(x=>x.Id==id)??throw new InvalidOperationException("录制会话不存在");
    store.DeleteSession(session,settings.StorageRoot);
    return Results.Ok(new {deleted=true});
});
app.MapDelete("/api/studio/videos/{clipId}",(string clipId)=>
{
    if(!studioStore.LocalVideos().Any(v=>v.ClipId==clipId))throw new InvalidOperationException("本地视频不存在");
    foreach(var path in studioStore.DeleteLocalVideo(clipId,settings.StorageRoot))
        try{if(File.Exists(path)){File.SetAttributes(path,FileAttributes.Normal);File.Delete(path);}}catch(IOException){}catch(UnauthorizedAccessException){}
    return Results.Ok(new {deleted=true});
});
app.MapDelete("/api/recordings/all",()=>{
    var sessions=store.Sessions();
    foreach(var video in studioStore.LocalVideos().ToArray())
        foreach(var path in studioStore.DeleteLocalVideo(video.ClipId,settings.StorageRoot))
            try{if(File.Exists(path)){File.SetAttributes(path,FileAttributes.Normal);File.Delete(path);}}catch(IOException){}catch(UnauthorizedAccessException){}
    foreach(var session in sessions)store.DeleteSession(session,settings.StorageRoot);
    foreach(var clip in store.Clips())store.DeleteClip(clip,settings.StorageRoot);
    return Results.Ok(new {cleared=true,sessions=sessions.Count});
});
app.MapGet("/api/rooms",()=>Results.Ok(new {rooms=coordinator.Rooms,maxRooms=LiveCoordinator.MaxRooms}));
// 直播监控的近实时预览：把会话里最近的录制分片（4 秒一段的 MPEG-TS）拼成滑动 HLS 播放列表。
// 画面来自本机正在录制的文件，不额外拉一次直播流，也不依赖外部网站。
app.MapGet("/api/rooms/{roomId:long}/live.m3u8",(HttpContext context,long roomId)=>
{
    // 播放列表每次都要最新的分片窗口，不允许被浏览器缓存。
    context.Response.Headers.CacheControl="no-store";
    var room=coordinator.Rooms.FirstOrDefault(x=>x.RoomId==roomId);
    var sessionId=room?.SessionId;
    var segments=sessionId is null?Array.Empty<MediaSegment>() as IReadOnlyList<MediaSegment>:store.LatestSegments(sessionId,6);
    var live=room?.State is RecordingState.Connecting or RecordingState.Recording or RecordingState.Reconnecting;
    var lines=new List<string>{"#EXTM3U","#EXT-X-VERSION:3"};
    if(segments.Count>0)
    {
        lines.Add($"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(segments.Max(s=>s.End-s.Start))+1}");
        lines.Add($"#EXT-X-MEDIA-SEQUENCE:{segments[0].Sequence}");
        foreach(var s in segments)
        {
            lines.Add($"#EXTINF:{s.End-s.Start:0.###},");
            // 用分片 ID 精确定位文件，重连换目录后也不会串到旧的 run 目录。
            lines.Add($"{s.Sequence:000000000}.ts?sid={s.Id}");
        }
    }
    else lines.Add("#EXT-X-TARGETDURATION:4");
    if(!live)lines.Add("#EXT-X-ENDLIST");
    return Results.Text(string.Join("\n",lines)+"\n","application/vnd.apple.mpegurl",System.Text.Encoding.UTF8);
});
// 分片走 /api/rooms/{id}/{name}：m3u8 里的相对地址正好解析到这里，也和上面的 live.m3u8 字面量路由不冲突。
app.MapGet("/api/rooms/{roomId:long}/{name}",(long roomId,string name,string? sid)=>
{
    // 只按分片 ID 查库定位真实路径；文件名仅用于让播放器区分条目。
    var sessionId=coordinator.Rooms.FirstOrDefault(x=>x.RoomId==roomId)?.SessionId;
    var segment=sessionId is null||string.IsNullOrEmpty(sid)?null:store.LatestSegments(sessionId,60).FirstOrDefault(x=>x.Id==sid);
    return segment is not null&&Path.GetFullPath(segment.Path).StartsWith(Path.GetFullPath(Path.Combine(settings.RecordingRoot,"buffer"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&File.Exists(segment.Path)
        ?Results.File(segment.Path,"video/mp2t",enableRangeProcessing:true)
        :Results.NotFound();
});
// 补全历史弹幕里被 B 站打码的用户名（"最***" → 完整昵称）。
app.MapPost("/api/danmaku/resolve-names",(int? max)=>Results.Ok(new {queued=coordinator.BackfillUserNames(Math.Clamp(max??200,1,1000))}));
app.MapGet("/api/sessions",()=>Results.Ok(store.Sessions()));
app.MapGet("/api/highlights",()=>Results.Ok(store.Highlights()));
string? DanmakuSession(string? selected)=>selected??coordinator.Session?.Id??store.Sessions().FirstOrDefault()?.Id;
app.MapGet("/api/danmaku",(string? sessionId)=>
{
    var id=DanmakuSession(sessionId);
    return Results.Ok(id is null?Array.Empty<DanmakuEvent>():store.RecentDanmaku(id,settings.DanmakuRecordLimit));
});
app.MapDelete("/api/danmaku/all-sessions",()=>Results.Ok(new {deleted=store.ClearAllDanmaku()}));
app.MapDelete("/api/danmaku/{id}",(string id,string? sessionId)=>
{
    var selected=DanmakuSession(sessionId);
    return selected is null?Results.NotFound(new {error="没有可清理的录制会话"}):Results.Ok(new {deleted=store.DeleteDanmaku(selected,id)});
});
app.MapDelete("/api/danmaku",(string? sessionId)=>
{
    var selected=DanmakuSession(sessionId);
    return selected is null?Results.NotFound(new {error="没有可清理的录制会话"}):Results.Ok(new {deleted=store.ClearDanmaku(selected)});
});
app.MapGet("/api/danmaku/hotwords",(string? sessionId)=>
{
    var id=DanmakuSession(sessionId);
    var events=id is null?Array.Empty<DanmakuEvent>():store.DanmakuForHotWords(id).ToArray();
    return Results.Ok(new {sessionId=id,scope="session",windowSeconds=0,firstSeconds=events.Length==0?0:events[0].SessionTime,lastSeconds=events.Length==0?0:events[^1].SessionTime,messageCount=events.Length,words=DanmakuHotWords.Analyze(events)});
});
app.MapGet("/api/clips",()=>Results.Ok(store.Clips().Select(c=>new {c.Id,c.EventId,c.Path,c.SrtPath,c.CreatedAt,
    // 列表显示名：高光素材用规范目录名（含同日序号），导入视频用原文件名。
    Title=OutputNaming.HighlightBase(c)??OutputNaming.SafeName(Path.GetFileNameWithoutExtension(c.Path))??c.EventId[..8]})));
app.MapGet("/api/video-exports",()=>
{
    var jobs=store.Jobs().ToDictionary(x=>x.Id);
    return Results.Ok(store.VideoExports().Select(x=>new {x.Id,x.ClipId,x.EventId,x.BitrateKbps,x.Resolution,x.Fps,x.CreatedAt,name=Path.GetFileName(x.OutputPath),status=jobs.GetValueOrDefault(x.Id)?.Status??"unknown",progress=jobs.GetValueOrDefault(x.Id)?.Progress??0,error=jobs.GetValueOrDefault(x.Id)?.Error}));
});
app.MapPost("/api/clips/{id}/export",(string id,[FromBody] VideoExportOptions options)=>
{
    var clip=store.Clips().FirstOrDefault(x=>x.Id==id);
    return clip is null?Results.NotFound(new {error="切片不存在"}):Results.Ok(videoExporter.Queue(clip,options.BitrateKbps,options.Resolution,options.Fps));
});
app.MapGet("/api/video-exports/{id}/video",(string id)=>
{
    var export=store.VideoExports().FirstOrDefault(x=>x.Id==id);
    var job=store.Jobs().FirstOrDefault(x=>x.Id==id);
    return export is null||job?.Status!="completed"||!File.Exists(export.OutputPath)
        ?Results.NotFound(new {error="导出视频尚未完成"})
        :Results.File(export.OutputPath,"video/mp4",Path.GetFileName(export.OutputPath),enableRangeProcessing:true);
});
app.MapPost("/api/transcribe/{eventId}",(string eventId)=>{var receipt=coordinator.QueueTranscriptionReceipt(eventId);return Results.Ok(new {queued=true,jobId=receipt.JobId,disposition=receipt.Disposition});});
// 保留 restarted 旧字段语义；disposition 区分新建与沿用 pending 任务。
app.MapPost("/api/transcribe/{eventId}/restart",(string eventId)=>{var receipt=coordinator.RestartTranscriptionReceipt(eventId);return Results.Ok(new {queued=true,restarted=true,jobId=receipt.JobId,disposition=receipt.Disposition});});
app.MapPost("/api/highlights/{eventId}/retry",async (string eventId,CancellationToken token)=>{await coordinator.RetryHighlightAsync(eventId,token);return Results.Ok(new {ready=true});});
app.MapMethods("/api/clips/{id}/video",new[]{"GET","HEAD"},(HttpContext http,string id)=>
{
    http.Response.Headers.AcceptRanges="bytes";
    var clip=store.Clips().FirstOrDefault(x=>x.Id==id);
    return clip is null||!File.Exists(clip.Path)?Results.NotFound(new {error="视频文件不存在"}):Results.File(clip.Path,"video/mp4",enableRangeProcessing:true);
});
TranscriptReadEndpoints.Map(app,store,studioStore);
Clip AutoClip(string id)=>store.Clips().FirstOrDefault(x=>x.Id==id)??throw new ArgumentException("视频不存在");
// 编辑器轮询这个接口：转录版本变化说明有新字幕，自动方案更新时间变化说明预勾选结果可用。
app.MapGet("/api/studio/clips/{id}/transcript-state",(string id)=>
{
    var clip=AutoClip(id);var report=autoEdit.Report(id);
    var effective=EffectiveTranscript.FromProjectJson(studioStore.Project(id),id,store.TranscriptJson(clip.EventId));
    return Results.Ok(new {clipId=id,transcriptVersion=store.TranscriptVersion(clip.EventId),
        transcriptSource=effective.Source,transcriptDigest=effective.Digest,transcriptCacheKey=effective.CacheKey,
        autoStatus=report?.Status,autoUpdatedAt=report?.UpdatedAt,autoKeepCount=report?.KeepCount??0,autoTrimmedSeconds=report?.TrimmedSeconds??0});
});
app.MapGet("/api/studio/clips/{id}/auto-plan",(string id)=>{AutoClip(id);var report=autoEdit.Report(id);return report is null?Results.NotFound(new {error="尚未生成自动方案"}):Results.Ok(report);});
// 遗留:直接写入;新 UI 用 proposal/apply。
app.MapPost("/api/studio/clips/{id}/auto-plan",async (string id,CancellationToken token)=>Results.Ok(await autoEdit.RunAsync(AutoClip(id),token)));
AutoPlanEndpoints.Map(app,store,studioStore,autoEdit);
// 自动剪气口：识别静音并从当前时间线里裁掉。
app.MapPost("/api/studio/clips/{id}/trim-silence",async (string id,[FromBody] TrimSilenceRequest body,CancellationToken token)=>
{
    var clip=AutoClip(id);
    var items=body.Timeline is {Length:>0}?body.Timeline:throw new ArgumentException("时间线为空，请先生成片段");
    if(items.Any(x=>x.Kind=="video"&&x.ClipId!=id))throw new ArgumentException("多源时间线需要逐来源规划，不能套用单源静音结果");
    var project=body.Project is {} json?EditorProjectCodec.Deserialize(json):(studioStore.Project(id) is {} saved?EditorProjectCodec.Deserialize(saved):new EditorProject());
    var transcript=EffectiveTranscript.Resolve(project,id,store.TranscriptJson(clip.EventId));
    var measured=await silenceTrim.PlanAsync(id,clip.Path,body.NoiseDb??SilenceCutPlanner.NoiseDb,body.MinSeconds??SilenceCutPlanner.MinSeconds,transcript.Segments,project.Fps,token);
    var candidates=measured.Silences;
    var after=SilenceTrimService.Trim(items,SilenceCutPlanner.AutomaticCuts(candidates),0);
    return Results.Ok(new {timeline=after,candidates,transcriptCacheKey=transcript.CacheKey,silenceCount=candidates.Length,evidenceProvider=measured.EvidenceProvider,warnings=measured.Warnings,trimmedSeconds=Math.Round(SilenceTrimService.TrimmedSeconds(items,after),2)});
});
// 编辑器在等待静音检测时轮询这个接口，把检测进度显示成百分比。
app.MapGet("/api/studio/clips/{id}/trim-silence-progress",(string id)=>Results.Ok(new {progress=Math.Round(silenceTrim.ProgressOf(id),3)}));
app.MapGet("/api/health",()=>Results.Ok(new {ok=true,app="LiveClipper",dataRoot=configDirectory,factoryResetId,resetting=resetRequested,startupWarnings}));
string? InstalledRoot()
{
    var root=Environment.GetEnvironmentVariable("LIVECLIPPER_INSTALL_ROOT");if(string.IsNullOrWhiteSpace(root))return null;
    try{using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"installation.json")));return manifest.RootElement.GetProperty("AppId").GetString()=="LiveClipper.UserInstall.v1"&&string.Equals(Path.GetFullPath(root),manifest.RootElement.GetProperty("Root").GetString(),StringComparison.OrdinalIgnoreCase)&&File.Exists(Path.Combine(root,"Uninstall.exe"))?root:null;}catch{return null;}
}
app.MapGet("/api/installation",()=>Results.Ok(new {installed=InstalledRoot() is not null,path=InstalledRoot()}));
app.MapPost("/api/installation/uninstall",()=>
{
    var root=InstalledRoot();if(root is null)return Results.BadRequest(new {error="此版本没有安装卸载入口"});
    Process.Start(new ProcessStartInfo(Path.Combine(root,"Uninstall.exe")){WorkingDirectory=root,UseShellExecute=true});
    return Results.Ok(new {opened=true});
});
CacheCleanupEndpoints.Map(app,store);
MusicEndpoints.Map(app,musicGeneration,settings,configPath,store,studioStore,coordinator.Media);
StudioEndpoints.Map(app,store,studioStore,studioMedia,aiAnalysis,audioLibrary,demucs,editorRenderer,coordinator.Media,candidatePipeline,settings);
AdvancedStudioEndpoints.Map(app,store,studioStore,editorRenderer,audioLibrary,coordinator.Media,settings,studioExports);
LiveClipper.App.LiveEndpoints.Map(app,liveManager,liveStorage,liveRegistry,livePreview,liveLookup,null,liveRecovery,liveDiagnostics,store,coordinator,canRecord:()=>!string.IsNullOrWhiteSpace(settings.RecordingDirectory)&&RenderStoragePaths.IsValidDirectory(settings.RecordingDirectory));
LiveClipper.App.BridgeEndpoints.Map(app,bridgeJobs,bridgeFcproj);
PerceptionEndpoints.Map(app,store,coordinator.Media);
WhisperModelEndpoints.Map(app,settings);
OllamaManagementEndpoints.Map(app,settings,path=>UpdateSettingsDurably(()=>settings.OllamaRuntimeBaseDirectory=path,indentedSettingsJson));
app.MapPost("/api/shutdown",()=>
{
    _=Task.Run(async()=>{await Task.Delay(100);await app.StopAsync();});
    return Results.Ok(new {stopping=true});
});
app.MapGet("/api/diagnostics/danmaku/{roomId:long}",async (long roomId,CancellationToken token)=>
{
    var server=await new BilibiliLiveSource().GetDanmakuServerAsync(roomId,token);
    return Results.Ok(new {server.RoomId,server.Host,server.Port,tokenAvailable=!string.IsNullOrEmpty(server.Token)});
});
// ===== 单页应用回退（A0）：发布版 wwwroot 放 React 构建产物，直接访问 /live、/studio、/music、/editor
// 时回退到 index.html；未注册的 /api/* 保持 404 JSON，避免前端把一份 HTML 当成接口成功响应。
app.Map("/api/{**rest}",(HttpContext context)=>Results.NotFound(new {error=$"未找到接口：{context.Request.Path}"}));
// 注意：MapFallbackToFile 的默认文件提供器是**内容根**（src/LiveClipper.App/wwwroot），
// 而不是 UseStaticFiles 用的 WebRoot。用 --webroot 指向 web/dist 做隔离测试时，静态资源走 dist、
// SPA 回退仍吐 src 下的旧副本，页面会因为引用不存在的旧 bundle 而整页空白（2026-11-05 实测到）。
// 这里改成直接读 WebRootPath 下的 index.html，路径来源唯一，不再依赖 provider 的默认值。
var spaEnvironment=(IWebHostEnvironment)app.Services.GetRequiredService(typeof(IWebHostEnvironment));
app.MapFallback(async (HttpContext context) =>
{
    var spaIndex=Path.Combine(spaEnvironment.WebRootPath??string.Empty,"index.html");
    if(!File.Exists(spaIndex)) return Results.NotFound(new {error="index.html not found under webroot: "+spaEnvironment.WebRootPath});
    context.Response.Headers.CacheControl="no-cache, must-revalidate";
    return Results.File(spaIndex,"text/html; charset=utf-8");
});
app.Lifetime.ApplicationStopping.Register(()=>
{
    liveManager.StopAllAsync().GetAwaiter().GetResult();
    coordinator.ShutdownAsync().GetAwaiter().GetResult();
    videoExporter.StopAsync().GetAwaiter().GetResult();
    demucs.StopAsync().GetAwaiter().GetResult();
    editorRenderer.StopAsync().GetAwaiter().GetResult();
    musicGeneration.StopAsync().GetAwaiter().GetResult();
    studioExports.StopAsync().GetAwaiter().GetResult();
    bridgeJobs.Dispose();
    RunExitCleanup();
});
// 关闭软件后的自动清理：回放缓存文件、弹幕记录、直播间与 UID 数据全部清掉，下次启动是干净状态；
// 已保存的高光 MP4、切片、转写结果与导出成片不受影响。可在设置里关闭。
void RunExitCleanup()
{
    if(!settings.CleanupOnExit)return;
    try
    {
        var report=new SessionCleanupService(store).RunHighlightCachesOnly();
        UpdateSettingsDurably(()=>
        {
            settings.BilibiliUid=0;settings.RoomId=0;settings.RoomIds.Clear();
            // 运行期 Cookie 是明文，落盘前必须重新加密，避免设置文件出现明文。
            settings.BilibiliCookie=CookieProtector.Protect(settings.BilibiliCookie);
        },indentedSettingsJson,persist:settingsWritable);
        Log($"退出清理完成：缓存文件 {report.BufferFiles} 个（{report.BufferMegabytes} MB），弹幕 {report.Danmaku} 条，直播间 {report.Rooms} 个，场次 {report.Sessions} 个，分片索引 {report.Segments} 条{(string.IsNullOrWhiteSpace(report.Warning)?"":"；警告："+report.Warning)}");
    }
    catch(Exception ex){Log("退出清理失败："+ex.Message);}
    void Log(string message)
    {
        try{File.AppendAllText(Path.Combine(configDirectory,"cleanup.log"),$"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");}catch(Exception){}
    }
}
_ = RunLiveRecoveryAsync();
_ = RunLiveStorageLoopAsync();
await app.RunAsync();
async Task RunLiveRecoveryAsync()
{
    void LogAttempt(LiveRecoveryAttempt attempt)
    {
        if(attempt.Succeeded)
            Console.WriteLine($"live room {attempt.RoomId} restored (attempt {attempt.Failures+1})");
        else
            Console.WriteLine($"live room {attempt.RoomId} restore failed: {attempt.Error}; retry in {attempt.NextDelay?.TotalSeconds:0.#}s (failures={attempt.Failures})");
    }
    try
    {
        var first=await liveRecovery.RunOnceAsync(async(roomId,token)=>await liveManager.StartAsync(roomId,token,
            !string.IsNullOrWhiteSpace(settings.RecordingDirectory)&&(await liveRegistry.ListAsync(token)).Any(r=>r.RoomId==roomId&&r.AutoRecord)),app.Lifetime.ApplicationStopping);
        foreach(var attempt in first.Attempts) LogAttempt(attempt);
        if(first.Attempts.Count==0) Console.WriteLine("live recovery: no active rooms to restore");
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { return; }
    catch (Exception ex) { Console.WriteLine("live recovery initial run failed: "+ex.Message); }

    using var timer=new PeriodicTimer(LiveRoomRecoveryService.LoopInterval);
    try
    {
        while(await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
        {
            try
            {
                var run=await liveRecovery.RunOnceAsync(async(roomId,token)=>await liveManager.StartAsync(roomId,token,
            !string.IsNullOrWhiteSpace(settings.RecordingDirectory)&&(await liveRegistry.ListAsync(token)).Any(r=>r.RoomId==roomId&&r.AutoRecord)),app.Lifetime.ApplicationStopping);
                foreach(var attempt in run.Attempts) LogAttempt(attempt);
                if(run.Failed>0) Console.WriteLine($"live recovery: attempted={run.Attempted}, succeeded={run.Succeeded}, failed={run.Failed}, skipped={run.Skipped}");
                foreach(var warning in run.Warnings) Console.WriteLine("live recovery warning: "+warning);
            }
            catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.WriteLine("live recovery run failed: "+ex.Message); }
        }
    }
    catch (OperationCanceledException) { }
}
async Task RunLiveStorageLoopAsync()
{
    try { await Task.Delay(TimeSpan.FromSeconds(30),app.Lifetime.ApplicationStopping); }
    catch (OperationCanceledException) { return; }
    using var timer=new PeriodicTimer(TimeSpan.FromMinutes(5));
    try
    {
        while(await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
        {
            try
            {
                var report=await liveStorage.EnforceAsync(app.Lifetime.ApplicationStopping);
                if(report.DeletedSessions>0)
                    Console.WriteLine($"live storage cleanup: removed {report.DeletedSessions} sessions / {report.DeletedBytes} bytes, total={report.TotalBytes}, free={report.FreeBytes}");
            }
            catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.WriteLine("live storage cleanup failed: "+ex.Message); }
        }
    }
    catch (OperationCanceledException) { }
}
if(resetRequested)
{
    try
    {
        factoryReset.Execute(studioStore.LocalVideos().Select(x=>x.Path));
        if(desktopHost)
        {
            // 进程内宿主没有独立服务进程可重启；由 WPF 窗口监视这个标记，
            // 关闭旧窗口后重新拉起 Desktop.exe，避免在 API 进程里再开一个窗口。
            Program.PublishFactoryResetMarker();
        }
        else
        {
            var launch=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppContext.BaseDirectory};
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))launch.ArgumentList.Add(typeof(FactoryResetRequest).Assembly.Location);
            launch.ArgumentList.Add("--factory-reset-parent");launch.ArgumentList.Add(Environment.ProcessId.ToString());launch.ArgumentList.Add("--factory-reset-id");launch.ArgumentList.Add(factoryResetId!);
            Process.Start(launch)?.Dispose();
        }
    }
    catch(Exception ex){File.WriteAllText(Path.Combine(configDirectory,"factory-reset-error.txt"),"恢复出厂设置未完成："+ex.Message);}
}

public sealed record StartRequest(long RoomId,long? Uid=null,bool AutoRecord=false,bool CacheEnabled=true);
public sealed record RecordingModeRequest(bool Enabled);
public sealed record MusicGenerationSettingsRequest(string? Provider=null,string? GenerationUrl=null,string? Model=null,string? AuthMode=null,string? ApiKey=null,bool? ClearApiKey=null);
public sealed record AiSettingsRequest(string? Provider=null,string? Endpoint=null,string? Model=null,string? ApiKey=null,bool? ClearApiKey=null);
public sealed record AutoClipPolicyRequest(bool? Enabled=null,double? CooldownSeconds=null,double? BaselineSeconds=null,double? ComparisonSeconds=null,double? TriggerRatio=null,double? ReleaseRatio=null,double? MaxExtensionSeconds=null,int? PreRollSeconds=null,int? PostRollSeconds=null,int? ManualPreRollSeconds=null,int? ManualPostRollSeconds=null,int? DanmakuRecordLimit=null,double? ReleaseSeconds=null,double? RebaseSeconds=null);
public sealed record CookieDetectRequest(bool Apply=true);
public sealed record CookieImportRequest(string Cookie,string? Source=null);
public sealed record TrimSilenceRequest(TimelineItem[]? Timeline,double? NoiseDb=null,double? MinSeconds=null,double? Padding=null,JsonElement? Project=null);
public sealed record VideoExportOptions(int BitrateKbps,int Resolution,int Fps);
public sealed record FactoryResetRequest(string Token,string Confirmation);

// Exposed so the desktop host can start the existing API in-process for P0.5.2.
public partial class Program
{
    // 5179 落在 Windows 的 TCP 保留段 5114–5213 里（`netsh interface ipv4 show excludedportrange`
    // 可复现），在那类机器上双击图标会直接绑不上、启动即失败。默认端口移到保留段外，
    // 再用 ChoosePort 兜住"这个端口也被别的进程占了"的情况。
    public const int DefaultPort=5255;
    /// <summary>LIVECLIPPER_PORT 的统一校验：仅接受 1024–65535，越界/非数字回退默认值（服务端与桌面壳共用）。</summary>
    public static int ResolvePort(string? raw,Action<string>? log=null)
    {
        if(string.IsNullOrWhiteSpace(raw))return DefaultPort;
        if(int.TryParse(raw,out var port)&&port is >=1024 and <=65535)return port;
        log?.Invoke($"LIVECLIPPER_PORT=\"{raw}\" 不在 1024–65535 范围内，已回退默认端口 {DefaultPort}");
        return DefaultPort;
    }
    /// <summary>
    /// 首选端口若被**别的进程**占着，就顺延到下一个能绑的端口，保证"装完就能起"。
    /// 唯一的例外是那个端口被本产品自己的实例占着（ServerSingletonExists）——那种情况必须保持原端口，
    /// 交给 Server 互斥量与激活事件去"把已有窗口叫到前台"，否则第二实例会另起一个服务。
    /// </summary>
    public static int ChoosePort(int preferred,Action<string>? log=null)
    {
        if(CanBindLoopback(preferred)||ServerSingletonExists(preferred))return preferred;
        for(var candidate=preferred+1;candidate<=preferred+20&&candidate<=65535;candidate++)
        {
            if(!CanBindLoopback(candidate))continue;
            log?.Invoke($"端口 {preferred} 被占用，改用 {candidate}");
            return candidate;
        }
        log?.Invoke($"端口 {preferred} 及后续 20 个端口都不可用，仍使用 {preferred}");
        return preferred;
    }
    /// <summary>试绑一次回环端口立即释放：只用来"能不能起"，不做占用。</summary>
    private static bool CanBindLoopback(int port)
    {
        TcpListener? listener=null;
        try
        {
            listener=new TcpListener(IPAddress.Loopback,port);
            listener.Start();
            return true;
        }
        catch(OutOfMemoryException){throw;}
        catch(Exception){return false;}
        finally
        {
            try{listener?.Stop();}catch{}
        }
    }
    /// <summary>第一个实例用它监听“请激活窗口”信号；第二个实例通过同名事件唤醒它。</summary>
    public static string ActivationEventName(int port)=>$@"Local\LiveClipper.Activate.{port}";
    /// <summary>第一个实例正常退出时置位；等待线程据此醒来结束，不留悬挂后台线程。</summary>
    public static string ActivationReleaseEventName(int port)=>$@"Local\LiveClipper.Activate.Release.{port}";
    public const string ActivationEventEnvironmentVariable="LIVECLIPPER_ACTIVATE_EVENT";
    /// <summary>
    /// 在 Main 里、宿主构建之前就把激活事件建好（AutoReset，二值命名事件）。
    /// 只在 Windows 上创建；失败只记日志并返回 false，调用方据此退回“窗口内建监听”。
    /// 当前进程的激活句柄会缓存到静态字段，供第三方反射探针直接读取（零副作用），等待线程仍然由 Main 起。
    /// </summary>
    public static bool TryCreateActivationChannel(int port,out EventWaitHandle? signal,out EventWaitHandle? release,out bool fresh)
    {
        signal=null;release=null;fresh=false;
        if(!OperatingSystem.IsWindows())return false;
        try
        {
            signal=new EventWaitHandle(false,EventResetMode.AutoReset,ActivationEventName(port),out var createdNew);
            fresh=createdNew;
            release=new EventWaitHandle(false,EventResetMode.AutoReset,ActivationReleaseEventName(port),out _);
            Volatile.Write(ref _activationSignalHandleCached,signal);
            Volatile.Write(ref _activationReleaseHandleCached,release);
            Environment.SetEnvironmentVariable(ActivationEventEnvironmentVariable,ActivationEventName(port));
            return true;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine("[activate] 创建激活事件失败："+ex.Message);
            try{signal?.Dispose();}catch{}
            try{release?.Dispose();}catch{}
            signal=null;release=null;
            return false;
        }
    }
    /// <summary>本进程持有的“请激活”信号（由 TryCreateActivationChannel 建立，Main 负责释放）。窗口程序集在 UI 线程读它，标 volatile 防止读到旧值。</summary>
    private static EventWaitHandle? _activationSignalHandleCached;
    public static EventWaitHandle? CurrentActivationSignal=>Volatile.Read(ref _activationSignalHandleCached);
    private static EventWaitHandle? _activationReleaseHandleCached;
    /// <summary>本进程持有的激活退出闸门。</summary>
    public static EventWaitHandle? CurrentActivationRelease=>Volatile.Read(ref _activationReleaseHandleCached);
    /// <summary>
    /// 收到的激活次数（只在等待线程里 +1，供窗口消费时清零）。窗口还没创建时，这里就是“pending 标记”。
    /// </summary>
    private static int _activationPending;   // 只在 ActivationSinkGate 锁内读写（等待线程 / TakeActivationPending / IncrementActivationPending）
    private static Action? _activationSink;
    private static readonly object ActivationSinkGate=new();
    /// <summary>
    /// 后台等待线程：AutoReset 事件每被 Set 一次就 WaitOne 返回一次。
    /// IsBackground=true，绝不会阻止进程退出；收到 release（进程退出闸门）或被 Dispose 即干净结束。
    /// 有窗口订阅时把信号交给订阅方（它负责投递到 UI 线程），没有订阅方时只累计 pending。
    /// </summary>
    public static Thread StartActivationPump(EventWaitHandle signal,Action? sink,EventWaitHandle? release)
    {
        lock(ActivationSinkGate)_activationSink=sink;
        var waitHandles=release is null?new WaitHandle[]{signal}:new WaitHandle[]{signal,release};
        var thread=new Thread(()=>
        {
            try
            {
                while(true)
                {
                    // 0 号是信号，1 号是退出闸门；WaitAny 返回 1 表示进程要走了。
                    if(WaitHandle.WaitAny(waitHandles)==1)break;
                    TracePump("pump-wake");
                    // 读 sink、判定、计数必须在同一个 lock 内完成：否则「读到 sink=null → 出锁 → 计数」之间窗口可能刚
                    // Attach 并 TakeActivationPending，这一次激活既没投递也没被 pending 记到，就永久搁浅。
                    Action? target;
                    lock(ActivationSinkGate)
                    {
                        target=_activationSink;
                        if(target is null){_activationPending++;TracePump("pump-pending++");continue;} // 窗口还没订阅：记 pending
                    }
                    try{target();}catch(Exception ex){Console.Error.WriteLine("[activate] 唤醒窗口失败："+ex.Message);}
                }
            }
            catch(Exception ex)
            {
                // 句柄被 Dispose / 进程在退出中：都是正常收敛路径。
                Console.Error.WriteLine("[activate] 等待线程结束："+ex.Message);
            }
        }){IsBackground=true,Name="LiveClipper.ActivationPump"};
        thread.Start();
        CurrentActivationPump=thread;
        return thread;
    }
    /// <summary>让窗口把自己挂到等待线程上；重复调用会替换（窗口重建时用）。</summary>
    public static void SetActivationSink(Action? sink)
    {
        lock(ActivationSinkGate)_activationSink=sink;
    }
    /// <summary>取走并清零 pending 计数（窗口 OnLoaded 时消费一次）。</summary>
    public static int TakeActivationPending()
    {
        // 与等待线程「读 sink + 计数」共用同一把锁：Take 期间到达的信号要么先被计数（随后被本次 Take 取走），
        // 要么看到 sink 已挂上而直接投递，不会两边都漏。
        lock(ActivationSinkGate)return _activationPending=0;
    }
    /// <summary>把一次未投递成功的激活补回 pending（窗口在极端时序下收到信号但还没 Loaded 时用）。</summary>
    public static void IncrementActivationPending()
    {
        lock(ActivationSinkGate){_activationPending++;TracePump("pending++ (restored by window)");}
    }
    /// <summary>等待线程的埋点（LIVECLIPPER_ACTIVATION_TRACE=1 才写 stderr）。</summary>
    private static void TracePump(string message)
    {
        if(Environment.GetEnvironmentVariable("LIVECLIPPER_ACTIVATION_TRACE")!="1")return;
        try{Console.Error.WriteLine($"[pump] {message} t={Environment.TickCount64}");}catch{}
    }
    /// <summary>当前等待线程（Main 启动，进程生命周期内唯一）。</summary>
    private static Thread? _activationPumpCached;
    public static Thread? CurrentActivationPump{get=>Volatile.Read(ref _activationPumpCached);private set=>Volatile.Write(ref _activationPumpCached,value);}
    /// <summary>进程退出时让等待线程收敛：置 release 事件并给它一小段时间自己退出。</summary>
    public static void StopActivationPump(EventWaitHandle? release)
    {
        try{release?.Set();}catch{}
        try{CurrentActivationPump?.Join(TimeSpan.FromSeconds(2));}catch{}
    }
    /// <summary>
    /// 单实例判定：<c>LiveClipper.Activate.&lt;port&gt;</c> 这个命名事件由第一个实例在启动早期创建，
    /// 所以「能 Open 到它」就等于「已经有一个实例建好通道、正在等激活信号」。
    /// 判定为第二实例时先发一次激活信号（把已有窗口带到前台），再按 exitOnHit 决定是 return 让调用方
    /// 退出，还是只返回 true。这里不做任何文件操作——重置标记的删除只属于确认自己是第一实例的那条路径。
    /// 竞态边界照实说：P1 起后约 300ms 内的 P2 探空时，P1 可能还没建好事件，本函数返回 false，
    /// P2 会继续走到窗口创建（闪一次窗）；但那时 P2 仍不会删任何标记，标记只由真正的第一实例清。
    /// </summary>
    /// <param name="port">服务端口，决定事件名。</param>
    /// <param name="quiet">true 时不写追踪日志。</param>
    /// <param name="exitOnHit">true 时判定为第二实例就直接 Environment.Exit(0)。</param>
    /// <returns>true 表示已经存在活跃实例。</returns>
    public static bool DetectExistingInstance(int port,bool quiet,bool exitOnHit)
    {
        if(!OperatingSystem.IsWindows())return false;
        EventWaitHandle? existing=null;
        try
        {
            if(!EventWaitHandle.TryOpenExisting(ActivationEventName(port),out existing)||existing is null)return false;
            if(!quiet)TraceFactoryReset("existing instance detected: "+ActivationEventName(port));
            existing.Set();
        }
        catch(Exception ex){if(!quiet)TraceFactoryReset("existing instance probe failed: "+ex.GetType().Name);return false;}
        finally{try{existing?.Dispose();}catch{}}
        if(exitOnHit)Environment.Exit(0);
        return true;
    }
    /// <summary>
    /// 只读探测"这个端口上是否已经有服务实例在跑"。
    /// 用的是与 Main 里 serverMutex 完全同名的用户会话级互斥量：P1 从拿到它开始一直持有到进程退出，
    /// 所以 P2 只要能打开到它，就说明已经有一个实例（无论它的激活事件建没建好）。
    /// 与 Mutex(true,...) 的区别是这里不持有、不改变语义，探完立刻释放句柄。
    /// 非 Windows / 探测异常一律返回 false（宁可多删一次残留标记，也不能把真标记挡住）。
    /// </summary>
    public static bool ServerSingletonExists(int port)
    {
        if(!OperatingSystem.IsWindows())return false;
        try
        {
            if(!Mutex.TryOpenExisting($@"Local\LiveClipper.Server.{port}",out var existing)||existing is null)return false;
            existing.Dispose();
            return true;
        }
        catch{return false;}
    }
    private static string FactoryResetMarkerDirectory=>FactoryResetMarker.PrivateDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    /// <summary>进程内 API 与 Desktop 共用运行身份，不接受其他运行留下的重置标记。</summary>
    public static string FactoryResetMarkerPath=>FactoryResetMarker.MarkerPath(FactoryResetMarkerDirectory,FactoryResetMarker.Current);
    public static void PublishFactoryResetMarker()=>FactoryResetMarker.Publish(FactoryResetMarkerDirectory,FactoryResetMarker.Current,DateTimeOffset.UtcNow);
    public static bool IsFactoryResetMarkerForCurrentProcess()
    {
        try{return FactoryResetMarker.ReadCurrent(FactoryResetMarkerDirectory,FactoryResetMarker.Current,DateTimeOffset.UtcNow,FactoryResetMarker.IsProcessAlive);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {TraceFactoryReset("read marker failed: "+ex.GetType().Name);return false;}
    }
    public static bool DeleteFactoryResetMarkerForCurrentProcess()
    {
        try{return FactoryResetMarker.DeleteCurrent(FactoryResetMarkerDirectory,FactoryResetMarker.Current);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {TraceFactoryReset("delete marker failed: "+ex.GetType().Name);return false;}
    }
    /// <summary>只清理私有目录内的无效/过期/死进程及本 PID 旧运行标记，保留其他活动实例。</summary>
    public static bool ClearFactoryResetMarker()
    {
        try{return FactoryResetMarker.ClearStale(FactoryResetMarkerDirectory,FactoryResetMarker.Current,DateTimeOffset.UtcNow,FactoryResetMarker.IsProcessAlive);}
        catch(Exception ex){TraceFactoryReset("clear marker failed: "+ex.GetType().Name);return false;}
    }
    private static void TraceFactoryReset(string message)
    {
        if(Environment.GetEnvironmentVariable("LIVECLIPPER_ACTIVATION_TRACE")!="1")return;
        try{Console.Error.WriteLine("[reset] "+message);}catch{}
    }
    public static void SignalFirstInstance(int port)
    {
        if(Environment.GetEnvironmentVariable("LIVECLIPPER_ACTIVATION_TRACE")=="1")
        {
            try{Console.Error.WriteLine($"[second] signaling {ActivationEventName(port)} attempt=1 t={Environment.TickCount64}");}catch{}
        }
        if(!OperatingSystem.IsWindows())return;
        try
        {
            if(!EventWaitHandle.TryOpenExisting(ActivationEventName(port),out var handle))
            {
                TraceSignal("signal-miss: activation event not found");
                return;
            }
            using(handle)
            {
                // 本进程刚被用户启动、持有前台权限；放行给第一个实例，否则它的 Activate() 只会闪任务栏。
                try{AllowSetForegroundWindow(-1);}catch{}
                handle.Set();
            }
            TraceSignal("signal-sent");
        }
        catch(Exception exception){TraceSignal("signal-error: "+exception.GetType().Name);}
    }
    /// <summary>LIVECLIPPER_ACTIVATION_TRACE=1 时把第二实例的发信号结果写进 stderr（默认静默）。</summary>
    private static void TraceSignal(string message)
    {
        if(Environment.GetEnvironmentVariable("LIVECLIPPER_ACTIVATION_TRACE")!="1")return;
        try{Console.Error.WriteLine($"[second] {message} t={Environment.TickCount64}");}catch{}
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}

// Kept separate from application startup so the actual read/export delegates can be regression tested.
public static class TranscriptReadEndpoints
{
    public static void Map(WebApplication app,Store store,StudioStore studio)
    {
        app.MapGet("/api/clips/{id}/srt",(string id)=>
        {
            var snapshot=Read(store,studio,id);
            return snapshot is null?Results.NotFound(new {error="字幕文件不存在"}):Srt(snapshot,"text/plain; charset=utf-8");
        });
        app.MapGet("/api/clips/{id}/transcript",(string id)=>
        {
            var snapshot=Read(store,studio,id);
            return snapshot is null||!snapshot.Available?Results.NotFound(new {error="转录文字尚未生成"})
                :Results.Ok(TranscriptExport.Parse(snapshot.Transcript.CanonicalJson));
        });
        app.MapGet("/api/clips/{id}/transcript/export",(string id,string format)=>
        {
            var snapshot=Read(store,studio,id);
            if(snapshot is null)return Results.NotFound(new {error="切片不存在"});
            if(format=="srt")return Srt(snapshot,"application/x-subrip");
            if(format is not ("txt" or "docx"))return Results.BadRequest(new {error="仅支持 DOCX、TXT、SRT"});
            if(!snapshot.Available)return Results.NotFound(new {error="转录文字尚未生成"});
            var preview=TranscriptExport.Parse(snapshot.Transcript.CanonicalJson);
            return format=="txt"
                ?Results.File(System.Text.Encoding.UTF8.GetBytes(preview.Text),"text/plain; charset=utf-8",$"{snapshot.Clip.EventId}.txt")
                :Results.File(TranscriptExport.ToDocx(preview),"application/vnd.openxmlformats-officedocument.wordprocessingml.document",$"{snapshot.Clip.EventId}.docx");
        });
    }

    private sealed record ReadSnapshot(Clip Clip,EffectiveTranscriptResult Transcript,bool Available);
    private static ReadSnapshot? Read(Store store,StudioStore studio,string id)
    {
        var clip=store.Clips().FirstOrDefault(x=>x.Id==id);
        if(clip is null)return null;
        var original=store.TranscriptJson(clip.EventId);
        var transcript=EffectiveTranscript.FromProjectJson(studio.Project(id),id,original);
        // An explicit empty override is a valid empty export, even when no ASR/file exists.
        return new(clip,transcript,transcript.Source=="override"||original is not null);
    }

    private static IResult Srt(ReadSnapshot snapshot,string contentType)
    {
        if(snapshot.Available)return Results.File(System.Text.Encoding.UTF8.GetBytes(SubtitleTools.ToSrt(snapshot.Transcript.Segments)),contentType,$"{snapshot.Clip.EventId}.srt");
        var path=snapshot.Clip.SrtPath;
        return path is null||!File.Exists(path)?Results.NotFound(new {error="字幕文件不存在"})
            :Results.File(path,contentType,$"{snapshot.Clip.EventId}.srt");
    }
}

public static class AutoPlanEndpoints
{
    // Missing constructor arguments (especially baseRevision=0) must not silently become a valid baseline.
    private static readonly JsonSerializerOptions ProposalJson=new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters=true,
        Converters={new JsonStringEnumConverter()}
    };

    public static void Map(WebApplication app,Store store,StudioStore studio,AutoEditService autoEdit)
    {
        Clip? ClipById(string id)=>store.Clips().FirstOrDefault(x=>x.Id==id);
        int Revision(string id)=>checked((int)studio.GetProjectRevision(id));
        IResult Invalid(string id,string message)=>Error("invalid-proposal",message,Revision(id),StatusCodes.Status400BadRequest);
        IResult Execute(string id,Func<IResult> action)
        {
            try{return action();}
            catch(StorageSchemaTooNewException ex)
            {
                return Results.Conflict(new {error=new {code=ex.Code,message=ex.Message,schemaVersion=ex.SchemaVersion,supportedVersion=ex.SupportedVersion},code=StudioErrorCodes.StorageSchemaTooNew});
            }
            catch(EditorRevisionConflictException ex)
            {
                return Results.Conflict(new {error=new {code=ex.Code,message=ex.Message,holder=ex.Holder},revision=ex.Revision,code=StudioErrorCodes.ForConflict(ex.Code)});
            }
            catch(EditorProjectSchemaTooNewException ex)
            {
                return Results.Conflict(new {error=new {code=ex.Code,message=ex.Message,schemaVersion=ex.SchemaVersion,supportedVersion=ex.SupportedVersion},code=StudioErrorCodes.ProjectSchemaTooNew});
            }
            catch(ReplicaExtensionException ex)
            {
                return Results.Json(new {error=new {code=ex.Code,message=ex.Message,fieldPath=ex.FieldPath},code=ex.Code},statusCode:StatusCodes.Status422UnprocessableEntity);
            }
            catch(EditorProjectSchemaException ex)
            {
                return Results.Json(new {error=new {code=ex.Code,message=ex.Message},code=StudioErrorCodes.ProjectSchemaInvalid},statusCode:StatusCodes.Status422UnprocessableEntity);
            }
            catch(ArgumentException ex){return Invalid(id,ex.Message);}
        }

        app.MapPost("/api/studio/clips/{id}/auto-plan/proposal",(string id,HttpResponse response,CancellationToken token)=>
        {
            var clip=ClipById(id);
            if(clip is null)return Results.NotFound(new {error="视频不存在",code=StudioErrorCodes.NotFound});
            response.Headers["X-Editor-Epoch"]=EditorRevisionStore.EpochFor(studio);
            return Execute(id,()=>Results.Ok(autoEdit.GeneratePlan(clip,token)));
        });
        app.MapPost("/api/studio/clips/{id}/auto-plan/apply",async Task<IResult> (string id,HttpRequest request,CancellationToken token)=>
        {
            var clip=ClipById(id);
            if(clip is null)return Results.NotFound(new {error="视频不存在",code=StudioErrorCodes.NotFound});
            request.HttpContext.Response.Headers["X-Editor-Epoch"]=EditorRevisionStore.EpochFor(studio);
            if(!request.HasJsonContentType())return Invalid(id,"请求体必须是完整的 JSON proposal");
            AutoEditProposal? proposal;
            try{proposal=await JsonSerializer.DeserializeAsync<AutoEditProposal>(request.Body,ProposalJson,token);}
            catch(JsonException){return Invalid(id,"请求体不是有效的完整 proposal");}
            catch(Microsoft.AspNetCore.Http.BadHttpRequestException ex){return Invalid(id,ex.Message);}
            if(proposal is null||string.IsNullOrWhiteSpace(proposal.ProposalId)||string.IsNullOrWhiteSpace(proposal.ClipId)
                ||string.IsNullOrWhiteSpace(proposal.Epoch)||proposal.BaseRevision<0||string.IsNullOrWhiteSpace(proposal.BaseHash)
                ||string.IsNullOrWhiteSpace(proposal.TranscriptHash)||string.IsNullOrWhiteSpace(proposal.Source)
                ||proposal.Timeline is not {Length:>0}||proposal.Timeline.Any(x=>x is null)
                ||proposal.Changes is null||proposal.Changes.Any(x=>x is null)
                ||proposal.SegmentReasons is null||proposal.SegmentReasons.Any(x=>x is null)||proposal.CreatedAt==default)
                return Invalid(id,"proposal 缺少有效的基线或时间线字段");
            return Execute(id,()=>
            {
                var sessionId=request.Headers.TryGetValue("X-Session-Id",out var value)?value.ToString():null;
                var result=autoEdit.ApplyProposal(clip,proposal,sessionId);
                return result.Applied?Results.Ok(result):Error(result.Code,result.Error??"无法应用方案",result.Revision,
                    result.Conflict?StatusCodes.Status409Conflict:StatusCodes.Status400BadRequest);
            });
        });
    }

    private static IResult Error(string code,string message,int revision,int status)
    {
        var publicCode=code switch
        {
            "stale-revision" or "session-busy" or "session-mismatch"=>StudioErrorCodes.ForConflict(code),
            "stale-epoch"=>StudioErrorCodes.StaleEpoch,
            _=>code.Replace('-','_')
        };
        return Results.Json(new {error=new {code,message},revision,code=publicCode},statusCode:status);
    }
}
