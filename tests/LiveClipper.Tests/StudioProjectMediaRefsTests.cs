using System.Text.Json;
using System.Text.Json.Nodes;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class StudioProjectMediaRefsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        ["primary"] = "copy-primary", ["secondary"] = "copy-secondary"
    };

    [Fact]
    public void Remap_CodecProject_MapsOnlySourcesAndDetachesMutableState()
    {
        var project = EditorProjectCodec.Deserialize("""
            {"timeline":[{"id":"primary","clipId":"primary","start":1.25,"end":4.5,"titleText":"primary",
              "eventPlacement":{"version":1,"sourceId":"primary","eventId":"primary"},
              "effects":[{"id":"primary","type":"compressor","params":{"threshold":-12}}],
              "keyframes":{"opacity":[{"time":0,"value":0.5}]}}],
             "silenceTrimBackup":[{"id":"backup","clipId":"secondary","start":0,"end":8}],
             "nestedSequences":{"primary":{"id":"primary","name":"primary","timeline":[{"id":"child","clipId":"secondary","start":2,"end":3}],"transitions":[]}},
             "captionOverrides":{"primary":[{"start":1,"end":2,"text":"primary"}],"secondary":[]},
             "sourceTranscripts":{"secondary":[{"start":0,"end":1,"text":"secondary"}]},
             "timelineCaptions":[{"id":"primary","start":0.5,"end":2,"text":"primary","font":"primary","size":30,"color":"#123456",
                 "sourceClipId":"secondary","sequenceItemId":"primary","sourceCueIndex":2,"sourceStart":3,"sourceEnd":4}],
             "selectedSegments":[2],"markers":[1.5],"musicAssetId":"primary","effectAssetId":"secondary"}
            """);
        var before = JsonSerializer.Serialize(project, Web);

        var copy = StudioProjectMediaRefs.Remap(project, Map);

        Assert.Equal(before, JsonSerializer.Serialize(project, Web));
        Assert.NotSame(project, copy);
        Assert.Equal("copy-primary", copy.Timeline![0].ClipId);
        Assert.Equal("primary", copy.Timeline[0].Id);
        Assert.Equal("primary", copy.Timeline[0].TitleText);
        Assert.Equal("copy-primary", copy.Timeline[0].EventPlacement!.SourceId);
        Assert.Equal("primary", copy.Timeline[0].EventPlacement!.EventId);
        Assert.Equal(1.25, copy.Timeline[0].Start);
        Assert.Equal(4.5, copy.Timeline[0].End);
        Assert.Equal("copy-secondary", copy.SilenceTrimBackup![0].ClipId);
        Assert.Equal("primary", copy.NestedSequences!["primary"].Id);
        Assert.Equal("copy-secondary", copy.NestedSequences["primary"].Timeline[0].ClipId);
        Assert.Equal("primary", copy.CaptionOverrides!["copy-primary"][0].Text);
        Assert.Empty(copy.CaptionOverrides["copy-secondary"]);
        Assert.Equal("secondary", copy.SourceTranscripts!["copy-secondary"][0].Text);
        Assert.Equal(project.TimelineCaptions![0] with { SourceClipId = "copy-secondary" }, copy.TimelineCaptions![0]);
        Assert.Equal("primary", copy.MusicAssetId);
        Assert.Equal("secondary", copy.EffectAssetId);

        copy.Timeline[0].Effects![0].Params!["threshold"] = 99;
        copy.Timeline[0].Keyframes!["opacity"][0] = new(9, 9);
        copy.CaptionOverrides["copy-primary"][0] = new(9, 10, "changed");
        copy.NestedSequences["primary"].Timeline[0] = new("changed", "changed", 0, 1);
        copy.SelectedSegments![0] = 99;
        Assert.Equal(-12d, project.Timeline[0].Effects![0].Params!["threshold"]);
        Assert.Equal(0d, project.Timeline[0].Keyframes!["opacity"][0].Time);
        Assert.Equal("primary", project.CaptionOverrides!["primary"][0].Text);
        Assert.Equal("child", project.NestedSequences!["primary"].Timeline[0].Id);
        Assert.Equal(2, project.SelectedSegments![0]);
        project.Markers![0] = 8;
        Assert.Equal(1.5, copy.Markers![0]);
    }

    [Fact]
    public void FindMissing_CollectsEveryTypedSourceLocationIncludingProvenanceAndClears()
    {
        var project = new EditorProject
        {
            Timeline = [new("item", "primary", 0, 1) { EventPlacement = new() { SourceId = "secondary", EventId = "event" } }],
            SilenceTrimBackup = [new("backup", "secondary", 0, 1)],
            NestedSequences = new() { ["seq"] = new("seq", "S", [new("nested", "primary", 0, 1)]) },
            CaptionOverrides = new() { ["primary"] = [] }, SourceTranscripts = new() { ["secondary"] = [] },
            TimelineCaptions = [new() { Id = "cue", SourceClipId = "secondary" }],
            ClearedEventSelections = [new() { SourceId = "primary" }]
        };
        var (missing, locations) = StudioProjectMediaRefs.FindMissing(project, "project", _ => false, (_, _) => true);
        Assert.Equal(new[] { "primary", "secondary" }, missing);
        Assert.Equal(new[]
        {
            "$.timeline[0].clipId", "$.timeline[0].eventPlacement.sourceId", "$.silenceTrimBackup[0].clipId",
            "$.nestedSequences[\"seq\"].timeline[0].clipId", "$.sourceTranscripts[\"secondary\"]",
            "$.captionOverrides[\"primary\"]", "$.timelineCaptions[0].sourceClipId", "$.clearedEventSelections[0].sourceId"
        }, locations.Select(x => x.FieldPath));
    }

    [Fact]
    public void Remap_ClearedEventSelection_PreservesEmptyTimelineAndSelectionVersion()
    {
        var project = EditorProjectCodec.Deserialize("""{"timeline":[],"clearedEventSelections":[{"version":1,"sourceId":"primary"}]}""");
        var copy = StudioProjectMediaRefs.Remap(project, Map);
        Assert.Empty(copy.Timeline!);
        Assert.Equal(new TimelineEventSelectionClear { Version = 1, SourceId = "copy-primary" }, Assert.Single(copy.ClearedEventSelections!));
        Assert.Equal("primary", project.ClearedEventSelections![0].SourceId);
    }

    [Fact]
    public void Remap_HostedSequenceAndProjectState_MirrorsSourcesAndPreservesOpaqueFields()
    {
        var project = Hosted();
        var original = project.ReplicaExtension!.Json.GetRawText();
        var copy = StudioProjectMediaRefs.Remap(project, Map);
        var document = copy.ReplicaExtension!.Document;
        var sequence = document.GetProperty("sequence");
        Assert.True(JsonElement.DeepEquals(sequence, document.GetProperty("projectState").GetProperty("sequence")));
        Assert.Equal(original, project.ReplicaExtension.Json.GetRawText());
        Assert.NotSame(project.ReplicaExtension, copy.ReplicaExtension);
        var clip = sequence.GetProperty("videoTracks")[0].GetProperty("clips")[0];
        Assert.Equal("copy-primary", clip.GetProperty("sourceId").GetString());
        Assert.Equal("primary", clip.GetProperty("key").GetString());
        Assert.Equal("copy-primary", clip.GetProperty("eventPlacement").GetProperty("sourceId").GetString());
        Assert.Equal("primary", clip.GetProperty("eventPlacement").GetProperty("eventId").GetString());
        Assert.Equal("copy-secondary", clip.GetProperty("nestedSeq").GetProperty("audioTracks")[0].GetProperty("clips")[0].GetProperty("sourceId").GetString());
        var angle = clip.GetProperty("multicam").GetProperty("angles")[0];
        Assert.Equal("copy-secondary", angle.GetProperty("clip").GetProperty("sourceId").GetString());
        Assert.Equal("copy-primary", angle.GetProperty("clips")[0].GetProperty("sourceId").GetString());
        Assert.True(sequence.GetProperty("hostCaptions").TryGetProperty("copy-primary", out var sourceCues));
        Assert.Equal("primary", sourceCues[0].GetProperty("text").GetString());
        Assert.Equal("copy-secondary", sequence.GetProperty("hostEventOriginals")[0].GetProperty("sourceId").GetString());
        Assert.Equal("copy-primary", sequence.GetProperty("hostClearedEventSelections")[0].GetProperty("sourceId").GetString());
        Assert.Equal("copy-secondary", sequence.GetProperty("captions")[0].GetProperty("sourceClipId").GetString());
        Assert.Equal("primary", sequence.GetProperty("captions")[0].GetProperty("sequenceItemId").GetString());
        Assert.Equal("primary", sequence.GetProperty("captions")[0].GetProperty("text").GetString());
        Assert.Equal("primary", clip.GetProperty("future").GetProperty("sourceId").GetString());
        Assert.Equal("primary", document.GetProperty("optionalMetadata").GetProperty("sourceId").GetString());
        foreach (var name in new[] { "opaque", "exactTimeMap", "compatibility", "futureTop" })
            Assert.True(JsonElement.DeepEquals(project.ReplicaExtension.Json.GetProperty(name), copy.ReplicaExtension.Json.GetProperty(name)));
        Assert.Equal("9007199254740993123456789", copy.ReplicaExtension.Opaque.GetProperty("large").GetRawText());
        var refs = StudioProjectMediaRefs.FindMissing(copy, "project", _ => false, (_, _) => true);
        Assert.Equal(new[] { "copy-primary", "copy-secondary" }, refs.Missing.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains(refs.Locations, x => x.FieldPath.EndsWith(".eventPlacement.sourceId", StringComparison.Ordinal));
        Assert.Contains(refs.Locations, x => x.FieldPath.Contains(".hostCaptions[", StringComparison.Ordinal));
        Assert.Contains(refs.Locations, x => x.FieldPath.Contains(".projectState.sequence.captions[0].sourceClipId", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("timeline")]
    [InlineData("backup")]
    [InlineData("nested")]
    [InlineData("placement")]
    [InlineData("clear")]
    [InlineData("cue")]
    [InlineData("override")]
    [InlineData("transcript")]
    [InlineData("replica")]
    public void Remap_MissingRequiredMapping_RefusesWithoutMutatingInput(string field)
    {
        var project = field switch
        {
            "timeline" => new EditorProject { Timeline = [new("i", "secondary", 0, 1)] },
            "backup" => new EditorProject { SilenceTrimBackup = [new("i", "secondary", 0, 1)] },
            "nested" => new EditorProject { NestedSequences = new() { ["s"] = new("s", "S", [new("i", "secondary", 0, 1)]) } },
            "placement" => new EditorProject { Timeline = [new("i", "primary", 0, 1) { EventPlacement = new() { SourceId = "secondary" } }] },
            "clear" => new EditorProject { ClearedEventSelections = [new() { SourceId = "secondary" }] },
            "cue" => new EditorProject { TimelineCaptions = [new() { SourceClipId = "secondary" }] },
            "override" => new EditorProject { CaptionOverrides = new() { ["secondary"] = [] } },
            "transcript" => new EditorProject { SourceTranscripts = new() { ["secondary"] = [] } },
            _ => Hosted()
        };
        var before = JsonSerializer.Serialize(project, Web);
        var error = Assert.Throws<ArgumentException>(() => StudioProjectMediaRefs.Remap(project, new Dictionary<string, string> { ["primary"] = "copy-primary" }));
        Assert.Contains("secondary", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(project, Web));
    }

    [Theory]
    [InlineData("""{"hostCaptions":{"secondary":[]}}""", ".hostCaptions[\"secondary\"]")]
    [InlineData("""{"hostEventOriginals":[{"key":"item","sourceId":"secondary"}]}""", ".hostEventOriginals[0].sourceId")]
    [InlineData("""{"hostClearedEventSelections":[{"version":1,"sourceId":"secondary"}]}""", ".hostClearedEventSelections[0].sourceId")]
    [InlineData("""{"captions":[{"id":"cue","sourceClipId":"secondary"}]}""", ".captions[0].sourceClipId")]
    [InlineData("""{"audioTracks":[{"clips":[{"key":"item","eventPlacement":{"sourceId":"secondary","eventId":"same"}}]}]}""", ".audioTracks[0].clips[0].eventPlacement.sourceId")]
    public void HostedReference_CollectorAndRemapRequireTheSameSource(string sequence, string suffix)
    {
        var project = Hosted(sequence);
        var refs = StudioProjectMediaRefs.FindMissing(project, "project", _ => false, (_, _) => true);
        Assert.Equal("secondary", Assert.Single(refs.Missing));
        Assert.Equal(2, refs.Locations.Count);
        Assert.All(refs.Locations, location => Assert.EndsWith(suffix, location.FieldPath));
        Assert.Throws<ArgumentException>(() => StudioProjectMediaRefs.Remap(project, new Dictionary<string, string>()));
        var copy = StudioProjectMediaRefs.Remap(project, Map);
        Assert.Empty(StudioProjectMediaRefs.FindMissing(copy, "project", id => id == "copy-secondary", (_, _) => true).Missing);
    }

    [Theory]
    [InlineData("overrides")]
    [InlineData("transcripts")]
    [InlineData("hostCaptions")]
    [InlineData("timeline")]
    public void Remap_TargetCollision_Refuses(string field)
    {
        var project = field switch
        {
            "overrides" => new EditorProject { CaptionOverrides = new() { ["primary"] = [], ["secondary"] = [] } },
            "transcripts" => new EditorProject { SourceTranscripts = new() { ["primary"] = [], ["secondary"] = [] } },
            "timeline" => new EditorProject { Timeline = [new("a", "primary", 0, 1), new("b", "secondary", 0, 1)] },
            _ => Hosted("""{"hostCaptions":{"primary":[],"secondary":[]}}""")
        };
        Assert.Contains("collision", Assert.Throws<ArgumentException>(() => StudioProjectMediaRefs.Remap(project,
            new Dictionary<string, string> { ["primary"] = "same", ["secondary"] = "same" })).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Remap_EmptyMappingTarget_Refuses(string target)
    {
        Assert.Throws<ArgumentException>(() => StudioProjectMediaRefs.Remap(new EditorProject { Timeline = [new("i", "primary", 0, 1)] },
            new Dictionary<string, string> { ["primary"] = target }));
    }

    [Fact]
    public void Remap_EmptyReferencesAndEntityIds_DoNotRequireMappings()
    {
        var project = new EditorProject { Timeline = [new("primary", "", 0, 1, NestedSequenceId: "primary")], TimelineCaptions = [new() { Id = "primary", Text = "primary" }] };
        var copy = StudioProjectMediaRefs.Remap(project, new Dictionary<string, string>());
        Assert.Equal(project.Timeline![0], copy.Timeline![0]);
        Assert.Equal(project.TimelineCaptions![0], copy.TimelineCaptions![0]);
    }

    [Fact]
    public void Remap_SingleCurrentMetadataCatalog_IsPreserved()
    {
        var project = Hosted("""{"name":"Main","videoTracks":[{"clips":[{"sourceId":"primary"}]}]}""", document =>
        {
            var catalog = JsonNode.Parse("""[{"id":"SEQ1","name":"Main","kind":"main","frames":60,"clips":[]}]""");
            document["sequences"] = catalog;
            document["activeSequence"] = "Main";
            document["projectState"]!["sequences"] = catalog!.DeepClone();
            document["projectState"]!["activeSequence"] = "Main";
        });
        var copy = StudioProjectMediaRefs.Remap(project, Map);
        Assert.True(JsonElement.DeepEquals(project.ReplicaExtension!.Document.GetProperty("sequences"), copy.ReplicaExtension!.Document.GetProperty("sequences")));
        Assert.Equal("copy-primary", copy.ReplicaExtension.Document.GetProperty("sequence").GetProperty("videoTracks")[0].GetProperty("clips")[0].GetProperty("sourceId").GetString());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("wrapper")]
    [InlineData("mirror")]
    [InlineData("extra")]
    [InlineData("catalogMedia")]
    [InlineData("catalogMirror")]
    public void Remap_UnsupportedArchiveShape_RefusesAndKeepsInput(string shape)
    {
        var project = Hosted(configure: document =>
        {
            if (shape == "wrapper") document["__fcproj"] = 2;
            if (shape == "mirror") document["projectState"]!["sequence"]!["name"] = "conflict";
            if (shape == "extra") document["sequences"] = JsonNode.Parse("""[{"name":"Main"},{"name":"Other"}]""");
            if (shape == "catalogMedia") document["sequences"] = JsonNode.Parse("""[{"name":"Main","kind":"main","clips":[{"sourceId":"secondary"}]}]""");
            if (shape == "catalogMirror") document["projectState"]!["sequences"] = JsonNode.Parse("""[{"name":"Main","kind":"main","clips":[]}]""");
        });
        if (shape == "foreign")
        {
            var json = JsonNode.Parse(JsonSerializer.Serialize(project, Web))!;
            json["replicaExtension"]!["compatibility"]!["projection"] = "foreign";
            project = EditorProjectCodec.Deserialize(json.ToJsonString());
        }
        var before = JsonSerializer.Serialize(project, Web);
        Assert.Throws<NotSupportedException>(() => StudioProjectMediaRefs.Remap(project, Map));
        Assert.Equal(before, JsonSerializer.Serialize(project, Web));
    }

    [Fact]
    public void Remap_EventIdentities_TypedAndHostedGraphsRemainConsistentAndDetached()
    {
        var project = Hosted("""
            {"videoTracks":[{"clips":[{"sourceId":"primary","eventPlacement":{"version":1,"sourceId":"primary","eventId":"e1"},
              "nestedSeq":{"audioTracks":[{"clips":[{"sourceId":"secondary","eventPlacement":{"version":1,"sourceId":"secondary","eventId":"e2"}}]}]},
              "multicam":{"angles":[{"clip":{"sourceId":"secondary","eventPlacement":{"version":1,"sourceId":"secondary","eventId":"e2"}},
                "clips":[{"sourceId":"primary","eventPlacement":{"version":1,"sourceId":"primary","eventId":"e3"}}]}]}}]}]}
            """) with
        {
            Timeline = [new("main", "primary", 0, 2) { EventPlacement = new() { SourceId = "primary", EventId = "e1" } }],
            SilenceTrimBackup = [new("backup", "secondary", 0, 2) { EventPlacement = new() { SourceId = "secondary", EventId = "e2" } }],
            NestedSequences = new() { ["nested"] = new("nested", "Nested", [new("nested-item", "primary", 0, 1)
                { EventPlacement = new() { SourceId = "primary", EventId = "e3" } }]) }
        };
        var before = JsonSerializer.Serialize(project, Web);
        var events = new Dictionary<string, string> { ["e1"] = "copy-e1", ["e2"] = "copy-e2", ["e3"] = "copy-e3" };
        var copy = StudioProjectMediaRefs.Remap(project, Map, events);
        Assert.Equal("copy-e1", copy.Timeline![0].EventPlacement!.EventId);
        Assert.Equal("copy-e2", copy.SilenceTrimBackup![0].EventPlacement!.EventId);
        Assert.Equal("copy-e3", copy.NestedSequences!["nested"].Timeline[0].EventPlacement!.EventId);
        var document = copy.ReplicaExtension!.Document;
        var sequence = document.GetProperty("sequence");
        Assert.True(JsonElement.DeepEquals(sequence, document.GetProperty("projectState").GetProperty("sequence")));
        var clip = sequence.GetProperty("videoTracks")[0].GetProperty("clips")[0];
        Assert.Equal("copy-e1", clip.GetProperty("eventPlacement").GetProperty("eventId").GetString());
        Assert.Equal("copy-primary", clip.GetProperty("eventPlacement").GetProperty("sourceId").GetString());
        Assert.Equal("copy-e2", clip.GetProperty("nestedSeq").GetProperty("audioTracks")[0].GetProperty("clips")[0].GetProperty("eventPlacement").GetProperty("eventId").GetString());
        var angle = clip.GetProperty("multicam").GetProperty("angles")[0];
        Assert.Equal("copy-e2", angle.GetProperty("clip").GetProperty("eventPlacement").GetProperty("eventId").GetString());
        Assert.Equal("copy-e3", angle.GetProperty("clips")[0].GetProperty("eventPlacement").GetProperty("eventId").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(project, Web));
        copy.Timeline[0] = copy.Timeline[0] with { EventPlacement = new() { SourceId = "changed", EventId = "changed" } };
        Assert.Equal(before, JsonSerializer.Serialize(project, Web));
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("hosted")]
    [InlineData("nested")]
    [InlineData("multicam")]
    [InlineData("missingField")]
    public void Remap_EventIdentities_RequireEveryReferencedEvent(string location)
    {
        var project = location switch
        {
            "typed" => new EditorProject { Timeline = [new("i", "primary", 0, 1) { EventPlacement = new() { SourceId = "primary", EventId = "unmapped" } }] },
            "nested" => Hosted("""{"videoTracks":[{"clips":[{"nestedSeq":{"audioTracks":[{"clips":[{"eventPlacement":{"sourceId":"primary","eventId":"unmapped"}}]}]}}]}]}"""),
            "multicam" => Hosted("""{"videoTracks":[{"clips":[{"multicam":{"angles":[{"clip":{"eventPlacement":{"sourceId":"primary","eventId":"unmapped"}}}]}}]}]}"""),
            "missingField" => Hosted("""{"videoTracks":[{"clips":[{"eventPlacement":{"sourceId":"primary"}}]}]}"""),
            _ => Hosted()
        };
        var before = JsonSerializer.Serialize(project, Web);
        var error = Assert.Throws<ArgumentException>(() => StudioProjectMediaRefs.Remap(project, Map, new Dictionary<string, string>()));
        Assert.Contains("eventPlacement.eventId", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(project, Web));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("same")]
    public void Remap_EventIdentities_RejectEmptyTargetsAndCollisions(string target)
    {
        var project = new EditorProject { Timeline = [
            new("a", "primary", 0, 1) { EventPlacement = new() { SourceId = "primary", EventId = "e1" } },
            new("b", "primary", 1, 2) { EventPlacement = new() { SourceId = "primary", EventId = "e2" } }] };
        var events = new Dictionary<string, string> { ["e1"] = target, ["e2"] = target };
        Assert.Throws<ArgumentException>(() => StudioProjectMediaRefs.Remap(project, Map, events));
    }

    [Fact]
    public void Remap_AssetIdentities_TypedAndHostedKnownFieldsOnly()
    {
        var project = Hosted("""
            {"hostMusic":{"musicAssetId":"music","future":"music"},"future":{"musicAssetId":"music"},
             "videoTracks":[{"clips":[{"sourceId":"primary","nestedSeq":{"hostMusic":{"musicAssetId":"music"}},
                "multicam":{"angles":[{"clip":{"nestedSeq":{"hostMusic":{"musicAssetId":"music"}}}}]}}]}]}
            """) with
        {
            MusicAssetId = "music", EffectAssetId = "effect",
            Timeline = [new("main", "primary", 0, 2) { StickerAssetId = "sticker", SoundAssetId = "sound" }],
            SilenceTrimBackup = [new("backup", "primary", 0, 2) { StickerAssetId = "sticker", SoundAssetId = "sound" }],
            NestedSequences = new() { ["nested"] = new("nested", "Nested", [new("nested-item", "primary", 0, 1)
                { StickerAssetId = "sticker", SoundAssetId = "sound" }]) }
        };
        var before = JsonSerializer.Serialize(project, Web);
        var assets = new Dictionary<string, string> { ["music"] = "new-music", ["effect"] = "new-effect", ["sticker"] = "new-sticker", ["sound"] = "new-sound" };
        var copy = StudioProjectMediaRefs.Remap(project, Map, assetIdMap: assets);
        Assert.Equal("new-music", copy.MusicAssetId);
        Assert.Equal("new-effect", copy.EffectAssetId);
        foreach (var item in new[] { copy.Timeline![0], copy.SilenceTrimBackup![0], copy.NestedSequences!["nested"].Timeline[0] })
        {
            Assert.Equal("new-sticker", item.StickerAssetId);
            Assert.Equal("new-sound", item.SoundAssetId);
        }
        var sequence = copy.ReplicaExtension!.Document.GetProperty("sequence");
        Assert.Equal("new-music", sequence.GetProperty("hostMusic").GetProperty("musicAssetId").GetString());
        Assert.Equal("music", sequence.GetProperty("hostMusic").GetProperty("future").GetString());
        Assert.Equal("music", sequence.GetProperty("future").GetProperty("musicAssetId").GetString());
        Assert.True(JsonElement.DeepEquals(sequence, copy.ReplicaExtension.Document.GetProperty("projectState").GetProperty("sequence")));
        var clip = sequence.GetProperty("videoTracks")[0].GetProperty("clips")[0];
        Assert.Equal("new-music", clip.GetProperty("nestedSeq").GetProperty("hostMusic").GetProperty("musicAssetId").GetString());
        Assert.Equal("new-music", clip.GetProperty("multicam").GetProperty("angles")[0].GetProperty("clip").GetProperty("nestedSeq").GetProperty("hostMusic").GetProperty("musicAssetId").GetString());
        Assert.True(JsonElement.DeepEquals(project.ReplicaExtension!.Opaque, copy.ReplicaExtension.Opaque));
        Assert.Equal(before, JsonSerializer.Serialize(project, Web));
    }

    [Theory]
    [InlineData("music")]
    [InlineData("effect")]
    [InlineData("sticker")]
    [InlineData("sound")]
    [InlineData("backup")]
    [InlineData("nested")]
    [InlineData("hosted")]
    public void Remap_AssetIdentities_RequireEveryKnownAsset(string location)
    {
        var project = location switch
        {
            "music" => new EditorProject { MusicAssetId = "asset" },
            "effect" => new EditorProject { EffectAssetId = "asset" },
            "sticker" => new EditorProject { Timeline = [new("i", "primary", 0, 1) { StickerAssetId = "asset" }] },
            "sound" => new EditorProject { Timeline = [new("i", "primary", 0, 1) { SoundAssetId = "asset" }] },
            "backup" => new EditorProject { SilenceTrimBackup = [new("i", "primary", 0, 1) { StickerAssetId = "asset" }] },
            "nested" => new EditorProject { NestedSequences = new() { ["s"] = new("s", "S", [new("i", "primary", 0, 1) { SoundAssetId = "asset" }]) } },
            _ => Hosted("""{"hostMusic":{"musicAssetId":"asset"}}""")
        };
        var before = JsonSerializer.Serialize(project, Web);
        var error = Assert.Throws<ArgumentException>(() => StudioProjectMediaRefs.Remap(project, Map, assetIdMap: new Dictionary<string, string>()));
        Assert.Contains("asset mapping", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(project, Web));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("same")]
    public void Remap_AssetIdentities_RejectEmptyTargetsAndCollisions(string target)
    {
        var project = new EditorProject { MusicAssetId = "a", EffectAssetId = "b" };
        Assert.Throws<ArgumentException>(() => StudioProjectMediaRefs.Remap(project, Map,
            assetIdMap: new Dictionary<string, string> { ["a"] = target, ["b"] = target }));
    }

    [Fact]
    public void Remap_AssetIdentities_EmptyReferencesNeedNoMappingAndLegacyCallPreservesAssets()
    {
        var project = Hosted("""{"hostMusic":{"musicAssetId":null}}""") with
        { MusicAssetId = null, EffectAssetId = "", Timeline = [new("i", "primary", 0, 1) { StickerAssetId = "", SoundAssetId = null }] };
        var copy = StudioProjectMediaRefs.Remap(project, Map, assetIdMap: new Dictionary<string, string>());
        Assert.Null(copy.MusicAssetId); Assert.Equal("", copy.EffectAssetId);
        Assert.Equal("", copy.Timeline![0].StickerAssetId); Assert.Null(copy.Timeline[0].SoundAssetId);
        var legacy = StudioProjectMediaRefs.Remap(Hosted() with { MusicAssetId = "unmapped" }, Map);
        Assert.Equal("unmapped", legacy.MusicAssetId);
        Assert.Equal("music", legacy.ReplicaExtension!.Document.GetProperty("sequence").GetProperty("hostMusic").GetProperty("musicAssetId").GetString());
    }

    [Fact]
    public void BinOnlySource_CollectedAndRemappedWithoutCreatingClip()
    {
        var project = Hosted("""{"hostBinSources":[{"sourceId":"secondary","name":"Camera A"}],"videoTracks":[],"audioTracks":[]}""");
        var refs = StudioProjectMediaRefs.FindMissing(project,"primary",id => id!="secondary",(_,_)=>true);
        Assert.Contains("secondary",refs.Missing);
        var copy=StudioProjectMediaRefs.Remap(project,Map);
        var sequence=copy.ReplicaExtension!.Document.GetProperty("sequence");
        Assert.Equal(0,sequence.GetProperty("videoTracks").GetArrayLength());
        var entry=sequence.GetProperty("hostBinSources")[0];
        Assert.Equal("copy-secondary",entry.GetProperty("sourceId").GetString());
        Assert.Equal("Camera A",entry.GetProperty("name").GetString());
        Assert.Equal("secondary",project.ReplicaExtension!.Document.GetProperty("sequence").GetProperty("hostBinSources")[0].GetProperty("sourceId").GetString());
    }

    private static EditorProject Hosted(string? sequenceJson = null, Action<JsonObject>? configure = null)
    {
        var sequence = JsonNode.Parse(sequenceJson ?? """
            {"name":"Main","hostMusic":{"musicAssetId":"music"},"hostCaptions":{"primary":[{"start":0,"end":1,"text":"primary"}]},
             "hostEventOriginals":[{"key":"primary","sourceId":"secondary"}],"hostClearedEventSelections":[{"version":1,"sourceId":"primary"}],
             "captions":[{"id":"primary","start":0,"end":30,"text":"primary","sourceClipId":"secondary","sequenceItemId":"primary","sourceStart":1.25,"sourceEnd":2.5}],
             "videoTracks":[{"clips":[{"key":"primary","sourceId":"primary","eventPlacement":{"version":1,"sourceId":"primary","eventId":"primary"},
               "nestedSeq":{"audioTracks":[{"clips":[{"key":"child","sourceId":"secondary"}]}]},
               "multicam":{"angles":[{"clip":{"sourceId":"secondary"},"clips":[{"sourceId":"primary"}]}]},
               "future":{"sourceId":"primary"}}]}]}
            """)!;
        var document = new JsonObject
        {
            ["__fcproj"] = 1, ["sequence"] = sequence,
            ["sequences"] = new JsonArray(),
            ["projectState"] = new JsonObject { ["sequence"] = sequence.DeepClone(), ["sequences"] = new JsonArray() },
            ["optionalMetadata"] = JsonNode.Parse("""{"sourceId":"primary"}""")
        };
        configure?.Invoke(document);
        return EditorProjectCodec.Deserialize("{\"replicaExtension\":{\"version\":1,\"codecVersion\":1,\"document\":" + document.ToJsonString()
            + ",\"exactTimeMap\":{\"primary\":{\"seconds\":1.230000e-40}},\"compatibility\":{\"projection\":\"lc-editor-next-active-sequence-v1\"},"
            + "\"opaque\":{\"sourceId\":\"primary\",\"large\":9007199254740993123456789},\"futureTop\":{\"sourceId\":\"primary\"}}}");
    }
}
