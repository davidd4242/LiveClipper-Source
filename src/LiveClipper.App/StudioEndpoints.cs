using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Mvc;

public sealed record SemanticAnalysisRequest(JsonElement Project,SemanticSelection[] SelectedCandidates,AiConnection Connection,string SourceSignature,string CandidateHash,string TranscriptCacheKey,bool Confirm=false);
public sealed record RoughCutCopyRequest(string Name,JsonElement Project,RoughCutSourceRange[] Ranges,RoughCutSourceRange[] BeforeRanges,string SourceSignature,string CandidateHash,string TranscriptCacheKey,int[]? SceneBoundaryIndexes=null,double TransitionDurationSeconds=.8);
public sealed record RoughCutPreviewRequest(JsonElement Project);
public sealed record IndependentAnalyzeRequest(JsonElement Project,AiConnection Connection);
public sealed record RoughCutVerifyRequest(JsonElement Project,string SourceSignature,string CandidateHash,string TranscriptCacheKey);
public sealed record SilencePreviewRequest(double Start,double End,double NoiseDb=-32,double MinSeconds=SilenceCutPlanner.MinSeconds,JsonElement? Project=null);
public sealed record SilenceVerifyRequest(string SourceSignature,string? TranscriptCacheKey=null,JsonElement? Project=null);
public sealed record ImportFolders(string VideoDirectory,string? SubtitleDirectory,string OutputDirectory);
public sealed record SubtitleUpdate(string Language,IReadOnlyList<TranscriptSegment> Segments);
public sealed record TranscriptFromRequest(string SourceClipId);
public sealed record EventCreate(string ClipId,double Start,double End,string Title,string? Summary,string? Hook);
public sealed record EventMerge(string ClipId,IReadOnlyList<string> Ids);
public sealed record ClipSelection(string ClipId);
public sealed record AiAnalyzeRequest(string ClipId,AiConnection Connection);
public sealed record ModelPullRequest(string Model);
public sealed record ImportFilesRequest(IReadOnlyList<string> Paths,string? OutputDirectory);
public sealed record VariantRequest(string Name,JsonElement Project);
public sealed record NewEditorProjectRequest(string Name,int Width=1920,int Height=1080,int Fps=30);
public sealed record ProjectCopyRequest(string Name,JsonElement Project);
public sealed record PremiereExportRequest(string Name,JsonElement Project,bool NormalizeVariableFrameRate=false);
/// <summary>批量命令请求。BaseRevision 为 null 表示"不检查"（老脚本），但仍会递增 revision。</summary>
public sealed record EditorCommandBatch(int? BaseRevision,List<EditorCommand> Commands,string? SessionId=null);

public static class StudioEndpoints
{
    public static void Map(WebApplication app,Store store,StudioStore studio,StudioMediaService mediaService,AiAnalysisService ai,AudioLibraryService audio,DemucsService demucs,EditorRenderService editor,MediaTools media,CandidatePipelineService pipeline,LiveSettings settings)
    {
        Clip? ClipById(string id)=>store.Clips().FirstOrDefault(x=>x.Id==id);
        IResult MissingMediaProject(string id)=>studio.GetIndependentProject(id) is not null
            ?Results.Json(new {error="独立工程的此项主素材操作尚未接入，包括导出与独立另存；即使已导入素材也暂不可用。空时间线不能导出成片。",code="independent_project_source_required"},statusCode:422)
            :Results.NotFound(new {error="工程或素材不存在",code=StudioErrorCodes.NotFound});
        app.MapGet("/api/studio/projects",()=>Results.Ok(studio.IndependentProjects()));
        app.MapPost("/api/studio/projects",([FromBody] NewEditorProjectRequest body)=>
        {
            try{return Results.Ok(studio.CreateIndependentProject(body.Name,body.Width,body.Height,body.Fps).Identity);}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message,code="invalid_project_configuration"});}
        });
        var roughCutPreview=new RoughCutPreviewService(store,studio);
        IndependentRoughCutService IndependentRoughCut()=>new(store,studio,Path.Combine(
            RenderStoragePaths.OutputRoot(Path.GetDirectoryName(store.DatabasePath)!,Path.GetDirectoryName(store.DatabasePath)!,settings.ExportOutputDirectory),"roughcut-copies"));
        app.MapPost("/api/studio/projects/{projectId}/sources/{sourceId}/analyze",async (string projectId,string sourceId,IndependentAnalyzeRequest body,HttpRequest request,CancellationToken token)=>
        {
            if(studio.GetIndependentProject(projectId) is null)return Results.NotFound(new {error="独立工程不存在"});
            EpochHeader(request.HttpContext.Response);long revision=0;
            var rejected=EditorTransaction(projectId,()=>
            {
                if(WritePrecondition(projectId,request,null,out var expected) is {} invalid)return invalid;
                if(!request.Headers.ContainsKey("X-Editor-Epoch"))return Results.Json(new {error="分析须携带当前编辑纪元"},statusCode:428);
                if(StaleRevision(projectId,expected) is {} stale)return stale;
                revision=expected;return Results.Ok();
            });
            if(rejected is not Microsoft.AspNetCore.Http.HttpResults.Ok)return rejected;
            try
            {
                var result=await IndependentRoughCut().AnalyzeAsync(projectId,sourceId,EditorProjectCodec.Deserialize(body.Project),revision,
                    (source,snapshot,ct)=>ai.AnalyzeAsync(source,ResolveConnection(body.Connection),ct,snapshot),token);
                return Results.Ok(new {result.Events,result.Advice,result.AdviceError});
            }
            catch(ProjectCopyConflictException ex){return Results.Conflict(new {error=ex.Message});}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(AiCompletionException ex){return Results.Json(new {error=ex.Message},statusCode:422);}
            catch(InvalidOperationException ex){return Results.Json(new {error=ex.Message},statusCode:422);}
            catch(HttpRequestException){return Results.Json(new {error="分析请求失败；缓存可能已更新，时间线未改，请重新读取并核对"},statusCode:502);}
        });
        app.MapPost("/api/studio/projects/{projectId}/sources/{sourceId}/roughcut-preview",async (string projectId,string sourceId,RoughCutPreviewRequest body,CancellationToken token)=>
        {
            try
            {
                var service=IndependentRoughCut();var clip=service.Source(projectId,sourceId);
                var duration=await media.DurationAsync(clip.Path,token);
                if(duration<=0)return Results.UnprocessableEntity(new {error="无法读取所选源素材时长"});
                return Results.Ok(service.Preview(projectId,sourceId,EditorProjectCodec.Deserialize(body.Project),duration));
            }
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(IOException){return Results.Conflict(new {error="所选源素材已不可读取"});}
        });
        app.MapPost("/api/studio/projects/{projectId}/sources/{sourceId}/roughcut-preview/verify",(string projectId,string sourceId,RoughCutVerifyRequest body)=>
        {
            try
            {
                IndependentRoughCut().Verify(projectId,sourceId,EditorProjectCodec.Deserialize(body.Project),0,body.SourceSignature,body.CandidateHash,body.TranscriptCacheKey);
                return Results.Ok(new {valid=true});
            }
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(ProjectCopyConflictException ex){return Results.Conflict(new {error=ex.Message});}
            catch(IOException){return Results.Conflict(new {error="所选源素材已不可读取"});}
        });
        app.MapPost("/api/studio/projects/{projectId}/sources/{sourceId}/semantic-review",async (string projectId,string sourceId,SemanticAnalysisRequest body,CancellationToken token)=>
        {
            if(!body.Confirm)return Results.BadRequest(new {error="请明确确认所选字幕和模型服务"});
            try
            {
                var service=IndependentRoughCut();var clip=service.Source(projectId,sourceId);
                var revision=studio.GetIndependentProject(projectId)!.Document.Revision;
                var project=EditorProjectCodec.Deserialize(body.Project);
                var duration=await media.DurationAsync(clip.Path,token);
                var preview=service.Verify(projectId,sourceId,project,duration,body.SourceSignature,body.CandidateHash,body.TranscriptCacheKey);
                var result=await SemanticReviewAnalysis.AnalyzeAsync(preview,body.SelectedCandidates,body.Confirm,body.SourceSignature,body.CandidateHash,body.TranscriptCacheKey,ResolveConnection(body.Connection),token);
                service.Verify(projectId,sourceId,project,duration,body.SourceSignature,body.CandidateHash,body.TranscriptCacheKey);
                if(studio.GetIndependentProject(projectId)?.Document.Revision!=revision)return Results.Conflict(new {error="分析期间工程已变化，建议未应用"});
                return Results.Ok(result);
            }
            catch(SemanticInputLimitException ex){return Results.BadRequest(new {error=ex.Message,code="semantic_input_limit",requestSent=false});}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
            catch(AiCompletionException ex){return Results.Json(new {error=ex.Message},statusCode:422);}
            catch(HttpRequestException){return Results.Json(new {error="模型服务请求失败；未重试或修改工程"},statusCode:502);}
            catch(JsonException){return Results.Json(new {error="模型返回格式无效；未修改工程"},statusCode:502);}
        });
        app.MapPost("/api/studio/projects/{projectId}/sources/{sourceId}/roughcut-copy",async (string projectId,string sourceId,RoughCutCopyRequest body,HttpRequest request,CancellationToken token)=>
        {
            if(studio.GetIndependentProject(projectId) is null)return Results.NotFound(new {error="独立工程不存在"});
            EpochHeader(request.HttpContext.Response);long revision=0;
            var rejected=EditorTransaction(projectId,()=>
            {
                if(WritePrecondition(projectId,request,null,out var expected) is {} invalid)return invalid;
                if(!request.Headers.ContainsKey("X-Editor-Epoch"))return Results.Json(new {error="生成结果须携带当前编辑纪元"},statusCode:428);
                if(StaleRevision(projectId,expected) is {} stale)return stale;
                revision=expected;return Results.Ok();
            });
            if(rejected is not Microsoft.AspNetCore.Http.HttpResults.Ok)return rejected;
            try
            {
                var service=IndependentRoughCut();var clip=service.Source(projectId,sourceId);
                var duration=await media.DurationAsync(clip.Path,token);
                return Results.Ok(await service.CreateAsync(projectId,sourceId,body.Name,EditorProjectCodec.Deserialize(body.Project),duration,
                    body.Ranges,body.BeforeRanges,body.SourceSignature,body.CandidateHash,body.TranscriptCacheKey,revision,token,body.SceneBoundaryIndexes,body.TransitionDurationSeconds));
            }
            catch(ProjectCopyConflictException ex){return Results.Conflict(new {error=ex.Message});}
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or JsonException or EditorProjectSchemaException or ReplicaExtensionException)
            {return Results.Json(new {error=ex.Message},statusCode:422);}
            catch(IOException){return Results.Json(new {error="派生结果复制失败，原工程未修改，未发布不完整结果"},statusCode:500);}
        });

        app.MapPost("/api/studio/clips/{id}/semantic-review",async (string id,SemanticAnalysisRequest req,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            if(!req.Confirm)return Results.BadRequest(new {error="请明确确认发送字幕及可能的服务商费用"});
            try
            {
                var project=EditorProjectCodec.Deserialize(req.Project.GetRawText());
                var duration=await media.DurationAsync(clip.Path,token);token.ThrowIfCancellationRequested();
                var baseline=roughCutPreview.Preview(clip,project,duration);
                var result=await SemanticReviewAnalysis.AnalyzeAsync(baseline,req.SelectedCandidates,req.Confirm,req.SourceSignature,req.CandidateHash,req.TranscriptCacheKey,ResolveConnection(req.Connection),token);
                var current=roughCutPreview.Preview(clip,project,duration);
                if(current.SourceSignature!=baseline.SourceSignature||current.CandidateHash!=baseline.CandidateHash||current.TranscriptCacheKey!=baseline.TranscriptCacheKey)return Results.Conflict(new {error="分析期间源素材或候选变化，结果未应用，请重新预览"});
                return Results.Ok(result);
            }
            catch(SemanticInputLimitException ex){return Results.BadRequest(new {error=ex.Message,code="semantic_input_limit",requestSent=false});}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
            catch(AiCompletionException ex){return Results.Json(new {error=ex.Message},statusCode:422);}
            catch(HttpRequestException){return Results.Json(new {error="语义请求失败，未自动重试；原候选未修改，服务商可能已计费"},statusCode:502);}
            catch(JsonException){return Results.Json(new {error="语义响应格式无效，未自动重试或修改候选"},statusCode:502);}
        });
        app.MapPost("/api/studio/clips/{id}/roughcut-preview",async (string id,RoughCutPreviewRequest req,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            try
            {
                var project=EditorProjectCodec.Deserialize(req.Project.GetRawText());
                var duration=await media.DurationAsync(clip.Path,token);token.ThrowIfCancellationRequested();
                if(duration<=0)return Results.UnprocessableEntity(new {error="无法读取源素材时长"});
                return Results.Ok(roughCutPreview.Preview(clip,project,duration));
            }
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
        });
        app.MapPost("/api/studio/clips/{id}/roughcut-preview/verify",(string id,RoughCutVerifyRequest req)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            if(!File.Exists(clip.Path))return Results.Conflict(new {error="源素材已变化，请重新预览"});
            var current=roughCutPreview.Preview(clip,EditorProjectCodec.Deserialize(req.Project.GetRawText()),0);
            return current.SourceSignature==req.SourceSignature&&current.CandidateHash==req.CandidateHash&&current.TranscriptCacheKey==req.TranscriptCacheKey
                ?Results.Ok(new {valid=true}):Results.Conflict(new {error="源素材、字幕或候选已变化，请重新预览"});
        });
        var previewAudio=new PreviewAudioService(media);
        app.MapGet("/api/studio/clips/{id}/preview-audio.wav",async (string id,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null||!File.Exists(clip.Path))return Results.NotFound(new {error="源素材不存在"});
            try
            {
                var workRoot=RenderStoragePaths.WorkRoot(settings.StorageRoot,settings.RenderWorkDirectory);
                var path=await previewAudio.GetAsync(clip.Path,workRoot,token);
                return Results.File(path,"audio/wav",enableRangeProcessing:true);
            }
            catch(NotSupportedException ex){return Results.Json(new {error=ex.Message},statusCode:422);}
            catch(Exception ex) when(ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {Console.Error.WriteLine("[preview-audio] "+ex);return Results.Json(new {error="预览音频生成失败，原素材未修改"},statusCode:500);}
        });
        var sourceSilence=new SourceSilencePreviewService(media,()=>settings.PythonPath);
        EditorProject SilenceProject(Clip clip,JsonElement? requested)=>requested is {} json
            ?EditorProjectCodec.Deserialize(json)
            :(studio.Project(clip.Id) is {} saved?EditorProjectCodec.Deserialize(saved):new EditorProject());
        async Task<IResult> PreviewSilence(Clip clip,EditorProject project,SilencePreviewRequest req,CancellationToken token)
        {
            var transcript=EffectiveTranscript.Resolve(project,clip.Id,store.TranscriptJson(clip.EventId));
            return Results.Ok(await sourceSilence.PreviewAsync(clip.Id,clip.Path,req.Start,req.End,req.NoiseDb,req.MinSeconds,token,
                transcript.Segments,project.Fps,SilenceCutPlanner.TranscriptKey(transcript.CacheKey,project.Fps)));
        }
        IResult VerifySilence(Clip clip,EditorProject project,SilenceVerifyRequest req)
        {
            var transcript=EffectiveTranscript.Resolve(project,clip.Id,store.TranscriptJson(clip.EventId));
            return File.Exists(clip.Path)&&SourceSilencePreviewService.Signature(clip.Path)==req.SourceSignature
                &&SilenceCutPlanner.TranscriptKey(transcript.CacheKey,project.Fps)==req.TranscriptCacheKey
                ?Results.Ok(new {valid=true}):Results.Conflict(new {error="源素材或字幕保护依据已变化，请重新检测停顿"});
        }
        app.MapPost("/api/studio/clips/{id}/silence-preview",async (string id,SilencePreviewRequest req,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            try{return await PreviewSilence(clip,SilenceProject(clip,req.Project),req,token);}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(NotSupportedException ex){return Results.Json(new {error=ex.Message},statusCode:422);}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
        });
        app.MapPost("/api/studio/clips/{id}/silence-preview/verify",(string id,SilenceVerifyRequest req)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            return VerifySilence(clip,SilenceProject(clip,req.Project),req);
        });
        app.MapPost("/api/studio/projects/{projectId}/sources/{sourceId}/silence-preview",async (string projectId,string sourceId,SilencePreviewRequest req,CancellationToken token)=>
        {
            try
            {
                if(req.Project is not {} json)return Results.BadRequest(new {error="需要当前工程快照"});
                var service=IndependentRoughCut();var clip=service.Source(projectId,sourceId);
                return await PreviewSilence(clip,service.ResolveSnapshot(projectId,sourceId,EditorProjectCodec.Deserialize(json)),req,token);
            }
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(NotSupportedException ex){return Results.Json(new {error=ex.Message},statusCode:422);}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
        });
        app.MapPost("/api/studio/projects/{projectId}/sources/{sourceId}/silence-preview/verify",(string projectId,string sourceId,SilenceVerifyRequest req)=>
        {
            try
            {
                if(req.Project is not {} json)return Results.BadRequest(new {error="需要当前工程快照"});
                var service=IndependentRoughCut();var clip=service.Source(projectId,sourceId);
                return VerifySilence(clip,service.ResolveSnapshot(projectId,sourceId,EditorProjectCodec.Deserialize(json)),req);
            }
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
        });
        AiConnection ResolveConnection(AiConnection requested)=>AiConnectionResolver.Resolve(requested,settings);
        // R51-Y2b：clip 绑定到 studio，revision 以 editor_projects.revision 为准；StorageSchemaTooNewException 派生自
        // InvalidOperationException，必须在任何通用 InvalidOperationException 处理之前单独映射。
        IResult EditorTransaction(string id,Func<IResult> action)
        {
            try{return EditorRevisionStore.Serialized<IResult>(studio,id,action);}
            catch(StorageSchemaTooNewException storageTooNew)
            {
                return Results.Conflict(new {error=new {code=storageTooNew.Code,message=storageTooNew.Message,schemaVersion=storageTooNew.SchemaVersion,supportedVersion=storageTooNew.SupportedVersion},code=StudioErrorCodes.StorageSchemaTooNew});
            }
            catch(EditorRevisionConflictException ex)
            {
                return Results.Conflict(new {error=new {code=ex.Code,message=ex.Message,holder=ex.Holder},revision=ex.Revision,code=StudioErrorCodes.ForConflict(ex.Code)});
            }
            catch(EditorProjectSchemaTooNewException projectTooNew)
            {
                return Results.Conflict(new {error=new {code=projectTooNew.Code,message=projectTooNew.Message,schemaVersion=projectTooNew.SchemaVersion,supportedVersion=projectTooNew.SupportedVersion},code=StudioErrorCodes.ProjectSchemaTooNew});
            }
            catch(ReplicaExtensionException extensionInvalid)
            {
                return Results.Json(new {error=new {code=extensionInvalid.Code,message=extensionInvalid.Message,fieldPath=extensionInvalid.FieldPath},code=extensionInvalid.Code},statusCode:StatusCodes.Status422UnprocessableEntity);
            }
            catch(EditorProjectSchemaException projectSchemaInvalid)
            {
                return Results.Json(new {error=new {code=projectSchemaInvalid.Code,message=projectSchemaInvalid.Message},code=StudioErrorCodes.ProjectSchemaInvalid},statusCode:StatusCodes.Status422UnprocessableEntity);
            }
        }
        // 读盘必过 codec：schemaVersion 过新 fail closed（409），非法 → 422；之后才反序列化成 DTO。
        static EditorProject LoadProject(string json)=>EditorProjectCodec.Deserialize(json);
        IResult RevisionConflict(int revision)=>
            Results.Conflict(new {error=new {code="stale-revision",message=$"工程已被修改（当前 revision {revision}），请重新读取后再提交"},revision,code=StudioErrorCodes.RevisionConflict});
        IResult? StaleRevision(string id,int expected)
        {
            var revision=EditorRevisionStore.Current(id);
            return expected!=revision?RevisionConflict(revision):null;
        }
        // 写路由（PUT editor / commands / undo / redo）的前置条件，在事务内、会话检查之前执行：
        // X-Base-Revision（commands 也可放在 body.baseRevision，两者都给时必须相等）缺失 → 428；非十进制非负整数或两处不一致 → 400；
        // X-Editor-Epoch 只要发了就必须等于当前 epoch → 否则 409 stale_epoch。render 使用 required:false，缺失 revision 时捕获当前值。
        IResult? WritePrecondition(string id,HttpRequest request,int? bodyRevision,out int baseRevision,bool required=true)
        {
            baseRevision=0;
            var header=request.Headers["X-Base-Revision"];
            int? fromHeader=null;var malformed=false;
            if(header.Count>0)
            {
                if(header.Count==1&&int.TryParse(header[0],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var parsed))fromHeader=parsed;
                else malformed=true;
            }
            if(bodyRevision is <0)malformed=true;
            if(fromHeader is { } headerValue&&bodyRevision is { } bodyValue&&headerValue!=bodyValue)malformed=true;
            if(malformed)
                return Results.Json(new {error=new {code="invalid-revision",message="X-Base-Revision 必须是非负十进制整数（与 body.baseRevision 同时出现时必须相等）"},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.InvalidRevision},statusCode:StatusCodes.Status400BadRequest);
            var expected=fromHeader??bodyRevision;
            if(required&&expected is null)
                return Results.Json(new {error=new {code="precondition-required",message="写入必须携带 X-Base-Revision：先读取编辑器工程拿到 revision"},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.PreconditionRequired},statusCode:StatusCodes.Status428PreconditionRequired);
            var epoch=EditorRevisionStore.EpochFor(studio);
            var sentEpoch=request.Headers["X-Editor-Epoch"];
            if(sentEpoch.Count>0&&!string.Equals(sentEpoch.ToString(),epoch,StringComparison.Ordinal))
                return Results.Conflict(new {error=new {code="stale-epoch",message="服务端已重启，撤销历史与编辑会话已失效，请重新读取工程"},revision=EditorRevisionStore.Current(id),epoch,code=StudioErrorCodes.StaleEpoch});
            baseRevision=expected??EditorRevisionStore.Current(id);
            return null;
        }
        void EpochHeader(HttpResponse response)=>response.Headers["X-Editor-Epoch"]=EditorRevisionStore.EpochFor(studio);
        static string? SessionId(HttpRequest request)=>request.Headers.TryGetValue("X-Session-Id",out var value)?value.ToString():null;
        app.MapGet("/api/studio/videos",()=>Results.Ok(studio.LocalVideos()));
        app.MapPost("/api/studio/import",async ([FromBody] ImportFolders body,CancellationToken token)=>
            Results.Ok(await mediaService.ImportAsync(body.VideoDirectory,body.SubtitleDirectory,body.OutputDirectory,token)));
        app.MapPost("/api/studio/import-paths",async ([FromBody] ImportFilesRequest body,CancellationToken token)=>
            Results.Ok(await mediaService.ImportPathsAsync(body.Paths,body.OutputDirectory,token)));
        app.MapPost("/api/studio/import-files",async (IFormFileCollection files,string? outputDirectory,CancellationToken token)=>
        {
            if(files.Count is <1 or >200)return Results.BadRequest(new {error="单次请拖入 1–200 个视频或字幕文件"});
            var folder=Path.Combine(Path.GetDirectoryName(store.DatabasePath)!,"imported-media",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            var paths=new List<string>();
            foreach(var file in files)
            {
                var name=Path.GetFileName(file.FileName);var ext=Path.GetExtension(name).ToLowerInvariant();
                if(ext is not (".mp4" or ".mkv" or ".mov" or ".flv" or ".webm" or ".m4v" or ".ts" or ".srt" or ".vtt" or ".ass" or ".ssa"))throw new ArgumentException("拖入的视频或字幕格式不受支持");
                var path=Path.Combine(folder,name);if(File.Exists(path))throw new ArgumentException("同一次导入请避免重名文件");
                await using(var stream=File.Create(path))await file.CopyToAsync(stream,token);paths.Add(path);
            }
            return Results.Ok(await mediaService.ImportPathsAsync(paths,outputDirectory,token));
        }).DisableAntiforgery();
        app.MapPost("/api/studio/clips/{id}/source-srt",async (string id,IFormFile file,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            if(file.Length is <1 or >50_000_000 || !Path.GetExtension(file.FileName).Equals(".srt",StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new {error="请选择不超过 50 MB 的 SRT 文件。"});
            using var reader=new StreamReader(file.OpenReadStream());
            var result=await mediaService.ImportSourceSrtIfAbsentAsync(clip,await reader.ReadToEndAsync(token),token);
            if(result.Error=="invalid_source_srt")return Results.BadRequest(new {error=result.Error});
            return result.Error is null?Results.Ok(result.Transcript):Results.Conflict(new {error=result.Error});
        }).DisableAntiforgery();
        app.MapPost("/api/studio/clips/{id}/subtitle",async (string id,IFormFile file,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            if(file.Length is <1 or >50_000_000)return Results.BadRequest(new {error="字幕文件需小于 50 MB"});
            var extension=Path.GetExtension(file.FileName).ToLowerInvariant();
            if(extension is not (".srt" or ".vtt" or ".ass" or ".ssa"))return Results.BadRequest(new {error="仅支持 SRT、VTT、ASS 字幕"});
            var path=Path.Combine(Path.GetTempPath(),"liveclipper-"+Guid.NewGuid().ToString("N")+extension);
            try
            {
                await using(var output=File.Create(path))await file.CopyToAsync(output,token);
                return Results.Ok(await mediaService.AttachSubtitleAsync(clip,path,token));
            }
            finally{if(File.Exists(path))File.Delete(path);}
        }).DisableAntiforgery();
        app.MapPost("/api/studio/clips/{id}/transcribe",(string id,[FromServices] LiveCoordinator coordinator)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            // 导入视频只登记 LocalVideo，不创建 HighlightEvent；两种来源共用 clip.EventId 排队。
            if(!store.Highlights().Any(x=>x.Id==clip.EventId)&&!studio.LocalVideos().Any(x=>x.ClipId==clip.Id))return Results.NotFound(new {error="视频对应的事件不存在"});
            var receipt=coordinator.QueueTranscriptionReceipt(clip.EventId);return Results.Ok(new {queued=true,jobId=receipt.JobId,disposition=receipt.Disposition});
        });
        app.MapPut("/api/studio/clips/{id}/transcript",async (string id,[FromBody] SubtitleUpdate body,CancellationToken token)=>
        {
            var clip=ClipById(id);return clip is null?Results.NotFound(new {error="视频不存在"}):Results.Ok(await mediaService.SaveTranscriptAsync(clip,body.Segments,body.Language,token));
        });
        // 加入字幕：把软件内另一个已转译视频的字幕复制到当前视频（本地字幕文件之外的第二种来源）。
        app.MapPost("/api/studio/clips/{id}/transcript-from",async (string id,[FromBody] TranscriptFromRequest body,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            var source=ClipById(body.SourceClipId);if(source is null)return Results.NotFound(new {error="源视频不存在"});
            if(source.Id==clip.Id)return Results.BadRequest(new {error="源视频就是当前视频"});
            var json=store.TranscriptJson(source.EventId);
            if(json is null)return Results.BadRequest(new {error="源视频还没有转译字幕"});
            var preview=TranscriptExport.Parse(json);
            if(preview.Segments.Count==0)return Results.BadRequest(new {error="源视频的字幕为空"});
            return Results.Ok(await mediaService.SaveTranscriptAsync(clip,preview.Segments,preview.Language,token));
        });
        app.MapPost("/api/studio/clips/{id}/transcript/clean",(string id)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            var json=store.TranscriptJson(clip.EventId);if(json is null)return Results.NotFound(new {error="没有转录文字"});
            var preview=TranscriptExport.Parse(json);var cleaned=SubtitleTools.Clean(preview.Segments);
            return Results.Ok(new TranscriptPreview(preview.Language,cleaned,string.Join(Environment.NewLine,cleaned.Select(x=>x.Text))));
        });
        app.MapGet("/api/studio/events",(string clipId,string? keyword)=>Results.Ok(studio.Events(clipId,keyword)));
        app.MapPost("/api/studio/events",([FromBody] EventCreate body)=>
        {
            if(ClipById(body.ClipId) is null)return Results.NotFound(new {error="视频不存在"});
            var item=new StudioEvent(Guid.NewGuid().ToString("N"),body.ClipId,body.Start,body.End,body.Title,body.Summary??"",body.Hook??"","manual");studio.SaveEvent(item);return Results.Ok(item);
        });
        app.MapPost("/api/studio/events/merge",([FromBody] EventMerge body)=>Results.Ok(studio.MergeEvents(body.ClipId,body.Ids)));
        app.MapPost("/api/studio/events/extract",([FromBody] ClipSelection body)=>
        {
            var clip=ClipById(body.ClipId);return clip is null?Results.NotFound(new {error="视频不存在"}):Results.Ok(mediaService.ExtractEvents(clip));
        });
        app.MapPost("/api/ai/models",async ([FromBody] AiConnection body,CancellationToken token)=>Results.Ok(await ai.ModelsAsync(body,token)));
        app.MapGet("/api/ai/download",()=>Results.Ok(ai.DownloadState));
        app.MapGet("/api/ai/runtime",async (CancellationToken token)=>Results.Ok(await OllamaRuntime.StatusAsync(token)));
        app.MapPost("/api/ai/runtime/start",async (CancellationToken token)=>{await OllamaRuntime.EnsureAsync(token);return Results.Ok(await OllamaRuntime.StatusAsync(token));});
        app.MapPost("/api/ai/download",([FromBody] ModelPullRequest body)=>ai.StartPull(body.Model)?Results.Ok(ai.DownloadState):Results.Conflict(new {error="已有模型正在下载"}));
        app.MapPost("/api/ai/download/cancel",()=>{ai.CancelPull();return Results.Ok(new {cancelled=true});});
        app.MapPost("/api/ai/analyze",async ([FromBody] AiAnalyzeRequest body,CancellationToken token)=>
        {
            // 返回事件列表 + 整场总结/发布文案/切片建议；建议会随视频保存，编辑器重开后仍能看到。
            var clip=ClipById(body.ClipId);if(clip is null)return Results.NotFound(new {error="视频不存在"});
            var result=await ai.AnalyzeAsync(clip,ResolveConnection(body.Connection),token);
            return Results.Ok(new {result.Events,result.Advice,result.AdviceError});
        });
        // 重跑最终排序：复用分块缓存，只重做合并/排序/精修/边界优化，不重复消耗分块 LLM 调用。
        app.MapPost("/api/ai/rerank",async ([FromBody] AiAnalyzeRequest body,CancellationToken token)=>
        {
            var clip=ClipById(body.ClipId);if(clip is null)return Results.NotFound(new {error="视频不存在"});
            var events=await pipeline.RerankAsync(clip,ResolveConnection(body.Connection),token);
            return Results.Ok(new {events});
        });
        app.MapGet("/api/studio/clips/{id}/ranking",(string id)=>
        {
            var clip=ClipById(id);
            var json=studio.Document("clip-ranking",id);
            if(clip is null||json is null)return Results.NotFound(new {error="还没有排序结果，请先运行 AI 分析"});
            try
            {
                var ranking=JsonSerializer.Deserialize<ClipRanking>(json);
                if(ranking is null)return Results.NotFound(new {error="排序结果已过期，请重新分析"});
                if(!string.Equals(ranking.SkillVersion,AiSkillText.SubtitleAnalysisVersion,StringComparison.Ordinal))return Results.NotFound(new {error="分析技能已更新，请重新分析"});
                if(ranking.TranscriptCacheKey is { } cachedKey)
                {
                    var effective=EffectiveTranscript.FromProjectJson(studio.Project(id),id,store.TranscriptJson(clip.EventId));
                    if(!string.Equals(cachedKey,effective.CacheKey,StringComparison.Ordinal))return Results.NotFound(new {error="字幕已更新，请重新分析"});
                }
                return Results.Ok(JsonSerializer.Deserialize<JsonElement>(json));
            }
            catch(JsonException){return Results.NotFound(new {error="排序结果已过期，请重新分析"});}
        });
        app.MapGet("/api/assets",()=>Results.Ok(studio.Assets()));
        app.MapGet("/api/assets/{id}/file",(string id)=>
        {
            var asset=studio.Assets().FirstOrDefault(x=>x.Id==id);
            return asset is null||!File.Exists(asset.Path)?Results.NotFound(new {error="音频文件不存在"}):Results.File(asset.Path,ContentType(asset.Path),enableRangeProcessing:true);
        });
        app.MapPost("/api/assets/import",(IFormFile file,string kind,string? style)=>
        {
            if(file.Length is <1 or >80_000_000)return Results.BadRequest(new {error="音频文件需小于 80 MB"});
            using var stream=file.OpenReadStream();return Results.Ok(audio.Import(file.FileName,stream,kind,style??"自定义"));
        }).DisableAntiforgery();
        ProjectPackageUploadEndpoints.Map(app,studio,()=>Path.Combine(string.IsNullOrWhiteSpace(settings.ExportOutputDirectory)?Path.GetDirectoryName(store.DatabasePath)!:settings.ExportOutputDirectory,"project-packages"));
        var packageServices=new System.Collections.Concurrent.ConcurrentDictionary<string,ProjectPackageService>(StringComparer.Ordinal);
        ProjectPackageService Packages()
        {
            var root=string.IsNullOrWhiteSpace(settings.ExportOutputDirectory)?Path.GetDirectoryName(store.DatabasePath)!:settings.ExportOutputDirectory;
            var packageRoot=Path.Combine(root,"project-packages");
            return packageServices.GetOrAdd(packageRoot,path=>new ProjectPackageService(studio,path));
        }
        app.MapPost("/api/studio/clips/{id}/jianying-export",async (string id,[FromBody] ProjectCopyRequest body,HttpRequest request,CancellationToken token)=>
        {
            var identity=studio.GetIndependentProject(id)?.Identity;
            if(identity is null&&ClipById(id) is null)return MissingMediaProject(id);
            EpochHeader(request.HttpContext.Response);
            var rejected=EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,null,out var expected) is {} invalid)return invalid;
                if(!request.Headers.ContainsKey("X-Editor-Epoch"))return Results.Json(new {error="导出须携带当前编辑纪元"},statusCode:428);
                if(StaleRevision(id,expected) is {} stale)return stale;
                return Results.Ok();
            });
            if(rejected is not Microsoft.AspNetCore.Http.HttpResults.Ok)return rejected;
            try
            {
                var root=string.IsNullOrWhiteSpace(settings.ExportOutputDirectory)?settings.StorageRoot:settings.ExportOutputDirectory;
                var service=new JianyingExportService(store,media,settings.PythonPath,Path.Combine(AppContext.BaseDirectory,"ai-worker","export_jianying_v3.py"),Path.Combine(root,"jianying-exports"));
                return Results.Ok(await service.ExportAsync(id,body.Name,EditorProjectCodec.Deserialize(body.Project),token,projectIdentity:identity));
            }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or JsonException or EditorProjectSchemaException or ReplicaExtensionException)
            {return Results.Json(new {error=ex.Message,code="jianying_export_unsupported"},statusCode:422);}
            catch(Exception ex) when(ex is IOException or InvalidOperationException)
            {Console.Error.WriteLine("[jianying-export] "+ex);return Results.Json(new {error="剪映候选草稿生成失败："+ex.Message,code="jianying_export_failed"},statusCode:500);}
        });
        app.MapPost("/api/studio/clips/{id}/premiere-export",async (string id,[FromBody] PremiereExportRequest body,HttpRequest request,CancellationToken token)=>
        {
            var identity=studio.GetIndependentProject(id)?.Identity;
            if(identity is null&&ClipById(id) is null)return MissingMediaProject(id);
            EpochHeader(request.HttpContext.Response);
            var rejected=EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,null,out var expected) is {} invalid)return invalid;
                if(!request.Headers.ContainsKey("X-Editor-Epoch"))return Results.Json(new {error="导出须携带当前编辑纪元"},statusCode:428);
                if(StaleRevision(id,expected) is {} stale)return stale;
                return Results.Ok();
            });
            if(rejected is not Microsoft.AspNetCore.Http.HttpResults.Ok)return rejected;
            try
            {
                var root=string.IsNullOrWhiteSpace(settings.ExportOutputDirectory)?settings.StorageRoot:settings.ExportOutputDirectory;
                var service=new PremiereExportService(store,studio,media,Path.Combine(root,"premiere-exports"));
                return Results.Ok(await service.ExportAsync(id,body.Name,EditorProjectCodec.Deserialize(body.Project),token,body.NormalizeVariableFrameRate,projectIdentity:identity));
            }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or JsonException or EditorProjectSchemaException or ReplicaExtensionException)
            {return Results.Json(new {error=ex.Message,code="premiere_export_unsupported"},statusCode:422);}
            catch(InvalidOperationException ex){Console.Error.WriteLine("[premiere-export] "+ex);return Results.Json(new {error="素材元数据读取或 CFR 转换验证失败，未发布完成目录；原工程未修改。",code="premiere_export_conversion_failed"},statusCode:422);}
            catch(IOException ex){Console.Error.WriteLine("[premiere-export] "+ex);return Results.Json(new {error="Premiere 素材复制或文件写入失败；原工程未修改，未发布完成目录。",code="premiere_export_io_failed"},statusCode:500);}
        });
        app.MapPost("/api/studio/clips/{id}/project-package",async (string id,[FromBody] ProjectCopyRequest body,HttpRequest request,CancellationToken token)=>
        {
            if(ClipById(id) is null&&studio.GetIndependentProject(id) is null)return Results.NotFound();
            EpochHeader(request.HttpContext.Response);long revision=0;
            var rejected=EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,null,out var expected) is { } invalid)return invalid;
                if(!request.Headers.ContainsKey("X-Editor-Epoch"))return Results.Json(new {error="保存工程包必须携带当前编辑纪元",code=StudioErrorCodes.PreconditionRequired},statusCode:428);
                if(StaleRevision(id,expected) is { } stale)return stale;
                revision=expected;return Results.Ok();
            });
            if(rejected is not Microsoft.AspNetCore.Http.HttpResults.Ok)return rejected;
            try
            {
                var result=await Packages().ExportAsync(id,body.Name,EditorProjectCodec.Deserialize(body.Project),revision,token);
                return Results.Ok(new {downloadUrl="/api/studio/project-packages/"+result.Token+"/file",fileName=result.FileName});
            }
            catch(ProjectCopyConflictException ex){return Results.Conflict(new {error=ex.Message,code=StudioErrorCodes.RevisionConflict});}
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or JsonException or EditorProjectSchemaException or ReplicaExtensionException or InvalidDataException)
            {return Results.Json(new {error=ex.Message,code="project_package_invalid"},statusCode:422);}
            catch(IOException ex){Console.Error.WriteLine("[project-package] "+ex);return Results.Json(new {error="工程包文件写入失败，原工程未修改。",code="project_package_io_failed"},statusCode:500);}
        });
        app.MapGet("/api/studio/project-packages/{packageId}/file",(string packageId)=>
        {
            var current=Packages();
            foreach(var service in packageServices.Values.Prepend(current).Distinct())
                if(service.DownloadPath(packageId) is {} path)return Results.File(path,"application/zip",service.DownloadFileName(packageId),enableRangeProcessing:true);
            return Results.NotFound(new {error="工程包不存在或尚未完成"});
        });
        app.MapPost("/api/studio/project-packages/import",async (HttpRequest request,HttpResponse response,CancellationToken token)=>
        {
            EpochHeader(response);
            try
            {
                if(!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType,out var mediaType)||
                    !string.Equals(mediaType.MediaType.Value,"multipart/form-data",StringComparison.OrdinalIgnoreCase)||
                    !mediaType.Boundary.HasValue)return Results.BadRequest(new {error="请选择 .lcproj 工程包文件"});
                var boundary=Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value!;
                var reader=new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary,request.Body){BodyLengthLimit=ProjectPackageService.MaximumBytes};
                while(await reader.ReadNextSectionAsync(token) is {} section)
                {
                    if(Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(section.ContentDisposition,out var disposition)&&
                        Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(disposition.Name).Value=="file"&&
                        (disposition.FileName.HasValue||disposition.FileNameStar.HasValue))
                        return Results.Ok(await Packages().ImportAsync(section.Body,token));
                }
                return Results.BadRequest(new {error="工程包请求没有文件"});
            }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or JsonException or EditorProjectSchemaException or ReplicaExtensionException or InvalidDataException)
            {return Results.Json(new {error=ex.Message,code="project_package_invalid"},statusCode:422);}
            catch(IOException ex){Console.Error.WriteLine("[project-package] "+ex);return Results.Json(new {error="工程包打开失败，未登记不完整工程。",code="project_package_io_failed"},statusCode:500);}
        }).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(ProjectPackageService.MaximumBytes));
        app.MapGet("/api/studio/clips/{id}/roughcut-restore",(string id)=>
            Results.Ok(new {available=studio.Document("roughcut-restore",id) is not null}));
        app.MapPost("/api/studio/clips/{id}/roughcut-restore",(string id,HttpRequest request)=>EditorTransaction(id,()=>
        {
            if(WritePrecondition(id,request,null,out var expected) is {} invalid)return invalid;
            if(!request.Headers.ContainsKey("X-Editor-Epoch"))return Results.Json(new {error="恢复须携带当前编辑纪元"},statusCode:428);
            if(StaleRevision(id,expected) is {} stale)return stale;
            try{return Results.Ok(new RoughCutCopyService(store,studio,"").Restore(id,expected,SessionId(request)));}
            catch(ProjectCopyConflictException ex){return Results.Conflict(new {error=ex.Message});}
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or JsonException){return Results.Json(new {error=ex.Message},statusCode:422);}
        }));
        app.MapPost("/api/studio/clips/{id}/roughcut-copy",async (string id,RoughCutCopyRequest body,HttpRequest request,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            if(body.BeforeRanges is null)return Results.BadRequest(new {error="必须提供生成前所选区间，以便准确恢复"});
            EpochHeader(request.HttpContext.Response);long revision=0;
            var rejected=EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,null,out var expected) is {} invalid)return invalid;
                if(!request.Headers.ContainsKey("X-Editor-Epoch"))return Results.Json(new {error="创建独立粗剪须携带编辑纪元"},statusCode:428);
                if(StaleRevision(id,expected) is {} stale)return stale;
                revision=expected;return Results.Ok();
            });
            if(rejected is not Microsoft.AspNetCore.Http.HttpResults.Ok)return rejected;
            try
            {
                var project=EditorProjectCodec.Deserialize(body.Project);
                var duration=await media.DurationAsync(clip.Path,token);token.ThrowIfCancellationRequested();
                var root=string.IsNullOrWhiteSpace(settings.ExportOutputDirectory)?Path.GetDirectoryName(store.DatabasePath)!:settings.ExportOutputDirectory;
                var service=new RoughCutCopyService(store,studio,Path.Combine(root,"roughcut-copies"));
                return Results.Ok(await service.CreateAsync(clip,body.Name,project,duration,body.Ranges,body.SourceSignature,body.CandidateHash,body.TranscriptCacheKey,revision,token,body.BeforeRanges,body.SceneBoundaryIndexes,body.TransitionDurationSeconds));
            }
            catch(ProjectCopyConflictException ex){return Results.Conflict(new {error=ex.Message});}
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or JsonException or EditorProjectSchemaException or ReplicaExtensionException)
            {return Results.Json(new {error=ex.Message},statusCode:422);}
            catch(IOException){return Results.Json(new {error="独立素材复制失败，原工程未修改，未发布不完整粗剪"},statusCode:500);}
        });
        app.MapPost("/api/studio/clips/{id}/copy",async (string id,[FromBody] ProjectCopyRequest body,HttpRequest request,CancellationToken token)=>
        {
            if(ClipById(id) is null&&studio.GetIndependentProject(id) is null)return Results.NotFound();
            EpochHeader(request.HttpContext.Response);
            long revision=0;
            var rejected=EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,null,out var expected) is { } invalid)return invalid;
                if(!request.Headers.ContainsKey("X-Editor-Epoch"))return Results.Json(new {error="创建副本必须携带当前编辑纪元",code=StudioErrorCodes.PreconditionRequired},statusCode:428);
                if(StaleRevision(id,expected) is { } stale)return stale;
                revision=expected;return Results.Ok();
            });
            if(rejected is not Microsoft.AspNetCore.Http.HttpResults.Ok)return rejected;
            try
            {
                var project=EditorProjectCodec.Deserialize(body.Project);
                var copies=new ProjectCopyService(studio,Path.Combine(RenderStoragePaths.OutputRoot(Path.GetDirectoryName(store.DatabasePath)!,Path.GetDirectoryName(store.DatabasePath)!,settings.ExportOutputDirectory),"project-copies"));
                return Results.Ok(await copies.CopyAsync(id,body.Name,project,revision,token));
            }
            catch(ProjectCopyConflictException ex){return Results.Conflict(new {error=ex.Message,code=StudioErrorCodes.RevisionConflict});}
            catch(StorageSchemaTooNewException ex){return Results.Conflict(new {error=ex.Message,code=StudioErrorCodes.StorageSchemaTooNew});}
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or JsonException or EditorProjectSchemaException or ReplicaExtensionException)
            {return Results.Json(new {error=ex.Message,code="project_copy_unsupported"},statusCode:422);}
            catch(IOException ex)
            {
                Console.Error.WriteLine("[project-copy] "+ex);
                return Results.Json(new {error="副本文件复制失败，原工程未修改，未登记不完整副本。",code="project_copy_io_failed"},statusCode:500);
            }
        });
        app.MapGet("/api/studio/clips/{id}/editor",async (string id,HttpResponse response,CancellationToken token)=>
        {
            if(studio.GetIndependentProject(id) is {} independent)
            {
                EpochHeader(response);
                return EditorTransaction(id,()=>
                {
                    var current=studio.GetIndependentProject(id)!;
                    return Results.Ok(new {projectIdentity=current.Identity,clip=(object?)null,duration=0,
                        width=current.Identity.Width,height=current.Identity.Height,fps=current.Identity.Fps,hasAudio=false,
                        transcript=(object?)null,project=LoadProject(current.Document.Json),revision=checked((int)current.Document.Revision),
                        epoch=EditorRevisionStore.EpochFor(studio),stems=(object?)null,events=Array.Empty<StudioEvent>(),advice=(object?)null});
                });
            }
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            var duration=await media.DurationAsync(clip.Path,token);
            var info=await media.ProbeAsync(clip.Path,token);
            var json=store.TranscriptJson(clip.EventId);
            EpochHeader(response);
            return EditorTransaction(id,()=>
            {
                // json 与 revision 出自同一条 SELECT；codec 先于反序列化。
                var record=studio.GetProjectRecord(id);
                var project=record is null?new EditorProject{Start=0,End=duration,Timeline=[new TimelineItem("source",id,0,duration)]}:LoadProject(record.Json);
                var revision=record is null?0:checked((int)record.Revision);
                return Results.Ok(new {clip,duration,width=project.CanvasWidth??info.Width,height=project.CanvasHeight??info.Height,hasAudio=info.HasAudio,transcript=json is null?null:TranscriptExport.Parse(json),project,revision,epoch=EditorRevisionStore.EpochFor(studio),stems=studio.Stems(id),events=studio.Events(id),advice=ai.SavedAdvice(id)});
            });
        });
        app.MapPut("/api/studio/clips/{id}/editor",(string id,[FromBody] JsonElement submitted,HttpRequest request)=>
        {
            var independent=studio.GetIndependentProject(id);
            if(independent is null&&ClipById(id) is null)return Results.NotFound(new {error="视频不存在",code=StudioErrorCodes.NotFound});
            EpochHeader(request.HttpContext.Response);
            return EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,null,out var baseRevision) is { } rejected)return rejected;
                var admittedAt=DateTimeOffset.UtcNow;
                EditorRevisionStore.EnsureSessionForPut(id,SessionId(request),admittedAt);
                if(StaleRevision(id,baseRevision) is { } stale)return stale;
                var previous=studio.Project(id);
                // Validate the raw envelope before typed deserialization can discard unsupported fields.
                EditorProject plan;
                try{plan=EditorProjectCodec.Deserialize(submitted);EditorRenderService.ValidateForSave(plan,independent is not null);
                    if(independent is not null&&(plan.Timeline is null||(plan.CanvasWidth is null&&plan.Fps!=independent.Identity.Fps)))
                        throw new ArgumentException("独立工程必须有显式时间线且帧率匹配工程配置。");}
                catch(Exception projectInvalid) when(projectInvalid is ArgumentException or JsonException or NotSupportedException)
                {
                    return Results.Json(new {error=projectInvalid.Message,code=StudioErrorCodes.InvalidProject},statusCode:StatusCodes.Status400BadRequest);
                }
                using var registryRead=MediaRegistryGate.EnterRead();
                var mediaIds=store.Clips().Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
                if(independent is null&&!mediaIds.Contains(id))return Results.NotFound(new {error="视频不存在",code=StudioErrorCodes.NotFound});
                var assetIds=studio.Assets().Select(x=>(x.Id,x.Kind)).ToHashSet();
                var (missing,locations)=StudioProjectMediaRefs.FindMissing(plan,id,mediaIds.Contains,(mediaId,kind)=>assetIds.Contains((mediaId,kind)));
                if(missing.Count>0)
                {
                    return Results.BadRequest(new {error=new {code=StudioErrorCodes.MissingMedia,message="工程引用了不存在的媒体",missing,locations},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.MissingMedia});
                }
                var committed=EditorRevisionStore.Commit(studio,id,JsonSerializer.Serialize(plan),baseRevision);
                if(!committed.Ok)return RevisionConflict(committed.Revision);
                EditorRevisionStore.CompletePutSession(id,admittedAt);
                EditorRevisionStore.RecordWrite(id,previous);
                EditorRevisionStore.TryAdvance(id,baseRevision,out var revision);
                return Results.Ok(new {saved=true,revision});
            });
        });
        // PUT 与 commands 共用一次完整事务；批内命令仍由纯执行器逐条验证。
        app.MapPost("/api/studio/clips/{id}/commands",async (string id,[FromBody] EditorCommandBatch body,HttpRequest request,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            // 媒体探测不占用工程锁；事务内必须重读，不能以探测前的快照提交。
            var needsDefault=EditorRevisionStore.Serialized(id,()=>studio.Project(id) is null);
            var duration=needsDefault?await media.DurationAsync(clip.Path,token):0;
            EpochHeader(request.HttpContext.Response);
            return EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,body.BaseRevision,out var baseRevision) is { } rejected)return rejected;
                EditorRevisionStore.EnsureSession(id,body.SessionId??SessionId(request));
                if(StaleRevision(id,baseRevision) is { } stale)return stale;
                if(body.Commands is null||body.Commands.Count==0)
                    return Results.BadRequest(new {error=new {code="empty-batch",message="commands 不能为空"},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.InvalidCommand});
                using var registryRead=MediaRegistryGate.EnterRead();
                var mediaIds=store.Clips().Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
                if(!mediaIds.Contains(id))return Results.NotFound(new {error="视频不存在",code=StudioErrorCodes.NotFound});
                var missing=StudioCommandMediaRefs.FindMissing(body.Commands,mediaIds.Contains);
                if(missing.Count>0)
                {
                    return Results.BadRequest(new {error=new {code=StudioErrorCodes.MissingMedia,message="命令引用了不存在的媒体",missing},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.MissingMedia});
                }
                var saved=studio.Project(id);
                var current=saved is null?new EditorProject{Start=0,End=duration}
                    :LoadProject(saved)??new EditorProject{Start=0,End=duration};
                EditorProject next;CommittedRecord[] records;
                try
                {
                    (next,records)=EditorCommandExecutor.ApplyBatch(current,body.Commands);
                }
                catch(EditorCommandException ex)
                {
                    return Results.BadRequest(new {error=new {code="invalid-command",message=ex.Message},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.InvalidCommand});
                }
                var assetIds=studio.Assets().Select(x=>(x.Id,x.Kind)).ToHashSet();
                var finalMedia=StudioProjectMediaRefs.FindMissing(next,id,mediaIds.Contains,
                    (mediaId,kind)=>assetIds.Contains((mediaId,kind)));
                if(finalMedia.Missing.Count>0)
                {
                    return Results.BadRequest(new {error=new {code=StudioErrorCodes.MissingMedia,message="工程引用了不存在的媒体",missing=finalMedia.Missing,locations=finalMedia.Locations},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.MissingMedia});
                }
                var committed=EditorRevisionStore.Commit(studio,id,JsonSerializer.Serialize(next),baseRevision);
                if(!committed.Ok)return RevisionConflict(committed.Revision);
                EditorRevisionStore.RecordWrite(id,saved??JsonSerializer.Serialize(current));
                EditorRevisionStore.TryAdvance(id,baseRevision,out var revision);
                return Results.Ok(new {revision,results=records});
            });
        });
        app.MapPost("/api/studio/clips/{id}/sessions",async (string id,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null&&studio.GetIndependentProject(id) is null)return MissingMediaProject(id);
            var needsDefault=EditorRevisionStore.Serialized(id,()=>studio.Project(id) is null);
            var duration=needsDefault&&clip is not null?await media.DurationAsync(clip.Path,token):0;
            return EditorTransaction(id,()=>
            {
                var saved=studio.Project(id);
                var checkpoint=saved??JsonSerializer.Serialize(new EditorProject{Start=0,End=duration});
                var session=EditorRevisionStore.BeginSession(id,checkpoint);
                return Results.Ok(new {sessionId=session.SessionId,checkpointRevision=session.CheckpointRevision});
            });
        });
        app.MapPost("/api/studio/clips/{id}/sessions/{sid}/end",(string id,string sid)=>
        {
            if(ClipById(id) is null&&studio.GetIndependentProject(id) is null)return MissingMediaProject(id);
            return EditorTransaction(id,()=>
            {
                var session=EditorRevisionStore.EndSession(id,sid);
                return Results.Ok(new {ended=true,checkpointRevision=session.CheckpointRevision,revision=EditorRevisionStore.Current(id)});
            });
        });
        // history 也是写入：保存成功后才移到另一栈，并递增 revision，不能复活旧请求。
        app.MapPost("/api/studio/clips/{id}/undo",(string id,HttpRequest request)=>
        {
            if(ClipById(id) is null&&studio.GetIndependentProject(id) is null)return MissingMediaProject(id);
            EpochHeader(request.HttpContext.Response);
            return EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,null,out var baseRevision) is { } rejected)return rejected;
                EditorRevisionStore.EnsureSession(id,SessionId(request),history:true);
                if(StaleRevision(id,baseRevision) is { } stale)return stale;
                var forward=studio.Project(id);
                var snapshot=forward is null?null:EditorRevisionStore.PopSnapshot(id);
                if(snapshot is null)
                    return Results.Conflict(new {error=new {code="nothing-to-undo",message="没有可撤销的步骤"},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.NothingToUndo});
                EditorCommit committed;
                try
                {
                    EditorRenderService.ValidateForSave(LoadProject(snapshot)??throw new InvalidDataException("撤销快照不是有效工程"),studio.GetIndependentProject(id) is not null);
                    committed=EditorRevisionStore.Commit(studio,id,snapshot,baseRevision);
                }
                catch
                {
                    EditorRevisionStore.PushSnapshot(id,snapshot);
                    throw;
                }
                if(!committed.Ok)
                {
                    EditorRevisionStore.PushSnapshot(id,snapshot);
                    return RevisionConflict(committed.Revision);
                }
                EditorRevisionStore.PushRedo(id,forward!);
                EditorRevisionStore.TryAdvance(id,baseRevision,out var revision);
                return Results.Ok(new {restored=true,revision});
            });
        });
        app.MapPost("/api/studio/clips/{id}/redo",(string id,HttpRequest request)=>
        {
            if(ClipById(id) is null&&studio.GetIndependentProject(id) is null)return MissingMediaProject(id);
            EpochHeader(request.HttpContext.Response);
            return EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,null,out var baseRevision) is { } rejected)return rejected;
                EditorRevisionStore.EnsureSession(id,SessionId(request),history:true);
                if(StaleRevision(id,baseRevision) is { } stale)return stale;
                var previous=studio.Project(id);
                var snapshot=previous is null?null:EditorRevisionStore.PopRedo(id);
                if(snapshot is null)
                    return Results.Conflict(new {error=new {code="nothing-to-redo",message="没有可重做的步骤"},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.NothingToRedo});
                EditorCommit committed;
                try
                {
                    EditorRenderService.ValidateForSave(LoadProject(snapshot)??throw new InvalidDataException("重做快照不是有效工程"),studio.GetIndependentProject(id) is not null);
                    committed=EditorRevisionStore.Commit(studio,id,snapshot,baseRevision);
                }
                catch
                {
                    EditorRevisionStore.PushRedo(id,snapshot);
                    throw;
                }
                if(!committed.Ok)
                {
                    EditorRevisionStore.PushRedo(id,snapshot);
                    return RevisionConflict(committed.Revision);
                }
                EditorRevisionStore.PushSnapshot(id,previous!);
                EditorRevisionStore.TryAdvance(id,baseRevision,out var revision);
                return Results.Ok(new {restored=true,revision});
            });
        });
        app.MapGet("/api/studio/clips/{id}/waveform",async (string id,int? width,int? height,CancellationToken token)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            var w=Math.Clamp(width??900,120,2400);var h=Math.Clamp(height??96,24,240);
            var directory=Path.Combine(Path.GetDirectoryName(store.DatabasePath)!,"waveforms");Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,$"{id}-{w}x{h}.png");
            if(!File.Exists(path))
            {
                try
                {
                    await media.RunAsync(media.Ffmpeg,["-hide_banner","-loglevel","error","-y","-i",clip.Path,"-filter_complex",$"showwavespic=s={w}x{h}:colors=0x8ce0ba:scale=sqrt:draw=full","-frames:v","1",path],token);
                }
                catch
                {
                    return Results.NotFound(new {error="无法生成音频波形"});
                }
            }
            return Results.File(path,"image/png");
        });
        app.MapGet("/api/studio/clips/{id}/project-backups",(string id)=>
            ClipById(id) is null?Results.NotFound(new {error="视频不存在"}):Results.Ok(studio.ProjectRevisions(id)));
        app.MapGet("/api/studio/clips/{id}/project-backups/{fileName}",(string id,string fileName)=>
        {
            if(ClipById(id) is null)return MissingMediaProject(id);
            var json=studio.ProjectRevision(id,fileName);
            if(json is null)return Results.NotFound(new {error="工程快照不存在"});
            // R51-Y2b：旧代码写出的备份可能为空或损坏；先过 codec 再反序列化，坏备份 422，绝不当成空工程交给客户端恢复。
            EditorProject? project=null;string? invalid=null;
            if(string.IsNullOrWhiteSpace(json))invalid="工程快照为空";
            else
            {
                try{project=LoadProject(json);if(project is null)invalid="工程快照无法解析";}
                catch(EditorProjectSchemaTooNewException backupTooNew)
                {
                    return Results.Conflict(new {error=backupTooNew.Message,code=StudioErrorCodes.ProjectSchemaTooNew,schemaVersion=backupTooNew.SchemaVersion,supportedVersion=backupTooNew.SupportedVersion});
                }
                catch(ReplicaExtensionException extensionInvalid)
                {
                    return Results.Json(new {error=new {code=extensionInvalid.Code,message=extensionInvalid.Message,fieldPath=extensionInvalid.FieldPath},code=extensionInvalid.Code},statusCode:StatusCodes.Status422UnprocessableEntity);
                }
                catch(Exception backupError) when(backupError is EditorProjectSchemaException or JsonException or NotSupportedException)
                {
                    invalid=$"工程快照无法解析：{backupError.Message}";
                }
            }
            if(invalid is not null||project is null)return Results.Json(new {error=invalid??"工程快照无法解析",code=StudioErrorCodes.BackupInvalid},statusCode:StatusCodes.Status422UnprocessableEntity);
            return Results.Ok(project);
        });
        app.MapGet("/api/studio/clips/{id}/variants",(string id)=>Results.Ok(studio.Variants(id).Select(x=>new {x.Id,x.Name,x.CreatedAt})));
        app.MapPost("/api/studio/clips/{id}/variants",(string id,[FromBody] VariantRequest body)=>
        {
            if(ClipById(id) is null)return MissingMediaProject(id);
            return EditorTransaction(id,()=>
            {
                EditorProject project;
                try{project=EditorProjectCodec.Deserialize(body.Project);EditorRenderService.Validate(project);}
                catch(Exception projectInvalid) when(projectInvalid is ArgumentException or JsonException or NotSupportedException)
                {
                    return Results.BadRequest(new {error=projectInvalid.Message,code=StudioErrorCodes.InvalidProject});
                }
                var item=new EditorVariant(Guid.NewGuid().ToString("N"),id,body.Name.Length>100?body.Name[..100]:body.Name,EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(project)),DateTimeOffset.UtcNow);
                studio.SaveVariant(item);return Results.Ok(new {item.Id});
            });
        });
        app.MapGet("/api/studio/variants/{id}",(string id)=>studio.Variant(id) is {} item?EditorTransaction(item.ClipId,()=>Results.Ok(new {item.Id,item.Name,project=LoadProject(item.ProjectJson)})):Results.NotFound(new {error="方案不存在"}));
        app.MapDelete("/api/studio/variants/{id}",(string id)=>studio.DeleteVariant(id)?Results.Ok(new {deleted=true}):Results.NotFound(new {error="序列不存在"}));
        // Legacy callers may omit both headers. Each supplied header is enforced under the same clip lock as PUT.
        app.MapPost("/api/studio/clips/{id}/render",(string id,[FromBody] EditorProject plan,HttpRequest request,HttpResponse response)=>
        {
            var clip=ClipById(id);var identity=studio.GetIndependentProject(id)?.Identity;
            if(clip is null&&identity is null)return MissingMediaProject(id);
            EpochHeader(response);
            return EditorTransaction(id,()=>
            {
                if(WritePrecondition(id,request,null,out var expected,required:false) is { } guard)return guard;
                if(StaleRevision(id,expected) is { } stale)return stale;
                try
                {
                    return Results.Ok(identity is null
                        ?editor.Queue(clip!,plan,EditorRevisionStore.Current(id),EditorRevisionStore.EpochFor(studio))
                        :editor.Queue(identity,plan,EditorRevisionStore.Current(id),EditorRevisionStore.EpochFor(studio)));
                }
                catch(ArgumentException ex){return Results.UnprocessableEntity(new {code="editor_render_unsupported",message=ex.Message});}
            });
        });
        app.MapGet("/api/studio/renders",()=>
        {
            var jobs=store.Jobs().ToDictionary(x=>x.Id);
            return Results.Ok(studio.Renders().Select(x=>new {x.Id,x.ClipId,x.ProjectId,x.OutputPath,x.CreatedAt,x.Revision,x.Epoch,status=jobs.GetValueOrDefault(x.Id)?.Status??"unknown",error=jobs.GetValueOrDefault(x.Id)?.Error}));
        });
        app.MapGet("/api/jobs",()=>
        {
            // Job IDs, not EventId or the selected clip, bind a render to its immutable origin.
            var renders=studio.Renders().ToDictionary(x=>x.Id);
            return Results.Ok(store.Jobs().Select(job=>
            {
                var render=job.Type=="edit-render"?renders.GetValueOrDefault(job.Id):null;
                return new {job.Id,job.Type,job.EventId,job.Status,job.Progress,job.Error,job.StartedAt,job.FinishedAt,job.RetryCount,job.Message,
                    clipId=render?.ClipId,projectId=render?.ProjectId,revision=render?.Revision,epoch=render?.Epoch};
            }));
        });
        app.MapGet("/api/studio/renders/{id}/file",(string id)=>
        {
            var render=studio.Renders().FirstOrDefault(x=>x.Id==id);var job=store.Jobs().FirstOrDefault(x=>x.Id==id);
            return render is null||job?.Status!="completed"||!File.Exists(render.OutputPath)?Results.NotFound(new {error="成片尚未生成"}):Results.File(render.OutputPath,ContentType(render.OutputPath),Path.GetFileName(render.OutputPath),enableRangeProcessing:true);
        });
        app.MapPost("/api/studio/clips/{id}/separate",(string id)=>
        {
            var clip=ClipById(id);if(clip is null)return MissingMediaProject(id);
            demucs.Queue(clip);return Results.Ok(new {queued=true});
        });
        app.MapGet("/api/studio/clips/{id}/stems",(string id)=>Results.Ok(new {ready=studio.Stems(id) is { } stems&&File.Exists(stems.VocalsPath)}));
        app.MapGet("/api/studio/clips/{id}/stems/vocals",(string id)=>
        {
            var stems=studio.Stems(id);
            return stems is null||!File.Exists(stems.VocalsPath)?Results.NotFound(new {error="尚未生成人声文件"}):Results.File(stems.VocalsPath,"audio/wav",enableRangeProcessing:true);
        });
    }
    private static string ContentType(string path)=>Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav"=>"audio/wav",".mp3"=>"audio/mpeg",".m4a"=>"audio/mp4",".ogg"=>"audio/ogg",".flac"=>"audio/flac",".aiff" or ".aif"=>"audio/aiff",".png"=>"image/png",".jpg" or ".jpeg"=>"image/jpeg",".webp"=>"image/webp",".gif"=>"image/gif",
        ".mp4"=>"video/mp4",".mov"=>"video/quicktime",".webm"=>"video/webm",".zip"=>"application/zip",_=>"application/octet-stream"
    };
}
