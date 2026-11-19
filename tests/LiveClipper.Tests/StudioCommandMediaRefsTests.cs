using System.Text.Json;
using System.Text.Json.Nodes;
using LiveClipper.Core;
using Xunit;

namespace LiveClipper.Tests;

public sealed class StudioCommandMediaRefsTests
{
    private static EditorCommand Cmd(string op, string argsJson = "{}")
        => new(op, JsonNode.Parse(argsJson));

    [Fact]
    public void Collect_ReadsAddItemsAndCaptionSources()
    {
        var commands = new[]
        {
            Cmd("addItems", """{"items":[{"clipId":"clip-a","start":0,"end":1},{"clipId":"clip-b","start":1,"end":2}]}"""),
            Cmd("setCaptions", """{"sourceClipId":"clip-c","segments":[]}""")
        };

        Assert.Equal(new[] { "clip-a", "clip-b", "clip-c" }, StudioCommandMediaRefs.Collect(commands));
    }

    [Fact]
    public void Collect_DeduplicatesAcrossCommandsInFirstSeenOrder()
    {
        var commands = new[]
        {
            Cmd("setCaptions", """{"sourceClipId":"clip-z","segments":null}"""),
            Cmd("addItems", """{"items":[{"clipId":"clip-b"},{"clipId":"clip-z"},{"clipId":"clip-b"},{"clipId":"clip-a"}]}"""),
            Cmd("setCaptions", """{"sourceClipId":"clip-b","segments":[]}""")
        };

        Assert.Equal(new[] { "clip-z", "clip-b", "clip-a" }, StudioCommandMediaRefs.Collect(commands));
    }

    [Fact]
    public void Collect_UsesOrdinalIdsWithoutTrimming()
    {
        var command = Cmd("addItems", """{"items":[{"clipId":"Clip-A"},{"clipId":"clip-a"},{"clipId":" clip-a "},{"clipId":"Clip-A"}]}""");

        Assert.Equal(new[] { "Clip-A", "clip-a", " clip-a " }, StudioCommandMediaRefs.Collect(new[] { command }));
    }

    [Fact]
    public void Collect_IgnoresMissingNullEmptyAndWhitespaceIds()
    {
        var commands = new[]
        {
            Cmd("addItems", """{"items":[{},null,{"clipId":null},{"clipId":""},{"clipId":"  "},{"clipId":"clip-a"}]}"""),
            Cmd("setCaptions"),
            Cmd("setCaptions", """{"sourceClipId":null}"""),
            Cmd("setCaptions", """{"sourceClipId":""}"""),
            Cmd("setCaptions", """{"sourceClipId":"\t"}""")
        };

        Assert.Equal(new[] { "clip-a" }, StudioCommandMediaRefs.Collect(commands));
    }

    [Theory]
    [InlineData("addItems", "null")]
    [InlineData("addItems", "[]")]
    [InlineData("addItems", "{\"items\":{\"clipId\":\"not-an-item\"}}")]
    [InlineData("addItems", "{\"items\":[1,true,[],{\"clipId\":42},{\"clipId\":{}}]}")]
    [InlineData("setCaptions", "{\"sourceClipId\":false}")]
    [InlineData("replaceProject", "{\"project\":5}")]
    [InlineData("replaceProject", "{\"project\":{\"Timeline\":{},\"NestedSequences\":[]}}")]
    public void Collect_LeavesMalformedShapesToExecutor(string op, string argsJson)
    {
        Assert.Empty(StudioCommandMediaRefs.Collect(new[] { Cmd(op, argsJson) }));
    }

    [Fact]
    public void Collect_MixedCommandsDoNotConfuseTimelineKeysWithMediaIds()
    {
        var commands = new[]
        {
            Cmd("moveItems", """{"moves":[{"key":"item-a","start":1}]}"""),
            Cmd("setItemProps", """{"items":[{"key":"item-b","opacity":0.5}]}"""),
            Cmd("removeItems", """{"keys":["item-c"]}"""),
            Cmd("setTransition", """{"key":"item-d","transition":null}"""),
            Cmd("trimItem", """{"key":"item-e","start":0,"end":1}"""),
            Cmd("splitItem", """{"key":"item-f","at":1}"""),
            Cmd("setRange", """{"start":0,"end":2}"""),
            Cmd("setRenderSettings", """{"format":"h264"}"""),
            Cmd("setTrackState", """{"mute":[4]}"""),
            Cmd("unknown", """{"clipId":"not-a-known-ref"}"""),
            Cmd("addItems", """{"items":[{"clipId":"clip-a"}]}"""),
            Cmd("setCaptions", """{"sourceClipId":"clip-b","segments":[]}""")
        };

        Assert.Equal(new[] { "clip-a", "clip-b" }, StudioCommandMediaRefs.Collect(commands));
    }

    [Fact]
    public void Collect_ReadsSerializedReplacementTimelineAndNestedSequences()
    {
        var project = new EditorProject
        {
            Timeline = new[] { new TimelineItem("item-a", "clip-a", 0, 1) },
            NestedSequences = new Dictionary<string, NestedSequence>
            {
                ["sequence-a"] = new("sequence-a", "nested", new[]
                {
                    new TimelineItem("item-b", "clip-b", 0, 1),
                    new TimelineItem("item-c", "clip-a", 1, 2)
                })
            },
            MusicAssetId = "music-asset",
            EffectAssetId = "effect-asset"
        };
        var command = new EditorCommand("replaceProject", new JsonObject
        {
            ["project"] = JsonSerializer.SerializeToNode(project)
        });

        Assert.Equal(new[] { "clip-a", "clip-b" }, StudioCommandMediaRefs.Collect(new[] { command }));
    }

    [Fact]
    public void Collect_ReplacementMediaFieldsFollowJsonOrderAndDeduplicate()
    {
        var commands = new[]
        {
            Cmd("setCaptions", """{"sourceClipId":"clip-z","segments":[]}"""),
            Cmd("replaceProject", """
                {"project":{
                    "CaptionOverrides":{"clip-c":[],"clip-z":[]},
                    "Timeline":[{"Id":"item-a","ClipId":"clip-a"},{"Id":"item-only"}],
                    "SourceTranscripts":{"clip-b":[]},
                    "SilenceTrimBackup":[{"Id":"backup-a","ClipId":"clip-d"}],
                    "NestedSequences":{"sequence-a":{"Timeline":[{"Id":"nested-a","ClipId":"clip-e"}]}},
                    "MusicAssetId":"asset-not-a-clip"
                }}
                """)
        };

        Assert.Equal(new[] { "clip-z", "clip-c", "clip-a", "clip-b", "clip-d", "clip-e" }, StudioCommandMediaRefs.Collect(commands));
    }

    [Fact]
    public void Collect_ReplacementIdsAreNotMediaRefs_ButCamelCaseProjectRefsAre()
    {
        // Id / NestedSequenceId 不是媒体引用。
        var nonMedia = new[]
        {
            Cmd("replaceProject", """{"project":{"Timeline":[{"Id":"item-only","NestedSequenceId":"sequence-only"}]}}""")
        };
        Assert.Empty(StudioCommandMediaRefs.Collect(nonMedia));

        // EditorProjectCodec 现按 Web 选项反序列化(camelCase 会被接受),
        // 预检必须同样识别 camelCase,否则可绕过 missing_media 守卫。
        var camel = new[]
        {
            Cmd("replaceProject", """{"project":{"timeline":[{"clipId":"camel-clip"}]}}""")
        };
        Assert.Equal(new[] { "camel-clip" }, StudioCommandMediaRefs.Collect(camel));
    }

    [Fact]
    public void FindMissing_ChecksEachDistinctIdOnceAndPreservesReferenceOrder()
    {
        var commands = new[]
        {
            Cmd("addItems", """{"items":[{"clipId":"missing-b"},{"clipId":"present"},{"clipId":"missing-a"},{"clipId":"missing-b"}]}"""),
            Cmd("setCaptions", """{"sourceClipId":"missing-a","segments":[]}""")
        };
        var checkedIds = new List<string>();

        var missing = StudioCommandMediaRefs.FindMissing(commands, id =>
        {
            checkedIds.Add(id);
            return id == "present";
        });

        Assert.Equal(new[] { "missing-b", "missing-a" }, missing);
        Assert.Equal(new[] { "missing-b", "present", "missing-a" }, checkedIds);
    }

    [Fact]
    public void FindMissing_ReturnsEmptyWhenAllReferencesExist()
    {
        var commands = new[] { Cmd("addItems", """{"items":[{"clipId":"present"}]}""") };

        Assert.Empty(StudioCommandMediaRefs.FindMissing(commands, _ => true));
    }

    [Fact]
    public void EmptyBatch_ProducesNoReferencesAndDoesNotCallExists()
    {
        var calls = 0;
        Assert.Empty(StudioCommandMediaRefs.Collect(Array.Empty<EditorCommand>()));
        Assert.Empty(StudioCommandMediaRefs.FindMissing(Array.Empty<EditorCommand>(), _ =>
        {
            calls++;
            return false;
        }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Collect_DoesNotMutateCommandArgs()
    {
        var command = Cmd("addItems", """{"items":[{"clipId":"clip-a"},{"clipId":"clip-a"}]}""");
        var before = command.Args!.ToJsonString();

        StudioCommandMediaRefs.FindMissing(new[] { command }, _ => false);

        Assert.Equal(before, command.Args.ToJsonString());
    }
}
