using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Tests;

public sealed class ProjectPackageServiceTests
{
    private sealed class Fixture
    {
        public string Root {get;}=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"LiveClipper-PackageTests-"+Guid.NewGuid().ToString("N"))).FullName;
        public Store Store {get;}
        public StudioStore Studio {get;}
        public ProjectPackageService Service {get;}
        public Fixture()
        {
            Store=new(Path.Combine(Root,"fixture.db"));Store.Migrate();Studio=new(Store.DatabasePath);Studio.Migrate();
            Service=new(Studio,Path.Combine(Root,"packages"));
        }
        public (Clip Clip,EditorProject Project) Seed()
        {
            var path=Path.Combine(Root,"录播.mp4");File.WriteAllBytes(path,[1,4,9,16]);
            var srt=Path.Combine(Root,"字幕.srt");File.WriteAllText(srt,"1\n00:00:00,000 --> 00:00:02,000\n独立字幕\n");
            var clip=new Clip(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),path,srt,DateTimeOffset.UtcNow);Store.SaveClip(clip);
            Studio.SaveLocalVideo(new(clip.Id,path,"原始录播",Path.Combine(Root,"exports"),clip.CreatedAt));
            Store.SaveTranscript(clip.EventId,"{\"language\":\"zh\",\"segments\":[{\"start\":0,\"end\":2,\"text\":\"独立字幕\"}]}",srt);
            for(var i=0;i<5;i++)Studio.SaveEvent(new("candidate-"+i,clip.Id,i,i+2,"候选"+i,"摘要","开头","ai"));
            Studio.SaveDocument("ai-advice",clip.Id,"{\"Summary\":\"已经分析的建议\"}");
            Studio.SaveDocument("clip-ranking",clip.Id,JsonSerializer.Serialize(new{ClipId=clip.Id,Candidates=new[]{new{Start=0,End=2,Title="候选0"}}}));
            var project=new EditorProject {Start=0,End=10,Timeline=[new("placed",clip.Id,0,2){EventPlacement=new(){SourceId=clip.Id,EventId="candidate-0"}}],
                CaptionOverrides=new(){[clip.Id]=[new(0,2,"编辑后的字幕")]}};
            Studio.SaveProject(clip.Id,JsonSerializer.Serialize(project));return(clip,project);
        }
        public long Count(string table)
        {using var db=new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText="SELECT COUNT(*) FROM "+table;return Convert.ToInt64(command.ExecuteScalar());}
        public void Execute(string sql)
        {using var db=new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
        public async Task<byte[]> Export(Clip clip,EditorProject project)
        {
            var result=await Service.ExportAsync(clip.Id,"中文便携工程",project,1);
            Assert.Equal("中文便携工程.lcproj",result.FileName);
            return await File.ReadAllBytesAsync(Service.DownloadPath(result.Token)!);
        }
    }
    private static readonly string[] PublishedTables=["clips","local_videos","transcripts","studio_events","editor_projects","editor_variants","audio_assets","studio_documents"];
    private static void Empty(Fixture fixture){foreach(var table in PublishedTables)Assert.Equal(0,fixture.Count(table));}
    private static byte[] Zip(IEnumerable<(string Name,byte[] Data)> files)
    {
        using var output=new MemoryStream();using(var archive=new ZipArchive(output,ZipArchiveMode.Create,true))
            foreach(var file in files){using var entry=archive.CreateEntry(file.Name).Open();entry.Write(file.Data);}
        return output.ToArray();
    }
    private static List<(string Name,byte[] Data)> Members(byte[] package)
    {
        using var source=new MemoryStream(package);using var archive=new ZipArchive(source,ZipArchiveMode.Read);
        return archive.Entries.Select(entry=>{using var stream=entry.Open();using var bytes=new MemoryStream();stream.CopyTo(bytes);return(entry.FullName,bytes.ToArray());}).ToList();
    }
    private static byte[] ChangeManifest(byte[] package,Action<JsonNode> change)
    {
        var members=Members(package);var index=members.FindIndex(e=>e.Name=="manifest.json");var node=JsonNode.Parse(members[index].Data)!;change(node);
        members[index]=("manifest.json",Encoding.UTF8.GetBytes(node.ToJsonString()));return Zip(members);
    }

    [Fact]
    public async Task IndependentV2_TransitionAndCaptionBindingsRoundtripWithoutTimingChanges()
    {
        var source=new Fixture();var (media,_)=source.Seed();
        var identity=source.Studio.CreateIndependentProject("transition");
        var project=new EditorProject{Start=0,End=8,Fps=30,
            Timeline=[new("left",media.Id,0,4),new("right",media.Id,4,8)],
            Transitions=[new("dissolve","right",1,"cross-dissolve",.8,"center")],
            TimelineCaptions=[
                new(){Id="bound",Start=3.2,End=4.2,Text="bound cue",SourceClipId=media.Id,SequenceItemId="right",SourceCueIndex=0,SourceStart=4,SourceEnd=5},
                new(){Id="manual",Start=5,End=6,Text="manual cue",Manual=true,PositionX=.5,PositionY=.8}]};
        var before=JsonSerializer.Serialize(project);var original=source.Studio.GetIndependentProject(identity.Identity.Id);
        var exported=await source.Service.ExportAsync(identity.Identity.Id,"transition",project,1);
        using(var archive=ZipFile.OpenRead(source.Service.DownloadPath(exported.Token)!))
        {using var manifest=JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());Assert.Equal(2,manifest.RootElement.GetProperty("version").GetInt32());}
        var target=new Fixture();await using var input=File.OpenRead(source.Service.DownloadPath(exported.Token)!);
        var result=await target.Service.ImportAsync(input);
        var restored=EditorProjectCodec.Deserialize(target.Studio.GetIndependentProject(result.ClipId)!.Document.Json);
        var copiedMedia=Assert.Single(target.Store.Clips());
        Assert.NotEqual(media.Id,copiedMedia.Id);Assert.NotEqual(result.ClipId,copiedMedia.Id);
        Assert.All(restored.Timeline!,item=>Assert.Equal(copiedMedia.Id,item.ClipId));
        Assert.Equal(project.Transitions,restored.Transitions);
        Assert.Equal(new[]{"left","right"},restored.Timeline!.Select(item=>item.Id));
        Assert.Equal(project.TimelineCaptions![0] with{SourceClipId=copiedMedia.Id},restored.TimelineCaptions![0]);
        Assert.Equal(project.TimelineCaptions[1],restored.TimelineCaptions[1]);
        Assert.Equal(before,JsonSerializer.Serialize(project));Assert.Equal(original,source.Studio.GetIndependentProject(identity.Identity.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentV2_RoundtripKeepsProjectAndMediaIdentitiesSeparate(bool withMedia)
    {
        var source=new Fixture();var independent=source.Studio.CreateIndependentProject("empty",1920,1080,30);
        var project=withMedia?source.Seed().Project:EditorProjectCodec.Deserialize(independent.Document.Json);
        var exported=await source.Service.ExportAsync(independent.Identity.Id,"portable",project,1);
        var bytes=await File.ReadAllBytesAsync(source.Service.DownloadPath(exported.Token)!);
        var members=Members(bytes);
        Assert.Equal(2,JsonNode.Parse(members.Single(m=>m.Name=="manifest.json").Data)!["version"]!.GetValue<int>());
        if(!withMedia)Assert.DoesNotContain(members,m=>m.Name.StartsWith("media/"));
        var target=new Fixture();using var input=new MemoryStream(bytes);
        var result=await target.Service.ImportAsync(input);
        var imported=target.Studio.GetIndependentProject(result.ClipId)!;
        Assert.Equal("portable",imported.Identity.Name);Assert.NotEqual(independent.Identity.Id,result.ClipId);
        Assert.Equal(withMedia?1:0,target.Count("clips"));Assert.Equal(1,target.Count("editor_project_catalog"));
        var restored=EditorProjectCodec.Deserialize(imported.Document.Json);
        if(withMedia)Assert.NotEqual(result.ClipId,restored.Timeline![0].ClipId);
        else Assert.Empty(restored.Timeline!);
        var corrupted=ChangeManifest(bytes,n=>n["version"]=1);
        var rejected=new Fixture();using var bad=new MemoryStream(corrupted);
        await Assert.ThrowsAsync<InvalidDataException>(()=>rejected.Service.ImportAsync(bad));
        Assert.Equal(0,rejected.Count("editor_project_catalog"));Empty(rejected);
    }

    [Fact]
    public async Task Package_RoundtripIntoNewDatabasePreservesChineseNameAndIndependentCandidatesAndSubtitles()
    {
        var source=new Fixture();var (old,project)=source.Seed();var original=source.Studio.GetProjectRecord(old.Id);var bytes=await source.Export(old,project);
        var target=new Fixture();Empty(target);using var input=new MemoryStream(bytes);var result=await target.Service.ImportAsync(input);
        Assert.Equal("中文便携工程",result.Name);Assert.Equal(1,result.Revision);Assert.NotEqual(old.Id,result.ClipId);
        Assert.DoesNotContain(target.Store.Clips(),c=>c.Id==old.Id||c.EventId==old.EventId);
        var clip=Assert.Single(target.Store.Clips());Assert.NotEqual(old.Path,clip.Path);Assert.NotEqual(old.SrtPath,clip.SrtPath);
        Assert.Equal(File.ReadAllBytes(old.Path),File.ReadAllBytes(clip.Path));
        Assert.Equal(source.Store.TranscriptJson(old.EventId),target.Store.TranscriptJson(clip.EventId));
        var restored=EditorProjectCodec.Deserialize(target.Studio.Project(result.ClipId)!);EditorRenderService.Validate(restored);
        Assert.Equal(result.ClipId,restored.Timeline![0].ClipId);Assert.Equal("编辑后的字幕",restored.CaptionOverrides![result.ClipId][0].Text);
        var candidates=target.Studio.Events(result.ClipId);Assert.Equal(5,candidates.Count);
        Assert.All(candidates,c=>{Assert.Equal(result.ClipId,c.ClipId);Assert.Equal("ai",c.Source);Assert.DoesNotContain(source.Studio.Events(old.Id),o=>o.Id==c.Id);});
        Assert.Equal(candidates.Single(c=>c.Title=="候选0").Id,restored.Timeline[0].EventPlacement!.EventId);
        Assert.Equal(result.ClipId,restored.Timeline[0].EventPlacement!.SourceId);
        Assert.Equal("中文便携工程",Assert.Single(target.Studio.LocalVideos()).Title);
        Assert.Equal(source.Studio.Document("ai-advice",old.Id),target.Studio.Document("ai-advice",result.ClipId));
        using(var ranking=JsonDocument.Parse(target.Studio.Document("clip-ranking",result.ClipId)!))Assert.Equal(result.ClipId,ranking.RootElement.GetProperty("ClipId").GetString());
        target.Store.SaveTranscript(clip.EventId,"{\"segments\":[]}",clip.SrtPath!);File.WriteAllText(clip.SrtPath!,"只改副本");
        target.Studio.SaveProject(result.ClipId,JsonSerializer.Serialize(restored with {End=7}));
        Assert.Equal(original,source.Studio.GetProjectRecord(old.Id));Assert.Contains("独立字幕",File.ReadAllText(old.SrtPath!));
        Assert.Equal(5,source.Studio.Events(old.Id).Count);
        var reopened=new StudioStore(target.Store.DatabasePath);Assert.Equal(7,EditorProjectCodec.Deserialize(reopened.Project(result.ClipId)!).End);
    }

    [Fact]
    public async Task Package_AssetsVariantsAndWhitelistedAssetDocumentsReceiveNewIdentities()
    {
        var source=new Fixture();var (clip,project)=source.Seed();
        var music=Path.Combine(source.Root,"music.wav");File.WriteAllBytes(music,[7,8,9]);
        var effect=Path.Combine(source.Root,"effect.wav");File.WriteAllBytes(effect,[3,2,1]);
        source.Studio.SaveAsset(new("music-old","中文配乐","music","原创",music,false));source.Studio.SaveAsset(new("effect-old","效果","effect","原创",effect,false));
        source.Studio.SaveDocument("music-analysis","music-old","{\"beats\":[1,2]}");source.Studio.SaveDocument("audio-attribution","music-old","{\"author\":\"原作者\"}");
        project=project with {MusicAssetId="music-old"};
        source.Studio.SaveVariant(new("variant-old",clip.Id,"方案甲",JsonSerializer.Serialize(project with {EffectAssetId="effect-old"}),DateTimeOffset.UtcNow));
        var bytes=await source.Export(clip,project);var target=new Fixture();using var input=new MemoryStream(bytes);var result=await target.Service.ImportAsync(input);
        var assets=target.Studio.Assets();Assert.Equal(2,assets.Count);Assert.DoesNotContain(assets,a=>a.Id is "music-old" or "effect-old");
        var restored=EditorProjectCodec.Deserialize(target.Studio.Project(result.ClipId)!);var newMusic=assets.Single(a=>a.Kind=="music");
        Assert.Equal(newMusic.Id,restored.MusicAssetId);Assert.NotEqual(music,newMusic.Path);Assert.Equal(File.ReadAllBytes(music),File.ReadAllBytes(newMusic.Path));
        Assert.Equal(source.Studio.Document("music-analysis","music-old"),target.Studio.Document("music-analysis",newMusic.Id));
        Assert.Equal(source.Studio.Document("audio-attribution","music-old"),target.Studio.Document("audio-attribution",newMusic.Id));
        var variant=Assert.Single(target.Studio.Variants(result.ClipId));Assert.NotEqual("variant-old",variant.Id);Assert.Equal("方案甲",variant.Name);
        var restoredVariant=EditorProjectCodec.Deserialize(variant.ProjectJson);Assert.Equal(newMusic.Id,restoredVariant.MusicAssetId);
        Assert.Equal(assets.Single(a=>a.Kind=="effect").Id,restoredVariant.EffectAssetId);Assert.Equal(result.ClipId,restoredVariant.Timeline![0].ClipId);
        Assert.Equal(restored.Timeline![0].EventPlacement,restoredVariant.Timeline[0].EventPlacement);
    }

    [Fact]
    public async Task Package_UnknownAssetDocumentIsNotSilentlyDiscarded()
    {
        var f=new Fixture();var (clip,project)=f.Seed();var path=Path.Combine(f.Root,"m.wav");File.WriteAllBytes(path,[1]);
        f.Studio.SaveAsset(new("music","m","music","s",path,false));f.Studio.SaveDocument("unknown-asset-data","music","{}");
        await Assert.ThrowsAsync<NotSupportedException>(()=>f.Service.ExportAsync(clip.Id,"工程",project with {MusicAssetId="music"},1));
        Assert.Equal(1,f.Count("clips"));Assert.Equal(1,f.Count("editor_projects"));
    }

    [Fact]
    public async Task Package_OrdinaryZipWithoutManifestIsRejected()
    {
        var f=new Fixture();using var input=new MemoryStream(Zip([("video.mp4",new byte[]{1,2,3})]));
        await Assert.ThrowsAsync<InvalidDataException>(()=>f.Service.ImportAsync(input));Empty(f);
    }
    [Theory]
    [InlineData("/absolute.mp4")]
    [InlineData("C:/absolute.mp4")]
    [InlineData("media\\bad.mp4")]
    [InlineData("../escape.mp4")]
    [InlineData("media/../escape.mp4")]
    [InlineData("MANIFEST.JSON")]
    public async Task Package_UnsafeOrCaseDuplicateMemberRejectedBeforePublication(string path)
    {
        var source=new Fixture();var (clip,project)=source.Seed();var members=Members(await source.Export(clip,project));members.Add((path,[1]));
        var target=new Fixture();using var input=new MemoryStream(Zip(members));await Assert.ThrowsAsync<InvalidDataException>(()=>target.Service.ImportAsync(input));Empty(target);
    }
    [Fact]
    public async Task Package_HashMismatchIsRejected()
    {
        var source=new Fixture();var (clip,project)=source.Seed();var members=Members(await source.Export(clip,project));var index=members.FindIndex(m=>m.Name.StartsWith("media/"));
        var content=members[index].Data.ToArray();content[0]^=0xff;members[index]=(members[index].Name,content);
        var target=new Fixture();using var input=new MemoryStream(Zip(members));await Assert.ThrowsAsync<InvalidDataException>(()=>target.Service.ImportAsync(input));Empty(target);
    }
    [Fact]
    public async Task Package_UndeclaredMemberIsRejected()
    {
        var source=new Fixture();var (clip,project)=source.Seed();var members=Members(await source.Export(clip,project));members.Add(("media/extra.mp4",[1,2]));
        var target=new Fixture();using var input=new MemoryStream(Zip(members));await Assert.ThrowsAsync<InvalidDataException>(()=>target.Service.ImportAsync(input));Empty(target);
    }
    [Fact]
    public async Task Package_UnknownVersionIsRejected()
    {
        var source=new Fixture();var (clip,project)=source.Seed();var bytes=ChangeManifest(await source.Export(clip,project),m=>m["version"]=999);
        var target=new Fixture();using var input=new MemoryStream(bytes);await Assert.ThrowsAsync<NotSupportedException>(()=>target.Service.ImportAsync(input));Empty(target);
    }
    [Fact]
    public async Task Package_DatabaseFailureRollsBackEveryPublishedTable()
    {
        var source=new Fixture();var (clip,project)=source.Seed();var bytes=await source.Export(clip,project);var original=source.Studio.GetProjectRecord(clip.Id);
        var target=new Fixture();target.Execute("CREATE TRIGGER reject_package BEFORE INSERT ON editor_projects BEGIN SELECT RAISE(ABORT,'fixture rollback'); END;");
        using var input=new MemoryStream(bytes);await Assert.ThrowsAsync<SqliteException>(()=>target.Service.ImportAsync(input));Empty(target);
        Assert.Equal(original,source.Studio.GetProjectRecord(clip.Id));Assert.Equal(5,source.Studio.Events(clip.Id).Count);
    }
    private static byte[] ChangeMetadata(byte[] package,string member,Func<JsonNode?,JsonNode?> change)
    {
        var members=Members(package);var index=members.FindIndex(m=>m.Name==member);
        var bytes=Encoding.UTF8.GetBytes(change(JsonNode.Parse(members[index].Data))?.ToJsonString()??"null");
        members[index]=(member,bytes);
        if(member!="manifest.json")
        {
            var manifestIndex=members.FindIndex(m=>m.Name=="manifest.json");var manifest=JsonNode.Parse(members[manifestIndex].Data)!;
            var declaration=manifest["files"]!.AsArray().Single(f=>f!["path"]!.GetValue<string>()==member)!;
            declaration["bytes"]=bytes.Length;declaration["sha256"]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            members[manifestIndex]=("manifest.json",Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        }
        return Zip(members);
    }

    [Fact]
    public async Task Package_SymlinkMemberIsRejectedBeforePublication()
    {
        var source=new Fixture();var (clip,project)=source.Seed();var members=Members(await source.Export(clip,project));
        using var package=new MemoryStream();
        using(var archive=new ZipArchive(package,ZipArchiveMode.Create,true))
            foreach(var member in members)
            {
                var entry=archive.CreateEntry(member.Name);
                if(member.Name.StartsWith("media/",StringComparison.Ordinal))entry.ExternalAttributes=unchecked((int)0xA0000000);
                using var output=entry.Open();output.Write(member.Data);
            }
        package.Position=0;var target=new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(()=>target.Service.ImportAsync(package));Empty(target);
    }

    [Theory]
    [InlineData("manifest-root")]
    [InlineData("manifest-files")]
    [InlineData("manifest-file-element")]
    [InlineData("manifest-missing-format")]
    [InlineData("manifest-missing-files")]
    [InlineData("associations-root")]
    [InlineData("associations-assets")]
    [InlineData("associations-asset-element")]
    [InlineData("associations-snapshot")]
    [InlineData("associations-missing-snapshot")]
    [InlineData("associations-sources")]
    [InlineData("associations-source-element")]
    public async Task Package_NullOrMissingRequiredMetadataFailsWithExplicitDataError(string defect)
    {
        var source=new Fixture();var (clip,project)=source.Seed();var bytes=await source.Export(clip,project);
        var member=defect.StartsWith("manifest-",StringComparison.Ordinal)?"manifest.json":"associations.json";
        bytes=ChangeMetadata(bytes,member,node=>
        {
            switch(defect)
            {
                case "manifest-root":case "associations-root":return null;
                case "manifest-files":node!["files"]=null;break;
                case "manifest-file-element":node!["files"]!.AsArray().Add((JsonNode?)null);break;
                case "manifest-missing-format":node!.AsObject().Remove("format");break;
                case "manifest-missing-files":node!.AsObject().Remove("files");break;
                case "associations-assets":node!["assets"]=null;break;
                case "associations-asset-element":node!["assets"]!.AsArray().Add((JsonNode?)null);break;
                case "associations-snapshot":node!["snapshot"]=null;break;
                case "associations-missing-snapshot":node!.AsObject().Remove("snapshot");break;
                case "associations-sources":node!["snapshot"]!["sources"]=null;break;
                case "associations-source-element":node!["snapshot"]!["sources"]!.AsArray().Add((JsonNode?)null);break;
            }
            return node;
        });
        var target=new Fixture();using var input=new MemoryStream(bytes);
        var error=await Record.ExceptionAsync(()=>target.Service.ImportAsync(input));
        Assert.True(error is JsonException or InvalidDataException,$"Expected explicit JSON/data error for {defect}, got {error}");Empty(target);
    }

    [Fact]
    public async Task Package_TwoResourcesCannotShareOneMutableMediaMember()
    {
        var source=new Fixture();var (clip,project)=source.Seed();var bytes=await source.Export(clip,project);
        bytes=ChangeMetadata(bytes,"associations.json",node=>
        {
            var owner=node!["snapshot"]!["sources"]![0]!;
            owner["srtPath"]=owner["path"]!.GetValue<string>();return node;
        });
        var target=new Fixture();using var input=new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(()=>target.Service.ImportAsync(input));Empty(target);
    }

    [Fact]
    public async Task Package_UnknownDeclaredResourceTypeIsRejected()
    {
        var source=new Fixture();var (clip,project)=source.Seed();var members=Members(await source.Export(clip,project));
        var content=new byte[]{1,2,3};const string name="unknown/resource.bin";members.Add((name,content));
        var index=members.FindIndex(m=>m.Name=="manifest.json");var manifest=JsonNode.Parse(members[index].Data)!;
        manifest["files"]!.AsArray().Add(new JsonObject { ["path"]=name,["bytes"]=content.Length,
            ["sha256"]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)) });
        members[index]=("manifest.json",Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        var target=new Fixture();using var input=new MemoryStream(Zip(members));
        await Assert.ThrowsAsync<InvalidDataException>(()=>target.Service.ImportAsync(input));Empty(target);
    }

    [Fact]
    public async Task Package_VariantReferringToDeletedEventFailsBeforePublishingDownload()
    {
        var f=new Fixture();var (clip,project)=f.Seed();
        var variant=project with { Timeline=[project.Timeline![0] with
            { EventPlacement=new(){SourceId=clip.Id,EventId="deleted-candidate"} }] };
        f.Studio.SaveVariant(new("invalid-variant",clip.Id,"旧方案",JsonSerializer.Serialize(variant),DateTimeOffset.UtcNow));
        var original=f.Studio.GetProjectRecord(clip.Id);
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>f.Service.ExportAsync(clip.Id,"拒绝损坏方案",project,1));
        Assert.Contains("deleted-candidate",error.Message);
        var downloads=Path.Combine(f.Root,"packages","downloads");
        Assert.True(!Directory.Exists(downloads)||!Directory.EnumerateFiles(downloads,"*.lcproj").Any(),"invalid event provenance must not publish a completed package");
        Assert.Equal(original,f.Studio.GetProjectRecord(clip.Id));Assert.Equal(5,f.Studio.Events(clip.Id).Count);
    }

}

