using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class EditorBaseAudioMuteTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static EditorProject Project() => new() { Start = 0, End = 1, Fps = 24, Resolution = 480 };
    private static EditorCommand Command(string args) => new("setBaseAudio", JsonNode.Parse(args));

    [Fact]
    public void LegacyJson_DefaultsToAudibleAndDoesNotAcquireMuteField()
    {
        const string legacy = """{"start":0,"end":1}""";
        var stamped = EditorProjectCodec.StampSchemaVersion(legacy);
        var project = JsonSerializer.Deserialize<EditorProject>(stamped, Json)!;
        Assert.False(project.BaseAudioMuted);
        Assert.True(EditorRenderService.IncludeBaseAudio(project.BaseAudioMuted, null, null));
        EditorRenderService.Validate(project);
        var serialized = JsonSerializer.Serialize(project, Json);
        Assert.DoesNotContain("baseAudioMuted", serialized);
        Assert.Equal(serialized, JsonSerializer.Serialize(project with { BaseAudioMuted = false }, Json));
        var roundTrip = EditorProjectCodec.StampSchemaVersion(serialized);
        Assert.Equal(EditorProjectCodec.CurrentSchemaVersion, EditorProjectCodec.EnsureSupported(roundTrip));
        Assert.False(JsonSerializer.Deserialize<EditorProject>(roundTrip, Json)!.BaseAudioMuted);
    }

    [Theory]
    [InlineData("""{"start":0,"end":1,"baseAudioMuted":true}""")]
    [InlineData("""{"schemaVersion":0,"start":0,"end":1,"baseAudioMuted":true}""")]
    [InlineData("""{"schemaVersion":1,"start":0,"end":1,"baseAudioMuted":true}""")]
    public void MutedJson_SurvivesCodecAndProjectRoundTrip(string json)
    {
        var stamped = EditorProjectCodec.StampSchemaVersion(json);
        var project = JsonSerializer.Deserialize<EditorProject>(stamped, Json)!;
        Assert.True(project.BaseAudioMuted);
        EditorRenderService.Validate(project);
        var saved = EditorProjectCodec.StampSchemaVersion(JsonSerializer.Serialize(project, Json));
        Assert.True(JsonNode.Parse(saved)!["baseAudioMuted"]!.GetValue<bool>());
        Assert.True(JsonSerializer.Deserialize<EditorProject>(saved, Json)!.BaseAudioMuted);
        Assert.Equal(EditorProjectCodec.CurrentSchemaVersion, EditorProjectCodec.EnsureSupported(saved));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("\"true\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ProjectJson_RejectsNonBooleanMute(string value)
    {
        var json = "{\"start\":0,\"end\":1,\"baseAudioMuted\":" + value + "}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EditorProject>(json, Json));
    }

    [Fact]
    public void BaseMute_ObeysAllValidAudioMasksWithoutChangingSoloOverMute()
    {
        foreach (var baseMuted in new[] { false, true })
        for (var muteMask = 0; muteMask < 8; muteMask++)
        for (var soloMask = 0; soloMask < 8; soloMask++)
        {
            var muted = new[] { 4, 5, 6 }.Where(track => (muteMask & (1 << (track - 4))) != 0).ToArray();
            var soloed = new[] { 4, 5, 6 }.Where(track => (soloMask & (1 << (track - 4))) != 0).ToArray();
            Assert.Equal(!baseMuted && soloMask == 0, EditorRenderService.IncludeBaseAudio(baseMuted, muted, soloed));
            EditorRenderService.Validate(Project() with { BaseAudioMuted = baseMuted, MutedTracks = muted, SoloedTracks = soloed });
            foreach (var track in new[] { 4, 5, 6 })
            {
                var bit = 1 << (track - 4);
                var expected = soloMask > 0 ? (soloMask & bit) != 0 : (muteMask & bit) == 0;
                Assert.Equal(expected, EditorRenderService.IncludeTrackAudio(track, muted, soloed));
            }
        }
        Assert.True(EditorRenderService.IncludeBaseAudio(false, [4, 5, 6], []));
        Assert.False(EditorRenderService.IncludeBaseAudio(false, [], [6]));
    }

    [Fact]
    public void Validate_KeepsMuteSoloAtAudioTracksOnly()
    {
        foreach (var value in new[] { false, true })
        foreach (var invalid in new[] { new[] { 1 }, new[] { 2 }, new[] { 3 }, new[] { 7 }, new[] { 4, 4 } })
        {
            Assert.Throws<ArgumentException>(() => EditorRenderService.Validate(Project() with { BaseAudioMuted = value, MutedTracks = invalid }));
            Assert.Throws<ArgumentException>(() => EditorRenderService.Validate(Project() with { BaseAudioMuted = value, SoloedTracks = invalid }));
        }
    }

    [Fact]
    public void SetBaseAudio_ChangesOnlyBaseFlagAndCanUnmute()
    {
        var initial = Project() with { MutedTracks = [4], SoloedTracks = [4], HiddenTracks = [1], LockedTracks = [1] };
        var muted = EditorCommandExecutor.Apply(initial, Command("""{"muted":true}"""), out var record);
        Assert.Equal(initial with { BaseAudioMuted = true }, muted);
        Assert.Equal("setBaseAudio", record.Command);
        Assert.False(string.IsNullOrWhiteSpace(record.OpId));
        var restored = EditorCommandExecutor.Apply(muted, Command("""{"muted":false}"""), out _);
        Assert.Equal(initial, restored);
        Assert.True(EditorRenderService.IncludeTrackAudio(4, restored.MutedTracks, restored.SoloedTracks));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"muted":null}""")]
    [InlineData("""{"muted":"true"}""")]
    [InlineData("""{"muted":1}""")]
    [InlineData("""{"muted":[]}""")]
    [InlineData("""{"muted":{}}""")]
    [InlineData("""{"muted":true,"solo":true}""")]
    public void SetBaseAudio_RejectsInvalidArgsAsCommandErrors(string args)
    {
        Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.Apply(Project(), Command(args), out _));
    }

    [Fact]
    public void SetBaseAudio_StillValidatesTheWholeProject()
    {
        var invalid = Project() with { MutedTracks = [1] };
        Assert.Throws<ArgumentException>(() => EditorCommandExecutor.Apply(invalid, Command("""{"muted":true}"""), out _));
    }

    [Fact]
    public async Task Export_BaseMuteProducesDigitalSilenceButKeepsOtherAudioAndVideo()
    {
        var tools = new MediaTools(TestFfmpeg());
        if (!tools.Available())
        {
            output.WriteLine("[skip] FFmpeg unavailable; set LIVECLIPPER_TEST_FFMPEG to run the V1 export volume check.");
            return;
        }
        try
        {
            await tools.RunAsync(tools.Ffprobe, ["-version"], CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output.WriteLine($"[skip] FFprobe unavailable: {ex.Message}");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "liveclipper-r52-base-mute-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new Store(Path.Combine(root, "test.db"));
        store.Migrate();
        var studio = new StudioStore(store.DatabasePath);
        studio.Migrate();
        var renderer = new EditorRenderService(store, studio, tools, root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        try
        {
            var source = Path.Combine(root, "source.mp4");
            await tools.RunAsync(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=white:size=160x90:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", source], token);
            var clip = new Clip("clip", "event", source, null, DateTimeOffset.UtcNow);
            store.SaveClip(clip);
            var baseItem = new TimelineItem("v1", "clip", 0, 1);
            var audio = new TimelineItem("a1", "clip", 0, 1, Track: 4);
            var overlay = new TimelineItem("v2", "clip", 0, 1, Track: 2);
            async Task<string> Render(string name, bool muted, TimelineItem[] items, int[]? mutes = null, int[]? solos = null, int[]? hidden = null)
            {
                var work = Path.Combine(root, name);
                Directory.CreateDirectory(work);
                var path = Path.Combine(work, "export.mp4");
                var plan = Project() with { Timeline = items, BaseAudioMuted = muted, MutedTracks = mutes, SoloedTracks = solos, HiddenTracks = hidden };
                await renderer.RenderAsync(clip, plan, path, work, token);
                return path;
            }
            async Task<double> MeanVolume(string path)
            {
                var log = await tools.RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostats", "-i", path, "-map", "0:a:0", "-af", "volumedetect", "-f", "null", "-"], token);
                var match = Regex.Match(log, @"mean_volume:\s*(-?\d+(?:\.\d+)?)\s+dB");
                Assert.True(match.Success, log);
                var mean = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                output.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(path))}: mean_volume={mean.ToString(CultureInfo.InvariantCulture)} dB");
                return mean;
            }
            var audible = await Render("audible", false, [baseItem]);
            var muted = await Render("base-muted", true, [baseItem]);
            var hiddenAudible = await Render("hidden-audible", false, [baseItem], hidden: [1]);
            var soloAudio = await Render("audio-solo-over-mute", true, [baseItem, audio], mutes: [4], solos: [4]);
            var overlayAudio = await Render("overlay-audio", true, [baseItem, overlay]);
            var emptySolo = await Render("empty-solo", false, [baseItem], solos: [6]);
            Assert.True(await MeanVolume(audible) > -35, "Baseline must contain audible V1 audio.");
            Assert.InRange(await MeanVolume(muted), -92, -90);
            Assert.True(await MeanVolume(hiddenAudible) > -35, "Visual hiding must not mute V1 audio.");
            Assert.True(await MeanVolume(soloAudio) > -35, "V1 M must not silence an independent soloed A1.");
            Assert.True(await MeanVolume(overlayAudio) > -35, "V1 M must not silence V2 embedded audio.");
            Assert.InRange(await MeanVolume(emptySolo), -92, -90);
            var mutedInfo = await tools.ProbeAsync(muted, token);
            Assert.True(mutedInfo.HasAudio);
            Assert.Equal(24d, mutedInfo.Fps, 3);
            var picture = await tools.RunAsync(tools.Ffmpeg, ["-hide_banner", "-i", muted, "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-an", "-frames:v", "1", "-f", "null", "-"], token);
            var luma = Regex.Match(picture, @"lavfi.signalstats.YAVG=([0-9.]+)");
            Assert.True(luma.Success, picture);
            Assert.True(double.Parse(luma.Groups[1].Value, CultureInfo.InvariantCulture) > 220, "Mute must leave the white video visible.");
        }
        finally
        {
            await renderer.StopAsync();
            Directory.Delete(root, true);
        }
    }

    private static string TestFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }
}
