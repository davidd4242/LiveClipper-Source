using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace LiveClipper.Installation
{
    public sealed class Installation
    {
        public string AppId { get; set; } = Paths.AppId;
        public string Root { get; set; }
        public string Version { get; set; } = "1.0.0";
        public string State { get; set; } = "installing";
        public string PayloadManifestSha256 { get; set; }
        public string Dotnet { get; set; }
        public string Ffmpeg { get; set; }
        public string Python { get; set; }
        public string WebView { get; set; }
        public string Ollama { get; set; }
        public bool Speech { get; set; }
        public bool Demucs { get; set; }
        public bool LocalAi { get; set; }
        public int OllamaPort { get; set; } = 11434;
        public string DownloadRegion { get; set; }
        public List<string> Shortcuts { get; set; } = new List<string>();
        public string RegistryKey { get; set; }
        public string Data => Path.Combine(Root,"data");
    }
    // Shared with the net48 launcher. Expected file digests come from the embedded ZIP, never the extracted tree.
    public static class PayloadIntegrity
    {
        public const string ManifestName="payload-manifest.json";
        public sealed class Entry
        {
            public string Path {get;set;}
            public long Length {get;set;}
            public string Sha256 {get;set;}
        }
        public sealed class Manifest
        {
            public int Schema {get;set;}
            public string Version {get;set;}
            public string ArchiveSha256 {get;set;}
            public List<Entry> Files {get;set;}
        }
        public static bool IsHash(string value)=>value!=null&&value.Length==64&&value.All(c=>
            (c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F'));
        static string Hash(Stream stream)
        {
            using(var digest=System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(digest.ComputeHash(stream)).Replace("-","");
        }
        static string FileHash(string path)
        {
            Paths.NoLinks(path);
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))return Hash(stream);
        }
        static string Relative(string name)
        {
            if(string.IsNullOrEmpty(name)||Path.IsPathRooted(name)||name.Contains("\\")||name.Contains(":")||
                name.Any(c=>c<32)||name.IndexOfAny(new[]{'<','>','"','|','?','*'})>=0)
                throw new IOException("payload 清单包含无效路径："+name);
            foreach(var part in name.Split('/'))
            {
                var device=part.Split('.')[0];
                if(part.Length==0||part=="."||part==".."||part.EndsWith(".")||part.EndsWith(" ")||
                    new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
                        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(device,StringComparer.OrdinalIgnoreCase))
                    throw new IOException("payload 清单包含路径别名："+name);
            }
            if(name.Equals(ManifestName,StringComparison.OrdinalIgnoreCase)||name.Equals(ManifestName+".tmp",StringComparison.OrdinalIgnoreCase))
                throw new IOException("payload 使用了保留清单路径："+name);
            return name;
        }
        static Dictionary<string,Entry> Entries(Manifest manifest,string version)
        {
            if(manifest==null||manifest.Schema!=1||string.IsNullOrEmpty(version)||manifest.Version!=version||
                !IsHash(manifest.ArchiveSha256)||manifest.Files==null||manifest.Files.Count==0)
                throw new IOException("payload 清单缺失、版本不符或格式无效");
            var expected=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in manifest.Files)
            {
                if(entry==null||entry.Length<0||!IsHash(entry.Sha256))throw new IOException("payload 文件摘要无效");
                var name=Relative(entry.Path);
                if(expected.ContainsKey(name))throw new IOException("payload 包含重复路径："+name);
                expected.Add(name,entry);
            }
            if(!expected.ContainsKey("LiveClipper.Desktop.exe"))throw new IOException("payload 缺少 LiveClipper.Desktop.exe");
            return expected;
        }
        public static Manifest FromArchive(Stream archive,string version)
        {
            if(archive==null||!archive.CanSeek)throw new IOException("无法读取内嵌 payload");
            archive.Position=0;
            var manifest=new Manifest{Schema=1,Version=version,ArchiveSha256=Hash(archive),Files=new List<Entry>()};
            archive.Position=0;
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using(var zip=new ZipArchive(archive,ZipArchiveMode.Read,true))
            foreach(var entry in zip.Entries)
            {
                var directory=entry.FullName.EndsWith("/");
                var name=Relative(directory?entry.FullName.Substring(0,entry.FullName.Length-1):entry.FullName);
                if(!names.Add(name))throw new IOException("payload 包含重复路径："+name);
                if(directory)continue;
                using(var input=entry.Open())manifest.Files.Add(new Entry{Path=name,Length=entry.Length,Sha256=Hash(input)});
            }
            Entries(manifest,version);
            return manifest;
        }
        public static void VerifyArchive(string archive,Manifest manifest)
        {
            if(manifest==null||!IsHash(manifest.ArchiveSha256)||
                !FileHash(archive).Equals(manifest.ArchiveSha256,StringComparison.OrdinalIgnoreCase))
                throw new IOException("payload 归档 SHA256 不匹配");
        }
        static void Inventory(string directory,string relative,Dictionary<string,string> files)
        {
            var path=relative.Length==0?directory:Path.Combine(directory,relative);Paths.NoLinks(path);
            foreach(var child in Directory.EnumerateFileSystemEntries(path,"*",SearchOption.TopDirectoryOnly))
            {
                Paths.NoLinks(child);
                var name=relative.Length==0?Path.GetFileName(child):relative+"/"+Path.GetFileName(child);
                if(Directory.Exists(child)){Relative(name);Inventory(directory,name,files);}
                else if(!name.Equals(ManifestName,StringComparison.OrdinalIgnoreCase))
                {
                    Relative(name);
                    if(files.ContainsKey(name))throw new IOException("payload 重复文件："+name);
                    files.Add(name,child);
                }
            }
        }
        static void VerifyFiles(string directory,Manifest manifest,string version)
        {
            Paths.NoLinks(directory);
            var expected=Entries(manifest,version);var actual=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            Inventory(directory,"",actual);
            if(expected.Count!=actual.Count)throw new IOException("payload 文件集合不匹配");
            foreach(var pair in expected)
            {
                string path;if(!actual.TryGetValue(pair.Key,out path))throw new IOException("payload 缺少文件："+pair.Key);
                var entry=pair.Value;Paths.NoLinks(path);
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                    if(entry.Length!=stream.Length||!Hash(stream).Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase))
                        throw new IOException("payload 文件 SHA256 不匹配："+pair.Key);
            }
        }
        public static string WriteManifest(string directory,Manifest manifest)
        {
            VerifyFiles(directory,manifest,manifest.Version);
            var path=Path.Combine(directory,ManifestName);
            Paths.SaveJsonAtomic(path,manifest);
            return VerifyDirectory(directory,manifest.Version);
        }
        public static string VerifyDirectory(string directory,string version,string expectedManifestHash=null)
        {
            var path=Path.Combine(directory,ManifestName);Paths.NoLinks(path);
            byte[] bytes;
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var memory=new MemoryStream()){stream.CopyTo(memory);bytes=memory.ToArray();}
            string hash;using(var input=new MemoryStream(bytes))hash=Hash(input);
            if(expectedManifestHash!=null&&(!IsHash(expectedManifestHash)||!hash.Equals(expectedManifestHash,StringComparison.OrdinalIgnoreCase)))
                throw new IOException("payload 清单 SHA256 不匹配");
            Manifest manifest;
            try{manifest=Paths.Json.Deserialize<Manifest>(new UTF8Encoding(false,true).GetString(bytes));}
            catch(Exception error)when(error is ArgumentException||error is InvalidOperationException||error is DecoderFallbackException)
            {throw new IOException("payload 清单无法解析",error);}
            VerifyFiles(directory,manifest,version);
            return hash;
        }
        public static void VerifyInstalled(Installation install)
        {
            if(!IsHash(install.PayloadManifestSha256))throw new IOException("安装缺少 payload SHA256 记录，请重新安装修复。");
            VerifyDirectory(Path.Combine(install.Root,"app"),install.Version,install.PayloadManifestSha256);
        }
    }
    // Shared with the small root launcher; it must not reference the installer-only Engine.
    public static class AppSwap
    {
        public const string Marker="app-swap.pending.json";
        public sealed class Journal
        {
            public int Schema {get;set;}
            public string AppId {get;set;}
            public string Root {get;set;}
            public string Version {get;set;}
            public string PreviousVersion {get;set;}
            public string PreviousState {get;set;}
            public string PreviousPayloadManifestSha256 {get;set;}
            public string PayloadManifestSha256 {get;set;}
            public bool? HadApp {get;set;}
            public string Phase {get;set;}
            public string Step {get;set;}
        }
        // Fault-injection hook: assigned only by C# crash-point tests; production leaves it null (?.Invoke is a no-op).
#pragma warning disable CS0649
        internal static Action<string> Checkpoint;
#pragma warning restore CS0649
        public static FileStream AcquireLease(string root)
        {
            root=Paths.ValidateRoot(root);
            var path=Path.Combine(root,"installation.lock");Paths.NoLinks(path);
            return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,
                FileShare.None,4096,FileOptions.DeleteOnClose);
        }
        public static bool HasRecovery(string root)=>File.Exists(Path.Combine(root,Marker))||
            Directory.Exists(Path.Combine(root,Marker))||Directory.Exists(Path.Combine(root,"app.bak"))||File.Exists(Path.Combine(root,"app.bak"));
        public static bool HasDesktop(string directory)
        {
            var exe=Path.Combine(directory,"LiveClipper.Desktop.exe");Paths.NoLinks(exe);
            return Directory.Exists(directory)&&File.Exists(exe);
        }
        public static void EnsureNoAppProcesses(string root)
        {
            foreach(var process in Process.GetProcesses())
            using(process)
            {
                string exe;
                try{if(process.Id==Process.GetCurrentProcess().Id||process.HasExited)continue;exe=process.MainModule.FileName;}
                catch(System.ComponentModel.Win32Exception){continue;}
                catch(InvalidOperationException){continue;}
                if(Paths.Inside(Path.Combine(root,"app"),exe)||Paths.Inside(Path.Combine(root,"app.bak"),exe))
                    throw new IOException("主程序仍在运行，已停止换包/恢复；请先正常退出程序。PID="+process.Id);
            }
        }
        static string CheckPaths(string root)
        {
            root=Paths.ValidateRoot(root);
            foreach(var name in new[]{"app","app-update","app.bak",Marker,Marker+".tmp","installation.json","installation.json.tmp"})
                Paths.NoLinks(Path.Combine(root,name));
            return root;
        }
        static Journal Read(string root)
        {
            var journal=Paths.Json.Deserialize<Journal>(File.ReadAllText(Path.Combine(root,Marker)));
            if(journal==null||journal.Schema!=1||journal.AppId!=Paths.AppId||journal.HadApp==null||
                string.IsNullOrEmpty(journal.Root)||!Paths.Full(journal.Root).Equals(root,StringComparison.OrdinalIgnoreCase)||
                string.IsNullOrEmpty(journal.Version)||string.IsNullOrEmpty(journal.PreviousVersion)||
                (journal.PreviousState!="complete"&&journal.PreviousState!="installing"&&journal.PreviousState!="failed"&&journal.PreviousState!="uninstalled-data-retained")||
                (journal.Phase!="pending"&&journal.Phase!="committed")||
                (journal.Step!="prepared"&&journal.Step!="swap"&&journal.Step!="rollback")||
                (journal.Phase=="committed"&&journal.Step!="swap")||
                (journal.PreviousState=="complete"&&journal.HadApp!=true))
                throw new IOException("程序替换记录无效，已保留现场");
            return journal;
        }
        static void Save(string root,Journal journal)=>Paths.SaveJsonAtomic(Path.Combine(root,Marker),journal);
        // Callers hold the lease across begin, staging, all post-swap work and commit/recovery.
        public static void Begin(string root,string version)
        {
            root=CheckPaths(root);
            if(HasRecovery(root))throw new IOException("上次程序替换尚未恢复，已保留现场");
            var install=Paths.Load(root);var target=Path.Combine(root,"app");
            if(File.Exists(target)||string.IsNullOrEmpty(version)||string.IsNullOrEmpty(install.Version)||
                (install.State!="complete"&&install.State!="installing"&&install.State!="failed"&&install.State!="uninstalled-data-retained"))
                throw new IOException("程序替换路径、状态或版本无效");
            var hadApp=Directory.Exists(target);
            if((hadApp&&!HasDesktop(target))||(!hadApp&&install.State=="complete"))
                throw new IOException("原程序不完整，已停止替换并保留现场");
            var journal=new Journal{Schema=1,AppId=Paths.AppId,Root=root,Version=version,
                PreviousVersion=install.Version,PreviousState=install.State,PreviousPayloadManifestSha256=install.PayloadManifestSha256,
                HadApp=hadApp,Phase="pending",Step="prepared"};
            Save(root,journal);
            Checkpoint?.Invoke("pending-written");
            install.State="installing";install.Version=version;Paths.Save(install);
        }
        public static void Stage(string root)
        {
            root=CheckPaths(root);
            var target=Path.Combine(root,"app");var staging=Path.Combine(root,"app-update");var backup=Path.Combine(root,"app.bak");
            var journal=Read(root);
            if(journal.Phase!="pending"||journal.Step!="prepared")throw new IOException("程序替换阶段不匹配，已保留现场");
            try
            {
                if(!HasDesktop(staging))throw new IOException("更新文件不完整，已保留原程序");
                journal.PayloadManifestSha256=PayloadIntegrity.VerifyDirectory(staging,journal.Version);
                if(File.Exists(target)||File.Exists(backup)||Directory.Exists(backup)||Directory.Exists(target)!=journal.HadApp.Value)
                    throw new IOException("程序目录已发生变化，已停止替换");
                EnsureNoAppProcesses(root);
                journal.Step="swap";Save(root,journal);
                Checkpoint?.Invoke("swap-intent-written");
                if(journal.HadApp.Value)Directory.Move(target,backup);
                Checkpoint?.Invoke("old-renamed");
                Directory.Move(staging,target);
                Checkpoint?.Invoke("new-renamed");
                if(!HasDesktop(target))throw new IOException("换包后缺少 LiveClipper.Desktop.exe");
                PayloadIntegrity.VerifyDirectory(target,journal.Version,journal.PayloadManifestSha256);
                var install=Paths.Load(root);install.PayloadManifestSha256=journal.PayloadManifestSha256;Paths.Save(install);
            }
            catch(Exception failure)
            {
                try{Recover(root);}
                catch(Exception recovery){throw new AggregateException("程序替换与回退均失败；已保留备份及恢复记录",failure,recovery);}
                throw;
            }
        }
        static void ClearJournal(string root)
        {
            Paths.DeleteOwned(root,Marker+".tmp");
            File.Delete(Path.Combine(root,Marker));
        }
        static void FinishCommitted(string root,Journal journal,Installation install)
        {
            if(install.State!="complete"||install.Version!=journal.Version||!HasDesktop(Path.Combine(root,"app")))
                throw new IOException("已提交的程序替换与安装记录不符，已保留现场");
            if(install.PayloadManifestSha256!=journal.PayloadManifestSha256)throw new IOException("已提交的 payload 清单记录不一致");
            PayloadIntegrity.VerifyInstalled(install);
            // A partially deleted backup is never a rollback source after committed is durable.
            Paths.DeleteOwned(root,"app.bak");
            ClearJournal(root);
            try{Paths.DeleteOwned(root,"app-update");}catch{}
        }
        public static void Commit(string root)
        {
            root=CheckPaths(root);var journal=Read(root);var install=Paths.Load(root);
            if(journal.Phase!="pending"||journal.Step!="swap"||install.State!="complete"||
                install.Version!=journal.Version||!HasDesktop(Path.Combine(root,"app")))
                throw new IOException("程序替换不能提交：安装尚未完整完成");
            if(install.PayloadManifestSha256!=journal.PayloadManifestSha256)throw new IOException("payload 清单记录不一致，不能提交");
            PayloadIntegrity.VerifyInstalled(install);
            journal.Phase="committed";Save(root,journal);
            Checkpoint?.Invoke("committed-written");
            FinishCommitted(root,journal,install);
        }
        public static void Recover(string root)
        {
            root=CheckPaths(root);
            var target=Path.Combine(root,"app");var backup=Path.Combine(root,"app.bak");var staging=Path.Combine(root,"app-update");
            if(!File.Exists(Path.Combine(root,Marker)))
            {
                if(HasRecovery(root))throw new IOException("发现没有恢复记录的 app.bak，已保留现场");
                return;
            }
            var journal=Read(root);var install=Paths.Load(root);
            if(journal.Phase=="committed"){FinishCommitted(root,journal,install);return;}
            if(File.Exists(target)||File.Exists(backup))throw new IOException("程序恢复路径不是目录，已保留现场");
            var hadApp=journal.HadApp.Value;
            if(journal.Step=="prepared"&&(Directory.Exists(backup)||(!hadApp&&Directory.Exists(target))))
                throw new IOException("准备阶段目录已发生变化，已保留现场");
            if(journal.Step=="swap"&&hadApp&&!Directory.Exists(backup)&&!Directory.Exists(staging))
                throw new IOException("缺少备份与暂存目录，无法确认旧程序，已保留现场");
            if(journal.Step=="swap"&&Directory.Exists(target)&&Directory.Exists(staging)&&(!hadApp||Directory.Exists(backup)))
                throw new IOException("目标与暂存目录同时存在，无法确认目标来源，已保留现场");
            if(hadApp)
            {
                if(Directory.Exists(backup)?!HasDesktop(backup):!HasDesktop(target))
                    throw new IOException("原程序或备份不完整，已保留现场");
            }
            else if(Directory.Exists(backup))throw new IOException("全新安装出现意外备份，已保留现场");
            if(Directory.Exists(backup)||(!hadApp&&Directory.Exists(target)))EnsureNoAppProcesses(root);
            // Persist rollback intent before touching the target: recovery after backup->app is idempotent.
            journal.Step="rollback";Save(root,journal);
            if(hadApp&&Directory.Exists(backup))
            {
                Paths.DeleteOwned(root,"app");
                Directory.Move(backup,target);
                Checkpoint?.Invoke("rollback-restored");
            }
            else if(!hadApp)Paths.DeleteOwned(root,"app");
            if(hadApp&&!HasDesktop(target))throw new IOException("回滚后的主程序不完整，已保留恢复记录");
            // App-only rollback: runtime, settings, shortcuts and registry are not snapshotted.
            install.Version=journal.PreviousVersion;
            install.PayloadManifestSha256=journal.PreviousPayloadManifestSha256;
            install.State=journal.PreviousState=="installing"?"failed":journal.PreviousState;
            Paths.Save(install);
            Checkpoint?.Invoke("rollback-manifest-saved");
            ClearJournal(root);
            try{Paths.DeleteOwned(root,"app-update");}catch{}
        }
    }
    public static class Paths
    {
        public const string AppId="LiveClipper.UserInstall.v1";
        public static readonly JavaScriptSerializer Json=new JavaScriptSerializer { MaxJsonLength=16*1024*1024 };
        public static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        public static string OptionalFull(string path)
        {
            if(string.IsNullOrWhiteSpace(path))return null;
            try
            {
                path=Environment.ExpandEnvironmentVariables(path.Trim());
                if(path.Length>=2&&path[0]=='"'&&path[path.Length-1]=='"')path=path.Substring(1,path.Length-2);
                if(!Path.IsPathRooted(path))return null;
                return Path.GetFullPath(path);
            }
            catch(ArgumentException){return null;}
            catch(NotSupportedException){return null;}
            catch(IOException){return null;}
        }
        public static string OptionalParent(string path)
        {
            var full=OptionalFull(path);
            return full==null?null:Path.GetDirectoryName(full);
        }
        public static bool Inside(string root,string path) => Full(path).StartsWith(Full(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        public static void NoLinks(string path)
        {
            for(var p=Full(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
                if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("安装路径包含链接或联接，请选择普通文件夹："+p);
        }
        public static string ValidateRoot(string root)
        {
            if(string.IsNullOrWhiteSpace(root))throw new IOException("请选择安装位置");
            var raw=root.Trim();
            if(raw.StartsWith(@"\\"))throw new IOException("请选择本机磁盘上的文件夹");
            // 必须是 X:\... 形式的绝对路径；相对路径、\foo、C:foo 都会按当前目录/当前驱动器解析，一律拒绝。
            if(raw.Length<3||!char.IsLetter(raw[0])||raw[1]!=':'||(raw[2]!='\\'&&raw[2]!='/'))throw new IOException(@"安装位置必须是完整的绝对路径（例如 D:\LiveClipper），不能使用相对路径："+raw);
            root=Full(raw);if(root.StartsWith(@"\\"))throw new IOException("请选择本机磁盘上的文件夹");
            try{if(new DriveInfo(root.Substring(0,1)).DriveType==DriveType.Network)throw new IOException("不支持安装到映射的网络驱动器，请选择本机磁盘上的文件夹");}
            catch(ArgumentException){}
            if(root==Path.GetPathRoot(root).TrimEnd('\\')||root.Length<4)throw new IOException("不能直接安装到磁盘根目录");
            var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var forbidden=new[]{windows,Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)};
            foreach(var directory in forbidden.Where(x=>!string.IsNullOrEmpty(x)))
                if(root.Equals(Full(directory),StringComparison.OrdinalIgnoreCase)||Inside(root,directory))
                    throw new IOException("请选择独立的软件文件夹，不能使用系统目录或用户目录本身");
            // Program Files 及其子目录：asInvoker 的用户级安装器没有写权限，装进去只会半途失败。
            foreach(var pf in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetEnvironmentVariable("ProgramW6432")}.Where(x=>!string.IsNullOrEmpty(x)))
                if(Inside(pf,root))throw new IOException("不能安装到 Program Files 目录下，请选择普通的用户可写文件夹");
            // Windows 目录可能取不到（返回空），此时跳过该判断，而不是抛参数异常。
            if(!string.IsNullOrEmpty(windows)&&Inside(windows,root))
                throw new IOException("请选择独立的软件文件夹，不能使用系统目录或用户目录本身");
            NoLinks(root);return root;
        }
        /// <summary>
        /// 安装完成后收紧安装根目录 ACL（最小惊讶策略）：
        /// 停用继承（保留已继承 ACE 的拷贝）后，只保留四条允许 ACE：
        ///   当前用户 / SYSTEM / Administrators → FullControl（可维护、可卸载、可自动更新）；
        ///   BUILTIN\\Users → 只读执行（UsersReadExecRights = 0x1200A9，见下方常量说明；不给写/删/改 ACL）。
        /// 其余（含 Authenticated Users / Everyone）的 ACE 全部移除，继承保持断开。
        /// 理由：这是 per-user 安装（装到用户自己选的文件夹，不经 UAC），但同一台机器上的
        /// 其他登录用户本来就能进入 F:\\ 这类数据盘；把整棵树收成"只有我能读"会让别人连
        /// 双击 exe、看安装说明都失败，属于过度收紧、与用户对普通文件夹的预期不符。
        /// 显式给 Users 只读执行既保住了"别人改不了安装目录"的安全意图，又保住了
        /// 同机多用户可读的可用性——写权限一家独有，读权限保持常见文件夹的默认语义。
        /// 用 .NET DirectorySecurity 实现，不调用 icacls。已有的显式子目录 ACE
        /// （如 WebView2 的 AppContainer 读权限）不受影响。
        /// </summary>
        public static void HardenAcl(string root)
        {
            var me=System.Security.Principal.WindowsIdentity.GetCurrent().User;
            var fullControl=new[]{me,new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid,null),new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid,null)};
            var readOnly=new[]{new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid,null)};
            var keep=fullControl.Concat(readOnly).ToArray();
            var inherit=System.Security.AccessControl.InheritanceFlags.ContainerInherit|System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            var info=new DirectoryInfo(root);
            var security=info.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            security.SetAccessRuleProtection(true,true);
            info.SetAccessControl(security);   // 先落盘：继承 ACE 变成显式 ACE
            security=info.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            // 先移除所有不在 keep 里的身份（含旧的 Users 写 ACE），再统一按下面的权限重加，避免残留放权。
            foreach(System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true,false,typeof(System.Security.Principal.SecurityIdentifier)))
                if(!keep.Contains((System.Security.Principal.SecurityIdentifier)rule.IdentityReference))security.RemoveAccessRuleSpecific(rule);
            foreach(var sid in fullControl)
                security.SetAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,System.Security.AccessControl.FileSystemRights.FullControl,inherit,System.Security.AccessControl.PropagationFlags.None,System.Security.AccessControl.AccessControlType.Allow));
            foreach(var sid in readOnly)
                security.SetAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,UsersReadExecRights,inherit,System.Security.AccessControl.PropagationFlags.None,System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        /// <summary>
        /// BUILTIN\\Users 的只读执行权限：Read + ExecuteFile + ReadAttributes + ReadExtendedAttributes +
        /// ReadPermissions + Synchronize。
        /// 为什么这里写显式掩码而不用 FileSystemRights.ReadAndExecute：
        /// ReadAndExecute 落到盘上就是 (RX)（AddAccessRule 与 SetAccessRule 都一样，Windows 不会自动补位）；
        /// (RX) 不带 WRITE_DAC，本身没有问题。真正踩过的坑在更早的那版代码：它用 AddAccessRule 追加，
        /// 而目录上已经有一条同 SID 的 ACE（从 F:\\ 之类数据盘继承来的 Modify），两条 ACE 按"同 SID 取并集"
        /// 合并后写成一条 Allow 0x1301BF —— 那是 Modify 的掩码，等于给了"可删除 / 可改 ACL"，
        /// 与"不给写权限"矛盾（发布链的广泛写权限检查也会把它判成写）。改用 SetAccessRule 覆盖后不再合并，
        /// 就没有这个问题。所以本常量保留 0x1200A9 只是为了让"只读执行"在代码里语义明确、不用去反推 (RX) 展开成什么，
        /// 不是因为在修一个 Windows 自动补位的 bug。
        /// 掩码算术：0x1200A9 = FILE_GENERIC_READ(0x120089) | FILE_GENERIC_EXECUTE(0x1200A0)，
        /// 等价于 .NET 的 FileSystemRights.ReadAndExecute（落到盘上显示为 (RX)）。
        /// （旧注释把 0x1200A9 拆成 0x120000 | 0x1 是错的：0x120000 | 0x1 = 0x120001，不是 0x1200A9。）
        /// 两个 GENERIC_ 值各自还带着 READ_CONTROL(0x20000)、SYNCHRONIZE(0x100000) 与
        /// FILE_READ_ATTRIBUTES(0x80)；GENERIC_EXECUTE 额外含 FILE_EXECUTE(0x20)、
        /// GENERIC_READ 额外含 FILE_READ_DATA(0x1) / FILE_READ_EA(0x8)。不含任何 Delete / Write_* / Write_DAC 位。
        /// 因果：AddAccessRule 对同 SID 的规则是并集，SetAccessRule 是覆盖；因为这里必须用 SetAccessRule，
        /// 继承来的 Modify 才不会被合并进来（并集会写成 Allow 0x1301BF = Modify，等于可删、可改 ACL）。
        /// </summary>
        const System.Security.AccessControl.FileSystemRights UsersReadExecRights=
            System.Security.AccessControl.FileSystemRights.Read|System.Security.AccessControl.FileSystemRights.ExecuteFile
            |System.Security.AccessControl.FileSystemRights.ReadAttributes|System.Security.AccessControl.FileSystemRights.ReadExtendedAttributes
            |System.Security.AccessControl.FileSystemRights.ReadPermissions|System.Security.AccessControl.FileSystemRights.Synchronize;
        /// <summary>卸载时的临时副本目录（%TEMP%\LiveClipper-Uninstall-*）。</summary>
        public static bool IsUninstallCopy(string exePath)
        {
            try
            {
                var dir=Path.GetDirectoryName(Full(exePath));
                return Path.GetFileName(dir).StartsWith("LiveClipper-Uninstall-",StringComparison.OrdinalIgnoreCase)
                    &&Path.GetDirectoryName(dir).Equals(Full(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase);
            }catch{return false;}
        }
        /// <summary>
        /// 正在运行的 exe 无法删除自己所在目录：先直接尝试删，失败则起一个隐藏的 cmd，
        /// 等本进程退出后重试删除。任何一步失败都只吞掉，不影响卸载结果。
        /// </summary>
        public static void CleanupUninstallCopy(string exePath)
        {
            try
            {
                if(!IsUninstallCopy(exePath))return;
                var dir=Path.GetDirectoryName(Full(exePath));
                try{Directory.Delete(dir,true);return;}catch{}
                var script="for /l %i in (1,1,60) do @(rd /s /q \""+dir+"\" 2>nul & if not exist \""+dir+"\" exit /b 0 & ping -n 2 127.0.0.1 >nul)";
                Process.Start(new ProcessStartInfo("cmd.exe","/d /c \""+script+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetTempPath()});
            }catch{}
        }
        public static Installation Load(string root)
        {
            root=ValidateRoot(root);
            var manifest=Json.Deserialize<Installation>(File.ReadAllText(Path.Combine(root,"installation.json")));
            if(manifest==null||manifest.AppId!=AppId||!Full(manifest.Root).Equals(root,StringComparison.OrdinalIgnoreCase))throw new IOException("安装记录不匹配，已停止操作");
            return manifest;
        }
        public static void Save(Installation manifest)
        {
            NoLinks(manifest.Root);
            SaveJsonAtomic(Path.Combine(manifest.Root,"installation.json"),manifest);
        }
        // Fault-injection hook: assigned only by C# crash-point tests; production leaves it null (?.Invoke is a no-op).
#pragma warning disable CS0649
        internal static Action<string> AtomicWriteCheckpoint;
#pragma warning restore CS0649
        public static void SaveJsonAtomic(string path,object value)
        {
            NoLinks(path);NoLinks(path+".tmp");
            var bytes=new UTF8Encoding(false).GetBytes(Json.Serialize(value));
            using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None))
            {
                stream.Write(bytes,0,bytes.Length);
                stream.Flush(true);
            }
            AtomicWriteCheckpoint?.Invoke("temp-flushed");
            // Never unlink the authoritative name: a failed replacement keeps the previous JSON.
            if(File.Exists(path))File.Replace(path+".tmp",path,null);
            else File.Move(path+".tmp",path);
            AtomicWriteCheckpoint?.Invoke("published");
        }
        public static void DeleteOwned(string root,string name)
        {
            var target=Full(Path.Combine(root,name));if(!Inside(root,target))throw new IOException("删除路径超出安装目录");
            NoLinks(Path.GetDirectoryName(target));
            if((File.Exists(target)||Directory.Exists(target))&&(File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0)
            {
                // Delete the owned link itself with a non-recursive operation; never traverse its destination.
                if((File.GetAttributes(target)&FileAttributes.Directory)!=0)Directory.Delete(target,false);else File.Delete(target);
                return;
            }
            if(Directory.Exists(target))
            {
                // Every recursive target is checked to remain within the installation root.
                foreach(var child in Directory.EnumerateFileSystemEntries(target,"*",SearchOption.TopDirectoryOnly))
                    DeleteOwned(root,child.Substring(Full(root).Length+1));
                Directory.Delete(target);
            }
            else if(File.Exists(target)){File.SetAttributes(target,FileAttributes.Normal);File.Delete(target);}
        }
        public static void ExtractZip(string archive,string target)
        {
            NoLinks(target);Directory.CreateDirectory(target);
            using(var zip=ZipFile.OpenRead(archive))
                foreach(var entry in zip.Entries)
                {
                    var destination=Full(Path.Combine(target,entry.FullName));
                    if(!Inside(target,destination))throw new IOException("压缩包包含无效路径："+entry.FullName);
                    NoLinks(destination);
                    if(entry.FullName.EndsWith("/")||entry.FullName.EndsWith("\\")){Directory.CreateDirectory(destination);continue;}
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));entry.ExtractToFile(destination,true);
                }
        }
        public static string Quote(string value)
        {
            // CommandLineToArgvW escaping; never invoke a shell with these arguments.
            var output=new StringBuilder("\"");var slashes=0;
            foreach(var c in value){if(c=='\\'){slashes++;continue;}if(c=='"'){output.Append('\\',slashes*2+1).Append('"');slashes=0;continue;}output.Append('\\',slashes).Append(c);slashes=0;}
            return output.Append('\\',slashes*2).Append('"').ToString();
        }
        public static string Probe(string exe,string arguments,int timeout=5000)
        {
            try
            {
                using(var p=Process.Start(new ProcessStartInfo(exe,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
                {
                    var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
                    if(!p.WaitForExit(timeout)){try{p.Kill();}catch{}return null;}
                    return p.ExitCode==0?stdout.GetAwaiter().GetResult().Trim():null;
                }
            }
            catch{return null;}
        }
        public static string Find(string exe)
        {
            var paths=(Environment.GetEnvironmentVariable("PATH")??"").Split(';');
            foreach(var path in paths){try{var directory=OptionalFull(path);if(directory==null)continue;var full=Path.Combine(directory,exe);if(File.Exists(full))return full;}catch{}}
            return null;
        }
        public static ProcessStartInfo LaunchInfo(Installation install,bool server=false)
        {
            PayloadIntegrity.VerifyInstalled(install);
            var start=new ProcessStartInfo(Path.Combine(install.Root,"app",server?@"server\LiveClipper.App.exe":"LiveClipper.Desktop.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.Combine(install.Root,"app",server?"server":"")};
            start.EnvironmentVariables["LIVECLIPPER_CONFIG_DIR"]=install.Data;
            start.EnvironmentVariables["LIVECLIPPER_INSTALL_ROOT"]=install.Root;
            if(!string.IsNullOrEmpty(install.Dotnet)){start.EnvironmentVariables["DOTNET_ROOT_X64"]=install.Dotnet;start.EnvironmentVariables["DOTNET_ROOT"]=install.Dotnet;}
            if(!string.IsNullOrEmpty(install.WebView))start.EnvironmentVariables["WEBVIEW2_BROWSER_EXECUTABLE_FOLDER"]=install.WebView;
            start.EnvironmentVariables["HF_HOME"]=Path.Combine(install.Root,"runtime","models","huggingface");
            start.EnvironmentVariables["HF_HUB_CACHE"]=Path.Combine(install.Root,"runtime","models","huggingface","hub");
            start.EnvironmentVariables["LIVECLIPPER_WHISPER_SMALL"]=Path.Combine(install.Root,"runtime","models","whisper-small");
            start.EnvironmentVariables["TORCH_HOME"]=Path.Combine(install.Root,"runtime","models","torch");
            start.EnvironmentVariables["LIVECLIPPER_DOWNLOAD_REGION"]=install.DownloadRegion??"AUTO";
            if(install.DownloadRegion=="CN")start.EnvironmentVariables["HF_ENDPOINT"]="https://hf-mirror.com";
            if(!string.IsNullOrEmpty(install.Ffmpeg))start.EnvironmentVariables["PATH"]=Path.GetDirectoryName(install.Ffmpeg)+";"+start.EnvironmentVariables["PATH"];
            if(!string.IsNullOrEmpty(install.Ollama)&&Inside(install.Root,install.Ollama))
            {start.EnvironmentVariables["LIVECLIPPER_OLLAMA_EXE"]=install.Ollama;start.EnvironmentVariables["OLLAMA_MODELS"]=Path.Combine(install.Root,"runtime","models","ollama");start.EnvironmentVariables["LIVECLIPPER_OLLAMA_URL"]="http://127.0.0.1:"+install.OllamaPort+"/";}
            start.EnvironmentVariables["LIVECLIPPER_FFMPEG"]=install.Ffmpeg??"ffmpeg";
            start.EnvironmentVariables["LIVECLIPPER_PYTHON"]=install.Python??"python";
            return start;
        }
    }
}
