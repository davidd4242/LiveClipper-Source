using System.Security.Cryptography;
using System.Text.Json;
using LiveClipper.App;
using LiveClipper.Core;
using Microsoft.AspNetCore.Mvc;

public sealed record MusicPreferenceRequest(bool AllowDownload,string ModelId);
public sealed record MusicDirectoryRequest(string Path);
public sealed record MusicModelDownloadRequest(string ModelId,bool AllowDownload);
public sealed record MusicModelUninstallPreviewRequest(string ModelId);
public sealed record MusicModelUninstallRequest(string ModelId,string? ExpectedFingerprint,bool Confirmed);
public sealed record MusicGenerateRequest(string ModelId,string Genre,string Style,string Content,string Theme,int DurationSeconds,bool AllowDownload,string? Provider="local");
public sealed record MusicDuckingRequest(double? TargetDb,double? AttackMs,double? ReleaseMs);

public static class MusicEndpoints
{
    public static void Map(WebApplication app,MusicGenerationService music,LiveSettings settings,string configPath,Store? store=null,StudioStore? studio=null,MediaTools? media=null)
    {
        app.MapGet("/api/music/status",()=>Results.Ok(music.Status()));
        app.MapPost("/api/music/preferences",async ([FromBody] MusicPreferenceRequest body)=>
        {
            try{await Save(configPath,settings,()=>music.SetPreferences(body.AllowDownload,body.ModelId));return Results.Ok(music.Status());}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
        });
        app.MapPost("/api/music/models/directory",([FromBody] MusicDirectoryRequest body)=>
        {
            try{music.ImportModelDirectory(body.Path);return Results.Ok(music.Status());}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
        });
        app.MapPost("/api/music/models/download",([FromBody] MusicModelDownloadRequest body)=>
        {
            try{return Results.Ok(music.StartModelDownload(body.ModelId,body.AllowDownload));}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
        });
        // 202 = cancel requested only; poll /api/music/status Download.State until "cancelled" (worker exited) or a failure.
        app.MapPost("/api/music/models/download/cancel",()=>
        {
            try{return Results.Accepted("/api/music/status",new {cancelRequested=true,download=music.CancelModelDownload()});}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
        });
        // Read-only: nothing is deleted. 400 = unknown/builtin model.
        app.MapPost("/api/music/models/uninstall-preview",([FromBody] MusicModelUninstallPreviewRequest body)=>
        {
            try{return Results.Ok(music.PreviewModelRemoval(body.ModelId));}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
        });
        // 400 invalid/builtin/unconfirmed; 409 busy, fingerprint changed or not removable; 500 partial/unknown (body names the real state).
        app.MapPost("/api/music/models/uninstall",([FromBody] MusicModelUninstallRequest body)=>
        {
            try{return Results.Ok(music.RemoveModel(body.ModelId,body.ExpectedFingerprint,body.Confirmed));}
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(MusicModelRemovalException ex){return Results.Json(new {status=ex.Status,error=ex.Message,directoryExists=ex.DirectoryExists,installed=ex.Installed},statusCode:500);}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
        });
        app.MapPost("/api/music/generate",async ([FromBody] MusicGenerateRequest body)=>
        {
            try
            {
                var text=new[]{body.Genre,body.Style,body.Content,body.Theme};
                if(text.Any(string.IsNullOrWhiteSpace))return Results.BadRequest(new {error="请填写曲风、风格、内容和主题"});
                if(text.Any(x=>x.Length>500))return Results.BadRequest(new {error="每个描述字段最多 500 个字符"});
                var provider=string.IsNullOrWhiteSpace(body.Provider)?"local":body.Provider.Trim();
                if(provider!="local"&&provider!="custom-http")return Results.BadRequest(new {error="音乐生成 provider 只能是 local 或 custom-http"});
                // Online generation must not rewrite the local-model preferences.
                if(provider=="local")await Save(configPath,settings,()=>music.SetPreferences(body.AllowDownload,body.ModelId));
                var request=new MusicCreateRequest(body.ModelId??"",body.Genre.Trim(),body.Style.Trim(),body.Content.Trim(),body.Theme.Trim(),body.DurationSeconds,provider);
                MusicOnlineConnection? online=null;
                // Same gate as POST /api/settings/music-generation: url/model/auth/key are copied in one critical section.
                if(provider=="custom-http")online=await StartupDurability.ReadSettingsAsync(configPath,()=>new MusicOnlineConnection(
                    settings.MusicGenerationProvider??"",settings.MusicApiUrl??"",settings.MusicApiModel??"",settings.MusicApiAuthMode??"",settings.MusicApiKey??""));
                return Results.Ok(music.StartGeneration(request,online));
            }
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
        });
        app.MapGet("/api/music/jobs/{id}",(string id)=>music.Job(id) is { } job?Results.Ok(job):Results.NotFound(new {error="音乐任务不存在"}));
        app.MapPost("/api/music/jobs/{id}/cancel",(string id)=>music.CancelJob(id)?Results.Ok(new {cancelled=true}):Results.Conflict(new {error="任务已经结束"}));

        /*
         * v1.1 音乐数据面。分析结果持久化在 studio_documents（scope=music-analysis），
         * 不改 audio_assets 表结构，旧数据库无需迁移；素材列表把分析结果一起返回。
         */
        app.MapGet("/api/music/assets",()=>Results.Ok((studio?.Assets()??[]).Select(asset=>new
        {
            id=asset.Id,
            name=asset.Name,
            kind=asset.Kind,
            style=asset.Style,
            path=asset.Path,
            builtIn=asset.BuiltIn,
            analysis=MusicGenerationService.LoadAnalysis(studio,asset.Id)
        }).ToArray()));

        app.MapPost("/api/music/assets/{id}/analyze",async (string id,CancellationToken token)=>
        {
            var asset=FindAsset(studio,id);
            if(asset is null)return Results.NotFound(new {error="音乐素材不存在"});
            if(media is null)return Results.Conflict(new {error="FFmpeg 不可用，无法分析音频"});
            var path=ResolvePath(settings,asset.Path);
            if(!File.Exists(path))return Results.NotFound(new {error="音频文件不存在",path=asset.Path});
            var analysis=await MusicGenerationService.AnalyzeAudioFileAsync(media,path,MusicGenerationService.DefaultTargetLufs,null,token);
            if(analysis is null)
                return Results.Ok(new {assetId=id,bpm=(double?)null,beats=Array.Empty<double>(),method="unavailable",degraded=true,reason="音频不可解码或节拍不明显，已降级为 null"});
            var lufs=await MusicGenerationService.MeasureLoudnessAsync(media,path,token);
            if(lufs is double value)analysis=analysis with{MeasuredLufs=value,TargetLufs=MusicGenerationService.DefaultTargetLufs,LoudnessMethod="ffmpeg-loudnorm"};
            MusicGenerationService.SaveAnalysis(studio,id,analysis);
            return Results.Ok(analysis);
        });

        app.MapGet("/api/music/assets/{id}/beats",(string id,double? targetSeconds,int? beatsPerBar)=>
        {
            var asset=FindAsset(studio,id);
            if(asset is null)return Results.NotFound(new {error="音乐素材不存在"});
            var analysis=MusicGenerationService.LoadAnalysis(studio,id);
            if(analysis?.Bpm is not double bpm||bpm<=0)
                return Results.Ok(new {assetId=id,bpm=(double?)null,beats=Array.Empty<double>(),degraded=true,reason="尚无 BPM/beat 分析：先调用 POST /api/music/assets/{id}/analyze 或重新生成"});
            var loop=MusicGenerationService.SuggestBeatLoop(bpm,analysis.DurationSeconds,targetSeconds??30,beatsPerBar??4);
            return Results.Ok(new
            {
                assetId=id,
                bpm,
                beatInterval=Math.Round(MusicGenerationService.BeatInterval(bpm),4),
                beats=analysis.Beats,
                beatCount=analysis.Beats.Length,
                bars=analysis.Beats.Length/Math.Max(1,beatsPerBar??4),
                loop,
                alignedCut=analysis.Beats.Length>0?new{beatSeconds=analysis.Beats[0],relativeSeconds=0.0}:null,
                method=analysis.Method,
                confidence=analysis.Confidence,
                durationSeconds=analysis.DurationSeconds,
                degraded=false
            });
        });

        app.MapPost("/api/music/projects/{clipId}/ducking",(string clipId,[FromBody] MusicDuckingRequest? body)=>
        {
            if(store is null||studio is null)return Results.Conflict(new {error="音乐数据面未接线"});
            var clip=store.Clips().FirstOrDefault(x=>x.Id==clipId);
            if(clip is null)return Results.NotFound(new {error="视频不存在"});
            var saved=studio.Project(clipId);
            if(saved is null)return Results.BadRequest(new {error="该视频还没有编辑器工程，请先在编辑器里保存一次"});
            EditorProject? project;
            try{project=JsonSerializer.Deserialize<EditorProject>(saved,DuckingJson);}catch{project=null;}
            if(project is null)return Results.BadRequest(new {error="编辑器工程解析失败"});
            var musicId=project.MusicAssetId;
            var musicAsset=studio.Assets().FirstOrDefault(x=>x.Id==musicId&&x.Kind=="music");
            if(musicAsset is null)
                return Results.Ok(new {clipId,saved=false,degraded=true,reason="当前工程没有配乐：请先在音乐页给工程选择配乐"});
            var targetDb=body?.TargetDb??-14;
            var attack=(body?.AttackMs??120)/1000.0;
            var release=(body?.ReleaseMs??280)/1000.0;
            if(!double.IsFinite(targetDb)||!double.IsFinite(attack)||!double.IsFinite(release))return Results.BadRequest(new {error="ducking 参数必须为有限值"});
            targetDb=Math.Round(Math.Clamp(targetDb,-60,0),2);attack=Math.Clamp(attack,0,2);release=Math.Clamp(release,0,3);
            try
            {
                var snapshot=DuckingSnapshot(store,project,clipId,targetDb,attack,release);
                if(snapshot.Duration<=.5)return Results.BadRequest(new {error="工程时长太短，无法生成 ducking 包络"});
                var merged=MusicGenerationService.MergeVoiceIntervals(snapshot.Voices,0,snapshot.Duration);
                if(merged.Length==0)
                {
                    var empty=new MusicDuckingEnvelope(clipId,musicId,"none",0,[],[],[],["没有有效人声字幕，未生成 ducking 包络；请先转录音频或检查字幕覆盖"],DateTimeOffset.UtcNow);
                    return Results.Ok(new {envelope=empty,saved=false,degraded=true,reason=empty.Warnings[0]});
                }
                var keyframes=MusicGenerationService.BuildDuckingKeyframes(merged,0,snapshot.Duration,targetDb,attack,release);
                return EditorRevisionStore.Serialized(studio,clipId,()=>
                {
                    var applied=new List<string>();
                    EditorRevisionStore.SaveExternal(studio,clipId,"music-ducking",latest=>
                    {
                        var fresh=latest is null?null:JsonSerializer.Deserialize<EditorProject>(latest,DuckingJson);
                        if(fresh is null)throw new InvalidOperationException("工程已更新，请重新生成 ducking 包络");
                        var current=DuckingSnapshot(store,fresh,clipId,targetDb,attack,release);
                        if(current.TranscriptCacheKey!=snapshot.TranscriptCacheKey||current.GeometryKey!=snapshot.GeometryKey)
                            throw new InvalidOperationException("字幕或时间线已更新，请重新生成 ducking 包络");
                        if(fresh.Timeline is not {Length:>0} timeline)return null;
                        var items=timeline.ToArray();
                        for(var slot=0;slot<items.Length;slot++)
                        {
                            var entry=items[slot];
                            if(entry.Disabled||entry.SoundAssetId!=musicId||!current.Starts.TryGetValue(entry.Id,out var start))continue;
                            items[slot]=entry with {VolumeKeyframes=LocalDucking(keyframes,start,EditorRenderService.ItemOutputDuration(entry),snapshot.Duration)};
                            applied.Add(entry.Id);
                        }
                        return applied.Count>0?JsonSerializer.Serialize(fresh with {Timeline=items}):null;
                    });
                    var target=applied.Count>0?"volume-keyframes":"project-document";
                    var warnings=applied.Count>0?Array.Empty<string>():["当前工程没有使用配乐的时间线片段：包络已写入工程文档（studio_documents scope=music-ducking），渲染端尚未消费"];
                    var envelope=new MusicDuckingEnvelope(clipId,musicId,target,Math.Round(targetDb,2),merged,keyframes,applied.ToArray(),warnings,DateTimeOffset.UtcNow);
                    // Keep the envelope and its identity in one row; a parallel request cannot pair old points with a new key.
                    studio.SaveDocument("music-ducking",clipId,JsonSerializer.Serialize(new
                    {
                        envelope.ClipId,envelope.MusicAssetId,envelope.Target,envelope.TargetDb,envelope.VoiceIntervals,envelope.Keyframes,
                        envelope.AppliedItemIds,envelope.Warnings,envelope.CreatedAt,snapshot.TranscriptCacheKey,snapshot.GeometryKey,
                        AttackSeconds=attack,ReleaseSeconds=release
                    },DuckingJson));
                    return Results.Ok(new {envelope,saved=true,degraded=false,voiceIntervals=merged.Length,keyframeCount=keyframes.Length,target});
                });
            }
            catch(ArgumentException ex){return Results.BadRequest(new {error=ex.Message});}
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
        });
        app.MapGet("/api/music/projects/{clipId}/ducking",(string clipId)=>
        {
            if(store is null||studio is null)return Results.NotFound(new {error="该工程还没有 ducking 包络"});
            var json=studio.Document("music-ducking",clipId);
            var saved=studio.Project(clipId);
            if(json is null||saved is null)return Results.NotFound(new {error="该工程还没有 ducking 包络"});
            try
            {
                using var document=JsonDocument.Parse(json);var root=document.RootElement;
                var envelope=JsonSerializer.Deserialize<MusicDuckingEnvelope>(json,DuckingJson);
                var project=JsonSerializer.Deserialize<EditorProject>(saved,DuckingJson);
                if(envelope is null||project is null||!root.TryGetProperty("transcriptCacheKey",out var transcriptKey)
                    ||!root.TryGetProperty("geometryKey",out var geometryKey)||!root.TryGetProperty("attackSeconds",out var attack)
                    ||!root.TryGetProperty("releaseSeconds",out var release))return Results.NotFound(new {error="ducking 包络已过期，请重新生成"});
                var current=DuckingSnapshot(store,project,clipId,envelope.TargetDb,attack.GetDouble(),release.GetDouble());
                return transcriptKey.GetString()==current.TranscriptCacheKey&&geometryKey.GetString()==current.GeometryKey
                    ?Results.Ok(envelope):Results.NotFound(new {error="字幕或时间线已更新，请重新生成 ducking 包络"});
            }
            catch(Exception ex)when(ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
            {return Results.NotFound(new {error="ducking 包络已过期，请重新生成"});}
        });
    }

    private static readonly JsonSerializerOptions DuckingJson=new(JsonSerializerDefaults.Web);
    private sealed record DuckingInput(Dictionary<string,double> Starts,double Duration,MusicVoiceInterval[] Voices,string TranscriptCacheKey,string GeometryKey);

    private static DuckingInput DuckingSnapshot(Store store,EditorProject project,string clipId,double targetDb,double attack,double release)
    {
        var plan=EditorRenderService.FlattenProject(project);
        EditorRenderService.Validate(plan);
        var items=EditorRenderService.Items(plan,clipId);
        var clips=store.Clips().ToDictionary(x=>x.Id,StringComparer.Ordinal);
        var sources=new Dictionary<string,EffectiveTranscriptResult>(StringComparer.Ordinal);
        var originals=new Dictionary<string,string?>(StringComparer.Ordinal);
        foreach(var id in items.Select(x=>x.ClipId).Distinct(StringComparer.Ordinal))
        {
            if(!clips.TryGetValue(id,out var source))throw new ArgumentException("时间线源视频不存在");
            originals[id]=store.TranscriptJson(source.EventId);
            sources[id]=EffectiveTranscript.Resolve(plan,id,originals[id]);
        }
        var selection=plan.SelectedSegments;
        var selectionApplies=!EditorRenderService.HasCaptionOverride(plan,clipId);
        if(selectionApplies&&selection is not null&&originals.TryGetValue(clipId,out var original))
        {
            // SelectedSegments addresses the unnormalized original array, not the effective array.
            try
            {
                var indexed=TranscriptExport.Parse(original??"{\"segments\":[]}").Segments
                    .Select((segment,index)=>(Segment:segment,Index:index))
                    .Where(x=>EditorRenderService.NormalizeCaptions([x.Segment]).Length>0).ToArray();
                var originalSelection=selection;
                selection=indexed.Select((entry,index)=>(entry.Index,EffectiveIndex:index))
                    .Where(x=>originalSelection.Contains(x.Index)).Select(x=>x.EffectiveIndex).ToArray();
            }
            catch(Exception ex)when(ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            {selection=[];}
        }
        var fps=EditorRenderService.TransitionFps(plan);
        var cuts=EditorRenderService.ResolveTransitions(items,plan.Transitions,fps);
        var starts=EditorRenderService.SequenceStarts(items,cuts);
        var duration=EditorRenderService.SequenceDuration(items,cuts);
        // Map each track independently: the legacy no-transition mapper has one cursor per call.
        var voices=items.GroupBy(EditorRenderService.TrackOf).SelectMany(track=>EditorRenderService.MapSubtitles(
            track.ToArray(),id=>sources[id].Segments,clipId,selection,plan.Transitions,fps,selectionApplies))
            .Select(x=>new MusicVoiceInterval(x.Start,x.End)).OrderBy(x=>x.Start).ThenBy(x=>x.End).ToArray();
        var transcriptKey=DuckingHash(sources.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>new {Id=x.Key,x.Value.CacheKey}));
        var geometryKey=DuckingHash(new
        {
            Version="sequence-ducking-v1",plan.MusicAssetId,Fps=fps,plan.SelectedSegments,selectionApplies,
            Items=items.Select(x=>new {x.Id,x.ClipId,x.Start,x.End,x.Kind,x.Track,x.Speed,x.GapBefore,x.Disabled,x.SoundAssetId}),
            plan.Transitions,Duration=duration,TargetDb=targetDb,AttackSeconds=attack,ReleaseSeconds=release
        });
        return new(starts,duration,voices,transcriptKey,geometryKey);
    }

    private static string DuckingHash<T>(T value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,DuckingJson))).ToLowerInvariant();

    private static VolumeKeyframe[] LocalDucking(VolumeKeyframe[] sequence,double start,double duration,double sequenceDuration)
    {
        var global=new TimelineItem("envelope","source",0,sequenceDuration,VolumeKeyframes:sequence);
        double DbAt(double time)=>20*Math.Log10(EditorRenderService.VolumeGainAt(global,time));
        var interior=sequence.Where(x=>x.Time>start&&x.Time<start+duration).Select(x=>new VolumeKeyframe(x.Time-start,x.Db)).ToArray();
        // Reserve both boundary samples before the renderer's 64-point cap.
        if(interior.Length>62)interior=Enumerable.Range(0,62).Select(i=>interior[(int)Math.Round(i*(interior.Length-1)/61.0)]).ToArray();
        return EditorRenderService.NormalizeVolumeKeyframes(interior.Prepend(new(0,DbAt(start))).Append(new(duration,DbAt(start+duration))));
    }

    private static AudioAsset? FindAsset(StudioStore? studio,string id)=>studio?.Assets().FirstOrDefault(x=>x.Id==id);

    private static string ResolvePath(LiveSettings settings,string path)=>Path.IsPathRooted(path)?path:Path.Combine(settings.StorageRoot,path);

    // Preferences mutate the same live object as Program; take its settings gate before the mutation, not just the write.
    private static Task Save(string path,LiveSettings settings,Action update)=>
        StartupDurability.UpdateSettingsAsync(path,settings,()=>
        {
            update();
            return Task.FromResult(true);
        },new JsonSerializerOptions{WriteIndented=true});
}
