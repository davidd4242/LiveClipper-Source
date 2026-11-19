using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class WinRenderAdjDipClosureTests(ITestOutputHelper output)
{
    private const int W = 16, H = 8, YuvSize = W * H * 3 / 2;
    private static string N(double n) => n.ToString("G17", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(false, 1.12, 1.12, 22, 28)]
    [InlineData(true, 1.12, 1.12, 22, 28)]
    [InlineData(false, .04, .04, 49, 1)]
    [InlineData(true, .04, .04, 49, 1)]
    [InlineData(false, .06, .08, 48, 2)]
    [InlineData(true, .06, .08, 48, 2)]
    [InlineData(false, 2, 2, 0, 50)]
    [InlineData(true, 2, 2, 0, 50)]
    public async Task C01_RealSdrPixelsFixedGridAndReachableZeroOffset(bool white, double requested, double duration, int prefix, int span)
    {
        await using var f = new Fixture(output);
        await f.Source("a", 0); await f.Source("b", 1);
        var plan = Plan(25, [white ? "dip-to-white" : "dip-to-black"], requested);
        var cut = Assert.Single(EditorRenderService.ResolveTransitions(plan));
        Assert.Equal(duration, cut.EffectiveDuration);
        var r = await f.Renderer.AssembleAsync(f.Clip("a"), plan, f.Work, f.Token);
        Assert.Equal(4 - duration, r.Length); Assert.Equal(2 - duration, r.Starts!["b"]);
        var a = await f.Raw(f.Part(0), "yuv420p"); var b = await f.Raw(f.Part(1), "yuv420p");
        var actual = await f.Raw(r.Video, "yuv420p");
        var expected = DipOracle(a, b, prefix, span, white, YuvSize, false);
        AssertBytes(expected, actual, "SDR every-plane exact");
        await f.Pts(r.Video, prefix + 50);
        AssertPcm(AudioOracle([0, 1], [duration]), await f.Audio(r.Video));
        if (span > 1) Assert.NotNull(Record.Exception(() => AssertBytes(DipOracle(a, b, prefix, span, !white, YuvSize, false), actual, "wrong dip color")));
        output.WriteLine($"C01 actual Resolve→Assemble: white={white} requested={requested:R} d={duration:R} prefix={prefix} D={span}; exact pixels/PTS/PCM; wrong-color rejected when D>1");
    }

    [Fact]
    public async Task C02_Auto25NonIntegralFourClipDipChainUsesNominalSeconds()
    {
        await using var f = new Fixture(output);
        foreach (var (id, n) in new[] { ("a", 0), ("b", 1), ("c", 2), ("d", 3) }) await f.Source(id, n);
        var plan = Plan(0, ["dip-to-black", "dip-to-white", "dip-to-black"], .5);
        var r = await f.Renderer.AssembleAsync(f.Clip("a"), plan, f.Work, f.Token);
        Assert.Equal(6.5, r.Length); Assert.Equal(25, r.Fps);
        Assert.Equal(new[] { 0d, 1.5, 3, 4.5 }, new[] { "a", "b", "c", "d" }.Select(id => r.Starts![id]).ToArray());
        var parts = new List<byte[]>(); for (var i = 0; i < 4; i++) parts.Add(await f.Raw(f.Part(i), "yuv420p"));
        var expected = parts[0];
        foreach (var i in Enumerable.Range(1, 3)) expected = DipOracle(expected, parts[i], new[] { 0, 38, 75, 113 }[i], 13, i == 2, YuvSize, false);
        var actual = await f.Raw(r.Video, "yuv420p"); AssertBytes(expected, actual, "nominal non-integral chain");
        var drift = parts[0];
        foreach (var i in Enumerable.Range(1, 3)) drift = DipOracle(drift, parts[i], new[] { 0, 38, 75, 112 }[i], 13, i == 2, YuvSize, false);
        Assert.NotNull(Record.Exception(() => AssertBytes(drift, actual, "D/f cumulative drift")));
        await f.Pts(r.Video, 163); AssertPcm(AudioOracle([0, 1, 2, 3], [.5, .5, .5]), await f.Audio(r.Video));
        output.WriteLine("C02 nominal offsets1.5/3/4.5; wrong1.5/2.98/4.46 rejected by actual frame content/count. 163 frames, 312000 stereo sample-frames.");
    }

    [Fact]
    public async Task C03_Auto25DipNativeCustomNoneChainHasContentAndClock()
    {
        await using var f = new Fixture(output);
        foreach (var (id, n) in new[] { ("a", 0), ("b", 1), ("c", 2), ("d", 3), ("e", 4) }) await f.Source(id, n, constant: true);
        var r = await f.Renderer.AssembleAsync(f.Clip("a"), Plan(0, ["dip-to-white", "cross-dissolve", "iris-round", "none"], .5), f.Work, f.Token);
        Assert.Equal(8.5, r.Length); Assert.Equal(new[] { 0d, 1.5, 3, 4.5, 6.5 }, new[] { "a", "b", "c", "d", "e" }.Select(id => r.Starts![id]).ToArray());
        var parts = new List<byte[]>(); for (var i = 0; i < 5; i++) parts.Add(await f.Raw(f.Part(i), "gbrp"));
        var actual = await f.Raw(r.Video, "gbrp"); var size = W * H * 3;
        // Frozen custom Join trims half-open seconds on a 25fps grid; unlike dip, its A tail is not padded.
        static int Samples(decimal start, decimal end) => Enumerable.Range(0, (int)decimal.Ceiling(end * 25)).Count(i => i / 25m >= start && i / 25m < end);
        var prefix = Samples(0, 4.5m); var aTail = Samples(4.5m, 5); var bHead = Samples(0, .5m); var bPost = Samples(.5m, 2);
        var noneBoundary = prefix + Math.Min(aTail, bHead) + bPost; var frames = noneBoundary + Samples(0, 2);
        Assert.Equal(frames * size, actual.Length); await f.Pts(r.Video, frames);
        var first = DipOracle(parts[0], parts[1], 38, 13, true, size, true);
        AssertBytes(first[..(75 * size)], actual[..(75 * size)], "mixed first dip untouched by later cuts");
        FrameEquals(parts[1], 0, actual, 75, size, "native A endpoint at3s");
        FrameEquals(parts[2], 0, actual, 90, size, "native B after overlap");
        FrameEquals(parts[2], 0, actual, prefix, size, "custom A endpoint at nominal4.5s ceiling");
        FrameEquals(parts[3], 0, actual, 130, size, "custom B after overlap");
        FrameEquals(parts[4], 0, actual, 170, size, "none concatenation payload");
        var boundary = Enumerable.Range(145, 30).First(i => actual.AsSpan(i * size, size).SequenceEqual(parts[4].AsSpan(0, size)));
        Assert.Equal(noneBoundary, boundary);
        AssertPcm(AudioOracle([0, 1, 2, 3, 4], [.5, .5, .5, 0]), await f.Audio(r.Video));
        output.WriteLine($"C03 actual dip/native/custom/none:{frames} frames; independent custom clock prefix{prefix}, A{aTail}/B{bHead}, shortest{Math.Min(aTail, bHead)}, post{bPost}; exact PTS; first dip every pixel; native/custom endpoints and post-overlap pixels; concat boundary{noneBoundary}; every PCM sample bit-identical. Unchanged custom nominal-seconds/frame-quantization compatibility difference; not a product fix or a frozen legacy pixel mapping oracle.");
    }

    [Fact]
    public async Task C04_OverlayAndMixContentRejectSameLengthSilentOrMissingPayload()
    {
        await using var f = new Fixture(output);
        await f.Source("a", 0, constant: true); await f.Source("b", 1, constant: true);
        await f.Source("overlay", 2, constant: true); await f.Source("mix", 3, constant: true);
        var plan = Plan(25, ["dip-to-black"], .5) with
        {
            Timeline = [new("a", "a", 0, 2), new("b", "b", 0, 2), new("overlay", "overlay", 0, 1, Track: 2), new("mix", "mix", 0, 1, Track: 4)]
        };
        var r = await f.Renderer.AssembleAsync(f.Clip("a"), plan, f.Work, f.Token);
        Assert.Equal(3.48, r.Length); await f.Pts(r.Video, 87);
        var actual = await f.Raw(r.Video, "yuv420p"); var overlay = await f.Raw(Path.Combine(f.Work, "overlay-000.mkv"), "yuv420p");
        FrameEquals(overlay, 0, actual, 0, YuvSize, "overlay visible content");
        var baseVideo = await f.Raw(Path.Combine(f.Work, "joined.mkv"), "yuv420p");
        Assert.Equal(baseVideo.Length, actual.Length);
        Assert.NotNull(Record.Exception(() => FrameEquals(overlay, 0, baseVideo, 0, YuvSize, "missing same-length overlay")));
        var expected = AudioOracle([0, 1], [.52]);
        for (var n = 0; n < 48000; n++) for (var c = 0; c < 2; c++) expected[n * 2 + c] = (expected[n * 2 + c] + Signal(n, 2, c)) + Signal(n, 3, c);
        var pcm = await f.Audio(r.Video); AssertPcm(expected, pcm);
        Assert.NotNull(Record.Exception(() => AssertPcm(expected, new byte[pcm.Length])));
        Assert.NotNull(Record.Exception(() => AssertPcm(expected, Floats(AudioOracle([0, 1], [.52])))));
        output.WriteLine("C04 overlay actual pixels + every mixed PCM sample; same-length missing overlay, base-only audio and all-silent audio rejected.");
    }

    private static EditorProject Plan(int fps, string[] kinds, double duration) => new()
    {
        Start = 0, End = (kinds.Length + 1) * 2, Fps = fps, Resolution = 0,
        Timeline = Enumerable.Range(0, kinds.Length + 1).Select(i => new TimelineItem(((char)('a' + i)).ToString(), ((char)('a' + i)).ToString(), 0, 2)).ToArray(),
        Transitions = kinds.Select((kind, i) => (kind, i)).Where(v => v.kind != "none").Select(v => new EditorTransition("cut" + v.i, ((char)('b' + v.i)).ToString(), 1, v.kind, duration, "center")).ToArray()
    };

    // Integer sample weights come from the written q/D contract, not TransitionMap or its graph.
    private static byte[] DipOracle(byte[] a, byte[] b, int prefix, int span, bool white, int size, bool rgb)
    {
        var result = new byte[prefix * size + b.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var frame = i / size; var sample = i % size; var q = frame - prefix;
            int av = a[Math.Min(frame, a.Length / size - 1) * size + sample];
            if (q < 0) { result[i] = (byte)av; continue; }
            int bv = b[q * size + sample];
            if (q >= span) { result[i] = (byte)bv; continue; }
            int center = rgb ? white ? 255 : 0 : sample < W * H ? white ? 235 : 16 : 128;
            int numerator = q * 2 <= span ? av * (span - 2 * q) + center * 2 * q : center * (2 * span - 2 * q) + bv * (2 * q - span);
            result[i] = (byte)((2 * numerator + span) / (2 * span));
        }
        return result;
    }
    private static void AssertBytes(byte[] expected, byte[] actual, string reason)
    {
        Assert.True(expected.Length == actual.Length, $"{reason}: byte lengths {expected.Length} != {actual.Length}");
        for (var i = 0; i < expected.Length; i++) Assert.True(expected[i] == actual[i], $"{reason}: byte{i} expected={expected[i]} actual={actual[i]}");
    }
    private static void FrameEquals(byte[] a, int ai, byte[] b, int bi, int size, string reason) => AssertBytes(a[(ai * size)..((ai + 1) * size)], b[(bi * size)..((bi + 1) * size)], reason);
    private static float Signal(int n, int source, int channel) => n % 997 == 17 + source * 47 ? (source + 1) / 16f * (channel == 0 ? 1 : -1) : 0;
    private static float[] AudioOracle(int[] sources, double[] overlaps)
    {
        float[] Part(int source) => Enumerable.Range(0, 96000 * 2).Select(i => Signal(i / 2, source, i % 2)).ToArray();
        var current = Part(sources[0]);
        for (var s = 1; s < sources.Length; s++)
        {
            var incoming = Part(sources[s]); var d = (int)Math.Round(overlaps[s - 1] * 48000); var offset = current.Length / 2 - d;
            var joined = new float[current.Length + incoming.Length - d * 2]; Array.Copy(current, joined, offset * 2);
            for (var n = 0; n < 96000; n++) for (var c = 0; c < 2; c++)
                joined[(offset + n) * 2 + c] = n < d ? (float)(current[(offset + n) * 2 + c] * ((d - 1d - n) / d) + incoming[n * 2 + c] * (n / (double)d)) : incoming[n * 2 + c];
            current = joined;
        }
        return current;
    }
    private static byte[] Floats(float[] values) { var bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
    private static void AssertPcm(float[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length * 4, actual.Length);
        for (var i = 0; i < expected.Length; i++) Assert.True(BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.ToInt32(actual, i * 4), $"PCM sample{i / 2} ch{i % 2} expected={expected[i]:R} actual={BitConverter.ToSingle(actual, i * 4):R}");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("CLOSURE_ROOT") ?? throw new InvalidOperationException("CLOSURE_ROOT required"), Guid.NewGuid().ToString("N"));
        public string Work => Path.Combine(Root, "assembled");
        public string Part(int i) => Path.Combine(Work, $"part-{i:000}.mkv");
        public Store Store { get; }
        public EditorRenderService Renderer { get; }
        private readonly MediaTools tools;
        private readonly ITestOutputHelper log;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));
        public CancellationToken Token => timeout.Token;
        private int serial;
        public Fixture(ITestOutputHelper log)
        {
            this.log = log; Directory.CreateDirectory(Root);
            var ffmpeg = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG") ?? throw new InvalidOperationException("explicit ffmpeg required");
            Assert.True(Path.IsPathFullyQualified(ffmpeg) && File.Exists(ffmpeg)); tools = new MediaTools(ffmpeg);
            Store = new Store(Path.Combine(Root, "store.db")); Store.Migrate(); var studio = new StudioStore(Store.DatabasePath); studio.Migrate();
            var proxy = Environment.GetEnvironmentVariable("CLOSURE_FFMPEG_PROXY");
            Renderer = new EditorRenderService(Store, studio, proxy is null ? tools : new MediaTools(proxy), Root);
            log.WriteLine("retained fixture: " + Root);
            File.WriteAllText(Path.Combine(Root, "runtime-identity.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, runtime = Environment.Version.ToString(), core = typeof(EditorRenderService).Assembly.Location, coreMvid = typeof(EditorRenderService).Module.ModuleVersionId, tests = typeof(WinRenderAdjDipClosureTests).Assembly.Location, testsMvid = typeof(WinRenderAdjDipClosureTests).Module.ModuleVersionId }));
        }
        public Clip Clip(string id) => Store.Clips().Single(c => c.Id == id);
        public async Task<string> Run(string exe, params string[] args)
        {
            var prefix = Path.Combine(Root, $"command-{++serial:0000}"); var started = DateTimeOffset.UtcNow;
            using var p = MediaTools.Start(exe, args); var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            try { await p.WaitForExitAsync(Token); } catch { if (!p.HasExited) p.Kill(true); await p.WaitForExitAsync(); throw; }
            var so = await stdout; var se = await stderr;
            await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(new { executable = exe, argv = args, cwd = Environment.CurrentDirectory, pid = p.Id, parentPid = Environment.ProcessId, started, ended = DateTimeOffset.UtcNow, exit = p.ExitCode }));
            await File.WriteAllTextAsync(prefix + ".stdout", so); await File.WriteAllTextAsync(prefix + ".stderr", se);
            Assert.True(p.ExitCode == 0, $"{exe} exit{p.ExitCode}: {se}"); return so;
        }
        public async Task Source(string id, int source, bool constant = false)
        {
            var raw = new byte[50 * YuvSize];
            for (var i = 0; i < raw.Length; i++) { var frame = i / YuvSize; var sample = i % YuvSize; raw[i] = constant ? (byte)(sample < W * H ? 40 + source * 30 : 128) : (byte)((sample < W * H ? 16 : 24) + (source * 41 + frame * 17 + sample * 7) % 200); }
            var rp = Path.Combine(Root, id + ".yuv"); var ap = Path.Combine(Root, id + ".f32"); var dest = Path.Combine(Root, id + ".mkv");
            await File.WriteAllBytesAsync(rp, raw); await File.WriteAllBytesAsync(ap, Floats(Enumerable.Range(0, 96000 * 2).Select(i => Signal(i / 2, source, i % 2)).ToArray()));
            await Run(tools.Ffmpeg, "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{W}x{H}", "-r", "25", "-i", rp, "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", ap, "-t", "2", "-c:v", "ffv1", "-level", "3", "-pix_fmt", "yuv420p", "-c:a", "pcm_f32le", dest);
            Store.SaveClip(new Clip(id, "closure-" + id, dest, null, DateTimeOffset.UtcNow));
        }
        public async Task<byte[]> Raw(string path, string format)
        {
            var dest = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".raw");
            await Run(tools.Ffmpeg, "-v", "error", "-y", "-i", path, "-map", "0:v:0", "-an", "-pix_fmt", format, "-fps_mode", "passthrough", "-f", "rawvideo", dest); return await File.ReadAllBytesAsync(dest);
        }
        public async Task<byte[]> Audio(string path)
        {
            var dest = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".f32");
            await Run(tools.Ffmpeg, "-v", "error", "-y", "-i", path, "-map", "0:a:0", "-vn", "-c:a", "pcm_f32le", "-f", "f32le", dest); return await File.ReadAllBytesAsync(dest);
        }
        public async Task Pts(string path, int count)
        {
            using var doc = JsonDocument.Parse(await Run(tools.Ffprobe, "-v", "error", "-select_streams", "v:0", "-show_streams", "-show_frames", "-of", "json", path));
            var frames = doc.RootElement.GetProperty("frames").EnumerateArray().ToArray(); Assert.Equal(count, frames.Length);
            var tb = Assert.Single(doc.RootElement.GetProperty("streams").EnumerateArray()).GetProperty("time_base").GetString()!.Split('/');
            for (var i = 0; i < count; i++) { BigInteger num = (BigInteger)i * long.Parse(tb[1]), den = 25 * long.Parse(tb[0]); Assert.Equal((long)((2 * num + den) / (2 * den)), frames[i].GetProperty("pts").GetInt64()); }
        }
        public async ValueTask DisposeAsync() { await Renderer.StopAsync(); timeout.Dispose(); log.WriteLine("retained: " + Root); }
    }
}
