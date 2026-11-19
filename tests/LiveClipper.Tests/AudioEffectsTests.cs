using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// 音频效果 v1（compressor / noiseGate）的模型归一化、滤镜链顺序与真实 ffmpeg 渲染测试。
/// 规格：docs/filmcraft-parity-next4.md §2；滤镜顺序 volume → atempo → 效果数组（compressor/noiseGate）→ 包络 → afade。
/// </summary>
public sealed class AudioEffectsTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public AudioEffectsTests(Xunit.Abstractions.ITestOutputHelper output)=>_output=output;
    private static string N(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);
    private static TimelineItem Item(string id,double start,double end,string clipId="clip",int track=1,double speed=1,double volumeDb=0,bool audioDetached=false,VolumeKeyframe[]? keyframes=null,AudioEffectInstance[]? effects=null,double fadeIn=0,double fadeOut=0)
        =>new(id,clipId,start,end,Track:track,Speed:speed,VolumeDb:volumeDb,AudioDetached:audioDetached,VolumeKeyframes:keyframes,Effects:effects,FadeIn:fadeIn,FadeOut:fadeOut);
    private static EditorProject Plan(TimelineItem[] timeline,int[]? muted=null,int[]? soloed=null)
        =>new(){Start=0,End=8,Timeline=timeline,Fps=30,MutedTracks=muted,SoloedTracks=soloed};
    private static Dictionary<string,double> Params(params (string Key,double Value)[] pairs)
        =>pairs.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.OrdinalIgnoreCase);
    private static AudioEffectInstance Effect(string type,bool enabled=true,Dictionary<string,double>? parameters=null)
        =>new("fx-"+type,type,enabled,parameters);
    private static string TestFfmpeg()
    {
        var fromEnv=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(!string.IsNullOrWhiteSpace(fromEnv)&&File.Exists(fromEnv))return fromEnv;
        var bundled=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","tools","ffmpeg","bin","ffmpeg.exe");
        return File.Exists(bundled)?bundled:"ffmpeg";
    }
    private static bool TryGet(AudioEffectInstance effect,string key,out double value)
    {
        value=0;return effect.Params is not null&&effect.Params.TryGetValue(key,out value);
    }

    [Fact]
    public void NormalizeAudioEffects_DropsUnknownTypesAndAliasesKnownOnes()
    {
        Assert.Equal("compressor",EditorRenderService.AudioEffectType("COMP"));
        Assert.Equal("compressor",EditorRenderService.AudioEffectType("compressor"));
        Assert.Equal("noiseGate",EditorRenderService.AudioEffectType("GATE"));
        Assert.Equal("noiseGate",EditorRenderService.AudioEffectType("Noise-Gate"));
        Assert.Null(EditorRenderService.AudioEffectType("reverb"));
        Assert.Null(EditorRenderService.AudioEffectType(null));
        var normalized=EditorRenderService.NormalizeAudioEffects(new[]
        {
            new AudioEffectInstance("a","reverb"),
            new AudioEffectInstance("","compressor"),
            new AudioEffectInstance("g","gate",false,Params(("thresholdDb",-30)))
        });
        Assert.Equal(2,normalized.Length);
        Assert.Equal("compressor",normalized[0].Type);
        Assert.StartsWith("fx-",normalized[0].Id);
        Assert.Equal("noiseGate",normalized[1].Type);
        Assert.False(normalized[1].Enabled);
        Assert.Empty(EditorRenderService.NormalizeAudioEffects((AudioEffectInstance[]?)null));
        var capped=EditorRenderService.NormalizeAudioEffects(Enumerable.Range(0,6).Select(i=>new AudioEffectInstance($"fx{i}","compressor")).ToArray());
        Assert.Equal(4,capped.Length);
    }

    [Fact]
    public void NormalizeAudioEffects_ClampsParametersAndDropsNonFiniteValues()
    {
        var normalized=EditorRenderService.NormalizeAudioEffects(new[]
        {
            new AudioEffectInstance("c","compressor",true,Params(("thresholdDb",-200),("ratio",100),("attackMs",0.0001),("releaseMs",999999),("makeupDb",100),("kneeDb",0))),
            new AudioEffectInstance("g","noiseGate",true,Params(("thresholdDb",-200),("ratio",0),("attackMs",0),("releaseMs",0),("rangeDb",100))),
            new AudioEffectInstance("bad","compressor",true,Params(("thresholdDb",double.NaN))),
            new AudioEffectInstance("bad2","noiseGate",true,Params(("ratio",double.PositiveInfinity)))
        });
        Assert.Equal(2,normalized.Length);
        var compressor=normalized[0];
        Assert.True(TryGet(compressor,"thresholdDb",out var threshold));
        Assert.Equal(-60,threshold,6);
        Assert.True(TryGet(compressor,"ratio",out var ratio));Assert.Equal(20,ratio,6);
        Assert.True(TryGet(compressor,"attackMs",out var attack));Assert.Equal(.1,attack,6);
        Assert.True(TryGet(compressor,"releaseMs",out var release));Assert.Equal(9000,release,6);
        Assert.True(TryGet(compressor,"makeupDb",out var makeup));Assert.Equal(24,makeup,6);
        Assert.True(TryGet(compressor,"kneeDb",out var knee));Assert.Equal(1,knee,6);
        var gate=normalized[1];
        Assert.True(TryGet(gate,"thresholdDb",out var gateThreshold));Assert.Equal(-80,gateThreshold,6);
        Assert.True(TryGet(gate,"ratio",out var gateRatio));Assert.Equal(1,gateRatio,6);
        Assert.True(TryGet(gate,"attackMs",out var gateAttack));Assert.Equal(.1,gateAttack,6);
        Assert.True(TryGet(gate,"releaseMs",out var gateRelease));Assert.Equal(1,gateRelease,6);
        Assert.True(TryGet(gate,"rangeDb",out var gateRange));Assert.Equal(60,gateRange,6);
        // 未给参数时使用 v1 默认值。
        var defaults=EditorRenderService.NormalizeAudioEffects(new[]{new AudioEffectInstance("d","compressor")})[0];
        Assert.True(TryGet(defaults,"thresholdDb",out var defaultValue));Assert.Equal(-20,defaultValue,6);
        Assert.True(TryGet(defaults,"makeupDb",out var defaultMakeup));Assert.Equal(0,defaultMakeup,6);
    }

    [Fact]
    public void AudioEffects_AppearAfterAtempoAndBeforeEnvelopeAndFades()
    {
        var effects=EditorRenderService.AudioEffectFilters(Item("a",0,4,effects:[Effect("noiseGate"),Effect("compressor")]));
        Assert.Contains("agate=",effects);Assert.Contains("acompressor=",effects);
        Assert.True(effects.IndexOf("agate=",StringComparison.Ordinal)<effects.IndexOf("acompressor=",StringComparison.Ordinal),"效果数组顺序没有保留");
        var item=Item("a",0,4,speed:2,volumeDb:-6,keyframes:[new VolumeKeyframe(0,0),new VolumeKeyframe(4,-6)],effects:[Effect("compressor")],fadeIn:.5,fadeOut:.5);
        var chain=EditorRenderService.AudioFilterChain(item);
        var volume=chain.IndexOf("volume=0.501187",StringComparison.Ordinal);
        var atempo=chain.IndexOf("atempo=2",StringComparison.Ordinal);
        var compressor=chain.IndexOf("acompressor=",StringComparison.Ordinal);
        var envelope=chain.IndexOf("volume=volume='",StringComparison.Ordinal);
        var fadeIn=chain.IndexOf("afade=t=in",StringComparison.Ordinal);
        var fadeOut=chain.IndexOf("afade=t=out",StringComparison.Ordinal);
        Assert.True(volume>=0&&volume<atempo,"volumeDb 必须在 atempo 之前: "+chain);
        Assert.True(atempo<compressor,"效果必须在 atempo 之后（输出时间轴）: "+chain);
        Assert.True(compressor<envelope,"效果必须在包络之前: "+chain);
        Assert.True(envelope<fadeIn&&fadeIn<fadeOut,"包络/淡入淡出顺序错误: "+chain);
        // 关闭的效果不进入滤镜链。
        Assert.DoesNotContain("acompressor",EditorRenderService.AudioFilterChain(Item("a",0,4,effects:[Effect("compressor",false)])));
        Assert.Equal("",EditorRenderService.AudioEffectFilters(Item("b",0,4)));
    }

    [Fact]
    public void NoEffects_KeepLegacyFilterChainByteIdentical()
    {
        var item=Item("a",0,4,speed:2,volumeDb:-6,keyframes:[new VolumeKeyframe(0,0),new VolumeKeyframe(4,-6)],fadeIn:.5,fadeOut:.5);
        var legacy=EditorRenderService.AudioFilterChain(item);
        var withDisabled=EditorRenderService.AudioFilterChain(item with {Effects=[Effect("compressor",false),Effect("gate",false)]});
        Assert.Equal(legacy,withDisabled);
        Assert.DoesNotContain("acompressor",legacy);
        Assert.DoesNotContain("agate",legacy);
        Assert.StartsWith("volume=0.501187,atempo=2,",legacy);
        Assert.Contains("afade=t=in:st=0:d=0.5",legacy);
        Assert.Equal("",EditorRenderService.AudioFilterChain(Item("plain",0,4)));
        Assert.Equal("volume=0.501187",EditorRenderService.AudioFilterChain(Item("gain",0,4,volumeDb:-6)));
        Assert.Equal("atempo=2",EditorRenderService.AudioFilterChain(Item("speed",0,4,speed:2)));
        // 效果滤镜的精确写法（ffmpeg 参数为线性值：dB→10^(dB/20)，gate range→10^(-rangeDb/20)）。
        Assert.Equal("acompressor=threshold=0.1:ratio=8:attack=5:release=100:makeup=1:knee=1",
            EditorRenderService.AudioEffectFilters(Item("c",0,4,effects:[Effect("compressor",true,Params(("thresholdDb",-20),("ratio",8),("attackMs",5),("releaseMs",100),("makeupDb",0),("kneeDb",1)))])));
        Assert.Equal("agate=threshold=0.01:ratio=4:attack=5:release=100:range=0.031623",
            EditorRenderService.AudioEffectFilters(Item("g",0,4,effects:[Effect("noiseGate",true,Params(("thresholdDb",-40),("ratio",4),("attackMs",5),("releaseMs",100),("rangeDb",30)))])));
    }

    [Fact]
    public void AudioEffectsJson_RoundTripsAndOldProjectsStayNull()
    {
        var plain=Item("a",0,2);
        Assert.DoesNotContain("Effects",JsonSerializer.Serialize(plain));
        var legacy=JsonSerializer.Deserialize<TimelineItem>("{\"Id\":\"a\",\"ClipId\":\"c\",\"Start\":0,\"End\":2}")!;
        Assert.Null(legacy.Effects);
        var options=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
        var fromWeb=JsonSerializer.Deserialize<TimelineItem>("{\"id\":\"a\",\"clipId\":\"c\",\"start\":0,\"end\":2,\"effects\":[{\"id\":\"fx1\",\"type\":\"compressor\",\"enabled\":true,\"params\":{\"thresholdDb\":-20,\"ratio\":4}},{\"id\":\"fx2\",\"type\":\"noiseGate\",\"enabled\":false,\"params\":{\"thresholdDb\":-45}}]}",options)!;
        Assert.Equal(2,fromWeb.Effects!.Length);
        Assert.Equal("compressor",fromWeb.Effects[0].Type);
        Assert.True(TryGet(fromWeb.Effects[0],"ratio",out var ratio));Assert.Equal(4,ratio,6);
        Assert.False(fromWeb.Effects[1].Enabled);
        var roundTrip=JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(fromWeb),options)!;
        Assert.Equal(fromWeb.Effects!.Length,roundTrip.Effects!.Length);
        for(var i=0;i<fromWeb.Effects.Length;i++)
        {
            // record 里的 Dictionary 用引用比较，这里逐字段验证往返内容。
            Assert.Equal(fromWeb.Effects[i].Id,roundTrip.Effects[i].Id);
            Assert.Equal(fromWeb.Effects[i].Type,roundTrip.Effects[i].Type);
            Assert.Equal(fromWeb.Effects[i].Enabled,roundTrip.Effects[i].Enabled);
            Assert.NotNull(roundTrip.Effects[i].Params);
            foreach(var pair in fromWeb.Effects[i].Params!)Assert.Equal(pair.Value,roundTrip.Effects[i].Params![pair.Key],6);
        }
    }

    [Fact]
    public void Validate_AcceptsOutOfRangeAudioEffectsWithoutThrowing()
    {
        var plan=Plan([Item("a",0,4,effects:[
            new AudioEffectInstance("x","reverb"),
            new AudioEffectInstance("c","compressor",true,Params(("thresholdDb",double.NaN))),
            new AudioEffectInstance("g","noiseGate",true,Params(("thresholdDb",-100),("ratio",1000)))
        ])]);
        EditorRenderService.Validate(plan);
        var normalized=EditorRenderService.NormalizeAudioEffects(plan.Timeline![0]);
        Assert.Single(normalized);
        Assert.Equal("noiseGate",normalized[0].Type);
        Assert.True(TryGet(normalized[0],"thresholdDb",out var threshold));Assert.Equal(-80,threshold,6);
        Assert.True(TryGet(normalized[0],"ratio",out var ratio));Assert.Equal(20,ratio,6);
    }

    [Fact]
    public async Task Assemble_CompressorLowersLoudSectionPeaks()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，压缩器音频测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-compressor-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","3","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-af","volume=6","-c:a","aac","-b:a","128k",source],CancellationToken.None);
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            async Task<(double Duration,string Path)> Render(AudioEffectInstance[]? effects,string name)
            {
                var plan=Plan([Item("a",0,3,effects:effects)]);var work=Path.Combine(root,name);Directory.CreateDirectory(work);
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
            var compressor=Effect("compressor",true,Params(("thresholdDb",-20),("ratio",8),("attackMs",5),("releaseMs",100),("makeupDb",0),("kneeDb",1)));
            var (plainDuration,plain)=await Render(null,"plain");
            var (compressedDuration,compressed)=await Render([compressor],"compressor");
            Assert.InRange(plainDuration,2.9,3.1);Assert.InRange(compressedDuration,2.9,3.1);
            var plainLoud=await MeanVolume(plain,0.2,1.2);
            var compressedLoud=await MeanVolume(compressed,0.2,1.2);
            Assert.True(plainLoud>-12,"原始响段应接近满音量: "+plainLoud.ToString(CultureInfo.InvariantCulture));
            Assert.True(compressedLoud<plainLoud-5,"压缩器没有压低响段: plain="+plainLoud.ToString(CultureInfo.InvariantCulture)+" compressed="+compressedLoud.ToString(CultureInfo.InvariantCulture));
            Assert.True(compressedLoud<-12,"压缩后仍高于阈值附近: "+compressedLoud.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    [Fact]
    public async Task Assemble_NoiseGateQuietsLowLevelSection()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，噪声门音频测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-gate-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            // 0–1s 响、1–2s 很轻（约 -52dBFS 幅度）、2–3s 恢复响；用 volume 表达式生成动态素材。
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","3","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-af","volume='if(lt(t,1),6,if(lt(t,2),0.02,6))':eval=frame","-c:a","aac","-b:a","128k",source],CancellationToken.None);
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            async Task<(double Duration,string Path)> Render(AudioEffectInstance[]? effects,string name)
            {
                var plan=Plan([Item("a",0,3,effects:effects)]);var work=Path.Combine(root,name);Directory.CreateDirectory(work);
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
            var gate=Effect("noiseGate",true,Params(("thresholdDb",-40),("ratio",4),("attackMs",5),("releaseMs",100),("rangeDb",30)));
            var (plainDuration,plain)=await Render(null,"plain");
            var (gatedDuration,gated)=await Render([gate],"gate");
            Assert.InRange(plainDuration,2.9,3.1);Assert.InRange(gatedDuration,2.9,3.1);
            var plainQuiet=await MeanVolume(plain,1.2,0.6);
            var gatedQuiet=await MeanVolume(gated,1.2,0.6);
            Assert.True(plainQuiet>gatedQuiet,"门限没有让轻段更安静");
            Assert.True(gatedQuiet<plainQuiet-5,"门限衰减不足: plain="+plainQuiet.ToString(CultureInfo.InvariantCulture)+" gated="+gatedQuiet.ToString(CultureInfo.InvariantCulture));
            // 门限打开时响段基本不变，说明效果不是整体压音量。
            var plainLoud=await MeanVolume(plain,0.2,0.6);
            var gatedLoud=await MeanVolume(gated,0.2,0.6);
            Assert.True(Math.Abs(gatedLoud-plainLoud)<3,"响段被门限误伤: plain="+plainLoud.ToString(CultureInfo.InvariantCulture)+" gated="+gatedLoud.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    [Fact]
    public async Task Assemble_MutedTrackWithAudioEffectStaysSilent()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，静音轨叠加音频效果测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-effectmute-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","2","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-af","volume=6","-c:a","aac","-b:a","128k",source],CancellationToken.None);
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            async Task<(double Duration,string Path)> Render(int[] muted,string name)
            {
                var effect=Effect("compressor",true,Params(("thresholdDb",-20),("ratio",4)));
                var plan=Plan([Item("v",0,2,audioDetached:true),Item("a1",0,2,track:4,effects:[effect])],muted:muted);
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
            Assert.InRange(onDuration,1.9,2.1);Assert.InRange(offDuration,1.9,2.1);
            var onVolume=await MeanVolume(on);
            var offVolume=await MeanVolume(off);
            Assert.True(onVolume>-25,"带效果的 A1 轨道应可听见: "+onVolume.ToString(CultureInfo.InvariantCulture));
            Assert.True(offVolume<-60,"静音轨道不应因效果而恢复声音: "+offVolume.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
