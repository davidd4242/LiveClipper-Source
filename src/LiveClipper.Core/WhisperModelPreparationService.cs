using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveClipper.Core;

/// <summary>Whisper 模型准备任务的不可变快照。Status: idle/preparing/completed/failed/cancelled；没有真实进度就不给百分比。</summary>
public sealed record WhisperPrepareState(string Status,string Model,string? Revision,string Source,string? Phase,string? Detail,string? ModelPath);

/// <summary>单任务地调用 transcribe.py --prepare-only 准备模型；不触发转写。</summary>
public sealed class WhisperModelPreparationService(LiveSettings settings):IDisposable
{
    private const int StderrTailChars=4000;
    private readonly object _gate=new();
    private CancellationTokenSource? _stop;
    private bool _running;
    private WhisperPrepareState _state=new("idle","",null,"auto",null,null,null);

    public WhisperPrepareState State{get{lock(_gate)return _state;}}

    public bool StartPrepare(string source,string? revision)
    {
        if(source is not ("auto" or "official" or "mirror"))throw new ArgumentException("下载源只能是 auto、official 或 mirror");
        var model=settings.WhisperModel?.Trim()??"";
        if(model.Length==0)throw new ArgumentException("未配置 Whisper 模型");
        revision=string.IsNullOrWhiteSpace(revision)?null:revision.Trim();
        if(revision is not null&&!Regex.IsMatch(revision,@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$"))throw new ArgumentException("revision 无效");
        var python=settings.PythonPath;
        lock(_gate)
        {
            if(_running)return false;
            _running=true;
            _stop=new CancellationTokenSource();
            _state=new("preparing",model,revision,source,"starting","正在启动准备任务",null);
            var stop=_stop;
            _=Task.Run(()=>RunAsync(python,model,revision,source,stop));
            return true;
        }
    }

    public void CancelPrepare()
    {
        lock(_gate)_stop?.Cancel();
    }

    public void Dispose()=>CancelPrepare();

    private void Set(CancellationTokenSource owner,WhisperPrepareState next)
    {
        lock(_gate){if(ReferenceEquals(_stop,owner))_state=next;}
    }

    private async Task RunAsync(string python,string model,string? revision,string source,CancellationTokenSource stop)
    {
        var errors=new StringBuilder();
        Exception? cancelError=null;
        WhisperPrepareState Failed(string detail){var current=State;return new("failed",model,revision,current.Source,current.Phase,detail,null);}
        WhisperPrepareState CancelResult()
        {
            Exception? err;lock(errors)err=cancelError;
            return err is null?State with{Status="cancelled",Detail="已取消"}:Failed("取消失败，未能终止准备进程："+err.Message);
        }
        WhisperPrepareState final;
        try
        {
            var script=Environment.GetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT") is { Length: > 0 } overrideScript?overrideScript:Path.Combine(AppContext.BaseDirectory,"ai-worker","transcribe.py");
            var info=new ProcessStartInfo(python){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
            foreach(var arg in new[]{script,"--prepare-only","--model",model,"--device","cpu"})info.ArgumentList.Add(arg);
            if(revision is not null){info.ArgumentList.Add("--revision");info.ArgumentList.Add(revision);}
            if(source=="official"){info.Environment["HF_ENDPOINT"]="https://huggingface.co";info.Environment["LIVECLIPPER_HF_FALLBACK"]="1";}
            else if(source=="mirror"){info.Environment["HF_ENDPOINT"]="https://hf-mirror.com";info.Environment["LIVECLIPPER_HF_FALLBACK"]="1";}
            else info.Environment.Remove("LIVECLIPPER_HF_FALLBACK");

            using var process=Process.Start(info)??throw new InvalidOperationException($"无法启动 {python}");
            using var killOnCancel=stop.Token.Register(()=>
            {
                try{process.Kill(entireProcessTree:true);}
                catch(Exception ex)
                {
                    bool exited;try{exited=process.HasExited;}catch(InvalidOperationException){exited=true;}
                    if(exited)return; // 进程已自行退出：取消竞态，不是失败
                    lock(errors)cancelError=ex;
                    var current=State;
                    Set(stop,current with{Detail="取消失败，进程仍在运行："+ex.Message});
                }
            });
            var stderr=Task.Run(async()=>
            {
                while(await process.StandardError.ReadLineAsync() is { } line)
                {
                    if(TryStatus(line,out var phase,out var src,out var rev))
                    {
                        var current=State;
                        Set(stop,current with{Phase=phase??current.Phase,Source=src??current.Source,Revision=rev??current.Revision,Detail=phase is null?current.Detail:$"阶段：{phase}"});
                    }
                    else lock(errors){errors.AppendLine(line);if(errors.Length>StderrTailChars)errors.Remove(0,errors.Length-StderrTailChars);}
                }
            });
            var stdout=process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout,stderr);
            string tail;lock(errors)tail=errors.ToString().Trim();

            if(stop.IsCancellationRequested)final=CancelResult();
            else if(process.ExitCode!=0)final=Failed($"准备失败（退出码 {process.ExitCode}）"+(tail.Length>0?"：\n"+tail:""));
            else if(!TryReady(stdout.Result,model,revision,out var reportedRevision,out var path,out var why))
                final=Failed(why+(tail.Length>0?"：\n"+tail:""));
            else final=new("completed",model,reportedRevision,State.Source,"ready","模型已准备好",path);
        }
        catch(Exception ex)
        {
            string tail;lock(errors)tail=errors.ToString().Trim();
            final=stop.IsCancellationRequested?CancelResult():Failed(ex.Message+(tail.Length>0?"：\n"+tail:""));
        }
        // 终态写入与释放单任务在同一把锁内，旧任务不会覆盖新任务。
        lock(_gate)
        {
            if(ReferenceEquals(_stop,stop)){_state=final;_stop=null;_running=false;}
            stop.Dispose();
        }
    }

    private static bool TryStatus(string line,out string? phase,out string? source,out string? revision)
    {
        phase=source=revision=null;
        var text=line.Trim();
        if(!text.StartsWith('{'))return false;
        try
        {
            using var doc=JsonDocument.Parse(text);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||Str(root,"type")!="model-status")return false;
            phase=Str(root,"phase");source=Str(root,"source");revision=Str(root,"revision");
            return true;
        }
        catch(JsonException){return false;}
    }

    private static bool TryReady(string stdout,string model,string? revision,out string? reportedRevision,out string? path,out string why)
    {
        reportedRevision=path=null;
        var last=stdout.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).LastOrDefault();
        if(last is null){why="进程退出但没有输出模型确认消息";return false;}
        try
        {
            using var doc=JsonDocument.Parse(last);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||Str(root,"type")!="model-prepared"){why="最后一行输出不是 model-prepared 确认";return false;}
            if(Str(root,"model")!=model){why="确认的模型与请求不一致";return false;}
            reportedRevision=Str(root,"revision");
            if(revision is not null&&reportedRevision!=revision){why="确认的 revision 与请求不一致";return false;}
            if(!root.TryGetProperty("ready",out var ready)||ready.ValueKind!=JsonValueKind.True){why="worker 未确认 ready=true";return false;}
            path=Str(root,"model_path");
            if(string.IsNullOrWhiteSpace(path)){why="确认消息缺少 model_path";return false;}
            why="";return true;
        }
        catch(JsonException){why="模型确认消息不是有效 JSON";return false;}
    }

    private static string? Str(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
}
