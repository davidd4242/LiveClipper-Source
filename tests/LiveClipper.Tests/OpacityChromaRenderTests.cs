using System.Globalization;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class OpacityChromaRenderTests(ITestOutputHelper output)
{
    private const int Width = 320;
    private const int Height = 180;
    private const int Fps = 30;
    private const int Frames = 60;
    private const int FrameBytes = Width * Height * 3 / 2;
    private const string Bars = "color=c=black:s=320x180:r=30:d=2,drawbox=x=64:y=0:w=64:h=180:color=0x808080:t=fill,drawbox=x=128:y=0:w=64:h=180:color=0xff0000:t=fill,drawbox=x=192:y=0:w=64:h=180:color=0x00ff00:t=fill,drawbox=x=256:y=0:w=64:h=180:color=0x0000ff:t=fill";

    [Fact]
    public async Task Constant085_PreservesBlackNeutralGrayAndNominalColors()
    {
        await using var f = await Fixture.Create("constant085", output);
        var part = await f.Assemble("opacity085", f.Item() with { Opacity = .85 });
        var raw = await f.Raw(part, "opacity085", "yuv420p");
        Assert.Equal(Frames * FrameBytes, raw.Length);
        var measured = Enumerable.Range(0, 5).Select(i => Sample(raw, 15, i)).ToArray();
        f.Log("opacity=.85 frame=15 YUV=" + JsonSerializer.Serialize(measured));
        f.Log("opacity=.85 frame=15 RGB=" + JsonSerializer.Serialize(measured.Select(Rgb)));
        AssertYuv(measured[0], 16, 128, 128, "constant .85 black", 0);
        AssertYuv(measured[1], 109, 128, 128, "constant .85 gray");
        var gray = Rgb(measured[1]);
        Assert.True(gray.Max() - gray.Min() <= 1, $"gray RGB must remain neutral; actual={string.Join('/', gray)}");
        double[][] expectedYuv = [[16, 128, 128], [109, 128, 128], [71, 95, 223], [124, 65, 48], [37, 223, 112]];
        double[][] nominal = [[0, 0, 0], [128, 128, 128], [255, 0, 0], [0, 255, 0], [0, 0, 255]];
        for (var i = 0; i < measured.Length; i++)
        {
            AssertYuv(measured[i], expectedYuv[i][0], expectedYuv[i][1], expectedYuv[i][2], $"patch {i}");
            var rgb = Rgb(measured[i]);
            for (var c = 0; c < 3; c++)
                Assert.True(Math.Abs(rgb[c] - nominal[i][c] * .85) <= 3,
                    $"patch {i} RGB[{c}] expected={nominal[i][c] * .85:R} +/-3 actual={rgb[c]:R}; YUV={measured[i]}");
        }
    }

    [Theory]
    [InlineData("plateau")]
    [InlineData("linear-hold")]
    [InlineData("track-times-item")]
    public async Task TimeExpressionsAndTrackMultiplier_KeepChromaCenteredEveryFrame(string curve)
    {
        await using var f = await Fixture.Create(curve, output);
        var item = f.Item();
        double[]? track = null;
        Func<double, double> opacity;
        switch (curve)
        {
            case "plateau":
                item = item with { Keyframes = Keys([0, 1, 2], [1, 1, .5]) };
                opacity = t => t <= 1 ? 1 : 1 - (t - 1) * .5;
                break;
            case "linear-hold":
                item = item with { Keyframes = Keys([.25, 1.5], [1, .25]) };
                opacity = t => t < .25 ? 1 : t >= 1.5 ? .25 : 1 - (t - .25) * .6;
                break;
            default:
                item = item with { Opacity = .5 };
                track = [.5, 1, 1];
                opacity = _ => .25;
                break;
        }
        var expression = EditorRenderService.OpacityExpression(item, track?[0] ?? 1);
        Assert.NotEqual("1", expression);
        f.Log("product opacity expression=" + expression);
        var part = await f.Assemble(curve, item, track: track);
        var raw = await f.Raw(part, curve, "yuv420p");
        Assert.Equal(Frames * FrameBytes, raw.Length);
        var samples = Enumerable.Range(0, Frames).Select(n => new { Frame = n, Time = n / (double)Fps, Black = Sample(raw, n, 0), Gray = Sample(raw, n, 1) }).ToArray();
        f.Log("every-frame=" + JsonSerializer.Serialize(samples));
        var previous = double.PositiveInfinity;
        foreach (var sample in samples)
        {
            var expectedY = Math.Floor(16 + 110 * opacity(sample.Time) + 1e-9);
            AssertYuv(sample.Black, 16, 128, 128, $"{curve} frame {sample.Frame} black", 0);
            AssertYuv(sample.Gray, expectedY, 128, 128, $"{curve} frame {sample.Frame} gray");
            Assert.Equal(128d, sample.Gray.U);
            Assert.Equal(128d, sample.Gray.V);
            Assert.True(sample.Gray.Y <= previous, $"{curve}: Y increased at frame {sample.Frame}: {previous} -> {sample.Gray.Y}");
            previous = sample.Gray.Y;
        }
        if (curve == "plateau")
        {
            foreach (var n in new[] { 0, 15, 30 }) Assert.Equal(126d, samples[n].Gray.Y);
            Assert.InRange(samples[45].Gray.Y, 97, 99);
        }
        else if (curve == "linear-hold")
        {
            Assert.Equal(126d, samples[0].Gray.Y);
            Assert.Equal(126d, samples[7].Gray.Y);
            Assert.Equal(samples[45].Gray, samples[59].Gray);
            Assert.InRange(samples[45].Gray.Y, 42, 44);
            Assert.True(samples[30].Gray.Y < samples[15].Gray.Y);
        }
        else Assert.All(samples, s => Assert.InRange(s.Gray.Y, 42, 44));
    }

    [Theory]
    [InlineData("bars")]
    [InlineData("testsrc2")]
    public async Task Identity_ConstantOneAndKeyedPlateauAreByteExact(string pattern)
    {
        await using var f = await Fixture.Create("identity-" + pattern, output, pattern);
        var source = await f.Raw(f.Source, "source", "yuv420p");
        var plain = await f.Assemble("constant-one", f.Item() with { Opacity = 1 });
        var plainRaw = await f.Raw(plain, "constant-one", "yuv420p");
        Assert.Equal("1", EditorRenderService.OpacityExpression(f.Item()));
        EqualBytes(source, plainRaw, pattern + " constant opacity=1", f);
        var keyed = f.Item() with { Keyframes = Keys([0, 1, 2], [1, 1, .5]) };
        Assert.NotEqual("1", EditorRenderService.OpacityExpression(keyed));
        var plateau = await f.Assemble("keyed-plateau", keyed);
        var plateauRaw = await f.Raw(plateau, "keyed-plateau", "yuv420p");
        Assert.Equal(Frames * FrameBytes, plateauRaw.Length);
        EqualBytes(source[..(31 * FrameBytes)], plateauRaw[..(31 * FrameBytes)], pattern + " opacity=1 forced blend frames 0..30", f);
    }

    [Theory]
    [InlineData("bars10", .85)]
    [InlineData("bars10", .5)]
    [InlineData("bars10", 0)]
    [InlineData("gradient", .85)]
    [InlineData("gradient", .5)]
    [InlineData("gradient", 0)]
    public async Task HighDepth_RealProductMatchesLegacyChainByteForByte(string pattern, double opacity)
    {
        await using var f = await Fixture.Create($"hdr-{pattern}-{opacity.ToString(CultureInfo.InvariantCulture)}", output, pattern);
        var item = f.Item() with { Opacity = opacity };
        var plan = new EditorProject { Format = "hevc", ColorSpace = "hdr10", Fps = Fps };
        Assert.True(EditorRenderService.RequiresHighBitDepth(plan));
        var part = await f.Assemble("product", item, plan: plan);
        var actual = await f.Raw(part, "product", "yuv420p10le");
        var legacy = await f.LegacyHdr(item, plan);
        EqualBytes(legacy, actual, $"HDR {pattern} opacity={opacity}", f);
        if (pattern == "bars10")
        {
            var black = Sample(actual, 15, 0, tenBit: true);
            var gray = Sample(actual, 15, 1, tenBit: true);
            f.Log($"HDR opacity={opacity} black={black} gray={gray}");
            AssertYuv(black, 64, 512, 512, "HDR black");
            Assert.Equal(512d, gray.U);
            Assert.Equal(512d, gray.V);
        }
        else if (opacity > 0)
        {
            var levels = new HashSet<int>();
            var lowBits = 0;
            for (var i = 0; i < f.Width * f.Height; i++)
            {
                var value = actual[2 * i] | actual[2 * i + 1] << 8;
                levels.Add(value);
                if (value % 4 != 0) lowBits++;
            }
            f.Log($"HDR {pattern} opacity={opacity}: levels={levels.Count}; nonZeroLowBits={lowBits}");
            Assert.True(levels.Count > 256, $"HDR gradient has only {levels.Count} levels");
            Assert.True(lowBits > 0, "HDR gradient low bits were zero");
        }
    }

    [Fact]
    public void HighDepthGraph_NewExpressionRetainsExistingRgb16Markers()
    {
        const string graph = "[0:v]format=rgb24[vopacity];color=c=black:s=320x180:r=30[vopacityblack];[vopacity][vopacityblack]blend=all_mode=normal:all_expr='B+(A-B)*(0.85)':shortest=1,format=yuv420p[vout]";
        var rewritten = EditorRenderService.HighBitDepthFilterGraph(graph);
        output.WriteLine(rewritten);
        Assert.Contains("format=gbrp16le[vopacity]", rewritten);
        Assert.Contains("[vopacityblack]format=gbrp16le[hdrblack];[vopacity][hdrblack]blend", rewritten);
        Assert.Contains("format=yuv420p10le[vout]", rewritten);
        Assert.DoesNotContain("format=rgb24", rewritten);
        Assert.DoesNotContain("format=gbrp[", rewritten);
        Assert.DoesNotContain("format=yuv420p[", rewritten);
    }

    private static Dictionary<string, ParamKeyframe[]> Keys(double[] times, double[] values) =>
        new() { ["opacity"] = times.Select((time, i) => new ParamKeyframe(time, values[i])).ToArray() };

    private sealed record Yuv(double Y, double U, double V);

    private static Yuv Sample(byte[] raw, int frame, int patch, bool tenBit = false)
    {
        var bytesPerSample = tenBit ? 2 : 1;
        var frameStart = frame * FrameBytes * bytesPerSample;
        double Mean(int plane, int stride, int x0, int y0, int width, int height)
        {
            long sum = 0;
            for (var y = y0; y < y0 + height; y++)
                for (var x = x0; x < x0 + width; x++)
                {
                    var at = frameStart + (plane + y * stride + x) * bytesPerSample;
                    sum += tenBit ? raw[at] | raw[at + 1] << 8 : raw[at];
                }
            return sum / (double)(width * height);
        }
        var left = patch * 64 + 8;
        return new(Mean(0, Width, left, 8, 48, Height - 16),
            Mean(Width * Height, Width / 2, left / 2, 4, 24, (Height - 16) / 2),
            Mean(Width * Height * 5 / 4, Width / 2, left / 2, 4, 24, (Height - 16) / 2));
    }

    private static double[] Rgb(Yuv value) =>
    [
        Math.Clamp(1.16438356 * (value.Y - 16) + 1.59602678 * (value.V - 128), 0, 255),
        Math.Clamp(1.16438356 * (value.Y - 16) - .39176229 * (value.U - 128) - .81296765 * (value.V - 128), 0, 255),
        Math.Clamp(1.16438356 * (value.Y - 16) + 2.01723214 * (value.U - 128), 0, 255)
    ];

    private static void AssertYuv(Yuv actual, double y, double u, double v, string name, double tolerance = 1) =>
        Assert.True(Math.Abs(actual.Y - y) <= tolerance && Math.Abs(actual.U - u) <= tolerance && Math.Abs(actual.V - v) <= tolerance,
            $"{name}: expected YUV=({y},{u},{v}) +/-{tolerance}; actual YUV=({actual.Y},{actual.U},{actual.V})");

    private static void EqualBytes(byte[] expected, byte[] actual, string name, Fixture f)
    {
        var different = expected.Zip(actual).Count(pair => pair.First != pair.Second);
        f.Log($"{name}: expectedBytes={expected.Length}; actualBytes={actual.Length}; differentBytes={different}");
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(different == 0, $"{name}: {different} raw YUV bytes differ");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        private readonly bool preserve;
        private int command;
        public string Root { get; }
        public string Source { get; private set; } = "";
        public int Width { get; }
        public int Height { get; }
        public MediaTools Tools { get; }
        public EditorRenderService Renderer { get; private set; } = null!;
        public Clip Clip { get; private set; } = null!;
        private CancellationToken Token => timeout.Token;

        private Fixture(string name, ITestOutputHelper output, bool gradient)
        {
            this.output = output;
            Width = gradient ? 1024 : OpacityChromaRenderTests.Width;
            Height = gradient ? 128 : OpacityChromaRenderTests.Height;
            var evidence = Environment.GetEnvironmentVariable("LIVECLIPPER_OPACITY_EVIDENCE");
            preserve = !string.IsNullOrWhiteSpace(evidence);
            Root = Path.Combine(evidence ?? Path.GetTempPath(), "opacity-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var executable = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
            Assert.True(!string.IsNullOrWhiteSpace(executable) && File.Exists(executable), "Native verification requires explicit LIVECLIPPER_TEST_FFMPEG; never count a skipped render as a pass.");
            Tools = new MediaTools(executable);
            Log("native ffmpeg=" + Tools.Ffmpeg);
        }

        public static async Task<Fixture> Create(string name, ITestOutputHelper output, string pattern = "bars")
        {
            var f = new Fixture(name, output, pattern == "gradient");
            f.Source = Path.Combine(f.Root, "source.mkv");
            var input = pattern switch
            {
                "testsrc2" => "testsrc2=s=320x180:r=30:d=2",
                "bars10" => Bars + ",format=yuv420p10le",
                "gradient" => "nullsrc=s=1024x128:r=30:d=2,format=yuv420p10le,geq=lum='64+floor(X*876/1023)':cb=512:cr=512",
                _ => Bars
            };
            await f.Run(f.Tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", input,
                "-t", "2", "-an", "-c:v", "ffv1", "-level", "3", "-pix_fmt", pattern is "bars10" or "gradient" ? "yuv420p10le" : "yuv420p", f.Source]);
            var store = new Store(Path.Combine(f.Root, "test.db")); store.Migrate();
            var studio = new StudioStore(store.DatabasePath); studio.Migrate();
            f.Renderer = new EditorRenderService(store, studio, f.Tools, f.Root);
            f.Clip = new Clip("clip", "opacity-chroma", f.Source, null, DateTimeOffset.UtcNow); store.SaveClip(f.Clip);
            return f;
        }

        public TimelineItem Item() => new("item", Clip.Id, 0, 2);
        public void Log(string text)
        {
            output.WriteLine(text);
            File.AppendAllText(Path.Combine(Root, "measurements.log"), text + Environment.NewLine);
        }

        public async Task<string> Assemble(string name, TimelineItem item, double[]? track = null, EditorProject? plan = null)
        {
            var work = Path.Combine(Root, name);
            plan = (plan ?? new EditorProject()) with { Start = 0, End = 2, Fps = Fps, Resolution = 0, Timeline = [item], TrackOpacities = track };
            Log("AssembleAsync=" + JsonSerializer.Serialize(plan));
            var result = await Renderer.AssembleAsync(Clip, plan, work, Token);
            Assert.Equal(2d, result.Length);
            Assert.Equal(30d, result.Fps);
            var part = Path.Combine(work, "part-000.mkv");
            Assert.True(File.Exists(part), "Missing product lossless part: " + part);
            var probe = await Run(Tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,pix_fmt,color_primaries,color_transfer,color_space", "-of", "json", part]);
            using var document = JsonDocument.Parse(probe);
            var video = Assert.Single(document.RootElement.GetProperty("streams").EnumerateArray());
            Assert.Equal("ffv1", video.GetProperty("codec_name").GetString());
            Assert.Equal(EditorRenderService.RequiresHighBitDepth(plan) ? "yuv420p10le" : "yuv420p", video.GetProperty("pix_fmt").GetString());
            if (plan.ColorSpace == "hdr10")
            {
                Assert.Equal("bt2020", video.GetProperty("color_primaries").GetString());
                Assert.Equal("smpte2084", video.GetProperty("color_transfer").GetString());
                Assert.Equal("bt2020nc", video.GetProperty("color_space").GetString());
            }
            return part;
        }

        public async Task<byte[]> Raw(string path, string name, string pixelFormat)
        {
            var raw = Path.Combine(Root, name + "." + pixelFormat);
            await Run(Tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-map", "0:v:0", "-an", "-frames:v", "60", "-pix_fmt", pixelFormat, "-c:v", "rawvideo", "-f", "rawvideo", raw]);
            return await File.ReadAllBytesAsync(raw, Token);
        }

        public async Task<byte[]> LegacyHdr(TimelineItem item, EditorProject plan)
        {
            // Frozen pre-fix chain is only the comparator; the other side is the real product AssembleAsync output.
            var visual = "trim=start=0:duration=2,setpts=PTS-STARTPTS," + EditorRenderService.VisualFilter(item, Width, Height, Fps, highBitDepth: true);
            var expression = EditorRenderService.OpacityExpression(item);
            var graph = $"[0:v]{visual}[vbase];[vbase]format=rgb24[vopacity];color=c=black:s={Width}x{Height}:r=30[vopacityblack];[vopacity][vopacityblack]blend=all_mode=normal:all_expr='A*({expression})':shortest=1,format=yuv420p[vout]";
            var path = Path.Combine(Root, "legacy.yuv420p10le");
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-t", "2", "-i", Source, "-filter_complex", graph, "-map", "[vout]", "-t", "2", "-frames:v", "60", "-an", "-pix_fmt", "yuv420p10le", "-c:v", "rawvideo", "-f", "rawvideo", path };
            EditorRenderService.ConfigureIntermediateArgs(args, plan);
            await Run(Tools.Ffmpeg, args.ToArray());
            return await File.ReadAllBytesAsync(path, Token);
        }

        public async Task<string> Run(string executable, string[] args)
        {
            var prefix = Path.Combine(Root, $"native-{++command:000}");
            var started = DateTimeOffset.UtcNow;
            using var process = MediaTools.Start(executable, args);
            var pid = process.Id;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            finally
            {
                var outText = await stdout;
                var errText = await stderr;
                await File.WriteAllTextAsync(prefix + ".stdout.log", outText);
                await File.WriteAllTextAsync(prefix + ".stderr.log", errText);
                await File.WriteAllTextAsync(prefix + ".command.json", JsonSerializer.Serialize(new { executable, argv = args, pid, started, ended = DateTimeOffset.UtcNow, exitCode = process.ExitCode }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Log($"native PID={pid} exit={process.ExitCode}: {executable} {JsonSerializer.Serialize(args)}");
            Assert.True(process.ExitCode == 0, $"Native process PID={pid} exit={process.ExitCode}: {await stderr}");
            return await stdout;
        }

        public async ValueTask DisposeAsync()
        {
            if (Renderer is not null) await Renderer.StopAsync();
            timeout.Dispose();
            if (!preserve) Directory.Delete(Root, recursive: true);
        }
    }
}
