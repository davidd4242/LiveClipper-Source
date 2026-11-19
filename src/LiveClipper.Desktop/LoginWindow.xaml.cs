using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace LiveClipper.Desktop;

/// <summary>
/// 软件内的 B 站扫码登录窗口：登录成功后由 WebView2 的 Cookie 容器里直接取到 SESSDATA，
/// 不需要用户自己复制 Cookie，也不受浏览器 v20 应用绑定加密的限制。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private CancellationTokenSource? _poll;
    public string? Cookie {get;private set;}
    public LoginWindow(CoreWebView2Environment environment)
    {
        InitializeComponent();
        _environment=environment;
        Loaded+=OnLoaded;
        Closed+=(_,_)=>_poll?.Cancel();
    }
    private async void OnLoaded(object sender,RoutedEventArgs e)
    {
        try
        {
            await LoginBrowser.EnsureCoreWebView2Async(_environment);
            LoginBrowser.CoreWebView2.Settings.AreDevToolsEnabled=false;
            LoginBrowser.CoreWebView2.NavigationCompleted+=(_,args)=>
            {
                if(!args.IsSuccess)LoginStatus.Text="登录页加载失败，请检查网络后重试。";
            };
            LoginBrowser.Source=new Uri("https://passport.bilibili.com/login");
            _poll=new CancellationTokenSource();
            _=PollAsync(_poll.Token);
        }
        catch(Exception ex){LoginStatus.Text="无法打开登录页面："+ex.Message;}
    }
    private async Task PollAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try
            {
                var cookie=await BilibiliCookies.ReadAsync(LoginBrowser.CoreWebView2);
                if(!string.IsNullOrWhiteSpace(cookie))
                {
                    Cookie=cookie;
                    LoginStatus.Text="登录成功，正在保存…";
                    DialogResult=true;
                    Close();
                    return;
                }
            }
            catch(OperationCanceledException){return;}
            catch(Exception){/* 页面切换过程中偶发失败，下一轮继续 */}
            try{await Task.Delay(1500,token);}catch(OperationCanceledException){return;}
        }
    }
    private void OnCancel(object sender,RoutedEventArgs e){DialogResult=false;Close();}
}
