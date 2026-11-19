using System.Configuration;
using System.Data;
using System.Windows;

namespace LiveClipper.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 标记"本进程是桌面壳"，给 LiveClipper.App 的 Program 用：它据此决定要不要在启动时清掉
        // 上一轮残留的恢复出厂设置标记（独立服务进程模式下不能碰这个标记）。
        // 必须在 base.OnStartup 之前：StartupUri 里的窗口是在 base.OnStartup 里创建的，
        // 而进程内宿主要等窗口 OnLoaded 才跑 Program.Main，时间上仍来得及，但先设好更稳、更易读。
        Environment.SetEnvironmentVariable("LIVECLIPPER_SERVER_ENTRY","1");
        // 第二实例在窗口出现之前就退出：这里用的是与 Program.Main 开头同一套判定
        // （Program.DetectExistingInstance），判定成功会自己发激活信号并 Environment.Exit(0)，
        // 所以 StartupUri 那个 MainWindow 根本不会被创建，也就没有闪窗。
        // 仍然是竞态窗口里的一层努力：P1 起后 ~300ms 内起来的 P2 若在 P1 建好激活事件之前探空，
        // 它照样会创建窗口、闪一次（见 MainWindow 构造函数注释）；构造期还会再判一次。
        if(Program.DetectExistingInstance(Program.ResolvePort(Environment.GetEnvironmentVariable("LIVECLIPPER_PORT")),quiet:true,exitOnHit:true))return;
        base.OnStartup(e);
    }
}

