using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LiveClipper.Installation
{
    public sealed class InstallOptions
    {
        public string Root;
        public bool Speech=true,LocalAi,Demucs,DesktopShortcut=true,StartMenu=true,Register=true,ForcePrivateDotnet,ForcePrivateWebView;
        public string DownloadRoute="AUTO",DetectedCountry;
    }
    public sealed class ProgressUpdate
    {
        public string Message;public int Percent;public bool Log;
        public string Stage,Detail;
        public ProgressUpdate(string message,int percent=-1,bool log=true,string stage=null,string detail=null){Message=message;Percent=percent;Log=log;Stage=stage;Detail=detail;}
    }
    public sealed partial class Engine
    {
        readonly IProgress<ProgressUpdate> progress;
        readonly CancellationToken token;
        string root,cache;
        string stage="准备";int overall,phaseStart,phaseSpan;
        SourcePolicy sources;
        readonly Dictionary<string,object> deps;
        readonly HttpClient http=new HttpClient{Timeout=Timeout.InfiniteTimeSpan};
        public Engine(IProgress<ProgressUpdate> progress,CancellationToken token)
        {
            this.progress=progress;this.token=token;
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("dependencies.json"))
            using(var reader=new StreamReader(stream))deps=Paths.Json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LiveClipperSetup/1.1");
        }
        void Report(string text,int percent=-1,bool log=true,string detail=null)=>progress.Report(new ProgressUpdate(text,percent<0?overall:percent,log,stage,detail));
        static void Resource(string name,string path)
        {
            Paths.NoLinks(path);Directory.CreateDirectory(Path.GetDirectoryName(path));
            using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using(var target=File.Create(path)){if(source==null)throw new IOException("安装包缺少资源："+name);source.CopyTo(target);}
        }
        public static string DetectDotnet()
        {
            var candidates=new[]{Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),Environment.GetEnvironmentVariable("DOTNET_ROOT"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet"),Paths.OptionalParent(Paths.Find("dotnet.exe"))};
            foreach(var directory in candidates.Select(Paths.OptionalFull).Where(x=>x!=null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var text=Paths.Probe(Path.Combine(directory,"dotnet.exe"),"--list-runtimes");if(text==null)continue;
                var shared=new[]{"Microsoft.NETCore.App","Microsoft.WindowsDesktop.App","Microsoft.AspNetCore.App"};
                // All three must have a common supported major to avoid incomplete runtime installs.
                for(int major=10;major>=9;major--)
                    if(shared.All(name=>text.Split('\n').Any(line=>line.StartsWith(name+" "+major+".",StringComparison.Ordinal))))return directory;
            }
            return null;
        }
        public static string DetectWebView()
        {
            var bases=new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),@"Microsoft\EdgeWebView\Application"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Microsoft\EdgeWebView\Application")};
            foreach(var path in bases)
                if(Directory.Exists(path))foreach(var directory in Directory.GetDirectories(path).OrderByDescending(x=>x))
                    if(File.Exists(Path.Combine(directory,"msedgewebview2.exe")))return directory;
            return null;
        }
        public static string DetectFfmpeg()
        {
            var exe=Paths.Find("ffmpeg.exe");
            // Existing LiveClipper per-user tools are reused, never overwritten or uninstalled.
            if(exe==null)try{var settings=Paths.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","settings.json")));object value;if(settings.TryGetValue("FfmpegPath",out value))exe=value as string;}catch{}
            if(exe!=null&&File.Exists(exe)&&File.Exists(Path.Combine(Path.GetDirectoryName(exe),"ffprobe.exe"))&&Paths.Probe(exe,"-version")!=null)return exe;
            var tools=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"LiveClipper\tools\ffmpeg");
            if(Directory.Exists(tools))foreach(var candidate in Directory.GetFiles(tools,"ffmpeg.exe",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc))
                if(File.Exists(Path.Combine(Path.GetDirectoryName(candidate),"ffprobe.exe"))&&Paths.Probe(candidate,"-version")!=null)return candidate;
            return null;
        }
        public static string DetectOllama()
        {
            var candidates=new List<string>{Paths.Find("ollama.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Programs\Ollama\ollama.exe")};
            foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine})
            using(var keys=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
            {if(keys==null)continue;foreach(var name in keys.GetSubKeyNames())using(var key=keys.OpenSubKey(name))if((key?.GetValue("DisplayName") as string??"").StartsWith("Ollama",StringComparison.OrdinalIgnoreCase)&&key.GetValue("InstallLocation") is string path){var directory=Paths.OptionalFull(path);if(directory!=null)candidates.Add(Path.Combine(directory,"ollama.exe"));}}
            return candidates.FirstOrDefault(x=>!string.IsNullOrEmpty(x)&&File.Exists(x));
        }
        public static bool HasVcRuntime()
        {
            foreach(var name in new[]{"msvcp140.dll","vcruntime140_1.dll"})
            {var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),name);if(!File.Exists(path))return false;var version=FileVersionInfo.GetVersionInfo(path);if(version.FileMajorPart<14||(version.FileMajorPart==14&&version.FileMinorPart<40))return false;}
            return true;
        }
        public static Dictionary<string,string> Diagnose()=>new Dictionary<string,string>{
            {".NET 桌面与服务环境","已随主程序内置（自包含），无需安装"},
            {"WebView2 浏览环境",DetectWebView()??"按需下载 · 安装到所选目录"},
            {"FFmpeg / FFprobe",DetectFfmpeg()??"按需下载 · 安装到所选目录"},
            {"Python 3.11","按需下载 · 优先复用已有 Python"},
            {"VC++ 运行库",HasVcRuntime()?"已有可用系统组件":"按需下载 · 可能需要 Windows 授权"},
            {"Ollama 本机 AI",DetectOllama()??"按需下载 · 可选"}
        };
        async Task Run(string exe,string arguments,Dictionary<string,string> environment=null)
        {
            token.ThrowIfCancellationRequested();Report("执行："+Path.GetFileName(exe)+" "+arguments);
            var start=new ProcessStartInfo(exe,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            if(environment!=null)foreach(var item in environment)start.EnvironmentVariables[item.Key]=item.Value;
            using(var process=new Process{StartInfo=start})
            {
                var errors=new StringBuilder();process.OutputDataReceived+=(s,e)=>{if(e.Data!=null)Report(e.Data);};process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null){lock(errors){errors.AppendLine(e.Data);}Report(e.Data);}};
                if(!process.Start())throw new IOException("无法启动安装子进程");process.BeginOutputReadLine();process.BeginErrorReadLine();
                using(token.Register(()=>{try{Process.Start(new ProcessStartInfo("taskkill.exe","/PID "+process.Id+" /T /F"){UseShellExecute=false,CreateNoWindow=true})?.WaitForExit(5000);}catch{}}))
                {await Task.Run(()=>process.WaitForExit());token.ThrowIfCancellationRequested();if(process.ExitCode!=0)throw new IOException(Path.GetFileName(exe)+" 执行失败："+errors);}
            }
        }
        /// <summary>安装包自身的版本号，随 csproj 的 Version 一起升级。</summary>
        public static string Version=>Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        /// <summary>
        /// 找到本机已有的 LiveClipper 安装（默认目录 + 当前用户卸载注册表项）。
        /// 找到就返回其清单，调用方据此把界面切成升级模式；找不到返回 null。
        /// </summary>
        public static Installation FindExistingInstall()
        {
            var candidates=new List<string>{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Programs\LiveClipper")};
            using(var keys=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if(keys!=null)foreach(var name in keys.GetSubKeyNames())
                    using(var key=keys.OpenSubKey(name))
                    {
                        if((key?.GetValue("DisplayName") as string??"").StartsWith("LiveClipper",StringComparison.OrdinalIgnoreCase)&&key.GetValue("InstallLocation") is string path)
                        {var full=Paths.OptionalFull(path);if(full!=null)candidates.Add(full);}
                    }
            }
            foreach(var root in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                try{if(File.Exists(Path.Combine(root,"installation.json")))return Paths.Load(root);}catch{}
            return null;
        }
        /// <summary>关闭安装目录里正在运行的程序，覆盖 app 之前必须先释放被占用的文件。</summary>
        public static async Task StopOwnedProcessesAsync(string root)
        {
            foreach(var process in Process.GetProcesses())
            using(process)try
            {
                if(process.Id==Process.GetCurrentProcess().Id)continue;
                var exe=process.MainModule.FileName;if(!Paths.Inside(root,exe))continue;
                // 先给窗口机会正常退出，让正在录制的切片落盘。
                if(process.CloseMainWindow())await Task.Run(()=>process.WaitForExit(20000));
            }catch{}
            foreach(var process in Process.GetProcesses())
            using(process)try
            {
                if(process.Id==Process.GetCurrentProcess().Id)continue;
                if(Paths.Inside(root,process.MainModule.FileName)&&!process.HasExited){process.Kill();await Task.Run(()=>process.WaitForExit(10000));}
            }catch{}
        }
        public static void PreparePayload(string archive,string staging,string version)
        {
            PayloadIntegrity.Manifest manifest;
            using(var source=typeof(Engine).Assembly.GetManifestResourceStream("payload.zip"))
            {
                if(source==null)throw new IOException("缺少内嵌 payload.zip");
                manifest=PayloadIntegrity.FromArchive(source,version);
            }
            Paths.NoLinks(archive);
            // Keep the archive read-only through verification and extraction, not two unlocked opens.
            using(var heldArchive=new FileStream(archive,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                PayloadIntegrity.VerifyArchive(archive,manifest);
                Paths.ExtractZip(archive,staging);
                PayloadIntegrity.WriteManifest(staging,manifest);
            }
        }
        /// <summary>
        /// Caller holds AppSwap.AcquireLease and calls Begin before changing State/Version.
        /// Two renames preserve a backup, not an atomic exchange; commit follows the complete manifest.
        /// </summary>
        public static void SwapApp(string root)=>AppSwap.Stage(root);
        public static void CommitAppSwap(string root)=>AppSwap.Commit(root);
        public static void RecoverAppSwap(string root)=>AppSwap.Recover(root);
        static string Identity(string root)
        {using(var digest=SHA256.Create())return BitConverter.ToString(digest.ComputeHash(Encoding.UTF8.GetBytes(Paths.Full(root).ToUpperInvariant()))).Replace("-","").Substring(0,12);}
        static bool PointsInto(string root,string path)
        {
            try{return !string.IsNullOrEmpty(path)&&Paths.Inside(root,path);}catch{return false;}
        }
        static string ShortcutTarget(string path)
        {
            try{dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic shortcut=shell.CreateShortcut(path);return (string)shortcut.TargetPath;}
            catch{return null;}
        }
        static void MakeShortcut(Installation install,string directory,string name,bool uninstall)
        {
            var path=Path.Combine(directory,name+".lnk");
            // 同名快捷方式如果本来就指向本次安装目录，直接覆盖；否则才另起名字，
            // 避免重复安装时堆出 LiveClipper-1、LiveClipper-2…
            if(File.Exists(path)&&!PointsInto(install.Root,ShortcutTarget(path)))
            {
                var suffix=0;var prefix=Path.Combine(directory,name);
                do{suffix++;path=prefix+"-"+Identity(install.Root).Substring(0,4)+(suffix>1?"-"+suffix:"")+".lnk";}while(File.Exists(path));
            }
            var type=Type.GetTypeFromProgID("WScript.Shell");dynamic shell=Activator.CreateInstance(type);dynamic shortcut=shell.CreateShortcut(path);
            shortcut.TargetPath=Path.Combine(install.Root,uninstall?"Uninstall.exe":"LiveClipper.exe");shortcut.WorkingDirectory=install.Root;shortcut.IconLocation=Path.Combine(install.Root,"LiveClipper.exe")+",0";shortcut.Description=uninstall?"卸载 LiveClipper":"打开 LiveClipper";shortcut.Save();
            if(!install.Shortcuts.Contains(path))install.Shortcuts.Add(path);
            Paths.Save(install);
        }
        static void Register(Installation install)
        {
            install.RegistryKey=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveClipper-"+Identity(install.Root);
            using(var key=Registry.CurrentUser.CreateSubKey(install.RegistryKey))
            {key.SetValue("DisplayName","LiveClipper");key.SetValue("DisplayVersion",install.Version);key.SetValue("InstallLocation",install.Root);key.SetValue("DisplayIcon",Path.Combine(install.Root,"LiveClipper.exe"));key.SetValue("UninstallString",Paths.Quote(Path.Combine(install.Root,"Uninstall.exe")));key.SetValue("NoModify",1);key.SetValue("NoRepair",1);}
            Paths.Save(install);
        }
        public static async Task Uninstall(string root,bool removeData,IProgress<ProgressUpdate> progress)
        {
            var install=Paths.Load(root);root=install.Root;
            using(var lease=AppSwap.AcquireLease(root))
            {
            progress.Report(new ProgressUpdate("正在关闭此安装目录中的程序…"));
            await StopOwnedProcessesAsync(root);
            RecoverAppSwap(root);install=Paths.Load(root);
            AppSwap.EnsureNoAppProcesses(root);
            foreach(var shortcutPath in install.Shortcuts)
            {
                var desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);var menu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"LiveClipper");
                if(!Paths.Inside(desktop,shortcutPath)&&!Paths.Inside(menu,shortcutPath))continue;
                if(!File.Exists(shortcutPath))continue;
                if(PointsInto(root,ShortcutTarget(shortcutPath))){Paths.NoLinks(shortcutPath);File.Delete(shortcutPath);}
            }
            var expected=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveClipper-"+Identity(root);
            if(install.RegistryKey==expected)
            {using(var key=Registry.CurrentUser.OpenSubKey(expected))if(string.Equals(key?.GetValue("InstallLocation") as string,root,StringComparison.OrdinalIgnoreCase))Registry.CurrentUser.DeleteSubKeyTree(expected,false);}
            foreach(var name in new[]{"app","app-update","app.bak",AppSwap.Marker,AppSwap.Marker+".tmp","runtime","cache","LiveClipper.exe","Uninstall.exe","Maintenance.exe","installation.json.tmp"})
            {progress.Report(new ProgressUpdate("移除 "+name));Paths.DeleteOwned(root,name);}
            if(removeData)Paths.DeleteOwned(root,"data");
            if(removeData||!Directory.Exists(install.Data))Paths.DeleteOwned(root,"installation.json");
            else{install.State="uninstalled-data-retained";install.Shortcuts.Clear();install.RegistryKey=null;Paths.Save(install);}
            }
            if(!Directory.EnumerateFileSystemEntries(root).Any())Directory.Delete(root);
            progress.Report(new ProgressUpdate(removeData?"卸载完成。外部原始文件与共享环境已保留。":"卸载完成。软件数据保留在："+install.Data,100));
        }
        public static string DetachForUninstall(string root)
        {
            Paths.Load(root);var folder=Path.Combine(Path.GetTempPath(),"LiveClipper-Uninstall-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            var copy=Path.Combine(folder,"LiveClipperSetup.exe");File.Copy(Assembly.GetExecutingAssembly().Location,copy);return copy;
        }
    }
}
