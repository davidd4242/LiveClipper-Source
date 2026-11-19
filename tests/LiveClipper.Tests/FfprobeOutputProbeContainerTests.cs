using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveClipper.App;
using LiveClipper.Core;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class FfprobeOutputProbeContainerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("mp4")]
    [InlineData("mov")]
    [InlineData("mov-fragmented")]
    [InlineData("mkv")]
    [InlineData("webm")]
    public async Task HealthyContainers_HavePositiveDuration_AndPassRealOutputProbe(string container)
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return; // AvailableToolsAsync writes the [skip] reason.
        var directory = CreateDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var sample = await CreateSampleAsync(tools, directory, container, "av", timeout.Token);
            using var json = await ReadProbeJsonAsync(tools, sample, timeout.Token);
            var parsed = FfprobeOutputProbe.ReadDurationSeconds(json.RootElement);
            var probe = await new FfprobeOutputProbe(tools).ProbeAsync(sample, timeout.Token);
            AssertHealthy(probe);
            Assert.Equal(parsed, probe.DurationSeconds, 6);
            if (container is "mkv" or "webm") AssertStreamTagDurations(json.RootElement);

            var result = await VerifyPreGeneratedOutputAsync(tools, directory, sample, timeout.Token);
            Assert.NotEqual("output_zero_duration", result.Code);
            Assert.True(result.Ok, $"{container}: {result.Code}: {result.Message}; {result.Stderr}");
            Assert.Null(result.Code);
            Assert.True(result.Bytes > 0);
            output.WriteLine($"{container}: duration={probe.DurationSeconds.ToString("R", CultureInfo.InvariantCulture)}; output check passed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mov_WithoutStreamDuration_UsesRealFormatDuration()
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return; // AvailableToolsAsync writes the [skip] reason.
        var directory = CreateDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var sample = await CreateSampleAsync(tools, directory, "mov", "av", timeout.Token);
            // FFmpeg writes stream durations for MOV. This real ffprobe projection tests their absence, not a native MOV omission.
            var raw = await tools.RunAsync(tools.Ffprobe,
                ["-v", "error", "-show_entries", "stream=codec_type:format=format_name,duration", "-print_format", "json", sample], timeout.Token);
            using var projected = JsonDocument.Parse(raw);
            var root = projected.RootElement;
            var streams = root.GetProperty("streams").EnumerateArray().ToArray();
            Assert.NotEmpty(streams);
            Assert.All(streams, stream => Assert.False(stream.TryGetProperty("duration", out _)));
            var expected = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            Assert.True(expected > 0 && expected <= 2);
            Assert.Equal(expected, FfprobeOutputProbe.ReadDurationSeconds(root), 6);

            var notAvailable = JsonNode.Parse(raw)!.AsObject();
            foreach (var stream in notAvailable["streams"]!.AsArray()) stream!["duration"] = "N/A";
            using var withNa = JsonDocument.Parse(notAvailable.ToJsonString());
            Assert.Equal(expected, FfprobeOutputProbe.ReadDurationSeconds(withNa.RootElement), 6);
            AssertHealthy(await new FfprobeOutputProbe(tools).ProbeAsync(sample, timeout.Token));
            var result = await VerifyPreGeneratedOutputAsync(tools, directory, sample, timeout.Token);
            Assert.NotEqual("output_zero_duration", result.Code);
            Assert.True(result.Ok, $"{result.Code}: {result.Message}; {result.Stderr}");
            output.WriteLine($"MOV projected missing/N/A stream.duration: format.duration={expected}; real unprojected probe passed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Matroska_WithOnlyDurationTags_PassesRealOutputProbe()
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return; // AvailableToolsAsync writes the [skip] reason.
        var directory = CreateDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var sample = await CreateSampleAsync(tools, directory, "mkv", "av", timeout.Token);
            await VoidMatroskaInfoDurationAsync(sample, timeout.Token);
            using var json = await ReadProbeJsonAsync(tools, sample, timeout.Token);
            var root = json.RootElement;
            Assert.False(root.GetProperty("format").TryGetProperty("duration", out _));
            AssertStreamTagDurations(root);
            var tagSeconds = root.GetProperty("streams").EnumerateArray()
                .Select(stream => ClockSeconds(stream.GetProperty("tags").GetProperty("DURATION").GetString()!)).Max();
            Assert.True(tagSeconds > 0 && tagSeconds <= 2);
            Assert.Equal(tagSeconds, FfprobeOutputProbe.ReadDurationSeconds(root), 6);
            var probe = await new FfprobeOutputProbe(tools).ProbeAsync(sample, timeout.Token);
            AssertHealthy(probe);
            Assert.Equal(tagSeconds, probe.DurationSeconds, 6);
            var result = await VerifyPreGeneratedOutputAsync(tools, directory, sample, timeout.Token);
            Assert.NotEqual("output_zero_duration", result.Code);
            Assert.True(result.Ok, $"{result.Code}: {result.Message}; {result.Stderr}");
            output.WriteLine($"MKV real tag-only file: duration={tagSeconds}; output check passed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("no-audio", "output_no_audio")]
    [InlineData("no-video", "output_no_video")]
    [InlineData("corrupt", "output_unreadable")]
    public async Task InvalidOutputs_ReturnSpecificErrorCode_WithRealProbe(string mode, string expectedCode)
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return; // AvailableToolsAsync writes the [skip] reason.
        var directory = CreateDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var sample = await CreateSampleAsync(tools, directory, "mp4", mode, timeout.Token);
            var probe = await new FfprobeOutputProbe(tools).ProbeAsync(sample, timeout.Token);
            Assert.True(probe.Available, probe.Detail);
            if (mode == "corrupt")
            {
                Assert.False(probe.Readable, probe.Detail);
                Assert.False(probe.HasVideo);
                Assert.False(probe.HasAudio);
                Assert.Equal(0d, probe.DurationSeconds);
                Assert.False(string.IsNullOrWhiteSpace(probe.Detail));
            }
            else
            {
                Assert.True(probe.Readable, probe.Detail);
                Assert.Equal(mode != "no-video", probe.HasVideo);
                Assert.Equal(mode != "no-audio", probe.HasAudio);
                Assert.True(probe.DurationSeconds > 0 && probe.DurationSeconds <= 2, probe.Detail);
            }
            var result = await VerifyPreGeneratedOutputAsync(tools, directory, sample, timeout.Token);
            Assert.False(result.Ok);
            Assert.Equal(expectedCode, result.Code);
            Assert.NotEqual("output_zero_duration", result.Code);
            Assert.Null(result.Output);
            Assert.Equal(0L, result.Bytes);
            Assert.Equal(0, result.ExitCode);
            output.WriteLine($"{mode}: {result.Code}; readable={probe.Readable}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<MediaTools?> AvailableToolsAsync()
    {
        var tools = new MediaTools(TestFfmpeg());
        if (!tools.Available())
        {
            output.WriteLine($"[skip] ffmpeg 不可用（{tools.Ffmpeg}）；设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件。");
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
        // CoreTests.TestFfmpeg is private; keep its environment/bundled/PATH lookup order here.
        var fromEnv = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lc-real-output-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> CreateSampleAsync(MediaTools tools, string directory, string container, string mode, CancellationToken token)
    {
        var extension = container.StartsWith("mov", StringComparison.Ordinal) ? "mov" : container;
        var sample = Path.Combine(directory, $"sample {mode}.{extension}");
        if (mode == "corrupt")
        {
            await File.WriteAllTextAsync(sample, "not a media container\n", token);
            return sample;
        }
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var hasVideo = mode != "no-video";
        var hasAudio = mode != "no-audio";
        if (hasVideo) arguments.AddRange(["-f", "lavfi", "-i", "color=c=black:s=64x64:r=10:d=1"]);
        if (hasAudio) arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1"]);
        arguments.AddRange(["-t", "1"]);
        if (hasVideo)
        {
            arguments.AddRange(["-map", "0:v:0", "-pix_fmt", "yuv420p", "-threads", "1"]);
            arguments.AddRange(container == "webm"
                ? ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8"]
                : ["-c:v", "libx264", "-preset", "ultrafast"]);
        }
        else arguments.Add("-vn");
        if (hasAudio) arguments.AddRange(["-map", hasVideo ? "1:a:0" : "0:a:0", "-c:a", container == "webm" ? "libopus" : "aac"]);
        else arguments.Add("-an");
        if (container == "mov-fragmented") arguments.AddRange(["-movflags", "+empty_moov+frag_keyframe"]);
        arguments.Add(sample);
        await tools.RunAsync(tools.Ffmpeg, arguments, token);
        Assert.True(File.Exists(sample), sample);
        Assert.True(new FileInfo(sample).Length > 0, sample);
        return sample;
    }

    private static async Task<JsonDocument> ReadProbeJsonAsync(MediaTools tools, string sample, CancellationToken token)
    {
        var raw = await tools.RunAsync(tools.Ffprobe,
            ["-v", "error", "-show_error", "-show_streams", "-show_format", "-print_format", "json", sample], token);
        return JsonDocument.Parse(raw);
    }

    private static void AssertHealthy(OutputProbeResult probe)
    {
        Assert.True(probe.Available, probe.Detail);
        Assert.True(probe.Readable, probe.Detail);
        Assert.True(probe.HasVideo, probe.Detail);
        Assert.True(probe.HasAudio, probe.Detail);
        Assert.True(probe.DurationSeconds > 0 && probe.DurationSeconds <= 2, probe.Detail);
    }

    private static void AssertStreamTagDurations(JsonElement root)
    {
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        Assert.NotEmpty(streams);
        Assert.All(streams, stream =>
        {
            Assert.False(stream.TryGetProperty("duration", out _));
            var clock = stream.GetProperty("tags").GetProperty("DURATION").GetString();
            Assert.False(string.IsNullOrWhiteSpace(clock));
            Assert.True(ClockSeconds(clock!) > 0);
        });
    }

    private static double ClockSeconds(string text)
    {
        var parts = text.Split(':');
        Assert.Equal(3, parts.Length);
        return int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600d
            + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60d
            + double.Parse(parts[2], CultureInfo.InvariantCulture);
    }

    private static async Task VoidMatroskaInfoDurationAsync(string path, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(path, token);
        var matches = new List<int>();
        for (var i = 0; i <= bytes.Length - 11; i++)
            if (bytes[i] == 0x44 && bytes[i + 1] == 0x89 && bytes[i + 2] == 0x88) matches.Add(i);
        var offset = Assert.Single(matches);
        // Equal-sized EBML Void removes Info.Duration without shifting clusters, seek offsets, or stream tags.
        Array.Clear(bytes, offset, 11);
        bytes[offset] = 0xec;
        bytes[offset + 1] = 0x89;
        await File.WriteAllBytesAsync(path, bytes, token);
    }

    private static async Task<FilmCraftExportResult> VerifyPreGeneratedOutputAsync(
        MediaTools tools, string directory, string sample, CancellationToken token)
    {
        var project = Path.Combine(directory, "project.fcproj");
        await File.WriteAllTextAsync(project, "{}", token);
        var isMov = Path.GetExtension(sample).Equals(".mov", StringComparison.OrdinalIgnoreCase);
        var checkedOutput = Path.Combine(directory, "checked output" + (isMov ? ".mov" : ".mp4"));
        // ExportAsync admits only MP4/MOV names. Copy MKV/WebM bytes unchanged; ffprobe detects the container from its contents.
        File.Copy(sample, checkedOutput);
        var request = new FilmCraftExportRequest(project, checkedOutput, End: 1, Format: isMov ? "prores" : "h264", ExpectAudio: true);
        var noOpCli = new ProcessStartInfo(tools.Ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        noOpCli.ArgumentList.Add("-version");
        // Only process discovery is substituted. The real probe and private VerifyOutputAsync run unchanged.
        return await FilmCraftCliRunner.ExportAsync(request, token, () => noOpCli, new FfprobeOutputProbe(tools));
    }
}
