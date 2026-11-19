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

public sealed class EditorCommandSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentSameBaseWrites_OnlyOneCommits(bool firstIsPut, bool secondIsPut)
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        using var ready = new CountdownEvent(2);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(int Status, JsonElement Body)> Submit(bool put, double end)
        {
            ready.Signal();
            await start.Task;
            return put
                ? await fixture.PutAsync(Project(end), initialRevision)
                : await fixture.CommandsAsync(initialRevision, null, Range(end));
        }
        var first = Task.Run(() => Submit(firstIsPut, 6));
        var second = Task.Run(() => Submit(secondIsPut, 5));
        var allReady = ready.Wait(TimeSpan.FromSeconds(10));
        start.SetResult(true);
        var results = await Task.WhenAll(first, second);

        Assert.True(allReady, "Both workers must reach the shared start gate.");
        var winner = Assert.Single(results, result => result.Status == StatusCodes.Status200OK);
        var loser = Assert.Single(results, result => result.Status == StatusCodes.Status409Conflict);
        Assert.Equal(initialRevision + 1, Revision(winner.Body));
        Assert.Equal(initialRevision + 1, Revision(loser.Body));
        Assert.Equal("stale-revision", Code(loser.Body));
        Assert.Equal(initialRevision + 1, fixture.CurrentRevision);
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        Assert.Equal(results[0].Status == StatusCodes.Status200OK ? 6d : 5d, fixture.Project.End);
        var undo = await fixture.HistoryAsync("undo", initialRevision + 1);
        Assert.Equal(StatusCodes.Status200OK, undo.Status);
        Assert.Equal(8d, fixture.Project.End);
    }

    [Fact]
    public async Task Session_ThreeCommandsBecomeOneUndoCheckpoint()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        var begin = await fixture.RequestAsync("sessions");
        Assert.Equal(StatusCodes.Status200OK, begin.Status);
        var sessionId = begin.Body.GetProperty("sessionId").GetString()!;
        Assert.Equal(initialRevision, begin.Body.GetProperty("checkpointRevision").GetInt32());
        for (var index = 0; index < 3; index++)
        {
            var result = await fixture.CommandsAsync(initialRevision + index, sessionId, Range(7 - index));
            Assert.Equal(StatusCodes.Status200OK, result.Status);
            Assert.Equal(initialRevision + index + 1, Revision(result.Body));
            Assert.Equal(0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        }
        var ended = await fixture.EndAsync(sessionId);
        Assert.Equal(StatusCodes.Status200OK, ended.Status);
        Assert.Equal(initialRevision, ended.Body.GetProperty("checkpointRevision").GetInt32());
        Assert.Equal(initialRevision + 3, Revision(ended.Body));
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(5d, fixture.Project.End);

        var undo = await fixture.HistoryAsync("undo", initialRevision + 3);
        Assert.Equal(StatusCodes.Status200OK, undo.Status);
        Assert.Equal(initialRevision + 4, Revision(undo.Body));
        Assert.Equal(8d, fixture.Project.End);
        var empty = await fixture.HistoryAsync("undo", initialRevision + 4);
        Assert.Equal(StatusCodes.Status409Conflict, empty.Status);
        Assert.Equal("nothing-to-undo", Code(empty.Body));
        var redo = await fixture.HistoryAsync("redo", initialRevision + 4);
        Assert.Equal(StatusCodes.Status200OK, redo.Status);
        Assert.Equal(initialRevision + 5, Revision(redo.Body));
        Assert.Equal(5d, fixture.Project.End);
    }

    [Fact]
    public async Task ExistingSession_ReturnsBusyWithHolderPrefix_WithoutBlockingAnotherClip()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        await using var other = new Fixture();
        var first = await fixture.RequestAsync("sessions");
        var sessionId = first.Body.GetProperty("sessionId").GetString()!;
        var busy = await fixture.RequestAsync("sessions");
        Assert.Equal(StatusCodes.Status409Conflict, busy.Status);
        Assert.Equal("session-busy", Code(busy.Body));
        Assert.Equal(sessionId[..8], busy.Body.GetProperty("error").GetProperty("holder").GetString());
        Assert.Equal(initialRevision, Revision(busy.Body));
        Assert.Equal(StatusCodes.Status200OK, (await other.RequestAsync("sessions")).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.EndAsync(sessionId)).Status);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "wrong-session")]
    [InlineData(true, null)]
    [InlineData(true, "wrong-session")]
    public async Task Session_RejectsForeignAndMissingIds(bool put, string? suppliedId)
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        var begin = await fixture.RequestAsync("sessions");
        var sessionId = begin.Body.GetProperty("sessionId").GetString()!;
        var rejected = put
            ? await fixture.PutAsync(Project(6), initialRevision, suppliedId)
            : await fixture.CommandsAsync(initialRevision, suppliedId, Range(6));
        Assert.Equal(StatusCodes.Status409Conflict, rejected.Status);
        Assert.Equal("session-mismatch", Code(rejected.Body));
        Assert.Equal(initialRevision, Revision(rejected.Body));
        Assert.Equal(8d, fixture.Project.End);
        Assert.Equal(0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        var wrongEnd = await fixture.EndAsync("wrong-session");
        Assert.Equal(StatusCodes.Status409Conflict, wrongEnd.Status);
        Assert.Equal("session-mismatch", Code(wrongEnd.Body));
        Assert.Equal(StatusCodes.Status200OK, (await fixture.EndAsync(sessionId)).Status);
        Assert.Equal(0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
    }

    [Fact]
    public async Task PutAndCommands_ShareSessionCheckpoint_AndHistoryCannotSplitIt()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        var begin = await fixture.RequestAsync("sessions");
        var sessionId = begin.Body.GetProperty("sessionId").GetString()!;
        Assert.Equal(StatusCodes.Status200OK, (await fixture.PutAsync(Project(6), initialRevision, sessionId)).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision + 1, sessionId, Range(5))).Status);
        var history = await fixture.HistoryAsync("undo", initialRevision + 2, sessionId);
        Assert.Equal(StatusCodes.Status409Conflict, history.Status);
        Assert.Equal("session-busy", Code(history.Body));
        Assert.Equal(initialRevision + 2, Revision(history.Body));
        Assert.Equal(StatusCodes.Status200OK, (await fixture.EndAsync(sessionId)).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.HistoryAsync("undo", initialRevision + 2)).Status);
        Assert.Equal(8d, fixture.Project.End);
        Assert.Equal(0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
    }

    [Fact]
    public async Task UndoRedo_RoundTrips_AndRevisionsStayMonotonic()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        var empty = await fixture.HistoryAsync("redo", initialRevision);
        Assert.Equal(StatusCodes.Status409Conflict, empty.Status);
        Assert.Equal("nothing-to-redo", Code(empty.Body));
        Assert.Equal(initialRevision, Revision(empty.Body));
        Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision, null, Range(6))).Status);
        var undo = await fixture.HistoryAsync("undo", initialRevision + 1);
        Assert.Equal(StatusCodes.Status200OK, undo.Status);
        Assert.Equal(initialRevision + 2, Revision(undo.Body));
        Assert.Equal(8d, fixture.Project.End);
        Assert.Equal(1, EditorRevisionStore.RedoDepth(fixture.ClipId));
        var redo = await fixture.HistoryAsync("redo", initialRevision + 2);
        Assert.Equal(StatusCodes.Status200OK, redo.Status);
        Assert.Equal(initialRevision + 3, Revision(redo.Body));
        Assert.Equal(6d, fixture.Project.End);
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(StatusCodes.Status200OK, (await fixture.HistoryAsync("undo", initialRevision + 3)).Status);
        Assert.Equal(initialRevision + 4, fixture.CurrentRevision);
        Assert.Equal(8d, fixture.Project.End);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewWrite_ClearsRedo(bool put)
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision, null, Range(6))).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.HistoryAsync("undo", initialRevision + 1)).Status);
        var write = put
            ? await fixture.PutAsync(Project(5), initialRevision + 2)
            : await fixture.CommandsAsync(initialRevision + 2, null, Range(5));
        Assert.Equal(StatusCodes.Status200OK, write.Status);
        Assert.Equal(initialRevision + 3, Revision(write.Body));
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        var redo = await fixture.HistoryAsync("redo", initialRevision + 3);
        Assert.Equal(StatusCodes.Status409Conflict, redo.Status);
        Assert.Equal("nothing-to-redo", Code(redo.Body));
        Assert.Equal(initialRevision + 3, Revision(redo.Body));
        Assert.Equal(5d, fixture.Project.End);
    }

    [Fact]
    public async Task StaleHistoryRequest_DoesNotConsumeEitherStack()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision, null, Range(6))).Status);
        var staleUndo = await fixture.HistoryAsync("undo", initialRevision);
        Assert.Equal(StatusCodes.Status409Conflict, staleUndo.Status);
        Assert.Equal("stale-revision", Code(staleUndo.Body));
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        Assert.Equal(6d, fixture.Project.End);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.HistoryAsync("undo", initialRevision + 1)).Status);
        var staleRedo = await fixture.HistoryAsync("redo", initialRevision + 1);
        Assert.Equal(StatusCodes.Status409Conflict, staleRedo.Status);
        Assert.Equal("stale-revision", Code(staleRedo.Body));
        Assert.Equal(1, EditorRevisionStore.RedoDepth(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(8d, fixture.Project.End);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.HistoryAsync("redo", initialRevision + 2)).Status);
        Assert.Equal(6d, fixture.Project.End);
    }

    [Fact]
    public async Task InvalidBatch_DoesNotPersistPrefixOrClearRedo()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision, null, Range(6))).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.HistoryAsync("undo", initialRevision + 1)).Status);
        var invalid = await fixture.CommandsAsync(initialRevision + 2, null, Range(5), new EditorCommand("unsupported", null));
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.Status);
        Assert.Equal("invalid-command", Code(invalid.Body));
        Assert.Equal(initialRevision + 2, Revision(invalid.Body));
        Assert.Equal(8d, fixture.Project.End);
        Assert.Equal(0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(1, EditorRevisionStore.RedoDepth(fixture.ClipId));
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("commands")]
    [InlineData("undo")]
    [InlineData("redo")]
    public async Task FailedSave_LeavesProjectRevisionAndHistoryUnchanged(string route)
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        if (route is "undo" or "redo")
            Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision, null, Range(6))).Status);
        if (route == "redo")
            Assert.Equal(StatusCodes.Status200OK, (await fixture.HistoryAsync("undo", initialRevision + 1)).Status);
        var previous = fixture.ProjectJson;
        var revision = fixture.CurrentRevision;
        var undoDepth = EditorRevisionStore.SnapshotDepth(fixture.ClipId);
        var redoDepth = EditorRevisionStore.RedoDepth(fixture.ClipId);
        fixture.SetWriteFailure(true);

        var error = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            if (route == "editor") await fixture.PutAsync(Project(5), revision);
            else if (route == "commands") await fixture.CommandsAsync(revision, null, Range(5));
            else await fixture.HistoryAsync(route, revision);
        });

        Assert.Contains("r50a-save-failure", error.Message, StringComparison.Ordinal);
        Assert.Equal(previous, fixture.ProjectJson);
        Assert.Equal(revision, fixture.CurrentRevision);
        Assert.Equal(undoDepth, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(redoDepth, EditorRevisionStore.RedoDepth(fixture.ClipId));
        fixture.SetWriteFailure(false);
        var retry = route == "editor"
            ? await fixture.PutAsync(Project(5), revision)
            : route == "commands"
                ? await fixture.CommandsAsync(revision, null, Range(5))
                : await fixture.HistoryAsync(route, revision);
        Assert.Equal(StatusCodes.Status200OK, retry.Status);
        Assert.Equal(revision + 1, Revision(retry.Body));
        Assert.Equal(revision + 1, fixture.CurrentRevision);
        Assert.Equal(route == "undo" ? 8d : route == "redo" ? 6d : 5d, fixture.Project.End);
    }

    [Fact]
    public void IdleTimeout_FinalizesCheckpointAndReleasesSession_WithoutRewindingRevision()
    {
        var id = "r50a-timeout-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var first = EditorRevisionStore.BeginSession(id, "checkpoint", now);
            var lastWrite = now.AddSeconds(30);
            EditorRevisionStore.EnsureSession(id, first.SessionId, now: lastWrite);
            EditorRevisionStore.RecordWrite(id, "checkpoint", lastWrite);
            Assert.True(EditorRevisionStore.TryAdvance(id, 0, out _));
            var busy = Assert.Throws<EditorRevisionConflictException>(() =>
                EditorRevisionStore.BeginSession(id, "next", lastWrite.AddSeconds(EditorRevisionStore.SessionIdleTimeoutSeconds - 1)));
            Assert.Equal("session-busy", busy.Code);
            var next = EditorRevisionStore.BeginSession(id, "next", lastWrite.AddSeconds(EditorRevisionStore.SessionIdleTimeoutSeconds));
            Assert.NotEqual(first.SessionId, next.SessionId);
            Assert.Equal(1, next.CheckpointRevision);
            Assert.Equal(1, EditorRevisionStore.SnapshotDepth(id));
            Assert.Equal("checkpoint", EditorRevisionStore.PopSnapshot(id));
            Assert.Equal(1, EditorRevisionStore.Current(id));
            EditorRevisionStore.EndSession(id, next.SessionId, lastWrite.AddSeconds(EditorRevisionStore.SessionIdleTimeoutSeconds));
            Assert.Equal(0, EditorRevisionStore.SnapshotDepth(id));
        }
        finally { EditorRevisionStore.Forget(id); }
    }

    private static EditorProject Project(double end) => new() { Start = 0, End = end };
    private static EditorCommand Range(double end) => new("setRange", new JsonObject { ["start"] = 0, ["end"] = end });
    private static int Revision(JsonElement body) => body.GetProperty("revision").GetInt32();
    private static string? Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-r50a-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;
        private readonly string _databasePath;
        public string ClipId { get; } = "r50a-" + Guid.NewGuid().ToString("N");
        public StudioStore Studio { get; }
        public int CurrentRevision => EditorRevisionStore.Current(Studio, ClipId);
        public string ProjectJson => Studio.Project(ClipId)!;
        public EditorProject Project => JsonSerializer.Deserialize<EditorProject>(ProjectJson)!;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            _databasePath = Path.Combine(_root, "test.db");
            var store = new Store(_databasePath);
            store.Migrate();
            Studio = new StudioStore(_databasePath);
            Studio.Migrate();
            store.SaveClip(new Clip(ClipId, "event-" + ClipId, Path.Combine(_root, "source.mp4"), null, DateTimeOffset.UtcNow));
            Studio.SaveProject(ClipId, JsonSerializer.Serialize(EditorCommandSessionTests.Project(8)));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            // Saved projects avoid media probing. Any unexpected media/AI access must fail, not be mocked away.
            StudioEndpoints.Map(_app, store, Studio, null!, null!, null!, null!, null!, null!, null!, new LiveSettings { StorageRoot = _root });
        }

        public Task<(int Status, JsonElement Body)> CommandsAsync(int? revision, string? sessionId, params EditorCommand[] commands)
            => RequestAsync("commands", new EditorCommandBatch(revision, commands.ToList(), sessionId));

        public Task<(int Status, JsonElement Body)> PutAsync(EditorProject project, int? revision, string? sessionId = null)
            => RequestAsync("editor", project, revision, sessionId, method: "PUT");

        public Task<(int Status, JsonElement Body)> HistoryAsync(string route, int? revision, string? sessionId = null)
            => RequestAsync(route, baseRevision: revision, sessionId: sessionId);

        public Task<(int Status, JsonElement Body)> EndAsync(string sessionId)
            => RequestAsync("sessions/{sid}/end", sid: sessionId);

        public async Task<(int Status, JsonElement Body)> RequestAsync(string route, object? payload = null,
            int? baseRevision = null, string? sessionId = null, string? sid = null, string method = "POST")
        {
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources
                .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == "/api/studio/clips/{id}/" + route
                    && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = method;
            context.Request.RouteValues["id"] = ClipId;
            if (sid is not null) context.Request.RouteValues["sid"] = sid;
            if (baseRevision is { } expected) context.Request.Headers["X-Base-Revision"] = expected.ToString(CultureInfo.InvariantCulture);
            if (sessionId is not null) context.Request.Headers["X-Session-Id"] = sessionId;
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
            return (context.Response.StatusCode, json.RootElement.Clone());
        }

        public void SetWriteFailure(bool fail)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = fail
                ? "CREATE TRIGGER fail_editor_update BEFORE UPDATE ON editor_projects BEGIN SELECT RAISE(ABORT, 'r50a-save-failure'); END;"
                : "DROP TRIGGER fail_editor_update;";
            command.ExecuteNonQuery();
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
