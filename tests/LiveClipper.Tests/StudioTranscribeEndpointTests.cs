using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveClipper.Tests;

[CollectionDefinition("StudioTranscribeEndpoint", DisableParallelization = true)]
public sealed class StudioTranscribeEndpointCollection;

[Collection("StudioTranscribeEndpoint")]
public sealed class StudioTranscribeEndpointTests
{
    [Fact]
    public async Task ExistingClip_QueuesItsEvent_AndReturnsQueuedReceipt()
    {
        await using var fixture = new Fixture(blockingWorker: true);
        fixture.Store.SaveHighlight(new HighlightEvent("event-1", "session-1", "manual", 1, 0, 2, HighlightState.Ready, 1));
        fixture.SaveClip("clip-1", "event-1");

        var (status, body) = await fixture.PostAsync("clip-1");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(new[] { "disposition", "jobId", "queued" }, PropertyNames(body));
        Assert.True(body.GetProperty("queued").GetBoolean());
        Assert.Equal("queued", body.GetProperty("disposition").GetString());
        var jobId = body.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        Assert.True(Guid.TryParseExact(jobId, "N", out _), $"jobId 必须是 N 格式 32 位 GUID，实际: {jobId}");
        Assert.Single(fixture.Store.Jobs());
        var job = Assert.Single(fixture.Store.Jobs(), x => x.Id == jobId);
        Assert.Equal("event-1", job.EventId);
        Assert.Equal("transcribe", job.Type);
        Assert.Equal(0, job.RetryCount);
        // 接口只表示「已受理」。fixture 里的真实转译进程（node 假装 worker）会阻塞在
        // worker.ready 之后，所以任务要么还在排队（pending），要么已被 worker 接手（running）；
        // 不会是 failed。等到 worker 真正启动后再断言，避免用「没来得及失败」冒充绿。
        await fixture.WaitForWorkerAsync("clip-1");
        var activeJob = Assert.Single(fixture.Store.Jobs(), x => x.Id == jobId);
        Assert.Contains(activeJob.Status, new[] { "pending", "running" });
        Assert.Equal("running", activeJob.Status);
    }

    [Fact]
    public async Task ImportedClipWithoutHighlight_QueuesItsEvent()
    {
        await using var fixture = new Fixture(blockingWorker: true);
        fixture.SaveClip("imported-clip", "import-event");
        var clip = Assert.Single(fixture.Store.Clips());
        fixture.Studio.SaveLocalVideo(new LocalVideo(clip.Id, clip.Path, "Imported", "output", clip.CreatedAt));
        Assert.Empty(fixture.Store.Highlights());

        var (status, body) = await fixture.PostAsync(clip.Id);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(new[] { "disposition", "jobId", "queued" }, PropertyNames(body));
        Assert.True(body.GetProperty("queued").GetBoolean());
        Assert.Equal("queued", body.GetProperty("disposition").GetString());
        var jobId = body.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        Assert.True(Guid.TryParseExact(jobId, "N", out _), $"jobId 必须是 N 格式 32 位 GUID，实际: {jobId}");
        Assert.Single(fixture.Store.Jobs());
        var job = Assert.Single(fixture.Store.Jobs(), x => x.Id == jobId);
        Assert.Equal(clip.EventId, job.EventId);
        Assert.Equal("transcribe", job.Type);
        Assert.Equal(0, job.RetryCount);
        // 同第一条用例：真实阻塞 worker 让任务停在 pending/running，等它启动后再断言。
        await fixture.WaitForWorkerAsync(clip.Id);
        var activeJob = Assert.Single(fixture.Store.Jobs(), x => x.Id == jobId);
        Assert.Contains(activeJob.Status, new[] { "pending", "running" });
        Assert.Equal("running", activeJob.Status);
    }

    [Fact]
    public async Task MissingClip_Returns404_WithoutQueueing()
    {
        await using var fixture = new Fixture();

        var (status, body) = await fixture.PostAsync("missing-clip");

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal("工程或素材不存在", body.GetProperty("error").GetString());
        Assert.Equal(StudioErrorCodes.NotFound, body.GetProperty("code").GetString());
        Assert.Empty(fixture.Store.Jobs());
    }

    [Fact]
    public async Task MissingEvent_Returns404_WithoutQueueing()
    {
        await using var fixture = new Fixture();
        fixture.SaveClip("orphan-clip", "missing-event");

        var (status, body) = await fixture.PostAsync("orphan-clip");

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal("视频对应的事件不存在", body.GetProperty("error").GetString());
        Assert.Empty(fixture.Store.Jobs());
    }

    /// <summary>响应体字段集合（升序，供逐字段精确比对；排序后与 JSON 书写顺序无关）。</summary>
    private static string[] PropertyNames(JsonElement body) =>
        body.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-w5d-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousScript = Environment.GetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT");
        private readonly WebApplication _app;
        private readonly LiveCoordinator _coordinator;
        public Store Store { get; }
        public StudioStore Studio { get; }

        public Fixture() : this(false) { }

        public Fixture(bool blockingWorker)
        {
            Directory.CreateDirectory(_root);
            Store = new Store(Path.Combine(_root, "test.db"));
            Store.Migrate();
            Studio = new StudioStore(Path.Combine(_root, "test.db"));
            Studio.Migrate();
            var pythonPath = Path.Combine(_root, "missing-python.exe");
            if(blockingWorker)
            {
                var node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                    .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "node.exe" : "node")).FirstOrDefault(File.Exists);
                Assert.True(node is not null, "阻塞 worker fixture 需要 node；不允许跳过");
                // 伪造转译进程：写下 worker.ready 后保持存活并持续输出进度，任务因此稳定停在 running。
                var script = Path.Combine(_root, "blocking-transcribe.cjs");
                File.WriteAllText(script,
                    "const fs=require('node:fs'),path=require('node:path');" +
                    "fs.writeFileSync(path.join(process.argv[3],'worker.ready'),String(process.pid));" +
                    "process.stderr.write('PROGRESS 0.1\\n');" +
                    "setInterval(()=>process.stderr.write('PROGRESS 0.1\\n'),1000);");
                Environment.SetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT", script);
                pythonPath = node!;
            }
            var settings = new LiveSettings
            {
                StorageRoot = _root,
                FfmpegPath = "ffmpeg",
                PythonPath = pythonPath
            };
            _coordinator = new LiveCoordinator(Store, settings);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(_coordinator);
            _app = builder.Build();
            StudioEndpoints.Map(_app, Store, Studio, null!, null!, null!, null!, null!, _coordinator.Media, null!, settings);
        }

        public void SaveClip(string clipId, string eventId) =>
            Store.SaveClip(new Clip(clipId, eventId, TestAudio.WriteSineWav(Path.Combine(_root, clipId + ".wav"),16000,1), null, DateTimeOffset.UtcNow));

        /// <summary>等待伪造的转译进程真正启动；进程启动失败时直接断言失败，而不是当成状态通过。</summary>
        public async Task WaitForWorkerAsync(string clipId)
        {
            var job = Assert.Single(Store.Jobs(), j => j.EventId == Store.Clips().Single(c => c.Id == clipId).EventId);
            var marker = Path.Combine(_root, "render-work", "transcription-audio", job.Id, "result", "worker.ready");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while(!File.Exists(marker) && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(File.Exists(marker), "阻塞转译进程未启动；jobs=" + JsonSerializer.Serialize(Store.Jobs()));
        }

        public async Task<(int Status, JsonElement Body)> PostAsync(string clipId)
        {
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources
                .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == "/api/studio/clips/{id}/transcribe");
            Assert.Equal(new[] { "POST" }, endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = "POST";
            context.Request.RouteValues["id"] = clipId;
            await using var body = new MemoryStream();
            context.Response.Body = body;
            await endpoint.RequestDelegate!(context);
            body.Position = 0;
            using var json = await JsonDocument.ParseAsync(body);
            return (context.Response.StatusCode, json.RootElement.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _coordinator.ShutdownAsync();
                await _app.DisposeAsync();
            }
            finally
            {
                Environment.SetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT", _previousScript);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                for(var attempt = 0; attempt < 10; attempt++)
                {
                    try { Directory.Delete(_root, true); break; }
                    catch(IOException) when(attempt < 9) { await Task.Delay(100); }
                    catch(UnauthorizedAccessException) when(attempt < 9) { await Task.Delay(100); }
                }
            }
        }
    }
}
