using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
namespace LiveClipper.Tests;
public sealed class RoughCutPreviewTests
{
    [Fact]
    public void TranscriptPriority_EmptyOverrideIsAuthoritative_NotTimelineCaptions()
    {
        var p=new EditorProject{SourceTranscripts=new(){["s"]=[new(1,2,"冻结源字幕")]},CaptionOverrides=new(){["s"]=[]}};
        Assert.Empty(RoughCutPreviewService.ResolveTranscript(p,"s","bad json"));
        Assert.Equal("冻结源字幕",Assert.Single(RoughCutPreviewService.ResolveTranscript(p with {CaptionOverrides=null},"s","bad json")).Text);
        Assert.Equal("原字幕",Assert.Single(RoughCutPreviewService.ResolveTranscript(new(),"s","{\"segments\":[{\"start\":0,\"end\":1,\"text\":\"原字幕\"}]}")).Text);
    }
    [Fact]
    public void LiteralHints_NeverTreatNegationNumbersOrShortResponsesAsDuplicates()
    {
        TranscriptSegment[] lines=[new(0,1,"今天买了三个"),new(2,3,"今天买了四个"),new(4,5,"今天没有买"),new(6,7," 今天买了三个 "),new(8,9,"好的"),new(10,11,"好的"),new(40,41,"今天买了三个")];
        var hint=Assert.Single(RoughCutPreviewService.FindRepeats(lines));Assert.Equal(0,hint.FirstIndex);Assert.Equal(3,hint.SecondIndex);
    }
    [Fact]
    public void ExistingFiveCandidates_PreserveIdsAndOriginalDb_AllFingerprintsDetectChanges()
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"RoughCutPreview-"+Guid.NewGuid().ToString("N"))).FullName;
        var store=new Store(Path.Combine(root,"fixture.db"));store.Migrate();var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var path=Path.Combine(root,"source.bin");File.WriteAllBytes(path,[1,2,3]);
        var clip=new Clip("source","event",path,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
        var project=new EditorProject{Start=0,End=60,SourceTranscripts=new(){[clip.Id]=[new(0,1,"同一段字幕内容"),new(2,3,"同一段字幕内容")]}};
        studio.SaveProject(clip.Id,JsonSerializer.Serialize(project));
        for(var i=0;i<5;i++)studio.SaveEvent(new("candidate-"+i,clip.Id,i*10,i*10+5,"标题"+i,"摘要","原句","ai"));
        var original=studio.GetProjectRecord(clip.Id);var events=JsonSerializer.Serialize(studio.Events(clip.Id));
        var service=new RoughCutPreviewService(store,studio);var result=service.Preview(clip,project,60);
        Assert.Equal("cached-candidates-and-text-rules",result.Mode);Assert.Equal(5,result.Candidates.Length);
        Assert.All(result.Candidates,x=>Assert.Equal(clip.Id,x.ClipId));Assert.Equal("candidate-4",result.Candidates[4].Id);
        Assert.Equal(original,studio.GetProjectRecord(clip.Id));Assert.Equal(events,JsonSerializer.Serialize(studio.Events(clip.Id)));
        Assert.Null(studio.Document("cut-suggestion",clip.Id));
        Assert.Equal(result.CandidateHash,service.Preview(clip,project,60).CandidateHash);
        var changed=project with {CaptionOverrides=new(){[clip.Id]=[new(0,1,"已修改字幕")]}};
        Assert.NotEqual(result.TranscriptCacheKey,service.Preview(clip,changed,60).TranscriptCacheKey);
        studio.SaveEvent(result.Candidates[0] with {Title="改标题"});Assert.NotEqual(result.CandidateHash,service.Preview(clip,project,60).CandidateHash);
        File.SetLastWriteTimeUtc(path,File.GetLastWriteTimeUtc(path).AddSeconds(2));Assert.NotEqual(result.SourceSignature,service.Preview(clip,project,60).SourceSignature);
    }
}
