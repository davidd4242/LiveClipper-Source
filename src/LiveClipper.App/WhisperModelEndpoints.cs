using System.Text.RegularExpressions;
using LiveClipper.Core;
using Microsoft.AspNetCore.Mvc;

public sealed record WhisperModelPrepareRequest(string? Source,string? Revision);

public static class WhisperModelEndpoints
{
    // Systran/faster-whisper-small 已核固定 commit；仅 small 采用，其它型号必须由请求给出固定 commit。
    private const string SmallRevision="536b0662742c02347bc0e980a01041f333bce120";

    public static void Map(WebApplication app,LiveSettings settings)
    {
        var service=new WhisperModelPreparationService(settings);
        app.Lifetime.ApplicationStopping.Register(service.Dispose);

        // 只读：不启动 Python、不下载。idle 只表示没有准备任务，不代表模型缺失。
        app.MapGet("/api/asr/model",()=>Results.Ok(new {model=settings.WhisperModel,state=service.State}));

        app.MapPost("/api/asr/model/prepare",([FromBody] WhisperModelPrepareRequest? body)=>
        {
            var model=settings.WhisperModel?.Trim()??"";
            var revision=string.IsNullOrWhiteSpace(body?.Revision)?null:body!.Revision!.Trim();
            if(Path.IsPathRooted(model)||Directory.Exists(model))
                return Results.BadRequest(new {error="当前 ASR 模型是本地目录，暂不支持目录身份准备"});
            if(revision is not null&&!Regex.IsMatch(revision,"^[0-9a-fA-F]{40}$"))
                return Results.BadRequest(new {error="revision 只接受 40 位十六进制 commit，不接受分支名"});
            if(revision is null)
            {
                if(model is "small" or "Systran/faster-whisper-small")revision=SmallRevision;
                else return Results.BadRequest(new {error=$"模型 {model} 没有已核固定版本，请提供 40 位 commit revision"});
            }
            try
            {
                return service.StartPrepare(string.IsNullOrWhiteSpace(body?.Source)?"auto":body!.Source!.Trim(),revision)
                    ?Results.Accepted("/api/asr/model",new {accepted=true,model,revision,state=service.State})
                    :Results.Conflict(new {error="已有 Whisper 模型准备任务在进行"});
            }
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
        });

        // 只发出取消请求；真实终态（cancelled/failed/completed）以 GET 为准。
        app.MapPost("/api/asr/model/prepare/cancel",()=>
        {
            service.CancelPrepare();
            return Results.Accepted("/api/asr/model",new {cancelRequested=true});
        });
    }
}
