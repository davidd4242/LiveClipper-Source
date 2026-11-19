using System.Globalization;
using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class SmallAudioTimestampTests
{
    private const string Proposed="aresample=48000:async=1:first_pts=0:min_comp=0:min_hard_comp=0";
    private static MediaTools Tools()=>new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Links","ffmpeg.exe"));
    [Theory]
    [InlineData(1920)]
    [InlineData(-1920)]
    [InlineData(24)]
    [InlineData(-24)]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task PositiveAndNegativeMidstreamGap_HasExactSampleAdjustment(int deltaSamples)
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"SmallPts-"+Guid.NewGuid().ToString("N"))).FullName;
        var media=Tools();var path=Path.Combine(root,"output.s16le");
        var filter="asetnsamples=n=480:p=0,asetpts=PTS+gte(N\\,48000)*"+deltaSamples.ToString(CultureInfo.InvariantCulture)+","+Proposed;
        await media.RunAsync(media.Ffmpeg,["-n","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=2","-af",filter,"-ac","1","-c:a","pcm_s16le","-f","s16le",path],default);
        var data=File.ReadAllBytes(path);Assert.Equal((96000+deltaSamples)*2,data.Length);
        if(deltaSamples>0)Assert.All(data.Skip(48000*2).Take(deltaSamples*2),b=>Assert.Equal((byte)0,b));
    }
    [Theory]
    [InlineData(1920)]
    [InlineData(-1920)]
    [InlineData(24)]
    [InlineData(1)]
    public async Task SourceStartOffset_IsPaddedOrTrimmedToZero(int offsetSamples)
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"StartPts-"+Guid.NewGuid().ToString("N"))).FullName;
        var media=Tools();var path=Path.Combine(root,"output.s16le");
        await media.RunAsync(media.Ffmpeg,["-n","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=2","-af","asetpts=PTS+"+offsetSamples.ToString(CultureInfo.InvariantCulture)+","+Proposed,"-ac","1","-c:a","pcm_s16le","-f","s16le",path],default);
        var data=File.ReadAllBytes(path);Assert.Equal((96000+offsetSamples)*2,data.Length);
        if(offsetSamples>0)Assert.All(data.Take(offsetSamples*2),b=>Assert.Equal((byte)0,b));
    }
    [Fact]
    public async Task Continuous44100Hz60Seconds_DoesNotAccumulateRoundingCorrections()
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"ContinuousPts-"+Guid.NewGuid().ToString("N"))).FullName;
        var media=Tools();var baseline=Path.Combine(root,"baseline.s16le");var proposed=Path.Combine(root,"proposed.s16le");
        foreach(var (path,filter) in new[]{(baseline,"aresample=48000"),(proposed,Proposed)})
            await media.RunAsync(media.Ffmpeg,["-n","-f","lavfi","-i","sine=frequency=440:sample_rate=44100:duration=60","-af",filter,"-ac","1","-c:a","pcm_s16le","-f","s16le",path],default);
        var a=File.ReadAllBytes(baseline);var b=File.ReadAllBytes(proposed);
        Assert.Equal(60*48000*2,a.Length);Assert.Equal(a.Length,b.Length);Assert.Equal(a,b);
    }
}
