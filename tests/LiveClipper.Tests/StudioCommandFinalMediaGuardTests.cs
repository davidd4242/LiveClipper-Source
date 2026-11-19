using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class StudioCommandFinalMediaGuardTests(ITestOutputHelper output)
{
    private const string Route = "/api/studio/clips/{id}/commands";
    private const string Projection = "lc-editor-next-active-sequence-v1";
    private const string P = "$.replicaExtension.document.sequence";
    private const string Weird = "a\"b\\雪";
    private const string WeirdKey = "[\"a\\\"b\\\\雪\"]";
    private static readonly DateTimeOffset StableAt = DateTimeOffset.MaxValue.AddDays(-1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("timeline")]
    [InlineData("backup")]
    [InlineData("nested")]
    [InlineData("transcript")]
    [InlineData("caption")]
    public async Task Commands_InheritedTimelineMissing_UnrelatedLegalCommand_Rejects(string place)
    {
        await using var f = new Fixture(output);
        var clean = Clean(f.Id);
        var missing = place is "transcript" or "caption" ? Weird : "gone";
        var project = place switch
        {
            "timeline" => BadTimeline(f.Id),
            "backup" => clean with { SilenceTrimBackup = [new("bad", missing, 0, 2)] },
            "nested" => clean with { NestedSequences = new() { [Weird] = new("unused", "unused", [new("bad", missing, 0, 2)]) } },
            "transcript" => clean with { SourceTranscripts = new() { [missing] = [] } },
            "caption" => clean with { CaptionOverrides = new() { [missing] = [] } },
            _ => throw new ArgumentOutOfRangeException(nameof(place))
        };
        var path = place switch
        {
            "timeline" => "$.timeline[0].clipId",
            "backup" => "$.silenceTrimBackup[0].clipId",
            "nested" => "$.nestedSequences" + WeirdKey + ".timeline[0].clipId",
            "transcript" => "$.sourceTranscripts" + WeirdKey,
            _ => "$.captionOverrides" + WeirdKey
        };
        f.Seed(project);
        await Reject(f, [Mute()], [missing], [new(missing, place is "transcript" or "caption" ? missing : "bad", path)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commands_RemoveInheritedBadItem_FinalGraphClean_Saves(bool batch)
    {
        await using var f = new Fixture(output);
        f.Seed(BadTimeline(f.Id));
        EditorCommand[] commands = batch ? [Mute(), Remove("bad")] : [Remove("bad")];
        var before = f.Capture();
        var reply = await f.Commands(commands);
        AssertSaved(f, before, reply, Clean(f.Id) with { BaseAudioMuted = batch }, commands.Length);
        Assert.Equal(new[] { "bad" }, reply.Body.GetProperty("results")[commands.Length - 1].GetProperty("removed").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Commands_RemoveOnlyItem_StillInvalidCommand()
    {
        await using var f = new Fixture(output);
        f.Seed(Clean(f.Id) with { Timeline = [new("bad", "gone", 0, 2)] });
        var before = f.Capture();
        var reply = await f.Commands([Remove("bad")]);
        Assert.Multiple(() => AssertError(before, reply, 400, "invalid_command", "invalid-command"),
            () => AssertUnchanged(before, f.Capture()));
    }

    [Theory]
    [InlineData("music", "missing")]
    [InlineData("music", "wrong-kind")]
    [InlineData("music", "clip-only")]
    [InlineData("music", "correct-kind-offline")]
    [InlineData("effect", "missing")]
    [InlineData("effect", "wrong-kind")]
    [InlineData("effect", "clip-only")]
    [InlineData("effect", "correct-kind-offline")]
    public async Task Commands_TypedAssetNamespace_FinalGuard(string kind, string registration)
    {
        await using var f = new Fixture(output);
        const string asset = "Asset-X";
        if (registration == "clip-only") f.RegisterClip(asset);
        if (registration == "wrong-kind") f.RegisterAsset(asset, kind == "music" ? "effect" : "music");
        if (registration == "correct-kind-offline") f.RegisterAsset(asset, kind);
        var project = AssetProject(f.Id, kind, asset);
        f.Seed(project);
        if (registration == "correct-kind-offline")
        {
            var before = f.Capture();
            AssertSaved(f, before, await f.Commands([Mute()]), project with { BaseAudioMuted = true }, 1);
        }
        else await Reject(f, [Mute()], [asset], [new(asset, f.Id, "$." + kind + "AssetId")]);
    }

    [Theory]
    [InlineData("music")]
    [InlineData("effect")]
    public async Task Commands_ReplaceProject_MissingTypedAsset_Rejects(string kind)
    {
        await using var f = new Fixture(output);
        f.Seed(Clean(f.Id));
        await Reject(f, [Replace(AssetProject(f.Id, kind, "gone-asset"))], ["gone-asset"],
            [new("gone-asset", f.Id, "$." + kind + "AssetId")]);
    }

    [Theory]
    [InlineData("asset-x")]
    [InlineData(" Asset-X ")]
    public async Task Commands_AssetIds_AreOrdinalAndNotTrimmed(string reference)
    {
        await using var f = new Fixture(output);
        f.RegisterAsset("Asset-X", "music");
        f.Seed(AssetProject(f.Id, "music", reference));
        await Reject(f, [Mute()], [reference], [new(reference, f.Id, "$.musicAssetId")]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \t ")]
    public async Task Commands_AbsentOrBlankAssetSelection_IsLegal(string? reference)
    {
        await using var f = new Fixture(output);
        var project = Clean(f.Id) with { MusicAssetId = reference, EffectAssetId = reference };
        f.Seed(project);
        var before = f.Capture();
        AssertSaved(f, before, await f.Commands([Mute()]), project with { BaseAudioMuted = true }, 1);
    }

    [Fact]
    public async Task Commands_AssetRegistration_DoesNotSatisfyClipNamespace()
    {
        await using var f = new Fixture(output);
        f.RegisterAsset("gone", "music");
        f.Seed(BadTimeline(f.Id));
        await Reject(f, [Mute()], ["gone"], [new("gone", "bad", "$.timeline[0].clipId")]);
    }

    [Fact]
    public async Task Commands_ReplaceProject_CanRepairOldClipAndAssetGraph()
    {
        await using var f = new Fixture(output);
        f.Seed(BadTimeline(f.Id) with { MusicAssetId = "gone-music" });
        var before = f.Capture();
        AssertSaved(f, before, await f.Commands([Replace(Clean(f.Id))]), Clean(f.Id), 1);
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("nested", false)]
    [InlineData("angle", false)]
    [InlineData("hostMusic", false)]
    public async Task Commands_HostedOptIn_FinalGraphRejects(string shape, bool replacement)
    {
        await using var f = new Fixture(output);
        JsonObject clip = HostedClip("v1", "gone");
        var path = P + ".videoTracks[0].clips[0].sourceId";
        var entity = "v1";
        if (shape == "nested")
        {
            clip = HostedClip("outer");
            clip["nestedSeq"] = Sequence(HostedClip("nested-v", "gone"));
            path = P + ".videoTracks[0].clips[0].nestedSeq.videoTracks[0].clips[0].sourceId";
            entity = "nested-v";
        }
        if (shape == "angle")
        {
            clip = HostedClip("multi");
            clip["multicam"] = new JsonObject { ["activeAngle"] = 0, ["angles"] = new JsonArray(
                new JsonObject { ["clip"] = HostedClip("active", f.Id) },
                new JsonObject { ["clip"] = HostedClip("inactive", "gone") }) };
            path = P + ".videoTracks[0].clips[0].multicam.angles[1].clip.sourceId";
            entity = "inactive";
        }
        var sequence = Sequence(clip);
        if (shape == "hostMusic")
        {
            f.RegisterAsset("gone", "effect");
            sequence = Sequence(HostedClip("v1", f.Id));
            sequence["hostMusic"] = new JsonObject { ["musicAssetId"] = "gone" };
            path = P + ".hostMusic.musicAssetId";
            entity = f.Id;
        }
        var project = Clean(f.Id) with { ReplicaExtension = Extension(sequence) };
        f.Seed(replacement ? Clean(f.Id) : project);
        await Reject(f, replacement ? [Replace(project)] : [Mute()], ["gone"], [new("gone", entity, path)]);
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("foreign")]
    [InlineData("null")]
    [InlineData("registered")]
    public async Task Commands_HostedOpaqueBoundary_AndOfflineClip_ArePreserved(string mode)
    {
        await using var f = new Fixture(output);
        var clip = HostedClip("opaque-entity");
        clip["id"] = "not-media";
        clip["SourceId"] = "wrong-case-not-media";
        clip["source"] = new JsonObject { ["id"] = "alias-not-media" };
        clip["metadata"] = new JsonObject { ["sourceId"] = "metadata-not-media" };
        clip["multicam"] = new JsonObject { ["angles"] = new JsonArray(new JsonObject {
            ["id"] = "angle-entity", ["runtimeId"] = "runtime-only", ["clip"] = HostedClip("angle-key") }) };
        if (mode is "foreign" or "null") clip["sourceId"] = "gone-opaque";
        if (mode == "registered") clip["sourceId"] = f.Id;
        var projection = mode == "null" ? null : mode == "foreign" ? "foreign-projection" : Projection;
        var extension = Extension(Sequence(clip), projection);
        var project = Clean(f.Id) with { ReplicaExtension = extension };
        f.Seed(project);
        var before = f.Capture();
        AssertSaved(f, before, await f.Commands([Mute()]), project with { BaseAudioMuted = true }, 1);
        var actual = EditorProjectCodec.Deserialize(f.Capture().Project.Json).ReplicaExtension!;
        Assert.True(JsonElement.DeepEquals(extension.Json, actual.Json));
        Assert.Equal("1.2300e+03", actual.Document.GetProperty("metadata").GetProperty("exact").GetRawText());
    }

    [Fact]
    public async Task Commands_MissingAndLocations_KeepFirstSeenOrderAcrossDuplicateOccurrences()
    {
        await using var f = new Fixture(output);
        var sequence = Sequence(HostedClip("v1", "M-Clip"), HostedClip("v2", "M-Clip"));
        sequence["hostMusic"] = new JsonObject { ["musicAssetId"] = "Z-Music" };
        var project = Clean(f.Id) with
        {
            MusicAssetId = "Z-Music", EffectAssetId = "A-Effect",
            Timeline = [new("t0", "M-Clip", 0, 2, StickerAssetId: "B-Sticker", SoundAssetId: "Z-Music"),
                new("t1", "M-Clip", 2, 4, StickerAssetId: "B-Sticker")],
            SilenceTrimBackup = [new("b0", f.Id, 0, 2, SoundAssetId: "A-Effect")],
            NestedSequences = new() { [Weird] = new("unused", "unused", [new("n0", "M-Clip", 0, 2)]) },
            SourceTranscripts = new() { ["M-Clip"] = [] }, CaptionOverrides = new() { ["M-Clip"] = [] },
            ReplicaExtension = Extension(sequence)
        };
        f.Seed(project);
        Location[] locations = [new("Z-Music", f.Id, "$.musicAssetId"), new("A-Effect", f.Id, "$.effectAssetId"),
            new("M-Clip", "t0", "$.timeline[0].clipId"), new("B-Sticker", "t0", "$.timeline[0].stickerAssetId"),
            new("Z-Music", "t0", "$.timeline[0].soundAssetId"), new("M-Clip", "t1", "$.timeline[1].clipId"),
            new("B-Sticker", "t1", "$.timeline[1].stickerAssetId"), new("A-Effect", "b0", "$.silenceTrimBackup[0].soundAssetId"),
            new("M-Clip", "n0", "$.nestedSequences" + WeirdKey + ".timeline[0].clipId"),
            new("M-Clip", "M-Clip", "$.sourceTranscripts[\"M-Clip\"]"), new("M-Clip", "M-Clip", "$.captionOverrides[\"M-Clip\"]"),
            new("Z-Music", f.Id, P + ".hostMusic.musicAssetId"), new("M-Clip", "v1", P + ".videoTracks[0].clips[0].sourceId"),
            new("M-Clip", "v2", P + ".videoTracks[0].clips[1].sourceId")];
        var first = await Reject(f, [Mute()], ["Z-Music", "A-Effect", "M-Clip", "B-Sticker"], locations);
        var second = await Reject(f, [Mute()], ["Z-Music", "A-Effect", "M-Clip", "B-Sticker"], locations);
        Assert.Equal(first.Body.GetRawText(), second.Body.GetRawText());
    }

    [Theory]
    [InlineData("clip", "none")]
    [InlineData("clip", "clean")]
    [InlineData("clip", "dirty")]
    [InlineData("asset", "none")]
    [InlineData("asset", "clean")]
    [InlineData("asset", "dirty")]
    public async Task Commands_FinalRejection_StableSessionPreservesAllState(string kind, string session)
    {
        await using var f = new Fixture(output);
        f.Seed(kind == "clip" ? BadTimeline(f.Id) : AssetProject(f.Id, "music", "gone"), session);
        var seeded = f.Capture();
        Assert.Equal(session != "none", seeded.Session is not null);
        if (seeded.Session is { } active)
        {
            Assert.Equal(session == "dirty", active.HasWrites);
            Assert.Equal(StableAt, active.LastActivity);
        }
        await Reject(f, [Mute()], ["gone"], [kind == "clip" ? new("gone", "bad", "$.timeline[0].clipId") : new("gone", f.Id, "$.musicAssetId")]);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("unknown-first")]
    [InlineData("replace")]
    [InlineData("replace-then-repair")]
    public async Task Commands_InputMissing_StillWinsAndCannotBeRepairedLaterInBatch(string shape)
    {
        await using var f = new Fixture(output);
        f.Seed(BadTimeline(f.Id) with { MusicAssetId = "Graph-Gone" });
        var input = Replace(Clean(f.Id) with { Timeline = [new("new", "Input-Gone", 0, 2)], MusicAssetId = "Graph-Gone" });
        var add = new EditorCommand("addItems", JsonNode.Parse("{\"items\":[{\"clipId\":\"Input-Gone\",\"start\":0,\"end\":2}]}"));
        EditorCommand[] commands = shape switch { "add" => [add], "unknown-first" => [new("unknownOp", null), add],
            "replace" => [input], _ => [input, Replace(Clean(f.Id))] };
        var before = f.Capture();
        var reply = await f.Commands(commands);
        Assert.Multiple(() => AssertError(before, reply, 400, "missing_media", "missing_media"),
            () => Assert.Equal("命令引用了不存在的媒体", reply.Body.GetProperty("error").GetProperty("message").GetString()),
            () => Assert.Equal(new[] { "Input-Gone" }, Missing(reply)),
            () => Assert.False(reply.Body.GetProperty("error").TryGetProperty("locations", out _)),
            () => Assert.False(reply.Body.TryGetProperty("results", out _)), () => AssertUnchanged(before, f.Capture()));
    }

    [Theory]
    [InlineData("unknown", 400, "invalid_command", "invalid-command")]
    [InlineData("empty", 400, "invalid_command", "empty-batch")]
    [InlineData("null", 400, "invalid_command", "empty-batch")]
    [InlineData("stale-empty", 409, "revision_conflict", "stale-revision")]
    [InlineData("stale-missing", 409, "revision_conflict", "stale-revision")]
    [InlineData("missing-revision", 428, "precondition_required", "precondition-required")]
    [InlineData("malformed-revision", 400, "invalid_revision", "invalid-revision")]
    [InlineData("revision-mismatch", 400, "invalid_revision", "invalid-revision")]
    [InlineData("stale-epoch", 409, "stale_epoch", "stale-epoch")]
    [InlineData("session-mismatch", 409, "session_not_found", "session-mismatch")]
    public async Task Commands_EarlierRejections_KeepPriority(string scenario, int status, string code, string nested)
    {
        await using var f = new Fixture(output);
        f.Seed(BadTimeline(f.Id), "clean");
        var before = f.Capture();
        EditorCommand[]? commands = scenario switch
        {
            "null" => null, "empty" or "stale-empty" => [], "unknown" => [new("unknownOp", null)],
            "stale-missing" => [Replace(Clean(f.Id) with { Timeline = [new("bad", "Input-Gone", 0, 2)] })],
            _ => [Mute()]
        };
        var header = scenario switch { "malformed-revision" => "bad", "stale-empty" or "stale-missing" or "session-mismatch" => "0", _ => null };
        var badEpoch = scenario is "missing-revision" or "malformed-revision" or "revision-mismatch" or "stale-epoch";
        var reply = await f.Commands(commands, revisionHeader: header, omitRevision: scenario == "missing-revision",
            bodyRevision: scenario == "revision-mismatch" ? 0 : null, epoch: badEpoch ? "old-epoch" : null,
            sessionId: scenario is "session-mismatch" or "stale-epoch" ? "wrong-session" : null);
        Assert.Multiple(() => AssertError(before, reply, status, code, nested), () => AssertUnchanged(before, f.Capture()));
        Assert.False(reply.Body.GetProperty("error").TryGetProperty("locations", out _));
    }

    [Fact]
    public async Task Commands_OuterMissingTarget_Remains404()
    {
        await using var f = new Fixture(output);
        f.Seed(BadTimeline(f.Id));
        var before = f.Capture();
        var reply = await f.Commands([Mute()], targetId: "absent-target", omitRevision: true, epoch: "old-epoch");
        Assert.Equal(404, reply.Status);
        Assert.Equal("not_found", reply.Body.GetProperty("code").GetString());
        AssertUnchanged(before, f.Capture());
    }

    private static EditorProject Clean(string id) => new() { End = 8, Timeline = [new("keep", id, 0, 2)] };
    private static EditorProject BadTimeline(string id) => Clean(id) with { Timeline = [new("bad", "gone", 0, 2), new("keep", id, 0, 2)] };
    private static EditorProject AssetProject(string id, string kind, string? asset)
        => kind == "music" ? Clean(id) with { MusicAssetId = asset } : Clean(id) with { EffectAssetId = asset };
    private static EditorCommand Mute() => new("setBaseAudio", new JsonObject { ["muted"] = true });
    private static EditorCommand Remove(string key) => new("removeItems", new JsonObject { ["keys"] = new JsonArray(key) });
    private static EditorCommand Replace(EditorProject project) => new("replaceProject", new JsonObject { ["project"] = JsonSerializer.SerializeToNode(project) });
    private static JsonObject HostedClip(string key, string? source = null)
    {
        var clip = new JsonObject { ["key"] = key, ["kind"] = "video", ["start"] = 0, ["end"] = 2 };
        if (source is not null) clip["sourceId"] = source;
        return clip;
    }
    private static JsonObject Sequence(params JsonObject[] clips) => new()
    {
        ["name"] = "S", ["fps"] = 30, ["duration"] = 60,
        ["videoTracks"] = new JsonArray(new JsonObject { ["id"] = "v", ["kind"] = "video", ["clips"] = new JsonArray(clips.Select(x => (JsonNode)x).ToArray()) }),
        ["audioTracks"] = new JsonArray()
    };
    private static ReplicaExtension Extension(JsonObject sequence, string? projection = Projection)
    {
        var envelope = new JsonObject
        {
            ["version"] = 1, ["codecVersion"] = 1,
            ["document"] = new JsonObject { ["__fcproj"] = 1, ["sequence"] = sequence,
                ["metadata"] = JsonNode.Parse("{\"exact\":1.2300e+03,\"sourceId\":\"opaque-meta\"}"),
                ["catalog"] = new JsonObject { ["sourceId"] = "opaque-catalog" } },
            ["exactTimeMap"] = new JsonObject(), ["compatibility"] = new JsonObject
                { ["requiredVersion"] = 1, ["requiredCodecVersion"] = 1, ["projection"] = projection }
        };
        return new ReplicaExtension(JsonSerializer.SerializeToElement(envelope));
    }

    private static string?[] Missing(Reply reply) => reply.Body.GetProperty("error").GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToArray();
    private static Location[] Locations(Reply reply) => reply.Body.GetProperty("error").GetProperty("locations").EnumerateArray()
        .Select(x => new Location(x.GetProperty("mediaId").GetString()!, x.GetProperty("entityId").GetString()!, x.GetProperty("fieldPath").GetString()!)).ToArray();
    private static async Task<Reply> Reject(Fixture f, EditorCommand[] commands, string[] missing, Location[] locations)
    {
        var before = f.Capture();
        var reply = await f.Commands(commands);
        Assert.Multiple(() => AssertError(before, reply, 400, "missing_media", "missing_media"),
            () => Assert.Equal("工程引用了不存在的媒体", reply.Body.GetProperty("error").GetProperty("message").GetString()),
            () => Assert.Equal(missing, Missing(reply)), () => Assert.Equal(locations, Locations(reply)),
            () => Assert.False(reply.Body.TryGetProperty("results", out _)), () => AssertUnchanged(before, f.Capture()));
        return reply;
    }
    private static void AssertError(Snapshot before, Reply reply, int status, string code, string nested)
    {
        Assert.Multiple(() => Assert.Equal(status, reply.Status),
            () => Assert.Equal(code, reply.Body.GetProperty("code").GetString()),
            () => Assert.Equal(nested, reply.Body.GetProperty("error").GetProperty("code").GetString()),
            () => Assert.Equal(before.Project.Revision, reply.Body.GetProperty("revision").GetInt64()),
            () => Assert.Equal(before.Epoch, reply.Epoch));
    }
    private static void AssertUnchanged(Snapshot before, Snapshot after)
    {
        Assert.Multiple(() => Assert.Equal(before.Project, after.Project), () => Assert.Equal(before.CachedRevision, after.CachedRevision),
            () => Assert.Equal(before.PendingRevision, after.PendingRevision), () => Assert.Equal(before.Epoch, after.Epoch),
            () => Assert.Equal(before.Backups, after.Backups), () => Assert.Equal(before.BackupCount, after.BackupCount),
            () => Assert.Equal(before.OrderedUndo, after.OrderedUndo), () => Assert.Equal(before.OrderedRedo, after.OrderedRedo),
            () => Assert.Equal(before.Session, after.Session), () => Assert.Equal(before.Registry, after.Registry));
    }
    private static void AssertSaved(Fixture f, Snapshot before, Reply reply, EditorProject expected, int count)
    {
        Assert.Equal(200, reply.Status);
        Assert.Equal(new[] { "revision", "results" }, reply.Body.EnumerateObject().Select(x => x.Name));
        Assert.Equal(count, reply.Body.GetProperty("results").GetArrayLength());
        Assert.Equal(before.Project.Revision + 1, reply.Body.GetProperty("revision").GetInt64());
        Assert.Equal(before.Epoch, reply.Epoch);
        var after = f.Capture();
        var json = EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(expected));
        Assert.Multiple(() => Assert.Equal(json, after.Project.Json), () => Assert.Equal(before.Project.Revision + 1, after.Project.Revision),
            () => Assert.Equal(after.Project.Revision, after.CachedRevision), () => Assert.Null(after.PendingRevision),
            () => Assert.Equal(before.Epoch, after.Epoch), () => Assert.Equal(before.BackupCount + 1, after.BackupCount),
            () => Assert.Equal(before.OrderedUndo.Append(before.Project.Json), after.OrderedUndo), () => Assert.Empty(after.OrderedRedo),
            () => Assert.Null(after.Session), () => Assert.Equal(before.Registry, after.Registry));
        Assert.Equal(after.Project, new StudioStore(f.Store.DatabasePath).GetProjectRecord(f.Id));
        Assert.Contains(f.Studio.ProjectRevisions(f.Id), backup => f.Studio.ProjectRevision(f.Id, backup.FileName) == before.Project.Json);
    }

    private sealed record Location(string MediaId, string EntityId, string FieldPath);
    private sealed record Reply(int Status, JsonElement Body, string Epoch);
    private sealed record SessionState(string Id, int CheckpointRevision, string Checkpoint, DateTimeOffset LastActivity, bool HasWrites);
    private sealed record Snapshot(StoredEditorProject Project, int CachedRevision, int? PendingRevision, string Epoch,
        string Backups, int BackupCount, string[] OrderedUndo, string[] OrderedRedo, SessionState? Session, string Registry);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly WebApplication _app;
        private readonly string _root;
        public Store Store { get; }
        public StudioStore Studio { get; }
        public string Id { get; } = "commands-final-" + Guid.NewGuid().ToString("N");
        public Fixture(ITestOutputHelper output, [CallerFilePath] string sourceFile = "")
        {
            _output = output;
            var repository = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
            _root = Path.Combine(repository, "docs", "merge", "_work", "windows-landing-20261110", "COMMANDS-FINAL-GRAPH-FIX", "fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _output.WriteLine("Retained isolated SQLite fixture: " + _root);
            _output.WriteLine("Runtime=" + RuntimeInformation.FrameworkDescription + " process=" + Environment.ProcessId);
            Store = new Store(Path.Combine(_root, "test.db"));
            Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath);
            Studio.Migrate();
            RegisterClip(Id);
            Studio.SaveProject(Id, JsonSerializer.Serialize(Clean(Id)));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            StudioEndpoints.Map(_app, Store, Studio, null!, null!, null!, null!, null!, null!, null!, new LiveSettings { StorageRoot = _root });
        }
        public void RegisterClip(string id)
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mp4");
            Assert.False(File.Exists(path));
            Store.SaveClip(new Clip(id, "event-" + id, path, null, DateTimeOffset.UtcNow));
        }
        public void RegisterAsset(string id, string kind)
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".offline");
            Assert.False(File.Exists(path));
            Studio.SaveAsset(new AudioAsset(id, id, kind, "test", path, false));
        }
        public void Seed(EditorProject project, string session = "none")
        {
            var json = JsonSerializer.Serialize(project);
            EditorRenderService.Validate(EditorProjectCodec.Deserialize(json));
            _output.WriteLine("HISTORICAL SEED (real codec/Validate passed, low-level persistence intentionally retains old refs) " + json);
            Studio.SaveProject(Id, json);
            EditorRevisionStore.Current(Studio, Id);
            EditorRevisionStore.EpochFor(Studio);
            EditorRevisionStore.Serialized(Studio, Id, () =>
            {
                var checkpoint = Studio.GetProjectRecord(Id)!;
                if (session != "none")
                {
                    var active = EditorRevisionStore.BeginSession(Id, checkpoint.Json, StableAt);
                    if (session == "dirty")
                    {
                        EditorRevisionStore.EnsureSession(Id, active.SessionId, now: StableAt);
                        var dirty = project with { End = project.End + 1 };
                        EditorRenderService.Validate(dirty);
                        var committed = EditorRevisionStore.Commit(Studio, Id, JsonSerializer.Serialize(dirty), (int)checkpoint.Revision);
                        Assert.True(committed.Ok);
                        EditorRevisionStore.RecordWrite(Id, checkpoint.Json, StableAt);
                        Assert.True(EditorRevisionStore.TryAdvance(Id, (int)checkpoint.Revision, out var revision));
                        Assert.Equal(committed.Revision, revision);
                        Assert.Equal(JsonSerializer.Serialize(project), JsonSerializer.Serialize(EditorProjectCodec.Deserialize(Studio.Project(Id)!) with { End = project.End }));
                    }
                }
                EditorRevisionStore.PushSnapshot(Id, JsonSerializer.Serialize(Clean(Id) with { End = 21 }));
                EditorRevisionStore.PushSnapshot(Id, JsonSerializer.Serialize(Clean(Id) with { End = 22 }));
                EditorRevisionStore.PushRedo(Id, JsonSerializer.Serialize(Clean(Id) with { End = 31 }));
                EditorRevisionStore.PushRedo(Id, JsonSerializer.Serialize(Clean(Id) with { End = 32 }));
                return true;
            });
        }
        public Snapshot Capture() => EditorRevisionStore.Serialized(Id, () =>
        {
            // Observe the already-bound entry without Bind, Rev, or idle maintenance.
            var entries = (IDictionary)typeof(EditorRevisionStore).GetField("Entries", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var entry = entries[Id]!;
            var active = Field(entry, "ActiveSession");
            var session = active is null ? null : new SessionState((string)Field(active, "Id")!, (int)Field(active, "CheckpointRevision")!,
                (string)Field(active, "Checkpoint")!, (DateTimeOffset)Field(active, "LastActivity")!, (bool)Field(active, "HasWrites")!);
            var backups = Studio.ProjectRevisions(Id);
            var contents = backups.Select(backup => new { Metadata = backup, Json = Studio.ProjectRevision(Id, backup.FileName) });
            return new Snapshot(Studio.GetProjectRecord(Id)!, (int)Field(entry, "Revision")!, (int?)Field(entry, "CommittedRevision"),
                EditorRevisionStore.EpochFor(Studio), JsonSerializer.Serialize(contents), backups.Count,
                ((List<string>)Field(entry, "Undo")!).ToArray(), ((List<string>)Field(entry, "Redo")!).ToArray(), session,
                JsonSerializer.Serialize(new { Clips = Store.Clips(), Assets = Studio.Assets(), Jobs = Store.Jobs(), Renders = Studio.Renders() }));
        });
        private static object? Field(object target, string name)
            => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
        public async Task<Reply> Commands(EditorCommand[]? commands, string? revisionHeader = null, bool omitRevision = false,
            int? bodyRevision = null, string? epoch = null, string? sessionId = null, string? targetId = null)
        {
            var before = Capture();
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>(),
                candidate => candidate.RoutePattern.RawText == Route && candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = "POST";
            context.Request.Path = Route.Replace("{id}", targetId ?? Id, StringComparison.Ordinal);
            context.Request.RouteValues["id"] = targetId ?? Id;
            if (!omitRevision) context.Request.Headers["X-Base-Revision"] = revisionHeader ?? before.Project.Revision.ToString(CultureInfo.InvariantCulture);
            context.Request.Headers["X-Editor-Epoch"] = epoch ?? before.Epoch;
            var sid = sessionId ?? before.Session?.Id;
            if (sid is not null) context.Request.Headers["X-Session-Id"] = sid;
            var body = JsonSerializer.Serialize(new EditorCommandBatch(bodyRevision, commands?.ToList()!, null), Web);
            var bytes = Encoding.UTF8.GetBytes(body);
            await using var input = new MemoryStream(bytes);
            context.Request.Body = input;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
            await using var response = new MemoryStream();
            context.Response.Body = response;
            _output.WriteLine("REQUEST POST " + context.Request.Path + " headers=" + JsonSerializer.Serialize(context.Request.Headers) + " payload=" + body);
            _output.WriteLine("BEFORE " + JsonSerializer.Serialize(before));
            try { await endpoint.RequestDelegate!(context); }
            finally { _output.WriteLine("AFTER " + JsonSerializer.Serialize(Capture())); }
            var raw = Encoding.UTF8.GetString(response.ToArray());
            _output.WriteLine("RESPONSE HTTP " + context.Response.StatusCode + " " + raw);
            using var json = JsonDocument.Parse(raw);
            return new(context.Response.StatusCode, json.RootElement.Clone(), context.Response.Headers["X-Editor-Epoch"].ToString());
        }
        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            EditorRevisionStore.Forget(Id);
            SqliteConnection.ClearAllPools();
        }
    }
    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
