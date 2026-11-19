using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace LiveClipper.Desktop;

public partial class MainWindow : Window
{
    private InProcessApiHost? _inProcess;
    private string? _inProcessError;
    private bool _closing;
    private bool _allowClose;
    private bool _starting;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Environment? _environment;
    private LocalWebHost? _webHost;
    // 关窗保存握手（GAPS #2）：关窗前先问 Web 编辑器改动写完没有，协议见 CloseHandshake.cs。
    private readonly CloseHandshake _closeHandshake=new();
    private bool _editorCloseConfirmed;
    private bool _webViewFailed;
    // 首屏 ready 合同：React 首次 location.ready 后发 lc-first-route-ready（web/src/App.tsx），
    // 壳收到才 SplashFirstRouteReady（等页面 bootstrap-ready 后发 lc-splash-exit）。Task 结果 null=ready，非 null=失败原因（进 StatusText）。
    private readonly TaskCompletionSource<string?> _firstRoute=new(TaskCreationOptions.RunContinuationsAsynchronously);
    // 与服务端同一套端口校验（1024–65535，越界回退 Program.DefaultPort=5255；5179 在 Windows 保留段内）。
    private static readonly int Port=Program.ResolvePort(Environment.GetEnvironmentVariable("LIVECLIPPER_PORT"),message=>Console.Error.WriteLine("[desktop] "+message));
    private static readonly string Url="http://127.0.0.1:"+Port;
    // 激活事件由 Program.Main 在宿主构建前创建（Program.TryCreateActivationChannel），
    // 本窗口只消费信号：不再持有/创建事件，避免出现“事件何时存在”的启动期竞态。
    private readonly ActivationInbox _activation=new();
    private int _activationHandled;   // 只有验证埋点会读它：已消费的进程内激活次数
    /// <summary>验证埋点：仅当设置 LIVECLIPPER_ACTIVATION_TRACE=1 时写 stderr（默认完全静默，不改变发行行为）。</summary>
    private static void Trace(string message)
    {
        if(Environment.GetEnvironmentVariable("LIVECLIPPER_ACTIVATION_TRACE")!="1")return;
        try{Console.Error.WriteLine($"{message} t={Environment.TickCount64}");}catch{}
    }
    private HttpClient Api => _inProcess?.Client
        ?? throw new InvalidOperationException(_inProcessError ?? "进程内 API 尚未启动");
    public MainWindow()
    {
        // 第二实例在这里就判定并静默退出，先于 InitializeComponent / 窗口显示 / OnLoaded。
        // 判定复用 Program.DetectExistingInstance（与 Program.Main 开头同一套逻辑，只保留一处实现）：
        // 能 TryOpenExisting 到 LiveClipper.Activate.<port>，说明已经有一个实例建好通道并在等激活信号。
        //
        // 为什么构造期还要再判一次（Program.Main 开头已经判过）：Program.Main 不是进程入口就执行的，
        // 它要等 OnLoaded 里 InProcessApiHost.TryStartAsync 才跑起来；而「窗口出现在屏幕上」发生在
        // base.OnStartup 创建窗口的时候，比 Program.Main 早。所以这里能早退就早退，少闪一次窗。
        //
        // 这段话必须照实写：这个探测不完备。P1 启动后 ~300ms 内的 P2 探空时，P1 可能还没建好激活事件，
        // 本函数返回 false，P2 会继续走 OnLoaded —— 窗口仍会闪一次（不消除，只如实记录）。
        // 但 P2 不会碰任何文件：私有重置目录的残留清理只发生在
        // Program.ClearFactoryResetMarker，而它的调用点在 DetectExistingInstance 判定通过之后，
        // 也就是只有确认自己是第一实例的进程才会删；P2 要么在这里退出，要么走到 Program.Main 开头的判定再退出。
        // Program.cs 的 server mutex 判定仍然保留作兜底（事件被外部程序占用、或本探测抛异常时）。
        var existingInstance=Program.DetectExistingInstance(Port,quiet:true,exitOnHit:false);
        if(existingInstance)
        {
            Trace("[desktop] second instance detected before window creation, signaling and exiting");
            // 纯桌面壳：已有实例在跑 → 不带任何窗口、不碰重置标记，直接退出
            // （与 Program.cs 的 server mutex 分支同语义；信号已由 DetectExistingInstance 发出）。
            Environment.Exit(0);
        }
        InitializeComponent();
        Browser.DefaultBackgroundColor=System.Drawing.Color.FromArgb(255,44,44,46);
        InitSplash();
        // 窗口标题带上程序集版号（与安装器、状态接口同源的 1.2.0）。
        Title=$"LiveClipper v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        Loaded+=OnLoaded;
        Closing+=OnClosing;
        Closed+=(_,_)=>{_closed.TrySetResult();StopActivationListener();};
    }
    private async void OnLoaded(object sender,RoutedEventArgs e)
    {
        _starting=true;
        try
        {
            Environment.SetEnvironmentVariable("LIVECLIPPER_DESKTOP_HOST","1");
            (_inProcess,_inProcessError)=await InProcessApiHost.TryStartAsync();
            if(_inProcess is null)
                throw new InvalidOperationException("进程内 API 启动失败："+(_inProcessError??"未知错误"));
            SplashStage(1);
            var browserData=Environment.GetEnvironmentVariable("LIVECLIPPER_WEBVIEW2_DIR")??Path.Combine(Environment.GetEnvironmentVariable("LIVECLIPPER_CONFIG_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper"),"WebView2");
            Directory.CreateDirectory(browserData);
            var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:browserData);
            _environment=environment;
            SplashStage(2);
            await Browser.EnsureCoreWebView2Async(environment);
            SplashStage(3);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled=false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled=false;
            AttachFullscreen(Browser.CoreWebView2);
            AttachExportDownloads(Browser.CoreWebView2);
            _webHost=new LocalWebHost(Browser.CoreWebView2,Api,new Uri(Url));
            _webHost.Attach();
            // 浏览器或渲染进程退出后无法应答；关窗时直接询问用户，不再等待探针。
            Browser.CoreWebView2.ProcessFailed+=(_,processArgs)=>
            {
                if(processArgs.ProcessFailedKind==CoreWebView2ProcessFailedKind.BrowserProcessExited
                    ||processArgs.ProcessFailedKind==CoreWebView2ProcessFailedKind.RenderProcessExited)
                {
                    _webViewFailed=true;
                    // 进程挂了 React 发不出 ready：结束首屏等待并走错误呈现，不把 Logo 停在第 4 阶段。
                    _firstRoute.TrySetResult("WebView 进程异常退出（"+processArgs.ProcessFailedKind+"），首屏未就绪");
                }
            };
            Browser.CoreWebView2.NavigationCompleted+=(_,navigationArgs)=>{if(navigationArgs.IsSuccess)_webViewFailed=false;};
            // Program 在激活通道就绪后清理历史残留；窗口通过同一 App 实现验证本次运行的标记。
            _=WatchFactoryResetAsync();
            var verifyOutput=Environment.GetEnvironmentVariable("LIVECLIPPER_VERIFY_OUTPUT");
            if(!string.IsNullOrWhiteSpace(verifyOutput))Browser.CoreWebView2.NavigationCompleted+=async (_,navigation)=>
            {
                JsonElement? webProbe=null;
                try { webProbe=await ProbeWebAsync(); } catch(Exception probeError) { webProbe=JsonSerializer.SerializeToElement(new {error=probeError.Message}); }
                await File.WriteAllTextAsync(verifyOutput,JsonSerializer.Serialize(new {navigation.IsSuccess,navigation.WebErrorStatus,title=Browser.CoreWebView2.DocumentTitle,browser=Browser.CoreWebView2.Environment.BrowserVersionString,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,url=Browser.Source?.ToString(),server=Url,inProcess=_inProcess is not null,inProcessError=_inProcessError,webRoot=_webHost.WebRoot,dataRoot=Environment.GetEnvironmentVariable("LIVECLIPPER_CONFIG_DIR"),icon=Icon is not null,webProbe,activation=new {pendingConsumed=_activationHandled,pumpRunning=Program.CurrentActivationPump is not null,trace=Environment.GetEnvironmentVariable("LIVECLIPPER_ACTIVATION_TRACE"),title=Title}}));
                // LIVECLIPPER_VERIFY_KEEP_OPEN=1：验证单实例激活时保留窗口，别自己关掉。
                if(Environment.GetEnvironmentVariable("LIVECLIPPER_VERIFY_KEEP_OPEN")!="1")Close();
            };
            StartActivationListener();
            // 只允许在 https://app.local 内导航；外站链接交给系统浏览器。about:/blob:/data: 等不产生跨进程导航的初始化文档一律放行。
            Browser.CoreWebView2.NavigationStarting+=(_,navigation)=>
            {
                if(!Uri.TryCreate(navigation.Uri,UriKind.Absolute,out var target))
                {
                    if(!IsNonNavigatingScheme(navigation.Uri)){navigation.Cancel=true;Console.Error.WriteLine("[desktop] 已拦截无法解析的导航");}
                    return;
                }
                if(IsAppOrigin(target)||IsNonNavigatingScheme(target))return;
                navigation.Cancel=true;
                Console.Error.WriteLine($"[desktop] 已拦截外部导航：{target.Scheme}://{target.Host}");
                // 仅用户主动点击的 http/https 链接交给系统浏览器；脚本触发的跳转与其它协议只取消不启动。
                if(navigation.IsUserInitiated&&(target.Scheme==Uri.UriSchemeHttp||target.Scheme==Uri.UriSchemeHttps))
                    try{Process.Start(new ProcessStartInfo(target.AbsoluteUri){UseShellExecute=true});}catch(Exception ex){Console.Error.WriteLine("[desktop] 打开系统浏览器失败："+ex.Message);}
            };
            // 子帧（iframe）导航走同一套 origin 策略：非 app origin 一律取消。子帧不交给系统浏览器——
            // 那等于把外站塞进嵌页面板，既不隔离也不可关闭。
            // about: / blob: / data: 一律放行：它们是渲染进程内部产生的文档（blob 源 URL、data URI 子帧等），
            // 不产生跨进程导航，上一轮只放行 about: 会连带拦掉子帧里的 blob:/data: 内容。
            Browser.CoreWebView2.FrameNavigationStarting+=(_,navigation)=>
            {
                if(!Uri.TryCreate(navigation.Uri,UriKind.Absolute,out var target))
                {
                    if(!IsNonNavigatingScheme(navigation.Uri)){navigation.Cancel=true;Console.Error.WriteLine("[desktop] 已拦截无法解析的子帧导航");}
                    return;
                }
                if(IsAppOrigin(target)||IsNonNavigatingScheme(target))return;
                navigation.Cancel=true;
                Console.Error.WriteLine($"[desktop] 已拦截外部子帧导航：{target.Scheme}://{target.Host}");
            };
            // window.open / target=_blank 一律不新建窗口：app origin 就地导航，外站只在用户主动点击时交系统浏览器。
            // 新建窗口没有本窗口的 WebMessage/导航闸门，等于绕过前面的校验，所以这里必须无条件 Handled=true。
            Browser.CoreWebView2.NewWindowRequested+=(_,args)=>
            {
                args.Handled=true;
                if(!Uri.TryCreate(args.Uri,UriKind.Absolute,out var target))
                {
                    if(string.Equals(args.Uri,"about:blank",StringComparison.OrdinalIgnoreCase))return;
                    Console.Error.WriteLine($"[desktop] 已丢弃无法解析的新窗口请求：{args.Uri}");
                    return;
                }
                if(IsAppOrigin(target))
                {
                    try{Browser.CoreWebView2.Navigate(target.AbsoluteUri);}
                    catch(Exception ex){Console.Error.WriteLine("[desktop] 新窗口请求就地导航失败："+ex.Message);}
                    return;
                }
                if(target.Scheme=="about")return;
                if(args.IsUserInitiated&&(target.Scheme==Uri.UriSchemeHttp||target.Scheme==Uri.UriSchemeHttps))
                    try{Process.Start(new ProcessStartInfo(target.AbsoluteUri){UseShellExecute=true});}catch(Exception ex){Console.Error.WriteLine("[desktop] 打开系统浏览器失败："+ex.Message);}
                else
                    Console.Error.WriteLine($"[desktop] 已丢弃非用户触发的新窗口请求：{target.Scheme}://{target.Host}");
            };
            Browser.CoreWebView2.WebMessageReceived+=async (source,args)=>
            {
                try
                {
                    // 来源必须是 https://app.local：外站页面即使被导航进来也不能触发文件选择/Cookie 导入。
                    if(!Uri.TryCreate(args.Source,UriKind.Absolute,out var origin)||!IsAppOrigin(origin))
                    {
                        Console.Error.WriteLine($"[desktop] 已忽略非 app.local 来源的 WebMessage：{args.Source}");
                        return;
                    }
                    // 关窗保存握手的 ack 先截走（迟到、过期的也吞掉），不落进下面按 requestId 分派的通用分支。
                    if(_closeHandshake.HandleMessage(args.WebMessageAsJson))return;
                    using var message=JsonDocument.Parse(args.WebMessageAsJson);
                    var root=message.RootElement;
                    // 主题通知没有 requestId，必须在下面读 requestId 之前截走。
                    if(WindowCaptionTheme.TryParse(root,out var captionTheme))
                    {
                        if(captionTheme is not null)WindowCaptionTheme.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle,captionTheme);
                        return;
                    }
                    // 首屏 ready/错误通知（web/src/App.tsx 发出，没有 requestId）：在通用分派前截走。
                    if(root.TryGetProperty("type",out var firstRouteType))
                    {
                        var firstRoute=firstRouteType.GetString();
                        if(firstRoute=="lc-first-route-ready"){_firstRoute.TrySetResult(null);return;}
                        if(firstRoute=="lc-first-route-error"){_firstRoute.TrySetResult("首屏渲染错误（React 上报 lc-first-route-error）");return;}
                    }
                    // 启动遮罩握手（web/public/startup-splash.js）：lc-splash-*，没有 requestId，同样先于通用分派截走。
                    if(TryHandleSplashMessage(root))return;
                    var type=root.GetProperty("type").GetString();
                    var requestId=root.GetProperty("requestId").GetString();
                    if(type=="reveal-export"){await HandleRevealExportAsync(root,requestId);return;}
                    if(type=="pick-files")
                    {
                        // 快捷位置（web/src/features/editor-next/desktopPickFiles.ts 只发受限枚举）：仅作选择器起始目录，
                        // 不接受网页传任意磁盘路径；识别失败/目录不存在 → 不设 InitialDirectory（系统默认位置）。
                        var initialDirectory=TryGetQuickLocationDirectory(root.TryGetProperty("location",out var locationProp)&&locationProp.ValueKind==JsonValueKind.String?locationProp.GetString():null);
                        var files=new Microsoft.Win32.OpenFileDialog{Title="选择录播或字幕文件",Multiselect=true,Filter="视频与字幕|*.mp4;*.mkv;*.mov;*.flv;*.webm;*.m4v;*.ts;*.srt;*.vtt;*.ass;*.ssa|所有文件|*.*"};
                        if(initialDirectory!=null)files.InitialDirectory=initialDirectory;
                        var paths=files.ShowDialog(this)==true?files.FileNames:Array.Empty<string>();
                        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {type="files-picked",requestId,paths}));return;
                    }
                    // 设置页的「在软件内扫码登录」：打开登录窗口，成功后把 Cookie 直接交给服务端保存。
                    if(type=="bilibili-login"){await ImportCookieFromLoginAsync(requestId);return;}
                    if(type!="pick-folder")return;
                    var role=root.GetProperty("role").GetString();
                    try
                    {
                        var title=role switch {"video"=>"选择本地录播文件夹","subtitle"=>"选择字幕文件夹","output"=>"选择成片输出文件夹","recording"=>"选择录制与高光缓存目录",_=>"选择文件夹"};
                        var picker=new Microsoft.Win32.OpenFolderDialog{Title=title,Multiselect=false};
                        var path=picker.ShowDialog(this)==true?picker.FolderName:null;
                        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {type="folder-picked",requestId,path}));
                    }
                    catch(Exception ex)
                    {
                        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {type="folder-picked",requestId,error=$"无法打开文件夹选择器：{ex.Message}"}));
                    }
                }
                catch { }
            };
            var startPath=Environment.GetEnvironmentVariable("LIVECLIPPER_START_PATH");
            SplashStage(4);
            // 首屏 ready = React 发出的 lc-first-route-ready（App.tsx 首次 location.ready；ready 的标记合同在
            // appNavigation.AppRouteCommitted / EditorNextPage，保持不变）。Logo 100%（第 5 阶段）只等这条消息。
            // NavigationCompleted 成功不再解除等待——文档加载完不等于 React 首屏已挂载；只有失败
            // （导航错误 / WebView 进程退出 / React 上报 lc-first-route-error）才提前结束等待并呈错误。
            // 消息识别在上方 WebMessageReceived 里，先于这次 Source 导航接好，不会错过 ready。
            void OnNavFailed(object? s,CoreWebView2NavigationCompletedEventArgs a)
            {
                if(a.IsSuccess)return;
                Browser.CoreWebView2.NavigationCompleted-=OnNavFailed;
                _firstRoute.TrySetResult("首页加载失败（"+a.WebErrorStatus+"）");
            }
            Browser.CoreWebView2.NavigationCompleted+=OnNavFailed;
            Browser.CoreWebView2.NavigationCompleted+=OnSplashNavigationCompleted;
            Browser.Source=new Uri(LocalWebHost.AppOrigin+(string.IsNullOrWhiteSpace(startPath)?"/index.html":startPath));
            var firstRouteFailure=await _firstRoute.Task;
            if(firstRouteFailure is not null)throw new InvalidOperationException(firstRouteFailure);
            // 首路由 ready：不再 WPF 缩放/隐藏 Browser；等页面 bootstrap-ready 后发 lc-splash-exit，complete 才撤 WPF 层。
            SplashFirstRouteReady();
            // 软件内置浏览器里登录过一次之后，以后每次启动都自动把 Cookie 同步给服务端（仅在还没配置 Cookie 时）。
            _=TrySyncBuiltInCookieAsync();
            // 窗口出现之前（Main 建好事件到本次 Loaded 之间）到达的激活信号在这里补消费一次，消费后清零。
            ConsumePendingActivation();
        }
        catch(Exception ex) {StatusText.Text="启动失败："+ex.Message;ShowSplashError("启动失败："+ex.Message);var verifyOutput=Environment.GetEnvironmentVariable("LIVECLIPPER_VERIFY_OUTPUT");if(!string.IsNullOrWhiteSpace(verifyOutput)){await File.WriteAllTextAsync(verifyOutput,JsonSerializer.Serialize(new {error=ex.ToString()}));_starting=false;Close();}}
        finally {_starting=false;}
    }
    // 顶层导航与子帧导航共用同一套 origin 判定：scheme + host + port 全部与 LocalWebHost.AppOrigin 相同才算 app origin。
    private static readonly Uri AppOriginUri=new(LocalWebHost.AppOrigin);
    private static bool IsAppOrigin(Uri uri)=>IsAppOrigin(uri,AppOriginUri);
    private static bool IsAppOrigin(Uri uri,Uri app)
        =>uri.Scheme==app.Scheme&&string.Equals(uri.Host,app.Host,StringComparison.OrdinalIgnoreCase)&&uri.Port==app.Port;
    /// <summary>
    /// about:/blob:/data: 是渲染进程内部文档（初始化文档、blob 源 URL、data URI），不产生跨进程导航，
    /// 顶层与子帧都直接放行；其它协议（http/https 外站、file:、自定义协议）仍按 origin 判定。
    /// </summary>
    private static bool IsNonNavigatingScheme(Uri uri)=>uri.Scheme is "about" or "blob" or "data";
    private static bool IsNonNavigatingScheme(string raw)
    {
        var colon=raw.IndexOf(':');
        if(colon<=0)return false;
        var scheme=raw[..colon];
        return scheme.Equals("about",StringComparison.OrdinalIgnoreCase)
            ||scheme.Equals("blob",StringComparison.OrdinalIgnoreCase)
            ||scheme.Equals("data",StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>
    /// 激活信号监听。事件与后台等待线程都由 Program.Main 在宿主构建前建立（TryCreateActivationChannel / StartActivationPump），
    /// 这里只把信箱挂到 UI 线程上，收到信号时还原+激活+临时置顶。
    /// </summary>
    private void StartActivationListener()
    {
        ActivationInbox.Attach(_activation,Dispatcher,ActivationSignaled);
        Trace($"[desktop] activation listener attached: {Program.ActivationEventName(Port)} fromProcess={_activation.IsFromProcess}");
    }
    private void StopActivationListener()=>_activation.Unbind();
    /// <summary>UI 线程回调：窗口已就绪 → 还原+激活+临时置顶。</summary>
    private void ActivationSignaled()
    {
        // 防御性分支：Attach 在当前实现里一定发生在 OnLoaded 之后（StartActivationListener 位于 OnLoaded 内），
        // 所以窗口没 Loaded 时这里根本不会被投递。万一将来 Attach 提前，这里直接把这次信号补回 pending 计数，
        // 由 OnLoaded 末尾的 ConsumePendingActivation 统一消费——不丢信号，也不留「留在 pending 里」的假注释。
        if(!IsLoaded) { Program.IncrementActivationPending(); return; }
        BringToFront();
    }
    /// <summary>OnLoaded 末尾调用：窗口出现之前到达的激活合并成一次，取走后清零，避免重复激活。</summary>
    private void ConsumePendingActivation()
    {
        if(Program.TakeActivationPending()==0)return;
        ActivatedByPeer();
    }
    /// <summary>进程内激活（第二实例发来信号）：记录一次「激活请求已到达并能定位到窗口」。</summary>
    private void ActivatedByPeer()
    {
        Interlocked.Increment(ref _activationHandled);
        BringToFront("peer-loaded");
    }
    private void BringToFront()=>BringToFront("activation");
    /// <summary>
    /// 还原+激活+临时置顶。phase 只用于验证埋点（LIVECLIPPER_ACTIVATION_TRACE=1 时写 stderr）。
    /// </summary>
    private void BringToFront(string phase)
    {
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        Show();
        Activate();
        // 兜底：前台锁定时 Activate 只会闪任务栏，临时置顶再取消可让窗口浮到最上。
        Topmost=true;Topmost=false;
        Focus();
        Trace($"[desktop] activation brought-to-front phase={phase}");
    }
    /// <summary>
    /// 激活信号信箱：等待线程（Program.StartActivationPump，Main 启动）收到“请激活”后投递到这里。
    /// 窗口还没 Loaded 时只累积 pending；OnLoaded 末尾统一消费并清零，避免重复激活。
    /// 等待线程不直接摸窗口，只调用 Post()。
    /// </summary>
    private sealed class ActivationInbox
    {
        private readonly object _gate=new();
        private Action? _handler;   // 窗口就绪后挂上；挂上之前到达的信号一律算 pending
        private Dispatcher? _dispatcher;
        private bool _fromProcess;
        /// <summary>等待线程用的只读入口（不能在等待线程里访问窗口字段）。</summary>
        private static volatile ActivationInbox? _current;
        public static ActivationInbox? Current=>_current;
        public bool IsFromProcess=>_fromProcess;
        public void Post()
        {
            lock(_gate)
            {
                var handler=_handler;
                var dispatcher=_dispatcher;
                if(handler is null||dispatcher is null)return; // 留在 Program 的 pending 计数里
                try{dispatcher.BeginInvoke(handler);}catch{}
            }
        }
        /// <summary>把窗口实例接到 UI 线程：先清掉窗口出现之前累积的 pending，再由等待线程直接投递。</summary>
        public static void Attach(ActivationInbox inbox,Dispatcher dispatcher,Action activated)
        {
            inbox._dispatcher=dispatcher;
            inbox._handler=activated;
            inbox._fromProcess=Program.CurrentActivationSignal is not null;
            Trace($"[desktop] inbox attach fromProcess={inbox._fromProcess} signalNull={Program.CurrentActivationSignal is null} pending={Program.CurrentActivationPump}");
            _current=inbox;
            // 等待线程一旦建立了窗口订阅，后续信号直接投递；之前的仍然记在 pending 里。
            Program.SetActivationSink(inbox.Post);
        }
        public void Unbind()
        {
            lock(_gate){_handler=null;_dispatcher=null;}
            if(ReferenceEquals(_current,this))Program.SetActivationSink(null);
        }
    }

    /// <summary>在软件内置浏览器里扫码登录，把拿到的 Cookie 交给服务端保存，并把结果回传给设置页。</summary>
    private async Task ImportCookieFromLoginAsync(string? requestId)
    {
        var ok=false;string? detail=null;string? cookie=null;
        try
        {
            if(_environment is null)throw new InvalidOperationException("浏览器环境尚未就绪，请稍后重试");
            var dialog=new LoginWindow(_environment){Owner=this};
            if(dialog.ShowDialog()==true&&!string.IsNullOrWhiteSpace(dialog.Cookie))
            {
                cookie=dialog.Cookie;
                (ok,detail)=await PostCookieAsync(cookie!,"软件内扫码登录");
            }
            else detail="已取消登录";
        }
        catch(Exception ex){detail="登录失败："+ex.Message;}
        PostToPage(new{type="cookie-imported",requestId,ok,detail,cookie});
    }
    /// <summary>启动时把软件内置浏览器里已有的 B 站 Cookie 自动同步给服务端（用户之前在软件里登录过才会有）。</summary>
    private async Task TrySyncBuiltInCookieAsync()
    {
        try
        {
            if(Browser.CoreWebView2 is null)return;
            using(var status=await Api.GetAsync(Url+"/api/cookies/status"))
            {
                if(status.IsSuccessStatusCode)
                {
                    using var doc=JsonDocument.Parse(await status.Content.ReadAsStringAsync());
                    if(doc.RootElement.TryGetProperty("configured",out var configured)&&configured.ValueKind==JsonValueKind.True)return;
                }
            }
            var cookie=await BilibiliCookies.ReadAsync(Browser.CoreWebView2);
            if(string.IsNullOrWhiteSpace(cookie))return;
            var (ok,detail)=await PostCookieAsync(cookie!,"内置浏览器");
            if(!ok)return;
            try{Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="cookie-imported",ok,detail,cookie,auto=true}));}catch{}
        }
        catch { }
    }
    private async Task<(bool Ok,string? Detail)> PostCookieAsync(string cookie,string source)
    {
        try
        {
            var body=JsonSerializer.Serialize(new{cookie,source});
            using var content=new StringContent(body,System.Text.Encoding.UTF8,"application/json");
            using var response=await Api.PostAsync(Url+"/api/cookies/import",content);
            var json=await response.Content.ReadAsStringAsync();
            using var doc=JsonDocument.Parse(json);
            var root=doc.RootElement;
            if(!response.IsSuccessStatusCode)
                return (false,root.TryGetProperty("error",out var error)?error.GetString():"Cookie 保存失败");
            var login=root.TryGetProperty("verify",out var verify)&&verify.TryGetProperty("login",out var flag)&&flag.ValueKind==JsonValueKind.True;
            var name=login&&verify.TryGetProperty("userName",out var user)?user.GetString():"";
            return (true,login?$"已登录：{name}":$"Cookie 已保存（{source}），但 B 站返回未登录，请确认登录是否完成");
        }
        catch(Exception ex){return (false,"Cookie 保存失败："+ex.Message);}
    }
    private void PostToPage(object payload)
    {
        try{Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));}catch{}
    }
    /// <summary>
    /// 恢复出厂设置由进程内 host 执行完后写标记；这里等旧窗口完全关闭再拉起
    /// 新的 Desktop.exe，避免旧进程的 Mutex 还没释放、新进程被判定为重复实例。
    /// </summary>
    private async Task WatchFactoryResetAsync()
    {
        // 不能把文件存在当作重置信号：App 统一校验 PID、运行 token、时间，并清掉无效/过期标记。
        while(!_closed.Task.IsCompleted)
        {
            await Task.Delay(1000);
            if(_closed.Task.IsCompleted)return;
            if(!Program.IsFactoryResetMarkerForCurrentProcess())continue;
            // 不要把重启代码放在窗口关闭之后：最后一个窗口关闭会触发 WPF
            // 退出，Dispatcher 停掉后这里的后续代码不保证还能跑。交给一个
            // 独立 helper 等旧 PID 退出后再拉起新实例。
            if(!StartRestartHelper(Environment.ProcessPath!))
            {
                StatusText.Text="恢复出厂设置已完成，但自动重启失败，请手动重新打开 LiveClipper。";
                StatusText.Visibility=Visibility.Visible;
                return;
            }
            // 验证用：LIVECLIPPER_VERIFY_KEEP_OPEN=1 时把标记留下来给外部观察（证明窗口闪了但标记没被删）。
            if(Environment.GetEnvironmentVariable("LIVECLIPPER_VERIFY_KEEP_OPEN")=="1")
            {
                try { Console.Error.WriteLine("[reset] restart scheduled, marker kept for verification: "+Program.FactoryResetMarkerPath); } catch { }
                return;
            }
            Program.DeleteFactoryResetMarkerForCurrentProcess();
            // 恢复出厂设置已经清掉了数据，关窗不再问“编辑器还有没保存的修改”：问了也没有可保存的地方。
            _editorCloseConfirmed=true;
            Close();
            return;
        }
    }
    /// <summary>
    /// 恢复出厂设置的重启 helper：等当前进程退出后，再由独立 PowerShell
    /// 拉起新实例。这样即使 WPF 在最后一个窗口关闭时停掉 Dispatcher，重启
    /// 也不会丢。
    /// </summary>
    private static bool StartRestartHelper(string executable)
    {
        var escaped=executable.Replace("'","''");
        var ack=Path.Combine(Path.GetTempPath(),"liveclipper-restart-"+Environment.ProcessId+".ok");
        try { File.Delete(ack); } catch { }
        var ackEscaped=ack.Replace("'","''");
        // helper 先写 ack，再等旧进程退出并拉起新实例。Process.Start 只证明
        // powershell.exe 被创建；ack 才能证明脚本本身真的执行了（策略/语法
        // 失败时不会留下 ack，调用方会保留窗口与错误信息）。
        var script="Set-Content -LiteralPath '"+ackEscaped+"' -Value '1'; $p=Get-Process -Id "+Environment.ProcessId+" -ErrorAction SilentlyContinue; if($p){$p.WaitForExit()}; Start-Sleep -Milliseconds 800; Start-Process -FilePath '"+escaped+"'";
        try
        {
            var process=Process.Start(new ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -WindowStyle Hidden -Command \""+script+"\"")
            {
                UseShellExecute=false,
                CreateNoWindow=true,
                WorkingDirectory=AppContext.BaseDirectory
            });
            if(process is null)return false;
            for(var i=0;i<30;i++)
            {
                if(File.Exists(ack))return true;
                Thread.Sleep(100);
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// 验证真实 WebView2 页面内的 Range/HEAD/416/SSE：这一步走 LocalWebHost 的
    /// WebResourceRequested 代理，而不是 TestServer 直连，能覆盖请求头复制、
    /// 响应头转换与流生命周期。只在设置了 LIVECLIPPER_VERIFY_OUTPUT 时运行。
    /// </summary>
    private async Task<JsonElement> ProbeWebAsync()
    {
        await Task.Delay(1200);
        if(Environment.GetEnvironmentVariable("LIVECLIPPER_PROBE_COOKIE")=="1")
        {
            try
            {
                // Never let this probe touch the real login profile. The caller must
                // point LIVECLIPPER_WEBVIEW2_DIR at an isolated UserDataFolder.
                if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIVECLIPPER_WEBVIEW2_DIR")))
                    return JsonSerializer.SerializeToElement(new{cookieProbe=new{ok=false,error="cookie probe requires LIVECLIPPER_WEBVIEW2_DIR"}});
                var core=Browser.CoreWebView2;
                var manager=core.CookieManager;
                var existing=(await manager.GetCookiesAsync("https://www.bilibili.com")).FirstOrDefault(c=>c.Name=="SESSDATA");
                var oldValue=existing?.Value;
                var oldDomain=existing?.Domain??".bilibili.com";
                var oldPath=existing?.Path??"/";
                var probeValue="probe-"+Guid.NewGuid().ToString("N");
                var read=default(string);
                var ok=false;
                try
                {
                    manager.AddOrUpdateCookie(manager.CreateCookie("SESSDATA",probeValue,oldDomain,oldPath));
                    await Task.Delay(300);
                    read=await BilibiliCookies.ReadAsync(core);
                    ok=read is not null&&read.Contains("SESSDATA="+probeValue,StringComparison.Ordinal);
                }
                finally
                {
                    foreach(var cookie in await manager.GetCookiesAsync("https://www.bilibili.com"))
                        if(cookie.Name=="SESSDATA"&&cookie.Value==probeValue)manager.DeleteCookie(cookie);
                    if(oldValue is not null)
                        manager.AddOrUpdateCookie(manager.CreateCookie("SESSDATA",oldValue,oldDomain,oldPath));
                }
                return JsonSerializer.SerializeToElement(new{cookieProbe=new{ok,read=read is null?null:"contains-probe",restored=oldValue is not null}});
            }
            catch(Exception ex)
            {
                return JsonSerializer.SerializeToElement(new{cookieProbe=new{ok=false,error=ex.Message}});
            }
        }
        const string script="""
        (async () => {
          const out = {};
          try {
            const clipsRes = await fetch('/api/clips');
            out.clipsStatus = clipsRes.status;
            const clips = await clipsRes.json();
            out.clip = clips && clips.length ? clips[0].id : null;
            if (out.clip) {
              const range = await fetch('/api/clips/' + encodeURIComponent(out.clip) + '/video', { headers: { Range: 'bytes=100-199' } });
              out.rangeStatus = range.status;
              out.rangeContentRange = range.headers.get('content-range');
              out.rangeBytes = (await range.arrayBuffer()).byteLength;
              const head = await fetch('/api/clips/' + encodeURIComponent(out.clip) + '/video', { method: 'HEAD' });
              out.headStatus = head.status;
              out.headLength = head.headers.get('content-length');
              const bad = await fetch('/api/clips/' + encodeURIComponent(out.clip) + '/video', { headers: { Range: 'bytes=999999999-' } });
              out.badStatus = bad.status;
              out.badContentRange = bad.headers.get('content-range');
            }
            const statusRes = await fetch('/api/status');
            out.statusStatus = statusRes.status;
            const status = await statusRes.json();
            const room = (status.rooms || []).find((item) => item.roomId === 1) || (status.rooms || [])[0];
            out.roomSessionId = room ? room.sessionId : null;
            if (room && room.sessionId) {
              const danmakuRes = await fetch('/api/danmaku?sessionId=' + encodeURIComponent(room.sessionId));
              out.danmakuStatus = danmakuRes.status;
              out.danmakuCount = (await danmakuRes.json()).length;
            }
          } catch (e) { out.error = String(e); }
          try {
            await new Promise((r) => setTimeout(r, 2800));
            const editorTest = Array.from(document.querySelectorAll('button')).find((el) => (el.textContent || '').includes('hanser-26-10-2026-高光时刻4'));
            if (editorTest) {
              editorTest.click();
              await new Promise((r) => setTimeout(r, 2200));
            }
            const open = Array.from(document.querySelectorAll('button')).find((b) => (b.textContent || '').includes('复刻版精修'));
            if (open) {
              open.click();
              await new Promise((r) => setTimeout(r, 2600));
              const frame = document.querySelector('iframe');
              const w = frame && frame.contentWindow;
              let seq = null, track = null, c = null, a = null;
              for (let attempt = 0; attempt < 24; attempt++) {
                seq = w && w.App && w.App.seq && w.App.seq.sequence;
                track = seq && seq.videoTracks && (seq.videoTracks.find((t) => t.id === "V1" && (t.clips || []).length) || seq.videoTracks.find((t) => (t.clips || []).some((x) => 90 >= x.start && 90 < x.end)) || seq.videoTracks.find((t) => t.clips && t.clips.length) || seq.videoTracks[0]);
                c = track && track.clips && (track.clips.find((x) => 90 >= x.start && 90 < x.end && x.assetId) || track.clips.find((x) => 90 >= x.start && 90 < x.end) || track.clips.find((x) => x.assetId) || track.clips[0]);
                a = c && w.Media && w.Media.source(c);
                if (c && c.assetId && a && a._ok) break;
                await new Promise((r) => setTimeout(r, 300));
              }
              let frameValue = null;
              try { frameValue = c && w.Media && typeof w.Media.frameFor === 'function' ? w.Media.frameFor(c, c.start, 960, 540) : null; } catch (e) { out.replicaFrameError = String(e); }
              out.replica = {
                found: !!frame,
                sync: document.body.innerText.includes('已同步'),
                clip: !!c,
                assetId: c && c.assetId,
                hasMedia: !!a,
                ok: a && a._ok,
                kind: a && a.kind,
                ready: a && a.el && a.el.readyState,
                videoWidth: a && a.el && a.el.videoWidth,
                cache: a && a.frameCache && typeof a.frameCache.size === 'number' ? a.frameCache.size : null,
                frameForType: w.Media && typeof w.Media.frameFor,
                hasFrame: !!frameValue,
                tracks: seq && seq.videoTracks ? seq.videoTracks.map((t) => ({ id: t.id, clips: t.clips ? t.clips.length : null })) : null,
                probeClip: c ? { id: c.id, assetId: c.assetId || null, start: c.start, end: c.end, track: track && track.id } : null,
                audioTracks: seq && seq.audioTracks ? seq.audioTracks.map((t) => ({ id: t.id, clips: t.clips ? t.clips.length : null })) : null,
                seqDuration: seq ? seq.duration : null,
              };
              await new Promise((r) => setTimeout(r, 2400));
              out.replica.afterCache = a && a.frameCache ? a.frameCache.size : null;
              out.replica.afterFrame = !!(c && w.Media && typeof w.Media.frameFor === 'function' && w.Media.frameFor(c, c.start, 960, 540, { cacheOnly: true }));
              out.replica.currentTime = a && a.el ? a.el.currentTime : null;
              out.replica.pendingSeek = a && a.pendingSeek ? Array.from(a.pendingSeek) : null;
              try {
                const firstKey = a && a.frameCache && a.frameCache.size ? Array.from(a.frameCache.keys())[0] : null;
                const cv = firstKey === null ? null : a.frameCache.get(firstKey);
                if (cv) {
                  const d = cv.getContext('2d').getImageData(Math.floor(cv.width / 2), Math.floor(cv.height / 2), 1, 1).data;
                  out.replica.cachedPixel = [d[0], d[1], d[2], d[3]];
                }
              } catch (e) { out.replica.cachedPixelError = String(e); }
              const pixelOf = (cv) => {
                try {
                  const d = cv.getContext('2d').getImageData(Math.floor(cv.width / 2), Math.floor(cv.height / 2), 1, 1).data;
                  return [d[0], d[1], d[2], d[3]];
                } catch (e) { return String(e); }
              };
              const canvases = Array.from(w.document.querySelectorAll('.mon-canvas'));
              out.replica.monCanvases = canvases.map(pixelOf);
              try {
                if (w.Monitor && typeof w.Monitor.invalidate === 'function') w.Monitor.invalidate();
                await new Promise((r) => setTimeout(r, 300));
                out.replica.monAfterInvalidate = Array.from(w.document.querySelectorAll('.mon-canvas')).map(pixelOf);
              } catch (e) { out.replica.monInvalidateError = String(e); }
              try {
                if (w.FCExport && typeof w.FCExport.renderSequence === 'function') {
                  const samples = [];
                  const info = await w.FCExport.renderSequence({ width: 960, height: 540, startFrame: 90, endFrame: 95 }, (canvas, frame) => {
                    const d = canvas.getContext('2d').getImageData(480, 270, 1, 1).data;
                    samples.push([frame, d[0], d[1], d[2], d[3]]);
                    if (frame === 90) {
                      const ctx = canvas.getContext('2d');
                      const img = ctx.getImageData(0, 0, canvas.width, canvas.height);
                      const colors = new Set();
                      const grid = [];
                      for (let y = 10; y < canvas.height; y += 40) {
                        for (let x = 10; x < canvas.width; x += 40) {
                          const i = (y * canvas.width + x) * 4;
                          const key = img.data[i] + ',' + img.data[i + 1] + ',' + img.data[i + 2];
                          colors.add(key);
                          if (grid.length < 12) grid.push(key);
                        }
                      }
                      out.replica.renderUnique = colors.size;
                      out.replica.renderGrid = grid;
                    }
                  });
                  out.replica.renderSamples = samples;
                  out.replica.renderInfo = info;
                  try {
                    const originalMask = c.mask;
                    const base = [];
                    const bandStats = (canvas) => {
                      const ctx = canvas.getContext('2d', { willReadFrequently: true });
                      let bright = 0, total = 0;
                      const x0 = Math.floor(canvas.width * 0.1), x1 = Math.ceil(canvas.width * 0.9);
                      const y0 = Math.floor(canvas.height * 0.78), y1 = Math.ceil(canvas.height * 0.96);
                      const img = ctx.getImageData(x0, y0, x1 - x0, y1 - y0).data;
                      for (let i = 0; i < img.length; i += 4) {
                        total++;
                        if (img[i] >= 230 && img[i + 1] >= 230 && img[i + 2] >= 230) bright++;
                      }
                      return { bright, total };
                    };
                    const baseBand = [];
                    await w.FCExport.renderSequence({ width: 960, height: 540, startFrame: 90, endFrame: 90 }, (canvas) => {
                      const ctx = canvas.getContext('2d');
                      const a = ctx.getImageData(100, 270, 1, 1).data;
                      const b = ctx.getImageData(480, 270, 1, 1).data;
                      base.push({ left: [a[0], a[1], a[2], a[3]], center: [b[0], b[1], b[2], b[3]] });
                      baseBand.push(bandStats(canvas));
                    });
                    c.mask = { shape: 'rect', x: 0.15, y: 0.5, w: 0.2, h: 0.5, feather: 0, invert: true, opacity: 1 };
                    const masked = [];
                    await w.FCExport.renderSequence({ width: 960, height: 540, startFrame: 90, endFrame: 90 }, (canvas) => {
                      const ctx = canvas.getContext('2d');
                      const a = ctx.getImageData(100, 270, 1, 1).data;
                      const b = ctx.getImageData(480, 270, 1, 1).data;
                      masked.push({ left: [a[0], a[1], a[2], a[3]], center: [b[0], b[1], b[2], b[3]] });
                    });
                    c.mask = originalMask;
                    out.replica.maskPixels = { baseline: base, masked };
                    if (w.FCaptions && typeof w.FCaptions.add === 'function') {
                      const before = (seq.captions || []).length;
                      const cap = w.FCaptions.add(seq, { start: 90, end: 91, text: 'PROBE 字幕 TEST' });
                      const capBand = [];
                      await w.FCExport.renderSequence({ width: 960, height: 540, startFrame: 90, endFrame: 90 }, (canvas) => capBand.push(bandStats(canvas)));
                      if (typeof w.FCaptions.remove === 'function') w.FCaptions.remove(seq, seq.captions.indexOf(cap));
                      out.replica.captionPixels = { baseline: baseBand, captioned: capBand, before, after: seq.captions.length };
                    }
                    try {
                      const track = seq.videoTracks.find((t) => (t.clips || []).length);
                      if (track && track.clips.length) {
                        const saved = JSON.parse(JSON.stringify(seq));
                        const first = track.clips[0];
                        first.end = 312;
                        const second = JSON.parse(JSON.stringify(first));
                        second.id = 'probe-transition-b';
                        second.start = 300;
                        second.end = 420;
                        second.sourceIn = (first.sourceIn || 0) + 120;
                        first.transition = { id: 'cross_dissolve', params: { duration: 0.5 } };
                        track.clips = [first, second];
                        seq.duration = 420;
                        const withTransition = [];
                        await w.FCExport.renderSequence({ width: 960, height: 540, startFrame: 306, endFrame: 306 }, (canvas) => {
                          const ctx = canvas.getContext('2d');
                          const px = ctx.getImageData(480, 270, 1, 1).data;
                          withTransition.push([px[0], px[1], px[2], px[3]]);
                        });
                        first.transition = null;
                        const withoutTransition = [];
                        await w.FCExport.renderSequence({ width: 960, height: 540, startFrame: 306, endFrame: 306 }, (canvas) => {
                          const ctx = canvas.getContext('2d');
                          const px = ctx.getImageData(480, 270, 1, 1).data;
                          withoutTransition.push([px[0], px[1], px[2], px[3]]);
                        });
                        out.replica.transitionPixels = { withTransition, withoutTransition };
                        if (w.App && w.App.seq) w.App.seq.sequence = saved;
                      }
                    } catch (e) { out.replica.transitionPixelError = String(e); }
                  } catch (e) { out.replica.maskPixelError = String(e); }
                } else {
                  out.replica.renderSamples = 'missing';
                }
              } catch (e) { out.replica.renderError = String(e); }
            }
          } catch (e) { out.replicaError = String(e); }
          return out;
        })()
        """;
        var raw=await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new {expression=script,awaitPromise=true,returnByValue=true}));
        using var doc=JsonDocument.Parse(raw);
        if(doc.RootElement.TryGetProperty("result",out var result)&&result.TryGetProperty("value",out var value))
            return value.Clone();
        return JsonSerializer.SerializeToElement(new {error="Runtime.evaluate 未返回 value",raw});
    }

    private async Task<bool> ConfirmEditorCloseAsync()
    {
        if(!_closeHandshake.Begin())return false;
        try
        {
            var decision=_webViewFailed ? CloseDecision.ConfirmUnresponsive : await CloseHandshake.DecideAsync(
                ()=>Browser.CoreWebView2.ExecuteScriptAsync(CloseHandshake.ProbeScript),
                ()=>
                {
                    var (json,ackTask)=_closeHandshake.CreateRequest();
                    Browser.CoreWebView2.PostWebMessageAsJson(json);
                    return ackTask;
                });
            if(decision==CloseDecision.Close)return true;
            return UnsavedChangesDialog.Confirm(this,decision);
        }
        finally{_closeHandshake.End();}
    }

    private async void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        // 收尾期间用户再次点关闭/Alt+F4 时，必须继续取消本次关闭；
        // 只有 OnClosing 自己完成 Dispose 后才允许真正的 Close。
        if(_allowClose)return;
        // 启动阶段（包括 TestServer 已开始恢复录制、但 _inProcess 还没赋值）不能直接关窗，
        // 否则 WPF 会退出，而后台宿主/录制/恢复没有机会收尾。
        if(_starting){e.Cancel=true;return;}
        if(_closing){e.Cancel=true;return;}
        // 关窗保存握手：页面的保存要经 LocalWebHost 转给进程内 API，一旦 Dispose 就再也存不进去，所以必须排在释放
        // _webHost/_inProcess 之前；外部服务模式（_inProcess 为 null）页面同样可能还有没写完的修改。
        if(!_editorCloseConfirmed&&_webHost is not null)
        {
            e.Cancel=true;
            // 上一轮还在等 ack 或确认框还开着：这次关闭直接取消，不叠第二轮。
            if(_closeHandshake.Running)return;
            bool proceed;
            try{proceed=await ConfirmEditorCloseAsync();}
            catch(Exception ex)
            {
                Console.Error.WriteLine("close confirmation failed: "+ex.Message);
                StatusText.Text="无法显示关窗确认，已留在当前窗口。请重试。";
                StatusText.Visibility=Visibility.Visible;
                return;
            }
            if(!proceed)return;
            _editorCloseConfirmed=true;
            // WPF 不允许在 Closing 处理过程中同步再调 Close，排到下一轮消息循环重新走一遍关窗流程。
            _=Dispatcher.BeginInvoke(new Action(Close));
            return;
        }
        if(_inProcess is null){_allowClose=true;return;}
        e.Cancel=true;_closing=true;StatusText.Text="正在完成录制、清理直播缓存并退出…";StatusText.Visibility=Visibility.Visible;Browser.Visibility=Visibility.Collapsed;
        try
        {
            _webHost?.Dispose();_webHost=null;
            await _inProcess.DisposeAsync();_inProcess=null;
            _allowClose=true;
            Close();
        }
        catch(Exception ex)
        {
            // 不能把“超时”当成“已收尾”：把错误留在窗口上，避免带着未完成的
            // FFmpeg/高光拼接直接退出。
            StatusText.Text="退出未完成："+ex.Message+"。请稍后重试关闭窗口。";
            _closing=false;
        }
    }
}
