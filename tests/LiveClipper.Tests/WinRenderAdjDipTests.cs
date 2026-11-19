using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Core.EditorFx;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class WinRenderAdjDipTests(ITestOutputHelper output)
{
    private const int W = 16, H = 8;
    private static string N(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(0)]
    [InlineData(.2)]
    public async Task D01_IntegerSpanHasExactLimitedWhiteSamples(double offset)
    {
        await using var f = new Fixture(output);
        var pre = offset == 0 ? 0 : 5;
        var a = Pattern(28 + pre, "yuv420p", 0, constant: 18);
        var b = Pattern(33, "yuv420p", 1, constant: 227);
        // Deliberately use the existing seven-argument entry point for a compilable behavioral RED.
        var graph = TransitionMap.Join("dip-to-white", "[a]", "[b]", "[out]", offset, 1.12, 25);
        var actual = await f.Join(a, b, "yuv420p", "25", graph);
        output.WriteLine($"D01 offset={offset}: frames={actual.Length / Samples("yuv420p")}; q1={ReadSample(actual, (pre + 1) * Samples("yuv420p"), false)}; expected frames={pre + 33}, q1=34");
        EqualVideo(a, b, actual, "yuv420p", pre, 28, true);
    }

    [Theory]
    [InlineData(0, 25, .5, .5, 13, 38, 50)]
    [InlineData(0, 29.97002997002997, 1, 1, 30, 30, 60)]
    [InlineData(25, 25, 1.12, 1.12, 28, 22, 50)]
    [InlineData(25, 25, .04, .04, 1, 49, 50)]
    [InlineData(0, 25, .04, 1d / 30, 1, 50, 50)]
    [InlineData(25, 25, .06, .08, 2, 48, 50)]
    [InlineData(25, 25, .1, .12, 3, 47, 50)]
    public async Task D02_ResolveKeepsTimelineGridAndJoinUsesActualGrid(int projectFps, double fps, double requested, double d, int span, int pre, int sourceFrames)
    {
        await using var f = new Fixture(output);
        var plan = Plan(projectFps, requested);
        EditorRenderService.Validate(plan);
        var cut = Assert.Single(EditorRenderService.ResolveTransitions(plan));
        Assert.Equal(d, cut.EffectiveDuration);
        Assert.Equal(projectFps == 0 ? 30 : projectFps, EditorRenderService.TransitionFps(plan));
        var a = Pattern(sourceFrames, "yuv420p", 0); var b = Pattern(sourceFrames, "yuv420p", 1);
        var graph = TransitionMap.Join(cut.Kind, "[a]", "[b]", "[out]", 2 - d, d, fps);
        var actual = await f.Join(a, b, "yuv420p", fps == 25 ? "25" : "30000/1001", graph);
        EqualVideo(a, b, actual, "yuv420p", pre, span, true);
        await f.FilterPts(a, b, "yuv420p", fps == 25 ? "25" : "30000/1001", graph, pre + sourceFrames);
    }

    [Theory]
    [InlineData(.05, 25, 1)]
    [InlineData(1.13, 25, 28)]
    [InlineData(1, 29.97002997002997, 30)]
    public async Task D02_FractionalLowLevelTimingIsAccepted(double duration, double fps, int span)
    {
        await using var f = new Fixture(output);
        var a = Pattern(span, "yuv420p", 0); var b = Pattern(span + 3, "yuv420p", 1);
        var graph = TransitionMap.Join("dip-to-black", "[a]", "[b]", "[out]", 0, duration, fps);
        EqualVideo(a, b, await f.Join(a, b, "yuv420p", fps == 25 ? "25" : "30000/1001", graph), "yuv420p", 0, span, false);
    }

    [Fact]
    public void D02_TimingGuardAndExplicitNonproductionDomainLimit()
    {
        foreach (var t in new[] { (-1d, 1d, 25d), (0d, 0d, 25d), (0d, double.NaN, 25d), (0d, 1d, double.PositiveInfinity), (0d, 1d, 0d) })
            Assert.Equal("transition-invalid-timing", Assert.Throws<ArgumentException>(() => TransitionMap.Join("dip-to-black", "[a]", "[b]", "[o]", t.Item1, t.Item2, t.Item3)).Message);
        Assert.Equal("transition-dip-frame-count-out-of-range", Assert.Throws<ArgumentException>(() => TransitionMap.Join("dip-to-black", "[a]", "[b]", "[o]", 0, 60, 40000000)).Message);
    }

    public static IEnumerable<object[]> PixelCases()
    {
        foreach (var format in new[] { "yuv420p", "yuv420p10le" })
        foreach (var white in new[] { false, true })
        foreach (var span in new[] { 1, 2, 3, 11, 28, 30 })
        foreach (var offset in new[] { 0d, .2, .21 })
            yield return [format, white, span, offset, (int)Math.Ceiling(offset * 25) + span, span + 4];
        foreach (var format in new[] { "yuv420p", "yuv420p10le" })
        foreach (var white in new[] { false, true })
        {
            yield return [format, white, 13, 1.5, 50, 50];
            yield return [format, white, 3, .26, 7, 8];
            yield return [format, white, 11, .21, 17, 11];
            yield return [format, white, 11, .21, 17, 7];
        }
        foreach (var white in new[] { false, true }) yield return ["gbrp", white, 28, .21, 34, 32];
    }

    [Theory]
    [MemberData(nameof(PixelCases))]
    public async Task D03_AllPlanesEndpointsPadAndShortIncoming(string format, bool white, int span, double offset, int na, int nb)
    {
        await using var f = new Fixture(output);
        var pre = Math.Min(na, (int)Math.Ceiling(offset * 25));
        var a = Pattern(na, format, 0); var b = Pattern(nb, format, 1);
        if (format == "yuv420p10le") Assert.Contains(Enumerable.Range(0, a.Length / 2), i => ReadSample(a, i, true) % 4 != 0);
        var graph = TransitionMap.Join(white ? "dip-to-white" : "dip-to-black", "[a]", "[b]", "[out]", offset, span / 25d, 25, format);
        var actual = await f.Join(a, b, format, "25", graph);
        EqualVideo(a, b, actual, format, pre, span, white);
        output.WriteLine($"D03 exact frames={pre + nb}; sample-values={(pre + nb) * Samples(format)}; no tolerance; B endpoint exists={nb > span}");
    }

    [Fact]
    public async Task D03_IntegerAndPadMutationsAreRejected()
    {
        await using var f = new Fixture(output);
        var a = Pattern(28, "yuv420p", 0, 18); var b = Pattern(33, "yuv420p", 1, 227);
        var graph = TransitionMap.Join("dip-to-white", "[a]", "[b]", "[out]", 0, 1.12, 25);
        var mutated = System.Text.RegularExpressions.Regex.Replace(graph, @"(?<![\d.])28(?![\d.])", "28.000000000000004");
        // Frame trim options are integral; mutate only the blend expression, not trim arguments.
        mutated = mutated.Replace("end_frame=28.000000000000004", "end_frame=28").Replace("start_frame=28.000000000000004", "start_frame=28");
        var bad = await f.Join(a, b, "yuv420p", "25", mutated);
        Assert.Equal(33, ReadSample(bad, Samples("yuv420p"), false));
        Assert.NotNull(Record.Exception(() => EqualVideo(a, b, bad, "yuv420p", 0, 28, true)));
        a = Pattern(50, "yuv420p", 0); b = Pattern(50, "yuv420p", 1);
        graph = TransitionMap.Join("dip-to-black", "[a]", "[b]", "[out]", 1.5, .5, 25);
        bad = await f.Join(a, b, "yuv420p", "25", graph.Replace("tpad=stop_mode=clone:stop=-1,", ""));
        Assert.Equal(87, bad.Length / Samples("yuv420p"));
        Assert.NotNull(Record.Exception(() => EqualVideo(a, b, bad, "yuv420p", 38, 13, false)));
    }

    [Fact]
    public void D04_PixelFormatContractAndNativeMappings()
    {
        foreach (var kind in new[] { "dip-to-black", "dip-to-white" })
        {
            Assert.Equal(kind == "dip-to-black" ? "fadeblack" : "fadewhite", TransitionMap.Xfade(kind));
            Assert.Equal(TransitionMap.Join(kind, "[a]", "[b]", "[o]", .2, .5, 25), TransitionMap.Join(kind, "[a]", "[b]", "[o]", .2, .5, 25, "yuv420p"));
            Assert.Equal("unsupported-dip-pixel-format:rgba;null", Assert.Throws<ArgumentException>(() => TransitionMap.Join(kind, "[a]", "[b]", "[o]", 0, 1, 25, "rgba;null")).Message);
        }
    }

    [Fact]
    public async Task D04_NonDipGraphsRemainFrozenAndFormatIsIgnored()
    {
        await using var f = new Fixture(output);
        var kinds = new[] { "cross-dissolve", "wipe-left", "wipe-right", "wipe-up", "wipe-down", "additive-dissolve", "non-additive-dissolve", "iris-box", "iris-cross", "iris-diamond", "iris-round", "barn-doors-vertical", "barn-doors-horizontal", "soft-wipe-up", "soft-wipe-right", "soft-wipe-down", "soft-wipe-left", "gradient-wipe-up", "gradient-wipe-right", "gradient-wipe-down", "gradient-wipe-left", "panel-wipe", "vr-iris-wipe", "vr-gradient-wipe", "flash", "ray", "vr-light-rays", "solarize", "flare", "light-sweep", "plateau-wipe", "shape-flow" };
        foreach (var kind in kinds)
        foreach (var offset in new[] { 0d, .2 })
        {
            var frozen = LegacyJoin(kind, "[a]", "[b]", "[out]", offset, .4, 25);
            Assert.Equal(frozen, TransitionMap.Join(kind, "[a]", "[b]", "[out]", offset, .4, 25));
            Assert.Equal(frozen, TransitionMap.Join(kind, "[a]", "[b]", "[out]", offset, .4, 25, "intentionally-invalid"));
            if (kind is "cross-dissolve" or "iris-round")
            {
                var a = Pattern(15, "gbrp", 0); var b = Pattern(20, "gbrp", 1);
                Assert.Equal(await f.Join(a, b, "gbrp", "25", frozen), await f.Join(a, b, "gbrp", "25", TransitionMap.Join(kind, "[a]", "[b]", "[out]", offset, .4, 25)));
            }
        }
    }

    // Frozen seven-parameter pre-D3 join shape; the unchanged non-dip kernels are not reimplemented.
    private static string LegacyJoin(string kind, string outgoing, string incoming, string result, double offset, double duration, double fps)
    {
        var expression = TransitionMap.BlendExpression(kind);
        if (expression is null) return $"{outgoing}{incoming}xfade=transition={TransitionMap.Xfade(kind)}:duration={N(duration)}:offset={N(offset)}{result}";
        var label = result.Trim('[', ']');
        var expressionAtTime = expression.Replace("$P", $"clip((N-1)/({N(duration)}*{N(fps)}),0,1)", StringComparison.Ordinal);
        var tail = offset == 0
            ? $"{outgoing}trim=duration={N(duration)},setpts=N/({N(fps)}*TB),format=gbrp[{label}a]"
            : $"{outgoing}split[{label}pre][{label}tail];[{label}pre]trim=end={N(offset)},setpts=N/({N(fps)}*TB),format=gbrp[{label}p];[{label}tail]trim=start={N(offset)}:duration={N(duration)},setpts=N/({N(fps)}*TB),format=gbrp[{label}a]";
        return tail + $";{incoming}split[{label}head][{label}post];[{label}head]trim=duration={N(duration)},setpts=N/({N(fps)}*TB),format=gbrp[{label}b];[{label}post]trim=start={N(duration)},setpts=N/({N(fps)}*TB),format=gbrp[{label}q];[{label}a][{label}b]blend=all_expr='{expressionAtTime}':shortest=1[{label}m];"
            + (offset == 0 ? $"[{label}m][{label}q]concat=n=2:v=1:a=0" : $"[{label}p][{label}m][{label}q]concat=n=3:v=1:a=0")
            + $",settb=AVTB,setpts=round(N/({N(fps)}*TB)){result}";
    }

    [Theory]
    [InlineData("25", 0, .5, "center", 88, 168000, 3.5, 1.5)]
    [InlineData("30000/1001", 0, 1, "center", 90, 144000, 3, 1)]
    [InlineData("25", 0, .04, "center", 100, 190400, 119d / 30, 59d / 30)]
    [InlineData("25", 0, .5, "start", 94, 180000, 3.75, 1.75)]
    [InlineData("25", 0, .5, "end", 82, 156000, 3.25, 1.25)]
    public async Task D05_RealAssembleAudioPtsAndTimeline(string rate, int projectFps, double requested, string align, int frames, int samples, double length, double bStart)
    {
        await using var f = new Fixture(output);
        await f.Source("a", rate, 3); await f.Source("b", rate, 3);
        var plan = Plan(projectFps, requested, align);
        var result = await f.Renderer.AssembleAsync(f.Store.Clips().Single(c => c.Id == "a"), plan, Path.Combine(f.Root, "assembled"), f.Token);
        Assert.Equal(length, result.Length);
        Assert.NotNull(result.Starts); Assert.Equal(0, result.Starts["a"]); Assert.Equal(bStart, result.Starts["b"]);
        Assert.Equal(bStart, Assert.Single(result.Subtitles.Where(s => s.Text == "B")).Start);
        var data = await f.Probe(result.Video, frames, "yuv420p", rate == "25" ? 25 : 2997003, rate == "25" ? 1 : 100000);
        var pcm = await f.Audio(result.Video);
        Assert.Equal(samples * 2 * sizeof(float), pcm.Length);
        var d = requested == .04 ? 1d / 30 : requested;
        var outgoingSamples = (int)Math.Round((2 + (align == "start" ? d / 2 : align == "end" ? -d / 2 : 0)) * 48000);
        var incomingSamples = 96000; var overlap = (int)Math.Round(d * 48000);
        EqualAudio(pcm, outgoingSamples, incomingSamples, overlap);
        var legacy = await f.LegacyAudio(outgoingSamples, incomingSamples, d);
        Assert.Equal(legacy, pcm);
        var argv = f.ActualJoinArgs(); var graph = argv[Array.IndexOf(argv, "-filter_complex") + 1];
        Assert.Contains($"acrossfade=d={d.ToString("0.######", CultureInfo.InvariantCulture)}:c1=tri:c2=tri", graph);
        Assert.Equal(length.ToString("0.######", CultureInfo.InvariantCulture), argv[Array.IndexOf(argv, "-t") + 1]);
        // Replay the actual production argv with only the dip video subgraph reverted to the frozen native join.
        // This compares old/new PCM from the same real part files, not just two analytical formulas.
        var graphStart = graph.IndexOf("[vin0]split", StringComparison.Ordinal);
        var graphEnd = graph.IndexOf("[vx1];", graphStart, StringComparison.Ordinal) + "[vx1]".Length;
        Assert.True(graphStart >= 0 && graphEnd > graphStart);
        var legacyArgs = (string[])argv.Clone();
        legacyArgs[Array.IndexOf(legacyArgs, "-filter_complex") + 1] = graph[..graphStart] + LegacyJoin("dip-to-white", "[vin0]", "[vin1]", "[vx1]", outgoingSamples / 48000d - d, d, result.Fps) + graph[graphEnd..];
        legacyArgs[^1] = Path.Combine(f.Root, "legacy-product.mkv");
        await f.Run(f.Tools.Ffmpeg, legacyArgs);
        Assert.Equal(pcm, await f.Audio(legacyArgs[^1]));
        output.WriteLine($"D05 frames={frames}, audioSamples={samples}, duration={length:R}, Bstart={bStart:R}; rational mux grid verified; legacy PCM identical; final argv retained.");
    }

    [Fact]
    public async Task D04_HighBitDepthRealAssembleAndConfiguredArguments()
    {
        await using var f = new Fixture(output);
        await f.Source("a", "30", 2, true); await f.Source("b", "30", 2, true);
        var plan = Plan(30, .4) with { Format = "hevc", ColorSpace = "hdr10" };
        var result = await f.Renderer.AssembleAsync(f.Store.Clips().Single(c => c.Id == "a"), plan, Path.Combine(f.Root, "assembled"), f.Token);
        Assert.Equal(3.6, result.Length);
        await f.Probe(result.Video, 108, "yuv420p10le", 30, 1, true);
        var raw = await f.Raw(result.Video, "yuv420p10le");
        Assert.Contains(Enumerable.Range(0, raw.Length / 2), i => ReadSample(raw, i, true) % 4 != 0);
        var args = f.ActualJoinArgs(); var graph = args[Array.IndexOf(args, "-filter_complex") + 1];
        Assert.Contains("format=yuv420p10le", graph); Assert.DoesNotContain("format=yuv420p[", graph);
        Assert.Contains("+940*", graph); Assert.Contains("+512*", graph); Assert.DoesNotContain("format=gbrp", graph);
        Assert.Contains("bt2020", args); Assert.Contains("smpte2084", args); Assert.Contains("bt2020nc", args);
        Assert.Equal(graph, EditorRenderService.HighBitDepthFilterGraph(graph));
        // Verify every sample of the actual configured graph, including non-multiple-of-four input values.
        var a = await f.Raw(Path.Combine(f.Root, "assembled", "part-000.mkv"), "yuv420p10le");
        var b = await f.Raw(Path.Combine(f.Root, "assembled", "part-001.mkv"), "yuv420p10le");
        EqualVideo(a, b, raw, "yuv420p10le", 48, 12, true);
    }

    [Theory]
    [InlineData("dip-to-black", "cross-dissolve")]
    [InlineData("cross-dissolve", "dip-to-white")]
    [InlineData("dip-to-white", "iris-round")]
    [InlineData("iris-round", "dip-to-black")]
    [InlineData("dip-to-black", "dip-to-white")]
    [InlineData("none", "none")]
    public async Task D06_MulticutNativeCustomAndNoTransition(string first, string second)
    {
        await using var f = new Fixture(output);
        foreach (var id in new[] { "a", "b", "c" }) await f.Source(id, "30", 2);
        var cuts = new List<EditorTransition>();
        if (first != "none") cuts.Add(new("one", "b", 1, first, .4, "center"));
        if (second != "none") cuts.Add(new("two", "c", 1, second, .4, "center"));
        var plan = Plan(30, .4) with { Timeline = [new("a", "a", 0, 2), new("b", "b", 0, 2), new("c", "c", 0, 2)], Transitions = cuts.ToArray(), End = 6 };
        var result = await f.Renderer.AssembleAsync(f.Store.Clips().Single(c => c.Id == "a"), plan, Path.Combine(f.Root, "assembled"), f.Token);
        var length = 6 - .4 * cuts.Count;
        Assert.Equal(length, result.Length); await f.Probe(result.Video, (int)Math.Round(length * 30), null, 30, 1);
        Assert.Equal((int)Math.Round(length * 48000) * 8, (await f.Audio(result.Video)).Length);
        if (cuts.Count > 0) { Assert.Equal(1.6, result.Starts!["b"]); Assert.Equal(3.2, result.Starts["c"]); }
        var argv = f.ActualJoinArgs(); var graph = argv[Array.IndexOf(argv, "-filter_complex") + 1];
        if (first == "iris-round" || second == "iris-round") Assert.Contains("format=gbrp", graph);
        output.WriteLine("D06 nominal seconds accumulation and mixed chain verified: " + first + " -> " + second);
    }

    [Fact]
    public async Task D06_OverlayMixAndKnownOldSubtitleStartHandleBias()
    {
        await using var f = new Fixture(output);
        await f.Source("a", "30", 2.1); await f.Source("b", "30", 2);
        var plan = Plan(0, .5, "start");
        var result = await f.Renderer.AssembleAsync(f.Store.Clips().Single(c => c.Id == "a"), plan, Path.Combine(f.Root, "handles"), f.Token);
        Assert.Equal(3.9, result.Length); Assert.Equal(1.9, result.Starts!["b"]);
        var caption = Assert.Single(result.Subtitles.Where(s => s.Text == "B"));
        Assert.Equal(1.75, caption.Start);
        Assert.Equal(150, (int)Math.Round((result.Starts["b"] - caption.Start) * 1000));
        output.WriteLine("KNOWN PRE-EXISTING NON-GOAL: source handle clamps actual d to .2; MapSubtitles resolves .5 without source handles, B caption is 150ms early. This test does NOT claim a fix.");
        var overlayPlan = Plan(30, .4) with { Timeline = [new("a", "a", 0, 2), new("b", "b", 0, 2), new("overlay", "b", 0, 1, Track: 2, Opacity: .5), new("mix", "b", 0, 1, Track: 4)] };
        var over = await f.Renderer.AssembleAsync(f.Store.Clips().Single(c => c.Id == "a"), overlayPlan, Path.Combine(f.Root, "overlays"), f.Token);
        Assert.Equal(3.6, over.Length); await f.Probe(over.Video, 108, null, 30, 1);
        Assert.Equal(172800 * 8, (await f.Audio(over.Video)).Length);
    }

    private static float Impulse(int index, int side, int channel) => index % 997 == (side == 0 ? 17 : 43) ? (side == 0 ? .5f : .25f) * (channel == 0 ? 1 : -1) : 0;
    private static void EqualAudio(byte[] actual, int aLength, int bLength, int overlap)
    {
        Assert.Equal((aLength + bLength - overlap) * 8, actual.Length);
        for (var n = 0; n < actual.Length / 8; n++)
        for (var c = 0; c < 2; c++)
        {
            float expected;
            if (n < aLength - overlap) expected = Impulse(n, 0, c);
            else if (n >= aLength) expected = Impulse(n - (aLength - overlap), 1, c);
            else
            {
                var k = n - (aLength - overlap);
                expected = (float)(Impulse(n, 0, c) * ((overlap - 1d - k) / overlap) + Impulse(k, 1, c) * (k / (double)overlap));
            }
            var got = BitConverter.ToSingle(actual, (n * 2 + c) * 4);
            Assert.True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(got), $"audio sample={n} channel={c} expected={expected:R} actual={got:R}");
        }
    }

    private static EditorProject Plan(int fps, double duration, string align = "center", string kind = "dip-to-white") => new()
    {
        Start = 0, End = 4, Fps = fps, Resolution = 0,
        Timeline = [new("a", "a", 0, 2), new("b", "b", 0, 2)],
        Transitions = [new("cut", "b", 1, kind, duration, align)],
        SourceTranscripts = new() { ["a"] = [new(0, .1, "A")], ["b"] = [new(0, .1, "B")] }
    };

    private static int Samples(string format) => format == "gbrp" ? W * H * 3 : W * H * 3 / 2;
    private static int ReadSample(byte[] bytes, int index, bool ten) => ten ? bytes[index * 2] | bytes[index * 2 + 1] << 8 : bytes[index];
    private static void WriteSample(byte[] bytes, int index, bool ten, int value)
    {
        if (ten) { bytes[index * 2] = (byte)value; bytes[index * 2 + 1] = (byte)(value >> 8); }
        else bytes[index] = (byte)value;
    }
    private static byte[] Pattern(int frames, string format, int source, int? constant = null)
    {
        var ten = format == "yuv420p10le";
        var count = Samples(format);
        var result = new byte[frames * count * (ten ? 2 : 1)];
        for (var frame = 0; frame < frames; frame++)
        for (var s = 0; s < count; s++)
        {
            var plane = format == "gbrp" ? s / (W * H) : s < W * H ? 0 : s < W * H * 5 / 4 ? 1 : 2;
            var local = format == "gbrp" ? s % (W * H) : plane == 0 ? s : (s - W * H) % (W * H / 4);
            var value = constant is int fixedY ? plane == 0 ? fixedY : 128
                : ten ? (plane == 0 ? 64 : 80) + (source * 193 + frame * 37 + local * 13 + plane * 29) % (plane == 0 ? 877 : 865)
                : format == "gbrp" ? (source * 83 + frame * 17 + local * 11 + plane * 43) % 256
                : (plane == 0 ? 16 : 20) + (source * 53 + frame * 19 + local * 7 + plane * 31) % (plane == 0 ? 220 : 221);
            WriteSample(result, frame * count + s, ten, value);
        }
        return result;
    }
    private static void EqualVideo(byte[] a, byte[] b, byte[] actual, string format, int pre, int span, bool white)
    {
        var ten = format == "yuv420p10le"; var count = Samples(format); var bytes = ten ? 2 : 1;
        var na = a.Length / count / bytes; var nb = b.Length / count / bytes;
        Assert.True(actual.Length == (pre + nb) * count * bytes, $"dip frame count: expected {pre + nb}, actual {actual.Length / count / bytes}");
        for (var frame = 0; frame < pre + nb; frame++)
        for (var s = 0; s < count; s++)
        {
            var q = frame - pre;
            var av = ReadSample(a, Math.Min(frame, na - 1) * count + s, ten);
            var expected = av;
            if (q >= 0)
            {
                var bv = ReadSample(b, q * count + s, ten);
                var chroma = format != "gbrp" && s >= W * H;
                var center = format == "gbrp" ? white ? 255 : 0 : (chroma ? 128 : white ? 235 : 16) * (ten ? 4 : 1);
                if (q >= span) expected = bv;
                else if (q > 0)
                {
                    BigInteger num = 2 * q <= span ? (BigInteger)av * (span - 2 * q) + center * 2 * q
                        : (BigInteger)center * (2 * span - 2 * q) + bv * (2 * q - span);
                    expected = (int)((2 * num + span) / (2 * span));
                }
            }
            var value = ReadSample(actual, frame * count + s, ten);
            Assert.True(expected == value, $"dip sample frame={frame}, q={q}, planeSample={s}, expected={expected}, actual={value}, format={format}, D={span}");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; }
        public MediaTools Tools { get; }
        public Store Store { get; }
        public EditorRenderService Renderer { get; }
        public CancellationToken Token => _timeout.Token;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));
        private readonly ITestOutputHelper _output;
        private int _serial;
        public Fixture(ITestOutputHelper output)
        {
            _output = output;
            Root = Path.Combine(Environment.GetEnvironmentVariable("D3_ARTIFACT_ROOT") ?? Path.Combine(Path.GetTempPath(), "liveclipper-d3"), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var ffmpeg = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
            Assert.True(!string.IsNullOrWhiteSpace(ffmpeg) && Path.IsPathFullyQualified(ffmpeg) && File.Exists(ffmpeg), "D3 requires absolute LIVECLIPPER_TEST_FFMPEG; never skip.");
            Tools = new MediaTools(ffmpeg);
            Assert.True(File.Exists(Tools.Ffprobe), "D3 requires sibling ffprobe; never skip.");
            Store = new Store(Path.Combine(Root, "test.db")); Store.Migrate();
            var studio = new StudioStore(Store.DatabasePath); studio.Migrate();
            var proxy = Environment.GetEnvironmentVariable("D3_FFMPEG_PROXY");
            Renderer = new EditorRenderService(Store, studio, string.IsNullOrWhiteSpace(proxy) ? Tools : new MediaTools(proxy), Root);
            output.WriteLine("D3 fixture: " + Root);
        }
        public async Task<string> Run(string executable, params string[] argv)
        {
            var prefix = Path.Combine(Root, $"command-{++_serial:0000}");
            var start = DateTimeOffset.UtcNow;
            using var process = MediaTools.Start(executable, argv);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(Token); }
            catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
            var so = await stdout; var se = await stderr;
            await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(new { executable, argv, pid = process.Id, start, end = DateTimeOffset.UtcNow, exit = process.ExitCode }));
            await File.WriteAllTextAsync(prefix + ".stdout", so); await File.WriteAllTextAsync(prefix + ".stderr", se);
            Assert.True(process.ExitCode == 0, $"{executable} exit={process.ExitCode}; {se}");
            return so;
        }
        public async Task<byte[]> Join(byte[] a, byte[] b, string format, string rate, string graph)
        {
            var tag = "join-" + Guid.NewGuid().ToString("N"); var ap = Path.Combine(Root, tag + "-a.raw"); var bp = Path.Combine(Root, tag + "-b.raw"); var dest = Path.Combine(Root, tag + "-out.raw");
            await File.WriteAllBytesAsync(ap, a, Token); await File.WriteAllBytesAsync(bp, b, Token);
            graph = "[0:v]settb=AVTB,setpts=PTS-STARTPTS[a];[1:v]settb=AVTB,setpts=PTS-STARTPTS[b];" + graph;
            await File.WriteAllTextAsync(Path.Combine(Root, tag + ".graph.txt"), graph, Token);
            await Run(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-filter_complex_threads", "1", "-f", "rawvideo", "-pixel_format", format, "-video_size", $"{W}x{H}", "-framerate", rate, "-i", ap,
                "-f", "rawvideo", "-pixel_format", format, "-video_size", $"{W}x{H}", "-framerate", rate, "-i", bp, "-filter_complex", graph, "-map", "[out]", "-fps_mode", "passthrough", "-pix_fmt", format, "-f", "rawvideo", dest);
            return await File.ReadAllBytesAsync(dest, Token);
        }
        public async Task FilterPts(byte[] a, byte[] b, string format, string rate, string graph, int expectedFrames)
        {
            var tag = Guid.NewGuid().ToString("N"); var ap = Path.Combine(Root, tag + "a.raw"); var bp = Path.Combine(Root, tag + "b.raw"); var dest = Path.Combine(Root, tag + ".nut");
            await File.WriteAllBytesAsync(ap, a); await File.WriteAllBytesAsync(bp, b);
            await Run(Tools.Ffmpeg, "-v", "error", "-y", "-filter_complex_threads", "1", "-f", "rawvideo", "-pix_fmt", format, "-s", $"{W}x{H}", "-r", rate, "-i", ap,
                "-f", "rawvideo", "-pix_fmt", format, "-s", $"{W}x{H}", "-r", rate, "-i", bp,
                "-filter_complex", "[0:v]settb=AVTB[a];[1:v]settb=AVTB[b];" + graph, "-map", "[out]", "-fps_mode", "passthrough", "-enc_time_base", "1:1000000", "-c:v", "rawvideo", "-pix_fmt", format, dest);
            await Probe(dest, expectedFrames, format, rate == "25" ? 25 : 30000, rate == "25" ? 1 : 1001);
        }
        public async Task<JsonElement> Probe(string path, int frames, string? format, long fpsNum, long fpsDen, bool hdr = false)
        {
            var json = await Run(Tools.Ffprobe, "-v", "error", "-select_streams", "v:0", "-show_streams", "-show_frames", "-of", "json", path);
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            var stream = Assert.Single(root.GetProperty("streams").EnumerateArray());
            var all = root.GetProperty("frames").EnumerateArray().ToArray(); Assert.Equal(frames, all.Length);
            var tb = stream.GetProperty("time_base").GetString()!.Split('/'); var tbNum = long.Parse(tb[0]); var tbDen = long.Parse(tb[1]);
            long previous = -1;
            for (var i = 0; i < all.Length; i++)
            {
                var n = (BigInteger)i * fpsDen * tbDen; var d = (BigInteger)fpsNum * tbNum;
                var expected = (long)((2 * n + d) / (2 * d)); var pts = all[i].GetProperty("pts").GetInt64();
                Assert.True(pts == expected, $"PTS i={i}, expected={expected}, actual={pts}, rate={fpsNum}/{fpsDen}, tb={tbNum}/{tbDen}");
                Assert.True(pts > previous); previous = pts;
            }
            if (format is not null) Assert.Equal(format, stream.GetProperty("pix_fmt").GetString());
            if (hdr) { Assert.Equal("bt2020", stream.GetProperty("color_primaries").GetString()); Assert.Equal("smpte2084", stream.GetProperty("color_transfer").GetString()); Assert.Equal("bt2020nc", stream.GetProperty("color_space").GetString()); }
            return stream.Clone();
        }
        public async Task Source(string id, string rate, double seconds, bool high = false)
        {
            var r = rate == "30000/1001" ? 30000d / 1001 : double.Parse(rate, CultureInfo.InvariantCulture);
            var format = high ? "yuv420p10le" : "yuv420p";
            var raw = Path.Combine(Root, id + ".raw"); var pcm = Path.Combine(Root, id + ".f32"); var path = Path.Combine(Root, id + ".mkv");
            await File.WriteAllBytesAsync(raw, Pattern((int)Math.Ceiling(seconds * r), format, id == "a" ? 0 : 1));
            var audio = new byte[(int)Math.Round(seconds * 48000) * 8];
            for (var n = 0; n < audio.Length / 8; n++) for (var c = 0; c < 2; c++) BitConverter.GetBytes(Impulse(n, id == "a" ? 0 : 1, c)).CopyTo(audio, (2 * n + c) * 4);
            await File.WriteAllBytesAsync(pcm, audio);
            var args = new List<string> { "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", format, "-s", $"{W}x{H}", "-r", rate, "-i", raw, "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", pcm, "-t", N(seconds), "-c:v", "ffv1", "-level", "3", "-pix_fmt", format, "-c:a", "pcm_f32le" };
            if (high) args.AddRange(["-color_range", "tv", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc"]);
            args.Add(path); await Run(Tools.Ffmpeg, args.ToArray()); Store.SaveClip(new Clip(id, "d3-" + id, path, null, DateTimeOffset.UtcNow));
        }
        public async Task<byte[]> Raw(string path, string format)
        {
            var dest = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".raw");
            await Run(Tools.Ffmpeg, "-v", "error", "-y", "-i", path, "-map", "0:v:0", "-an", "-pix_fmt", format, "-fps_mode", "passthrough", "-f", "rawvideo", dest);
            return await File.ReadAllBytesAsync(dest);
        }
        public async Task<byte[]> Audio(string path)
        {
            var dest = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".f32");
            await Run(Tools.Ffmpeg, "-v", "error", "-y", "-i", path, "-map", "0:a:0", "-vn", "-c:a", "pcm_f32le", "-f", "f32le", dest);
            return await File.ReadAllBytesAsync(dest);
        }
        public async Task<byte[]> LegacyAudio(int aLength, int bLength, double duration)
        {
            var dest = Path.Combine(Root, "legacy-audio.f32");
            await Run(Tools.Ffmpeg, "-v", "error", "-y", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", Path.Combine(Root, "a.f32"), "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", Path.Combine(Root, "b.f32"),
                "-filter_complex", $"[0:a]atrim=end_sample={aLength},asetpts=PTS-STARTPTS[a];[1:a]atrim=end_sample={bLength},asetpts=PTS-STARTPTS[b];[a][b]acrossfade=d={duration.ToString("0.######", CultureInfo.InvariantCulture)}:c1=tri:c2=tri[o]", "-map", "[o]", "-f", "f32le", dest);
            return await File.ReadAllBytesAsync(dest);
        }
        public string[] ActualJoinArgs()
        {
            Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("D3_FFMPEG_PROXY")), "D3 product integration requires transparent FFmpeg argv recorder.");
            var records = Directory.GetFiles(Root, "actual-ffmpeg-*.json", SearchOption.AllDirectories);
            foreach (var record in records)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(record)); var args = doc.RootElement.GetProperty("argv").EnumerateArray().Select(x => x.GetString()!).ToArray();
                if (args.Contains("-filter_complex") && args.Last().EndsWith("joined.mkv", StringComparison.Ordinal)) return args;
            }
            throw new InvalidOperationException("No actual joined.mkv FFmpeg invocation recorded.");
        }
        public async ValueTask DisposeAsync() { await Renderer.StopAsync(); _timeout.Dispose(); _output.WriteLine("D3 fixture retained: " + Root); }
    }
}
