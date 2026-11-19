using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LiveClipper.Core;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Tests;

public sealed class IndependentEditorProjectTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"lc-independent-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(root,"liveclipper.db");
    private readonly StudioStore studio;
    public IndependentEditorProjectTests()
    {
        Directory.CreateDirectory(root);new Store(Database).Migrate();studio=new StudioStore(Database);studio.Migrate();
    }
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    private long Count(string table)
    {
        using var db=new SqliteConnection($"Data Source={Database};Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT COUNT(*) FROM {table}";return (long)cmd.ExecuteScalar()!;
    }
    [Fact] public void CreationIsIndependentAndSurvivesReopenWithoutMediaOrEvents()
    {
        var files=Directory.GetFiles(root).Order().ToArray();
        var created=studio.CreateIndependentProject(" Empty ",1080,1920,25);
        Assert.Equal("Empty",created.Identity.Name);Assert.Equal(1,created.Document.Revision);
        var p=EditorProjectCodec.Deserialize(created.Document.Json);Assert.Empty(p.Timeline!);Assert.Empty(p.TimelineCaptions!);
        Assert.Equal(0,Count("clips"));Assert.Equal(0,Count("highlight_events"));Assert.Equal(0,Count("studio_events"));Assert.Equal(0,Count("local_videos"));
        Assert.Equal(files,Directory.GetFiles(root).Order().ToArray());
        var reopened=new StudioStore(Database);reopened.Migrate();Assert.Equal(created,reopened.GetIndependentProject(created.Identity.Id));
        Assert.Equal(1,Count("editor_project_catalog"));
    }
    [Fact] public void ExplicitEmptyTimelineCanBeSavedButNotRendered()
    {
        var created=studio.CreateIndependentProject("Empty");var p=EditorProjectCodec.Deserialize(created.Document.Json);
        EditorRenderService.ValidateForSave(p,true);
        EditorRenderService.Validate(p);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForRender(p));
        var saved=studio.SaveIndependentProject(created.Identity.Id,p,1);Assert.True(saved.Ok);Assert.Equal(2,saved.Revision);
        var conflict=studio.SaveIndependentProject(created.Identity.Id,p,1);Assert.False(conflict.Ok);Assert.Equal(2,conflict.Revision);
        Assert.Throws<ArgumentException>(()=>studio.SaveIndependentProject(created.Identity.Id,p with{Timeline=null},2));
    }
    [Fact] public void OldProjectsStayOutsideCatalogAndKeepLegacySourceSemantics()
    {
        studio.SaveProject("legacy",System.Text.Json.JsonSerializer.Serialize(new EditorProject{Start=0,End=3}));
        Assert.Null(studio.GetIndependentProject("legacy"));
        EditorRenderService.Validate(EditorProjectCodec.Deserialize(studio.Project("legacy")!));
        var before=studio.Project("legacy");studio.Migrate();Assert.Equal(before,studio.Project("legacy"));
        Assert.Equal(0,Count("editor_project_catalog"));
    }
    [Fact] public void InvalidCreationDoesNotPublishAnyRows()
    {
        Assert.Throws<ArgumentException>(()=>studio.CreateIndependentProject(" "));
        Assert.Throws<ArgumentOutOfRangeException>(()=>studio.CreateIndependentProject("x",0,1080));
        Assert.Throws<ArgumentOutOfRangeException>(()=>studio.CreateIndependentProject("x",1920,1080,29));
        Assert.Equal(0,Count("editor_project_catalog"));Assert.Equal(0,Count("editor_projects"));
    }
    [Fact] public async Task RealHttpCreateReadSaveConflictAndSourceDependentRejection()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Testing"});
        builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();
        // Null media service ensures empty load never probes a made-up source.
        StudioEndpoints.Map(app,new Store(Database),studio,null!,null!,null!,null!,new EditorRenderService(new Store(Database),studio,new MediaTools("missing-ffmpeg"),root),null!,null!,new LiveSettings{StorageRoot=root});
        await app.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        using var created=await client.PostAsJsonAsync("/api/studio/projects",new{name="Blank HTTP",width=1920,height=1080,fps=30});
        Assert.True(created.IsSuccessStatusCode,await created.Content.ReadAsStringAsync());
        var identity=await created.Content.ReadFromJsonAsync<JsonElement>();var id=identity.GetProperty("id").GetString()!;
        var payload=await client.GetFromJsonAsync<JsonElement>($"/api/studio/clips/{id}/editor");
        Assert.Equal(JsonValueKind.Null,payload.GetProperty("clip").ValueKind);
        Assert.Equal(id,payload.GetProperty("projectIdentity").GetProperty("id").GetString());
        Assert.Equal(0,payload.GetProperty("duration").GetInt32());
        var plan=payload.GetProperty("project");Assert.Equal(0,plan.GetProperty("timeline").GetArrayLength());
        var list=await client.GetFromJsonAsync<JsonElement>("/api/studio/projects");Assert.Equal(1,list.GetArrayLength());
        using var sessionResponse=await client.PostAsync($"/api/studio/clips/{id}/sessions",null);
        Assert.True(sessionResponse.IsSuccessStatusCode,await sessionResponse.Content.ReadAsStringAsync());
        var session=await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var ended=await client.PostAsync($"/api/studio/clips/{id}/sessions/{session.GetProperty("sessionId").GetString()}/end",null);
        Assert.True(ended.IsSuccessStatusCode,await ended.Content.ReadAsStringAsync());
        async Task<HttpResponseMessage> Save(int revision,object body)
        {
            var request=new HttpRequestMessage(HttpMethod.Put,$"/api/studio/clips/{id}/editor"){Content=JsonContent.Create(body)};
            request.Headers.Add("X-Base-Revision",revision.ToString());request.Headers.Add("X-Editor-Epoch",payload.GetProperty("epoch").GetString());
            return await client.SendAsync(request);
        }
        using var saved=await Save(1,plan);Assert.True(saved.IsSuccessStatusCode,await saved.Content.ReadAsStringAsync());
        using var conflict=await Save(1,plan);Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);
        var missing=EditorProjectCodec.Deserialize(plan) with{Timeline=[new("missing","not-a-source",0,1)]};
        using var refused=await Save(2,missing);Assert.Equal(HttpStatusCode.BadRequest,refused.StatusCode);
        foreach(var route in new[]{"render","jianying-export","premiere-export","commands"})
        {
            object body=route=="render"?plan:route=="commands"?new{commands=Array.Empty<object>()}:new{name="empty",project=plan};
            using var exportRequest=new HttpRequestMessage(HttpMethod.Post,$"/api/studio/clips/{id}/{route}"){Content=JsonContent.Create(body)};
            exportRequest.Headers.Add("X-Base-Revision","2");exportRequest.Headers.Add("X-Editor-Epoch",payload.GetProperty("epoch").GetString());
            using var response=await client.SendAsync(exportRequest);
            Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
            Assert.Contains(route=="jianying-export"?"jianying_export_unsupported":route=="premiere-export"?"premiere_export_unsupported":route=="render"?"editor_render_unsupported":"independent_project_source_required",await response.Content.ReadAsStringAsync());
        }
        var sequenceSettings=EditorProjectCodec.Deserialize(plan) with{CanvasWidth=1080,CanvasHeight=1920,Fps=25};
        using var settingsSaved=await Save(2,sequenceSettings);
        Assert.True(settingsSaved.IsSuccessStatusCode,await settingsSaved.Content.ReadAsStringAsync());
        var changed=await client.GetFromJsonAsync<JsonElement>($"/api/studio/clips/{id}/editor");
        Assert.Equal(1080,changed.GetProperty("width").GetInt32());Assert.Equal(1920,changed.GetProperty("height").GetInt32());
        Assert.Equal(25,changed.GetProperty("projectIdentity").GetProperty("fps").GetInt32());
        Assert.Equal(25,changed.GetProperty("project").GetProperty("fps").GetInt32());
        using var undoSaved=await Save(3,sequenceSettings with{CanvasWidth=1920,CanvasHeight=1080,Fps=30});
        Assert.True(undoSaved.IsSuccessStatusCode,await undoSaved.Content.ReadAsStringAsync());
        Assert.Equal(0,Count("clips"));Assert.Equal(0,Count("highlight_events"));Assert.Empty(studio.Renders());
        await app.StopAsync();EditorRevisionStore.Forget(id);
    }

    [Fact] public void SequenceSettingsSaveConflictUndoAndReopenAreAtomic()
    {
        var original=studio.CreateIndependentProject("Sequence",1920,1080,30);
        var before=EditorProjectCodec.Deserialize(original.Document.Json) with{CanvasWidth=1920,CanvasHeight=1080};
        var after=before with{CanvasWidth=1080,CanvasHeight=1920,Fps=25};
        Assert.True(studio.SaveIndependentProject(original.Identity.Id,after,1).Ok);
        var reopened=studio.GetIndependentProject(original.Identity.Id)!;
        Assert.Equal((1080,1920,25),(reopened.Identity.Width,reopened.Identity.Height,reopened.Identity.Fps));
        Assert.False(studio.SaveIndependentProject(original.Identity.Id,before,1).Ok);
        Assert.Equal(reopened,studio.GetIndependentProject(original.Identity.Id));
        Assert.True(studio.SaveIndependentProject(original.Identity.Id,before,2).Ok);
        reopened=studio.GetIndependentProject(original.Identity.Id)!;
        Assert.Equal((1920,1080,30),(reopened.Identity.Width,reopened.Identity.Height,reopened.Identity.Fps));
        Assert.Equal(1920,EditorProjectCodec.Deserialize(reopened.Document.Json).CanvasWidth);
    }
    [Fact] public void SequenceCanvasValidationAndSecondBasedTiming()
    {
        var p=new EditorProject{Timeline=[new("source","clip",1.23,5.67)],CanvasWidth=1080,CanvasHeight=1920,Fps=25,Markers=[2.3]};
        EditorRenderService.ValidateForSave(p);
        var roundtrip=EditorProjectCodec.Deserialize(JsonSerializer.Serialize(p));
        Assert.Equal(1.23,roundtrip.Timeline![0].Start);Assert.Equal(5.67,roundtrip.Timeline[0].End);Assert.Equal(2.3,roundtrip.Markers![0]);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForSave(p with{CanvasHeight=null}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForSave(p with{CanvasWidth=1079}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForSave(p with{Fps=0}));
    }

    [Fact] public void EmptyAdmissionDoesNotBypassOtherProjectValidation()
    {
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForSave(new EditorProject{Timeline=[],MasterVolume=99},true));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForSave(new EditorProject{Timeline=[],Markers=[2]},true));
    }
}
