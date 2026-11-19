using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

public sealed class JianyingExportServiceTests
{
    [Fact]
    public async Task Export_TransitionPointEightRejectsBeforeScriptOrOutput()
    {
        var output=Path.Combine(Path.GetTempPath(),"JianyingRejected-"+Guid.NewGuid().ToString("N"));
        var project=new EditorProject{Start=0,End=8,Fps=30,Timeline=[new("left","media",0,4),new("right","media",4,8)],
            Transitions=[new("dissolve","right",1,"cross-dissolve",.8,"center")],
            TimelineCaptions=[new(){Id="cue",Start=3.2,End=4.2,Text="cue",SourceClipId="media",SequenceItemId="right"}]};
        var identity=new EditorProjectIdentity("independent","transition",1920,1080,30,DateTimeOffset.UtcNow);
        var before=JsonSerializer.Serialize(project);
        var service=new JianyingExportService(null!,null!,"must-not-run","must-not-run",output);
        await Assert.ThrowsAsync<NotSupportedException>(()=>service.ExportAsync(identity.Id,"transition",project,projectIdentity:identity));
        Assert.False(Directory.Exists(output));Assert.Equal(before,JsonSerializer.Serialize(project));
    }

    [Fact]
    public void Manifest_PreservesSourceMicrosecondsTrackGapsAndMasterVolume()
    {
        var p=new EditorProject{Start=0,End=8,Fps=30,MasterVolume=1.04,BaseAudioMuted=true,
            Timeline=[new("v1","source",1.234567,2.234567,GapBefore:5),new("v3","source",0,2,Track:3),new("a3","source",1,2,Track:6,GapBefore:3,AudioDetached:true)]};
        var before=JsonSerializer.Serialize(p);
        var manifest=JianyingExportService.CreateManifest(p,"source","editable",720,1280,[new("source","final","staged")],[new(.533333333333,1.1,"字幕")]);
        Assert.Equal(1234567,manifest.Segments[0].SourceStartUs);Assert.Equal(5000000,manifest.Segments[0].TargetStartUs);
        Assert.Equal(1000000,manifest.Segments[0].DurationUs);Assert.False(manifest.Segments[0].AudioEnabled);
        Assert.True(manifest.Segments[2].AudioEnabled);Assert.Equal(0,manifest.Segments[1].TargetStartUs);Assert.Equal(3000000,manifest.Segments[2].TargetStartUs);
        Assert.All(manifest.Segments,s=>Assert.Equal(1.04,s.Volume));Assert.Equal(533333,manifest.Captions[0].StartUs);
        Assert.Equal(before,JsonSerializer.Serialize(p));
    }
    [Fact]
    public void Manifest_UsesRoundedAbsoluteBoundariesWithoutAccumulatingMicrosecondError()
    {
        var p=new EditorProject{Start=0,End=3,Timeline=[new("fractional","source",1.00000049,2.00000051,GapBefore:.33333349)]};
        var m=JianyingExportService.CreateManifest(p,"source","precision",720,1280,[new("source","final","staged")],[new(.33333349,.66666651,"微秒")]);
        Assert.Equal(1000000,m.Segments[0].SourceStartUs);Assert.Equal(1000001,m.Segments[0].DurationUs);
        Assert.Equal(2000001,m.Segments[0].SourceStartUs+m.Segments[0].DurationUs);
        Assert.Equal(333333,m.Captions[0].StartUs);Assert.Equal(333334,m.Captions[0].DurationUs);
        Assert.Equal(666667,m.Captions[0].StartUs+m.Captions[0].DurationUs);
    }
    [Fact]
    public void ReviewedThreeRangeAppendPreservesAllFiveFrameAlignedCutsWithoutMicroOverlap()
    {
        var frames=new[]{(5,2812),(5602,9465),(16,136),(166,616),(646,1350)};
        var p=new EditorProject{Start=0,End=320,Fps=30,Timeline=frames.Select((f,i)=>new TimelineItem(i.ToString(),"s",f.Item1/30d,f.Item2/30d)).ToArray()};
        var m=JianyingExportService.CreateManifest(p,"s","review append",1280,720,[new("s","final","staged")],[]);
        long end=0;
        for(int n=0;n<frames.Length;n++)
        {
            var s=m.Segments[n];Assert.Equal(end,s.TargetStartUs);end=s.TargetStartUs+s.DurationUs;
            Assert.Equal(JianyingExportService.Micros(frames[n].Item2/30d),s.SourceStartUs+s.SourceDurationUs);
        }
        Assert.Equal(222333333,m.Segments[2].TargetStartUs);
        Assert.Equal(264800000,end);
    }

    [Fact]
    public void UnsupportedEffectsAndHiddenContentAreExplicitlyRejected()
    {
        var p=new EditorProject{Start=0,End=2,Timeline=[new("v","s",0,2)]};
        Assert.Throws<NotSupportedException>(()=>JianyingExportService.Validate(p with {HiddenTracks=[1]}));
        Assert.Throws<NotSupportedException>(()=>JianyingExportService.Validate(p with {Timeline=[new("v","s",0,2,Filter:"warm")]}));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPython03Export_PublishesIndependentDraftAndPreservesOriginalProject(bool independentCatalog)
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"JianyingCore-"+Guid.NewGuid().ToString("N"))).FullName;
        var ffmpeg=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Links","ffmpeg.exe");
        Assert.True(File.Exists(ffmpeg));var media=new MediaTools(ffmpeg);
        var source=Path.Combine(root,"original.mp4");
        await media.RunAsync(ffmpeg,["-hide_banner","-y","-f","lavfi","-i","color=red:s=160x90:r=30:d=3","-f","lavfi","-i","sine=frequency=1000:sample_rate=48000:duration=3","-c:v","libx264","-c:a","aac","-ac","2","-shortest",source],default);
        var store=new Store(Path.Combine(root,"fixture.db"));store.Migrate();var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var clip=new Clip(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
        var project=new EditorProject{Start=0,End=3,Fps=30,Resolution=720,MasterVolume=1.04,Timeline=[new("v1",clip.Id,1.234567,2.234567,GapBefore:1),new("v3",clip.Id,0,3,Track:3),new("a3",clip.Id,1.234567,2.234567,Track:6)]};
        studio.SaveProject(clip.Id,JsonSerializer.Serialize(project));var before=studio.GetProjectRecord(clip.Id);
        var repo=new DirectoryInfo(AppContext.BaseDirectory);while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"ai-worker","export_jianying_v3.py")))repo=repo.Parent;
        Assert.NotNull(repo);
        var service=new JianyingExportService(store,media,"python",Path.Combine(repo!.FullName,"ai-worker","export_jianying_v3.py"),Path.Combine(root,"exports"));
        var identity=independentCatalog?studio.CreateIndependentProject("portrait",1080,1920,30).Identity:null;
        var result=await service.ExportAsync(identity?.Id??clip.Id,"新建候选",project,projectIdentity:identity);
        Assert.True(File.Exists(Path.Combine(result.DraftPath,"draft_content.json")));Assert.True(File.Exists(Path.Combine(result.DraftPath,"draft_meta_info.json")));
        Assert.False(File.Exists(Path.Combine(result.DraftPath,"draft_info.json")));
        var manifest=JsonSerializer.Deserialize<JianyingManifest>(await File.ReadAllTextAsync(result.ManifestPath),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if(identity is not null){Assert.Equal(1080,manifest.Width);Assert.Equal(1920,manifest.Height);Assert.DoesNotContain(store.Clips(),c=>c.Id==identity.Id);}
        Assert.All(manifest.Media,m=>{Assert.Null(m.StagedPath);Assert.True(File.Exists(m.Path));Assert.NotEqual(source,m.Path);Assert.Equal(File.ReadAllBytes(source),File.ReadAllBytes(m.Path));Assert.NotNull(m.AudioPath);Assert.True(File.Exists(m.AudioPath));Assert.Null(m.AudioStagedPath);});
        using var draft=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.DraftPath,"draft_content.json")));
        var videoMaterials=draft.RootElement.GetProperty("materials").GetProperty("videos").EnumerateArray().ToArray();
        Assert.All(videoMaterials,m=>Assert.True(File.Exists(m.GetProperty("path").GetString())));
        var segments=draft.RootElement.GetProperty("tracks").EnumerateArray().SelectMany(t=>t.GetProperty("segments").EnumerateArray()).ToArray();
        Assert.Contains(segments,s=>s.GetProperty("source_timerange").GetProperty("start").GetInt64()==1234567&&s.GetProperty("target_timerange").GetProperty("start").GetInt64()==1000000);
        Assert.Equal(before,studio.GetProjectRecord(clip.Id));Assert.Single(store.Clips());
        var offsetSource=Path.Combine(root,"offset.mkv");
        await media.RunAsync(ffmpeg,["-hide_banner","-y","-f","lavfi","-i","color=red:s=160x90:r=30:d=3","-f","lavfi","-i","sine=frequency=1000:sample_rate=48000:duration=2","-filter_complex","[1:a]asetpts=PTS+0.2/TB[a]","-map","0:v","-map","[a]","-c:v","ffv1","-c:a","pcm_s16le","-copyts","-avoid_negative_ts","disabled",offsetSource],default);
        var offsetClip=new Clip(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),offsetSource,null,DateTimeOffset.UtcNow);store.SaveClip(offsetClip);
        var offsetProject=new EditorProject{Start=0,End=2,Timeline=[new("audio",offsetClip.Id,.5,1.5,Track:4)]};
        var error=await Assert.ThrowsAsync<NotSupportedException>(()=>service.ExportAsync(offsetClip.Id,"offset rejected",offsetProject));
        Assert.Contains("非零音频起点",error.Message);
        Assert.Single(Directory.GetDirectories(Path.Combine(root,"exports")).Where(d=>!Path.GetFileName(d).StartsWith(".partial-")));
        Assert.Equal(before,studio.GetProjectRecord(clip.Id));

    }
}
