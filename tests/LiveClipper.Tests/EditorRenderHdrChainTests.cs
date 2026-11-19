using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class EditorRenderHdrChainTests(ITestOutputHelper output)
{
    private const int Width = 1024;
    private const int Height = 128;
    private const string LegacyCodec = "-c:v|libx264|-preset|veryfast|-crf|20|-pix_fmt|yuv420p";
    private const string BaseFilter = "scale=1024:128:force_original_aspect_ratio=decrease,pad=1024:128:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=24";

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("h264", "hdr10", false)]
    [InlineData("hevc", null, false)]
    [InlineData("hevc", "bt709", false)]
    [InlineData("hevc", "hdr10", true)]
    [InlineData("prores", null, true)]
    [InlineData("prores", "bt709", true)]
    [InlineData("prores", "hdr10", true)]
    [InlineData("dnxhr", null, false)]
    [InlineData("dnxhr", "bt709", false)]
    [InlineData("dnxhr", "hdr10", true)]
    public void IntermediatePolicy_SelectsHighDepthOnlyForHighDepthExports(string? format, string? color, bool high)
    {
        var plan = new EditorProject { Format = format, ColorSpace = color };
        Assert.Equal(high, EditorRenderService.RequiresHighBitDepth(plan));
        var args = EditorRenderService.IntermediateVideoCodecArgs(plan);
        const string lossless = "-c:v|ffv1|-level|3|-slices|4|-slicecrc|1";
        var colorArgs = color == "hdr10" ? "|-color_primaries|bt2020|-color_trc|smpte2084|-colorspace|bt2020nc" : "";
        Assert.Equal(lossless + (high ? "|-pix_fmt|yuv420p10le" : "") + colorArgs, string.Join('|', args));
        Assert.Equal(lossless + colorArgs,
            string.Join('|', EditorRenderService.IntermediateVideoCodecArgs(plan, explicitPixelFormat: false)));
        Assert.Equal("ffv1", args[1]);
        Assert.Equal(high, args.Contains("-pix_fmt"));
        Assert.Equal(color == "hdr10", args.Contains("-color_trc"));
        Assert.DoesNotContain("tonemap", string.Join(' ', args));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("h264", "hdr10")]
    [InlineData("hevc", null)]
    [InlineData("hevc", "bt709")]
    [InlineData("dnxhr", null)]
    [InlineData("dnxhr", "bt709")]
    public void SdrPolicy_PreservesEveryArgumentByteForAllSevenIntermediateShapes(string? format, string? color)
    {
        var plan = new EditorProject { Format = format, ColorSpace = color };
        foreach (var args in IntermediateCommands(plan))
        {
            var expected = new List<string>(args);
            if (color == "hdr10")
                for (var i = expected.Count - 2; i >= 0; i--)
                    if (expected[i] == "-i") expected.InsertRange(i, ["-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc"]);
            EditorRenderService.ConfigureIntermediateArgs(args, plan);
            Assert.Equal(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expected)), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args)));
        }
    }

    [Fact]
    public void HighDepthPolicy_ReplacesEveryIntermediateCodecWithoutChangingAudioOrOutput()
    {
        var plan = new EditorProject { Format = "hevc", ColorSpace = "hdr10" };
        foreach (var args in IntermediateCommands(plan))
        {
            var outputPath = args[^1];
            var audio = args.Skip(args.IndexOf("-c:a")).ToArray();
            EditorRenderService.ConfigureIntermediateArgs(args, plan);
            Assert.Equal("ffv1", args[args.IndexOf("-c:v") + 1]);
            Assert.Equal("yuv420p10le", args[args.IndexOf("-pix_fmt") + 1]);
            Assert.Equal(outputPath, args[^1]);
            Assert.Equal(audio, args.Skip(args.IndexOf("-c:a")).ToArray());
            Assert.DoesNotContain("libx264", args);
            Assert.DoesNotContain("yuv420p", args);
            Assert.Equal("smpte2084", args[args.IndexOf("-color_trc") + 1]);
        }
    }

    [Fact]
    public void SdrVisualFilter_DefaultAndEffectsAreCharacterForCharacterUnchanged()
    {
        Assert.Equal(BaseFilter, EditorRenderService.VisualFilter(new("v1", "c", 0, 1), Width, Height, 24));
        var item = new TimelineItem("v1", "c", 0, 1, Motion: "push", Filter: "vivid",
            Grade: new ColorGrade(.1, 1.2, .8, 0), FadeIn: .25, FadeOut: .25);
        var expected = BaseFilter + ",zoompan=z='1+0.12*min(on/24,1)':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s=1024x128:fps=24"
            + ",eq=saturation=1.3:contrast=1.08,eq=brightness=0.1:contrast=1.2:saturation=0.8"
            + ",fade=t=in:st=0:d=0.25,fade=t=out:st=0.75:d=0.25";
        Assert.Equal(expected, EditorRenderService.VisualFilter(item, Width, Height, 24));
        Assert.Equal(expected, EditorRenderService.VisualFilter(item, Width, Height, 24, highBitDepth: false));
    }

    [Fact]
    public void HighDepthVisualFilter_AvoidsEightBitEqAndZoompanIncludingKeyframes()
    {
        var item = new TimelineItem("v1", "c", 0, 1, Motion: "push", Filter: "vivid", Grade: new ColorGrade(.02, 1.1, .9));
        var filter = EditorRenderService.VisualFilter(item, Width, Height, 24, highBitDepth: true);
        Assert.StartsWith("format=yuv420p10le," + BaseFilter, filter);
        Assert.DoesNotContain("zoompan=", filter);
        Assert.DoesNotContain(",eq=", filter);
        Assert.Contains(EditorRenderService.HighBitDepthZoom("1+0.12*min(N/24,1)"), filter);
        Assert.Contains(EditorRenderService.HighBitDepthEq(.02, 1.1, .9), filter);
        var keyed = item with { Keyframes = new() { ["scale"] = [new(0, 1), new(1, 1.2)] } };
        filter = EditorRenderService.VisualFilter(keyed, Width, Height, 24, highBitDepth: true);
        Assert.Contains("geq=lum='lum((X-W/2)/max(1,(", filter);
        Assert.Contains("N/24", filter);
        Assert.DoesNotContain("zoompan=", filter);
        Assert.DoesNotContain("on/", filter);
    }

    [Fact]
    public void HighDepthGraph_PreservesImageDepthAndUsesFullRangeMatteCoverage()
    {
        const string graph = "[0:v]format=yuv420p[vbase];[1:v]format=gray[vmatte];color=black[vmaskblack];"
            + "[vbase][vmaskblack][vmatte]maskedmerge[vmasked];[vmasked]format=rgb24[vopacity];"
            + "color=black[vopacityblack];[vopacity][vopacityblack]blend=all_expr='A*0.75',format=yuv420p[vout];"
            + "[2:v]format=rgba[sticker];[vout][sticker]overlay=x=0:y=0[composed]";
        var result = EditorRenderService.HighBitDepthFilterGraph(graph);
        Assert.DoesNotContain("format=yuv420p[", result);
        Assert.DoesNotContain("format=gray", result);
        Assert.DoesNotContain("format=rgb24", result);
        Assert.DoesNotContain("format=rgba", result);
        Assert.Contains("format=gbrap16le[sticker]", result);
        Assert.Contains("format=gbrp16le[vmatte]", result);
        Assert.Contains("[hdrmasksource][hdrmaskblack][vmatte]maskedmerge", result);
        Assert.Contains("[vopacityblack]format=gbrp16le[hdrblack];[vopacity][hdrblack]blend", result);
        Assert.Contains("overlay=format=yuv420p10:x=0", result);
        Assert.Equal("[0:v]lut=y=64:u=512:v=512[hidden]", EditorRenderService.HighBitDepthFilterGraph(
            "[0:v]drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill[hidden]"));
    }

    [Fact]
    public void FinalCodec_HevcHdrMetadataAndLegacySdrRemainExact()
    {
        Assert.Equal("-c:v|libx265|-preset|fast|-tag:v|hvc1|-crf|24|-pix_fmt|yuv420p10le|-x265-params|level-idc=3.1:high-tier=0:colorprim=9:transfer=16:colormatrix=9:hdr10=1:hdr10-opt=1:repeat-headers=1|-color_primaries|bt2020|-color_trc|smpte2084|-colorspace|bt2020nc",
            string.Join('|', EditorRenderService.VideoCodecArgs("hevc", 0, Width, Height, 24, "hdr10")));
        Assert.Equal("-c:v|libx265|-preset|fast|-tag:v|hvc1|-crf|24|-pix_fmt|yuv420p|-x265-params|level-idc=3.1:high-tier=0",
            string.Join('|', EditorRenderService.VideoCodecArgs("hevc", 0, Width, Height, 24)));
        Assert.Equal("-c:v|libx264|-preset|veryfast|-crf|22|-pix_fmt|yuv420p", string.Join('|', EditorRenderService.VideoCodecArgs("h264", 0)));
        Assert.Equal("-c:v|prores_ks|-profile:v|3|-vendor|apl0|-pix_fmt|yuv422p10le", string.Join('|', EditorRenderService.VideoCodecArgs("prores", 0)));
        Assert.Equal("-c:v|dnxhd|-profile:v|dnxhr_sq|-pix_fmt|yuv422p", string.Join('|', EditorRenderService.VideoCodecArgs("dnxhr", 0)));
        Assert.Equal("-c:v|dnxhd|-profile:v|dnxhr_hqx|-pix_fmt|yuv422p10le|-color_primaries|bt2020|-color_trc|smpte2084|-colorspace|bt2020nc",
            string.Join('|', EditorRenderService.VideoCodecArgs("dnxhr", 0, colorSpace: "hdr10")));
    }

    [Theory]
    [InlineData("hevc", false)]
    [InlineData("hevc", true)]
    [InlineData("prores", false)]
    [InlineData("dnxhr", false)]
    public async Task RealRender_IntermediateAndFinalRetainPqAndMoreThan256LumaLevels(string format, bool effects)
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return; // AvailableToolsAsync always writes an explicit [skip].
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        var root = Path.Combine(Path.GetTempPath(), "liveclipper-hdr-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        EditorRenderService? renderer = null;
        try
        {
            var store = new Store(Path.Combine(root, "test.db")); store.Migrate();
            var studio = new StudioStore(store.DatabasePath); studio.Migrate();
            renderer = new EditorRenderService(store, studio, tools, root);
            var source = Path.Combine(root, "source.mkv");
            var hdr = new EditorProject { Format = "hevc", ColorSpace = "hdr10" };
            await tools.RunAsync(tools.Ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "nullsrc=s=1024x128:r=24,format=yuv420p10le,geq=lum='64+floor(X*876/1023)':cb=512:cr=512",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "2",
                .. EditorRenderService.IntermediateVideoCodecArgs(hdr), "-c:a", "aac", source
            ], token);
            var clip = new Clip("gradient", "event-gradient", source, null, DateTimeOffset.UtcNow);
            store.SaveClip(clip);
            var first = new TimelineItem("first", clip.Id, 0, 1);
            if (effects) first = first with
            {
                Opacity = .85, Motion = "push", Filter = "vivid", Grade = new ColorGrade(.01, 1.02, .9),
                Masks = [new("mask", "rect", true, 0, 0, 1, .5, 0, 0, 1)]
            };
            var plan = hdr with
            {
                Format = format, Resolution = 0, Fps = 24,
                Timeline = [first, new("second", clip.Id, 0, 1), new("overlay", clip.Id, 0, 1.75, Track: 2, Opacity: .75)],
                Transitions = [new("fade", "second", 1, "cross-dissolve", .25, "center")]
            };
            var work = Path.Combine(root, "work");
            var final = Path.Combine(root, "final" + EditorRenderService.OutputExtension(format));
            await renderer.RenderAsync(clip, plan, final, work, token);
            foreach (var name in new[] { "part-000.mkv", "part-001.mkv", "joined.mkv", "overlay-000.mkv", "composed.mkv" })
                await AssertTenBitPqAsync(tools, Path.Combine(work, name), root, token);
            await AssertTenBitPqAsync(tools, final, root, token);
        }
        finally
        {
            if (renderer is not null) await renderer.StopAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException ex) { output.WriteLine("[cleanup] " + ex.Message); }
            catch (UnauthorizedAccessException ex) { output.WriteLine("[cleanup] " + ex.Message); }
        }
    }

    private async Task AssertTenBitPqAsync(MediaTools tools, string path, string root, CancellationToken token)
    {
        Assert.True(File.Exists(path), "Missing render stage: " + path);
        var probe = await tools.RunAsync(tools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt,color_primaries,color_transfer,color_space", "-of", "json", path], token);
        output.WriteLine(Path.GetFileName(path) + ": " + probe);
        using var doc = JsonDocument.Parse(probe);
        var video = Assert.Single(doc.RootElement.GetProperty("streams").EnumerateArray());
        Assert.Contains(video.GetProperty("pix_fmt").GetString(), new[] { "yuv420p10le", "yuv422p10le", "yuv422p12le" });
        Assert.Equal("bt2020", video.GetProperty("color_primaries").GetString());
        Assert.Equal("smpte2084", video.GetProperty("color_transfer").GetString());
        Assert.Equal("bt2020nc", video.GetProperty("color_space").GetString());
        var raw = Path.Combine(root, "luma.yuv");
        await tools.RunAsync(tools.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-ss", "0.5", "-i", path, "-vf", "format=yuv420p10le", "-frames:v", "1", "-pix_fmt", "yuv420p10le", "-f", "rawvideo", raw], token);
        var bytes = await File.ReadAllBytesAsync(raw, token);
        Assert.True(bytes.Length >= Width * Height * 2, "No full luma plane decoded.");
        var levels = new HashSet<int>(); var lowBits = 0;
        for (var i = 0; i < Width * Height; i++)
        {
            var sample = bytes[2 * i] | (bytes[2 * i + 1] << 8);
            levels.Add(sample); if (sample % 4 != 0) lowBits++;
        }
        output.WriteLine($"{Path.GetFileName(path)}: uniqueLumaLevels={levels.Count}; nonZeroLowBits={lowBits}");
        Assert.True(levels.Count > 256, $"{path}: only {levels.Count} luma levels; possible 8-bit quantization.");
        Assert.True(lowBits > 0, path + ": all low bits were zero.");
    }

    private async Task<MediaTools?> AvailableToolsAsync()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
        var tools = new MediaTools(!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv) ? fromEnv : File.Exists(bundled) ? bundled : "ffmpeg");
        if (!tools.Available())
        {
            output.WriteLine($"[skip] ffmpeg 不可用（{tools.Ffmpeg}）；设置 LIVECLIPPER_TEST_FFMPEG 后运行 HDR 链路测试。");
            return null;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await tools.RunAsync(tools.Ffprobe, ["-version"], timeout.Token); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            output.WriteLine($"[skip] ffprobe 不可用（{tools.Ffprobe}）：{ex.Message}");
            return null;
        }
        return tools;
    }

    private static IEnumerable<List<string>> IntermediateCommands(EditorProject plan)
    {
        foreach (var args in LegacyCommands())
        {
            var codec = args.IndexOf("-c:v");
            var audio = args.IndexOf("-c:a");
            args.RemoveRange(codec, audio - codec);
            args.InsertRange(codec, EditorRenderService.IntermediateVideoCodecArgs(plan));
            audio = args.IndexOf("-c:a");
            args.RemoveRange(audio, 8);
            args.InsertRange(audio, EditorRenderService.IntermediateAudioCodecArgs());
            args[^1] = Path.ChangeExtension(args[^1], ".mkv");
            yield return args;
        }
    }

    private static IEnumerable<List<string>> LegacyCommands()
    {
        foreach (var name in new[] { "gap", "disabled", "base" })
            yield return ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=1024x128:r=24:d=1", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "1", "-map", "0:v:0", "-map", "1:a:0", .. LegacyCodec.Split('|'), "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k", name + ".mp4"];
        yield return ["-hide_banner", "-loglevel", "error", "-y", "-ss", "0", "-t", "1", "-i", "source.mp4", "-map", "0:v:0", "-map", "0:a:0", "-vf", BaseFilter, "-t", "1", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k", "part.mp4"];
        yield return ["-hide_banner", "-loglevel", "error", "-y", "-i", "part0.mp4", "-i", "part1.mp4", "-filter_complex", "[0:v]format=yuv420p[a];[1:v]format=yuv420p[b];[a][b]xfade=duration=0.25[vout]", "-map", "[vout]", "-t", "1.75", "-r", "24", .. LegacyCodec.Split('|'), "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k", "joined.mp4"];
        yield return ["-hide_banner", "-loglevel", "error", "-y", "-i", "joined.mp4", "-i", "overlay.mp4", "-filter_complex", "[1:v]format=rgba,colorchannelmixer=aa=0.75[ov];[0:v][ov]overlay=x=0:y=0[vout]", "-map", "[vout]", "-t", "1.75", .. LegacyCodec.Split('|'), "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k", "composed.mp4"];
        yield return ["-hide_banner", "-loglevel", "error", "-y", "-ss", "0", "-t", "1", "-i", "source.mp4", "-map", "0:v:0", "-map", "0:a:0", "-vf", BaseFilter, "-t", "1", .. LegacyCodec.Split('|'), "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k", "overlay.mp4"];
    }
}
