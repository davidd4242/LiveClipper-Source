using System.Globalization;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class MaskCoverageRenderTests(ITestOutputHelper output)
{
    private const int Width = 384;
    private const int Height = 192;
    private const int Fps = 30;
    private const int Frames = 30;
    private const int FrameBytes = Width * Height * 3 / 2;
    private const string Bars = "color=c=black:s=384x192:r=30:d=1,drawbox=x=64:y=0:w=64:h=192:color=0x808080:t=fill,drawbox=x=128:y=0:w=64:h=192:color=0xff0000:t=fill,drawbox=x=192:y=0:w=64:h=192:color=0x00ff00:t=fill,drawbox=x=256:y=0:w=64:h=192:color=0x0000ff:t=fill,drawbox=x=320:y=0:w=64:h=192:color=white:t=fill";

    [Theory]
    [InlineData("rect", 1d)]
    [InlineData("ellipse", 1d)]
    [InlineData("polygon", 1d)]
    [InlineData("rect", .4)]
    [InlineData("ellipse", .4)]
    [InlineData("polygon", .4)]
    public async Task RealMatte_InsideIsByteExactAndOutsideUsesAllPlaneCoverage(string shape, double maskOpacity)
    {
        await using var f = await Fixture.Create($"{shape}-{maskOpacity.ToString(CultureInfo.InvariantCulture)}", output);
        var plain = await f.Assemble("plain", f.Item());
        var baseline = await f.Raw(plain.Video, "plain", "yuv420p");
        var masked = await f.Assemble("masked", f.Item() with { Masks = [Mask(shape, maskOpacity)] });
        var actual = await f.Raw(masked.Video, "masked", "yuv420p");
        var matte = await f.Matte(masked.Matte!);
        Assert.Equal(Frames * FrameBytes, baseline.Length);
        Assert.Equal(baseline.Length, actual.Length);
        // Mask opacity is removal coverage outside the shape: m=255*opacity (102 at .4).
        // Each code-value plane uses round((A*(255-m)+black*m)/255), with black=(16,128,128).
        // This is derived from coverage, not from a rendered expected image; endpoints have zero tolerance.
        var removal = (int)(255 * maskOpacity);
        var failures = new List<string>();
        for (var patch = 0; patch < 6; patch++)
        {
            var x = patch * 64 + 16;
            f.Log(JsonSerializer.Serialize(new { shape, maskOpacity, patch,
                baseline = Pixel(baseline, 15, x, 88), inside = Pixel(actual, 15, x, 88), outside = Pixel(actual, 15, x, 8),
                expectedOutside = Pixel(baseline, 15, x, 8).Select((a, p) => Blend(a, p == 0 ? 16 : 128, removal)).ToArray(),
                matteInside = matte[88 * Width + x], matteOutside = matte[8 * Width + x] }));
            AssertMatteRegion(matte, x, 88, 0);
            AssertMatteRegion(matte, x, 8, removal);
            CheckRegion(baseline, actual, x, 88, 0, $"{shape} opacity={maskOpacity} patch={patch} inside", failures);
            CheckRegion(baseline, actual, x, 8, removal, $"{shape} opacity={maskOpacity} patch={patch} outside", failures);
        }
        f.Log("coverage failures=" + JsonSerializer.Serialize(failures));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("rect")]
    [InlineData("ellipse")]
    [InlineData("polygon")]
    public async Task RealMatte_IdentityRegionsPreserveDetailedSourceBytes(string shape)
    {
        await using var f = await Fixture.Create("detail-" + shape, output, "testsrc2");
        var baseline = await f.Raw((await f.Assemble("plain", f.Item())).Video, "plain", "yuv420p");
        var masked = await f.Assemble("masked", f.Item() with { Masks = [Mask(shape, 1)] });
        var actual = await f.Raw(masked.Video, "masked", "yuv420p");
        var failures = new List<string>();
        for (var patch = 0; patch < 6; patch++)
        {
            CheckRegion(baseline, actual, patch * 64 + 16, 88, 0, $"detail {shape} patch={patch} inside", failures);
            CheckRegion(baseline, actual, patch * 64 + 16, 8, 255, $"detail {shape} patch={patch} outside", failures);
        }
        f.Log("detailed identity failures=" + JsonSerializer.Serialize(failures));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("bars10", 1d, 1d)]
    [InlineData("bars10", .4, 1d)]
    [InlineData("bars10", .4, .85)]
    [InlineData("gradient", 1d, 1d)]
    [InlineData("gradient", .4, 1d)]
    [InlineData("gradient", .4, .85)]
    public async Task HighDepth_RealMaskAndOpacityRemainByteExactToLegacy(string pattern, double maskOpacity, double itemOpacity)
    {
        await using var f = await Fixture.Create($"hdr-{pattern}-{maskOpacity.ToString(CultureInfo.InvariantCulture)}-{itemOpacity.ToString(CultureInfo.InvariantCulture)}", output, pattern);
        var item = f.Item() with { Masks = [Mask("rect", maskOpacity)], Opacity = itemOpacity };
        var plan = new EditorProject { Format = "hevc", ColorSpace = "hdr10", Fps = Fps };
        var masked = await f.Assemble("product", item, plan);
        var actual = await f.Raw(masked.Video, "product", "yuv420p10le");
        var legacy = await f.LegacyHdr(item, plan, masked.Matte!);
        var differences = legacy.Zip(actual).Count(p => p.First != p.Second);
        f.Log($"HDR {pattern} mask={maskOpacity} opacity={itemOpacity}: expectedBytes={legacy.Length}; actualBytes={actual.Length}; differentBytes={differences}");
        Assert.Equal(Frames * FrameBytes * 2, actual.Length);
        Assert.Equal(legacy.Length, actual.Length);
        Assert.True(differences == 0, $"HDR mask chain changed {differences} raw bytes");
    }

    private static MaskInstance Mask(string shape, double opacity) => new("mask", shape, true, 0, 0,
        shape == "ellipse" ? 1.8 : 2, shape == "ellipse" ? .6 : .5, 0, 0, opacity,
        shape == "polygon" ? [new(0, .25), new(1, .25), new(1, .75), new(0, .75)] : null);

    private static int Blend(int a, int black, int removal) => (a * (255 - removal) + black * removal + 127) / 255;
    private static int[] Pixel(byte[] raw, int frame, int x, int y) =>
    [raw[frame * FrameBytes + y * Width + x],
     raw[frame * FrameBytes + Width * Height + y / 2 * (Width / 2) + x / 2],
     raw[frame * FrameBytes + Width * Height * 5 / 4 + y / 2 * (Width / 2) + x / 2]];

    private static void AssertMatteRegion(byte[] matte, int x0, int y0, int expected)
    {
        for (var y = y0; y < y0 + 16; y++)
            for (var x = x0; x < x0 + 32; x++)
                Assert.True(matte[y * Width + x] == expected, $"product PNG ({x},{y}): expected={expected} actual={matte[y * Width + x]}");
    }

    private static void CheckRegion(byte[] baseline, byte[] actual, int x0, int y0, int removal, string name, List<string> failures)
    {
        Assert.Equal(Frames * FrameBytes, baseline.Length);
        Assert.Equal(baseline.Length, actual.Length);
        var count = 0;
        string? first = null;
        for (var frame = 0; frame < Frames; frame++)
            for (var plane = 0; plane < 3; plane++)
            {
                var scale = plane == 0 ? 1 : 2;
                var offset = frame * FrameBytes + (plane == 0 ? 0 : plane == 1 ? Width * Height : Width * Height * 5 / 4);
                for (var y = y0 / scale; y < (y0 + 16) / scale; y++)
                    for (var x = x0 / scale; x < (x0 + 32) / scale; x++)
                    {
                        var at = offset + y * (Width / scale) + x;
                        var expected = Blend(baseline[at], plane == 0 ? 16 : 128, removal);
                        if (actual[at] == expected) continue;
                        count++;
                        first ??= $"frame={frame} plane={plane} ({x},{y}) baseline={baseline[at]} expected={expected} actual={actual[at]}";
                    }
            }
        if (count > 0) failures.Add($"{name}: {count} bytes differ; {first}");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        private int command;
        public string Root { get; }
        public string Source { get; private set; } = "";
        public MediaTools Tools { get; }
        public EditorRenderService Renderer { get; private set; } = null!;
        public Clip Clip { get; private set; } = null!;
        private CancellationToken Token => timeout.Token;

        private Fixture(string name, ITestOutputHelper output)
        {
            this.output = output;
            var evidence = Environment.GetEnvironmentVariable("LIVECLIPPER_MASK_COVERAGE_EVIDENCE");
            Root = Path.Combine(evidence ?? Path.GetTempPath(), "mask-coverage-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var executable = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
            Assert.True(!string.IsNullOrWhiteSpace(executable) && File.Exists(executable), "Real mask verification requires LIVECLIPPER_TEST_FFMPEG; do not count a skipped render as a pass.");
            Tools = new MediaTools(executable);
        }

        public static async Task<Fixture> Create(string name, ITestOutputHelper output, string pattern = "bars")
        {
            var f = new Fixture(name, output);
            f.Source = Path.Combine(f.Root, "source.mkv");
            var input = pattern switch
            {
                "testsrc2" => "testsrc2=s=384x192:r=30:d=1",
                "bars10" => Bars + ",format=yuv420p10le",
                "gradient" => "nullsrc=s=384x192:r=30:d=1,format=yuv420p10le,geq=lum='64+floor(X*876/383)':cb=512:cr=512",
                _ => Bars
            };
            await f.Run(f.Tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", input,
                "-t", "1", "-an", "-c:v", "ffv1", "-level", "3", "-pix_fmt", pattern is "bars10" or "gradient" ? "yuv420p10le" : "yuv420p", f.Source]);
            var store = new Store(Path.Combine(f.Root, "test.db")); store.Migrate();
            var studio = new StudioStore(store.DatabasePath); studio.Migrate();
            f.Renderer = new EditorRenderService(store, studio, f.Tools, f.Root);
            f.Clip = new Clip("clip", "mask-coverage", f.Source, null, DateTimeOffset.UtcNow); store.SaveClip(f.Clip);
            return f;
        }

        public TimelineItem Item() => new("item", Clip.Id, 0, 1);
        public void Log(string text)
        {
            output.WriteLine(text);
            File.AppendAllText(Path.Combine(Root, "measurements.log"), text + Environment.NewLine);
        }

        public async Task<(string Video, string? Matte)> Assemble(string name, TimelineItem item, EditorProject? plan = null)
        {
            var work = Path.Combine(Root, name);
            plan = (plan ?? new EditorProject()) with { Start = 0, End = 1, Fps = Fps, Resolution = 0, Timeline = [item] };
            Log("AssembleAsync=" + JsonSerializer.Serialize(plan));
            var result = await Renderer.AssembleAsync(Clip, plan, work, Token);
            Assert.Equal(1d, result.Length);
            Assert.Equal(30d, result.Fps);
            Assert.True(File.Exists(result.Video), "Missing actual assembled video");
            var matte = item.Masks is null ? null : Path.Combine(work, "mask-000.png");
            if (matte is not null) Assert.True(File.Exists(matte), "AssembleAsync did not generate its real static matte PNG");
            var probe = await Run(Tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,pix_fmt", "-of", "json", result.Video]);
            using var document = JsonDocument.Parse(probe);
            var video = Assert.Single(document.RootElement.GetProperty("streams").EnumerateArray());
            Assert.Equal("ffv1", video.GetProperty("codec_name").GetString());
            Assert.Equal(EditorRenderService.RequiresHighBitDepth(plan) ? "yuv420p10le" : "yuv420p", video.GetProperty("pix_fmt").GetString());
            return (result.Video, matte);
        }

        public async Task<byte[]> Raw(string path, string name, string pixelFormat)
        {
            var raw = Path.Combine(Root, name + "." + pixelFormat);
            await Run(Tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-map", "0:v:0", "-an", "-frames:v", "30", "-pix_fmt", pixelFormat, "-c:v", "rawvideo", "-f", "rawvideo", raw]);
            return await File.ReadAllBytesAsync(raw, Token);
        }

        public async Task<byte[]> Matte(string path)
        {
            var raw = Path.Combine(Root, "matte.gray");
            await Run(Tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-frames:v", "1", "-pix_fmt", "gray", "-f", "rawvideo", raw]);
            var bytes = await File.ReadAllBytesAsync(raw, Token);
            Assert.Equal(Width * Height, bytes.Length);
            return bytes;
        }

        public async Task<byte[]> LegacyHdr(TimelineItem item, EditorProject plan, string matte)
        {
            // Frozen pre-fix graph is the comparator; actual always comes from AssembleAsync above.
            var visual = "trim=start=0:duration=1,setpts=PTS-STARTPTS," + EditorRenderService.VisualFilter(item, Width, Height, Fps, highBitDepth: true);
            var graph = $"[0:v]{visual}[vbase];[1:v]fps=30,format=gray[vmatte];color=c=black:s={Width}x{Height}:r=30[vmaskblack];[vbase][vmaskblack][vmatte]maskedmerge[vmasked]";
            var expression = EditorRenderService.OpacityExpression(item);
            graph += expression == "1" ? ";[vmasked]format=yuv420p[vout]"
                : $";[vmasked]format=rgb24[vopacity];color=c=black:s={Width}x{Height}:r=30[vopacityblack];[vopacity][vopacityblack]blend=all_mode=normal:all_expr='B+(A-B)*({expression})':shortest=1,format=yuv420p[vout]";
            var raw = Path.Combine(Root, "legacy.yuv420p10le");
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-t", "1", "-i", Source,
                "-loop", "1", "-framerate", "30", "-i", matte, "-filter_complex", graph, "-map", "[vout]", "-t", "1", "-frames:v", "30", "-an", "-pix_fmt", "yuv420p10le", "-c:v", "rawvideo", "-f", "rawvideo", raw };
            EditorRenderService.ConfigureIntermediateArgs(args, plan);
            Log("legacy HDR graph=" + args[args.IndexOf("-filter_complex") + 1]);
            await Run(Tools.Ffmpeg, args.ToArray());
            return await File.ReadAllBytesAsync(raw, Token);
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
                await File.WriteAllTextAsync(prefix + ".stdout.log", await stdout);
                await File.WriteAllTextAsync(prefix + ".stderr.log", await stderr);
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
            // Preserve real PNGs, assembled outputs and raw samples for evidence, including failed runs.
        }
    }
}
