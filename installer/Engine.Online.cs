using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LiveClipper.Installation
{
    public sealed partial class Engine
    {
        void Phase(string name,string text,int start,int span)
        {stage=name;phaseStart=start;phaseSpan=span;overall=Math.Max(overall,start);Report(text);}

        // 低完整性子进程无法写 %LOCALAPPDATA% 的默认数据目录，会以 SQLite Error 14
        // 的形式在首次启动才暴露。安装阶段就查一次，别把坏包交给用户。
        // 直接读 mandatory label 的 SID（S-1-16-4096 = Low），不依赖 icacls 的本地化文本。
        [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
        static extern uint GetNamedSecurityInfo(string objectName,int objectType,uint securityInfo,
            out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
        [DllImport("advapi32.dll",SetLastError=true)]
        static extern bool GetAce(IntPtr acl,uint index,out IntPtr ace);
        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);

        static bool IsLowIntegrity(string path)
        {
            var rc=GetNamedSecurityInfo(path,1,0x10,out _,out _,out _,out var sacl,out var descriptor);
            if(rc!=0)throw new IOException("完整性检查失败：无法读取 "+path+" 的标签（Win32 "+rc+"）。");
            try
            {
                if(sacl==IntPtr.Zero)return false;
                var count=Marshal.ReadInt16(sacl,4);
                for(var i=0;i<count;i++)
                {
                    if(!GetAce(sacl,(uint)i,out var ace))continue;
                    // SYSTEM_MANDATORY_LABEL_ACE: header 4 + mask 4, SID starts at +8.
                    var sid=IntPtr.Add(ace,8);
                    if(Marshal.ReadByte(sid,1)==0)continue;
                    if(Marshal.ReadInt32(sid,8)==0x1000)return true;
                }
                return false;
            }
            finally { if(descriptor!=IntPtr.Zero)LocalFree(descriptor); }
        }

        void AssertMediumIntegrity(string path)
        {
            if(!File.Exists(path))throw new IOException("完整性检查失败：缺少 "+path);
            var icacls=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"icacls.exe");
            var acl=Paths.Probe(icacls,"\""+path+"\"",10000);
            var low=IsLowIntegrity(path);
            if(string.IsNullOrWhiteSpace(acl))
                throw new IOException("完整性检查失败：无法读取 "+path+" 的 ACL/完整性标签。");
            if(low)
                throw new IOException("完整性检查失败："+path+" 带 Low Mandatory Label，无法写默认数据目录。请从普通权限目录重新构建安装包。");
        }

        async Task<string> Download(string key,string filename,string algorithm="SHA256")
        {
            var metadata=(Dictionary<string,object>)deps[key];object mirror;
            metadata.TryGetValue("chinaUrl",out mirror);
            return await new DownloadClient(http).Fetch(sources.Candidates((string)metadata["url"],mirror as string),Path.Combine(cache,filename),(string)metadata["hash"],algorithm,token,(message,percent,detail)=>
            {
                if(percent>=0)overall=Math.Max(overall,phaseStart+percent*phaseSpan/100);
                Report(message+" "+filename,overall,false,detail);
            });
        }

        async Task Pip(string uv,string arguments,Dictionary<string,string> environment,bool torch=false)
        {
            var preferred=torch?(sources.China?SourcePolicy.TorchChina:SourcePolicy.TorchOfficial):(sources.China?SourcePolicy.PipChina:SourcePolicy.PipOfficial);
            var backup=torch?SourcePolicy.TorchOfficial:SourcePolicy.PipOfficial;
            try{await Run(uv,arguments+" --index-url "+Paths.Quote(preferred)+" --no-config",environment);}
            catch(IOException)when(sources.China)
            {Report("国内 Python 源暂不可用，正在切换官方源");await Run(uv,arguments+" --index-url "+Paths.Quote(backup)+" --no-config",environment);}
        }

        public async Task<Installation> Install(InstallOptions options)
        {
            overall=0;root=Paths.ValidateRoot(options.Root);
            Installation install;var fresh=false;
            if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())
            {
                if(!File.Exists(Path.Combine(root,"installation.json")))throw new IOException("该文件夹已有其他文件，请选择一个新的 LiveClipper 文件夹");
                install=Paths.Load(root);
            }
            else{Directory.CreateDirectory(root);install=new Installation{Root=root,State="failed"};fresh=true;}
            try
            {
              using(var lease=AppSwap.AcquireLease(root))
              {
                // Repeat admission under the lease: another installer may have won the initial empty-directory race.
                if(File.Exists(Path.Combine(root,"installation.json"))){install=Paths.Load(root);fresh=false;}
                else
                {
                    if(Directory.EnumerateFileSystemEntries(root).Any(path=>!Path.GetFileName(path).Equals("installation.lock",StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("该文件夹已有其他文件，请选择一个新的 LiveClipper 文件夹");
                    Paths.Save(install);
                }
                RecoverAppSwap(root);
                install=Paths.Load(root);
                var updating=!fresh&&Directory.Exists(Path.Combine(root,"app"));
                try
                {
                AppSwap.Begin(root,Version);
                install=Paths.Load(root);
                Phase("准备",updating?"正在准备就地更新":"正在选择下载线路",0,2);
                sources=options.DownloadRoute=="CN"?new SourcePolicy{Country="CN"}:options.DownloadRoute=="GLOBAL"?new SourcePolicy{Country="GLOBAL"}:options.DetectedCountry!=null?new SourcePolicy{Country=options.DetectedCountry}:await SourcePolicy.Detect(token);
                install.DownloadRegion=sources.Country;
                Report(updating?"检测到已安装的 LiveClipper，将就地更新主程序并保留数据与运行环境":"正在安装到所选目录");
                // 重复安装时保留已就绪的环境记录，避免把运行时再下载一遍；只有全新安装才清空。
                if(fresh){install.WebView=null;install.Python=null;install.Dotnet=null;install.Ffmpeg=null;}
                Paths.Save(install);
                cache=Path.Combine(root,"cache");Directory.CreateDirectory(cache);Directory.CreateDirectory(install.Data);
                var minimum=(options.Demucs?5L:options.LocalAi?5L:options.Speech?2L:1L)*1073741824;
                if(new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace<minimum)throw new IOException("所选组件需要至少 "+minimum/1073741824+" GB 可用空间");
                Phase("程序",updating?"正在更新主程序":"正在解压主程序",2,3);
                // The journal already owns recovery before any staging cleanup or extraction.
                var staging=Path.Combine(root,"app-update");
                Paths.DeleteOwned(root,"app-update");   // 清掉上次失败留下的临时目录，否则移动会失败
                Resource("payload.zip",Path.Combine(cache,"software.zip"));
                PreparePayload(Path.Combine(cache,"software.zip"),staging,Version);
                // 先验 staging，再替换；否则坏包已经落到 app/ 才报错，用户会留下一个装了一半的版本。
                AssertMediumIntegrity(Path.Combine(staging,"LiveClipper.Desktop.exe"));
                token.ThrowIfCancellationRequested();
                if(updating)await StopOwnedProcessesAsync(root);
                token.ThrowIfCancellationRequested();
                SwapApp(root);
                install=Paths.Load(root); // Stage publishes the new payload anchor; do not overwrite it with the pre-swap object.
                AssertMediumIntegrity(Path.Combine(root,"app","LiveClipper.Desktop.exe"));
                Resource("launcher.exe",Path.Combine(root,"LiveClipper.exe"));Resource("launcher.exe",Path.Combine(root,"Uninstall.exe"));
                Paths.NoLinks(Path.Combine(root,"Maintenance.exe"));File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(root,"Maintenance.exe"),true);
                await InstallRuntimes(install,options);
                install.Speech=options.Speech;install.Demucs=options.Demucs;install.LocalAi=options.LocalAi;
                Phase("组件","准备所选 Python 组件",45,30);
                if(options.Speech||options.Demucs)await InstallPython(install,options);
                Phase("组件","准备本机 AI（可选）",75,18);
                install.Ollama=DetectOllama();
                if(options.LocalAi)
                {
                    if(install.Ollama==null)
                    {
                        var archive=await Download("ollama","ollama.zip");var directory=Path.Combine(root,@"runtime\ollama");Paths.ExtractZip(archive,directory);
                        install.Ollama=Directory.GetFiles(directory,"ollama.exe",SearchOption.AllDirectories).FirstOrDefault()??throw new IOException("Ollama 安装失败");
                        var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();install.OllamaPort=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                    }
                    if(Paths.Probe(install.Ollama,"--version",10000)==null)throw new IOException("Ollama 环境验证失败");
                    Report("本机 AI 环境已就绪，模型可在软件设置中按需下载");
                }
                Phase("配置","正在保存设置与启动入口",93,7);token.ThrowIfCancellationRequested();
                var settingsPath=Path.Combine(install.Data,"settings.json");
                var settings=File.Exists(settingsPath)?Paths.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(settingsPath)):new Dictionary<string,object>();
                if(!settings.ContainsKey("StorageRoot"))settings["StorageRoot"]=install.Data;
                if(!settings.ContainsKey("LocalOutputRoot"))settings["LocalOutputRoot"]=Path.Combine(install.Data,"exports");
                settings["FfmpegPath"]=install.Ffmpeg;settings["PythonPath"]=install.Python??"python";
                if(!settings.ContainsKey("AiModel"))settings["AiModel"]="qwen2.5:1.5b";
                File.WriteAllText(settingsPath,Paths.Json.Serialize(settings),new UTF8Encoding(false));
                Directory.CreateDirectory(Path.Combine(install.Data,"exports"));Paths.Save(install);
                if(options.DesktopShortcut)MakeShortcut(install,Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"LiveClipper",false);
                if(options.StartMenu)
                {var menu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"LiveClipper");Directory.CreateDirectory(menu);MakeShortcut(install,menu,"LiveClipper",false);MakeShortcut(install,menu,"卸载 LiveClipper",true);}
                if(options.Register)Register(install);
                // 收紧 ACL：失败只记录，不让整个安装失败。
                try{Report("正在收紧安装目录权限（仅当前用户 / SYSTEM / 管理员）");Paths.HardenAcl(root);}
                catch(Exception ex){Report("警告：安装目录权限收紧失败（已跳过）："+ex.Message);}
                token.ThrowIfCancellationRequested();
                PayloadIntegrity.VerifyInstalled(install);
                install.State="complete";Paths.Save(install);CommitAppSwap(root);
                stage="完成";overall=100;Report("安装完成，可以启动软件",100,true,root);return install;
                }
                catch(Exception failure)
                {
                    // Includes OperationCanceledException and failures before the first rename.
                    try{RecoverAppSwap(root);}
                    catch(Exception recovery){throw new AggregateException("安装失败且回退尚未完成；请保留目录后重试修复",failure,recovery);}
                    throw;
                }
              }
            }
            finally{http.Dispose();}
        }

        /// <summary>所选目录里已解压好、且仍可用的私有环境（重复安装时直接复用，不重新下载）。</summary>
        string ExistingPrivate(string relative,string probeFile,string probeArguments)
        {
            var directory=Path.Combine(root,relative);if(!Directory.Exists(directory))return null;
            var file=Directory.EnumerateFiles(directory,probeFile,SearchOption.AllDirectories).FirstOrDefault();
            return file!=null&&Paths.Probe(file,probeArguments)!=null?file:null;
        }
        async Task InstallRuntimes(Installation install,InstallOptions options)
        {
            Phase("环境","准备 .NET 运行环境",5,15);
            // 自包含主程序已内置 .NET 运行时：无需下载，安装完成即可直接启动。
            if(File.Exists(Path.Combine(root,"app","self-contained.flag")))
            {install.Dotnet=null;Report(".NET 运行环境已随主程序内置，无需下载",-1,true,null);}
            else
            {
                var privateDotnet=ExistingPrivate(@"runtime\dotnet","dotnet.exe","--list-runtimes");
                install.Dotnet=options.ForcePrivateDotnet?privateDotnet??Path.Combine(root,@"runtime\dotnet"):DetectDotnet()??privateDotnet;
                if(privateDotnet!=null)Report("复用上次安装的 .NET 环境",-1,true,privateDotnet);
                else if(install.Dotnet==null)
                {
                    install.Dotnet=Path.Combine(root,@"runtime\dotnet");
                    foreach(var key in new[]{"desktop","aspnet"})
                    {var archive=await Download(key,key+"-runtime.zip","SHA512");Report("正在解压 .NET 环境");Paths.ExtractZip(archive,install.Dotnet);}
                    if(Paths.Probe(Path.Combine(install.Dotnet,"dotnet.exe"),"--list-runtimes")==null)throw new IOException(".NET 环境验证失败");
                }
                Report(".NET 运行环境已就绪",-1,true,install.Dotnet);
            }
            Phase("环境","准备 WebView2 浏览环境",20,12);
            var privateWebView=ExistingPrivate(@"runtime\webview2","msedgewebview2.exe","--version");
            if(privateWebView!=null){install.WebView=Path.GetDirectoryName(privateWebView);Report("复用上次安装的 WebView2");}
            else if(options.ForcePrivateWebView||DetectWebView()==null)
            {
                var cab=await Download("webview","webview2.cab");var directory=Path.Combine(root,@"runtime\webview2");Directory.CreateDirectory(directory);
                await Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"expand.exe"),"-F:* "+Paths.Quote(cab)+" "+Paths.Quote(directory));
                install.WebView=Path.GetDirectoryName(Directory.GetFiles(directory,"msedgewebview2.exe",SearchOption.AllDirectories).FirstOrDefault()??throw new IOException("WebView2 解压失败"));
                await Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"icacls.exe"),Paths.Quote(install.WebView)+" /grant *S-1-15-2-1:(OI)(CI)(RX) *S-1-15-2-2:(OI)(CI)(RX) /T /Q");
            }
            else Report("复用已有 WebView2");
            Phase("环境","准备 FFmpeg 视频工具",32,13);
            install.Ffmpeg=DetectFfmpeg()??ExistingPrivate(@"runtime\ffmpeg","ffmpeg.exe","-version");
            if(install.Ffmpeg!=null&&!Paths.Inside(root,install.Ffmpeg))Report("复用已有 FFmpeg",-1,true,install.Ffmpeg);
            if(install.Ffmpeg==null)
            {
                var zip=await Download("ffmpeg","ffmpeg.zip");Paths.ExtractZip(zip,Path.Combine(root,@"runtime\ffmpeg"));
                install.Ffmpeg=Directory.GetFiles(Path.Combine(root,@"runtime\ffmpeg"),"ffmpeg.exe",SearchOption.AllDirectories).FirstOrDefault();
                if(install.Ffmpeg==null||Paths.Probe(install.Ffmpeg,"-version")==null||Paths.Probe(Path.Combine(Path.GetDirectoryName(install.Ffmpeg),"ffprobe.exe"),"-version")==null)throw new IOException("FFmpeg 验证失败");
            }
            Report("FFmpeg 已就绪",-1,true,install.Ffmpeg);
        }

        async Task InstallPython(Installation install,InstallOptions options)
        {
            if(!HasVcRuntime())
            {
                var vc=await Download("vc","vc_redist.x64.exe");Report("正在安装 VC++ 系统组件",-1,true,"请完成 Windows 授权窗口");
                using(var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(vc,"/install /passive /norestart"){UseShellExecute=true,Verb="runas"}))
                {await Task.Run(()=>process.WaitForExit());token.ThrowIfCancellationRequested();if(process.ExitCode!=0&&process.ExitCode!=3010&&process.ExitCode!=1638)throw new IOException("VC++ 安装未完成（"+process.ExitCode+"）");}
                if(!HasVcRuntime())throw new IOException("VC++ 运行库尚未就绪，请完成官方安装器要求或重启后重试。");
            }
            var archive=await Download("uv","uv.zip");Paths.ExtractZip(archive,Path.Combine(root,@"runtime\uv"));
            var uv=Directory.GetFiles(Path.Combine(root,@"runtime\uv"),"uv.exe",SearchOption.AllDirectories).Single();
            var env=new Dictionary<string,string>{{"UV_CACHE_DIR",Path.Combine(cache,"uv")},{"UV_PYTHON_INSTALL_DIR",Path.Combine(root,@"runtime\python")},{"UV_NO_PROGRESS","1"},{"UV_SYSTEM_CERTS","true"},{"UV_HTTP_TIMEOUT","30"},{"UV_HTTP_RETRIES","1"}};
            var python=Paths.Probe(uv,"python find 3.11 --no-managed-python --no-python-downloads");
            // 重复安装时目录里已经有上次装好的 Python，直接用，不再下一遍。
            if(string.IsNullOrEmpty(python)||!File.Exists(python))python=ExistingPrivate(@"runtime\python","python.exe","-c \"import sys; print(sys.version)\"");
            if(python!=null)Report("复用上次安装的 Python 运行环境",-1,true,python);
            if(string.IsNullOrEmpty(python)||!File.Exists(python))
            {
                var args="python install 3.11 --no-bin --no-registry --install-dir "+Paths.Quote(env["UV_PYTHON_INSTALL_DIR"])+" --no-config";
                if(sources.China)env["UV_PYTHON_INSTALL_MIRROR"]=SourcePolicy.PythonChina;
                try{await Run(uv,args,env);}
                catch(IOException)when(sources.China){Report("国内 Python 环境源暂不可用，切换官方源");env.Remove("UV_PYTHON_INSTALL_MIRROR");await Run(uv,args,env);}
                python=Directory.GetFiles(env["UV_PYTHON_INSTALL_DIR"],"python.exe",SearchOption.AllDirectories).FirstOrDefault()??throw new IOException("Python 安装失败");
            }
            // venv 已存在时 uv 只做校验与保留；pip 也会跳过已装好的包。
            var venv=Path.Combine(root,@"runtime\venv");await Run(uv,"venv --python "+Paths.Quote(python)+" "+Paths.Quote(venv)+" --allow-existing --no-config",env);
            install.Python=Path.Combine(venv,@"Scripts\python.exe");
            if(options.Speech)
            {
                // 单进程发布把 ai-worker 放在 app\ai-worker；server\ 目录已不再生成。
                await Pip(uv,"pip install --python "+Paths.Quote(install.Python)+" -r "+Paths.Quote(Path.Combine(root,@"app\ai-worker\requirements.txt"))+" -r "+Paths.Quote(Path.Combine(root,@"app\ai-worker\editing-requirements.txt")),env);
                await Run(install.Python,"-c \"import faster_whisper, av, pyJianYingDraft; print('语音与草稿组件已就绪')\"");
                Report("语音模型在首次转写时按地区下载");
            }
            if(options.Demucs)
            {
                await Pip(uv,"pip install --python "+Paths.Quote(install.Python)+" torch==2.5.1+cpu torchaudio==2.5.1+cpu",env,true);
                await Pip(uv,"pip install --python "+Paths.Quote(install.Python)+" demucs==4.0.1 numpy==1.26.4",env);
                await Run(install.Python,"-c \"import demucs, torch, torchaudio, numpy as np; torch.from_numpy(np.zeros(8)).numpy(); print('人声分离组件已就绪')\"");
            }
        }
    }
}
