using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

/// <summary>P1.6-a：同源字幕覆盖（own-key 语义）、快照冻结与字幕映射的 Core 行为。</summary>
public sealed class CaptionOverrideTests
{
    private static TranscriptSegment Seg(double start, double end, string text) => new(start, end, text);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "liveclipper-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void NormalizeCaptions_DropsNonFiniteEmptyAndInverted_AndClampsStart()
    {
        var normalized = EditorRenderService.NormalizeCaptions(new[]
        {
            Seg(-2, 1, "clamped"),
            Seg(1, 1, "zero-length"),
            Seg(2, 1, "inverted"),
            Seg(3, 4, "   "),
            Seg(double.NaN, 5, "nan"),
            Seg(6, double.PositiveInfinity, "inf"),
            Seg(7, 8, " keep "),
        });

        Assert.Equal(2, normalized.Length);
        Assert.Equal(0, normalized[0].Start);
        Assert.Equal("clamped", normalized[0].Text);
        Assert.Equal(" keep ", normalized[1].Text);
    }

    [Fact]
    public void CaptionsFor_PrefersOverrideThenFrozenSnapshotThenFallback()
    {
        var both = new EditorProject
        {
            CaptionOverrides = new() { ["c1"] = new[] { Seg(0, 1, "override") } },
            SourceTranscripts = new() { ["c1"] = new[] { Seg(0, 1, "snapshot") } },
        };
        Assert.Equal("override", EditorRenderService.CaptionsFor(both, "c1", new[] { Seg(0, 1, "fallback") }).Single().Text);

        var frozen = new EditorProject { SourceTranscripts = new() { ["c1"] = new[] { Seg(0, 1, "snapshot") } } };
        Assert.Equal("snapshot", EditorRenderService.CaptionsFor(frozen, "c1", new[] { Seg(0, 1, "fallback") }).Single().Text);

        Assert.Equal("fallback", EditorRenderService.CaptionsFor(null, "c1", new[] { Seg(0, 1, "fallback") }).Single().Text);
        Assert.Equal("fallback", EditorRenderService.CaptionsFor(new EditorProject(), "c1", new[] { Seg(0, 1, "fallback") }).Single().Text);
        Assert.Empty(EditorRenderService.CaptionsFor(new EditorProject(), "c1", null));
    }

    [Fact]
    public void CaptionsFor_ExplicitEmptyOverrideIsAuthoritative()
    {
        var plan = new EditorProject
        {
            CaptionOverrides = new() { ["c1"] = Array.Empty<TranscriptSegment>() },
            SourceTranscripts = new() { ["c1"] = new[] { Seg(0, 1, "stale") } },
        };

        Assert.Empty(EditorRenderService.CaptionsFor(plan, "c1", new[] { Seg(0, 1, "fallback") }));
        Assert.True(EditorRenderService.HasCaptionOverride(plan, "c1"));
        Assert.False(EditorRenderService.HasCaptionOverride(plan, "c2"));
        Assert.False(EditorRenderService.HasCaptionOverride(null, "c1"));
    }

    [Fact]
    public void FreezeCaptionOverrides_IsolatesFromLaterArrayEdits()
    {
        var edited = new[] { Seg(0, 1, "edited") };
        var plan = new EditorProject { CaptionOverrides = new() { ["c1"] = edited } };

        var frozen = EditorRenderService.FreezeCaptionOverrides(plan)!;
        edited[0] = Seg(5, 6, "mutated");

        Assert.NotSame(edited, frozen["c1"]);
        Assert.Equal("edited", frozen["c1"].Single().Text);
        Assert.Null(EditorRenderService.FreezeCaptionOverrides(new EditorProject()));
    }

    [Fact]
    public void MapSubtitles_PrimaryOverrideSkipsSelectedSegments()
    {
        var items = new[] { new TimelineItem("i1", "c1", 0, 10) };
        var source = new[] { Seg(0, 1, "a"), Seg(2, 3, "b") };

        var filtered = EditorRenderService.MapSubtitles(items, _ => source, "c1", new[] { 0 });
        Assert.Equal("a", filtered.Single().Text);

        var all = EditorRenderService.MapSubtitles(items, _ => source, "c1", new[] { 0 }, null, 30, false);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void MapSubtitles_MapsManySourcesWithSpeedGapAndSplit()
    {
        const int sourceCount = 25;
        var sources = new Dictionary<string, TranscriptSegment[]>();
        for (var i = 0; i < sourceCount; i++)
        {
            sources[$"s{i}"] = new[]
            {
                Seg(0, 5, $"s{i}-head"),
                Seg(10, 20, $"s{i}-mid"),
                Seg(25, 30, $"s{i}-tail"),
            };
        }

        var items = new TimelineItem[30];
        for (var i = 0; i < items.Length; i++)
            items[i] = new TimelineItem($"i{i}", $"s{i % sourceCount}", 10, 20,
                Speed: i % 2 == 0 ? 1 : 2, GapBefore: i * 0.25);

        var mapped = EditorRenderService.MapSubtitles(items, id => sources[id], "s0", null);

        Assert.Equal(30, mapped.Count);
        Assert.Equal("s0-mid", mapped[0].Text);
        Assert.Equal(0, mapped[0].Start, 9);
        Assert.Equal(10, mapped[0].End, 9);
        Assert.Equal("s1-mid", mapped[1].Text);
        Assert.Equal(10.25, mapped[1].Start, 9);
        Assert.Equal(15.25, mapped[1].End, 9);
        for (var i = 1; i < mapped.Count; i++)
            Assert.True(mapped[i].Start > mapped[i - 1].Start);
    }

    [Fact]
    public void EditorProject_JsonRoundtripsCaptionOverrides_AndNullIsOmitted()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plan = new EditorProject
        {
            Start = 0,
            End = 5,
            CaptionOverrides = new()
            {
                ["c1"] = new[] { Seg(1, 2, "hi") },
                ["c2"] = Array.Empty<TranscriptSegment>(),
            },
        };

        var json = JsonSerializer.Serialize(plan, options);
        Assert.Contains("captionOverrides", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<EditorProject>(json, options)!;
        Assert.Equal("hi", back.CaptionOverrides!["c1"].Single().Text);
        Assert.Empty(back.CaptionOverrides["c2"]);

        var bare = JsonSerializer.Serialize(new EditorProject { Start = 0, End = 1 }, options);
        Assert.DoesNotContain("captionOverrides", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_PrefersOverrideAndFallsBackToStoreTranscript()
    {
        var root = TempDir();
        try
        {
            var store = new Store(Path.Combine(root, "test.db"));
            store.Migrate();
            var studio = new StudioStore(store.DatabasePath);
            studio.Migrate();
            var renderer = new EditorRenderService(store, studio, new MediaTools(null), root);

            var firstPath = Path.Combine(root, "c1.mp4");
            var secondPath = Path.Combine(root, "c2.mp4");
            File.WriteAllText(firstPath, "x");
            File.WriteAllText(secondPath, "y");
            store.SaveClip(new Clip("c1", "e1", firstPath, null, DateTimeOffset.UtcNow));
            store.SaveClip(new Clip("c2", "e2", secondPath, null, DateTimeOffset.UtcNow));
            store.SaveTranscript("e2",
                "{\"language\":\"zh\",\"segments\":[{\"start\":1,\"end\":2,\"text\":\"from-store\"}]}",
                Path.Combine(root, "e2.srt"));

            var edited = new[] { Seg(0.5, 1.5, "edited") };
            var plan = new EditorProject
            {
                Start = 0,
                End = 10,
                Timeline = new[] { new TimelineItem("i1", "c1", 0, 10), new TimelineItem("i2", "c2", 0, 10) },
                CaptionOverrides = new() { ["c1"] = edited },
            };

            var snapshot = renderer.Snapshot(store.Clips().Single(x => x.Id == "c1"), plan);

            Assert.Equal("edited", snapshot.SourceTranscripts!["c1"].Single().Text);
            Assert.Equal("from-store", snapshot.SourceTranscripts!["c2"].Single().Text);

            edited[0] = Seg(7, 8, "mutated");
            Assert.Equal("edited", snapshot.CaptionOverrides!["c1"].Single().Text);
            Assert.Equal("edited", snapshot.SourceTranscripts["c1"].Single().Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
