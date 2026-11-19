using LiveClipper.Core;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Tests;

public sealed class EditorRevisionOverflowTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"liveclipper-revision-overflow-"+Guid.NewGuid().ToString("N"));
    private readonly List<string> _clipIds=[];
    private string DatabasePath=>Path.Combine(_root,"test.db");

    public EditorRevisionOverflowTests()=>Directory.CreateDirectory(_root);
    public void Dispose()
    {
        foreach(var clipId in _clipIds)EditorRevisionStore.Forget(clipId);
        SqliteConnection.ClearAllPools();
        if(Directory.Exists(_root))Directory.Delete(_root,true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_LastRepresentableRevisionSucceeds_AndNextWriteFailsWithoutChangingDisk(bool unconditional)
    {
        var (studio,clipId)=TemporaryStore(int.MaxValue-1L);
        var accepted=EditorRevisionStore.Commit(studio,clipId,"{\"end\":2}",unconditional?null:int.MaxValue-1);
        Assert.True(accepted.Ok);
        Assert.Equal(int.MaxValue,accepted.Revision);
        Assert.True(EditorRevisionStore.TryAdvance(clipId,null,out var advanced));
        Assert.Equal(int.MaxValue,advanced);
        var before=studio.GetProjectRecord(clipId)!;
        var backups=BackupCount(clipId);
        var error=Assert.Throws<EditorRevisionConflictException>(()=>
            EditorRevisionStore.Commit(studio,clipId,"{\"end\":3}",unconditional?null:int.MaxValue));
        Assert.Equal("revision-limit",error.Code);
        Assert.Equal(int.MaxValue,error.Revision);
        Assert.Equal(before,studio.GetProjectRecord(clipId));
        Assert.Equal(backups,BackupCount(clipId));
        Assert.False(EditorRevisionStore.TryAdvance(clipId,null,out var unchanged));
        Assert.Equal(int.MaxValue,unchanged);
    }

    [Fact]
    public void ExternalWrite_AtLimit_PreservesDiskHistoryAndActiveSession()
    {
        var (studio,clipId)=TemporaryStore(int.MaxValue-1L);
        Assert.Equal(int.MaxValue-1,EditorRevisionStore.Current(studio,clipId));
        var session=EditorRevisionStore.BeginSession(clipId,"checkpoint");
        EditorRevisionStore.RecordWrite(clipId,"previous");
        EditorRevisionStore.PushRedo(clipId,"redo");
        SetRevision(clipId,int.MaxValue);
        var before=studio.GetProjectRecord(clipId)!;
        var backups=BackupCount(clipId);
        var error=Assert.Throws<EditorRevisionConflictException>(()=>
            EditorRevisionStore.SaveExternal(studio,clipId,"test",_=>"{\"end\":3}"));
        Assert.Equal("revision-limit",error.Code);
        Assert.Equal(int.MaxValue,error.Revision);
        Assert.Equal(before,studio.GetProjectRecord(clipId));
        Assert.Equal(backups,BackupCount(clipId));
        Assert.Equal(0,EditorRevisionStore.SnapshotDepth(clipId));
        Assert.Equal(1,EditorRevisionStore.RedoDepth(clipId));
        EditorRevisionStore.EnsureSession(clipId,session.SessionId);
        Assert.Equal(session,EditorRevisionStore.EndSession(clipId,session.SessionId));
        Assert.Equal(1,EditorRevisionStore.SnapshotDepth(clipId));
    }

    [Fact]
    public void ExternalWrite_LastRepresentableRevisionSucceeds_AndNoOpAtLimitStillWorks()
    {
        var (studio,clipId)=TemporaryStore(int.MaxValue-1L);
        var accepted=EditorRevisionStore.SaveExternal(studio,clipId,"test",_=>"{\"end\":2}");
        Assert.True(accepted.Saved);
        Assert.Equal(int.MaxValue,accepted.Revision);
        var before=studio.GetProjectRecord(clipId)!;
        var backups=BackupCount(clipId);
        var noOp=EditorRevisionStore.SaveExternal(studio,clipId,"test",current=>current);
        Assert.False(noOp.Saved);
        Assert.Equal(int.MaxValue,noOp.Revision);
        Assert.Equal(before,studio.GetProjectRecord(clipId));
        Assert.Equal(backups,BackupCount(clipId));
    }

    [Fact]
    public void ExternalConflict_ThatCrossesIntBoundary_IsControlledAndDoesNotOverwriteRival()
    {
        var (studio,clipId)=TemporaryStore(int.MaxValue-1L);
        var error=Assert.Throws<EditorRevisionConflictException>(()=>
            EditorRevisionStore.SaveExternal(studio,clipId,"test",_=>
            {
                SetRevision(clipId,(long)int.MaxValue+1);
                return "{\"end\":3}";
            }));
        Assert.Equal("revision-limit",error.Code);
        Assert.Equal(int.MaxValue,error.Revision);
        Assert.Equal((long)int.MaxValue+1,studio.GetProjectRevision(clipId));
        Assert.Equal("{\"end\":1}",studio.Project(clipId));
        Assert.Equal(0,EditorRevisionStore.SnapshotDepth(clipId));
        Assert.Equal(0,EditorRevisionStore.RedoDepth(clipId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyOutOfRangeRevision_ReportsControlledErrorBeforeAnySave(bool external)
    {
        var (studio,clipId)=TemporaryStore((long)int.MaxValue+1);
        var before=studio.GetProjectRecord(clipId)!;
        var invoked=false;
        var error=Assert.Throws<EditorRevisionConflictException>(()=>
        {
            if(external)
                EditorRevisionStore.SaveExternal(studio,clipId,"test",_=>{invoked=true;return "{\"end\":3}";});
            else
                EditorRevisionStore.Commit(studio,clipId,"{\"end\":3}",null);
        });
        Assert.Equal("revision-limit",error.Code);
        Assert.Equal(int.MaxValue,error.Revision);
        Assert.False(invoked);
        Assert.Equal(before,studio.GetProjectRecord(clipId));
        Assert.Equal(0,BackupCount(clipId));
    }

    private (StudioStore studio,string clipId) TemporaryStore(long revision)
    {
        var store=new Store(DatabasePath);
        store.Migrate();
        var studio=new StudioStore(DatabasePath);
        studio.Migrate();
        var clipId="revision-overflow-"+Guid.NewGuid().ToString("N");
        _clipIds.Add(clipId);
        studio.SaveProject(clipId,"{\"end\":1}");
        SetRevision(clipId,revision);
        return(studio,clipId);
    }

    private void SetRevision(string clipId,long revision)
    {
        using var db=new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        db.Open();
        using var command=db.CreateCommand();
        command.CommandText="UPDATE editor_projects SET revision=$revision WHERE clip_id=$clip";
        command.Parameters.AddWithValue("$revision",revision);
        command.Parameters.AddWithValue("$clip",clipId);
        Assert.Equal(1,command.ExecuteNonQuery());
    }

    private int BackupCount(string clipId)
    {
        var directory=Path.Combine(_root,"project-backups",clipId);
        return Directory.Exists(directory)?Directory.GetFiles(directory,"*.json").Length:0;
    }
}
