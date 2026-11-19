using Microsoft.Data.Sqlite;

namespace LiveClipper.Core;

public sealed record LocalVideo(string ClipId,string Path,string Title,string OutputDirectory,DateTimeOffset ImportedAt);
public sealed record StudioEvent(string Id,string ClipId,double Start,double End,string Title,string Summary,string Hook,string Source);
public sealed record AudioAsset(string Id,string Name,string Kind,string Style,string Path,bool BuiltIn);
public sealed record AudioStems(string ClipId,string VocalsPath,string InstrumentalPath);
public sealed record EditorRender(string Id,string ClipId,string OutputPath,string ProjectJson,DateTimeOffset CreatedAt,int? Revision=null,string? Epoch=null,EditorProjectIdentity? ProjectIdentity=null)
{
    public string ProjectId=>ProjectIdentity?.Id??ClipId;
}
public sealed record EditorVariant(string Id,string ClipId,string Name,string ProjectJson,DateTimeOffset CreatedAt);
public sealed record ProjectRevisionInfo(string FileName,DateTimeOffset CreatedAt,long Size);
public sealed record StudioOutput(string Id,string ClipId,string Kind,string OutputPath,string ProjectJson,DateTimeOffset CreatedAt);

/// <summary>Result of StudioStore.SaveProjectCas. Ok=false is a revision conflict: nothing was written and Revision is the current stored revision.</summary>
public readonly record struct ProjectSaveResult(bool Ok,long Revision)
{
    /// <summary>Set when the save committed but the post-commit backup of the previous JSON failed. The save itself is durable.</summary>
    public string? BackupError { get; init; }
}
/// <summary>Editor project row read by one SELECT, so the JSON and its revision always belong together.</summary>
public sealed record StoredEditorProject(string ClipId,string Json,long Revision,DateTimeOffset? UpdatedAt);

/// <summary>The shared database was migrated by a newer build. Fail closed: refuse to open, migrate or write it.</summary>
public sealed class StorageSchemaTooNewException(string databasePath,long schemaVersion,long supportedVersion)
    :InvalidOperationException($"database {databasePath} has schema_migrations version {schemaVersion}, newer than this build supports ({supportedVersion}); refusing to open it")
{
    public const string ErrorCode="storage-schema-too-new";
    public string Code => ErrorCode;
    public string DatabasePath { get; } = databasePath;
    public long SchemaVersion { get; } = schemaVersion;
    public long SupportedVersion { get; } = supportedVersion;
}

/// <summary>Result of DurableFile.ReadJson. A corrupt file is never reported as missing and is preserved before anyone can overwrite it.</summary>
public sealed record DurableJsonLoad<T>(T? Value,bool Exists,string? Error,string? PreservedCorruptPath) where T:class
{
    public bool Ok => Exists&&Error is null&&Value is not null;
    public bool Corrupt => Error is not null;
}

/// <summary>Crash-safe whole-file publication: unique sibling temp (CreateNew), flush to disk, then rename over the destination.</summary>
public static class DurableFile
{
    /// <summary>Every temp name contains this marker; directory readers skip such names and only owned stale temps are cleaned.</summary>
    public const string TempMarker=".lc-tmp-";

    /// <summary>Never truncates the destination in place and never deletes it first; on failure the old file stays intact and the temp is removed.</summary>
    public static void WriteAllText(string path,string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full=Path.GetFullPath(path);
        var directory=Path.GetDirectoryName(full)??throw new ArgumentException("path has no parent directory",nameof(path));
        Directory.CreateDirectory(directory);
        var temp=Path.Combine(directory,$"{Path.GetFileName(full)}{TempMarker}{Guid.NewGuid():N}.tmp");
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                using(var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false),16*1024,leaveOpen:true))
                {
                    writer.Write(content);
                    writer.Flush();
                }
                stream.Flush(flushToDisk:true);
            }
            File.Move(temp,full,overwrite:true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Copies an unreadable file to a sibling .corrupt-yyyyMMdd-HHmmssfff and returns that path. The original stays in place.</summary>
    public static string PreserveCorrupt(string path)
    {
        var full=Path.GetFullPath(path);
        var target=$"{full}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
        if(File.Exists(target))target+="-"+Guid.NewGuid().ToString("N");
        File.Copy(full,target,overwrite:false);
        return target;
    }

    /// <summary>
    /// Reads a JSON file without ever turning a parse failure into defaults: a missing file reports Exists=false;
    /// malformed JSON or a JSON null is preserved via PreserveCorrupt and reported in Error. I/O failures propagate.
    /// </summary>
    public static DurableJsonLoad<T> ReadJson<T>(string path,System.Text.Json.JsonSerializerOptions? options=null) where T:class
    {
        string text;
        try{text=File.ReadAllText(path);}
        catch(FileNotFoundException){return new(null,false,null,null);}
        catch(DirectoryNotFoundException){return new(null,false,null,null);}
        try
        {
            var value=System.Text.Json.JsonSerializer.Deserialize<T>(text,options);
            if(value is not null)return new(value,true,null,null);
            return new(null,true,"file contains JSON null",PreserveCorrupt(path));
        }
        catch(System.Text.Json.JsonException ex)
        {
            return new(null,true,ex.Message,PreserveCorrupt(path));
        }
    }

    /// <summary>Deletes temps carrying TempMarker that are older than the given age (left behind by a crash). Returns how many were removed.</summary>
    public static int DeleteStaleTemps(string directory,TimeSpan olderThan)
    {
        if(!Directory.Exists(directory))return 0;
        var removed=0;
        var cutoff=DateTime.UtcNow-olderThan;
        foreach(var file in Directory.EnumerateFiles(directory,"*"+TempMarker+"*.tmp"))
        {
            if(!IsTempName(Path.GetFileName(file)))continue;
            try
            {
                if(File.GetLastWriteTimeUtc(file)>cutoff)continue;
                File.Delete(file);
                removed++;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        }
        return removed;
    }

    public static bool IsTempName(string fileName)=>fileName.Contains(TempMarker,StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try{File.Delete(path);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
    }
}

public sealed partial class StudioStore(string databasePath)
{
    /// <summary>
    /// Highest schema_migrations version this build understands. Store.cs owns migrations 1-7 of the shared database;
    /// StudioStore owns migrations 8 (editor_projects.revision) and 9 (independent project catalog). A database stamped higher came from a newer build and is refused.
    /// </summary>
    public const long SupportedSchemaVersion=9;
    /// <summary>Largest revision ever issued; stays inside the JavaScript safe-integer range.</summary>
    public const long MaxProjectRevision=9007199254740991;
    public const int ProjectBackupRetention=30;
    private volatile bool schemaChecked;
    /// <summary>Last post-commit backup failure seen by this instance (diagnostics only; that save had already committed).</summary>
    public string? LastBackupError { get; private set; }

    private SqliteConnection Open()
    {
        var db=new SqliteConnection($"Data Source={databasePath};Pooling=False");db.Open();
        try
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText="PRAGMA busy_timeout=5000;";cmd.ExecuteNonQuery();
            // Refuse a newer database before PRAGMA journal_mode or any migration can change it.
            if(!schemaChecked){ThrowIfSchemaTooNew(databasePath,ReadSchemaVersion(db,null));schemaChecked=true;}
            cmd.CommandText="PRAGMA journal_mode=WAL;";cmd.ExecuteNonQuery();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }
    /// <summary>Highest schema_migrations version in an open database; 0 when the table does not exist yet.</summary>
    private static long ReadSchemaVersion(SqliteConnection db,SqliteTransaction? tx)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
        if(Convert.ToInt64(cmd.ExecuteScalar())==0)return 0;
        cmd.CommandText="SELECT MAX(version) FROM schema_migrations";
        var value=cmd.ExecuteScalar();
        if(value is null||value is DBNull)return 0;
        return Convert.ToInt64(value);
    }
    private static void ThrowIfSchemaTooNew(string databasePath,long version)
    {
        if(version>SupportedSchemaVersion)throw new StorageSchemaTooNewException(databasePath,version,SupportedSchemaVersion);
    }
    /// <summary>Read-only probe: never creates, migrates or switches the journal mode of the file. Returns 0 when the file does not exist.</summary>
    public static long ProbeSchemaVersion(string databasePath)
    {
        if(!File.Exists(databasePath))return 0;
        var builder=new SqliteConnectionStringBuilder{DataSource=databasePath,Mode=SqliteOpenMode.ReadOnly,Pooling=false};
        using var db=new SqliteConnection(builder.ToString());db.Open();
        return ReadSchemaVersion(db,null);
    }
    /// <summary>Call before Store.Migrate(): throws StorageSchemaTooNewException for a database written by a newer build.</summary>
    public static void EnsureSchemaSupported(string databasePath)=>ThrowIfSchemaTooNew(databasePath,ProbeSchemaVersion(databasePath));
    /// <summary>
    /// Migration 8: durable editor_projects.revision. Idempotent (column-existence check) and atomic with its schema_migrations marker.
    /// Rows saved before revisions existed become revision 1, so revision 0 keeps meaning "no stored project".
    /// </summary>
    private void MigrateProjectRevision(SqliteConnection db)
    {
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY);";cmd.ExecuteNonQuery();
        ThrowIfSchemaTooNew(databasePath,ReadSchemaVersion(db,tx));
        cmd.CommandText="SELECT COUNT(*) FROM schema_migrations WHERE version=8";
        if(Convert.ToInt64(cmd.ExecuteScalar())!=0)return;
        var hasRevision=false;
        cmd.CommandText="PRAGMA table_info(editor_projects)";
        using(var columns=cmd.ExecuteReader())
            while(columns.Read())if(columns.GetString(1)=="revision")hasRevision=true;
        if(!hasRevision)
        {
            cmd.CommandText="ALTER TABLE editor_projects ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;";cmd.ExecuteNonQuery();
        }
        cmd.CommandText="UPDATE editor_projects SET revision=1 WHERE revision=0; INSERT OR IGNORE INTO schema_migrations(version) VALUES(8);";cmd.ExecuteNonQuery();
        tx.Commit();
    }
    public void Migrate()
    {
        using var db=Open();
        MigrateTables(db);
        MigrateProjectRevision(db);
        MigrateProjectCatalog(db);
    }
    private static void MigrateTables(SqliteConnection db)
    {
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS local_videos(clip_id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE,title TEXT NOT NULL,output_directory TEXT NOT NULL,imported_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS studio_events(id TEXT PRIMARY KEY,clip_id TEXT NOT NULL,start REAL NOT NULL,end REAL NOT NULL,title TEXT NOT NULL,summary TEXT NOT NULL,hook TEXT NOT NULL,source TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_studio_events_clip ON studio_events(clip_id,start);
            CREATE TABLE IF NOT EXISTS audio_assets(id TEXT PRIMARY KEY,name TEXT NOT NULL,kind TEXT NOT NULL,style TEXT NOT NULL,path TEXT NOT NULL UNIQUE,built_in INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS audio_stems(clip_id TEXT PRIMARY KEY,vocals_path TEXT NOT NULL,instrumental_path TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS editor_projects(clip_id TEXT PRIMARY KEY,json TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS editor_renders(id TEXT PRIMARY KEY,clip_id TEXT NOT NULL,output_path TEXT NOT NULL,project_json TEXT NOT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS editor_variants(id TEXT PRIMARY KEY,clip_id TEXT NOT NULL,name TEXT NOT NULL,project_json TEXT NOT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS studio_outputs(id TEXT PRIMARY KEY,clip_id TEXT NOT NULL,kind TEXT NOT NULL,output_path TEXT NOT NULL,project_json TEXT NOT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS studio_documents(scope TEXT NOT NULL,key TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(scope,key));
            """;
        cmd.ExecuteNonQuery();
    }
    public string? Document(string scope,string key)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM studio_documents WHERE scope=$scope AND key=$key";cmd.Parameters.AddWithValue("$scope",scope);cmd.Parameters.AddWithValue("$key",key);return cmd.ExecuteScalar() as string;
    }
    public void SaveDocument(string scope,string key,string json)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO studio_documents VALUES($scope,$key,$json) ON CONFLICT(scope,key) DO UPDATE SET json=$json";cmd.Parameters.AddWithValue("$scope",scope);cmd.Parameters.AddWithValue("$key",key);cmd.Parameters.AddWithValue("$json",json);cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<StudioOutput> Outputs()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,clip_id,kind,output_path,project_json,created_at FROM studio_outputs ORDER BY created_at DESC";using var r=cmd.ExecuteReader();var result=new List<StudioOutput>();while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),DateTimeOffset.Parse(r.GetString(5))));return result;
    }
    public void SaveOutput(StudioOutput output)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO studio_outputs VALUES($id,$clip,$kind,$path,$json,$at)";cmd.Parameters.AddWithValue("$id",output.Id);cmd.Parameters.AddWithValue("$clip",output.ClipId);cmd.Parameters.AddWithValue("$kind",output.Kind);cmd.Parameters.AddWithValue("$path",output.OutputPath);cmd.Parameters.AddWithValue("$json",output.ProjectJson);cmd.Parameters.AddWithValue("$at",output.CreatedAt.ToString("O"));cmd.ExecuteNonQuery();
    }
    public bool DeleteEvent(string clipId,string id)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM studio_events WHERE clip_id=$clip AND id=$id";cmd.Parameters.AddWithValue("$clip",clipId);cmd.Parameters.AddWithValue("$id",id);return cmd.ExecuteNonQuery()>0;
    }
    /// <summary>按来源清空事件（如 "ai"），供候选重跑排序时避免重复。</summary>
    public int DeleteEventsBySource(string clipId,string source)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM studio_events WHERE clip_id=$clip AND source=$source";cmd.Parameters.AddWithValue("$clip",clipId);cmd.Parameters.AddWithValue("$source",source);return cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<EditorVariant> Variants(string clipId)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,clip_id,name,project_json,created_at FROM editor_variants WHERE clip_id=$clip ORDER BY created_at DESC";cmd.Parameters.AddWithValue("$clip",clipId);
        using var r=cmd.ExecuteReader();var result=new List<EditorVariant>();while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),DateTimeOffset.Parse(r.GetString(4))));return result;
    }
    public EditorVariant? Variant(string id)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,clip_id,name,project_json,created_at FROM editor_variants WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),DateTimeOffset.Parse(r.GetString(4))):null;
    }
    public void SaveVariant(EditorVariant variant)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO editor_variants VALUES($id,$clip,$name,$json,$at)";
        cmd.Parameters.AddWithValue("$id",variant.Id);cmd.Parameters.AddWithValue("$clip",variant.ClipId);cmd.Parameters.AddWithValue("$name",variant.Name);cmd.Parameters.AddWithValue("$json",variant.ProjectJson);cmd.Parameters.AddWithValue("$at",variant.CreatedAt.ToString("O"));cmd.ExecuteNonQuery();
    }
    public bool DeleteVariant(string id)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM editor_variants WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);return cmd.ExecuteNonQuery()>0;
    }
    public IReadOnlyList<LocalVideo> LocalVideos()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT clip_id,path,title,output_directory,imported_at FROM local_videos ORDER BY imported_at DESC";
        using var r=cmd.ExecuteReader();var result=new List<LocalVideo>();
        while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),DateTimeOffset.Parse(r.GetString(4))));
        return result;
    }
    public void SaveLocalVideo(LocalVideo video)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="INSERT INTO local_videos VALUES($id,$path,$title,$output,$at) ON CONFLICT(path) DO UPDATE SET output_directory=$output,title=$title";
        cmd.Parameters.AddWithValue("$id",video.ClipId);cmd.Parameters.AddWithValue("$path",video.Path);cmd.Parameters.AddWithValue("$title",video.Title);cmd.Parameters.AddWithValue("$output",video.OutputDirectory);cmd.Parameters.AddWithValue("$at",video.ImportedAt.ToString("O"));cmd.ExecuteNonQuery();
    }
    /// <summary>在工程 Gate → 注册表写锁内删除本地视频的 Studio 记录与 clip，避免保存重插孤儿工程；
    /// 返回可在锁外清理的 Demucs 缓存路径。clip 文件仍按 Store 的 storageRoot 限制清理。</summary>
    public List<string> DeleteLocalVideo(string clipId,string? storageRoot=null) => EditorRevisionStore.Serialized(clipId,()=>
    {
        using var registryWrite=MediaRegistryGate.EnterWrite();
        var cachePaths=new List<string>();
        using var db=Open();
        using(var query=db.CreateCommand())
        {
            query.CommandText="SELECT vocals_path,instrumental_path FROM audio_stems WHERE clip_id=$id";query.Parameters.AddWithValue("$id",clipId);
            using var r=query.ExecuteReader();while(r.Read()){cachePaths.Add(r.GetString(0));cachePaths.Add(r.GetString(1));}
        }
        foreach(var sql in new[]{
            "DELETE FROM studio_events WHERE clip_id=$id",
            "DELETE FROM editor_projects WHERE clip_id=$id",
            "DELETE FROM studio_documents WHERE scope='editor-render-origin' AND key IN (SELECT id FROM editor_renders WHERE clip_id=$id)",
            "DELETE FROM editor_renders WHERE clip_id=$id",
            "DELETE FROM editor_variants WHERE clip_id=$id",
            "DELETE FROM audio_stems WHERE clip_id=$id",
            "DELETE FROM studio_outputs WHERE clip_id=$id",
            "DELETE FROM local_videos WHERE clip_id=$id"})
        {
            using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.Parameters.AddWithValue("$id",clipId);cmd.ExecuteNonQuery();
        }
        var store=new Store(databasePath);
        var clip=store.Clips().FirstOrDefault(x=>x.Id==clipId);
        if(clip is not null)store.DeleteClip(clip,storageRoot);
        return cachePaths;
    });
    public IReadOnlyList<StudioEvent> Events(string clipId,string? keyword=null)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT id,clip_id,start,end,title,summary,hook,source FROM studio_events WHERE clip_id=$clip AND ($keyword='' OR title LIKE $pattern OR summary LIKE $pattern OR hook LIKE $pattern) ORDER BY start";
        cmd.Parameters.AddWithValue("$clip",clipId);cmd.Parameters.AddWithValue("$keyword",keyword??"");cmd.Parameters.AddWithValue("$pattern","%"+(keyword??"")+"%");
        using var r=cmd.ExecuteReader();var result=new List<StudioEvent>();
        while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetDouble(2),r.GetDouble(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7)));
        return result;
    }
    public void SaveEvent(StudioEvent item)
    {
        if(item.Start<0||item.End<=item.Start||item.Title.Trim().Length==0)throw new ArgumentException("事件时间或标题无效");
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="INSERT INTO studio_events VALUES($id,$clip,$start,$end,$title,$summary,$hook,$source) ON CONFLICT(id) DO UPDATE SET start=$start,end=$end,title=$title,summary=$summary,hook=$hook,source=$source";
        cmd.Parameters.AddWithValue("$id",item.Id);cmd.Parameters.AddWithValue("$clip",item.ClipId);cmd.Parameters.AddWithValue("$start",item.Start);cmd.Parameters.AddWithValue("$end",item.End);cmd.Parameters.AddWithValue("$title",item.Title.Trim());cmd.Parameters.AddWithValue("$summary",item.Summary);cmd.Parameters.AddWithValue("$hook",item.Hook);cmd.Parameters.AddWithValue("$source",item.Source);cmd.ExecuteNonQuery();
    }
    public StudioEvent MergeEvents(string clipId,IReadOnlyList<string> ids)
    {
        var chosen=Events(clipId).Where(x=>ids.Contains(x.Id)).ToArray();
        if(chosen.Length<2||chosen.Length!=ids.Distinct().Count())throw new ArgumentException("请选择至少两个同一视频的事件");
        var merged=new StudioEvent(Guid.NewGuid().ToString("N"),clipId,chosen.Min(x=>x.Start),chosen.Max(x=>x.End),string.Join(" + ",chosen.Select(x=>x.Title)),string.Join("；",chosen.Select(x=>x.Summary).Where(x=>x.Length>0)),string.Join("；",chosen.Select(x=>x.Hook).Where(x=>x.Length>0)),"merged");
        using var db=Open();using var tx=db.BeginTransaction();
        foreach(var item in chosen)
        {
            using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="DELETE FROM studio_events WHERE id=$id AND clip_id=$clip";cmd.Parameters.AddWithValue("$id",item.Id);cmd.Parameters.AddWithValue("$clip",clipId);cmd.ExecuteNonQuery();
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;cmd.CommandText="INSERT INTO studio_events VALUES($id,$clip,$start,$end,$title,$summary,$hook,$source)";
            cmd.Parameters.AddWithValue("$id",merged.Id);cmd.Parameters.AddWithValue("$clip",clipId);cmd.Parameters.AddWithValue("$start",merged.Start);cmd.Parameters.AddWithValue("$end",merged.End);cmd.Parameters.AddWithValue("$title",merged.Title);cmd.Parameters.AddWithValue("$summary",merged.Summary);cmd.Parameters.AddWithValue("$hook",merged.Hook);cmd.Parameters.AddWithValue("$source",merged.Source);cmd.ExecuteNonQuery();
        }
        tx.Commit();return merged;
    }
    public IReadOnlyList<AudioAsset> Assets()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,name,kind,style,path,built_in FROM audio_assets ORDER BY kind,style,name";using var r=cmd.ExecuteReader();var result=new List<AudioAsset>();
        while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt32(5)!=0));return result;
    }
    public void SaveAsset(AudioAsset asset)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO audio_assets VALUES($id,$name,$kind,$style,$path,$built) ON CONFLICT(id) DO UPDATE SET name=$name,kind=$kind,style=$style,path=$path,built_in=$built";
        cmd.Parameters.AddWithValue("$id",asset.Id);cmd.Parameters.AddWithValue("$name",asset.Name);cmd.Parameters.AddWithValue("$kind",asset.Kind);cmd.Parameters.AddWithValue("$style",asset.Style);cmd.Parameters.AddWithValue("$path",asset.Path);cmd.Parameters.AddWithValue("$built",asset.BuiltIn?1:0);cmd.ExecuteNonQuery();
    }
    public AudioStems? Stems(string clipId)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT clip_id,vocals_path,instrumental_path FROM audio_stems WHERE clip_id=$id";cmd.Parameters.AddWithValue("$id",clipId);using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2)):null;
    }
    public void SaveStems(AudioStems stems)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO audio_stems VALUES($id,$voice,$music) ON CONFLICT(clip_id) DO UPDATE SET vocals_path=$voice,instrumental_path=$music";
        cmd.Parameters.AddWithValue("$id",stems.ClipId);cmd.Parameters.AddWithValue("$voice",stems.VocalsPath);cmd.Parameters.AddWithValue("$music",stems.InstrumentalPath);cmd.ExecuteNonQuery();
    }
    public string? Project(string clipId)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM editor_projects WHERE clip_id=$id";cmd.Parameters.AddWithValue("$id",clipId);return cmd.ExecuteScalar() as string;
    }
    /// <summary>Unconditional save (legacy signature). Bumps the durable revision through the same atomic path as SaveProjectCas.</summary>
    public void SaveProject(string clipId,string json)=>SaveProjectCas(clipId,json,null);
    /// <summary>
    /// Compare-and-swap save in one IMMEDIATE transaction: reads the stored revision, compares it with expectedRevision
    /// (null = unconditional), then writes json, updated_at and revision+1 together. A missing project has revision 0.
    /// On conflict nothing is written and the current revision is returned. The previous JSON is backed up only after
    /// the commit; a backup failure is reported in BackupError and never turns a committed save into a failure.
    /// </summary>
    public ProjectSaveResult SaveProjectCas(string clipId,string json,long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(clipId);
        ArgumentNullException.ThrowIfNull(json);
        if(expectedRevision is <0 or >MaxProjectRevision)throw new ArgumentOutOfRangeException(nameof(expectedRevision),expectedRevision,$"revision must be between 0 and {MaxProjectRevision}");
        string? previous=null;
        long next=0;
        using(var db=Open())
        using(var tx=db.BeginTransaction())
        {
            long current=0;
            var exists=false;
            using(var read=db.CreateCommand())
            {
                read.Transaction=tx;
                read.CommandText="SELECT json,revision FROM editor_projects WHERE clip_id=$id";
                read.Parameters.AddWithValue("$id",clipId);
                using var reader=read.ExecuteReader();
                if(reader.Read()){exists=true;previous=reader.GetString(0);current=reader.GetInt64(1);}
            }
            if(expectedRevision is { } expected&&expected!=current)return new(false,current);
            if(current>=MaxProjectRevision)throw new InvalidOperationException($"editor project {clipId} reached the maximum revision {MaxProjectRevision}");
            next=current+1;
            using(var write=db.CreateCommand())
            {
                write.Transaction=tx;
                write.CommandText=exists
                    ?"UPDATE editor_projects SET json=$json,updated_at=$at,revision=$next WHERE clip_id=$id AND revision=$current"
                    :"INSERT INTO editor_projects(clip_id,json,updated_at,revision) VALUES($id,$json,$at,$next)";
                write.Parameters.AddWithValue("$id",clipId);
                write.Parameters.AddWithValue("$json",json);
                write.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
                write.Parameters.AddWithValue("$next",next);
                if(exists)write.Parameters.AddWithValue("$current",current);
                if(write.ExecuteNonQuery()!=1)throw new InvalidOperationException($"editor project {clipId} save affected an unexpected number of rows");
            }
            using(var catalog=db.CreateCommand())
            {
                catalog.Transaction=tx;
                catalog.CommandText="SELECT COUNT(*) FROM editor_project_catalog WHERE id=$id";
                catalog.Parameters.AddWithValue("$id",clipId);
                if((long)catalog.ExecuteScalar()!>0)
                {
                    var plan=EditorProjectCodec.Deserialize(json);
                    EditorRenderService.ValidateCanvas(plan);
                    if(plan.CanvasWidth is {} width && plan.CanvasHeight is {} height)
                    {
                        catalog.CommandText="UPDATE editor_project_catalog SET width=$width,height=$height,fps=$fps WHERE id=$id";
                        catalog.Parameters.AddWithValue("$width",width);catalog.Parameters.AddWithValue("$height",height);catalog.Parameters.AddWithValue("$fps",plan.Fps);
                        catalog.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
        }
        var result=new ProjectSaveResult(true,next);
        if(!string.IsNullOrWhiteSpace(previous)&&previous!=json)
        {
            var backupError=BackupProjectRevision(clipId,previous);
            if(backupError is not null)result=result with{BackupError=backupError};
        }
        return result;
    }
    /// <summary>Backs up the replaced JSON after a committed save. I/O problems are returned as text, never thrown.</summary>
    private string? BackupProjectRevision(string clipId,string json)
    {
        string directory;
        try
        {
            directory=ProjectRevisionDirectory(clipId);
            DurableFile.WriteAllText(Path.Combine(directory,$"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json"),json);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            LastBackupError=$"project backup for {clipId} failed after the save committed: {ex.Message}";
            return LastBackupError;
        }
        PruneProjectRevisions(directory);
        return null;
    }
    /// <summary>Best effort, only after a backup was published: keeps the newest ProjectBackupRetention backups and removes owned stale temps.</summary>
    private static void PruneProjectRevisions(string directory)
    {
        try
        {
            foreach(var old in Directory.GetFiles(directory,"*.json").Where(IsBackupName).OrderByDescending(x=>Path.GetFileName(x),StringComparer.Ordinal).Skip(ProjectBackupRetention))
            {
                try{File.Delete(old);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
            }
            DurableFile.DeleteStaleTemps(directory,TimeSpan.FromHours(1));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
    }
    private static bool IsBackupName(string path)
    {
        var name=Path.GetFileName(path);
        return name.EndsWith(".json",StringComparison.OrdinalIgnoreCase)&&!DurableFile.IsTempName(name);
    }
    public IReadOnlyList<ProjectRevisionInfo> ProjectRevisions(string clipId)
    {
        var directory=ProjectRevisionDirectory(clipId);
        if(!Directory.Exists(directory))return [];
        return Directory.GetFiles(directory,"*.json")
            .Where(IsBackupName)
            .Select(path=>new FileInfo(path))
            .Where(file=>file.Exists)
            .OrderByDescending(file=>file.Name,StringComparer.Ordinal)
            .Select(file=>new ProjectRevisionInfo(file.Name,new DateTimeOffset(file.LastWriteTimeUtc,TimeSpan.Zero),file.Length))
            .ToArray();
    }
    /// <summary>Durable revision of the stored project; 0 when there is no stored project.</summary>
    public long GetProjectRevision(string clipId)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT revision FROM editor_projects WHERE clip_id=$id";cmd.Parameters.AddWithValue("$id",clipId);
        var value=cmd.ExecuteScalar();
        if(value is null||value is DBNull)return 0;
        return Convert.ToInt64(value);
    }
    /// <summary>JSON, revision and updated_at from one SELECT; null when there is no stored project.</summary>
    public StoredEditorProject? GetProjectRecord(string clipId)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT json,revision,updated_at FROM editor_projects WHERE clip_id=$id";cmd.Parameters.AddWithValue("$id",clipId);
        using var r=cmd.ExecuteReader();
        if(!r.Read())return null;
        DateTimeOffset? updatedAt=DateTimeOffset.TryParse(r.GetString(2),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var parsed)?parsed:null;
        return new(clipId,r.GetString(0),r.GetInt64(1),updatedAt);
    }
    public string? ProjectRevision(string clipId,string fileName)
    {
        if(string.IsNullOrWhiteSpace(fileName)||Path.GetFileName(fileName)!=fileName||!fileName.EndsWith(".json",StringComparison.OrdinalIgnoreCase))return null;
        var path=Path.Combine(ProjectRevisionDirectory(clipId),fileName);
        return File.Exists(path)?File.ReadAllText(path):null;
    }
    private string ProjectRevisionDirectory(string clipId)
    {
        var dataDirectory=Path.GetDirectoryName(Path.GetFullPath(databasePath))??".";
        return Path.Combine(dataDirectory,"project-backups",clipId);
    }
    private sealed record RenderOrigin(int? Revision,string? Epoch,EditorProjectIdentity? ProjectIdentity=null);
    public IReadOnlyList<EditorRender> Renders()
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT r.id,r.clip_id,r.output_path,r.project_json,r.created_at,d.json FROM editor_renders r LEFT JOIN studio_documents d ON d.scope='editor-render-origin' AND d.key=r.id ORDER BY r.created_at DESC";
        using var r=cmd.ExecuteReader();var result=new List<EditorRender>();
        while(r.Read())
        {
            var origin=r.IsDBNull(5)?null:System.Text.Json.JsonSerializer.Deserialize<RenderOrigin>(r.GetString(5));
            result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),DateTimeOffset.Parse(r.GetString(4)),origin?.Revision,origin?.Epoch,origin?.ProjectIdentity));
        }
        return result;
    }
    public void SaveRender(EditorRender render)
    {
        // Keep origin and render atomic without changing the shared database schema version. Older rows have no origin.
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO editor_renders(id,clip_id,output_path,project_json,created_at) VALUES($id,$clip,$output,$project,$at)";
        cmd.Parameters.AddWithValue("$id",render.Id);cmd.Parameters.AddWithValue("$clip",render.ClipId);cmd.Parameters.AddWithValue("$output",render.OutputPath);cmd.Parameters.AddWithValue("$project",render.ProjectJson);cmd.Parameters.AddWithValue("$at",render.CreatedAt.ToString("O"));cmd.ExecuteNonQuery();
        if(render.Revision is not null||render.Epoch is not null||render.ProjectIdentity is not null)
        {
            cmd.CommandText="INSERT INTO studio_documents(scope,key,json) VALUES('editor-render-origin',$id,$origin)";
            cmd.Parameters.AddWithValue("$origin",System.Text.Json.JsonSerializer.Serialize(new RenderOrigin(render.Revision,render.Epoch,render.ProjectIdentity)));cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
