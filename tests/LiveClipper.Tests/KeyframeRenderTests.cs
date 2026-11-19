using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

/// <summary>
/// 参数关键帧（scale/rotation/positionX/positionY/opacity）与静态蒙版：
/// JSON 契约、归一化、插值、ffmpeg 按帧求值滤镜与真实渲染验证（docs/keyframe-render-status.md）。
/// </summary>
public sealed class KeyframeRenderTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public KeyframeRenderTests(Xunit.Abstractions.ITestOutputHelper output)=>_output=output;
    private static TimelineItem Item(string id,double start,double end,string clipId="clip",int track=1,double speed=1,double zoom=1,double rotation=0,double positionX=0,double positionY=0,double opacity=1)
        =>new(id,clipId,start,end,Track:track,Speed:speed,Zoom:zoom,Rotation:rotation,PositionX:positionX,PositionY:positionY,Opacity:opacity);
    private static Dictionary<string,ParamKeyframe[]> Kf(params (string Prop,double[] Times,double[] Values)[] props)
    {
        var result=new Dictionary<string,ParamKeyframe[]>(StringComparer.Ordinal);
        foreach(var (prop,times,values) in props)
            result[prop]=times.Select((t,i)=>new ParamKeyframe(t,values[i])).ToArray();
        return result;
    }
    private static EditorProject Plan(TimelineItem[] timeline,int fps=25)
        =>new(){Start=0,End=3,Timeline=timeline,Fps=fps};

    [Fact]
    public void KeyframesJson_RoundTripsAndOmitsNullFieldsOnLegacyProjects()
    {
        var plain=JsonSerializer.Serialize(new TimelineItem("a","clip",0,3));
        Assert.DoesNotContain("Keyframes",plain);Assert.DoesNotContain("Masks",plain);

        var item=Item("a",0,3) with
        {
            Keyframes=Kf(("scale",[0,3],[1,2])),
            Masks=[new MaskInstance("m","rect",true,0,0,.5,.5,0,10,.8)],
        };
        var json=JsonSerializer.Serialize(item);
        Assert.Contains("\"Keyframes\"",json);Assert.Contains("\"Masks\"",json);
        var back=JsonSerializer.Deserialize<TimelineItem>(json)!;
        Assert.NotNull(back.Keyframes);Assert.NotNull(back.Masks);
        Assert.Equal(2,back.Keyframes!["scale"].Length);
        Assert.Equal(1d,back.Keyframes["scale"][0].Value,6);Assert.Equal(2d,back.Keyframes["scale"][1].Value,6);
        Assert.Single(back.Masks!);Assert.Equal("rect",back.Masks![0].Type);Assert.Equal(.8,back.Masks[0].Opacity,6);

        // web 端提交的是 camelCase（keyframes/time/value、masks），读取时按大小写不敏感绑定。
        const string webJson="""{"Id":"a","ClipId":"clip","Start":0,"End":3,"keyframes":{"scale":[{"time":0,"value":1},{"time":3,"value":2}]},"masks":[{"id":"m","type":"ellipse","enabled":true,"x":0,"y":0,"w":0.5,"h":0.5,"rotation":0,"feather":10,"opacity":0.8}]}""";
        var fromWeb=JsonSerializer.Deserialize<TimelineItem>(webJson,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        Assert.NotNull(fromWeb.Keyframes);Assert.Equal(2,fromWeb.Keyframes!["scale"].Length);
        Assert.Equal(2d,fromWeb.Keyframes["scale"][1].Value,6);
        Assert.Equal("ellipse",fromWeb.Masks![0].Type);
    }

    [Fact]
    public void NormalizeParamKeyframes_SortsClampsDedupesAndCapsAt64()
    {
        var points=new List<ParamKeyframe>{new(2,.5),new(0,.2),new(1,2),new(1.01,1.4)};
        var normalized=EditorRenderService.NormalizeParamKeyframes("rotation",points,4);
        Assert.Equal(new[]{0d,1.01,2d},normalized.Select(x=>x.Time));
        Assert.Equal(new[]{.2,1.4,.5},normalized.Select(x=>x.Value));

        var clamped=EditorRenderService.NormalizeParamKeyframes("scale",[new(0,99),new(1,-99)],4);
        Assert.Equal(new[]{3d,.5},clamped.Select(x=>x.Value));
        var timeClamped=EditorRenderService.NormalizeParamKeyframes("rotation",[new(9,1),new(-2,.5)],4);
        Assert.Equal(new[]{0d,4d},timeClamped.Select(x=>x.Time));
        Assert.Equal(new[]{.5,1d},timeClamped.Select(x=>x.Value));

        Assert.Empty(EditorRenderService.NormalizeParamKeyframes("scale",[new(double.NaN,1),new(0,double.PositiveInfinity)],4));
        var many=Enumerable.Range(0,80).Select(i=>new ParamKeyframe(i*.05,1)).ToArray();
        Assert.Equal(64,EditorRenderService.NormalizeParamKeyframes("opacity",many,10).Length);
        Assert.Empty(EditorRenderService.NormalizeParamKeyframes("unknown",[new(0,1)],10));
        Assert.DoesNotContain("unknown",EditorRenderService.ParamKeyframeProperties);
    }

    [Fact]
    public void NormalizeKeyframes_ClampsTimeToSpeedAdjustedOutputDuration()
    {
        // speed=2 的 8 秒片段输出只有 4 秒，落在输出时长之外的关键帧应被 clamp/丢弃。
        var item=Item("a",0,8,speed:2) with { Keyframes=Kf(("scale",[3.9,4.5,9],[1,2,3])) };
        var normalized=EditorRenderService.NormalizeKeyframes(item)!;
        Assert.Equal(new[]{3.9,4d},normalized["scale"].Select(x=>x.Time));
        Assert.Equal(new[]{1d,3d},normalized["scale"].Select(x=>x.Value));
        Assert.Equal(4d,EditorRenderService.ItemOutputDuration(item),6);

        var sorted=EditorRenderService.NormalizeParamKeyframes("rotation",[new(2,10),new(0,-10)],4);
        Assert.Equal(new[]{0d,2d},sorted.Select(x=>x.Time));
        Assert.Equal(new[]{-10d,10d},sorted.Select(x=>x.Value));
    }

    [Fact]
    public void ParamValueAt_InterpolatesLinearlyAndHoldsEndpointValues()
    {
        ParamKeyframe[] points=[new(1,0),new(3,12)];
        Assert.Equal(0d,EditorRenderService.ParamValueAt(points,0,999),6);
        Assert.Equal(6d,EditorRenderService.ParamValueAt(points,2,999),6);
        Assert.Equal(12d,EditorRenderService.ParamValueAt(points,5,999),6);
        Assert.Equal(999d,EditorRenderService.ParamValueAt([],1,999),6);

        var item=Item("a",0,4) with { Keyframes=Kf(("opacity",[0,2,4],[0,1,0]),("positionX",[0,4],[-.5,.5])) };
        Assert.Equal(.25,EditorRenderService.ParamValueAtItem(item,"opacity",.5),6);
        Assert.Equal(1d,EditorRenderService.ParamValueAtItem(item,"opacity",2),6);
        Assert.Equal(0d,EditorRenderService.ParamValueAtItem(item,"positionX",2),6);
    }

    [Fact]
    public void VisualFilter_WithoutKeyframes_KeepsLegacyChainByteIdentical()
    {
        var item=Item("a",0,3);
        Assert.Equal("scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=25",EditorRenderService.VisualFilter(item,640,360,25));
        Assert.Contains("crop=iw/2:ih/2,scale=640:360",EditorRenderService.VisualFilter(Item("z",0,3,zoom:2),640,360,25));
        // 非主轨上的关键帧不参与 V1 渲染：滤镜链必须与无关键帧完全一致。
        var track2=Item("b",0,3,track:2) with { Keyframes=Kf(("scale",[0,3],[1,2]),("positionX",[0,3],[-.5,.5])) };
        var plainTrack2=Item("b",0,3,track:2);
        Assert.Equal(EditorRenderService.VisualFilter(plainTrack2,640,360,25),EditorRenderService.VisualFilter(track2,640,360,25));
    }

    [Fact]
    public void ParamKeyframeExpression_BuildsSegmentedLinearExpressionWithFrameAndSpeedFactor()
    {
        var item=Item("a",0,3) with { Keyframes=Kf(("scale",[0,3],[1,2])) };
        var expression=EditorRenderService.ParamKeyframeExpression(item,"scale","on",25);
        Assert.StartsWith("if(lt((on/25),0),1,",expression);
        Assert.Contains("if(lt((on/25),3),(1+(1)*((on/25)-0)/3),2)",expression);
        Assert.EndsWith(",2))",expression);

        var fast=Item("a",0,3,speed:2) with { Keyframes=Kf(("positionX",[0,2],[-.3,.3])) };
        var fastExpression=EditorRenderService.ParamKeyframeExpression(fast,"positionX","n",25,2);
        Assert.Contains("(n/50)",fastExpression);
        Assert.Contains("-0.3",fastExpression);

        var unknown=Assert.Throws<ArgumentException>(()=>EditorRenderService.ParamKeyframeExpression(Item("fx",0,3),"blur","n",25));
        Assert.Contains("fx",unknown.Message);Assert.Contains("blur",unknown.Message);
        var empty=Assert.Throws<ArgumentException>(()=>EditorRenderService.ParamKeyframeExpression(Item("empty",0,3),"scale","on",25));
        Assert.Contains("empty",empty.Message);Assert.Contains("scale",empty.Message);
    }

    [Fact]
    public void VisualFilter_WithTransformKeyframes_UsesPerFrameExpressions()
    {
        var item=Item("a",0,3) with
        {
            Keyframes=Kf(("scale",[0,3],[1,2]),("rotation",[0,3],[0,90]),("positionX",[0,3],[-.3,.3]),("opacity",[0,3],[.5,.5])),
        };
        var filter=EditorRenderService.VisualFilter(item,640,360,25);
        Assert.Contains("zoompan=z='if(lt((on/25),0),1,",filter);
        Assert.Contains("pad=1280:720:320:180",filter);
        Assert.Contains("crop=640:360:x='320-(if(lt((n/25),0),-0.3,",filter);
        Assert.Contains(")*360'",filter);
        Assert.Contains("rotate='(if(lt((n/25),0),0,",filter);
        Assert.Contains("*PI/180':ow=iw:oh=ih:c=black",filter);
        // opacity 关键帧改由 part 级 blend 表达式合成，VisualFilter 不再输出 colorchannelmixer。
        Assert.DoesNotContain("colorchannelmixer",filter);
        Assert.Contains("if(lt((n/25),0),0,",filter);
    }

    [Fact]
    public void NormalizeMasks_DefaultsClampsAndCaps()
    {
        var masks=new[]
        {
            new MaskInstance("","Tri",true,5,0,.001,9,999,-1,2),
            new MaskInstance("m2","ellipse",false,.2,.1,.3,.4,45,20,.5),
            new MaskInstance("m3","rect",true,0,0,.5,.5,0,0,1),
            new MaskInstance("m4","rect",true,0,0,.5,.5,0,0,1),
            new MaskInstance("m5","rect",true,0,0,.5,.5,0,0,1),
            new MaskInstance("m6","rect",true,0,0,.5,.5,0,0,1),
        };
        var normalized=EditorRenderService.NormalizeMasks(Item("a",0,3) with { Masks=masks })!;
        Assert.Equal(4,normalized.Length);
        Assert.Equal("mask-1",normalized[0].Id);Assert.Equal("rect",normalized[0].Type);
        Assert.Equal(.5,normalized[0].X,6);Assert.Equal(.01,normalized[0].W,6);
        Assert.Equal(2,normalized[0].H,6);Assert.Equal(180,normalized[0].Rotation,6);
        Assert.Equal(0,normalized[0].Feather,6);Assert.Equal(1,normalized[0].Opacity,6);
        Assert.Equal("ellipse",normalized[1].Type);Assert.False(normalized[1].Enabled);
        Assert.Null(EditorRenderService.NormalizeMasks(Item("a",0,3)));
        // 多边形：顶点 clamp 到 0..1、去连续重复点、最多 24 个；不足 3 个有效顶点整条丢弃。
        var polygon=new[]
        {
            new MaskInstance("p1","polygon",true,0,0,.5,.5,0,0,1,[new(-.5,.3),new(.6,.3),new(.6,1.4),new(.6,1.4)]),
            new MaskInstance("p2","polygon",true,0,0,.5,.5,0,0,1,[new(.1,.1),new(.2,.2)]),
        };
        var polygonNormalized=EditorRenderService.NormalizeMasks(Item("a",0,3) with { Masks=polygon })!;
        Assert.Single(polygonNormalized);
        Assert.Equal("polygon",polygonNormalized[0].Type);
        var polygonPoints=polygonNormalized[0].Points!;
        Assert.Equal(3,polygonPoints.Length);
        Assert.Equal(0,polygonPoints[0].X,6);Assert.Equal(1,polygonPoints[2].Y,6);
        Assert.Null(EditorRenderService.NormalizeMaskPoints([new(.1,.1),new(.2,.2)]));
        var many=Enumerable.Range(0,40).Select(i=>new MaskPoint(i/40.0,i/40.0)).ToArray();
        Assert.Equal(24,EditorRenderService.NormalizeMaskPoints(many)!.Length);
    }

    [Fact]
    public void Validate_AcceptsOutOfRangeKeyframesAndMasksWithoutThrowing()
    {
        var item=Item("a",0,3) with
        {
            Keyframes=new Dictionary<string,ParamKeyframe[]>
            {
                ["scale"]=[new(double.NaN,1),new(0,99)],
                ["unknown"]=[new(0,1)],
            },
            Masks=[new MaskInstance("","",true,0,0,.5,.5,0,0,1)],
        };
        EditorRenderService.Validate(Plan([item]));
        var normalized=EditorRenderService.NormalizeKeyframes(item)!;
        Assert.Single(normalized);Assert.Equal(3d,normalized["scale"][0].Value,6);
        Assert.DoesNotContain("unknown",normalized.Keys);
    }

    [Fact]
    public async Task Assemble_AnimatedKeyframesRenderExpectedDurationFramesAndDirection()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，关键帧动画渲染测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-keyframes-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var media=ResolveTestMedia(root,tools);
            var clip=new Clip("clip","event",media,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            // tmp/editor-test.mp4 是纯色灰帧（YMIN=YMAX=54、无纹理），适合验证时长/帧数与位置黑边，
            // 但无法区分缩放差异；缩放方向另用同规格 testsrc2 图案源验证。
            var pattern=Path.Combine(root,"pattern.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=640x360:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","8","-c:v","libx264","-preset","ultrafast","-g","25","-pix_fmt","yuv420p","-c:a","aac","-b:a","128k",pattern],CancellationToken.None);
            // clips 表按 event_id 去重（ON CONFLICT(event_id)），图案源必须用独立 event id，否则会覆盖 clip 的 path。
            var patternClip=new Clip("pattern","event-pattern",pattern,null,DateTimeOffset.UtcNow);store.SaveClip(patternClip);
            var framesDir=Path.Combine(RepoRoot(),"tmp","keyframe-frames");Directory.CreateDirectory(framesDir);
            var fps=25;

            async Task<(string Path,double Duration)> Render(string clipId,TimelineItem[] items,string name)
            {
                var plan=Plan(items,fps);var work=Path.Combine(root,name);Directory.CreateDirectory(work);
                var target=clipId=="pattern"?patternClip:clip;
                var assembled=await renderer.AssembleAsync(target,plan,work,CancellationToken.None);
                Assert.True(File.Exists(assembled.Video));
                // 保留渲染产物便于人工抽查（tmp/ 目录可随时清理）。
                var probeDir=Path.Combine(RepoRoot(),"tmp","keyframe-probe");Directory.CreateDirectory(probeDir);
                File.Copy(assembled.Video,Path.Combine(probeDir,name+".mp4"),true);
                File.WriteAllText(Path.Combine(probeDir,name+".filter.txt"),EditorRenderService.VisualFilter(items[0],640,360,25));
                File.WriteAllText(Path.Combine(probeDir,name+".source.txt"),$"{target.Id}|{target.Path}|{items[0].ClipId}");
                return (assembled.Video,await tools.DurationAsync(assembled.Video,CancellationToken.None));
            }
            async Task<string> Frame(string video,double seconds,string name)
            {
                var path=Path.Combine(framesDir,name+".png");
                await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-ss",Invariant(seconds),"-i",video,"-frames:v","1",path],CancellationToken.None);
                Assert.True(File.Exists(path),"抽帧失败: "+path);
                return path;
            }
            async Task<double> RegionLuma(string image,string crop)
            {
                var log=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","info","-i",image,"-vf",$"{crop},signalstats,metadata=print","-frames:v","1","-an","-f","null","-"],CancellationToken.None);
                var match=Regex.Match(log,@"lavfi\.signalstats\.YAVG=([0-9.]+)");
                Assert.True(match.Success,"无法读取区域亮度: "+log);
                return double.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
            }
            async Task<double> Ssim(string a,string b)
            {
                var log=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","info","-i",a,"-i",b,"-lavfi","ssim","-f","null","-"],CancellationToken.None);
                var match=Regex.Match(log,@"All:([0-9.]+)");
                Assert.True(match.Success,"无法读取 SSIM: "+log);
                return double.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
            }
            async Task<double> FrameCount(string video)
            {
                var log=await tools.RunAsync(tools.Ffprobe,["-v","error","-count_frames","-select_streams","v:0","-show_entries","stream=nb_read_frames","-of","default=noprint_wrappers=1:nokey=1",video],CancellationToken.None);
                var last=log.Split('\n',StringSplitOptions.RemoveEmptyEntries).Last().Trim();
                return double.Parse(last,System.Globalization.CultureInfo.InvariantCulture);
            }

            // ① scale 1→2 + positionX -0.3→0.3 的 3 秒工程：时长/帧数符合预期，左右黑边方向随关键帧翻转。
            var animated=Item("a",0,3) with
            {
                Keyframes=Kf(("scale",[0,3],[1,2]),("positionX",[0,3],[-.3,.3])),
            };
            var (animatedPath,animatedDuration)=await Render("clip",[animated],"animated");
            Assert.InRange(animatedDuration,2.9,3.1);
            Assert.InRange(await FrameCount(animatedPath),73,77);

            var early=await Frame(animatedPath,.1,"keyframe-combined-0.1");
            var middle=await Frame(animatedPath,1.5,"keyframe-combined-1.5");
            var late=await Frame(animatedPath,2.9,"keyframe-combined-2.9");
            const string leftCrop="crop=iw*0.18:ih:0:0";
            const string rightCrop="crop=iw*0.18:ih:iw*0.82:0";
            var earlyLeft=await RegionLuma(early,leftCrop);var earlyRight=await RegionLuma(early,rightCrop);
            var middleLeft=await RegionLuma(middle,leftCrop);var middleRight=await RegionLuma(middle,rightCrop);
            var lateLeft=await RegionLuma(late,leftCrop);var lateRight=await RegionLuma(late,rightCrop);
            Assert.True(earlyLeft>earlyRight+20,$"起始位置应偏左（画面在左、黑边在右），实际 left={earlyLeft} right={earlyRight}");
            Assert.True(lateRight>lateLeft+20,$"结束位置应偏右（画面在右、黑边在左），实际 left={lateLeft} right={lateRight}");
            Assert.True(Math.Abs(middleLeft-middleRight)<12,$"中点两侧应基本对称，实际 left={middleLeft} right={middleRight}");

            // ② scale 方向（图案源）：scale 1→2 的末帧应与静态 Zoom=2 一致、首帧应与静态 Zoom=1 一致（SSIM 关系断言）。
            var zoomItem=Item("z",0,3,clipId:"pattern") with { Keyframes=Kf(("scale",[0,3],[1,2])) };
            var (zoomPath,_)=await Render("pattern",[zoomItem],"scale-animated");
            var (onePath,_)=await Render("pattern",[Item("o",0,3,clipId:"pattern",zoom:1)],"zoom-1");
            var (twoPath,_)=await Render("pattern",[Item("t",0,3,clipId:"pattern",zoom:2)],"zoom-2");
            var s0=await Frame(zoomPath,.1,"keyframe-scale-0.1");
            var s2=await Frame(zoomPath,2.9,"keyframe-scale-2.9");
            var one0=await Frame(onePath,.1,"keyframe-zoom1-0.1");
            var one2=await Frame(onePath,2.9,"keyframe-zoom1-2.9");
            var two0=await Frame(twoPath,.1,"keyframe-zoom2-0.1");
            var two2=await Frame(twoPath,2.9,"keyframe-zoom2-2.9");
            var earlyVsOne=await Ssim(s0,one0);var earlyVsTwo=await Ssim(s0,two0);
            var lateVsTwo=await Ssim(s2,two2);var lateVsOne=await Ssim(s2,one2);
            Assert.True(earlyVsOne>earlyVsTwo,"scale 起始帧应更接近 Zoom=1 的参考帧");
            Assert.True(lateVsTwo>lateVsOne,"scale 结束帧应更接近 Zoom=2 的参考帧");
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}
        }
    }

    private static string Invariant(double value)=>value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string TestFfmpeg()
    {
        var fromEnv=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(!string.IsNullOrWhiteSpace(fromEnv)&&File.Exists(fromEnv))return fromEnv;
        var bundled=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","tools","ffmpeg","bin","ffmpeg.exe");
        return File.Exists(bundled)?bundled:"ffmpeg";
    }
    private static string RepoRoot()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null&&!File.Exists(Path.Combine(dir.FullName,"LiveClipper.sln")))dir=dir.Parent;
        return dir?.FullName??Directory.GetCurrentDirectory();
    }
    private static string ResolveTestMedia(string root,MediaTools tools)
    {
        var media=Path.Combine(RepoRoot(),"tmp","editor-test.mp4");
        if(File.Exists(media))return media;
        // 兜底：现场生成 3 秒参考素材，保证测试在干净工作区也能跑。
        // 必须与 tmp/editor-test.mp4 同规格的「纯色灰帧」（YAVG≈54、无纹理）：本用例第 277 行
        // 的中点对称断言以「画面内容在水平方向均匀」为前提；用 testsrc2 等带纹理图案会在中点
        // 产生左右亮度差（136 vs 110）而误报失败。缩放方向另用测试内自建的 testsrc2 图案源验证。
        var generated=Path.Combine(root,"source.mp4");
        tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","color=c=0x363636:size=640x360:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","3","-c:v","libx264","-preset","ultrafast","-g","25","-pix_fmt","yuv420p","-c:a","aac","-b:a","128k",generated],CancellationToken.None).GetAwaiter().GetResult();
        return generated;
    }
}
