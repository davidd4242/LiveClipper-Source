using System.Globalization;
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

namespace LiveClipper.Tests;

public sealed class EditorReplicaExtensionTests
{
    private const string Extension = """
        {"version":1,"codecVersion":1,"document":{"sequences":[{"id":"seq-1","track":{"id":"semantic-track"},"future":{"integer":900719925474099312345678901234567890,"decimal":0.1234567890123456789012345678901234567890,"exponent":1.230000e-40,"negativeZero":-0.000,"values":[false,null,"",{},[],"未知/𝄞","\u0061"]}}]},"opaque":{"byClipId":{"clip-1":{"untouched":true}}},"exactTimeMap":{"source":{"seconds":123.0000000000000000000000000000000000001,"fps":{"numerator":30000,"denominator":1001}}},"compatibility":{"requiredVersion":1,"requiredCodecVersion":1,"futureOptional":{"flags":[false,null]}},"futureTop":{"sentinel":"one"}}
        """;
    private static string ExtensionTwo => Extension.Replace("\"sentinel\":\"one\"", "\"sentinel\":\"two\"", StringComparison.Ordinal);
    private static string ExtensionThree => Extension.Replace("\"sentinel\":\"one\"", "\"sentinel\":\"three\"", StringComparison.Ordinal);
    private static string UnsupportedExtension => Extension.Replace("\"requiredVersion\":1", "\"requiredFuture\":true", StringComparison.Ordinal);
    private const string Prefix = "/api/studio/clips/{id}/";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Converter_PreservesRawNumbersAndUnknownFields_AfterSourceDocumentDisposed(bool webOptions)
    {
        EditorProject project;
        using (var source = JsonDocument.Parse(ProjectJson(Extension)))
            project = EditorProjectCodec.Deserialize(source.RootElement);

        var extension = Assert.IsType<ReplicaExtension>(project.ReplicaExtension);
        Assert.Equal(1, extension.Version);
        Assert.Equal(1, extension.CodecVersion);
        Assert.Equal(Extension, extension.Json.GetRawText());
        var options = webOptions ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions();
        var serialized = JsonSerializer.Serialize(project, options);
        using var saved = JsonDocument.Parse(serialized);
        Assert.Equal(Extension, saved.RootElement.GetProperty("replicaExtension").GetRawText());
        AssertExtension(Extension, serialized);
        AssertExtension(Extension, JsonSerializer.Serialize(EditorProjectCodec.Deserialize(serialized), options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public void Stamp_OnlyChangesRootVersion_AndNeverReencodesExtension(int? version)
    {
        var original = ProjectJson(Extension, schemaVersion: version);
        var stamped = EditorProjectCodec.StampSchemaVersion(original);
        Assert.Equal(1, EditorProjectCodec.ReadSchemaVersion(stamped));
        Assert.Equal(1, EditorProjectCodec.CurrentSchemaVersion);
        using var document = JsonDocument.Parse(stamped);
        Assert.Equal(Extension, document.RootElement.GetProperty("replicaExtension").GetRawText());
        Assert.Equal(stamped, EditorProjectCodec.StampSchemaVersion(stamped));
        if (version == 1) Assert.Equal(original, stamped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyProject_WithoutExtension_HasUnchangedSerializedBytes(bool webOptions)
    {
        // Fixed pre-extension serialization, including property order and null/default fields.
        const string legacy = """
            {"Start":0,"End":8,"Timeline":null,"BurnSubtitles":false,"SelectedSegments":null,"VoiceOnly":false,"MusicAssetId":null,"EffectAssetId":null,"VoiceVolume":1,"MusicVolume":0.25,"EffectVolume":0.5,"MasterVolume":1,"EffectAt":0,"SubtitleFont":"Microsoft YaHei","SubtitleSize":22,"SubtitleColor":"#FFFFFF","SubtitlePosition":"bottom","Resolution":720,"Fps":30,"BitrateKbps":0,"SentenceMode":false,"DuckMusic":true,"SubtitleOutline":1.5,"SubtitleMargin":18,"SourceTranscripts":null,"SilenceTrimBackup":null,"SilenceTrimmed":false,"SilenceTrimmedSeconds":0,"MutedTracks":null,"SoloedTracks":null,"HiddenTracks":null,"LockedTracks":null,"Markers":null,"TrackOpacities":null}
            """;
        const string legacyWeb = """
            {"start":0,"end":8,"timeline":null,"burnSubtitles":false,"selectedSegments":null,"voiceOnly":false,"musicAssetId":null,"effectAssetId":null,"voiceVolume":1,"musicVolume":0.25,"effectVolume":0.5,"masterVolume":1,"effectAt":0,"subtitleFont":"Microsoft YaHei","subtitleSize":22,"subtitleColor":"#FFFFFF","subtitlePosition":"bottom","resolution":720,"fps":30,"bitrateKbps":0,"sentenceMode":false,"duckMusic":true,"subtitleOutline":1.5,"subtitleMargin":18,"sourceTranscripts":null,"silenceTrimBackup":null,"silenceTrimmed":false,"silenceTrimmedSeconds":0,"mutedTracks":null,"soloedTracks":null,"hiddenTracks":null,"lockedTracks":null,"markers":null,"trackOpacities":null}
            """;
        var expected = webOptions ? legacyWeb : legacy;
        var options = webOptions ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions();
        var project = EditorProjectCodec.Deserialize(expected);
        Assert.Null(project.ReplicaExtension);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), JsonSerializer.SerializeToUtf8Bytes(project, options));
        var stamped = EditorProjectCodec.StampSchemaVersion(expected);
        Assert.Equal(expected[..^1] + ",\"schemaVersion\":1}", stamped);
        Assert.DoesNotContain("replicaExtension", stamped);
    }

    [Theory]
    [InlineData("requiredVersion", "2", ReplicaExtensionErrors.Unsupported)]
    [InlineData("requiredCodecVersion", "2", ReplicaExtensionErrors.Unsupported)]
    [InlineData("requiredFeatures", "[\"future-engine\"]", ReplicaExtensionErrors.Unsupported)]
    [InlineData("requiredFuture", "false", ReplicaExtensionErrors.Unsupported)]
    [InlineData("RequiredFuture", "null", ReplicaExtensionErrors.Unsupported)]
    [InlineData("requiredVersion", "\"1\"", ReplicaExtensionErrors.Invalid)]
    [InlineData("requiredCodecVersion", "1.0", ReplicaExtensionErrors.Invalid)]
    [InlineData("requiredVersion", "0", ReplicaExtensionErrors.Invalid)]
    public void Compatibility_RejectsUnknownOrUnsupportedRequirements(string name, string value, string code)
    {
        var extension = "{\"version\":1,\"codecVersion\":1,\"document\":{},\"exactTimeMap\":{},\"compatibility\":{\"" + name + "\":" + value + "}}";
        var json = ProjectJson(extension);
        var error = Assert.Throws<ReplicaExtensionException>(() => EditorProjectCodec.Deserialize(json));
        Assert.Equal(code, error.Code);
        Assert.Equal("$.replicaExtension.compatibility." + name, error.FieldPath);
        Assert.Throws<ReplicaExtensionException>(() => EditorProjectCodec.StampSchemaVersion(json));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("codecVersion")]
    public void ExtensionVersion_IsIndependentOfRootVersion_AndFailsClosed(string field)
    {
        var extension = Extension.Replace("\"" + field + "\":1", "\"" + field + "\":2", StringComparison.Ordinal);
        var json = ProjectJson(extension);
        Assert.Equal(1, EditorProjectCodec.ReadSchemaVersion(json));
        var error = Assert.Throws<ReplicaExtensionException>(() => EditorProjectCodec.EnsureSupported(json));
        Assert.Equal(ReplicaExtensionErrors.Unsupported, error.Code);
        Assert.Equal("$.replicaExtension." + field, error.FieldPath);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"codecVersion\":1,\"document\":[],\"exactTimeMap\":{},\"compatibility\":{}}")]
    [InlineData("{\"version\":1,\"Version\":1,\"codecVersion\":1,\"document\":{},\"exactTimeMap\":{},\"compatibility\":{}}")]
    [InlineData("{\"version\":1,\"codecVersion\":1,\"document\":{},\"exactTimeMap\":{},\"compatibility\":{\"requiredVersion\":1,\"requiredVersion\":1}}")]
    public void InvalidExtensionShape_IsNotSilentlyDropped(string extension)
    {
        var error = Assert.Throws<ReplicaExtensionException>(() => EditorProjectCodec.Deserialize(ProjectJson(extension)));
        Assert.Equal(ReplicaExtensionErrors.Invalid, error.Code);
    }

    [Fact]
    public void DuplicateExtensionProperty_IsRejectedBeforeTypedDeserialization()
    {
        var json = "{\"schemaVersion\":1,\"replicaExtension\":" + Extension + ",\"ReplicaExtension\":" + Extension + "}";
        var error = Assert.Throws<ReplicaExtensionException>(() => EditorProjectCodec.Deserialize(json));
        Assert.Equal(ReplicaExtensionErrors.Invalid, error.Code);
    }

    [Fact]
    public void ReplaceProjectAndOrdinaryCommand_PreserveExtension_AndRejectUnknownRequired()
    {
        var command = Replace(ProjectJson(Extension));
        var replaced = EditorCommandExecutor.Apply(new EditorProject { Start = 0, End = 8 }, command, out _);
        var (changed, _) = EditorCommandExecutor.ApplyBatch(replaced,
            [new EditorCommand("setRange", new JsonObject { ["start"] = 0.0, ["end"] = 7.0 })]);
        AssertExtension(Extension, JsonSerializer.Serialize(replaced));
        AssertExtension(Extension, JsonSerializer.Serialize(changed));
        Assert.Same(replaced.ReplicaExtension, changed.ReplicaExtension);
        var error = Assert.Throws<ReplicaExtensionException>(() => EditorCommandExecutor.Apply(changed, Replace(ProjectJson(UnsupportedExtension)), out _));
        Assert.Equal(ReplicaExtensionErrors.Unsupported, error.Code);
        Assert.Equal(7d, changed.End);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ReplaceProject_NonObject_RemainsACommandError(string project)
    {
        Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.Apply(
            new EditorProject { Start = 0, End = 8 }, Replace(project), out _));
    }

    [Fact]
    public async Task PutReplaceSessionUndoRedo_PreserveEveryExtensionSnapshot()
    {
        await using var fixture = new Fixture(ProjectJson(Extension));
        await fixture.ExpectOk("editor", ProjectJson(ExtensionTwo, 7), "PUT");
        AssertExtension(ExtensionTwo, fixture.ProjectJson);
        await fixture.ExpectOk("commands", fixture.ReplaceBody(ProjectJson(ExtensionThree, 6)));
        AssertExtension(ExtensionThree, fixture.ProjectJson);
        Assert.Equal(2, EditorRevisionStore.SnapshotDepth(fixture.ClipId));

        var begin = await fixture.Request("sessions");
        Assert.Equal(StatusCodes.Status200OK, begin.Status);
        var session = begin.Body.GetProperty("sessionId").GetString()!;
        await fixture.ExpectOk("editor", ProjectJson(ExtensionTwo, 5), "PUT", session);
        await fixture.ExpectOk("commands", fixture.ReplaceBody(ProjectJson(Extension, 4)), sessionId: session);
        AssertExtension(Extension, fixture.ProjectJson);
        Assert.Equal(2, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        var ended = await fixture.Request("sessions/{sid}/end", sid: session);
        Assert.Equal(StatusCodes.Status200OK, ended.Status);
        Assert.Equal(3, EditorRevisionStore.SnapshotDepth(fixture.ClipId));

        await fixture.ExpectOk("undo");
        AssertExtension(ExtensionThree, fixture.ProjectJson);
        await fixture.ExpectOk("redo");
        AssertExtension(Extension, fixture.ProjectJson);
        await fixture.ExpectOk("undo");
        AssertExtension(ExtensionThree, fixture.ProjectJson);
        await fixture.ExpectOk("undo");
        AssertExtension(ExtensionTwo, fixture.ProjectJson);
        await fixture.ExpectOk("undo");
        AssertExtension(Extension, fixture.ProjectJson);
    }

    [Theory]
    [InlineData("editor", "PUT")]
    [InlineData("commands", "POST")]
    [InlineData("variants", "POST")]
    public async Task WriteEndpoints_RejectRequiredBeforeDtoParsing_WithoutStateChanges(string route, string method)
    {
        await using var fixture = new Fixture(ProjectJson(Extension));
        // Invalid typed Start proves raw extension validation ran first, not just its property converter.
        var submitted = ProjectJson(UnsupportedExtension).Replace("\"start\":0", "\"start\":{}", StringComparison.Ordinal);
        var payload = route switch
        {
            "commands" => fixture.ReplaceBody(submitted),
            "variants" => "{\"name\":\"unsupported\",\"project\":" + submitted + "}",
            _ => submitted
        };
        var before = fixture.ProjectJson;
        var revision = fixture.Revision;
        var result = await fixture.Request(route, payload, method);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.Status);
        Assert.Equal(ReplicaExtensionErrors.Unsupported, result.Body.GetProperty("code").GetString());
        Assert.Equal(ReplicaExtensionErrors.Unsupported, result.Body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("$.replicaExtension.compatibility.requiredFuture", result.Body.GetProperty("error").GetProperty("fieldPath").GetString());
        Assert.Equal(before, fixture.ProjectJson);
        Assert.Equal(revision, fixture.Revision);
        Assert.Equal(0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        Assert.Empty(fixture.Studio.Variants(fixture.ClipId));
    }

    [Fact]
    public async Task Put_RejectsFutureRootSchema_BeforeDtoParsing()
    {
        await using var fixture = new Fixture(ProjectJson(Extension));
        var before = fixture.ProjectJson;
        var revision = fixture.Revision;
        var result = await fixture.Request("editor", "{\"schemaVersion\":2,\"start\":{},\"end\":8}", "PUT");
        Assert.Equal(StatusCodes.Status409Conflict, result.Status);
        Assert.Equal(EditorProjectSchemaErrors.TooNew, result.Body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before, fixture.ProjectJson);
        Assert.Equal(revision, fixture.Revision);
    }

    [Fact]
    public async Task Variant_RejectsInvalidTypedFields_WithoutSaving()
    {
        await using var fixture = new Fixture(ProjectJson(Extension));
        var malformed = ProjectJson(Extension).Replace("\"start\":0", "\"start\":{}", StringComparison.Ordinal);
        var result = await fixture.Request("variants", "{\"name\":\"invalid\",\"project\":" + malformed + "}");
        Assert.Equal(StatusCodes.Status400BadRequest, result.Status);
        Assert.Equal(StudioErrorCodes.InvalidProject, result.Body.GetProperty("code").GetString());
        Assert.Empty(fixture.Studio.Variants(fixture.ClipId));
    }

    [Fact]
    public async Task VariantBackupAndReopenedStore_PreserveExtensionAndStringStorage()
    {
        var original = ProjectJson(Extension);
        await using var fixture = new Fixture(original);
        var variant = await fixture.Request("variants", "{\"name\":\"archive\",\"project\":" + original + "}");
        Assert.Equal(StatusCodes.Status200OK, variant.Status);
        var variantId = variant.Body.GetProperty("id").GetString()!;
        var storedVariant = fixture.Studio.Variant(variantId)!;
        AssertExtension(Extension, storedVariant.ProjectJson);
        var retrieved = await fixture.Request("/api/studio/variants/{id}", method: "GET", routeId: variantId);
        Assert.Equal(StatusCodes.Status200OK, retrieved.Status);
        AssertExtension(Extension, retrieved.Body.GetProperty("project").GetRawText());

        await fixture.ExpectOk("editor", ProjectJson(ExtensionTwo), "PUT");
        var backup = Assert.Single(fixture.Studio.ProjectRevisions(fixture.ClipId));
        Assert.Equal(original, fixture.Studio.ProjectRevision(fixture.ClipId, backup.FileName));
        var backupResponse = await fixture.Request("project-backups/{fileName}", method: "GET", fileName: backup.FileName);
        Assert.Equal(StatusCodes.Status200OK, backupResponse.Status);
        AssertExtension(Extension, backupResponse.Body.GetRawText());

        var reopened = new StudioStore(fixture.DatabasePath);
        Assert.Equal(fixture.ProjectJson, reopened.Project(fixture.ClipId));
        Assert.Equal(storedVariant.ProjectJson, reopened.Variant(variantId)!.ProjectJson);
        Assert.Equal(original, reopened.ProjectRevision(fixture.ClipId, backup.FileName));
        AssertExtension(ExtensionTwo, reopened.Project(fixture.ClipId)!);
    }

    [Fact]
    public async Task ExternalWriteAndRenderSnapshot_PreserveExtension()
    {
        await using var fixture = new Fixture(ProjectJson(Extension));
        var before = fixture.ProjectJson;
        var result = EditorRevisionStore.SaveExternal(fixture.Studio, fixture.ClipId, "extension-test", saved =>
            JsonSerializer.Serialize(EditorProjectCodec.Deserialize(saved!) with { End = 7 }));
        Assert.True(result.Saved);
        AssertExtension(Extension, fixture.ProjectJson);
        Assert.Equal(before, EditorRevisionStore.PopSnapshot(fixture.ClipId));
        var renderId = Guid.NewGuid().ToString("N");
        fixture.Studio.SaveRender(new EditorRender(renderId, fixture.ClipId, "not-rendered.mp4", fixture.ProjectJson, DateTimeOffset.UtcNow));
        Assert.Equal(fixture.ProjectJson, Assert.Single(fixture.Studio.Renders()).ProjectJson);
    }

    [Theory]
    [InlineData("undo")]
    [InlineData("redo")]
    public async Task History_RejectsUnknownRequired_AndRestoresPoppedSnapshot(string route)
    {
        await using var fixture = new Fixture(ProjectJson(Extension));
        _ = fixture.Revision; // Bind before seeding the in-memory stack, just as a committed write does.
        var unsupported = ProjectJson(UnsupportedExtension);
        if (route == "undo") EditorRevisionStore.PushSnapshot(fixture.ClipId, unsupported);
        else EditorRevisionStore.PushRedo(fixture.ClipId, unsupported);
        var before = fixture.ProjectJson;
        var revision = fixture.Revision;
        var result = await fixture.Request(route);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.Status);
        Assert.Equal(ReplicaExtensionErrors.Unsupported, result.Body.GetProperty("code").GetString());
        Assert.Equal(before, fixture.ProjectJson);
        Assert.Equal(revision, fixture.Revision);
        Assert.Equal(route == "undo" ? 1 : 0, EditorRevisionStore.SnapshotDepth(fixture.ClipId));
        Assert.Equal(route == "redo" ? 1 : 0, EditorRevisionStore.RedoDepth(fixture.ClipId));
        Assert.Equal(unsupported, route == "undo" ? EditorRevisionStore.PopSnapshot(fixture.ClipId) : EditorRevisionStore.PopRedo(fixture.ClipId));
    }

    private static string ProjectJson(string extension, int end = 8, int? schemaVersion = 1)
        => "{" + (schemaVersion is { } version ? "\"schemaVersion\":" + version + "," : "") +
            "\"start\":0,\"end\":" + end + ",\"replicaExtension\":" + extension + "}";

    private static EditorCommand Replace(string project)
        => new("replaceProject", new JsonObject { ["project"] = JsonNode.Parse(project) });

    private static void AssertExtension(string expected, string project)
    {
        using var source = JsonDocument.Parse(expected);
        using var saved = JsonDocument.Parse(project);
        AssertJson(source.RootElement, saved.RootElement.GetProperty("replicaExtension"));
    }

    private static void AssertJson(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProperties = expected.EnumerateObject().ToArray();
                var actualProperties = actual.EnumerateObject().ToArray();
                Assert.Equal(expectedProperties.Select(p => p.Name), actualProperties.Select(p => p.Name));
                for (var i = 0; i < expectedProperties.Length; i++) AssertJson(expectedProperties[i].Value, actualProperties[i].Value);
                break;
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (var i = 0; i < expected.GetArrayLength(); i++) AssertJson(expected[i], actual[i]);
                break;
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            case JsonValueKind.Number:
                // Compare tokens, never doubles/decimals: precision and exponent/trailing-zero spelling matter.
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-replica-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;
        public string ClipId { get; } = "replica-" + Guid.NewGuid().ToString("N");
        public string DatabasePath { get; }
        public StudioStore Studio { get; }
        public int Revision => EditorRevisionStore.Current(Studio, ClipId);
        public string ProjectJson => Studio.Project(ClipId)!;

        public Fixture(string initial)
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "test.db");
            var store = new Store(DatabasePath);
            store.Migrate();
            Studio = new StudioStore(DatabasePath);
            Studio.Migrate();
            store.SaveClip(new Clip(ClipId, "event-" + ClipId, Path.Combine(_root, "source.mp4"), null, DateTimeOffset.UtcNow));
            Studio.SaveProject(ClipId, initial);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            // Stored projects avoid media probing. Unexpected media/AI access fails rather than being mocked away.
            StudioEndpoints.Map(_app, store, Studio, null!, null!, null!, null!, null!, null!, null!, new LiveSettings { StorageRoot = _root });
        }

        public string ReplaceBody(string project)
            => "{\"baseRevision\":" + Revision + ",\"commands\":[{\"op\":\"replaceProject\",\"args\":{\"project\":" + project + "}}]}";

        public async Task ExpectOk(string route, string? json = null, string method = "POST", string? sessionId = null)
        {
            var revision = Revision;
            var result = await Request(route, json, method, sessionId);
            Assert.Equal(StatusCodes.Status200OK, result.Status);
            Assert.Equal(revision + 1, result.Body.GetProperty("revision").GetInt32());
            Assert.Equal(1, EditorProjectCodec.ReadSchemaVersion(ProjectJson));
        }

        public async Task<(int Status, JsonElement Body)> Request(string route, string? json = null, string method = "POST",
            string? sessionId = null, string? sid = null, string? routeId = null, string? fileName = null)
        {
            var pattern = route.StartsWith("/", StringComparison.Ordinal) ? route : Prefix + route;
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources
                .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == pattern && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = method;
            context.Request.RouteValues["id"] = routeId ?? ClipId;
            if (sid is not null) context.Request.RouteValues["sid"] = sid;
            if (fileName is not null) context.Request.RouteValues["fileName"] = fileName;
            context.Request.Headers["X-Base-Revision"] = Revision.ToString(CultureInfo.InvariantCulture);
            if (sessionId is not null) context.Request.Headers["X-Session-Id"] = sessionId;
            byte[] bytes = json is null ? [] : Encoding.UTF8.GetBytes(json);
            await using var input = new MemoryStream(bytes);
            if (json is not null)
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
            using var document = await JsonDocument.ParseAsync(output);
            return (context.Response.StatusCode, document.RootElement.Clone());
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
