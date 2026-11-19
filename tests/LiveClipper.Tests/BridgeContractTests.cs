using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Core.Bridge;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace LiveClipper.Tests;

/// <summary>
/// B12-2 桥接端点契约测试：只依赖 Core，不启动 WebApplication。
/// 端点层是薄封装；这里锁定 jobId 形状、状态机、progress 单调、并发上限、取消/失败语义。
/// 端到端 HTTP 契约由 scripts/bridge-contract-test.mjs 对真实服务覆盖。
/// </summary>
public sealed class BridgeContractTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "liveclipper-bridge-" + Guid.NewGuid().ToString("N"));

    private static BridgeJobSnapshot WaitFor(BridgeJobService service, string id, Func<BridgeJobSnapshot, bool> predicate, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        BridgeJobSnapshot last = service.Get(id).Job!;
        while (DateTime.UtcNow < deadline)
        {
            last = service.Get(id).Job!;
            if (predicate(last)) return last;
            Thread.Sleep(25);
        }
        throw new Xunit.Sdk.XunitException($"等待任务 {id} 超时，最后状态：{last.Status} progress={last.Progress}");
    }

    [Fact]
    public void FakeExecutor_SucceedsWithContractShape()
    {
        var root = NewRoot();
        try
        {
            using var service = new BridgeJobService(new FakeBridgeExecutor(durationMs: 120), root);
            var (job, error, status) = service.Enqueue(new BridgeExportRequest("clip-1"));
            Assert.Null(error);
            Assert.Equal(202, status);
            Assert.NotNull(job);
            Assert.Matches("^[0-9a-f]{32}$", job!.Id);
            Assert.Equal("preserve", job.Variant);
            Assert.Equal("h264", job.Format);

            var finished = WaitFor(service, job.Id, snapshot => snapshot.Status is "succeeded" or "failed");
            Assert.Equal("succeeded", finished.Status);
            Assert.Equal(100, finished.Progress);
            Assert.NotNull(finished.ResultPath);
            Assert.StartsWith(Path.Combine(root, "bridge-clips"), finished.ResultPath!, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(finished.ResultPath));
            Assert.Null(finished.Error);
            Assert.NotNull(finished.StartedAt);
            Assert.NotNull(finished.FinishedAt);
            Assert.Equal(job.Id, finished.Id);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Progress_IsMonotonicAndWithinRange()
    {
        var root = NewRoot();
        try
        {
            using var service = new BridgeJobService(new FakeBridgeExecutor(durationMs: 250), root);
            var job = service.Enqueue(new BridgeExportRequest("clip-progress")).Job!;
            var seen = new List<int>();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = service.Get(job.Id).Job!;
                seen.Add(snapshot.Progress);
                Assert.InRange(snapshot.Progress, 0, 100);
                if (snapshot.Status is "succeeded" or "failed") break;
                Thread.Sleep(10);
            }
            var status = service.Get(job.Id).Job!;
            Assert.Equal("succeeded", status.Status);
            for (var i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1], $"progress 回退：{seen[i - 1]} → {seen[i]}");
            Assert.Contains(seen, value => value is > 0 and < 100);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Enqueue_ValidatesRequestAndClipExistence()
    {
        var root = NewRoot();
        try
        {
            using var service = new BridgeJobService(new FakeBridgeExecutor(), root, clipId => clipId == "known");
            var empty = service.Enqueue(new BridgeExportRequest(" "));
            Assert.Equal(400, empty.StatusCode);
            Assert.Equal("ValidationError", empty.Error!.Class);

            var badVariant = service.Enqueue(new BridgeExportRequest("known", Variant: "explode"));
            Assert.Equal(400, badVariant.StatusCode);
            Assert.Equal("ValidationError", badVariant.Error!.Class);

            var badFormat = service.Enqueue(new BridgeExportRequest("known", Variant: "fold", Format: "webm"));
            Assert.Equal(400, badFormat.StatusCode);

            var missing = service.Enqueue(new BridgeExportRequest("ghost"));
            Assert.Equal(404, missing.StatusCode);
            Assert.Equal("NotFound", missing.Error!.Class);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Enqueue_RejectsDuplicateClipAndThirdConcurrentJob()
    {
        var root = NewRoot();
        try
        {
            using var blocker = new BlockingExecutor();
            using var service = new BridgeJobService(blocker, root);
            var first = service.Enqueue(new BridgeExportRequest("clip-a")).Job!;
            blocker.WaitStarted("clip-a");
            var duplicate = service.Enqueue(new BridgeExportRequest("clip-a"));
            Assert.Equal(409, duplicate.StatusCode);
            Assert.Equal("Conflict", duplicate.Error!.Class);

            var second = service.Enqueue(new BridgeExportRequest("clip-b")).Job!;
            blocker.WaitStarted("clip-b");
            Assert.NotEqual(first.Id, second.Id);
            var third = service.Enqueue(new BridgeExportRequest("clip-c"));
            Assert.Equal(409, third.StatusCode);
            Assert.Equal("Conflict", third.Error!.Class);

            blocker.ReleaseAll();
            WaitFor(service, first.Id, snapshot => snapshot.Status == "succeeded");
            WaitFor(service, second.Id, snapshot => snapshot.Status == "succeeded");
            await Task.CompletedTask;
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Cancel_RunningSucceeds_TerminalConflicts()
    {
        var root = NewRoot();
        try
        {
            using var blocker = new BlockingExecutor();
            using var service = new BridgeJobService(blocker, root);
            var job = service.Enqueue(new BridgeExportRequest("clip-cancel")).Job!;
            blocker.WaitStarted("clip-cancel");
            Assert.Equal("running", service.Get(job.Id).Job!.Status);

            var cancelled = service.Cancel(job.Id);
            Assert.Null(cancelled.Error);
            Assert.Equal(200, cancelled.StatusCode);
            Assert.Equal("cancelled", cancelled.Job!.Status);

            var again = service.Cancel(job.Id);
            Assert.Equal(409, again.StatusCode);
            Assert.Equal("Conflict", again.Error!.Class);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ExecutorFailure_BecomesFailedWithClass()
    {
        var root = NewRoot();
        try
        {
            using var service = new BridgeJobService(new FailingExecutor(), root);
            var job = service.Enqueue(new BridgeExportRequest("clip-fail")).Job!;
            var finished = WaitFor(service, job.Id, snapshot => snapshot.Status == "failed");
            Assert.Equal("failed", finished.Status);
            Assert.NotNull(finished.Error);
            Assert.Equal("FilmCraftError", finished.Error!.Class);
            Assert.Contains("模拟导出失败", finished.Error.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void GetAndCancel_ValidateJobIdShape()
    {
        var root = NewRoot();
        try
        {
            using var service = new BridgeJobService(new FakeBridgeExecutor(), root);
            Assert.Equal(400, service.Get("not-a-job").StatusCode);
            Assert.Equal(400, service.Cancel("XYZ").StatusCode);
            Assert.True(BridgeEndpointFormatting.IsJobId(Guid.NewGuid().ToString("N")));
            Assert.False(BridgeEndpointFormatting.IsJobId(Guid.NewGuid().ToString("D")));
            Assert.Equal(404, service.Get(Guid.NewGuid().ToString("N")).StatusCode);

            var (job, _, _) = service.Enqueue(new BridgeExportRequest("clip-list"));
            Assert.NotNull(job);
            var list = service.List();
            Assert.Contains(list, item => item.Id == job!.Id);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Capabilities_ExposeVariantsFormatsAndSchema()
    {
        var root = NewRoot();
        try
        {
            using var service = new BridgeJobService(new FakeBridgeExecutor(), root);
            var capabilities = service.Capabilities;
            Assert.Contains("preserve", capabilities.Variants);
            Assert.Contains("fold", capabilities.Variants);
            Assert.Contains("h264", capabilities.Formats);
            Assert.Equal(15, capabilities.SchemaVersion);
            Assert.True(capabilities.CompensateDefault);
            Assert.Equal(2, BridgeJobService.MaxConcurrentJobs);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Compensate_DefaultsOnAndReachesExecutor()
    {
        var root = NewRoot();
        try
        {
            var executor = new RecordingExecutor();
            using var service = new BridgeJobService(executor, root);
            var job = service.Enqueue(new BridgeExportRequest("clip-compensate")).Job!;
            Assert.Contains(job.Warnings, warning => warning.Contains("已启用转场时长补偿"));
            var finished = WaitFor(service, job.Id, snapshot => snapshot.Status == "succeeded");
            Assert.Contains(finished.Warnings, warning => warning.Contains("已启用转场时长补偿"));
            Assert.True(executor.LastCompensate);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Compensate_CanBeDisabledPerRequest()
    {
        var root = NewRoot();
        try
        {
            var executor = new RecordingExecutor();
            using var service = new BridgeJobService(executor, root);
            var job = service.Enqueue(new BridgeExportRequest("clip-no-compensate",
                Options: new BridgeExportOptions(Compensate: false))).Job!;
            Assert.Contains(job.Warnings, warning => warning.Contains("已关闭转场时长补偿"));
            var finished = WaitFor(service, job.Id, snapshot => snapshot.Status == "succeeded");
            Assert.Contains(finished.Warnings, warning => warning.Contains("已关闭转场时长补偿"));
            Assert.False(executor.LastCompensate);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Success_WritesSummaryLog()
    {
        var root = NewRoot();
        try
        {
            using var service = new BridgeJobService(new FakeBridgeExecutor(durationMs: 100), root);
            var job = service.Enqueue(new BridgeExportRequest("clip-log-ok")).Job!;
            var finished = WaitFor(service, job.Id, snapshot => snapshot.Status == "succeeded");
            var logPath = Path.Combine(root, "bridge-clips", job.Id + ".log");
            Assert.True(File.Exists(logPath), $"缺少成功日志：{logPath}");
            var text = File.ReadAllText(logPath);
            Assert.Contains("succeeded", text);
            Assert.Contains("clip-log-ok", text);
            Assert.Contains(finished.ResultPath!, text);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Failure_WritesClassMessageAndDetailLog()
    {
        var root = NewRoot();
        try
        {
            using var service = new BridgeJobService(new FailingExecutor(), root);
            var job = service.Enqueue(new BridgeExportRequest("clip-log-fail")).Job!;
            WaitFor(service, job.Id, snapshot => snapshot.Status == "failed");
            var logPath = Path.Combine(root, "bridge-clips", job.Id + ".log");
            Assert.True(File.Exists(logPath), $"缺少失败日志：{logPath}");
            var text = File.ReadAllText(logPath);
            Assert.Contains("failed", text);
            Assert.Contains("FilmCraftError", text);
            Assert.Contains("模拟导出失败", text);
            Assert.Contains("detail=test", text);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ChunkPlanner_KeepsShortClipsAsSingleSegment()
    {
        var eight = BridgeChunkPlanner.Plan(8.0);
        Assert.Single(eight);
        Assert.Equal(0, eight[0].StartSeconds);
        Assert.Equal(8.0, eight[0].LengthSeconds, 6);

        var thirty = BridgeChunkPlanner.Plan(30.0);
        Assert.Single(thirty);
        Assert.Equal(30.0, thirty[0].LengthSeconds, 6);
    }

    [Fact]
    public void ChunkPlanner_SplitsLongClipIntoNineSecondChunks()
    {
        var chunks = BridgeChunkPlanner.Plan(212.184);
        Assert.Equal(24, chunks.Count);
        Assert.Equal(0, chunks[0].StartSeconds);
        Assert.Equal(9, chunks[0].LengthSeconds, 6);
        Assert.Equal(207, chunks[^1].StartSeconds, 6);
        Assert.Equal(5.184, chunks[^1].LengthSeconds, 3);
        Assert.All(chunks, chunk => Assert.InRange(chunk.LengthSeconds, 0.001, 9.0));
        Assert.Equal(212.184, chunks.Sum(chunk => chunk.LengthSeconds), 3);
    }

    [Fact]
    public void LogRotation_PrunesOldAndExcessLogs()
    {
        var directory = NewRoot();
        Directory.CreateDirectory(directory);
        try
        {
            var now = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
            for (var index = 0; index < 55; index++)
            {
                var path = Path.Combine(directory, $"job-{index:D2}.log");
                File.WriteAllText(path, "log");
                File.SetLastWriteTimeUtc(path, (now - TimeSpan.FromHours(index + 1)).UtcDateTime);
            }
            var ancient = Path.Combine(directory, "ancient.log");
            File.WriteAllText(ancient, "log");
            File.SetLastWriteTimeUtc(ancient, (now - TimeSpan.FromDays(10)).UtcDateTime);

            var deleted = BridgeLogRotation.Prune(directory, now);

            Assert.False(File.Exists(ancient));
            Assert.Equal(50, Directory.EnumerateFiles(directory, "*.log").Count());
            Assert.Equal(6, deleted); // 55 个中最新 50 个保留、最旧 5 个 + ancient 各删一次
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private sealed class BlockingExecutor : IBridgeExecutor, IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BridgeCapabilitiesInfo Capabilities { get; } = new("0.13.0", 15, ["transitions"], BridgeJobService.SupportedVariants, BridgeJobService.SupportedFormats);

        public Task<string> RunAsync(BridgeJobContext context, CancellationToken token)
        {
            TaskCompletionSource started;
            lock (_gate)
            {
                if (!_started.TryGetValue(context.ClipId, out started!))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _started[context.ClipId] = started;
                }
            }
            context.ReportProgress(5);
            started.TrySetResult();
            return WaitAsync();

            async Task<string> WaitAsync()
            {
                try
                {
                    await _release.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(context.ResultPath)!);
                await File.WriteAllTextAsync(context.ResultPath, "fake", token);
                context.ReportProgress(100);
                return context.ResultPath;
            }
        }

        public void WaitStarted(string clipId)
        {
            TaskCompletionSource started;
            lock (_gate)
            {
                started = _started.TryGetValue(clipId, out var existing)
                    ? existing
                    : _started[clipId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (!started.Task.Wait(TimeSpan.FromSeconds(10))) throw new Xunit.Sdk.XunitException($"执行器未启动：{clipId}");
        }

        public void ReleaseAll() => _release.TrySetResult();

        public void Dispose() => _release.TrySetResult();
    }

    private sealed class FailingExecutor : IBridgeExecutor
    {
        public BridgeCapabilitiesInfo Capabilities { get; } = new("0.13.0", 15, [], BridgeJobService.SupportedVariants, BridgeJobService.SupportedFormats);

        public Task<string> RunAsync(BridgeJobContext context, CancellationToken token) =>
            Task.FromException<string>(new BridgeExecutionException(new BridgeError("FilmCraftError", "模拟导出失败", "test")));
    }

    /// <summary>记录执行器收到的 Compensate 标志（B8-3 默认开启/可关闭）。</summary>
    private sealed class RecordingExecutor : IBridgeExecutor
    {
        public bool LastCompensate { get; private set; }
        public BridgeCapabilitiesInfo Capabilities { get; } = new("0.13.0", 15, [], BridgeJobService.SupportedVariants, BridgeJobService.SupportedFormats);

        public async Task<string> RunAsync(BridgeJobContext context, CancellationToken token)
        {
            LastCompensate = context.Compensate;
            Directory.CreateDirectory(Path.GetDirectoryName(context.ResultPath)!);
            await File.WriteAllTextAsync(context.ResultPath, "fake", token);
            context.ReportProgress(100);
            return context.ResultPath;
        }
    }

    [Fact]
    public async Task ProductionEndpoints_KeepRenderRoutesWithoutFcprojImport()
    {
        var root = NewRoot();
        try
        {
            await using var app = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" }).Build();
            using var jobs = new BridgeJobService(new FakeBridgeExecutor(), root);
            var fcproj = new FcprojExportService(_ => null, "node", "mapper.mjs", "ffprobe", root);

            LiveClipper.App.BridgeEndpoints.Map(app, jobs, fcproj);

            var endpoints = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
            Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/bridge/import");
            var routes = endpoints.SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}")).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "GET /api/bridge/capabilities",
                "GET /api/bridge/jobs",
                "GET /api/bridge/jobs/{id}",
                "POST /api/bridge/export",
                "POST /api/bridge/fcproj",
                "POST /api/bridge/jobs/{id}/cancel",
                "POST /api/bridge/open"
            }, routes);
            Assert.DoesNotContain(typeof(LiveClipper.App.BridgeEndpoints).GetMethod(nameof(LiveClipper.App.BridgeEndpoints.Map))!.GetParameters(),
                parameter => parameter.ParameterType == typeof(BridgeImportService));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ===== B13-2：FilmCraft .fcproj → editor project 导入服务 =====

    private static string WriteFcproj(string directory, string mediaPath, int schemaVersion = 15)
    {
        Directory.CreateDirectory(directory);
        var json = $$"""
        {
          "format": "filmcraft.project",
          "schema_version": {{schemaVersion}},
          "project": {
            "name": "bridge-import-fixture",
            "items": {
              "1": { "id": 1, "name": "Main", "label": "Main", "kind": { "Sequence": {
                "settings": { "width": 640, "height": 360, "frame_rate": { "num": 30, "den": 1 } },
                "video_tracks": [ { "id": 1, "kind": "Video", "name": "Video 1", "items": [
                  { "id": 4, "item": 2, "start": 0, "duration": 762048000000, "source_in": 0, "speed": 1, "enabled": true, "effects": [] }
                ], "transitions": [] } ],
                "audio_tracks": [], "caption_tracks": []
              } } },
              "2": { "id": 2, "name": "clip.mp4", "kind": { "Media": { "media": { "File": { "path": {{JsonSerializer.Serialize(mediaPath)}} } }, "info": {} } } }
            },
            "root": { "id": 0, "name": "root", "children": [ { "Item": 1 } ] }
          },
          "view": { "active_sequence": 1 }
        }
        """;
        var path = Path.Combine(directory, "import.fcproj");
        File.WriteAllText(path, json);
        return path;
    }

    private static (BridgeImportService Service, List<(string ClipId, string Json)> Saved) NewImportService(string mediaPath)
    {
        var clips = new List<Clip> { new("clip-1", "ev-1", mediaPath, null, DateTimeOffset.UtcNow) };
        var saved = new List<(string, string)>();
        var service = new BridgeImportService(() => clips, _ => null, (clipId, json) => saved.Add((clipId, json)));
        return (service, saved);
    }

    [Fact]
    public void ImportService_ImportsFcprojAndSavesProject()
    {
        var root = NewRoot();
        try
        {
            var media = Path.Combine(root, "media", "movie.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(media)!);
            File.WriteAllBytes(media, []);
            var fcproj = WriteFcproj(root, media);
            var (service, saved) = NewImportService(media);

            var summary = service.Import(new BridgeImportRequest(fcproj));

            Assert.Equal("clip-1", summary.ProjectId);
            Assert.Equal(1, summary.TimelineItems);
            Assert.Equal(3.0, summary.DurationSeconds, 3);
            Assert.Single(saved);
            Assert.Equal("clip-1", saved[0].ClipId);
            var project = JsonSerializer.Deserialize<EditorProject>(saved[0].Json);
            Assert.NotNull(project);
            Assert.Single(project!.Timeline!);
            Assert.Equal("clip-1", project.Timeline![0].ClipId);
            Assert.Equal(3.0, project.End, 3);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ImportService_UsesExplicitClipMapping()
    {
        var root = NewRoot();
        try
        {
            var fcproj = WriteFcproj(root, @"C:\render\zoom.mp4");
            var (service, saved) = NewImportService(Path.Combine(root, "unrelated.mp4"));
            var summary = service.Import(new BridgeImportRequest(fcproj, new Dictionary<string, string> { [@"C:\render\zoom.mp4"] = "clip-1" }));

            Assert.Equal("clip-1", summary.ProjectId);
            Assert.Single(saved);
            Assert.Contains(@"C:\render\zoom.mp4", summary.ResolvedMedia.Keys);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ImportService_RejectsUnmappedMedia()
    {
        var root = NewRoot();
        try
        {
            var fcproj = WriteFcproj(root, @"C:\render\missing.mp4");
            var service = new BridgeImportService(() => [], _ => null, (_, _) => { });

            var ex = Assert.Throws<BridgeImportException>(() => service.Import(new BridgeImportRequest(fcproj)));

            Assert.Equal("UnmappedMedia", ex.Class);
            Assert.Contains("missing.mp4", ex.Detail);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ImportService_UsesTargetClipIdFallback()
    {
        var root = NewRoot();
        try
        {
            var fcproj = WriteFcproj(root, @"C:ender
o-match.mp4");
            var (service, saved) = NewImportService(Path.Combine(root, "unrelated.mp4"));

            var summary = service.Import(new BridgeImportRequest(fcproj, null, "clip-1"));

            Assert.Equal("clip-1", summary.ProjectId);
            Assert.Single(saved);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ImportService_RejectsUnsupportedSchema()
    {
        var root = NewRoot();
        try
        {
            var fcproj = WriteFcproj(root, @"C:\render\clip.mp4", schemaVersion: 16);
            var (service, _) = NewImportService(Path.Combine(root, "clip.mp4"));

            var ex = Assert.Throws<BridgeImportException>(() => service.Import(new BridgeImportRequest(fcproj)));

            Assert.Equal("UnsupportedSchema", ex.Class);
        }
        finally
        {
            TryDelete(root);
        }
    }
}
