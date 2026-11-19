using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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
using Xunit;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class StudioPutMissingMediaTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Route = "/api/studio/clips/{id}/editor";

    [Theory]
    [InlineData("Timeline")]
    [InlineData("NestedSequences")]
    [InlineData("SilenceTrimBackup")]
    [InlineData("SourceTranscripts")]
    [InlineData("CaptionOverrides")]
    public async Task Put_UnregisteredReference_Returns400MissingMediaWithoutWrites(string location)
    {
        await using var fixture = new Fixture(output);
        var missing = "Missing-" + location;
        var project = Project(fixture.Primary.Id);
        switch (location)
        {
            case "Timeline":
                project = project with { Timeline = [new("missing-root", missing, 0, 2)] };
                break;
            case "NestedSequences":
                project = project with
                {
                    Timeline = [new("sequence-use", "", 0, 2, NestedSequenceId: "Sequence-A")],
                    NestedSequences = new() { ["Sequence-A"] = new("Sequence-A", "nested", [new("child", missing, 0, 2)]) }
                };
                break;
            case "SilenceTrimBackup":
                project = project with { SilenceTrimBackup = [new("backup", missing, 0, 2)], SilenceTrimmed = true };
                break;
            case "SourceTranscripts":
                project = project with { SourceTranscripts = new() { [missing] = [] } };
                break;
            case "CaptionOverrides":
                project = project with { CaptionOverrides = new() { [missing] = [] } };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(location));
        }
        var body = JsonSerializer.Serialize(project, Web);
        ValidatePayload(body);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissingWithoutWrites(fixture, before, reply, missing);
    }

    [Theory]
    [InlineData("camelCase")]
    [InlineData("PascalCase")]
    [InlineData("mixedCase")]
    public async Task Put_AcceptedPropertyCasing_RejectsEveryKnownReferenceAndDeduplicatesOrdinally(string casing)
    {
        await using var fixture = new Fixture(output);
        var ids = new[] { "Missing-Root", "Missing-Nested", "Missing-Backup", "Missing-Transcript", "Missing-Caption" };
        var project = new EditorProject
        {
            End = 2,
            Timeline = [new("root", ids[0], 0, 2), new("root-repeat", ids[0], 0, 2)],
            NestedSequences = new()
            {
                ["Sequence-A"] = new("Sequence-A", "nested", [new("nested", ids[1], 0, 2), new("nested-repeat", ids[0], 0, 2)])
            },
            SilenceTrimBackup = [new("backup", ids[2], 0, 2)],
            SourceTranscripts = new() { [ids[3]] = [], [ids[0]] = [] },
            CaptionOverrides = new() { [ids[4]] = [], [ids[2]] = [] }
        };
        var body = RenameProperties(JsonSerializer.SerializeToNode(project)!, casing).ToJsonString();
        var decoded = ValidatePayload(body);
        Assert.Equal(ids[0], decoded.Timeline![0].ClipId);
        Assert.True(decoded.SourceTranscripts!.ContainsKey(ids[3]));
        Assert.True(decoded.CaptionOverrides!.ContainsKey(ids[4]));
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissingWithoutWrites(fixture, before, reply, ids);
    }

    [Fact]
    public async Task Put_MediaIdsAreOrdinal_NotCaseFoldedTrimmedOrRepeated()
    {
        await using var fixture = new Fixture(output);
        var offline = fixture.Register("Case-Sensitive-Media");
        var differentCase = offline.Id.ToLowerInvariant();
        var padded = " " + offline.Id + " ";
        var project = Project(fixture.Primary.Id) with
        {
            Timeline = [new("known", offline.Id, 0, 2), new("case", differentCase, 0, 2), new("padded", padded, 0, 2), new("repeat", differentCase, 0, 2)],
            SourceTranscripts = new() { [differentCase] = [] },
            CaptionOverrides = new() { [padded] = [] }
        };
        var body = JsonSerializer.Serialize(project, Web);
        ValidatePayload(body);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissingWithoutWrites(fixture, before, reply, differentCase, padded);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("item")]
    [InlineData("nestedTimeline")]
    public async Task Put_LastCaseAliasWithMissingMedia_Returns400WithoutWrites(string alias)
    {
        await using var fixture = new Fixture(output);
        var missing = "Missing-Last-Alias";
        var body = AliasPayload(fixture.Primary.Id, missing, alias);
        var decoded = ValidatePayload(body);
        var effective = alias == "nestedTimeline"
            ? Assert.Single(decoded.NestedSequences!["Sequence-A"].Timeline).ClipId
            : Assert.Single(decoded.Timeline!).ClipId;
        Assert.Equal(missing, effective);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissingWithoutWrites(fixture, before, reply, missing);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("item")]
    [InlineData("nestedTimeline")]
    public async Task Put_LastCaseAliasWithRegisteredMedia_SavesIgnoringShadowedMissingAlias(string alias)
    {
        await using var fixture = new Fixture(output);
        var body = AliasPayload("Missing-Shadowed-Alias", fixture.Primary.Id, alias);
        ValidatePayload(body);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertSaved(fixture, before, reply, body);
    }

    [Fact]
    public async Task Put_RegisteredButOfflineReferencesInEveryKnownLocation_SaveNormally()
    {
        await using var fixture = new Fixture(output);
        var root = fixture.Register("Offline-Root");
        var nested = fixture.Register("Offline-Nested");
        var backup = fixture.Register("Offline-Backup");
        var transcript = fixture.Register("Offline-Transcript");
        var caption = fixture.Register("Offline-Caption");
        foreach (var clip in new[] { root, nested, backup, transcript, caption })
        {
            Assert.Contains(fixture.Store.Clips(), registered => registered.Id == clip.Id);
            Assert.False(File.Exists(clip.Path), "The save contract checks the registry, not file availability: " + clip.Path);
        }
        var project = new EditorProject
        {
            End = 2,
            Timeline = [new("root", root.Id, 0, 2)],
            NestedSequences = new() { ["Sequence-A"] = new("Sequence-A", "nested", [new("child", nested.Id, 0, 2)]) },
            SilenceTrimBackup = [new("backup", backup.Id, 0, 2)],
            SourceTranscripts = new() { [transcript.Id] = [] },
            CaptionOverrides = new() { [caption.Id] = [] }
        };
        var body = JsonSerializer.Serialize(project, Web);
        ValidatePayload(body);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertSaved(fixture, before, reply, body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Put_NoExplicitMediaIds_ImplicitPrimaryOrSyntheticGraphic_RemainsValid(bool graphic)
    {
        await using var fixture = new Fixture(output);
        var project = new EditorProject { End = 2 };
        if (graphic)
            project = project with
            {
                Timeline = [new TimelineItem("synthetic-title", "", 0, 2, Kind: "graphic", Track: 3, TitleText: "Title") { GraphicKind = "title" }],
                SourceTranscripts = new(),
                CaptionOverrides = new()
            };
        var body = JsonSerializer.Serialize(project, Web);
        ValidatePayload(body);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertSaved(fixture, before, reply, body);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("epoch")]
    public async Task Put_MissingMediaDoesNotOverrideStaleCas_Returns409WithoutWrites(string stale)
    {
        await using var fixture = new Fixture(output);
        var body = JsonSerializer.Serialize(Project("Missing-CAS-Media"), Web);
        ValidatePayload(body);
        var before = fixture.Capture();
        Assert.True(before.Revision > 0);

        var reply = await fixture.PutAsync(body,
            revision: stale == "revision" ? before.Revision - 1 : before.Revision,
            epoch: stale == "epoch" ? "obsolete-epoch" : before.Epoch);

        Assert.Multiple(
            () => Assert.Equal(StatusCodes.Status409Conflict, reply.Status),
            () => Assert.Equal(stale == "epoch" ? "stale_epoch" : "revision_conflict", Code(reply.Body)),
            () => Assert.Equal(stale == "epoch" ? "stale-epoch" : "stale-revision", NestedCode(reply.Body)),
            () => Assert.Equal(before.Revision, Revision(reply.Body)),
            () => Assert.Equal(before.Epoch, reply.Epoch),
            () => AssertUnchanged(before, fixture.Capture()));
        if (stale == "epoch") Assert.Equal(before.Epoch, reply.Body.GetProperty("epoch").GetString());
    }

    private static EditorProject Project(string clipId) => new() { End = 2, Timeline = [new("item", clipId, 0, 2)] };

    private static EditorProject ValidatePayload(string body)
    {
        var decoded = EditorProjectCodec.Deserialize(body);
        EditorRenderService.Validate(decoded);
        return decoded;
    }

    private static string AliasPayload(string earlier, string later, string alias)
    {
        var first = new JsonObject { ["Id"] = "item-first", ["ClipId"] = earlier, ["Start"] = 0, ["End"] = 2 };
        var last = new JsonObject { ["id"] = "item-last", ["clipId"] = later, ["start"] = 0, ["end"] = 2 };
        var project = new JsonObject { ["end"] = 2 };
        switch (alias)
        {
            case "project":
                project["Timeline"] = new JsonArray(first);
                project["timeline"] = new JsonArray(last);
                break;
            case "item":
                first["clipId"] = later;
                project["timeline"] = new JsonArray(first);
                break;
            case "nestedTimeline":
                project["timeline"] = new JsonArray(new JsonObject
                {
                    ["id"] = "sequence-use", ["clipId"] = "", ["nestedSequenceId"] = "Sequence-A", ["start"] = 0, ["end"] = 2
                });
                project["nestedSequences"] = new JsonObject
                {
                    ["Sequence-A"] = new JsonObject
                    {
                        ["id"] = "Sequence-A", ["name"] = "nested",
                        ["Timeline"] = new JsonArray(first), ["timeline"] = new JsonArray(last)
                    }
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(alias));
        }
        return project.ToJsonString();
    }

    // Dictionary keys are media/sequence IDs and must not be renamed with JSON property names.
    private static JsonNode RenameProperties(JsonNode node, string casing, bool dictionary = false)
    {
        if (node is JsonArray array)
            return new JsonArray(array.Select(item => item is null ? null : RenameProperties(item, casing)).ToArray());
        if (node is not JsonObject obj) return node.DeepClone();
        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            var name = dictionary ? key : casing switch
            {
                "PascalCase" => key,
                "camelCase" => char.ToLowerInvariant(key[0]) + key[1..],
                "mixedCase" => string.Concat(key.Select((letter, index) => index % 2 == 0 ? char.ToLowerInvariant(letter) : char.ToUpperInvariant(letter))),
                _ => throw new ArgumentOutOfRangeException(nameof(casing))
            };
            var childDictionary = !dictionary && key is "NestedSequences" or "SourceTranscripts" or "CaptionOverrides";
            result[name] = value is null ? null : RenameProperties(value, casing, childDictionary);
        }
        return result;
    }

    private static string? Code(JsonElement body) => body.TryGetProperty("code", out var code) ? code.GetString() : null;
    private static string? NestedCode(JsonElement body) => body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("code", out var code) ? code.GetString() : null;
    private static long? Revision(JsonElement body) => body.TryGetProperty("revision", out var revision) && revision.TryGetInt64(out var value) ? value : null;
    private static string[] Missing(JsonElement body) => body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.Array
        ? missing.EnumerateArray().Select(id => id.GetString()!).ToArray() : [];

    private static void AssertMissingWithoutWrites(Fixture fixture, Snapshot before, Reply reply, params string[] missing)
    {
        var actual = Missing(reply.Body);
        Assert.Multiple(
            () => Assert.Equal(StatusCodes.Status400BadRequest, reply.Status),
            () => Assert.Equal("missing_media", Code(reply.Body)),
            () => Assert.Equal("missing_media", NestedCode(reply.Body)),
            () => Assert.Equal(before.Revision, Revision(reply.Body)),
            () => Assert.Equal(missing.OrderBy(id => id, StringComparer.Ordinal), actual.OrderBy(id => id, StringComparer.Ordinal)),
            () => Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count()),
            () => Assert.Equal(before.Epoch, reply.Epoch),
            () => AssertUnchanged(before, fixture.Capture()));
    }

    private static void AssertUnchanged(Snapshot before, Snapshot after)
    {
        Assert.Multiple(
            () => Assert.Equal(before.Project, after.Project),
            () => Assert.Equal(before.Revision, after.Revision),
            () => Assert.Equal(before.VolatileRevision, after.VolatileRevision),
            () => Assert.Equal(before.Epoch, after.Epoch),
            () => Assert.Equal(before.Backups, after.Backups),
            () => Assert.Equal(before.Undo, after.Undo),
            () => Assert.Equal(before.Redo, after.Redo));
    }

    private static void AssertSaved(Fixture fixture, Snapshot before, Reply reply, string submitted)
    {
        Assert.Equal(StatusCodes.Status200OK, reply.Status);
        Assert.True(reply.Body.GetProperty("saved").GetBoolean());
        Assert.Equal(before.Revision + 1, Revision(reply.Body));
        Assert.Equal(before.Epoch, reply.Epoch);
        var after = fixture.Capture();
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(before.VolatileRevision + 1, after.VolatileRevision);
        Assert.Equal(before.Epoch, after.Epoch);
        var expected = EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(EditorProjectCodec.Deserialize(submitted)));
        Assert.Equal(expected, after.Project!.Json);
        Assert.Equal(after.Project, new StudioStore(fixture.Store.DatabasePath).GetProjectRecord(fixture.Primary.Id));
        Assert.Equal(before.Undo + 1, after.Undo);
        Assert.Equal(0, after.Redo);
        Assert.Equal(before.BackupCount + 1, after.BackupCount);
        Assert.Contains(fixture.Studio.ProjectRevisions(fixture.Primary.Id), backup => fixture.Studio.ProjectRevision(fixture.Primary.Id, backup.FileName) == before.Project!.Json);
    }

    private sealed record Reply(int Status, JsonElement Body, string Epoch);
    private sealed record Snapshot(StoredEditorProject? Project, long Revision, int VolatileRevision, string Epoch, string Backups, int BackupCount, int Undo, int Redo);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly WebApplication _app;
        private readonly string _root;
        public Store Store { get; }
        public StudioStore Studio { get; }
        public Clip Primary { get; }

        public Fixture(ITestOutputHelper output, [CallerFilePath] string sourceFile = "")
        {
            _output = output;
            var repository = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
            _root = Path.Combine(repository, "docs", "merge", "_work", "r82", "PUT-MISSING-MEDIA-TESTS", "fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _output.WriteLine("New isolated SQLite fixture (retained): " + _root);
            Store = new Store(Path.Combine(_root, "test.db"));
            Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath);
            Studio.Migrate();
            Primary = Register("Primary-" + Guid.NewGuid().ToString("N"));
            Studio.SaveProject(Primary.Id, JsonSerializer.Serialize(new EditorProject { End = 8 }));
            Studio.SaveProject(Primary.Id, JsonSerializer.Serialize(new EditorProject { End = 7 }));
            EditorRevisionStore.Current(Studio, Primary.Id);
            // Seed both branches so a rejected PUT cannot silently consume redo or append undo.
            EditorRevisionStore.PushSnapshot(Primary.Id, JsonSerializer.Serialize(new EditorProject { End = 9 }));
            EditorRevisionStore.PushRedo(Primary.Id, JsonSerializer.Serialize(new EditorProject { End = 6 }));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            // PUT uses the real stores only. Unexpected media/AI execution must fail, not be mocked.
            StudioEndpoints.Map(_app, Store, Studio, null!, null!, null!, null!, null!, null!, null!, new LiveSettings { StorageRoot = _root });
        }

        public Clip Register(string id)
        {
            var clip = new Clip(id, "event-" + id, Path.Combine(_root, id + ".mp4"), null, DateTimeOffset.UtcNow);
            Store.SaveClip(clip);
            return clip;
        }

        public Snapshot Capture()
        {
            var backups = Studio.ProjectRevisions(Primary.Id);
            var contents = backups.Select(backup => new { Metadata = backup, Json = Studio.ProjectRevision(Primary.Id, backup.FileName) }).ToArray();
            return new(Studio.GetProjectRecord(Primary.Id), Studio.GetProjectRevision(Primary.Id), EditorRevisionStore.Current(Studio, Primary.Id),
                EditorRevisionStore.EpochFor(Studio), JsonSerializer.Serialize(contents), backups.Count,
                EditorRevisionStore.SnapshotDepth(Primary.Id), EditorRevisionStore.RedoDepth(Primary.Id));
        }

        public async Task<Reply> PutAsync(string body, long? revision = null, string? epoch = null)
        {
            var before = Capture();
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                candidate => candidate.RoutePattern.RawText == Route && candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = "PUT";
            context.Request.Path = "/api/studio/clips/" + Primary.Id + "/editor";
            context.Request.RouteValues["id"] = Primary.Id;
            context.Request.Headers["X-Base-Revision"] = (revision ?? before.Revision).ToString(CultureInfo.InvariantCulture);
            context.Request.Headers["X-Editor-Epoch"] = epoch ?? before.Epoch;
            var bytes = Encoding.UTF8.GetBytes(body);
            await using var input = new MemoryStream(bytes);
            context.Request.Body = input;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
            await using var response = new MemoryStream();
            context.Response.Body = response;
            _output.WriteLine("PUT " + Route + " X-Base-Revision=" + context.Request.Headers["X-Base-Revision"] + " X-Editor-Epoch=" + context.Request.Headers["X-Editor-Epoch"]);
            _output.WriteLine("SUBMITTED " + body);
            await endpoint.RequestDelegate!(context);
            var raw = Encoding.UTF8.GetString(response.ToArray());
            _output.WriteLine("RESPONSE HTTP " + context.Response.StatusCode + " " + raw);
            _output.WriteLine("BEFORE " + JsonSerializer.Serialize(before));
            _output.WriteLine("AFTER " + JsonSerializer.Serialize(Capture()));
            using var json = JsonDocument.Parse(raw);
            return new(context.Response.StatusCode, json.RootElement.Clone(), context.Response.Headers["X-Editor-Epoch"].ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            EditorRevisionStore.Forget(Primary.Id);
            SqliteConnection.ClearAllPools();
            // Evidence lives only below PUT-MISSING-MEDIA-TESTS; leave the new DB/backups for inspection.
        }
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
