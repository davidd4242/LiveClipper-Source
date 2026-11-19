using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace LiveClipper.Desktop;

// 启动页：早期 owned 原生窗（StartupSplashWindow）遮盖 → 页面 DOM 同合成面遮罩接管 → DOM 内弹簧放大/淡出。
// 协议见 web/public/startup-splash.js PROTOCOL。握手顺序：
//   host 阶段 1..4（真实）→ 页面 lc-splash-bootstrap-request → host 回 stage 快照
//   → 页面应用 stage + 两 rAF 后 lc-splash-bootstrap-ready → host 关闭原生窗
//   → 首路由 ready 且 bootstrap-ready 都到后 host 发 lc-splash-exit（只发一次/文档）
//   → 页面动画结束 lc-splash-complete → host 才标 _splashDone、撤 WPF 启动层。
// 阶段 1 进程内 API 已启动  2 WebView2 Environment 已创建  3 CoreWebView2 已初始化  4 LocalWebHost 与导航/消息处理已接好。
// 第五阶段（不透明度 1）由页面在 exit 门槛通过后自己置位，宿主不发 5，进度不跳 100。
public partial class MainWindow
{
    private const int SplashStageMax=4;
    private StartupSplashWindow? _splashWindow;
    private int _splashStagesDone;
    private bool _splashPageListening;      // 已收到本文档的 bootstrap-request，页面监听器确认存在
    private bool _splashBootstrapReady;
    private bool _splashFirstRouteReady;
    private bool _splashExitSent;
    private bool _splashDone;
    private string? _splashHostError;       // 宿主侧真实错误；页面晚到的 bootstrap-request 后补发

    private void InitSplash()
    {
        SourceInitialized+=(_,_)=>
        {
            var hwnd=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var source=System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source.CompositionTarget.BackgroundColor=System.Windows.Media.Color.FromRgb(44,44,46);
            WindowCaptionTheme.SetStartupCloak(hwnd,true);
            WindowCaptionTheme.Apply(hwnd,"dark");
            void RevealFirstFrame(object? sender,EventArgs args)
            {
                ContentRendered-=RevealFirstFrame;
                WindowCaptionTheme.SetStartupCloak(hwnd,false);
            }
            ContentRendered+=RevealFirstFrame;
        };
        Closed+=(_,_)=>CloseStartupSplash();
        // owned 窗必须等 owner 已显示（Loaded）才能建；先于 OnLoaded 订阅，所以先于 Browser 导航。
        Loaded+=(_,_)=>ShowStartupSplash();
        try
        {
            var decoder=BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/liveclipper.ico"),BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
            SplashLogo.Source=decoder.Frames.OrderByDescending(f=>f.PixelWidth).First();
        }
        catch(Exception ex){Console.Error.WriteLine("[desktop] 启动 Logo 加载失败："+ex.Message);}
    }

    /// <summary>Loaded 里建 owned 原生遮盖窗；此时 owner 已显示，Browser 还没开始导航。</summary>
    private void ShowStartupSplash()
    {
        if(_splashDone||_splashWindow is not null)return;
        try
        {
            var window=new StartupSplashWindow(this,SplashLogo.Source);
            window.Show();
            window.Sync();
            window.SetStage(_splashStagesDone);
            if(_splashHostError is not null)window.SetMessage(_splashHostError);
            _splashWindow=window;
            LocationChanged+=OnSplashOwnerMoved;
            SizeChanged+=OnSplashOwnerMoved;
            StateChanged+=OnSplashOwnerMoved;
            DpiChanged+=OnSplashOwnerMoved;
        }
        catch(Exception ex){Console.Error.WriteLine("[desktop] 启动遮盖窗创建失败："+ex.Message);}
    }

    private void OnSplashOwnerMoved(object? sender,EventArgs e)=>_splashWindow?.Sync();

    /// <summary>关闭原生遮盖窗并解除跟随事件；重复调用安全。</summary>
    private void CloseStartupSplash()
    {
        LocationChanged-=OnSplashOwnerMoved;
        SizeChanged-=OnSplashOwnerMoved;
        StateChanged-=OnSplashOwnerMoved;
        DpiChanged-=OnSplashOwnerMoved;
        var window=_splashWindow;
        _splashWindow=null;
        if(window is not null&&window.IsVisible)window.Close();
    }

    /// <summary>一个真实启动阶段（1..4）完成：原生 Logo 不透明度=阶段×0.2，并同步给已握手的页面。</summary>
    private void SplashStage(int stage)
    {
        if(_splashDone||stage<=_splashStagesDone||stage>SplashStageMax)return;
        _splashStagesDone=stage;
        SplashLogo.Opacity=stage*0.2;
        _splashWindow?.SetStage(stage);
        if(_splashPageListening)PostSplashStage();
    }

    private void PostSplashStage()=>PostToPage(new{type="lc-splash-stage",stage=_splashStagesDone});

    /// <summary>NavigationCompleted 成功：主动再发一次 stage 快照（防 bootstrap-request 回复丢失）。</summary>
    private void OnSplashNavigationCompleted(object? sender,CoreWebView2NavigationCompletedEventArgs e)
    {
        if(e.IsSuccess&&!_splashDone)PostSplashStage();
    }

    /// <summary>首路由 ready 已确认（Home 已等 5 份数据 commit）。</summary>
    private void SplashFirstRouteReady()
    {
        _splashFirstRouteReady=true;
        TrySendSplashExit();
    }

    // 必须 bootstrap-ready 在先，避免 exit 先于 stage 握手；每个文档只发一次。
    private void TrySendSplashExit()
    {
        if(!_splashBootstrapReady||!_splashFirstRouteReady||_splashExitSent)return;
        _splashExitSent=true;
        PostToPage(new{type="lc-splash-exit"});
    }

    /// <summary>宿主侧真实错误（首路由 error / 启动异常）：在仍可见的原生窗与页面 DOM Logo 旁显示。</summary>
    private void ShowSplashError(string message)
    {
        _splashHostError=message;
        _splashWindow?.SetMessage(message);
        if(_splashPageListening)PostToPage(new{type="lc-splash-error",message});
        // 页面进程已死时 DOM 不可能显示：撤下 HWND，露出 WPF 启动层与 StatusText。
        if(_webViewFailed&&!_splashDone)Browser.Visibility=Visibility.Collapsed;
    }

    /// <summary>
    /// 页面→宿主 lc-splash-* 消息，shape 必须精确（只有 type；error 另有非空字符串 message）。
    /// 返回 true 表示属于 lc-splash-* 命名空间，已处理或丢弃，调用方不再落到通用 requestId 分支。
    /// </summary>
    private bool TryHandleSplashMessage(JsonElement root)
    {
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("type",out var typeElement)||typeElement.ValueKind!=JsonValueKind.String)return false;
        var type=typeElement.GetString();
        if(type is null||!type.StartsWith("lc-splash-",StringComparison.Ordinal))return false;
        var count=root.EnumerateObject().Count();
        switch(type)
        {
            case "lc-splash-bootstrap-request": if(count==1)OnSplashBootstrapRequest(); break;
            case "lc-splash-bootstrap-ready": if(count==1)OnSplashBootstrapReady(); break;
            case "lc-splash-complete": if(count==1)OnSplashComplete(); break;
            case "lc-splash-error":
                if(count==2&&root.TryGetProperty("message",out var message)&&message.ValueKind==JsonValueKind.String&&!string.IsNullOrEmpty(message.GetString()))
                {
                    // 页面自己已在 DOM 显示；宿主只在原生窗同步显示，不回发，避免 error 互 post 循环。
                    var text=message.GetString()!;
                    Console.Error.WriteLine("[desktop] 启动遮罩页面错误："+text);
                    _splashWindow?.SetMessage(text);
                }
                break;
        }
        return true;
    }

    private void OnSplashBootstrapRequest()
    {
        // 新文档（首次或之后的重载）：握手状态按文档重置。
        _splashPageListening=true;
        _splashBootstrapReady=false;
        _splashExitSent=false;
        PostSplashStage();
        if(_splashHostError is not null)PostToPage(new{type="lc-splash-error",message=_splashHostError});
    }

    private void OnSplashBootstrapReady()
    {
        if(!_splashPageListening)return;
        _splashBootstrapReady=true;
        // 页面 DOM 的 Logo 已同位置/尺寸/opacity 且已两 rAF，此刻才撤原生窗。
        CloseStartupSplash();
        TrySendSplashExit();
    }

    /// <summary>页面动画结束且 DOM 遮罩已移除：此时才撤 WPF 启动层与启动监听。</summary>
    private void OnSplashComplete()
    {
        if(!_splashExitSent||_splashDone)return;
        _splashDone=true;
        CloseStartupSplash();
        if(Browser.CoreWebView2 is not null)Browser.CoreWebView2.NavigationCompleted-=OnSplashNavigationCompleted;
        SplashLayer.Visibility=Visibility.Collapsed;
        StatusText.Visibility=Visibility.Collapsed;
    }
}

