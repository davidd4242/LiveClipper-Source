using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
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

public sealed class StudioPutAssetMediaTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PathStrings = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const string EditorRoute = "/api/studio/clips/{id}/editor";
    private const string SessionRoute = "/api/studio/clips/{id}/sessions";
    private const string SequenceKey = "unused.seq['\\\"雪]";
    private static readonly string[] Slots =
    [
        "musicAssetId", "effectAssetId", "timeline.stickerAssetId", "timeline.soundAssetId",
        "silenceTrimBackup.stickerAssetId", "silenceTrimBackup.soundAssetId",
        "nestedSequences.stickerAssetId", "nestedSequences.soundAssetId"
    ];

    public static IEnumerable<object[]> InvalidAssets()
    {
        foreach (var slot in Slots)
            foreach (var registry in new[] { "missing", "wrong-kind", "clip-only" })
                yield return [slot, registry];
    }

    public static IEnumerable<object[]> AssetSlots() => Slots.Select(slot => new object[] { slot });
    public static IEnumerable<object[]> OrdinalAssets() => Slots.SelectMany(slot => new[] { "case", "padded" }.Select(identity => new object[] { slot, identity }));

    [Theory]
    [MemberData(nameof(InvalidAssets))]
    public async Task Put_MissingWrongKindOrClipOnlyAsset_Returns400WithLocationsAndNoWrites(string slot, string registry)
    {
        await using var fixture = new Fixture(output);
        var id = "Asset-" + registry;
        if (registry == "wrong-kind") fixture.RegisterAsset(id, KindFor(slot) == "effect" ? "music" : "effect");
        if (registry == "clip-only") fixture.RegisterClip(id);
        Assert.DoesNotContain(fixture.Studio.Assets(), asset => asset.Id == id && asset.Kind == KindFor(slot));
        if (registry == "clip-only") Assert.Contains(fixture.Store.Clips(), clip => clip.Id == id);
        var body = Payload(WithAsset(Project(fixture.Primary.Id), slot, id));
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissing(fixture, before, reply, [LocationFor(slot, id, fixture.Primary.Id)]);
    }

    [Theory]
    [MemberData(nameof(AssetSlots))]
    public async Task Put_RegisteredOfflineAssetInEachTypedPosition_SavesNormally(string slot)
    {
        await using var fixture = new Fixture(output);
        var asset = fixture.RegisterAsset("Offline-Typed-Asset", KindFor(slot));
        Assert.Contains(fixture.Studio.Assets(), row => row == asset);
        Assert.False(File.Exists(asset.Path));
        var body = Payload(WithAsset(Project(fixture.Primary.Id), slot, asset.Id));
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertSaved(fixture, before, reply, body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public async Task Put_AbsentAssetSelectionsInEveryTypedPosition_SaveNormally(string? absent)
    {
        await using var fixture = new Fixture(output);
        var project = Project(fixture.Primary.Id);
        foreach (var slot in Slots) project = WithAsset(project, slot, absent);
        var body = Payload(project);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertSaved(fixture, before, reply, body);
    }

    [Theory]
    [MemberData(nameof(OrdinalAssets))]
    public async Task Put_AssetIdsUseOrdinalIdentityWithoutTrimming(string slot, string identity)
    {
        await using var fixture = new Fixture(output);
        var asset = fixture.RegisterAsset("Case-Sensitive-Asset", KindFor(slot));
        var missing = identity == "case" ? asset.Id.ToLowerInvariant() : " " + asset.Id + " ";
        var body = Payload(WithAsset(Project(fixture.Primary.Id), slot, missing));
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissing(fixture, before, reply, [LocationFor(slot, missing, fixture.Primary.Id)]);
    }

    [Theory]
    [InlineData("builtin-chill", "musicAssetId", "music")]
    [InlineData("builtin-upbeat", "musicAssetId", "music")]
    [InlineData("builtin-suspense", "musicAssetId", "music")]
    [InlineData("builtin-chime", "effectAssetId", "effect")]
    [InlineData("builtin-pop", "effectAssetId", "effect")]
    [InlineData("builtin-whoosh", "effectAssetId", "effect")]
    [InlineData("builtin-riser", "effectAssetId", "effect")]
    public async Task Put_ActualBuiltinRowIsValidEvenOffline(string id, string slot, string kind)
    {
        await using var fixture = new Fixture(output);
        var asset = fixture.RegisterAsset(id, kind, builtIn: true);
        Assert.True(asset.BuiltIn);
        Assert.False(File.Exists(asset.Path));
        var body = Payload(WithAsset(Project(fixture.Primary.Id), slot, id));
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertSaved(fixture, before, reply, body);
    }

    [Theory]
    [InlineData("musicAssetId", "builtin-chill")]
    [InlineData("effectAssetId", "builtin-pop")]
    [InlineData("timeline.stickerAssetId", "builtin-not-registered-sticker")]
    [InlineData("timeline.soundAssetId", "builtin-not-registered-sound")]
    public async Task Put_BuiltinPrefixWithoutRegistryRow_IsStillMissing(string slot, string id)
    {
        await using var fixture = new Fixture(output);
        Assert.DoesNotContain(fixture.Studio.Assets(), asset => asset.Id == id);
        var body = Payload(WithAsset(Project(fixture.Primary.Id), slot, id));
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissing(fixture, before, reply, [LocationFor(slot, id, fixture.Primary.Id)]);
    }

    [Fact]
    public async Task Put_AssetRegistryRowDoesNotSatisfyClipReference()
    {
        await using var fixture = new Fixture(output);
        var asset = fixture.RegisterAsset("Asset-Only-Not-A-Clip", "music");
        Assert.DoesNotContain(fixture.Store.Clips(), clip => clip.Id == asset.Id);
        var body = Payload(new EditorProject { End = 2, Timeline = [new("asset-as-clip", asset.Id, 0, 2)] });
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissing(fixture, before, reply, [new(asset.Id, "asset-as-clip", "$.timeline[0].clipId")]);
    }

    [Theory]
    [InlineData("timeline")]
    [InlineData("silenceTrimBackup")]
    [InlineData("nestedSequences")]
    [InlineData("sourceTranscripts")]
    [InlineData("captionOverrides")]
    public async Task Put_KnownClipReferenceAddsCanonicalEscapedLocationWithoutChangingEnvelope(string position)
    {
        await using var fixture = new Fixture(output);
        const string missing = "Missing.Clip['\\\"雪]";
        var project = Project(fixture.Primary.Id);
        var location = new Location(missing, "location-item", "$." + position + "[0].clipId");
        switch (position)
        {
            case "timeline": project = project with { Timeline = [new("location-item", missing, 0, 2)] }; break;
            case "silenceTrimBackup": project = project with { SilenceTrimBackup = [new("location-item", missing, 0, 2)] }; break;
            case "nestedSequences":
                project = project with { NestedSequences = new() { [SequenceKey] = new("unused-sequence", "Unused", [new("location-item", missing, 0, 2)]) } };
                location = location with { FieldPath = "$.nestedSequences" + Key(SequenceKey) + ".timeline[0].clipId" };
                break;
            case "sourceTranscripts":
                project = project with { SourceTranscripts = new() { [missing] = [] } };
                location = new(missing, missing, "$.sourceTranscripts" + Key(missing));
                break;
            case "captionOverrides":
                project = project with { CaptionOverrides = new() { [missing] = [] } };
                location = new(missing, missing, "$.captionOverrides" + Key(missing));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(position));
        }
        var body = Payload(project);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissing(fixture, before, reply, [location]);
    }

    [Theory]
    [InlineData("unused")]
    [InlineData("deep")]
    public async Task Put_StoredAssetsInUnusedAndDeepNestedSequences_AreNotSkipped(string placement)
    {
        await using var fixture = new Fixture(output);
        var project = Project(fixture.Primary.Id) with
        {
            NestedSequences = new()
            {
                [SequenceKey] = new("leaf-sequence", "Leaf", [new("leaf-item", fixture.Primary.Id, 0, 2, StickerAssetId: "Missing-Leaf-Sticker", SoundAssetId: "Missing-Leaf-Sound")])
            }
        };
        if (placement == "deep")
            project = project with
            {
                Timeline = [new("root-container", "", 0, 2, NestedSequenceId: "level-one")],
                NestedSequences = new(project.NestedSequences!)
                {
                    ["level-one"] = new("level-one", "Level one", [new("middle-container", "", 0, 2, NestedSequenceId: "level-two")]),
                    ["level-two"] = new("level-two", "Level two", [new("leaf-container", "", 0, 2, NestedSequenceId: "leaf-sequence")])
                }
            };
        var body = Payload(project);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissing(fixture, before, reply,
        [
            new("Missing-Leaf-Sticker", "leaf-item", "$.nestedSequences" + Key(SequenceKey) + ".timeline[0].stickerAssetId"),
            new("Missing-Leaf-Sound", "leaf-item", "$.nestedSequences" + Key(SequenceKey) + ".timeline[0].soundAssetId")
        ]);
    }

    [Fact]
    public async Task Put_DisabledHiddenItemsRetainBothExplicitAssetReferences()
    {
        await using var fixture = new Fixture(output);
        var project = Project(fixture.Primary.Id) with
        {
            Timeline =
            [
                new("visible", fixture.Primary.Id, 0, 2),
                new("disabled-hidden", fixture.Primary.Id, 0, 2, StickerAssetId: "Missing-Hidden-Sticker", SoundAssetId: "Missing-Hidden-Sound", Track: 2, Disabled: true)
            ],
            HiddenTracks = [2], TrackOpacities = [1, 0, 1]
        };
        var body = Payload(project);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertMissing(fixture, before, reply,
        [
            new("Missing-Hidden-Sticker", "disabled-hidden", "$.timeline[1].stickerAssetId"),
            new("Missing-Hidden-Sound", "disabled-hidden", "$.timeline[1].soundAssetId")
        ]);
    }

    [Fact]
    public async Task Put_MixedClipAndAssetFailures_KeepUniqueIdsAndEveryOccurrenceLocation()
    {
        await using var fixture = new Fixture(output);
        const string shared = "Missing-Shared";
        const string other = "Missing-Other";
        var project = new EditorProject
        {
            End = 2, MusicAssetId = shared, EffectAssetId = other,
            Timeline =
            [
                new("root-first", shared, 0, 2, StickerAssetId: shared, SoundAssetId: other),
                new("root-repeat", shared, 0, 2, StickerAssetId: shared, SoundAssetId: shared)
            ],
            SilenceTrimBackup = [new("backup-repeat", shared, 0, 2, StickerAssetId: shared, SoundAssetId: shared)],
            NestedSequences = new() { [SequenceKey] = new("unused-sequence", "Unused", [new("nested-repeat", shared, 0, 2, StickerAssetId: shared, SoundAssetId: shared)]) },
            SourceTranscripts = new() { [shared] = [], [other] = [] },
            CaptionOverrides = new() { [shared] = [] }
        };
        Location[] locations =
        [
            new(shared, fixture.Primary.Id, "$.musicAssetId"), new(other, fixture.Primary.Id, "$.effectAssetId"),
            new(shared, "root-first", "$.timeline[0].clipId"), new(shared, "root-first", "$.timeline[0].stickerAssetId"), new(other, "root-first", "$.timeline[0].soundAssetId"),
            new(shared, "root-repeat", "$.timeline[1].clipId"), new(shared, "root-repeat", "$.timeline[1].stickerAssetId"), new(shared, "root-repeat", "$.timeline[1].soundAssetId"),
            new(shared, "backup-repeat", "$.silenceTrimBackup[0].clipId"), new(shared, "backup-repeat", "$.silenceTrimBackup[0].stickerAssetId"), new(shared, "backup-repeat", "$.silenceTrimBackup[0].soundAssetId"),
            new(shared, "nested-repeat", "$.nestedSequences" + Key(SequenceKey) + ".timeline[0].clipId"),
            new(shared, "nested-repeat", "$.nestedSequences" + Key(SequenceKey) + ".timeline[0].stickerAssetId"),
            new(shared, "nested-repeat", "$.nestedSequences" + Key(SequenceKey) + ".timeline[0].soundAssetId"),
            new(shared, shared, "$.sourceTranscripts" + Key(shared)), new(other, other, "$.sourceTranscripts" + Key(other)),
            new(shared, shared, "$.captionOverrides" + Key(shared))
        ];
        var body = Payload(project);
        var before = fixture.Capture();

        var first = await fixture.PutAsync(body);
        AssertMissing(fixture, before, first, locations);
        var second = await fixture.PutAsync(body);
        AssertMissing(fixture, before, second, locations);
        Assert.Equal(Missing(first.Body), Missing(second.Body));
    }

    [Fact]
    public async Task Put_OpaqueEntityAndEffectKernelIds_AreNotRegistryReferences()
    {
        await using var fixture = new Fixture(output);
        const string decoy = "Unknown-Not-Media";
        var project = Project(fixture.Primary.Id) with
        {
            Timeline = [new(decoy, fixture.Primary.Id, 0, 2, LinkGroup: decoy, Effects: [new(decoy, "compressor")])],
            NestedSequences = new() { [decoy] = new(decoy, decoy, [new(decoy, fixture.Primary.Id, 0, 2)]) }
        };
        var node = JsonSerializer.SerializeToNode(project, Web)!.AsObject();
        node["replicaExtension"] = new JsonObject
        {
            ["version"] = 1, ["codecVersion"] = 1,
            ["document"] = new JsonObject { ["optionalMetadata"] = new JsonObject { ["clipId"] = decoy, ["musicAssetId"] = decoy } },
            ["exactTimeMap"] = new JsonObject { ["byClipId"] = new JsonObject { [decoy] = new JsonObject() } },
            ["compatibility"] = new JsonObject { ["optionalSourceId"] = decoy },
            ["opaque"] = new JsonObject { ["sourceId"] = decoy, ["stickerAssetId"] = decoy, ["soundAssetId"] = decoy, ["effectAssetId"] = decoy }
        };
        var body = node.ToJsonString();
        ValidatePayload(body);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        AssertSaved(fixture, before, reply, body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Put_MatchingActiveSession_RejectedMediaDoesNotWriteOrConsumeHistory(bool missingAsset)
    {
        await using var fixture = new Fixture(output);
        var session = await fixture.BeginSessionAsync();
        var project = missingAsset
            ? Project(fixture.Primary.Id) with { MusicAssetId = "Missing-Session-Asset" }
            : Project("Missing-Session-Clip");
        var location = missingAsset
            ? new Location("Missing-Session-Asset", fixture.Primary.Id, "$.musicAssetId")
            : new Location("Missing-Session-Clip", "root-item", "$.timeline[0].clipId");
        var body = Payload(project);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body, session.SessionId);
        var ended = EditorRevisionStore.EndSession(fixture.Primary.Id, session.SessionId);

        Assert.Equal(session.CheckpointRevision, ended.CheckpointRevision);
        AssertMissing(fixture, before, reply, [location]);
    }

    [Fact]
    public async Task Put_MissingIds_AreListedInFirstSeenTraversalOrderNotSortedOrder()
    {
        await using var fixture = new Fixture(output);
        var project = new EditorProject
        {
            End = 2, MusicAssetId = "Z-Music", EffectAssetId = "A-Effect",
            Timeline =
            [
                new("t0", "M-Clip", 0, 2, StickerAssetId: "B-Sticker", SoundAssetId: "Z-Music"),
                new("t1", "A-Clip", 0, 2, StickerAssetId: "B-Sticker")
            ]
        };
        var body = Payload(project);
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(body);

        Assert.Equal(["Z-Music", "A-Effect", "M-Clip", "B-Sticker", "A-Clip"], Missing(reply.Body));
        Assert.Equal(Missing(reply.Body), Locations(reply.Body).Select(l => l.MediaId).Distinct(StringComparer.Ordinal).ToArray());
        AssertUnchanged(before, fixture.Capture());
    }

    [Fact]
    public async Task Put_SameIdIsCheckedIndependentlyInClipAndAssetNamespaces()
    {
        await using var fixture = new Fixture(output);
        const string id = "Shared-Namespace-Id";
        fixture.RegisterClip(id);
        var project = Project(id) with { MusicAssetId = id };
        var before = fixture.Capture();

        var reply = await fixture.PutAsync(Payload(project));

        AssertMissing(fixture, before, reply, [new Location(id, fixture.Primary.Id, "$.musicAssetId")]);
    }

    [Fact]
    public async Task Put_MatchingSessionAfterPriorWrite_RejectedMediaStillLeavesWrittenStateUntouched()
    {
        await using var fixture = new Fixture(output);
        var session = await fixture.BeginSessionAsync();
        var written = await fixture.PutAsync(Payload(Project(fixture.Primary.Id)), session.SessionId);
        Assert.Equal(StatusCodes.Status200OK, written.Status);
        var mid = fixture.Capture();
        var body = Payload(Project(fixture.Primary.Id) with { EffectAssetId = "Missing-After-Write" });

        var reply = await fixture.PutAsync(body, session.SessionId);

        AssertMissing(fixture, mid, reply, [new Location("Missing-After-Write", fixture.Primary.Id, "$.effectAssetId")]);
        EditorRevisionStore.EndSession(fixture.Primary.Id, session.SessionId);
    }

    private static EditorProject Project(string clipId) => new() { End = 2, Timeline = [new("root-item", clipId, 0, 2)] };
    private static string KindFor(string slot) => slot == "musicAssetId" ? "music" : slot.EndsWith("stickerAssetId", StringComparison.Ordinal) ? "sticker" : "effect";
    private static string Key(string value) => "[" + JsonSerializer.Serialize(value, PathStrings) + "]";

    private static EditorProject WithAsset(EditorProject project, string slot, string? id)
    {
        if (slot == "musicAssetId") return project with { MusicAssetId = id };
        if (slot == "effectAssetId") return project with { EffectAssetId = id };
        var parts = slot.Split('.');
        var item = parts[0] switch
        {
            "timeline" => project.Timeline![0],
            "silenceTrimBackup" => project.SilenceTrimBackup?[0] ?? new("backup-item", project.Timeline![0].ClipId, 0, 2),
            "nestedSequences" => project.NestedSequences?.GetValueOrDefault(SequenceKey)?.Timeline[0] ?? new("nested-item", project.Timeline![0].ClipId, 0, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
        item = parts[1] == "stickerAssetId" ? item with { StickerAssetId = id } : item with { SoundAssetId = id };
        return parts[0] switch
        {
            "timeline" => project with { Timeline = [item] },
            "silenceTrimBackup" => project with { SilenceTrimBackup = [item] },
            "nestedSequences" => project with { NestedSequences = new() { [SequenceKey] = new("unused-sequence", "Unused", [item]) } },
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private static Location LocationFor(string slot, string id, string projectId)
    {
        if (slot is "musicAssetId" or "effectAssetId") return new(id, projectId, "$." + slot);
        var parts = slot.Split('.');
        return parts[0] switch
        {
            "timeline" => new(id, "root-item", "$.timeline[0]." + parts[1]),
            "silenceTrimBackup" => new(id, "backup-item", "$.silenceTrimBackup[0]." + parts[1]),
            "nestedSequences" => new(id, "nested-item", "$.nestedSequences" + Key(SequenceKey) + ".timeline[0]." + parts[1]),
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private static string Payload(EditorProject project)
    {
        var body = JsonSerializer.Serialize(project, Web);
        ValidatePayload(body);
        return body;
    }

    private static void ValidatePayload(string body)
    {
        var decoded = EditorProjectCodec.Deserialize(body);
        EditorRenderService.Validate(decoded);
        if (decoded.SilenceTrimBackup is { Length: > 0 } backup)
            EditorRenderService.Validate(decoded with { Timeline = backup, Transitions = null });
        foreach (var sequence in decoded.NestedSequences?.Values ?? Enumerable.Empty<NestedSequence>())
            EditorRenderService.Validate(decoded with { Timeline = sequence.Timeline, Transitions = sequence.Transitions });
        EditorRenderService.Validate(EditorRenderService.FlattenProject(decoded));
    }

    private static string? Code(JsonElement body) => body.TryGetProperty("code", out var code) ? code.GetString() : null;
    private static string? NestedCode(JsonElement body) => body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("code", out var code) ? code.GetString() : null;
    private static long? Revision(JsonElement body) => body.TryGetProperty("revision", out var revision) && revision.TryGetInt64(out var value) ? value : null;
    private static string[] Missing(JsonElement body) => body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.Array
        ? missing.EnumerateArray().Select(id => id.GetString()!).ToArray() : [];
    private static Location[] Locations(JsonElement body) => body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array
        ? locations.EnumerateArray().Select(location => new Location(location.GetProperty("mediaId").GetString()!, location.GetProperty("entityId").GetString()!, location.GetProperty("fieldPath").GetString()!)).ToArray() : [];

    private static void AssertMissing(Fixture fixture, Snapshot before, Reply reply, Location[] expectedLocations)
    {
        var actual = Missing(reply.Body);
        var expectedIds = expectedLocations.Select(location => location.MediaId).Distinct(StringComparer.Ordinal);
        var actualLocations = Locations(reply.Body);
        Assert.Multiple(
            () => Assert.Equal(StatusCodes.Status400BadRequest, reply.Status),
            () => Assert.Equal("missing_media", Code(reply.Body)),
            () => Assert.Equal("missing_media", NestedCode(reply.Body)),
            () => Assert.True(reply.Body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString())),
            () => Assert.Equal(before.Revision, Revision(reply.Body)),
            () => Assert.Equal(expectedIds.OrderBy(id => id, StringComparer.Ordinal), actual.OrderBy(id => id, StringComparer.Ordinal)),
            () => Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count()),
            () => Assert.Equal(expectedLocations.OrderBy(location => location.FieldPath, StringComparer.Ordinal), actualLocations.OrderBy(location => location.FieldPath, StringComparer.Ordinal)),
            () => Assert.Equal(before.Epoch, reply.Epoch),
            () => AssertUnchanged(before, fixture.Capture()));
    }

    private static void AssertUnchanged(Snapshot before, Snapshot after)
    {
        Assert.Multiple(
            () => Assert.Equal(before.Project, after.Project),
            () => Assert.Equal(before.Revision, after.Revision),
            () => Assert.Equal(before.BoundRevision, after.BoundRevision),
            () => Assert.Equal(before.Epoch, after.Epoch),
            () => Assert.Equal(before.Backups, after.Backups),
            () => Assert.Equal(before.BackupCount, after.BackupCount),
            () => Assert.Equal(before.Undo, after.Undo),
            () => Assert.Equal(before.Redo, after.Redo),
            () => Assert.Equal(before.Jobs, after.Jobs),
            () => Assert.Equal(before.Renders, after.Renders),
            () => Assert.Equal(before.Assets, after.Assets),
            () => Assert.Equal(before.Clips, after.Clips));
    }

    private static void AssertSaved(Fixture fixture, Snapshot before, Reply reply, string submitted)
    {
        Assert.Equal(StatusCodes.Status200OK, reply.Status);
        Assert.True(reply.Body.GetProperty("saved").GetBoolean());
        Assert.Equal(before.Revision + 1, Revision(reply.Body));
        Assert.Equal(before.Epoch, reply.Epoch);
        var after = fixture.Capture();
        var expected = EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(EditorProjectCodec.Deserialize(submitted)));
        Assert.Multiple(
            () => Assert.Equal(expected, after.Project!.Json),
            () => Assert.Equal(before.Revision + 1, after.Revision),
            () => Assert.Equal(before.BoundRevision + 1, after.BoundRevision),
            () => Assert.Equal(before.Epoch, after.Epoch),
            () => Assert.Equal(before.Undo + 1, after.Undo),
            () => Assert.Equal(0, after.Redo),
            () => Assert.Equal(before.BackupCount + 1, after.BackupCount),
            () => Assert.Equal(before.Jobs, after.Jobs),
            () => Assert.Equal(before.Renders, after.Renders),
            () => Assert.Equal(before.Assets, after.Assets),
            () => Assert.Equal(before.Clips, after.Clips));
        Assert.Equal(after.Project, new StudioStore(fixture.Store.DatabasePath).GetProjectRecord(fixture.Primary.Id));
        Assert.Contains(fixture.Studio.ProjectRevisions(fixture.Primary.Id), backup => fixture.Studio.ProjectRevision(fixture.Primary.Id, backup.FileName) == before.Project!.Json);
    }

    private sealed record Location(string MediaId, string EntityId, string FieldPath);
    private sealed record Reply(int Status, JsonElement Body, string Epoch);
    private sealed record Snapshot(StoredEditorProject? Project, long Revision, int BoundRevision, string Epoch, string Backups, int BackupCount,
        int Undo, int Redo, string Jobs, string Renders, string Assets, string Clips);

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
            _root = Path.Combine(repository, "docs", "merge", "_work", "r82", "PUT-ASSET-MEDIA-TESTS", "fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _output.WriteLine("New isolated SQLite fixture (retained): " + _root);
            Store = new Store(Path.Combine(_root, "test.db"));
            Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath);
            Studio.Migrate();
            Primary = RegisterClip("Primary-" + Guid.NewGuid().ToString("N"));
            Studio.SaveProject(Primary.Id, JsonSerializer.Serialize(new EditorProject { End = 8 }));
            Studio.SaveProject(Primary.Id, JsonSerializer.Serialize(new EditorProject { End = 7 }));
            EditorRevisionStore.Current(Studio, Primary.Id);
            EditorRevisionStore.EpochFor(Studio);
            EditorRevisionStore.PushSnapshot(Primary.Id, JsonSerializer.Serialize(new EditorProject { End = 9 }));
            EditorRevisionStore.PushRedo(Primary.Id, JsonSerializer.Serialize(new EditorProject { End = 6 }));
            Store.SaveJob(new("sentinel-job", "editor-render", Primary.EventId, "completed", 1, null, Message: "retained before PUT"));
            Studio.SaveRender(new("sentinel-render", Primary.Id, Path.Combine(_root, "offline-render.mp4"), Studio.Project(Primary.Id)!, DateTimeOffset.UtcNow,
                EditorRevisionStore.Current(Primary.Id), EditorRevisionStore.EpochFor(Studio)));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            StudioEndpoints.Map(_app, Store, Studio, null!, null!, null!, null!, null!, null!, null!, new LiveSettings { StorageRoot = _root });
        }

        public Clip RegisterClip(string id)
        {
            var clip = new Clip(id, "event-" + id, Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mp4"), null, DateTimeOffset.UtcNow);
            Store.SaveClip(clip);
            return clip;
        }

        public AudioAsset RegisterAsset(string id, string kind, bool builtIn = false)
        {
            var asset = new AudioAsset(id, id, kind, "test", Path.Combine(_root, Guid.NewGuid().ToString("N") + ".offline"), builtIn);
            Studio.SaveAsset(asset);
            return asset;
        }

        public Snapshot Capture()
        {
            var backups = Studio.ProjectRevisions(Primary.Id);
            var contents = backups.Select(backup => new { Metadata = backup, Json = Studio.ProjectRevision(Primary.Id, backup.FileName) }).ToArray();
            return new(Studio.GetProjectRecord(Primary.Id), Studio.GetProjectRevision(Primary.Id), EditorRevisionStore.Current(Primary.Id),
                EditorRevisionStore.EpochFor(Studio), JsonSerializer.Serialize(contents), backups.Count,
                EditorRevisionStore.SnapshotDepth(Primary.Id), EditorRevisionStore.RedoDepth(Primary.Id),
                JsonSerializer.Serialize(Store.Jobs()), JsonSerializer.Serialize(Studio.Renders()), JsonSerializer.Serialize(Studio.Assets()), JsonSerializer.Serialize(Store.Clips()));
        }

        public async Task<EditorSession> BeginSessionAsync()
        {
            var reply = await InvokeAsync("POST", SessionRoute, null, null);
            Assert.Equal(StatusCodes.Status200OK, reply.Status);
            return new(reply.Body.GetProperty("sessionId").GetString()!, reply.Body.GetProperty("checkpointRevision").GetInt32());
        }

        public Task<Reply> PutAsync(string body, string? sessionId = null) => InvokeAsync("PUT", EditorRoute, body, sessionId);

        private async Task<Reply> InvokeAsync(string method, string route, string? body, string? sessionId)
        {
            var before = Capture();
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                candidate => candidate.RoutePattern.RawText == route && candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = method;
            context.Request.Path = route.Replace("{id}", Primary.Id, StringComparison.Ordinal);
            context.Request.RouteValues["id"] = Primary.Id;
            context.Request.Headers["X-Base-Revision"] = before.Revision.ToString(CultureInfo.InvariantCulture);
            context.Request.Headers["X-Editor-Epoch"] = before.Epoch;
            if (sessionId is not null) context.Request.Headers["X-Session-Id"] = sessionId;
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            await using var input = new MemoryStream(bytes);
            context.Request.Body = input;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
            await using var response = new MemoryStream();
            context.Response.Body = response;
            _output.WriteLine(method + " " + route + " X-Base-Revision=" + before.Revision + " X-Editor-Epoch=" + before.Epoch + " X-Session-Id=" + sessionId);
            if (body is not null) _output.WriteLine("PREREQUISITE codec/render valid; SUBMITTED " + body);
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
        }
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
