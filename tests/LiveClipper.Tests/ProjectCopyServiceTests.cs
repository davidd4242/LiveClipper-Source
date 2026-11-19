using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Tests;

public sealed class ProjectCopyServiceTests
{
    private sealed class Fixture
    {
        public readonly string Root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"LiveClipper-ProjectCopyServiceTests-"+Guid.NewGuid().ToString("N"))).FullName;
        public Store Store {get;}
        public StudioStore Studio {get;}
        public Clip Clip {get;}
        public EditorProject Project {get;}
        public ProjectCopyService Service {get;}
        public Fixture()
        {
            Store=new(Path.Combine(Root,"test.db"));Store.Migrate();Studio=new(Store.DatabasePath);Studio.Migrate();
            var media=Path.Combine(Root,"source.mp4");File.WriteAllBytes(media,[1,2,3,4,5]);
            var srt=Path.Combine(Root,"source.srt");File.WriteAllText(srt,"1\n00:00:00,000 --> 00:00:02,000\n原字幕\n");
            Clip=new(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),media,srt,DateTimeOffset.UtcNow);Store.SaveClip(Clip);
            Studio.SaveLocalVideo(new(Clip.Id,media,"原工程",Path.Combine(Root,"original-exports"),Clip.CreatedAt));
            Store.SaveTranscript(Clip.EventId,"{\"language\":\"zh\",\"segments\":[{\"start\":0,\"end\":2,\"text\":\"原字幕\"}]}",srt);
            Studio.SaveEvent(new("candidate",Clip.Id,1,3,"候选","摘要","开头","ai"));
            Studio.SaveDocument("ai-advice",Clip.Id,"{\"Summary\":\"建议\",\"TranscriptCacheKey\":\"existing\"}");
            Studio.SaveDocument("clip-ranking",Clip.Id,JsonSerializer.Serialize(new {ClipId=Clip.Id,Candidates=new[]{new {Start=1,End=3,Title="候选"}}}));
            Studio.SaveDocument("llm-chunks-skill",Clip.Id,"{\"Chunks\":[]}");
            Project=new(){Start=0,End=10,Timeline=[new("placed",Clip.Id,1,3){EventPlacement=new(){SourceId=Clip.Id,EventId="candidate"}}],
                CaptionOverrides=new(){[Clip.Id]=[new(0,2,"独立覆盖")]}};
            Studio.SaveProject(Clip.Id,JsonSerializer.Serialize(Project));
            Service=new(Studio,Path.Combine(Root,"copies"));
        }
        public AudioAsset SeedMusic(string kind="music")
        {
            var path=Path.Combine(Root,"original-music.wav");File.WriteAllBytes(path,[7,8,9]);
            var asset=new AudioAsset("music-original","原曲",kind,"原声",path,true);
            Studio.SaveAsset(asset);
            Studio.SaveDocument("music-analysis",asset.Id,"{\"beats\":[1,2]}");
            Studio.SaveDocument("audio-attribution",asset.Id,"{\"author\":\"署名\"}");
            return asset;
        }
        public long Count(string table)
        {
            using var db=new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText="SELECT COUNT(*) FROM "+table;return Convert.ToInt64(command.ExecuteScalar());
        }
        public void Execute(string sql)
        {using var db=new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    }

    [Fact]
    public async Task IndependentEmpty_HttpCopyAndPackageKeepSourceRevisionAndNoMedia()
    {
        var f=new Fixture();var source=f.Studio.CreateIndependentProject("blank");
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Testing"});
        builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();
        StudioEndpoints.Map(app,f.Store,f.Studio,null!,null!,null!,null!,null!,null!,null!,new LiveSettings{StorageRoot=f.Root,ExportOutputDirectory=Path.Combine(f.Root,"exports")});
        await app.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        var id=source.Identity.Id;
        var payload=await client.GetFromJsonAsync<JsonElement>($"/api/studio/clips/{id}/editor");
        foreach(var route in new[]{"copy","project-package"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/studio/clips/{id}/{route}"){Content=JsonContent.Create(new{name="portable",project=payload.GetProperty("project")})};
            request.Headers.Add("X-Base-Revision","1");request.Headers.Add("X-Editor-Epoch",payload.GetProperty("epoch").GetString());
            using var response=await client.SendAsync(request);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
            var result=await response.Content.ReadFromJsonAsync<JsonElement>();
            if(route=="copy")Assert.NotNull(f.Studio.GetIndependentProject(result.GetProperty("clipId").GetString()!));
            else {using var download=await client.GetAsync(result.GetProperty("downloadUrl").GetString());Assert.True(download.IsSuccessStatusCode);}
        }
        Assert.Equal(source,f.Studio.GetIndependentProject(id));Assert.Equal(1,f.Count("clips"));
        Assert.False(Directory.Exists(Path.Combine(f.Root,"exports","project-copies")));
        await app.StopAsync();
    }

    [Fact]
    public async Task IndependentCopy_PublicationRollsBackCatalogAndMediaTogether()
    {
        var f=new Fixture();var source=f.Studio.CreateIndependentProject("source");
        f.Execute("CREATE TRIGGER reject_copy BEFORE INSERT ON editor_projects BEGIN SELECT RAISE(ABORT,'test publication failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(()=>f.Service.CopyAsync(source.Identity.Id,"copy",f.Project,1));
        Assert.Equal(1,f.Count("editor_project_catalog"));Assert.Equal(1,f.Count("clips"));Assert.Equal(2,f.Count("editor_projects"));
        Assert.Equal(source,f.Studio.GetIndependentProject(source.Identity.Id));
    }

    [Fact]
    public async Task IndependentEmptyCopy_HasSeparateCatalogAndNoMediaDirectory()
    {
        var f=new Fixture();var source=f.Studio.CreateIndependentProject("empty",1280,720,25);
        var project=EditorProjectCodec.Deserialize(source.Document.Json);
        var result=await f.Service.CopyAsync(source.Identity.Id,"saved",project,1);
        var copy=f.Studio.GetIndependentProject(result.ClipId)!;
        Assert.NotEqual(source.Identity.Id,copy.Identity.Id);Assert.Equal("saved",copy.Identity.Name);
        Assert.Equal(1280,copy.Identity.Width);Assert.Equal(25,copy.Identity.Fps);
        Assert.Empty(EditorProjectCodec.Deserialize(copy.Document.Json).Timeline!);
        Assert.Equal(1,f.Count("clips"));Assert.Equal(2,f.Count("editor_project_catalog"));
        Assert.False(Directory.Exists(Path.Combine(f.Root,"copies")));
        Assert.Equal(source,f.Studio.GetIndependentProject(source.Identity.Id));
    }

    [Fact]
    public async Task IndependentCopy_MapsMediaSeparatelyFromProjectAndRejectsStaleRevision()
    {
        var f=new Fixture();var source=f.Studio.CreateIndependentProject("independent");
        var result=await f.Service.CopyAsync(source.Identity.Id,"copy",f.Project,1);
        var copy=f.Studio.GetIndependentProject(result.ClipId)!;
        var document=EditorProjectCodec.Deserialize(copy.Document.Json);
        Assert.NotEqual(result.ClipId,document.Timeline![0].ClipId);
        Assert.NotEqual(f.Clip.Id,document.Timeline[0].ClipId);
        Assert.Equal(2,f.Count("clips"));
        await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>f.Service.CopyAsync(source.Identity.Id,"stale",f.Project,0));
        Assert.Equal(2,f.Count("editor_project_catalog"));Assert.Equal(2,f.Count("clips"));
    }

    [Fact]
    public async Task Copy_CreatesIndependentMediaProjectTranscriptAndCandidates_WithoutChangingOriginal()
    {
        var f=new Fixture();var original=f.Studio.GetProjectRecord(f.Clip.Id)!;var transcript=f.Store.TranscriptJson(f.Clip.EventId);
        var result=await f.Service.CopyAsync(f.Clip.Id,"G3 副本",f.Project,original.Revision);
        Assert.NotEqual(f.Clip.Id,result.ClipId);Assert.Equal(1,result.Revision);Assert.Equal("G3 副本",result.Name);
        var clip=f.Store.Clips().Single(x=>x.Id==result.ClipId);
        Assert.NotEqual(f.Clip.EventId,clip.EventId);Assert.NotEqual(f.Clip.Path,clip.Path);Assert.NotEqual(f.Clip.SrtPath,clip.SrtPath);
        Assert.Equal(File.ReadAllBytes(f.Clip.Path),File.ReadAllBytes(clip.Path));
        Assert.Equal(transcript,f.Store.TranscriptJson(clip.EventId));
        var project=EditorProjectCodec.Deserialize(f.Studio.Project(result.ClipId)!);
        Assert.Equal(result.ClipId,project.Timeline![0].ClipId);
        var candidate=f.Studio.Events(result.ClipId).Single();Assert.NotEqual("candidate",candidate.Id);Assert.Equal("ai",candidate.Source);
        Assert.Equal(candidate.Id,project.Timeline[0].EventPlacement!.EventId);Assert.Equal(result.ClipId,project.Timeline[0].EventPlacement!.SourceId);
        Assert.Equal("独立覆盖",project.CaptionOverrides![result.ClipId][0].Text);
        Assert.Equal(f.Studio.Document("ai-advice",f.Clip.Id),f.Studio.Document("ai-advice",result.ClipId));
        using(var ranking=JsonDocument.Parse(f.Studio.Document("clip-ranking",result.ClipId)!))Assert.Equal(result.ClipId,ranking.RootElement.GetProperty("ClipId").GetString());
        f.Studio.SaveProject(result.ClipId,JsonSerializer.Serialize(project with {End=9}));
        f.Studio.SaveEvent(candidate with {Title="改副本"});
        f.Store.SaveTranscript(clip.EventId,"{\"segments\":[]}",clip.SrtPath!);
        File.WriteAllText(clip.SrtPath!,"副本字幕文件");
        Assert.Equal(original,f.Studio.GetProjectRecord(f.Clip.Id));Assert.Equal(transcript,f.Store.TranscriptJson(f.Clip.EventId));
        Assert.Equal("候选",f.Studio.Events(f.Clip.Id).Single().Title);Assert.Contains("原字幕",File.ReadAllText(f.Clip.SrtPath!));
        // Independent copy deletion cannot delete the original registered media or transcript.
        f.Studio.DeleteLocalVideo(result.ClipId,f.Root);
        Assert.True(File.Exists(f.Clip.Path));Assert.True(File.Exists(f.Clip.SrtPath));Assert.Equal(original,f.Studio.GetProjectRecord(f.Clip.Id));
    }

    [Fact]
    public async Task Copy_FiveExistingAiCandidatesHaveNewIdsAndRemainIndependentlySelectable()
    {
        var f=new Fixture();
        for(var i=1;i<=4;i++)f.Studio.SaveEvent(new("candidate-"+i,f.Clip.Id,i,i+2,"候选"+i,"摘要","开头","ai"));
        var original=f.Studio.GetProjectRecord(f.Clip.Id);var originals=f.Studio.Events(f.Clip.Id);
        var result=await f.Service.CopyAsync(f.Clip.Id,"五候选副本",f.Project,1);
        var events=f.Studio.Events(result.ClipId);
        Assert.Equal(5,events.Count);Assert.All(events,e=>{Assert.Equal(result.ClipId,e.ClipId);Assert.Equal("ai",e.Source);Assert.DoesNotContain(originals,o=>o.Id==e.Id);});
        Assert.Equal(originals.Select(e=>(e.Start,e.End,e.Title,e.Summary,e.Hook)),events.Select(e=>(e.Start,e.End,e.Title,e.Summary,e.Hook)));
        Assert.Equal(original,f.Studio.GetProjectRecord(f.Clip.Id));Assert.Equal(originals,f.Studio.Events(f.Clip.Id));
    }

    [Fact]
    public async Task Copy_StemsAreCopiedInsteadOfSharingDeletablePaths()
    {
        var f=new Fixture();var vocal=Path.Combine(f.Root,"v.wav");var instrumental=Path.Combine(f.Root,"i.wav");
        File.WriteAllBytes(vocal,[7,8]);File.WriteAllBytes(instrumental,[9,10]);f.Studio.SaveStems(new(f.Clip.Id,vocal,instrumental));
        var result=await f.Service.CopyAsync(f.Clip.Id,"副本",f.Project,1);
        var stems=f.Studio.Stems(result.ClipId)!;Assert.NotEqual(vocal,stems.VocalsPath);Assert.NotEqual(instrumental,stems.InstrumentalPath);
        Assert.Equal(File.ReadAllBytes(vocal),File.ReadAllBytes(stems.VocalsPath));Assert.Equal(File.ReadAllBytes(instrumental),File.ReadAllBytes(stems.InstrumentalPath));
    }

    [Fact]
    public async Task Copy_StaleRevisionOrMissingMediaDoesNotRegisterAnything()
    {
        var f=new Fixture();await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>f.Service.CopyAsync(f.Clip.Id,"副本",f.Project,0));
        File.Move(f.Clip.Path,f.Clip.Path+".unavailable");
        await Assert.ThrowsAsync<ProjectCopyMediaStager.StagingException>(()=>f.Service.CopyAsync(f.Clip.Id,"副本",f.Project,1));
        Assert.Equal(1,f.Count("clips"));Assert.Equal(1,f.Count("editor_projects"));Assert.Equal(1,f.Count("studio_events"));
    }

    [Fact]
    public async Task Copy_DatabaseFailureRollsBackAllPublishedRows()
    {
        var f=new Fixture();var original=f.Studio.GetProjectRecord(f.Clip.Id);
        f.Execute("CREATE TRIGGER reject_copy BEFORE INSERT ON editor_projects BEGIN SELECT RAISE(ABORT,'fixture publication failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(()=>f.Service.CopyAsync(f.Clip.Id,"副本",f.Project,1));
        foreach(var table in new[]{"clips","local_videos","transcripts","studio_events","editor_projects"})Assert.Equal(1,f.Count(table));
        Assert.Equal(3,f.Count("studio_documents"));Assert.Equal(original,f.Studio.GetProjectRecord(f.Clip.Id));
    }

    [Fact]
    public async Task Copy_AssociatedDataChangeDuringStagingRejectsPublish()
    {
        var f=new Fixture();var captured=f.Studio.CaptureProjectCopy(f.Clip.Id,[f.Clip.Id],1);
        var staged=await ProjectCopyMediaStager.StageAsync(Path.Combine(f.Root,"copies"),[new(f.Clip.Id,f.Clip.Path,f.Clip.SrtPath)]);
        f.Studio.SaveEvent(f.Studio.Events(f.Clip.Id).Single() with {Title="并发编辑"});
        var clips=new Dictionary<string,string>{{f.Clip.Id,staged.Clips[f.Clip.Id].ClipId}};var events=new Dictionary<string,string>{{"candidate","new-event"}};
        var project=StudioProjectMediaRefs.Remap(f.Project,clips,events);
        Assert.Throws<ProjectCopyConflictException>(()=>f.Studio.PublishProjectCopy(captured.Snapshot,staged,clips,events,project,"副本"));
        Assert.Equal(1,f.Count("clips"));Assert.Equal(1,f.Count("editor_projects"));Assert.Equal("并发编辑",f.Studio.Events(f.Clip.Id).Single().Title);
    }

    private static ReplicaExtension MusicArchive(string musicId,string? effectId=null)
    {
        var sequence=new {name="Main",hostMusic=new {musicAssetId=musicId,musicVolume=.37,duckMusic=false},
            hostEffect=new {effectAssetId=effectId,effectVolume=.5,effectAt=0}};
        return new ReplicaExtension(JsonSerializer.SerializeToElement(new {version=1,codecVersion=1,
            document=new {__fcproj=1,sequence,projectState=new {sequence}},exactTimeMap=new {},
            compatibility=new {projection="lc-editor-next-active-sequence-v1"}}));
    }

    [Fact]
    public async Task IndependentCopy_GlobalMusicReopensWithSeparateFileAndBothHostedReferences()
    {
        var f=new Fixture();var music=f.SeedMusic();var source=f.Studio.CreateIndependentProject("原工程");
        var project=f.Project with {MusicAssetId=music.Id,MusicVolume=.37,DuckMusic=false,ReplicaExtension=MusicArchive(music.Id)};
        var saved=f.Studio.SaveIndependentProject(source.Identity.Id,project,1);Assert.True(saved.Ok);
        var original=f.Studio.GetIndependentProject(source.Identity.Id);
        var copy=await f.Service.CopyAsync(source.Identity.Id,"含配乐的副本",project,saved.Revision);
        var reopened=new StudioStore(f.Store.DatabasePath);
        var document=EditorProjectCodec.Deserialize(reopened.GetIndependentProject(copy.ClipId)!.Document.Json);
        var track=reopened.Assets().Single(a=>a.Id==document.MusicAssetId);
        Assert.NotEqual(music.Id,track.Id);Assert.NotEqual(music.Path,track.Path);
        Assert.Equal(music with {Id=track.Id,Path=track.Path,BuiltIn=false},track);
        Assert.Equal(File.ReadAllBytes(music.Path),File.ReadAllBytes(track.Path));
        Assert.Equal(.37,document.MusicVolume);Assert.False(document.DuckMusic);
        var archive=document.ReplicaExtension!.Document;
        foreach(var seq in new[]{archive.GetProperty("sequence"),archive.GetProperty("projectState").GetProperty("sequence")})
        {
            var mix=seq.GetProperty("hostMusic");
            Assert.Equal(track.Id,mix.GetProperty("musicAssetId").GetString());
            Assert.Equal(.37,mix.GetProperty("musicVolume").GetDouble());Assert.False(mix.GetProperty("duckMusic").GetBoolean());
        }
        foreach(var scope in new[]{"music-analysis","audio-attribution"})
            Assert.Equal(reopened.Document(scope,music.Id),reopened.Document(scope,track.Id));
        var clonedClip=f.Store.Clips().Single(c=>c.Id==document.Timeline![0].ClipId);
        Assert.NotEqual(f.Clip.Id,clonedClip.Id);Assert.NotEqual(f.Clip.EventId,clonedClip.EventId);
        Assert.NotEqual(f.Clip.SrtPath,clonedClip.SrtPath);Assert.Equal(File.ReadAllBytes(f.Clip.SrtPath!),File.ReadAllBytes(clonedClip.SrtPath!));
        Assert.NotEqual("candidate",document.Timeline![0].EventPlacement!.EventId);
        // Rendering resolves the projected ID and kind against this registry, not against the original file.
        File.WriteAllBytes(music.Path,[99]);Assert.Equal(new byte[]{7,8,9},File.ReadAllBytes(track.Path));
        File.WriteAllBytes(track.Path,[42]);Assert.Equal(new byte[]{99},File.ReadAllBytes(music.Path));
        reopened.SaveDocument("music-analysis",track.Id,"{}");Assert.Equal("{\"beats\":[1,2]}",reopened.Document("music-analysis",music.Id));
        Assert.Equal(original,reopened.GetIndependentProject(source.Identity.Id));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("document")]
    public async Task Copy_ChangedMusicAbortsPublicationAndCleansOnlyThisStagingDirectory(string change)
    {
        var f=new Fixture();var music=f.SeedMusic();var original=f.Studio.GetProjectRecord(f.Clip.Id);
        var root=Directory.CreateDirectory(Path.Combine(f.Root,"copies")).FullName;
        var sibling=Directory.CreateDirectory(Path.Combine(root,"unrelated-copy")).FullName;
        var sentinel=Path.Combine(sibling,"keep.txt");File.WriteAllText(sentinel,"keep");
        void Change()
        {
            if(change=="edit")f.Studio.SaveAsset(music with {Name="改名"});
            else if(change=="document")f.Studio.SaveDocument("music-analysis",music.Id,"{}");
            else f.Execute("DELETE FROM audio_assets WHERE id='music-original'");
        }
        await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>f.Service.CopyAsync(f.Clip.Id,"副本",f.Project with {MusicAssetId=music.Id},1,validateBeforePublish:Change));
        Assert.Equal(change=="delete"?0:1,f.Count("audio_assets"));Assert.Equal(1,f.Count("clips"));Assert.Equal(1,f.Count("editor_projects"));
        Assert.Equal(original,f.Studio.GetProjectRecord(f.Clip.Id));Assert.Equal(new byte[]{7,8,9},File.ReadAllBytes(music.Path));
        Assert.Equal(sibling,Assert.Single(Directory.GetDirectories(root)));Assert.Equal("keep",File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_MusicStagingOrFinalInsertFailureLeavesNoPublishedRowsOrFiles(bool failDuringStaging)
    {
        var f=new Fixture();var music=f.SeedMusic();var source=f.Studio.CreateIndependentProject("原工程");
        if(failDuringStaging)File.Move(music.Path,music.Path+".moved");
        else f.Execute("CREATE TRIGGER reject_copy BEFORE INSERT ON editor_projects BEGIN SELECT RAISE(ABORT,'test publication failure'); END;");
        Task Copy()=>f.Service.CopyAsync(source.Identity.Id,"副本",f.Project with {MusicAssetId=music.Id},1);
        if(failDuringStaging)await Assert.ThrowsAsync<IOException>(Copy);
        else await Assert.ThrowsAsync<SqliteException>(Copy);
        Assert.Equal(1,f.Count("audio_assets"));Assert.Equal(1,f.Count("clips"));Assert.Equal(1,f.Count("editor_project_catalog"));
        Assert.Equal(5,f.Count("studio_documents"));Assert.Equal(2,f.Count("editor_projects"));Assert.Equal(source,f.Studio.GetIndependentProject(source.Identity.Id));
        Assert.Equal(new byte[]{7,8,9},File.ReadAllBytes(failDuringStaging?music.Path+".moved":music.Path));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(f.Root,"copies")));
    }

    [Theory]
    [InlineData("wrong-kind")]
    [InlineData("effect")]
    [InlineData("archived-effect")]
    [InlineData("unknown-document")]
    public async Task Copy_NonMusicDependenciesRejectedBeforeStaging(string reason)
    {
        var f=new Fixture();var asset=f.SeedMusic(reason=="wrong-kind"?"effect":"music");
        var project=f.Project with {MusicAssetId=asset.Id};
        if(reason=="effect")project=project with {EffectAssetId=asset.Id};
        if(reason=="archived-effect")project=project with {ReplicaExtension=MusicArchive(asset.Id,"fx")};
        if(reason=="unknown-document")f.Studio.SaveDocument("unknown-music-data",asset.Id,"{}");
        await Assert.ThrowsAsync<NotSupportedException>(()=>f.Service.CopyAsync(f.Clip.Id,"副本",project,1));
        Assert.False(Directory.Exists(Path.Combine(f.Root,"copies")));Assert.Equal(1,f.Count("clips"));Assert.Equal(1,f.Count("audio_assets"));
    }

    [Fact]
    public async Task Copy_UnsupportedMutableDependenciesAreExplicitlyRejected()
    {
        var f=new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Service.CopyAsync(f.Clip.Id,"副本",f.Project with {MusicAssetId="missing-music"},1));
        f.Studio.SaveDocument("unknown-analysis",f.Clip.Id,"{}");
        await Assert.ThrowsAsync<NotSupportedException>(()=>f.Service.CopyAsync(f.Clip.Id,"副本",f.Project,1));
        Assert.Equal(1,f.Count("clips"));Assert.Equal(1,f.Count("editor_projects"));
    }
}
