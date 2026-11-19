using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

public sealed class EditorTests
{
    [Fact]
    public void StickerRotation_SurvivesProjectSaveAndOlderPlansDefaultToZero()
    {
        var original=new EditorProject{Timeline=[new("a","source",0,2,StickerAssetId:"sticker",StickerRotation:63,StickerX:.4,StickerWidth:.3)]};
        var restored=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(original))!;Assert.Equal(original.Timeline![0],restored.Timeline![0]);
        var legacy=JsonSerializer.Deserialize<TimelineItem>("{\"Id\":\"a\",\"ClipId\":\"source\",\"Start\":0,\"End\":2}")!;Assert.Equal(0,legacy.StickerRotation);
    }
    [Fact]
    public void SentenceCuts_ExcludeDeletedSentenceAndPreserveGaps()
    {
        TranscriptSegment[] segments=[new(0,1,"第一句"),new(1,1.1,"删掉这句"),new(1.1,2,"第三句"),new(3,4,"第四句")];
        var items=EditorRenderService.SentenceTimeline("source",segments,[0,2,3],0,4);
        Assert.Equal(3,items.Length);Assert.Equal(new[]{0d,1.1,3},items.Select(x=>x.Start));Assert.Equal(2.9,items.Sum(x=>x.End-x.Start),6);
    }
    [Fact]
    public void TimelineCaptions_FollowReorderingAndIncludeUnselectedHook()
    {
        TranscriptSegment[] segments=[new(0,2,"开头钩子"),new(2,4,"删除"),new(4,7,"保留")];
        TimelineItem[] items=[new("h","a",0,2,"hook"),new("b","a",5,7)];
        var mapped=EditorRenderService.MapSubtitles(items,_=>segments,"a",[2]);
        Assert.Equal(new[]{"开头钩子","保留"},mapped.Select(x=>x.Text));Assert.Equal(0,mapped[0].Start);Assert.Equal(2,mapped[1].Start);Assert.Equal(4,mapped[1].End);
    }
    [Theory]
    [InlineData(24)][InlineData(25)][InlineData(30)][InlineData(50)][InlineData(60)][InlineData(30000d/1001)][InlineData(24000d/1001)][InlineData(60000d/1001)]
    public void ToAss_FrameAligned_StartVisibleOnItsFrameAndEndHiddenOnItsFrame(double fps)
    {
        // 模拟 subtitles 滤镜：帧 pts 截断为毫秒，事件可见 ⇔ start≤ms<end（ASS 精度 1/100 秒）。
        static long Ms(string t){var p=t.Split(':','.');return(long.Parse(p[0])*3600+long.Parse(p[1])*60+long.Parse(p[2]))*1000+long.Parse(p[3])*10;}
        foreach(var (s,e) in new[]{(0,5),(189,251),(190,250),(1,2),(7,100),(333,334),(600,1201)})
        {
            var cap=new TimelineCaption{Id="c",Start=s/fps,End=e/fps,Text="x"};
            var line=TimelineCaptionTools.ToAss([cap],640,360,fps).Split('\n').Single(x=>x.StartsWith("Dialogue:")).Split(',');
            long a=Ms(line[1]),b=Ms(line[2]);
            bool Vis(int n)=>(long)Math.Floor(n/fps*1000) is var ms&&a<=ms&&ms<b;
            Assert.True(Vis(s),$"fps={fps} start frame {s}");
            if(s>0)Assert.False(Vis(s-1),$"fps={fps} frame before start {s}");
            Assert.True(Vis(e-1),$"fps={fps} last frame {e-1}");
            Assert.False(Vis(e),$"fps={fps} end frame {e}");
        }
    }
    [Fact]
    public async Task RealFfmpeg_AssUnicodeWrapKeepsLongChineseInsidePortraitMargins()
    {
        var executable=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(string.IsNullOrWhiteSpace(executable))return;
        var tools=new MediaTools(executable);Assert.True(tools.Available());
        var root=Path.Combine(Path.GetTempPath(),"lc-ass-wrap-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var caption=new TimelineCaption{Id="wrap",Start=0,End=2,Text="竖屏序列字幕测试：这是一行用于验证自动换行和安全边界的文字，不修改真实素材字幕。"};
            var ass=Path.Combine(root,"caption.ass");await File.WriteAllTextAsync(ass,TimelineCaptionTools.ToAss([caption],1080,1920,25));
            async Task<(int Left,int Right,int Bands)> Render(string filter,string file)
            {
                var output=Path.Combine(root,file);
                await tools.RunAsync(executable,["-v","error","-y","-f","lavfi","-i","color=black:s=1080x1920:r=25:d=1","-vf",filter,"-frames:v","1","-pix_fmt","gray","-f","rawvideo",output],CancellationToken.None);
                var bytes=await File.ReadAllBytesAsync(output);Assert.Equal(1080*1920,bytes.Length);
                var left=1080;var right=-1;var bands=0;var prior=false;
                for(var y=0;y<1920;y++)
                {
                    var row=false;for(var x=0;x<1080;x++)if(bytes[y*1080+x]>100){left=Math.Min(left,x);right=Math.Max(right,x);row=true;}
                    if(row&&!prior)bands++;prior=row;
                }
                return(left,right,bands);
            }
            var filter=EditorRenderService.IndependentCaptionFilter(ass);
            var actual=await Render(filter,"wrapped.gray");
            Assert.InRange(actual.Left,35,1044);Assert.InRange(actual.Right,35,1044);Assert.Equal(2,actual.Bands);
            // Negative witness proves this fixture catches the previous default-auto ASS path.
            var broken=await Render(filter.Replace(":wrap_unicode=1",""),"unwrapped.gray");
            Assert.Equal((0,1079,1),broken);
        }
        finally{Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData(1920,1080,30,540)]
    [InlineData(1080,1920,25,1707)]
    [InlineData(1080,1080,60,960)]
    public void SequenceCanvas_AssUsesAspectAndFrameRateWithoutChangingCueSeconds(int width,int height,int fps,int playHeight)
    {
        var cue=new TimelineCaption{Id="sequence-cue",Start=1.23,End=3.67,Text="Long caption stays within the sequence canvas",PositionX=.5,PositionY=.8,Size=34,Margin=36};
        var project=new EditorProject{CanvasWidth=width,CanvasHeight=height,Fps=fps,TimelineCaptions=[cue]};
        var saved=EditorProjectCodec.Deserialize(JsonSerializer.Serialize(project));
        var ass=TimelineCaptionTools.ToAss(saved.TimelineCaptions!,saved.CanvasWidth!.Value,saved.CanvasHeight!.Value,saved.Fps);
        Assert.Contains("PlayResX: 960\nPlayResY: "+playHeight,ass);
        Assert.Contains("\\pos(480,"+(playHeight*.8).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+")",ass);
        Assert.Contains(",36,36,36,,",ass);Assert.Contains("\\fs34",ass);
        Assert.Equal(1.23,saved.TimelineCaptions![0].Start);Assert.Equal(3.67,saved.TimelineCaptions[0].End);
        var dialogue=ass.Split('\n').Single(x=>x.StartsWith("Dialogue:")).Split(',');
        static double Seconds(string value){var parts=value.Split(':');return double.Parse(parts[0])*3600+double.Parse(parts[1])*60+double.Parse(parts[2],System.Globalization.CultureInfo.InvariantCulture);}
        var first=Math.Ceiling(cue.Start*fps)/fps;var end=Math.Ceiling(cue.End*fps)/fps;
        Assert.InRange(first-Seconds(dialogue[1]),0,.011);Assert.InRange(end-Seconds(dialogue[2]),0,.011);
        Assert.True(Seconds(dialogue[1])>first-1d/fps);Assert.True(Seconds(dialogue[2])>end-1d/fps);
    }

    [Fact]
    public void ToAss_WithoutFps_KeepsLegacyCentisecondRounding()
    {
        var cap=new TimelineCaption{Id="c",Start=251/30d,End=8.3666667,Text="x"};
        var legacy=TimelineCaptionTools.ToAss([cap],640,360);
        Assert.Contains("0:00:08.37,0:00:08.37",legacy);
        Assert.Equal(legacy,TimelineCaptionTools.ToAss([cap],640,360,null));
        Assert.Equal(legacy,TimelineCaptionTools.ToAss([cap],640,360,120));
    }
    [Fact]
    public void TimelineCaptions_MapInsertedSourceToOutputTime()
    {
        TimelineItem[] items=[new("a","primary",3,5),new("b","insert",10,12,"insert"),new("c","primary",0,1)];
        var mapped=EditorRenderService.MapSubtitles(items,id=>id=="primary"?[new(0,1,"末尾"),new(3.5,5,"开头")]:[new(10.5,11.5,"插入")],"primary",null);
        Assert.Equal(new[]{.5,2.5,4},mapped.Select(x=>x.Start));Assert.Equal(new[]{"开头","插入","末尾"},mapped.Select(x=>x.Text));
    }
    [Fact]
    public void InvalidEffectsAndDuplicateIds_AreRejectedBeforeRendering()
    {
        var plan=new EditorProject{Timeline=[new("same","a",0,2),new("same","b",0,2)]};Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Zoom:double.NaN)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Filter:"arbitrary FFmpeg filter")]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,StickerRotation:double.NaN)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,StickerRotation:181)]}));
    }
    [Fact]
    public void Gaps_SurviveSerializationAndShiftSubtitles()
    {
        var plan=new EditorProject{Timeline=[new("a","a",0,1),new("b","a",1,2,GapBefore:2)]};
        var restored=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(2,restored.Timeline![1].GapBefore);
        var legacy=JsonSerializer.Deserialize<TimelineItem>("{\"Id\":\"a\",\"ClipId\":\"source\",\"Start\":0,\"End\":2}")!;
        Assert.Equal(0,legacy.GapBefore);

        TimelineItem[] items=[new("a","source",0,1),new("b","source",1,2,GapBefore:2)];
        var mapped=EditorRenderService.MapSubtitles(items,_=>[new(0,1,"一"),new(1,2,"二")],"source",null);
        Assert.Equal(new[]{0d,3d},mapped.Select(x=>x.Start));
        Assert.Equal(new[]{1d,4d},mapped.Select(x=>x.End));
    }

    [Fact]
    public void Gaps_AreValidatedBeforeRendering()
    {
        var plan=new EditorProject{Timeline=[new("a","a",0,2,GapBefore:1)]};
        EditorRenderService.Validate(plan);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,GapBefore:double.NaN)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,GapBefore:-1)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,GapBefore:21601)]}));
    }

    [Fact]
    public void Speed_ScalesOutputDurationAndSubtitlesAndIsValidated()
    {
        var plan=new EditorProject{Timeline=[new("a","source",0,2,GapBefore:1,Speed:2)]};
        EditorRenderService.Validate(plan);
        var restored=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(2,restored.Timeline![0].Speed);
        var legacy=JsonSerializer.Deserialize<TimelineItem>("{\"Id\":\"a\",\"ClipId\":\"source\",\"Start\":0,\"End\":2}")!;
        Assert.Equal(1,legacy.Speed);
        var mapped=EditorRenderService.MapSubtitles(plan.Timeline!,_=>[new(0,2,"整段")],"source",null);
        Assert.Equal(1,mapped.Single().Start,6);
        Assert.Equal(2,mapped.Single().End,6);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Speed:.24)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Speed:4.1)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Speed:double.NaN)]}));
    }

    [Fact]
    public void Tracks_UseLongestLaneAndKeepV1AsProgramBase()
    {
        var plan=new EditorProject{Timeline=[
            new("base","source",0,2),
            new("overlay","source",0,2,GapBefore:1,Track:2),
        ]};
        EditorRenderService.Validate(plan);
        Assert.Single(EditorRenderService.BaseItems(plan.Timeline!));
        Assert.Equal(3,EditorRenderService.TimelineDuration(plan.Timeline!),6);
        var mapped=EditorRenderService.MapSubtitles(EditorRenderService.BaseItems(plan.Timeline!),_=>[new(0,2,"主轨字幕")],"source",null);
        Assert.Single(mapped);Assert.Equal(0,mapped[0].Start);Assert.Equal(2,mapped[0].End);
        var restored=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(2,restored.Timeline![1].Track);
        var legacy=JsonSerializer.Deserialize<TimelineItem>("{\"Id\":\"a\",\"ClipId\":\"source\",\"Start\":0,\"End\":2}")!;
        Assert.Equal(1,legacy.Track);
        var withAudio=plan with {Timeline=[
            new("base","source",0,2),
            new("voice","source",0,1,GapBefore:2,Track:4),
        ]};
        EditorRenderService.Validate(withAudio);
        Assert.Equal(3,EditorRenderService.TimelineDuration(withAudio.Timeline!),6);
        Assert.Equal(4,JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(withAudio))!.Timeline![1].Track);
        var withTrackState=withAudio with {MutedTracks=[4],SoloedTracks=[5]};
        EditorRenderService.Validate(withTrackState);
        var restoredState=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(withTrackState))!;
        Assert.Equal([4],restoredState.MutedTracks!);
        Assert.Equal([5],restoredState.SoloedTracks!);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(withTrackState with {MutedTracks=[7]}));
        var withHidden=withAudio with {HiddenTracks=[1]};
        EditorRenderService.Validate(withHidden);
        var restoredHidden=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(withHidden))!;
        Assert.Equal([1],restoredHidden.HiddenTracks!);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(withAudio with {HiddenTracks=[4]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(withAudio with {HiddenTracks=[1,1]}));
        var withLocked=withAudio with {LockedTracks=[1,6]};
        EditorRenderService.Validate(withLocked);
        Assert.Equal([1,6],JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(withLocked))!.LockedTracks!);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(withAudio with {LockedTracks=[7]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(withAudio with {LockedTracks=[4,4]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Track:7)]}));
        var overlayOnly=plan with {Timeline=[new("a","a",0,2,Track:2)]};
        EditorRenderService.Validate(overlayOnly);
        Assert.Empty(EditorRenderService.BaseItems(overlayOnly.Timeline!));
        var detached=plan with {Timeline=[new("a","a",0,2,AudioDetached:true)]};
        EditorRenderService.Validate(detached);
        Assert.True(JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(detached.Timeline![0]))!.AudioDetached);
        var withTransform=plan with {Timeline=[new("a","a",0,2,Rotation:15,Opacity:.5)]};
        EditorRenderService.Validate(withTransform);
        var restoredTransform=JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(withTransform.Timeline![0]))!;
        Assert.Equal(15,restoredTransform.Rotation);
        Assert.Equal(.5,restoredTransform.Opacity);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Rotation:181)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Opacity:1.1)]}));
        var withPosition=plan with {Timeline=[new("a","a",0,2,PositionX:.25,PositionY:-.2)]};
        EditorRenderService.Validate(withPosition);
        var restoredPosition=JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(withPosition.Timeline![0]))!;
        Assert.Equal(.25,restoredPosition.PositionX);
        Assert.Equal(-.2,restoredPosition.PositionY);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,PositionX:.6)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,PositionY:-.6)]}));
        var withTitle=plan with {Timeline=[new("a","a",0,2,Track:3,TitleText:"测试标题",TitleSize:54,TitleColor:"#FFD166",TitleX:.5,TitleY:.82)]};
        EditorRenderService.Validate(withTitle);
        var restoredTitle=JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(withTitle.Timeline![0]))!;
        Assert.Equal("测试标题",restoredTitle.TitleText);
        Assert.Equal(54,restoredTitle.TitleSize);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Track:3,TitleText:new string('x',121))]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Track:3,TitleText:"x",TitleSize:11)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,Track:3,TitleText:"x",TitleColor:"red")]}));
        var withFade=plan with {Timeline=[new("a","a",0,2,FadeIn:.5,FadeOut:.4)]};
        EditorRenderService.Validate(withFade);
        var restoredFade=JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(withFade.Timeline![0]))!;
        Assert.Equal(.5,restoredFade.FadeIn);
        Assert.Equal(.4,restoredFade.FadeOut);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,FadeIn:-.1)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,FadeIn:1.2,FadeOut:1.2)]}));
        var withTrackOpacity=plan with {TrackOpacities=[1,.5,1]};
        EditorRenderService.Validate(withTrackOpacity);
        Assert.Equal([1,.5,1],JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(withTrackOpacity))!.TrackOpacities!);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {TrackOpacities=[1,.5]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {TrackOpacities=[1,1.2,1]}));
        var withDisabled=plan with {Timeline=[new("a","a",0,2,Disabled:true)]};
        var restoredDisabled=JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(withDisabled.Timeline![0]))!;
        Assert.True(restoredDisabled.Disabled);
        Assert.Equal(2,EditorRenderService.TimelineDuration(withDisabled.Timeline!));
        var withClipVolume=plan with {Timeline=[new("a","a",0,2,VolumeDb:-6.5)]};
        EditorRenderService.Validate(withClipVolume);
        Assert.Equal(-6.5,JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(withClipVolume.Timeline![0]))!.VolumeDb);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,VolumeDb:-61)]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("a","a",0,2,VolumeDb:13)]}));
        var withMarkers=plan with {Markers=[.5,1.5]};
        EditorRenderService.Validate(withMarkers);
        Assert.Equal([.5,1.5],JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(withMarkers))!.Markers!);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(withMarkers with {Markers=[.5,.5]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(withMarkers with {Markers=[3.5]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(withMarkers with {Markers=[double.NaN]}));
    }

    [Fact]
    public void MasterVolume_IsValidatedAndSurvivesSerialization()
    {
        var plan=new EditorProject{Timeline=[new("a","a",0,2)],MasterVolume=.65};
        EditorRenderService.Validate(plan);
        var restored=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(plan))!;Assert.Equal(.65,restored.MasterVolume);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {MasterVolume=-.1}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {MasterVolume=2.1}));
        var legacy=JsonSerializer.Deserialize<EditorProject>("{\"Start\":0,\"End\":2}")!;Assert.Equal(1,legacy.MasterVolume);
    }
    [Fact]
    public async Task QueueSnapshots_KeepCaptionsStableWhileNextProjectIsEdited()
    {
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-snapshot-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var store=new Store(Path.Combine(root,"test.db"));store.Migrate();var studio=new StudioStore(store.DatabasePath);studio.Migrate();var clip=new Clip("clip","event","source.mp4",null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            store.SaveTranscript("event",SubtitleTools.ToJson(new("zh",[new(0,1,"原字幕")],"")),"source.srt");var renderer=new EditorRenderService(store,studio,new MediaTools(),root);
            var snapshot=renderer.Snapshot(clip,new EditorProject{Start=0,End=1});store.SaveTranscript("event",SubtitleTools.ToJson(new("zh",[new(0,1,"后来修改")],"")),"source.srt");
            Assert.Equal("原字幕",snapshot.SourceTranscripts![clip.Id][0].Text);
            var output=new StudioOutput("id",clip.Id,"package","output.zip",JsonSerializer.Serialize(snapshot),DateTimeOffset.UtcNow);studio.SaveOutput(output);var restored=JsonSerializer.Deserialize<EditorProject>(studio.Outputs()[0].ProjectJson);Assert.NotNull(restored);Assert.Equal(snapshot,restored with {SourceTranscripts=snapshot.SourceTranscripts});
            await renderer.StopAsync();
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void EventClear_LegacyProjectWithoutMarksValidatesWithSingleFallbackItem()
    {
        foreach(var plan in new[]{new EditorProject{Start=0,End=2},new EditorProject{Start=0,End=2,Timeline=null,ClearedEventSelections=null}})
        {
            EditorRenderService.Validate(plan);
            Assert.Equal(new TimelineItem("source","primary",0,2),Assert.Single(EditorRenderService.Items(plan,"primary")));
        }
    }
    [Fact]
    public void EventClear_EmptyTimelineWithMarksRoundTripsAndKeepsSourceCaptionOverrides()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plan=new EditorProject
        {
            Timeline=[],
            ClearedEventSelections=[new TimelineEventSelectionClear{Version=1,SourceId="source"}],
            Markers=[0],
            CaptionOverrides=new Dictionary<string,TranscriptSegment[]>{["source"]=[new TranscriptSegment(0,1,"保留")]},
        };
        EditorRenderService.Validate(plan);
        var restored=EditorProjectCodec.Deserialize(EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(plan,options)));
        EditorRenderService.Validate(restored);
        Assert.Empty(restored.Timeline!);Assert.Empty(EditorRenderService.Items(restored,"primary"));
        var clear=Assert.Single(restored.ClearedEventSelections!);
        Assert.Equal(1,clear.Version);Assert.Equal("source",clear.SourceId);
        Assert.Equal([0],restored.Markers!);
        Assert.Equal([new TranscriptSegment(0,1,"保留")],restored.CaptionOverrides!["source"]);
    }
    [Fact]
    public void EmptyTimeline_IsPersistableWithoutMarks_ButCannotRender()
    {
        foreach(var plan in new[]{new EditorProject{Timeline=[],ClearedEventSelections=null},new EditorProject{Timeline=[],ClearedEventSelections=[]}})
        {
            EditorRenderService.ValidateForSave(plan);
            var restored=EditorProjectCodec.Deserialize(JsonSerializer.Serialize(plan));
            Assert.Empty(EditorRenderService.Items(restored,"primary"));
            Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForRender(restored));
        }
        var invalid=new EditorProject{Timeline=[],Markers=[.5]};
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForSave(invalid));
        Assert.Equal([.5],invalid.Markers!);
        var captioned=invalid with {Markers=null,TimelineCaptions=[new TimelineCaption{Id="cue",Start=0,End=1,Text="keep"}]};
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForSave(captioned));
        Assert.Equal("keep",Assert.Single(captioned.TimelineCaptions!).Text);
    }
    [Theory]
    [InlineData("""{"timeline":[],"clearedEventSelections":[{"version":2,"sourceId":"source"}]}""","cleared-event-selection-invalid-version")]
    [InlineData("""{"timeline":[],"clearedEventSelections":[{"version":1.0,"sourceId":"source"}]}""","cleared-event-selection-invalid-version")]
    [InlineData("""{"timeline":[],"clearedEventSelections":[{"version":1,"sourceId":"source"},{"version":1,"sourceId":"source"}]}""","cleared-event-selection-duplicate-source")]
    [InlineData("""{"timeline":[],"clearedEventSelections":[{"version":1,"sourceId":"source","force":true}]}""","cleared-event-selection-invalid-field")]
    [InlineData("""{"timeline":[],"clearedEventSelections":[{"version":1,"sourceId":" "}]}""","cleared-event-selection-invalid-source")]
    public void Codec_RejectsRawClearedEventSelectionPerCase(string json,string code)
    {
        var ex=Assert.Throws<EditorProjectSchemaException>(()=>EditorProjectCodec.Deserialize(json));
        Assert.Equal(EditorProjectSchemaErrors.Invalid,ex.Code);Assert.Equal(code,ex.Message);
    }
    [Fact]
    public void EventClear_TypedValidateRejectsNullTimelineAndPlacedManagedContradiction()
    {
        var clear=new TimelineEventSelectionClear{Version=1,SourceId="source"};
        Assert.Equal("受管理事件清空标记要求 timeline 为显式数组（[] 或非空），不能是缺省/null",Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(new EditorProject{Start=0,End=2,ClearedEventSelections=[clear]})).Message);
        var placed=new TimelineItem("evt-1","source",0,2){EventPlacement=new TimelineEventPlacement{Version=1,SourceId="source",EventId="evt-1"}};
        Assert.Equal("受管理事件清空标记与时间线矛盾：被标记清空的源仍有事件片段在时间线上",Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(new EditorProject{Timeline=[placed],ClearedEventSelections=[clear]})).Message);
    }
    [Fact]
    public void EventClear_EmptyTimelineRejectsOutOfRangeMarkersAndCaptionsWithoutDeletingThem()
    {
        var plan=new EditorProject{Timeline=[],ClearedEventSelections=[new TimelineEventSelectionClear{Version=1,SourceId="source"}],Markers=[.5]};
        Assert.Equal("时间线标记无效",Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan)).Message);
        Assert.Equal([.5],plan.Markers!);
        var captioned=plan with {Markers=null,TimelineCaptions=[new TimelineCaption{Id="cue-1",Start=0,End=1,Text="保留"}]};
        Assert.Equal("独立字幕无效：需唯一 id、非空文本、有限且 end>start 的序列秒、字体/字号(10–80)/颜色(#RRGGBB)/位置(0–1，X/Y 成对)/描边(0–8)/边距(0–200)",Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(captioned)).Message);
        Assert.Equal("保留",Assert.Single(captioned.TimelineCaptions!).Text);
    }
    [Fact]
    public void EventClear_AnnotatedEmptyRetainsTimeDomainAndRoundTrips()
    {
        var plan=new EditorProject { Timeline=[],ClearedEventSelections=[new(){SourceId="source"}],EmptySequenceDuration=5,
            Markers=[3],TimelineCaptions=[new(){Id="cc",Start=.5,End=2,Text="keep"}],EffectAssetId="builtin-chime",EffectAt=1,EffectVolume=.6 };
        EditorRenderService.Validate(plan);
        var restored=EditorProjectCodec.Deserialize(EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(plan,new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        EditorRenderService.Validate(restored);Assert.Equal(5,EditorRenderService.SequenceDuration(restored));
        Assert.Empty(restored.Timeline!);Assert.Equal(plan.Markers,restored.Markers);Assert.Equal(plan.TimelineCaptions,restored.TimelineCaptions);
        Assert.Equal(1,restored.EffectAt);Assert.Equal("builtin-chime",restored.EffectAssetId);Assert.Equal(.6,restored.EffectVolume);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {EmptySequenceDuration=1}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {EmptySequenceDuration=-1}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {EmptySequenceDuration=21601}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {EmptySequenceDuration=double.NaN}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {ClearedEventSelections=null}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan with {Timeline=[new("manual","source",0,5)]}));
    }

}
