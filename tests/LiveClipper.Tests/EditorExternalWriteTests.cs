using System.Collections.Concurrent;
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

/// <summary>
/// R51-A2：带外写入（AutoEdit、配乐 ducking）经 EditorRevisionStore.SaveExternal 落盘，
/// 与 PUT / commands / undo / redo 共用同一把 clip 锁、同一条 revision 序列和同一份 undo/redo 历史。
/// </summary>
public sealed class EditorExternalWriteTests
{
    [Fact]
    public async Task ExternalWrite_BumpsRevision_AndCommandUndoRestoresPreExternalState()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        var command = await fixture.CommandsAsync(initialRevision, null, Range(6));
        Assert.Equal(StatusCodes.Status200OK, command.Status);
        Assert.Equal(initialRevision + 1, Revision(command.Body));
        var beforeExternal = Normalize(fixture.ProjectJson);

        var external = EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "auto-edit", stored => WithEnd(stored, 7));

        Assert.True(external.Saved);
        Assert.Equal(initialRevision + 2, external.Revision);
        Assert.Equal("auto-edit", external.Label);
        Assert.Null(external.EndedSessionId);
        Assert.Equal(initialRevision + 2, fixture.CurrentRevision);
        Assert.Equal(2, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        Assert.Equal(7, fixture.Project.End);

        var stale = await fixture.PutAsync(Project(5), initialRevision + 1);
        Assert.Equal(StatusCodes.Status409Conflict, stale.Status);
        Assert.Equal("stale-revision", Code(stale.Body));
        Assert.Equal(initialRevision + 2, Revision(stale.Body));
        Assert.Equal(7, fixture.Project.End);

        var undo = await fixture.HistoryAsync("undo", initialRevision + 2);
        Assert.Equal(StatusCodes.Status200OK, undo.Status);
        Assert.Equal(initialRevision + 3, Revision(undo.Body));
        Assert.Equal(beforeExternal, Normalize(fixture.ProjectJson));
        Assert.Equal(6, fixture.Project.End);

        undo = await fixture.HistoryAsync("undo", initialRevision + 3);
        Assert.Equal(StatusCodes.Status200OK, undo.Status);
        Assert.Equal(initialRevision + 4, Revision(undo.Body));
        Assert.Equal(8, fixture.Project.End);

        var redo = await fixture.HistoryAsync("redo", initialRevision + 4);
        Assert.Equal(StatusCodes.Status200OK, redo.Status);
        Assert.Equal(initialRevision + 5, Revision(redo.Body));
        Assert.Equal(6, fixture.Project.End);

        redo = await fixture.HistoryAsync("redo", initialRevision + 5);
        Assert.Equal(StatusCodes.Status200OK, redo.Status);
        Assert.Equal(initialRevision + 6, Revision(redo.Body));
        Assert.Equal(7, fixture.Project.End);
    }

    [Fact]
    public async Task ExternalWrite_ClearsRedo_LikeAnyNewWrite()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision, null, Range(6))).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.HistoryAsync("undo", initialRevision + 1)).Status);
        Assert.Equal(1, EditorRevisionStore.RedoDepth(fixture.ClipId));

        var external = EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "music-ducking", stored => WithEnd(stored, 7));

        Assert.True(external.Saved);
        Assert.Equal(initialRevision + 3, external.Revision);
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        var redo = await fixture.HistoryAsync("redo", initialRevision + 3);
        Assert.Equal(StatusCodes.Status409Conflict, redo.Status);
        Assert.Equal("nothing-to-redo", Code(redo.Body));
        Assert.Equal(7, fixture.Project.End);

        var undo = await fixture.HistoryAsync("undo", initialRevision + 3);
        Assert.Equal(StatusCodes.Status200OK, undo.Status);
        Assert.Equal(initialRevision + 4, Revision(undo.Body));
        Assert.Equal(8, fixture.Project.End);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoOpExternalWrite_DoesNotBumpRevisionTouchHistoryOrEndSession(bool returnNull)
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision, null, Range(6))).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision + 1, null, Range(5))).Status);
        Assert.Equal(StatusCodes.Status200OK, (await fixture.HistoryAsync("undo", initialRevision + 2)).Status);
        var begin = await fixture.RequestAsync("sessions");
        Assert.Equal(StatusCodes.Status200OK, begin.Status);
        var sessionId = begin.Body.GetProperty("sessionId").GetString()!;
        var stored = fixture.ProjectJson;
        string? seen = null;

        var external = EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "auto-edit", latest =>
        {
            seen = latest;
            return returnNull ? null : latest;
        });

        Assert.False(external.Saved);
        Assert.Equal(initialRevision + 3, external.Revision);
        Assert.Equal("auto-edit", external.Label);
        Assert.Null(external.EndedSessionId);
        Assert.Equal(stored, seen);
        Assert.Equal(stored, fixture.ProjectJson);
        Assert.Equal(initialRevision + 3, fixture.CurrentRevision);
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(1, EditorRevisionStore.RedoDepth(fixture.ClipId));

        var ended = await fixture.EndAsync(sessionId);
        Assert.Equal(StatusCodes.Status200OK, ended.Status);
        Assert.Equal(initialRevision + 3, ended.Body.GetProperty("checkpointRevision").GetInt32());
        Assert.Equal(initialRevision + 3, Revision(ended.Body));
    }

    [Fact]
    public async Task ExternalWrite_WithoutStoredProject_CreatesItWithoutUndoSnapshot()
    {
        await using var fixture = new Fixture();
        var clipId = "r51a2-new-" + Guid.NewGuid().ToString("N");
        try
        {
            string? seen = "unset";
            var external = EditorRevisionStore.SaveExternal(fixture.Studio, clipId, "auto-edit", stored =>
            {
                seen = stored;
                return JsonSerializer.Serialize(Project(5));
            });

            Assert.Null(seen);
            Assert.True(external.Saved);
            Assert.Equal(1, external.Revision);
            Assert.Equal(0, EditorRevisionStore.SnapshotDepth(clipId));
            Assert.Equal(5, JsonSerializer.Deserialize<EditorProject>(fixture.Studio.Project(clipId)!)!.End);
        }
        finally
        {
            EditorRevisionStore.Forget(clipId);
        }
    }

    [Fact]
    public async Task ConcurrentExternalAndCommandWrites_LoseNoUpdate_AndGetDistinctMonotonicRevisions()
    {
        const int Writers = 6;
        const int Total = Writers * 3;
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var revisions = new ConcurrentBag<int>();
        var commandEnds = new ConcurrentDictionary<int, double>();
        var expectedMarkers = new List<double>();
        var writers = new List<Task>();
        for (var index = 0; index < Writers; index++)
        {
            var externalMarker = 1.0 + index * 0.5;
            var putMarker = 4.0 + index * 0.5;
            var commandEnd = 7.0 + index * 0.125;
            expectedMarkers.Add(externalMarker);
            expectedMarkers.Add(putMarker);

            // 带外写入在锁内读最新工程，再追加自己的 marker。
            writers.Add(Task.Run(async () =>
            {
                await start.Task;
                var external = EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "auto-edit", stored => JsonSerializer.Serialize(AddMarker(stored, externalMarker)));
                Assert.True(external.Saved);
                revisions.Add(external.Revision);
            }));

            // PUT 的内容与 revision 必须来自同一次临界区读取；冲突后重读。
            writers.Add(Task.Run(async () =>
            {
                await start.Task;
                for (var attempt = 0; ; attempt++)
                {
                    Assert.True(attempt < 100, "PUT 重试次数过多");
                    var (json, revision) = EditorRevisionStore.Serialized(fixture.Studio, fixture.ClipId, () => (fixture.Studio.Project(fixture.ClipId)!, fixture.CurrentRevision));
                    var put = await fixture.PutAsync(AddMarker(json, putMarker), revision);
                    if (put.Status == StatusCodes.Status409Conflict && Code(put.Body) == "stale-revision") continue;
                    Assert.Equal(StatusCodes.Status200OK, put.Status);
                    revisions.Add(Revision(put.Body));
                    return;
                }
            }));

            // commands 也携带真实 baseRevision，仅对 stale-revision 重读重试。
            writers.Add(Task.Run(async () =>
            {
                await start.Task;
                for (var attempt = 0; ; attempt++)
                {
                    Assert.True(attempt < 100, "commands 重试次数过多");
                    var command = await fixture.CommandsAsync(fixture.CurrentRevision, null, Range(commandEnd));
                    if (command.Status == StatusCodes.Status409Conflict && Code(command.Body) == "stale-revision") continue;
                    Assert.Equal(StatusCodes.Status200OK, command.Status);
                    var revision = Revision(command.Body);
                    revisions.Add(revision);
                    commandEnds[revision] = commandEnd;
                    return;
                }
            }));
        }

        start.SetResult(true);
        await Task.WhenAll(writers);

        Assert.Equal(Enumerable.Range(initialRevision + 1, Total).ToArray(), revisions.OrderBy(value => value).ToArray());
        Assert.Equal(initialRevision + Total, fixture.CurrentRevision);
        Assert.Equal(Total, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        var project = fixture.Project;
        Assert.Equal(expectedMarkers.OrderBy(value => value).ToArray(), (project.Markers ?? Array.Empty<double>()).OrderBy(value => value).ToArray());
        Assert.Equal(commandEnds[commandEnds.Keys.Max()], project.End);
    }

    [Fact]
    public async Task ExternalWrite_FinishesActiveSession_AndKeepsItsCheckpoint()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        var begin = await fixture.RequestAsync("sessions");
        Assert.Equal(StatusCodes.Status200OK, begin.Status);
        var sessionId = begin.Body.GetProperty("sessionId").GetString()!;
        var inSession = await fixture.CommandsAsync(initialRevision, sessionId, Range(6));
        Assert.Equal(StatusCodes.Status200OK, inSession.Status);
        Assert.Equal(initialRevision + 1, Revision(inSession.Body));

        var external = EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "music-ducking", stored => WithEnd(stored, 7));

        Assert.True(external.Saved);
        Assert.Equal(initialRevision + 2, external.Revision);
        Assert.Equal(sessionId, external.EndedSessionId);
        Assert.Equal(2, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(7, fixture.Project.End);

        var late = await fixture.CommandsAsync(initialRevision + 2, sessionId, Range(5));
        Assert.Equal(StatusCodes.Status409Conflict, late.Status);
        Assert.Equal("session-mismatch", Code(late.Body));
        var ended = await fixture.EndAsync(sessionId);
        Assert.Equal(StatusCodes.Status409Conflict, ended.Status);
        Assert.Equal("session-mismatch", Code(ended.Body));
        Assert.Equal(7, fixture.Project.End);
        Assert.Equal(initialRevision + 2, fixture.CurrentRevision);

        var undo = await fixture.HistoryAsync("undo", initialRevision + 2);
        Assert.Equal(StatusCodes.Status200OK, undo.Status);
        Assert.Equal(initialRevision + 3, Revision(undo.Body));
        Assert.Equal(6, fixture.Project.End);

        undo = await fixture.HistoryAsync("undo", initialRevision + 3);
        Assert.Equal(StatusCodes.Status200OK, undo.Status);
        Assert.Equal(initialRevision + 4, Revision(undo.Body));
        Assert.Equal(8, fixture.Project.End);

        var next = await fixture.RequestAsync("sessions");
        Assert.Equal(StatusCodes.Status200OK, next.Status);
        Assert.Equal(initialRevision + 4, next.Body.GetProperty("checkpointRevision").GetInt32());
    }

    [Fact]
    public async Task ExternalWrite_WaitsForSameClipLock_ButDoesNotBlockAnotherClip()
    {
        await using var held = new Fixture();
        await using var other = new Fixture();
        var heldRevision = held.CurrentRevision;
        var otherRevision = other.CurrentRevision;
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Factory.StartNew(() => EditorRevisionStore.Serialized(held.Studio, held.ClipId, () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return 0;
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<EditorExternalWrite>? blocked = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

            var free = await Task.Run(() => EditorRevisionStore.SaveExternal(other.Studio, other.ClipId, "auto-edit", stored => WithEnd(stored, 7))).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(free.Saved);
            Assert.Equal(otherRevision + 1, free.Revision);
            Assert.Equal(7, other.Project.End);

            blocked = Task.Run(() => EditorRevisionStore.SaveExternal(held.Studio, held.ClipId, "auto-edit", stored => WithEnd(stored, 6)));
            await Task.Delay(200);
            Assert.False(blocked.IsCompleted);
            Assert.Equal(8, held.Project.End);
        }
        finally
        {
            release.Set();
            await holder;
        }

        var unblocked = await blocked!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(unblocked.Saved);
        Assert.Equal(heldRevision + 1, unblocked.Revision);
        Assert.Equal(6, held.Project.End);
    }

    [Fact]
    public async Task FailedExternalWrite_LeavesProjectRevisionHistoryAndSessionUnchanged()
    {
        await using var fixture = new Fixture();
        var initialRevision = fixture.CurrentRevision;
        Assert.Equal(StatusCodes.Status200OK, (await fixture.CommandsAsync(initialRevision, null, Range(6))).Status);
        var begin = await fixture.RequestAsync("sessions");
        Assert.Equal(StatusCodes.Status200OK, begin.Status);
        var sessionId = begin.Body.GetProperty("sessionId").GetString()!;
        var stored = fixture.ProjectJson;

        fixture.SetWriteFailure(true);
        var failure = Assert.Throws<SqliteException>(() => EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "auto-edit", latest => WithEnd(latest, 7)));
        Assert.Contains("r51a2-save-failure", failure.Message);
        fixture.SetWriteFailure(false);
        var invalid = Assert.Throws<ArgumentException>(() => EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "auto-edit", _ => throw new ArgumentException("r51a2-invalid")));
        Assert.Equal("r51a2-invalid", invalid.Message);

        Assert.Equal(stored, fixture.ProjectJson);
        Assert.Equal(initialRevision + 1, fixture.CurrentRevision);
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        var inSession = await fixture.CommandsAsync(initialRevision + 1, sessionId, Range(5));
        Assert.Equal(StatusCodes.Status200OK, inSession.Status);
        Assert.Equal(initialRevision + 2, Revision(inSession.Body));
    }

    private static EditorProject Project(double end) => new() { Start = 0, End = end };

    private static EditorCommand Range(double end) => new("setRange", new JsonObject { ["start"] = 0, ["end"] = end });

    private static int Revision(JsonElement body) => body.GetProperty("revision").GetInt32();

    private static string? Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString();

    private static EditorProject Parse(string? json) => JsonSerializer.Deserialize<EditorProject>(json ?? throw new InvalidOperationException("工程不存在"))!;

    private static string Normalize(string json) => JsonSerializer.Serialize(Parse(json));

    private static string WithEnd(string? json, double end) => JsonSerializer.Serialize(Parse(json) with { End = end });

    private static EditorProject AddMarker(string? json, double marker)
    {
        var project = Parse(json);
        return project with { Markers = (project.Markers ?? Array.Empty<double>()).Append(marker).ToArray() };
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-r51a2-" + Guid.NewGuid().ToString("N"));
        private readonly string _databasePath;
        private readonly WebApplication _app;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            _databasePath = Path.Combine(_root, "test.db");
            var store = new Store(_databasePath);
            store.Migrate();
            Studio = new StudioStore(_databasePath);
            Studio.Migrate();
            store.SaveClip(new Clip(ClipId, "event-" + ClipId, Path.Combine(_root, "source.mp4"), null, DateTimeOffset.UtcNow));
            Studio.SaveProject(ClipId, JsonSerializer.Serialize(EditorExternalWriteTests.Project(8)));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            StudioEndpoints.Map(_app, store, Studio, null!, null!, null!, null!, null!, null!, null!, new LiveSettings { StorageRoot = _root });
        }

        public string ClipId { get; } = "r51a2-" + Guid.NewGuid().ToString("N");

        public StudioStore Studio { get; }

        public int CurrentRevision => EditorRevisionStore.Current(Studio, ClipId);

        public string ProjectJson => Studio.Project(ClipId)!;

        public EditorProject Project => JsonSerializer.Deserialize<EditorProject>(ProjectJson)!;

        public Task<(int Status, JsonElement Body)> CommandsAsync(int? revision, string? sessionId, params EditorCommand[] commands) =>
            RequestAsync("commands", new EditorCommandBatch(revision, commands.ToList(), sessionId));

        public Task<(int Status, JsonElement Body)> PutAsync(EditorProject project, int? revision, string? sessionId = null) =>
            RequestAsync("editor", project, revision, sessionId, method: "PUT");

        public Task<(int Status, JsonElement Body)> HistoryAsync(string route, int? revision, string? sessionId = null) =>
            RequestAsync(route, baseRevision: revision, sessionId: sessionId);

        public Task<(int Status, JsonElement Body)> EndAsync(string sessionId) =>
            RequestAsync("sessions/{sid}/end", sid: sessionId);

        public async Task<(int Status, JsonElement Body)> RequestAsync(string route, object? payload = null, int? baseRevision = null, string? sessionId = null, string? sid = null, string method = "POST")
        {
            var endpoint = Assert.Single(
                ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                candidate => candidate.RoutePattern.RawText == "/api/studio/clips/{id}/" + route && candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
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
                ? "CREATE TRIGGER fail_editor_update BEFORE UPDATE ON editor_projects BEGIN SELECT RAISE(ABORT, 'r51a2-save-failure'); END;"
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
