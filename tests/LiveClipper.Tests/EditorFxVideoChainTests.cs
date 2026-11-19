using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Core.EditorFx;

namespace LiveClipper.Tests;

public sealed class EditorFxVideoChainTests
{
    private static CatalogFxInstance Fx(string id, bool enabled = true, params (string K, double V)[] p)
        => new(id, enabled, p.ToDictionary(x => x.K, x => x.V));

    // AUDIT0 rejects the entire IDs, including former identity inputs and disabled DTOs.
    // RGB/alpha equivalence was never proven; preview data remains in replicaExtension instead.
    private static void AssertArchivedV4Rejected(CatalogFxInstance effect)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effects = new[] { effect with { Enabled = enabled } };
            Assert.Equal(VideoFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => VideoFxChain.Validate(effects)).Message);
            Assert.Equal(VideoFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(effects)).Message);
            foreach (var track in new[] { 1, 2, 3 })
            {
                var item = new TimelineItem("v1", "c", 0, 1) { Track = track, CatalogFxVersion = 1, CatalogFx = effects };
                Assert.Equal(VideoFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item, 64, 32, 24)).Message);
            }
        }
    }

    [Fact]
    public void Build_OrdersFlipsAndBlur_SkipsDisabled()
    {
        var chain = new[] { Fx("vertical_flip"), Fx("gaussian_blur", true, ("radius", 12.5)), Fx("horizontal_flip", false), Fx("horizontal_flip") };
        Assert.Equal("vflip," + VideoFxChain.Build(new[] { chain[1] }) + ",hflip", VideoFxChain.Build(chain));
        Assert.Contains("format=gbrapf32le", VideoFxChain.Build(new[] { chain[1] }));
        Assert.DoesNotContain("gblur=", VideoFxChain.Build(chain));
    }

    [Fact]
    public void Build_BlurAtOrBelowIdentityCutoff_AddsNothing()
        => Assert.Equal("", VideoFxChain.Build(new[] { Fx("gaussian_blur", true, ("radius", 0.3)) }));

    [Theory]
    [InlineData("zoom_blur", VideoFxChain.UnsupportedId)]
    [InlineData("horizontal_flip", VideoFxChain.UnsupportedParam)]
    public void Build_UnknownIdOrParam_ThrowsStableCode(string id, string code)
    {
        var ex = Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, true, ("radius", 1)) }));
        Assert.Equal(code, ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1025)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Build_BlurRadiusOutOfRange_Throws(double radius)
    {
        var ex = Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx("gaussian_blur", true, ("radius", radius)) }));
        Assert.Equal(VideoFxChain.InvalidParam, ex.Message);
    }

    [Fact]
    public void Build_NullChain_Throws()
        => Assert.Equal(VideoFxChain.InvalidChain, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(null!)).Message);

    [Fact]
    public void VisualFilter_WithoutCatalogFx_IsUnchanged_AndWithFxAppends()
    {
        var plain = new TimelineItem("v1", "c", 0, 1);
        var baseline = EditorRenderService.VisualFilter(plain, 64, 32, 24);
        var withFx = EditorRenderService.VisualFilter(plain with { CatalogFxVersion = 1, CatalogFx = new[] { Fx("horizontal_flip") } }, 64, 32, 24);
        Assert.DoesNotContain("hflip", baseline);
        Assert.EndsWith("hflip", withFx);
        Assert.StartsWith(baseline, withFx);
    }

    [Fact]
    public void VisualFilter_CatalogFxWithBadVersion_Throws()
    {
        var item = new TimelineItem("v1", "c", 0, 1) { CatalogFxVersion = 2, CatalogFx = new[] { Fx("horizontal_flip") } };
        Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item, 64, 32, 24));
    }

    [Fact]
    public void Build_V4MapsInOrder_WithInvariantNumbersAndKernelDefaults()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var archived = new[] { Fx("unsharp_mask", true, ("amount", 75), ("radius", 2.25), ("threshold", 0)),
                Fx("brightness_contrast", true, ("brightness", -25), ("contrast", 40)) };
            // AUDIT0: encoded-YUV eq/unsharp are structurally different from the RGB/linear-light preview.
            // Reject the old mixed chain rather than silently rendering an approximation or dropping an effect.
            Assert.Equal(VideoFxChain.UnsupportedId, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(
                new[] { Fx("sharpen", true, ("amount", 125)), archived[0], archived[1], Fx("tint") })).Message);
            foreach (var effect in archived) AssertArchivedV4Rejected(effect);
            var filter = VideoFxChain.Build(new[] { Fx("sharpen", true, ("amount", 125)), Fx("tint") });
            var sharpen = VideoFxChain.Build(new[] { Fx("sharpen", true, ("amount", 125)) });
            var tint = VideoFxChain.Build(new[] { Fx("tint") });
            Assert.Equal(sharpen + "," + tint, filter);
            Assert.Contains("0.2126*", tint);
            Assert.Contains("0.7152*", tint);
            Assert.Contains("0.0722*", tint);
            Assert.EndsWith(":a='alpha(X,Y)':interpolation=nearest", filter);
            AssertArchivedV4Rejected(Fx("unsharp_mask", true, ("radius", 6)));
            AssertArchivedV4Rejected(Fx("unsharp_mask"));
            Assert.Contains("clip(X+(-1),0,W-1)", VideoFxChain.Build(new[] { Fx("sharpen") }));
            Assert.DoesNotContain("unsharp=", sharpen);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Build_V4NoOpsAndDisabledEffects_RejectArchivedIdsAndKeepSharpenZeroLutRoundTrip()
    {
        Assert.Equal(VideoFxChain.Build(new[] { Fx("sharpen", true, ("amount", 0)) }), VideoFxChain.Build(new[] { Fx("sharpen", true, ("amount", 0)),
            Fx("tint", true, ("amount", 0)), Fx("tint", false), Fx("sharpen", false) }));
        foreach (var effect in new[] { Fx("unsharp_mask", true, ("radius", .3)), Fx("unsharp_mask", true, ("amount", 0), ("radius", 1)),
            Fx("brightness_contrast"), Fx("brightness_contrast", true, ("brightness", 0), ("contrast", 0)) })
            AssertArchivedV4Rejected(effect);
    }

    [Theory]
    [InlineData("sharpen", "amount", 500.01)]
    [InlineData("sharpen", "amount", -0.01)]
    [InlineData("sharpen", "amount", double.NaN)]
    [InlineData("unsharp_mask", "amount", double.PositiveInfinity)]
    [InlineData("unsharp_mask", "radius", 6.01)]
    [InlineData("unsharp_mask", "radius", -0.01)]
    [InlineData("unsharp_mask", "threshold", 0.01)]
    [InlineData("brightness_contrast", "brightness", 100.01)]
    [InlineData("brightness_contrast", "contrast", -100.01)]
    [InlineData("tint", "amount", 100.01)]
    [InlineData("tint", "black_r", -0.01)]
    [InlineData("tint", "white_b", 1.01)]
    public void Build_V4InvalidNumericParams_ThrowEvenWhenDisabled(string id, string key, double value)
    {
        if (id is "unsharp_mask" or "brightness_contrast")
        {
            AssertArchivedV4Rejected(Fx(id, true, (key, value)));
            return;
        }
        foreach (var enabled in new[] { true, false })
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(
                () => VideoFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Theory]
    [InlineData("sharpen", "radius")]
    [InlineData("unsharp_mask", "legacy")]
    [InlineData("brightness_contrast", "gamma")]
    [InlineData("tint", "black")]
    public void Build_V4UnknownParam_IsNotSilentlyDiscarded(string id, string key)
    {
        if (id is "unsharp_mask" or "brightness_contrast")
        {
            AssertArchivedV4Rejected(Fx(id, true, (key, 1)));
            return;
        }
        Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(
            () => VideoFxChain.Build(new[] { Fx(id, true, (key, 1)) })).Message);
    }

    [Fact]
    public void Build_V4InclusiveBounds_RejectArchivedAndAcceptTintOffsetsAndInvertedEndpoints()
    {
        VideoFxChain.Validate(new[] { Fx("sharpen", true, ("amount", 500)), Fx("tint", true, ("amount", 100), ("black_r", 0), ("white_b", 1)) });
        foreach (var effect in new[] { Fx("unsharp_mask", true, ("radius", 6), ("amount", 500), ("threshold", 0)),
            Fx("unsharp_mask", true, ("radius", 0), ("amount", 0), ("threshold", 0)),
            Fx("brightness_contrast", true, ("brightness", -100), ("contrast", 100)), Fx("brightness_contrast", true, ("brightness", 100), ("contrast", -100)) })
            AssertArchivedV4Rejected(effect);
        var offset = VideoFxChain.Build(new[] { Fx("tint", true, ("black_r", .25), ("white_r", .75)) });
        Assert.DoesNotContain("colorchannelmixer=", offset);
        Assert.Contains("255*(0.25+(0.5)*ld(1))", offset);
        Assert.Contains("floor(clip(", offset);
        var inverted = VideoFxChain.Build(new[] { Fx("tint", true, ("black_r", 1), ("black_g", 1), ("black_b", 1), ("white_r", 0), ("white_g", 0), ("white_b", 0)) });
        Assert.DoesNotContain("colorchannelmixer", inverted);
        Assert.StartsWith("format=gbrap,geq=r='st(0,", inverted);
        Assert.Contains("255*(1+(-1)*ld(1))", inverted);
    }

    private static string CatalogProject(string effects)
        => "{\"start\":0,\"end\":1,\"timeline\":[{\"id\":\"v1\",\"clipId\":\"c\",\"start\":0,\"end\":1,\"track\":1,\"catalogFxVersion\":1,\"catalogFx\":" + effects + "}]}";

    [Fact]
    public void Codec_V4NumericContract_RoundTripsSupportedAndRejectsArchivedDtos()
    {
        foreach (var effect in new[] { Fx("unsharp_mask", true, ("amount", 75), ("radius", 2.25), ("threshold", 0)),
            Fx("brightness_contrast", true, ("brightness", -25), ("contrast", 40)) })
        foreach (var enabled in new[] { true, false })
        {
            var oldDto = JsonSerializer.Serialize(new[] { effect with { Enabled = enabled } });
            var error = Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(oldDto)));
            Assert.Equal(EditorProjectSchemaErrors.Invalid, error.Code);
            Assert.Equal(VideoFxChain.UnsupportedId, error.Message);
        }
        var effects = new[] { Fx("sharpen", true, ("amount", 125)),
            Fx("tint", true, ("amount", 60), ("black_r", 17d / 255), ("black_g", 34d / 255), ("black_b", 51d / 255), ("white_r", 1), ("white_g", .5), ("white_b", 0)) };
        var project = EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(effects)));
        var item = Assert.Single(project.Timeline!);
        Assert.Equal(1, item.CatalogFxVersion);
        Assert.Equal(VideoFxChain.Build(effects), VideoFxChain.Build(item.CatalogFx!));
        var serialized = JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = Assert.Single(EditorProjectCodec.Deserialize(serialized).Timeline!);
        Assert.Equal(1, restored.CatalogFxVersion);
        Assert.Equal(JsonSerializer.Serialize(effects), JsonSerializer.Serialize(restored.CatalogFx));
    }

    [Theory]
    [InlineData("sharpen", "\"amount\":501")]
    [InlineData("unsharp_mask", "\"radius\":1,\"threshold\":1")]
    [InlineData("unsharp_mask", "\"radius\":6.01")]
    [InlineData("unsharp_mask", "\"amount\":100")]
    [InlineData("brightness_contrast", "\"contrast\":-101")]
    [InlineData("tint", "\"black_r\":-0.01")]
    [InlineData("tint", "\"white_b\":1.01")]
    [InlineData("tint", "\"black\":\"#000\"")]
    [InlineData("tint", "\"black_r\":[0,0,0]")]
    [InlineData("tint", "\"amount\":10,\"amount\":20")]
    public void Codec_V4RejectsMalformedOrUnsupportedNumericContract(string id, string parameters)
    {
        foreach (var enabled in new[] { "true", "false" })
        {
            var effects = "[{\"id\":\"" + id + "\",\"enabled\":" + enabled + ",\"params\":{" + parameters + "}}]";
            var error = Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(effects)));
            Assert.Equal(EditorProjectSchemaErrors.Invalid, error.Code);
            if (id is "unsharp_mask" or "brightness_contrast") Assert.Equal(VideoFxChain.UnsupportedId, error.Message);
        }
    }

    [Theory]
    [InlineData("unsharp_mask", "{}")]
    [InlineData("unsharp_mask", "{\"amount\":50,\"radius\":1,\"threshold\":0}")]
    [InlineData("unsharp_mask", "{\"amount\":0,\"radius\":0,\"threshold\":0}")]
    [InlineData("unsharp_mask", "{\"amount\":500,\"radius\":6,\"threshold\":0}")]
    [InlineData("brightness_contrast", "{}")]
    [InlineData("brightness_contrast", "{\"brightness\":0,\"contrast\":0}")]
    [InlineData("brightness_contrast", "{\"brightness\":-100,\"contrast\":100}")]
    [InlineData("brightness_contrast", "{\"brightness\":100,\"contrast\":-100}")]
    public void Codec_ArchivedV4Ids_RejectAllPersistedContainers(string id, string parameters)
    {
        foreach (var enabled in new[] { "true", "false" })
        foreach (var track in new[] { 1, 2, 3 })
        {
            var item = "{\"id\":\"v1\",\"clipId\":\"c\",\"start\":0,\"end\":1,\"track\":" + track
                + ",\"catalogFxVersion\":1,\"catalogFx\":[{\"id\":\"" + id + "\",\"enabled\":" + enabled + ",\"params\":" + parameters + "}]}";
            foreach (var json in new[] {
                "{\"start\":0,\"end\":1,\"timeline\":[" + item + "]}",
                "{\"start\":0,\"end\":1,\"timeline\":[],\"silenceTrimBackup\":[" + item + "]}",
                "{\"start\":0,\"end\":1,\"timeline\":[],\"nestedSequences\":{\"nested\":{\"timeline\":[" + item + "]}}}" })
            {
                var error = Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(json));
                Assert.Equal(EditorProjectSchemaErrors.Invalid, error.Code);
                Assert.Equal(VideoFxChain.UnsupportedId, error.Message);
            }
        }
    }

    private static string Ffmpeg()
    {
        var env = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }

    private static byte[] RenderGray(string filter)
    {
        var psi = new ProcessStartInfo(Ffmpeg()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-f", "lavfi", "-i", "nullsrc=s=64x32:r=24,geq=lum='X*4+Y':cb=128:cr=128", "-frames:v", "1", "-vf", filter + ",format=gray", "-f", "rawvideo", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        using var ms = new MemoryStream();
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(ms);
        Assert.True(p.WaitForExit(60000), "ffmpeg timed out");
        Assert.True(p.ExitCode == 0, "ffmpeg failed: " + err.Result);
        return ms.ToArray();
    }

    private static byte[] RenderPixels(string source, string filter, string pixelFormat, int bytesPerPixel)
    {
        var psi = new ProcessStartInfo(Ffmpeg()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-f", "lavfi", "-i", source, "-frames:v", "1", "-vf", filter + ",format=" + pixelFormat, "-f", "rawvideo", "-" })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        using var output = new MemoryStream();
        var error = process.StandardError.ReadToEndAsync();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ffmpeg timed out");
        }
        copy.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, "ffmpeg failed: " + error.GetAwaiter().GetResult());
        var pixels = output.ToArray();
        Assert.Equal(64 * 32 * bytesPerPixel, pixels.Length);
        return pixels;
    }

    [Theory]
    [InlineData("sharpen")]
    [InlineData("unsharp_mask")]
    [InlineData("brightness_contrast")]
    [InlineData("tint")]
    public void RealFfmpeg_V4VisualFilter_ChangesSupportedPixelsAndRejectsArchivedBeforeRender(string id)
    {
        const string source = "nullsrc=s=64x32:r=24,geq=lum='64+24*sin(X*0.7)+12*cos(Y*0.9)':cb=100:cr=150";
        var effect = id switch
        {
            "sharpen" => Fx(id, true, ("amount", 150)),
            "unsharp_mask" => Fx(id, true, ("amount", 75), ("radius", 2.25), ("threshold", 0)),
            "brightness_contrast" => Fx(id, true, ("brightness", 25), ("contrast", 25)),
            "tint" => Fx(id, true, ("white_r", 1), ("white_g", 0), ("white_b", 0), ("amount", 100)),
            _ => throw new ArgumentException(id),
        };
        if (id is "unsharp_mask" or "brightness_contrast")
        {
            // The previous changed-byte count did not prove preview parity; no render may start now.
            AssertArchivedV4Rejected(effect);
            return;
        }
        var item = new TimelineItem("v1", "c", 0, 1);
        var none = RenderPixels(source, EditorRenderService.VisualFilter(item, 64, 32, 24), "rgb24", 3);
        var rendered = RenderPixels(source, EditorRenderService.VisualFilter(item with { CatalogFxVersion = 1, CatalogFx = new[] { effect } }, 64, 32, 24), "rgb24", 3);
        Assert.True(none.Where((value, index) => value != rendered[index]).Count() > 64, id + " must change real output pixels");
        if (id == "tint")
        {
            var red = 0L; var other = 0L;
            for (var i = 0; i < rendered.Length; i += 3) { red += rendered[i]; other += rendered[i + 1] + rendered[i + 2]; }
            Assert.True(red > 4 * (other + 1), "full red tint must alter hue, not only luma");
        }
    }

    [Theory]
    [InlineData(0.31)]
    [InlineData(6)]
    public void VisualFilter_V4UnsharpFormerRadiusBounds_RejectBeforeRender(double radius)
    {
        AssertArchivedV4Rejected(Fx("unsharp_mask", true, ("radius", radius), ("amount", 500), ("threshold", 0)));
    }

    [Fact]
    public void RealFfmpeg_V4Tint_PreservesAlphaAndAppliesBlackOffsetBeforeInvertedSlopeClipping()
    {
        const string source = "nullsrc=s=64x32:r=24,format=gbrap,geq=r='X*3':g='Y*7':b='40':a='64+X*2'";
        var none = RenderPixels(source, "null", "rgba", 4);
        var inverted = RenderPixels(source, VideoFxChain.Build(new[] { Fx("tint", true, ("black_r", 1), ("black_g", 1), ("black_b", 1), ("white_r", 0), ("white_g", 0), ("white_b", 0)) }), "rgba", 4);
        var offset = RenderPixels(source, VideoFxChain.Build(new[] { Fx("tint", true, ("black_r", .25), ("black_g", .25), ("black_b", .25), ("white_r", .75), ("white_g", .75), ("white_b", .75)) }), "rgba", 4);
        var expectedInverted = R72Preview(none, new[] { Fx("tint") }, new[] { "{\"black\":[1,1,1],\"white\":[0,0,0]}" });
        var expectedOffset = R72Preview(none, new[] { Fx("tint") }, new[] { "{\"black\":[0.25,0.25,0.25],\"white\":[0.75,0.75,0.75]}" });
        for (var i = 0; i < none.Length; i += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                Assert.InRange(Math.Abs(inverted[i + channel] - expectedInverted[i + channel]), 0, 1);
                Assert.InRange(Math.Abs(offset[i + channel] - expectedOffset[i + channel]), 0, 1);
            }
            Assert.Equal(none[i + 3], inverted[i + 3]);
            Assert.Equal(none[i + 3], offset[i + 3]);
        }
    }

    [Fact]
    public void Build_R71DefaultsNoOpsAndR70SemanticsRemainDistinct()
    {
        Assert.Equal("", VideoFxChain.Build(new[] { Fx("gamma_correction", true, ("gamma", 10)), Fx("invert", true, ("blend", 100)),
            Fx("proc_amp", true, ("contrast", 100)), Fx("vignette", true, ("amount", 0)), Fx("black_white", false) }));
        Assert.Contains("pow(val/255,10)", VideoFxChain.Build(new[] { Fx("gamma_correction") }));
        Assert.Contains("((val/255-0.5)*0+0.5+0)*255", VideoFxChain.Build(new[] { Fx("proc_amp") }));
        AssertArchivedV4Rejected(Fx("brightness_contrast", true, ("brightness", 25), ("contrast", 40)));
        Assert.StartsWith("format=gbrap,geq=r='st(0,", VideoFxChain.Build(new[] { Fx("tint") }));
        Assert.Contains("*4096)/4096", VideoFxChain.Build(new[] { Fx("tint") }));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var effects = new[] { Fx("invert"), Fx("gamma_correction", true, ("gamma", 6.5)), Fx("black_white") };
            var combined = VideoFxChain.Build(effects);
            Assert.Equal(string.Join(',', effects.Select(effect => VideoFxChain.Build(new[] { effect }))), combined);
            Assert.Contains("pow(val/255,0.65)", combined);
            Assert.EndsWith(combined, EditorRenderService.VisualFilter(new TimelineItem("v1", "c", 0, 1) { CatalogFxVersion = 1, CatalogFx = effects }, 64, 32, 24));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("proc_amp", "brightness", 100.01)]
    [InlineData("proc_amp", "contrast", -0.01)]
    [InlineData("proc_amp", "contrast", 200.01)]
    [InlineData("proc_amp", "hue", -360.01)]
    [InlineData("proc_amp", "saturation", 200.01)]
    [InlineData("invert", "channel", 4)]
    [InlineData("invert", "channel", 4.01)]
    [InlineData("invert", "channel", 1.5)]
    [InlineData("invert", "blend", -0.01)]
    [InlineData("gamma_correction", "gamma", -0.01)]
    [InlineData("gamma_correction", "gamma", 100.01)]
    [InlineData("gamma_correction", "gamma", double.NaN)]
    [InlineData("vignette", "amount", -100.01)]
    [InlineData("vignette", "midpoint", 100.01)]
    [InlineData("vignette", "roundness", 100.01)]
    [InlineData("vignette", "feather", -0.01)]
    [InlineData("vignette", "color_r", 1.01)]
    [InlineData("channel_mix", "rr", 200.01)]
    [InlineData("channel_mix", "rc", -200.01)]
    [InlineData("channel_mix", "monochrome", 0.5)]
    [InlineData("color_pass", "similarity", 100.01)]
    [InlineData("color_pass", "color_b", double.PositiveInfinity)]
    [InlineData("color_pass", "reverse", 0.5)]
    public void Build_R71InvalidParametersRejectEvenDisabled(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Theory]
    [InlineData("black_white", "amount")]
    [InlineData("proc_amp", "gamma")]
    [InlineData("invert", "mode")]
    [InlineData("gamma_correction", "red")]
    [InlineData("vignette", "color")]
    [InlineData("channel_mix", "alpha")]
    [InlineData("color_pass", "color")]
    public void Build_R71UnknownParametersAreNotDropped(string id, string key)
        => Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, true, (key, 1)) })).Message);

    public static IEnumerable<object[]> R71ColorCases()
    {
        yield return new object[] { "proc-default", Fx("proc_amp"), "{}" };
        yield return new object[] { "proc-hsl", Fx("proc_amp", true, ("brightness", 25), ("contrast", 150), ("hue", 70), ("saturation", 150)), "{\"brightness\":25,\"contrast\":150,\"hue\":70,\"saturation\":150}" };
        yield return new object[] { "proc-desaturate", Fx("proc_amp", true, ("brightness", -35), ("contrast", 65), ("hue", -280), ("saturation", 0)), "{\"brightness\":-35,\"contrast\":65,\"hue\":-280,\"saturation\":0}" };
        yield return new object[] { "black-white", Fx("black_white"), "{}" };
        yield return new object[] { "invert-rgb", Fx("invert"), "{}" };
        yield return new object[] { "invert-green-blend", Fx("invert", true, ("channel", 2), ("blend", 27)), "{\"channel\":\"Green\",\"blend\":27}" };
        yield return new object[] { "gamma-default", Fx("gamma_correction"), "{}" };
        yield return new object[] { "gamma-zero", Fx("gamma_correction", true, ("gamma", 0)), "{\"gamma\":0}" };
        yield return new object[] { "gamma-fraction", Fx("gamma_correction", true, ("gamma", 6.5)), "{\"gamma\":6.5}" };
        yield return new object[] { "vignette-default", Fx("vignette"), "{}" };
        yield return new object[] { "vignette-color-square", Fx("vignette", true, ("amount", -80), ("midpoint", 30), ("roundness", -60), ("feather", 25), ("color_r", .2), ("color_g", .4), ("color_b", .8)), "{\"amount\":-80,\"midpoint\":30,\"roundness\":-60,\"feather\":25,\"color\":[0.2,0.4,0.8]}" };
        yield return new object[] { "vignette-white-round", Fx("vignette", true, ("amount", 60), ("roundness", 80), ("feather", 0)), "{\"amount\":60,\"roundness\":80,\"feather\":0}" };
        yield return new object[] { "mix-negative", Fx("channel_mix", true, ("rr", 35), ("rg", 65), ("bb", -100)), "{\"rr\":35,\"rg\":65,\"bb\":-100}" };
        yield return new object[] { "mix-offset-before-clip", Fx("channel_mix", true, ("rr", -100), ("rc", 100), ("gg", 50), ("gc", 25), ("bb", 0), ("bg", 100)), "{\"rr\":-100,\"rc\":100,\"gg\":50,\"gc\":25,\"bb\":0,\"bg\":100}" };
        yield return new object[] { "mix-monochrome", Fx("channel_mix", true, ("rr", 30), ("rg", 60), ("rb", 10), ("rc", 10), ("monochrome", 1)), "{\"rr\":30,\"rg\":60,\"rb\":10,\"rc\":10,\"monochrome\":true}" };
        yield return new object[] { "pass-default", Fx("color_pass"), "{}" };
        yield return new object[] { "pass-reverse", Fx("color_pass", true, ("color_r", 1), ("color_g", 0), ("color_b", 0), ("similarity", 60), ("reverse", 1)), "{\"color\":[1,0,0],\"similarity\":60,\"reverse\":true}" };
        yield return new object[] { "proc-bounds", Fx("proc_amp", true, ("brightness", -100), ("contrast", 200), ("hue", -360), ("saturation", 200)), "{\"brightness\":-100,\"contrast\":200,\"hue\":-360,\"saturation\":200}" };
        yield return new object[] { "vignette-bounds", Fx("vignette", true, ("amount", -100), ("midpoint", 0), ("roundness", -100), ("feather", 100)), "{\"amount\":-100,\"midpoint\":0,\"roundness\":-100,\"feather\":100}" };
        yield return new object[] { "mix-bounds", Fx("channel_mix", true, ("rr", -200), ("rg", 200), ("rc", -200)), "{\"rr\":-200,\"rg\":200,\"rc\":-200}" };
        yield return new object[] { "pass-boundary", Fx("color_pass", true, ("similarity", 0)), "{\"similarity\":0}" };
    }

    [Theory]
    [MemberData(nameof(R71ColorCases))]
    public void Codec_R71NumericContractRoundTrips(string name, CatalogFxInstance effect, string previewParams)
    {
        var json = JsonSerializer.Serialize(new[] { effect });
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion);
        Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        Assert.Equal(VideoFxChain.Build(new[] { effect }), VideoFxChain.Build(item.CatalogFx!));
    }

    [Theory]
    [InlineData("invert", "\"channel\":1.5")]
    [InlineData("channel_mix", "\"monochrome\":true")]
    [InlineData("color_pass", "\"reverse\":0.5")]
    [InlineData("vignette", "\"color\":\"#fff\"")]
    [InlineData("gamma_correction", "\"gamma\":101")]
    public void Codec_R71MalformedContractFailsClosed(string id, string parameters)
    {
        var json = "[{\"id\":\"" + id + "\",\"enabled\":false,\"params\":{" + parameters + "}}]";
        Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(json)));
    }

    private static byte[] R71Sample()
    {
        var data = new byte[64 * 32 * 4];
        for (var y = 0; y < 32; y++) for (var x = 0; x < 64; x++)
        {
            var i = (y * 64 + x) * 4;
            data[i] = (byte)((x * 7 + y * 19) & 255); data[i + 1] = (byte)((y * 13 + x * 3) & 255);
            data[i + 2] = (byte)((x * 17 + y * 11) & 255); data[i + 3] = (byte)((x * 5 + y * 7) & 255);
        }
        new byte[] { 0, 0, 255, 128, 0, 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255 }.CopyTo(data, 0);
        return data;
    }

    private static byte[] R71Process(string executable, string[] args, byte[] input)
    {
        var psi = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(executable + " did not start");
        using var output = new MemoryStream();
        var error = process.StandardError.ReadToEndAsync();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var inputCopy = Task.Run(async () => { await process.StandardInput.BaseStream.WriteAsync(input); process.StandardInput.Close(); });
        if (!process.WaitForExit(60000)) { process.Kill(entireProcessTree: true); throw new TimeoutException(executable + " timed out"); }
        Task.WhenAll(inputCopy, copy).GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, executable + " failed: " + error.GetAwaiter().GetResult());
        return output.ToArray();
    }

    private static byte[] R71Preview(byte[] input, string id, string parameters)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "filmcraft-preview", "wwwroot", "js", "fx-engine.js"))) directory = directory.Parent;
        Assert.NotNull(directory);
        const string script = "const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');const p=JSON.parse(fs.readFileSync(0,'utf8'));const c=vm.createContext({window:{},console,Uint8ClampedArray,Float32Array});for(const name of ['fx-engine.js','fx-more-a.js']){vm.runInContext(fs.readFileSync(path.join(p.root,'filmcraft-preview','wwwroot','js',name),'utf8'),c);c.FX=c.window.FX;}const pixels=new Uint8ClampedArray(Buffer.from(p.input,'base64'));c.FX.apply(pixels,64,32,p.id,p.params);process.stdout.write(Buffer.from(pixels));";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { root = directory!.FullName, input = Convert.ToBase64String(input), id, @params = JsonSerializer.Deserialize<JsonElement>(parameters) });
        var node = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_NODE") ?? "node";
        return R71Process(node, new[] { "-e", script }, payload);
    }

    [Theory]
    [MemberData(nameof(R71ColorCases))]
    public void RealFfmpeg_R71MatchesActualPreviewWithinOneCodeValue(string name, CatalogFxInstance effect, string previewParams)
    {
        var input = R71Sample();
        var expected = R71Preview(input, effect.Id, previewParams);
        var filter = VideoFxChain.Build(new[] { effect });
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-frames:v", "1", "-vf", filter + ",format=rgba", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        Assert.False(input.SequenceEqual(expected), name + " fixture must exercise a visible preview change");
        var maxError = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else maxError = Math.Max(maxError, Math.Abs(expected[i] - actual[i]));
        }
        Assert.True(maxError <= 1, name + " max RGB error=" + maxError);
    }

    [Fact]
    public void RealFfmpeg_R71ColorChainMaintainsOrderAndCumulativePreviewAccuracy()
    {
        var input = R71Sample();
        var effects = new[] { Fx("invert", true, ("channel", 1), ("blend", 27)), Fx("gamma_correction", true, ("gamma", 6.5)), Fx("black_white"), Fx("vignette") };
        var expected = R71Preview(R71Preview(R71Preview(R71Preview(input, "invert", "{\"channel\":\"Red\",\"blend\":27}"), "gamma_correction", "{\"gamma\":6.5}"), "black_white", "{}"), "vignette", "{}");
        var filter = VideoFxChain.Build(effects);
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-frames:v", "1", "-vf", filter + ",format=rgba", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(input.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, 2);
    }

    public static IEnumerable<object[]> R71V2GeometryCases()
    {
        yield return new object[] { "defaults", Fx("transform"), "{}" };
        yield return new object[] { "anisotropic-rotate-translate", Fx("transform", true, ("scale_x", 125), ("scale_y", 75), ("rotation", 23), ("position_x", 36.25), ("position_y", 14.5)), "{\"uniform_scale\":false,\"scale_width\":125,\"scale_height\":75,\"rotation\":23,\"position\":[36.25,14.5]}" };
        yield return new object[] { "off-center", Fx("transform", true, ("anchor_x", 10), ("anchor_y", 5), ("position_x", 35), ("position_y", 17), ("scale_x", 50), ("scale_y", 150), ("rotation", -35)), "{\"anchor\":[10,5],\"position\":[35,17],\"uniform_scale\":false,\"scale_width\":50,\"scale_height\":150,\"rotation\":-35}" };
        yield return new object[] { "integer-translation", Fx("transform", true, ("position_x", 36), ("position_y", 18)), "{\"position\":[36,18]}" };
        yield return new object[] { "scale-lower-bound", Fx("transform", true, ("scale_x", 1), ("scale_y", 1000)), "{\"uniform_scale\":false,\"scale_width\":1,\"scale_height\":1000}" };
        yield return new object[] { "offset-default-blend", Fx("offset", true, ("shift_x", 33), ("shift_y", 16)), "{\"shift\":[33,16]}" };
        yield return new object[] { "offset-fractional-seam-blend", Fx("offset", true, ("shift_x", 33.5), ("shift_y", 15.25), ("blend", 25)), "{\"shift\":[33.5,15.25],\"blend\":25}" };
        yield return new object[] { "offset-negative-multitile", Fx("offset", true, ("shift_x", -96.25), ("shift_y", 81.5), ("blend", 0)), "{\"shift\":[-96.25,81.5],\"blend\":0}" };
        yield return new object[] { "offset-full-original", Fx("offset", true, ("shift_x", 0), ("shift_y", 0), ("blend", 100)), "{\"shift\":[0,0],\"blend\":100}" };
        yield return new object[] { "rotate-90", Fx("rotate_3d", true, ("rot_z", 90)), "{\"rot_z\":90}" };
        yield return new object[] { "rotate-fraction", Fx("rotate_3d", true, ("rot_z", -37)), "{\"rot_z\":-37,\"perspective\":0,\"hide_back\":true}" };
    }

    private static byte[] R71V2GeometryPreview(byte[] input, string id, string parameters)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "filmcraft-preview", "wwwroot", "js", "fx-more-b.js"))) directory = directory.Parent;
        Assert.NotNull(directory);
        const string script = "const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');const p=JSON.parse(fs.readFileSync(0,'utf8'));const c=vm.createContext({window:{},console,Uint8ClampedArray,Float32Array});for(const name of ['fx-engine.js','fx-more-b.js']){vm.runInContext(fs.readFileSync(path.join(p.root,'filmcraft-preview','wwwroot','js',name),'utf8'),c);c.FX=c.window.FX;}if(c.FX.status('rotate_3d')!=='real')throw Error('geometry overrides not loaded');const pixels=new Uint8ClampedArray(Buffer.from(p.input,'base64'));c.FX.apply(pixels,64,32,p.id,p.params);process.stdout.write(Buffer.from(pixels));";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { root = directory!.FullName, input = Convert.ToBase64String(input), id, @params = JsonSerializer.Deserialize<JsonElement>(parameters) });
        return R71Process(Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_NODE") ?? "node", new[] { "-e", script }, payload);
    }

    [Theory]
    [MemberData(nameof(R71V2GeometryCases))]
    public void RealFfmpeg_R71V2GeometryMatchesFinalPreviewWithFixedCanvas(string name, CatalogFxInstance effect, string previewParams)
    {
        var input = R71Sample();
        var expected = R71V2GeometryPreview(input, effect.Id, previewParams);
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-framerate", "24", "-i", "pipe:0", "-frames:v", "1", "-vf", VideoFxChain.Build(new[] { effect }) + ",format=rgba", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1, name + " channel=" + i + " preview=" + expected[i] + " exported=" + actual[i]);
    }

    [Fact]
    public void RealFfmpeg_R71V2GeometryChainKeepsOrderFrameCountAndTimestamps()
    {
        var input = R71Sample();
        var effects = new[] { Fx("transform", true, ("position_x", 35), ("position_y", 18), ("rotation", 20)), Fx("offset", true, ("shift_x", 33.5), ("shift_y", 15.25), ("blend", 25)), Fx("rotate_3d", true, ("rot_z", -90)) };
        var expected = R71V2GeometryPreview(R71V2GeometryPreview(R71V2GeometryPreview(input, "transform", "{\"position\":[35,18],\"rotation\":20}"), "offset", "{\"shift\":[33.5,15.25],\"blend\":25}"), "rotate_3d", "{\"rot_z\":-90}");
        var filter = VideoFxChain.Build(effects);
        Assert.Equal(string.Join(',', effects.Select(effect => VideoFxChain.Build(new[] { effect }))), filter);
        Assert.DoesNotContain("setpts", filter); Assert.DoesNotContain("fps=", filter);
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-framerate", "24", "-i", "pipe:0", "-vf", filter + ",format=rgba", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, 2);
        var frames = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x32:r=24:duration=0.25", "-vf", filter + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 64x32");
        Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(value => value.Trim()).ToArray()).ToArray();
        Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][1]); Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][2]);
            Assert.Equal("1", rows[i][3]); Assert.Equal((64 * 32 * 4).ToString(CultureInfo.InvariantCulture), rows[i][4]);
        }
        var item = new TimelineItem("v1", "c", 0, 1) { CatalogFxVersion = 1, CatalogFx = effects };
        Assert.EndsWith(filter + ",premultiply=inplace=1,format=rgb24", EditorRenderService.VisualFilter(item, 64, 32, 24));
        Assert.Equal(64 * 32 * 3, RenderPixels("testsrc2=s=64x32:r=24", EditorRenderService.VisualFilter(item, 64, 32, 24), "rgb24", 3).Length);
    }

    [Fact]
    public void RealFfmpeg_R71V2VisualFilterMattesAfterFullGeometryChain()
    {
        var input = R71Sample();
        var baseItem = new TimelineItem("v1", "c", 0, 1) { Track = 1 };
        byte[] Run(string filter, string pixelFormat) => R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-framerate", "24", "-i", "pipe:0", "-frames:v", "1", "-vf", filter + ",format=" + pixelFormat, "-f", "rawvideo", "pipe:1" }, input);
        var baseline = Run(EditorRenderService.VisualFilter(baseItem, 64, 32, 24), "rgba");
        var expected = R71V2GeometryPreview(R71V2GeometryPreview(R71V2GeometryPreview(baseline, "transform", "{\"position\":[35,18],\"rotation\":20}"), "offset", "{\"shift\":[33.5,15.25],\"blend\":25}"), "rotate_3d", "{\"rot_z\":-90}");
        var effects = new[] { Fx("transform", true, ("position_x", 35), ("position_y", 18), ("rotation", 20)), Fx("offset", true, ("shift_x", 33.5), ("shift_y", 15.25), ("blend", 25)), Fx("rotate_3d", true, ("rot_z", -90)) };
        var actual = Run(EditorRenderService.VisualFilter(baseItem with { CatalogFxVersion = 1, CatalogFx = effects }, 64, 32, 24), "rgb24");
        Assert.Equal(64 * 32 * 4, expected.Length); Assert.Equal(64 * 32 * 3, actual.Length);
        for (var pixel = 0; pixel < 64 * 32; pixel++)
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange(Math.Abs(actual[pixel * 3 + channel] - Math.Round(expected[pixel * 4 + channel] * expected[pixel * 4 + 3] / 255d)), 0, 1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void VisualFilter_R71V2OverlayRejectsAlphaHolesButAllowsWrappedOffset(int track)
    {
        var item = new TimelineItem("overlay", "c", 0, 1) { Track = track, CatalogFxVersion = 1 };
        foreach (var id in new[] { "transform", "rotate_3d" })
        {
            Assert.Equal("catalog-fx-alpha-overlay-unsupported", Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item with { CatalogFx = new[] { Fx(id) } }, 64, 32, 24)).Message);
            Assert.DoesNotContain("premultiply", EditorRenderService.VisualFilter(item with { CatalogFx = new[] { Fx(id, false) } }, 64, 32, 24));
        }
        var offset = new[] { Fx("offset", true, ("shift_x", 33), ("shift_y", 16)) };
        Assert.EndsWith(VideoFxChain.Build(offset), EditorRenderService.VisualFilter(item with { CatalogFx = offset }, 64, 32, 24));
        Assert.DoesNotContain("premultiply", EditorRenderService.VisualFilter(item with { CatalogFx = offset }, 64, 32, 24));
    }

    [Theory]
    [InlineData("transform", "scale_x", 0)]
    [InlineData("transform", "scale_y", 1001)]
    [InlineData("transform", "rotation", 360.01)]
    [InlineData("transform", "anchor_x", double.NaN)]
    [InlineData("offset", "blend", -0.01)]
    [InlineData("offset", "blend", 100.01)]
    [InlineData("offset", "shift_x", double.PositiveInfinity)]
    [InlineData("rotate_3d", "rot_z", -360.01)]
    public void Build_R71V2GeometryInvalidNumbersRejectEvenDisabled(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Theory]
    [InlineData("transform", "skew")]
    [InlineData("transform", "opacity")]
    [InlineData("transform", "shutter_override")]
    [InlineData("offset", "wrap")]
    [InlineData("rotate_3d", "rot_x")]
    [InlineData("rotate_3d", "rot_y")]
    [InlineData("rotate_3d", "z")]
    public void Build_R71V2UnsupportedGeometryParamsReject(string id, string key)
        => Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, true, (key, 0)) })).Message);

    [Theory]
    [InlineData("transform", "anchor_x")]
    [InlineData("transform", "position_y")]
    [InlineData("offset", "shift_x")]
    public void Codec_R71V2GeometryPartialPointsRejectEvenDisabled(string id, string key)
    {
        var effects = "[{\"id\":\"" + id + "\",\"enabled\":false,\"params\":{\"" + key + "\":1}}]";
        Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(effects)));
    }

    [Theory]
    [MemberData(nameof(R71V2GeometryCases))]
    public void Codec_R71V2GeometryNumericContractRoundTrips(string name, CatalogFxInstance effect, string previewParams)
    {
        var effects = JsonSerializer.Serialize(new[] { effect });
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(effects)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(effects, JsonSerializer.Serialize(item.CatalogFx));
        Assert.Equal(VideoFxChain.Build(new[] { effect }), VideoFxChain.Build(item.CatalogFx!));
        var invariantFilter = VideoFxChain.Build(new[] { effect });
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(invariantFilter, VideoFxChain.Build(new[] { effect })); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
    }

    public static IEnumerable<object[]> R72V5Cases()
    {
        yield return new object[] { "directional-fractional", Fx("directional_blur", true, ("length", 2.7), ("direction", 33)), "{\"length\":2.7,\"direction\":33}" };
        yield return new object[] { "directional-boundary", Fx("directional_blur", true, ("length", 8), ("direction", -72)), "{\"length\":8,\"direction\":-72}" };
        yield return new object[] { "channel-four-planes", Fx("channel_blur", true, ("red", 2), ("green", 1), ("blue", 3), ("alpha", 2), ("repeat_edge", 1)), "{\"red\":2,\"green\":1,\"blue\":3,\"alpha\":2,\"repeat_edge\":true}" };
        yield return new object[] { "channel-zero-edge", Fx("channel_blur", true, ("red", 8), ("green", .1), ("blue", 4), ("dimensions", 1)), "{\"red\":8,\"green\":0.1,\"blue\":4,\"dimensions\":\"Horizontal\",\"repeat_edge\":false}" };
        yield return new object[] { "flicker", Fx("reduce_interlace_flicker", true, ("softness", 8)), "{\"softness\":8}" };
        yield return new object[] { "extract-soft", Fx("extract", true, ("black", 60), ("white", 200), ("softness", 25)), "{\"black\":60,\"white\":200,\"softness\":25}" };
        yield return new object[] { "extract-invert", Fx("extract", true, ("black", 90), ("white", 140), ("softness", 80), ("invert", 1)), "{\"black\":90,\"white\":140,\"softness\":80,\"invert\":true}" };
        yield return new object[] { "extract-strict-ends", Fx("extract"), "{}" };
        yield return new object[] { "cdl", Fx("asc_cdl", true, ("r_slope", 1.2), ("g_slope", .7), ("b_slope", 1.4), ("r_offset", -.12), ("g_offset", .15), ("b_offset", -.2), ("r_power", .7), ("g_power", 1.3), ("b_power", 1.1), ("saturation", 1.4)), "{\"r_slope\":1.2,\"g_slope\":0.7,\"b_slope\":1.4,\"r_offset\":-0.12,\"g_offset\":0.15,\"b_offset\":-0.2,\"r_power\":0.7,\"g_power\":1.3,\"b_power\":1.1,\"saturation\":1.4}" };
        foreach (var axis in new[] { 0, 1, 2, 3 })
            yield return new object[] { "limiter-axis-" + axis, Fx("video_limiter", true, ("clip_level", 90), ("compression", 3), ("axis", axis)), "{\"clip_level\":90,\"compression\":3,\"axis\":" + axis + "}" };
        yield return new object[] { "limiter-warning", Fx("video_limiter", true, ("clip_level", 90), ("axis", 3), ("gamut_warning", 1), ("warning_r", .1), ("warning_g", .2), ("warning_b", .8)), "{\"clip_level\":\"90 IRE\",\"axis\":\"RGB\",\"gamut_warning\":true,\"warning_color\":[0.1,0.2,0.8]}" };
        foreach (var solid in new[] { false, true })
            yield return new object[] { "replace-solid-" + solid, Fx("color_replace", true, ("target_r", 0), ("target_g", 0), ("target_b", 1), ("replace_r", .2), ("replace_g", .9), ("replace_b", .3), ("similarity", 85), ("solid", solid ? 1 : 0)), "{\"target\":[0,0,1],\"replace\":[0.2,0.9,0.3],\"similarity\":85,\"solid\":" + (solid ? "true" : "false") + "}" };
        yield return new object[] { "crop-border", Fx("rounded_crop", true, ("left", 10), ("right", 15), ("top", 5), ("bottom", 10), ("radius", 4), ("feather", 2), ("border", 2), ("border_r", .2), ("border_g", .8), ("border_b", .3)), "{\"left\":10,\"right\":15,\"top\":5,\"bottom\":10,\"radius\":4,\"feather\":2,\"border\":2,\"border_color\":[0.2,0.8,0.3]}" };
        yield return new object[] { "crop-empty", Fx("rounded_crop", true, ("left", 60), ("right", 50)), "{\"left\":60,\"right\":50}" };
        yield return new object[] { "levels", Fx("levels", true, ("in_black", 16), ("in_white", 230), ("out_black", 10), ("out_white", 245), ("gamma", 135)), "{\"in_black\":16,\"in_white\":230,\"out_black\":10,\"out_white\":245,\"gamma\":135}" };
        yield return new object[] { "posterize", Fx("posterize", true, ("levels", 7)), "{\"levels\":7}" };
        yield return new object[] { "balance-preserve", Fx("color_balance", true, ("shadow_r", 80), ("shadow_g", -50), ("mid_g", 50), ("hi_b", -70), ("preserve", 1)), "{\"shadow_r\":80,\"shadow_g\":-50,\"mid_g\":50,\"hi_b\":-70,\"preserve\":true}" };
        foreach (var conversion in new[] { 0, 1, 2 })
            yield return new object[] { "cineon-conversion-" + conversion, Fx("cineon_converter", true, ("conversion", conversion)), "{\"conversion\":" + conversion + "}" };
    }

    private static byte[] R72Preview(byte[] input, IReadOnlyList<CatalogFxInstance> effects, IReadOnlyList<string> parameters)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "filmcraft-preview", "wwwroot", "js", "fx-engine.js"))) directory = directory.Parent;
        Assert.NotNull(directory);
        const string script = "const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');const p=JSON.parse(fs.readFileSync(0,'utf8'));const c=vm.createContext({window:{},console,Uint8ClampedArray,Float32Array});for(const name of ['fx-engine.js','fx-more-a.js','fx-more-b.js']){vm.runInContext(fs.readFileSync(path.join(p.root,'filmcraft-preview','wwwroot','js',name),'utf8'),c);c.FX=c.window.FX;}const pixels=new Uint8ClampedArray(Buffer.from(p.input,'base64'));for(let i=0;i<p.ids.length;i++){if(c.FX.status(p.ids[i])!=='real')throw Error('preview registration missing '+p.ids[i]);c.FX.apply(pixels,64,32,p.ids[i],p.params[i],{seconds:0,clipId:'r72-real-preview-'+i});}process.stdout.write(Buffer.from(pixels));";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { root = directory!.FullName, input = Convert.ToBase64String(input), ids = effects.Select(effect => effect.Id), @params = parameters.Select(value => JsonSerializer.Deserialize<JsonElement>(value)) });
        return R71Process(Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_NODE") ?? "node", new[] { "-e", script }, payload);
    }

    [Theory]
    [MemberData(nameof(R72V5Cases))]
    public void RealFfmpeg_R72V5MatchesFinalActualPreviewWithinOneByte(string name, CatalogFxInstance effect, string previewParams)
    {
        var input = R71Sample();
        var expected = R72Preview(input, new[] { effect }, new[] { previewParams });
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(new[] { effect }) + ",format=rgba", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        Assert.False(input.SequenceEqual(expected), name + " must exercise a visible preview change");
        var max = 0;
        for (var i = 0; i < actual.Length; i++) max = Math.Max(max, Math.Abs(expected[i] - actual[i]));
        Assert.True(max <= 1, name + " max RGBA error=" + max);
    }

    [Theory]
    [MemberData(nameof(R72V5Cases))]
    public void Codec_R72V5NumericContractRoundTrips(string name, CatalogFxInstance effect, string previewParams)
    {
        var json = JsonSerializer.Serialize(new[] { effect });
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        Assert.Equal(VideoFxChain.Build(new[] { effect }), VideoFxChain.Build(item.CatalogFx!));
        var invariant = VideoFxChain.Build(new[] { effect }); var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(invariant, VideoFxChain.Build(new[] { effect })); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
    }

    [Theory]
    [InlineData("directional_blur", "length", 8.01)]
    [InlineData("channel_blur", "red", 8.01)]
    [InlineData("channel_blur", "dimensions", 1.5)]
    [InlineData("channel_blur", "repeat_edge", .5)]
    [InlineData("extract", "invert", .5)]
    [InlineData("asc_cdl", "r_power", -1)]
    [InlineData("video_limiter", "clip_level", 91)]
    [InlineData("video_limiter", "axis", 1.5)]
    [InlineData("video_limiter", "gamut_warning", .5)]
    [InlineData("color_replace", "replace_b", 1.1)]
    [InlineData("rounded_crop", "feather", double.PositiveInfinity)]
    [InlineData("levels", "gamma", 0)]
    [InlineData("posterize", "levels", double.NaN)]
    [InlineData("color_balance", "preserve", .5)]
    [InlineData("cineon_converter", "conversion", 1.5)]
    public void Build_R72V5RejectsInvalidValuesEvenDisabled(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Theory]
    [InlineData("rounded_crop", 0)]
    [InlineData("channel_blur", 2)]
    public void VisualFilter_R72V5AlphaChangesRejectOverlayAndMatteAtChainTail(string id, double alpha)
    {
        var effect = id == "channel_blur" ? Fx(id, true, ("red", 2), ("alpha", alpha)) : Fx(id, true, ("radius", 4));
        var effects = new[] { effect, Fx("levels", true, ("gamma", 125)) };
        var item = new TimelineItem("v1", "c", 0, 1) { CatalogFxVersion = 1, CatalogFx = effects };
        var filter = EditorRenderService.VisualFilter(item, 64, 32, 24);
        Assert.EndsWith(VideoFxChain.Build(effects) + ",premultiply=inplace=1,format=rgb24", filter);
        foreach (var track in new[] { 2, 3 })
            Assert.Equal("catalog-fx-alpha-overlay-unsupported", Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item with { Track = track }, 64, 32, 24)).Message);
        var input = R71Sample(); var preview = R72Preview(input, effects, new[] { id == "channel_blur" ? "{\"red\":2,\"alpha\":2}" : "{\"radius\":4}", "{\"gamma\":125}" });
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(effects) + ",premultiply=inplace=1,format=rgb24", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(64 * 32 * 3, actual.Length);
        for (var i = 0; i < actual.Length; i++)
            Assert.InRange(Math.Abs(actual[i] - Math.Round(preview[i / 3 * 4 + i % 3] * preview[i / 3 * 4 + 3] / 255d)), 0, 1);
    }

    [Fact]
    public void RealFfmpeg_R72V5OrderedMixedChainMatchesFinalPreview()
    {
        var input = R71Sample();
        var effects = new[] { Fx("rounded_crop", true, ("radius", 4), ("feather", 2)), Fx("levels", true, ("gamma", 125)), Fx("asc_cdl", true, ("r_slope", 1.1), ("b_offset", .05), ("saturation", .8)), Fx("video_limiter", true, ("clip_level", 95), ("axis", 3), ("compression", 2)) };
        var preview = R72Preview(input, effects, new[] { "{\"radius\":4,\"feather\":2}", "{\"gamma\":125}", "{\"r_slope\":1.1,\"b_offset\":0.05,\"saturation\":0.8}", "{\"clip_level\":95,\"axis\":3,\"compression\":2}" });
        var filter = VideoFxChain.Build(effects);
        var reversed = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(effects.Reverse().ToArray()), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }, input);
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", filter, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }, input);
        Assert.Equal(input.Length, actual.Length); Assert.False(actual.SequenceEqual(reversed));
        for (var i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - preview[i]), 0, 1);
    }

    [Theory]
    [InlineData("directional_blur", "length", 3)]
    [InlineData("channel_blur", "red", 2)]
    [InlineData("reduce_interlace_flicker", "softness", 2)]
    [InlineData("extract", "softness", 20)]
    public void RealFfmpeg_R72V5FloatAndSamplingChainsKeepFrameCountAndTimestamps(string id, string key, double value)
    {
        var frames = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x32:r=24:duration=0.25", "-vf", VideoFxChain.Build(new[] { Fx(id, true, (key, value)) }) + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 64x32");
        Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(part => part.Trim()).ToArray()).ToArray();
        Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][1]); Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][2]);
            Assert.Equal("1", rows[i][3]); Assert.Equal((64 * 32 * 4).ToString(CultureInfo.InvariantCulture), rows[i][4]);
        }
    }

    [Theory]
    [InlineData("directional_blur")]
    [InlineData("channel_blur")]
    [InlineData("reduce_interlace_flicker")]
    [InlineData("extract")]
    [InlineData("asc_cdl")]
    [InlineData("video_limiter")]
    [InlineData("color_replace")]
    [InlineData("rounded_crop")]
    [InlineData("levels")]
    [InlineData("posterize")]
    [InlineData("color_balance")]
    [InlineData("cineon_converter")]
    public void Codec_R72V5RejectsUnknownFieldsEvenDisabled(string id)
    {
        var json = "[{\"id\":\"" + id + "\",\"enabled\":false,\"params\":{\"unknown\":0}}]";
        Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(json)));
    }

    [Fact]
    public void RealFfmpeg_HorizontalFlip_MirrorsPixels_AndBlurSmoothsHighFrequency()
    {
        var item = new TimelineItem("v1", "c", 0, 1);
        var none = RenderGray(EditorRenderService.VisualFilter(item, 64, 32, 24));
        var flipped = RenderGray(EditorRenderService.VisualFilter(item with { CatalogFxVersion = 1, CatalogFx = new[] { Fx("horizontal_flip") } }, 64, 32, 24));
        var vflipped = RenderGray(EditorRenderService.VisualFilter(item with { CatalogFxVersion = 1, CatalogFx = new[] { Fx("vertical_flip") } }, 64, 32, 24));
        Assert.Equal(64 * 32, none.Length);
        Assert.NotEqual(none, flipped);
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 64; x++)
            {
                Assert.Equal(none[y * 64 + (63 - x)], flipped[y * 64 + x]);
                Assert.Equal(none[(31 - y) * 64 + x], vflipped[y * 64 + x]);
            }
        var blurred = RenderGray(EditorRenderService.VisualFilter(item with { CatalogFxVersion = 1, CatalogFx = new[] { Fx("gaussian_blur", true, ("radius", 6)) } }, 64, 32, 24));
        Assert.NotEqual(none, blurred);
    }

    public static IEnumerable<object[]> R73V6Cases()
    {
        yield return new object[] { "mosaic-default", Fx("mosaic"), "{}" };
        yield return new object[] { "mosaic-fractional", Fx("mosaic", true, ("horizontal", 7.3), ("vertical", 3.7), ("sharp", 1)), "{\"horizontal\":7.3,\"vertical\":3.7,\"sharp\":true}" };
        yield return new object[] { "mosaic-boundary", Fx("mosaic", true, ("horizontal", 4096), ("vertical", 1)), "{\"horizontal\":4096,\"vertical\":1}" };
        yield return new object[] { "edges-default", Fx("find_edges"), "{}" };
        yield return new object[] { "edges-invert-blend", Fx("find_edges", true, ("invert", 1), ("blend", 37)), "{\"invert\":true,\"blend\":37}" };
        yield return new object[] { "emboss-default", Fx("emboss"), "{}" };
        yield return new object[] { "emboss-nondefault", Fx("emboss", true, ("direction", -133), ("relief", 4.3), ("contrast", 173), ("blend", 38)), "{\"direction\":-133,\"relief\":4.3,\"contrast\":173,\"blend\":38}" };
        yield return new object[] { "emboss-extreme", Fx("emboss", true, ("direction", 13.37), ("relief", 100), ("contrast", 500)), "{\"direction\":13.37,\"relief\":100,\"contrast\":500}" };
        yield return new object[] { "colored-default", Fx("color_emboss"), "{}" };
        yield return new object[] { "colored-nondefault", Fx("color_emboss", true, ("direction", 233), ("relief", 7.3), ("contrast", 207)), "{\"direction\":233,\"relief\":7.3,\"contrast\":207}" };
        yield return new object[] { "leave-default", Fx("leave_color"), "{}" };
        yield return new object[] { "leave-nondefault", Fx("leave_color", true, ("amount", 71), ("color_r", .9), ("color_g", .2), ("color_b", .7), ("tolerance", 12), ("softness", 7)), "{\"amount\":71,\"color\":[0.9,0.2,0.7],\"tolerance\":12,\"softness\":7}" };
        yield return new object[] { "change-default", Fx("change_to_color"), "{}" };
        yield return new object[] { "change-nondefault", Fx("change_to_color", true, ("from_r", .9), ("from_g", .2), ("from_b", .7), ("to_r", .1), ("to_g", .7), ("to_b", .8), ("hue_tol", 15), ("softness", 19)), "{\"from\":[0.9,0.2,0.7],\"to\":[0.1,0.7,0.8],\"hue_tol\":15,\"softness\":19}" };
        yield return new object[] { "threeway-default", Fx("three_way_color"), "{}" };
        yield return new object[] { "threeway-wheels", Fx("three_way_color", true, ("shadow_hue", 73), ("shadow_balance", 63), ("shadow_level", -7), ("mid_hue", 290), ("mid_balance", 51), ("mid_level", 11), ("hi_hue", -170), ("hi_balance", 39), ("hi_level", 17), ("shadow_threshold", 33), ("shadow_softness", 30), ("hi_threshold", 66), ("hi_softness", 30), ("saturation", 137)), "{\"shadow_hue\":73,\"shadow_balance\":63,\"shadow_level\":-7,\"mid_hue\":290,\"mid_balance\":51,\"mid_level\":11,\"hi_hue\":-170,\"hi_balance\":39,\"hi_level\":17,\"shadow_threshold\":33,\"shadow_softness\":30,\"hi_threshold\":66,\"hi_softness\":30,\"saturation\":137}" };
        yield return new object[] { "median-identity", Fx("median"), "{}" };
        yield return new object[] { "median-one", Fx("median", true, ("radius", 1)), "{\"radius\":1}" };
        yield return new object[] { "median-sparse-fractional", Fx("median", true, ("radius", 3.7)), "{\"radius\":3.7}" };
        yield return new object[] { "median-clamped", Fx("median", true, ("radius", 4096)), "{\"radius\":4096}" };
    }

    [Theory]
    [MemberData(nameof(R73V6Cases))]
    public void R73V6_RealFfmpegMatchesFinalActualPreviewWithinOneRgbaByte(string name, CatalogFxInstance effect, string previewParams)
    {
        var input = R71Sample();
        var expected = R72Preview(input, new[] { effect }, new[] { previewParams });
        var filter = VideoFxChain.Build(new[] { effect });
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", string.IsNullOrEmpty(filter) ? "format=rgba" : filter + ",format=rgba", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        if (name == "median-identity") Assert.Equal(input, actual);
        else Assert.False(input.SequenceEqual(expected), name + " must exercise a visible preview change");
        var max = 0;
        for (var i = 0; i < actual.Length; i++) max = Math.Max(max, Math.Abs(expected[i] - actual[i]));
        Assert.True(max <= 1, name + " max RGBA error=" + max);
        for (var i = 3; i < actual.Length; i += 4) Assert.Equal(expected[i], actual[i]);
    }

    [Theory]
    [MemberData(nameof(R73V6Cases))]
    public void R73V6_CodecRoundTripsNumericContractAndInvariantBuild(string name, CatalogFxInstance effect, string previewParams)
    {
        var json = JsonSerializer.Serialize(new[] { effect });
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var invariant = VideoFxChain.Build(new[] { effect }); Assert.Equal(invariant, VideoFxChain.Build(item.CatalogFx!));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(invariant, VideoFxChain.Build(new[] { effect })); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
    }

    [Theory]
    [InlineData("mosaic", "horizontal", .9)]
    [InlineData("mosaic", "vertical", 4096.01)]
    [InlineData("mosaic", "sharp", .5)]
    [InlineData("find_edges", "invert", .5)]
    [InlineData("find_edges", "blend", 100.01)]
    [InlineData("emboss", "direction", 360.01)]
    [InlineData("emboss", "relief", -1)]
    [InlineData("color_emboss", "contrast", 500.01)]
    [InlineData("leave_color", "color_r", 1.01)]
    [InlineData("leave_color", "amount", double.NaN)]
    [InlineData("change_to_color", "to_b", -.01)]
    [InlineData("change_to_color", "hue_tol", double.NegativeInfinity)]
    [InlineData("three_way_color", "hi_level", 100.01)]
    [InlineData("three_way_color", "shadow_softness", -.01)]
    [InlineData("three_way_color", "saturation", 200.01)]
    [InlineData("median", "radius", double.PositiveInfinity)]
    [InlineData("median", "radius", 4096.01)]
    public void R73V6_BuildRejectsInvalidValuesEvenWhenDisabled(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Theory]
    [InlineData("mosaic")]
    [InlineData("find_edges")]
    [InlineData("emboss")]
    [InlineData("color_emboss")]
    [InlineData("leave_color")]
    [InlineData("change_to_color")]
    [InlineData("three_way_color")]
    [InlineData("median")]
    public void R73V6_CodecAndBuildRejectUnknownParamsEvenWhenDisabled(string id)
    {
        foreach (var enabled in new[] { true, false })
        {
            Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, enabled, ("unknown", 0)) })).Message);
            var json = "[{\"id\":\"" + id + "\",\"enabled\":" + (enabled ? "true" : "false") + ",\"params\":{\"unknown\":0}}]";
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(json)));
        }
    }

    [Theory]
    [InlineData("noise")]
    [InlineData("basic_3d")]
    [InlineData("bevel_alpha")]
    [InlineData("posterize_time")]
    [InlineData("echo")]
    public void R73V6_UnprovedOrTemporalKernelsStayUnsupported(string id)
    {
        foreach (var enabled in new[] { true, false })
        {
            // V14 proves zero-only subdomains; preserve rejection using explicit nonzero inputs.
            var effect = id == "noise" ? Fx(id, enabled, ("amount", 20)) : id == "basic_3d" ? Fx(id, enabled, ("swivel", .001)) : Fx(id, enabled);
            var expected = id is "noise" or "basic_3d" ? VideoFxChain.InvalidParam : VideoFxChain.UnsupportedId;
            Assert.Equal(expected, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Fact]
    public void R73V6_MosaicChangesAlphaAndRequiresV1TailMatteAndOverlayRejection()
    {
        var effects = new[] { Fx("mosaic", true, ("horizontal", 7.3), ("vertical", 3.7)), Fx("three_way_color", true, ("mid_level", 7), ("saturation", 73)) };
        var item = new TimelineItem("v1", "c", 0, 1) { CatalogFxVersion = 1, CatalogFx = effects };
        var filter = EditorRenderService.VisualFilter(item, 64, 32, 24);
        Assert.EndsWith(VideoFxChain.Build(effects) + ",premultiply=inplace=1,format=rgb24", filter);
        foreach (var track in new[] { 2, 3 })
            Assert.Equal("catalog-fx-alpha-overlay-unsupported", Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item with { Track = track }, 64, 32, 24)).Message);
        var input = R71Sample(); var preview = R72Preview(input, effects, new[] { "{\"horizontal\":7.3,\"vertical\":3.7}", "{\"mid_level\":7,\"saturation\":73}" });
        Assert.Contains(Enumerable.Range(0, 64 * 32), i => preview[i * 4 + 3] != input[i * 4 + 3]);
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(effects) + ",premultiply=inplace=1,format=rgb24", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(64 * 32 * 3, actual.Length);
        for (var i = 0; i < actual.Length; i++)
            Assert.InRange(Math.Abs(actual[i] - Math.Round(preview[i / 3 * 4 + i % 3] * preview[i / 3 * 4 + 3] / 255d)), 0, 1);
        Assert.DoesNotContain("premultiply", EditorRenderService.VisualFilter(item with { Track = 2, CatalogFx = new[] { Fx("median", true, ("radius", 1)), Fx("find_edges") } }, 64, 32, 24));
    }

    [Fact]
    public void R73V6_OrderedMixedChainMatchesActualPreviewAndIsNotCommutative()
    {
        var input = R71Sample();
        var effects = new[] { Fx("mosaic", true, ("horizontal", 9.3), ("vertical", 5.7)), Fx("change_to_color"), Fx("leave_color", true, ("amount", 47)), Fx("three_way_color", true, ("mid_level", 7), ("saturation", 73)) };
        var preview = R72Preview(input, effects, new[] { "{\"horizontal\":9.3,\"vertical\":5.7}", "{}", "{\"amount\":47}", "{\"mid_level\":7,\"saturation\":73}" });
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(effects), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }, input);
        var reversed = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(effects.Reverse().ToArray()), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }, input);
        Assert.Equal(input.Length, actual.Length); Assert.False(actual.SequenceEqual(reversed));
        for (var i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - preview[i]), 0, 1);
    }

    [Theory]
    [InlineData("mosaic", "horizontal", 7.3)]
    [InlineData("find_edges", "blend", 37)]
    [InlineData("emboss", "direction", 133)]
    [InlineData("color_emboss", "relief", 1.7)]
    [InlineData("leave_color", "amount", 71)]
    [InlineData("change_to_color", "hue_tol", 15)]
    [InlineData("three_way_color", "mid_level", 7)]
    [InlineData("median", "radius", 3.7)]
    public void R73V6_RealFfmpegKeepsFrameCountSizeAndTimestamps(string id, string key, double value)
    {
        var frames = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x32:r=24:duration=0.25", "-vf", VideoFxChain.Build(new[] { Fx(id, true, (key, value)) }) + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 64x32"); Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(part => part.Trim()).ToArray()).ToArray();
        Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][1]); Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][2]);
            Assert.Equal("1", rows[i][3]); Assert.Equal((64 * 32 * 4).ToString(CultureInfo.InvariantCulture), rows[i][4]);
        }
    }

    public static IEnumerable<object[]> R74V7Cases()
    {
        yield return new object[] { "lighting-default", Fx("lighting_effects"), "{}" };
        yield return new object[] { "lighting-black", Fx("lighting_effects", true, ("ambient", 0), ("intensity", 0)), "{\"ambient\":0,\"intensity\":0}" };
        yield return new object[] { "lighting-white", Fx("lighting_effects", true, ("ambient", 100), ("intensity", 100)), "{\"ambient\":100,\"intensity\":100}" };
        yield return new object[] { "lighting-zero-radius", Fx("lighting_effects", true, ("radius", 0), ("position_x", .5), ("position_y", .5), ("ambient", 0), ("intensity", 100)), "{\"radius\":0,\"position\":[0.5,0.5],\"ambient\":0,\"intensity\":100}" };
        yield return new object[] { "lighting-fractional", Fx("lighting_effects", true, ("radius", .1), ("position_x", 1.5), ("position_y", 1.5), ("ambient", 25), ("intensity", 75), ("color_r", .5), ("color_g", 0), ("color_b", 1)), "{\"radius\":0.1,\"position\":[1.5,1.5],\"ambient\":25,\"intensity\":75,\"color\":[0.5,0,1]}" };
        yield return new object[] { "lighting-coordinate-boundaries", Fx("lighting_effects", true, ("radius", 1_000_000), ("position_x", -1_000_000), ("position_y", 1_000_000), ("ambient", 11.25), ("intensity", 89.125), ("color_r", .2), ("color_g", 2d / 3), ("color_b", 1)), "{\"radius\":1000000,\"position\":{\"x\":-1000000,\"y\":1000000},\"ambient\":11.25,\"intensity\":89.125,\"color\":\"#3aF\"}" };
        yield return new object[] { "lighting-scalar-point-color", Fx("lighting_effects", true, ("position_x", 7.25), ("position_y", 7.25), ("color_r", .5), ("color_g", .5), ("color_b", .5)), "{\"position\":7.25,\"color\":0.5}" };
        yield return new object[] { "lighting-no-spot", Fx("lighting_effects", true, ("radius", 5.75), ("ambient", 50), ("intensity", 0)), "{\"radius\":5.75,\"ambient\":50,\"intensity\":0}" };
        yield return new object[] { "lumetri-default", Fx("lumetri"), "{}" };
        yield return new object[] { "lumetri-basic-off", Fx("lumetri", true, ("basic_on", 0), ("exposure", 5), ("temperature", 100), ("tint", -100), ("saturation", 200)), "{\"basic_on\":false,\"exposure\":5,\"temperature\":100,\"tint\":-100,\"saturation\":200}" };
        yield return new object[] { "lumetri-white-balance-exposure", Fx("lumetri", true, ("exposure", 1.25), ("temperature", 37.5), ("tint", -25), ("saturation", 125)), "{\"exposure\":1.25,\"temperature\":37.5,\"tint\":-25,\"saturation\":125}" };
        yield return new object[] { "lumetri-low-extremes", Fx("lumetri", true, ("exposure", -5), ("temperature", -100), ("tint", 100), ("contrast", -100), ("highlights", -100), ("shadows", 100), ("whites", 100), ("blacks", -100), ("saturation", 0)), "{\"exposure\":-5,\"temperature\":-100,\"tint\":100,\"contrast\":-100,\"highlights\":-100,\"shadows\":100,\"whites\":100,\"blacks\":-100,\"saturation\":0}" };
        yield return new object[] { "lumetri-high-extremes", Fx("lumetri", true, ("exposure", 5), ("temperature", 100), ("tint", -100), ("contrast", 100), ("highlights", 100), ("shadows", -100), ("whites", -100), ("blacks", 100), ("saturation", 200)), "{\"exposure\":5,\"temperature\":100,\"tint\":-100,\"contrast\":100,\"highlights\":100,\"shadows\":-100,\"whites\":-100,\"blacks\":100,\"saturation\":200}" };
        yield return new object[] { "lumetri-contrast-below-threshold", Fx("lumetri", true, ("contrast", .005), ("highlights", 57.125), ("shadows", -67.25), ("whites", 19.5), ("blacks", 12.25), ("saturation", 145.5)), "{\"contrast\":0.005,\"highlights\":57.125,\"shadows\":-67.25,\"whites\":19.5,\"blacks\":12.25,\"saturation\":145.5}" };
        yield return new object[] { "lumetri-negative-threshold", Fx("lumetri", true, ("contrast", -.01), ("exposure", -1.235), ("blacks", 100), ("shadows", -100), ("saturation", 199.9)), "{\"contrast\":-0.01,\"exposure\":-1.235,\"blacks\":100,\"shadows\":-100,\"saturation\":199.9}" };
        yield return new object[] { "lumetri-positive-threshold", Fx("lumetri", true, ("contrast", .01), ("exposure", .125), ("whites", 100), ("highlights", -100), ("saturation", .1)), "{\"contrast\":0.01,\"exposure\":0.125,\"whites\":100,\"highlights\":-100,\"saturation\":0.1}" };
        foreach (var contrast in new[] { -.010000001, .010000001 })
            yield return new object[] { "lumetri-contrast-over-threshold-" + contrast.ToString(CultureInfo.InvariantCulture), Fx("lumetri", true, ("contrast", contrast), ("exposure", .5)), JsonSerializer.Serialize(new { contrast, exposure = .5 }) };
    }

    [Theory]
    [MemberData(nameof(R74V7Cases))]
    public void R74V7_RealFfmpegMatchesFinalActualPreviewWithinOneRgbaByte(string name, CatalogFxInstance effect, string previewParams)
    {
        var input = R71Sample(); var expected = R72Preview(input, new[] { effect }, new[] { previewParams });
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(new[] { effect }) + ",format=rgba", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        var max = 0;
        for (var i = 0; i < actual.Length; i++) max = Math.Max(max, Math.Abs(expected[i] - actual[i]));
        Assert.True(max <= 1, name + " max RGBA error=" + max);
        for (var i = 3; i < actual.Length; i += 4) { Assert.Equal(input[i], expected[i]); Assert.Equal(input[i], actual[i]); }
    }

    [Theory]
    [MemberData(nameof(R74V7Cases))]
    public void R74V7_CodecRoundTripsNumericContractWithInvariantBuildAndValidDisabledEntry(string name, CatalogFxInstance effect, string previewParams)
    {
        var json = JsonSerializer.Serialize(new[] { effect });
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var invariant = VideoFxChain.Build(new[] { effect }); Assert.Equal(invariant, VideoFxChain.Build(item.CatalogFx!));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(invariant, VideoFxChain.Build(new[] { effect })); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
    }

    [Theory]
    [InlineData("lighting_effects", "radius", -.001)]
    [InlineData("lighting_effects", "radius", 1_000_000.01)]
    [InlineData("lighting_effects", "position_x", -1_000_000.01)]
    [InlineData("lighting_effects", "position_y", 1_000_000.01)]
    [InlineData("lighting_effects", "intensity", 100.01)]
    [InlineData("lighting_effects", "ambient", -.01)]
    [InlineData("lighting_effects", "color_r", -.001)]
    [InlineData("lighting_effects", "color_g", 1.001)]
    [InlineData("lighting_effects", "color_b", double.NaN)]
    [InlineData("lighting_effects", "radius", double.PositiveInfinity)]
    [InlineData("lumetri", "basic_on", .5)]
    [InlineData("lumetri", "exposure", 5.001)]
    [InlineData("lumetri", "exposure", -5.001)]
    [InlineData("lumetri", "temperature", 100.01)]
    [InlineData("lumetri", "tint", -100.01)]
    [InlineData("lumetri", "contrast", 100.01)]
    [InlineData("lumetri", "highlights", -100.01)]
    [InlineData("lumetri", "shadows", 100.01)]
    [InlineData("lumetri", "whites", -100.01)]
    [InlineData("lumetri", "blacks", 100.01)]
    [InlineData("lumetri", "saturation", -.01)]
    [InlineData("lumetri", "saturation", 200.01)]
    [InlineData("lumetri", "contrast", double.NegativeInfinity)]
    public void R74V7_InvalidNumericContractRejectsEvenWhenDisabledOrBasicOff(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = Fx(id, enabled, (key, value));
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            if (id == "lumetri" && key != "basic_on")
                Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect with { Params = new(effect.Params) { ["basic_on"] = 0 } } })).Message);
            if (double.IsFinite(value))
                Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Theory]
    [InlineData("position_x")]
    [InlineData("position_y")]
    public void R74V7_LightingIncompletePointRejectsEvenWhenDisabled(string key)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = Fx("lighting_effects", enabled, (key, 0));
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Theory]
    [InlineData("lighting_effects", "unknown")]
    [InlineData("lumetri", "unknown")]
    [InlineData("lumetri", "creative_on")]
    [InlineData("lumetri", "look")]
    [InlineData("lumetri", "look_intensity")]
    [InlineData("lumetri", "curves_on")]
    [InlineData("lumetri", "curve_master")]
    [InlineData("lumetri", "wheels_on")]
    [InlineData("lumetri", "shadow_hue")]
    [InlineData("lumetri", "hsl_on")]
    [InlineData("lumetri", "hsl_hue")]
    [InlineData("lumetri", "hdr")]
    [InlineData("lumetri", "hdr_exposure")]
    [InlineData("lumetri", "vignette_on")]
    [InlineData("lumetri", "vig_amount")]
    public void R74V7_UnknownAndNonBasicLumetriKeysRejectWithoutSilentTruncation(string id, string key)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = Fx(id, enabled, (key, 0));
            Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Theory]
    [InlineData("noise")]
    [InlineData("corner_pin")]
    [InlineData("turbulent_displace")]
    [InlineData("brush_strokes")]
    [InlineData("roughen_edges")]
    public void R74V7_UnprovedAlphaOrTemporalKernelsRemainUnsupported(string id)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = id is "noise" or "turbulent_displace" ? Fx(id, enabled, ("amount", 20)) : Fx(id, enabled);
            var expected = id is "noise" or "turbulent_displace" ? VideoFxChain.InvalidParam : VideoFxChain.UnsupportedId;
            Assert.Equal(expected, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    // R79 migration: these ids became supported (V12) only inside a proven static domain; the old whole-id rejection
    // is replaced by an out-of-domain rejection of equal strength (enabled and disabled both fail closed).
    [Theory]
    [InlineData("mirror", "angle", 360.01)]
    [InlineData("lens_distortion", "curvature", 100.01)]
    [InlineData("twirl", "radius", 100.01)]
    [InlineData("magnify", "magnification", 1000.01)]
    [InlineData("spherize", "radius", 1000000.01)]
    [InlineData("warp_stabilizer", "smoothness", 100.01)]
    [InlineData("spacer", "fill", 1)]
    public void R79MIG_FormerlyUnsupportedIdsRejectOutOfDomainParams(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Fact]
    public void R79MIG_WaveWarpDefaultAnimatedSpeedStaysUnsupported()
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx("wave_warp", enabled) })).Message);
    }

    [Fact]
    public void R74V7_AlphaPreservingOrderedChainWorksOnOverlayAndDiffersFromReversedOrder()
    {
        var effects = new[] { Fx("lighting_effects", true, ("radius", 48), ("position_x", 16), ("position_y", 12), ("ambient", 35), ("intensity", 90), ("color_r", .7), ("color_g", 1), ("color_b", .3)), Fx("lumetri", true, ("temperature", 20), ("exposure", .5), ("shadows", 11), ("contrast", 23), ("saturation", 130)) };
        var chain = VideoFxChain.Build(effects);
        foreach (var track in new[] { 1, 2, 3 })
        {
            var item = new TimelineItem("v" + track, "c", 0, 1) { Track = track, CatalogFxVersion = 1, CatalogFx = effects };
            var filter = EditorRenderService.VisualFilter(item, 64, 32, 24);
            Assert.EndsWith(chain, filter); Assert.DoesNotContain("premultiply", filter);
        }
        var input = R71Sample();
        var preview = R72Preview(input, effects, new[] { "{\"radius\":48,\"position\":[16,12],\"ambient\":35,\"intensity\":90,\"color\":[0.7,1,0.3]}", "{\"temperature\":20,\"exposure\":0.5,\"shadows\":11,\"contrast\":23,\"saturation\":130}" });
        byte[] Run(IReadOnlyList<CatalogFxInstance> selected) => R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(selected), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }, input);
        var actual = Run(effects); var reversed = Run(effects.Reverse().ToArray());
        Assert.Equal(input.Length, actual.Length); Assert.False(actual.SequenceEqual(reversed));
        for (var i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - preview[i]), 0, 1);
        for (var i = 3; i < actual.Length; i += 4) Assert.Equal(input[i], actual[i]);
    }

    [Theory]
    [InlineData("lighting_effects")]
    [InlineData("lumetri")]
    public void R74V7_ArchiveOnlyNoOpWouldFailRealPixelProof(string id)
    {
        var input = R71Sample(); var effect = id == "lumetri" ? Fx(id, true, ("exposure", 1.25), ("saturation", 137)) : Fx(id);
        var expected = R72Preview(input, new[] { effect }, new[] { id == "lumetri" ? "{\"exposure\":1.25,\"saturation\":137}" : "{}" });
        var noOp = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", "format=rgba", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }, input);
        Assert.True(noOp.Where((value, index) => Math.Abs(value - expected[index]) > 1).Any(), id + " must reject an archive-only/no-op implementation");
    }

    [Theory]
    [InlineData("lighting_effects")]
    [InlineData("lumetri")]
    public void R74V7_RealFfmpegKeepsFrameCountSizeAndTimestamps(string id)
    {
        var frames = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x32:r=24:duration=0.25", "-vf", VideoFxChain.Build(new[] { Fx(id) }) + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 64x32"); Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(part => part.Trim()).ToArray()).ToArray();
        Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][1]); Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][2]);
            Assert.Equal("1", rows[i][3]); Assert.Equal("8192", rows[i][4]);
        }
    }

    public static IEnumerable<object[]> R74V8Cases()
    {
        yield return new object[] { "four-default", Fx("four_color_gradient"), "{}" };
        yield return new object[] { "four-opacity", Fx("four_color_gradient", true, ("opacity", 33.7), ("blend", 0), ("c1_r", .1), ("c1_g", .3), ("c1_b", .7)), "{\"opacity\":33.7,\"blend\":0,\"c1\":[0.1,0.3,0.7]}" };
        yield return new object[] { "gradient-default", Fx("gradient"), "{}" };
        yield return new object[] { "gradient-radial", Fx("gradient", true, ("shape", 1), ("midpoint", 0), ("start_x", 2.5), ("start_y", -7), ("end_x", 31), ("end_y", 31)), "{\"shape\":\"Radial\",\"midpoint\":0,\"start\":[2.5,-7],\"end\":31}" };
        yield return new object[] { "gradient-degenerate", Fx("gradient", true, ("start_x", 3), ("start_y", 8), ("end_x", 3), ("end_y", 8), ("midpoint", 100)), "{\"start\":[3,8],\"end\":[3,8],\"midpoint\":100}" };
        yield return new object[] { "rgb-default", Fx("rgb_split"), "{}" };
        yield return new object[] { "rgb-radial", Fx("rgb_split", true, ("mode", 1), ("amount", -2.71), ("angle", -77), ("center_x", 5.5), ("center_y", 18)), "{\"mode\":\"Radial\",\"amount\":-2.71,\"angle\":-77,\"center\":[5.5,18]}" };
        yield return new object[] { "clone-default", Fx("clone"), "{}" };
        yield return new object[] { "clone-mirror-gap", Fx("clone", true, ("columns", 3.5), ("rows", 2.5), ("gap", .7), ("mirror", 1)), "{\"columns\":3.5,\"rows\":2.5,\"gap\":0.7,\"mirror\":true}" };
        yield return new object[] { "clone-empty-cells", Fx("clone", true, ("gap", 4096)), "{\"gap\":4096}" };
        yield return new object[] { "lens-default", Fx("lens_flare"), "{}" };
        yield return new object[] { "lens-off-center", Fx("lens_flare", true, ("brightness", 12.3), ("blend", 31), ("center_x", 1.7), ("center_y", 5.2)), "{\"brightness\":12.3,\"blend\":31,\"center\":[1.7,5.2]}" };
    }

    [Theory]
    [MemberData(nameof(R74V8Cases))]
    public void R74V8_RealFfmpegMatchesActualPreviewWithinOneRgbaByte(string name, CatalogFxInstance effect, string previewParams)
    {
        var input = R71Sample(); var expected = R72Preview(input, new[] { effect }, new[] { previewParams });
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(new[] { effect }) + ",format=rgba", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        Assert.False(input.SequenceEqual(expected), name + " fixture must exercise a visible change");
        for (var i = 0; i < actual.Length; i++) Assert.True(Math.Abs(actual[i] - expected[i]) <= 1, name + " channel=" + i + " expected=" + expected[i] + " actual=" + actual[i]);
    }

    [Theory]
    [MemberData(nameof(R74V8Cases))]
    public void R74V8_CodecRoundTripsAndBuildIsCultureInvariant(string name, CatalogFxInstance effect, string previewParams)
    {
        var json = JsonSerializer.Serialize(new[] { effect });
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var filter = VideoFxChain.Build(new[] { effect }); Assert.Equal(filter, VideoFxChain.Build(item.CatalogFx!));
        var original = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(filter, VideoFxChain.Build(new[] { effect })); }
        finally { CultureInfo.CurrentCulture = original; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
    }

    [Theory]
    [InlineData("four_color_gradient", "opacity", -1)]
    [InlineData("four_color_gradient", "blend", 100.1)]
    [InlineData("four_color_gradient", "c1_r", 1.01)]
    [InlineData("gradient", "scatter", .01)]
    [InlineData("gradient", "shape", .5)]
    [InlineData("gradient", "midpoint", 101)]
    [InlineData("gradient", "start_r", double.NaN)]
    [InlineData("rgb_split", "mode", 2)]
    [InlineData("rgb_split", "amount", 1024.1)]
    [InlineData("rgb_split", "angle", -360.1)]
    [InlineData("clone", "columns", .99)]
    [InlineData("clone", "rows", 128.1)]
    [InlineData("clone", "gap", 4096.1)]
    [InlineData("clone", "mirror", .5)]
    [InlineData("lens_flare", "brightness", 300.1)]
    [InlineData("lens_flare", "blend", double.PositiveInfinity)]
    public void R74V8_InvalidValuesFailEvenWhenDisabled(string id, string key, double value)
    {
        foreach (var enabled in new[] { true, false })
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, enabled, (key, value)) })).Message);
    }

    [Theory]
    [InlineData("four_color_gradient")]
    [InlineData("gradient")]
    [InlineData("rgb_split")]
    [InlineData("clone")]
    [InlineData("lens_flare")]
    public void R74V8_UnknownParamsFailCodecAndBuildEvenWhenDisabled(string id)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effects = new[] { Fx(id, enabled, ("unknown", 0)) };
            Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(effects)).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(effects))));
        }
    }

    [Theory]
    [InlineData("gradient", "start")]
    [InlineData("gradient", "end")]
    [InlineData("rgb_split", "center")]
    [InlineData("lens_flare", "center")]
    public void R74V8_PointPairsRequireBothFiniteCoordinates(string id, string point)
    {
        foreach (var enabled in new[] { true, false })
        {
            foreach (var key in new[] { point + "_x", point + "_y" })
                Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, enabled, (key, 2)) })).Message);
            Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { Fx(id, enabled, (point + "_x", 1_000_000.1), (point + "_y", 2)) })).Message);
        }
    }

    [Theory]
    [InlineData("echo_glow")]
    [InlineData("edge_glow")]
    [InlineData("glint")]
    [InlineData("light_leaks")]
    [InlineData("volumetric_rays")]
    [InlineData("wonder_glow")]
    [InlineData("basic_3d")]
    [InlineData("bevel_alpha")]
    [InlineData("corner_pin")]
    [InlineData("simple_text")]
    [InlineData("auto_align")]
    [InlineData("stroke")]
    [InlineData("metadata_burnin")]
    [InlineData("posterize_time")]
    [InlineData("auto_reframe")]
    [InlineData("camera_shake")]
    [InlineData("turbulent_displace")]
    [InlineData("brush_strokes")]
    [InlineData("roughen_edges")]
    [InlineData("spin")]
    [InlineData("wiggle")]
    public void R74V8_UnprovedHistoryAndProceduralKernelsRemainUnsupported(string id)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = id switch
            {
                "wonder_glow" => Fx(id, enabled, ("intensity", .001)),
                "basic_3d" => Fx(id, enabled, ("swivel", .001)),
                "stroke" => Fx(id, enabled, ("width", .001), ("opacity", .001)),
                "camera_shake" => Fx(id, enabled, ("amount", .001), ("rotation", 0), ("zoom", 0)),
                "turbulent_displace" or "spin" or "wiggle" => Fx(id, enabled, ("amount", .001)),
                _ => Fx(id, enabled),
            };
            var expected = id is "wonder_glow" or "basic_3d" or "stroke" or "camera_shake" or "turbulent_displace" or "spin" or "wiggle" ? VideoFxChain.InvalidParam : VideoFxChain.UnsupportedId;
            Assert.Equal(expected, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Theory]
    [InlineData("four_color_gradient")]
    [InlineData("rgb_split")]
    [InlineData("clone")]
    [InlineData("lens_flare")]
    public void R74V8_AlphaKernelsRequireV1AndOnlyTailMatte(string id)
    {
        var effects = new[] { Fx(id), Fx("offset", true, ("shift_x", 2), ("shift_y", 1), ("blend", 0)) };
        var item = new TimelineItem("v1", "c", 0, 1) { CatalogFxVersion = 1, CatalogFx = effects };
        Assert.EndsWith(VideoFxChain.Build(effects) + ",premultiply=inplace=1,format=rgb24", EditorRenderService.VisualFilter(item, 64, 32, 24));
        foreach (var track in new[] { 2, 3 })
        {
            Assert.Equal("catalog-fx-alpha-overlay-unsupported", Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item with { Track = track }, 64, 32, 24)).Message);
            Assert.DoesNotContain("premultiply", EditorRenderService.VisualFilter(item with { Track = track, CatalogFx = new[] { Fx(id, false) } }, 64, 32, 24));
        }
        var input = R71Sample(); var preview = R72Preview(input, effects, new[] { "{}", "{\"shift\":[2,1],\"blend\":0}" });
        var actual = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(effects) + ",premultiply=inplace=1,format=rgb24", "-frames:v", "1", "-f", "rawvideo", "pipe:1" }, input);
        Assert.Equal(64 * 32 * 3, actual.Length);
        for (var i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - Math.Round(preview[i / 3 * 4 + i % 3] * preview[i / 3 * 4 + 3] / 255d)), 0, 1);
    }

    [Fact]
    public void R74V8_OpaqueGradientRemainsRenderableOnBothOverlays()
    {
        var effect = Fx("gradient");
        foreach (var track in new[] { 2, 3 })
        {
            var item = new TimelineItem("overlay", "c", 0, 1) { Track = track, CatalogFxVersion = 1, CatalogFx = new[] { effect } };
            var filter = EditorRenderService.VisualFilter(item, 64, 32, 24);
            Assert.EndsWith(VideoFxChain.Build(new[] { effect }), filter); Assert.DoesNotContain("premultiply", filter);
            Assert.Equal(64 * 32 * 3, RenderPixels("testsrc2=s=64x32:r=24", filter, "rgb24", 3).Length);
        }
    }

    [Fact]
    public void R74V8_OrderedMixedChainMatchesPreviewAndIsNotCommutative()
    {
        var effects = new[] { Fx("clone", true, ("columns", 2), ("rows", 1), ("gap", 2)), Fx("four_color_gradient", true, ("opacity", 37), ("blend", 31)), Fx("rgb_split", true, ("amount", 2.71), ("angle", 35)) };
        var input = R71Sample(); var preview = R72Preview(input, effects, new[] { "{\"columns\":2,\"rows\":1,\"gap\":2}", "{\"opacity\":37,\"blend\":31}", "{\"amount\":2.71,\"angle\":35}" });
        byte[] Run(IReadOnlyList<CatalogFxInstance> chain) => R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "64x32", "-i", "pipe:0", "-vf", VideoFxChain.Build(chain), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }, input);
        var actual = Run(effects); Assert.Equal(input.Length, actual.Length); Assert.False(actual.SequenceEqual(Run(effects.Reverse().ToArray())));
        for (var i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - preview[i]), 0, 1);
    }

    [Theory]
    [InlineData("four_color_gradient")]
    [InlineData("gradient")]
    [InlineData("rgb_split")]
    [InlineData("clone")]
    [InlineData("lens_flare")]
    public void R74V8_RealFfmpegPreservesFrameCountDimensionsAndTimestamps(string id)
    {
        var frames = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x32:r=24:duration=0.25", "-vf", VideoFxChain.Build(new[] { Fx(id) }) + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 64x32"); Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(part => part.Trim()).ToArray()).ToArray(); Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][1]); Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][2]);
            Assert.Equal("1", rows[i][3]); Assert.Equal((64 * 32 * 4).ToString(CultureInfo.InvariantCulture), rows[i][4]);
        }
    }

    public static IEnumerable<object[]> R74EXACTCases()
    {
        yield return new object[] { "tint-default", Fx("tint"), "{}", 64, 32 };
        yield return new object[] { "tint-zero", Fx("tint", true, ("amount", 0)), "{\"amount\":0}", 1, 1 };
        yield return new object[] { "tint-inverted", Fx("tint", true, ("black_r", 1), ("black_g", 1), ("black_b", 1), ("white_r", 0), ("white_g", 0), ("white_b", 0)), "{\"black\":[1,1,1],\"white\":[0,0,0]}", 1, 17 };
        yield return new object[] { "tint-offset", Fx("tint", true, ("black_r", .25), ("black_g", .25), ("black_b", .25), ("white_r", .75), ("white_g", .75), ("white_b", .75)), "{\"black\":[0.25,0.25,0.25],\"white\":[0.75,0.75,0.75]}", 19, 1 };
        yield return new object[] { "tint-fractional", Fx("tint", true, ("amount", 37.127), ("black_r", .731), ("black_g", .041), ("black_b", .275), ("white_r", .093), ("white_g", .857), ("white_b", .423)), "{\"amount\":37.127,\"black\":[0.731,0.041,0.275],\"white\":[0.093,0.857,0.423]}", 9, 7 };
        var dimensions = new[] { (1, 1), (1, 17), (19, 1), (2, 3), (3, 2), (9, 7), (32, 16), (64, 32) };
        var radii = new[] { 0d, .3, .3000000001, .5, .5000000001, 1, 2.715, 1024 };
        for (var i = 0; i < radii.Length; i++)
        {
            var value = radii[i]; var (width, height) = dimensions[i];
            yield return new object[] { "gaussian-" + value.ToString("R", CultureInfo.InvariantCulture), Fx("gaussian_blur", true, ("radius", value)), JsonSerializer.Serialize(new { blurriness = value }), width, height };
        }
        var amounts = new[] { 0d, 1e-9, 1, 37.127, 100, 175.13, 447.536, 500 };
        for (var i = 0; i < amounts.Length; i++)
        {
            var value = amounts[i]; var (width, height) = dimensions[i];
            yield return new object[] { "sharpen-" + value.ToString("R", CultureInfo.InvariantCulture), Fx("sharpen", true, ("amount", value)), JsonSerializer.Serialize(new { amount = value }), width, height };
        }
    }

    private static byte[] R74EXACTSample(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var colors = new byte[,] { { 0, 0, 0 }, { 255, 255, 255 }, { 255, 0, 0 }, { 0, 255, 0 }, { 0, 0, 255 }, { 1, 2, 3 }, { 10, 11, 12 }, { 254, 1, 127 } };
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var k = i / 4;
            for (var c = 0; c < 3; c++) pixels[i + c] = k % 3 == 0 ? colors[k % 8, c] : (byte)((k * (c * 42 + 73) + c * 19 + 3) % 256);
            pixels[i + 3] = new byte[] { 0, 1, 127, 255 }[k % 4];
        }
        return pixels;
    }

    private static byte[] R74EXACTPreview(byte[] input, IReadOnlyList<CatalogFxInstance> effects, IReadOnlyList<string> parameters, int width, int height)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "filmcraft-preview", "wwwroot", "js", "fx-engine.js"))) directory = directory.Parent;
        Assert.NotNull(directory);
        const string script = "const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');const p=JSON.parse(fs.readFileSync(0,'utf8'));const c=vm.createContext({window:{},console,Uint8ClampedArray,Float32Array});for(const name of ['fx-engine.js','fx-more-a.js','fx-more-b.js']){vm.runInContext(fs.readFileSync(path.join(p.root,'filmcraft-preview','wwwroot','js',name),'utf8'),c);c.FX=c.window.FX;}const pixels=new Uint8ClampedArray(Buffer.from(p.input,'base64'));for(let i=0;i<p.ids.length;i++){if(c.FX.status(p.ids[i])!=='real')throw Error('preview registration missing '+p.ids[i]);c.FX.apply(pixels,p.width,p.height,p.ids[i],p.params[i],{seconds:0,frame:0,clipId:'r74-exact-'+i});}process.stdout.write(Buffer.from(pixels));";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { root = directory!.FullName, input = Convert.ToBase64String(input), ids = effects.Select(effect => effect.Id), @params = parameters.Select(value => JsonSerializer.Deserialize<JsonElement>(value)), width, height });
        return R71Process(Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_NODE") ?? "node", new[] { "-e", script }, payload);
    }

    private static byte[] R74EXACTRender(byte[] input, IReadOnlyList<CatalogFxInstance> effects, int width, int height)
    {
        var filter = VideoFxChain.Build(effects);
        return R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", width + "x" + height, "-i", "pipe:0", "-vf", (filter.Length == 0 ? "null" : filter) + ",format=rgba", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }, input);
    }

    [Theory]
    [MemberData(nameof(R74EXACTCases))]
    public void R74EXACT_RealFfmpegMatchesFinalPreviewIncludingStraightRgbAndAlpha(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var input = R74EXACTSample(width, height);
        var expected = R74EXACTPreview(input, new[] { effect }, new[] { previewParams }, width, height);
        var actual = R74EXACTRender(input, new[] { effect }, width, height);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
            if (i % 4 == 3) Assert.Equal(input[i], actual[i]);
        }
    }

    [Theory]
    [MemberData(nameof(R74EXACTCases))]
    public void R74EXACT_CodecRoundtripInvariantOrderAndDisabledContract(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var chain = new[] { Fx("vertical_flip"), effect, Fx("horizontal_flip") };
        var json = JsonSerializer.Serialize(chain);
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var expected = VideoFxChain.Build(chain);
        Assert.Equal(expected, VideoFxChain.Build(item.CatalogFx!));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(expected, VideoFxChain.Build(chain)); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
        Assert.EndsWith(expected, EditorRenderService.VisualFilter(item, 64, 32, 24));
    }

    [Fact]
    public void R74EXACT_SharpenZeroStillUsesPreviewLutWhileTintAndBlurCutoffAreIdentity()
    {
        var input = new byte[] { 1, 2, 3, 7 };
        var effect = Fx("sharpen", true, ("amount", 0));
        var actual = R74EXACTRender(input, new[] { effect }, 1, 1);
        Assert.False(input.SequenceEqual(actual));
        Assert.Equal(R74EXACTPreview(input, new[] { effect }, new[] { "{\"amount\":0}" }, 1, 1), actual);
        Assert.Equal("", VideoFxChain.Build(new[] { Fx("tint", true, ("amount", 0)), Fx("gaussian_blur", true, ("radius", .3)) }));
    }

    [Fact]
    public void R74EXACT_OrderedMixedChainMatchesPreviewAndIsNotCommutative()
    {
        var chain = new[] { Fx("tint", true, ("amount", 60), ("black_r", .25), ("white_r", .75), ("white_b", .5)), Fx("gaussian_blur", true, ("radius", 2.715)), Fx("sharpen", true, ("amount", 175.13)) };
        var input = R74EXACTSample(32, 16);
        var expected = R74EXACTPreview(input, chain, new[] { "{\"amount\":60,\"black\":[0.25,0,0],\"white\":[0.75,1,0.5]}", "{\"blurriness\":2.715}", "{\"amount\":175.13}" }, 32, 16);
        var actual = R74EXACTRender(input, chain, 32, 16);
        Assert.Equal(input.Length, actual.Length); Assert.False(actual.SequenceEqual(R74EXACTRender(input, chain.Reverse().ToArray(), 32, 16)));
        for (var i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
    }

    [Theory]
    [InlineData("tint")]
    [InlineData("gaussian_blur")]
    [InlineData("sharpen")]
    public void R74EXACT_RealFfmpegPreservesFrameCountDimensionsAndTimestamps(string id)
    {
        var effect = id == "gaussian_blur" ? Fx(id, true, ("radius", 2.715)) : Fx(id);
        var frames = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=32x16:r=24:duration=0.25", "-vf", VideoFxChain.Build(new[] { effect }) + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 32x16"); Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(part => part.Trim()).ToArray()).ToArray(); Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][1]); Assert.Equal(i.ToString(CultureInfo.InvariantCulture), rows[i][2]);
            Assert.Equal("1", rows[i][3]); Assert.Equal((32 * 16 * 4).ToString(CultureInfo.InvariantCulture), rows[i][4]);
        }
    }

    public static IEnumerable<object[]> R76V9Cases()
    {
        var radii = new[] { 0d, .3, .30001, .9, 1, 2, 3.17, 8, 20, 64, 1024 };
        foreach (var radius in radii)
        {
            yield return new object[] { "compound-radius-" + radius.ToString("R", CultureInfo.InvariantCulture), Fx("compound_blur", true, ("max", radius)), JsonSerializer.Serialize(new { max = radius }), 32, 16 };
            yield return new object[] { "focus-radius-" + radius.ToString("R", CultureInfo.InvariantCulture), Fx("focus_blur", true, ("amount", radius), ("size", 5), ("feather", 3)), JsonSerializer.Serialize(new { amount = radius, size = 5, feather = 3 }), 32, 16 };
        }
        for (var layer = 0; layer <= 2; layer++)
            for (var invert = 0; invert <= 1; invert++)
                yield return new object[] { "compound-mask-" + layer + "-" + invert, Fx("compound_blur", true, ("max", 7.5), ("layer", layer), ("invert", invert), ("stretch", 1)), JsonSerializer.Serialize(new { max = 7.5, layer, invert = invert == 1, stretch = true }), 32, 16 };
        yield return new object[] { "compound-default", Fx("compound_blur"), "{}", 32, 16 };
        yield return new object[] { "compound-invert-alpha", Fx("compound_blur", true, ("max", 20), ("layer", 2), ("invert", 1), ("stretch", 0)), "{\"max\":20,\"layer\":\"Alpha\",\"invert\":true,\"stretch\":false}", 32, 16 };
        yield return new object[] { "compound-catalog-default", Fx("compound_blur", true, ("max", 20), ("layer", 0), ("stretch", 1), ("invert", 0)), "{\"max\":20,\"layer\":null,\"stretch\":true,\"invert\":false}", 32, 16 };
        yield return new object[] { "focus-default", Fx("focus_blur"), "{}", 32, 16 };
        for (var shape = 0; shape <= 1; shape++)
            foreach (var angle in new[] { 0d, 33, -90, 360 })
                yield return new object[] { "focus-mask-" + shape + "-" + angle, Fx("focus_blur", true, ("amount", 3.17), ("size", 0), ("feather", 0), ("shape", shape), ("angle", angle)), JsonSerializer.Serialize(new { amount = 3.17, size = 0, feather = 0, shape, angle }), 32, 16 };
        yield return new object[] { "focus-offset-linear", Fx("focus_blur", true, ("amount", 20), ("size", 0), ("feather", 3), ("shape", 1), ("center_x", 1.5), ("center_y", -3), ("angle", 33), ("show", 0)), "{\"amount\":20,\"size\":0,\"feather\":3,\"shape\":\"Linear\",\"center\":[1.5,-3],\"angle\":33,\"show\":false}", 32, 16 };
        yield return new object[] { "focus-catalog-default", Fx("focus_blur", true, ("center_x", 64), ("center_y", 64)), "{\"shape\":\"Radial\",\"center\":64,\"amount\":20,\"size\":200,\"feather\":200,\"angle\":0,\"show\":false}", 32, 16 };
        var dimensions = new[] { (1, 1), (1, 17), (19, 1), (2, 3), (3, 2), (9, 7), (31, 17), (64, 32) };
        for (var i = 0; i < 16; i++)
        {
            var (width, height) = dimensions[i % dimensions.Length];
            var radius = new[] { .3000001, .9, 3.17, 7.5, 20d, 64, 1024, 2.715 }[i % 8];
            var layer = i % 3; var invert = i % 2; var shape = i % 2; var angle = i * 41.17 - 300;
            var center = new[] { i - 5.75, i * .71 - 4 }; var size = i % 4 == 0 ? 1_000_000 : i * 1.17; var feather = i % 3 == 0 ? 0 : i * .57;
            yield return new object[] { "compound-adversarial-" + i, Fx("compound_blur", true, ("max", radius), ("layer", layer), ("invert", invert)), JsonSerializer.Serialize(new { max = radius, layer, invert = invert == 1 }), width, height };
            yield return new object[] { "focus-adversarial-" + i, Fx("focus_blur", true, ("amount", radius), ("shape", shape), ("angle", angle), ("size", size), ("feather", feather), ("center_x", center[0]), ("center_y", center[1])), JsonSerializer.Serialize(new { amount = radius, shape, angle, size, feather, center }), width, height };
        }
    }

    [Theory]
    [MemberData(nameof(R76V9Cases))]
    public void R76V9_RealFfmpegMatchesFinalPreviewIncludingAdversarialDimensions(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var input = R74EXACTSample(width, height);
        var expected = R74EXACTPreview(input, new[] { effect }, new[] { previewParams }, width, height);
        var actual = R74EXACTRender(input, new[] { effect }, width, height);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
            if (i % 4 == 3) Assert.Equal(input[i], actual[i]);
        }
    }

    [Theory]
    [MemberData(nameof(R76V9Cases))]
    public void R76V9_CodecRoundtripInvariantOrderDisabledAndOverlay(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var chain = new[] { Fx("vertical_flip"), effect, Fx("horizontal_flip") };
        var json = JsonSerializer.Serialize(chain);
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var expected = VideoFxChain.Build(chain); Assert.Equal(expected, VideoFxChain.Build(item.CatalogFx!));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(expected, VideoFxChain.Build(chain)); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
        foreach (var track in new[] { 1, 2, 3 }) Assert.EndsWith(expected, EditorRenderService.VisualFilter(item with { Track = track }, 64, 32, 24));
    }

    [Fact]
    public void R76V9_InvalidDomainsAreRejectedEvenWhenDisabled()
    {
        var invalid = new[]
        {
            Fx("compound_blur", true, ("max", -.1)), Fx("compound_blur", true, ("max", 1024.01)), Fx("compound_blur", true, ("max", double.NaN)), Fx("compound_blur", true, ("max", double.PositiveInfinity)),
            Fx("compound_blur", true, ("layer", 1.5)), Fx("compound_blur", true, ("layer", 3)), Fx("compound_blur", true, ("stretch", .5)), Fx("compound_blur", true, ("invert", -1)),
            Fx("focus_blur", true, ("amount", 1024.01)), Fx("focus_blur", true, ("amount", double.NegativeInfinity)), Fx("focus_blur", true, ("show", 1)), Fx("focus_blur", true, ("show", .5)),
            Fx("focus_blur", true, ("shape", 1.5)), Fx("focus_blur", true, ("angle", 360.1)), Fx("focus_blur", true, ("size", -1)), Fx("focus_blur", true, ("feather", 1_000_001)),
            Fx("focus_blur", true, ("center_x", 1)), Fx("focus_blur", true, ("center_x", 1_000_001), ("center_y", 1)),
        };
        foreach (var effect in invalid)
            foreach (var enabled in new[] { true, false })
                Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect with { Enabled = enabled } })).Message);
    }

    [Theory]
    [InlineData("compound_blur", "layer_image")]
    [InlineData("compound_blur", "center_x")]
    [InlineData("compound_blur", "unknown")]
    [InlineData("focus_blur", "layer")]
    [InlineData("focus_blur", "center")]
    [InlineData("focus_blur", "unknown")]
    public void R76V9_UnknownFieldsRejectWholeEffectAndProject(string id, string key)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = Fx(id, enabled, (key, 0));
            Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Fact]
    public void R76V9_RepeatedNoncommutativeChainMatchesFinalPreviewWithoutLabelCollisions()
    {
        var chain = new[] { Fx("compound_blur", true, ("max", 7.5), ("layer", 2), ("invert", 1)), Fx("focus_blur", true, ("amount", 3.17), ("size", 2), ("feather", 3), ("center_x", 13), ("center_y", 2), ("shape", 1), ("angle", 33)), Fx("compound_blur", true, ("max", 2), ("layer", 1)) };
        var parameters = new[] { "{\"max\":7.5,\"layer\":\"Alpha\",\"invert\":true}", "{\"amount\":3.17,\"size\":2,\"feather\":3,\"center\":[13,2],\"shape\":\"Linear\",\"angle\":33}", "{\"max\":2,\"layer\":\"Luma\"}" };
        var input = R74EXACTSample(32, 16); var filter = VideoFxChain.Build(chain);
        Assert.InRange(filter.Length, 1, 30_000);
        var actual = R74EXACTRender(input, chain, 32, 16); var expected = R74EXACTPreview(input, chain, parameters, 32, 16);
        Assert.False(actual.SequenceEqual(input)); Assert.False(actual.SequenceEqual(R74EXACTRender(input, chain.Reverse().ToArray(), 32, 16)));
        Assert.Equal(input.Length, actual.Length); Assert.Equal(input.Length, expected.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
            if (i % 4 == 3) Assert.Equal(input[i], actual[i]);
        }
    }

    [Theory]
    [InlineData("compound_blur")]
    [InlineData("focus_blur")]
    public void R76V9_RealFfmpegPreservesFrameCountDimensionsAndTimestamps(string id)
    {
        var effect = id == "focus_blur" ? Fx(id, true, ("amount", 3.17), ("size", 0), ("feather", 2)) : Fx(id);
        var frames = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=32x16:r=24:duration=0.25", "-vf", VideoFxChain.Build(new[] { effect }) + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 32x16"); Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(part => part.Trim()).ToArray()).ToArray(); Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++) Assert.Equal(new[] { i.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture), "1", "2048" }, rows[i][1..5]);
    }

    public static IEnumerable<object[]> R77V10Cases()
    {
        var dimensions = new[] { (32, 16), (31, 17), (1, 1), (1, 17), (19, 1), (2, 3), (7, 9) };
        var shapes = new[] { 0, 3, 4, 5, 6, 8 };
        for (var i = 0; i < 32; i++)
        {
            var (width, height) = dimensions[i % dimensions.Length];
            var period = new[] { 0d, .0001, .001, .17, 1, 1_000_000 }[i % 6];
            var duration = Math.Max(period, .001);
            var blend = new[] { 0d, 50, 100, 33.17, 47.125 }[i % 5];
            var color = new[] { (i * .731) % 1, (i * .347) % 1, (i * .193) % 1 };
            var shape = shapes[i % shapes.Length]; var rotation = new[] { -360d, 0, 33.17, 360 }[i % 4];
            yield return new object[] { "strobe-" + i, Fx("strobe", true, ("period", period), ("duration", duration), ("blend", blend), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { period, duration, blend, color }), width, height };
            yield return new object[] { "bokeh-zero-" + i, Fx("bokeh_blur", true, ("amount", 0), ("shape", shape), ("rotation", rotation), ("highlight", i * 3.17), ("threshold", 100 - i * 3.17)), JsonSerializer.Serialize(new { amount = 0, shape, rotation, highlight = i * 3.17, threshold = 100 - i * 3.17 }), width, height };
        }
    }

    [Theory]
    [MemberData(nameof(R77V10Cases))]
    public void R77V10_RealFfmpegMatchesFinalPreviewRgbAndExactAlpha(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var input = R74EXACTSample(width, height);
        var expected = R74EXACTPreview(input, new[] { effect }, new[] { previewParams }, width, height);
        var actual = R74EXACTRender(input, new[] { effect }, width, height);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
            if (i % 4 == 3) { Assert.Equal(expected[i], actual[i]); Assert.Equal(input[i], actual[i]); }
        }
    }

    [Theory]
    [MemberData(nameof(R77V10Cases))]
    public void R77V10_CodecRoundtripInvariantOrderDisabledAndOverlay(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var chain = new[] { Fx("vertical_flip"), effect, Fx("horizontal_flip") };
        var json = JsonSerializer.Serialize(chain);
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var expected = VideoFxChain.Build(chain); Assert.Equal(expected, VideoFxChain.Build(item.CatalogFx!));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(expected, VideoFxChain.Build(chain)); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
        foreach (var track in new[] { 1, 2, 3 }) Assert.EndsWith(expected, EditorRenderService.VisualFilter(item with { Track = track }, width, height, 24));
    }

    [Fact]
    public void R77V10_InvalidDefaultsAndUnprovedDomainsRejectBeforeDisabled()
    {
        var invalid = new[]
        {
            Fx("strobe"), Fx("strobe", true, ("period", 1), ("duration", .9999999)), Fx("strobe", true, ("period", 0), ("duration", 0)),
            Fx("strobe", true, ("period", 1), ("duration", 0), ("blend", 100)), Fx("strobe", true, ("period", -.1), ("duration", 1)),
            Fx("strobe", true, ("period", 1_000_001), ("duration", 1_000_001)), Fx("strobe", true, ("duration", 1_000_001)),
            Fx("strobe", true, ("period", double.NaN), ("duration", 1)), Fx("strobe", true, ("duration", double.PositiveInfinity)),
            Fx("strobe", true, ("duration", 1), ("blend", -.01)), Fx("strobe", true, ("duration", 1), ("blend", 100.01)),
            Fx("strobe", true, ("duration", 1), ("color_r", -.01)), Fx("strobe", true, ("duration", 1), ("color_g", 1.001)),
            Fx("strobe", true, ("duration", 1), ("color_b", double.NegativeInfinity)),
            Fx("bokeh_blur"), Fx("bokeh_blur", true, ("amount", 1e-9)), Fx("bokeh_blur", true, ("amount", -1)), Fx("bokeh_blur", true, ("amount", double.NaN)),
            Fx("bokeh_blur", true, ("amount", 0), ("shape", 1)), Fx("bokeh_blur", true, ("amount", 0), ("shape", 1.5)),
            Fx("bokeh_blur", true, ("amount", 0), ("shape", 7)), Fx("bokeh_blur", true, ("amount", 0), ("rotation", 360.01)),
            Fx("bokeh_blur", true, ("amount", 0), ("highlight", -.01)), Fx("bokeh_blur", true, ("amount", 0), ("threshold", 100.01)),
        };
        foreach (var effect in invalid)
            foreach (var enabled in new[] { true, false })
            {
                var selected = effect with { Enabled = enabled };
                Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { selected })).Message);
                if (selected.Params.Values.All(double.IsFinite)) Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { selected }))));
            }
    }

    [Theory]
    [InlineData("strobe", "color")]
    [InlineData("strobe", "phase")]
    [InlineData("strobe", "unknown")]
    [InlineData("bokeh_blur", "color_r")]
    [InlineData("bokeh_blur", "unknown")]
    public void R77V10_UnknownKeysRejectWholeEffectAndProject(string id, string key)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = id == "strobe" ? Fx(id, enabled, ("duration", 1), (key, 0)) : Fx(id, enabled, ("amount", 0), (key, 0));
            Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Fact]
    public void R77V10_RepeatedNoncommutativeChainMatchesFinalPreviewIncludingHiddenRgbAndExactAlpha()
    {
        var chain = new[] { Fx("strobe", true, ("duration", 1), ("blend", 33.17), ("color_r", 1), ("color_g", 0), ("color_b", 1)), Fx("bokeh_blur", true, ("amount", 0)), Fx("strobe", true, ("period", 0), ("duration", .001), ("blend", 50), ("color_r", .25), ("color_g", .25), ("color_b", .25)) };
        var parameters = new[] { "{\"period\":1,\"duration\":1,\"blend\":33.17,\"color\":\"#f0f\"}", "{\"amount\":0}", "{\"period\":0,\"duration\":0.001,\"blend\":50,\"color\":0.25}" };
        var input = R74EXACTSample(32, 16); var filter = VideoFxChain.Build(chain);
        Assert.InRange(filter.Length, 1, 30_000);
        var actual = R74EXACTRender(input, chain, 32, 16); var expected = R74EXACTPreview(input, chain, parameters, 32, 16);
        Assert.False(actual.SequenceEqual(input)); Assert.False(actual.SequenceEqual(R74EXACTRender(input, chain.Reverse().ToArray(), 32, 16)));
        Assert.Equal(input.Length, actual.Length); Assert.Equal(input.Length, expected.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
            if (i % 4 == 3) Assert.Equal(input[i], actual[i]);
        }
        Assert.Equal("", VideoFxChain.Build(new[] { Fx("bokeh_blur", true, ("amount", 0)) }));
    }

    [Theory]
    [InlineData("strobe")]
    [InlineData("bokeh_blur")]
    public void R77V10_RealFfmpegPreservesFrameCountDimensionsAndTimestamps(string id)
    {
        var effect = id == "strobe" ? Fx(id, true, ("period", 0), ("duration", .001), ("blend", 47.125)) : Fx(id, true, ("amount", 0));
        var filter = VideoFxChain.Build(new[] { effect });
        var frames = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=32x16:r=24:duration=0.25", "-vf", (filter.Length == 0 ? "null" : filter) + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 32x16"); Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(part => part.Trim()).ToArray()).ToArray(); Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++) Assert.Equal(new[] { i.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture), "1", "2048" }, rows[i][1..5]);
    }

    public static IEnumerable<object[]> R78V11Cases()
    {
        var ids = new[] { "circle", "grid", "checkerboard", "ellipse", "paint_bucket", "alpha_adjust", "track_matte", "color_key", "luma_key", "logo_cutout" };
        foreach (var id in ids)
            yield return new object[] { id + "-default", id == "paint_bucket" ? Fx(id, true, ("tolerance", 100)) : Fx(id), id == "paint_bucket" ? "{\"tolerance\":100}" : "{}", 32, 16 };
        var dimensions = new[] { (32, 16), (31, 17), (1, 1), (1, 17), (19, 1), (2, 3), (7, 9), (64, 32) };
        for (var i = 0; i < dimensions.Length; i++)
        {
            var (width, height) = dimensions[i]; var opacity = new[] { 0d, 100, 50, 33.17, 47.125, 1, 99.11, 73.17 }[i];
            var size = new[] { 0d, .01, .5, 1, 3.17, 10, 100, 1_000_000 }[i]; var span = Math.Min(size * 1.17, 1_000_000);
            var color = new[] { (i * .731) % 1, (i * .347) % 1, (i * .193) % 1 }; var point = new[] { i * 3.17 - 4, i * 1.13 - 3 };
            var invert = i % 2 == 1; var mode = i % 5; var replace = i % 3 == 0;
            yield return new object[] { "circle-" + i, Fx("circle", true, ("center_x", point[0]), ("center_y", point[1]), ("radius", size), ("opacity", opacity), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { center = point, radius = size, opacity, color }), width, height };
            yield return new object[] { "grid-" + i, Fx("grid", true, ("size", size), ("border", size / 3), ("opacity", opacity), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { size, border = size / 3, opacity, color }), width, height };
            yield return new object[] { "checker-" + i, Fx("checkerboard", true, ("anchor_x", point[0]), ("anchor_y", point[1]), ("width", size), ("height", span), ("feather", i % 2 == 0 ? 0 : size / 3), ("square", 0), ("mode", replace ? 0 : mode), ("replace", replace ? 1 : 0), ("opacity", opacity), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { anchor = point, width = size, height = span, feather = i % 2 == 0 ? 0 : size / 3, square = false, mode = replace ? "None" : new[] { "Normal", "Add", "Screen", "Multiply", "Overlay" }[mode], opacity, color }), width, height };
            yield return new object[] { "ellipse-" + i, Fx("ellipse", true, ("center_x", point[0]), ("center_y", point[1]), ("width", -size), ("height", span), ("thickness", i % 3 == 0 ? 0 : size / 2), ("softness", opacity), ("composite", invert ? 1 : 0), ("inside_r", color[0]), ("inside_g", color[1]), ("inside_b", color[2]), ("outside_r", color[2]), ("outside_g", color[0]), ("outside_b", color[1])), JsonSerializer.Serialize(new { center = point, width = -size, height = span, thickness = i % 3 == 0 ? 0 : size / 2, softness = opacity, composite = invert, inside = color, outside = new[] { color[2], color[0], color[1] } }), width, height };
            yield return new object[] { "paint-" + i, Fx("paint_bucket", true, ("point_x", point[0]), ("point_y", point[1]), ("tolerance", 100), ("selector", i % 4), ("invert", invert ? 1 : 0), ("mode", mode), ("opacity", opacity), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { point, tolerance = 100, selector = i % 4, invert, mode, opacity, color }), width, height };
            yield return new object[] { "alpha-" + i, Fx("alpha_adjust", true, ("opacity", opacity), ("ignore", i % 2), ("invert", i / 2 % 2), ("mask_only", i / 4 % 2)), JsonSerializer.Serialize(new { opacity, ignore = i % 2 != 0, invert = i / 2 % 2 != 0, mask_only = i / 4 % 2 != 0 }), width, height };
            yield return new object[] { "matte-" + i, Fx("track_matte", true, ("matte", i % 3), ("composite", i % 2), ("reverse", invert ? 1 : 0)), JsonSerializer.Serialize(new { matte = i % 3, composite = i % 2, reverse = invert }), width, height };
            var key = i % 2 == 0 ? new[] { 0d, 0, 0 } : new[] { 254d / 255, 1d / 255, 127d / 255 };
            yield return new object[] { "color-key-" + i, Fx("color_key", true, ("color_r", key[0]), ("color_g", key[1]), ("color_b", key[2])), JsonSerializer.Serialize(new { color = key }), width, height };
            yield return new object[] { "luma-key-" + i, Fx("luma_key", true, ("threshold", opacity), ("cutoff", i % 3 == 0 ? opacity : 13.17)), JsonSerializer.Serialize(new { threshold = opacity, cutoff = i % 3 == 0 ? opacity : 13.17 }), width, height };
            yield return new object[] { "logo-" + i, Fx("logo_cutout", true, ("background", i % 3), ("threshold", opacity), ("softness", i % 2 == 0 ? 0 : 47.125), ("unmultiply", i / 2 % 2), ("invert", invert ? 1 : 0), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { background = i % 3, threshold = opacity, softness = i % 2 == 0 ? 0 : 47.125, unmultiply = i / 2 % 2 != 0, invert, color }), width, height };
        }
    }

    [Theory]
    [MemberData(nameof(R78V11Cases))]
    public void R78V11_RealFfmpegMatchesFinalPreviewRgbAndExactAlphaIncludingHiddenRgb(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var input = R74EXACTSample(width, height); var expected = R74EXACTPreview(input, new[] { effect }, new[] { previewParams }, width, height);
        var actual = R74EXACTRender(input, new[] { effect }, width, height);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
    }

    [Theory]
    [MemberData(nameof(R78V11Cases))]
    public void R78V11_CodecCultureDisabledAndAlphaOverlayContracts(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var chain = new[] { Fx("vertical_flip"), effect, Fx("horizontal_flip") }; var json = JsonSerializer.Serialize(chain);
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var filter = VideoFxChain.Build(chain); Assert.Equal(filter, VideoFxChain.Build(item.CatalogFx!));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(filter, VideoFxChain.Build(chain)); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
        var visual = EditorRenderService.VisualFilter(item with { Track = 1 }, width, height, 24);
        Assert.EndsWith(filter + ",premultiply=inplace=1,format=rgb24", visual);
        Assert.Equal(visual.IndexOf("premultiply=inplace=1", StringComparison.Ordinal), visual.LastIndexOf("premultiply=inplace=1", StringComparison.Ordinal));
        foreach (var track in new[] { 2, 3 })
        {
            Assert.Equal("catalog-fx-alpha-overlay-unsupported", Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item with { Track = track }, width, height, 24)).Message);
            Assert.DoesNotContain("premultiply=inplace=1", EditorRenderService.VisualFilter(item with { Track = track, CatalogFx = new[] { effect with { Enabled = false } } }, width, height, 24));
        }
    }

    [Fact]
    public void R78V11_InvalidDomainsRejectBeforeDisabledWithoutSilentCoercion()
    {
        var invalid = new[]
        {
            Fx("circle", true, ("radius", -.001)), Fx("circle", true, ("radius", 1_000_001)), Fx("circle", true, ("center_x", 0)),
            Fx("grid", true, ("border", -1)), Fx("grid", true, ("opacity", 100.001)), Fx("grid", true, ("size", double.NaN)),
            Fx("checkerboard", true, ("mode", 5)), Fx("checkerboard", true, ("mode", .5)), Fx("checkerboard", true, ("square", .5)),
            Fx("checkerboard", true, ("mode", 1), ("replace", 1)), Fx("checkerboard", true, ("replace", 0), ("anchor_y", 0)),
            Fx("ellipse", true, ("width", -1_000_001)), Fx("ellipse", true, ("thickness", -.1)), Fx("ellipse", true, ("composite", .5)),
            Fx("paint_bucket"), Fx("paint_bucket", true, ("tolerance", 99.999)), Fx("paint_bucket", true, ("tolerance", 100.001)),
            Fx("paint_bucket", true, ("tolerance", 100), ("selector", .5)), Fx("paint_bucket", true, ("tolerance", 100), ("point_x", 0)),
            Fx("alpha_adjust", true, ("opacity", -.01)), Fx("alpha_adjust", true, ("ignore", .5)), Fx("alpha_adjust", true, ("mask_only", 2)),
            Fx("track_matte", true, ("matte", 3)), Fx("track_matte", true, ("composite", .5)), Fx("track_matte", true, ("reverse", -1)),
            Fx("color_key", true, ("tolerance", 1e-9)), Fx("color_key", true, ("thin", -1)), Fx("color_key", true, ("feather", .001)),
            Fx("color_key", true, ("color_r", double.PositiveInfinity)), Fx("luma_key", true, ("threshold", 100.001)), Fx("luma_key", true, ("cutoff", -.001)),
            Fx("logo_cutout", true, ("background", .5)), Fx("logo_cutout", true, ("softness", -.001)), Fx("logo_cutout", true, ("unmultiply", .5)),
            Fx("logo_cutout", true, ("color_b", 1.001)), Fx("ellipse", true, ("outside_g", -.001)),
        };
        foreach (var effect in invalid)
            foreach (var enabled in new[] { true, false })
            {
                var selected = effect with { Enabled = enabled };
                Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { selected })).Message);
                if (selected.Params.Values.All(double.IsFinite)) Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { selected }))));
            }
    }

    [Theory]
    [InlineData("circle")]
    [InlineData("grid")]
    [InlineData("checkerboard")]
    [InlineData("ellipse")]
    [InlineData("paint_bucket")]
    [InlineData("alpha_adjust")]
    [InlineData("track_matte")]
    [InlineData("color_key")]
    [InlineData("luma_key")]
    [InlineData("logo_cutout")]
    public void R78V11_UnknownParamsRejectCodecAndBuildEvenWhenDisabled(string id)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = Fx(id, enabled, ("unknown", 0));
            Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Fact]
    public void R78V11_RealFfmpegRepeatedOrderedAlphaChainMatchesPreviewAndDiffersFromReversed()
    {
        var chain = new[] { Fx("circle", true, ("center_x", 7.1), ("center_y", 5.3), ("radius", 7.13), ("opacity", 33.17), ("color_r", 170d / 255), ("color_g", 204d / 255), ("color_b", 1)), Fx("alpha_adjust", true, ("opacity", 47.125), ("invert", 1)), Fx("checkerboard", true, ("mode", 4), ("replace", 0), ("width", 3.17), ("feather", 1.3), ("opacity", 33.17), ("color_r", 1), ("color_g", 0), ("color_b", 1)), Fx("track_matte", true, ("matte", 2)), Fx("circle", true, ("radius", 9.17), ("opacity", 47.125), ("color_r", .25), ("color_g", .25), ("color_b", .25)) };
        var parameters = new[] { "{\"center\":[7.1,5.3],\"radius\":7.13,\"opacity\":33.17,\"color\":\"#acf\"}", "{\"opacity\":47.125,\"invert\":true}", "{\"mode\":\"Overlay\",\"width\":3.17,\"feather\":1.3,\"opacity\":33.17,\"color\":\"#f0f\"}", "{\"matte\":\"Luma\"}", "{\"radius\":9.17,\"opacity\":47.125,\"color\":0.25}" };
        var input = R74EXACTSample(32, 16); var actual = R74EXACTRender(input, chain, 32, 16); var expected = R74EXACTPreview(input, chain, parameters, 32, 16);
        Assert.InRange(VideoFxChain.Build(chain).Length, 1, 30_000); Assert.False(actual.SequenceEqual(input)); Assert.False(actual.SequenceEqual(R74EXACTRender(input, chain.Reverse().ToArray(), 32, 16)));
        Assert.Equal(input.Length, actual.Length); Assert.Equal(input.Length, expected.Length);
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
    }

    [Theory]
    [InlineData("circle")]
    [InlineData("grid")]
    [InlineData("checkerboard")]
    [InlineData("ellipse")]
    [InlineData("paint_bucket")]
    [InlineData("alpha_adjust")]
    [InlineData("track_matte")]
    [InlineData("color_key")]
    [InlineData("luma_key")]
    [InlineData("logo_cutout")]
    public void R78V11_RealFfmpegPreservesFrameCountDimensionsAndTimestamps(string id)
    {
        var effect = id == "paint_bucket" ? Fx(id, true, ("tolerance", 100)) : Fx(id); var filter = VideoFxChain.Build(new[] { effect });
        var frames = R71Process(Ffmpeg(), new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=32x16:r=24:duration=0.25", "-vf", filter + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 32x16"); Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(part => part.Trim()).ToArray()).ToArray(); Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++) Assert.Equal(new[] { i.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture), "1", "2048" }, rows[i][1..5]);
    }

    public static IEnumerable<object[]> R80V13Cases()
    {
        var dimensions = new[] { (32, 16), (31, 17), (1, 1), (1, 17), (19, 1), (2, 3), (7, 9), (64, 32) };
        for (var i = 0; i < dimensions.Length; i++)
        {
            var (width, height) = dimensions[i]; var point = new[] { i * 3.17 - 4, i * 1.13 - 3 };
            var angle = new[] { -360d, 360, 0, 33.17, -73.17, .001, 179.999, -13.17 }[i];
            var amount = new[] { 0d, 100, 50, 33.17, 47.125, 1, 99.11, 73.17 }[i];
            var color = new[] { .2, .7, .9 }; var output = i % 3; var setting = i % 4;
            yield return new object[] { "ultra-" + i, Fx("ultra_key", true, ("key_color_r", 0), ("key_color_g", 1), ("key_color_b", 0), ("tolerance", amount), ("spill", amount), ("output", output), ("setting", setting)), JsonSerializer.Serialize(new { key_color = new[] { 0, 1, 0 }, tolerance = amount, spill = amount, output, setting }), width, height };
            var only = i % 2 == 0; var fade = i % 3 != 0; var distance = i % 2 == 0 ? 3.17 : -2.13;
            yield return new object[] { "drop-" + i, Fx("drop_shadow", true, ("direction", angle), ("distance", distance), ("opacity", amount), ("only", only ? 1 : 0), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { direction = angle, distance, opacity = amount, only, color }), width, height };
            var length = i % 3 == 0 ? 0 : i % 3 == 1 ? .7 : 1;
            yield return new object[] { "long-" + i, Fx("long_shadow", true, ("length", length), ("angle", angle), ("fade", fade ? 1 : 0), ("only", only ? 1 : 0), ("opacity", amount), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { length, angle, fade, only, opacity = amount, color }), width, height };
            var z = new[] { 0d, .000999, .001, 100, 1000, 73.17, 173.17, 99.11 }[i]; var easing = i % 4;
            foreach (var id in new[] { "grow", "shrink" })
                yield return new object[] { id + "-" + i, Fx(id, true, ("from", z), ("to", z), ("center_x", point[0]), ("center_y", point[1]), ("easing", easing)), JsonSerializer.Serialize(new { from = z, to = z, center = point, easing }), width, height };
            yield return new object[] { "move-" + i, Fx("move", true, ("from_x", point[0]), ("from_y", point[1]), ("to_x", point[0]), ("to_y", point[1]), ("motion_blur", 0), ("easing", easing)), JsonSerializer.Serialize(new { from = point, to = point, motion_blur = 0, easing }), width, height };
        }
        yield return new object[] { "ultra-default", Fx("ultra_key"), "{}", 32, 16 };
        yield return new object[] { "drop-default", Fx("drop_shadow"), "{}", 32, 16 };
        yield return new object[] { "drop-hidden-rgb-cardinal", Fx("drop_shadow", true, ("direction", -360), ("distance", .5), ("opacity", 100), ("only", 1), ("color_r", 1), ("color_g", 1), ("color_b", 1)), "{\"direction\":-360,\"distance\":0.5,\"opacity\":100,\"only\":true,\"color\":1}", 31, 17 };
    }

    [Theory]
    [MemberData(nameof(R80V13Cases))]
    public void R80V13_RealFfmpegMatchesFinalPreviewRgbAndExactAlphaIncludingHiddenRgb(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var input = R74EXACTSample(width, height);
        for (var i = 3; i < input.Length; i += 4) input[i] = new byte[] { 0, 1, 17, 91, 127, 254, 255 }[(i / 4) % 7];
        var expected = R79V12Preview(input, new[] { effect }, new[] { previewParams }, width, height); var actual = R79V12Render(input, new[] { effect }, width, height);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
    }

    [Theory]
    [MemberData(nameof(R80V13Cases))]
    public void R80V13_CodecCultureDisabledAndAlphaOverlayContracts(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var chain = new[] { Fx("vertical_flip"), effect, Fx("horizontal_flip") }; var json = JsonSerializer.Serialize(chain);
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var filter = VideoFxChain.Build(chain); Assert.Equal(filter, VideoFxChain.Build(item.CatalogFx!));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(filter, VideoFxChain.Build(chain)); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
        var visual = EditorRenderService.VisualFilter(item with { Track = 1 }, width, height, 24);
        Assert.EndsWith(filter + ",premultiply=inplace=1,format=rgb24", visual);
        Assert.Equal(visual.IndexOf("premultiply=inplace=1", StringComparison.Ordinal), visual.LastIndexOf("premultiply=inplace=1", StringComparison.Ordinal));
        foreach (var track in new[] { 2, 3 })
        {
            Assert.Equal("catalog-fx-alpha-overlay-unsupported", Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item with { Track = track }, width, height, 24)).Message);
            Assert.DoesNotContain("premultiply=inplace=1", EditorRenderService.VisualFilter(item with { Track = track, CatalogFx = new[] { effect with { Enabled = false } } }, width, height, 24));
        }
    }

    [Fact]
    public void R80V13_InvalidDomainsRejectBeforeDisabledWithoutSilentCoercion()
    {
        var invalid = new[]
        {
            Fx("ultra_key", true, ("choke", .001)), Fx("ultra_key", true, ("soften", .001)), Fx("ultra_key", true, ("output", .5)), Fx("ultra_key", true, ("setting", 4)), Fx("ultra_key", true, ("spill", double.NaN)), Fx("ultra_key", true, ("key_color_g", 1.001)),
            Fx("drop_shadow", true, ("softness", .001)), Fx("drop_shadow", true, ("direction", 360.001)), Fx("drop_shadow", true, ("distance", -1_000_001)), Fx("drop_shadow", true, ("only", .5)),
            Fx("long_shadow"), Fx("long_shadow", true, ("length", 1.000001)), Fx("long_shadow", true, ("length", -.001)), Fx("long_shadow", true, ("length", 1), ("fade", .5)),
            Fx("grow"), Fx("shrink"), Fx("grow", true, ("from", 73.17), ("to", 73.170001)), Fx("shrink", true, ("from", 1000.001), ("to", 1000.001)), Fx("grow", true, ("from", 100), ("to", 100), ("center_x", 0)),
            Fx("move"), Fx("move", true, ("from_x", 0), ("from_y", 0), ("to_x", 0)), Fx("move", true, ("from_x", 0), ("from_y", 0), ("to_x", 0), ("to_y", .000001)), Fx("move", true, ("from_x", 0), ("from_y", 0), ("to_x", 0), ("to_y", 0), ("motion_blur", .001)),
        };
        foreach (var effect in invalid)
            foreach (var enabled in new[] { true, false })
            {
                var selected = effect with { Enabled = enabled };
                Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { selected })).Message);
                if (selected.Params.Values.All(double.IsFinite)) Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { selected }))));
            }
    }

    [Theory]
    [InlineData("ultra_key")]
    [InlineData("drop_shadow")]
    [InlineData("long_shadow")]
    [InlineData("grow")]
    [InlineData("shrink")]
    [InlineData("move")]
    public void R80V13_UnknownParamsRejectCodecAndBuildEvenWhenDisabled(string id)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = Fx(id, enabled, ("unknown", 0));
            Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Fact]
    public void R80V13_OrderedAlphaChainMatchesPreviewAndDiffersFromReversed()
    {
        var chain = new[] { Fx("drop_shadow", true, ("direction", 33.17), ("distance", 3.17), ("opacity", 47.125)), Fx("ultra_key", true, ("key_color_r", 0), ("key_color_g", 1), ("key_color_b", 0), ("tolerance", 33.17)), Fx("long_shadow", true, ("length", .7), ("angle", -73.17), ("only", 1)), Fx("drop_shadow", true, ("direction", -13.17), ("distance", -2.13), ("color_r", .2), ("color_g", .7), ("color_b", .9)) };
        var parameters = new[] { "{\"direction\":33.17,\"distance\":3.17,\"opacity\":47.125}", "{\"key_color\":[0,1,0],\"tolerance\":33.17}", "{\"length\":0.7,\"angle\":-73.17,\"only\":true}", "{\"direction\":-13.17,\"distance\":-2.13,\"color\":[0.2,0.7,0.9]}" };
        var input = R74EXACTSample(32, 16); var actual = R79V12Render(input, chain, 32, 16); var expected = R79V12Preview(input, chain, parameters, 32, 16);
        Assert.InRange(VideoFxChain.Build(chain).Length, 1, 30_000); Assert.False(actual.SequenceEqual(input)); Assert.False(actual.SequenceEqual(R79V12Render(input, chain.Reverse().ToArray(), 32, 16)));
        Assert.Equal(input.Length, actual.Length); Assert.Equal(input.Length, expected.Length);
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
    }

    public static IEnumerable<object[]> R80V14Cases()
    {
        var dimensions = new[] { (32, 16), (31, 17), (1, 1), (1, 17), (19, 1), (2, 3), (7, 9), (64, 32) };
        for (var i = 0; i < dimensions.Length; i++)
        {
            var (width, height) = dimensions[i]; var point = new[] { i * 3.17 - 4, i * 1.13 - 3 };
            var toggle = i % 2 == 0; var quality = i % 2; var choice = i % 3;
            yield return new object[] { "noise-" + i, Fx("noise", true, ("amount", 0), ("color", toggle ? 1 : 0), ("clip", toggle ? 0 : 1)), JsonSerializer.Serialize(new { amount = 0, color = toggle, clip = !toggle }), width, height };
            yield return new object[] { "basic-" + i, Fx("basic_3d", true, ("swivel", 0), ("tilt", 0), ("distance", 0)), "{\"swivel\":0,\"tilt\":0,\"distance\":0}", width, height };
            yield return new object[] { "turbulent-" + i, Fx("turbulent_displace", true, ("amount", 0), ("displacement", i % 6), ("size", 1_000_000), ("complexity", 3.17), ("evolution", -360), ("cycle", 1), ("cycle_revs", 0), ("seed", -1_000_000), ("pinning", i % 4), ("antialias", quality), ("offset_x", point[0]), ("offset_y", point[1])), JsonSerializer.Serialize(new { amount = 0, displacement = i % 6, size = 1_000_000, complexity = 3.17, evolution = -360, cycle = true, cycle_revs = 0, seed = -1_000_000, pinning = i % 4, antialias = quality, offset = point }), width, height };
            var scale = new[] { 0d, .000999, .001, 100, 1000, 73.17, 173.17, 99.11 }[i]; var easing = i % 4;
            yield return new object[] { "spin-" + i, Fx("spin", true, ("amount", 0), ("scale", scale), ("center_x", point[0]), ("center_y", point[1]), ("easing", easing)), JsonSerializer.Serialize(new { amount = 0, scale, center = point, easing }), width, height };
            yield return new object[] { "camera-" + i, Fx("camera_shake", true, ("amount", 0), ("rotation", 0), ("zoom", 0), ("motion_blur", 0), ("complexity", 3.17), ("frequency", 1_000_000), ("seed", -1_000_000)), "{\"amount\":0,\"rotation\":0,\"zoom\":0,\"motion_blur\":0,\"complexity\":3.17,\"frequency\":1000000,\"seed\":-1000000}", width, height };
            yield return new object[] { "wiggle-" + i, Fx("wiggle", true, ("amount", 0), ("rotation", 0), ("scale", 0), ("frequency", 1_000_000), ("seed", -1_000_000), ("dimensions", choice)), JsonSerializer.Serialize(new { amount = 0, rotation = 0, scale = 0, frequency = 1_000_000, seed = -1_000_000, dimensions = choice }), width, height };
            var color = new[] { .111, .347, .999 }; var mode = i % 5;
            yield return new object[] { "wonder-" + i, Fx("wonder_glow", true, ("intensity", 0), ("threshold", 0), ("radius", 1_000_000), ("saturation", 200), ("mode", mode), ("use_color", 1), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { intensity = 0, threshold = 0, radius = 1_000_000, saturation = 200, mode, use_color = true, color }), width, height };
            var strokeWidth = toggle ? 1_000_000 : 0; var opacity = toggle ? 0 : 73.17;
            yield return new object[] { "stroke-" + i, Fx("stroke", true, ("width", strokeWidth), ("opacity", opacity), ("only", toggle ? 1 : 0), ("position", choice), ("color_r", color[0]), ("color_g", color[1]), ("color_b", color[2])), JsonSerializer.Serialize(new { width = strokeWidth, opacity, only = toggle, position = choice, color }), width, height };
        }
        yield return new object[] { "noise-default", Fx("noise"), "{}", 32, 16 };
        yield return new object[] { "basic-default", Fx("basic_3d"), "{}", 32, 16 };
        yield return new object[] { "stroke-width-zero-only", Fx("stroke", true, ("width", 0), ("only", 1)), "{\"width\":0,\"only\":true}", 31, 17 };
    }

    [Theory]
    [MemberData(nameof(R80V14Cases))]
    public void R80V14_RealFfmpegMatchesFinalPreviewRgbAndExactAlphaIncludingHiddenRgb(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var input = R74EXACTSample(width, height);
        for (var i = 3; i < input.Length; i += 4) input[i] = new byte[] { 0, 1, 17, 91, 127, 254, 255 }[(i / 4) % 7];
        var expected = R79V12Preview(input, new[] { effect }, new[] { previewParams }, width, height); var actual = R79V12Render(input, new[] { effect }, width, height);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
    }

    [Theory]
    [MemberData(nameof(R80V14Cases))]
    public void R80V14_CodecCultureDisabledAndAlphaOverlayContracts(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var chain = new[] { Fx("vertical_flip"), effect, Fx("horizontal_flip") }; var json = JsonSerializer.Serialize(chain);
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var filter = VideoFxChain.Build(chain); Assert.Equal(filter, VideoFxChain.Build(item.CatalogFx!));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(filter, VideoFxChain.Build(chain)); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
        var visual = EditorRenderService.VisualFilter(item with { Track = 1 }, width, height, 24);
        Assert.EndsWith(filter + ",premultiply=inplace=1,format=rgb24", visual);
        Assert.Equal(visual.IndexOf("premultiply=inplace=1", StringComparison.Ordinal), visual.LastIndexOf("premultiply=inplace=1", StringComparison.Ordinal));
        foreach (var track in new[] { 2, 3 })
        {
            Assert.Equal("catalog-fx-alpha-overlay-unsupported", Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item with { Track = track }, width, height, 24)).Message);
            Assert.DoesNotContain("premultiply=inplace=1", EditorRenderService.VisualFilter(item with { Track = track, CatalogFx = new[] { effect with { Enabled = false } } }, width, height, 24));
        }
    }

    [Fact]
    public void R80V14_InvalidDomainsRejectBeforeDisabledWithoutSilentCoercion()
    {
        var invalid = new[]
        {
            Fx("noise", true, ("amount", .001)), Fx("noise", true, ("clip", .5)), Fx("noise", true, ("amount", double.NaN)),
            Fx("basic_3d", true, ("swivel", .001)), Fx("basic_3d", true, ("tilt", -.001)), Fx("basic_3d", true, ("distance", .001)),
            Fx("turbulent_displace"), Fx("turbulent_displace", true, ("amount", 0), ("displacement", .5)), Fx("turbulent_displace", true, ("amount", 0), ("antialias", 2)), Fx("turbulent_displace", true, ("amount", 0), ("offset_x", 0)), Fx("turbulent_displace", true, ("amount", 0), ("complexity", 5.001)),
            Fx("spin"), Fx("spin", true, ("amount", 0), ("scale", 1000.001)), Fx("spin", true, ("amount", 0), ("center_y", 0)), Fx("spin", true, ("amount", 0), ("easing", .5)),
            Fx("camera_shake"), Fx("camera_shake", true, ("amount", 0), ("rotation", 0)), Fx("camera_shake", true, ("amount", 0), ("rotation", 0), ("zoom", 0), ("motion_blur", .001)),
            Fx("wiggle"), Fx("wiggle", true, ("amount", 0), ("rotation", .001)), Fx("wiggle", true, ("amount", 0), ("scale", .001)), Fx("wiggle", true, ("amount", 0), ("dimensions", .5)),
            Fx("wonder_glow"), Fx("wonder_glow", true, ("intensity", .001)), Fx("wonder_glow", true, ("intensity", 0), ("mode", .5)), Fx("wonder_glow", true, ("intensity", 0), ("color_g", 1.001)),
            Fx("stroke"), Fx("stroke", true, ("width", .001), ("opacity", .001)), Fx("stroke", true, ("width", -.001)), Fx("stroke", true, ("width", 0), ("only", .5)),
        };
        foreach (var effect in invalid)
            foreach (var enabled in new[] { true, false })
            {
                var selected = effect with { Enabled = enabled };
                Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { selected })).Message);
                if (selected.Params.Values.All(double.IsFinite)) Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { selected }))));
            }
    }

    [Theory]
    [InlineData("noise")]
    [InlineData("basic_3d")]
    [InlineData("turbulent_displace")]
    [InlineData("spin")]
    [InlineData("camera_shake")]
    [InlineData("wiggle")]
    [InlineData("wonder_glow")]
    [InlineData("stroke")]
    public void R80V14_UnknownParamsRejectCodecAndBuildEvenWhenDisabled(string id)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = Fx(id, enabled, ("unknown", 0));
            Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Fact]
    public void R80V14_OrderedAlphaChainMatchesPreviewAndDiffersFromReversed()
    {
        var chain = new[] { Fx("spin", true, ("amount", 0), ("scale", 173.17), ("center_x", 7.13), ("center_y", 5.17)), Fx("stroke", true, ("width", 0)), Fx("wonder_glow", true, ("intensity", 0)), Fx("spin", true, ("amount", 0), ("scale", 73.17), ("center_x", 3.17), ("center_y", -1.13)) };
        var parameters = new[] { "{\"amount\":0,\"scale\":173.17,\"center\":[7.13,5.17]}", "{\"width\":0}", "{\"intensity\":0}", "{\"amount\":0,\"scale\":73.17,\"center\":[3.17,-1.13]}" };
        var input = R74EXACTSample(32, 16); var actual = R79V12Render(input, chain, 32, 16); var expected = R79V12Preview(input, chain, parameters, 32, 16);
        Assert.InRange(VideoFxChain.Build(chain).Length, 1, 30_000); Assert.False(actual.SequenceEqual(input)); Assert.False(actual.SequenceEqual(R79V12Render(input, chain.Reverse().ToArray(), 32, 16)));
        Assert.Equal(input.Length, actual.Length); Assert.Equal(input.Length, expected.Length);
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
    }

    public static IEnumerable<object[]> R79V12Cases()
    {
        var ids = new[] { "mirror", "lens_distortion", "twirl", "wave_warp", "spherize", "warp_stabilizer", "spacer", "magnify" };
        foreach (var id in ids)
            yield return new object[] { id + "-default-static", id == "wave_warp" ? Fx(id, true, ("speed", 0)) : Fx(id), id == "wave_warp" ? "{\"speed\":0}" : "{}", 32, 16 };
        var dimensions = new[] { (32, 16), (31, 17), (1, 1), (1, 17), (19, 1), (2, 3), (7, 9), (64, 32) };
        for (var i = 0; i < dimensions.Length; i++)
        {
            var (width, height) = dimensions[i]; var point = new[] { i * 3.17 - 4, i * 1.13 - 3 };
            var angle = new[] { -360d, 360, 0, 33.17, -73.17, .001, 179.999, -13.17 }[i];
            var amount = new[] { 0d, 100, 50, 33.17, 47.125, 1, 99.11, 73.17 }[i];
            var radius = new[] { 0d, 1_000_000, .01, .5, 1, 3.17, 13.17, 100 }[i];
            yield return new object[] { "mirror-" + i, Fx("mirror", true, ("center_x", point[0]), ("center_y", point[1]), ("angle", angle)), JsonSerializer.Serialize(new { center = point, angle }), width, height };
            var curvature = amount * 2 - 100;
            yield return new object[] { "lens-" + i, Fx("lens_distortion", true, ("curvature", curvature), ("h_decentering", -curvature), ("v_decentering", curvature / 3)), JsonSerializer.Serialize(new { curvature, h_decentering = -curvature, v_decentering = curvature / 3 }), width, height };
            yield return new object[] { "twirl-" + i, Fx("twirl", true, ("center_x", point[0]), ("center_y", point[1]), ("angle", angle), ("radius", amount)), JsonSerializer.Serialize(new { center = point, angle, radius = amount }), width, height };
            var waveWidth = new[] { 0d, -1_000_000, .01, -.1, 1, 3.17, -7.13, 40 }[i]; var waveHeight = i % 2 == 0 ? -radius : radius;
            yield return new object[] { "wave-" + i, Fx("wave_warp", true, ("speed", 0), ("height", waveHeight), ("width", waveWidth), ("direction", angle)), JsonSerializer.Serialize(new { speed = 0, height = waveHeight, width = waveWidth, direction = angle }), width, height };
            yield return new object[] { "sphere-" + i, Fx("spherize", true, ("center_x", point[0]), ("center_y", point[1]), ("radius", radius)), JsonSerializer.Serialize(new { center = point, radius }), width, height };
            var method = i % 4; var framing = (i + 1) % 4; var result = i % 2; var preserve = i / 2 % 2 != 0; var detailed = i % 2 == 0;
            var motionScale = i % 2 == 0 ? 0 : 93.17; var extra = i % 3 == 0 ? 0 : 103.17;
            yield return new object[] { "stabilizer-" + i, Fx("warp_stabilizer", true, ("motion_x", point[0]), ("motion_y", point[1]), ("result", result), ("method", method), ("framing", framing), ("smoothness", amount), ("preserve_scale", preserve ? 1 : 0), ("detailed", detailed ? 1 : 0), ("motion_scale", motionScale), ("additional_scale", extra), ("motion_rotation", angle), ("motion_shear", curvature), ("action_safe", amount), ("crop_less", 100 - amount)), JsonSerializer.Serialize(new { motion = point, result, method, framing, smoothness = amount, preserve_scale = preserve, detailed, motion_scale = motionScale, additional_scale = extra, motion_rotation = angle, motion_shear = curvature, action_safe = amount, crop_less = 100 - amount }), width, height };
            var left = i % 3 == 0 ? 0 : i * .17; var top = i * .13; var right = i * .11; var bottom = i * .07; var uniform = i % 2 != 0;
            yield return new object[] { "spacer-" + i, Fx("spacer", true, ("left", left), ("top", top), ("right", right), ("bottom", bottom), ("radius", radius), ("uniform", uniform ? 1 : 0), ("fill", 0)), JsonSerializer.Serialize(new { left, top, right, bottom, radius, uniform, fill = false }), width, height };
            var magnification = new[] { 0d, 1000, 100, 73.17, 200, 1, 99.11, 173.17 }[i]; var feather = i % 2 == 0 ? 0 : radius / 3;
            yield return new object[] { "magnify-" + i, Fx("magnify", true, ("center_x", point[0]), ("center_y", point[1]), ("magnification", magnification), ("size", radius), ("feather", feather), ("shape", i % 2), ("mode", i % 4), ("opacity", amount)), JsonSerializer.Serialize(new { center = point, magnification, size = radius, feather, shape = i % 2, mode = i % 4, opacity = amount }), width, height };
        }
    }

    private static byte[] R79V12Preview(byte[] input, IReadOnlyList<CatalogFxInstance> effects, IReadOnlyList<string> parameters, int width, int height)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "filmcraft-preview", "wwwroot", "js", "fx-engine.js"))) directory = directory.Parent;
        Assert.NotNull(directory);
        const string script = "const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');const p=JSON.parse(fs.readFileSync(0,'utf8'));const c=vm.createContext({window:{},console:{...console,warn(...a){throw Error(a.join(' '));}},Uint8ClampedArray,Float32Array});for(const name of ['fx-engine.js','fx-more-a.js','fx-more-b.js']){vm.runInContext(fs.readFileSync(path.join(p.root,'filmcraft-preview','wwwroot','js',name),'utf8'),c);c.FX=c.window.FX;}const pixels=new Uint8ClampedArray(Buffer.from(p.input,'base64'));for(let i=0;i<p.ids.length;i++){if(c.FX.status(p.ids[i])!=='real')throw Error('preview registration missing '+p.ids[i]);c.FX.apply(pixels,p.width,p.height,p.ids[i],p.params[i],{seconds:0,fps:24,pxScale:1,clipId:'r79-v12-'+i});}process.stdout.write(Buffer.from(pixels));";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { root = directory!.FullName, input = Convert.ToBase64String(input), ids = effects.Select(effect => effect.Id), @params = parameters.Select(value => JsonSerializer.Deserialize<JsonElement>(value)), width, height });
        return R71Process(Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_NODE") ?? "node", new[] { "-e", script }, payload);
    }

    private static string R79V12Ffmpeg()
    {
        var path = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        Assert.False(string.IsNullOrWhiteSpace(path), "R79V12 requires LIVECLIPPER_TEST_FFMPEG; this test must not skip or use a PATH fallback.");
        Assert.True(File.Exists(path), "LIVECLIPPER_TEST_FFMPEG must name an existing ffmpeg executable.");
        return path!;
    }

    private static byte[] R79V12Render(byte[] input, IReadOnlyList<CatalogFxInstance> effects, int width, int height)
    {
        var filter = VideoFxChain.Build(effects);
        return R71Process(R79V12Ffmpeg(), new[] { "-v", "error", "-threads", "1", "-filter_threads", "1", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", width + "x" + height, "-i", "pipe:0", "-vf", (filter.Length == 0 ? "null" : filter) + ",format=rgba", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }, input);
    }

    [Theory]
    [MemberData(nameof(R79V12Cases))]
    public void R79V12_RealFfmpegMatchesFinalPreviewRgbAndExactAlphaIncludingHiddenRgb(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var input = R74EXACTSample(width, height);
        for (var i = 3; i < input.Length; i += 4) input[i] = new byte[] { 0, 1, 17, 91, 127, 254, 255 }[(i / 4) % 7];
        var expected = R79V12Preview(input, new[] { effect }, new[] { previewParams }, width, height); var actual = R79V12Render(input, new[] { effect }, width, height);
        Assert.Equal(input.Length, expected.Length); Assert.Equal(input.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
    }

    [Theory]
    [MemberData(nameof(R79V12Cases))]
    public void R79V12_CodecCultureDisabledAndAlphaOverlayContracts(string name, CatalogFxInstance effect, string previewParams, int width, int height)
    {
        var chain = new[] { Fx("vertical_flip"), effect, Fx("horizontal_flip") }; var json = JsonSerializer.Serialize(chain);
        var item = Assert.Single(EditorProjectCodec.Deserialize(CatalogProject(json)).Timeline!);
        Assert.Equal(1, item.CatalogFxVersion); Assert.Equal(json, JsonSerializer.Serialize(item.CatalogFx));
        var filter = VideoFxChain.Build(chain); Assert.Equal(filter, VideoFxChain.Build(item.CatalogFx!));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(filter, VideoFxChain.Build(chain)); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("", VideoFxChain.Build(new[] { effect with { Enabled = false } }));
        var visual = EditorRenderService.VisualFilter(item with { Track = 1 }, width, height, 24);
        Assert.EndsWith(filter + ",premultiply=inplace=1,format=rgb24", visual);
        Assert.Equal(visual.IndexOf("premultiply=inplace=1", StringComparison.Ordinal), visual.LastIndexOf("premultiply=inplace=1", StringComparison.Ordinal));
        foreach (var track in new[] { 2, 3 })
        {
            Assert.Equal("catalog-fx-alpha-overlay-unsupported", Assert.Throws<ArgumentException>(() => EditorRenderService.VisualFilter(item with { Track = track }, width, height, 24)).Message);
            Assert.DoesNotContain("premultiply=inplace=1", EditorRenderService.VisualFilter(item with { Track = track, CatalogFx = new[] { effect with { Enabled = false } } }, width, height, 24));
        }
    }

    [Fact]
    public void R79V12_InvalidDomainsRejectBeforeDisabledWithoutSilentCoercion()
    {
        var invalid = new[]
        {
            Fx("mirror", true, ("angle", 360.001)), Fx("mirror", true, ("center_x", 0)), Fx("mirror", true, ("angle", double.NaN)),
            Fx("lens_distortion", true, ("curvature", 100.001)), Fx("lens_distortion", true, ("h_decentering", -100.001)),
            Fx("twirl", true, ("radius", -.001)), Fx("twirl", true, ("radius", 100.001)), Fx("spherize", true, ("radius", 1_000_001)),
            Fx("wave_warp"), Fx("wave_warp", true, ("speed", 1)), Fx("wave_warp", true, ("speed", -1e-9)), Fx("wave_warp", true, ("speed", 0), ("width", -1_000_001)),
            Fx("spacer", true, ("fill", 1)), Fx("spacer", true, ("uniform", .5)), Fx("spacer", true, ("left", -.001)), Fx("spacer", true, ("color_r", 1.001)),
            Fx("magnify", true, ("shape", .5)), Fx("magnify", true, ("mode", 4)), Fx("magnify", true, ("mode", .5)), Fx("magnify", true, ("opacity", 100.001)), Fx("magnify", true, ("magnification", 1000.001)),
            Fx("warp_stabilizer", true, ("motion_y", 0)), Fx("warp_stabilizer", true, ("result", .5)), Fx("warp_stabilizer", true, ("method", 4)), Fx("warp_stabilizer", true, ("framing", .5)),
            Fx("warp_stabilizer", true, ("preserve_scale", .5)), Fx("warp_stabilizer", true, ("detailed", 2)), Fx("warp_stabilizer", true, ("motion_shear", 100.001)), Fx("warp_stabilizer", true, ("motion_scale", double.PositiveInfinity)),
        };
        foreach (var effect in invalid)
            foreach (var enabled in new[] { true, false })
            {
                var selected = effect with { Enabled = enabled };
                Assert.Equal(VideoFxChain.InvalidParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { selected })).Message);
                if (selected.Params.Values.All(double.IsFinite)) Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { selected }))));
            }
    }

    [Theory]
    [InlineData("mirror")]
    [InlineData("lens_distortion")]
    [InlineData("twirl")]
    [InlineData("wave_warp")]
    [InlineData("spherize")]
    [InlineData("warp_stabilizer")]
    [InlineData("spacer")]
    [InlineData("magnify")]
    public void R79V12_UnknownParamsRejectCodecAndBuildEvenWhenDisabled(string id)
    {
        foreach (var enabled in new[] { true, false })
        {
            var effect = Fx(id, enabled, ("unknown", 0));
            Assert.Equal(VideoFxChain.UnsupportedParam, Assert.Throws<ArgumentException>(() => VideoFxChain.Build(new[] { effect })).Message);
            Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.Deserialize(CatalogProject(JsonSerializer.Serialize(new[] { effect }))));
        }
    }

    [Fact]
    public void R79V12_RealFfmpegRepeatedOrderedAlphaChainMatchesPreviewAndDiffersFromReversed()
    {
        var chain = new[] { Fx("mirror", true, ("angle", 33.17), ("center_x", 13.17), ("center_y", 5.17)), Fx("lens_distortion", true, ("curvature", 13.17)), Fx("magnify", true, ("center_x", 7.13), ("center_y", 5.17), ("size", 8.17), ("feather", 2.13), ("mode", 1), ("opacity", 47.125)), Fx("mirror", true, ("angle", -13.17), ("center_x", 17.13), ("center_y", 9.17)) };
        var parameters = new[] { "{\"angle\":33.17,\"center\":[13.17,5.17]}", "{\"curvature\":13.17}", "{\"center\":[7.13,5.17],\"size\":8.17,\"feather\":2.13,\"mode\":\"Screen\",\"opacity\":47.125}", "{\"angle\":-13.17,\"center\":[17.13,9.17]}" };
        var input = R74EXACTSample(32, 16); var actual = R79V12Render(input, chain, 32, 16); var expected = R79V12Preview(input, chain, parameters, 32, 16);
        Assert.InRange(VideoFxChain.Build(chain).Length, 1, 30_000); Assert.False(actual.SequenceEqual(input)); Assert.False(actual.SequenceEqual(R79V12Render(input, chain.Reverse().ToArray(), 32, 16)));
        Assert.Equal(input.Length, actual.Length); Assert.Equal(input.Length, expected.Length);
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 == 3) Assert.Equal(expected[i], actual[i]);
            else Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
    }

    [Theory]
    [InlineData("mirror")]
    [InlineData("lens_distortion")]
    [InlineData("twirl")]
    [InlineData("wave_warp")]
    [InlineData("spherize")]
    [InlineData("warp_stabilizer")]
    [InlineData("spacer")]
    [InlineData("magnify")]
    public void R79V12_RealFfmpegPreservesFrameCountDimensionsAndTimestamps(string id)
    {
        var effect = id == "wave_warp" ? Fx(id, true, ("speed", 0)) : id == "spherize" ? Fx(id, true, ("radius", 7.13)) : Fx(id); var filter = VideoFxChain.Build(new[] { effect });
        var frames = R71Process(R79V12Ffmpeg(), new[] { "-v", "error", "-threads", "1", "-filter_threads", "1", "-f", "lavfi", "-i", "testsrc2=s=32x16:r=24:duration=0.25", "-vf", (filter.Length == 0 ? "null" : filter) + ",format=rgba", "-f", "framemd5", "pipe:1" }, Array.Empty<byte>());
        var lines = System.Text.Encoding.UTF8.GetString(frames).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.TrimEnd() == "#dimensions 0: 32x16"); Assert.Contains(lines, line => line.TrimEnd() == "#tb 0: 1/24");
        var rows = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Select(part => part.Trim()).ToArray()).ToArray(); Assert.Equal(6, rows.Length);
        for (var i = 0; i < rows.Length; i++) Assert.Equal(new[] { i.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture), "1", "2048" }, rows[i][1..5]);
    }
}
