using System.Globalization;
using System.Text.RegularExpressions;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// V1 主轨的 opacity 逐帧渲染（blend 表达式）与静态蒙版（matte PNG + maskedmerge）测试。
/// 覆盖：表达式语义、无效果时滤镜链回归、真实 ffmpeg 抽帧的亮度/黑边断言。
/// </summary>
public sealed class MaskRenderTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public MaskRenderTests(Xunit.Abstractions.ITestOutputHelper output)=>_output=output;
    [Fact]
    public void OpacityExpression_MatchesContinuousKeyframeSemantics()
    {
        // 默认 opacity=1 且无关键帧：无需合成，返回常量 "1"（调用方保持旧滤镜链）。
        Assert.Equal("1",EditorRenderService.OpacityExpression(Item("a",0,3)));
        // 静态 opacity 与轨道不透明度都作为常量因子乘进去。
        Assert.Equal("0.5",EditorRenderService.OpacityExpression(Item("a",0,3,opacity:.5)));
        Assert.Equal("0.5",EditorRenderService.OpacityExpression(Item("a",0,3),.5));
        // 关键帧表达式：按 T（输出相对秒）线性插值 + 首尾 hold，与前端 paramValueAt 一致。
        var keyed=Item("a",0,3) with {Keyframes=Kf(("opacity",[1,2],[.8,.4]))};
        var expr=EditorRenderService.OpacityExpression(keyed);
        Assert.Contains("if(lt(T,1)",expr);
        Assert.Contains("if(lt(T,2)",expr);
        Assert.Contains("0.8",expr);
        Assert.Contains("0.4",expr);
        // 轨道不透明度因子包在外层。
        var scaled=EditorRenderService.OpacityExpression(keyed,.5);
        Assert.StartsWith("0.5*(",scaled);
    }

    [Fact]
    public void MaskMatteExpression_UsesRectAndEllipseFormulas()
    {
        var rect=EditorRenderService.MaskMatteExpression(new MaskInstance("m1","rect",true,0,0,.6,.4,0,0,1),320,180);
        Assert.StartsWith("255*1*",rect);
        Assert.Contains("lt(abs(",rect);
        var ellipse=EditorRenderService.MaskMatteExpression(new MaskInstance("m1","ellipse",true,0,0,.5,.5,0,0,1),320,180);
        Assert.Contains("lte(pow(",ellipse);
        // 旋转 90° 与不旋转的表达式不同，且半透明蒙版把 255 缩放到对应比例。
        var rotated=EditorRenderService.MaskMatteExpression(new MaskInstance("m1","rect",true,0,0,.6,.4,90,0,1),320,180);
        Assert.NotEqual(rect,rotated);
        var translucent=EditorRenderService.MaskMatteExpression(new MaskInstance("m1","rect",true,0,0,.6,.4,0,0,.4),320,180);
        Assert.StartsWith("255*0.4*",translucent);
        // 多边形：winding 求和判定，无效顶点返回 0（不挡画面），半透明按比例缩放。
        var polygon=EditorRenderService.MaskMatteExpression(
            new MaskInstance("m1","polygon",true,0,0,.6,.4,0,0,1,[new(.2,.2),new(.8,.2),new(.8,.8),new(.2,.8)]),320,180);
        Assert.StartsWith("255*1*(1-",polygon);
        Assert.Contains("gt(abs(",polygon);
        Assert.Equal("0",EditorRenderService.MaskMatteExpression(
            new MaskInstance("m1","polygon",true,0,0,.6,.4,0,0,1,[new(.2,.2),new(.8,.8)]),320,180));
    }

    [Fact]
    public void VisualFilter_WithoutOpacityOrMasks_KeepsLegacyChain()
    {
        // 无关键帧/无蒙版：基础链逐字节不变，且不含已经废弃的 colorchannelmixer alpha。
        const string legacy="scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30";
        Assert.Equal(legacy,EditorRenderService.VisualFilter(Item("a",0,3),640,360,30));
        // 静态 opacity 现在由 part 级 blend 处理，不再写进 VisualFilter。
        var withOpacity=EditorRenderService.VisualFilter(Item("a",0,3,opacity:.5),640,360,30);
        Assert.Equal(legacy,withOpacity);
        Assert.DoesNotContain("colorchannelmixer",withOpacity);
        // 静态蒙版也不改变 VisualFilter（matte 在 part 级合成）。
        var withMask=EditorRenderService.VisualFilter(Item("a",0,3) with {Masks=[Mask("rect")]},640,360,30);
        Assert.Equal(legacy,withMask);
    }

    [Fact]
    public async Task Assemble_OpacityKeyframesFadeTowardsTransparent()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，不透明度关键帧测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=NewRoot("opacity");
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=await CreatePatternAsync(tools,root);
            var clip=new Clip("clip","event-mask-opacity",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            async Task<string> Render(TimelineItem item,string name)
            {
                var work=Path.Combine(root,name);Directory.CreateDirectory(work);
                var plan=new EditorProject{Start=0,End=3,Timeline=[item],Fps=30};
                var assembled=await renderer.AssembleAsync(clip,plan,work,CancellationToken.None);
                var duration=await tools.DurationAsync(assembled.Video,CancellationToken.None);
                Assert.InRange(duration,2.9,3.1);
                var probe=Path.Combine(RepoRoot(),"tmp","mask-probe");Directory.CreateDirectory(probe);
                File.Copy(assembled.Video,Path.Combine(probe,name+".mp4"),true);
                return assembled.Video;
            }
            // 帧亮度直接从渲染视频逐帧统计读取（-ss 抽单帧 PNG 在部分编码参数下会取到错误帧，
            // 因此数值断言改用 video 级 signalstats 序列；tmp/mask-probe/*.mp4 仅作为人工抽查产物）。
            const string centerCrop="crop=80:50:120:65";

            // ① 关键帧 opacity 1→0.2：逐帧亮度应递减且比例与插值值一致（t=1.5→0.6、t=2.8→约0.253）。
            var keyed=Item("a",0,3) with {Keyframes=Kf(("opacity",[0,3],[1,.2]))};
            var keyedVideo=await Render(keyed,"opacity-keyframes");
            var keyedSeries=await LumaSeries(tools,keyedVideo,centerCrop);
            var y0=LumaNearest(keyedSeries,.2);
            var y1=LumaNearest(keyedSeries,1.5);
            var y2=LumaNearest(keyedSeries,2.8);
            Assert.True(y0>y1&&y1>y2,$"关键帧 opacity 应让画面逐帧变暗，实际 {y0}/{y1}/{y2}");
            Assert.InRange(y1/y0,.5,.75);
            Assert.InRange(y2/y0,.14,.36);

            // ② 静态 opacity .5：与无 opacity 的同源渲染相比中心亮度约为一半。
            var plain=await Render(Item("a",0,3),"opacity-plain");
            var half=await Render(Item("a",0,3,opacity:.5),"opacity-static-half");
            var plainLuma=LumaNearest(await LumaSeries(tools,plain,centerCrop),1.5);
            var halfLuma=LumaNearest(await LumaSeries(tools,half,centerCrop),1.5);
            Assert.InRange(halfLuma/plainLuma,.4,.62);
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}
        }
    }

    [Fact]
    public async Task Assemble_StaticMasksBlackOutOutsideAreas()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，静态蒙版渲染测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=NewRoot("mask");
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=await CreatePatternAsync(tools,root);
            var clip=new Clip("clip","event-mask-static",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            async Task<string> Render(TimelineItem item,string name)
            {
                var work=Path.Combine(root,name);Directory.CreateDirectory(work);
                var plan=new EditorProject{Start=0,End=3,Timeline=[item],Fps=30};
                var assembled=await renderer.AssembleAsync(clip,plan,work,CancellationToken.None);
                var probe=Path.Combine(RepoRoot(),"tmp","mask-probe");Directory.CreateDirectory(probe);
                File.Copy(assembled.Video,Path.Combine(probe,name+".mp4"),true);
                return assembled.Video;
            }
            // 帧亮度直接从渲染视频逐帧统计读取（不依赖 -ss 抽单帧 PNG）。
            const string centerCrop="crop=80:50:120:65";
            string[] cornerCrops=["crop=30:30:0:0","crop=30:30:290:0","crop=30:30:0:150","crop=30:30:290:150"];
            async Task<double[]> Corners(string video)
            {
                var result=new double[cornerCrops.Length];
                for(var i=0;i<cornerCrops.Length;i++)result[i]=LumaNearest(await LumaSeries(tools,video,cornerCrops[i]),1.5);
                return result;
            }
            async Task<double> Center(string video)=>LumaNearest(await LumaSeries(tools,video,centerCrop),1.5);

            var plain=await Render(Item("a",0,3),"mask-plain");
            var plainCorners=await Corners(plain);
            var plainCenter=await Center(plain);

            // ① 矩形蒙版（居中 0.6×0.4）：四角变黑、中心保留画面。
            var rect=await Render(Item("a",0,3) with {Masks=[Mask("rect")]},"mask-rect");
            var rectCorners=await Corners(rect);
            var rectCenter=await Center(rect);
            Assert.All(rectCorners,y=>Assert.True(y<30,$"矩形蒙版外角应接近黑，实际 {y}"));
            Assert.True(rectCenter>rectCorners.Max()+60,$"矩形蒙版中心应保留画面，center={rectCenter} corners={string.Join('/',rectCorners)}");

            // ② 椭圆蒙版（0.5×0.5）：四角变黑、中心保留。
            var ellipse=await Render(Item("a",0,3) with {Masks=[Mask("ellipse")]},"mask-ellipse");
            var ellipseCorners=await Corners(ellipse);
            var ellipseCenter=await Center(ellipse);
            Assert.All(ellipseCorners,y=>Assert.True(y<30,$"椭圆蒙版外角应接近黑，实际 {y}"));
            Assert.True(ellipseCenter>ellipseCorners.Max()+50,$"椭圆蒙版中心应保留画面，center={ellipseCenter} corners={string.Join('/',ellipseCorners)}");

            // ③ 禁用蒙版：画面与无蒙版基线一致（允许编码误差）。
            var disabled=await Render(Item("a",0,3) with {Masks=[Mask("rect") with {Enabled=false}]},"mask-disabled");
            var disabledCorners=await Corners(disabled);
            for(var i=0;i<cornerCrops.Length;i++)Assert.InRange(disabledCorners[i],plainCorners[i]-8,plainCorners[i]+8);
            Assert.InRange(await Center(disabled),plainCenter-8,plainCenter+8);

            // ④ 两个蒙版取并集：左矩形覆盖整条左边、右矩形覆盖中部，四角不全黑。
            var unionLeft=Mask("rect") with {X=-.25,Y=0,W=.5,H=1};
            var unionRight=Mask("rect") with {X=.25,Y=.2,W=.5,H=.4};
            var union=await Render(Item("a",0,3) with {Masks=[unionLeft,unionRight]},"mask-union");
            var unionCorners=await Corners(union);
            Assert.True(unionCorners[0]>60||unionCorners[2]>60,$"并集蒙版应保留左侧矩形覆盖的角，实际 {string.Join('/',unionCorners)}");
            Assert.True(unionCorners[1]>60||unionCorners[3]>60,$"并集蒙版应保留右侧矩形覆盖的角，实际 {string.Join('/',unionCorners)}");
            // 并集下中心应保留画面。
            Assert.True(await Center(union)>30,$"并集蒙版中心应保留画面");

            // ⑤ 多边形蒙版：左半屏矩形多边形（归一化顶点）→ 左侧两角保留、右侧两角变黑。
            var polygonMask=new MaskInstance("mask-1","polygon",true,0,0,.6,.4,0,0,1,
            [
                new MaskPoint(0,0),new MaskPoint(.5,0),new MaskPoint(.5,1),new MaskPoint(0,1),
            ]);
            var polygon=await Render(Item("a",0,3) with {Masks=[polygonMask]},"mask-polygon");
            var polygonCorners=await Corners(polygon);
            Assert.InRange(polygonCorners[0],plainCorners[0]-10,plainCorners[0]+10);
            Assert.InRange(polygonCorners[2],plainCorners[2]-10,plainCorners[2]+10);
            Assert.True(polygonCorners[1]<30,$"多边形右侧上角应被挡黑，实际 {polygonCorners[1]}");
            Assert.True(polygonCorners[3]<30,$"多边形右侧下角应被挡黑，实际 {polygonCorners[3]}");
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}
        }
    }

    private static async Task<string> CreatePatternAsync(MediaTools tools,string root)
    {
        var source=Path.Combine(root,"source.mp4");
        await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=30","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","3","-c:v","libx264","-preset","ultrafast","-g","15","-pix_fmt","yuv420p","-c:a","aac","-b:a","128k",source],CancellationToken.None);
        Assert.True(File.Exists(source),"无法生成测试源");
        return source;
    }
    /// <summary>
    /// 对整段渲染视频逐帧统计指定区域的亮度，返回 (pts_time, YAVG) 序列。
    /// 比起用 -ss 抽单帧 PNG 再测量，整段统计不会受 seek/关键帧影响，数值与真实播放一致。
    /// </summary>
    private static async Task<IReadOnlyList<(double Time,double Luma)>> LumaSeries(MediaTools tools,string video,string crop)
    {
        var log=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","info","-i",video,"-vf",$"{crop},signalstats,metadata=print","-an","-f","null","-"],CancellationToken.None);
        var times=new List<double>();var lumas=new List<double>();
        foreach(var raw in log.Split('\n'))
        {
            var line=raw.Trim();
            var timeMatch=Regex.Match(line,@"pts_time:([0-9.]+)");
            if(timeMatch.Success){times.Add(double.Parse(timeMatch.Groups[1].Value,CultureInfo.InvariantCulture));continue;}
            var lumaMatch=Regex.Match(line,@"lavfi\.signalstats\.YAVG=([0-9.]+)");
            if(lumaMatch.Success)lumas.Add(double.Parse(lumaMatch.Groups[1].Value,CultureInfo.InvariantCulture));
        }
        Assert.True(times.Count>0&&times.Count==lumas.Count,"无法读取区域亮度序列: "+log);
        return times.Select((t,i)=>(t,lumas[i])).ToList();
    }
    private static double LumaNearest(IReadOnlyList<(double Time,double Luma)> series,double seconds)=>
        series.OrderBy(x=>Math.Abs(x.Time-seconds)).First().Luma;
    private static string NewRoot(string scenario)
    {
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-mask-"+scenario+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static TimelineItem Item(string id,double start,double end,double opacity=1)=>
        new(id,"clip",start,end,Opacity:opacity);
    private static Dictionary<string,ParamKeyframe[]> Kf(params (string Prop,double[] Times,double[] Values)[] groups)=>
        groups.ToDictionary(g=>g.Prop,g=>g.Times.Select((t,i)=>new ParamKeyframe(t,g.Values[i])).ToArray());
    private static MaskInstance Mask(string type)=>new("mask-1",type,true,0,0,.6,.4,0,0,1);
    private static string Inv(double value)=>value.ToString(CultureInfo.InvariantCulture);
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
}
