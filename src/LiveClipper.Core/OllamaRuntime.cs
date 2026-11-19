using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace LiveClipper.Core;

public sealed record OllamaRuntimeStatus(bool Ready,bool Installed,string? Executable,string? Version,string Detail);

public static class OllamaRuntime
{
    public static Uri BaseUri=>new(Environment.GetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL")??"http://127.0.0.1:11434/");
    private static readonly HttpClient Client=new(){Timeout=TimeSpan.FromSeconds(2)};
    private static readonly SemaphoreSlim StartGate=new(1,1);
    public static string? FindExecutable()
    {
        var paths=new List<string>{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Ollama","ollama.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Ollama","ollama.exe")};
        var privateExecutable=Environment.GetEnvironmentVariable("LIVECLIPPER_OLLAMA_EXE");if(!string.IsNullOrWhiteSpace(privateExecutable))paths.Insert(0,privateExecutable);
        foreach(var target in new[]{EnvironmentVariableTarget.Process,EnvironmentVariableTarget.User,EnvironmentVariableTarget.Machine})
            foreach(var directory in (Environment.GetEnvironmentVariable("PATH",target)??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries))
                try{paths.Add(Path.Combine(directory.Trim().Trim('"'),OperatingSystem.IsWindows()?"ollama.exe":"ollama"));}catch(ArgumentException){}
        if(OperatingSystem.IsWindows())paths.AddRange(WindowsInstallations());
        return paths.FirstOrDefault(File.Exists);
    }
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> WindowsInstallations()
    {
        var paths=new List<string>();
        foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine})
        {
            using var uninstall=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");if(uninstall is null)continue;
            foreach(var name in uninstall.GetSubKeyNames())
            {
                using var key=uninstall.OpenSubKey(name);
                if(key?.GetValue("DisplayName") is string display&&(display.Equals("Ollama",StringComparison.OrdinalIgnoreCase)||display.StartsWith("Ollama version ",StringComparison.OrdinalIgnoreCase))&&key.GetValue("InstallLocation") is string directory)
                    paths.Add(Path.Combine(directory,"ollama.exe"));
            }
        }
        return paths;
    }
    private static async Task<string?> VersionAsync(CancellationToken token)
    {
        try
        {
            using var response=await Client.GetAsync(new Uri(BaseUri,"api/version"),token);if(!response.IsSuccessStatusCode)return null;
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return json.RootElement.TryGetProperty("version",out var version)?version.GetString():null;
        }
        catch(HttpRequestException){return null;}
        catch(JsonException){return null;}
        catch(OperationCanceledException) when(!token.IsCancellationRequested){return null;}
    }
    public static async Task<OllamaRuntimeStatus> StatusAsync(CancellationToken token)
    {
        var executable=FindExecutable();var version=await VersionAsync(token);
        return new(version is not null,executable is not null,executable,version,version is not null?"Ollama 已连接 · "+version:executable is not null?"Ollama 已安装，服务尚未启动":"未安装 Ollama，请从官网安装后重试");
    }
    public static async Task EnsureAsync(CancellationToken token)
    {
        if(await VersionAsync(token) is not null)return;
        await StartGate.WaitAsync(token);
        try
        {
            if(await VersionAsync(token) is not null)return;
            var executable=FindExecutable()??throw new InvalidOperationException("本机尚未安装 Ollama。请在设置页打开 Ollama 官网安装；安装后点击检查并启动服务。");
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(executable)!};start.ArgumentList.Add("serve");
            start.Environment["OLLAMA_HOST"]=BaseUri.Authority;
            var models=Environment.GetEnvironmentVariable("OLLAMA_MODELS")??Environment.GetEnvironmentVariable("OLLAMA_MODELS",EnvironmentVariableTarget.User);if(!string.IsNullOrWhiteSpace(models))start.Environment["OLLAMA_MODELS"]=models;
            using var process=Process.Start(start)??throw new InvalidOperationException("Ollama 服务启动失败");
            var deadline=DateTimeOffset.UtcNow.AddSeconds(20);
            while(DateTimeOffset.UtcNow<deadline)
            {
                if(await VersionAsync(token) is not null)return;
                if(process.HasExited)throw new InvalidOperationException("Ollama 服务启动后退出，请检查 Ollama 日志或 11434 端口是否被其他程序占用");
                await Task.Delay(300,token);
            }
            throw new InvalidOperationException("Ollama 已启动但连接超时，请稍后点击检查并启动服务");
        }
        finally{StartGate.Release();}
    }
}
