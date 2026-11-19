using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace LiveClipper.Installation
{
    static class Launcher
    {
        [STAThread] static int Main(string[] args)
        {
            try
            {
                var root=Paths.ValidateRoot(AppDomain.CurrentDomain.BaseDirectory);
                ProcessStartInfo maintenance=null;
                using(var lease=AppSwap.AcquireLease(root))
                {
                    // Recovery is excluded while the installer holds its cross-await file lease.
                    if(AppSwap.HasRecovery(root))AppSwap.Recover(root);
                    var install=Paths.Load(root);
                    if(Path.GetFileName(Environment.GetCommandLineArgs()[0]).Equals("Uninstall.exe",StringComparison.OrdinalIgnoreCase))
                        maintenance=new ProcessStartInfo(Path.Combine(install.Root,"Maintenance.exe"),"/uninstall --dir "+Paths.Quote(install.Root)){UseShellExecute=true};
                    else
                    {
                        if(install.State!="complete")throw new InvalidOperationException("安装尚未完成，请重新运行安装程序并选择原目录重试。");
                        if(!AppSwap.HasDesktop(Path.Combine(root,"app")))throw new IOException("缺少 LiveClipper.Desktop.exe，请重新安装修复。");
                        Process.Start(Paths.LaunchInfo(install));return 0;
                    }
                }
                // The maintenance process acquires this same lease; release before spawning it.
                Process.Start(maintenance);return 0;
            }
            catch(Exception ex){MessageBox.Show(ex.Message,"LiveClipper",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
        }
    }
}
