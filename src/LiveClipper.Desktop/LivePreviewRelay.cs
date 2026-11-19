using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace LiveClipper.Desktop;

// Only the infinite live preview crosses a socket; all ordinary APIs stay in-process.
internal sealed class LivePreviewRelay : IDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Origin {get; private set;} = "";
    private LivePreviewRelay(WebApplication app) { _app=app; }
    public static async Task<LivePreviewRelay> StartAsync(HttpClient proxy)
    {
        var builder=WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options=>options.Listen(IPAddress.Loopback,0));
        var relay=new LivePreviewRelay(builder.Build());
        relay._app.Run(async context=> {
            if(context.Request.Method!="GET" || context.Request.Query["key"]!=relay._secret ||
               !TryRoom(context.Request.Path.Value,out var room)) { context.Response.StatusCode=404;return; }
            var origin=context.Request.Headers.Origin.ToString();
            if(origin.Length>0 && origin!="https://app.local") { context.Response.StatusCode=403;return; }
            context.Response.Headers.AccessControlAllowOrigin="https://app.local";
            context.Response.Headers.CacheControl="no-store";
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,relay._stop.Token);
            try {
                using var response=await proxy.GetAsync($"/api/live/rooms/{room}/preview.flv",HttpCompletionOption.ResponseHeadersRead,linked.Token).ConfigureAwait(false);
                context.Response.StatusCode=(int)response.StatusCode;
                context.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"video/x-flv";
                await context.Response.StartAsync(linked.Token).ConfigureAwait(false);
                await response.Content.CopyToAsync(context.Response.Body,linked.Token).ConfigureAwait(false);
            } catch(OperationCanceledException) when(linked.IsCancellationRequested) { }
        });
        await relay._app.StartAsync().ConfigureAwait(false);
        relay.Origin=relay._app.Urls.Single();
        return relay;
    }
    internal static bool TryRoom(string? path,out long room) {
        room=0;const string prefix="/api/live/rooms/",suffix="/preview.flv";
        return path is not null && path.StartsWith(prefix,StringComparison.Ordinal) && path.EndsWith(suffix,StringComparison.Ordinal)
            && long.TryParse(path[prefix.Length..^suffix.Length],out room) && room>0;
    }
    public string Url(long room)=>$"{Origin}/api/live/rooms/{room}/preview.flv?key={_secret}";
    public Task Shutdown {get;private set;}=Task.CompletedTask;
    private int _disposed;
    public void Dispose() {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _stop.Cancel();
        Shutdown=Task.Run(async()=> { await _app.StopAsync().ConfigureAwait(false);await _app.DisposeAsync().ConfigureAwait(false);_stop.Dispose(); });
    }
}
