using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

public sealed class EditorRenderMixTests
{
    [Fact]
    public void AudioSelection_MuteExcludesTracksWhenNothingIsSoloed()
    {
        Assert.Equal(new[]{1,2,3,5},Selected([4,6],null));
        Assert.Equal(new[]{1,2,3,4,5,6},Selected(null,null));
        Assert.Equal(new[]{1,2,3},Selected([4,5,6],[]));
    }

    [Fact]
    public void AudioSelection_SoloWinsOverMuteAndExcludesBaseAndOverlays()
    {
        Assert.Equal(new[]{4},Selected([4,5],[4]));
        Assert.Equal(new[]{4,6},Selected([4,5,6],[4,6]));
        Assert.Empty(Selected(null,[7]));
    }

    private static int[] Selected(int[]? muted,int[]? soloed)=>Enumerable.Range(1,6)
        .Where(track=>EditorRenderService.IncludeTrackAudio(track,muted,soloed)).ToArray();

    [Theory]
    [InlineData(1280,720,30,"3.1")]
    [InlineData(720,1280,30,"3.1")]
    [InlineData(1280,720,60,"4")]
    [InlineData(1920,1080,30,"4")]
    [InlineData(1920,1080,60,"4.1")]
    [InlineData(1080,1920,60,"4.1")]
    [InlineData(3840,2160,30,"5")]
    [InlineData(3840,2160,60,"5.1")]
    [InlineData(4096,2160,60,"5.1")]
    public void HevcLevel_UsesActualPictureAndSampleRate(int width,int height,double fps,string level)
    {
        Assert.Equal(level,EditorRenderService.HevcLevel(width,height,fps));
        var args=EditorRenderService.VideoCodecArgs("hevc",0,width,height,fps);
        Assert.Equal($"level-idc={level}:high-tier=0",Value(args,"-x265-params"));
    }

    [Theory]
    [InlineData(0,720,30)]
    [InlineData(1280,-1,30)]
    [InlineData(1280,720,0)]
    [InlineData(1280,720,double.NaN)]
    [InlineData(1280,720,double.PositiveInfinity)]
    [InlineData(7680,4320,60)]
    [InlineData(20000,2,30)]
    public void HevcLevel_RejectsInvalidOrUnsupportedOutput(int width,int height,double fps)
        =>Assert.Throws<ArgumentException>(()=>EditorRenderService.VideoCodecArgs("hevc",0,width,height,fps));

    [Fact]
    public void Hevc_DefaultAndBt709KeepEightBitAndOnlyAddRequestedColorTags()
    {
        Assert.Equal(new[]{"-c:v","libx265","-preset","fast","-tag:v","hvc1","-crf","24","-pix_fmt","yuv420p","-x265-params","level-idc=4.1:high-tier=0"},
            EditorRenderService.VideoCodecArgs("hevc",0,1920,1080,60));
        Assert.Equal(new[]{"-c:v","libx265","-preset","fast","-tag:v","hvc1","-b:v","8000k","-pix_fmt","yuv420p","-x265-params","level-idc=4:high-tier=0:colorprim=1:transfer=1:colormatrix=1","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"},
            EditorRenderService.VideoCodecArgs("hevc",8000,1920,1080,30,"bt709"));
    }

    [Fact]
    public void Hevc_Hdr10PinsTenBitPqBt2020AndMainTier()
    {
        Assert.Equal(new[]{"-c:v","libx265","-preset","fast","-tag:v","hvc1","-crf","24","-pix_fmt","yuv420p10le","-x265-params","level-idc=4.1:high-tier=0:colorprim=9:transfer=16:colormatrix=9:hdr10=1:hdr10-opt=1:repeat-headers=1","-color_primaries","bt2020","-color_trc","smpte2084","-colorspace","bt2020nc"},
            EditorRenderService.VideoCodecArgs(" HEVC ",0,1920,1080,60,"hdr10"));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.VideoCodecArgs("hevc",0,1920,1080,60,"hlg"));
    }

    [Theory]
    [InlineData(null,0,"-c:v|libx264|-preset|veryfast|-crf|22|-pix_fmt|yuv420p")]
    [InlineData("h264",0,"-c:v|libx264|-preset|veryfast|-crf|22|-pix_fmt|yuv420p")]
    [InlineData("h264",8000,"-c:v|libx264|-preset|veryfast|-b:v|8000k|-pix_fmt|yuv420p")]
    [InlineData("prores",0,"-c:v|prores_ks|-profile:v|3|-vendor|apl0|-pix_fmt|yuv422p10le")]
    [InlineData("prores",8000,"-c:v|prores_ks|-profile:v|3|-vendor|apl0|-pix_fmt|yuv422p10le")]
    [InlineData("dnxhr",0,"-c:v|dnxhd|-profile:v|dnxhr_sq|-pix_fmt|yuv422p")]
    [InlineData("dnxhr",8000,"-c:v|dnxhd|-profile:v|dnxhr_sq|-pix_fmt|yuv422p")]
    public void NonHevc_ArgumentsAreByteExactIncludingOrder(string? format,int bitrate,string expected)
    {
        // NUL separates argv elements, so both every UTF-8 byte and every argument boundary are pinned.
        var bytes=System.Text.Encoding.UTF8.GetBytes(string.Join('\0',expected.Split('|')));
        Assert.Equal(bytes,System.Text.Encoding.UTF8.GetBytes(string.Join('\0',EditorRenderService.VideoCodecArgs(format,bitrate))));
        // R66 (HDR chain): DNxHR is the one non-HEVC format whose hdr10 request is now honoured (10-bit HQX + BT.2020/PQ tags);
        // every other non-HEVC format must stay byte-identical under hdr10.
        var hdrExpected=format=="dnxhr"
            ? "-c:v|dnxhd|-profile:v|dnxhr_hqx|-pix_fmt|yuv422p10le|-color_primaries|bt2020|-color_trc|smpte2084|-colorspace|bt2020nc"
            : expected;
        var hdrBytes=System.Text.Encoding.UTF8.GetBytes(string.Join('\0',hdrExpected.Split('|')));
        Assert.Equal(hdrBytes,System.Text.Encoding.UTF8.GetBytes(string.Join('\0',EditorRenderService.VideoCodecArgs(format,bitrate,3840,2160,60,"hdr10"))));
    }

    [Fact]
    public void ColorSpace_LegacyJsonIsUnchangedAndOptInRoundTrips()
    {
        var options=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
        var legacy=JsonSerializer.Deserialize<EditorProject>("{\"start\":0,\"end\":2}",options)!;
        Assert.Null(legacy.ColorSpace);
        var json=JsonSerializer.Serialize(legacy,options);
        Assert.DoesNotContain("colorSpace",json);
        Assert.Equal(json,JsonSerializer.Serialize(legacy with{ColorSpace=null},options));
        foreach(var color in new[]{"hdr10","bt709"})
        {
            var restored=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(legacy with{Format="hevc",ColorSpace=color},options),options)!;
            Assert.Equal(color,restored.ColorSpace);
        }
    }

    [Fact]
    public async Task Assemble_MuteSoloAndHiddenVideoKeepIndependentBehavior()
    {
        var tools=new MediaTools(TestFfmpeg());
        if(!tools.Available())throw new InvalidOperationException("FFmpeg is required for EditorRenderMixTests; set LIVECLIPPER_TEST_FFMPEG.");
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-r50-mix-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","color=c=white:size=160x90:rate=24","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","1","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac",source],CancellationToken.None);
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            var baseItem=new TimelineItem("v","clip",0,1);
            var audio=new TimelineItem("a","clip",0,1,Track:4);
            async Task<string> Render(string name,TimelineItem[] items,int[]? muted=null,int[]? solo=null,int[]? hidden=null)
            {
                var plan=new EditorProject{Start=0,End=1,Timeline=items,Fps=24,Resolution=480,MutedTracks=muted,SoloedTracks=solo,HiddenTracks=hidden};
                var work=Path.Combine(root,name);Directory.CreateDirectory(work);
                return (await renderer.AssembleAsync(clip,plan,work,CancellationToken.None)).Video;
            }
            async Task<double> Rms(string path)
            {
                var log=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-nostats","-i",path,"-map","0:a:0","-af","astats=metadata=0:reset=0","-f","null","-"],CancellationToken.None);
                var match=Regex.Match(log,@"RMS level dB:\s*(-?inf|-?[0-9.]+)");
                Assert.True(match.Success,log);
                return match.Groups[1].Value=="-inf"?double.NegativeInfinity:double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
            }
            var soloWins=await Render("solo-wins",[baseItem,audio],[4],[4]);
            var muted=await Render("muted",[baseItem with{AudioDetached=true},audio],[4]);
            var emptySolo=await Render("empty-solo",[baseItem],solo:[4]);
            var hidden=await Render("hidden",[baseItem],hidden:[1]);
            Assert.True(await Rms(soloWins)>-30,"Same-track solo must win over mute.");
            Assert.True(await Rms(muted)<-70,"Muted A1 leaked into the mix.");
            Assert.True(await Rms(emptySolo)<-70,"Soloing an empty A track must silence V1.");
            Assert.True(await Rms(hidden)>-30,"Hiding V1 must not mute its embedded audio.");
            var visibleLog=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-i",soloWins,"-vf","signalstats,metadata=print:key=lavfi.signalstats.YAVG","-an","-frames:v","1","-f","null","-"],CancellationToken.None);
            var hiddenLog=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-i",hidden,"-vf","signalstats,metadata=print:key=lavfi.signalstats.YAVG","-an","-frames:v","1","-f","null","-"],CancellationToken.None);
            Assert.True(YAverage(visibleLog)>220,visibleLog);
            Assert.InRange(YAverage(hiddenLog),15,17);
        }
        finally
        {
            await renderer.StopAsync();
            Directory.Delete(root,true);
        }
    }

    private static double YAverage(string log)
    {
        var match=Regex.Match(log,@"lavfi.signalstats.YAVG=([0-9.]+)");
        Assert.True(match.Success,log);
        return double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
    }
    private static string Value(string[] args,string key)
    {
        var at=Array.IndexOf(args,key);Assert.True(at>=0,key);
        return args[at+1];
    }
    private static string TestFfmpeg()
    {
        var fromEnv=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(!string.IsNullOrWhiteSpace(fromEnv)&&File.Exists(fromEnv))return fromEnv;
        var bundled=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","tools","ffmpeg","bin","ffmpeg.exe");
        return File.Exists(bundled)?bundled:"ffmpeg";
    }
}
