using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace LiveClipper.Installation
{
    static class SelfTest
    {
        public static void Run(string output)
        {
            var root=Path.Combine(Path.GetTempPath(),"LiveClipper-Safety-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var checks=0;
            Action<Action> reject=action=>{bool thrown=false;try{action();}catch(IOException){thrown=true;}if(!thrown)throw new Exception("安全校验未拒绝无效操作");checks++;};
            try
            {
                reject(()=>Paths.ValidateRoot(Path.GetPathRoot(root)));
                reject(()=>Paths.ValidateRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
                reject(()=>Paths.DeleteOwned(root,"..\\do-not-delete"));
                File.WriteAllText(Path.Combine(root,"installation.json"),"{\"AppId\":\"OtherApp\",\"Root\":\"wrong\"}");
                reject(()=>Paths.Load(root));
                var occupied=Path.Combine(root,"occupied");Directory.CreateDirectory(occupied);File.WriteAllText(Path.Combine(occupied,"original.txt"),"keep");
                reject(()=>new Engine(new Progress<ProgressUpdate>(),System.Threading.CancellationToken.None).Install(new InstallOptions{Root=occupied}).GetAwaiter().GetResult());
                if(File.ReadAllText(Path.Combine(occupied,"original.txt"))!="keep"||Directory.GetFileSystemEntries(occupied).Length!=1)throw new Exception("已占用目录被改写");checks++;
                var zip=Path.Combine(root,"hostile.zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){using(var writer=new StreamWriter(archive.CreateEntry("../outside.txt").Open()))writer.Write("no");}
                reject(()=>Paths.ExtractZip(zip,Path.Combine(root,"extract")));
                var install=new Installation{Root=root};Paths.Save(install);if(Paths.Load(root).Root!=root)throw new Exception("安装记录未能回读");checks++;
                var arguments=new[]{"simple","C:\\with spaces\\", "text\"quoted", "C:\\back\\slash"};
                foreach(var argument in arguments){var quoted=Paths.Quote(argument);if(!quoted.StartsWith("\"")||!quoted.EndsWith("\""))throw new Exception("参数转义失败");checks++;}
                File.WriteAllText(Path.Combine(root,"keep.txt"),"original");Directory.CreateDirectory(Path.Combine(root,"owned"));File.WriteAllText(Path.Combine(root,"owned","item.txt"),"remove");Paths.DeleteOwned(root,"owned");
                if(File.ReadAllText(Path.Combine(root,"keep.txt"))!="original")throw new Exception("删除波及外部数据");checks++;
                foreach(var path in new[]{null,"","   ","relative-folder","C:\\bad|folder"})
                {if(Paths.OptionalFull(path)!=null||Paths.OptionalParent(path)!=null)throw new Exception("无效可选路径未被忽略");checks++;}
                if(Paths.OptionalFull(" \""+root+"\" ")!=Path.GetFullPath(root))throw new Exception("带引号的环境路径未正确处理");checks++;
                var originalPath=Environment.GetEnvironmentVariable("PATH");var originalRoot=Environment.GetEnvironmentVariable("DOTNET_ROOT_X64");var originalGenericRoot=Environment.GetEnvironmentVariable("DOTNET_ROOT");
                try
                {
                    Environment.SetEnvironmentVariable("PATH",null);Environment.SetEnvironmentVariable("DOTNET_ROOT_X64","C:\\bad|folder");Environment.SetEnvironmentVariable("DOTNET_ROOT","relative-folder");
                    if(Paths.Find("dotnet.exe")!=null)throw new Exception("空 PATH 仍返回可执行文件");checks++;
                    Engine.DetectDotnet();Engine.Diagnose();checks++;
                }
                finally{Environment.SetEnvironmentVariable("PATH",originalPath);Environment.SetEnvironmentVariable("DOTNET_ROOT_X64",originalRoot);Environment.SetEnvironmentVariable("DOTNET_ROOT",originalGenericRoot);}
                checks+=DownloadChecks.Run(root);
                if(output!=null)File.WriteAllText(output,checks+" installer safety, routing and download checks passed");
            }
            finally{Paths.DeleteOwned(Path.GetDirectoryName(root),Path.GetFileName(root));}
        }
    }
}
