using LiveClipper.Desktop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace LiveClipper.Tests;

public class ProxyPumpStreamTests
{
    [Fact]
    public async Task Relay_RealLoopbackStreamsBeforeEnd_RejectsOtherPathsTokensAndOrigins()
    {
        var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server=new TestServer(new WebHostBuilder().Configure(app=>app.Run(async context=> {
            Assert.Equal("/api/live/rooms/7734200/preview.flv",context.Request.Path.Value);
            context.Response.ContentType="video/x-flv";
            try { await context.Response.Body.WriteAsync(new byte[]{70,76,86});await context.Response.Body.FlushAsync();
                await Task.Delay(Timeout.Infinite,context.RequestAborted); }
            catch(OperationCanceledException) { stopped.TrySetResult(); }
        })));
        using var upstream=server.CreateClient();
        using var relay=await LivePreviewRelay.StartAsync(upstream);
        Assert.Equal("127.0.0.1",new Uri(relay.Origin).Host);
        using var client=new HttpClient();
        Assert.Equal(System.Net.HttpStatusCode.NotFound,(await client.GetAsync(relay.Origin+"/api/status")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,(await client.GetAsync(relay.Origin+"/api/live/rooms/7734200/preview.flv?key=wrong")).StatusCode);
        using var denied=new HttpRequestMessage(HttpMethod.Get,relay.Url(7734200));denied.Headers.Add("Origin","https://evil.test");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,(await client.SendAsync(denied)).StatusCode);
        using var request=new HttpRequestMessage(HttpMethod.Get,relay.Url(7734200));request.Headers.Add("Origin","https://app.local");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("https://app.local",response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var stream=await response.Content.ReadAsStreamAsync();var data=new byte[3];
        await stream.ReadExactlyAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(new byte[]{70,76,86},data);
        relay.Dispose();await relay.Shutdown.WaitAsync(TimeSpan.FromSeconds(5));await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task Responses_UnregisterAtEofOrDisposeExactlyOnce()
    {
        var active=new System.Collections.Concurrent.ConcurrentDictionary<ProxyResponseStream,byte>();
        for(var i=0;i<100;i++) {
            var pump=new ProxyPumpStream(new MemoryStream(new byte[]{1}));
            var response=new HttpResponseMessage();
            var calls=0;
            using var stream=new ProxyResponseStream(pump,response,item=>{ calls++;active.TryRemove(item,out _); });
            active.TryAdd(stream,0);
            Assert.Equal(1,await stream.ReadAsync(new byte[4]));
            if(i%2==0) Assert.Equal(0,await stream.ReadAsync(new byte[4]));
            else stream.Dispose();
            stream.Dispose();
            Assert.Equal(1,calls);Assert.Empty(active);
            await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    [Fact]
    public async Task RealTestServerLiveStream_DeliversBeforeEnd_AndOtherRequestsContinue()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new TestServer(new WebHostBuilder().Configure(app=>app.Run(async context=> {
            if (context.Request.Path=="/status") { await context.Response.WriteAsync("ready"); return; }
            context.Response.ContentType="video/x-flv";
            try {
                await context.Response.Body.WriteAsync(new byte[]{70,76,86},context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(Timeout.Infinite,context.RequestAborted);
            } catch(OperationCanceledException) { cancelled.TrySetResult(); }
        })));
        using var client=server.CreateClient();
        using var response=await client.GetAsync("/live",HttpCompletionOption.ResponseHeadersRead);
        using var pump=new ProxyPumpStream(await response.Content.ReadAsStreamAsync());
        var bytes=new byte[3];
        Assert.Equal(3,await Task.Run(()=>pump.Read(bytes,0,3)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new byte[]{70,76,86},bytes);
        Assert.Equal("ready",await client.GetStringAsync("/status").WaitAsync(TimeSpan.FromSeconds(5)));
        var pending=pump.ReadAsync(new byte[1],0,1,CancellationToken.None);
        pump.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task Backpressure_DisposeStopsProducerWaitingOnFullQueue()
    {
        using var pump=new ProxyPumpStream(new MemoryStream(new byte[1024*1024]));
        await Task.Delay(50);
        Assert.False(pump.Completion.IsCompleted);
        pump.Dispose();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task IndependentStreams_ConsumerCancellation_DoesNotStopOtherRoom()
    {
        using var one=new ProxyPumpStream(new MemoryStream(new byte[]{1,2,3}));
        using var two=new ProxyPumpStream(new MemoryStream(new byte[]{4,5}));
        one.Dispose();
        var bytes=new byte[2];
        Assert.Equal(2,await two.ReadAsync(bytes));Assert.Equal(new byte[]{4,5},bytes);
        Assert.Equal(0,await two.ReadAsync(bytes));
        Assert.Equal(0,await two.ReadAsync(Memory<byte>.Empty));
    }
}
