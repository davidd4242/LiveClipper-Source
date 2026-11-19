using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Core;

public sealed class ProjectCopyConflictException(string message) : InvalidOperationException(message);
public sealed record ProjectCopyResult(string ClipId,string Name,long Revision);

public sealed class ProjectCopyService(StudioStore studio,string destinationRoot)
{
    public async Task<ProjectCopyResult> CopyAsync(string sourceId,string name,EditorProject project,long expectedRevision,CancellationToken token=default,string? initialRestoreJson=null,bool generatedSourceOnly=false,Action? validateBeforePublish=null)
    {
        name=name.Trim();
        if(name.Length is 0 or >100)throw new ArgumentException("副本名称须为 1–100 个字符");
        var independent=studio.GetIndependentProject(sourceId) is not null;
        ValidateCopyProject(project,independent);
        var sources=new HashSet<string>(StringComparer.Ordinal);
        if(!independent)sources.Add(sourceId);
        var musicIds=new HashSet<string>(StringComparer.Ordinal);
        StudioProjectMediaRefs.FindMissing(project,sourceId,id=>{sources.Add(id);return true;},(id,kind)=>
        {
            if(kind!="music" || initialRestoreJson is not null || generatedSourceOnly)
                throw new NotSupportedException("独立副本仅支持全局配乐，不支持此素材关联；未创建副本。");
            musicIds.Add(id);return true;
        });
        if(musicIds.Count>1)throw new NotSupportedException("工程引用了不同的全局配乐，未创建副本。");
        // Clone before the first await; the caller cannot alter the published snapshot during media copying.
        var detached=StudioProjectMediaRefs.Remap(project,sources.ToDictionary(id=>id,id=>id,StringComparer.Ordinal),
            assetIdMap:musicIds.ToDictionary(id=>id,id=>id,StringComparer.Ordinal));
        // The host loads its global effect directly from the active archive on reopen.
        // A saved archive can still bind it even when the projected effectAssetId is null.
        if(musicIds.Count!=0 && detached.ReplicaExtension is {} archive
            && archive.Document.GetProperty("sequence").TryGetProperty("hostEffect",out var hostEffect)
            && hostEffect.ValueKind==JsonValueKind.Object && hostEffect.TryGetProperty("effectAssetId",out var effect)
            && effect.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(effect.GetString()))
            throw new NotSupportedException("独立配乐副本暂不支持同时引用全局音效，未创建副本。");
        var (captured,music)=studio.CaptureProjectCopy(sourceId,sources.Order(StringComparer.Ordinal).ToArray(),expectedRevision,generatedSourceOnly,musicIds.ToArray());
        if(captured.Catalog is {} catalog&&project.Fps!=catalog.Fps)throw new ArgumentException("Project frame rate must match its catalog.");
        var stagingSources=captured.Sources.Select(source=>new ProjectCopyMediaStager.Source(source.Id,source.Path,source.SrtPath)).ToList();
        foreach(var source in captured.Sources.Where(source=>!generatedSourceOnly&&source.Stems is not null))
            stagingSources.Add(new("stems:"+source.Id,source.Stems![0],source.Stems[1]));
        foreach(var asset in music)stagingSources.Add(new("music:"+asset.Id,asset.Path));
        string? unpublishedMusicDirectory=null;
        try
        {
            var staged=stagingSources.Count==0
                ?new ProjectCopyMediaStager.Result(destinationRoot,new Dictionary<string,ProjectCopyMediaStager.StagedClip>())
                :await ProjectCopyMediaStager.StageAsync(destinationRoot,stagingSources,token).ConfigureAwait(false);
            if(music.Length!=0)unpublishedMusicDirectory=staged.StagingDirectory;
            var clipMap=captured.Sources.ToDictionary(source=>source.Id,source=>staged.Clips[source.Id].ClipId,StringComparer.Ordinal);
            var events=captured.Sources.SelectMany(source=>source.Events).ToDictionary(row=>row[0],_=>Guid.NewGuid().ToString("N"),StringComparer.Ordinal);
            var assetMap=music.ToDictionary(a=>a.Id,a=>staged.Clips["music:"+a.Id].ClipId,StringComparer.Ordinal);
            var copiedMusic=music.Select(a=>a with {Path=staged.Clips["music:"+a.Id].Media.Path}).ToArray();
            var copy=StudioProjectMediaRefs.Remap(detached,clipMap,events,assetMap);
            ValidateCopyProject(copy,captured.Catalog is not null);
            token.ThrowIfCancellationRequested();
            validateBeforePublish?.Invoke();
            var result=studio.PublishProjectCopy(captured,staged,clipMap,events,copy,name,assets:copiedMusic,assetMap:assetMap,
                initialRestoreJson:initialRestoreJson,generatedSourceOnly:generatedSourceOnly,sourceMusic:music);
            unpublishedMusicDirectory=null;
            return result;
        }
        catch(ProjectCopyMediaStager.StagingException ex) when(music.Length!=0)
        {
            unpublishedMusicDirectory=ex.StagingDirectory;
            throw new IOException("独立副本媒体复制失败。",ex.InnerException);
        }
        catch(ProjectCopyMediaStager.StagingCanceledException ex) when(music.Length!=0)
        {
            unpublishedMusicDirectory=ex.StagingDirectory;
            throw new OperationCanceledException("已取消创建独立副本。",ex.InnerException,ex.CancellationToken);
        }
        finally
        {
            // The stager names its own allocated directory, never the shared destination root.
            if(unpublishedMusicDirectory is not null && Directory.Exists(unpublishedMusicDirectory))
                Directory.Delete(unpublishedMusicDirectory,recursive:true);
        }
    }
    internal static void ValidateCopyProject(EditorProject project,bool independent)
    {
        if(independent&&project.Timeline is null)throw new ArgumentException("Independent projects require an explicit timeline.");
        EditorRenderService.ValidateForSave(project,independent);
    }
}

public sealed partial class StudioStore
{
    internal sealed record CopySource(string Id,string EventId,string Path,string? SrtPath,string? TranscriptJson,string? TranscriptUpdatedAt,
        string? Title,string? OutputDirectory,string[]? Stems,string[][] Events,string[][] Documents,string[][] Variants,
        [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ProjectJson=null);
    internal sealed record CopySnapshot(string PrimaryId,long Revision,string? ProjectJson,CopySource[] Sources,
        [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] EditorProjectIdentity? Catalog=null);
    private static readonly HashSet<string> CopyDocumentScopes=new(StringComparer.Ordinal){"ai-advice","clip-ranking","llm-chunks-skill","llm-chunks","roughcut-restore"};

    internal PackageAssociations CaptureProjectCopy(string primaryId,string[] sourceIds,long expectedRevision,bool generatedSourceOnly=false,string[]? musicIds=null)
        =>EditorRevisionStore.Serialized(this,primaryId,()=>
        {
            using var registry=MediaRegistryGate.EnterRead();
            using var db=Open();using var tx=db.BeginTransaction();
            var snapshot=ReadCopySnapshot(db,tx,primaryId,sourceIds);
            if(snapshot.Revision!=expectedRevision)throw new ProjectCopyConflictException("原工程已改变，请重新读取后创建副本。");
            foreach(var source in generatedSourceOnly?Array.Empty<CopySource>():snapshot.Sources)
            {
                if(source.Documents.Any(row=>!CopyDocumentScopes.Contains(row[0])))
                    throw new NotSupportedException("工程包含尚不支持复制的分析关联，未创建副本。");
                if(source.Variants.Length>0)
                    throw new NotSupportedException("工程包含已保存版本，暂不支持完整独立复制，未创建副本。");
            }
            var music=ReadPackageAssets(db,tx,musicIds??[]);
            foreach(var asset in music)
            {
                if(asset.Kind!="music")throw new NotSupportedException("全局配乐引用的不是音乐素材，未创建副本。");
                ValidateAssetDocuments(asset.Documents);
            }
            tx.Commit();return new PackageAssociations(snapshot,music);
        });

    private static CopySnapshot ReadCopySnapshot(SqliteConnection db,SqliteTransaction tx,string primaryId,string[] sourceIds)
    {
        string[][] Rows(string sql,string id)
        {
            using var command=db.CreateCommand();command.Transaction=tx;command.CommandText=sql;command.Parameters.AddWithValue("$id",id);
            using var reader=command.ExecuteReader();var rows=new List<string[]>();
            while(reader.Read())rows.Add(Enumerable.Range(0,reader.FieldCount).Select(i=>reader.IsDBNull(i)?null!:Convert.ToString(reader.GetValue(i),System.Globalization.CultureInfo.InvariantCulture)!).ToArray());
            return rows.ToArray();
        }
        var project=Rows("SELECT json,revision FROM editor_projects WHERE clip_id=$id",primaryId).SingleOrDefault();
        var catalog=Rows("SELECT id,name,width,height,fps,created_at FROM editor_project_catalog WHERE id=$id",primaryId).SingleOrDefault();
        EditorProjectIdentity? identity=catalog is null?null:new(catalog[0],catalog[1],int.Parse(catalog[2]),int.Parse(catalog[3]),int.Parse(catalog[4]),DateTimeOffset.Parse(catalog[5]));
        var sources=sourceIds.Select(id=>
        {
            var clip=Rows("SELECT event_id,path,srt_path FROM clips WHERE id=$id",id).SingleOrDefault()??throw new ArgumentException("源媒体已不存在："+id);
            var local=Rows("SELECT title,output_directory FROM local_videos WHERE clip_id=$id",id).SingleOrDefault();
            var transcript=Rows("SELECT json,updated_at FROM transcripts WHERE event_id=$id",clip[0]).SingleOrDefault();
            return new CopySource(id,clip[0],clip[1],clip[2],transcript?[0],transcript?[1],local?[0],local?[1],
                Rows("SELECT vocals_path,instrumental_path FROM audio_stems WHERE clip_id=$id",id).SingleOrDefault(),
                Rows("SELECT id,start,end,title,summary,hook,source FROM studio_events WHERE clip_id=$id ORDER BY id",id),
                Rows("SELECT scope,json FROM studio_documents WHERE key=$id ORDER BY scope",id),
                Rows("SELECT id,name,project_json FROM editor_variants WHERE clip_id=$id ORDER BY id",id),
                Rows("SELECT json FROM editor_projects WHERE clip_id=$id",id).SingleOrDefault()?[0]);
        }).ToArray();
        return new(primaryId,project is null?0:long.Parse(project[1],System.Globalization.CultureInfo.InvariantCulture),project?[0],sources,identity);
    }

    internal ProjectCopyResult PublishProjectCopy(CopySnapshot captured,ProjectCopyMediaStager.Result staged,
        IReadOnlyDictionary<string,string> clipMap,IReadOnlyDictionary<string,string> eventMap,EditorProject project,string name,
        bool verifySource=true,IReadOnlyList<PackageAsset>? assets=null,IReadOnlyDictionary<string,string>? assetMap=null,string? initialRestoreJson=null,bool generatedSourceOnly=false,PackageAsset[]? sourceMusic=null)
        =>EditorRevisionStore.Serialized(this,captured.PrimaryId,()=>
        {
            using var registry=MediaRegistryGate.EnterWrite();
            using var db=Open();using var tx=db.BeginTransaction();
            if(verifySource)
            {
                var current=ReadCopySnapshot(db,tx,captured.PrimaryId,captured.Sources.Select(source=>source.Id).ToArray());
                if(sourceMusic is {Length:>0})
                {
                    PackageAsset[] currentMusic;
                    try { currentMusic=ReadPackageAssets(db,tx,sourceMusic.Select(a=>a.Id).ToArray()); }
                    catch(ArgumentException) { throw new ProjectCopyConflictException("原配乐已删除；未登记副本。"); }
                    if(JsonSerializer.Serialize(currentMusic)!=JsonSerializer.Serialize(sourceMusic))
                        throw new ProjectCopyConflictException("原配乐或其关联数据在复制过程中已改变；未登记副本。");
                }
                if(JsonSerializer.Serialize(current)!=JsonSerializer.Serialize(captured))
                    throw new ProjectCopyConflictException("原工程或关联数据在复制过程中已改变；未登记副本，请重新创建。");
            }
            void Insert(string sql,params (string Name,object? Value)[] values)
            {
                using var command=db.CreateCommand();command.Transaction=tx;command.CommandText=sql;
                foreach(var value in values)command.Parameters.AddWithValue(value.Name,value.Value??DBNull.Value);
                command.ExecuteNonQuery();
            }
            var at=DateTimeOffset.UtcNow.ToString("O");
            foreach(var source in captured.Sources)
            {
                var file=staged.Clips[source.Id];var id=clipMap[source.Id];var eventId=Guid.NewGuid().ToString("N");
                Insert("INSERT INTO clips(id,event_id,path,srt_path,created_at) VALUES($id,$event,$path,$srt,$at)",
                    ("$id",id),("$event",eventId),("$path",file.Media.Path),("$srt",file.Srt?.Path),("$at",at));
                Insert("INSERT INTO local_videos(clip_id,path,title,output_directory,imported_at) VALUES($id,$path,$title,$output,$at)",
                    ("$id",id),("$path",file.Media.Path),("$title",source.Id==captured.PrimaryId?name:(source.Title??"副本素材")),
                    ("$output",Path.Combine(staged.StagingDirectory,"exports")),("$at",at));
                if(source.TranscriptJson is not null)
                {
                    if(file.Srt is null)throw new NotSupportedException("源转录缺少独立字幕文件，未登记副本。");
                    Insert("INSERT INTO transcripts(id,event_id,json,srt_path,updated_at) VALUES($id,$event,$json,$srt,$at)",
                        ("$id",Guid.NewGuid().ToString("N")),("$event",eventId),("$json",source.TranscriptJson),("$srt",file.Srt.Path),("$at",source.TranscriptUpdatedAt??at));
                }
                foreach(var item in generatedSourceOnly?Array.Empty<string[]>():source.Events)
                    Insert("INSERT INTO studio_events(id,clip_id,start,end,title,summary,hook,source) VALUES($id,$clip,$start,$end,$title,$summary,$hook,$source)",
                        ("$id",eventMap[item[0]]),("$clip",id),("$start",double.Parse(item[1],System.Globalization.CultureInfo.InvariantCulture)),
                        ("$end",double.Parse(item[2],System.Globalization.CultureInfo.InvariantCulture)),("$title",item[3]),("$summary",item[4]),("$hook",item[5]),("$source",item[6]));
                foreach(var document in generatedSourceOnly?Array.Empty<string[]>():source.Documents)
                {
                    var json=document[1];
                    if(document[0]=="clip-ranking")
                    {
                        var node=JsonNode.Parse(json)?.AsObject()??throw new JsonException("Invalid clip-ranking document");
                        var key=node.ContainsKey("ClipId")?"ClipId":"clipId";node[key]=id;json=node.ToJsonString();
                    }
                    Insert("INSERT INTO studio_documents(scope,key,json) VALUES($scope,$id,$json)",("$scope",document[0]),("$id",id),("$json",json));
                }
                if(!generatedSourceOnly&&source.Stems is not null)
                {
                    var stems=staged.Clips["stems:"+source.Id];
                    Insert("INSERT INTO audio_stems(clip_id,vocals_path,instrumental_path) VALUES($id,$vocals,$instrumental)",
                        ("$id",id),("$vocals",stems.Media.Path),("$instrumental",stems.Srt!.Path));
                }
            }
            foreach(var source in captured.Sources)
                foreach(var variant in generatedSourceOnly?Array.Empty<string[]>():source.Variants)
                {
                    var variantProject=StudioProjectMediaRefs.Remap(EditorProjectCodec.Deserialize(variant[2]),clipMap,eventMap,assetMap);
                    EditorRenderService.Validate(variantProject);
                    Insert("INSERT INTO editor_variants(id,clip_id,name,project_json,created_at) VALUES($id,$clip,$name,$json,$at)",
                        ("$id",Guid.NewGuid().ToString("N")),("$clip",clipMap[source.Id]),("$name",variant[1]),
                        ("$json",EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(variantProject))),("$at",at));
                }
            foreach(var asset in assets??[])
            {
                var id=assetMap![asset.Id];
                Insert("INSERT INTO audio_assets(id,name,kind,style,path,built_in) VALUES($id,$name,$kind,$style,$path,0)",
                    ("$id",id),("$name",asset.Name),("$kind",asset.Kind),("$style",asset.Style),("$path",asset.Path));
                foreach(var doc in asset.Documents)
                    Insert("INSERT INTO studio_documents(scope,key,json) VALUES($scope,$key,$json)",("$scope",doc[0]),("$key",id),("$json",doc[1]));
            }
            var primary=captured.Catalog is null?clipMap[captured.PrimaryId]:Guid.NewGuid().ToString("N");
            if(captured.Catalog is {} catalog)
                Insert("INSERT INTO editor_project_catalog(id,name,width,height,fps,created_at) VALUES($id,$name,$width,$height,$fps,$at)",
                    ("$id",primary),("$name",name),("$width",catalog.Width),("$height",catalog.Height),("$fps",catalog.Fps),("$at",at));
            Insert("INSERT INTO editor_projects(clip_id,json,updated_at,revision) VALUES($id,$json,$at,1)",
                ("$id",primary),("$json",EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(project))),("$at",at));
            if(initialRestoreJson is not null)
                Insert("INSERT INTO studio_documents(scope,key,json) VALUES('roughcut-restore',$id,$json) ON CONFLICT(scope,key) DO UPDATE SET json=excluded.json",("$id",primary),("$json",initialRestoreJson));
            tx.Commit();return new ProjectCopyResult(primary,name,1);
        });
}
