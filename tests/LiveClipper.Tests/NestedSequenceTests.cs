using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

/// <summary>序列嵌套 v1：模型 JSON、flatten 顺序/时长/轨道映射、深度与环检测、真实 ffmpeg 渲染（docs/filmcraft-parity-next5.md §4）。</summary>
public sealed class NestedSequenceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public NestedSequenceTests(Xunit.Abstractions.ITestOutputHelper output)=>_output=output;
    private static TimelineItem Item(string id,double start,double end,string clipId="source",int track=1,double gap=0,bool disabled=false,string? nested=null,AudioEffectInstance[]? effects=null,VolumeKeyframe[]? keyframes=null)
        =>new(id,nested is null?clipId:"",start,end,Track:track,GapBefore:gap,Disabled:disabled,NestedSequenceId:nested,Effects:effects,VolumeKeyframes:keyframes);
    private static NestedSequence Seq(string id,TimelineItem[] items,EditorTransition[]? transitions=null)
        =>new(id,id,items,transitions);
    private static EditorProject Plan(TimelineItem[] timeline,EditorTransition[]? transitions=null,Dictionary<string,NestedSequence>? nested=null)
        =>new(){Start=0,End=10,Timeline=timeline,Transitions=transitions,NestedSequences=nested,Fps=30};
    private static Dictionary<string,NestedSequence> Chain(int levels)
    {
        var nested=new Dictionary<string,NestedSequence>();
        for(var i=levels;i>=1;i--)
        {
            TimelineItem[] items=i==levels?[Item("clip",0,1,"clip")]:[Item("child",0,0,nested:$"seq-{i+1}")];
            nested[$"seq-{i}"]=Seq($"seq-{i}",items);
        }
        return nested;
    }

    [Fact]
    public void NestedSequenceModel_RoundTripsJsonAndKeepsLegacyProjectsUntouched()
    {
        var legacy=JsonSerializer.Deserialize<EditorProject>("{}")!;
        Assert.Null(legacy.NestedSequences);
        Assert.DoesNotContain("NestedSequences",JsonSerializer.Serialize(Plan([Item("a",0,4)])));
        Assert.DoesNotContain("NestedSequenceId",JsonSerializer.Serialize(Plan([Item("a",0,4)])));
        var nested=Seq("seq-a",[Item("x",0,3,"clip")]);
        var plan=Plan([Item("n1",0,0,nested:"seq-a")],[],new(){["seq-a"]=nested});
        var json=JsonSerializer.Serialize(plan);
        Assert.Contains("NestedSequences",json);
        Assert.Contains("NestedSequenceId",json);
        Assert.Contains("seq-a",json);
        var restored=JsonSerializer.Deserialize<EditorProject>(json)!;
        Assert.Equal("seq-a",Assert.Single(restored.Timeline!).NestedSequenceId);
        var restoredNested=Assert.Single(restored.NestedSequences!).Value;
        Assert.Equal("seq-a",restoredNested.Id);
        Assert.Equal(3,Assert.Single(restoredNested.Timeline).End,6);
        // 嵌套片段允许 ClipId 留空、Start/End 只是占位：保存（Validate）不应因它们抛错。
        EditorRenderService.Validate(plan);
    }

    [Fact]
    public void Flatten_ExpandsNestedItemsInPlaceAndAddsTheirDurations()
    {
        var nested=new Dictionary<string,NestedSequence>{["seq-a"]=Seq("seq-a",[Item("x",0,3,"clip"),Item("y",3,5,"clip")])};
        var plan=Plan([Item("a",0,2,"clip"),Item("n1",0,0,nested:"seq-a",gap:1.5),Item("c",2,4,"clip")],[],nested);
        Assert.True(EditorRenderService.HasNestedSequences(plan));
        var flat=EditorRenderService.FlattenProject(plan);
        Assert.False(EditorRenderService.HasNestedSequences(flat));
        Assert.Null(flat.NestedSequences);
        Assert.Equal(new[]{"a","n1:x","n1:y","c"},flat.Timeline!.Select(x=>x.Id).ToArray());
        // 嵌套片段的 GapBefore 只落到子序列映射到各轨道的第一个片段上。
        Assert.Equal(1.5,flat.Timeline![1].GapBefore,6);
        Assert.Equal(0,flat.Timeline![2].GapBefore,6);
        // a(2s)+gap(1.5)+x(3s)+y(2s)+c(2s)=10.5s
        Assert.Equal(10.5,EditorRenderService.TimelineDuration(flat.Timeline),6);
        // 没有嵌套的工程原样返回（引用相等），保证无嵌套路径行为不变。
        var plain=Plan([Item("a",0,4)]);
        Assert.Same(plain,EditorRenderService.FlattenProject(plain));
    }

    [Fact]
    public void Flatten_MapsChildTracksRelativeToTheNestedItemTrack()
    {
        var nested=new Dictionary<string,NestedSequence>{["seq-a"]=Seq("seq-a",[Item("v1",0,2,"clip",track:1),Item("v2",0,1,"clip",track:2),Item("a1",0,2,"clip",track:4)])};
        var flat=EditorRenderService.FlattenProject(Plan([Item("n1",0,0,nested:"seq-a",track:2)],[],nested));
        Assert.Equal(new[]{2,3,4},flat.Timeline!.Select(x=>x.Track).ToArray());
        // 子序列 V1→父 V2、V2→父 V3、A1→A1；轨道时长取最长轨（2s），而不是把叠层相加成 5s。
        Assert.Equal(2,EditorRenderService.TimelineDuration(flat.Timeline!),6);
        Assert.Equal(2,EditorRenderService.TimelineDuration(flat.Timeline!.Where(x=>x.Track==2)),6);
        Assert.Equal(1,EditorRenderService.TimelineDuration(flat.Timeline!.Where(x=>x.Track==3)),6);
        Assert.Equal(2,EditorRenderService.TimelineDuration(flat.Timeline!.Where(x=>x.Track==4)),6);
    }

    [Fact]
    public void Flatten_AllowsFourNestedLevelsAndRejectsFive()
    {
        var allowed=EditorRenderService.FlattenProject(Plan([Item("root",0,0,nested:"seq-1")],[],Chain(4)));
        Assert.Equal("root:child:child:child:clip",Assert.Single(allowed.Timeline!).Id);
        var tooDeep=Assert.Throws<ArgumentException>(()=>EditorRenderService.FlattenProject(Plan([Item("root",0,0,nested:"seq-1")],[],Chain(5))));
        Assert.Contains("嵌套层级不能超过 4 层",tooDeep.Message);
    }

    [Fact]
    public void Flatten_RejectsCyclesEmptyAndMissingReferences()
    {
        var cycle=new Dictionary<string,NestedSequence>
        {
            ["a"]=Seq("a",[Item("to-b",0,0,nested:"b")]),
            ["b"]=Seq("b",[Item("to-a",0,0,nested:"a")])
        };
        var cycleError=Assert.Throws<ArgumentException>(()=>EditorRenderService.FlattenProject(Plan([Item("root",0,0,nested:"a")],[],cycle)));
        Assert.Contains("嵌套序列存在循环引用",cycleError.Message);
        Assert.Contains("a → b → a",cycleError.Message);
        var empty=Assert.Throws<ArgumentException>(()=>EditorRenderService.FlattenProject(Plan([Item("root",0,0,nested:"empty")],[],new(){["empty"]=Seq("empty",[])})));
        Assert.Contains("子序列为空",empty.Message);
        var missing=Assert.Throws<ArgumentException>(()=>EditorRenderService.FlattenProject(Plan([Item("root",0,0,nested:"missing")],[],new(){["other"]=Seq("other",[Item("x",0,1,"clip")])})));
        Assert.Contains("嵌套序列引用不存在：missing",missing.Message);
    }

    [Fact]
    public void Flatten_RejectsTransitionsAcrossNestedBoundaries()
    {
        var nested=new Dictionary<string,NestedSequence>{["seq-a"]=Seq("seq-a",[Item("x",0,2,"clip")])};
        // 转场直接挂在嵌套片段入点上。
        var onNested=Assert.Throws<ArgumentException>(()=>EditorRenderService.FlattenProject(
            Plan([Item("n1",0,0,nested:"seq-a"),Item("n2",0,0,nested:"seq-a")],[new EditorTransition("t","n1",1,"cross-dissolve",.5,"center")],nested)));
        Assert.Contains("嵌套序列暂不支持跨嵌套转场",onNested.Message);
        // 转场挂在紧邻嵌套片段之后的片段上（上一片是嵌套片段）。
        var afterNested=Assert.Throws<ArgumentException>(()=>EditorRenderService.FlattenProject(
            Plan([Item("n1",0,0,nested:"seq-a"),Item("b",0,4,"clip")],[new EditorTransition("t","b",1,"cross-dissolve",.5,"center")],nested)));
        Assert.Contains("嵌套序列暂不支持跨嵌套转场",afterNested.Message);
    }

    [Fact]
    public void Flatten_KeepsChildEffectsAndTransitionsForMultipleInstances()
    {
        var childEffects=new[]{new AudioEffectInstance("fx-comp","compressor",true,new Dictionary<string,double>{["thresholdDb"]=-12})};
        var childKeyframes=new[]{new VolumeKeyframe(0,0),new VolumeKeyframe(1,-18)};
        var nested=new Dictionary<string,NestedSequence>
        {
            ["seq"]=Seq("seq",[Item("x",0,3,"clip",effects:childEffects,keyframes:childKeyframes),Item("y",3,6,"clip")],
                [new EditorTransition("t-y","y",1,"cross-dissolve",.5,"center")])
        };
        var plan=Plan([Item("n1",0,0,nested:"seq"),Item("n2",0,0,nested:"seq")],[],nested);
        var flat=EditorRenderService.FlattenProject(plan);
        Assert.Equal(new[]{"n1:x","n1:y","n2:x","n2:y"},flat.Timeline!.Select(x=>x.Id).ToArray());
        var first=flat.Timeline![0];
        Assert.Equal(-12,first.Effects![0].Params!["thresholdDb"],6);
        Assert.Equal(new[]{0d,1d},first.VolumeKeyframes!.Select(x=>x.Time).ToArray());
        // 子序列内部转场被复制到两个实例，并改名为带实例前缀的 id。
        Assert.Equal(new[]{"n1:t-y","n2:t-y"},flat.Transitions!.Select(x=>x.Id).OrderBy(x=>x).ToArray());
        Assert.Equal(new[]{"n1:y","n2:y"},flat.Transitions!.Select(x=>x.ItemId).OrderBy(x=>x).ToArray());
        var cuts=EditorRenderService.ResolveTransitions(flat);
        Assert.Equal(2,cuts.Length);
        // 每个实例：3+3−0.5=5.5s，两份实例总时长 11s。
        Assert.Equal(11,EditorRenderService.SequenceDuration(flat),6);
    }

    private static string TestFfmpeg()
    {
        var fromEnv=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(!string.IsNullOrWhiteSpace(fromEnv)&&File.Exists(fromEnv))return fromEnv;
        var bundled=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","tools","ffmpeg","bin","ffmpeg.exe");
        return File.Exists(bundled)?bundled:"ffmpeg";
    }

    [Fact]
    public async Task Assemble_NestedSequenceRendersFlattenedDuration()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，嵌套序列渲染测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-nested-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=640x360:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","8","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-b:a","128k",source],CancellationToken.None);
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            async Task<(double Duration,string Path)> Render(EditorProject plan,string name)
            {
                var work=Path.Combine(root,name);Directory.CreateDirectory(work);
                var assembled=await renderer.AssembleAsync(clip,plan,work,CancellationToken.None);
                Assert.True(File.Exists(assembled.Video));
                return (await tools.DurationAsync(assembled.Video,CancellationToken.None),assembled.Video);
            }
            // 子序列 5s（3+2），主时间线再跟一个 2s 片段 → 7s；嵌套片段自身的占位 Start/End 不参与时长。
            var nested=new Dictionary<string,NestedSequence>{["seq"]=Seq("seq",[Item("x",0,3,"clip"),Item("y",3,5,"clip")])};
            var plain=Plan([Item("n1",0,0,nested:"seq"),Item("b",0,2,"clip")],[],nested);
            var flat=EditorRenderService.FlattenProject(plain);
            Assert.Equal(7,EditorRenderService.TimelineDuration(flat.Timeline!),6);
            var (duration,_)=await Render(plain,"nested-basic");
            Assert.InRange(duration,6.9,7.1);
            // 子序列内部 0.5s 交叉淡化：5−0.5=4.5s，剪点附近不应出现黑场。
            var nestedWithTransition=new Dictionary<string,NestedSequence>
            {
                ["seq"]=Seq("seq",[Item("x",0,3,"clip"),Item("y",3,5,"clip")],[new EditorTransition("t-y","y",1,"cross-dissolve",.5,"center")])
            };
            var transitionPlan=Plan([Item("n1",0,0,nested:"seq")],[],nestedWithTransition);
            var (transitionDuration,transitionPath)=await Render(transitionPlan,"nested-transition");
            Assert.InRange(transitionDuration,4.4,4.6);
            var lumaLog=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","info","-ss","2.75","-i",transitionPath,"-vf","signalstats,metadata=print","-frames:v","1","-an","-f","null","-"],CancellationToken.None);
            var luma=System.Text.RegularExpressions.Regex.Match(lumaLog,@"lavfi\.signalstats\.YAVG=([0-9.]+)");
            Assert.True(luma.Success,"无法从 ffmpeg 读取帧亮度: "+lumaLog);
            Assert.True(double.Parse(luma.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)>16,"嵌套序列内部转场出现黑场");
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}
        }
    }
}
