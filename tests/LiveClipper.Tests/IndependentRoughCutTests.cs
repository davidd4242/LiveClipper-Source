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

public sealed class IndependentRoughCutTests
{
    private sealed class Fixture:IDisposable
    {
        public string Root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"IndependentRoughCut-"+Guid.NewGuid().ToString("N"))).FullName;
        public Store Store;public StudioStore Studio;public Clip Source;public IndependentEditorProject Independent;public EditorProject Snapshot;public IndependentRoughCutService Service;
        public Fixture()
        {
            Store=new(Path.Combine(Root,"test.db"));Store.Migrate();Studio=new(Store.DatabasePath);Studio.Migrate();
            var path=Path.Combine(Root,"source.mp4");File.WriteAllBytes(path,[1,2,3]);
            Source=new("source","event",path,null,DateTimeOffset.UtcNow);Store.SaveClip(Source);
            Studio.SaveLocalVideo(new(Source.Id,path,"source",Root,Source.CreatedAt));
            Studio.SaveProject(Source.Id,JsonSerializer.Serialize(new EditorProject{Start=0,End=10,CaptionOverrides=new(){[Source.Id]=[new(0,3,"edited-source"),new(4,7,"second-source")]}}));
            Independent=Studio.CreateIndependentProject("input");Snapshot=EditorProjectCodec.Deserialize(Independent.Document.Json);
            Studio.SaveEvent(new("candidate",Source.Id,0,7,"topic","summary","hook","ai"));
            Service=new(Store,Studio,Path.Combine(Root,"derived"));
        }
        public RoughCutPreview Preview()=>Service.Preview(Independent.Identity.Id,Source.Id,Snapshot,10);
        public Task<RoughCutCopyResult> Create(RoughCutPreview? preview=null,long? revision=null)
        {
            var p=preview??Preview();return Service.CreateAsync(Independent.Identity.Id,Source.Id,"derived",Snapshot,10,[new(0,3)],[new(0,7)],p.SourceSignature,p.CandidateHash,p.TranscriptCacheKey,revision??Independent.Document.Revision);
        }
        public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(Root,true);}
    }
    [Fact]
    public void ExplicitIndependentIdentityAndSourceEditedTranscriptAreRequired()
    {
        using var f=new Fixture();Assert.Equal("edited-source",f.Preview().Transcript[0].Text);
        Assert.Throws<ArgumentException>(()=>f.Service.Preview(f.Source.Id,f.Source.Id,f.Snapshot,10));
        Assert.Throws<ArgumentException>(()=>f.Service.Preview(f.Independent.Identity.Id,"missing",f.Snapshot,10));
        var edited=f.Snapshot with {CaptionOverrides=new(){[f.Source.Id]=[new(1,2,"project-override")]}};
        Assert.Equal("project-override",f.Service.Preview(f.Independent.Identity.Id,f.Source.Id,edited,10).Transcript[0].Text);
        var empty=edited with {CaptionOverrides=new(){[f.Source.Id]=[]}};
        Assert.Empty(f.Service.Preview(f.Independent.Identity.Id,f.Source.Id,empty,10).Transcript);
    }
    [Fact]
    public async Task DerivedResultPreservesOriginalAndRestoresUsingRemappedSource()
    {
        using var f=new Fixture();var sourceBefore=f.Studio.GetProjectRecord(f.Source.Id);var projectBefore=f.Studio.GetProjectRecord(f.Independent.Identity.Id);
        var result=await f.Create();Assert.NotEqual(f.Source.Id,result.ClipId);Assert.NotEqual(f.Independent.Identity.Id,result.ClipId);
        Assert.NotNull(f.Studio.GetIndependentProject(result.ClipId));
        var generated=EditorProjectCodec.Deserialize(f.Studio.Project(result.ClipId)!);var newSource=Assert.Single(generated.Timeline!).ClipId;
        Assert.NotEqual(f.Source.Id,newSource);Assert.Equal("edited-source",Assert.Single(generated.TimelineCaptions!).Text);
        Assert.Equal("edited-source",f.Service.Preview(result.ClipId,newSource,generated,10).Transcript[0].Text);
        Assert.Equal(sourceBefore,f.Studio.GetProjectRecord(f.Source.Id));Assert.Equal(projectBefore,f.Studio.GetProjectRecord(f.Independent.Identity.Id));
        var restored=new RoughCutCopyService(f.Store,f.Studio,"").Restore(result.ClipId,1);
        Assert.Equal(2,restored.Revision);Assert.Equal(7,restored.Project.End);Assert.Equal(newSource,Assert.Single(restored.Project.Timeline!).ClipId);
        Assert.Equal(2,restored.Project.TimelineCaptions!.Length);
        Assert.Equal("edited-source",f.Service.Preview(result.ClipId,newSource,restored.Project,10).Transcript[0].Text);
        Assert.Throws<ProjectCopyConflictException>(()=>new RoughCutCopyService(f.Store,f.Studio,"").Restore(result.ClipId,2));
        Assert.Equal(sourceBefore,f.Studio.GetProjectRecord(f.Source.Id));
    }
    [Fact]
    public async Task UnknownAnalysisAndVersionsAreRetainedOnlyOnSourceOrdinaryCopyStillRejects()
    {
        using var f=new Fixture();f.Studio.SaveDocument("cut-suggestion",f.Source.Id,"{\"old\":true}");f.Studio.SaveDocument("future-user-analysis",f.Source.Id,"{}");
        using(var db=new SqliteConnection("Data Source="+f.Store.DatabasePath))
        {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO editor_variants(id,clip_id,name,project_json,created_at) VALUES('v','source','saved',$json,$at)";cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(new EditorProject{Start=0,End=10}));cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}
        var sourceProject=EditorProjectCodec.Deserialize(f.Studio.Project(f.Source.Id)!);
        await Assert.ThrowsAsync<NotSupportedException>(()=>new ProjectCopyService(f.Studio,Path.Combine(f.Root,"ordinary")).CopyAsync(f.Source.Id,"ordinary",sourceProject,f.Studio.GetProjectRecord(f.Source.Id)!.Revision));
        var result=await f.Create();var generated=EditorProjectCodec.Deserialize(f.Studio.Project(result.ClipId)!);var newSource=generated.Timeline![0].ClipId;
        Assert.Null(f.Studio.Document("cut-suggestion",newSource));Assert.Null(f.Studio.Document("future-user-analysis",newSource));Assert.Empty(f.Studio.Events(newSource));
        Assert.Equal("{\"old\":true}",f.Studio.Document("cut-suggestion",f.Source.Id));Assert.Single(f.Studio.Events(f.Source.Id));
        using var verify=new SqliteConnection("Data Source="+f.Store.DatabasePath);verify.Open();using var count=verify.CreateCommand();count.CommandText="SELECT COUNT(*) FROM editor_variants";Assert.Equal(1L,count.ExecuteScalar());
    }
    [Fact]
    public async Task StaleRevisionTranscriptAndCandidatesDoNotPublish()
    {
        using var f=new Fixture();var p=f.Preview();
        await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>f.Create(p,99));
        f.Studio.SaveEvent(new("candidate-2",f.Source.Id,7,9,"topic2","","","ai"));
        await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>f.Create(p));
        p=f.Preview();f.Studio.SaveProject(f.Source.Id,JsonSerializer.Serialize(new EditorProject{Start=0,End=10,CaptionOverrides=new(){[f.Source.Id]=[new(0,3,"changed")]}}));
        await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>f.Create(p));Assert.Single(f.Studio.IndependentProjects());
    }
    [Fact]
    public async Task SourceProjectChangeDuringCopyIsIncludedInPublicationComparison()
    {
        using var f=new Fixture();var project=new EditorProject{Start=0,End=3,Fps=30,Timeline=[new("item",f.Source.Id,0,3)]};
        await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>new ProjectCopyService(f.Studio,Path.Combine(f.Root,"concurrent")).CopyAsync(
            f.Independent.Identity.Id,"not-published",project,1,generatedSourceOnly:true,
            validateBeforePublish:()=>f.Studio.SaveProject(f.Source.Id,JsonSerializer.Serialize(new EditorProject{Start=0,End=10,CaptionOverrides=new(){[f.Source.Id]=[new(0,3,"changed-after-capture")]}}))));
        Assert.Single(f.Studio.IndependentProjects());Assert.Single(f.Store.Clips());
    }
    [Fact]
    public async Task HttpRoutesKeepProjectSourceIdentityAndRequireProjectPreconditions()
    {
        using var f=new Fixture();var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Testing"});
        builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");await using var app=builder.Build();
        StudioEndpoints.Map(app,f.Store,f.Studio,null!,null!,null!,null!,null!,null!,null!,new LiveSettings{StorageRoot=f.Root,ExportOutputDirectory=Path.Combine(f.Root,"output")});
        await app.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        var p=f.Preview();var path=$"/api/studio/projects/{f.Independent.Identity.Id}/sources/{f.Source.Id}";
        using(var response=await client.PostAsJsonAsync(path+"/roughcut-preview/verify",new {project=f.Snapshot,p.SourceSignature,p.CandidateHash,p.TranscriptCacheKey}))
            Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        using(var response=await client.PostAsJsonAsync($"/api/studio/projects/{f.Source.Id}/sources/{f.Source.Id}/roughcut-preview/verify",new {project=f.Snapshot,p.SourceSignature,p.CandidateHash,p.TranscriptCacheKey}))
            Assert.Equal(System.Net.HttpStatusCode.BadRequest,response.StatusCode);
        var loaded=await client.GetFromJsonAsync<JsonElement>($"/api/studio/clips/{f.Independent.Identity.Id}/editor");
        foreach(var suffix in new[]{"roughcut-copy","analyze"})
        {
            var body=new {name="result",project=f.Snapshot,connection=new AiConnection("ollama","http://127.0.0.1:11434/","never-called",null),ranges=new[]{new RoughCutSourceRange(0,3)},beforeRanges=new[]{new RoughCutSourceRange(0,7)},p.SourceSignature,p.CandidateHash,p.TranscriptCacheKey};
            using(var response=await client.PostAsJsonAsync(path+"/"+suffix,body))Assert.Equal((System.Net.HttpStatusCode)428,response.StatusCode);
            using var request=new HttpRequestMessage(HttpMethod.Post,path+"/"+suffix){Content=JsonContent.Create(body)};
            request.Headers.Add("X-Base-Revision","999");request.Headers.Add("X-Editor-Epoch",loaded.GetProperty("epoch").GetString());
            using var stale=await client.SendAsync(request);Assert.Equal(System.Net.HttpStatusCode.Conflict,stale.StatusCode);
        }
        Assert.Single(f.Studio.IndependentProjects());Assert.Single(f.Store.Clips());await app.StopAsync();
    }

    [Fact]
    public async Task AnalysisUsesSameEffectiveSnapshotAndRejectsStaleCompletionHonestly()
    {
        using var f=new Fixture();var calls=0;
        Task<AiAnalysisResult> Analyze(Clip source,EditorProject snapshot,CancellationToken token)
        {
            calls++;Assert.Equal(f.Source.Id,source.Id);Assert.Equal("edited-source",snapshot.CaptionOverrides![source.Id][0].Text);
            return Task.FromResult(new AiAnalysisResult([],new("",[],[]),null));
        }
        await f.Service.AnalyzeAsync(f.Independent.Identity.Id,f.Source.Id,f.Snapshot,1,Analyze);Assert.Equal(1,calls);
        await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>f.Service.AnalyzeAsync(f.Independent.Identity.Id,f.Source.Id,f.Snapshot,99,Analyze));Assert.Equal(1,calls);
        var own=f.Snapshot with {CaptionOverrides=new(){[f.Source.Id]=[new(1,2,"own-effective")]}};
        await f.Service.AnalyzeAsync(f.Independent.Identity.Id,f.Source.Id,own,1,(source,snapshot,token)=>
        {Assert.Equal("own-effective",snapshot.CaptionOverrides![source.Id][0].Text);return Task.FromResult(new AiAnalysisResult([],new("",[],[]),null));});
        var error=await Assert.ThrowsAsync<ProjectCopyConflictException>(()=>f.Service.AnalyzeAsync(f.Independent.Identity.Id,f.Source.Id,f.Snapshot,1,(source,snapshot,token)=>
        {
            f.Studio.SaveDocument("ai-advice",source.Id,"{}");
            f.Studio.SaveProject(source.Id,JsonSerializer.Serialize(new EditorProject{Start=0,End=10,CaptionOverrides=new(){[source.Id]=[new(0,2,"changed")]}}));
            return Task.FromResult(new AiAnalysisResult([],new("",[],[]),null));
        }));
        Assert.Contains("缓存可能已更新",error.Message);Assert.Equal("{}",f.Studio.Document("ai-advice",f.Source.Id));
        Assert.Equal(1,f.Studio.GetIndependentProject(f.Independent.Identity.Id)!.Document.Revision);
    }

    [Fact]
    public async Task ResultEditedAfterGenerationCannotBeRestoredOverNewWork()
    {
        using var f=new Fixture();var result=await f.Create();var project=EditorProjectCodec.Deserialize(f.Studio.Project(result.ClipId)!);
        f.Studio.SaveProject(result.ClipId,JsonSerializer.Serialize(project with {Markers=[1]}));var record=f.Studio.GetProjectRecord(result.ClipId)!;
        Assert.Throws<ProjectCopyConflictException>(()=>new RoughCutCopyService(f.Store,f.Studio,"").Restore(result.ClipId,(int)record.Revision));
        Assert.Equal(record,f.Studio.GetProjectRecord(result.ClipId));
    }
}
