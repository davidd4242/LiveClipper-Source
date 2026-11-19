using System.Reflection;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Tests;

public sealed class IndependentEditorRenderTests : IDisposable
{
    private readonly string root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"lc-independent-render-"+Guid.NewGuid().ToString("N"))).FullName;
    private readonly Store store;
    private readonly StudioStore studio;
    private readonly MediaTools media=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Links","ffmpeg.exe"));
    public IndependentEditorRenderTests(){store=new(Path.Combine(root,"test.db"));store.Migrate();studio=new(store.DatabasePath);studio.Migrate();}
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    private EditorRenderService Service()=>new(store,studio,media,root,()=>Path.Combine(root,"work"),()=>Path.Combine(root,"output"));
    private async Task<Clip> Source(string id,string color)
    {
        var path=Path.Combine(root,id+".mp4");
        await media.RunAsync(media.Ffmpeg,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i",$"color=c={color}:s=320x180:r=25:d=1","-f","lavfi","-i","sine=frequency=440:duration=1","-c:v","libx264","-pix_fmt","yuv420p","-c:a","aac","-shortest",path],CancellationToken.None);
        var clip=new Clip(id,"event-"+id,path,null,DateTimeOffset.UtcNow);store.SaveClip(clip);return clip;
    }
    private async Task<Job> Finished(string id)
    {
        for(var i=0;i<600;i++)
        {
            var job=store.Jobs().Single(j=>j.Id==id);
            if(job.Status is "completed" or "failed" or "cancelled")return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Render did not finish");
    }
    [Fact]
    public async Task QueuedIndependentRenderUsesRealSourcesCatalogCanvasAndBurnedCaptions()
    {
        var a=await Source("a","red");var b=await Source("b","blue");
        var identity=studio.CreateIndependentProject("portrait",180,320,25).Identity;
        var plan=new EditorProject{Fps=25,Resolution=720,Timeline=[new("a",a.Id,0,1),new("b",b.Id,0,1)],BurnSubtitles=true,
            TimelineCaptions=[new(){Id="cue",Start=.1,End=.8,Text="REAL CAPTION",Size=80,PositionX=.5,PositionY=.5}]};
        var service=Service();
        try
        {
            var render=service.Queue(identity,plan,7,"epoch");var job=await Finished(render.Id);
            Assert.True(job.Status=="completed",job.Error);Assert.Equal("",job.EventId);Assert.Equal("",render.ClipId);Assert.Equal(identity.Id,render.ProjectId);
            var persisted=new StudioStore(store.DatabasePath).Renders().Single();Assert.Equal(identity,persisted.ProjectIdentity);Assert.Equal(7,persisted.Revision);
            var info=await media.ProbeAsync(render.OutputPath,CancellationToken.None);Assert.Equal(180,info.Width);Assert.Equal(320,info.Height);Assert.Equal(25,info.Fps);
            Assert.Contains("REAL CAPTION",File.ReadAllText(Path.ChangeExtension(render.OutputPath,".srt")));
            Assert.Equal(2,store.Clips().Count);Assert.DoesNotContain(store.Clips(),c=>c.Id==identity.Id);
            Assert.Empty(EditorProjectCodec.Deserialize(studio.GetIndependentProject(identity.Id)!.Document.Json).Timeline!);
            // Decode a subtitle frame: the white pixels are in the MP4 itself, not a sidecar renderer.
            var pixels=Path.Combine(root,"pixels.rgb");await media.RunAsync(media.Ffmpeg,["-hide_banner","-loglevel","error","-y","-ss","0.4","-i",render.OutputPath,"-frames:v","1","-f","rawvideo","-pix_fmt","rgb24",pixels],CancellationToken.None);
            var rgb=File.ReadAllBytes(pixels);Assert.True(Enumerable.Range(0,rgb.Length/3).Count(i=>rgb[i*3]>200&&rgb[i*3+1]>200&&rgb[i*3+2]>200)>10);
        }
        finally{await service.StopAsync();}
    }
    [Theory]
    [InlineData("overlap",20,10)]
    [InlineData("preserve",30,15)]
    public async Task IndependentTransitionQueueProducesMixedFramesAndAudio(string layout,int frameCount,int middle)
    {
        var a=await Source("red","red");var b=await Source("blue","blue");
        var identity=studio.CreateIndependentProject("transition",320,180,25).Identity;
        var absolute=layout=="preserve";
        var plan=new EditorProject{Fps=25,Timeline=[new("a",a.Id,.2,.8,SequenceStart:absolute?0:null),new("b",b.Id,.2,.8,SequenceStart:absolute?.6:null)],
            Transitions=[new("cut","b",1,"cross-dissolve",.4,"center",layout)],TimelineCaptions=[new(){Id="cue",Start=.04,End=.16,Text="CHECK"}]};
        var service=Service();
        try
        {
            var render=service.Queue(identity,plan);var job=await Finished(render.Id);
            Assert.True(job.Status=="completed",job.Error);
            var frameFile=Path.Combine(root,"frames.rgb");
            await media.RunAsync(media.Ffmpeg,["-v","error","-y","-i",render.OutputPath,"-vf","scale=1:1","-fps_mode","passthrough","-pix_fmt","rgb24","-f","rawvideo",frameFile],CancellationToken.None);
            var frames=File.ReadAllBytes(frameFile);Assert.Equal(frameCount*3,frames.Length);
            Assert.True(frames[0]>195&&frames[2]<45);Assert.True(frames[^3]<45&&frames[^1]>195);
            Assert.InRange((int)frames[3*middle],45,210);Assert.InRange((int)frames[3*middle+2],45,210);
            var pcm=Path.Combine(root,"audio.pcm");
            await media.RunAsync(media.Ffmpeg,["-v","error","-y","-i",render.OutputPath,"-vn","-ar","48000","-ac","1","-f","s16le",pcm],CancellationToken.None);
            var audio=File.ReadAllBytes(pcm);var sample=middle*48000/25;
            Assert.True(Enumerable.Range(sample,480).Any(i=>Math.Abs((int)BitConverter.ToInt16(audio,i*2))>200));
            Assert.Contains("CHECK",File.ReadAllText(Path.ChangeExtension(render.OutputPath,".srt")));
        }
        finally{await service.StopAsync();}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentMusicRenderMixesLoopAndOriginalAcrossTheSequence(bool duck)
    {
        var src=await Source("original","blue");
        var bgm=Path.Combine(root,"short.wav");
        await media.RunAsync(media.Ffmpeg,["-v","error","-f","lavfi","-i","sine=frequency=880:duration=0.25",bgm],CancellationToken.None);
        studio.SaveAsset(new("tone","tone","music","test",bgm,false));
        var id=studio.CreateIndependentProject("mix",320,180,25).Identity;
        var plan=new EditorProject{Fps=25,Timeline=[new("a",src.Id,0,1),new("b",src.Id,0,1)],
            MusicAssetId="tone",MusicVolume=.35,VoiceVolume=.8,DuckMusic=duck};
        var service=Service();
        try
        {
            var render=service.Queue(id,plan);var job=await Finished(render.Id);
            Assert.True(job.Status=="completed",job.Error);
            var pcm=Path.Combine(root,"mix.pcm");
            await media.RunAsync(media.Ffmpeg,["-v","error","-i",render.OutputPath,"-vn","-ar","48000","-ac","1","-f","s16le",pcm],CancellationToken.None);
            var bytes=File.ReadAllBytes(pcm);Assert.InRange(bytes.Length/96000d,1.98,2.04);
            // The probe begins after two full music loops; a source-only or non-looped export fails.
            double Amplitude(int hz){
                double re=0,im=0; const int n=9600;
                for(int i=0;i<n;i++){var sample=BitConverter.ToInt16(bytes,(24000+i)*2);
                    double angle=2*Math.PI*hz*i/48000;re+=sample*Math.Cos(angle);im+=sample*Math.Sin(angle);}
                return 2*Math.Sqrt(re*re+im*im)/n;
            }
            Assert.True(Amplitude(440)>500);
            Assert.InRange(Amplitude(880),100,Amplitude(440)*.75);
        }
        finally{await service.StopAsync();}
    }
    [Fact]
    public async Task PendingReopenCancelAndRetryKeepImmutableIdentityWithoutCatalogOrPrimaryClip()
    {
        var source=await Source("source","green");
        // Persist only the immutable origin: recovery must not need a catalog lookup or fabricated clip.
        var identity=new EditorProjectIdentity("independent-owner","snapshot",180,320,25,DateTimeOffset.UtcNow);
        var plan=new EditorProject{Fps=25,Timeline=[new("v",source.Id,0,1)],TimelineCaptions=[]};
        var id=Guid.NewGuid().ToString("N");var output=Path.Combine(root,"recovered.mp4");
        studio.SaveRender(new(id,"",output,JsonSerializer.Serialize(plan),DateTimeOffset.UtcNow,4,"saved-epoch",identity));
        store.SaveJob(new(id,"edit-render","","pending",0,null));
        var reopened=Service();var worker=(SemaphoreSlim)typeof(EditorRenderService).GetField("_worker",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(reopened)!;
        await worker.WaitAsync();
        try
        {
            reopened.ResumePending();Assert.True(reopened.Control(id,"cancel"));Assert.Equal("cancelled",(await Finished(id)).Status);Assert.False(File.Exists(output));
            worker.Release();Assert.True(reopened.Control(id,"retry"));var done=await Finished(id);Assert.True(done.Status=="completed",done.Error);
            var info=await media.ProbeAsync(output,CancellationToken.None);Assert.Equal(180,info.Width);Assert.Equal(320,info.Height);
            Assert.Null(studio.GetIndependentProject(identity.Id));Assert.Single(store.Clips());Assert.Equal(identity,studio.Renders().Single().ProjectIdentity);
        }
        finally{await reopened.StopAsync();}
    }
    [Fact]
    public void IndependentTransitionSizeUsesIdentityBeforeJobCreation()
    {
        var identity=new EditorProjectIdentity("p","p",1280,720,25,DateTimeOffset.UtcNow);
        var plan=new EditorProject{Fps=25,Timeline=[new("a","red",0,1),new("b","blue",0,1)],
            Transitions=[new("cut","b",1,"panel-wipe",.4,"center")]};
        EditorRenderService.ValidateIndependentRender(identity,plan);
        foreach(var (w,h) in new[]{(1920,1080),(1280,1080),(1920,720),(720,1280)})
        {
            var other=identity with{Width=w,Height=h};
            var ex=Assert.Throws<ArgumentException>(()=>Service().Queue(other,plan));
            Assert.Equal($"unsupported_transition:panel-wipe:output_size:{w}x{h}:requires_1280x720",ex.Message);
            EditorRenderService.ValidateIndependentRender(other,plan with{Transitions=[new("cut","b",1,"gradient-wipe-right",.4,"center")]});
        }
        Assert.Empty(store.Jobs());Assert.Empty(studio.Renders());
    }
    [Fact]
    public void UnsupportedCombinationsFailBeforeCreatingJobsAndOldOriginRemainsCompatible()
    {
        var identity=new EditorProjectIdentity("p","p",180,320,25,DateTimeOffset.UtcNow);
        var plan=new EditorProject{Fps=25,Timeline=[new("v","real",0,1)]};var service=Service();
        foreach(var invalid in new[]{plan with{Timeline=[]},plan with{Timeline=[new("a","real",0,1,Kind:"audio",Track:4)]},plan with{Format="hevc"},plan with{VoiceOnly=true},plan with{EffectAssetId="effect"},plan with{Fps=30}})
            Assert.Throws<ArgumentException>(()=>service.Queue(identity,invalid));
        Assert.Empty(store.Jobs());Assert.Empty(studio.Renders());
        studio.SaveRender(new("old","old-clip",Path.Combine(root,"old.mp4"),"{}",DateTimeOffset.UtcNow));
        var old=studio.Renders().Single();Assert.Null(old.ProjectIdentity);Assert.Equal("old-clip",old.ProjectId);
    }
}
