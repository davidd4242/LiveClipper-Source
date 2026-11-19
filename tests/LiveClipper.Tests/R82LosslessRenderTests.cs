using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class R82LosslessRenderTests(ITestOutputHelper output)
{
    [Fact]
    public void HdrInputMetadata_CoversEveryMediaLavfiAndAudioInputWithoutChangingFinalCodec()
    {
        var plan = new EditorProject { Format = "dnxhr", ColorSpace = "hdr10" };
        List<string> args = ["-f", "lavfi", "-i", "color=s=320x180", "-i", "video.mkv", "-i", "audio.mka",
            "-filter_complex", "[0:v][1:v]overlay=x=0:y=0[v]", .. EditorRenderService.IntermediateVideoCodecArgs(plan), "out.mkv"];
        EditorRenderService.ConfigureIntermediateArgs(args, plan);
        var inputs = args.Select((value, index) => (value, index)).Where(x => x.value == "-i").ToArray();
        Assert.Equal(3, inputs.Length);
        foreach (var (_, index) in inputs)
            Assert.Equal(new[] { "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc" }, args.Skip(index - 6).Take(6));
        Assert.Contains("overlay=format=yuv420p10:", args[args.IndexOf("-filter_complex") + 1]);
        Assert.Contains("dnxhr_hqx", EditorRenderService.VideoCodecArgs("dnxhr", 0, colorSpace: "hdr10"));
        Assert.DoesNotContain("ffv1", EditorRenderService.VideoCodecArgs("dnxhr", 0, colorSpace: "hdr10"));
    }

    [Fact]
    public async Task Real_PcmAndPngKeepSourcePrecisionIncludingAlphaAndExactLengths()
    {
        if (!NativeEnabled()) return;
        await using var f = await Fixture.Create("precision", output);
        var wav = await f.Render("wav", f.Plan("wavlpcm"), [f.Item("one")]);
        var original = await f.Pcm(f.Source, "original", 1);
        var actual = await f.Pcm(wav.Output, "final-wav");
        f.AssertPcm(original, actual, "source -> final PCM");
        await f.CheckIntermediates(wav.Work);
        var png = await f.Render("png", f.Plan("png"), [f.Item("one")]);
        var frames = Path.Combine(f.Root, "png-frames");
        ZipFile.ExtractToDirectory(png.Output, frames);
        Assert.Equal(30, Directory.GetFiles(frames, "*.png").Length);
        var firstName = Path.GetFileName(Directory.GetFiles(frames, "*.png").Order().First());
        var pattern = System.Text.RegularExpressions.Regex.Replace(firstName, @"\d+(?=\.png$)", "%06d");
        var rendered = await f.Raw(Path.Combine(frames, pattern), "png", "rgba", ["-framerate", "30"], 30);
        var reference = await f.Raw(f.Source, "source-rgba", "rgba", frames: 30);
        f.AssertPixels(reference, rendered, 4, "all 30 PNG frames", alpha: true);
        await f.CheckIntermediates(png.Work);
    }

    [Fact]
    public async Task Real_ConcatAndAudioOnlyComposeRemainSampleAndPixelExact()
    {
        if (!NativeEnabled()) return;
        await using var f = await Fixture.Create("concat-compose", output);
        var assembled = await f.Assemble("concat", f.Plan("png") with { HiddenTracks = [2] },
            [f.Item("first", 0, .5), f.Item("second", .5, 1)]);
        var expectedPcm = await f.Pcm(f.Source, "source", 1);
        f.AssertPcm(expectedPcm, await f.Pcm(assembled.Video, "composed"), "concat+compose PCM");
        f.AssertPixels(await f.Raw(f.Source, "source", "rgba", frames: 30),
            await f.Raw(assembled.Video, "composed", "rgba"), 4, "concat+compose pixels", alpha: true);
        await f.CheckIntermediates(assembled.Work);
        Assert.True(File.Exists(Path.Combine(assembled.Work, "joined.mkv")));
        Assert.True(File.Exists(Path.Combine(assembled.Work, "composed.mkv")));
        // A1 solo must preserve the audio-only path without doubling base audio or retaining AAC.
        var solo = await f.Assemble("audio-solo", f.Plan("wavlpcm") with { SoloedTracks = [4] },
            [f.Item("base"), f.Item("audio-first", 0, .5) with { Track = 4 }, f.Item("audio-second", .5, 1) with { Track = 4 }]);
        f.AssertPcm(expectedPcm, await f.Pcm(solo.Video, "solo"), "A1 solo PCM");
        await f.CheckIntermediates(solo.Work);
        Assert.True(File.Exists(Path.Combine(solo.Work, "audio-000.mka")));
        var mute = await f.Assemble("mute", f.Plan("wavlpcm") with { BaseAudioMuted = true }, [f.Item("base")]);
        var silence = await f.Pcm(mute.Video, "muted");
        Assert.Equal(expectedPcm.Length, silence.Length);
        Assert.All(silence, value => Assert.Equal(0f, value));
        f.Log($"muted samples={silence.Length}; maxAbs=0");
        await f.CheckIntermediates(mute.Work);
        var overlay = await f.Assemble("overlay-cut", f.Plan("wavlpcm") with { BaseAudioMuted = true },
            [f.Item("base"), f.Item("overlay-first", 0, .5) with { Track = 2 }, f.Item("overlay-second", .5, 1) with { Track = 2 }]);
        f.AssertPcm(expectedPcm, await f.Pcm(overlay.Video, "overlay-audio"), "split overlay PCM");
        f.AssertPcm(expectedPcm.Skip(48000).ToArray(), await f.Pcm(Path.Combine(overlay.Work, "overlay-001.mkv"), "overlay-second"), "nonzero-start overlay PCM");
        await f.CheckIntermediates(overlay.Work);
    }

    [Fact]
    public async Task Real_RgbAlphaSourceSurvivesNoEffectConcatAndCompose()
    {
        if (!NativeEnabled()) return;
        await using var f = await Fixture.Create("alpha-concat", output, alpha: true);
        var assembled = await f.Assemble("alpha", f.Plan("png") with { HiddenTracks = [2] },
            [f.Item("first", 0, .5), f.Item("second", .5, 1)]);
        var reference = await f.Raw(f.Source, "source-alpha", "rgba", frames: 30);
        Assert.Contains(reference.Where((_, index) => index % 4 == 3), value => value is > 0 and < 255);
        f.AssertPixels(reference, await f.Raw(assembled.Video, "joined-alpha", "rgba"), 4, "RGBA concat+compose", alpha: true);
        f.AssertPcm(await f.Pcm(f.Source, "source-audio", 1), await f.Pcm(assembled.Video, "alpha-audio"), "RGBA source PCM");
        await f.CheckIntermediates(assembled.Work);
    }

    [Fact]
    public async Task Real_MovingWipeTransitionHasContinuousFramesAndReferenceAudio()
    {
        if (!NativeEnabled()) return;
        await using var f = await Fixture.Create("moving-transition", output);
        var plan = f.Plan("png") with { HiddenTracks = [2], Transitions = [new("wipe", "right", 1, "wipe-left", .2, "center")] };
        var assembled = await f.Assemble("wipe", plan, [f.Item("left"), f.Item("right") with { Filter = "mono" }]);
        Assert.Equal(1.8, assembled.Length, 6);
        var referenceFile = Path.Combine(f.Root, "reference.f32");
        await f.Run(f.Tools.Ffmpeg, ["-v", "error", "-y", "-t", "1", "-i", f.Source, "-t", "1", "-i", f.Source,
            "-filter_complex", "[0:a]apad,atrim=end=1,asetpts=PTS-STARTPTS[a];[1:a]apad,atrim=end=1,asetpts=PTS-STARTPTS[b];[a][b]acrossfade=d=0.2:c1=tri:c2=tri[out]",
            "-map", "[out]", "-c:a", "pcm_f32le", "-ar", "48000", "-ac", "2", "-f", "f32le", referenceFile]);
        var reference = Floats(await File.ReadAllBytesAsync(referenceFile));
        Assert.Equal(172800, reference.Length);
        f.AssertPcm(reference, await f.Pcm(assembled.Video, "wipe"), "wipe acrossfade continuity");
        var raw = await f.Raw(assembled.Video, "wipe", "rgb24");
        var info = await f.Tools.ProbeAsync(assembled.Video, f.Token);
        Assert.Equal(info.Width * info.Height * 3 * 54, raw.Length);
        var probe = await f.Probe(assembled.Video, frames: true);
        var frameData = probe.GetProperty("frames").EnumerateArray().Where(x => x.GetProperty("media_type").GetString() == "video").ToArray();
        Assert.Equal(54, frameData.Length);
        for (var i = 1; i < frameData.Length; i++)
        {
            var previous = decimal.Parse(frameData[i - 1].GetProperty("best_effort_timestamp_time").GetString()!, CultureInfo.InvariantCulture);
            var next = decimal.Parse(frameData[i].GetProperty("best_effort_timestamp_time").GetString()!, CultureInfo.InvariantCulture);
            Assert.InRange(next - previous, .032m, .034m);
        }
        var frameBytes = info.Width * info.Height * 3;
        Assert.False(raw.AsSpan(24 * frameBytes, frameBytes).SequenceEqual(raw.AsSpan(27 * frameBytes, frameBytes)), "Moving transition must change pixels inside its interval.");
        f.Log("wipe-left: 54 continuous frames, 172800 interleaved PCM samples; transition frames differ");
        await f.CheckIntermediates(assembled.Work);
    }

    [Fact]
    public async Task Real_SdrGapAndDisabledPartDecodeAcrossMixedPixelFormats()
    {
        if (!NativeEnabled()) return;
        await using var f = await Fixture.Create("sdr-gap", output);
        var result = await f.Assemble("gap", f.Plan("png"),
            [f.Item("visible", 0, .5) with { GapBefore = .2 }, f.Item("disabled", .5, 1) with { Disabled = true }]);
        await f.CheckIntermediates(result.Work);
        var info = await f.Tools.ProbeAsync(result.Video, f.Token);
        var full = await f.Raw(result.Video, "full", "rgba");
        var source = await f.Raw(f.Source, "source", "rgba", frames: 15);
        var bytesPerFrame = info.Width * info.Height * 4;
        Assert.Equal(bytesPerFrame * 36, full.Length);
        f.AssertPixels(source, full.AsSpan(6 * bytesPerFrame, 15 * bytesPerFrame).ToArray(), 4, "visible after gap", alpha: true);
        foreach (var frame in new[] { 0, 5, 21, 35 })
            for (var i = frame * bytesPerFrame; i < (frame + 1) * bytesPerFrame; i += 4)
            { Assert.Equal(0, full[i]); Assert.Equal(0, full[i + 1]); Assert.Equal(0, full[i + 2]); Assert.Equal(255, full[i + 3]); }
        var pcm = await f.Pcm(result.Video, "gap");
        Assert.Equal(115200, pcm.Length);
        Assert.All(pcm.Take(19200).Concat(pcm.Skip(67200)), sample => Assert.Equal(0f, sample));
        f.AssertPcm(await f.Pcm(f.Source, "source", .5), pcm.Skip(19200).Take(48000).ToArray(), "PCM after black gap");
    }

    [Theory]
    [InlineData("dnxhr")]
    [InlineData("dnxhr-hqx")]
    public async Task Real_HdrGraphicsAndEveryLavfiStageKeepTagsAndTenBitPixels(string format)
    {
        if (!NativeEnabled()) return;
        await using var f = await Fixture.Create("hdr-" + format, output, gradient: true);
        var plan = f.Plan(format) with { ColorSpace = "hdr10", Fps = 30 };
        var graphic = new TimelineItem("graphic", "", 0, 1, "graphic", Track: 3, TitleText: "R82", TitleSize: 24, TitleX: .8, TitleY: .8)
        { GraphicKind = "box", GraphicBoxColor = "#FF0000", GraphicBoxOpacity = .5, GraphicBoxX = .1, GraphicBoxY = .1, GraphicBoxW = .5, GraphicBoxH = .5, GraphicLineThickness = 20, PreviewOnly = false };
        var rendered = await f.Render("graphics", plan, [f.Item("base"), f.Item("overlay") with { Track = 2, Opacity = .25 }, graphic]);
        await f.CheckIntermediates(rendered.Work, hdr: true);
        foreach (var path in Directory.GetFiles(rendered.Work, "*.mkv").Append(rendered.Output))
            await f.AssertTenBit(path, gradient: true);
        var final = await f.Probe(rendered.Output);
        var video = final.GetProperty("streams").EnumerateArray().Single(x => x.GetProperty("codec_type").GetString() == "video");
        Assert.Equal("dnxhd", video.GetProperty("codec_name").GetString());
        Assert.Equal("DNXHR HQX", video.GetProperty("profile").GetString());
        Assert.Equal("yuv422p10le", video.GetProperty("pix_fmt").GetString());
        var hidden = await f.Assemble("gap-hidden", plan,
            [f.Item("disabled", 0, .5) with { Disabled = true, GapBefore = .2 }, f.Item("visible", .5, 1)]);
        await f.CheckIntermediates(hidden.Work, hdr: true);
        foreach (var path in Directory.GetFiles(hidden.Work, "*.mkv")) await f.AssertTenBit(path, gradient: false);
        var only = await f.Assemble("graphics-only", plan with { Resolution = 480 }, [graphic]);
        await f.CheckIntermediates(only.Work, hdr: true);
        foreach (var path in Directory.GetFiles(only.Work, "*.mkv")) await f.AssertTenBit(path, gradient: false);
    }

    [Theory]
    [InlineData("h264", "h264", "yuv420p")]
    [InlineData("hevc", "hevc", "yuv420p")]
    [InlineData("prores", "prores", "yuv422p10le")]
    public async Task Real_FinalProductCodecRemainsIndependent(string format, string codec, string pixelFormat)
    {
        if (!NativeEnabled()) return;
        await using var f = await Fixture.Create("final-" + format, output);
        var result = await f.Render(format, f.Plan(format), [f.Item("one")]);
        await f.CheckIntermediates(result.Work);
        var probe = await f.Probe(result.Output);
        var video = probe.GetProperty("streams").EnumerateArray().Single(x => x.GetProperty("codec_type").GetString() == "video");
        Assert.Equal(codec, video.GetProperty("codec_name").GetString());
        Assert.Equal(pixelFormat, video.GetProperty("pix_fmt").GetString());
    }

    private bool NativeEnabled()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG"))) return true;
        output.WriteLine("[native not enabled] Set LIVECLIPPER_TEST_FFMPEG; this result is not native verification evidence.");
        return false;
    }

    private static float[] Floats(byte[] bytes)
    {
        Assert.Equal(0, bytes.Length % 4);
        var result = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length); return result;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));
        private readonly bool preserve;
        public string Root { get; }
        public string Source { get; private set; } = "";
        public MediaTools Tools { get; }
        public EditorRenderService Renderer { get; private set; } = null!;
        public Clip Clip { get; private set; } = null!;
        public CancellationToken Token => timeout.Token;
        private Fixture(string name, ITestOutputHelper output)
        {
            this.output = output;
            var evidence = Environment.GetEnvironmentVariable("LIVECLIPPER_R82_EVIDENCE");
            preserve = !string.IsNullOrWhiteSpace(evidence);
            Root = Path.Combine(evidence ?? Path.GetTempPath(), "r82-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var real = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG")!;
            Assert.True(File.Exists(real), "Explicit native ffmpeg path must exist: " + real);
            Tools = new MediaTools(Environment.GetEnvironmentVariable("LIVECLIPPER_R82_TRACE_FFMPEG") ?? real);
            Assert.True(Tools.Available());
            Log("ffmpeg=" + Tools.Ffmpeg + "; original=" + real);
        }
        public static async Task<Fixture> Create(string name, ITestOutputHelper output, bool gradient = false, bool alpha = false)
        {
            var f = new Fixture(name, output);
            var supplied = Environment.GetEnvironmentVariable("LIVECLIPPER_R82_SOURCE");
            f.Source = !gradient && !alpha && !string.IsNullOrEmpty(supplied) ? supplied : Path.Combine(f.Root, "source.mkv");
            if (f.Source == supplied) Assert.True(File.Exists(f.Source));
            else await f.Run(f.Tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", gradient
                    ? "nullsrc=s=1024x128:r=30,format=yuv420p10le,geq=lum='64+floor(X*876/1023)':cb=512:cr=512"
                    : alpha ? "testsrc=s=320x180:r=30,format=rgba,colorchannelmixer=aa=0.5" : "testsrc2=s=320x180:r=30,noise=alls=6:allf=t:all_seed=27",
                "-f", "lavfi", "-i", "aevalsrc=0.2*sin(2*PI*440*t)|0.15*sin(2*PI*660*t):s=48000", "-t", "2",
                "-c:v", "ffv1", "-level", "3", "-c:a", "pcm_f32le", f.Source]);
            var store = new Store(Path.Combine(f.Root, "test.db")); store.Migrate();
            var studio = new StudioStore(store.DatabasePath); studio.Migrate();
            f.Renderer = new EditorRenderService(store, studio, f.Tools, f.Root);
            f.Clip = new Clip("clip", "event", f.Source, null, DateTimeOffset.UtcNow); store.SaveClip(f.Clip);
            return f;
        }
        public EditorProject Plan(string format) => new() { Start = 0, End = 1, Format = format, Resolution = 0, Fps = 30 };
        public TimelineItem Item(string id, double start = 0, double end = 1) => new(id, Clip.Id, start, end);
        public void Log(string text) { output.WriteLine(text); File.AppendAllText(Path.Combine(Root, "native.log"), DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine); }
        public async Task<string> Run(string executable, string[] args)
        {
            Log(executable + " " + JsonSerializer.Serialize(args));
            try { var result = await Tools.RunAsync(executable, args, Token); Log("exit=0 " + result); return result; }
            catch (Exception ex) { Log("ERROR " + ex); throw; }
        }
        public async Task<(string Work, string Output)> Render(string name, EditorProject plan, TimelineItem[] items)
        {
            var work = Path.Combine(Root, name); var path = Path.Combine(Root, name + EditorRenderService.OutputExtension(plan.Format));
            var watch = Stopwatch.StartNew();
            Log("Render " + name + " " + JsonSerializer.Serialize(plan with { Timeline = items }));
            await Renderer.RenderAsync(Clip, plan with { Timeline = items }, path, work, Token);
            Log($"render elapsedMs={watch.ElapsedMilliseconds}; finalBytes={new FileInfo(path).Length}; intermediateBytes={Directory.GetFiles(work, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length)}");
            return (work, path);
        }
        public async Task<(string Video, string Work, double Length)> Assemble(string name, EditorProject plan, TimelineItem[] items)
        {
            var work = Path.Combine(Root, name); Log("Assemble " + name + " " + JsonSerializer.Serialize(plan with { Timeline = items }));
            var result = await Renderer.AssembleAsync(Clip, plan with { Timeline = items }, work, Token);
            return (result.Video, work, result.Length);
        }
        public async Task<JsonElement> Probe(string path, bool frames = false)
        {
            var text = await Run(Tools.Ffprobe, frames ? ["-v", "error", "-show_frames", "-show_entries", "frame=media_type,best_effort_timestamp_time", "-of", "json", path]
                : ["-v", "error", "-show_streams", "-show_format", "-of", "json", path]);
            using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone();
        }
        public async Task<float[]> Pcm(string path, string name, double? length = null)
        {
            var raw = Path.Combine(Root, name + ".f32");
            await Run(Tools.Ffmpeg, ["-v", "error", "-y", "-i", path, ..(length is { } d ? new[] { "-t", d.ToString(CultureInfo.InvariantCulture) } : []),
                "-map", "0:a:0", "-c:a", "pcm_f32le", "-ar", "48000", "-ac", "2", "-f", "f32le", raw]);
            return Floats(await File.ReadAllBytesAsync(raw, Token));
        }
        public async Task<byte[]> Raw(string path, string name, string pixelFormat, string[]? inputArgs = null, int? frames = null)
        {
            var raw = Path.Combine(Root, name + "." + pixelFormat);
            await Run(Tools.Ffmpeg, ["-v", "error", "-y", ..(inputArgs ?? []), "-i", path, ..(frames is { } count ? new[] { "-frames:v", count.ToString() } : []),
                "-pix_fmt", pixelFormat, "-f", "rawvideo", raw]);
            return await File.ReadAllBytesAsync(raw, Token);
        }
        public void AssertPcm(float[] expected, float[] actual, string name)
        {
            Assert.NotEmpty(expected); Assert.Equal(expected.Length, actual.Length);
            var maximum = expected.Zip(actual).Max(pair => Math.Abs((double)pair.First - pair.Second));
            Log($"{name}: samples={actual.Length}; maxAbs={maximum:R}"); Assert.InRange(maximum, 0, 1e-6);
        }
        public void AssertPixels(byte[] expected, byte[] actual, int channels, string name, bool alpha)
        {
            Assert.NotEmpty(expected); Assert.Equal(expected.Length, actual.Length);
            var maximum = 0; var alphaMaximum = 0;
            for (var i = 0; i < expected.Length; i++)
                if (alpha && i % channels == channels - 1) alphaMaximum = Math.Max(alphaMaximum, Math.Abs(expected[i] - actual[i]));
                else maximum = Math.Max(maximum, Math.Abs(expected[i] - actual[i]));
            Log($"{name}: bytes={actual.Length}; maxPixel={maximum}; alphaMax={alphaMaximum}");
            Assert.InRange(maximum, 0, 1); Assert.Equal(0, alphaMaximum);
        }
        public async Task CheckIntermediates(string work, bool hdr = false)
        {
            Assert.Empty(Directory.GetFiles(work, "*.mp4")); Assert.Empty(Directory.GetFiles(work, "*.m4a"));
            var files = Directory.GetFiles(work).Where(p => p.EndsWith(".mkv") || p.EndsWith(".mka")).ToArray(); Assert.NotEmpty(files);
            foreach (var path in files)
            {
                var probe = await Probe(path);
                Assert.Contains("matroska", probe.GetProperty("format").GetProperty("format_name").GetString()!);
                foreach (var stream in probe.GetProperty("streams").EnumerateArray())
                {
                    var type = stream.GetProperty("codec_type").GetString();
                    if (type == "video") { Assert.Equal("ffv1", stream.GetProperty("codec_name").GetString()); if (hdr) AssertTags(stream); }
                    if (type == "audio") { Assert.Equal("pcm_f32le", stream.GetProperty("codec_name").GetString()); Assert.Equal("48000", stream.GetProperty("sample_rate").GetString()); Assert.Equal(2, stream.GetProperty("channels").GetInt32()); }
                }
            }
        }
        private static void AssertTags(JsonElement video)
        {
            Assert.Equal("bt2020nc", video.GetProperty("color_space").GetString());
            Assert.Equal("smpte2084", video.GetProperty("color_transfer").GetString());
            Assert.Equal("bt2020", video.GetProperty("color_primaries").GetString());
        }
        public async Task AssertTenBit(string path, bool gradient)
        {
            var probe = await Probe(path);
            var video = probe.GetProperty("streams").EnumerateArray().Single(x => x.GetProperty("codec_type").GetString() == "video");
            AssertTags(video); Assert.Contains(video.GetProperty("pix_fmt").GetString(), new[] { "yuv420p10le", "yuv422p10le", "yuv422p12le" });
            if (!gradient) return;
            var bytes = await Raw(path, Path.GetFileName(path) + "-luma", "yuv420p10le", frames: 1);
            var width = video.GetProperty("width").GetInt32(); var height = video.GetProperty("height").GetInt32();
            var levels = new HashSet<int>(); var lowBits = 0;
            for (var i = 0; i < width * height; i++) { var value = bytes[2 * i] | (bytes[2 * i + 1] << 8); levels.Add(value); if (value % 4 != 0) lowBits++; }
            Log(Path.GetFileName(path) + $": uniqueLumaLevels={levels.Count}; nonZeroLowBits={lowBits}");
            Assert.True(levels.Count > 256, "Lost 10-bit gradient: " + path); Assert.True(lowBits > 0, "Zeroed low bits: " + path);
        }
        public async ValueTask DisposeAsync()
        {
            await Renderer.StopAsync(); timeout.Dispose();
            if (!preserve) Directory.Delete(Root, recursive: true);
        }
    }
}
