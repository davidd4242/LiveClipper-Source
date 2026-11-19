using System.IO.Compression;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// R82：导出清单（project.json 的 parts）必须引用渲染器真实产出的中间文件。
/// 渲染器（EditorRenderService.AssembleAsync）写 part-NNN.mkv / overlay-NNN.mkv / audio-NNN.mka，
/// 编码为 FFV1 + PCM，容器为 Matroska；清单里的每个 file 都必须能在导出包里找到。
/// </summary>
public sealed class R82ExportManifestTests
{
    private static TimelineItem Item(string id,double start,double end,string clipId="clip",int track=1,bool disabled=false,string kind="video")
        => new(id,clipId,start,end,kind,Track:track,Disabled:disabled);

    private static EditorProject Plan(params TimelineItem[] items)=>new()
    {
        Start=0,End=items.Max(x=>x.End),Fps=30,Resolution=480,Timeline=items
    };

    [Fact]
    public void BuildManifest_NamesAndNumbersMatchRendererIntermediateLayout()
    {
        var plan=Plan(
            Item("b0",0,1),                                  // V1 → part-000.mkv
            Item("b1",1,2,disabled:true),                    // V1 禁用片段渲染器仍产出黑帧 part-001.mkv
            Item("o0",0,1,track:2),                          // overlay-000.mkv
            Item("o1",0,1,track:3),                          // overlay-001.mkv
            new TimelineItem("g0","",0,1,"graphic",Track:3),  // source-free 图形：渲染器不产文件，也不占 overlay 编号
            Item("o2",1,2,track:3),                          // overlay-002.mkv（不是 003）
            Item("a0",0,1,track:4),                          // audio-000.mka
            Item("a1",1,2,track:4,disabled:true),            // 禁用音频片段不产文件
            Item("a2",2,3,track:5));                         // audio-001.mka

        var (parts,_)=StudioExportService.BuildManifest(plan,"clip");

        Assert.Equal(new[]
        {
            "part-000.mkv","part-001.mkv",
            "overlay-000.mkv","overlay-001.mkv","overlay-002.mkv",
            "audio-000.mka","audio-001.mka"
        },parts.Select(x=>x.File));
        Assert.DoesNotContain(parts,x=>x.File.EndsWith(".mp4",StringComparison.Ordinal)||x.File.EndsWith(".m4a",StringComparison.Ordinal));
    }

    [Fact]
    public void BuildManifest_SkipsHiddenOverlayTrackWithoutBurningNumbers()
    {
        var plan=Plan(
            Item("b0",0,1),
            Item("o0",0,1,track:2),
            Item("o1",0,1,track:3)) with {HiddenTracks=[3]};

        var (parts,_)=StudioExportService.BuildManifest(plan,"clip");

        Assert.Equal(new[]{"part-000.mkv","overlay-000.mkv"},parts.Select(x=>x.File));
    }

    [Fact]
    public async Task PackageManifest_ReferencesRealFfviPcmFilesThatExistInTheZip()
    {
        var ffmpeg=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(string.IsNullOrWhiteSpace(ffmpeg)||!File.Exists(ffmpeg))return; // 原生 FFmpeg gate

        var root=Path.Combine(Path.GetTempPath(),"lcs-r82-export-manifest-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tools=new MediaTools(ffmpeg);
        StudioExportService? service=null;
        try
        {
            var source=Path.Combine(root,"source.mkv");
            await tools.RunAsync(tools.Ffmpeg,
            [
                "-hide_banner","-loglevel","error","-y",
                "-f","lavfi","-i","testsrc2=s=320x180:r=30,noise=alls=2:allf=t",
                "-f","lavfi","-i","aevalsrc=0.25*sin(2*PI*440*t):s=48000:d=3",
                "-t","3","-map","0:v:0","-map","1:a:0",
                "-c:v","ffv1","-level","3","-c:a","pcm_f32le","-shortest",source
            ],CancellationToken.None);

            var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
            var studio=new StudioStore(store.DatabasePath);studio.Migrate();
            var clip=new Clip("clip","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
            var renderer=new EditorRenderService(store,studio,tools,root);
            var settings=new LiveSettings{StorageRoot=root,FfmpegPath=ffmpeg};
            service=new StudioExportService(store,studio,renderer,tools,settings);

            var plan=Plan(
                Item("b0",0,2),          // 主轨 → part-000.mkv
                Item("o0",0,1,track:2),  // 叠加轨 → overlay-000.mkv
                Item("a0",0,2,track:4)); // 音频轨 → audio-000.mka

            var output=service.Queue(clip,plan,"package");
            var job=await WaitForJobAsync(store,output.Id,TimeSpan.FromSeconds(180));
            Assert.True(job.Status=="completed",$"导出任务失败：{job.Status} / {job.Error}");

            using var zip=ZipFile.OpenRead(output.OutputPath);
            var entries=zip.Entries.Select(x=>x.FullName).ToHashSet(StringComparer.Ordinal);
            using var projectEntry=zip.GetEntry("project.json")!.Open();
            using var document=JsonDocument.Parse(projectEntry);
            var parts=document.RootElement.GetProperty("parts").EnumerateArray()
                .Select(x=>x.GetProperty("file").GetString()!).ToArray();

            Assert.Contains("part-000.mkv",parts);
            Assert.Contains("overlay-000.mkv",parts);
            Assert.Contains("audio-000.mka",parts);
            foreach(var file in parts)
                Assert.True(entries.Contains(file),$"清单引用了导出包内不存在的文件：{file}");

            var unpacked=Path.Combine(root,"unpacked");Directory.CreateDirectory(unpacked);
            foreach(var file in parts)zip.GetEntry(file)!.ExtractToFile(Path.Combine(unpacked,file),true);

            foreach(var file in parts.Where(x=>x.StartsWith("part-",StringComparison.Ordinal)||x.StartsWith("overlay-",StringComparison.Ordinal)))
            {
                var (container,codecs)=await ProbeAsync(tools,Path.Combine(unpacked,file));
                Assert.StartsWith("matroska",container);
                Assert.Contains("ffv1",codecs);
                Assert.Contains("pcm_f32le",codecs);
            }
            foreach(var file in parts.Where(x=>x.StartsWith("audio-",StringComparison.Ordinal)))
            {
                var (container,codecs)=await ProbeAsync(tools,Path.Combine(unpacked,file));
                Assert.StartsWith("matroska",container);
                Assert.Contains("pcm_f32le",codecs);
                Assert.DoesNotContain("ffv1",codecs);
            }
        }
        finally
        {
            if(service is not null)await service.StopAsync();
            try{Directory.Delete(root,true);}catch{}
        }
    }

    private static async Task<Job> WaitForJobAsync(Store store,string id,TimeSpan timeout)
    {
        var deadline=DateTimeOffset.UtcNow+timeout;
        while(DateTimeOffset.UtcNow<deadline)
        {
            var job=store.Jobs().FirstOrDefault(x=>x.Id==id);
            if(job is not null&&job.Status is "completed" or "failed")return job;
            await Task.Delay(200);
        }
        throw new TimeoutException($"导出任务 {id} 在 {timeout.TotalSeconds:0} 秒内未完成");
    }

    private static async Task<(string Container,string[] Codecs)> ProbeAsync(MediaTools tools,string path)
    {
        var json=await tools.RunAsync(tools.Ffprobe,["-v","error","-show_format","-show_streams","-of","json",path],CancellationToken.None);
        using var document=JsonDocument.Parse(json);
        var container=document.RootElement.GetProperty("format").GetProperty("format_name").GetString()??"";
        var codecs=document.RootElement.GetProperty("streams").EnumerateArray()
            .Select(x=>x.GetProperty("codec_name").GetString()??"").ToArray();
        return (container,codecs);
    }
}
