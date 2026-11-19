using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace LiveClipper.Desktop;

/// <summary>
/// 启动期临时遮盖：owner=MainWindow 的无边框窗口，盖住主窗 client 区（不遮 caption）。
/// WPF 元素盖不住 WebView2 的 HWND，所以由这个独立 HWND 在页面 DOM 遮罩接管前顶上；
/// 页面发 lc-splash-bootstrap-ready 后由 MainWindow 关闭它。不 Topmost、不进任务栏、不抢焦点。
/// Logo 与页面遮罩一致：160 DIP、深底 #2c2c2e、不透明度 = 阶段 × 0.2。
/// </summary>
internal sealed class StartupSplashWindow : Window
{
    private const double LogoDip=160;
    private const double MessageOffsetDip=LogoDip/2+44;   // 与 startup-splash.js 的 .lc-msg 同位置
    private const int GwlExStyle=-20;
    private const int WsExToolWindow=0x00000080, WsExNoActivate=0x08000000;
    private const uint SwpNoZOrder=0x0004, SwpNoActivate=0x0010;

    [StructLayout(LayoutKind.Sequential)] private struct Point32{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct Rect32{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd,out Rect32 rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd,ref Point32 point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd,int index,int value);

    private readonly Window _main;
    private readonly System.Windows.Controls.Image _logo;
    private readonly TextBlock _message;

    public StartupSplashWindow(Window main,ImageSource? logo)
    {
        _main=main;
        WindowStyle=WindowStyle.None;
        ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=false;
        ShowActivated=false;
        Topmost=false;
        WindowStartupLocation=WindowStartupLocation.Manual;
        Background=new SolidColorBrush(Color.FromRgb(0x2c,0x2c,0x2e));
        Owner=main;
        _logo=new System.Windows.Controls.Image{Width=LogoDip,Height=LogoDip,Stretch=Stretch.Uniform,Opacity=0,Source=logo,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        RenderOptions.SetBitmapScalingMode(_logo,BitmapScalingMode.HighQuality);
        _message=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(0xc7,0xc7,0xcc)),FontSize=13,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Visibility=Visibility.Collapsed};
        var grid=new Grid();
        grid.Children.Add(_logo);
        grid.Children.Add(_message);
        Content=grid;
        // 文本在 Logo 正下方：随窗口高度重算上边距（DIP）。
        SizeChanged+=(_,_)=>_message.Margin=new Thickness(24,ActualHeight/2+MessageOffsetDip,24,0);
        SourceInitialized+=(_,_)=>
        {
            var hwnd=new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd,GwlExStyle,GetWindowLong(hwnd,GwlExStyle)|WsExToolWindow|WsExNoActivate);
            Sync();   // 首次显示前就落在主窗 client 区，不闪错位
        };
        DpiChanged+=(_,_)=>Sync();
    }

    public void SetStage(int stage)=>_logo.Opacity=stage*0.2;

    public void SetMessage(string text){_message.Text=text;_message.Visibility=Visibility.Visible;}

    /// <summary>把窗口对齐到主窗 client 区。全程用设备像素（GetClientRect/ClientToScreen/SetWindowPos），不经 DIP 换算。</summary>
    public void Sync()
    {
        var main=new WindowInteropHelper(_main).Handle;
        var self=new WindowInteropHelper(this).Handle;
        if(main==IntPtr.Zero||self==IntPtr.Zero||_main.WindowState==WindowState.Minimized)return;
        if(!GetClientRect(main,out var rect))return;
        var origin=new Point32();
        if(!ClientToScreen(main,ref origin))return;
        var width=rect.Right-rect.Left;var height=rect.Bottom-rect.Top;
        if(width<=0||height<=0)return;
        SetWindowPos(self,IntPtr.Zero,origin.X,origin.Y,width,height,SwpNoZOrder|SwpNoActivate);
    }
}
