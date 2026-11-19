using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LiveClipper.Core;

/// <summary>ApproxBytes: 0 on a non-builtin model means the download size is unknown (HF repo size not measured). CompatibilityVerified: true only after an actual local load+generate was run; metadata-only checks stay false.</summary>
public sealed record MusicModelInfo(string Id,string Name,string Engine,string Description,long ApproxBytes,string License,bool BuiltIn,bool CompatibilityVerified=false);
public sealed record MusicModelStatus(string Id,string Name,string Engine,string Description,long ApproxBytes,string License,bool BuiltIn,bool Installed,bool CompatibilityVerified=false);
public sealed record MusicDownloadState(string State,string ModelId,int Progress,string Detail,string? Error=null);
public sealed record MusicJobState(string Id,string Status,int Progress,string Detail,string? AssetId,string? Error,DateTimeOffset UpdatedAt,MusicBeatAnalysis? Analysis=null);
public sealed record MusicCreateRequest(string ModelId,string Genre,string Style,string Content,string Theme,int DurationSeconds,string Provider="local");
public sealed record MusicStatus(IReadOnlyList<MusicModelStatus> Models,MusicDownloadState Download,bool AllowDownload,string DefaultModelId,bool PythonAvailable,string EngineNote,string GenerationProvider="local",bool HttpConfigured=false,string ApiModel="",bool DownloadBusy=false,bool LocalModelBusy=false);
/// <summary>One immutable, same-gate capture of the online music settings. StoredKey is the raw protected ciphertext; ToString never prints fields.</summary>
public sealed class MusicOnlineConnection(string provider,string url,string model,string authMode,string storedKey)
{
    public string Provider{get;}=provider;
    public string Url{get;}=url;
    public string Model{get;}=model;
    public string AuthMode{get;}=authMode;
    public string StoredKey{get;}=storedKey;
    public override string ToString()=>nameof(MusicOnlineConnection);
}
/// <summary>音乐分析结果：BPM/beat 时间点与响度测量。任一字段不可得时为 null，生成流程不得因此失败。</summary>
public sealed record MusicBeatAnalysis(double? Bpm,double[] Beats,string Method,double Confidence,double DurationSeconds,double? MeasuredLufs=null,double? TargetLufs=null,string? LoudnessMethod=null,DateTimeOffset? AnalyzedAt=null);
/// <summary>按节拍计算的循环/裁切点（秒，相对素材起点）。</summary>
public sealed record MusicLoopSuggestion(double Start,double End,double Seconds,int BeatCount,int BarCount,double Bpm,string Method);
/// <summary>人声区间（秒，与转写共用同一时间轴）。</summary>
public sealed record MusicVoiceInterval(double Start,double End);
/// <summary>一键 ducking 请求参数。TargetDb 为压低后的绝对增益（dB）。</summary>
public sealed record MusicDuckingRequest(double TargetDb=-14,double AttackMs=120,double ReleaseMs=280);
/// <summary>ducking 包络建议，持久化在 studio_documents（scope=music-ducking，key=clipId）。</summary>
public sealed record MusicDuckingEnvelope(string ClipId,string? MusicAssetId,string Target,double TargetDb,MusicVoiceInterval[] VoiceIntervals,VolumeKeyframe[] Keyframes,string[] AppliedItemIds,string[] Warnings,DateTimeOffset CreatedAt);

/// <summary>
/// 本地音乐生成的可选组件：内置合成器永远可用；MusicGen 模型由用户明确允许后下载，
/// 工作进程与依赖单独放在 StorageRoot/runtime/music-venv，避免污染系统 Python。
/// </summary>
public sealed partial class MusicGenerationService(LiveSettings settings,AudioLibraryService audio,StudioStore? studio=null,MediaTools? media=null)
{
    public const string BuiltInModelId="liveclipper-builtin";
    public const string MusicGenModelId="facebook/musicgen-small";

    private const string MusicGenLicense="CC BY-NC 4.0：模型权重须署名，仅限非商业用途；商业使用须另获权利人许可。";
    private static readonly MusicModelInfo[] Catalog=
    [
        new(BuiltInModelId,"内置原创合成器","builtin","无需下载，直接按曲风、主题和内容生成原创背景音乐。",0,"LiveClipper 内置原创",true),
        new(MusicGenModelId,"MusicGen Small","musicgen","Meta 文本到音乐模型，本地推理；实际下载体积以模型文件为准，可能包括依赖；首次使用需安装 Python 运行依赖。",0,MusicGenLicense,false),
        // HF API metadata (library_name=transformers, architectures=MusicgenForConditionalGeneration, same as small) matches the worker loader; never loaded here, sizes not measured.
        new("facebook/musicgen-medium","MusicGen Medium","musicgen","Meta 文本到音乐模型（中等规模），与 Small 使用同一 transformers MusicGen 加载方式；下载体积与显存/内存需求未测量，兼容性未在本机验证。",0,MusicGenLicense,false),
        new("facebook/musicgen-large","MusicGen Large","musicgen","Meta 文本到音乐模型（大规模，权重分片），与 Small 使用同一 transformers MusicGen 加载方式；下载体积与显存/内存需求未测量，兼容性未在本机验证。",0,MusicGenLicense,false)
    ];

    private readonly ConcurrentDictionary<string,MusicJobState> _jobs=new();
    private readonly object _gate=new();
    private MusicDownloadState _download=new("idle","",0,"尚未下载本地音乐模型");
    private CancellationTokenSource? _downloadStop;
    private CancellationTokenSource? _generationStop;
    private Task? _downloadTask;
    private Task? _generationTask;
    private bool _generationLocal;

    public MusicDownloadState DownloadState { get { lock(_gate)return _download; } }
    public IReadOnlyList<MusicModelInfo> Models=>Catalog;

    public MusicStatus Status()
    {
        var models=AvailableModels().Select(model=>new MusicModelStatus(model.Id,model.Name,model.Engine,model.Description,model.ApproxBytes,model.License,model.BuiltIn,IsModelInstalled(model.Id),model.CompatibilityVerified)).ToArray();
        bool downloadBusy,localBusy;
        lock(_gate){downloadBusy=_downloadStop is not null;localBusy=_generationLocal&&_generationTask is {IsCompleted:false};}
        return new(models,DownloadState,settings.MusicModelDownloadAllowed,settings.MusicModelId,PythonAvailable(),"内置合成器无需下载；AI 模型实际下载体积以模型文件为准，可能包括依赖，生成上限 30 秒。",settings.MusicGenerationProvider,HttpConfigured(),settings.MusicApiModel??"",downloadBusy,localBusy);
    }

    private bool HttpConfigured()
    {
        var key=settings.MusicApiKey;var auth=settings.MusicApiAuthMode;
        return settings.MusicGenerationProvider=="custom-http"&&!string.IsNullOrWhiteSpace(settings.MusicApiUrl)&&!string.IsNullOrWhiteSpace(settings.MusicApiModel)
            &&(auth!="bearer"||!string.IsNullOrWhiteSpace(key));
    }

    private sealed record OnlineConfig(string Url,string Model,string? Bearer);

    private static OnlineConfig CaptureOnlineConfig(MusicOnlineConnection snapshot)
    {
        // The snapshot was taken by the caller under the settings gate; nothing here reads live settings.
        var provider=snapshot.Provider;var url=(snapshot.Url??"").Trim();var model=(snapshot.Model??"").Trim();
        var auth=snapshot.AuthMode;var stored=snapshot.StoredKey??"";
        if(provider!="custom-http")throw new ArgumentException("尚未启用自定义在线音乐接口，请先在设置中保存配置");
        if(url.Length==0||model.Length==0)throw new ArgumentException("在线音乐接口地址或模型名未配置");
        string? bearer=null;
        if(auth=="bearer")
        {
            try{bearer=CookieProtector.Unprotect(stored);}catch{bearer=null;}
            if(string.IsNullOrWhiteSpace(bearer))throw new ArgumentException("在线音乐接口密钥无法读取，请在设置中重新保存密钥");
        }
        else if(auth!="none")throw new ArgumentException("在线音乐接口鉴权方式无效");
        return new(url,model,bearer);
    }

    public void SetPreferences(bool allowDownload,string modelId)
    {
        if(FindModel(modelId) is null)throw new ArgumentException("未知的音乐模型");
        settings.MusicModelDownloadAllowed=allowDownload;
        settings.MusicModelId=modelId;
    }

    public MusicDownloadState StartModelDownload(string modelId,bool allowDownload)
    {
        var model=FindModel(modelId)??throw new ArgumentException("未知的音乐模型");
        if(model.Id==ExternalId)throw new InvalidOperationException("外部目录不通过软件下载");
        if(model.BuiltIn)throw new InvalidOperationException("内置合成器无需下载模型");
        if(!allowDownload)throw new InvalidOperationException("请先勾选“允许下载本地音乐模型”");
        lock(_gate)
        {
            if(_downloadStop is not null)return _download;
            // Same gate as StartGeneration: a running local generation (worker/venv) excludes a download.
            if(_generationLocal&&_generationTask is {IsCompleted:false})throw new InvalidOperationException("本地音乐正在生成，请等待完成后再下载模型");
            var stop=new CancellationTokenSource();_downloadStop=stop;
            _download=new("preparing",model.Id,1,"正在准备本地音乐运行环境");
            _downloadTask=Task.Run(()=>DownloadModelAsync(model,stop.Token));
            return _download;
        }
    }

    /// <summary>Dedicated download cancel (not the generation job cancel). Only records the request: state becomes "cancelling" and stays so until the worker process has exited, then DownloadModelAsync publishes "cancelled". Idempotent while cancelling; throws InvalidOperationException when no cancellable download is running.</summary>
    public MusicDownloadState CancelModelDownload()
    {
        lock(_gate)
        {
            if(_downloadStop is null||_download.State is not ("preparing" or "downloading" or "cancelling"))
                throw new InvalidOperationException("当前没有可取消的模型下载");
            if(_download.State=="cancelling")return _download;
            _download=_download with{State="cancelling",Detail="已请求取消，正在等待工作进程退出"};
            _downloadStop.Cancel();
            return _download;
        }
    }

    /// <summary>onlineConnection is required for provider custom-http (captured under the settings gate by the caller); ignored for local.</summary>
    public MusicJobState StartGeneration(MusicCreateRequest request,MusicOnlineConnection? onlineConnection=null)
    {
        var online=string.Equals(request.Provider,"custom-http",StringComparison.Ordinal);
        if(!online&&!string.Equals(request.Provider,"local",StringComparison.Ordinal))throw new ArgumentException("未知的音乐生成 provider");
        if(request.DurationSeconds is <5 or >180)throw new ArgumentException("音乐时长需在 5–180 秒之间");
        OnlineConfig? config=null;
        if(online)
        {
            if(media is null)throw new ArgumentException("FFmpeg 不可用，无法校验在线音乐");
            if(onlineConnection is null)throw new ArgumentException("在线音乐接口配置快照缺失");
            config=CaptureOnlineConfig(onlineConnection);
        }
        var model=online?null:FindModel(request.ModelId)??throw new ArgumentException("未知的音乐模型");
        if(model is not null&&model.Engine=="musicgen"&&!IsModelInstalled(model.Id))
            throw new InvalidOperationException(model.Id==ExternalId?"外部模型目录已移动或不完整，请重新选择":settings.MusicModelDownloadAllowed?"音乐模型尚未下载，请先在创建音乐窗口下载模型":"未允许下载本地音乐模型，当前只能使用内置原创合成器");
        lock(_gate)
        {
            if(_generationTask is {IsCompleted:false})throw new InvalidOperationException("已有音乐任务正在生成，请等待完成");
            // Online generation uses no local model/worker, so a local download does not block it.
            if(!online&&_downloadStop is not null)throw new InvalidOperationException("模型正在下载，请等待下载完成后生成");
            var id=Guid.NewGuid().ToString("N");var now=DateTimeOffset.UtcNow;
            var state=new MusicJobState(id,"queued",0,"等待开始",null,null,now);
            _jobs[id]=state;
            _generationLocal=!online;
            var stop=new CancellationTokenSource();_generationStop=stop;
            _generationTask=Task.Run(()=>GenerateAsync(id,request,stop.Token,config));
            return state;
        }
    }

    public MusicJobState? Job(string id)=>_jobs.TryGetValue(id,out var state)?state:null;

    public bool CancelJob(string id)
    {
        lock(_gate)
        {
            if(!_jobs.TryGetValue(id,out var state)||state.Status is "completed" or "failed" or "cancelled")return false;
            _generationStop?.Cancel();return true;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? download;CancellationTokenSource? generation;Task? generationTask;Task? downloadTask;
        lock(_gate){download=_downloadStop;generation=_generationStop;generationTask=_generationTask;downloadTask=_downloadTask;}
        download?.Cancel();generation?.Cancel();
        // Bounded so a worker that refuses to die cannot hang shutdown.
        if(downloadTask is not null)try{await downloadTask.WaitAsync(TimeSpan.FromSeconds(30));}catch{}
        if(generationTask is not null)try{await generationTask.WaitAsync(TimeSpan.FromSeconds(30));}catch{}
    }

    public bool IsModelInstalled(string modelId)
    {
        var model=FindModel(modelId);if(model is null)return false;
        if(model.BuiltIn)return true;
        if(model.Id==ExternalId)return ExternalDirectory() is string path&&HasExternalFiles(path);
        var directory=ModelDirectory(model.Id);
        return File.Exists(Path.Combine(directory,"config.json"))&&File.Exists(Path.Combine(directory,".liveclipper-model.json"));
    }

    private const string ExternalId="local/musicgen-directory";
    private string? ExternalDirectory()=>studio?.Document("music-model","external-directory") is string json?JsonSerializer.Deserialize<string>(json):null;
    private IEnumerable<MusicModelInfo> AvailableModels()
    {
        foreach(var model in Catalog)yield return model;
        if(ExternalDirectory() is string path)yield return new(ExternalId,"外部 MusicGen · "+Path.GetFileName(path),"musicgen","只读引用本机目录，不下载或卸载；须有 MusicGen Python 依赖。",0,"使用前请核实模型许可",false);
    }

    public void ImportModelDirectory(string path)
    {
        if(studio is null)throw new InvalidOperationException("音乐工作室存储不可用");
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path))throw new ArgumentException("请选择本地模型的完整目录");
        path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(path.StartsWith(@"\\")||!Directory.Exists(path))throw new ArgumentException("请选择本机已存在的模型目录");
        if(!HasExternalFiles(path))throw new ArgumentException("需要 config.json、preprocessor_config.json、tokenizer_config.json、分词器和单文件 model.safetensors");
        try
        {
            using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(path,"config.json")));
            if(config.RootElement.ValueKind!=JsonValueKind.Object||!config.RootElement.TryGetProperty("model_type",out var type)||type.ValueKind!=JsonValueKind.String||type.GetString()!="musicgen")
                throw new ArgumentException("仅支持 Transformers MusicGen 模型目录");
        }
        catch(JsonException){throw new ArgumentException("模型 config.json 不是有效 JSON");}
        lock(_gate)
        {
            if(_generationLocal&&_generationTask is {IsCompleted:false})throw new InvalidOperationException("本地生成期间不能更换模型目录");
            studio.SaveDocument("music-model","external-directory",JsonSerializer.Serialize(path));
        }
    }
    private static bool HasExternalFiles(string path)=>new[]{"config.json","preprocessor_config.json","tokenizer_config.json","model.safetensors"}.All(name=>File.Exists(Path.Combine(path,name)))&&(File.Exists(Path.Combine(path,"tokenizer.json"))||File.Exists(Path.Combine(path,"spiece.model")));

    private MusicModelInfo? FindModel(string id)=>AvailableModels().FirstOrDefault(x=>string.Equals(x.Id,id,StringComparison.OrdinalIgnoreCase));

    private bool PythonAvailable()
    {
        try
        {
            if(Path.IsPathRooted(settings.PythonPath))return File.Exists(settings.PythonPath);
            return !string.IsNullOrWhiteSpace(settings.PythonPath);
        }
        catch{return false;}
    }

    private string ModelDirectory(string modelId)
    {
        var safe=new string(modelId.Select(c=>char.IsLetterOrDigit(c)||c is '-' or '_' or '.'?c:'_').ToArray());
        return Path.Combine(settings.StorageRoot,"runtime","models","music",safe);
    }

    private string MusicPythonPath()
    {
        var root=Path.Combine(settings.StorageRoot,"runtime","music-venv");
        return OperatingSystem.IsWindows()?Path.Combine(root,"Scripts","python.exe"):Path.Combine(root,"bin","python");
    }

    private async Task DownloadModelAsync(MusicModelInfo model,CancellationToken token)
    {
        MusicDownloadState? final=null;
        try
        {
            var python=await EnsureMusicPythonAsync(token);
            SetDownload("downloading",model.Id,3,"正在安装 Python 运行依赖和下载模型，耗时取决于网速");
            await RunWorkerAsync(python,["prepare","--model",model.Id,"--target",ModelDirectory(model.Id),"--with-runtime"],(value,detail)=>SetDownload("downloading",model.Id,Math.Clamp(value,3,99),detail),token);
            if(!IsModelInstalled(model.Id))throw new InvalidOperationException("模型文件不完整，请重试下载");
            final=new("completed",model.Id,100,"模型已下载，可以开始生成音乐");
        }
        catch(OperationCanceledException){final=new("cancelled",model.Id,0,"模型下载已取消，工作进程已退出");}
        catch(WorkerStuckException ex)
        {
            // Kill failed or the process outlived the grace period: report the error but stay busy until it really exits.
            SetDownload("failed",model.Id,0,ex.Message,ex.Message);
            try{await ex.Process.WaitForExitAsync();}catch{}
            try{ex.Process.Dispose();}catch{}
        }
        catch(Exception ex){final=new("failed",model.Id,0,ex.Message,ex.Message);}
        finally{FinishDownload(final);}
    }

    private async Task GenerateAsync(string id,MusicCreateRequest request,CancellationToken token,OnlineConfig? online=null)
    {
        try
        {
            if(online is not null){await GenerateOnlineAsync(id,request,online,token);return;}
            SetJob(id,"running",4,"正在准备本地音乐环境");
            var model=FindModel(request.ModelId)!;
            var python=model.BuiltIn||(model.Id==ExternalId&&!File.Exists(MusicPythonPath()))?settings.PythonPath:await EnsureMusicPythonAsync(token);
            var output=Path.Combine(settings.StorageRoot,"music","generated",id+".wav");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            SetJob(id,"running",12,model.BuiltIn?"正在创作旋律与配器…":"正在加载本地 AI 模型…");
            var args=new List<string>{"generate","--engine",model.Engine,"--output",output,"--duration",request.DurationSeconds.ToString(CultureInfo.InvariantCulture),"--genre",request.Genre,"--style",request.Style,"--content",request.Content,"--theme",request.Theme};
            if(model.Engine=="musicgen"){args.Add("--model-dir");args.Add(model.Id==ExternalId?ExternalDirectory()!:ModelDirectory(model.Id));if(model.Id==ExternalId)args.Add("--external-safetensors");}
            var workerResult=await RunWorkerAsync(python,args,(value,detail)=>SetJob(id,"running",Math.Clamp(value,12,96),detail),token);
            if(!File.Exists(output))throw new InvalidOperationException("音乐工作进程没有生成音频文件");
            SetJob(id,"running",97,"正在加入素材库…");
            var title=string.Join(" · ",new[]{request.Theme,request.Genre,request.Style}.Where(x=>!string.IsNullOrWhiteSpace(x)).Take(3));
            if(string.IsNullOrWhiteSpace(title))title="AI 原创音乐";
            foreach(var invalid in Path.GetInvalidFileNameChars())title=title.Replace(invalid,'_');
            var analysis=await TryAnalyzeGeneratedAsync(output,workerResult,token);
            using(var stream=File.OpenRead(output))
            {
                var asset=audio.Import(title+".wav",stream,"music",request.Genre.Length>0?request.Genre:"本地生成");
                if(studio is not null&&analysis is not null)SaveAnalysis(studio,asset.Id,analysis);
                var detail=analysis?.Bpm is double bpm?$"生成完成，已加入音乐与音效库（BPM {bpm:0.#}）":"生成完成，已加入音乐与音效库（节拍未识别，可稍后手动分析）";
                SetJob(id,"completed",100,detail,asset.Id,analysis:analysis);
            }
        }
        catch(OperationCanceledException){SetJob(id,"cancelled",0,online is not null?"已停止本地等待；无法保证远端已取消或不计费":"生成已取消");}
        catch(WorkerStuckException ex)
        {
            SetJob(id,"failed",0,ex.Message,null,ex.Message);
            // Keep this task (and so the busy gate) alive until the live process exits.
            try{await ex.Process.WaitForExitAsync();}catch{}
            try{ex.Process.Dispose();}catch{}
        }
        catch(Exception ex){SetJob(id,"failed",0,ex.Message,null,ex.Message);}
        finally
        {
            try{var output=Path.Combine(settings.StorageRoot,"music","generated",id+".wav");if(File.Exists(output))File.Delete(output);}catch{}
            lock(_gate){_generationStop?.Dispose();_generationStop=null;_generationLocal=false;if(_generationTask?.IsCompleted==true)_generationTask=null;}
        }
    }

    private async Task GenerateOnlineAsync(string id,MusicCreateRequest request,OnlineConfig config,CancellationToken token)
    {
        var output=Path.Combine(settings.StorageRoot,"music","generated",id+".wav");
        SetJob(id,"running",0,"正在请求在线音乐接口，等待远端返回音频（无法显示远端进度）");
        await new CustomMusicHttpProvider(media!).GenerateToFileAsync(request,config.Url,config.Model,config.Bearer,output,token);
        token.ThrowIfCancellationRequested();
        SetJob(id,"running",95,"音频已通过格式校验，正在加入素材库…");
        var title=string.Join(" · ",new[]{request.Theme,request.Genre,request.Style}.Where(x=>!string.IsNullOrWhiteSpace(x)).Take(3));
        if(string.IsNullOrWhiteSpace(title))title="在线生成音乐";
        foreach(var invalid in Path.GetInvalidFileNameChars())title=title.Replace(invalid,'_');
        var analysis=await TryAnalyzeGeneratedAsync(output,null,token);
        token.ThrowIfCancellationRequested();
        using var stream=File.OpenRead(output);
        var asset=audio.Import(title+".wav",stream,"music",request.Genre.Length>0?request.Genre:"在线生成");
        if(studio is not null&&analysis is not null)SaveAnalysis(studio,asset.Id,analysis);
        SetJob(id,"completed",100,analysis?.Bpm is double bpm?$"生成完成，已加入音乐与音效库（BPM {bpm:0.#}）":"生成完成，已加入音乐与音效库（节拍未识别，可稍后手动分析）",asset.Id,analysis:analysis);
    }

    private async Task<string> EnsureMusicPythonAsync(CancellationToken token)
    {
        var python=MusicPythonPath();
        if(File.Exists(python))return python;
        var venv=Path.Combine(settings.StorageRoot,"runtime","music-venv");Directory.CreateDirectory(Path.GetDirectoryName(venv)!);
        await RunProcessAsync(settings.PythonPath,["-m","venv",venv],token);
        if(!File.Exists(python))throw new InvalidOperationException("创建本地音乐运行环境失败，请在设置中确认 Python 路径可用");
        return python;
    }

    private async Task<string?> RunWorkerAsync(string python,IReadOnlyList<string> args,Action<int,string>? progress,CancellationToken token)
    {
        var script=Path.Combine(AppContext.BaseDirectory,"ai-worker","music_worker.py");
        if(!File.Exists(script))throw new FileNotFoundException("找不到音乐工作脚本 music_worker.py",script);
        var all=new List<string>{script};all.AddRange(args);
        var process=MediaTools.Start(python,all);var stuck=false;
        try
        {
        var errors=new StringBuilder();string? resultJson=null;
        async Task PumpAsync(StreamReader reader)
        {
            while(await reader.ReadLineAsync(token) is { } line)
            {
                if(line.StartsWith("PROGRESS ",StringComparison.Ordinal))
                {
                    var parts=line.Split(' ',3,StringSplitOptions.RemoveEmptyEntries);
                    if(parts.Length>=2&&double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out var value))
                        progress?.Invoke(Math.Clamp((int)Math.Round(value*100),0,100),parts.Length>2?parts[2]:"处理中");
                }
                else if(line.StartsWith("RESULT ",StringComparison.Ordinal))resultJson=line["RESULT ".Length..].Trim();
                else lock(errors){if(errors.Length<8000)errors.AppendLine(line);}
            }
        }
        var stdout=PumpAsync(process.StandardOutput);var stderr=PumpAsync(process.StandardError);
        try{await process.WaitForExitAsync(token);}
        catch(OperationCanceledException)
        {
            // Only report cancellation after the owned process tree has really exited.
            try{await KillAndConfirmAsync(process);}catch(WorkerStuckException){stuck=true;throw;}
            try{await Task.WhenAll(stdout,stderr);}catch{}
            throw;
        }
        await Task.WhenAll(stdout,stderr);
        if(process.ExitCode!=0)
        {
            var detail=errors.ToString().Trim();throw new InvalidOperationException("音乐工作进程失败："+(detail.Length>0?detail[^Math.Min(detail.Length,1800)..]:"退出码 "+process.ExitCode));
        }
        return resultJson;
        }
        finally{if(!stuck)process.Dispose();}
    }

    private static async Task RunProcessAsync(string executable,IReadOnlyList<string> args,CancellationToken token)
    {
        var process=MediaTools.Start(executable,args);var stuck=false;
        try
        {
            var stdout=process.StandardOutput.ReadToEndAsync(token);var stderr=process.StandardError.ReadToEndAsync(token);
            try{await process.WaitForExitAsync(token);}
            catch(OperationCanceledException)
            {
                try{await KillAndConfirmAsync(process);}catch(WorkerStuckException){stuck=true;throw;}
                try{await Task.WhenAll(stdout,stderr);}catch{}
                throw;
            }
            var output=(await stdout)+"\n"+(await stderr);
            if(process.ExitCode!=0)throw new InvalidOperationException($"{Path.GetFileName(executable)} 运行失败：{output[^Math.Min(output.Length,1200)..]}");
        }
        finally{if(!stuck)process.Dispose();}
    }

    /// <summary>Process still alive after a kill attempt. Carries the live process so the owner can keep its busy state until it really exits.</summary>
    private sealed class WorkerStuckException(Process process,string message,Exception? inner=null):InvalidOperationException(message,inner)
    {
        public Process Process{get;}=process;
    }

    /// <summary>Kills the owned process tree and returns only after the process has exited; otherwise throws WorkerStuckException (never swallows a failed kill).</summary>
    private static async Task KillAndConfirmAsync(Process process)
    {
        try{if(!process.HasExited)process.Kill(true);}
        catch(Exception ex){if(!process.HasExited)throw new WorkerStuckException(process,"无法终止音乐工作进程（"+ex.Message+"），进程仍在运行，任务保持占用直到它退出",ex);}
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}
        catch(TimeoutException){throw new WorkerStuckException(process,"音乐工作进程在终止后 15 秒仍未退出，任务保持占用直到它退出");}
    }

    private void SetDownload(string state,string model,int progress,string detail,string? error=null)
    {
        lock(_gate)
        {
            // A late progress callback must not turn an accepted cancel request back into "downloading".
            if(_download.State=="cancelling"&&state is "preparing" or "downloading")return;
            _download=new(state,model,progress,detail,error);
        }
    }

    /// <summary>Publishes the terminal state and releases the download slot in one critical section, so a poller never sees a terminal state while still busy (or the reverse).</summary>
    private void FinishDownload(MusicDownloadState? final)
    {
        lock(_gate){if(final is not null)_download=final;_downloadStop?.Dispose();_downloadStop=null;}
    }
    private void SetJob(string id,string status,int progress,string detail,string? assetId=null,string? error=null,MusicBeatAnalysis? analysis=null)
    {
        var baseState=_jobs.TryGetValue(id,out var current)?current:new MusicJobState(id,status,progress,detail,assetId,error,DateTimeOffset.UtcNow);
        _jobs[id]=baseState with{Status=status,Progress=Math.Clamp(progress,0,100),Detail=detail,AssetId=assetId??baseState.AssetId,Error=error,UpdatedAt=DateTimeOffset.UtcNow,Analysis=analysis??baseState.Analysis};
    }

    /* ── v1.1 音乐数据面：BPM/beat、节拍循环、ducking、响度 ───────────────── */

    public const string AnalysisScope="music-analysis";
    public const string DuckingScope="music-ducking";
    public const double DefaultTargetLufs=-14;
    private static readonly JsonSerializerOptions AnalysisJson=new(JsonSerializerDefaults.Web);

    private async Task<MusicBeatAnalysis?> TryAnalyzeGeneratedAsync(string path,string? workerResult,CancellationToken token)
    {
        try
        {
            var analysis=ParseWorkerResult(workerResult);
            if(analysis is null&&media is not null)analysis=await AnalyzeAudioFileAsync(media,path,DefaultTargetLufs,null,token);
            if(analysis is null)return null;
            if(media is not null)
            {
                var lufs=await MeasureLoudnessAsync(media,path,token);
                if(lufs is double value)analysis=analysis with{MeasuredLufs=value,TargetLufs=DefaultTargetLufs,LoudnessMethod="ffmpeg-loudnorm"};
            }
            return analysis with{AnalyzedAt=DateTimeOffset.UtcNow};
        }
        catch(OperationCanceledException){throw;}
        catch{return null;}
    }

    public static double BeatInterval(double bpm)=>bpm>0?60.0/bpm:0;

    /// <summary>按 BPM 生成 0..duration 的 beat 时间点；bpm 不可用时返回空数组（降级而非抛异常）。</summary>
    public static double[] BuildBeats(double? bpm,double durationSeconds)
    {
        if(bpm is not double value||value<=0||durationSeconds<=0)return [];
        var interval=60.0/value;var points=new List<double>();
        for(var time=0.0;time<durationSeconds-1e-6;time+=interval)points.Add(Math.Round(time,6));
        return points.ToArray();
    }

    /// <summary>按整小节对齐的循环/裁切点；bpm 为 null 时返回 null（调用方给出降级提示）。</summary>
    public static MusicLoopSuggestion? SuggestBeatLoop(double? bpm,double durationSeconds,double targetSeconds,int beatsPerBar=4)
    {
        if(bpm is not double value||value<=0||durationSeconds<=0)return null;
        var beat=60.0/value;var bar=beat*Math.Max(1,beatsPerBar);
        var wanted=targetSeconds>0?Math.Clamp(targetSeconds,beat,durationSeconds):Math.Min(durationSeconds,bar*4);
        var bars=Math.Max(1,(int)Math.Round(wanted/bar,MidpointRounding.ToZero));
        var seconds=Math.Round(Math.Min(durationSeconds,bars*bar),6);
        return new(0,seconds,seconds,(int)Math.Round(seconds/beat),(int)Math.Round(seconds/bar),Math.Round(value,3),"bar-aligned");
    }

    /// <summary>把转写区间裁剪、排序并合并重叠/近邻人声区间（间隔小于 minGap 视为连续）。</summary>
    public static MusicVoiceInterval[] MergeVoiceIntervals(IEnumerable<MusicVoiceInterval> intervals,double from,double to,double minGap=0.05)
    {
        var clipped=intervals.Select(x=>new MusicVoiceInterval(Math.Max(from,x.Start),Math.Min(to,x.End))).Where(x=>x.End-x.Start>0.12).OrderBy(x=>x.Start).ToList();
        var merged=new List<MusicVoiceInterval>();
        foreach(var item in clipped)
        {
            if(merged.Count>0&&item.Start<=merged[^1].End+Math.Max(0.001,minGap))merged[^1]=merged[^1] with{End=Math.Max(merged[^1].End,item.End)};
            else merged.Add(item);
        }
        return merged.ToArray();
    }

    /// <summary>
    /// 生成 ducking 包络：人声出现前 attack 秒开始下滑、人声区间保持 targetDb、结束后 release 秒回到 0 dB。
    /// 时间相对 from；超出 [0,to-from] 的点会被裁剪。最多 64 个关键帧（与编辑器包络上限一致）。
    /// </summary>
    public static VolumeKeyframe[] BuildDuckingKeyframes(IReadOnlyList<MusicVoiceInterval> voices,double from,double to,double targetDb,double attackSeconds,double releaseSeconds)
    {
        var duration=Math.Max(0.001,to-from);var attack=Math.Max(0,attackSeconds);var release=Math.Max(0,releaseSeconds);
        var map=new SortedDictionary<double,double>();
        void Add(double time,double db){var key=Math.Round(Math.Clamp(time,0,duration),3);map[key]=Math.Clamp(db,-60,12);}
        Add(0,0);
        foreach(var voice in MergeVoiceIntervals(voices,from,to))
        {
            var start=Math.Clamp(voice.Start-from,0,duration);var end=Math.Clamp(voice.End-from,0,duration);
            Add(Math.Max(0,start-attack),0);
            Add(start,Math.Clamp(targetDb,-60,12));
            Add(end,Math.Clamp(targetDb,-60,12));
            Add(Math.Min(duration,end+release),0);
        }
        if(map.Count>64)
        {
            var pairs=map.ToList();
            while(pairs.Count>64)
            {
                var reduced=new List<KeyValuePair<double,double>>();
                for(var index=0;index<pairs.Count;index+=2)reduced.Add(pairs[index]);
                if(reduced[^1].Key!=pairs[^1].Key)reduced.Add(pairs[^1]);
                pairs=reduced;
            }
            return pairs.Select(pair=>new VolumeKeyframe(pair.Key,pair.Value)).ToArray();
        }
        return map.Select(pair=>new VolumeKeyframe(pair.Key,pair.Value)).ToArray();
    }

    /// <summary>解析 worker 输出的 RESULT JSON（bpm/beats/method/confidence/durationSeconds）。</summary>
    public static MusicBeatAnalysis? ParseWorkerResult(string? json)
    {
        if(string.IsNullOrWhiteSpace(json))return null;
        try
        {
            using var document=JsonDocument.Parse(json);var root=document.RootElement;
            double? bpm=null;
            if(root.TryGetProperty("bpm",out var bpmElement)&&bpmElement.ValueKind==JsonValueKind.Number)bpm=bpmElement.GetDouble();
            var beats=new List<double>();
            if(root.TryGetProperty("beats",out var beatsElement)&&beatsElement.ValueKind==JsonValueKind.Array)
                foreach(var item in beatsElement.EnumerateArray())if(item.ValueKind==JsonValueKind.Number)beats.Add(item.GetDouble());
            var method=root.TryGetProperty("method",out var methodElement)?methodElement.GetString()??"worker":"worker";
            var confidence=root.TryGetProperty("confidence",out var confidenceElement)&&confidenceElement.ValueKind==JsonValueKind.Number?confidenceElement.GetDouble():0;
            var duration=root.TryGetProperty("durationSeconds",out var durationElement)&&durationElement.ValueKind==JsonValueKind.Number?durationElement.GetDouble():0;
            return new MusicBeatAnalysis(bpm,beats.OrderBy(x=>x).ToArray(),method,Math.Round(confidence,3),Math.Round(duration,3),AnalyzedAt:DateTimeOffset.UtcNow);
        }
        catch{return null;}
    }

    /// <summary>读取 ffmpeg 解码出的单声道 WAV（PCM16 或 float32），返回归一化采样。</summary>
    public static (float[] Samples,int SampleRate)? ReadWavMono(string path)
    {
        try
        {
            using var stream=File.OpenRead(path);using var reader=new BinaryReader(stream);
            if(new string(reader.ReadChars(4))!="RIFF")return null;reader.ReadInt32();if(new string(reader.ReadChars(4))!="WAVE")return null;
            var format=0;var channels=0;var sampleRate=0;var bits=0;byte[]? data=null;
            while(stream.Position+8<=stream.Length)
            {
                var id=new string(reader.ReadChars(4));var size=reader.ReadInt32();
                if(size<0||stream.Position+size>stream.Length)break;
                if(id=="fmt "){format=reader.ReadInt16();channels=reader.ReadInt16();sampleRate=reader.ReadInt32();reader.ReadInt32();reader.ReadInt16();bits=reader.ReadInt16();var skip=size-16;if(skip>0)reader.ReadBytes(skip);}
                else if(id=="data"){data=reader.ReadBytes(size);break;}
                else reader.ReadBytes(size);
            }
            if(data is null||sampleRate<=0||channels<=0)return null;
            var values=new List<float>(data.Length/Math.Max(1,channels*2));
            if(format==1&&bits==16){for(var offset=0;offset+2*channels<=data.Length;offset+=2*channels)values.Add(BitConverter.ToInt16(data,offset)/32768f);}
            else if(format==3&&bits==32){for(var offset=0;offset+4*channels<=data.Length;offset+=4*channels)values.Add(BitConverter.ToSingle(data,offset));}
            else return null;
            return (values.ToArray(),sampleRate);
        }
        catch{return null;}
    }

    /// <summary>
    /// 纯 C# 自相关节拍分析：能量包络 → 归一化自相关（55–220 BPM）→ 相位与 beat 网格。
    /// 静音或节拍不明显时返回 null，调用方降级。
    /// </summary>
    public static MusicBeatAnalysis? AnalyzePcm(IReadOnlyList<float> samples,int sampleRate,string method="csharp-autocorrelation")
    {
        if(sampleRate<=0||samples.Count<sampleRate)return null;
        const int frameRate=50;var frameSize=Math.Max(1,sampleRate/frameRate);var count=samples.Count/frameSize;
        var energy=new double[count];
        for(var index=0;index<count;index++)
        {
            var sum=0.0;
            for(var sample=0;sample<frameSize;sample++){var value=samples[index*frameSize+sample];sum+=value*value;}
            energy[index]=Math.Sqrt(sum/frameSize);
        }
        var peak=energy.Max();if(peak<1e-5)return null;
        for(var index=0;index<count;index++)energy[index]/=peak;
        var onset=new double[count];
        for(var index=1;index<count;index++)onset[index]=Math.Max(0,energy[index]-energy[index-1]);
        var onsetPeak=onset.Max();if(onsetPeak<=0)return null;
        for(var index=0;index<count;index++)onset[index]/=onsetPeak;
        var minLag=Math.Max(2,(int)Math.Round(60.0*frameRate/220));
        var maxLag=Math.Min(count-2,(int)Math.Round(60.0*frameRate/55));
        var bestScore=0.0;var bestLag=0;
        for(var lag=minLag;lag<=maxLag;lag++)
        {
            double dot=0,energyA=0,energyB=0;
            for(var index=0;index<count-lag;index++){dot+=onset[index]*onset[index+lag];energyA+=onset[index]*onset[index];energyB+=onset[index+lag]*onset[index+lag];}
            if(dot<=0)continue;
            var score=dot/Math.Sqrt(energyA*energyB+1e-12);
            if(score>bestScore){bestScore=score;bestLag=lag;}
        }
        if(bestLag<minLag||bestScore<0.15)return null;
        if(bestLag>=minLag*2)
        {
            var half=bestLag/2;double dotHalf=0,energyHalfA=0,energyHalfB=0;
            for(var index=0;index<count-half;index++){dotHalf+=onset[index]*onset[index+half];energyHalfA+=onset[index]*onset[index];energyHalfB+=onset[index+half]*onset[index+half];}
            var halfScore=dotHalf>0?dotHalf/Math.Sqrt(energyHalfA*energyHalfB+1e-12):0;
            if(halfScore>=bestScore*0.75){bestLag=half;bestScore=halfScore;}
        }
        var bpm=Math.Round(60.0*frameRate/bestLag,2);
        var duration=samples.Count/(double)sampleRate;
        var search=Math.Min(count-1,Math.Max(1,bestLag)*2);var phase=0;var phaseBest=0.0;
        for(var index=0;index<=search;index++)if(onset[index]>phaseBest){phaseBest=onset[index];phase=index;}
        var beatInterval=60.0/bpm;var beats=new List<double>();
        var start=Math.Max(0,phase/(double)frameRate-beatInterval/2);
        for(var time=start;time<duration-1e-6;time+=beatInterval)beats.Add(Math.Round(time,4));
        return new MusicBeatAnalysis(bpm,beats.ToArray(),method,Math.Round(bestScore,3),Math.Round(duration,3));
    }

    public static async Task<MusicBeatAnalysis?> AnalyzeAudioFileAsync(MediaTools media,string path,double? targetLufs,double? measuredLufs,CancellationToken token)
    {
        var temp=Path.Combine(Path.GetTempPath(),"liveclipper-music-"+Guid.NewGuid().ToString("N")+".wav");
        try
        {
            await media.RunAsync(media.Ffmpeg,["-hide_banner","-loglevel","error","-y","-i",path,"-vn","-ac","1","-ar","8000","-f","wav",temp],token);
            if(!File.Exists(temp))return null;
            if(ReadWavMono(temp) is not { } pcm)return null;
            var analysis=AnalyzePcm(pcm.Samples,pcm.SampleRate);
            if(analysis is null)return null;
            if(measuredLufs is double value)analysis=analysis with{MeasuredLufs=Math.Round(value,1),TargetLufs=targetLufs??DefaultTargetLufs,LoudnessMethod="ffmpeg-loudnorm"};
            return analysis with{AnalyzedAt=DateTimeOffset.UtcNow};
        }
        catch(OperationCanceledException){throw;}
        catch{return null;}
        finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}
    }

    public static async Task<double?> MeasureLoudnessAsync(MediaTools media,string path,CancellationToken token)
    {
        try
        {
            var loudnorm=$"loudnorm=I={DefaultTargetLufs.ToString("0.###",CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11:print_format=json";
            var output=await media.CaptureAsync(media.Ffmpeg,["-hide_banner","-i",path,"-af",loudnorm,"-f","null","-"],token);
            return ParseLoudnormLufs(output);
        }
        catch(OperationCanceledException){throw;}
        catch{return null;}
    }

    /// <summary>从 ffmpeg loudnorm 的分析输出里取出 measured 响度（LUFS）。解析失败返回 null。</summary>
    public static double? ParseLoudnormLufs(string? output)
    {
        if(string.IsNullOrWhiteSpace(output))return null;
        var begin=output.LastIndexOf('{');var end=output.LastIndexOf('}');
        if(begin<0||end<=begin)return null;
        try
        {
            using var document=JsonDocument.Parse(output[begin..(end+1)]);var root=document.RootElement;
            foreach(var name in new[]{"measured_I","measured_i","input_i"})
            {
                if(!root.TryGetProperty(name,out var element))continue;
                var text=element.ValueKind==JsonValueKind.String?element.GetString():element.ToString();
                if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))return null;
                return Math.Round(value,1);
            }
            return null;
        }
        catch{return null;}
    }

    /// <summary>把分析结果持久化到 studio_documents（不改 audio_assets 表结构，兼容旧数据）。</summary>
    public static void SaveAnalysis(StudioStore? studio,string assetId,MusicBeatAnalysis analysis)=>studio?.SaveDocument(AnalysisScope,assetId,JsonSerializer.Serialize(analysis,AnalysisJson));

    public static MusicBeatAnalysis? LoadAnalysis(StudioStore? studio,string assetId)
    {
        var json=studio?.Document(AnalysisScope,assetId);
        if(string.IsNullOrWhiteSpace(json))return null;
        try{return JsonSerializer.Deserialize<MusicBeatAnalysis>(json,AnalysisJson);}catch{return null;}
    }

    public static void SaveDucking(StudioStore? studio,MusicDuckingEnvelope envelope)=>studio?.SaveDocument(DuckingScope,envelope.ClipId,JsonSerializer.Serialize(envelope,AnalysisJson));

    public static MusicDuckingEnvelope? LoadDucking(StudioStore? studio,string clipId)
    {
        var json=studio?.Document(DuckingScope,clipId);
        if(string.IsNullOrWhiteSpace(json))return null;
        try{return JsonSerializer.Deserialize<MusicDuckingEnvelope>(json,AnalysisJson);}catch{return null;}
    }
}
