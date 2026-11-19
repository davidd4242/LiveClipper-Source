using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class SourceSilencePreviewTests
{
    [Fact]
    public void Parse_ClipsSelectedRangeWithoutRemovingOtherFragments()
    {
        var r=SourceSilencePreviewService.Parse("silence_start: 1.1\nsilence_end: 2.7\nsilence_start: 9",1.2,10,.2);
        Assert.Equal(2,r.Length);Assert.Equal(1.2,r[0].Start);Assert.Equal(2.7,r[0].End);Assert.Equal(10,r[1].End);
        Assert.Empty(SourceSilencePreviewService.Parse("silence_start: 0\nsilence_end: 2",0,2,10));
    }
    [Fact]
    public async Task RealFfmpeg_AllSilentOffsetNoAudioCancellationAndSignature()
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"SilencePreview-"+Guid.NewGuid().ToString("N"))).FullName;
        var ffmpeg=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Links","ffmpeg.exe");
        Assert.True(File.Exists(ffmpeg));var media=new MediaTools(ffmpeg);var service=new SourceSilencePreviewService(media);
        var silent=Path.Combine(root,"silent.mkv");
        await media.RunAsync(media.Ffmpeg,["-y","-f","lavfi","-i","color=s=32x32:r=25:d=3","-f","lavfi","-i","anullsrc=r=48000:cl=stereo","-t","3","-c:v","ffv1","-c:a","pcm_s16le",silent],default);
        var before=File.ReadAllBytes(silent);
        var full=await service.PreviewAsync("s",silent,0,3,-32,.2);
        Assert.Equal("energy-only-fallback",full.EvidenceProvider);Assert.All(full.Silences,x=>{Assert.False(x.AutoEligible);Assert.Contains("vad-unavailable",x.RiskCodes!);});
        var range=Assert.Single(full.Silences);Assert.InRange(range.Start,0,.001);Assert.InRange(range.End,2.99,3);
        Assert.Empty((await service.PreviewAsync("s",silent,0,3,-32,10)).Silences);
        Assert.Equal(before,File.ReadAllBytes(silent));
        var offset=Path.Combine(root,"offset.mkv");
        await media.RunAsync(media.Ffmpeg,["-y","-f","lavfi","-i","color=s=32x32:r=25:d=3","-f","lavfi","-i","anullsrc=r=48000:cl=stereo","-filter_complex","[1:a]atrim=duration=2,asetpts=PTS+0.5/TB[a]","-map","0:v","-map","[a]","-c:v","ffv1","-c:a","pcm_s16le",offset],default);
        var delayed=await service.PreviewAsync("s",offset,0,3,-32,.2);
        Assert.InRange(Assert.Single(delayed.Silences).Start,.49,.51);Assert.Contains(delayed.Warnings,w=>w.Contains("延迟"));
        var video=Path.Combine(root,"video.mkv");
        await media.RunAsync(media.Ffmpeg,["-y","-i",silent,"-an","-c:v","copy",video],default);
        await Assert.ThrowsAsync<NotSupportedException>(()=>service.PreviewAsync("s",video,0,3,-32,.2));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.PreviewAsync("s",silent,0,3,-32,.2,cancel.Token));
        File.SetLastWriteTimeUtc(silent,File.GetLastWriteTimeUtc(silent).AddSeconds(2));
        Assert.NotEqual(full.SourceSignature,SourceSilencePreviewService.Signature(silent));
    }
}
