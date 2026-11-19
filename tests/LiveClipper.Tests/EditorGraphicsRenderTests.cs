using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit;

namespace LiveClipper.Tests;

public sealed class EditorGraphicsRenderTests
{
    private static TimelineItem Graphic(string kind="box")=>new("graphic","",0,1,"graphic",Track:3,TitleText:"Graphics",TitleSize:36,TitleColor:"#FFFFFF",TitleX:.5,TitleY:.5)
    {GraphicKind=kind,GraphicBoxColor="#FF0000",GraphicBoxOpacity=.5,GraphicBoxX=.1,GraphicBoxY=.1,GraphicBoxW=.5,GraphicBoxH=.5,GraphicLineThickness=40,PreviewOnly=false};

    [Fact]
    public void Build_BoxUsesFourInsideRectsBeforeTextAndHalfOpenTiming()
    {
        var graph=EditorProjectGraphics.Build(Graphic(),320,180,2,3);
        Assert.Equal(4,graph.Split("drawbox=").Length-1);
        Assert.Contains("drawbox=x=32:y=18:w=160:h=10:color=#FF0000@0.5:t=fill",graph);
        Assert.Contains("drawbox=x=32:y=98:w=160:h=10",graph);
        Assert.Contains("drawbox=x=32:y=28:w=10:h=70",graph);
        Assert.Contains("drawbox=x=182:y=28:w=10:h=70",graph);
        Assert.True(graph.LastIndexOf("drawbox=",StringComparison.Ordinal)<graph.IndexOf("drawtext=",StringComparison.Ordinal));
        // Slash conversion must precede colon escaping, or the escape's own backslash would be turned into a slash.
        var fontDir=Environment.GetFolderPath(Environment.SpecialFolder.Fonts).Replace('\\','/').Replace(":","\\:");
        Assert.Contains($"fontfile='{fontDir}/msyh.ttc'",graph,StringComparison.Ordinal);
        Assert.Contains("fontsize=12:x=w*0.5-text_w/2:y=h*0.5-text_h/2:shadowcolor=black@0.65:shadowx=2:shadowy=2",graph);
        Assert.Contains("enable='gte(t,2)*lt(t,3)'",graph);
    }

    [Theory]
    [InlineData("title",0)]
    [InlineData("lowerThirdBlock",1)]
    [InlineData("line",1)]
    public void Build_AllExportableKindsUseContractPrimitives(string kind,int boxes)
    {
        var item=Graphic(kind) with {GraphicBoxW=3.0/320,GraphicLineThickness=40};
        var graph=EditorProjectGraphics.Build(item,320,180,0,1,.5);
        Assert.Equal(boxes,graph.Split("drawbox=").Length-1);
        if(boxes>0)Assert.Contains("w=3:h=90:color=#FF0000@0.25:t=fill",graph);
        Assert.Contains("fontcolor=#FFFFFF@0.5",graph);
    }

    public static IEnumerable<object[]> InvalidItems()
    {
        yield return [Graphic() with {GraphicKind="unknown"},"invalid_graphic_kind"];
        yield return [Graphic() with {Track=2},"invalid_graphic_track"];
        yield return [Graphic() with {ClipId="media"},"invalid_graphic_source"];
        yield return [Graphic() with {Speed=2},"invalid_graphic_timing"];
        yield return [Graphic() with {TitleText=new string('x',121)},"invalid_graphic_title_text"];
        yield return [Graphic() with {TitleText="a\nb"},"invalid_graphic_title_text"];
        yield return [Graphic() with {TitleSize=161},"invalid_graphic_title_size"];
        yield return [Graphic() with {TitleColor="red"},"invalid_graphic_title_color"];
        yield return [Graphic() with {TitleX=double.NaN},"invalid_graphic_title_position"];
        yield return [Graphic() with {GraphicBoxColor="#00000000"},"invalid_graphic_box_color"];
        yield return [Graphic() with {GraphicBoxOpacity=1.01},"invalid_graphic_box_opacity"];
        yield return [Graphic() with {GraphicBoxX=-.1},"invalid_graphic_box_position"];
        yield return [Graphic() with {GraphicBoxW=double.PositiveInfinity},"invalid_graphic_box_size"];
        yield return [Graphic() with {GraphicBoxW=.95},"invalid_graphic_box_bounds"];
        yield return [Graphic() with {GraphicLineThickness=41},"invalid_graphic_line_thickness"];
        yield return [Graphic() with {PreviewOnly=true},"invalid_graphic_preview_only"];
        yield return [Graphic() with {GraphicKind="callout-pointer",PreviewOnly=true},"invalid_graphic_preview_only"];
        yield return [Graphic() with {GraphicKind="end-credits",PreviewOnly=true},"invalid_graphic_preview_only"];
    }
    [Theory,MemberData(nameof(InvalidItems))]
    public void Validate_RejectsInvalidGraphicsWithSpecificCode(TimelineItem item,string code)
    {
        Assert.Equal(code,Assert.Throws<ArgumentException>(()=>EditorProjectGraphics.Validate(item)).Message);
        Assert.Equal(code,Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(new EditorProject {Timeline=[item]})).Message);
    }

    [Fact]
    public void Codec_RoundtripsGraphicsAndAppliesOnlyExplicitDefaults()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var project=EditorProjectCodec.Deserialize(JsonSerializer.Serialize(new EditorProject{Timeline=[Graphic()]},options));
        Assert.Equal(Graphic(),Assert.Single(project.Timeline!));
        var defaults=EditorProjectCodec.Deserialize("""{"timeline":[{"id":"g","clipId":"","start":0,"end":1,"kind":"graphic","track":3,"graphicKind":"title"}]}""");
        Assert.Equal("标题",defaults.Timeline![0].TitleText);
        Assert.Equal("#E6E6E6",defaults.Timeline[0].TitleColor);
        Assert.Equal(.5,defaults.Timeline[0].TitleY);
        EditorRenderService.Validate(defaults);
        var legacy=EditorProjectCodec.Deserialize("""{"timeline":[{"id":"a","clipId":"clip","start":0,"end":1}]}""");
        var old=Assert.Single(legacy.Timeline!);
        Assert.Null(old.GraphicKind);Assert.Equal(.82,old.TitleY);Assert.Equal("#FFFFFF",old.TitleColor);
        var json=JsonSerializer.Serialize(old,options);
        Assert.DoesNotContain("graphic",json);Assert.DoesNotContain("previewOnly",json);
        Assert.Equal("scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30",EditorRenderService.VisualFilter(old,640,360,30));
    }

    [Theory]
    [InlineData("Microsoft YaHei")]
    [InlineData("Segoe UI")]
    [InlineData("Arial")]
    public void Codec_TitleFontRoundTripsForContractFontsOnPlainTitleAndGraphic(string font)
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plain=new TimelineItem("title","clip",0,1,Track:3,TitleText:"Legacy"){TitleFont=font};
        var graphic=Graphic("title") with {TitleFont=font};
        foreach(var item in new[]{plain,graphic})
        {
            EditorProjectGraphics.Validate(item);
            var json=JsonSerializer.Serialize(new EditorProject{Timeline=[item]},options);
            using var document=JsonDocument.Parse(json);
            Assert.Equal(font,document.RootElement.GetProperty("timeline")[0].GetProperty("titleFont").GetString());
            var restored=EditorProjectCodec.Deserialize(json);
            Assert.Equal(item,Assert.Single(restored.Timeline!));
            EditorRenderService.Validate(restored);
        }
    }
    [Fact]
    public void Codec_TitleFontDefaultAndExplicitNullStayAbsentFromWebJson()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach(var item in new TimelineItem[]{new("title","clip",0,1,Track:3,TitleText:"Legacy"),Graphic("title")})
        {
            var json=JsonSerializer.Serialize(new EditorProject{Timeline=[item]},options);
            using var document=JsonDocument.Parse(json);
            Assert.False(document.RootElement.GetProperty("timeline")[0].TryGetProperty("titleFont",out _));
            Assert.Null(EditorProjectCodec.Deserialize(json).Timeline![0].TitleFont);
        }
        var plainNull=EditorProjectCodec.Deserialize("""{"timeline":[{"id":"title","clipId":"clip","start":0,"end":1,"track":3,"titleText":"Legacy","titleFont":null}]}""");
        Assert.Null(plainNull.Timeline![0].TitleFont);
        var plainJson=JsonSerializer.Serialize(plainNull,options);
        using var plainDocument=JsonDocument.Parse(plainJson);
        Assert.False(plainDocument.RootElement.GetProperty("timeline")[0].TryGetProperty("titleFont",out _));
        var plainAgain=EditorProjectCodec.Deserialize(plainJson);
        Assert.Null(plainAgain.Timeline![0].TitleFont);EditorRenderService.Validate(plainAgain);
        var graphicNull=EditorProjectCodec.Deserialize("""{"timeline":[{"id":"g","clipId":"","start":0,"end":1,"kind":"graphic","track":3,"graphicKind":"title","titleFont":null}]}""");
        Assert.Null(graphicNull.Timeline![0].TitleFont);
        var graphicJson=JsonSerializer.Serialize(graphicNull,options);
        using var graphicDocument=JsonDocument.Parse(graphicJson);
        Assert.False(graphicDocument.RootElement.GetProperty("timeline")[0].TryGetProperty("titleFont",out _));
        var graphicAgain=EditorProjectCodec.Deserialize(graphicJson);
        Assert.Null(graphicAgain.Timeline![0].TitleFont);EditorRenderService.Validate(graphicAgain);
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Arial ")]
    [InlineData("arial")]
    [InlineData("ARIAL")]
    [InlineData("Arial, sans-serif")]
    [InlineData(@"C:\Windows\Fonts\arial.ttf")]
    [InlineData("123")]
    public void Validate_RejectsIllegalTitleFontsAndOnlyGraphicCodecRaisesSchemaCode(string font)
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plain=new TimelineItem("title","clip",0,1,Track:3,TitleText:"Legacy"){TitleFont=font};
        var graphic=Graphic("title") with {TitleFont=font};
        Assert.Equal("invalid_title_font",Assert.Throws<ArgumentException>(()=>EditorProjectGraphics.Validate(plain)).Message);
        Assert.Equal("invalid_title_font",Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(new EditorProject{Timeline=[plain]})).Message);
        Assert.Equal("invalid_title_font",Assert.Throws<ArgumentException>(()=>EditorProjectGraphics.Validate(graphic)).Message);
        Assert.Equal("invalid_title_font",Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(new EditorProject{Timeline=[graphic]})).Message);
        Assert.Equal("invalid_title_font",Assert.Throws<EditorProjectSchemaException>(()=>EditorProjectCodec.Deserialize(JsonSerializer.Serialize(new EditorProject{Timeline=[graphic]},options))).Code);
        var plainProject=EditorProjectCodec.Deserialize(JsonSerializer.Serialize(new EditorProject{Timeline=[plain]},options));
        Assert.Equal("invalid_title_font",Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plainProject)).Message);
    }
    [Fact]
    public void Codec_RejectsNumericTitleFontAsTypeMismatch()
    {
        Assert.Throws<JsonException>(()=>EditorProjectCodec.Deserialize("""{"timeline":[{"id":"title","clipId":"clip","start":0,"end":1,"track":3,"titleText":"Legacy","titleFont":123}]}"""));
        var ex=Assert.Throws<EditorProjectSchemaException>(()=>EditorProjectCodec.Deserialize("""{"timeline":[{"id":"g","clipId":"","start":0,"end":1,"kind":"graphic","track":3,"graphicKind":"title","titleFont":123}]}"""));
        Assert.Equal("invalid_graphic_titlefont_type",ex.Code);
    }

    [Theory]
    [InlineData("\"graphicBoxOpacity\":\"0.5\"")]
    [InlineData("\"graphicBoxX\":null")]
    [InlineData("\"graphicBoxW\":0.2,\"GraphicBoxW\":0.3")]
    [InlineData("\"previewOnly\":\"false\"")]
    public void Codec_RejectsWrongTypesAndDuplicateKeys(string field)
    {
        var json="{\"timeline\":[{\"id\":\"g\",\"clipId\":\"\",\"start\":0,\"end\":1,\"kind\":\"graphic\",\"track\":3,\"graphicKind\":\"box\","+field+"}]}";
        Assert.StartsWith("invalid_graphic_",Assert.Throws<EditorProjectSchemaException>(()=>EditorProjectCodec.Deserialize(json)).Message);
    }

    [Fact]
    public void Codec_ValidatesBackupAndNestedGraphicsAndPreservesLegacyTitle()
    {
        const string invalid="""{"id":"g","clipId":"","start":0,"end":1,"kind":"graphic","track":3,"graphicKind":"box","graphicBoxOpacity":2}""";
        foreach(var json in new[]{"{\"silenceTrimBackup\":["+invalid+"]}","{\"nestedSequences\":{\"s\":{\"id\":\"s\",\"name\":\"S\",\"timeline\":["+invalid+"]}}}"})
            Assert.Equal("invalid_graphic_box_opacity",Assert.Throws<EditorProjectSchemaException>(()=>EditorProjectCodec.Deserialize(json)).Code);
        var old=new TimelineItem("old","clip",0,1,Track:3,TitleText:"Legacy");
        var filter=EditorRenderService.VisualFilter(old,1280,720,30);
        Assert.Contains("text='Legacy':fontcolor=#FFFFFF:fontsize=48:x=w*0.5-text_w/2:y=h*0.82-text_h/2:shadowcolor=black@0.65:shadowx=2:shadowy=2",filter);
        Assert.DoesNotContain("drawbox=",filter);Assert.DoesNotContain("enable=",filter);
    }

    [Fact]
    public void Graphics_DoNotParticipateInTransitions()
    {
        var left=Graphic() with {Id="left"};var right=Graphic("title") with {Id="right"};
        Assert.Empty(EditorRenderService.ResolveTransitions([left,right],[new("cut","right",3,"cross-dissolve",.2,"center")],30));
    }

    [Fact]
    public async Task RealFfmpeg_SourceFreeBoxAndTitleBlendOverTestsrc()
    {
        var executable=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(string.IsNullOrWhiteSpace(executable))return; // Explicit opt-in; never used as evidence unless enabled.
        var tools=new MediaTools(executable);Assert.True(tools.Available());
        Assert.True(File.Exists(@"C:\Windows\Fonts\msyh.ttc"),"Graphics render requires the contract font msyh.ttc");
        var root=Path.Combine(Path.GetTempPath(),"LiveClipper-graphics-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var renderer=new EditorRenderService(store,studio,tools,root);
        try
        {
            var source=Path.Combine(root,"source.mp4");
            await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc=size=320x180:rate=30","-f","lavfi","-i","anullsrc=r=48000:cl=stereo","-t","1","-c:v","libx264","-pix_fmt","yuv420p","-c:a","aac",source],CancellationToken.None);
            var clip=new Clip("clip","graphics-event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            var media=new TimelineItem("base","clip",0,1);
            async Task<string> Render(TimelineItem[] items,string name)
            {
                var result=await renderer.AssembleAsync(clip,new EditorProject{Timeline=items,End=1,Resolution=0,Fps=30},Path.Combine(root,name),CancellationToken.None);
                Assert.InRange(await tools.DurationAsync(result.Video,CancellationToken.None),.95,1.1);return result.Video;
            }
            var baseline=await Render([media],"baseline");
            // One box item includes text; text center is outside all sampled stroke pixels.
            var graphic=Graphic() with {TitleText="R81",TitleX=.8,TitleY=.8};
            var frozen=renderer.Snapshot(clip,new EditorProject{Timeline=[media,graphic],End=1});
            Assert.Single(frozen.SourceTranscripts!);Assert.False(frozen.SourceTranscripts!.ContainsKey(""));
            var actual=await Render([media,graphic],"graphics");
            async Task<byte[]> Frame(string path,string name)
            {
                var raw=Path.Combine(root,name+".rgb");
                await tools.RunAsync(tools.Ffmpeg,["-hide_banner","-loglevel","error","-y","-i",path,"-vf","select=eq(n\\,15)","-frames:v","1","-pix_fmt","rgb24","-f","rawvideo",raw],CancellationToken.None);
                var bytes=await File.ReadAllBytesAsync(raw);Assert.Equal(320*180*3,bytes.Length);return bytes;
            }
            var before=await Frame(baseline,"before");var after=await Frame(actual,"after");
            // Interior of top stroke, clear of chroma boundaries and antialiased text.
            var sample=(22*320+94)*3;
            Assert.True(Math.Max(Math.Abs(before[sample]-255),Math.Max(before[sample+1],before[sample+2]))>80,"Sample must differ clearly from the red overlay colour: otherwise alpha blending is unobservable");
            // This expectation is the product contract (box opacity 0.5 = half-over compositing) and must not be
            // relaxed. It failed before R81 FIXP: EditorProjectGraphics.cs:136 emits
            // drawbox=...:color=#FF0000@0.5:t=fill and ffmpeg ignores the colour alpha in fill mode on yuv pixel
            // formats, so the box landed ~88% opaque red with colour fringing -- sample (94,22) became (255,12,26)
            // instead of the 50% blend (254,0,126) of before=(254,0,253); on a flat grey 128 source the same drawbox
            // yielded (255,30,28) instead of (191,64,64). Fixed by wrapping the graphics chain in format=rgba ...
            // format=yuv420p at the composition site (EditorRenderService.AssembleAsync), where real ffmpeg 9.0.2
            // now returns (191,63,63) on grey, i.e. the exact 50% blend.
            for(var y=21;y<25;y++)for(var x=90;x<98;x++)for(var c=0;c<3;c++)
            {
                var index=(y*320+x)*3+c;var expected=.5*before[index]+.5*(c==0?255:0);
                Assert.InRange((double)after[index],expected-18,expected+18);
            }
            // Hollow interior and pixels outside the box retain the timeline image.
            foreach(var (x,y) in new[]{(50,50),(12,12)})for(var c=0;c<3;c++)
            {
                var index=(y*320+x)*3+c;Assert.InRange((int)after[index],before[index]-12,before[index]+12);
            }
            // No Clip record/media file is needed for a graphics-only canvas.
            var missing=new Clip("missing","missing",Path.Combine(root,"does-not-exist.mp4"),null,DateTimeOffset.UtcNow);
            var only=await renderer.AssembleAsync(missing,new EditorProject{Timeline=[graphic],End=1,Resolution=480,Fps=30},Path.Combine(root,"only"),CancellationToken.None);
            Assert.InRange(await tools.DurationAsync(only.Video,CancellationToken.None),.95,1.1);
        }
        finally {await renderer.StopAsync();try{Directory.Delete(root,true);}catch(IOException){}}
    }
}
