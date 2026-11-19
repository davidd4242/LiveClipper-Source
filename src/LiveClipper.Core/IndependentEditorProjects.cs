using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Core;

/// <summary>Project identity and canvas, not a media/clip record.</summary>
public sealed record EditorProjectIdentity(string Id,string Name,int Width,int Height,int Fps,DateTimeOffset CreatedAt);
public sealed record IndependentEditorProject(EditorProjectIdentity Identity,StoredEditorProject Document);

public sealed partial class StudioStore
{
    private void MigrateProjectCatalog(SqliteConnection db)
    {
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS editor_project_catalog(
                id TEXT PRIMARY KEY,name TEXT NOT NULL,width INTEGER NOT NULL,height INTEGER NOT NULL,
                fps INTEGER NOT NULL,created_at TEXT NOT NULL);
            INSERT OR IGNORE INTO schema_migrations(version) VALUES(9);
            """;
        cmd.ExecuteNonQuery();tx.Commit();
    }

    public IndependentEditorProject CreateIndependentProject(string name,int width=1920,int height=1080,int fps=30)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>120)throw new ArgumentException("Project name must contain 1-120 characters.",nameof(name));
        if(width<16||width>16384||height<16||height>16384)throw new ArgumentOutOfRangeException(nameof(width),"Canvas dimensions must be 16-16384 pixels.");
        if(fps is not (24 or 25 or 30 or 50 or 60))throw new ArgumentOutOfRangeException(nameof(fps));
        var identity=new EditorProjectIdentity(Guid.NewGuid().ToString("N"),name.Trim(),width,height,fps,DateTimeOffset.UtcNow);
        var project=new EditorProject{Start=0,End=0,Timeline=[],TimelineCaptions=[],Fps=fps};
        EditorRenderService.ValidateForSave(project,independentProject:true);
        var json=EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(project));
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO editor_project_catalog(id,name,width,height,fps,created_at) VALUES($id,$name,$width,$height,$fps,$at);
            INSERT INTO editor_projects(clip_id,json,updated_at,revision) VALUES($id,$json,$at,1);
            """;
        cmd.Parameters.AddWithValue("$id",identity.Id);cmd.Parameters.AddWithValue("$name",identity.Name);
        cmd.Parameters.AddWithValue("$width",width);cmd.Parameters.AddWithValue("$height",height);cmd.Parameters.AddWithValue("$fps",fps);
        cmd.Parameters.AddWithValue("$at",identity.CreatedAt.ToString("O"));cmd.Parameters.AddWithValue("$json",json);
        cmd.ExecuteNonQuery();tx.Commit();
        return new(identity,new(identity.Id,json,1,identity.CreatedAt));
    }

    public IReadOnlyList<EditorProjectIdentity> IndependentProjects()
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT id,name,width,height,fps,created_at FROM editor_project_catalog ORDER BY created_at DESC,id";
        using var r=cmd.ExecuteReader();var result=new List<EditorProjectIdentity>();
        while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),DateTimeOffset.Parse(r.GetString(5))));
        return result;
    }

    public IndependentEditorProject? GetIndependentProject(string id)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT c.id,c.name,c.width,c.height,c.fps,c.created_at,p.json,p.revision,p.updated_at
            FROM editor_project_catalog c JOIN editor_projects p ON p.clip_id=c.id WHERE c.id=$id;
            """;
        cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();if(!r.Read())return null;
        return new(new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),DateTimeOffset.Parse(r.GetString(5))),
            new(r.GetString(0),r.GetString(6),r.GetInt64(7),DateTimeOffset.Parse(r.GetString(8))));
    }

    public ProjectSaveResult SaveIndependentProject(string id,EditorProject project,long expectedRevision)
    {
        var current=GetIndependentProject(id)??throw new KeyNotFoundException("Independent project does not exist.");
        if(project.Timeline is null)throw new ArgumentException("Independent project requires an explicit timeline, including an empty array.");
        if(project.CanvasWidth is null && project.Fps!=current.Identity.Fps)throw new ArgumentException("Project frame rate must match its canvas configuration.");
        EditorRenderService.ValidateForSave(project,independentProject:true);
        return SaveProjectCas(id,EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(project)),expectedRevision);
    }
}
