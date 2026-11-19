using LiveClipper.Core;
using LiveClipper.Domain;
namespace LiveClipper.Tests;
[Collection("TranscriptionShutdownBoundary")]
public sealed class TranscriptionOutputDirectoryTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("fail")]
    [InlineData("cancel")]
    [InlineData("publish-fail")]
    public async Task NewJobCapturesOutputRootAndNeverOverwritesExistingTranscriptOnFailureOrCancel(string mode)
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"TranscriptOutput-"+Guid.NewGuid().ToString("N"))).FullName;
        var ffmpeg=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Links","ffmpeg.exe");var media=new MediaTools(ffmpeg);
        var source=Path.Combine(root,"source.m4a");await media.RunAsync(ffmpeg,["-n","-f","lavfi","-i","sine=frequency=440:duration=1","-c:a","aac",source],default);
        var oldSrt=Path.Combine(root,"chosen-results","transcripts","clip","transcript.srt");Directory.CreateDirectory(Path.GetDirectoryName(oldSrt)!);File.WriteAllText(oldSrt,"old subtitle remains");const string oldJson="{\"language\":\"zh\",\"segments\":[{\"start\":0,\"end\":1,\"text\":\"old\"}]}";
        var store=new Store(Path.Combine(root,"fixture.db"));store.Migrate();var clip=new Clip("clip","event",source,oldSrt,DateTimeOffset.UtcNow);store.SaveClip(clip);store.SaveTranscript(clip.EventId,oldJson,oldSrt);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(oldSrt)!,"transcript.json"),oldJson);
        if(mode=="publish-fail")
        {
            using var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");db.Open();using var cmd=db.CreateCommand();
            cmd.CommandText="CREATE TRIGGER reject_transcript_publish BEFORE UPDATE OF srt_path ON clips BEGIN SELECT RAISE(ABORT,'fixture publish failure'); END;";cmd.ExecuteNonQuery();
        }
        var script=Path.Combine(root,"worker.cjs");
        File.WriteAllText(script,"const fs=require('node:fs'),path=require('node:path');const out=process.argv[3];fs.mkdirSync(out,{recursive:true});fs.writeFileSync(path.join(out,'transcript.json'),JSON.stringify({language:'zh',segments:[{start:0,end:1,text:'new'}]}));fs.writeFileSync(path.join(out,'transcript.srt'),'new subtitle');fs.writeFileSync(path.join(out,'ready'),'ready');const t=setInterval(()=>{if(fs.existsSync(path.join(out,'release'))){clearInterval(t);process.exit("+(mode=="fail"?"1":"0")+");}},20);");
        var oldScript=Environment.GetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT");Environment.SetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT",script);
        var output=Path.Combine(root,"chosen-results");var work=Path.Combine(root,"chosen-work");
        var settings=new LiveSettings{StorageRoot=Path.Combine(root,"storage"),ExportOutputDirectory=output,RenderWorkDirectory=work,PythonPath=@"C:\Program Files\nodejs\node.exe",WhisperModel="fixture-no-model"};
        var service=new TranscriptionService(store,settings,media);
        try
        {
            var receipt=service.QueueReceipt(clip);
            settings.ExportOutputDirectory=Path.Combine(root,"changed-results");settings.RenderWorkDirectory=Path.Combine(root,"changed-work");
            var expected=Path.Combine(work,"transcription-audio",receipt.JobId,"result");
            var published=Path.Combine(output,"transcripts",clip.Id);
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(!File.Exists(Path.Combine(expected,"ready"))&&DateTime.UtcNow<deadline)await Task.Delay(20);
            Assert.True(File.Exists(Path.Combine(expected,"ready")),"worker did not reach isolated output directory");
            Assert.Equal(oldJson,store.TranscriptJson(clip.EventId));Assert.Equal(oldSrt,store.Clips().Single().SrtPath);Assert.Equal("old subtitle remains",File.ReadAllText(oldSrt));
            Assert.True(Directory.Exists(Path.Combine(work,"transcription-audio",receipt.JobId)));
            if(mode=="cancel")await service.StopAsync();
            else
            {
                File.WriteAllText(Path.Combine(expected,"release"),"go");
                while(store.Jobs().Single().Status is "pending" or "running"&&DateTime.UtcNow<deadline)await Task.Delay(20);
            }
            Assert.Equal(mode=="success"?"new subtitle":"old subtitle remains",File.ReadAllText(oldSrt));Assert.False(Directory.Exists(Path.Combine(settings.StorageRoot,"transcripts")));
            Assert.False(Directory.Exists(settings.ExportOutputDirectory));Assert.False(Directory.Exists(settings.RenderWorkDirectory));
            if(mode=="success")
            {
                Assert.Equal("completed",store.Jobs().Single().Status);Assert.Equal(Path.Combine(published,"transcript.srt"),store.Clips().Single().SrtPath);
                Assert.Contains("media-pts-v1",store.TranscriptJson(clip.EventId));
            }
            else {Assert.Equal(oldJson,store.TranscriptJson(clip.EventId));Assert.Equal(oldSrt,store.Clips().Single().SrtPath);Assert.Equal(mode=="cancel"?"pending":"failed",store.Jobs().Single().Status);Assert.Equal(oldJson,File.ReadAllText(Path.Combine(published,"transcript.json")));}
            Assert.False(Directory.Exists(Path.Combine(work,"transcription-audio",receipt.JobId)));
        }
        finally{await service.StopAsync();Environment.SetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT",oldScript);Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase);Directory.Delete(root,true);}
    }
}
