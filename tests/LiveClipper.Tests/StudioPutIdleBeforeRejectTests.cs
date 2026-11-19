using System.Collections;
using System.Globalization;
using System.Reflection;
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
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class StudioPutIdleBeforeRejectTests(ITestOutputHelper output)
{
    private const string Route = "/api/studio/clips/{id}/editor";
    private const string MissingId = "missing-idle-media";
    private static readonly DateTimeOffset ExpiredAt = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FreshAt = DateTimeOffset.MaxValue.AddDays(-1);

    [Fact]
    public async Task Put_Missing_ExpiredDirty_NoSessionHeader_PreservesCheckpointAndSession()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        var before = fixture.Capture();
        Assert.True(before.Session!.HasWrites);
        Assert.NotEqual(before.Session.Checkpoint, before.Undo[^1]);

        AssertMissing(before, await fixture.PutAsync(MissingProject()));

        AssertUnchanged(before, fixture.Capture());
    }

    [Fact]
    public async Task Put_Missing_ExpiredDirty_FullUndo_DoesNotEvictOldest()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true, undoCount: 30);
        var before = fixture.Capture();
        Assert.Equal(30, before.Undo.Length);
        Assert.NotEqual(before.Session!.Checkpoint, before.Undo[^1]);

        AssertMissing(before, await fixture.PutAsync(MissingProject()));

        var after = fixture.Capture();
        Assert.Equal(30, after.Undo.Length);
        Assert.Multiple(
            () => Assert.Equal(before.Undo[0], after.Undo[0]),
            () => AssertUnchanged(before, after));
    }

    [Fact]
    public async Task Put_Missing_ExpiredDirty_DuplicateTop_StillPreservesActiveSession()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true, duplicateTop: true);
        var before = fixture.Capture();
        Assert.Equal(before.Session!.Checkpoint, before.Undo[^1]);

        AssertMissing(before, await fixture.PutAsync(MissingProject()));

        var after = fixture.Capture();
        Assert.Equal(before.Undo, after.Undo);
        AssertUnchanged(before, after);
    }

    [Fact]
    public async Task Put_Missing_ExpiredClean_PreservesSessionWithoutHistoryGrowth()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: false);
        var before = fixture.Capture();
        Assert.False(before.Session!.HasWrites);

        AssertMissing(before, await fixture.PutAsync(MissingProject()));

        var after = fixture.Capture();
        Assert.Equal(before.Undo, after.Undo);
        AssertUnchanged(before, after);
    }

    [Fact]
    public async Task Put_RepeatedMissing_DoesNotConsumeIdleCheckpoint_ThenSuccessFinalizesOnce()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        var before = fixture.Capture();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            AssertMissing(before, await fixture.PutAsync(MissingProject()));
            AssertUnchanged(before, fixture.Capture());
        }

        var reply = await fixture.PutAsync(Project(6));

        AssertExpiredSaved(fixture, before, reply);
    }

    [Fact]
    public async Task Put_ExpiredDirty_OldSessionId_StillRejectsBeforeStaleTypedAndMissing_WithoutMaintenance()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        var before = fixture.Capture();
        var reply = await fixture.PutAsync(MissingProject() with { End = -1 },
            revisionHeader: "0", sessionId: before.Session!.Id);

        AssertError(before, reply, StatusCodes.Status409Conflict, "session_not_found", "session-mismatch");
        AssertUnchanged(before, fixture.Capture());
    }

    [Fact]
    public async Task Put_ExpiredDirty_StaleBase_StillRejectsBeforeTypedAndMissing_WithoutMaintenance()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        var before = fixture.Capture();
        var reply = await fixture.PutAsync(MissingProject() with { End = -1 }, revisionHeader: "0");

        AssertError(before, reply, StatusCodes.Status409Conflict, "revision_conflict", "stale-revision");
        AssertUnchanged(before, fixture.Capture());
    }

    [Fact]
    public async Task Put_ExpiredDirty_TypedInvalid_StillRejectsBeforeMissing_WithoutMaintenance()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        var before = fixture.Capture();
        var invalid = MissingProject() with { MasterVolume = 100 };
        Assert.Throws<ArgumentException>(() => EditorRenderService.Validate(invalid));

        var reply = await fixture.PutAsync(invalid);

        Assert.Equal(StatusCodes.Status400BadRequest, reply.Status);
        Assert.Equal("invalid_project", Code(reply.Body));
        Assert.Equal(JsonValueKind.String, reply.Body.GetProperty("error").ValueKind);
        AssertUnchanged(before, fixture.Capture());
    }

    [Fact]
    public async Task Put_FreshMatchingSession_Missing_DoesNotWriteOrRefreshActivity()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true, at: FreshAt);
        var before = fixture.Capture();

        AssertMissing(before, await fixture.PutAsync(MissingProject(), sessionId: before.Session!.Id));

        AssertUnchanged(before, fixture.Capture());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-session")]
    [InlineData("")]
    public async Task Put_FreshSession_MissingOrWrongId_RejectsBeforeStaleAndTyped(string? sessionId)
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true, at: FreshAt);
        var before = fixture.Capture();
        var reply = await fixture.PutAsync(MissingProject() with { MasterVolume = 100 },
            revisionHeader: "0", sessionId: sessionId);

        AssertError(before, reply, StatusCodes.Status409Conflict, "session_not_found", "session-mismatch");
        AssertUnchanged(before, fixture.Capture());
    }

    [Theory]
    [InlineData("missing-base", 428, "precondition_required", "precondition-required")]
    [InlineData("invalid-base", 400, "invalid_revision", "invalid-revision")]
    [InlineData("old-epoch", 409, "stale_epoch", "stale-epoch")]
    public async Task Put_ExpiredDirty_PreconditionKeepsPriority_WithoutMaintenance(
        string condition, int status, string code, string nestedCode)
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        var before = fixture.Capture();
        var reply = await fixture.PutAsync(MissingProject(),
            revisionHeader: condition == "invalid-base" ? "not-a-number" : null,
            omitRevision: condition == "missing-base",
            epoch: condition == "old-epoch" ? "old-epoch" : null,
            sessionId: before.Session!.Id);

        AssertError(before, reply, status, code, nestedCode);
        AssertUnchanged(before, fixture.Capture());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Put_ExpiredSession_Success_FinalizesOnlyDirtyCheckpointBeforePrevious(bool dirty)
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty);
        var before = fixture.Capture();

        AssertExpiredSaved(fixture, before, await fixture.PutAsync(Project(6)));
    }

    [Fact]
    public async Task Put_ExpiredDirty_SqliteWriteFailure_DoesNotConsumeIdle_ThenRetrySucceeds()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        fixture.SetWriteFailure(true);
        var before = fixture.Capture();

        var error = await Assert.ThrowsAsync<SqliteException>(() => fixture.PutAsync(Project(6)));

        Assert.Contains("b2-3-save-failure", error.Message, StringComparison.Ordinal);
        AssertUnchanged(before, fixture.Capture());
        fixture.SetWriteFailure(false);
        AssertExpiredSaved(fixture, before, await fixture.PutAsync(Project(6)));
    }

    [Fact]
    public async Task Capture_DoesNotBindRefreshOrExpire_AndSameStoreBindIsNoOp()
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        var before = fixture.Capture();
        AssertUnchanged(before, fixture.Capture());
        EditorRevisionStore.Serialized(fixture.Studio, fixture.ClipId, () => true);
        AssertUnchanged(before, fixture.Capture());
    }

    [Theory]
    [InlineData(299, true)]
    [InlineData(300, false)]
    public async Task PutAdmission_IdleBoundary_IsReadOnlyAndKeepsIdRules(int idleSeconds, bool active)
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        var before = fixture.Capture();
        var admittedAt = before.Session!.LastActivity.AddSeconds(idleSeconds);

        EditorRevisionStore.Serialized(fixture.Studio, fixture.ClipId, () =>
        {
            EditorRevisionStore.EnsureSessionForPut(fixture.ClipId, active ? before.Session.Id : null, admittedAt);
            AssertUnchanged(before, fixture.Capture());
            var mismatch = Assert.Throws<EditorRevisionConflictException>(() =>
                EditorRevisionStore.EnsureSessionForPut(fixture.ClipId, active ? null : before.Session.Id, admittedAt));
            Assert.Equal("session-mismatch", mismatch.Code);
            AssertUnchanged(before, fixture.Capture());
            return true;
        });
    }

    [Theory]
    [InlineData(299, 301, true)]
    [InlineData(300, 301, false)]
    public async Task PutCompletion_UsesAdmissionTime_AcrossSlowSaveBoundary(
        int admissionSeconds, int completionSeconds, bool staysGrouped)
    {
        await using var fixture = new Fixture(output);
        fixture.SeedSession(dirty: true);
        var before = fixture.Capture();
        var admittedAt = before.Session!.LastActivity.AddSeconds(admissionSeconds);
        var completedAt = before.Session.LastActivity.AddSeconds(completionSeconds);

        EditorRevisionStore.Serialized(fixture.Studio, fixture.ClipId, () =>
        {
            EditorRevisionStore.EnsureSessionForPut(fixture.ClipId, staysGrouped ? before.Session.Id : null, admittedAt);
            AssertUnchanged(before, fixture.Capture());
            var committed = EditorRevisionStore.Commit(fixture.Studio, fixture.ClipId,
                JsonSerializer.Serialize(Project(6)), (int)before.Project.Revision);
            Assert.True(committed.Ok);
            // Existing time arguments model a save crossing 300s without sleeping or replacing SQLite.
            EditorRevisionStore.CompletePutSession(fixture.ClipId, admittedAt);
            var completed = fixture.Capture();
            Assert.Equal(before.Redo, completed.Redo);
            if (staysGrouped)
            {
                Assert.Equal(before.Session, completed.Session);
                Assert.Equal(before.Undo, completed.Undo);
            }
            else
            {
                Assert.Null(completed.Session);
                Assert.Equal(before.Undo.Append(before.Session.Checkpoint), completed.Undo);
            }
            EditorRevisionStore.RecordWrite(fixture.ClipId, before.Project.Json, completedAt);
            Assert.True(EditorRevisionStore.TryAdvance(fixture.ClipId, (int)before.Project.Revision, out var revision));
            Assert.Equal(before.Project.Revision + 1, revision);
            return true;
        });

        var after = fixture.Capture();
        Assert.Equal(before.Project.Revision + 1, after.Project.Revision);
        Assert.Equal(EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(Project(6))), after.Project.Json);
        Assert.Equal(after.Project.Revision, after.CachedRevision);
        Assert.Null(after.CommittedRevision);
        Assert.Equal(before.BackupCount + 1, after.BackupCount);
        Assert.Equal(before.Epoch, after.Epoch);
        Assert.Empty(after.Redo);
        if (staysGrouped)
        {
            Assert.Equal(before.Undo, after.Undo);
            Assert.Equal(before.Session with { LastActivity = completedAt }, after.Session);
            EditorRevisionStore.EndSession(fixture.ClipId, before.Session.Id, completedAt);
            Assert.Equal(before.Undo.Append(before.Session.Checkpoint), fixture.Capture().Undo);
            Assert.Null(fixture.Capture().Session);
        }
        else
        {
            Assert.Null(after.Session);
            Assert.Equal(before.Undo.Append(before.Session.Checkpoint).Append(before.Project.Json), after.Undo);
        }
    }

    private static EditorProject Project(double end) => new() { End = end };
    private static EditorProject MissingProject() => new()
    {
        End = 2, Timeline = [new("missing-root", MissingId, 0, 2)]
    };
    private static string? Code(JsonElement body) => body.GetProperty("code").GetString();
    private static string? NestedCode(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString();

    private static void AssertMissing(Snapshot before, Reply reply)
    {
        AssertError(before, reply, StatusCodes.Status400BadRequest, "missing_media", "missing_media");
        Assert.Equal(new[] { MissingId }, reply.Body.GetProperty("error").GetProperty("missing")
            .EnumerateArray().Select(value => value.GetString()).ToArray());
    }

    private static void AssertError(Snapshot before, Reply reply, int status, string code, string nestedCode)
    {
        Assert.Multiple(
            () => Assert.Equal(status, reply.Status),
            () => Assert.Equal(code, Code(reply.Body)),
            () => Assert.Equal(nestedCode, NestedCode(reply.Body)),
            () => Assert.Equal(before.Project.Revision, reply.Body.GetProperty("revision").GetInt64()),
            () => Assert.Equal(before.Epoch, reply.Epoch));
    }

    private static void AssertUnchanged(Snapshot before, Snapshot after)
    {
        Assert.Multiple(
            () => Assert.Equal(before.Project, after.Project),
            () => Assert.Equal(before.CachedRevision, after.CachedRevision),
            () => Assert.Equal(before.CommittedRevision, after.CommittedRevision),
            () => Assert.Equal(before.Epoch, after.Epoch),
            () => Assert.Equal(before.Backups, after.Backups),
            () => Assert.Equal(before.Undo, after.Undo),
            () => Assert.Equal(before.Redo, after.Redo),
            () => Assert.Equal(before.Session, after.Session));
    }

    private static void AssertExpiredSaved(Fixture fixture, Snapshot before, Reply reply)
    {
        Assert.Equal(StatusCodes.Status200OK, reply.Status);
        Assert.True(reply.Body.GetProperty("saved").GetBoolean());
        Assert.Equal(before.Project.Revision + 1, reply.Body.GetProperty("revision").GetInt64());
        var after = fixture.Capture();
        var expectedUndo = before.Undo.ToList();
        if (before.Session!.HasWrites) expectedUndo.Add(before.Session.Checkpoint);
        expectedUndo.Add(before.Project.Json);
        Assert.Equal(expectedUndo, after.Undo);
        Assert.Empty(after.Redo);
        Assert.Null(after.Session);
        Assert.Null(after.CommittedRevision);
        Assert.Equal(before.Project.Revision + 1, after.Project.Revision);
        Assert.Equal(after.Project.Revision, after.CachedRevision);
        Assert.Equal(before.Epoch, after.Epoch);
        Assert.Equal(EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(Project(6))), after.Project.Json);
        Assert.Equal(before.BackupCount + 1, after.BackupCount);
        Assert.Contains(fixture.Studio.ProjectRevisions(fixture.ClipId),
            backup => fixture.Studio.ProjectRevision(fixture.ClipId, backup.FileName) == before.Project.Json);
    }

    private sealed record Reply(int Status, JsonElement Body, string Epoch);
    private sealed record SessionSnapshot(string Id, int CheckpointRevision, string Checkpoint, DateTimeOffset LastActivity, bool HasWrites);
    private sealed record Snapshot(StoredEditorProject Project, int CachedRevision, int? CommittedRevision, string Epoch,
        string Backups, int BackupCount, string[] Undo, string[] Redo, SessionSnapshot? Session);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly WebApplication _app;
        private readonly string _root;
        public Store Store { get; }
        public StudioStore Studio { get; }
        public string ClipId { get; } = "b2-3-" + Guid.NewGuid().ToString("N");

        public Fixture(ITestOutputHelper output, [CallerFilePath] string sourceFile = "")
        {
            _output = output;
            var repository = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
            _root = Path.Combine(repository, "docs", "merge", "_work", "r82", "B2-3-IMPL", "fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _output.WriteLine("Retained isolated SQLite fixture: " + _root);
            Store = new Store(Path.Combine(_root, "test.db"));
            Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath);
            Studio.Migrate();
            Store.SaveClip(new Clip(ClipId, "event-" + ClipId, Path.Combine(_root, "source.mp4"), null, ExpiredAt));
            EditorRevisionStore.Serialized(Studio, ClipId, () =>
            {
                var committed = EditorRevisionStore.Commit(Studio, ClipId, JsonSerializer.Serialize(Project(8)), 0);
                Assert.True(committed.Ok);
                EditorRevisionStore.RecordWrite(ClipId, null, ExpiredAt);
                Assert.True(EditorRevisionStore.TryAdvance(ClipId, 0, out var revision));
                Assert.Equal(committed.Revision, revision);
                return true;
            });
            EditorRenderService.Validate(EditorProjectCodec.Deserialize(JsonSerializer.Serialize(MissingProject())));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            StudioEndpoints.Map(_app, Store, Studio, null!, null!, null!, null!, null!, null!, null!, new LiveSettings { StorageRoot = _root });
        }

        public void SeedSession(bool dirty, int undoCount = 1, bool duplicateTop = false, DateTimeOffset? at = null)
        {
            EditorRevisionStore.Serialized(Studio, ClipId, () =>
            {
                var timestamp = at ?? ExpiredAt;
                var checkpoint = Studio.GetProjectRecord(ClipId)!;
                var session = EditorRevisionStore.BeginSession(ClipId, checkpoint.Json, timestamp);
                if (dirty)
                {
                    EditorRevisionStore.EnsureSession(ClipId, session.SessionId, now: timestamp);
                    var committed = EditorRevisionStore.Commit(Studio, ClipId, JsonSerializer.Serialize(Project(7)), (int)checkpoint.Revision);
                    Assert.True(committed.Ok);
                    EditorRevisionStore.RecordWrite(ClipId, checkpoint.Json, timestamp);
                    Assert.True(EditorRevisionStore.TryAdvance(ClipId, (int)checkpoint.Revision, out var revision));
                    Assert.Equal(committed.Revision, revision);
                }
                for (var i = 0; i < undoCount; i++)
                    EditorRevisionStore.PushSnapshot(ClipId, JsonSerializer.Serialize(Project(20 + i)));
                if (duplicateTop) EditorRevisionStore.PushSnapshot(ClipId, checkpoint.Json);
                EditorRevisionStore.PushRedo(ClipId, JsonSerializer.Serialize(Project(5)));
                return true;
            });
        }

        public Snapshot Capture() => EditorRevisionStore.Serialized(ClipId, () =>
        {
            // Read under the existing Gate without Bind, Rev, or any API that performs idle maintenance.
            var entries = (IDictionary)typeof(EditorRevisionStore).GetField("Entries", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var entry = entries[ClipId]!;
            var active = Field(entry, "ActiveSession");
            var session = active is null ? null : new SessionSnapshot(
                (string)Field(active, "Id")!, (int)Field(active, "CheckpointRevision")!, (string)Field(active, "Checkpoint")!,
                (DateTimeOffset)Field(active, "LastActivity")!, (bool)Field(active, "HasWrites")!);
            var backups = Studio.ProjectRevisions(ClipId);
            var contents = backups.Select(backup => new { Metadata = backup, Json = Studio.ProjectRevision(ClipId, backup.FileName) }).ToArray();
            return new Snapshot(Studio.GetProjectRecord(ClipId)!, (int)Field(entry, "Revision")!, (int?)Field(entry, "CommittedRevision"),
                EditorRevisionStore.EpochFor(Studio), JsonSerializer.Serialize(contents), backups.Count,
                ((List<string>)Field(entry, "Undo")!).ToArray(), ((List<string>)Field(entry, "Redo")!).ToArray(), session);
        });

        private static object? Field(object target, string name)
            => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);

        public async Task<Reply> PutAsync(EditorProject project, string? revisionHeader = null,
            bool omitRevision = false, string? epoch = null, string? sessionId = null)
        {
            var before = Capture();
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                candidate => candidate.RoutePattern.RawText == Route && candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = "PUT";
            context.Request.Path = "/api/studio/clips/" + ClipId + "/editor";
            context.Request.RouteValues["id"] = ClipId;
            if (!omitRevision) context.Request.Headers["X-Base-Revision"] = revisionHeader ?? before.Project.Revision.ToString(CultureInfo.InvariantCulture);
            context.Request.Headers["X-Editor-Epoch"] = epoch ?? before.Epoch;
            if (sessionId is not null) context.Request.Headers["X-Session-Id"] = sessionId;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(project);
            await using var input = new MemoryStream(bytes);
            context.Request.Body = input;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
            await using var response = new MemoryStream();
            context.Response.Body = response;
            _output.WriteLine("BEFORE " + JsonSerializer.Serialize(before));
            try { await endpoint.RequestDelegate!(context); }
            finally { _output.WriteLine("AFTER " + JsonSerializer.Serialize(Capture())); }
            var raw = Encoding.UTF8.GetString(response.ToArray());
            _output.WriteLine("RESPONSE HTTP " + context.Response.StatusCode + " " + raw);
            using var json = JsonDocument.Parse(raw);
            return new(context.Response.StatusCode, json.RootElement.Clone(), context.Response.Headers["X-Editor-Epoch"].ToString());
        }

        public void SetWriteFailure(bool fail)
        {
            using var connection = new SqliteConnection($"Data Source={Store.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = fail
                ? "CREATE TRIGGER fail_editor_update BEFORE UPDATE ON editor_projects BEGIN SELECT RAISE(ABORT, 'b2-3-save-failure'); END;"
                : "DROP TRIGGER fail_editor_update;";
            command.ExecuteNonQuery();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            EditorRevisionStore.Forget(ClipId);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
