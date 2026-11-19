using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LiveClipper.Tests;

public sealed class StudioRevisionDurabilityTests
{
    [Fact]
    public async Task SaveProjectCas_SuccessAddsOne_ConflictLeavesDatabaseUnchanged()
    {
        await using var fixture = new Fixture();
        var initial = fixture.Studio.GetProjectRevision(fixture.ClipId);
        var nextJson = JsonSerializer.Serialize(Project(7));

        var saved = fixture.Studio.SaveProjectCas(fixture.ClipId, nextJson, initial);

        Assert.True(saved.Ok);
        Assert.Equal(initial + 1, saved.Revision);
        var reopened = fixture.ReopenStudio();
        var record = reopened.GetProjectRecord(fixture.ClipId);
        Assert.NotNull(record);
        Assert.Equal(nextJson, record!.Json);
        Assert.Equal(initial + 1, record.Revision);
        var stale = reopened.SaveProjectCas(fixture.ClipId, JsonSerializer.Serialize(Project(5)), initial);
        Assert.False(stale.Ok);
        Assert.Equal(initial + 1, stale.Revision);
        Assert.Equal(nextJson, fixture.Studio.Project(fixture.ClipId));
        Assert.Equal(initial + 1, fixture.Studio.GetProjectRevision(fixture.ClipId));
    }

    [Fact]
    public async Task CommittedWrite_StaleBaseReturns409WithCurrentRevision()
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;
        var saved = await fixture.RequestAsync("editor", Number(initial), fixture.Epoch, end: 7);
        Assert.Equal(StatusCodes.Status200OK, saved.Status);
        Assert.Equal(initial + 1, saved.Revision);
        var stored = fixture.ProjectJson;
        var undoDepth = EditorRevisionStore.SnapshotDepth(fixture.ClipId);

        var stale = await fixture.RequestAsync("editor", Number(initial), saved.Epoch, end: 5);

        AssertError(stale, StatusCodes.Status409Conflict, "revision_conflict", "stale-revision", initial + 1);
        Assert.Equal(saved.Epoch, stale.Epoch);
        Assert.Equal(stored, fixture.ProjectJson);
        Assert.Equal(initial + 1, fixture.CurrentRevision);
        Assert.Equal(undoDepth, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restart_ReadsDatabaseRevision_WithFreshEpochAndNoVolatileState(bool forgetEntry)
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;
        var saved = await fixture.RequestAsync("editor", Number(initial), fixture.Epoch, end: 7);
        Assert.Equal(StatusCodes.Status200OK, saved.Status);
        var begin = await fixture.RequestAsync("sessions");
        Assert.Equal(StatusCodes.Status200OK, begin.Status);
        var sessionId = begin.Body.GetProperty("sessionId").GetString()!;
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        var stored = fixture.ProjectJson;

        // The revision store is static: Forget models a cold cache; rebinding models a new host instance.
        await fixture.RestartAsync(forgetEntry);

        Assert.NotEqual(saved.Epoch, fixture.Epoch);
        Assert.Equal(initial + 1, fixture.CurrentRevision);
        Assert.Equal((long)initial + 1, fixture.Studio.GetProjectRevision(fixture.ClipId));
        Assert.Equal(stored, fixture.ProjectJson);
        Assert.Equal(0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        var oldSession = await fixture.RequestAsync("sessions/{sid}/end", sessionId: sessionId);
        AssertError(oldSession, StatusCodes.Status409Conflict, "session_not_found", "session-mismatch", initial + 1);
        var undo = await fixture.RequestAsync("undo", Number(initial + 1), fixture.Epoch);
        AssertError(undo, StatusCodes.Status409Conflict, "nothing_to_undo", "nothing-to-undo", initial + 1);
        var next = await fixture.RequestAsync("editor", Number(initial + 1), fixture.Epoch, end: 6);
        Assert.Equal(StatusCodes.Status200OK, next.Status);
        Assert.Equal(initial + 2, next.Revision);
        Assert.Equal(fixture.Epoch, next.Epoch);
        Assert.Equal((long)initial + 2, fixture.ReopenStudio().GetProjectRevision(fixture.ClipId));
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("commands")]
    [InlineData("undo")]
    [InlineData("redo")]
    public async Task WriteWithoutBaseRevision_Returns428AndWritesNothing(string route)
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;
        var stored = fixture.ProjectJson;

        var rejected = await fixture.RequestAsync(route, epoch: fixture.Epoch);

        AssertError(rejected, StatusCodes.Status428PreconditionRequired, "precondition_required", "precondition-required", initial);
        Assert.Equal(fixture.Epoch, rejected.Epoch);
        AssertUnchanged(fixture, stored, initial);
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("commands")]
    [InlineData("undo")]
    [InlineData("redo")]
    public async Task WriteWithNonnumericBaseRevision_Returns400AndWritesNothing(string route)
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;
        var stored = fixture.ProjectJson;

        var rejected = await fixture.RequestAsync(route, "not-a-number", fixture.Epoch);

        AssertError(rejected, StatusCodes.Status400BadRequest, "invalid_revision", "invalid-revision", initial);
        Assert.Equal(fixture.Epoch, rejected.Epoch);
        AssertUnchanged(fixture, stored, initial);
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("commands")]
    [InlineData("undo")]
    [InlineData("redo")]
    public async Task WriteWithOldEpoch_Returns409EvenWhenDurableRevisionMatches(string route)
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;
        var saved = await fixture.RequestAsync("editor", Number(initial), fixture.Epoch, end: 7);
        Assert.Equal(StatusCodes.Status200OK, saved.Status);
        var oldEpoch = saved.Epoch;
        Assert.False(string.IsNullOrWhiteSpace(oldEpoch));
        await fixture.RestartAsync(forgetEntry: true);
        var revision = fixture.CurrentRevision;
        var stored = fixture.ProjectJson;
        Assert.Equal(initial + 1, revision);
        Assert.NotEqual(oldEpoch, fixture.Epoch);

        var rejected = await fixture.RequestAsync(route, Number(revision), oldEpoch);

        AssertError(rejected, StatusCodes.Status409Conflict, "stale_epoch", "stale-epoch", revision);
        Assert.Equal(fixture.Epoch, rejected.Epoch);
        Assert.Equal(fixture.Epoch, rejected.Body.GetProperty("epoch").GetString());
        AssertUnchanged(fixture, stored, revision);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("header")]
    [InlineData("both")]
    public async Task Commands_AcceptMatchingBodyOrHeaderBaseRevision(string source)
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;

        var saved = await fixture.RequestAsync("commands", source == "body" ? null : Number(initial), fixture.Epoch,
            commandRevision: source == "header" ? null : initial);

        Assert.Equal(StatusCodes.Status200OK, saved.Status);
        Assert.Equal(initial + 1, saved.Revision);
        Assert.Equal(fixture.Epoch, saved.Epoch);
        Assert.Equal(6d, Parse(fixture.ProjectJson).End);
        Assert.Equal(initial + 1, fixture.CurrentRevision);
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
    }

    [Fact]
    public async Task Commands_DisagreeingHeaderAndBody_Return400BeforeWrite()
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;
        var stored = fixture.ProjectJson;

        var rejected = await fixture.RequestAsync("commands", Number(initial), fixture.Epoch, commandRevision: initial + 1);

        AssertError(rejected, StatusCodes.Status400BadRequest, "invalid_revision", "invalid-revision", initial);
        AssertUnchanged(fixture, stored, initial);
    }

    [Fact]
    public async Task SaveExternal_UsesFreshDatabaseJsonAndRevision_AfterRivalStoreCommit()
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;
        var rival = fixture.ReopenStudio();
        var rivalJson = JsonSerializer.Serialize(Project(7) with { Markers = [1.25] });
        var rivalSave = rival.SaveProjectCas(fixture.ClipId, rivalJson, initial);
        Assert.True(rivalSave.Ok);
        Assert.Equal((long)initial + 1, rivalSave.Revision);
        string? seen = null;

        var saved = EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "auto-edit", latest =>
        {
            seen = latest;
            return JsonSerializer.Serialize(Parse(latest!) with { End = 6 });
        });

        Assert.Equal(rivalJson, seen);
        Assert.True(saved.Saved);
        Assert.Equal(initial + 2, saved.Revision);
        Assert.Equal(initial + 2, fixture.CurrentRevision);
        Assert.Equal(6d, Parse(fixture.ProjectJson).End);
        Assert.Equal(new[] { 1.25 }, Parse(fixture.ProjectJson).Markers);
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(rivalJson, EditorRevisionStore.PopSnapshot(fixture.ClipId));
    }

    [Fact]
    public async Task SaveExternal_RivalCommitDuringCallback_ConflictsWithoutLosingHistoryOrSession()
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;
        Assert.Equal(StatusCodes.Status200OK, (await fixture.RequestAsync("editor", Number(initial), end: 7)).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.RequestAsync("editor", Number(initial + 1), end: 6)).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.RequestAsync("undo", Number(initial + 2))).Status);
        var begin = await fixture.RequestAsync("sessions");
        Assert.Equal(StatusCodes.Status200OK, begin.Status);
        var sessionId = begin.Body.GetProperty("sessionId").GetString()!;
        var revision = fixture.CurrentRevision;
        var stored = fixture.ProjectJson;
        var undoDepth = EditorRevisionStore.SnapshotDepth(fixture.ClipId);
        var redoDepth = EditorRevisionStore.RedoDepth(fixture.ClipId);
        Assert.Equal(1, undoDepth);
        Assert.Equal(1, redoDepth);
        var rival = fixture.ReopenStudio();
        var rivalJson = JsonSerializer.Serialize(Project(5) with { Markers = [2.5] });
        var callbackCount = 0;

        var conflict = Assert.Throws<EditorRevisionConflictException>(() =>
            EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "music-ducking", latest =>
            {
                callbackCount++;
                Assert.Equal(stored, latest);
                var saved = rival.SaveProjectCas(fixture.ClipId, rivalJson, revision);
                Assert.True(saved.Ok);
                Assert.Equal((long)revision + 1, saved.Revision);
                return JsonSerializer.Serialize(Parse(latest!) with { End = 4 });
            }));

        Assert.Equal(1, callbackCount);
        Assert.Equal("stale-revision", conflict.Code);
        Assert.Equal(revision + 1, conflict.Revision);
        Assert.Equal(rivalJson, fixture.ProjectJson);
        Assert.Equal(revision + 1, fixture.CurrentRevision);
        Assert.Equal(undoDepth, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(redoDepth, EditorRevisionStore.RedoDepth(fixture.ClipId));
        var ended = await fixture.RequestAsync("sessions/{sid}/end", sessionId: sessionId);
        Assert.Equal(StatusCodes.Status200OK, ended.Status);
        Assert.Equal(revision, ended.Body.GetProperty("checkpointRevision").GetInt32());
        Assert.Equal(revision + 1, ended.Revision);
        Assert.Equal(undoDepth, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(redoDepth, EditorRevisionStore.RedoDepth(fixture.ClipId));
    }

    [Fact]
    public async Task ProviderDefaultTransaction_ReservesWriterBeforeAnySelect()
    {
        await using var fixture = new Fixture();
        var initial = fixture.CurrentRevision;
        var stored = fixture.ProjectJson;
        using var owner = fixture.OpenDatabase();
        using var contender = fixture.OpenDatabase();
        contender.DefaultTimeout = 1;
        using var reservation = owner.BeginTransaction();
        using var competingWrite = contender.CreateCommand();
        competingWrite.CommandText = "BEGIN IMMEDIATE;";

        // No SELECT or UPDATE has run on owner: failure here distinguishes IMMEDIATE from deferred BEGIN.
        var busy = Assert.Throws<SqliteException>(() => competingWrite.ExecuteNonQuery());

        Assert.Equal(5, busy.SqliteErrorCode);
        reservation.Rollback();
        using var retry = contender.BeginTransaction();
        retry.Rollback();
        AssertUnchanged(fixture, stored, initial);
    }

    private static EditorProject Project(double end) => new() { Start = 0, End = end };
    private static EditorProject Parse(string json) => JsonSerializer.Deserialize<EditorProject>(json)!;
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void AssertError(Reply reply, int status, string code, string nestedCode, int revision)
    {
        Assert.Equal(status, reply.Status);
        Assert.Equal(code, reply.Body.GetProperty("code").GetString());
        Assert.Equal(nestedCode, reply.Body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(revision, reply.Revision);
    }

    private static void AssertUnchanged(Fixture fixture, string stored, int revision)
    {
        Assert.Equal(stored, fixture.ProjectJson);
        Assert.Equal(revision, fixture.CurrentRevision);
        Assert.Equal((long)revision, fixture.Studio.GetProjectRevision(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
    }

    private sealed record Reply(int Status, JsonElement Body, string Epoch)
    {
        public int Revision => Body.GetProperty("revision").GetInt32();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-r52-y2e-" + Guid.NewGuid().ToString("N"));
        private readonly Store _store;
        private WebApplication _app;
        public string ClipId { get; } = "r52-y2e-" + Guid.NewGuid().ToString("N");
        public string DatabasePath { get; }
        public StudioStore Studio { get; private set; }
        public int CurrentRevision => EditorRevisionStore.Current(Studio, ClipId);
        public string Epoch => EditorRevisionStore.EpochFor(Studio);
        public string ProjectJson => Studio.Project(ClipId)!;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "test.db");
            _store = new Store(DatabasePath);
            _store.Migrate();
            Studio = new StudioStore(DatabasePath);
            Studio.Migrate();
            _store.SaveClip(new Clip(ClipId, "event-" + ClipId, Path.Combine(_root, "source.mp4"), null, DateTimeOffset.UtcNow));
            Studio.SaveProject(ClipId, JsonSerializer.Serialize(Project(8)));
            _app = BuildApp();
        }

        private WebApplication BuildApp()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            var app = builder.Build();
            // These routes use stored projects; an unexpected media/AI dependency must fail, not be mocked.
            StudioEndpoints.Map(app, _store, Studio, null!, null!, null!, null!, null!, null!, null!, new LiveSettings { StorageRoot = _root });
            return app;
        }

        public StudioStore ReopenStudio() => new(DatabasePath);

        public async Task RestartAsync(bool forgetEntry)
        {
            await _app.DisposeAsync();
            if (forgetEntry) EditorRevisionStore.Forget(ClipId);
            Studio = ReopenStudio();
            Studio.Migrate();
            _app = BuildApp();
        }

        public SqliteConnection OpenDatabase()
        {
            var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            return connection;
        }

        public async Task<Reply> RequestAsync(string route, string? baseRevision = null, string? epoch = null,
            int? commandRevision = null, double end = 6, string? sessionId = null)
        {
            var method = route == "editor" ? "PUT" : "POST";
            object? payload = route == "editor" ? Project(end)
                : route == "commands" ? new EditorCommandBatch(commandRevision,
                    [new EditorCommand("setRange", new JsonObject { ["start"] = 0, ["end"] = end })])
                : null;
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources
                .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                candidate => candidate.RoutePattern.RawText == "/api/studio/clips/{id}/" + route
                    && candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = method;
            context.Request.RouteValues["id"] = ClipId;
            if (sessionId is not null) context.Request.RouteValues["sid"] = sessionId;
            if (baseRevision is not null) context.Request.Headers["X-Base-Revision"] = baseRevision;
            if (epoch is not null) context.Request.Headers["X-Editor-Epoch"] = epoch;
            byte[] bytes = payload is null ? [] : JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await using var input = new MemoryStream(bytes);
            if (payload is not null)
            {
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = input;
            }
            await using var output = new MemoryStream();
            context.Response.Body = output;
            await endpoint.RequestDelegate!(context);
            output.Position = 0;
            using var json = await JsonDocument.ParseAsync(output);
            return new Reply(context.Response.StatusCode, json.RootElement.Clone(), context.Response.Headers["X-Editor-Epoch"].ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            EditorRevisionStore.Forget(ClipId);
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
        }
    }

    private sealed class BodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
