using LiveClipper.Core;
namespace LiveClipper.App;
public static class CacheCleanupEndpoints
{
    public sealed record ConfirmRequest(string Token);
    public static void Map(WebApplication app,Store store) {
        var service=new SafeCacheCleanupService(Path.GetDirectoryName(store.DatabasePath)!,()=>store.Clips().ToDictionary(x=>x.Id,x=>x.Path));
        app.MapGet("/api/cache-cleanup",()=>Results.Ok(service.Scan()));
        app.MapPost("/api/cache-cleanup",(ConfirmRequest request)=> {
            try{return Results.Ok(service.Clean(request.Token));}
            catch(InvalidOperationException error){return Results.Conflict(new {error=error.Message});}
        });
    }
}
