using System.Diagnostics;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

/// <summary>
/// CapCut（剪映）草稿 v2：片段滤镜/运镜/淡入淡出与文字样式要写进草稿的可编辑素材，
/// 并且 ai-worker/export_draft.py 真实跑通 pyJianYingDraft 0.3.0（有 python + ffmpeg 时）。
/// 清单字段名保持小写；v1 清单（没有 filters/animation/texts）仍要能导出、字幕退回 SRT。
/// </summary>
public sealed class DraftV2Tests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public DraftV2Tests(Xunit.Abstractions.ITestOutputHelper output)=>_output=output;
    private static TimelineItem Item(string id,double start,double end,string clipId="source",int track=1,
        double fadeIn=0,double fadeOut=0,string filter="none",string motion="none",
        string? title=null,double titleSize=48,string titleColor="#FFFFFF",double titleX=.5,double titleY=.82)
        =>new(id,clipId,start,end,Track:track,Filter:filter,Motion:motion,FadeIn:fadeIn,FadeOut:fadeOut,
            TitleText:title,TitleSize:titleSize,TitleColor:titleColor,TitleX:titleX,TitleY:titleY);
    private static EditorProject Plan(TimelineItem[] timeline)=>new(){Start=0,End=8,Timeline=timeline,Fps=25};
    private static readonly TranscriptSegment[] Subtitles=[new(.2,1.2,"第一句")];

    [Fact]
    public void Filters_MapTheFourRendererFiltersToDraftKinds()
    {
        Assert.Equal("warm",StudioExportService.BuildFilters(Item("a",0,4,filter:"warm"))!.Single().Kind);
        Assert.Equal("cool",StudioExportService.BuildFilters(Item("a",0,4,filter:"cool"))!.Single().Kind);
        Assert.Equal("mono",StudioExportService.BuildFilters(Item("a",0,4,filter:"mono"))!.Single().Kind);
        Assert.Equal("vivid",StudioExportService.BuildFilters(Item("a",0,4,filter:"vivid"))!.Single().Kind);
        Assert.Null(StudioExportService.BuildFilters(Item("a",0,4)));
        Assert.Equal(100,StudioExportService.BuildFilters(Item("a",0,4,filter:"warm"))!.Single().Intensity,6);
    }

    [Fact]
    public void Animation_CarriesFadesAndMotion_AndIsNullWhenEverythingIsOff()
    {
        var animation=StudioExportService.BuildAnimation(Item("a",0,4,fadeIn:.2,fadeOut:.3,motion:"push"));
        Assert.NotNull(animation);
        Assert.Equal(.2,animation!.FadeIn,6);Assert.Equal(.3,animation.FadeOut,6);Assert.Equal("push",animation.Motion);
        Assert.Equal("shake",StudioExportService.BuildAnimation(Item("a",0,4,motion:"shake"))!.Motion);
        Assert.Null(StudioExportService.BuildAnimation(Item("a",0,4)));
        Assert.Null(StudioExportService.BuildAnimation(Item("a",0,4,motion:"spin")));
    }

    [Fact]
    public void BuildManifest_PutsFiltersAndAnimationOnBasePartsOnly()
    {
        var items=new[]{Item("a",0,2,filter:"warm",fadeIn:.2,fadeOut:.3,motion:"push"),Item("overlay",0,1,track:2)};
        var (parts,_)=StudioExportService.BuildManifest(Plan(items),"source");
        Assert.Equal(new[]{"part-000.mkv","overlay-000.mkv"},parts.Select(x=>x.File));
        Assert.Equal("warm",parts[0].Filters!.Single().Kind);
        Assert.Equal(.2,parts[0].Animation!.FadeIn,6);
        Assert.Equal("push",parts[0].Animation!.Motion);
        Assert.Null(parts[1].Filters);
        Assert.Null(parts[1].Animation);
    }

    [Fact]
    public void ManifestParts_SerializeExtrasWithTheLowercaseKeysTheExporterReads_AndOmitAbsentOnes()
    {
        var items=new[]{Item("a",0,2,filter:"warm",fadeIn:.2,fadeOut:.3,motion:"push"),Item("b",2,4)};
        var (parts,_)=StudioExportService.BuildManifest(Plan(items),"source");
        var json=JsonSerializer.Serialize(parts);
        using var document=JsonDocument.Parse(json);
        var withExtras=document.RootElement[0];
        Assert.Equal("warm",withExtras.GetProperty("filters")[0].GetProperty("kind").GetString());
        Assert.Equal(100,withExtras.GetProperty("filters")[0].GetProperty("intensity").GetDouble(),6);
        Assert.Equal(.2,withExtras.GetProperty("animation").GetProperty("fadeIn").GetDouble(),6);
        Assert.Equal(.3,withExtras.GetProperty("animation").GetProperty("fadeOut").GetDouble(),6);
        Assert.Equal("push",withExtras.GetProperty("animation").GetProperty("motion").GetString());
        var plain=document.RootElement[1];
        Assert.False(plain.TryGetProperty("filters",out _));
        Assert.False(plain.TryGetProperty("animation",out _));
    }

    [Fact]
    public void BuildTexts_UsesSubtitleStyleAndTitlePositionForEditableText()
    {
        var items=new[]{Item("a",0,2,title:"开场",titleSize:64,titleColor:"#ffd166",titleX:.5,titleY:.2)};
        var texts=StudioExportService.BuildTexts(Plan(items),"source",Subtitles);
        Assert.Equal(2,texts.Length);
        var subtitle=texts[0];
        Assert.Equal("第一句",subtitle.Text);
        Assert.Equal(.2,subtitle.Start,6);Assert.Equal(1,subtitle.Duration,6);
        Assert.Equal(0,subtitle.TransformX,6);Assert.Equal(-.72,subtitle.TransformY,6);
        Assert.Equal("Microsoft YaHei",subtitle.Style.Font);
        Assert.Equal(11,subtitle.Style.Size,6);
        Assert.Equal("#FFFFFF",subtitle.Style.Color);
        Assert.Equal("bottom",subtitle.Style.Position);
        var title=texts[1];
        Assert.Equal("开场",title.Text);
        Assert.Equal(0,title.Start,6);Assert.Equal(2,title.Duration,6);
        Assert.Equal(0,title.TransformX,6);Assert.Equal(.6,title.TransformY,6);
        Assert.Equal(16,title.Style.Size,6);
        Assert.Equal("#FFD166",title.Style.Color);
    }

    [Fact]
    public void BuildTexts_HonorsTopPositionAndSkipsBlankOrNegativeCues()
    {
        var plan=Plan([Item("a",0,2)]) with {SubtitlePosition="top",SubtitleColor="#ff0000",SubtitleOutline=1.5};
        var texts=StudioExportService.BuildTexts(plan,"source",[new(0,1,"顶部"),new(2,2,"零长"),new(3,4,"  ")]);
        var only=Assert.Single(texts);
        Assert.Equal(.72,only.TransformY,6);
        Assert.Equal("top",only.Style.Position);
        Assert.Equal("#FF0000",only.Style.Color);
        Assert.Equal(1.5,only.Style.Outline,6);
    }

    [Fact]
    public void BuildTexts_FallsBackToWhiteWhenColorIsInvalid()
    {
        var plan=Plan([Item("a",0,2)]) with {SubtitleColor="red"};
        var texts=StudioExportService.BuildTexts(plan,"source",Subtitles);
        Assert.Equal("#FFFFFF",Assert.Single(texts).Style.Color);
    }

    // ---- 真实导出脚本（有 python + pyJianYingDraft + ffmpeg 时运行，否则跳过） ----
    [Fact]
    public async Task PythonExporter_WritesFiltersAnimationsFadesAndStyledTextsIntoDraft()
    {
        var (python,script,ffmpeg)=ResolveExportTools();
        if(python is null||script is null||ffmpeg is null){_output.WriteLine("[skip] 未找到 python / 导出脚本 / ffmpeg（或 python 无法 import pyJianYingDraft），剪映草稿 V2 导出测试跳过");return;}
        var root=Directory.CreateTempSubdirectory("lc-draft-v2-");
        try
        {
            var clipPath=Path.Combine(root.FullName,"clip.mp4");
            await RunAsync(ffmpeg,["-hide_banner","-loglevel","error","-y",
                "-f","lavfi","-i","testsrc2=size=640x360:rate=25:duration=2",
                "-f","lavfi","-i","sine=frequency=440:duration=2","-shortest","-pix_fmt","yuv420p",clipPath],root.FullName);
            var items=new[]{Item("a",0,2,filter:"warm",fadeIn:.2,fadeOut:.3,motion:"push",
                title:"开场",titleSize:64,titleColor:"#FFD166",titleX:.5,titleY:.2),Item("b",2,3.5)};
            var plan=Plan(items) with {SubtitleColor="#FF8000",SubtitleOutline=1.5};
            var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
            for(var index=0;index<parts.Length;index++)File.Copy(clipPath,Path.Combine(root.FullName,parts[index].File));
            var texts=StudioExportService.BuildTexts(plan,"source",Subtitles);
            var manifest=new{schemaVersion=1,draftVersion=2,application="LiveClipper",
                draftName="LiveClipper-draft-v2-test",width=640,height=360,fps=25,duration,parts,texts,
                assets=new Dictionary<string,string>(),project=plan};
            var manifestPath=Path.Combine(root.FullName,"project.json");
            await File.WriteAllTextAsync(manifestPath,JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(root.FullName,"subtitles.srt"),"");
            var result=await RunAsync(python,[script,"--manifest",manifestPath],root.FullName);
            Assert.True(result.ExitCode==0,$"export_draft.py 失败: {result.Stderr}\n{result.Stdout}");
            var summary=JsonDocument.Parse(result.Stdout.Trim().Split('\n')[^1]);
            Assert.Equal(1,summary.RootElement.GetProperty("filters").GetInt32());
            Assert.Equal(1,summary.RootElement.GetProperty("fades").GetInt32());
            Assert.Equal(1,summary.RootElement.GetProperty("animations").GetInt32());
            Assert.Equal(2,summary.RootElement.GetProperty("texts").GetInt32());
            Assert.Equal("styled",summary.RootElement.GetProperty("textMode").GetString());
            var draft=LoadDraftContent(root.FullName,"LiveClipper-draft-v2-test");
            var materials=draft.RootElement.GetProperty("materials");
            Assert.Contains(materials.GetProperty("effects").EnumerateArray(),
                effect=>effect.GetProperty("name").GetString()=="暖黄"&&Math.Abs(effect.GetProperty("value").GetDouble()-1.0)<1e-6);
            var animations=materials.GetProperty("material_animations")[0].GetProperty("animations")[0];
            Assert.Equal("放大",animations.GetProperty("name").GetString());
            Assert.Equal("in",animations.GetProperty("type").GetString());
            Assert.Equal(600000,animations.GetProperty("duration").GetInt64());
            var fade=materials.GetProperty("audio_fades")[0];
            Assert.Equal(200000,fade.GetProperty("fade_in_duration").GetInt64());
            Assert.Equal(300000,fade.GetProperty("fade_out_duration").GetInt64());
            var textMaterials=materials.GetProperty("texts").EnumerateArray().ToArray();
            var subtitleMaterial=textMaterials.Single(x=>x.GetProperty("content").GetString()!.Contains("第一句"));
            using var content=JsonDocument.Parse(subtitleMaterial.GetProperty("content").GetString()!);
            var style=content.RootElement.GetProperty("styles")[0];
            Assert.Equal(11,style.GetProperty("size").GetDouble(),6);
            var color=style.GetProperty("fill").GetProperty("content").GetProperty("solid").GetProperty("color").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
            Assert.Equal(1,color[0],6);Assert.Equal(.5019608,color[1],6);Assert.Equal(0,color[2],6);
            Assert.Equal(.08,style.GetProperty("strokes")[0].GetProperty("width").GetDouble(),4);
            Assert.Equal(1,subtitleMaterial.GetProperty("alignment").GetInt32());
            var tracks=draft.RootElement.GetProperty("tracks").EnumerateArray().ToArray();
            Assert.Contains(tracks,track=>track.GetProperty("type").GetString()=="video");
            // 字幕与标题分两条文字轨道，避免同轨时间重叠被 pyd 拒绝。
            var textTracks=tracks.Where(track=>track.GetProperty("type").GetString()=="text").ToArray();
            Assert.Equal(2,textTracks.Length);
            static double TextTransformY(JsonElement track)=>track.GetProperty("segments")[0]
                .GetProperty("clip").GetProperty("transform").GetProperty("y").GetDouble();
            var subtitleTrack=Assert.Single(textTracks,track=>Math.Abs(TextTransformY(track)+.72)<1e-6);
            Assert.Equal(1,subtitleTrack.GetProperty("segments").GetArrayLength());
            Assert.Contains(textTracks,track=>Math.Abs(TextTransformY(track)-.6)<1e-6);
        }
        finally
        {
            try{root.Delete(true);}catch(IOException){}
        }
    }

    [Fact]
    public async Task PythonExporter_OldV1Manifest_StillFallsBackToSrtSubtitleImport()
    {
        var (python,script,ffmpeg)=ResolveExportTools();
        if(python is null||script is null||ffmpeg is null){_output.WriteLine("[skip] 未找到 python / 导出脚本 / ffmpeg（或 python 无法 import pyJianYingDraft），剪映草稿 V1 回退测试跳过");return;}
        var root=Directory.CreateTempSubdirectory("lc-draft-v1-");
        try
        {
            var clipPath=Path.Combine(root.FullName,"clip.mp4");
            await RunAsync(ffmpeg,["-hide_banner","-loglevel","error","-y",
                "-f","lavfi","-i","testsrc2=size=640x360:rate=25:duration=2",
                "-f","lavfi","-i","sine=frequency=440:duration=2","-shortest","-pix_fmt","yuv420p",clipPath],root.FullName);
            var plan=Plan([Item("a",0,2)]);
            var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
            for(var index=0;index<parts.Length;index++)File.Copy(clipPath,Path.Combine(root.FullName,parts[index].File));
            // v1 清单：没有 draftVersion/texts，part 也没有 filters/animation。
            var manifest=new{schemaVersion=1,application="LiveClipper",draftName="LiveClipper-draft-v1-test",
                width=640,height=360,fps=25,duration,parts,assets=new Dictionary<string,string>(),project=plan};
            var manifestPath=Path.Combine(root.FullName,"project.json");
            await File.WriteAllTextAsync(manifestPath,JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(root.FullName,"subtitles.srt"),
                "1\n00:00:00,100 --> 00:00:01,000\n旧字幕\n\n");
            var result=await RunAsync(python,[script,"--manifest",manifestPath],root.FullName);
            Assert.True(result.ExitCode==0,$"export_draft.py 失败: {result.Stderr}\n{result.Stdout}");
            var summary=JsonDocument.Parse(result.Stdout.Trim().Split('\n')[^1]);
            Assert.Equal("srt",summary.RootElement.GetProperty("textMode").GetString());
            Assert.Equal(0,summary.RootElement.GetProperty("filters").GetInt32());
            var draft=LoadDraftContent(root.FullName,"LiveClipper-draft-v1-test");
            Assert.Contains(draft.RootElement.GetProperty("materials").GetProperty("texts").EnumerateArray(),
                text=>text.GetProperty("content").GetString()!.Contains("旧字幕"));
        }
        finally
        {
            try{root.Delete(true);}catch(IOException){}
        }
    }

    private static JsonDocument LoadDraftContent(string root,string draftName)
        =>JsonDocument.Parse(File.ReadAllText(Path.Combine(root,draftName,"draft_content.json")));

    /// <summary>找 python/脚本/ffmpeg：任一缺失就跳过集成测试（与 AudioEnvelopeTests 的环境约定一致）。</summary>
    private static (string? Python,string? Script,string? Ffmpeg) ResolveExportTools()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"ai-worker","export_draft.py")))root=root.Parent;
        if(root is null)return (null,null,null);
        var script=Path.Combine(root.FullName,"ai-worker","export_draft.py");
        var python=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_PYTHON");
        if(string.IsNullOrWhiteSpace(python))
        {
            var venv=Path.Combine(root.FullName,".venv","Scripts","python.exe");
            python=File.Exists(venv)?venv:"python";
        }
        var ffmpeg=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(string.IsNullOrWhiteSpace(ffmpeg))
        {
            var local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","tools","ffmpeg","bin","ffmpeg.exe");
            ffmpeg=File.Exists(local)?local:"ffmpeg";
        }
        try
        {
            using var probe=Process.Start(new ProcessStartInfo(python,"-c \"import pyJianYingDraft\""){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true});
            probe!.WaitForExit(30000);
            if(probe.ExitCode!=0)return (null,null,null);
        }
        catch{return (null,null,null);}
        if(!File.Exists(ffmpeg)&&ffmpeg=="ffmpeg")return (null,null,null);
        return (python,script,ffmpeg);
    }

    private static async Task<(int ExitCode,string Stdout,string Stderr)> RunAsync(string file,IEnumerable<string> arguments,string workingDirectory)
    {
        var start=new ProcessStartInfo(file){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=workingDirectory};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new InvalidOperationException($"无法启动 {file}");
        var stdout=await process.StandardOutput.ReadToEndAsync();
        var stderr=await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode,stdout,stderr);
    }
}
