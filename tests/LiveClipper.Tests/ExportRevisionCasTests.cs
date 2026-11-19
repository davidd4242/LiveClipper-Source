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

namespace LiveClipper.Tests;

public sealed class ExportRevisionCasTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string RenderRoute = "/api/studio/clips/{id}/render";
    private const string EditorRoute = "/api/studio/clips/{id}/editor";
    private const string RendersRoute = "/api/studio/renders";
    private const string JobsRoute = "/api/jobs";

    [Fact]
    public async Task Matching_headers_start_render_and_record_confirmed_revision_and_epoch()
    {
        await using var fixture = new Fixture();
        fixture.RequireFfmpeg();
        var revision = fixture.Revision;
        var response = await fixture.StartAsync(revision.ToString(), fixture.Epoch);
        Assert.Equal(200, response.Status);
        Assert.Equal(fixture.Epoch, response.Epoch);
        var started = response.Json();
        Assert.Equal(fixture.Clip.Id, started.GetProperty("clipId").GetString());
        Assert.Equal(revision, started.GetProperty("revision").GetInt32());
        Assert.Equal(fixture.Epoch, started.GetProperty("epoch").GetString());
        Assert.Single(fixture.Store.Jobs());
        Assert.Single(fixture.Studio.Renders());
    }

    [Fact]
    public async Task Stale_revision_matches_PUT_conflict_contract_and_does_not_queue()
    {
        await using var fixture = new Fixture();
        var stale = fixture.Revision;
        fixture.Advance();
        var render = await fixture.StartAsync(stale.ToString(), fixture.Epoch);
        var put = await fixture.InvokeAsync("PUT", EditorRoute, fixture.ProjectJson, stale.ToString(), fixture.Epoch);
        AssertConflict(render, "stale-revision", fixture.Revision);
        Assert.Equal(put.Json().GetProperty("code").GetString(), render.Json().GetProperty("code").GetString());
        Assert.Equal(put.Json().GetProperty("error").GetRawText(), render.Json().GetProperty("error").GetRawText());
        Assert.Empty(fixture.Store.Jobs());
        Assert.Empty(fixture.Studio.Renders());
    }

    [Fact]
    public async Task Stale_epoch_matches_PUT_conflict_contract_and_does_not_queue()
    {
        await using var fixture = new Fixture();
        var render = await fixture.StartAsync(fixture.Revision.ToString(), "previous-server-epoch");
        var put = await fixture.InvokeAsync("PUT", EditorRoute, fixture.ProjectJson, fixture.Revision.ToString(), "previous-server-epoch");
        AssertConflict(render, "stale-epoch", fixture.Revision);
        Assert.Equal(fixture.Epoch, render.Json().GetProperty("epoch").GetString());
        Assert.Equal(put.Json().GetProperty("code").GetString(), render.Json().GetProperty("code").GetString());
        Assert.Equal(put.Json().GetProperty("error").GetRawText(), render.Json().GetProperty("error").GetRawText());
        Assert.Empty(fixture.Store.Jobs());
        Assert.Empty(fixture.Studio.Renders());
    }

    [Fact]
    public async Task Absent_headers_keep_legacy_body_behavior_but_record_current_origin()
    {
        await using var fixture = new Fixture();
        fixture.RequireFfmpeg();
        var olderBody = fixture.ProjectJson;
        fixture.Advance();
        var response = await fixture.InvokeAsync("POST", RenderRoute, olderBody);
        Assert.Equal(200, response.Status);
        Assert.Equal(fixture.Revision, response.Json().GetProperty("revision").GetInt32());
        Assert.Equal(fixture.Epoch, response.Json().GetProperty("epoch").GetString());
        // Legacy compatibility means the body is not substituted with the saved project.
        var persisted = EditorProjectCodec.Deserialize(Assert.Single(fixture.Studio.Renders()).ProjectJson);
        Assert.Equal(4, persisted.End);
    }

    [Fact]
    public async Task Each_supplied_header_is_enforced_even_when_the_other_is_absent()
    {
        await using var fixture = new Fixture();
        var stale = fixture.Revision;
        fixture.Advance();
        AssertConflict(await fixture.StartAsync(stale.ToString()), "stale-revision", fixture.Revision);
        AssertConflict(await fixture.StartAsync(epoch: "old-epoch"), "stale-epoch", fixture.Revision);
        Assert.Empty(fixture.Store.Jobs());
        Assert.Empty(fixture.Studio.Renders());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Matching_single_header_can_start_and_records_both_origin_values(bool revisionHeader, bool epochHeader)
    {
        await using var fixture = new Fixture();
        fixture.RequireFfmpeg();
        var response = await fixture.StartAsync(revisionHeader ? fixture.Revision.ToString() : null, epochHeader ? fixture.Epoch : null);
        Assert.Equal(200, response.Status);
        Assert.Equal(fixture.Revision, response.Json().GetProperty("revision").GetInt32());
        Assert.Equal(fixture.Epoch, response.Json().GetProperty("epoch").GetString());
        Assert.Single(fixture.Store.Jobs());
    }

    [Fact]
    public async Task Duplicate_revision_headers_fail_closed_and_do_not_queue()
    {
        await using var fixture = new Fixture();
        var response = await fixture.InvokeAsync("POST", RenderRoute, fixture.ProjectJson, duplicateRevision: true);
        Assert.Equal(400, response.Status);
        Assert.Equal("invalid-revision", response.Json().GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fixture.Store.Jobs());
        Assert.Empty(fixture.Studio.Renders());
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("2147483648")]
    public async Task Present_malformed_revision_is_not_treated_as_legacy(string value)
    {
        await using var fixture = new Fixture();
        var response = await fixture.StartAsync(value);
        Assert.Equal(400, response.Status);
        Assert.Equal("invalid-revision", response.Json().GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(fixture.Revision, response.Json().GetProperty("revision").GetInt32());
        Assert.Empty(fixture.Store.Jobs());
        Assert.Empty(fixture.Studio.Renders());
    }

    [Fact]
    public async Task Status_and_reopened_store_keep_job_origin_after_another_save()
    {
        await using var fixture = new Fixture();
        fixture.RequireFfmpeg();
        var revision = fixture.Revision;
        var response = await fixture.StartAsync(revision.ToString(), fixture.Epoch);
        Assert.Equal(200, response.Status);
        var id = response.Json().GetProperty("id").GetString()!;
        fixture.Advance();
        var reopened = new StudioStore(fixture.Store.DatabasePath);
        var persisted = Assert.Single(reopened.Renders(), x => x.Id == id);
        Assert.Equal(revision, persisted.Revision);
        Assert.Equal(fixture.Epoch, persisted.Epoch);
        foreach (var route in new[] { RendersRoute, JobsRoute })
        {
            var status = await fixture.InvokeAsync("GET", route);
            Assert.Equal(200, status.Status);
            var job = Assert.Single(status.Json().EnumerateArray(), x => x.GetProperty("id").GetString() == id);
            Assert.Equal(fixture.Clip.Id, job.GetProperty("clipId").GetString());
            Assert.Equal(revision, job.GetProperty("revision").GetInt32());
            Assert.Equal(fixture.Epoch, job.GetProperty("epoch").GetString());
        }
    }

    [Fact]
    public async Task Jobs_for_two_clips_are_isolated_by_render_id_and_clip()
    {
        await using var fixture = new Fixture();
        fixture.RequireFfmpeg();
        // clips.event_id is UNIQUE (one clip per event): a second clip needs its own event; isolation is by render id + clip id.
        var second = fixture.AddClip(Guid.NewGuid().ToString("N"));
        var firstResponse = await fixture.StartAsync(fixture.Revision.ToString(), fixture.Epoch);
        var secondResponse = await fixture.StartAsync(EditorRevisionStore.Current(fixture.Studio, second.Id).ToString(), fixture.Epoch, second.Id);
        Assert.Equal(200, firstResponse.Status);
        Assert.Equal(200, secondResponse.Status);
        var firstId = firstResponse.Json().GetProperty("id").GetString()!;
        var secondId = secondResponse.Json().GetProperty("id").GetString()!;
        Assert.NotEqual(firstId, secondId);
        foreach (var route in new[] { RendersRoute, JobsRoute })
        {
            var status = await fixture.InvokeAsync("GET", route);
            Assert.Equal(200, status.Status);
            var entries = status.Json().EnumerateArray().ToArray();
            Assert.Equal(fixture.Clip.Id, Assert.Single(entries, x => x.GetProperty("id").GetString() == firstId).GetProperty("clipId").GetString());
            Assert.Equal(second.Id, Assert.Single(entries, x => x.GetProperty("id").GetString() == secondId).GetProperty("clipId").GetString());
            Assert.DoesNotContain(entries, x => x.GetProperty("id").GetString() == firstId && x.GetProperty("clipId").GetString() == second.Id);
        }
        Assert.Equal(fixture.Clip.Id, Assert.Single(fixture.Studio.Renders(), x => x.Id == firstId).ClipId);
        Assert.Equal(second.Id, Assert.Single(fixture.Studio.Renders(), x => x.Id == secondId).ClipId);
    }

    [Fact]
    public async Task Pre_origin_render_rows_remain_readable_with_null_metadata()
    {
        await using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        fixture.Studio.SaveRender(new EditorRender(id, fixture.Clip.Id, "legacy.mp4", fixture.ProjectJson, DateTimeOffset.UtcNow));
        fixture.Store.SaveJob(new Job(id, "edit-render", fixture.Clip.EventId, "failed", 0, "legacy"));
        var render = Assert.Single(new StudioStore(fixture.Store.DatabasePath).Renders());
        Assert.Null(render.Revision);
        Assert.Null(render.Epoch);
        foreach (var route in new[] { RendersRoute, JobsRoute })
        {
            var status = await fixture.InvokeAsync("GET", route);
            var entry = Assert.Single(status.Json().EnumerateArray());
            Assert.Equal(fixture.Clip.Id, entry.GetProperty("clipId").GetString());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("revision").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("epoch").ValueKind);
        }
    }

    [Fact]
    public async Task Unrelated_job_status_preserves_all_existing_fields_without_claiming_a_render_origin()
    {
        await using var fixture = new Fixture();
        var startedAt = DateTimeOffset.UtcNow;
        var finishedAt = startedAt.AddSeconds(2);
        fixture.Store.SaveJob(new Job("transcription", "transcribe", fixture.Clip.EventId, "failed", .5, "test error", startedAt, finishedAt, 2, "test message"));
        var status = await fixture.InvokeAsync("GET", JobsRoute);
        Assert.Equal(200, status.Status);
        var entry = Assert.Single(status.Json().EnumerateArray());
        Assert.Equal("transcription", entry.GetProperty("id").GetString());
        Assert.Equal("transcribe", entry.GetProperty("type").GetString());
        Assert.Equal(fixture.Clip.EventId, entry.GetProperty("eventId").GetString());
        Assert.Equal("failed", entry.GetProperty("status").GetString());
        Assert.Equal(.5, entry.GetProperty("progress").GetDouble());
        Assert.Equal("test error", entry.GetProperty("error").GetString());
        // Store.SaveJob owns the timestamps (failed => finished_at set, started_at stays NULL); compare with what the store reports.
        var stored = Assert.Single(fixture.Store.Jobs());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("startedAt").ValueKind);
        Assert.Null(stored.StartedAt);
        Assert.Equal(stored.FinishedAt, entry.GetProperty("finishedAt").GetDateTimeOffset());
        Assert.Equal(2, entry.GetProperty("retryCount").GetInt32());
        Assert.Equal("test message", entry.GetProperty("message").GetString());
        foreach (var property in new[] { "clipId", "revision", "epoch" }) Assert.Equal(JsonValueKind.Null, entry.GetProperty(property).ValueKind);
    }

    private static void AssertConflict(Response response, string code, int revision)
    {
        Assert.Equal(409, response.Status);
        Assert.Equal(code, response.Json().GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(revision, response.Json().GetProperty("revision").GetInt32());
    }

    private sealed record Response(int Status, byte[] Body, string Epoch)
    {
        public JsonElement Json() => JsonDocument.Parse(Body).RootElement.Clone();
    }

    // Same real route-delegate host pattern as AutoPlanEndpointTests; no mocked CAS or renderer.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly EditorRenderService _editor;
        private readonly MediaTools _media;
        private readonly List<string> _clipIds = [];
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "liveclipper-export-cas-" + Guid.NewGuid().ToString("N"));
        public Store Store { get; }
        public StudioStore Studio { get; }
        public Clip Clip { get; }
        public string ProjectJson => JsonSerializer.Serialize(Project(Clip.Id), Web);
        public int Revision => EditorRevisionStore.Current(Studio, Clip.Id);
        public string Epoch => EditorRevisionStore.EpochFor(Studio);

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new Store(Path.Combine(Root, "test.db")); Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath); Studio.Migrate();
            Clip = AddClip(Guid.NewGuid().ToString("N"));
            _media = new MediaTools(FindFfmpeg());
            _editor = new EditorRenderService(Store, Studio, _media, Root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            StudioEndpoints.Map(_app, Store, Studio, null!, null!, null!, null!, _editor, _media, null!, new LiveSettings());
        }

        public Clip AddClip(string eventId)
        {
            var clip = new Clip(Guid.NewGuid().ToString("N"), eventId, Path.Combine(Root, "missing-source.mp4"), null, DateTimeOffset.UtcNow);
            Store.SaveClip(clip);
            Studio.SaveProject(clip.Id, JsonSerializer.Serialize(Project(clip.Id), Web));
            EditorRevisionStore.Current(Studio, clip.Id);
            _clipIds.Add(clip.Id);
            return clip;
        }

        private static EditorProject Project(string clipId) => new() { Start = 0, End = 4, Timeline = [new("base", clipId, 0, 4)] };
        public void Advance()
        {
            var committed = EditorRevisionStore.Commit(Studio, Clip.Id, JsonSerializer.Serialize(Project(Clip.Id) with { End = 5 }, Web), Revision);
            Assert.True(committed.Ok);
        }
        public void RequireFfmpeg() => Assert.True(_media.Available(), "Real render starts require FFmpeg; set LIVECLIPPER_TEST_FFMPEG. This test must not skip.");
        private static string FindFfmpeg()
        {
            var configured = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
            return File.Exists(bundled) ? bundled : "ffmpeg";
        }

        public Task<Response> StartAsync(string? revision = null, string? epoch = null, string? id = null) =>
            InvokeAsync("POST", RenderRoute, JsonSerializer.Serialize(Project(id ?? Clip.Id), Web), revision, epoch, id);

        public async Task<Response> InvokeAsync(string method, string pattern, string? body = null, string? revision = null, string? epoch = null, string? id = null, bool duplicateRevision = false)
        {
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>(),
                x => x.RoutePattern.RawText == pattern && x.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = method;
            context.Request.RouteValues["id"] = id ?? Clip.Id;
            if (revision is not null) context.Request.Headers["X-Base-Revision"] = revision;
            if (duplicateRevision) context.Request.Headers["X-Base-Revision"] = new Microsoft.Extensions.Primitives.StringValues([Revision.ToString(), Revision.ToString()]);
            if (epoch is not null) context.Request.Headers["X-Editor-Epoch"] = epoch;
            var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
            await using var input = new MemoryStream(bytes);
            context.Request.Body = input;
            if (body is not null)
            {
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
            }
            await using var output = new MemoryStream();
            context.Response.Body = output;
            await endpoint.RequestDelegate!(context);
            return new(context.Response.StatusCode, output.ToArray(), context.Response.Headers["X-Editor-Epoch"].ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await _editor.StopAsync();
            await _app.DisposeAsync();
            foreach (var id in _clipIds) EditorRevisionStore.Forget(id);
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
