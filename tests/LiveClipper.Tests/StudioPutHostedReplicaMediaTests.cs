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
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class StudioPutHostedReplicaMediaTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string EditorRoute = "/api/studio/clips/{id}/editor";
    private const string SessionRoute = "/api/studio/clips/{id}/sessions";
    private const string P = "$.replicaExtension.document";
    private const string Projection = "lc-editor-next-active-sequence-v1";

    public static IEnumerable<object[]> MissingCases()
    {
        yield return ["R1-active-video", """{"sequence":{"videoTracks":[{"clips":[{"key":"v","sourceId":"gone"}]}]}}""",
            new Location[] { new("gone", "v", P + ".sequence.videoTracks[0].clips[0].sourceId") }];
        yield return ["R2-audio-clip-namespace", """{"sequence":{"audioTracks":[{"clips":[{"key":"a","sourceId":"asset-only"}]}]}}""",
            new Location[] { new("asset-only", "a", P + ".sequence.audioTracks[0].clips[0].sourceId") }];
        yield return ["R3-nested-video", """{"sequence":{"videoTracks":[{"clips":[{"key":"n","nestedSeq":{"videoTracks":[{"clips":[{"key":"c","sourceId":"gone"}]}]}}]}]}}""",
            new Location[] { new("gone", "c", P + ".sequence.videoTracks[0].clips[0].nestedSeq.videoTracks[0].clips[0].sourceId") }];
        yield return ["R4-deep-hidden-audio", """{"sequence":{"videoTracks":[{"clips":[{"nestedSeq":{"videoTracks":[{"clips":[{"nestedSeq":{"audioTracks":[{"hidden":true,"enabled":false,"clips":[{"key":"a","sourceId":"gone"}]}]}}]}]}}]}]}}""",
            new Location[] { new("gone", "a", P + ".sequence.videoTracks[0].clips[0].nestedSeq.videoTracks[0].clips[0].nestedSeq.audioTracks[0].clips[0].sourceId") }];
        yield return ["R5-inactive-angle-snapshot", """{"sequence":{"videoTracks":[{"clips":[{"key":"p","sourceId":"ok","multicam":{"angle":0,"cuts":[{"at":0,"angle":0}],"angles":[{"clip":{"sourceId":"ok"}},{"clip":{"key":"cam","sourceId":"gone"}}]}}]}]}}""",
            new Location[] { new("gone", "cam", P + ".sequence.videoTracks[0].clips[0].multicam.angles[1].clip.sourceId") }];
        yield return ["R6-angle-clips", """{"sequence":{"videoTracks":[{"clips":[{"multicam":{"angles":[{"clips":[{"key":"c","sourceId":"gone"}]}]}}]}]}}""",
            new Location[] { new("gone", "c", P + ".sequence.videoTracks[0].clips[0].multicam.angles[0].clips[0].sourceId") }];
        yield return ["R7-nested-in-angle", """{"sequence":{"videoTracks":[{"clips":[{"multicam":{"angles":[{"clips":[{"nestedSeq":{"audioTracks":[{"clips":[{"key":"c","sourceId":"gone"}]}]}}]}]}}]}]}}""",
            new Location[] { new("gone", "c", P + ".sequence.videoTracks[0].clips[0].multicam.angles[0].clips[0].nestedSeq.audioTracks[0].clips[0].sourceId") }];
        yield return ["R8-deduplicate-id-not-locations", """{"sequence":{"videoTracks":[{"clips":[{"key":"v1","sourceId":"gone"},{"key":"v2","sourceId":"gone"}]}]}}""",
            new Location[] { new("gone", "v1", P + ".sequence.videoTracks[0].clips[0].sourceId"), new("gone", "v2", P + ".sequence.videoTracks[0].clips[1].sourceId") }];
        yield return ["R9-no-trim", """{"sequence":{"videoTracks":[{"clips":[{"key":"v","sourceId":" ok "}]}]}}""",
            new Location[] { new(" ok ", "v", P + ".sequence.videoTracks[0].clips[0].sourceId") }];
        yield return ["R10-equivalent-mirror", """{"sequence":{"videoTracks":[{"clips":[{"key":"v","sourceId":"gone"}]}]},"projectState":{"sequence":{"videoTracks":[{"clips":[{"key":"v","sourceId":"gone"}]}]}}}""",
            new Location[] { new("gone", "v", P + ".sequence.videoTracks[0].clips[0].sourceId"), new("gone", "v", P + ".projectState.sequence.videoTracks[0].clips[0].sourceId") }];
        yield return ["R11-archive-host-music", """{"sequence":{"hostMusic":{"musicAssetId":"music-gone","musicVolume":0.25,"duckMusic":false}}}""",
            new Location[] { new("music-gone", "$project", P + ".sequence.hostMusic.musicAssetId") }];
        yield return ["R12-no-key", """{"sequence":{"videoTracks":[{"clips":[{"sourceId":"gone"}]}]}}""",
            new Location[] { new("gone", "$project", P + ".sequence.videoTracks[0].clips[0].sourceId") }];
    }

    [Theory]
    [MemberData(nameof(MissingCases))]
    public async Task Put_ExplicitHostedReference_ReturnsMissingMediaWithoutWrites(string caseName, string fragment, Location[] locations)
    {
        output.WriteLine(caseName);
        await using var fixture = new Fixture(output);
        var body = Payload(Document(fragment));
        var expected = locations.Select(location => location.EntityId == "$project" ? location with { EntityId = fixture.Primary.Id } : location).ToArray();
        var session = await fixture.BeginSessionAsync();
        var before = fixture.Capture();

        var first = await fixture.PutAsync(body, session.SessionId);
        AssertMissing(fixture, before, first, expected);
        var retry = await fixture.PutAsync(body, session.SessionId);
        AssertMissing(fixture, before, retry, expected);
        var ended = EditorRevisionStore.EndSession(fixture.Primary.Id, session.SessionId);
        Assert.Equal(session.CheckpointRevision, ended.CheckpointRevision);
        AssertUnchanged(before, fixture.Capture());
    }

    [Fact]
    public async Task Put_G1_OpaqueEntityAndEffectKernelIds_AreNotRegistryReferences()
    {
        const string decoy = "gone";
        var project = Project() with
        {
            Timeline = [new(decoy, "ok", 0, 2, LinkGroup: decoy, Effects: [new(decoy, "compressor")])],
            NestedSequences = new() { [decoy] = new(decoy, decoy, [new(decoy, "ok", 0, 2)]) }
        };
        var document = Document("""{"optionalMetadata":{"clipId":"gone","musicAssetId":"gone"}}""");
        var body = Payload(document, project: project, configure: extension =>
        {
            extension["exactTimeMap"] = JsonNode.Parse("""{"byClipId":{"gone":{}}}""");
            extension["compatibility"]!["optionalSourceId"] = decoy;
            extension["opaque"] = JsonNode.Parse("""{"sourceId":"gone","stickerAssetId":"gone","soundAssetId":"gone","effectAssetId":"gone"}""");
        });
        await AssertAcceptedAsync(body);
    }

    [Fact]
    public async Task Put_G2_CatalogEntriesWithGraphDecoys_AreOpaque()
    {
        var document = Document("""{"sequences":[{"id":"gone","name":"S","kind":"main","frames":60,"sourceId":"gone","clips":[{"sourceId":"gone"}],"videoTracks":[{"clips":[{"sourceId":"gone"}]}]}]}""");
        document["projectState"] = new JsonObject { ["sequences"] = document["sequences"]!.DeepClone() };
        await AssertAcceptedAsync(Payload(document));
    }

    [Fact]
    public async Task Put_G3_RuntimeAngleWithoutSourceId_DoesNotInferIdentity()
        => await AssertAcceptedAsync(Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"sourceId":"ok","multicam":{"angles":[{"clip":{"key":"gone","assetId":"host-offline:gone","mediaPath":"gone"}}]}}]}]}}""")));

    [Fact]
    public async Task Put_G4_RegisteredButOfflineClip_IsAccepted()
        => await AssertAcceptedAsync(Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"key":"v","sourceId":"ok","assetId":"host-offline:ok","offline":true}]}]}}""")));

    [Fact]
    public async Task Put_G5_EmptySourcesAndSourcelessContainers_AreAccepted()
        => await AssertAcceptedAsync(Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"nested":true,"nestedSeq":{"videoTracks":[],"audioTracks":[]}},{"kind":"title","sourceId":""},{"sourceId":"  "},{"sourceId":null}]}]}}""")));

    [Fact]
    public async Task Put_G6_UnknownParametersWithSourceIds_AreOpaque()
        => await AssertAcceptedAsync(Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"sourceId":"ok","scene":{"sourceId":"gone"},"effects":[{"id":"gone","sourceId":"gone"}],"graph":{"clips":[{"sourceId":"gone"}]}}]}]},"optionalMetadata":{"sourceId":"gone"}}""")));

    [Fact]
    public async Task Put_G7_LegacyOrForeignProjection_IsOpaque()
    {
        foreach (var projection in new string?[] { null, "foreign-v1" })
            await AssertAcceptedAsync(Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"key":"v","sourceId":"gone"}]}]}}"""), projection));
    }

    [Fact]
    public async Task Put_G8_NonStringAndNonCanonicalCaseSources_AreNotCoerced()
        => await AssertAcceptedAsync(Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"sourceId":42},{"sourceId":{"id":"gone"}},{"SourceId":"gone"}]}]}}""")));

    [Fact]
    public async Task Put_G9_SourceOnlyAlias_IsNotResolved()
        => await AssertAcceptedAsync(Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"key":"v","source":{"sourceId":"gone"}}]}]}}""")));

    [Fact]
    public async Task Put_G10_ConflictingMirror_IsNotAnIndependentGraph()
        => await AssertAcceptedAsync(Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"sourceId":"ok"}]}]},"projectState":{"sequence":{"videoTracks":[{"clips":[{"sourceId":"gone"}]}]}}}""")));

    [Fact]
    public async Task Put_G11_EmptyOrRegisteredOfflineHostMusic_IsAccepted()
    {
        await AssertAcceptedAsync(Payload(Document("""{"sequence":{"hostMusic":{"musicAssetId":null,"musicVolume":0.25,"duckMusic":false}}}""")));
        await AssertAcceptedAsync(Payload(Document("""{"sequence":{"hostMusic":{"musicAssetId":"music-ok","musicVolume":0.25,"duckMusic":false}}}""")));
    }

    [Fact]
    public async Task Put_TypedAndReplicaSharedId_PreservesBothLocations()
    {
        await using var fixture = new Fixture(output);
        var project = Project() with { Timeline = [new("same", "gone", 0, 2)] };
        var body = Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"key":"same","sourceId":"gone"}]}]}}"""), project: project);
        await AssertRejectedAsync(fixture, body,
            new Location("gone", "same", "$.timeline[0].clipId"),
            new Location("gone", "same", P + ".sequence.videoTracks[0].clips[0].sourceId"));
    }

    [Fact]
    public async Task Put_BothAngleFormsAndRecursiveEdges_PreserveDepthFirstOrder()
    {
        await using var fixture = new Fixture(output);
        var document = Document("""
            {"sequence":{"hostMusic":{"musicAssetId":"music-gone"},"videoTracks":[{"clips":[
              {"key":"same","sourceId":"parent-gone",
               "nestedSeq":{"audioTracks":[{"clips":[{"key":"same","sourceId":"nested-gone"}]}]},
               "multicam":{"angles":[
                 {"clip":{"key":"same","sourceId":"snapshot-gone","multicam":{"angles":[{"clip":{"key":"same","sourceId":"recursive-gone"}}]}},
                  "clips":[{"key":"same","sourceId":"list-gone"}]},
                 {"clip":{"key":"same","sourceId":"angle-two-gone"}}]}},
              {"key":"same","sourceId":"video-two-gone"}]}],
             "audioTracks":[{"clips":[{"key":"same","sourceId":"audio-gone"}]}]}}
            """);
        var clip = P + ".sequence.videoTracks[0].clips[0]";
        await AssertRejectedAsync(fixture, Payload(document),
            new Location("music-gone", fixture.Primary.Id, P + ".sequence.hostMusic.musicAssetId"),
            new Location("parent-gone", "same", clip + ".sourceId"),
            new Location("nested-gone", "same", clip + ".nestedSeq.audioTracks[0].clips[0].sourceId"),
            new Location("snapshot-gone", "same", clip + ".multicam.angles[0].clip.sourceId"),
            new Location("recursive-gone", "same", clip + ".multicam.angles[0].clip.multicam.angles[0].clip.sourceId"),
            new Location("list-gone", "same", clip + ".multicam.angles[0].clips[0].sourceId"),
            new Location("angle-two-gone", "same", clip + ".multicam.angles[1].clip.sourceId"),
            new Location("video-two-gone", "same", P + ".sequence.videoTracks[0].clips[1].sourceId"),
            new Location("audio-gone", "same", P + ".sequence.audioTracks[0].clips[0].sourceId"));
    }

    [Fact]
    public async Task Put_UnchangedReplica_DoesNotBypassBackupOrTypedNestedChecks()
    {
        foreach (var projection in new string?[] { Projection, null, "foreign-v1" })
        {
            await using var fixture = new Fixture(output);
            var project = Project() with
            {
                SilenceTrimBackup = [new("backup", "gone", 0, 2)],
                NestedSequences = new() { ["unused"] = new("unused", "Unused", [new("nested", "gone", 0, 2)]) }
            };
            var body = Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"sourceId":"ok"}]}]}}"""), projection, project);
            await AssertRejectedAsync(fixture, body,
                new Location("gone", "backup", "$.silenceTrimBackup[0].clipId"),
                new Location("gone", "nested", "$.nestedSequences[\"unused\"].timeline[0].clipId"));
        }
    }

    [Fact]
    public async Task Put_HostMusicEffectOnlyRow_DoesNotSatisfyMusicNamespace()
    {
        await using var fixture = new Fixture(output);
        fixture.RegisterAsset("music-gone", "effect");
        await AssertRejectedAsync(fixture, Payload(Document("""{"sequence":{"hostMusic":{"musicAssetId":"music-gone","musicVolume":0.25,"duckMusic":false}}}""")),
            new Location("music-gone", fixture.Primary.Id, P + ".sequence.hostMusic.musicAssetId"));
    }

    [Fact]
    public async Task Put_OnlyConsistentKnownWrappers_OptIntoHostNamespace()
    {
        foreach (var wrapper in new[]
        {
            "{}", """{"__fcproj":2}""", """{"__fcproj":"1"}""", """{"__fcproj":null}""",
            """{"format":"filmcraft-project"}""", """{"format":"filmcraft-project","version":"1"}""",
            """{"format":"foreign-project","version":1}""", """{"__fcproj":1,"version":2}""",
            """{"__fcproj":1,"format":"filmcraft-project"}""", """{"__fcproj":1,"format":"foreign-project","version":1}""",
            """{"__fcproj":1,"bridgeSchema":3}""", """{"__fcproj":1,"bridgeSchema":"2"}"""
        })
            await AssertAcceptedAsync(Payload(WithWrapper(wrapper)));
        foreach (var wrapper in new[]
        {
            """{"__fcproj":1}""", """{"format":"filmcraft-project","version":1}""",
            """{"__fcproj":1,"format":"filmcraft-project","version":1,"bridgeSchema":2}""",
            """{"__fcproj":1.0,"version":1e0,"bridgeSchema":2.0}"""
        })
        {
            await using var fixture = new Fixture(output);
            await AssertRejectedAsync(fixture, Payload(WithWrapper(wrapper)),
                new Location("gone", "v", P + ".sequence.videoTracks[0].clips[0].sourceId"));
        }

        static JsonObject WithWrapper(string wrapper)
        {
            var document = Document("""{"sequence":{"videoTracks":[{"clips":[{"key":"v","sourceId":"gone"}]}]}}""");
            document.Remove("__fcproj");
            foreach (var field in JsonNode.Parse(wrapper)!.AsObject()) document[field.Key] = field.Value?.DeepClone();
            return document;
        }
    }

    [Fact]
    public async Task Put_ReorderedEquivalentMirror_RetainsMusicAndClipLocations()
    {
        await using var fixture = new Fixture(output);
        var document = Document("""
            {"sequence":{"hostMusic":{"musicAssetId":"music-gone","duckMusic":false},"videoTracks":[{"clips":[{"key":"v","sourceId":"gone"}]}]},
             "projectState":{"sequence":{"videoTracks":[{"clips":[{"sourceId":"gone","key":"v"}]}],"hostMusic":{"duckMusic":false,"musicAssetId":"music-gone"}}}}
            """);
        await AssertRejectedAsync(fixture, Payload(document),
            new Location("music-gone", fixture.Primary.Id, P + ".sequence.hostMusic.musicAssetId"),
            new Location("gone", "v", P + ".sequence.videoTracks[0].clips[0].sourceId"),
            new Location("music-gone", fixture.Primary.Id, P + ".projectState.sequence.hostMusic.musicAssetId"),
            new Location("gone", "v", P + ".projectState.sequence.videoTracks[0].clips[0].sourceId"));
    }

    [Fact]
    public async Task Put_UnknownOrMalformedEdges_AndNestedHostMusic_AreOpaque()
    {
        await AssertAcceptedAsync(Payload(Document("""
            {"sequence":{"tracks":[{"clips":[{"sourceId":"gone"}]}],"clips":[{"sourceId":"gone"}],
             "audioTracks":{"clips":[{"sourceId":"gone"}]},"videoTracks":[null,{"clips":{"sourceId":"gone"}},{"clips":[
               {"sourceId":"ok","nestedSeq":{"hostMusic":{"musicAssetId":"music-gone"}},
                "nested":{"id":"gone"},"subseq":{"id":"gone"},"multicam":{"id":"gone","angles":[null,{"clip":42,"clips":{"sourceId":"gone"}}]},
                "gpuFx":{"sourceId":"gone"},"mogrt":{"sourceId":"gone"}},
               {"nestedSeq":[{"sourceId":"gone"}],"multicam":{"angles":{"clip":{"sourceId":"gone"}}}}]}]}}
            """)));
    }

    [Fact]
    public async Task Put_SkippedShapes_PreserveIndicesAndExactEntityKeys()
    {
        await using var fixture = new Fixture(output);
        var document = Document("""
            {"sequence":{"videoTracks":[null,42,{"clips":[null,false,[],
              {"key":"","sourceId":"gone"},{"key":42,"sourceId":"gone"},
              {"key":"  ","sourceId":"gone"},{"key":" padded ","sourceId":"gone"}]}]}}
            """);
        await AssertRejectedAsync(fixture, Payload(document),
            new Location("gone", fixture.Primary.Id, P + ".sequence.videoTracks[2].clips[3].sourceId"),
            new Location("gone", fixture.Primary.Id, P + ".sequence.videoTracks[2].clips[4].sourceId"),
            new Location("gone", "  ", P + ".sequence.videoTracks[2].clips[5].sourceId"),
            new Location("gone", " padded ", P + ".sequence.videoTracks[2].clips[6].sourceId"));
    }

    [Fact]
    public async Task Put_CanonicalSource_WinsWithoutResolvingAliases()
    {
        await using var fixture = new Fixture(output);
        await AssertRejectedAsync(fixture, Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"key":"v","sourceId":"gone","source":{"sourceId":"ok","id":"ok"}}]}]}}""")),
            new Location("gone", "v", P + ".sequence.videoTracks[0].clips[0].sourceId"));
        await AssertAcceptedAsync(Payload(Document("""{"sequence":{"videoTracks":[{"clips":[{"key":"v","sourceId":"ok","source":{"sourceId":"gone","id":"gone"}}]}]}}""")));
    }

    [Fact]
    public async Task Put_NestedGraphBeyondFourLevels_IsNotTruncated()
    {
        await using var fixture = new Fixture(output);
        var sequence = JsonNode.Parse("""{"audioTracks":[{"clips":[{"key":"leaf","sourceId":"gone"}]}]}""")!.AsObject();
        for (var i = 0; i < 6; i++)
            sequence = new JsonObject
            {
                ["videoTracks"] = new JsonArray(new JsonObject { ["clips"] = new JsonArray(new JsonObject { ["nestedSeq"] = sequence }) })
            };
        var document = Document(new JsonObject { ["sequence"] = sequence }.ToJsonString());
        await AssertRejectedAsync(fixture, Payload(document),
            new Location("gone", "leaf", P + ".sequence" + string.Concat(Enumerable.Repeat(".videoTracks[0].clips[0].nestedSeq", 6)) + ".audioTracks[0].clips[0].sourceId"));
    }

    private static async Task AssertRejectedAsync(Fixture fixture, string body, params Location[] expected)
    {
        var session = await fixture.BeginSessionAsync();
        var before = fixture.Capture();
        AssertMissing(fixture, before, await fixture.PutAsync(body, session.SessionId), expected);
        AssertMissing(fixture, before, await fixture.PutAsync(body, session.SessionId), expected);
        var ended = EditorRevisionStore.EndSession(fixture.Primary.Id, session.SessionId);
        Assert.Equal(session.CheckpointRevision, ended.CheckpointRevision);
        AssertUnchanged(before, fixture.Capture());
    }

    private async Task AssertAcceptedAsync(string body)
    {
        await using var fixture = new Fixture(output);
        var before = fixture.Capture();
        var reply = await fixture.PutAsync(body);
        AssertSaved(fixture, before, reply, body);
    }

    private static EditorProject Project() => new() { End = 2, Timeline = [new("typed-item", "ok", 0, 2)] };

    private static JsonObject Document(string fragment)
    {
        var document = JsonNode.Parse(fragment)!.AsObject();
        document["__fcproj"] = 1;
        document["sequence"] ??= new JsonObject();
        CompleteSequence(document["sequence"]!.AsObject());
        if (document["projectState"] is JsonObject state && state["sequence"] is JsonObject mirror)
            CompleteSequence(mirror);
        return document;
    }

    private static void CompleteSequence(JsonObject sequence)
    {
        sequence["name"] ??= "S";
        sequence["fps"] ??= 30;
        sequence["duration"] ??= 60;
        foreach (var kind in new[] { "video", "audio" })
        {
            var name = kind + "Tracks";
            sequence[name] ??= new JsonArray();
            if (sequence[name] is not JsonArray tracks) continue;
            for (var i = 0; i < tracks.Count; i++)
            {
                if (tracks[i] is not JsonObject track) continue;
                track["id"] ??= kind + "-" + i;
                track["name"] ??= kind + " " + i;
                track["kind"] ??= kind;
                track["clips"] ??= new JsonArray();
                if (track["clips"] is JsonArray clips)
                    foreach (var clip in clips.OfType<JsonObject>()) CompleteClip(clip, kind);
            }
        }
    }

    private static void CompleteClip(JsonObject clip, string kind)
    {
        // Do not synthesize key/sourceId: their absence is part of several cases.
        clip["kind"] ??= kind;
        clip["start"] ??= 0;
        clip["end"] ??= 60;
        if (clip["nestedSeq"] is JsonObject nested) CompleteSequence(nested);
        if (clip["multicam"] is not JsonObject multicam || multicam["angles"] is not JsonArray angles) return;
        foreach (var angle in angles.OfType<JsonObject>())
        {
            if (angle["clip"] is JsonObject snapshot) CompleteClip(snapshot, kind);
            if (angle["clips"] is JsonArray clips)
                foreach (var child in clips.OfType<JsonObject>()) CompleteClip(child, kind);
        }
    }

    private static string Payload(JsonObject document, string? projection = Projection, EditorProject? project = null, Action<JsonObject>? configure = null)
    {
        var compatibility = new JsonObject { ["requiredVersion"] = 1, ["requiredCodecVersion"] = 1 };
        if (projection is not null) compatibility["projection"] = projection;
        var extension = new JsonObject
        {
            ["version"] = 1, ["codecVersion"] = 1, ["document"] = document,
            ["exactTimeMap"] = new JsonObject(), ["compatibility"] = compatibility
        };
        configure?.Invoke(extension);
        var node = JsonSerializer.SerializeToNode(project ?? Project(), Web)!.AsObject();
        node["replicaExtension"] = extension;
        var body = node.ToJsonString();
        var decoded = EditorProjectCodec.Deserialize(body);
        EditorRenderService.Validate(decoded);
        EditorRenderService.Validate(EditorRenderService.FlattenProject(decoded));
        return body;
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

    private static void AssertMissing(Fixture fixture, Snapshot before, Reply reply, Location[] expected)
    {
        var actual = Missing(reply.Body);
        Assert.Multiple(
            () => Assert.Equal(StatusCodes.Status400BadRequest, reply.Status),
            () => Assert.Equal("missing_media", Code(reply.Body)),
            () => Assert.Equal("missing_media", NestedCode(reply.Body)),
            () => Assert.True(reply.Body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString())),
            () => Assert.Equal(before.Revision, Revision(reply.Body)),
            () => Assert.Equal(expected.Select(location => location.MediaId).Distinct(StringComparer.Ordinal).ToArray(), actual),
            () => Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count()),
            () => Assert.Equal(expected, Locations(reply.Body)),
            () => Assert.Equal(before.Epoch, reply.Epoch),
            () => AssertUnchanged(before, fixture.Capture()));
    }

    private static void AssertUnchanged(Snapshot before, Snapshot after) => Assert.Equal(before, after);

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

    public sealed record Location(string MediaId, string EntityId, string FieldPath);
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
            _root = Path.Combine(repository, "docs", "merge", "_work", "r82", "B2-2-IMPL", "fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _output.WriteLine("New isolated SQLite fixture (retained): " + _root);
            Store = new Store(Path.Combine(_root, "test.db"));
            Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath);
            Studio.Migrate();
            Primary = RegisterClip("Primary-" + Guid.NewGuid().ToString("N"));
            var source = RegisterClip("ok");
            var assetOnly = RegisterAsset("asset-only", "music");
            var music = RegisterAsset("music-ok", "music");
            Assert.False(File.Exists(source.Path));
            Assert.False(File.Exists(assetOnly.Path));
            Assert.False(File.Exists(music.Path));
            Assert.DoesNotContain(Store.Clips(), clip => clip.Id is "gone" or "asset-only");
            Assert.DoesNotContain(Studio.Assets(), asset => asset.Id == "music-gone");
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

        public AudioAsset RegisterAsset(string id, string kind)
        {
            var asset = new AudioAsset(id, id, kind, "test", Path.Combine(_root, Guid.NewGuid().ToString("N") + ".offline"), false);
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
