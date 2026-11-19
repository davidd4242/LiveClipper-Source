using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveClipper.Tests;

public sealed class ProjectPackageRoutingTests
{
    [Fact]
    public async Task RealCombinedRoutes_ExportDownloadImport_UseConfiguredOutputAndNeverOverwriteOriginal()
    {
        var root=Path.Combine(Path.GetTempPath(),"LiveClipper-PackageRouting-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"data","test.db"));store.Migrate();
        var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var source=Path.Combine(root,"source.mp4");await File.WriteAllBytesAsync(source,[1,2,3,4]);
        var clip=new Clip(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
        studio.SaveLocalVideo(new(clip.Id,source,"route source",root,clip.CreatedAt));
        var project=new EditorProject{Start=0,End=4,Timeline=[new("base",clip.Id,0,4)]};
        studio.SaveProject(clip.Id,JsonSerializer.Serialize(project));
        var original=studio.GetProjectRecord(clip.Id)!;
        var settings=new LiveSettings{StorageRoot=Path.GetDirectoryName(store.DatabasePath)!,ExportOutputDirectory=Path.Combine(root,"export-drive")};
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Testing"});
        builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();
        StudioEndpoints.Map(app,store,studio,null!,null!,null!,null!,null!,null!,null!,settings);
        AdvancedStudioEndpoints.Map(app,store,studio,null!,null!,null!,settings,null!);
        var routes=((IEndpointRouteBuilder)app).DataSources.SelectMany(s=>s.Endpoints).OfType<RouteEndpoint>();
        var packageRoutes=routes.Where(e=>e.RoutePattern.RawText!.Contains("package"));
        var keys=packageRoutes.SelectMany(e=>(e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods??[])
            .Select(m=>m+" "+Regex.Replace(e.RoutePattern.RawText!,@"\{[^}]+\}","{}"))).ToArray();
        Assert.Equal(keys.Length,keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        await app.StartAsync();
        using var client=new HttpClient{BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/studio/clips/{clip.Id}/project-package");
        request.Headers.Add("X-Base-Revision",original.Revision.ToString());request.Headers.Add("X-Editor-Epoch",EditorRevisionStore.EpochFor(studio));
        request.Content=JsonContent.Create(new{name="portable",project});
        using var response=await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var payload=await response.Content.ReadFromJsonAsync<JsonElement>();
        var url=payload.GetProperty("downloadUrl").GetString()!;
        Assert.StartsWith("/api/studio/project-packages/",url);
        Assert.Single(Directory.GetFiles(Path.Combine(settings.ExportOutputDirectory,"project-packages","downloads"),"*.lcproj"));
        Assert.False(Directory.Exists(Path.Combine(settings.StorageRoot,"project-packages")));
        using var download=await client.GetAsync(url);Assert.True(download.IsSuccessStatusCode);
        var bytes=await download.Content.ReadAsByteArrayAsync();Assert.True(bytes.Length>0);
        // Changing the output setting must not strand a package already issued in this process.
        settings.ExportOutputDirectory=Path.Combine(root,"new-export-drive");
        using var again=await client.GetAsync(url);Assert.True(again.IsSuccessStatusCode);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(bytes),"file","portable.lcproj");
        using var imported=await client.PostAsync("/api/studio/project-packages/import",form);
        Assert.True(imported.IsSuccessStatusCode,await imported.Content.ReadAsStringAsync());
        var result=await imported.Content.ReadFromJsonAsync<JsonElement>();var newId=result.GetProperty("clipId").GetString()!;
        Assert.NotEqual(clip.Id,newId);Assert.Equal(1,result.GetProperty("revision").GetInt32());
        Assert.Equal(original,studio.GetProjectRecord(clip.Id));Assert.NotNull(studio.GetProjectRecord(newId));
        Assert.StartsWith(settings.ExportOutputDirectory,store.Clips().Single(c=>c.Id==newId).Path);
        var countBefore=store.Clips().Count();
        var uploadRoot=settings.ExportOutputDirectory;
        using var start=await client.PostAsJsonAsync("/api/studio/project-packages/uploads",new{totalBytes=bytes.Length});
        Assert.True(start.IsSuccessStatusCode,await start.Content.ReadAsStringAsync());
        var uploadId=(await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString()!;
        var uploadUrl="/api/studio/project-packages/uploads/"+uploadId;
        Assert.Single(Directory.GetFiles(Path.Combine(uploadRoot,"project-packages","uploads")));
        settings.ExportOutputDirectory=Path.Combine(root,"third-export-drive");
        using var incomplete=await client.PostAsync(uploadUrl+"/complete",null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict,incomplete.StatusCode);Assert.Equal(countBefore,store.Clips().Count());
        async Task<HttpResponseMessage> Chunk(int offset,int length)
        {
            var content=new ByteArrayContent(bytes,offset,length);
            content.Headers.ContentType=new("application/octet-stream");
            return await client.PostAsync(uploadUrl+"/chunks?offset="+offset,content);
        }
        var half=bytes.Length/2;
        using var wrong=await Chunk(1,half);Assert.Equal(System.Net.HttpStatusCode.Conflict,wrong.StatusCode);
        using var first=await Chunk(0,half);Assert.True(first.IsSuccessStatusCode,await first.Content.ReadAsStringAsync());
        using var stillIncomplete=await client.PostAsync(uploadUrl+"/complete",null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict,stillIncomplete.StatusCode);Assert.Equal(countBefore,store.Clips().Count());
        using var second=await Chunk(half,bytes.Length-half);Assert.True(second.IsSuccessStatusCode);
        using var completed=await client.PostAsync(uploadUrl+"/complete",null);
        Assert.True(completed.IsSuccessStatusCode,await completed.Content.ReadAsStringAsync());
        var chunkId=(await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clipId").GetString()!;
        Assert.NotEqual(clip.Id,chunkId);Assert.Equal(original,studio.GetProjectRecord(clip.Id));
        Assert.StartsWith(uploadRoot,store.Clips().Single(c=>c.Id==chunkId).Path);
        Assert.Empty(Directory.GetFiles(Path.Combine(uploadRoot,"project-packages","uploads")));
        using var cancelledStart=await client.PostAsJsonAsync("/api/studio/project-packages/uploads",new{totalBytes=bytes.Length});
        var cancelledId=(await cancelledStart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString()!;
        using var cancelled=await client.DeleteAsync("/api/studio/project-packages/uploads/"+cancelledId);
        Assert.True(cancelled.IsSuccessStatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(settings.ExportOutputDirectory,"project-packages","uploads")));
        Assert.Equal(countBefore+1,store.Clips().Count());Assert.Equal(original,studio.GetProjectRecord(clip.Id));
        await app.StopAsync();
    }
}
