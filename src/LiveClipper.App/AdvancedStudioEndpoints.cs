using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Mvc;

public sealed record OnlineAudioRequest(string Query,string Kind="music",string Source="all",string License="usable",int Page=1);
public sealed record OnlineAudioImport(string Id,string Kind);
public sealed record CutPlanRequest(double Start,double End,AiConnection? Connection);
public sealed record FinePlanRequest(EditorProject Project,AiConnection? Connection);

public static class AdvancedStudioEndpoints
{
    public static void Map(WebApplication app,Store store,StudioStore studio,EditorRenderService editor,AudioLibraryService library,MediaTools media,LiveSettings settings,StudioExportService exports,HttpClient? aiTransport=null)
    {
        var online=new OnlineAudioService(studio,library,media);var planner=new EditPlanningService(store,studio,aiTransport);
        Clip Clip(string id)=>store.Clips().FirstOrDefault(x=>x.Id==id)??throw new ArgumentException("视频不存在");
        app.MapPost("/api/audio/online/search",async ([FromBody] OnlineAudioRequest body,CancellationToken token)=>Results.Ok(await online.SearchAsync(body.Query,body.Kind,body.Source,body.License,body.Page,token)));
        app.MapPost("/api/audio/online/import",async ([FromBody] OnlineAudioImport body,CancellationToken token)=>Results.Ok(await online.DownloadAsync(body.Id,body.Kind,token)));
        app.MapGet("/api/assets/{id}/attribution",(string id)=>{var value=studio.Document("audio-attribution",id);return value is null?Results.NotFound():Results.Content(value,"application/json");});
        app.MapGet("/api/audio/attributions",()=>Results.Ok(studio.Assets().Select(x=>new {x.Id,json=studio.Document("audio-attribution",x.Id)}).Where(x=>x.json is not null).ToDictionary(x=>x.Id,x=>JsonSerializer.Deserialize<OnlineAudio>(x.json!))));
        app.MapPost("/api/studio/clips/{id}/cut-plan",async (string id,[FromBody] CutPlanRequest body,CancellationToken token)=>Results.Ok(await planner.CutAsync(Clip(id),body.Start,body.End,body.Connection is {} connection?AiConnectionResolver.Resolve(connection,settings):null,token)));
        app.MapPost("/api/studio/clips/{id}/fine-plan",async (string id,[FromBody] FinePlanRequest body,CancellationToken token)=>Results.Ok(await planner.FineAsync(Clip(id),body.Project,body.Connection is {} connection?AiConnectionResolver.Resolve(connection,settings):null,token)));
        app.MapDelete("/api/studio/clips/{id}/events/{eventId}",(string id,string eventId)=>Results.Ok(new {deleted=studio.DeleteEvent(id,eventId)}));
        app.MapPost("/api/studio/renders/{id}/{action}",(string id,string action)=>editor.Control(id,action)?Results.Ok(new {ok=true}):Results.NotFound());
        app.MapPost("/api/studio/clips/{id}/package",(string id,[FromBody] EditorProject plan)=>Results.Ok(exports.Queue(Clip(id),plan,"package")));
        app.MapPost("/api/studio/clips/{id}/draft",(string id,[FromBody] EditorProject plan)=>Results.Ok(exports.Queue(Clip(id),plan,"draft")));
        app.MapGet("/api/studio/outputs",()=>{var jobs=store.Jobs().ToDictionary(x=>x.Id);return Results.Ok(studio.Outputs().Select(x=>new {x.Id,x.ClipId,x.Kind,x.OutputPath,x.CreatedAt,status=jobs.GetValueOrDefault(x.Id)?.Status,error=jobs.GetValueOrDefault(x.Id)?.Error,progress=jobs.GetValueOrDefault(x.Id)?.Progress}));});
        app.MapGet("/api/studio/packages/{id}/file",(string id)=>{
            var item=studio.Outputs().FirstOrDefault(x=>x.Id==id);
            if(item is null||store.Jobs().FirstOrDefault(x=>x.Id==id)?.Status!="completed"||!File.Exists(item.OutputPath))return Results.NotFound();
            // 草稿 ZIP 直接用磁盘上的新命名，素材包沿用原来的下载名。
            var name=item.Kind=="draft"?Path.GetFileName(item.OutputPath):"LiveClipper-"+item.Kind+"-"+id[..8]+".zip";
            return Results.File(item.OutputPath,"application/zip",name);
        });
        app.MapPost("/api/studio/clips/{id}/mix-preview",async (string id,[FromBody] EditorProject plan,CancellationToken token)=>
        {
            var clip=Clip(id);EditorRenderService.Validate(plan);var items=new List<TimelineItem>();var remaining=15d;
            foreach(var x in EditorRenderService.Items(plan,id)){if(remaining<=0)break;var item=x with {End=Math.Min(x.End,x.Start+remaining)};items.Add(item);remaining-=item.End-item.Start;}
            var previewId=Guid.NewGuid().ToString("N");var directory=Path.Combine(settings.StorageRoot,"mix-previews",previewId);Directory.CreateDirectory(directory);
            var preview=plan with {Timeline=items.ToArray(),Resolution=480,EffectAt=Math.Min(plan.EffectAt,items.Sum(x=>x.End-x.Start))};
            var path=Path.Combine(directory,"preview.mp4");await editor.RenderAsync(clip,preview,path,Path.Combine(directory,"work"),token);
            studio.SaveDocument("mix-preview",previewId,path);return Results.Ok(new {id=previewId,url="/api/studio/mix-previews/"+previewId+"/file"});
        });
        app.MapGet("/api/studio/mix-previews/{id}/file",(string id)=>{var path=studio.Document("mix-preview",id);return path is null||!File.Exists(path)?Results.NotFound():Results.File(path,"video/mp4",enableRangeProcessing:true);});
        app.MapPost("/api/studio/stickers",async (IFormFile file,CancellationToken token)=>
        {
            var extension=Path.GetExtension(file.FileName).ToLowerInvariant();if(file.Length is <1 or >10_000_000||extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))throw new ArgumentException("请选择小于 10 MB 的 PNG、JPEG 或 WebP 图片");
            var id=Guid.NewGuid().ToString("N");var directory=Path.Combine(settings.StorageRoot,"stickers");Directory.CreateDirectory(directory);var path=Path.Combine(directory,id+extension);
            await using(var target=File.Create(path))await file.CopyToAsync(target,token);await media.ProbeAsync(path,token);
            var item=new AudioAsset(id,Path.GetFileNameWithoutExtension(file.FileName),"sticker","自定义",path,false);studio.SaveAsset(item);return Results.Ok(item);
        }).DisableAntiforgery();
        // 封面编辑功能已移除；历史封面文件仍会被素材包导出兼容处理。
    }
}
