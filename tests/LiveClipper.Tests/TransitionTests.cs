using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

/// <summary>剪辑点转场：数据模型、合法性过滤、时间换算与 ffmpeg 渲染验收（docs/transitions-spec.md §6）。</summary>
public sealed class TransitionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public TransitionTests(Xunit.Abstractions.ITestOutputHelper output)=>_output=output;
    private static TimelineItem Item(string id,double start,double end,string clipId="source",int track=1,double gap=0,double speed=1,bool disabled=false,double fadeIn=0,double fadeOut=0)
        =>new(id,clipId,start,end,Track:track,GapBefore:gap,Speed:speed,Disabled:disabled,FadeIn:fadeIn,FadeOut:fadeOut);
    private static EditorTransition Transition(string itemId,string kind="cross-dissolve",double duration=.5,string align="center",int track=1,string? id=null)
        =>new(id??$"transition-{itemId}",itemId,track,kind,duration,align);
    private static EditorProject Plan(TimelineItem[] timeline,EditorTransition[]? transitions=null,int fps=30)
        =>new(){Start=0,End=8,Timeline=timeline,Transitions=transitions,Fps=fps};

    [Fact]
    public void TwoBaseClipsWithHalfSecondTransition_CompressSequenceTime()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8)};var plan=Plan(items,[Transition("b")]);
        var cuts=EditorRenderService.ResolveTransitions(plan);
        var cut=Assert.Single(cuts);
        Assert.Equal("a",cut.PreviousItemId);Assert.Equal("b",cut.ItemId);Assert.Equal(1,cut.Track);
        Assert.Equal("cross-dissolve",cut.Kind);Assert.Equal("center",cut.Align);
        Assert.Equal(.5,cut.RequestedDuration,6);Assert.Equal(.5,cut.EffectiveDuration,6);Assert.Equal(3.5,cut.Start,6);
        var starts=EditorRenderService.SequenceStarts(items,cuts);
        Assert.Equal(0,starts["a"],6);Assert.Equal(3.5,starts["b"],6);
        Assert.Equal(8,EditorRenderService.TimelineDuration(items),6);
        Assert.Equal(7.5,EditorRenderService.TrackDuration(items,cuts,1),6);
        Assert.Equal(7.5,EditorRenderService.SequenceDuration(items,cuts),6);
        Assert.Equal(7.5,EditorRenderService.SequenceDuration(plan),6);
        EditorRenderService.Validate(plan);
    }
    [Fact]
    public void GapBeforeAtCut_RejectsTransition()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8,gap:1)};
        Assert.Empty(EditorRenderService.ResolveTransitions(items,[Transition("b")],30));
        var starts=EditorRenderService.SequenceStarts(items,[]);
        Assert.Equal(5,starts["b"],6);
        Assert.Equal(9,EditorRenderService.SequenceDuration(items,[]),6);
        Assert.Equal(9,EditorRenderService.TimelineDuration(items),6);
        var plan=Plan(items,[Transition("b")]);EditorRenderService.Validate(plan);
        Assert.Equal(9,EditorRenderService.SequenceDuration(plan),6);
    }
    [Fact]
    public void OversizedDuration_ClampsToShorterNeighbour()
    {
        var items=new[]{Item("a",0,2),Item("b",2,8)};
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",duration:10)],30));
        Assert.Equal(10,cut.RequestedDuration,6);Assert.Equal(2,cut.EffectiveDuration,6);Assert.Equal(0,cut.Start,6);
        var starts=EditorRenderService.SequenceStarts(items,[cut]);
        Assert.Equal(0,starts["b"],6);
        Assert.Equal(6,EditorRenderService.SequenceDuration(items,[cut]),6);
        Assert.Equal(8,EditorRenderService.TimelineDuration(items),6);
    }
    [Fact]
    public void CenterAlignment_KeepsVersionOneLayout()
    {
        // center 是 v1 行为基线：Shift=EffectiveDuration，带以 cut 为中心，序列缩短 d。
        var items=new[]{Item("a",0,4),Item("b",4,8)};
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",align:"center")],30));
        Assert.Equal("center",cut.Align);Assert.Equal(.5,cut.EffectiveDuration,6);Assert.Equal(.5,cut.Shift,6);
        Assert.Equal(3.5,cut.Start,6);
        var span=EditorRenderService.TransitionSpan("center",.5);
        Assert.Equal(-.25,span.Start,6);Assert.Equal(.25,span.End,6);
        Assert.Equal(0,EditorRenderService.TransitionTailAdjust("center",.5),6);
        Assert.Equal(.5,EditorRenderService.TransitionShift("center",.5),6);
    }
    [Fact]
    public void StartAlignedTransition_AnchorsBandAtCut_AndNeedsTailHandle()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8)};
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",align:"start")],30));
        Assert.Equal("start",cut.Align);Assert.Equal(.5,cut.EffectiveDuration,6);
        // start：带 [C,C+d]，C=4-d/2；Shift=d/2=.25，右侧片段起点=3.75。
        Assert.Equal(.25,cut.Shift,6);Assert.Equal(3.75,cut.Start,6);
        Assert.Equal(.25,EditorRenderService.TransitionTailAdjust(cut),6);
        var span=EditorRenderService.TransitionSpan("start",.5);
        Assert.Equal(0,span.Start,6);Assert.Equal(.5,span.End,6);
        var starts=EditorRenderService.SequenceStarts(items,[cut]);
        Assert.Equal(3.75,starts["b"],6);
        Assert.Equal(7.75,EditorRenderService.TrackDuration(items,[cut],1),6);
        Assert.Equal(7.75,EditorRenderService.SequenceDuration(items,[cut]),6);
        Assert.Equal(8,EditorRenderService.TimelineDuration(items),6);
    }
    [Fact]
    public void EndAlignedTransition_AnchorsBandEndAtCut_AndTrimsPreviousTail()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8)};
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",align:"end")],30));
        Assert.Equal("end",cut.Align);Assert.Equal(.5,cut.EffectiveDuration,6);
        // end：带 [C-d,C]，C=4-d/2=3.75；Shift=3d/2=.75，右侧片段起点=3.25，前段尾部裁掉 d/2。
        Assert.Equal(.75,cut.Shift,6);Assert.Equal(3.25,cut.Start,6);
        Assert.Equal(-.25,EditorRenderService.TransitionTailAdjust(cut),6);
        var span=EditorRenderService.TransitionSpan("end",.5);
        Assert.Equal(-.5,span.Start,6);Assert.Equal(0,span.End,6);
        var starts=EditorRenderService.SequenceStarts(items,[cut]);
        Assert.Equal(3.25,starts["b"],6);
        Assert.Equal(7.25,EditorRenderService.TrackDuration(items,[cut],1),6);
        Assert.Equal(7.25,EditorRenderService.SequenceDuration(items,[cut]),6);
    }
    [Fact]
    public void EndAlignedTransition_ClampsToTwoThirdsOfPreviousDuration()
    {
        // end 需要转场带起点不越过前段起点：d≤2/3·前段时长。
        var items=new[]{Item("a",0,.6),Item("b",.6,4.6)};
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",align:"end",duration:.5)],30));
        Assert.Equal(.4,cut.EffectiveDuration,6);Assert.Equal(.6,cut.Shift,6);Assert.Equal(0,cut.Start,6);
        var span=EditorRenderService.TransitionSpan("end",.4);
        Assert.Equal(-.4,span.Start,6);Assert.Equal(0,span.End,6);
    }
    [Fact]
    public void StartAlignedTransition_ClampsToAvailableTailHandle()
    {
        // start 需要前段在源 End 之后还有 d/2 素材；只有拿到源时长时才按 handle clamp。
        var items=new[]{Item("a",0,3.8,"ca"),Item("b",3.8,7.8,"ca")};
        var optimistic=Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",align:"start")],30));
        Assert.Equal(.5,optimistic.EffectiveDuration,6);
        var durations=new Dictionary<string,double>{{"ca",4}}; // 前段尾部只剩 0.2s handle → d≤0.4
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",align:"start")],30,durations));
        Assert.Equal(.4,cut.EffectiveDuration,6);Assert.Equal(.2,cut.Shift,6);Assert.Equal(3.6,cut.Start,6);
        var starts=EditorRenderService.SequenceStarts(items,[cut]);
        Assert.Equal(3.6,starts["b"],6);
        // 前段正好用到源结尾（无 handle）→ 该转场不合法。
        var noHandleItems=new[]{Item("a",0,4,"cd"),Item("b",4,8,"cd")};
        var noHandle=new Dictionary<string,double>{{"cd",4}};
        Assert.Empty(EditorRenderService.ResolveTransitions(noHandleItems,[Transition("b",align:"start")],30,noHandle));
        // center 不受 handle clamp 影响。
        Assert.Equal(.5,Assert.Single(EditorRenderService.ResolveTransitions(noHandleItems,[Transition("b",align:"center")],30,noHandle)).EffectiveDuration,6);
    }
    [Fact]
    public void DurationBelowOneFrame_ClampsToMinimum_AndTinyClipsAreRejected()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8)};
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",duration:.001)],30));
        Assert.Equal(.001,cut.RequestedDuration,6);Assert.Equal(1.0/30,cut.EffectiveDuration,6);
        Assert.Equal(.04,Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",duration:0)],25)).EffectiveDuration,.0001);
        var tiny=new[]{Item("a",0,.02),Item("b",.02,.04)};
        Assert.Empty(EditorRenderService.ResolveTransitions(tiny,[Transition("b",duration:.001)],30));
    }
    [Fact]
    public void InvalidTransitions_AreDroppedButUnknownKindsReject()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8)};
        var error=Assert.Throws<ArgumentException>(()=>EditorRenderService.ResolveTransitions(items,[Transition("b",kind:"wipe",align:"left")],30));
        Assert.Contains("unsupported_transition:wipe",error.Message);
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(items,[Transition("b",align:"left")],30));
        Assert.Equal("cross-dissolve",cut.Kind);Assert.Equal("center",cut.Align);
        Assert.Empty(EditorRenderService.ResolveTransitions(items,[Transition("b",track:2)],30));
        Assert.Empty(EditorRenderService.ResolveTransitions(items,[Transition("missing")],30));
        Assert.Empty(EditorRenderService.ResolveTransitions([Item("a",0,4),Item("b",4,8,disabled:true)],[Transition("b")],30));
        Assert.Empty(EditorRenderService.ResolveTransitions([Item("a",0,4,disabled:true),Item("b",4,8)],[Transition("b")],30));
        Assert.Empty(EditorRenderService.ResolveTransitions(items,[],30));
        Assert.Empty(EditorRenderService.ResolveTransitions(items,null,30));
        var dup=EditorRenderService.ResolveTransitions(items,[Transition("b",duration:.5),Transition("b",duration:1.25,align:"start")],30);
        Assert.Equal(1.25,Assert.Single(dup).RequestedDuration,6);
        Assert.Equal("start",dup[0].Align);
        var noId=Assert.Single(EditorRenderService.ResolveTransitions(items,[new EditorTransition("","b",0,"dip-to-white",.5,"end")],30));
        Assert.Equal("transition-b",noId.Id);Assert.Equal("dip-to-white",noId.Kind);Assert.Equal("end",noId.Align);
    }
    [Fact]
    public void ProjectJson_OmitsNullTransitionsAndRoundTripsThem()
    {
        var legacy=JsonSerializer.Deserialize<EditorProject>("{}")!;Assert.Null(legacy.Transitions);
        var explicitNull=JsonSerializer.Deserialize<EditorProject>("{\"Transitions\":null}")!;Assert.Null(explicitNull.Transitions);
        var plain=Plan([Item("a",0,4),Item("b",4,8)]);Assert.DoesNotContain("Transitions",JsonSerializer.Serialize(plain));
        var withTransition=Plan([Item("a",0,4),Item("b",4,8)],[Transition("b","dip-to-black",1,"start")]);
        var json=JsonSerializer.Serialize(withTransition);Assert.Contains("dip-to-black",json);
        var restored=JsonSerializer.Deserialize<EditorProject>(json)!;
        Assert.Equal(withTransition.Transitions![0],Assert.Single(restored.Transitions!));
        Assert.Equal(1,restored.Transitions![0].Duration,6);
        EditorRenderService.Validate(restored);
    }
    [Fact]
    public void MultiTrackTimeline_UsesCompressedStartsAndMaxTrackDuration()
    {
        var items=new[]
        {
            Item("a",0,4),Item("b",4,8),                                  // V1：0.5s 转场 → 7.5s
            Item("o1",0,5,track:2),Item("o2",0,2,track:2,gap:1),          // V2：0 + 5 + gap1 + 2 = 8s，o2 start=6
            Item("t1",0,3,track:3,gap:1),                                 // V3：1 + 3 = 4s
            Item("s1",0,9,track:4)                                        // A1：9s
        };
        var cuts=EditorRenderService.ResolveTransitions(items,[Transition("b")],30);
        var starts=EditorRenderService.SequenceStarts(items,cuts);
        Assert.Equal(0,starts["a"],6);Assert.Equal(3.5,starts["b"],6);
        Assert.Equal(0,starts["o1"],6);Assert.Equal(6,starts["o2"],6);
        Assert.Equal(1,starts["t1"],6);Assert.Equal(0,starts["s1"],6);
        Assert.Equal(7.5,EditorRenderService.TrackDuration(items,cuts,1),6);
        Assert.Equal(8,EditorRenderService.TrackDuration(items,cuts,2),6);
        Assert.Equal(4,EditorRenderService.TrackDuration(items,cuts,3),6);
        Assert.Equal(9,EditorRenderService.SequenceDuration(items,cuts),6);
        EditorRenderService.Validate(Plan(items,[Transition("b")]));
    }
    [Fact]
    public void NoTransitions_KeepOriginalTimelineDurationAndSubtitleMapping()
    {
        var items=new[]{Item("a",0,4,"ca"),Item("b",0,4,"cb",gap:.5,speed:2)};
        Assert.Equal(6.5,EditorRenderService.TimelineDuration(items),6);
        Assert.Equal(6.5,EditorRenderService.SequenceDuration(items,[]),6);
        Assert.Equal(4.5,EditorRenderService.SequenceStarts(items,[])["b"],6);
        IReadOnlyList<TranscriptSegment> Source(string clip)=>[new(0,4,"x")];
        var mapped=EditorRenderService.MapSubtitles(items,Source,"ca",null);
        Assert.Equal(new[]{0d,4.5d},mapped.Select(x=>x.Start).ToArray());
        Assert.Equal(new[]{4d,6.5d},mapped.Select(x=>x.End).ToArray());
        var same=EditorRenderService.MapSubtitles(items,Source,"source",null,[],30);
        Assert.Equal(mapped,same);
    }
    [Fact]
    public void SubtitlesAndOverlays_UseCompressedSequenceStart()
    {
        var items=new[]{Item("a",0,4,"ca"),Item("b",0,4,"cb")};
        IReadOnlyList<TranscriptSegment> Source(string clip)=>clip=="ca"?[new(0,4,"A")]:[new(0,4,"B")];
        var mapped=EditorRenderService.MapSubtitles(items,Source,"ca",null,[Transition("b")],30);
        Assert.Equal(new[]{0d,3.5},mapped.Select(x=>x.Start).ToArray());
        Assert.Equal(new[]{4d,7.5},mapped.Select(x=>x.End).ToArray());
        Assert.Equal(new[]{"A","B"},mapped.Select(x=>x.Text).ToArray());
        var untouched=EditorRenderService.MapSubtitles(items,Source,"ca",null);
        Assert.Equal(new[]{0d,4d},untouched.Select(x=>x.Start).ToArray());
    }
    [Fact]
    public void TimecodeAndCutLabel_IdentifyTheCut()
    {
        Assert.Equal("00:00:03:00",EditorRenderService.Timecode(3,30));
        Assert.Equal("00:00:03:15",EditorRenderService.Timecode(3.5,30));
        Assert.Equal("00:01:05:05",EditorRenderService.Timecode(65.2,25));
        Assert.Equal("00:00:00:00",EditorRenderService.Timecode(-1,0));
        var cut=Assert.Single(EditorRenderService.ResolveTransitions([Item("a",0,4),Item("b",4,8)],[Transition("b")],30));
        var label=EditorRenderService.CutLabel(cut,30);
        Assert.Contains("剪点 00:00:03:15",label);Assert.Contains("片段 b",label);
    }
    [Fact]
    public void Validate_AcceptsLegalAndSilentlyDropsIllegalTransitions()
    {
        EditorRenderService.Validate(Plan([Item("a",0,4),Item("b",4,8)],[Transition("b")]));
        var unsupported=Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(Plan([Item("a",0,4),Item("b",4,8)],[Transition("missing"),Transition("b",kind:"wipe",track:3),Transition("b",duration:-5)])));
        Assert.Contains("unsupported_transition:wipe",unsupported.Message);
        EditorRenderService.Validate(Plan([Item("a",0,4),Item("b",4,8)],[Transition("missing"),Transition("b",track:3),Transition("b",duration:-5)]));
        EditorRenderService.Validate(Plan([Item("a",0,4),Item("b",4,8,gap:2)],[Transition("b")]));
    }

    private static string TestFfmpeg()
    {
        var fromEnv=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(!string.IsNullOrWhiteSpace(fromEnv)&&File.Exists(fromEnv))return fromEnv;
        var bundled=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","tools","ffmpeg","bin","ffmpeg.exe");
        return File.Exists(bundled)?bundled:"ffmpeg";
    }
    [Fact]
    public async Task Assemble_WithTransitionsRendersCompressedDuration_AndMatchesConcatBaselineWithoutThem()
    {
        var tools=new MediaTools(TestFfmpeg());if(!tools.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，转场渲染测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-transition-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=640x360:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","8","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-b:a","128k",source],CancellationToken.None);
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            var timeline=new[]{Item("a",0,4,"clip"),Item("b",4,8,"clip")};

            async Task<(double Duration,string Path)> Render(TimelineItem[] items,EditorTransition[]? transitions,string name)
            {
                var plan=Plan(items,transitions);var work=Path.Combine(root,name);Directory.CreateDirectory(work);
                var assembled=await renderer.AssembleAsync(clip,plan,work,CancellationToken.None);
                Assert.True(File.Exists(assembled.Video));
                return (await tools.DurationAsync(assembled.Video,CancellationToken.None),assembled.Video);
            }
            // 转场剪点中间帧的平均亮度；用来确认交叉淡化没有出现黑场空洞（规格 §5(3)/§6）。
            async Task<double> LumaAt(string path,double seconds)
            {
                var log=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","info","-ss",seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),"-i",path,"-vf","signalstats,metadata=print","-frames:v","1","-an","-f","null","-"],CancellationToken.None);
                var match=System.Text.RegularExpressions.Regex.Match(log,@"lavfi\.signalstats\.YAVG=([0-9.]+)");
                Assert.True(match.Success,"无法从 ffmpeg 读取帧亮度: "+log);
                return double.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
            }

            var (dissolve,dissolvePath)=await Render(timeline,[Transition("b")],"dissolve");
            Assert.InRange(dissolve,7.4,7.6);
            // 0.5s 交叉淡化（剪点 3.5–4.0s）：中点处应仍是可见画面，而不是黑场。
            Assert.True(await LumaAt(dissolvePath,3.75)>16,"交叉淡化中点出现黑场");
            var blackLog=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","info","-i",dissolvePath,"-vf","blackdetect=d=0.1:pix_th=0.10","-an","-f","null","-"],CancellationToken.None);
            Assert.DoesNotContain("black_start",blackLog);
            var (dip,_)=await Render(timeline,[Transition("b","dip-to-black")],"dip");
            Assert.InRange(dip,7.4,7.6);
            var (baseline,_)=await Render(timeline,null,"baseline");
            Assert.InRange(baseline,7.9,8.1);
            // 连续两个转场：3+3+2−0.5−0.5=7.0 秒
            var (chain,_)=await Render([Item("a",0,3,"clip"),Item("b",3,6,"clip"),Item("c",6,8,"clip")],[Transition("b"),Transition("c")],"chain");
            Assert.InRange(chain,6.9,7.1);
            // 超长请求被 clamp 到短片段：2+6 秒片段重叠 2 秒 → 6.0 秒
            var (clamped,_)=await Render([Item("a",0,2,"clip"),Item("b",2,8,"clip")],[Transition("b",duration:10)],"clamped");
            Assert.InRange(clamped,5.9,6.1);
            // V1 转场 + V2 叠加层：叠加层按压缩后的起点落位，总时长仍是 7.5 秒
            var (withOverlay,_)=await Render([Item("a",0,4,"clip"),Item("b",4,8,"clip"),Item("o1",0,4,"clip",track:2)],[Transition("b")],"overlay");
            Assert.InRange(withOverlay,7.4,7.6);
            // 对齐三态（规格 §10）：start 让带起点落在 cut（3.5+3.5−0.25=6.75），end 让带终点落在 cut（7−0.75=6.25）。
            var aligned=new[]{Item("a",0,3.5,"clip"),Item("b",4.5,8,"clip")};
            var (startAligned,startAlignedPath)=await Render(aligned,[Transition("b",align:"start")],"start-align");
            Assert.InRange(startAligned,6.65,6.85);
            var startBlackLog=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","info","-i",startAlignedPath,"-vf","blackdetect=d=0.1:pix_th=0.10","-an","-f","null","-"],CancellationToken.None);
            Assert.DoesNotContain("black_start",startBlackLog);
            Assert.True(await LumaAt(startAlignedPath,3.5)>16,"起点对齐转场区间出现黑场");
            var (endAligned,endAlignedPath)=await Render(aligned,[Transition("b",align:"end")],"end-align");
            Assert.InRange(endAligned,6.15,6.35);
            var endBlackLog=await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","info","-i",endAlignedPath,"-vf","blackdetect=d=0.1:pix_th=0.10","-an","-f","null","-"],CancellationToken.None);
            Assert.DoesNotContain("black_start",endBlackLog);
            Assert.True(await LumaAt(endAlignedPath,3.0)>16,"终点对齐转场区间出现黑场");
        }
        finally
        {
            await renderer.StopAsync();
            try{Directory.Delete(root,true);}catch(IOException){}
        }
    }

    [Theory]
    [InlineData("cross-dissolve")]
    [InlineData("dip-to-black")]
    [InlineData("dip-to-white")]
    [InlineData("wipe-left")]
    [InlineData("wipe-right")]
    [InlineData("wipe-up")]
    [InlineData("wipe-down")]
    [InlineData("additive-dissolve")]
    public void R73T2_CanonicalKindsRetainJsonAndResolvedTiming(string kind)
    {
        var plan=Plan([Item("a",0,4),Item("b",4,8)],[Transition("b",kind,1,"center")]);
        var restored=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(kind,Assert.Single(restored.Transitions!).Kind);
        EditorRenderService.Validate(restored);
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(restored));
        Assert.Equal(kind,cut.Kind);Assert.Equal(1,cut.EffectiveDuration,6);Assert.Equal(3,cut.Start,6);
        Assert.Equal(7,EditorRenderService.SequenceDuration(restored),6);
    }

    [Theory]
    [InlineData("cross_dissolve_legacy")]
    [InlineData("barn_doors")]
    [InlineData("iris_box")]
    [InlineData("iris_cross")]
    [InlineData("iris_diamond")]
    [InlineData("iris_round")]
    [InlineData("gradient_wipe")]
    [InlineData("push_legacy")]
    [InlineData("slide_legacy")]
    [InlineData("circleopen")]
    [InlineData("rectcrop")]
    public void R73T2_UnprojectedCatalogKindsStayExplicitlyRejectedAfterJsonReload(string kind)
    {
        var plan=Plan([Item("a",0,4),Item("b",4,8)],[Transition("b",kind)]);
        var restored=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(kind,Assert.Single(restored.Transitions!).Kind);
        Assert.Contains($"unsupported_transition:{kind}",Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(restored)).Message);
        Assert.Contains($"unsupported_transition:{kind}",Assert.Throws<ArgumentException>(()=>EditorRenderService.ResolveTransitions(restored)).Message);
        restored=restored with {Transitions=[Transition("missing",kind,track:3),Transition("b")]};
        Assert.Contains($"unsupported_transition:{kind}",Assert.Throws<ArgumentException>(()=>EditorRenderService.ResolveTransitions(restored)).Message);
    }
}
