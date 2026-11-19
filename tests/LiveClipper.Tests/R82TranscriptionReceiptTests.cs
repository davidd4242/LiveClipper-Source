using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

[CollectionDefinition("R82TranscriptionReceipt", DisableParallelization = true)]
public sealed class R82TranscriptionReceiptCollection;

[Collection("R82TranscriptionReceipt")]
public sealed class R82TranscriptionReceiptTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PendingOnly_ReceiptAndLegacyRestartReuseRowWithoutScheduling()
    {
        await using var f = new Fixture();
        var clip = f.Clip("pending");
        f.Store.SaveJob(new Job("existing", "transcribe", clip.EventId, "pending", 0, null));
        var receipt = f.Service.RestartReceipt(clip);
        Assert.Equal(new TranscriptionReceipt("existing", "already-pending", false), receipt);
        f.Service.Restart(clip);
        Assert.Equal("existing", Assert.Single(f.Store.Jobs()).Id);
        await f.Service.StopAsync();
        Assert.Equal("pending", Assert.Single(f.Store.Jobs()).Status);
        Assert.False(f.WorkerReady(clip));
    }

    [Fact]
    public async Task RunningRestart_ReturnsNewRowAndInterruptsRealWorker()
    {
        await using var f = new Fixture();
        var clip = f.Clip("running");
        var first = f.Service.QueueReceipt(clip);
        Assert.True(first.Started); Assert.Equal("queued", first.Disposition);
        await f.WaitAsync(() => f.WorkerReady(clip), "real slow worker start");
        Assert.Equal("running", f.Find(first.JobId).Status);
        var next = f.Service.RestartReceipt(clip);
        Assert.True(next.Started); Assert.Equal("queued", next.Disposition);
        Assert.NotEqual(first.JobId, next.JobId);
        await f.WaitAsync(() => f.Find(first.JobId).Status == "interrupted", "old worker interruption");
        Assert.Equal(2, f.Store.Jobs().Count);
        Assert.Contains(f.Find(next.JobId).Status, new[] { "pending", "running" });
        output.WriteLine($"old={first.JobId}/interrupted new={next.JobId}/{f.Find(next.JobId).Status}");
    }

    [Fact]
    public async Task ScheduledPendingBehindRealWorker_RestartReusesIdWithoutSecondSchedule()
    {
        await using var f = new Fixture();
        var blocker = f.Clip("blocker");
        f.Service.Queue(blocker);
        await f.WaitAsync(() => f.WorkerReady(blocker), "blocking worker start");
        var clip = f.Clip("waiting");
        var queued = f.Service.QueueReceipt(clip);
        // Yield to the actual scheduled task while the first process holds the worker semaphore.
        await Task.Delay(150);
        Assert.Equal("pending", f.Find(queued.JobId).Status);
        var reused = f.Service.RestartReceipt(clip);
        output.WriteLine($"pending={queued.JobId} receipt={reused.JobId}/{reused.Disposition} rows={f.Store.Jobs().Count(x => x.EventId == clip.EventId)}");
        Assert.Equal(new TranscriptionReceipt(queued.JobId, "already-pending", false), reused);
        Assert.Single(f.Store.Jobs(), x => x.EventId == clip.EventId);
        Assert.False(f.WorkerReady(clip));
    }

    [Fact]
    public async Task RepeatedRestartWhileOldWorkerCancels_ReusesSingleReplacement()
    {
        await using var f = new Fixture();
        var clip = f.Clip("repeat-running");
        var first = f.Service.QueueReceipt(clip);
        await f.WaitAsync(() => f.WorkerReady(clip), "real worker start before repeated restart");
        // Queue another real worker first so the replacement cannot already be running on the second call.
        // Restarting a replacement that genuinely reached running would correctly create another job.
        var blocker = f.Clip("repeat-blocker");
        f.Service.Queue(blocker);
        await Task.Delay(150);
        var replacement = f.Service.RestartReceipt(clip);
        var repeated = f.Service.RestartReceipt(clip);
        output.WriteLine($"old={first.JobId} replacement={replacement.JobId} repeated={repeated.JobId}/{repeated.Disposition} eventRows={f.Store.Jobs().Count(x => x.EventId == clip.EventId)}");
        Assert.Equal(new TranscriptionReceipt(replacement.JobId, "already-pending", false), repeated);
        await f.WaitAsync(() => f.Find(first.JobId).Status == "interrupted", "old repeated-restart worker interruption");
        await f.WaitAsync(() => f.WorkerReady(blocker), "next real blocker start");
        Assert.Equal("pending", f.Find(replacement.JobId).Status);
        Assert.Equal(2, f.Store.Jobs().Count(x => x.EventId == clip.EventId));
        Assert.Equal(3, f.Store.Jobs().Count);
    }

    [Fact]
    public async Task Coordinator_PreservesVoidSignaturesDuplicateGuardAndMissingClipErrors()
    {
        await using var f = new Fixture();
        var clip = f.Clip("coordinator");
        var coordinator = f.Coordinator;
        var receipt = coordinator.QueueTranscriptionReceipt(clip.EventId);
        Assert.Equal("transcribe", f.Find(receipt.JobId).Type);
        Assert.Equal(clip.EventId, f.Find(receipt.JobId).EventId);
        Assert.Equal("该高光正在转写", Assert.Throws<InvalidOperationException>(() => coordinator.QueueTranscriptionReceipt(clip.EventId)).Message);
        Assert.Equal("该高光正在转写", Assert.Throws<InvalidOperationException>(() => coordinator.QueueTranscription(clip.EventId)).Message);
        var pending = f.Clip("coordinator-pending");
        f.Store.SaveJob(new Job("coordinator-old", "transcribe", pending.EventId, "pending", 0, null));
        Assert.Equal(new TranscriptionReceipt("coordinator-old", "already-pending", false), coordinator.RestartTranscriptionReceipt(pending.EventId));
        coordinator.RestartTranscription(pending.EventId);
        Assert.Single(f.Store.Jobs(), x => x.EventId == pending.EventId);
        Assert.Equal("找不到高光视频", Assert.Throws<InvalidOperationException>(() => coordinator.QueueTranscriptionReceipt("missing")).Message);
        Assert.Equal("找不到高光视频", Assert.Throws<InvalidOperationException>(() => coordinator.RestartTranscriptionReceipt("missing")).Message);
        Assert.Equal(typeof(void), typeof(TranscriptionService).GetMethod("Queue")!.ReturnType);
        Assert.Equal(typeof(void), typeof(TranscriptionService).GetMethod("Restart")!.ReturnType);
        Assert.Equal(typeof(void), typeof(LiveCoordinator).GetMethod("QueueTranscription")!.ReturnType);
        Assert.Equal(typeof(void), typeof(LiveCoordinator).GetMethod("RestartTranscription")!.ReturnType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealStudioEndpoint_ReturnsReceiptForHighlightAndImportedClip(bool imported)
    {
        await using var f = new Fixture();
        var clip = f.Clip(imported ? "imported" : "highlight");
        var studio = new StudioStore(f.Database); studio.Migrate();
        if (imported) studio.SaveLocalVideo(new LocalVideo(clip.Id, clip.Path, "Imported", "output", clip.CreatedAt));
        else f.Store.SaveHighlight(new HighlightEvent(clip.EventId, "session", "manual", 1, 0, 2, HighlightState.Ready, 1));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders(); builder.Services.AddSingleton(f.Coordinator);
        await using var app = builder.Build();
        StudioEndpoints.Map(app, f.Store, studio, null!, null!, null!, null!, null!, f.Coordinator.Media, null!, f.Settings);
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText == "/api/studio/clips/{id}/transcribe");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "POST"; context.Request.RouteValues["id"] = clip.Id;
        await using var body = new MemoryStream(); context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        body.Position = 0; using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal(200, context.Response.StatusCode);
        var result = json.RootElement;
        Assert.Equal(new[] { "disposition", "jobId", "queued" }, result.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        Assert.True(result.GetProperty("queued").GetBoolean()); Assert.Equal("queued", result.GetProperty("disposition").GetString());
        var job = f.Find(result.GetProperty("jobId").GetString()!);
        Assert.Equal(clip.EventId, job.EventId); Assert.Equal("transcribe", job.Type);
        Assert.Single(f.Store.Jobs());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "lc-r82-receipt-" + Guid.NewGuid().ToString("N"));
        private readonly string? oldScript = Environment.GetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT");
        private readonly string? oldStall = Environment.GetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_STALL_SECONDS");
        private LiveCoordinator? coordinator;
        public string Database => Path.Combine(root, "store.db");
        public Store Store { get; }
        public LiveSettings Settings { get; }
        public TranscriptionService Service { get; }
        public LiveCoordinator Coordinator => coordinator ??= new LiveCoordinator(Store, Settings);

        public Fixture()
        {
            Directory.CreateDirectory(root);
            var node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? "node.exe" : "node")).FirstOrDefault(File.Exists);
            Assert.True(node is not null, "Node is required for the real slow worker fixture; this test must not skip.");
            var script = Path.Combine(root, "slow-transcribe.cjs");
            File.WriteAllText(script, "const fs=require('node:fs'),path=require('node:path');fs.writeFileSync(path.join(process.argv[3],'worker.ready'),String(process.pid));process.stderr.write('PROGRESS 0.1\\n');setInterval(()=>{},1000);");
            Environment.SetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT", script);
            Environment.SetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_STALL_SECONDS", "600");
            Store = new Store(Database); Store.Migrate();
            Settings = new LiveSettings { StorageRoot = root, PythonPath = node!, FfmpegPath = "ffmpeg", CleanupOnExit = false };
            Service = new TranscriptionService(Store, Settings, new MediaTools(Settings.FfmpegPath));
        }

        public Clip Clip(string id)
        {
            var clip = new Clip(id, "event-" + id, TestAudio.WriteSineWav(Path.Combine(root, id + ".wav"),16000,1), null, DateTimeOffset.UtcNow);
            Store.SaveClip(clip); return clip;
        }
        public bool WorkerReady(Clip clip) => Store.Jobs().Where(j => j.EventId == clip.EventId).Any(j => File.Exists(Path.Combine(root,"render-work","transcription-audio",j.Id,"result","worker.ready")));
        public Job Find(string id) => Assert.Single(Store.Jobs(), job => job.Id == id);
        public async Task WaitAsync(Func<bool> predicate, string label)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(predicate(), label + " timed out; jobs=" + JsonSerializer.Serialize(Store.Jobs()));
        }
        public async ValueTask DisposeAsync()
        {
            try { await Service.StopAsync(); if (coordinator is not null) await coordinator.ShutdownAsync(); }
            finally
            {
                Environment.SetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_SCRIPT", oldScript);
                Environment.SetEnvironmentVariable("LIVECLIPPER_TRANSCRIBE_STALL_SECONDS", oldStall);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
        }
    }
}
