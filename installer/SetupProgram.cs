using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace LiveClipper.Installation
{
    static class SetupProgram
    {
        static string Arg(string[] args,string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
        [STAThread] static int Main(string[] args)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try{return Run(args);}
            // 卸载副本（%TEMP%\LiveClipper-Uninstall-*）用完即删；删不掉只吞掉。
            finally{Paths.CleanupUninstallCopy(Assembly.GetExecutingAssembly().Location);}
        }
        static int Run(string[] args)
        {
            try
            {
                var root=Arg(args,"--dir");
                if(Array.IndexOf(args,"/diagnose")>=0){File.WriteAllText(Arg(args,"--output")??"diagnostics.json",Paths.Json.Serialize(Engine.Diagnose()));return 0;}
                if(Array.IndexOf(args,"/self-test")>=0){SelfTest.Run(Arg(args,"--output"));return 0;}
                var uninstall=Array.IndexOf(args,"/uninstall")>=0;
                if(uninstall&&Paths.Inside(root,Assembly.GetExecutingAssembly().Location))
                {
                    var detached=Engine.DetachForUninstall(root);var forwarded="/uninstall --dir "+Paths.Quote(root)+(Array.IndexOf(args,"--quiet")>=0?" --quiet":"")+(Array.IndexOf(args,"--remove-data")>=0?" --remove-data":"");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(detached,forwarded){UseShellExecute=true});return 0;
                }
                if(Array.IndexOf(args,"--quiet")>=0&&Arg(args,"--render")==null)
                {
                    // Automated smoke tests use explicit dedicated directories; no default silent target.
                    if(root==null)throw new ArgumentException("--quiet 必须明确指定 --dir");
                    var updates=new Progress<ProgressUpdate>(x=>{try{Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"LiveClipper-Logs"));File.AppendAllText(Path.Combine(Path.GetTempPath(),"LiveClipper-Logs","automation.log"),x.Message+Environment.NewLine);}catch{}});
                    if(uninstall)Engine.Uninstall(root,Array.IndexOf(args,"--remove-data")>=0,updates).GetAwaiter().GetResult();
                    else new Engine(updates,CancellationToken.None).Install(new InstallOptions{Root=root,Speech=Array.IndexOf(args,"--core-only")<0,LocalAi=Array.IndexOf(args,"--local-ai")>=0,Demucs=Array.IndexOf(args,"--demucs")>=0,DesktopShortcut=Array.IndexOf(args,"--no-shortcuts")<0,StartMenu=Array.IndexOf(args,"--no-shortcuts")<0,Register=Array.IndexOf(args,"--no-register")<0,ForcePrivateDotnet=Array.IndexOf(args,"--private-dotnet")>=0,ForcePrivateWebView=Array.IndexOf(args,"--private-webview")>=0,DownloadRoute=Arg(args,"--source")??"AUTO"}).GetAwaiter().GetResult();
                    return 0;
                }
                using(var form=new SetupForm(uninstall,root,Arg(args,"--render")!=null))
                {
                    var render=Arg(args,"--render");if(render!=null){form.Show();form.SetProofDiagnostics();Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(render,System.Drawing.Imaging.ImageFormat.Png);}return 0;}
                    Application.Run(form);
                }
                return 0;
            }
            catch(Exception ex)
            {
                var output=Arg(args,"--error-output");if(output!=null)File.WriteAllText(output,ex.ToString());
                if(Array.IndexOf(args,"--quiet")<0)MessageBox.Show(ex.Message,"LiveClipper 安装程序",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
