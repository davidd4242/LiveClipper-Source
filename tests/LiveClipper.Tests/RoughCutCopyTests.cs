using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
namespace LiveClipper.Tests;
public sealed class RoughCutCopyTests
{
    [Fact]
    public void FractionalSourceEndpointsKeepExactOffsetsAcrossManyCuts()
    {
        var ranges=Enumerable.Range(0,20).Select(i=>new RoughCutSourceRange(i*3+.013,i*3+2.027)).ToArray();
        var cues=ranges.Select(r=>new TranscriptSegment(r.Start+.123,r.End-.017,"cue")).ToArray();
        var result=RoughCutCopyService.Build("s",new(){Fps=30},cues,60,ranges,Enumerable.Range(1,19).ToArray(),.2);
        var starts=EditorRenderService.SequenceStarts(result.Timeline!,EditorRenderService.ResolveTransitions(result));
        Assert.Equal(ranges.Sum(r=>r.End-r.Start),result.End,9);
        for(var i=0;i<20;i++)
        {
            var caption=result.TimelineCaptions![i];var item=result.Timeline![i];
            Assert.Equal(starts[item.Id]+.123,caption.Start,9);
            Assert.Equal(starts[item.Id]+item.End-item.Start-.017,caption.End,9);
            Assert.Equal(cues[i].Start,caption.SourceStart);Assert.Equal(cues[i].End,caption.SourceEnd);
        }
    }
    [Fact]
    public void AiSceneBoundariesNeverAddTransitionsOrCompressCaptions()
    {
        var snapshot=new EditorProject{Fps=30,Transitions=[new("manual","existing",1,"cross-dissolve",.8,"center","preserve")]};
        var before=JsonSerializer.Serialize(snapshot);
        var ranges=new RoughCutSourceRange[]{new(0,2),new(5,7),new(10,12)};
        var transcript=new TranscriptSegment[]{new(1.8,2,"left"),new(5,5.2,"right"),new(10,11,"last")};
        var result=RoughCutCopyService.Build("s",snapshot,transcript,12,ranges,[1,2],.8);
        Assert.Null(result.Transitions);Assert.Equal(6,result.End);
        Assert.Equal(ranges.Select(r=>r.Start),result.Timeline!.Select(i=>i.Start));
        Assert.Equal(ranges.Select(r=>r.End),result.Timeline!.Select(i=>i.End));
        Assert.Equal(2,result.TimelineCaptions![1].Start);Assert.Equal(4,result.TimelineCaptions[2].Start);
        Assert.Equal(before,JsonSerializer.Serialize(snapshot));
    }
    [Fact]
    public void LegacySceneOptionsCannotEnableAiTransitionsEvenForTinyOrContinuousCuts()
    {
        var p=new EditorProject{Fps=30};
        foreach(var ranges in new RoughCutSourceRange[][]{[new(0,.05),new(1,2)],[new(0,1),new(1,2)]})
        {
            var result=RoughCutCopyService.Build("s",p,[],3,ranges,[1],.8);
            Assert.Null(result.Transitions);Assert.Equal(ranges.Sum(r=>r.End-r.Start),result.End,8);
        }
    }
    [Fact]
    public void LegacyAutomaticReplacementCannotRemoveManualTransitions()
    {
        var p=new EditorProject{Transitions=[new("manual","right",1,"cross-dissolve",.8,"center","preserve")]};
        var before=JsonSerializer.Serialize(p);
        Assert.Throws<ArgumentException>(()=>AutoEditService.RequireNoManualTransitionsForReplacement(p));
        Assert.Equal(before,JsonSerializer.Serialize(p));
        AutoEditService.RequireNoManualTransitionsForReplacement(new EditorProject());
    }
    [Fact]
    public void BuildMapsOnlyChosenSourceRangesAndCaptionsWithoutOldNativeOrEffects()
    {
        var source=new EditorProject{Start=0,End=20,MasterVolume=1.2,Timeline=[new("old","s",0,20,Track:3)]};
        var before=JsonSerializer.Serialize(source);
        var result=RoughCutCopyService.Build("s",source,[new(2,5,"甲"),new(12,16,"乙")],20,[new(12,15),new(2,6)]);
        Assert.Equal(7,result.End);Assert.Equal(12,result.Timeline![0].Start);Assert.All(result.Timeline,x=>Assert.Equal(1,x.Track));
        Assert.Equal(0,result.TimelineCaptions![0].Start);Assert.Equal(3,result.TimelineCaptions[0].End);
        Assert.Equal(3,result.TimelineCaptions[1].Start);Assert.Equal(6,result.TimelineCaptions[1].End);
        Assert.Equal(1,result.MasterVolume);Assert.Null(result.ReplicaExtension);Assert.Equal(before,JsonSerializer.Serialize(source));
        Assert.Throws<ArgumentException>(()=>RoughCutCopyService.Build("s",source,[],20,[]));
        Assert.Throws<ArgumentException>(()=>RoughCutCopyService.Build("s",source,[],20,[new(0,21)]));
    }
    [Fact]
    public async Task IndependentCopyRemapsEventsAndFilesWithoutChangingOriginal_RejectsStaleFingerprint()
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"RoughCutCopy-"+Guid.NewGuid().ToString("N"))).FullName;
        try
        {
        var store=new Store(Path.Combine(root,"fixture.db"));store.Migrate();var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var path=Path.Combine(root,"source.mp4");File.WriteAllBytes(path,[1,2,3]);var srt=Path.Combine(root,"source.srt");File.WriteAllText(srt,"fixture");
        var clip=new Clip("source","recording",path,srt,DateTimeOffset.UtcNow);store.SaveClip(clip);studio.SaveLocalVideo(new(clip.Id,path,"original",root,clip.CreatedAt));
        store.SaveTranscript(clip.EventId,"{\"segments\":[{\"start\":0,\"end\":2,\"text\":\"源字幕\"}]}",srt);
        studio.SaveEvent(new("event",clip.Id,0,4,"candidate","summary","hook","manual"));
        var project=new EditorProject{Start=0,End=10,Timeline=[new("old",clip.Id,0,10)],SourceTranscripts=new(){[clip.Id]=[new(0,2,"修改后源字幕")]}};
        studio.SaveProject(clip.Id,JsonSerializer.Serialize(project));var record=studio.GetProjectRecord(clip.Id)!;
        var preview=new RoughCutPreviewService(store,studio).Preview(clip,project,10);var service=new RoughCutCopyService(store,studio,Path.Combine(root,"copies"));
        await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>service.CreateAsync(clip,"bad",project,10,[new(0,3)],"wrong",preview.CandidateHash,preview.TranscriptCacheKey,record.Revision));Assert.Single(store.Clips());
        var copy=await service.CreateAsync(clip,"独立粗剪",project,10,[new(0,3),new(5,7)],preview.SourceSignature,preview.CandidateHash,preview.TranscriptCacheKey,record.Revision,beforeRanges:[new(0,4),new(5,9)],sceneBoundaryIndexes:[1]);
        Assert.NotEqual(clip.Id,copy.ClipId);Assert.Equal(record,studio.GetProjectRecord(clip.Id));Assert.Equal(2,store.Clips().Count);
        var result=EditorProjectCodec.Deserialize(studio.Project(copy.ClipId)!);Assert.Equal(5,result.End,6);Assert.Empty(copy.NaturalTransitions.Boundaries);Assert.Equal(0,copy.NaturalTransitions.TotalShortening);Assert.All(result.Timeline!,i=>Assert.Equal(copy.ClipId,i.ClipId));
        Assert.Equal("修改后源字幕",Assert.Single(result.TimelineCaptions!).Text);Assert.True(result.SourceTranscripts!.ContainsKey(copy.ClipId));
        var ev=Assert.Single(studio.Events(copy.ClipId));Assert.NotEqual("event",ev.Id);Assert.Equal(copy.ClipId,ev.ClipId);
        var copied=store.Clips().Single(c=>c.Id==copy.ClipId);Assert.NotEqual(path,copied.Path);Assert.Equal(File.ReadAllBytes(path),File.ReadAllBytes(copied.Path));
        Assert.NotNull(studio.Document("roughcut-restore",copy.ClipId));
        Assert.Null(studio.Document("roughcut-restore",clip.Id));
        var packages=new ProjectPackageService(studio,Path.Combine(root,"packages"));
        var package=await packages.ExportAsync(copy.ClipId,"portable",result,copy.Revision);
        var targetStore=new Store(Path.Combine(root,"target.db"));targetStore.Migrate();var targetStudio=new StudioStore(targetStore.DatabasePath);targetStudio.Migrate();
        var importer=new ProjectPackageService(targetStudio,Path.Combine(root,"imports"));
        await using var stream=File.OpenRead(packages.DownloadPath(package.Token)!);var imported=await importer.ImportAsync(stream);
        var restored=new RoughCutCopyService(targetStore,targetStudio,"").Restore(imported.ClipId,1);
        Assert.Equal(8,restored.Project.End);Assert.Null(restored.Project.Transitions);Assert.Equal(2,restored.Revision);
        Assert.Equal(record,studio.GetProjectRecord(clip.Id));
        var secondCopy=await new ProjectCopyService(studio,Path.Combine(root,"second-copy")).CopyAsync(copy.ClipId,"再次复制",result,copy.Revision);
        Assert.NotNull(studio.Document("roughcut-restore",secondCopy.ClipId));
        Assert.Equal(8,service.Restore(secondCopy.ClipId,1).Project.End);
        var changed=result with {MasterVolume=1.1};studio.SaveProject(copy.ClipId,JsonSerializer.Serialize(changed));
        var changedRecord=studio.GetProjectRecord(copy.ClipId)!;
        Assert.Throws<ProjectCopyConflictException>(()=>service.Restore(copy.ClipId,(int)changedRecord.Revision));
        Assert.Equal(changedRecord,studio.GetProjectRecord(copy.ClipId));
        File.WriteAllBytes(copied.Path,[9]);Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(path));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
}
