using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class EditorExportRealFfmpegTests(ITestOutputHelper output)
{
    private const int Width = 160;
    private const int Height = 90;
    private const int Fps = 24;
    private const double Seconds = 2;

    [Theory]
    [InlineData("h264", null, "yuv420p", null, null, null)]
    [InlineData("hevc", null, "yuv420p", null, null, null)]
    [InlineData("hevc", "bt709", "yuv420p", "bt709", "bt709", "bt709")]
    [InlineData("hevc", "hdr10", "yuv420p10le", "bt2020", "smpte2084", "bt2020nc")]
    public async Task Render_FinalFileHasRequestedCodecStreamsDurationAndColor(
        string format, string? colorSpace, string pixelFormat,
        string? primaries, string? transfer, string? matrix)
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var root = TemporaryRoot();
        EditorRenderService? renderer = null;
        try
        {
            var store = new Store(Path.Combine(root, "test.db"));
            store.Migrate();
            var studio = new StudioStore(store.DatabasePath);
            studio.Migrate();
            renderer = new EditorRenderService(store, studio, tools, root);
            var clip = await CreateSourceAsync(tools, store, root, "tone", silent: false, token: token);
            var plan = Plan(format, [new TimelineItem("v1", clip.Id, 0, Seconds)]) with
            {
                ColorSpace = colorSpace
            };

            // AssembleAsync always produces H.264 intermediates; only RenderAsync applies the requested export codec.
            var path = await RenderAsync(renderer, clip, plan, root, "codec", token);
            var video = await AssertFinalMediaAsync(tools, path, format, token);
            Assert.Equal(pixelFormat, video.GetProperty("pix_fmt").GetString());
            if (format == "hevc")
            {
                Assert.Equal("hvc1", video.GetProperty("codec_tag_string").GetString());
                Assert.Equal(93, video.GetProperty("level").GetInt32());
            }
            if (primaries is not null)
            {
                Assert.Equal(primaries, video.GetProperty("color_primaries").GetString());
                Assert.Equal(transfer, video.GetProperty("color_transfer").GetString());
                Assert.Equal(matrix, video.GetProperty("color_space").GetString());
            }
            Assert.True(await MeanVolumeAsync(tools, path, token) > -30,
                "The codec export lost the source's audible tone.");
        }
        finally
        {
            if (renderer is not null) await renderer.StopAsync();
            DeleteTemporaryRoot(root);
        }
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("hevc")]
    public async Task Render_FinalAudioHonorsMuteSoloPriorityAndExcludesNonSoloTracks(string format)
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var root = TemporaryRoot();
        EditorRenderService? renderer = null;
        try
        {
            var store = new Store(Path.Combine(root, "test.db"));
            store.Migrate();
            var studio = new StudioStore(store.DatabasePath);
            studio.Migrate();
            renderer = new EditorRenderService(store, studio, tools, root);
            var tone = await CreateSourceAsync(tools, store, root, "tone", silent: false, token: token);
            var silence = await CreateSourceAsync(tools, store, root, "silence", silent: true, token: token);
            var detachedBase = new TimelineItem("v1", tone.Id, 0, Seconds, AudioDetached: true);
            var audibleA1 = new TimelineItem("a1", tone.Id, 0, Seconds, Track: 4);
            var baseline = Plan(format, [detachedBase, audibleA1]);

            async Task<double> ExportMeanAsync(string name, EditorProject plan)
            {
                var path = await RenderAsync(renderer, tone, plan, root, name, token);
                await AssertFinalMediaAsync(tools, path, format, token);
                return await MeanVolumeAsync(tools, path, token);
            }

            var unmutedMean = await ExportMeanAsync("unmuted", baseline);
            var mutedMean = await ExportMeanAsync("muted", baseline with { MutedTracks = [4] });
            var soloWinsMean = await ExportMeanAsync("solo-wins", baseline with
            {
                MutedTracks = [4], SoloedTracks = [4]
            });
            // V1 and A2 both contain a tone. A silent, soloed-and-muted A1 must exclude both real sound sources.
            var soloOnlyMean = await ExportMeanAsync("silent-solo", Plan(format,
            [
                detachedBase with { AudioDetached = false },
                new TimelineItem("a1", silence.Id, 0, Seconds, Track: 4),
                new TimelineItem("a2", tone.Id, 0, Seconds, Track: 5)
            ]) with { MutedTracks = [4], SoloedTracks = [4] });

            Assert.True(unmutedMean > -30, $"Unmuted A1 was not audible: {unmutedMean} dB.");
            Assert.True(mutedMean < -70, $"Muted A1 leaked into the final file: {mutedMean} dB.");
            Assert.True(mutedMean < unmutedMean - 45,
                $"Mute did not remove the measured tone: on={unmutedMean}, off={mutedMean} dB.");
            Assert.True(soloWinsMean > -30, $"Same-track solo did not override mute: {soloWinsMean} dB.");
            Assert.InRange(soloWinsMean - unmutedMean, -1d, 1d);
            Assert.True(soloOnlyMean < -70,
                $"Non-solo V1 or A2 leaked while silent A1 was soloed: {soloOnlyMean} dB.");
        }
        finally
        {
            if (renderer is not null) await renderer.StopAsync();
            DeleteTemporaryRoot(root);
        }
    }

    private static EditorProject Plan(string format, TimelineItem[] items) => new()
    {
        Start = 0, End = Seconds, Timeline = items, Format = format,
        Resolution = 480, Fps = Fps
    };

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "liveclipper-real-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private void DeleteTemporaryRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException ex) { output.WriteLine($"[cleanup] {root}: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { output.WriteLine($"[cleanup] {root}: {ex.Message}"); }
    }

    private static async Task<Clip> CreateSourceAsync(
        MediaTools tools, Store store, string root, string id, bool silent, CancellationToken token)
    {
        var path = Path.Combine(root, id + ".mp4");
        await tools.RunAsync(tools.Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={Width}x{Height}:rate={Fps}",
            "-f", "lavfi", "-i", silent ? "anullsrc=r=48000:cl=stereo" : "sine=frequency=440:sample_rate=48000",
            "-t", Seconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k", path
        ], token);
        var clip = new Clip(id, "event-" + id, path, null, DateTimeOffset.UtcNow);
        store.SaveClip(clip);
        return clip;
    }

    private static async Task<string> RenderAsync(
        EditorRenderService renderer, Clip clip, EditorProject plan, string root, string name, CancellationToken token)
    {
        var path = Path.Combine(root, name + ".mp4");
        var work = Path.Combine(root, name + "-work");
        await renderer.RenderAsync(clip, plan, path, work, token);
        Assert.True(File.Exists(path), "RenderAsync did not create " + path);
        Assert.True(new FileInfo(path).Length > 0, "RenderAsync created an empty file: " + path);
        return path;
    }

    private async Task<JsonElement> AssertFinalMediaAsync(
        MediaTools tools, string path, string expectedCodec, CancellationToken token)
    {
        var log = await tools.RunAsync(tools.Ffprobe,
        [
            "-v", "error", "-show_entries",
            "stream=codec_name,codec_type,codec_tag_string,width,height,avg_frame_rate,pix_fmt,level,color_primaries,color_transfer,color_space,duration:format=duration",
            "-of", "json", path
        ], token);
        output.WriteLine($"ffprobe {Path.GetFileName(path)}: {log}");
        using var doc = JsonDocument.Parse(log);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = Assert.Single(streams.Where(x => x.GetProperty("codec_type").GetString() == "video"));
        var audio = Assert.Single(streams.Where(x => x.GetProperty("codec_type").GetString() == "audio"));
        Assert.Equal(expectedCodec, video.GetProperty("codec_name").GetString());
        Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
        Assert.Equal(Width, video.GetProperty("width").GetInt32());
        Assert.Equal(Height, video.GetProperty("height").GetInt32());
        Assert.Equal("24/1", video.GetProperty("avg_frame_rate").GetString());
        AssertDuration(doc.RootElement.GetProperty("format"));
        AssertDuration(video);
        AssertDuration(audio);
        return video.Clone();
    }

    private static void AssertDuration(JsonElement element)
    {
        var duration = double.Parse(element.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(duration, Seconds - .1, Seconds + .1);
    }

    private async Task<double> MeanVolumeAsync(MediaTools tools, string path, CancellationToken token)
    {
        var log = await tools.RunAsync(tools.Ffmpeg,
        [
            "-hide_banner", "-nostats", "-loglevel", "info", "-i", path,
            "-map", "0:a:0", "-vn", "-af", "volumedetect", "-f", "null", "-"
        ], token);
        // FFmpeg 9 can log an unused filter instance with n_samples=0 before the final measurement.
        var samples = Regex.Matches(log, @"n_samples:\s*(\d+)");
        Assert.True(samples.Count > 0, "volumedetect did not report decoded samples: " + log);
        var sampleCount = samples[samples.Count - 1].Groups[1].Value;
        Assert.True(long.Parse(sampleCount, CultureInfo.InvariantCulture) > 0,
            "volumedetect measured no audio samples: " + log);
        var match = Regex.Match(log, @"mean_volume:\s*(-?inf|[+-]?[0-9]+(?:\.[0-9]+)?)\s*dB", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "volumedetect did not report mean_volume: " + log);
        var value = match.Groups[1].Value;
        var mean = value.Equals("-inf", StringComparison.OrdinalIgnoreCase)
            ? double.NegativeInfinity
            : double.Parse(value, CultureInfo.InvariantCulture);
        output.WriteLine($"volumedetect {Path.GetFileName(path)}: n_samples={sampleCount}, mean_volume={value} dB");
        return mean;
    }

    private async Task<MediaTools?> AvailableToolsAsync()
    {
        var tools = new MediaTools(TestFfmpeg());
        if (!tools.Available())
        {
            output.WriteLine($"[skip] 未找到可运行的 ffmpeg（{tools.Ffmpeg}）；设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件。");
            return null;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await tools.RunAsync(tools.Ffprobe, ["-version"], timeout.Token);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            output.WriteLine($"[skip] ffprobe 不可用（{tools.Ffprobe}）：{ex.Message}");
            return null;
        }
        return tools;
    }

    private static string TestFfmpeg()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }
}
