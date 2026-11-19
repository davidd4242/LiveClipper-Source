using System.Text.Json.Nodes;
using LiveClipper.Core;
using LiveClipper.Core.Bridge;

namespace LiveClipper.Tests;

/// <summary>P1.6-a：fcproj 快照导出契约（即时工程优先、裁剪不重置、多源真实路径、校验不回落）。</summary>
public sealed class FcprojSnapshotTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "liveclipper-fcproj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static EditorProject TrimmedPlan(string caption = "edit") => new()
    {
        Start = 2,
        End = 6,
        Timeline = new[] { new TimelineItem("i1", "c1", 2, 6) },
        CaptionOverrides = new() { ["c1"] = new[] { new TranscriptSegment(2.5, 3.5, caption) } },
    };

    [Theory]
    [InlineData(3840, 2160, 720, 1280, 720, 30, false)]
    [InlineData(3840, 2160, 720, 1280, 720, 60, true)]
    [InlineData(3840, 2160, 1080, 1920, 1080, 60, false)]
    [InlineData(3840, 2160, 1080, 1920, 1080, 30, true)]
    [InlineData(3840, 2160, 0, 3840, 2160, 30, false)]
    [InlineData(3840, 2160, 0, 3840, 2160, 60, true)]
    [InlineData(2160, 3840, 720, 404, 720, 30, false)]
    [InlineData(2160, 3840, 1080, 608, 1080, 60, true)]
    [InlineData(640, 360, 1080, 640, 360, 30, false)]
    [InlineData(640, 360, 720, 640, 360, 60, true)]
    [InlineData(1921, 1081, 1080, 1918, 1080, 60, false)]
    [InlineData(1921, 1081, 0, 1918, 1080, 30, true)]
    [InlineData(1, 1, 720, 2, 2, 30, false)]
    [InlineData(0, 0, 720, 1280, 720, 30, false)]
    [InlineData(-1, -1, 1080, 1920, 1080, 60, true)]
    public void BuildDocument_ResolutionMatchesRenderDimensionsAndKeepsFps(
        int sourceWidth, int sourceHeight, int resolution, int expectedWidth, int expectedHeight,
        int fps, bool useSavedProject)
    {
        var root = TempDir();
        try
        {
            var primary = Path.Combine(root, "c1.mp4");
            File.WriteAllText(primary, "x");
            var plan = TrimmedPlan() with { Resolution = resolution, Fps = fps };
            var saved = useSavedProject
                ? System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                : null;

            var doc = FcprojExportService.BuildDocument("c1", primary, useSavedProject ? null : plan,
                saved, 10, sourceWidth, sourceHeight, _ => null);

            var project = doc["project"]!.AsObject();
            Assert.Equal(expectedWidth, project["width"]!.GetValue<int>());
            Assert.Equal(expectedHeight, project["height"]!.GetValue<int>());
            Assert.Equal(fps, project["fps"]!.GetValue<int>());
            Assert.Equal(resolution, project["resolution"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildDocument_InlinePlanWinsOverSavedAndKeepsTrim()
    {
        var root = TempDir();
        try
        {
            var primary = Path.Combine(root, "c1.mp4");
            File.WriteAllText(primary, "x");
            const string saved =
                "{\"start\":0,\"end\":10,\"timeline\":[{\"id\":\"old\",\"clipId\":\"c1\",\"start\":0,\"end\":10}]}";

            var calls = new List<(string ClipId, EditorProject Project)>();
            var doc = FcprojExportService.BuildDocument("c1", primary, TrimmedPlan(), saved, 10, 1920, 1080,
                _ => null, (clipId, project) => { calls.Add((clipId, project)); return project; });

            var project = doc["project"]!.AsObject();
            Assert.Equal(2d, project["start"]!.GetValue<double>());
            Assert.Equal(6d, project["end"]!.GetValue<double>());
            Assert.Single(calls);
            Assert.Equal("c1", calls[0].ClipId);
            Assert.Equal("edit", project["captionOverrides"]!["c1"]![0]!["text"]!.GetValue<string>());

            var clips = doc["clips"]!.AsArray();
            Assert.Single(clips);
            Assert.Equal("c1", clips[0]!["id"]!.GetValue<string>());
            Assert.Equal(primary, clips[0]!["path"]!.GetValue<string>());
            Assert.Equal(primary, doc["clip"]!["path"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildDocument_ResolvesEveryTimelineSourceWithoutOverwritingPrimary()
    {
        var root = TempDir();
        try
        {
            var primary = Path.Combine(root, "c1.mp4");
            var second = Path.Combine(root, "c2.mp4");
            File.WriteAllText(primary, "x");
            File.WriteAllText(second, "y");

            var plan = new EditorProject
            {
                Start = 0,
                End = 10,
                Timeline = new[]
                {
                    new TimelineItem("i1", "c1", 0, 10),
                    new TimelineItem("i2", "c2", 0, 10),
                },
            };

            var doc = FcprojExportService.BuildDocument("c1", primary, plan, null, 10, 1280, 720,
                id => id == "c2" ? second : null);

            var clips = doc["clips"]!.AsArray();
            Assert.Equal(2, clips.Count);
            Assert.Equal(primary, clips[0]!["path"]!.GetValue<string>());
            Assert.Equal("c2", clips[1]!["id"]!.GetValue<string>());
            Assert.Equal(second, clips[1]!["path"]!.GetValue<string>());
            Assert.Equal(primary, doc["clip"]!["path"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildDocument_UnknownTimelineSourceFailsExplicitly()
    {
        var root = TempDir();
        try
        {
            var primary = Path.Combine(root, "c1.mp4");
            File.WriteAllText(primary, "x");
            var plan = new EditorProject
            {
                Start = 0,
                End = 10,
                Timeline = new[]
                {
                    new TimelineItem("i1", "c1", 0, 10),
                    new TimelineItem("i2", "ghost", 0, 10),
                },
            };

            var error = Assert.Throws<BridgeExecutionException>(() =>
                FcprojExportService.BuildDocument("c1", primary, plan, null, 10, 1280, 720, _ => null));

            Assert.Equal("NotFound", error.Error.Class);
            Assert.Contains("ghost", error.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildDocument_FallsBackToSavedProjectWhenPlanMissing()
    {
        var root = TempDir();
        try
        {
            var primary = Path.Combine(root, "c1.mp4");
            File.WriteAllText(primary, "x");
            const string saved =
                "{\"start\":1,\"end\":5,\"timeline\":[{\"id\":\"s1\",\"clipId\":\"c1\",\"start\":1,\"end\":5}]}";

            var seen = new List<EditorProject>();
            var doc = FcprojExportService.BuildDocument("c1", primary, null, saved, 10, 1280, 720,
                _ => null, (_, project) => { seen.Add(project); return project; });

            var project = doc["project"]!.AsObject();
            Assert.Equal(1d, project["start"]!.GetValue<double>());
            Assert.Equal(5d, project["end"]!.GetValue<double>());
            Assert.Single(seen);
            Assert.Null(seen[0].CaptionOverrides);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildDocument_NormalizesOverrideBeforeMapping()
    {
        var root = TempDir();
        try
        {
            var primary = Path.Combine(root, "c1.mp4");
            File.WriteAllText(primary, "x");
            var plan = new EditorProject
            {
                Start = 0,
                End = 10,
                Timeline = new[] { new TimelineItem("i1", "c1", 0, 10) },
                CaptionOverrides = new()
                {
                    ["c1"] = new[]
                    {
                        new TranscriptSegment(1, 2, "keep"),
                        new TranscriptSegment(3, 1, "inverted"),
                        new TranscriptSegment(double.NaN, 4, "nan"),
                    },
                },
            };

            var doc = FcprojExportService.BuildDocument("c1", primary, plan, null, 10, 1280, 720, _ => null,
                (_, project) => project with
                {
                    CaptionOverrides = new()
                    {
                        ["c1"] = EditorRenderService.NormalizeCaptions(project.CaptionOverrides!["c1"]),
                    },
                });

            var captions = doc["project"]!["captionOverrides"]!["c1"]!.AsArray();
            Assert.Single(captions);
            Assert.Equal("keep", captions[0]!["text"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Export_InvalidInlinePlanReturnsValidationError_WithoutFallback()
    {
        var root = TempDir();
        try
        {
            var primary = Path.Combine(root, "c1.mp4");
            File.WriteAllText(primary, "x");
            var mapper = Path.Combine(root, "map.mjs");
            File.WriteAllText(mapper, "// stub");

            var service = new FcprojExportService(_ => primary, "node", mapper, "ffprobe", root,
                _ => "{\"start\":0,\"end\":10,\"timeline\":[{\"id\":\"old\",\"clipId\":\"c1\",\"start\":0,\"end\":10}]}");

            var invalid = new EditorProject
            {
                Start = 0,
                End = 10,
                Timeline = new[] { new TimelineItem("i1", "c1", 0, 10, Speed: 9) },
            };

            var error = Assert.Throws<BridgeExecutionException>(() =>
                service.Export("c1", null, CancellationToken.None, invalid));
            Assert.Equal("ValidationError", error.Error.Class);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildDocument_FreezeFailureIsValidationError_NotSilentFallback()
    {
        var root = TempDir();
        try
        {
            var primary = Path.Combine(root, "c1.mp4");
            File.WriteAllText(primary, "x");
            const string saved =
                "{\"start\":0,\"end\":10,\"timeline\":[{\"id\":\"old\",\"clipId\":\"c1\",\"start\":0,\"end\":10}]}";
            Func<string, EditorProject, EditorProject> failing = (_, _) => throw new ArgumentException("时间线源视频不存在");

            var savedError = Assert.Throws<BridgeExecutionException>(() =>
                FcprojExportService.BuildDocument("c1", primary, null, saved, 10, 1280, 720, _ => null, failing));
            Assert.Equal("ValidationError", savedError.Error.Class);
            Assert.Contains("时间线源视频不存在", savedError.Error.Detail!);

            var inlineError = Assert.Throws<BridgeExecutionException>(() =>
                FcprojExportService.BuildDocument("c1", primary, TrimmedPlan(), saved, 10, 1280, 720, _ => null, failing));
            Assert.Equal("ValidationError", inlineError.Error.Class);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"start\":2,\"end\":6}")]
    [InlineData("{\"start\":2,\"end\":6,\"timeline\":[]}")]
    public void BuildDocument_SavedProjectWithoutTimelineKeepsTrim(string saved)
    {
        var root = TempDir();
        try
        {
            var primary = Path.Combine(root, "c1.mp4");
            File.WriteAllText(primary, "x");

            var doc = FcprojExportService.BuildDocument("c1", primary, null, saved, 10, 1280, 720, _ => null);

            var project = doc["project"]!.AsObject();
            Assert.Equal(2d, project["start"]!.GetValue<double>());
            Assert.Equal(6d, project["end"]!.GetValue<double>());
            var timeline = project["timeline"]!.AsArray();
            Assert.Single(timeline);
            Assert.Equal(2d, timeline[0]!["start"]!.GetValue<double>());
            Assert.Equal(6d, timeline[0]!["end"]!.GetValue<double>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
