using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Core.EditorFx;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

public sealed class EditorFxAudioChainTests
{
    private static CatalogAudioFxInstance Fx(string id, bool enabled = true, params (string K, double V)[] p)
        => new(id, enabled, p.ToDictionary(x => x.K, x => x.V));

    [Fact]
    public void Build_OrdersFiveEffects_UsesPreviewDbHzAndQ_SkipsDisabled()
    {
        var chain = new[] { Fx("amplify", true, ("gain", 6)), Fx("highpass", true, ("cutoff", 80)),
            Fx("lowpass", true, ("cutoff", 8000)), Fx("bass", true, ("boost", 3)),
            Fx("treble", false, ("boost", 10)), Fx("treble", true, ("boost", -4)) };
        Assert.Equal("volume=6dB:precision=double,highpass=f=80:p=2:t=q:w=0.707:r=f64,lowpass=f=8000:p=2:t=q:w=0.707:r=f64,lowshelf=g=3:f=200:p=2:t=q:w=0.707:r=f64,highshelf=g=-4:f=4000:p=2:t=q:w=0.707:r=f64", AudioFxChain.Build(chain));
    }

    [Fact]
    public void Build_DefaultsIdentityAndSilence_MatchPreview()
    {
        Assert.Equal("", AudioFxChain.Build([]));
        Assert.Equal("", AudioFxChain.Build(new[] { Fx("amplify"), Fx("bass"), Fx("treble"), Fx("lowpass", false) }));
        Assert.Equal("highpass=f=80:p=2:t=q:w=0.707:r=f64,lowpass=f=8000:p=2:t=q:w=0.707:r=f64", AudioFxChain.Build(new[] { Fx("highpass"), Fx("lowpass") }));
        Assert.Equal("volume=0:precision=double", AudioFxChain.Build(new[] { Fx("amplify", true, ("gain", -96)) }));
    }

    [Theory]
    [InlineData("amplify", "gain", -96)]
    [InlineData("amplify", "gain", 24)]
    [InlineData("highpass", "cutoff", 20)]
    [InlineData("highpass", "cutoff", 20000)]
    [InlineData("lowpass", "cutoff", 20)]
    [InlineData("lowpass", "cutoff", 20000)]
    [InlineData("bass", "boost", -30)]
    [InlineData("bass", "boost", 30)]
    [InlineData("treble", "boost", -30)]
    [InlineData("treble", "boost", 30)]
    public void Build_PreviewRangeEndpoints_AreAccepted(string id, string key, double value)
        => Assert.NotEmpty(AudioFxChain.Build(new[] { Fx(id, true, (key, value)) }));

    [Theory]
    [InlineData("amplify", "gain", -96.01)]
    [InlineData("amplify", "gain", 24.01)]
    [InlineData("highpass", "cutoff", 19.99)]
    [InlineData("highpass", "cutoff", 20000.01)]
    [InlineData("lowpass", "cutoff", 19.99)]
    [InlineData("lowpass", "cutoff", 20000.01)]
    [InlineData("bass", "boost", -30.01)]
    [InlineData("bass", "boost", 30.01)]
    [InlineData("treble", "boost", -30.01)]
    [InlineData("treble", "boost", 30.01)]
    [InlineData("amplify", "gain", double.NaN)]
    [InlineData("highpass", "cutoff", double.PositiveInfinity)]
    [InlineData("treble", "boost", double.NegativeInfinity)]
    public void Build_InvalidValue_ThrowsStableCodeEvenWhenDisabled(string id, string key, double value)
    {
        foreach (var enabled in new[] { false, true })
            Assert.Equal(AudioFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Fact]
    public void Build_RejectsUnknownIdsParamsAndNulls_WithStableCodes()
    {
        Assert.Equal(AudioFxChain.InvalidChain, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(null!)).Message);
        Assert.Equal(AudioFxChain.InvalidChain, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new CatalogAudioFxInstance[] { null! })).Message);
        Assert.Equal(AudioFxChain.InvalidChain, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { new CatalogAudioFxInstance("bass", true, null!) })).Message);
        Assert.Equal(AudioFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("reverb", false) })).Message);
        Assert.Equal(AudioFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("amplify", true, ("volume", 1)) })).Message);
        Assert.Equal(AudioFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("bass", false, ("frequency", 100)) })).Message);
    }

    [Fact]
    public void Build_IsCultureInvariant()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("volume=1.25dB:precision=double", AudioFxChain.Build(new[] { Fx("amplify", true, ("gain", 1.25)) }));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void AudioFilter_UsesSeparateCatalogChain_AfterHostFxBeforeEnvelopeAndFades(int track)
    {
        var host = new[] {
            new AudioEffectInstance("host-comp", "compressor", true, new() { ["ratio"] = 4 }),
            new AudioEffectInstance("host-gate", "noiseGate", true, new() { ["ratio"] = 2 }),
            new AudioEffectInstance("host-eq", "parametric_eq", true, new() { ["master_gain"] = 2 }) };
        var item = new TimelineItem("item", "source", 0, 2, Track: track, VolumeDb: 3, Speed: 1.25, FadeIn: .1, Effects: host,
            VolumeKeyframes: new[] { new VolumeKeyframe(0, -3), new VolumeKeyframe(1, 0) });
        var baseline = EditorRenderService.AudioFilterChain(item);
        var withFx = item with { CatalogAudioFxVersion = 1, CatalogAudioFx = new[] { Fx("amplify", true, ("gain", 6)), Fx("lowpass") } };
        var rendered = EditorRenderService.AudioFilterChain(withFx);
        var expected = baseline.Replace(",volume=volume=", ",aresample=48000," + AudioFxChain.Build(withFx.CatalogAudioFx!) + ",volume=volume=", StringComparison.Ordinal);
        Assert.Contains("acompressor=", baseline);
        Assert.Contains("agate=", baseline);
        Assert.Contains("aresample=48000", baseline);
        Assert.Contains("volume=volume=", baseline);
        Assert.Contains("atempo=1.25", baseline);
        Assert.Equal(expected, rendered);
        Assert.Same(host, withFx.Effects);
        Assert.Equal(baseline, EditorRenderService.AudioFilterChain(item with { CatalogAudioFxVersion = 1, CatalogAudioFx = [] }));
    }

    [Fact]
    public void AudioFilter_RejectsInvalidVersionAndUnsupportedTrack()
    {
        var item = new TimelineItem("item", "source", 0, 1) { CatalogAudioFxVersion = 1, CatalogAudioFx = new[] { Fx("amplify") } };
        Assert.Equal(AudioFxChain.InvalidVersion, Assert.Throws<ArgumentException>(() => EditorRenderService.AudioFilterChain(item with { CatalogAudioFxVersion = 2 })).Message);
        Assert.Equal(AudioFxChain.InvalidVersion, Assert.Throws<ArgumentException>(() => EditorRenderService.AudioFilterChain(item with { CatalogAudioFx = null })).Message);
        Assert.Equal(AudioFxChain.InvalidVersion, Assert.Throws<ArgumentException>(() => EditorRenderService.AudioFilterChain(item with { CatalogAudioFxVersion = null })).Message);
        foreach (var track in new[] { 0, 2, 3, 7 })
            Assert.Equal(AudioFxChain.UnsupportedTrack, Assert.Throws<ArgumentException>(() => EditorRenderService.AudioFilterChain(item with { Track = track })).Message);
    }

    [Fact]
    public void Codec_RoundTripsCatalogAudioAndHostFx_WithoutChangingLegacyJson()
    {
        const string json = """{"schemaVersion":1,"timeline":[{"id":"a1","clipId":"c","start":0,"end":1,"track":4,"catalogAudioFxVersion":1,"catalogAudioFx":[{"id":"amplify","enabled":true,"params":{"gain":6}}],"effects":[{"id":"host","type":"compressor","enabled":true,"params":{"ratio":4}}]}]}""";
        var project = EditorProjectCodec.Deserialize(json);
        var item = Assert.Single(project.Timeline!);
        Assert.Equal(1, item.CatalogAudioFxVersion);
        Assert.Equal(6d, Assert.Single(item.CatalogAudioFx!).Params["gain"]);
        Assert.Equal("compressor", Assert.Single(item.Effects!).Type);
        var restored = EditorProjectCodec.Deserialize(JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var restoredItem = Assert.Single(restored.Timeline!);
        Assert.Equal(item.CatalogAudioFx![0].Id, restoredItem.CatalogAudioFx![0].Id);
        Assert.Equal(4d, restoredItem.Effects![0].Params!["ratio"]);
        var legacy = JsonSerializer.Serialize(new TimelineItem("old", "source", 0, 1), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("catalogAudioFx", legacy);
    }

    [Theory]
    [InlineData("\"catalogAudioFx\":[]", AudioFxChain.InvalidVersion)]
    [InlineData("\"catalogAudioFxVersion\":2,\"catalogAudioFx\":[]", AudioFxChain.InvalidVersion)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":null", AudioFxChain.InvalidChain)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"amplify\",\"enabled\":true,\"params\":{\"gain\":25}}]", AudioFxChain.InvalidParam)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"reverb\",\"enabled\":false,\"params\":{}}]", AudioFxChain.UnsupportedId)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"bass\",\"enabled\":true,\"params\":{\"gain\":3}}]", AudioFxChain.UnsupportedParam)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"bass\",\"enabled\":true,\"params\":{},\"mix\":0.5}]", "catalog-audio-fx-invalid-field")]
    [InlineData("\"catalogAudioFxVersion\":1,\"CatalogAudioFxVersion\":2,\"catalogAudioFx\":[]", "catalog-audio-fx-duplicate-field")]
    public void Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested(string fields, string code)
    {
        var item = "{\"id\":\"a1\",\"track\":4," + fields + "}";
        foreach (var container in new[] { "\"timeline\":[" + item + "]", "\"silenceTrimBackup\":[" + item + "]", "\"nestedSequences\":{\"n\":{\"timeline\":[" + item + "]}}" })
        {
            var ex = Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.EnsureSupported("{\"schemaVersion\":1," + container + "}"));
            Assert.Equal(EditorProjectSchemaErrors.Invalid, ex.Code);
            Assert.Equal(code, ex.Message);
        }
    }

    [Fact]
    public void Codec_RejectsAudioCatalogOnOverlayTracks()
    {
        foreach (var track in new[] { 2, 3 })
        {
            var ex = Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.EnsureSupported("{\"schemaVersion\":1,\"timeline\":[{\"track\":" + track + ",\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[]}]}"));
            Assert.Equal(AudioFxChain.UnsupportedTrack, ex.Message);
        }
    }

    [Fact]
    public void Build_A2StaticFilters_KeepOrderAndExactDryWetTaps()
    {
        var chain = new[] { Fx("delay", true, ("delay", .1), ("feedback", 0), ("mix", 25)),
            Fx("analog_delay", true, ("delay", 125), ("feedback", 0), ("dry", 30), ("wet", 60), ("trash", 200), ("mode", 2)),
            Fx("bandpass", true, ("center", 1000), ("q", 2)), Fx("simple_notch", true, ("center", 8000), ("q", 10)),
            Fx("simple_eq", true, ("center", 500), ("q", .75), ("boost", 6)) };
        Assert.Equal("aformat=sample_fmts=dbl,asplit=2[catalogAudioTap0d][catalogAudioTap0w];[catalogAudioTap0d]volume=0.75:precision=double[catalogAudioTap0dv];[catalogAudioTap0w]adelay=4800S:all=1,volume=0.25:precision=double[catalogAudioTap0wv];[catalogAudioTap0dv][catalogAudioTap0wv]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,aformat=sample_fmts=dbl,asplit=2[catalogAudioTap1d][catalogAudioTap1w];[catalogAudioTap1d]volume=0.3:precision=double[catalogAudioTap1dv];[catalogAudioTap1w]adelay=6000S:all=1,volume=0.6:precision=double[catalogAudioTap1wv];[catalogAudioTap1dv][catalogAudioTap1wv]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,bandpass=f=1000:t=q:w=2:r=f64,bandreject=f=8000:t=q:w=10:r=f64,equalizer=f=500:t=q:w=0.75:g=6:r=f64", AudioFxChain.Build(chain));
        Assert.Equal("adelay=4801S:all=1", AudioFxChain.Build(new[] { Fx("delay", true, ("delay", 4801d / 48000), ("feedback", 0), ("mix", 100)) }));
        Assert.Equal("adelay=6000S:all=1,volume=0.6:precision=double", AudioFxChain.Build(new[] { Fx("analog_delay", true, ("delay", 125), ("feedback", 0), ("dry", 0), ("wet", 60)) }));
        Assert.Equal("", AudioFxChain.Build(new[] { Fx("delay", true, ("mix", 0), ("feedback", 95)), Fx("notch"), Fx("parametric_eq"), Fx("graphic_eq"), Fx("graphic_eq_20"), Fx("graphic_eq_30") }));
        Assert.Equal("volume=0:precision=double", AudioFxChain.Build(new[] { Fx("analog_delay", true, ("wet", 0), ("dry", 0), ("feedback", 200), ("spread", 200)) }));
    }

    [Fact]
    public void Build_A2NotchEqAndGraphicBanks_UseFiniteCutsAndAllDeclaredBands()
    {
        var notch = AudioFxChain.Build(new[] { Fx("notch", true, ("width", 1), ("n1_on", 1), ("n1_freq", 1000), ("n1_gain", -30)) });
        Assert.Equal("bandreject=f=1000:t=q:w=20:m=" + (1 - Math.Pow(10, -30d / 20)).ToString("R", CultureInfo.InvariantCulture) + ":r=f64", notch);
        var peq = AudioFxChain.Build(new[] { Fx("parametric_eq", true, ("hp_on", 1), ("hp_slope", 3), ("lp_on", 1), ("lp_slope", 2),
            ("low_gain", 6), ("high_gain", -3), ("b1_gain", 1), ("b2_gain", 2), ("mid_gain", 3), ("b4_gain", 4), ("b5_gain", 5), ("master_gain", -2)) });
        Assert.Equal(4, peq.Split(',').Count(x => x.StartsWith("highpass=", StringComparison.Ordinal)));
        Assert.Equal(3, peq.Split(',').Count(x => x.StartsWith("lowpass=", StringComparison.Ordinal)));
        Assert.Equal(5, peq.Split(',').Count(x => x.StartsWith("equalizer=", StringComparison.Ordinal)));
        Assert.Contains("lowshelf=f=100:t=q:w=0.707:g=6:r=f64", peq);
        Assert.Contains("highshelf=f=8000:t=q:w=0.707:g=-3:r=f64", peq);
        Assert.EndsWith("volume=-2dB:precision=double", peq);
        foreach (var (id, bands, lastFrequency) in new[] { ("graphic_eq", 10, 16000), ("graphic_eq_20", 20, 22400), ("graphic_eq_30", 30, 20000) })
        {
            var values = Enumerable.Range(1, bands).Select(i => ($"b{i}", i % 2 == 0 ? 24d : -24d)).Append(("gain", -2d)).ToArray();
            var graphic = AudioFxChain.Build(new[] { Fx(id, true, values) });
            Assert.Equal(bands, graphic.Split(',').Count(x => x.StartsWith("equalizer=", StringComparison.Ordinal)));
            Assert.Contains($"equalizer=f={lastFrequency}:", graphic);
            Assert.EndsWith("volume=-2dB:precision=double", graphic);
        }
    }

    [Theory]
    [InlineData("delay", "delay", .00099)]
    [InlineData("delay", "delay", 2.01)]
    [InlineData("delay", "mix", 100.01)]
    [InlineData("analog_delay", "delay", 8001)]
    [InlineData("analog_delay", "mode", 1.5)]
    [InlineData("analog_delay", "trash", 201)]
    [InlineData("notch", "width", .5)]
    [InlineData("notch", "n1_on", .5)]
    [InlineData("notch", "n6_gain", -90.01)]
    [InlineData("simple_notch", "center", 20001)]
    [InlineData("bandpass", "q", .099)]
    [InlineData("simple_eq", "boost", 30.01)]
    [InlineData("parametric_eq", "hp_on", .5)]
    [InlineData("parametric_eq", "lp_slope", 4)]
    [InlineData("parametric_eq", "low_q", 30.01)]
    [InlineData("parametric_eq", "master_gain", double.NaN)]
    [InlineData("graphic_eq", "b10", -24.01)]
    [InlineData("graphic_eq_20", "b20", 24.01)]
    [InlineData("graphic_eq_30", "gain", double.PositiveInfinity)]
    public void Build_A2InvalidValues_AreRejectedEvenWhenDisabled(string id, string key, double value)
    {
        foreach (var enabled in new[] { false, true })
            Assert.Equal(AudioFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Fact]
    public void Build_A2UnfaithfulSubsetsAndUnknownKeys_AreRejectedWithoutTruncation()
    {
        var cases = new[] { Fx("delay"), Fx("delay", true, ("feedback", 1)), Fx("delay", true, ("delay", .10001), ("feedback", 0)),
            Fx("analog_delay"), Fx("analog_delay", true, ("feedback", 0), ("spread", 1)), Fx("analog_delay", true, ("feedback", 0), ("delay", 250.01)) };
        foreach (var effect in cases)
            foreach (var enabled in new[] { false, true })
                Assert.Equal(AudioFxChain.UnsupportedMapping, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { effect with { Enabled = enabled } })).Message);
        foreach (var effect in new[] { Fx("notch", true, ("n7_on", 1)), Fx("parametric_eq", true, ("b3_gain", 6)), Fx("graphic_eq", true, ("b11", 1)), Fx("graphic_eq_20", true, ("b21", 1)), Fx("graphic_eq_30", true, ("b31", 1)) })
            Assert.Equal(AudioFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { effect })).Message);
        // Dehummer's whole-cascade dry/wet projection is covered by R74A5 real PCM tests below.
        foreach (var id in new[] { "denoise", "dereverb" })
            Assert.Equal(AudioFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id) })).Message);
    }

    [Theory]
    [InlineData("delay", "\"delay\":0.1,\"feedback\":0,\"mix\":25")]
    [InlineData("analog_delay", "\"feedback\":0,\"wet\":100,\"dry\":0")]
    [InlineData("notch", "\"n6_on\":1,\"n6_gain\":-90,\"width\":2")]
    [InlineData("simple_notch", "\"center\":1000,\"q\":30")]
    [InlineData("bandpass", "\"center\":1000,\"q\":2")]
    [InlineData("simple_eq", "\"boost\":6")]
    [InlineData("parametric_eq", "\"hp_on\":1,\"hp_slope\":3,\"low_gain\":6,\"mid_gain\":-3")]
    [InlineData("graphic_eq", "\"b10\":24")]
    [InlineData("graphic_eq_20", "\"b20\":24")]
    [InlineData("graphic_eq_30", "\"b30\":24")]
    public void Codec_A2NumericV1_RoundTripsInAllItemContainers(string id, string parameters)
    {
        var fxJson = "{\"id\":\"" + id + "\",\"enabled\":true,\"params\":{" + parameters + "}}";
        var itemJson = "{\"id\":\"a1\",\"track\":4,\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[" + fxJson + "]}";
        foreach (var container in new[] { "\"timeline\":[" + itemJson + "]", "\"silenceTrimBackup\":[" + itemJson + "]", "\"nestedSequences\":{\"n\":{\"timeline\":[" + itemJson + "]}}" })
        {
            var project = EditorProjectCodec.Deserialize("{\"schemaVersion\":1," + container + "}");
            var json = JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            EditorProjectCodec.EnsureSupported(json);
            Assert.Contains("\"catalogAudioFxVersion\":1", json);
            Assert.Contains("\"id\":\"" + id + "\"", json);
        }
    }

    [Theory]
    [InlineData("\"catalogAudioFxVersion\":1.0,\"catalogAudioFx\":[]", AudioFxChain.InvalidVersion)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"delay\",\"enabled\":true,\"params\":{\"feedback\":20}}]", AudioFxChain.UnsupportedMapping)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"notch\",\"enabled\":true,\"params\":{\"n1_on\":true}}]", AudioFxChain.InvalidParam)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"parametric_eq\",\"enabled\":true,\"params\":{\"mid_gain\":31}}]", AudioFxChain.InvalidParam)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"graphic_eq_30\",\"enabled\":true,\"params\":{\"b30\":3,\"b30\":6}}]", AudioFxChain.InvalidParam)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"denoise\",\"enabled\":true,\"params\":{\"amount\":0}}]", AudioFxChain.UnsupportedId)]
    public void Codec_A2InvalidNumericV1_RejectsTimelineBackupAndNested(string fields, string code)
        => Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested(fields, code);

    private static string Ffmpeg()
    {
        var env = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }

    private static async Task<double> SineRms(int frequency, string filter)
    {
        var psi = new ProcessStartInfo(Ffmpeg()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-nostdin", "-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000:duration=1", "-af", string.IsNullOrEmpty(filter) ? "anull" : filter, "-ac", "1", "-ar", "48000", "-c:a", "pcm_f64le", "-f", "f64le", "-" })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        using var output = new MemoryStream();
        var error = process.StandardError.ReadToEndAsync();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("ffmpeg timed out"); }
        await copy;
        Assert.True(process.ExitCode == 0, "ffmpeg failed: " + await error);
        var pcm = output.ToArray();
        Assert.Equal(48000 * sizeof(double), pcm.Length);
        double squareSum = 0;
        // Exclude the first 100 ms of biquad startup; compare steady-state gain, not filter history.
        const int first = 4800;
        for (var i = first; i < 48000; i++)
        {
            var sample = BinaryPrimitives.ReadDoubleLittleEndian(pcm.AsSpan(i * sizeof(double), sizeof(double)));
            Assert.True(double.IsFinite(sample), "non-finite sample");
            squareSum += sample * sample;
        }
        return Math.Sqrt(squareSum / (48000 - first));
    }

    private static string RenderChain(params CatalogAudioFxInstance[] effects)
        => EditorRenderService.AudioFilterChain(new TimelineItem("a1", "source", 0, 1, Track: 4)
            { CatalogAudioFxVersion = 1, CatalogAudioFx = effects });

    private static async Task<double[]> RenderSamples(double[] samples, string filter)
    {
        var input = new byte[samples.Length * sizeof(double)];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(input.AsSpan(i * sizeof(double), sizeof(double)), samples[i]);
        var psi = new ProcessStartInfo(Ffmpeg()) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-nostdin", "-f", "f64le", "-ar", "48000", "-ac", "1", "-i", "-", "-af", string.IsNullOrEmpty(filter) ? "anull" : filter,
            "-t", (samples.Length / 48000d).ToString("R", CultureInfo.InvariantCulture), "-c:a", "pcm_f64le", "-f", "f64le", "-" }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var error = process.StandardError.ReadToEndAsync();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        async Task Feed()
        {
            await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
            process.StandardInput.Close();
        }
        var feed = Feed();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("ffmpeg timed out"); }
        await Task.WhenAll(copy, feed);
        Assert.True(process.ExitCode == 0, "ffmpeg failed: " + await error);
        var pcm = output.ToArray();
        Assert.Equal(input.Length, pcm.Length);
        var result = new double[samples.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadDoubleLittleEndian(pcm.AsSpan(i * sizeof(double), sizeof(double)));
            Assert.True(double.IsFinite(result[i]), "non-finite sample");
        }
        return result;
    }

    private static async Task<double> SineRms64(int frequency, string filter)
    {
        // lavfi sine is quantized s16 and leaves harmonics in a deep-notch test; feed a clean f64 tone instead.
        var samples = Enumerable.Range(0, 48000).Select(i => .125 * Math.Sin(2 * Math.PI * frequency * i / 48000)).ToArray();
        var output = await RenderSamples(samples, filter);
        return Math.Sqrt(output.Skip(4800).Sum(x => x * x) / (output.Length - 4800));
    }

    [Fact]
    public async Task RealFfmpeg_A2BandpassNotchesAndSimpleEq_HaveExpectedFrequencyResponse()
    {
        var baseline = await SineRms64(1000, "anull");
        var bandpass = RenderChain(Fx("bandpass", true, ("center", 1000), ("q", 2)));
        Assert.InRange(await SineRms64(1000, bandpass) / baseline, .998, 1.002);
        Assert.InRange(await SineRms64(8000, bandpass) / await SineRms64(8000, "anull"), .001, .1);
        var finiteNotch = RenderChain(Fx("notch", true, ("n1_on", 1), ("n1_freq", 1000), ("n1_gain", -30), ("width", 1)));
        Assert.InRange(await SineRms64(1000, finiteNotch) / baseline, .031, .0323);
        var infiniteNotch = RenderChain(Fx("simple_notch", true, ("center", 1000), ("q", 10)));
        Assert.InRange(await SineRms64(1000, infiniteNotch) / baseline, 0, .0001);
        Assert.InRange(await SineRms64(8000, infiniteNotch) / await SineRms64(8000, "anull"), .99, 1.001);
        var peak = RenderChain(Fx("simple_eq", true, ("center", 1000), ("q", 1), ("boost", 6)));
        Assert.InRange(await SineRms64(1000, peak) / baseline, 1.993, 1.998);
    }

    [Fact]
    public async Task RealFfmpeg_A2ParametricAndAllGraphicBanks_HaveExpectedGainAndButterworthSlopes()
    {
        var at1000 = await SineRms64(1000, "anull");
        var parametric = RenderChain(Fx("parametric_eq", true, ("mid_gain", 6), ("master_gain", -2)));
        Assert.InRange(await SineRms64(1000, parametric) / at1000, 1.583, 1.587);
        foreach (var (id, band, frequency) in new[] { ("graphic_eq", 6, 1000), ("graphic_eq_20", 20, 22400), ("graphic_eq_30", 30, 20000) })
        {
            var chain = RenderChain(Fx(id, true, ($"b{band}", 6), ("gain", -2)));
            var baseline = frequency == 1000 ? at1000 : await SineRms64(frequency, "anull");
            Assert.InRange(await SineRms64(frequency, chain) / baseline, 1.582, 1.588);
        }
        for (var slope = 0; slope <= 3; slope++)
        {
            var hp = RenderChain(Fx("parametric_eq", true, ("hp_on", 1), ("hp_freq", 1000), ("hp_slope", slope)));
            var lp = RenderChain(Fx("parametric_eq", true, ("lp_on", 1), ("lp_freq", 1000), ("lp_slope", slope)));
            Assert.InRange(await SineRms64(1000, hp) / at1000, .705, .709);
            Assert.InRange(await SineRms64(1000, lp) / at1000, .705, .709);
            Assert.True(await SineRms64(100, hp) / await SineRms64(100, "anull") < .011);
            Assert.True(await SineRms64(8000, lp) / await SineRms64(8000, "anull") < .014);
        }
        var shelves = RenderChain(Fx("parametric_eq", true, ("low_gain", 6), ("high_gain", -6)));
        Assert.InRange(await SineRms64(20, shelves) / await SineRms64(20, "anull"), 1.98, 2.01);
        Assert.InRange(await SineRms64(20000, shelves) / await SineRms64(20000, "anull"), .49, .52);
    }

    [Fact]
    public async Task RealFfmpeg_A2MixedTapAndAdelay_PreserveTapSamplesDryWetAndClipEnd()
    {
        var input = new double[24000]; input[0] = .5;
        var cases = new[] {
            (Fx("delay", true, ("delay", .1), ("feedback", 0), ("mix", 25)), 4800, .75, .25),
            (Fx("delay", true, ("delay", 4801d / 48000), ("feedback", 0), ("mix", 100)), 4801, 0d, 1d),
            (Fx("analog_delay", true, ("delay", 125), ("feedback", 0), ("dry", 30), ("wet", 60), ("trash", 200), ("mode", 2)), 6000, .3, .6),
            (Fx("analog_delay", true, ("delay", 125), ("feedback", 0), ("dry", 0), ("wet", 60)), 6000, 0d, .6) };
        foreach (var (effect, tap, dry, wet) in cases)
        {
            var output = await RenderSamples(input, RenderChain(effect));
            for (var i = 0; i < output.Length; i++)
                Assert.InRange(Math.Abs(output[i] - (i == 0 ? .5 * dry : i == tap ? .5 * wet : 0)), 0, 1e-7);
        }
        // Mixed taps must retain >1 headroom, exact odd-sample timing and independent labels when chained.
        input[0] = 2; input[4801] = 2;
        var doubleTap = await RenderSamples(input, RenderChain(
            Fx("delay", true, ("delay", 4801d / 48000), ("feedback", 0), ("mix", 25)),
            Fx("delay", true, ("delay", 4801d / 48000), ("feedback", 0), ("mix", 50))));
        var expectedTaps = new Dictionary<int, double> { [0] = .75, [4801] = 1.75, [9602] = 1.25, [14403] = .25 };
        for (var i = 0; i < doubleTap.Length; i++) Assert.InRange(Math.Abs(doubleTap[i] - expectedTaps.GetValueOrDefault(i)), 0, 1e-7);
        input[0] = .5; input[4801] = 0;
        // The renderer bounds each item with -t; a tap after the item end is not a new tail clip.
        var beyondEnd = await RenderSamples(input, RenderChain(Fx("analog_delay", true, ("delay", 5000), ("feedback", 0), ("dry", 75), ("wet", 25))));
        Assert.InRange(Math.Abs(beyondEnd[0] - .375), 0, 1e-7);
        Assert.All(beyondEnd.Skip(1), value => Assert.InRange(Math.Abs(value), 0, 1e-7));
    }

    [Fact]
    public void Build_R72DynamicSubsets_AreExactAndDoNotReuseHostAliases()
    {
        Assert.Equal("", AudioFxChain.Build(new[] { Fx("deesser", true, ("reduction", 0)) }));
        foreach (var effect in R72FaithfulCases().Select(row => (CatalogAudioFxInstance)row[0]).Where(effect => effect.Id != "deesser"))
        {
            var filter = AudioFxChain.Build(new[] { effect });
            Assert.StartsWith("aformat=sample_fmts=flt,aeval=exprs='", filter);
            Assert.EndsWith("':c=same,aformat=sample_fmts=flt", filter);
        }
        var tube = AudioFxChain.Build(new[] { Fx("tube_compressor", true, ("ratio", 1), ("output", 6)) });
        Assert.Contains("2*tanh(0.5*val(ch)*pow(10,ld(0)/20))", tube);
        var single = AudioFxChain.Build(new[] { Fx("single_band_compressor", true, ("ratio", 1), ("output", 6)) });
        Assert.Equal(single + "," + tube, AudioFxChain.Build(new[] { Fx("single_band_compressor", true, ("ratio", 1), ("output", 6)), Fx("tube_compressor", true, ("ratio", 1), ("output", 6)) }));
        Assert.Equal("", AudioFxChain.Build(new[] { Fx("tube_compressor", false, ("ratio", 1)) }));
        Assert.DoesNotContain("acompressor=", tube); Assert.DoesNotContain("alimiter=", tube);
    }

    [Fact]
    public void Build_R72MigratedDynamics_AdmitProvenIdsAndRejectRemainingMappingsEvenWhenDisabled()
    {
        var active = new[] { Fx("dynamics"), Fx("single_band_compressor"), Fx("tube_compressor"), Fx("dynamics_rack"),
            Fx("dynamics", true, ("ratio", 1.0000001)), Fx("tube_compressor", true, ("ratio", 2)),
            Fx("dynamics_rack", true, ("comp_on", 0), ("gate_on", 1), ("gate_hold", 0)),
            Fx("dynamics_rack", true, ("comp_ratio", 1), ("exp_on", 1)), Fx("dynamics_rack", true, ("comp_on", 0), ("lim_on", 1)),
            Fx("distortion", true, ("drive", 0)), Fx("distortion", true, ("mix", .0000001)) };
        foreach (var effect in active)
        {
            Assert.Contains("aeval=exprs=", AudioFxChain.Build(new[] { effect }));
            Assert.Equal("", AudioFxChain.Build(new[] { effect with { Enabled = false } }));
        }
        var effects = new[] { Fx("deesser"), Fx("deesser", true, ("reduction", .0000001)) };
        foreach (var effect in effects)
            foreach (var enabled in new[] { true, false })
                Assert.Equal(AudioFxChain.UnsupportedMapping, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { effect with { Enabled = enabled } })).Message);
        Assert.Contains("asplit=4", AudioFxChain.Build(new[] { Fx("multiband_compressor") }));
        Assert.Equal("", AudioFxChain.Build(new[] { Fx("multiband_compressor", false) }));
        foreach (var id in new[] { "compressor", "noiseGate", "hard_limiter" })
            Assert.Equal(AudioFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, false) })).Message);
    }

    [Theory]
    [InlineData("single_band_compressor", "output", 40.01)]
    [InlineData("dynamics", "attack", .009)]
    [InlineData("tube_compressor", "release", 5000.01)]
    [InlineData("dynamics_rack", "gate_on", .5)]
    [InlineData("dynamics_rack", "comp_ratio", 31)]
    [InlineData("dynamics_rack", "comp_makeup", -1)]
    [InlineData("deesser", "frequency", 12001)]
    [InlineData("distortion", "curve", 1.5)]
    [InlineData("distortion", "mix", -1)]
    [InlineData("distortion", "tone", double.NaN)]
    public void Build_R72InvalidParams_NeverClampInactiveModules(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(AudioFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
        Assert.Equal(AudioFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("single_band_compressor", false, ("ratio", 1), ("knee", 0)) })).Message);
    }

    [Theory]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"deesser\",\"enabled\":true,\"params\":{\"reduction\":8}}]", AudioFxChain.UnsupportedMapping)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"deesser\",\"enabled\":false,\"params\":{\"reduction\":0.0000001}}]", AudioFxChain.UnsupportedMapping)]
    [InlineData("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"distortion\",\"enabled\":false,\"params\":{\"mix\":0,\"curve\":0.5}}]", AudioFxChain.InvalidParam)]
    public void Codec_R72UnfaithfulDynamics_RejectAllContainers(string fields, string code)
        => Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested(fields, code);

    [Fact]
    public void Codec_R72FaithfulDynamicSubsets_RoundTripEveryContainer()
    {
        var effects = R72FaithfulCases().Select(row => (CatalogAudioFxInstance)row[0]).ToArray();
        foreach (var container in new[] { "timeline", "silenceTrimBackup", "nested" })
        {
            var values = new[] { new TimelineItem("r72", "source", 0, 1, Track: 4) { CatalogAudioFxVersion = 1, CatalogAudioFx = effects } };
            var payload = JsonSerializer.Serialize(values, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var body = container == "nested" ? "\"nestedSequences\":{\"n\":{\"timeline\":" + payload + "}}" : "\"" + container + "\":" + payload;
            var project = EditorProjectCodec.Deserialize("{\"schemaVersion\":1," + body + "}");
            var stored = JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var json = JsonDocument.Parse(stored);
            var owner = container == "nested" ? json.RootElement.GetProperty("nestedSequences").GetProperty("n").GetProperty("timeline") : json.RootElement.GetProperty(container);
            var restored = Assert.Single(owner.EnumerateArray());
            Assert.Equal(1, restored.GetProperty("catalogAudioFxVersion").GetInt32());
            Assert.Equal(effects.Select(effect => effect.Id), restored.GetProperty("catalogAudioFx").EnumerateArray().Select(effect => effect.GetProperty("id").GetString()));
            EditorProjectCodec.EnsureSupported(stored);
        }
    }

    private static double[] R72Stepped() => Enumerable.Range(0, 57600)
        .Select(i => (double)(float)Math.Pow(10, (i < 9600 ? -40 : i < 19200 ? -6 : i < 33600 ? -40 : i < 43200 ? -21 : -19) / 20d)).ToArray();
    private static double R72Rms(double[] samples, int first = 0) => Math.Sqrt(samples.Skip(first).Sum(value => value * value) / (samples.Length - first));
    private static double R72MaxError(double[] actual, double[] expected) => actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max();
    private static readonly int[] R72CurveTimes = [200, 201, 205, 210, 250, 399, 400, 401, 405, 410, 450, 500, 699, 899, 1199];
    private static double R72GainDb(double[] input, double[] output, int ms)
        => 20 * Math.Log10(Math.Max(1e-20, Math.Abs(output[ms * 48] / input[ms * 48])));
    private static double R72GainError(double[] input, double[] actual, double[] expected)
        => R72CurveTimes.Max(ms => Math.Abs(R72GainDb(input, actual, ms) - R72GainDb(input, expected, ms)));

    private static async Task<double[]> R72ActualPreview(double[] samples, CatalogAudioFxInstance effect)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "merge", "r72-audio-numerical.mjs"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var node = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_NODE") ?? "node";
        var psi = new ProcessStartInfo(node) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(Path.Combine(directory!.FullName, "tests", "merge", "r72-audio-numerical.mjs")); psi.ArgumentList.Add("--preview");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("actual preview node did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        var input = JsonSerializer.SerializeToUtf8Bytes(new { samples = new[] { samples }, effect = new { effect = effect.Id, @params = effect.Params } });
        async Task Feed() { await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token); process.StandardInput.Close(); }
        var feed = Feed();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("actual preview node timed out"); }
        await feed;
        Assert.True(process.ExitCode == 0, "actual preview node failed: " + await error);
        var channels = JsonSerializer.Deserialize<double[][]>(await output);
        Assert.NotNull(channels);
        var result = Assert.Single(channels!);
        Assert.Equal(samples.Length, result.Length); Assert.All(result, value => Assert.True(double.IsFinite(value)));
        return result;
    }

    public static IEnumerable<object[]> R72ShelfCases()
    {
        foreach (var id in new[] { "bass", "treble" })
            foreach (var boost in new[] { -30d, -12d, 12d, 30d })
                foreach (var scale in new[] { .5, 1, 2 })
                    yield return new object[] { id, boost, (int)((id == "bass" ? 200 : 4000) * scale) };
    }

    [Theory]
    [MemberData(nameof(R72ShelfCases))]
    public async Task RealFfmpeg_R72Shelves_MatchActualPreviewFrequencyResponse(string id, double boost, int frequency)
    {
        var samples = Enumerable.Range(0, 48000).Select(i => (double)(float)(.125 * Math.Sin(2 * Math.PI * frequency * i / 48000))).ToArray();
        var effect = Fx(id, true, ("boost", boost));
        var expected = await R72ActualPreview(samples, effect); var actual = await RenderSamples(samples, RenderChain(effect));
        var error = Math.Abs(R72Rms(actual, 4800) / R72Rms(expected, 4800) - 1);
        Assert.True(error < 2e-5, $"{id} {boost}dB @{frequency} RMS relative error={error:R}");
    }

    [Theory]
    [InlineData("bass", 100, 200)]
    [InlineData("treble", 2000, 4000)]
    public async Task RealFfmpeg_R72OldShelf_RevertNegativeFailsSameTolerance(string id, int frequency, int center)
    {
        var samples = Enumerable.Range(0, 48000).Select(i => (double)(float)(.125 * Math.Sin(2 * Math.PI * frequency * i / 48000))).ToArray();
        var expected = await R72ActualPreview(samples, Fx(id, true, ("boost", 12)));
        var oldOutput = await RenderSamples(samples, $"{id}=g=12:f={center}:p=2:t=q:w=0.707:r=f64");
        var error = Math.Abs(R72Rms(oldOutput, 4800) / R72Rms(expected, 4800) - 1);
        Assert.True(error > 2e-5, $"Reverted {id} unexpectedly passed RMS tolerance: {error:R}");
    }

    public static IEnumerable<object[]> R72FaithfulCases()
    {
        yield return new object[] { Fx("dynamics", true, ("ratio", 1), ("threshold", -60), ("attack", .01), ("release", 3000)) };
        yield return new object[] { Fx("single_band_compressor", true, ("ratio", 1), ("output", 6)) };
        yield return new object[] { Fx("single_band_compressor", true, ("ratio", 1), ("output", -40), ("attack", 300), ("release", 1)) };
        yield return new object[] { Fx("dynamics_rack", true, ("comp_ratio", 1), ("comp_auto", 1), ("comp_makeup", 8), ("exp_on", 1), ("exp_ratio", 1), ("output", -2), ("soft_clip", 1)) };
        yield return new object[] { Fx("dynamics_rack", true, ("comp_on", 0), ("comp_makeup", 30), ("output", -6)) };
        yield return new object[] { Fx("deesser", true, ("reduction", 0), ("threshold", -40), ("frequency", 12000)) };
        yield return new object[] { Fx("distortion", true, ("mix", 0), ("drive", 48), ("symmetry", 100), ("tone", 500), ("output", 12)) };
        yield return new object[] { Fx("tube_compressor", true, ("ratio", 1), ("output", 6)) };
    }

    [Theory]
    [MemberData(nameof(R72FaithfulCases))]
    public async Task RealFfmpeg_R72FaithfulDynamics_MatchActualPreviewRmsAndGainCurve(CatalogAudioFxInstance effect)
    {
        var samples = R72Stepped(); var expected = await R72ActualPreview(samples, effect);
        var actual = await RenderSamples(samples, RenderChain(effect));
        var sampleError = R72MaxError(actual, expected); var rmsError = Math.Abs(R72Rms(actual) / R72Rms(expected) - 1);
        var gainError = R72GainError(samples, actual, expected);
        Assert.True(sampleError < 1e-6 && rmsError < 2e-5 && gainError < 2e-5,
            $"{effect.Id}: sample={sampleError:R}, RMS={rmsError:R}, gain dB={gainError:R}");
    }

    public static IEnumerable<object[]> R72EndpointCases()
    {
        yield return new object[] { Fx("single_band_compressor", true, ("ratio", 1), ("output", 40), ("threshold", -60), ("attack", .01), ("release", 3000)) };
        yield return new object[] { Fx("dynamics_rack", true, ("comp_ratio", 1), ("comp_makeup", 30), ("output", 30), ("comp_auto", 1), ("exp_on", 1), ("exp_ratio", 1), ("soft_clip", 1)) };
        yield return new object[] { Fx("tube_compressor", true, ("ratio", 1), ("output", 30), ("threshold", 0), ("attack", 500), ("release", 5000)) };
        yield return new object[] { Fx("tube_compressor", true, ("ratio", 1), ("output", -30), ("threshold", -60), ("attack", .1), ("release", 1)) };
    }

    [Theory]
    [MemberData(nameof(R72EndpointCases))]
    public async Task RealFfmpeg_R72DynamicGainEndpoints_PreserveUnclippedHeadroom(CatalogAudioFxInstance effect)
    {
        var samples = R72Stepped(); var expected = await R72ActualPreview(samples, effect);
        var actual = await RenderSamples(samples, RenderChain(effect));
        // At +60dB the preview rounds f32 near 500; compare normalized sample error without relaxing the standard cases.
        var normalized = R72MaxError(actual, expected) / Math.Max(1, expected.Max(value => Math.Abs(value)));
        var rmsError = Math.Abs(R72Rms(actual) / R72Rms(expected) - 1);
        Assert.True(normalized < 1e-6 && rmsError < 2e-5, $"{effect.Id}: normalized sample={normalized:R}, RMS={rmsError:R}");
    }

    [Theory]
    [InlineData("single_band_compressor", 10, 100, 1)]
    [InlineData("dynamics", .01, 1, 1.9952623149688795)]
    [InlineData("dynamics", .01, 1, 6)]
    public async Task RealFfmpeg_R72Acompressor_AttackReleaseAndKneeDoNotMatchPreview(string id, double attack, double release, double knee)
    {
        var input = R72Stepped(); var effect = Fx(id, true, ("threshold", -20), ("ratio", 4), ("attack", attack), ("release", release));
        var expected = await R72ActualPreview(input, effect);
        var filter = FormattableString.Invariant($"acompressor=threshold=0.1:ratio=4:attack={attack}:release={release}:makeup=1:knee={knee}:detection=peak:link=maximum");
        var actual = await RenderSamples(input, filter);
        var rmsError = Math.Abs(R72Rms(actual) / R72Rms(expected) - 1);
        Assert.True(R72GainError(input, actual, expected) > 2e-5);
        // Converted knee passes whole-signal RMS but fails transients: RMS alone must not admit it.
        if (id == "dynamics" && knee != 6) Assert.True(rmsError < 2e-5);
        else Assert.True(rmsError > 2e-5);
        if (id == "single_band_compressor") Assert.True(R72GainError(input, actual, expected) > 6);
        else
        {
            var settled = new[] { 899, 1199 }.Max(ms => Math.Abs(R72GainDb(input, actual, ms) - R72GainDb(input, expected, ms)));
            if (knee == 6) Assert.True(settled > .8, $"raw knee unexpectedly matched: {settled:R}");
            else Assert.True(settled < 2e-5, $"converted knee settled curve mismatch: {settled:R}");
        }
        // R77A6: the stateful DSP now admits this active state; the shipped filter must match the preview where acompressor/agate does not.
        var exact = await RenderSamples(input, RenderChain(effect));
        Assert.True(R72MaxError(exact, expected) / Math.Max(1, expected.Max(value => Math.Abs(value))) < 1e-6 && Math.Abs(R72Rms(exact) / R72Rms(expected) - 1) < 2e-5, "shipped stateful chain must match preview");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public async Task RealFfmpeg_R72Agate_AttackReleaseAndHoldDoNotMatchPreview(double hold)
    {
        var input = R72Stepped(); var effect = Fx("dynamics_rack", true, ("gate_on", 1), ("comp_on", 0), ("gate_threshold", -30), ("gate_attack", 2), ("gate_release", 100), ("gate_hold", hold));
        var expected = await R72ActualPreview(input, effect);
        var actual = await RenderSamples(input, "agate=threshold=0.03162277660168379:ratio=9000:attack=2:release=100:range=0.0001:knee=1:detection=peak:link=maximum");
        Assert.True(Math.Abs(R72Rms(actual) / R72Rms(expected) - 1) > 2e-5);
        Assert.True(R72GainError(input, actual, expected) > 40);
        // R77A6: the stateful DSP now admits this active state; the shipped filter must match the preview where acompressor/agate does not.
        var exact = await RenderSamples(input, RenderChain(effect));
        Assert.True(R72MaxError(exact, expected) / Math.Max(1, expected.Max(value => Math.Abs(value))) < 1e-6 && Math.Abs(R72Rms(exact) / R72Rms(expected) - 1) < 2e-5, "shipped stateful chain must match preview");
    }

    [Fact]
    public async Task RealFfmpeg_R72Alimiter_EnvelopeAndLatencyDoNotMatchPreview()
    {
        const string filter = "alimiter=level_in=1:level_out=1:limit=0.5011872336272722:attack=5:release=60:asc=0:level=0:latency=0";
        var input = R72Stepped(); var effect = Fx("hard_limiter", true, ("max", -6), ("boost", 0), ("release", 60));
        var expected = await R72ActualPreview(input, effect); var actual = await RenderSamples(input, filter);
        Assert.True(Math.Abs(R72Rms(actual) / R72Rms(expected) - 1) > 2e-5); Assert.True(R72GainError(input, actual, expected) > 2e-5);
        var impulse = new double[2048]; impulse[0] = (double)(float).1;
        var previewImpulse = await R72ActualPreview(impulse, effect); var ffmpegImpulse = await RenderSamples(impulse, filter);
        var previewFirst = Array.FindIndex(previewImpulse, value => Math.Abs(value) > 1e-8);
        Assert.Equal(248, previewFirst); Assert.NotEqual(previewFirst, Array.FindIndex(ffmpegImpulse, value => Math.Abs(value) > 1e-8));
        Assert.Equal(AudioFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { effect })).Message);
    }

    [Fact]
    public async Task RealFfmpeg_R72Multiband_RatioOneIsNotDryBecauseOfCrossovers()
    {
        var impulse = new double[2048]; impulse[0] = (double)(float).1;
        var effect = Fx("multiband_compressor", true, ("b1_ratio", 1), ("b2_ratio", 1), ("b3_ratio", 1), ("b4_ratio", 1));
        var expected = await R72ActualPreview(impulse, effect); var actual = await RenderSamples(impulse, "anull");
        Assert.True(R72MaxError(actual, expected) > .01);
        // R79 migration: multiband_compressor is now exported (A8); the crossover-aware chain must match the preview, not dry anull.
        var exact = await RenderSamples(impulse, RenderChain(effect));
        Assert.True(R72MaxError(exact, expected) < 1e-6);
    }

    [Fact]
    public async Task RealFfmpeg_Amplify6DbAndLowpass_ChangeSineRms()
    {
        var item = new TimelineItem("a1", "source", 0, 1, Track: 4);
        var gain = item with { CatalogAudioFxVersion = 1, CatalogAudioFx = new[] { Fx("amplify", true, ("gain", 6)) } };
        var filtered = gain with { CatalogAudioFx = new[] { Fx("amplify", true, ("gain", 6)), Fx("lowpass", true, ("cutoff", 1000)) } };
        var baseline = await SineRms(100, EditorRenderService.AudioFilterChain(item));
        var amplified = await SineRms(100, EditorRenderService.AudioFilterChain(gain));
        Assert.InRange(amplified / baseline, Math.Pow(10, 6d / 20) - .002, Math.Pow(10, 6d / 20) + .002);
        var passband = await SineRms(100, EditorRenderService.AudioFilterChain(filtered));
        Assert.InRange(passband / baseline, 1.98, 2.01);
        var highBaseline = await SineRms(8000, EditorRenderService.AudioFilterChain(item));
        var highFiltered = await SineRms(8000, EditorRenderService.AudioFilterChain(filtered));
        Assert.InRange(highFiltered / highBaseline, .001, .08);
        // Execute the remaining filters too: a builder-only string assertion cannot detect invalid FFmpeg options.
        var all = item with { CatalogAudioFxVersion = 1, CatalogAudioFx = new[] { Fx("amplify", true, ("gain", 6)), Fx("highpass"), Fx("lowpass"), Fx("bass", true, ("boost", 3)), Fx("treble", true, ("boost", -4)) } };
        Assert.InRange(await SineRms(1000, EditorRenderService.AudioFilterChain(all)), .05, .3);
    }

    private static IEnumerable<CatalogAudioFxInstance> R73A4Effects()
    {
        foreach (var id in new[] { "fill_left", "fill_right", "invert_a", "swap_channels" }) yield return Fx(id);
        foreach (var width in new[] { 0d, 37.5, 100, 200 }) yield return Fx("stereo_width", true, ("width", width));
        yield return Fx("stereo_expander", true, ("expand", 0));
        yield return Fx("stereo_expander", true, ("expand", 300), ("center_pan", -100));
        yield return Fx("stereo_expander", true, ("expand", 200), ("center_pan", 100));
        yield return Fx("stereo_expander", true, ("expand", 137.25), ("center_pan", -24.5));
        yield return Fx("panner_ambisonics");
        yield return Fx("panner_ambisonics", true, ("pan", 180), ("tilt", 90), ("roll", 180));
        yield return Fx("panner_ambisonics", true, ("pan", -180), ("tilt", -90), ("roll", -180));
        yield return Fx("panner_ambisonics", true, ("pan", -45), ("tilt", 30), ("roll", -12));
        yield return Fx("panner_ambisonics", true, ("pan", 12.25), ("tilt", -19.5), ("roll", 23.75));
        yield return Fx("panner_ambisonics", true, ("pan", 180));
        yield return Fx("channel_mixer_a");
        yield return Fx("channel_mixer_a", true, ("l_from_l", 0), ("l_from_r", 100), ("r_from_l", 100), ("r_from_r", 0), ("invert_r", 1));
        yield return Fx("channel_mixer_a", true, ("l_from_l", 100), ("l_from_r", -100), ("r_from_l", -100), ("r_from_r", 100), ("invert_l", 1));
        yield return Fx("channel_mixer_a", true, ("l_from_l", -37.5), ("l_from_r", 82.5), ("r_from_l", 12.25), ("r_from_r", -56.75), ("invert_l", 1), ("invert_r", 1));
        yield return Fx("channel_volume_a");
        yield return Fx("channel_volume_a", true, ("left", -96), ("right", 24), ("center", -96), ("lfe", 24), ("left_surround", -96), ("right_surround", 24));
        yield return Fx("channel_volume_a", true, ("left", 24), ("right", -96), ("center", 6), ("lfe", -6), ("left_surround", 12.5), ("right_surround", -95.9));
        foreach (var target in new[] { -36d, -23, -5 }) yield return Fx("loudness_radar", true, ("target", target));
    }

    public static IEnumerable<object[]> R73A4Cases()
    {
        foreach (var effect in R73A4Effects())
            foreach (var channels in new[] { 1, 2, 6, 8 }) yield return new object[] { effect, channels };
    }

    private static double[][] R73A4Signal(int channels, int frames = 9600, double scale = 1)
        => Enumerable.Range(0, channels).Select(c => Enumerable.Range(0, frames).Select(i => (double)(float)(scale *
            ((i < frames * .7 ? .12 * Math.Sin(2 * Math.PI * (173 + c * 317) * i / 48000) + .04 * Math.Cos(2 * Math.PI * (2201 + c * 97) * i / 48000) : 0)
                + (i % 997 == 11 + c ? .2 : 0)))).ToArray()).ToArray();

    private static async Task<double[][]> R73A4ActualPreview(double[][] samples, CatalogAudioFxInstance effect)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var relative = Path.Combine("docs", "merge", "_work", "r73", "R73-FXA4", "numerical.mjs");
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative))) directory = directory.Parent;
        Assert.NotNull(directory);
        var node = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_NODE") ?? "node";
        var psi = new ProcessStartInfo(node) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(Path.Combine(directory!.FullName, relative)); psi.ArgumentList.Add("--preview");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("R73A4 actual preview did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        var input = JsonSerializer.SerializeToUtf8Bytes(new { samples, effect = new { effect = effect.Id, @params = effect.Params } });
        async Task Feed() { await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token); process.StandardInput.Close(); }
        var feed = Feed();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("R73A4 actual preview timed out"); }
        await feed;
        Assert.True(process.ExitCode == 0, "R73A4 actual preview failed: " + await error);
        var result = JsonSerializer.Deserialize<double[][]>(await output);
        Assert.NotNull(result); Assert.Equal(samples.Length, result!.Length);
        foreach (var channel in result) { Assert.Equal(samples[0].Length, channel.Length); Assert.All(channel, value => Assert.True(double.IsFinite(value))); }
        return result;
    }

    private static async Task<double[][]> R73A4Render(double[][] samples, string filter)
    {
        var channels = samples.Length; var frames = samples[0].Length;
        var input = new byte[frames * channels * sizeof(double)];
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < channels; c++) BinaryPrimitives.WriteDoubleLittleEndian(input.AsSpan((i * channels + c) * sizeof(double), sizeof(double)), samples[c][i]);
        var psi = new ProcessStartInfo(Ffmpeg()) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-nostdin", "-f", "f64le", "-ar", "48000", "-ac", channels.ToString(CultureInfo.InvariantCulture), "-i", "-", "-af", string.IsNullOrEmpty(filter) ? "anull" : filter,
            "-t", (frames / 48000d).ToString("R", CultureInfo.InvariantCulture), "-c:a", "pcm_f64le", "-f", "f64le", "-" }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("R73A4 ffmpeg did not start");
        using var output = new MemoryStream(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var error = process.StandardError.ReadToEndAsync(); var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        async Task Feed() { await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token); process.StandardInput.Close(); }
        var feed = Feed();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("R73A4 ffmpeg timed out"); }
        await Task.WhenAll(copy, feed); Assert.True(process.ExitCode == 0, "R73A4 ffmpeg failed: " + await error);
        var pcm = output.ToArray(); Assert.Equal(input.Length, pcm.Length);
        var result = Enumerable.Range(0, channels).Select(_ => new double[frames]).ToArray();
        for (var i = 0; i < frames; i++) for (var c = 0; c < channels; c++)
        {
            result[c][i] = BinaryPrimitives.ReadDoubleLittleEndian(pcm.AsSpan((i * channels + c) * sizeof(double), sizeof(double)));
            Assert.True(double.IsFinite(result[c][i]), "non-finite sample");
        }
        return result;
    }

    private static void R73A4AssertPcm(double[][] expected, double[][] actual, bool headroom = false)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var c = 0; c < expected.Length; c++)
        {
            Assert.Equal(expected[c].Length, actual[c].Length);
            var error = R72MaxError(actual[c], expected[c]);
            if (headroom) error /= Math.Max(1, expected[c].Max(value => Math.Abs(value)));
            var referenceRms = R72Rms(expected[c]); var actualRms = R72Rms(actual[c]);
            var rmsError = referenceRms == 0 ? actualRms == 0 ? 0 : double.PositiveInfinity : Math.Abs(actualRms / referenceRms - 1);
            Assert.True(error < 1e-6 && rmsError < 2e-5, $"channel {c}: sample={error:R}, RMS={rmsError:R}");
        }
    }

    [Theory]
    [MemberData(nameof(R73A4Cases))]
    public async Task R73A4_RealFfmpeg_MatchesActualPreviewMonoStereoAndSurround(CatalogAudioFxInstance effect, int channels)
    {
        var input = R73A4Signal(channels);
        var expected = await R73A4ActualPreview(input, effect);
        var actual = await R73A4Render(input, RenderChain(effect));
        R73A4AssertPcm(expected, actual);
        if (effect.Id != "channel_volume_a" && effect.Id != "invert_a")
            for (var c = 2; c < channels; c++) Assert.Equal(input[c], actual[c]);
        if (effect.Id == "channel_volume_a") for (var c = 6; c < channels; c++) Assert.Equal(input[c], actual[c]);
    }

    [Fact]
    public async Task R73A4_RealFfmpeg_PreservesHeadroomAndNoncommutingOrder()
    {
        var input = R73A4Signal(8, scale: 10);
        var gain = Fx("channel_volume_a", true, ("left", 24), ("right", 24), ("center", 24), ("lfe", 24), ("left_surround", 24), ("right_surround", 24));
        var expected = await R73A4ActualPreview(input, gain); var actual = await R73A4Render(input, RenderChain(gain));
        R73A4AssertPcm(expected, actual, headroom: true); Assert.True(actual[0].Max(value => Math.Abs(value)) > 1);
        input = R73A4Signal(2);
        var chain = new[] { Fx("stereo_expander", true, ("expand", 137.25), ("center_pan", -24.5)),
            Fx("panner_ambisonics", true, ("pan", -45), ("tilt", 30), ("roll", -12)), Fx("fill_right"), Fx("invert_a") };
        expected = input;
        foreach (var effect in chain) expected = await R73A4ActualPreview(expected, effect);
        actual = await R73A4Render(input, RenderChain(chain)); R73A4AssertPcm(expected, actual);
        var reversed = await R73A4Render(input, RenderChain(chain.Reverse().ToArray()));
        Assert.True(R72MaxError(expected[0], reversed[0]) > .01);
    }

    [Fact]
    public void R73A4_Build_RoutingDefaultsOrderDisabledAndCultureAreStrict()
    {
        var effects = R73A4Effects().ToArray();
        Assert.Equal(string.Join(',', effects.Select(effect => AudioFxChain.Build(new[] { effect })).Where(value => value.Length > 0)), AudioFxChain.Build(effects));
        Assert.Equal("", AudioFxChain.Build(effects.Select(effect => effect with { Enabled = false }).ToArray()));
        Assert.Equal("", AudioFxChain.Build(new[] { Fx("loudness_radar", true, ("target", -5)) }));
        Assert.Equal("aeval=exprs='-val(ch)':c=same", AudioFxChain.Build(new[] { Fx("invert_a") }));
        Assert.Contains("if(eq(ch,0),val(1),if(eq(ch,1),val(1),val(ch)))", AudioFxChain.Build(new[] { Fx("fill_left") }));
        Assert.Contains("if(eq(ch,0),val(0),if(eq(ch,1),val(0),val(ch)))", AudioFxChain.Build(new[] { Fx("fill_right") }));
        var before = CultureInfo.CurrentCulture;
        try
        {
            var expected = AudioFxChain.Build(effects); CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal(expected, AudioFxChain.Build(effects));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("stereo_width", "width", -0.01)]
    [InlineData("stereo_width", "width", 200.01)]
    [InlineData("stereo_width", "width", double.NaN)]
    [InlineData("stereo_expander", "expand", 300.01)]
    [InlineData("stereo_expander", "expand", -0.01)]
    [InlineData("stereo_expander", "center_pan", -100.01)]
    [InlineData("stereo_expander", "center_pan", 100.01)]
    [InlineData("panner_ambisonics", "pan", -180.01)]
    [InlineData("panner_ambisonics", "pan", 180.01)]
    [InlineData("panner_ambisonics", "tilt", -90.01)]
    [InlineData("panner_ambisonics", "tilt", 90.01)]
    [InlineData("panner_ambisonics", "roll", -180.01)]
    [InlineData("panner_ambisonics", "roll", 180.01)]
    [InlineData("channel_mixer_a", "invert_l", 0.5)]
    [InlineData("channel_mixer_a", "invert_r", 2)]
    [InlineData("channel_mixer_a", "l_from_l", -100.01)]
    [InlineData("channel_mixer_a", "r_from_r", 100.01)]
    [InlineData("channel_volume_a", "left", -96.01)]
    [InlineData("channel_volume_a", "right_surround", 24.01)]
    [InlineData("channel_volume_a", "center", double.PositiveInfinity)]
    [InlineData("loudness_radar", "target", -36.01)]
    [InlineData("loudness_radar", "target", -4.99)]
    [InlineData("loudness_radar", "target", double.NegativeInfinity)]
    public void R73A4_Build_RejectsInvalidParamsEvenDisabled(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(AudioFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Fact]
    public void R73A4_Codec_ValidatesAllContainersAndRoundTripsWithHostEffects()
    {
        var audio = JsonSerializer.Serialize(R73A4Effects().ToArray());
        var item = "{\"id\":\"a\",\"clipId\":\"s\",\"start\":0,\"end\":1,\"track\":4,\"catalogAudioFxVersion\":1,\"catalogAudioFx\":" + audio
            + ",\"effects\":[{\"id\":\"host\",\"type\":\"compressor\",\"enabled\":true,\"params\":{\"ratio\":2}}]}";
        foreach (var container in new[] { "\"timeline\":[" + item + "]", "\"silenceTrimBackup\":[" + item + "]", "\"nestedSequences\":{\"n\":{\"timeline\":[" + item + "]}}" })
            EditorProjectCodec.EnsureSupported("{\"schemaVersion\":1," + container + "}");
        var project = EditorProjectCodec.Deserialize("{\"schemaVersion\":1,\"timeline\":[" + item + "]}");
        var restored = EditorProjectCodec.Deserialize(JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(audio, JsonSerializer.Serialize(Assert.Single(restored.Timeline!).CatalogAudioFx));
        Assert.Equal("compressor", Assert.Single(Assert.Single(restored.Timeline!).Effects!).Type);
        foreach (var (fxJson, code) in new[] {
            ("{\"id\":\"channel_mixer_a\",\"enabled\":true,\"params\":{\"invert_l\":true}}", AudioFxChain.InvalidParam),
            ("{\"id\":\"channel_mixer_a\",\"enabled\":false,\"params\":{\"invert_l\":0.5}}", AudioFxChain.InvalidParam),
            ("{\"id\":\"stereo_width\",\"enabled\":true,\"params\":{\"width\":\"100\"}}", AudioFxChain.InvalidParam),
            ("{\"id\":\"panner_ambisonics\",\"enabled\":true,\"params\":{\"pan\":0,\"pan\":1}}", AudioFxChain.InvalidParam),
            ("{\"id\":\"fill_left\",\"enabled\":false,\"params\":{\"gain\":0}}", AudioFxChain.UnsupportedParam),
            ("{\"id\":\"loudness_radar\",\"enabled\":true,\"params\":{\"target\":-37}}", AudioFxChain.InvalidParam),
            ("{\"id\":\"mastering\",\"enabled\":false,\"params\":{\"reverb\":1}}", AudioFxChain.UnsupportedMapping) })
            Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[" + fxJson + "]", code);
    }

    [Theory]
    [InlineData("studio_reverb")]
    [InlineData("convolution_reverb")]
    [InlineData("surround_reverb")]
    [InlineData("denoise")]
    [InlineData("binauralizer")]
    [InlineData("loudnorm")]
    public void R73A4_Build_NeverSubstitutesApproximateMasteringOrRestoration(string id)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(AudioFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, enabled) })).Message);
    }

    [Fact]
    public async Task R73A4_RealFfmpeg_RejectsReverbDenoiseAndLoudnessAliasesNumerically()
    {
        var input = R73A4Signal(2, 48000);
        var candidates = new[] {
            (Fx("studio_reverb", true, ("room", 50), ("decay", 60), ("damping", 40), ("dry", 75), ("wet", 25)), "aecho=1:1:20|31|43|59:0.15|0.12|0.08|0.05"),
            (Fx("convolution_reverb", true, ("mix", 25)), "aecho=1:1:20|31|43|59:0.15|0.12|0.08|0.05"),
            (Fx("surround_reverb", true, ("dry", 80), ("wet", 30)), "aecho=1:1:20|31|43|59:0.15|0.12|0.08|0.05"),
            (Fx("denoise", true, ("amount", 50)), "afftdn=nr=20:nf=-25"),
            (Fx("denoise", true, ("amount", 0)), "anull"),
            (Fx("mastering", true, ("loudness", 70)), "volume=2.630267991895382:precision=double"),
            (Fx("mastering", true, ("loudness", 70)), "loudnorm=I=-23:TP=-1:LRA=11,aresample=48000") };
        foreach (var (effect, filter) in candidates)
        {
            var expected = await R73A4ActualPreview(input, effect); var actual = await R73A4Render(input, filter);
            Assert.True(actual.Select((channel, c) => R72MaxError(channel, expected[c])).Max() > 1e-6);
            if (effect.Id == "mastering") R78A7AssertPcm(expected, await R73A4Render(input, RenderChain(effect)));
            else Assert.Equal(AudioFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { effect })).Message);
        }
    }

    private static IEnumerable<CatalogAudioFxInstance> R74A5Effects()
    {
        foreach (var mode in new[] { 0, 1 }) foreach (var order in new[] { 1, 2, 3, 4, 5, 6 }) foreach (var cutoff in new[] { 20d, 1000, 20000 })
            yield return Fx("scientific_filter", true, ("type", 1), ("mode", mode), ("order", order), ("cutoff", cutoff), ("gain", cutoff == 1000 ? 6 : 0));
        yield return Fx("vocal_enhancer", true, ("mode", 2));
        foreach (var frequency in new[] { 0, 1 }) foreach (var harmonics in new[] { 1, 5, 10 }) foreach (var q in new[] { 1d, 30, 100 })
            yield return Fx("dehummer", true, ("freq", frequency), ("harmonics", harmonics), ("q", q), ("gain", q == 30 ? -40 : -96));
        yield return Fx("dehummer");
        yield return Fx("dehummer", true, ("freq", 0), ("gain", -6.25), ("harmonics", 4), ("q", 17.5));
        yield return Fx("multitap_delay");
        yield return Fx("multitap_delay", true, ("delay1", 1), ("delay2", 3.125), ("delay3", 8.5), ("delay4", 20), ("level1", 0), ("level2", -3.25), ("level3", -96), ("level4", -18.75), ("mix", 100));
        yield return Fx("multitap_delay", true, ("delay1", 1), ("delay2", 3.125), ("delay3", 8.5), ("delay4", 20), ("level1", 0), ("level2", 0), ("level3", 0), ("level4", 0), ("mix", 37.5));
        yield return Fx("multitap_delay", true, ("delay1", 4000), ("delay2", 4000), ("delay3", 4000), ("delay4", 4000), ("mix", 100));
        yield return Fx("multitap_delay", true, ("feedback1", 95), ("feedback4", 37), ("delay1", 1.001), ("mix", 0));
    }

    public static IEnumerable<object[]> R74A5Cases()
    {
        foreach (var effect in R74A5Effects()) foreach (var channels in new[] { 1, 2, 6, 8 }) yield return new object[] { effect, channels };
    }

    [Theory]
    [MemberData(nameof(R74A5Cases))]
    public async Task R74A5_RealFfmpeg_MatchesActualPreviewWithoutSkipping(CatalogAudioFxInstance effect, int channels)
    {
        var input = R73A4Signal(channels, effect.Id == "multitap_delay" ? 60000 : 9600);
        var expected = await R73A4ActualPreview(input, effect);
        var actual = await R73A4Render(input, RenderChain(effect));
        R73A4AssertPcm(expected, actual);
    }

    [Fact]
    public async Task R74A5_RealFfmpeg_ScientificGainExtremesMatchActualPreview()
    {
        var input = R73A4Signal(2);
        foreach (var mode in new[] { 0, 1 }) foreach (var order in new[] { 1, 3, 6 }) foreach (var gain in new[] { -30d, 30 })
        {
            var effect = Fx("scientific_filter", true, ("mode", mode), ("order", order), ("cutoff", 1375.25), ("gain", gain));
            R73A4AssertPcm(await R73A4ActualPreview(input, effect), await R73A4Render(input, RenderChain(effect)));
        }
    }

    [Fact]
    public async Task R74A5_RealFfmpeg_RepeatedGraphsPreserveWholeCascadeMixAndAllChannels()
    {
        var input = R73A4Signal(8, 12000, 2);
        var chain = new[] { Fx("dehummer", true, ("freq", 0), ("gain", -40), ("harmonics", 10), ("q", 30)),
            Fx("multitap_delay", true, ("delay1", 1), ("delay2", 3.125), ("delay3", 8.5), ("delay4", 20), ("mix", 37.5)),
            Fx("vocal_enhancer", true, ("mode", 2)), Fx("scientific_filter", true, ("mode", 1), ("order", 3), ("cutoff", 311.5)),
            Fx("dehummer", true, ("gain", -6.25), ("harmonics", 4), ("q", 17.5)),
            Fx("multitap_delay", true, ("delay1", 2), ("delay2", 4), ("delay3", 6), ("delay4", 8)) };
        var expected = input;
        foreach (var effect in chain) expected = await R73A4ActualPreview(expected, effect);
        var filter = RenderChain(chain);
        foreach (var tag in new[] { "catalogAudioHum0", "catalogAudioMulti1", "catalogAudioHum2", "catalogAudioMulti3" }) Assert.Contains(tag, filter);
        R73A4AssertPcm(expected, await R73A4Render(input, filter));
    }

    [Fact]
    public async Task R74A5_RealFfmpeg_LongestTapKeepsHeadroomAndDoesNotExtendItem()
    {
        var effect = Fx("multitap_delay", true, ("delay1", 4000), ("delay2", 4000), ("delay3", 4000), ("delay4", 4000),
            ("level1", 0), ("level2", 0), ("level3", 0), ("level4", 0), ("mix", 100));
        var input = new[] { new double[196800] }; input[0][0] = 2;
        var actual = await R73A4Render(input, RenderChain(effect));
        R73A4AssertPcm(await R73A4ActualPreview(input, effect), actual);
        Assert.Equal(8d, actual[0][192000]); Assert.Equal(1, actual[0].Count(value => value != 0));
        var shortInput = new[] { input[0].Take(9600).ToArray() };
        var clipped = await R73A4Render(shortInput, RenderChain(effect));
        R73A4AssertPcm(await R73A4ActualPreview(shortInput, effect), clipped);
        Assert.All(clipped[0], value => Assert.Equal(0d, value));
    }

    [Fact]
    public void R74A5_MigratedA2DehummerAdmissionKeepsInvalidParameterRejection()
    {
        Assert.Equal("", AudioFxChain.Build(new[] { Fx("dehummer") }));
        Assert.Contains("bandreject=f=60:", AudioFxChain.Build(new[] { Fx("dehummer", true, ("gain", -40)) }));
        foreach (var enabled in new[] { true, false })
        {
            Assert.Equal(AudioFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("dehummer", enabled, ("q", 0)) })).Message);
            Assert.Equal(AudioFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("dehummer", enabled, ("amount", 100)) })).Message);
        }
    }

    [Fact]
    public void R74A5_Build_DefaultsDisabledAndCultureMatchProjectMapping()
    {
        Assert.Equal("", AudioFxChain.Build(new[] { Fx("dehummer"), Fx("multitap_delay", true, ("mix", 0), ("feedback1", 95), ("delay1", 1.001)) }));
        var vocal = AudioFxChain.Build(new[] { Fx("vocal_enhancer", true, ("mode", 2)) });
        Assert.Equal("highpass=f=40:p=2:t=q:w=0.707:r=f64,equalizer=f=1500:t=q:w=0.7:g=-4:r=f64,equalizer=f=3500:t=q:w=1:g=-2:r=f64", vocal);
        var chain = new[] { Fx("dehummer", true, ("gain", -6.25), ("q", 17.5)), Fx("scientific_filter", true, ("order", 3), ("cutoff", 311.5)), Fx("multitap_delay", true, ("delay1", 3.125)) };
        var baseline = AudioFxChain.Build(chain);
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(baseline, AudioFxChain.Build(chain)); }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Equal("", AudioFxChain.Build(R74A5Effects().Select(effect => effect with { Enabled = false }).ToArray()));
        Assert.Contains("adelay=150S", baseline); Assert.DoesNotContain("aecho=", baseline); Assert.DoesNotContain("acompressor=", vocal);
    }

    [Fact]
    public void R74A5_Build_RejectsUnprovenSubsetsEvenDisabled()
    {
        var effects = new[] { Fx("scientific_filter", true, ("type", 0)), Fx("scientific_filter", true, ("type", 2)), Fx("scientific_filter", true, ("type", 3)),
            Fx("scientific_filter", true, ("mode", 2)), Fx("scientific_filter", true, ("mode", 3)), Fx("scientific_filter", true, ("order", 7)),
            Fx("scientific_filter", true, ("order", 11), ("cutoff", 20)), Fx("scientific_filter", true, ("order", 12)), Fx("vocal_enhancer"), Fx("vocal_enhancer", true, ("mode", 1)),
            Fx("multitap_delay", true, ("feedback1", .001)), Fx("multitap_delay", true, ("feedback4", 95)), Fx("multitap_delay", true, ("delay1", 1.001)) };
        foreach (var effect in effects) foreach (var enabled in new[] { true, false })
            Assert.Equal(AudioFxChain.UnsupportedMapping, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { effect with { Enabled = enabled } })).Message);
        foreach (var id in new[] { "speech_enhance", "mastering" })
        {
            Assert.Contains("aeval=exprs=", AudioFxChain.Build(new[] { Fx(id) }));
            Assert.Equal("", AudioFxChain.Build(new[] { Fx(id, false) }));
        }
        Assert.Contains("asplit=4", AudioFxChain.Build(new[] { Fx("multiband_compressor") }));
        Assert.Equal("", AudioFxChain.Build(new[] { Fx("multiband_compressor", false) }));
        foreach (var id in new[] { "fft_filter", "hard_limiter", "binauralizer", "guitar_suite", "pitch_shifter", "studio_reverb", "convolution_reverb", "surround_reverb", "declicker", "denoise", "dereverb" })
            foreach (var enabled in new[] { true, false })
                Assert.Equal(AudioFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, enabled) })).Message);
    }

    [Theory]
    [InlineData("scientific_filter", "type", .5)]
    [InlineData("scientific_filter", "mode", 4)]
    [InlineData("scientific_filter", "order", 6.5)]
    [InlineData("scientific_filter", "order", 0)]
    [InlineData("scientific_filter", "order", 13)]
    [InlineData("scientific_filter", "cutoff", 19.99)]
    [InlineData("scientific_filter", "high_cutoff", 20000.01)]
    [InlineData("scientific_filter", "ripple", .009)]
    [InlineData("scientific_filter", "stop_atten", 120.01)]
    [InlineData("scientific_filter", "gain", double.NaN)]
    [InlineData("dehummer", "freq", .5)]
    [InlineData("dehummer", "gain", -96.01)]
    [InlineData("dehummer", "gain", .01)]
    [InlineData("dehummer", "harmonics", 1.5)]
    [InlineData("dehummer", "harmonics", 11)]
    [InlineData("dehummer", "q", .99)]
    [InlineData("dehummer", "q", double.PositiveInfinity)]
    [InlineData("vocal_enhancer", "mode", 2.5)]
    [InlineData("multitap_delay", "delay1", .99)]
    [InlineData("multitap_delay", "delay4", 4000.01)]
    [InlineData("multitap_delay", "feedback3", 95.01)]
    [InlineData("multitap_delay", "level2", -96.01)]
    [InlineData("multitap_delay", "level4", .01)]
    [InlineData("multitap_delay", "mix", double.NegativeInfinity)]
    public void R74A5_Build_InvalidParamsAreNotClampedWhenDisabled(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(AudioFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
        Assert.Equal(AudioFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, false, ("unknown", 0)) })).Message);
    }

    [Fact]
    public void R74A5_Codec_RoundTripsEveryContainerWithoutReplacingHostEffects()
    {
        var audio = JsonSerializer.Serialize(R74A5Effects().ToArray());
        var item = "{\"id\":\"a\",\"clipId\":\"s\",\"start\":0,\"end\":1,\"track\":4,\"catalogAudioFxVersion\":1,\"catalogAudioFx\":" + audio
            + ",\"effects\":[{\"id\":\"host\",\"type\":\"compressor\",\"enabled\":true,\"params\":{\"ratio\":2}}]}";
        foreach (var container in new[] { "\"timeline\":[" + item + "]", "\"silenceTrimBackup\":[" + item + "]", "\"nestedSequences\":{\"n\":{\"timeline\":[" + item + "]}}" })
        {
            var project = EditorProjectCodec.Deserialize("{\"schemaVersion\":1," + container + "}");
            var json = JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            EditorProjectCodec.EnsureSupported(json); Assert.Contains("\"catalogAudioFxVersion\":1", json);
        }
        var restored = EditorProjectCodec.Deserialize("{\"schemaVersion\":1,\"timeline\":[" + item + "]}");
        Assert.Equal(audio, JsonSerializer.Serialize(Assert.Single(restored.Timeline!).CatalogAudioFx));
        Assert.Equal("compressor", Assert.Single(Assert.Single(restored.Timeline!).Effects!).Type);
        foreach (var (parameters, code) in new[] {
            ("{\"id\":\"vocal_enhancer\",\"enabled\":true,\"params\":{\"mode\":\"Music\"}}", AudioFxChain.InvalidParam),
            ("{\"id\":\"dehummer\",\"enabled\":false,\"params\":{\"gain\":true}}", AudioFxChain.InvalidParam),
            ("{\"id\":\"scientific_filter\",\"enabled\":true,\"params\":{\"order\":11}}", AudioFxChain.UnsupportedMapping),
            ("{\"id\":\"multitap_delay\",\"enabled\":false,\"params\":{\"feedback1\":1}}", AudioFxChain.UnsupportedMapping),
            ("{\"id\":\"dehummer\",\"enabled\":true,\"params\":{\"q\":1,\"q\":2}}", AudioFxChain.InvalidParam),
            ("{\"id\":\"scientific_filter\",\"enabled\":false,\"params\":{\"unsupported\":0}}", AudioFxChain.UnsupportedParam) })
            Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[" + parameters + "]", code);
    }

    private static async Task<JsonElement> R75AKUiProbe(object request)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var relative = Path.Combine("docs", "merge", "_work", "r75", "R75-AUDIOKEYS", "probe.mjs");
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative))) directory = directory.Parent;
        Assert.NotNull(directory);
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_NODE") ?? "node")
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        psi.ArgumentList.Add(Path.Combine(directory!.FullName, relative)); psi.ArgumentList.Add("--request");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("R75AK actual UI probe did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        async Task Feed()
        {
            await process.StandardInput.BaseStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request), timeout.Token);
            process.StandardInput.Close();
        }
        var feed = Feed();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("R75AK actual UI probe timed out"); }
        await feed;
        Assert.True(process.ExitCode == 0, "R75AK actual UI probe failed: " + await error);
        using var result = JsonDocument.Parse(await output);
        return result.RootElement.Clone();
    }

    public static IEnumerable<object[]> R75AKUiCases()
    {
        foreach (var id in new[] { "highpass", "lowpass", "bass", "treble", "bandpass", "simple_notch", "simple_eq", "delay", "dehummer", "dynamics", "single_band_compressor" })
            foreach (var channels in new[] { 1, 2, 6, 8 }) yield return new object[] { id, channels, false };
        foreach (var id in new[] { "bass", "treble", "simple_eq", "single_band_compressor" })
            foreach (var channels in new[] { 1, 2, 6, 8 }) yield return new object[] { id, channels, true };
    }

    [Theory]
    [MemberData(nameof(R75AKUiCases))]
    public async Task R75AK_FullMechanicalUiParams_ProjectCodecBuilderAndRealPcmStayEquivalent(string id, int channels, bool nonIdentity)
    {
        var input = R73A4Signal(channels, id == "delay" ? 60000 : 9600);
        var overrides = new Dictionary<string, object>();
        if (id is "dynamics" or "single_band_compressor") overrides["ratio"] = 1;
        if (nonIdentity) overrides[id == "single_band_compressor" ? "output" : "boost"] = id == "single_band_compressor" ? 3 : 6;
        var result = await R75AKUiProbe(new { id, overrides, samples = input, includeProject = true });
        var raw = result.GetProperty("raw");
        var effect = JsonSerializer.Deserialize<CatalogAudioFxInstance>(result.GetProperty("projected"));
        Assert.NotNull(effect); Assert.Equal(id, effect!.Id);
        var keys = id switch
        {
            "highpass" or "lowpass" => new[] { "cutoff" },
            "bass" or "treble" => new[] { "boost" },
            "bandpass" or "simple_notch" => new[] { "center", "q" },
            "simple_eq" => new[] { "center", "q", "boost" },
            "delay" => new[] { "delay", "feedback", "mix" },
            "dynamics" => new[] { "threshold", "ratio", "attack", "release" },
            "single_band_compressor" => new[] { "threshold", "ratio", "attack", "release", "output" },
            _ => new[] { "freq", "gain", "harmonics", "q" }
        };
        Assert.Equal(keys.OrderBy(key => key), effect.Params.Keys.OrderBy(key => key));
        Assert.True(raw.GetProperty("params").EnumerateObject().Count() > effect.Params.Count);
        var saved = result.GetProperty("saved"); var json = saved.GetProperty("project").GetRawText();
        EditorProjectCodec.EnsureSupported(json);
        var project = EditorProjectCodec.Deserialize(json);
        Assert.Equal(new[] { 1, 4, 5, 6 }, project.Timeline!.Select(item => item.Track).OrderBy(track => track));
        foreach (var item in project.Timeline!)
        {
            Assert.Equal(1, item.CatalogAudioFxVersion);
            Assert.Equal(JsonSerializer.Serialize(effect), JsonSerializer.Serialize(Assert.Single(item.CatalogAudioFx!)));
            var host = Assert.Single(item.Effects!); Assert.Equal("compressor", host.Type);
            Assert.Equal(2d, host.Params!["ratio"]); Assert.Equal(6d, host.Params["kneeDb"]);
        }
        Assert.True(project.BaseAudioMuted); Assert.Equal(new[] { 4 }, project.MutedTracks); Assert.Equal(new[] { 4 }, project.SoloedTracks);
        Assert.DoesNotContain(saved.GetProperty("warnings").EnumerateArray(), warning => warning.GetProperty("code").GetString() == "effects_archived_not_rendered");
        var archive = project.ReplicaExtension!.Document;
        foreach (var track in archive.GetProperty("sequence").GetProperty("videoTracks").EnumerateArray()
            .Concat(archive.GetProperty("sequence").GetProperty("audioTracks").EnumerateArray()))
            foreach (var clip in track.GetProperty("clips").EnumerateArray()) Assert.Equal(raw.GetRawText(), clip.GetProperty("effects")[0].GetRawText());
        var restored = EditorProjectCodec.Deserialize(JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(archive.GetRawText(), restored.ReplicaExtension!.Document.GetRawText());
        var expected = JsonSerializer.Deserialize<double[][]>(result.GetProperty("preview")); Assert.NotNull(expected);
        var filter = EditorRenderService.AudioFilterChain(project.Timeline!.Single(item => item.Track == 4) with { Effects = null });
        var shouldChange = nonIdentity || id is "highpass" or "lowpass" or "bandpass" or "simple_notch" or "delay" or "dehummer";
        if (shouldChange)
        {
            Assert.False(string.IsNullOrWhiteSpace(filter), "R75AK non-identity effect must reach the actual render filter");
            for (var channel = 0; channel < channels; channel++)
                Assert.True(R72MaxError(input[channel], expected![channel]) > 1e-4, $"R75AK {id} channel {channel} preview must differ observably from the input");
        }
        else
        {
            // Ratio-one dynamics and zero-boost/output defaults are identity by design.
            R73A4AssertPcm(input, expected!);
        }
        R73A4AssertPcm(expected!, await R73A4Render(input, filter));
    }

    [Fact]
    public async Task R75AK_RealUiUnsupportedDefaultsRemainArchivedAndEffectiveControlsAreNotDiscarded()
    {
        // R77 separately proves active compressor defaults through the real PCM/export path.
        var ids = new[] { "scientific_filter", "vocal_enhancer" };
        var requests = ids.Select(id => new { id, includeProject = true }).ToArray();
        var results = await R75AKUiProbe(new { requests });
        foreach (var result in results.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, result.GetProperty("projected").ValueKind);
            var raw = result.GetProperty("raw"); var id = raw.GetProperty("effect").GetString();
            Assert.Equal(id == "scientific_filter" ? "Bessel" : "Male", raw.GetProperty("params").GetProperty(id == "scientific_filter" ? "type" : "mode").GetString());
            var saved = result.GetProperty("saved");
            var project = EditorProjectCodec.Deserialize(saved.GetProperty("project").GetRawText());
            Assert.All(project.Timeline!, item => { Assert.Null(item.CatalogAudioFxVersion); Assert.Null(item.CatalogAudioFx); Assert.Single(item.Effects!); });
            var warning = Assert.Single(saved.GetProperty("warnings").EnumerateArray().Where(value => value.GetProperty("code").GetString() == "effects_archived_not_rendered"));
            Assert.Equal("效果已保存到工程，但当前导出尚不渲染这些效果", warning.GetProperty("message").GetString());
            Assert.Equal(new[] { "clip-1", "clip-4", "clip-5", "clip-6" }, warning.GetProperty("clipIds").EnumerateArray().Select(value => value.GetString()).OrderBy(value => value));
            var archive = project.ReplicaExtension!.Document.GetProperty("sequence");
            foreach (var track in archive.GetProperty("videoTracks").EnumerateArray().Concat(archive.GetProperty("audioTracks").EnumerateArray()))
                foreach (var clip in track.GetProperty("clips").EnumerateArray()) Assert.Equal(raw.GetRawText(), clip.GetProperty("effects")[0].GetRawText());
        }
    }

    private static IEnumerable<CatalogAudioFxInstance> R77A6Effects()
    {
        foreach (var id in new[] { "dynamics", "single_band_compressor", "dynamics_rack", "tube_compressor", "distortion" }) yield return Fx(id);
        foreach (var id in new[] { "dynamics", "single_band_compressor", "tube_compressor" })
        {
            var edge = Fx(id, true, ("threshold", -60), ("ratio", 30), ("attack", id == "tube_compressor" ? .1 : .01), ("release", 1));
            if (id != "dynamics") edge.Params["output"] = 30;
            yield return edge;
            var unity = Fx(id, true, ("threshold", 0), ("ratio", 1), ("attack", 300), ("release", 3000));
            if (id != "dynamics") unity.Params["output"] = 30;
            yield return unity;
            yield return Fx(id, true, ("threshold", -17.25), ("ratio", 2.3), ("attack", .7), ("release", 91.5));
        }
        yield return Fx("single_band_compressor", true, ("ratio", 1), ("output", 40));
        yield return Fx("dynamics_rack", true, ("gate_on", 1), ("comp_on", 0), ("gate_threshold", -30), ("gate_hold", 0));
        yield return Fx("dynamics_rack", true, ("gate_on", 1), ("comp_on", 0), ("gate_threshold", -30), ("gate_hold", 50.125), ("gate_attack", .1), ("gate_release", 1));
        yield return Fx("dynamics_rack", true, ("gate_on", 1), ("comp_on", 0), ("gate_threshold", 0), ("gate_hold", 1000), ("gate_attack", 100), ("gate_release", 3000));
        yield return Fx("dynamics_rack", true, ("exp_on", 1), ("exp_threshold", -20), ("exp_ratio", 30), ("comp_on", 0));
        yield return Fx("dynamics_rack", true, ("comp_auto", 1), ("comp_makeup", 30), ("output", 30), ("comp_threshold", -60), ("comp_ratio", 30));
        yield return Fx("dynamics_rack", true, ("gate_on", 1), ("exp_on", 1), ("exp_threshold", -20), ("exp_ratio", 4), ("comp_auto", 1), ("lim_on", 1), ("soft_clip", 1), ("lim_threshold", -12), ("output", 6));
        yield return Fx("dynamics_rack", true, ("comp_on", 0), ("lim_on", 1), ("lim_threshold", -30), ("lim_release", 1));
        foreach (var curve in new[] { 0, 1, 2, 3 })
        {
            yield return Fx("distortion", true, ("curve", curve), ("drive", 48), ("symmetry", -100), ("tone", 500), ("output", 12), ("mix", 100));
            yield return Fx("distortion", true, ("curve", curve), ("drive", 31.5), ("symmetry", 100), ("tone", 19998.5), ("output", -12), ("mix", 37.5));
        }
        yield return Fx("distortion", true, ("curve", 2), ("mix", 0), ("tone", 19999));
    }

    public static IEnumerable<object[]> R77A6Cases()
    {
        foreach (var effect in R77A6Effects())
            foreach (var channels in new[] { 1, 2, 6, 8 }) yield return new object[] { effect, channels };
    }

    private static double[][] R77A6Signal(int channels, int frames = 24000)
    {
        var amplitudes = new[] { 0d, .001, .8, .015, .14, 2, .000001, 0 };
        return Enumerable.Range(0, channels).Select(c => Enumerable.Range(0, frames).Select(i =>
        {
            var amplitude = amplitudes[i / 2000 % 8];
            return (double)(float)(amplitude * (c == channels - 1 ? 1 : .17 + c * .03)
                * (.7 * Math.Sin((i + c * 31) * (173 + c * 937) * Math.PI * 2 / 48000) + .3) + (i == 511 + c ? -.9 : 0));
        }).ToArray()).ToArray();
    }

    private static void R77A6AssertPcm(double[][] expected, double[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var c = 0; c < expected.Length; c++)
        {
            Assert.Equal(expected[c].Length, actual[c].Length);
            Assert.All(expected[c], value => Assert.True(double.IsFinite(value)));
            Assert.All(actual[c], value => Assert.True(double.IsFinite(value)));
            var maximum = R72MaxError(actual[c], expected[c]);
            var referenceRms = R72Rms(expected[c]); var actualRms = R72Rms(actual[c]);
            var relative = referenceRms == 0 ? actualRms == 0 ? 0 : double.PositiveInfinity : Math.Abs(actualRms / referenceRms - 1);
            Assert.True(maximum < 1e-5 && relative < 1e-3, $"R77A6 channel={c} max={maximum:R} RMS={relative:R}");
        }
    }

    [Theory]
    [MemberData(nameof(R77A6Cases))]
    public async Task R77A6_ActualUiCodecBuilderAndRealPcm_MatchEveryChannel(CatalogAudioFxInstance overrides, int channels)
    {
        var input = R77A6Signal(channels);
        var result = await R75AKUiProbe(new { id = overrides.Id, overrides = overrides.Params, samples = input, includeProject = true });
        var effect = JsonSerializer.Deserialize<CatalogAudioFxInstance>(result.GetProperty("projected")); Assert.NotNull(effect);
        var expected = JsonSerializer.Deserialize<double[][]>(result.GetProperty("preview")); Assert.NotNull(expected);
        var saved = result.GetProperty("saved");
        var project = EditorProjectCodec.Deserialize(saved.GetProperty("project").GetRawText());
        Assert.Equal(new[] { 1, 4, 5, 6 }, project.Timeline!.Select(item => item.Track).OrderBy(track => track));
        Assert.True(project.BaseAudioMuted); Assert.Equal(new[] { 4 }, project.MutedTracks); Assert.Equal(new[] { 4 }, project.SoloedTracks);
        Assert.DoesNotContain(saved.GetProperty("warnings").EnumerateArray(), warning => warning.GetProperty("code").GetString() == "effects_archived_not_rendered");
        foreach (var item in project.Timeline!)
        {
            Assert.Equal(1, item.CatalogAudioFxVersion);
            Assert.Equal(JsonSerializer.Serialize(effect), JsonSerializer.Serialize(Assert.Single(item.CatalogAudioFx!)));
            Assert.Equal("compressor", Assert.Single(item.Effects!).Type);
        }
        var restored = EditorProjectCodec.Deserialize(JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(project.ReplicaExtension!.Document.GetRawText(), restored.ReplicaExtension!.Document.GetRawText());
        var filter = EditorRenderService.AudioFilterChain(project.Timeline!.Single(item => item.Track == 4) with { Effects = null });
        Assert.Contains("aformat=sample_fmts=flt,aeval=exprs=", filter);
        Assert.EndsWith(",aresample", filter);
        R77A6AssertPcm(expected!, await R73A4Render(input, filter));
        Assert.Equal("", AudioFxChain.Build(new[] { effect! with { Enabled = false } }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task R77A6_LinkedDetectorChainOrderAndLongState_ArePreserved(int channels)
    {
        var input = R77A6Signal(channels, 96000);
        var chain = new[] { Fx("dynamics_rack", true, ("gate_on", 1), ("gate_hold", 137.125), ("exp_on", 1), ("exp_threshold", -20), ("exp_ratio", 3), ("comp_auto", 1)),
            Fx("tube_compressor", true, ("threshold", -35), ("ratio", 12), ("release", 5000)),
            Fx("distortion", true, ("curve", 2), ("drive", 28), ("symmetry", 53), ("tone", 913), ("mix", 73)) };
        var expected = input;
        foreach (var effect in chain) expected = await R73A4ActualPreview(expected, effect);
        R77A6AssertPcm(expected, await R73A4Render(input, RenderChain(chain)));
        var reversed = await R73A4Render(input, RenderChain(chain.Reverse().ToArray()));
        Assert.True(expected.Select((channel, c) => R72MaxError(channel, reversed[c])).Max() > .01);
        if (channels > 1)
        {
            var effect = Fx("dynamics", true, ("threshold", -40), ("ratio", 12), ("attack", .01));
            var linked = await R73A4Render(input, RenderChain(effect));
            R77A6AssertPcm(await R73A4ActualPreview(input, effect), linked);
            var isolated = await R73A4ActualPreview(new[] { input[0] }, effect);
            Assert.True(R72MaxError(linked[0], isolated[0]) > .001, "Detector must link the loud surround channel, not compress channels independently");
        }
    }

    [Fact]
    public void R77A6_ActiveDefaultsRoundTripEveryContainer_WithoutWeakeningValidation()
    {
        var effects = new[] { "dynamics", "single_band_compressor", "dynamics_rack", "tube_compressor", "distortion" }.Select(id => Fx(id)).ToArray();
        foreach (var container in new[] { "timeline", "silenceTrimBackup", "nested" })
        {
            var item = new TimelineItem("r77", "source", 0, 1, Track: 4) { CatalogAudioFxVersion = 1, CatalogAudioFx = effects };
            var payload = JsonSerializer.Serialize(new[] { item }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var body = container == "nested" ? "\"nestedSequences\":{\"n\":{\"timeline\":" + payload + "}}" : "\"" + container + "\":" + payload;
            var project = EditorProjectCodec.Deserialize("{\"schemaVersion\":1," + body + "}");
            EditorProjectCodec.EnsureSupported(JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        foreach (var id in effects.Select(effect => effect.Id))
            Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"" + id + "\",\"enabled\":false,\"params\":{\"unknown\":0}}]", AudioFxChain.UnsupportedParam);
        Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"multiband_compressor\",\"enabled\":false,\"params\":{\"link\":0.5}}]", AudioFxChain.InvalidParam);
        foreach (var id in new[] { "hard_limiter", "studio_reverb", "convolution_reverb", "surround_reverb", "denoise" })
            Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"" + id + "\",\"enabled\":false,\"params\":{}}]", AudioFxChain.UnsupportedId);
    }

    private static CatalogAudioFxInstance R75MONOEffect(string id) => id switch
    {
        "tube_compressor" => Fx(id, true, ("ratio", 1), ("output", 6)),
        "channel_volume_a" => Fx(id, true, ("left", -6), ("right", 12), ("center", 3), ("lfe", -9), ("left_surround", -96), ("right_surround", 6)),
        _ => Fx(id)
    };

    [Fact]
    public void R75MONO_AudioFilter_AddsOneOutputBarrierOnlyForEnabledAeval()
    {
        var ids = new[] { "tube_compressor", "fill_left", "fill_right", "invert_a", "swap_channels", "stereo_width",
            "stereo_expander", "channel_mixer_a", "channel_volume_a", "panner_ambisonics" };
        var effects = ids.Select(R75MONOEffect).ToArray();
        foreach (var effect in effects)
        {
            Assert.Equal("aresample=48000," + AudioFxChain.Build(new[] { effect }) + ",aresample", RenderChain(effect));
            Assert.Equal("", RenderChain(effect with { Enabled = false }));
        }
        Assert.Equal("aresample=48000," + AudioFxChain.Build(effects) + ",aresample", RenderChain(effects));
        Assert.Equal("", RenderChain());
        Assert.Equal("aresample=48000,volume=6dB:precision=double", RenderChain(Fx("amplify", true, ("gain", 6))));
        var item = new TimelineItem("v1", "source", 0, 1, FadeIn: .1, FadeOut: .2)
            { CatalogAudioFxVersion = 1, CatalogAudioFx = new[] { Fx("invert_a") } };
        Assert.Equal("aresample=48000,aeval=exprs='-val(ch)':c=same,aresample,afade=t=in:st=0:d=0.1,afade=t=out:st=0.8:d=0.2",
            EditorRenderService.AudioFilterChain(item));
    }

    public static IEnumerable<object[]> R75MONONativeCases()
    {
        foreach (var id in new[] { "tube_compressor", "fill_left", "invert_a", "channel_volume_a" })
            foreach (var channels in new[] { 1, 2, 6, 8 }) yield return new object[] { id, channels };
    }

    [Theory]
    [MemberData(nameof(R75MONONativeCases))]
    public async Task R75MONO_RealFfmpeg_PreservesNativeChannelPreviewSemantics(string id, int channels)
    {
        var input = R73A4Signal(channels);
        var effect = R75MONOEffect(id);
        R73A4AssertPcm(await R73A4ActualPreview(input, effect), await R73A4Render(input, RenderChain(effect)));
    }

    [Theory]
    [InlineData("tube_compressor", false)]
    [InlineData("tube_compressor", true)]
    [InlineData("fill_left", false)]
    [InlineData("fill_left", true)]
    [InlineData("invert_a", false)]
    [InlineData("invert_a", true)]
    [InlineData("channel_volume_a", false)]
    [InlineData("channel_volume_a", true)]
    public async Task R75MONO_RenderAsync_MonoFixtureExportsAudibleStereoAac(string id, bool detached)
    {
        var root = Path.Combine(Path.GetTempPath(), "liveclipper-r75-mono-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var tools = new MediaTools(Ffmpeg());
        EditorRenderService? renderer = null;
        try
        {
            var store = new Store(Path.Combine(root, "test.db")); store.Migrate();
            var studio = new StudioStore(store.DatabasePath); studio.Migrate();
            renderer = new EditorRenderService(store, studio, tools, root);
            var source = Path.Combine(root, "mono-source.mp4");
            await tools.RunAsync(tools.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=white:size=160x90:rate=24",
                 "-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000", "-t", "0.5", "-c:v", "libx264", "-preset", "ultrafast",
                 "-pix_fmt", "yuv420p", "-ar", "48000", "-ac", "1", "-c:a", "aac", source], token);
            var sourceAudio = await R75MONOProbeAudio(tools, source, token);
            Assert.Equal(1, sourceAudio.GetProperty("channels").GetInt32());
            var clip = new Clip("mono", "mono-event", source, null, DateTimeOffset.UtcNow); store.SaveClip(clip);
            var effect = R75MONOEffect(id);
            var video = new TimelineItem("v1", clip.Id, 0, .5, AudioDetached: detached);
            TimelineItem[] timeline = detached
                ? new[] { video, new TimelineItem("a1", clip.Id, 0, .5, Track: 4)
                    { CatalogAudioFxVersion = 1, CatalogAudioFx = new[] { effect } } }
                : new[] { video with { CatalogAudioFxVersion = 1, CatalogAudioFx = new[] { effect } } };
            var plan = new EditorProject { Start = 0, End = .5, Timeline = timeline, Format = "h264", Resolution = 480, Fps = 24 };
            var output = Path.Combine(root, "rendered.mp4");
            await renderer.RenderAsync(clip, plan, output, Path.Combine(root, "work"), token);
            Assert.True(File.Exists(output) && new FileInfo(output).Length > 0, "RenderAsync must produce a nonempty MP4");
            var audio = await R75MONOProbeAudio(tools, output, token);
            Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
            Assert.Equal(2, audio.GetProperty("channels").GetInt32());
            Assert.Equal("48000", audio.GetProperty("sample_rate").GetString());
            var duration = double.Parse(audio.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            Assert.InRange(duration, .45, .6);
            var pcmPath = Path.Combine(root, "decoded.f64le");
            await tools.RunAsync(tools.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", output, "-map", "0:a:0", "-c:a", "pcm_f64le", "-f", "f64le", pcmPath], token);
            var pcm = await File.ReadAllBytesAsync(pcmPath, token);
            Assert.Equal(0, pcm.Length % (2 * sizeof(double)));
            var frames = pcm.Length / (2 * sizeof(double));
            Assert.InRange(frames, 21600, 28800);
            for (var c = 0; c < 2; c++)
            {
                double squareSum = 0;
                for (var i = 0; i < frames; i++)
                {
                    var sample = BinaryPrimitives.ReadDoubleLittleEndian(pcm.AsSpan((i * 2 + c) * sizeof(double), sizeof(double)));
                    Assert.True(double.IsFinite(sample), "Non-finite decoded AAC sample");
                    squareSum += sample * sample;
                }
                Assert.True(Math.Sqrt(squareSum / frames) > .01, $"AAC channel {c} lost the source tone");
            }
        }
        finally
        {
            if (renderer is not null) await renderer.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<CatalogAudioFxInstance> R78A7Effects()
    {
        yield return Fx("speech_enhance");
        foreach (var tone in new[] { 0, 1 }) foreach (var mix in new[] { 0d, 37.5, 100 })
            yield return Fx("speech_enhance", true, ("tone", tone), ("mix", mix));
        yield return Fx("mastering");
        yield return Fx("mastering", true, ("loudness", 100), ("output", 12));
        yield return Fx("mastering", true, ("eq_low", 12), ("eq_mid", -12), ("eq_mid_freq", 100), ("eq_high", 12), ("output", 12), ("widener", 200));
        yield return Fx("mastering", true, ("eq_low", -12), ("eq_mid", 12), ("eq_mid_freq", 10000), ("eq_high", -12), ("output", -24), ("widener", 0));
        foreach (var mode in new[] { 0, 1, 2 })
        {
            yield return Fx("mastering", true, ("exciter_mode", mode), ("exciter", 100), ("widener", 200), ("loudness", 75), ("output", 6));
            yield return Fx("mastering", true, ("exciter_mode", mode), ("exciter", 37.5), ("widener", 37.5), ("loudness", 1), ("eq_mid", 8), ("eq_mid_freq", 1234.5));
        }
    }

    public static IEnumerable<object[]> R78A7Cases()
    {
        foreach (var effect in R78A7Effects()) foreach (var channels in new[] { 1, 2, 6, 8 }) yield return new object[] { effect, channels };
    }

    private static void R78A7AssertPcm(double[][] expected, double[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var c = 0; c < expected.Length; c++)
        {
            Assert.Equal(expected[c].Length, actual[c].Length);
            double errorEnergy = 0, referenceEnergy = 0;
            for (var i = 0; i < expected[c].Length; i++)
            {
                Assert.True(double.IsFinite(expected[c][i]) && double.IsFinite(actual[c][i]));
                var difference = actual[c][i] - expected[c][i];
                errorEnergy += difference * difference; referenceEnergy += expected[c][i] * expected[c][i];
            }
            var maximum = R72MaxError(actual[c], expected[c]);
            var referenceRms = R72Rms(expected[c]); var actualRms = R72Rms(actual[c]);
            var relative = referenceRms == 0 ? actualRms == 0 ? 0 : double.PositiveInfinity : Math.Abs(actualRms / referenceRms - 1);
            var normalizedError = Math.Sqrt(errorEnergy / Math.Max(referenceEnergy, 1e-30));
            Assert.True(maximum <= 1e-6 && relative <= 2e-5 && normalizedError <= 2e-5,
                $"R78A7 channel={c} max={maximum:R} RMS={relative:R} errorRMS={normalizedError:R}");
        }
    }

    [Theory]
    [MemberData(nameof(R78A7Cases))]
    public async Task R78A7_ActualUiCodecAndProductionBuilder_MatchPreviewStrictPcm(CatalogAudioFxInstance overrides, int channels)
    {
        var input = R77A6Signal(channels);
        var result = await R75AKUiProbe(new { id = overrides.Id, overrides = overrides.Params, samples = input, includeProject = true });
        var effect = JsonSerializer.Deserialize<CatalogAudioFxInstance>(result.GetProperty("projected")); Assert.NotNull(effect);
        var expected = JsonSerializer.Deserialize<double[][]>(result.GetProperty("preview")); Assert.NotNull(expected);
        var saved = result.GetProperty("saved");
        var project = EditorProjectCodec.Deserialize(saved.GetProperty("project").GetRawText());
        Assert.Equal(new[] { 1, 4, 5, 6 }, project.Timeline!.Select(item => item.Track).OrderBy(track => track));
        Assert.True(project.BaseAudioMuted); Assert.Equal(new[] { 4 }, project.MutedTracks); Assert.Equal(new[] { 4 }, project.SoloedTracks);
        Assert.DoesNotContain(saved.GetProperty("warnings").EnumerateArray(), warning => warning.GetProperty("code").GetString() == "effects_archived_not_rendered");
        foreach (var item in project.Timeline!)
        {
            Assert.Equal(1, item.CatalogAudioFxVersion);
            Assert.Equal(JsonSerializer.Serialize(effect), JsonSerializer.Serialize(Assert.Single(item.CatalogAudioFx!)));
            Assert.Equal("compressor", Assert.Single(item.Effects!).Type);
        }
        var restored = EditorProjectCodec.Deserialize(JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(project.ReplicaExtension!.Document.GetRawText(), restored.ReplicaExtension!.Document.GetRawText());
        var filter = EditorRenderService.AudioFilterChain(project.Timeline!.Single(item => item.Track == 4) with { Effects = null });
        if (effect!.Id == "speech_enhance" && effect.Params["mix"] == 0) Assert.Equal("", filter);
        else { Assert.Contains("aeval=exprs=", filter); Assert.EndsWith(",aresample", filter); }
        R78A7AssertPcm(expected!, await R73A4Render(input, filter.Length == 0 ? "anull" : filter));
        Assert.Equal("", AudioFxChain.Build(new[] { effect with { Enabled = false } }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task R78A7_LongRepeatedChainLinkedDetectorAndOrder_RemainExact(int channels)
    {
        var input = R77A6Signal(channels, 96000);
        var chain = new[] { Fx("speech_enhance", true, ("mix", 37.5), ("tone", 1)),
            Fx("mastering", true, ("eq_low", 9), ("eq_mid", -7), ("eq_high", 5), ("exciter", 77.5), ("exciter_mode", 2), ("widener", 173), ("loudness", 43), ("output", -3)),
            Fx("speech_enhance", true, ("mix", 66.75), ("tone", 0)) };
        var expected = input;
        foreach (var effect in chain) expected = await R73A4ActualPreview(expected, effect);
        R78A7AssertPcm(expected, await R73A4Render(input, RenderChain(chain)));
        var reversed = await R73A4Render(input, RenderChain(chain.Reverse().ToArray()));
        Assert.True(expected.Select((channel, c) => R72MaxError(channel, reversed[c])).Max() > .001);
        if (channels > 1)
        {
            var effect = Fx("speech_enhance");
            var linked = await R73A4Render(input, RenderChain(effect));
            R78A7AssertPcm(await R73A4ActualPreview(input, effect), linked);
            var isolated = await R73A4ActualPreview(new[] { input[0] }, effect);
            Assert.True(R72MaxError(linked[0], isolated[0]) > .001);
        }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public async Task R78A7_MasteringOutputAndNonlinearStages_OnlyProcessFirstPair(int channels)
    {
        var input = R77A6Signal(channels);
        var effect = Fx("mastering", true, ("output", 12), ("exciter", 100), ("widener", 200), ("loudness", 100));
        var output = await R73A4Render(input, RenderChain(effect));
        R78A7AssertPcm(await R73A4ActualPreview(input, effect), output);
        for (var c = 2; c < channels; c++) Assert.Equal(input[c], output[c]);
        Assert.True(R72MaxError(input[0], output[0]) > .01);
    }

    [Theory]
    [InlineData("denoise", 1)]
    [InlineData("denoise", 2)]
    [InlineData("denoise", 6)]
    [InlineData("denoise", 8)]
    [InlineData("dereverb", 1)]
    [InlineData("dereverb", 2)]
    [InlineData("dereverb", 6)]
    [InlineData("dereverb", 8)]
    public async Task R78A7_ZeroAmountStft_IsNotAnUnconditionalDelayOnlyMapping(string id, int channels)
    {
        var input = R77A6Signal(channels, 12000).Select(channel => channel.Select(value => (double)(float)(value * 32)).ToArray()).ToArray();
        var effect = Fx(id, true, ("amount", 0));
        var expected = await R73A4ActualPreview(input, effect);
        var delayed = await R73A4Render(input, "adelay=2048S:all=1");
        Assert.True(expected.Select((channel, c) => R72MaxError(channel, delayed[c])).Max() > 1e-6);
        Assert.Equal(AudioFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { effect })).Message);
    }

    [Fact]
    public void R78A7_NewExactMappingsRoundTripEveryContainer_KeepStrictDisabledValidation()
    {
        foreach (var container in new[] { "timeline", "silenceTrimBackup", "nested" })
        {
            var effects = R78A7Effects().Concat(new[] { Fx("mastering", false), Fx("speech_enhance", false) }).ToArray();
            var item = new TimelineItem("r78", "source", 0, 1, Track: 4) { CatalogAudioFxVersion = 1, CatalogAudioFx = effects };
            var payload = JsonSerializer.Serialize(new[] { item }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var body = container == "nested" ? "\"nestedSequences\":{\"n\":{\"timeline\":" + payload + "}}" : "\"" + container + "\":" + payload;
            var json = "{\"schemaVersion\":1," + body + "}";
            EditorProjectCodec.EnsureSupported(json);
            EditorProjectCodec.Deserialize(json);
        }
        foreach (var enabled in new[] { true, false })
        {
            Assert.Equal(AudioFxChain.UnsupportedMapping, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("mastering", enabled, ("reverb", .01)) })).Message);
            foreach (var id in new[] { "speech_enhance", "mastering" })
                Assert.Equal(AudioFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, enabled, ("unknown", 0)) })).Message);
            foreach (var effect in new[] { Fx("speech_enhance", enabled, ("tone", .5)), Fx("speech_enhance", enabled, ("mix", double.NaN)),
                Fx("speech_enhance", enabled, ("mix", 100.01)), Fx("mastering", enabled, ("exciter_mode", .5)), Fx("mastering", enabled, ("output", 12.01)) })
                Assert.Equal(AudioFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { effect })).Message);
        }
        Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"mastering\",\"enabled\":false,\"params\":{\"reverb\":1}}]", AudioFxChain.UnsupportedMapping);
        var effectsForCulture = R78A7Effects().ToArray(); var invariant = AudioFxChain.Build(effectsForCulture); var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(invariant, AudioFxChain.Build(effectsForCulture)); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static IEnumerable<CatalogAudioFxInstance> R79A8Effects()
    {
        yield return Fx("multiband_compressor");
        yield return Fx("multiband_compressor", true, ("link", 0));
        yield return Fx("multiband_compressor", true, ("b1_ratio", 1), ("b2_ratio", 1), ("b3_ratio", 1), ("b4_ratio", 1));
        yield return Fx("multiband_compressor", true, ("xo1", 1000), ("xo2", 100), ("xo3", 1000));
        yield return Fx("multiband_compressor", true, ("xo1", 20), ("xo2", 8000), ("xo3", 20000), ("link", 0), ("output", 18));
        yield return Fx("multiband_compressor", true, ("b1_solo", 1), ("b1_gain", 18), ("b1_threshold", -60), ("b1_ratio", 30), ("b1_attack", .1), ("b1_release", 1));
        yield return Fx("multiband_compressor", true, ("b2_solo", 1), ("b3_solo", 1), ("b2_bypass", 1), ("b2_gain", -18), ("b3_gain", 12.25), ("output", -18));
        yield return Fx("multiband_compressor", true, ("b1_bypass", 1), ("b2_bypass", 1), ("b3_bypass", 1), ("b4_bypass", 1), ("output", 18));
        yield return Fx("multiband_compressor", true, ("lim_on", 1), ("lim_threshold", -30), ("lim_release", 1), ("output", 18));
        yield return Fx("multiband_compressor", true, ("lim_on", 1), ("lim_threshold", 0), ("lim_release", 1000), ("link", 0), ("xo1", 31.5), ("xo2", 311.25), ("xo3", 17777.5));
        for (var b = 1; b <= 4; b++)
            yield return Fx("multiband_compressor", true, ($"b{b}_solo", 1), ($"b{b}_threshold", 0), ($"b{b}_ratio", 30), ($"b{b}_attack", 500), ($"b{b}_release", 5000), ($"b{b}_gain", 18));
    }

    public static IEnumerable<object[]> R79A8Cases()
    {
        foreach (var effect in R79A8Effects()) foreach (var channels in new[] { 1, 2, 6, 8 }) yield return new object[] { effect, channels };
    }

    private static Task<double[][]> R79A8Render(double[][] samples, string filter)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        Assert.True(!string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(ffmpeg), "Set LIVECLIPPER_TEST_FFMPEG to a real executable; R79A8 never skips FFmpeg tests.");
        return R73A4Render(samples, filter);
    }

    [Theory]
    [MemberData(nameof(R79A8Cases))]
    public async Task R79A8_ActualUiCodecAndProductionGraph_MatchStrictPreviewPcm(CatalogAudioFxInstance overrides, int channels)
    {
        var input = R77A6Signal(channels);
        var result = await R75AKUiProbe(new { id = overrides.Id, overrides = overrides.Params, samples = input, includeProject = true });
        var effect = JsonSerializer.Deserialize<CatalogAudioFxInstance>(result.GetProperty("projected")); Assert.NotNull(effect);
        Assert.Equal(36, effect!.Params.Count);
        var expected = JsonSerializer.Deserialize<double[][]>(result.GetProperty("preview")); Assert.NotNull(expected);
        var saved = result.GetProperty("saved");
        var project = EditorProjectCodec.Deserialize(saved.GetProperty("project").GetRawText());
        Assert.Equal(new[] { 1, 4, 5, 6 }, project.Timeline!.Select(item => item.Track).OrderBy(track => track));
        Assert.True(project.BaseAudioMuted); Assert.Equal(new[] { 4 }, project.MutedTracks); Assert.Equal(new[] { 4 }, project.SoloedTracks);
        Assert.DoesNotContain(saved.GetProperty("warnings").EnumerateArray(), warning => warning.GetProperty("code").GetString() == "effects_archived_not_rendered");
        foreach (var item in project.Timeline!)
        {
            Assert.Equal(1, item.CatalogAudioFxVersion);
            Assert.Equal(JsonSerializer.Serialize(effect), JsonSerializer.Serialize(Assert.Single(item.CatalogAudioFx!)));
            Assert.Equal("compressor", Assert.Single(item.Effects!).Type);
        }
        var restored = EditorProjectCodec.Deserialize(JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(project.ReplicaExtension!.Document.GetRawText(), restored.ReplicaExtension!.Document.GetRawText());
        var filter = EditorRenderService.AudioFilterChain(project.Timeline!.Single(item => item.Track == 4) with { Effects = null });
        Assert.Contains("asplit=4", filter); Assert.Contains("amix=inputs=4", filter); Assert.EndsWith(",aresample", filter);
        R78A7AssertPcm(expected!, await R79A8Render(input, filter));
        Assert.Equal("", AudioFxChain.Build(new[] { effect with { Enabled = false } }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task R79A8_LongRepeatedGraphsAndLinkedDetection_PreserveStateAndOrder(int channels)
    {
        var input = R77A6Signal(channels, 96000);
        var chain = new[] { Fx("multiband_compressor", true, ("b1_gain", 9), ("b3_ratio", 8), ("xo2", 1777), ("output", 5)),
            Fx("multiband_compressor", true, ("link", 0), ("b2_gain", -12), ("b4_solo", 1), ("lim_on", 1), ("lim_threshold", -9)) };
        var expected = input;
        foreach (var effect in chain) expected = await R73A4ActualPreview(expected, effect);
        R78A7AssertPcm(expected, await R79A8Render(input, RenderChain(chain)));
        var reversed = await R79A8Render(input, RenderChain(chain.Reverse().ToArray()));
        Assert.True(expected.Select((channel, c) => R72MaxError(channel, reversed[c])).Max() > .001);
        if (channels > 1)
        {
            var effect = Fx("multiband_compressor");
            var linked = await R79A8Render(input, RenderChain(effect));
            R78A7AssertPcm(await R73A4ActualPreview(input, effect), linked);
            var isolated = await R73A4ActualPreview(new[] { input[0] }, effect);
            Assert.True(R72MaxError(linked[0], isolated[0]) > .001);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task R79A8_ChannelsAfterEight_ArePassthroughAndNeverDriveLinkedLimiter(int link)
    {
        var input = R77A6Signal(16, 12000);
        for (var c = 8; c < 16; c++) input[c] = input[c].Select(value => (double)(float)(value * 100)).ToArray();
        var effect = Fx("multiband_compressor", true, ("link", link), ("output", 18), ("lim_on", 1), ("lim_threshold", -30));
        var actual = await R79A8Render(input, RenderChain(effect));
        R78A7AssertPcm(await R73A4ActualPreview(input, effect), actual);
        for (var c = 8; c < 16; c++) Assert.Equal(input[c], actual[c]);
        R78A7AssertPcm(await R73A4ActualPreview(input.Take(8).ToArray(), effect), actual.Take(8).ToArray());
    }

    [Fact]
    public void R79A8_AllControlsValidateBeforeDisabledSkip_AndEveryContainerRoundTrips()
    {
        foreach (var container in new[] { "timeline", "silenceTrimBackup", "nested" })
        {
            var effects = R79A8Effects().Concat(new[] { Fx("multiband_compressor", false) }).ToArray();
            var item = new TimelineItem("r79", "source", 0, 1, Track: 4) { CatalogAudioFxVersion = 1, CatalogAudioFx = effects };
            var payload = JsonSerializer.Serialize(new[] { item }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var body = container == "nested" ? "\"nestedSequences\":{\"n\":{\"timeline\":" + payload + "}}" : "\"" + container + "\":" + payload;
            var json = "{\"schemaVersion\":1," + body + "}";
            EditorProjectCodec.EnsureSupported(json); EditorProjectCodec.Deserialize(json);
        }
        var ranges = new List<(string Key, double Low, double High)> { ("xo1", 20, 1000), ("xo2", 100, 8000), ("xo3", 1000, 20000), ("output", -18, 18), ("lim_threshold", -30, 0), ("lim_release", 1, 1000) };
        for (var b = 1; b <= 4; b++)
            ranges.AddRange(new[] { ($"b{b}_threshold", -60d, 0d), ($"b{b}_ratio", 1d, 30d), ($"b{b}_attack", .1, 500d), ($"b{b}_release", 1d, 5000d), ($"b{b}_gain", -18d, 18d) });
        foreach (var (key, low, high) in ranges) foreach (var enabled in new[] { true, false })
        {
            foreach (var value in new[] { low, high }) AudioFxChain.Build(new[] { Fx("multiband_compressor", enabled, (key, value)) });
            foreach (var value in new[] { low - .01, high + .01, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Assert.Equal(AudioFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("multiband_compressor", enabled, (key, value)) })).Message);
        }
        var toggles = new[] { "link", "lim_on", "b1_solo", "b2_solo", "b3_solo", "b4_solo", "b1_bypass", "b2_bypass", "b3_bypass", "b4_bypass" };
        foreach (var key in toggles) foreach (var enabled in new[] { true, false })
        {
            foreach (var value in new[] { 0d, 1d }) AudioFxChain.Build(new[] { Fx("multiband_compressor", enabled, (key, value)) });
            foreach (var value in new[] { -.01, .5, 1.01, double.NaN })
                Assert.Equal(AudioFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("multiband_compressor", enabled, (key, value)) })).Message);
        }
        foreach (var enabled in new[] { true, false })
            Assert.Equal(AudioFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("multiband_compressor", enabled, ("unknown", 0)) })).Message);
        Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"multiband_compressor\",\"enabled\":false,\"params\":{\"link\":0.5}}]", AudioFxChain.InvalidParam);
        Codec_InvalidCatalogAudio_RejectsTimelineBackupAndNested("\"catalogAudioFxVersion\":1,\"catalogAudioFx\":[{\"id\":\"multiband_compressor\",\"enabled\":false,\"params\":{\"unknown\":0}}]", AudioFxChain.UnsupportedParam);
        var invariant = AudioFxChain.Build(R79A8Effects().ToArray()); var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(invariant, AudioFxChain.Build(R79A8Effects().ToArray())); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void R79A8_UnprovedRemainingFamilies_StillRejectEnabledAndDisabledDtos()
    {
        foreach (var enabled in new[] { true, false })
        {
            foreach (var id in new[] { "hard_limiter", "studio_reverb", "convolution_reverb", "surround_reverb", "denoise", "dereverb" })
                Assert.Equal(AudioFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx(id, enabled) })).Message);
            Assert.Equal(AudioFxChain.UnsupportedMapping, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("deesser", enabled, ("reduction", .0000001)) })).Message);
            Assert.Equal(AudioFxChain.UnsupportedMapping, Assert.Throws<ArgumentException>(() => AudioFxChain.Build(new[] { Fx("mastering", enabled, ("reverb", .01)) })).Message);
        }
    }

    private static async Task<JsonElement> R75MONOProbeAudio(MediaTools tools, string path, CancellationToken token)
    {
        var json = await tools.RunAsync(tools.Ffprobe,
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name,channels,sample_rate,duration", "-of", "json", path], token);
        using var document = JsonDocument.Parse(json);
        return Assert.Single(document.RootElement.GetProperty("streams").EnumerateArray()).Clone();
    }
}
