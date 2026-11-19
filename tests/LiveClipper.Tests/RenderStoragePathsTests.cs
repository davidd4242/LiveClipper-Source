using System.Reflection;
using System.Text.Json;
using LiveClipper.App;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

public sealed class RenderStoragePathsTests
{
    private static string Root()=>Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"LiveClipper-RenderStorageTests-"+Guid.NewGuid().ToString("N"))).FullName;
    [Fact]
    public void DefaultsPreserveExistingWorkAndPerProjectOutput()
    {
        var root=Root();var output=Path.Combine(root,"project-output");
        Assert.Equal(Path.Combine(root,"render-work"),RenderStoragePaths.WorkRoot(root,""));
        Assert.Equal(output,RenderStoragePaths.OutputRoot(root,output,null));
        Assert.Equal(Path.Combine(root,"edited-clips"),RenderStoragePaths.OutputRoot(root,null,""));
        Assert.Equal(Path.Combine(root,"custom-work"),RenderStoragePaths.WorkRoot(root,Path.Combine(root,"custom-work")));
        Assert.Equal(Path.Combine(root,"exports"),RenderStoragePaths.OutputRoot(root,output,Path.Combine(root,"exports")));
    }
    [Fact]
    public void SettingsValidatePatchPersistAndReloadWithoutChangingStorageRoot()
    {
        var root=Root();var settings=new LiveSettings{StorageRoot=Path.Combine(root,"data")};
        var work=Path.Combine(root,"work");var output=Path.Combine(root,"exports");
        using var patch=JsonDocument.Parse(JsonSerializer.Serialize(new{renderWorkDirectory=work,exportOutputDirectory=output}));
        Assert.True(LiveSettingsMutations.TryApplyPatch(settings,patch.RootElement,out var merged,out var invalid,out var changed));
        Assert.Empty(invalid);Assert.Contains("renderWorkDirectory",changed);Assert.Contains("exportOutputDirectory",changed);
        Assert.Equal("",settings.RenderWorkDirectory);Assert.Equal(settings.StorageRoot,merged.StorageRoot);
        var config=Path.Combine(root,"test-settings.json");
        Assert.True(StartupDurability.UpdateSettings(config,settings,()=>{settings.RenderWorkDirectory=merged.RenderWorkDirectory;settings.ExportOutputDirectory=merged.ExportOutputDirectory;return true;}));
        var restored=JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(config))!;
        Assert.Equal(work,restored.RenderWorkDirectory);Assert.Equal(output,restored.ExportOutputDirectory);Assert.Equal(Path.Combine(root,"data"),restored.StorageRoot);
        using var reset=JsonDocument.Parse("{\"renderWorkDirectory\":\"\",\"exportOutputDirectory\":\"\"}");
        Assert.True(LiveSettingsMutations.TryApplyPatch(restored,reset.RootElement,out var defaults,out _,out _));
        Assert.Equal(Path.Combine(settings.StorageRoot,"render-work"),RenderStoragePaths.WorkRoot(settings.StorageRoot,defaults.RenderWorkDirectory));
    }
    [Fact]
    public void RecordingDirectoryPatchSurvivesMonitorSaveAndReload()
    {
        var root=Root();
        try
        {
            var settings=new LiveSettings{StorageRoot=Path.Combine(root,"data")};
            var recording=Path.Combine(root,"recordings");
            using var patch=JsonDocument.Parse(JsonSerializer.Serialize(new{recordingDirectory=recording}));
            Assert.True(LiveSettingsMutations.TryApplyPatch(settings,patch.RootElement,out var merged,out var invalid,out var changed));
            Assert.Empty(invalid);Assert.Contains("recordingDirectory",changed);Assert.Equal(recording,merged.RecordingDirectory);
            var config=Path.Combine(root,"settings.json");
            Assert.True(StartupDurability.UpdateSettings(config,settings,()=>{settings.RecordingDirectory=RenderStoragePaths.NormalizeDirectory(merged.RecordingDirectory);return true;}));
            Assert.Equal(recording,JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(config))!.RecordingDirectory);
            Assert.True(StartupDurability.UpdateSettings(config,settings,()=>{settings.RoomIds.Add(21603945);return true;}));
            var reopened=JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(config))!;
            Assert.Equal(recording,reopened.RecordingDirectory);Assert.Equal(recording,reopened.RecordingRoot);
            Assert.Equal(Path.Combine(root,"data"),reopened.StorageRoot);Assert.Contains(21603945,reopened.RoomIds);Assert.Empty(reopened.AutoRecordingRoomIds);
            using var invalidPatch=JsonDocument.Parse("{\"recordingDirectory\":\"relative\"}");
            Assert.False(LiveSettingsMutations.TryApplyPatch(reopened,invalidPatch.RootElement,out _,out invalid,out _));
            Assert.Contains("recordingDirectory",invalid);Assert.Equal(recording,reopened.RecordingDirectory);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void InvalidDirectoriesAreRejectedBeforeSettingsMutation()
    {
        var root=Root();var existing=Path.Combine(root,"file.txt");File.WriteAllText(existing,"keep");
        foreach(var value in new[]{"relative",Path.GetPathRoot(root)!,Path.Combine(root,"..","escape"),existing})
        {
            Assert.False(RenderStoragePaths.IsValidDirectory(value));
            var settings=new LiveSettings();using var patch=JsonDocument.Parse(JsonSerializer.Serialize(new{renderWorkDirectory=value}));
            Assert.False(LiveSettingsMutations.TryApplyPatch(settings,patch.RootElement,out _,out var invalid,out _));
            Assert.Contains("renderWorkDirectory",invalid);Assert.Equal("",settings.RenderWorkDirectory);
        }
        Assert.Equal("keep",File.ReadAllText(existing));
    }
    [Fact]
    public void CapturedScopeCleansOnlyOwnedJobAfterSettingsChange()
    {
        var root=Root();var original=Path.Combine(root,"first");var changed=Path.Combine(root,"second");
        var output=Path.Combine(root,"result.mp4");File.WriteAllText(output,"completed");
        var scope=RenderWorkScope.Capture(root,original,"job-one",output);scope.Create();
        File.WriteAllText(Path.Combine(scope.Work,"part.mkv"),"part");
        Directory.CreateDirectory(Path.Combine(original,"job-two"));File.WriteAllText(Path.Combine(original,"job-two","keep"),"other job");
        Directory.CreateDirectory(Path.Combine(changed,"job-one"));File.WriteAllText(Path.Combine(changed,"job-one","keep"),"new setting");
        scope.Cleanup();Assert.False(Directory.Exists(scope.Work));Assert.Equal(original,scope.Root);
        Assert.True(File.Exists(Path.Combine(original,"job-two","keep")));Assert.True(File.Exists(Path.Combine(changed,"job-one","keep")));Assert.Equal("completed",File.ReadAllText(output));
    }
    [Fact]
    public void UnownedFolderAndOutputInsideWorkAreNeverDeleted()
    {
        var root=Root();var work=Path.Combine(root,"work");var job=Path.Combine(work,"job");Directory.CreateDirectory(job);
        var keep=Path.Combine(job,"keep.mp4");File.WriteAllText(keep,"user file");
        Assert.Throws<ArgumentException>(()=>RenderWorkScope.Capture(root,work,"job",keep));
        var scope=RenderWorkScope.Capture(root,work,"job",Path.Combine(root,"out.mp4"));
        Assert.Throws<IOException>(()=>scope.Create());scope.Cleanup();Assert.Equal("user file",File.ReadAllText(keep));
        foreach(var id in new[]{"..","x"+Path.DirectorySeparatorChar+"y"})Assert.Throws<ArgumentException>(()=>RenderWorkScope.Capture(root,work,id,Path.Combine(root,"out.mp4")));
    }
    [Fact]
    public async Task ScheduledJobCapturesSettingOnce_QueuedCancellationPreservesExistingOutput()
    {
        var root=Root();var store=new Store(Path.Combine(root,"test.db"));store.Migrate();var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var current=Path.Combine(root,"first");var reads=0;
        var service=new EditorRenderService(store,studio,new MediaTools("missing-test-ffmpeg"),root,()=>{reads++;return current;});
        var worker=(SemaphoreSlim)typeof(EditorRenderService).GetField("_worker",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(service)!;
        await worker.WaitAsync();
        var id=Guid.NewGuid().ToString("N");var clip=new Clip("clip","event",Path.Combine(root,"source.mp4"),null,DateTimeOffset.UtcNow);
        var output=Path.Combine(root,"keep.mp4");File.WriteAllText(output,"existing output");
        var render=new EditorRender(id,clip.Id,output,JsonSerializer.Serialize(new EditorProject{Start=0,End=1}),DateTimeOffset.UtcNow);
        var job=new Job(id,"edit-render",clip.EventId,"pending",0,null);studio.SaveRender(render);store.SaveJob(job);
        typeof(EditorRenderService).GetMethod("Schedule",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(service,[job,render,clip]);
        current=Path.Combine(root,"changed");await service.StopAsync();worker.Release();
        Assert.Equal(1,reads);Assert.Equal("existing output",File.ReadAllText(output));Assert.False(Directory.Exists(Path.Combine(root,"changed")));
    }
}
