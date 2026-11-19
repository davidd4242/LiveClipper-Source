using LiveClipper.Core;
using LiveClipper.Domain;
namespace LiveClipper.Tests;
public sealed class SourceSrtImportTests
{
    private const string Srt="1\n00:00:01,000 --> 00:00:03,000\nG2 synthetic source subtitle.\n";
    private sealed class Fixture
    {
        public readonly string Root=Path.Combine(Path.GetTempPath(),"liveclipper-source-srt-"+Guid.NewGuid().ToString("N"));
        public readonly Store Store;
        public readonly Clip Clip;
        public readonly StudioMediaService Service;
        public Fixture()
        {
            Directory.CreateDirectory(Root);Store=new Store(Path.Combine(Root,"test.db"));Store.Migrate();
            Clip=new Clip("synthetic","event",Path.Combine(Root,"test.mp4"),null,DateTimeOffset.UtcNow);Store.SaveClip(Clip);
            Service=new StudioMediaService(Store,new StudioStore(Store.DatabasePath),new MediaTools(),Root);
        }
    }
    [Fact] public async Task SuccessfulImportHasMatchingFileAndRecordAndRefusesOverwrite()
    {
        var f=new Fixture();var first=await f.Service.ImportSourceSrtIfAbsentAsync(f.Clip,Srt,default);
        Assert.Null(first.Error);Assert.Single(first.Transcript!.Segments);
        var clip=Assert.Single(f.Store.Clips());Assert.NotNull(clip.SrtPath);var bytes=File.ReadAllBytes(clip.SrtPath!);var json=f.Store.TranscriptJson("event");
        var second=await f.Service.ImportSourceSrtIfAbsentAsync(f.Clip,Srt.Replace("G2","Changed"),default);
        Assert.Equal("source_transcript_exists",second.Error);Assert.Equal(json,f.Store.TranscriptJson("event"));Assert.Equal(bytes,File.ReadAllBytes(clip.SrtPath!));
        Assert.Single(Directory.GetFiles(Path.Combine(f.Root,"imports",f.Clip.Id),"*.srt"));
    }
    [Theory] [InlineData("pending")] [InlineData("running")] [InlineData("retried")]
    public async Task ActiveTranscriptionRefusesWithoutPublishingFileOrRecord(string status)
    {
        var f=new Fixture();f.Store.SaveJob(new Job("job","transcribe","event",status,0,null));
        var result=await f.Service.ImportSourceSrtIfAbsentAsync(f.Clip,Srt,default);
        Assert.Equal("transcribing",result.Error);Assert.Null(f.Store.TranscriptJson("event"));Assert.Null(Assert.Single(f.Store.Clips()).SrtPath);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root,"imports",f.Clip.Id),"*.srt"));
    }
    [Fact] public async Task InvalidAndCancelledInputsDoNotPublish()
    {
        var f=new Fixture();Assert.Equal("invalid_source_srt",(await f.Service.ImportSourceSrtIfAbsentAsync(f.Clip,"not subtitles",default)).Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Service.ImportSourceSrtIfAbsentAsync(f.Clip,Srt,new CancellationToken(true)));
        Assert.Null(f.Store.TranscriptJson("event"));Assert.False(Directory.Exists(Path.Combine(f.Root,"imports")));
    }
    [Fact] public async Task ConcurrentImportsHaveOneWinnerAndOneMatchingFile()
    {
        var f=new Fixture();var results=await Task.WhenAll(Enumerable.Range(0,2).Select(i=>Task.Run(()=>f.Service.ImportSourceSrtIfAbsentAsync(f.Clip,Srt.Replace("G2","G2-"+i),default))));
        Assert.Single(results.Where(x=>x.Error is null));Assert.Single(results.Where(x=>x.Error=="source_transcript_exists"));
        var clip=Assert.Single(f.Store.Clips());Assert.Single(Directory.GetFiles(Path.Combine(f.Root,"imports",f.Clip.Id),"*.srt"));
        Assert.Equal(SubtitleTools.ToJson(SubtitleTools.ParseSrt(await File.ReadAllTextAsync(clip.SrtPath!))),f.Store.TranscriptJson("event"));
    }
}
