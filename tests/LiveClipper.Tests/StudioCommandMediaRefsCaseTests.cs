using System.Text.Json.Nodes;
using LiveClipper.Core;
using Xunit;

namespace LiveClipper.Tests;

public sealed class StudioCommandMediaRefsCaseTests
{
    private static readonly string[] ExpectedReferences =
        ["Clip-Main", "Clip-Present", "Clip-Backup", "Clip-Nested", "Clip-Transcript", "Clip-Caption"];

    [Theory]
    [InlineData("camelCase")]
    [InlineData("PascalCase")]
    [InlineData("mixedCase")]
    public void FindMissing_ReplaceProjectRecognizesEveryReferenceInAcceptedCasing(string casing)
    {
        var command = Replacement(casing);
        var project = EditorProjectCodec.Deserialize(command.Args!["project"]!.ToJsonString());
        Assert.Equal("Clip-Main", project.Timeline![0].ClipId);
        Assert.Equal("Clip-Backup", Assert.Single(project.SilenceTrimBackup!).ClipId);
        Assert.Equal("Clip-Nested", project.NestedSequences!["Sequence-A"].Timeline[0].ClipId);
        Assert.True(project.SourceTranscripts!.ContainsKey("Clip-Transcript"));
        Assert.True(project.CaptionOverrides!.ContainsKey("Clip-Caption"));
        var checkedIds = new List<string>();

        var missing = StudioCommandMediaRefs.FindMissing([command], id =>
        {
            checkedIds.Add(id);
            return string.Equals(id, "Clip-Present", StringComparison.Ordinal);
        });

        Assert.Equal(new[] { "Clip-Main", "Clip-Backup", "Clip-Nested", "Clip-Transcript", "Clip-Caption" }, missing);
        Assert.Equal(ExpectedReferences, checkedIds);
    }

    [Theory]
    [InlineData("camelCase")]
    [InlineData("PascalCase")]
    [InlineData("mixedCase")]
    public void FindMissing_ReplaceProjectPassesWhenEveryReferencedMediaExists(string casing)
    {
        var command = Replacement(casing);
        var available = new HashSet<string>(ExpectedReferences, StringComparer.Ordinal);
        var checkedIds = new List<string>();

        var missing = StudioCommandMediaRefs.FindMissing([command], id =>
        {
            checkedIds.Add(id);
            return available.Contains(id);
        });

        Assert.Empty(missing);
        Assert.Equal(ExpectedReferences, checkedIds);
        Assert.Equal(ExpectedReferences, StudioCommandMediaRefs.Collect([command]));
    }

    [Theory]
    [InlineData("camelCase")]
    [InlineData("PascalCase")]
    [InlineData("mixedCase")]
    public void FindMissing_PropertyCasingDoesNotChangeMediaIdCasing(string casing)
    {
        var command = new EditorCommand("replaceProject", new JsonObject
        {
            ["project"] = new JsonObject
            {
                [Name("Timeline", casing)] = new JsonArray
                {
                    new JsonObject { [Name("ClipId", casing)] = "Clip-A" },
                    new JsonObject { [Name("ClipId", casing)] = "clip-a" },
                    new JsonObject { [Name("ClipId", casing)] = "Clip-A" }
                }
            }
        });
        var available = new HashSet<string>(StringComparer.Ordinal) { "Clip-A" };

        Assert.Equal(new[] { "Clip-A", "clip-a" }, StudioCommandMediaRefs.Collect([command]));
        Assert.Equal(new[] { "clip-a" }, StudioCommandMediaRefs.FindMissing([command], available.Contains));
    }

    [Theory]
    [InlineData("camelCase")]
    [InlineData("PascalCase")]
    [InlineData("mixedCase")]
    public void FindMissing_ReadsItemsAndCaptionSourceAcrossPropertyCasing(string casing)
    {
        var commands = new[]
        {
            new EditorCommand("addItems", new JsonObject
            {
                [Name("Items", casing)] = new JsonArray
                {
                    new JsonObject { [Name("ClipId", casing)] = "Clip-Item" }
                }
            }),
            new EditorCommand("setCaptions", new JsonObject { [Name("SourceClipId", casing)] = "Clip-Caption" })
        };
        var available = new HashSet<string>(StringComparer.Ordinal) { "Clip-Caption" };

        Assert.Equal(new[] { "Clip-Item", "Clip-Caption" }, StudioCommandMediaRefs.Collect(commands));
        Assert.Equal(new[] { "Clip-Item" }, StudioCommandMediaRefs.FindMissing(commands, available.Contains));
    }

    [Fact]
    public void FindMissing_UsesLastCaseAliasAcceptedByWebDeserialization()
    {
        var command = new EditorCommand("replaceProject", JsonNode.Parse("""
            {"project":{
                "Timeline":[{"Id":"item-main","ClipId":"Clip-Present","clipId":"Clip-Missing","Start":0,"End":1}],
                "NestedSequences":{"Sequence-A":{
                    "Id":"Sequence-A","Name":"nested","Timeline":[],
                    "timeline":[{"id":"nested-item","clipId":"Clip-Nested-Missing","start":0,"end":1}]
                }}
            }}
            """));
        var project = EditorProjectCodec.Deserialize(command.Args!["project"]!.ToJsonString());
        Assert.Equal("Clip-Missing", Assert.Single(project.Timeline!).ClipId);
        Assert.Equal("Clip-Nested-Missing", Assert.Single(project.NestedSequences!["Sequence-A"].Timeline).ClipId);
        var available = new HashSet<string>(StringComparer.Ordinal) { "Clip-Present" };

        Assert.Equal(new[] { "Clip-Missing", "Clip-Nested-Missing" },
            StudioCommandMediaRefs.FindMissing([command], available.Contains));
    }

    [Fact]
    public void Collect_IgnoresNonMediaIdentifiersAndDoesNotMutateReplacement()
    {
        var command = Replacement("mixedCase");
        var before = command.Args!.ToJsonString();

        Assert.Equal(ExpectedReferences, StudioCommandMediaRefs.Collect([command]));
        Assert.Equal(before, command.Args.ToJsonString());
    }

    private static EditorCommand Replacement(string casing)
    {
        string Key(string name) => Name(name, casing);
        JsonObject Item(string id, string clipId, double start = 0) => new()
        {
            [Key("Id")] = id,
            [Key("ClipId")] = clipId,
            [Key("Start")] = start,
            [Key("End")] = start + 1
        };

        return new EditorCommand("replaceProject", new JsonObject
        {
            ["project"] = new JsonObject
            {
                [Key("Timeline")] = new JsonArray(Item("item-main", "Clip-Main"), Item("item-present", "Clip-Present", 1)),
                [Key("SilenceTrimBackup")] = new JsonArray(Item("backup-item", "Clip-Backup")),
                [Key("NestedSequences")] = new JsonObject
                {
                    ["Sequence-A"] = new JsonObject
                    {
                        [Key("Id")] = "Sequence-A",
                        [Key("Name")] = "nested",
                        [Key("Timeline")] = new JsonArray(Item("nested-item", "Clip-Nested"), Item("duplicate-item", "Clip-Main", 1))
                    }
                },
                [Key("SourceTranscripts")] = new JsonObject { ["Clip-Transcript"] = new JsonArray() },
                [Key("CaptionOverrides")] = new JsonObject { ["Clip-Caption"] = new JsonArray() },
                [Key("MusicAssetId")] = "asset-not-media",
                [Key("EffectAssetId")] = "another-asset-not-media"
            }
        });
    }

    private static string Name(string name, string casing) => casing switch
    {
        "PascalCase" => name,
        "camelCase" => char.ToLowerInvariant(name[0]) + name[1..],
        "mixedCase" => string.Concat(name.Select((letter, index) =>
            index % 2 == 0 ? char.ToLowerInvariant(letter) : char.ToUpperInvariant(letter))),
        _ => throw new ArgumentOutOfRangeException(nameof(casing), casing, "Unknown property casing")
    };
}
