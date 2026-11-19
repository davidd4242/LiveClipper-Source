using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

/// <summary>
/// GET /api/studio/clips/{id}/editor 对「刚导入、尚无工程记录」的 clip 的初始 timeline 契约。
/// 真实 StudioEndpoints、真实 SQLite、真实 import-paths 端点、真实 MediaTools(ffmpeg/ffprobe)；
/// 无任何 mock。媒体由真实 ffmpeg 在隔离目录生成，隔离库与媒体均保留不清理。
/// </summary>
public sealed class StudioEditorInitialTimelineTests(ITestOutputHelper output)
{
    private const string EditorRoute = "/api/studio/clips/{id}/editor";
    private const string ImportRoute = "/api/studio/import-paths";
    private const double GeneratedSeconds = 3.0;

    [Fact]
    public async Task Get_FreshlyImportedClip_ReturnsSourceTimelineMappedToProbedDurationWithoutPersisting()
    {
        await using var fixture = new Fixture(output);
        var media = fixture.GenerateMedia("fresh.mp4");
        var independentDuration = await fixture.FfprobeDurationAsync(media);
        output.WriteLine("independent ffprobe duration=" + independentDuration.ToString("R", CultureInfo.InvariantCulture));
        Assert.InRange(independentDuration, GeneratedSeconds - 0.2, GeneratedSeconds + 0.2);

        var imported = await fixture.InvokeAsync("POST", ImportRoute, null, JsonSerializer.Serialize(new { paths = new[] { media }, outputDirectory = Path.Combine(fixture.Root, "out") }), null, null);
        Assert.Equal(200, imported.Status);
        Assert.Equal(1, imported.Body.GetProperty("added").GetInt32());
        var id = Assert.Single(imported.Body.GetProperty("clipIds").EnumerateArray()).GetString()!;
        Assert.Null(fixture.Studio.GetProjectRecord(id));
        Assert.Null(fixture.Studio.Project(id));
        Assert.Equal(Path.GetFullPath(media), Assert.Single(fixture.Store.Clips(), c => c.Id == id).Path);

        var reply = await fixture.InvokeAsync("GET", EditorRoute, id, null, null, null);

        Assert.Equal(200, reply.Status);
        var body = reply.Body;
        Assert.Equal(id, body.GetProperty("clip").GetProperty("id").GetString());
        var duration = body.GetProperty("duration").GetDouble();
        Assert.Equal(independentDuration, duration, 0.01);
        Assert.NotEqual(4.0, duration); // DurationAsync 探测失败时的硬编码兜底值，不得误当真实时长
        Assert.Equal(320, body.GetProperty("width").GetInt32());
        Assert.Equal(180, body.GetProperty("height").GetInt32());
        Assert.True(body.GetProperty("hasAudio").GetBoolean());

        var project = body.GetProperty("project");
        Assert.Equal(0, project.GetProperty("start").GetDouble());
        Assert.Equal(duration, project.GetProperty("end").GetDouble(), 9);
        var item = Assert.Single(project.GetProperty("timeline").EnumerateArray());
        Assert.Equal("source", item.GetProperty("id").GetString());
        Assert.Equal(id, item.GetProperty("clipId").GetString());
        Assert.Equal(0, item.GetProperty("start").GetDouble());
        Assert.Equal(duration, item.GetProperty("end").GetDouble(), 9);
        Assert.Equal(1, item.GetProperty("speed").GetDouble());
        Assert.Equal(1, item.GetProperty("track").GetInt32());
        Assert.False(item.GetProperty("disabled").GetBoolean());

        Assert.Equal(0, body.GetProperty("revision").GetInt32());
        var epoch = body.GetProperty("epoch").GetString();
        Assert.False(string.IsNullOrWhiteSpace(epoch));
        Assert.Equal(EditorRevisionStore.EpochFor(fixture.Studio), epoch);
        Assert.Equal(epoch, reply.EpochHeader);

        Assert.Null(fixture.Studio.GetProjectRecord(id));
        Assert.Null(fixture.Studio.Project(id));
        Assert.Equal(0, EditorRevisionStore.Current(fixture.Studio, id));
    }

    [Fact]
    public async Task Get_AfterRealSave_ReturnsSavedProjectAtAdvancedRevisionInSameEpoch()
    {
        await using var fixture = new Fixture(output);
        var media = fixture.GenerateMedia("saved.mp4");
        var imported = await fixture.InvokeAsync("POST", ImportRoute, null, JsonSerializer.Serialize(new { paths = new[] { media }, outputDirectory = Path.Combine(fixture.Root, "out") }), null, null);
        var id = Assert.Single(imported.Body.GetProperty("clipIds").EnumerateArray()).GetString()!;
        var first = await fixture.InvokeAsync("GET", EditorRoute, id, null, null, null);
        var duration = first.Body.GetProperty("duration").GetDouble();
        var epoch = first.Body.GetProperty("epoch").GetString()!;
        Assert.Equal(0, first.Body.GetProperty("revision").GetInt32());

        var trimmedEnd = duration - 1;
        var saved = JsonSerializer.Serialize(new
        {
            start = 0.0,
            end = trimmedEnd,
            timeline = new[] { new { id = "source", clipId = id, start = 0.0, end = trimmedEnd } }
        });
        var put = await fixture.InvokeAsync("PUT", EditorRoute, id, saved, 0, epoch);
        Assert.Equal(200, put.Status);
        Assert.True(put.Body.GetProperty("saved").GetBoolean());
        Assert.Equal(1, put.Body.GetProperty("revision").GetInt32());

        var second = await fixture.InvokeAsync("GET", EditorRoute, id, null, null, null);

        Assert.Equal(200, second.Status);
        Assert.Equal(1, second.Body.GetProperty("revision").GetInt32());
        Assert.Equal(epoch, second.Body.GetProperty("epoch").GetString());
        Assert.Equal(duration, second.Body.GetProperty("duration").GetDouble(), 9);
        var item = Assert.Single(second.Body.GetProperty("project").GetProperty("timeline").EnumerateArray());
        Assert.Equal(id, item.GetProperty("clipId").GetString());
        Assert.Equal(0, item.GetProperty("start").GetDouble());
        Assert.Equal(trimmedEnd, item.GetProperty("end").GetDouble(), 9);
        Assert.Equal(trimmedEnd, second.Body.GetProperty("project").GetProperty("end").GetDouble(), 9);
        Assert.NotNull(fixture.Studio.GetProjectRecord(id));

        // Explicitly clearing a legacy clip is an editing state, not a request to restore its source.
        var empty = JsonSerializer.Serialize(new EditorProject { Start=0, End=duration, Timeline=[] });
        var cleared = await fixture.InvokeAsync("PUT", EditorRoute, id, empty, 1, epoch);
        Assert.Equal(200, cleared.Status);
        Assert.Equal(2, cleared.Body.GetProperty("revision").GetInt32());
        EditorRevisionStore.Forget(id); // Reload persisted state rather than relying on the in-memory revision cache.
        var reopened = await fixture.InvokeAsync("GET", EditorRoute, id, null, null, null);
        Assert.Equal(200, reopened.Status);
        Assert.Equal(2, reopened.Body.GetProperty("revision").GetInt32());
        Assert.Empty(reopened.Body.GetProperty("project").GetProperty("timeline").EnumerateArray());
        var persisted = EditorProjectCodec.Deserialize(fixture.Studio.Project(id)!);
        Assert.Empty(persisted.Timeline!);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ValidateForRender(persisted));
        var restored = await fixture.InvokeAsync("PUT", EditorRoute, id, saved, 2, reopened.Body.GetProperty("epoch").GetString());
        Assert.Equal(200, restored.Status);
        var final = await fixture.InvokeAsync("GET", EditorRoute, id, null, null, null);
        Assert.Equal(3, final.Body.GetProperty("revision").GetInt32());
        Assert.Equal(id, Assert.Single(final.Body.GetProperty("project").GetProperty("timeline").EnumerateArray()).GetProperty("clipId").GetString());
    }

    private sealed record Reply(int Status, JsonElement Body, string EpochHeader);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly WebApplication _app;
        private readonly MediaTools _media;
        private readonly List<string> _clips = [];
        public string Root { get; }
        public Store Store { get; }
        public StudioStore Studio { get; }

        public Fixture(ITestOutputHelper output, [CallerFilePath] string sourceFile = "")
        {
            _output = output;
            var repository = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
            Root = Path.Combine(repository, "docs", "merge", "_work", "r82", "FC-EDITOR-INITIAL-CONTRACT-20261114-0324", "fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _output.WriteLine("New isolated SQLite+media fixture (retained): " + Root);
            _media = new MediaTools(FindFfmpeg(repository));
            Assert.True(_media.Available(), "ffmpeg not runnable: " + _media.Ffmpeg);
            _output.WriteLine("ffmpeg=" + _media.Ffmpeg + " ffprobe=" + _media.Ffprobe);
            Store = new Store(Path.Combine(Root, "test.db"));
            Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath);
            Studio.Migrate();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            var mediaService = new StudioMediaService(Store, Studio, _media, Root);
            StudioEndpoints.Map(_app, Store, Studio, mediaService, new AiAnalysisService(Store, Studio), null!, null!, null!, _media, null!, new LiveSettings { StorageRoot = Root });
        }

        private static string FindFfmpeg(string repository)
        {
            var env = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(bundled)) return bundled;
            return Path.Combine(repository, "LiveClipper-BuildCache", "offline", "ffmpeg", "bin", "ffmpeg.exe");
        }

        public string GenerateMedia(string name)
        {
            var dir = Path.Combine(Root, "media");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            using var process = MediaTools.Start(_media.Ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=320x180:rate=25:duration=" + GeneratedSeconds.ToString(CultureInfo.InvariantCulture),
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=" + GeneratedSeconds.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path
            ]);
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0 && File.Exists(path), "ffmpeg generate failed: " + stderr);
            return path;
        }

        public async Task<double> FfprobeDurationAsync(string path)
        {
            using var process = MediaTools.Start(_media.Ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path]);
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            return double.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
        }

        public async Task<Reply> InvokeAsync(string method, string route, string? id, string? body, int? baseRevision, string? epoch)
        {
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                candidate => candidate.RoutePattern.RawText == route && candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = method;
            context.Request.Path = id is null ? route : route.Replace("{id}", id, StringComparison.Ordinal);
            if (id is not null) context.Request.RouteValues["id"] = id;
            if (baseRevision is not null) context.Request.Headers["X-Base-Revision"] = baseRevision.Value.ToString(CultureInfo.InvariantCulture);
            if (epoch is not null) context.Request.Headers["X-Editor-Epoch"] = epoch;
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            await using var input = new MemoryStream(bytes);
            context.Request.Body = input;
            if (body is not null)
            {
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
            }
            await using var response = new MemoryStream();
            context.Response.Body = response;
            _output.WriteLine(method + " " + context.Request.Path + (body is null ? "" : " BODY " + body));
            await endpoint.RequestDelegate!(context);
            var raw = Encoding.UTF8.GetString(response.ToArray());
            _output.WriteLine("RESPONSE HTTP " + context.Response.StatusCode + " " + raw);
            using var json = JsonDocument.Parse(raw);
            // 在端点执行之后才记录：导入产生的 clipId 只有此时才存在；EditorRevisionStore 状态由 GET/PUT 写入，Dispose 时逐个 Forget。
            if (!string.IsNullOrEmpty(id) && !_clips.Contains(id)) _clips.Add(id);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("clipIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                foreach (var clipId in ids.EnumerateArray().Select(e => e.GetString()))
                    if (!string.IsNullOrEmpty(clipId) && !_clips.Contains(clipId)) _clips.Add(clipId);
            return new(context.Response.StatusCode, json.RootElement.Clone(), context.Response.Headers["X-Editor-Epoch"].ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            foreach (var id in _clips) EditorRevisionStore.Forget(id);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
