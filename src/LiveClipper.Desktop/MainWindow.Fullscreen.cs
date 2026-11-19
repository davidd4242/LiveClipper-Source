using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace LiveClipper.Desktop;

// 页面 document.documentElement.requestFullscreen() 触发的整窗口原生全屏。
// 退出由 WebView 自带 Esc / 页面 exitFullscreen 触发，窗口只跟随 ContainsFullScreenElement 状态。
public partial class MainWindow
{
    private CoreWebView2? _fullscreenSource;
    private bool _fullscreenActive;
    private bool _fullscreenApplying;
    private WindowStyle _fsStyle;
    private ResizeMode _fsResize;
    private WindowState _fsState;
    private Rect _fsBounds;

    private void AttachFullscreen(CoreWebView2 core)
    {
        _fullscreenSource=core;
        core.ContainsFullScreenElementChanged+=OnContainsFullScreenElementChanged;
        Closed+=(_,_)=>DetachFullscreen();
    }

    private void DetachFullscreen()
    {
        if(_fullscreenSource is null)return;
        _fullscreenSource.ContainsFullScreenElementChanged-=OnContainsFullScreenElementChanged;
        _fullscreenSource=null;
    }

    private void OnContainsFullScreenElementChanged(object? sender,object e)
    {
        if(_fullscreenApplying)return;
        var want=_fullscreenSource?.ContainsFullScreenElement==true;
        if(want==_fullscreenActive)return;
        _fullscreenApplying=true;
        try
        {
            if(want)EnterNativeFullscreen();else ExitNativeFullscreen();
        }
        finally{_fullscreenApplying=false;}
    }

    private void EnterNativeFullscreen()
    {
        _fsStyle=WindowStyle;
        _fsResize=ResizeMode;
        _fsState=WindowState==WindowState.Minimized?WindowState.Normal:WindowState;
        _fsBounds=WindowState==WindowState.Normal?new Rect(Left,Top,Width,Height):RestoreBounds;
        _fullscreenActive=true;
        // 先回 Normal 再改样式，最后最大化：WPF 对 None+Maximized 取当前显示器整块区域（含任务栏），已最大化窗口也要经过这一步才会刷新非客户区。
        WindowState=WindowState.Normal;
        WindowStyle=WindowStyle.None;
        ResizeMode=ResizeMode.NoResize;
        WindowState=WindowState.Maximized;
    }

    private void ExitNativeFullscreen()
    {
        _fullscreenActive=false;
        WindowState=WindowState.Normal;
        WindowStyle=_fsStyle;
        ResizeMode=_fsResize;
        // 常态窗口恢复原位置大小；原本最大化的保持 RestoreBounds 不动，只回到最大化。
        if(_fsState==WindowState.Normal)
        {
            Left=_fsBounds.Left;Top=_fsBounds.Top;Width=_fsBounds.Width;Height=_fsBounds.Height;
        }
        else WindowState=_fsState;
    }
}
