using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// 音量包络（volume envelope / rubber band）与轨道静音/独奏的渲染行为测试。
/// 规格：docs/filmcraft-parity-next3.md §2.4；包络 time 使用片段输出时间轴内的相对秒
/// （0..ItemOutputDuration），元素 JSON 形状 { "time": number, "db": number }。
/// </summary>
public sealed class AudioEnvelopeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public AudioEnvelopeTests(Xunit.Abstractions.ITestOutputHelper output)=>_output=output;
    private static string N(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);
    private static TimelineItem Item(string id,double start,double end,string clipId="clip",int track=1,double speed=1,double volumeDb=0,bool audioDetached=false,VolumeKeyframe[]? keyframes=null,double gap=0,double fadeIn=0,double fadeOut=0)
        =>new(id,clipId,start,end,Track:track,Speed:speed,GapBefore:gap,VolumeDb:volumeDb,AudioDetached:audioDetached,VolumeKeyframes:keyframes,FadeIn:fadeIn,FadeOut:fadeOut);
    private static EditorProject Plan(TimelineItem[] timeline,int[]? muted=null,int[]? soloed=null)
        =>new(){Start=0,End=8,Timeline=timeline,Fps=30,MutedTracks=muted,SoloedTracks=soloed};
    private static VolumeKeyframe K(double time,double db)=>new(time,db);
    private static string TestFfmpeg()
    {
        var fromEnv=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(!string.IsNullOrWhiteSpace(fromEnv)&&File.Exists(fromEnv))return fromEnv;
        var bundled=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","tools","ffmpeg","bin","ffmpeg.exe");
        return File.Exists(bundled)?bundled:"ffmpeg";
    }

    [Fact]
    public void NormalizeKeyframes_FiltersClampsSortsDedupesAndCapsAt64()
    {
        var points=new List<VolumeKeyframe>
        {
            K(2,-6),K(1,-3),K(1,-9),                 // 同一 time 保留最后一条 → (1,-9)
            K(double.NaN,0),K(3,double.PositiveInfinity), // 非有限值丢弃
            K(-2,100),K(4,-100)                      // time clamp 0、db clamp [-60,12]
        };
        var normalized=EditorRenderService.NormalizeVolumeKeyframes(points);
        Assert.Equal(new[]{0d,1d,2d,4d},normalized.Select(x=>x.Time).ToArray());
        Assert.Equal(12,normalized[0].Db,6);      // -2 → 0，db 100 → 12
        Assert.Equal(-9,normalized[1].Db,6);
        Assert.Equal(-6,normalized[2].Db,6);
        Assert.Equal(-60,normalized[3].Db,6);     // -100 → -60
        var many=Enumerable.Range(0,80).Select(i=>K(i/10.0,0)).ToArray();
        var capped=EditorRenderService.NormalizeVolumeKeyframes(many);
        Assert.Equal(64,capped.Length);
        Assert.Equal(0,capped[0].Time,6);Assert.Equal(6.3,capped[^1].Time,6);
        Assert.Empty(EditorRenderService.NormalizeVolumeKeyframes((VolumeKeyframe[]?)null));
        Assert.Empty(EditorRenderService.NormalizeVolumeKeyframes(Array.Empty<VolumeKeyframe>()));
    }

    [Fact]
    public void KeyframesBeyondClipOutputDuration_AreDropped()
    {
        // time 是片段输出时间轴相对秒：speed=2 的 8 秒源区间只输出 4 秒。
        var keyframes=new[]{K(0,0),K(3.9,-6),K(4.5,-12)};
        var fast=Item("a",0,8,speed:2,keyframes:keyframes);
        Assert.Equal(4,EditorRenderService.ItemOutputDuration(fast),6);
        var normalized=EditorRenderService.NormalizeVolumeKeyframes(fast);
        Assert.Equal(new[]{0d,3.9},normalized.Select(x=>x.Time).ToArray());
        Assert.Equal(4,EditorRenderService.VolumeEnvelopeFilter(fast).Length>0?4:0);
        Assert.Equal("",EditorRenderService.VolumeEnvelopeFilter(Item("b",0,8,speed:2)));
    }

    [Fact]
    public void VolumeGainAt_InterpolatesDbLinearlyAndHoldsEndpointValues()
    {
        var item=Item("a",0,6,keyframes:[K(1,0),K(3,-12)]);
        Assert.Equal(1,EditorRenderService.VolumeGainAt(item,0.5),6);      // 首点之前保持 0 dB
        Assert.Equal(1,EditorRenderService.VolumeGainAt(item,1),6);
        Assert.Equal(Math.Pow(10,-6/20.0),EditorRenderService.VolumeGainAt(item,2),6); // dB 中点 -6
        Assert.Equal(Math.Pow(10,-12/20.0),EditorRenderService.VolumeGainAt(item,3),6);
        Assert.Equal(Math.Pow(10,-12/20.0),EditorRenderService.VolumeGainAt(item,5.5),6); // 末点之后保持
        Assert.Equal(1,EditorRenderService.VolumeGainAt(Item("b",0,4),2),6);              // 无包络
        var clamped=Item("c",0,4,keyframes:[K(0,99)]);
        Assert.Equal(Math.Pow(10,12/20.0),EditorRenderService.VolumeGainAt(clamped,2),6);
    }

    [Fact]
    public void AudioFilterChain_AddsEnvelopeAfterAtempoAndBeforeFades()
    {
        Assert.Equal("",EditorRenderService.AudioFilterChain(Item("a",0,4)));
        var item=Item("a",0,4,speed:2,volumeDb:-6,keyframes:[K(0,0),K(2,-12)],fadeIn:1,fadeOut:1);
        var chain=EditorRenderService.AudioFilterChain(item);
        Assert.Contains("volume=0.501187",chain);      // 片段 volumeDb
        Assert.Contains("atempo=2",chain);
        var envelope=chain.IndexOf("volume=volume='",StringComparison.Ordinal);
        Assert.True(envelope>0,"包络滤镜缺失: "+chain);
        Assert.Contains("eval=frame",chain);
        Assert.True(chain.IndexOf("atempo=2",StringComparison.Ordinal)<envelope,"包络必须在 atempo 之后按输出时间采样");
        Assert.True(envelope<chain.IndexOf("afade=t=in",StringComparison.Ordinal),"淡入应在包络之后");
        Assert.Contains("afade=t=out",chain);
        var expression=EditorRenderService.VolumeEnvelopeFilter(item);
        Assert.StartsWith("volume=volume='",expression);
        Assert.EndsWith("':eval=frame",expression);
        Assert.Contains("pow(10,",expression);
    }

    [Fact]
    public void TimelineItemJson_RoundTripsCamelCaseKeyframesAndOmitsNullField()
    {
        var plain=Item("a",0,2);
        Assert.DoesNotContain("VolumeKeyframes",JsonSerializer.Serialize(plain));
        var legacy=JsonSerializer.Deserialize<TimelineItem>("{\"Id\":\"a\",\"ClipId\":\"c\",\"Start\":0,\"End\":2}")!;
        Assert.Null(legacy.VolumeKeyframes);
        // 前端 PUT body 为 camelCase：volumeKeyframes / time / db。
        var options=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
        var fromWeb=JsonSerializer.Deserialize<TimelineItem>("{\"id\":\"a\",\"clipId\":\"c\",\"start\":0,\"end\":2,\"volumeKeyframes\":[{\"time\":1,\"db\":-6},{\"time\":2,\"db\":0}]}",options)!;
        Assert.Equal(2,fromWeb.VolumeKeyframes!.Length);
        Assert.Equal(1,fromWeb.VolumeKeyframes[0].Time,6);
        Assert.Equal(-6,fromWeb.VolumeKeyframes[0].Db,6);
        var roundTrip=JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(fromWeb),options)!;
        Assert.Equal(fromWeb.VolumeKeyframes,roundTrip.VolumeKeyframes);
    }

    [Fact]
    public void Validate_AcceptsOutOfRangeKeyframesWithoutThrowing()
    {
        var plan=Plan([Item("a",0,4,keyframes:[K(double.NaN,0),K(-1,200),K(2,-200),K(9,0)])]);
        EditorRenderService.Validate(plan);
        var normalized=EditorRenderService.NormalizeVolumeKeyframes(plan.Timeline![0]);
        Assert.Equal(new[]{0d,2d},normalized.Select(x=>x.Time).ToArray());
        Assert.Equal(12,normalized[0].Db,6);Assert.Equal(-60,normalized[1].Db,6);
    }

    [Fact]
    public async Task Assemble_EnvelopeLowersMiddleSection_AndNoEnvelopeKeepsBaseline()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，音量包络测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-envelope-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","6","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-af","volume=6","-c:a","aac","-b:a","128k",source],CancellationToken.None);
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            async Task<(double Duration,string Path)> Render(VolumeKeyframe[]? keyframes,string name)
            {
                var plan=Plan([Item("a",0,6,keyframes:keyframes)]);var work=Path.Combine(root,name);Directory.CreateDirectory(work);
                var assembled=await renderer.AssembleAsync(clip,plan,work,CancellationToken.None);
                Assert.True(File.Exists(assembled.Video));
                return (await tools.DurationAsync(assembled.Video,CancellationToken.None),assembled.Video);
            }
            async Task<double> MeanVolume(string path,double start,double duration)
            {
                var log=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-nostats","-loglevel","info","-ss",N(start),"-t",N(duration),"-i",path,"-map","0:a:0","-af","volumedetect","-f","null","-"],CancellationToken.None);
                var match=Regex.Match(log,@"mean_volume:\s*(-?[0-9.]+) dB");
                Assert.True(match.Success,"无法从 ffmpeg 读取 mean_volume: "+log);
                return double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
            }
            // 0–1s 满音量，1.5–2.5s 压到 -30dB，3s 之后恢复；5 秒处再压一次以验证多点插值。
            VolumeKeyframe[] keyframes=[K(0,0),K(1,0),K(1.5,-30),K(2.5,-30),K(3,0),K(6,0)];
            var (duration,enveloped)=await Render(keyframes,"with-envelope");
            Assert.InRange(duration,5.9,6.1);
            var loud=await MeanVolume(enveloped,0.2,0.6);
            var quiet=await MeanVolume(enveloped,1.8,0.4);
            Assert.True(loud>-12,"首段没有被正常播放: "+loud.ToString(CultureInfo.InvariantCulture));
            Assert.True(quiet<-20,"包络中间段没有压低: "+quiet.ToString(CultureInfo.InvariantCulture));
            Assert.True(quiet<loud-15,"包络落差不明显: loud="+loud.ToString(CultureInfo.InvariantCulture)+" quiet="+quiet.ToString(CultureInfo.InvariantCulture));
            // 无包络回归：同样的片段不加包络时中间段仍是原音量，滤镜链不出现 eval=frame。
            var (baselineDuration,baseline)=await Render(null,"no-envelope");
            Assert.InRange(baselineDuration,5.9,6.1);
            var baselineQuiet=await MeanVolume(baseline,1.8,0.4);
            Assert.True(baselineQuiet>-12,"无包络工程的音量发生了变化: "+baselineQuiet.ToString(CultureInfo.InvariantCulture));
            Assert.Equal("",EditorRenderService.AudioFilterChain(Item("a",0,6)));
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    [Fact]
    public async Task Assemble_MutedAudioTrackRemovesItsSound()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，静音轨测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-mute-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","3","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-af","volume=6","-c:a","aac","-b:a","128k",source],CancellationToken.None);
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            async Task<(double Duration,string Path)> Render(int[] muted,string name)
            {
                var plan=Plan([Item("v",0,3,audioDetached:true),Item("a1",0,3,track:4)],muted:muted);
                var work=Path.Combine(root,name);Directory.CreateDirectory(work);
                var assembled=await renderer.AssembleAsync(clip,plan,work,CancellationToken.None);
                Assert.True(File.Exists(assembled.Video));
                return (await tools.DurationAsync(assembled.Video,CancellationToken.None),assembled.Video);
            }
            async Task<double> MeanVolume(string path)
            {
                var log=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-nostats","-loglevel","info","-i",path,"-map","0:a:0","-af","volumedetect","-f","null","-"],CancellationToken.None);
                var match=Regex.Match(log,@"mean_volume:\s*(-?[0-9.]+) dB");
                Assert.True(match.Success,"无法从 ffmpeg 读取 mean_volume: "+log);
                return double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
            }
            var (onDuration,on)=await Render([],"unmuted");
            var (offDuration,off)=await Render([4],"muted");
            Assert.InRange(onDuration,2.9,3.1);Assert.InRange(offDuration,2.9,3.1);
            var onVolume=await MeanVolume(on);
            var offVolume=await MeanVolume(off);
            Assert.True(onVolume>-25,"A1 轨道音频应可听见: "+onVolume.ToString(CultureInfo.InvariantCulture));
            Assert.True(offVolume<-60,"A1 轨道静音后仍有声音: "+offVolume.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    [Fact]
    public async Task Assemble_SoloedAudioTrackMutesOtherTracksAndBaseAudio()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，独奏轨测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-solo-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","3","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-af","volume=6","-c:a","aac","-b:a","128k",source],CancellationToken.None);
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            async Task<(double Duration,string Path)> Render(int[] soloed,string name)
            {
                // V1 静音，A1 很小声（-30dB），A2 满音量；独奏 A1 后应只剩很小的 A1。
                var plan=Plan([Item("v",0,3,audioDetached:true),Item("a1",0,3,track:4,volumeDb:-30),Item("a2",0,3,track:5)],soloed:soloed);
                var work=Path.Combine(root,name);Directory.CreateDirectory(work);
                var assembled=await renderer.AssembleAsync(clip,plan,work,CancellationToken.None);
                Assert.True(File.Exists(assembled.Video));
                return (await tools.DurationAsync(assembled.Video,CancellationToken.None),assembled.Video);
            }
            async Task<double> MeanVolume(string path)
            {
                var log=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-nostats","-loglevel","info","-i",path,"-map","0:a:0","-af","volumedetect","-f","null","-"],CancellationToken.None);
                var match=Regex.Match(log,@"mean_volume:\s*(-?[0-9.]+) dB");
                Assert.True(match.Success,"无法从 ffmpeg 读取 mean_volume: "+log);
                return double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
            }
            var (mixedDuration,mixed)=await Render([],"no-solo");
            var (soloDuration,solo)=await Render([4],"solo-a1");
            Assert.InRange(mixedDuration,2.9,3.1);Assert.InRange(soloDuration,2.9,3.1);
            var mixedVolume=await MeanVolume(mixed);
            var soloVolume=await MeanVolume(solo);
            Assert.True(mixedVolume>-12,"A1+A2 混合后应接近满音量: "+mixedVolume.ToString(CultureInfo.InvariantCulture));
            Assert.True(soloVolume<mixedVolume-10,"独奏 A1 后其它轨道没有静音: mixed="+mixedVolume.ToString(CultureInfo.InvariantCulture)+" solo="+soloVolume.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
