using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

/// <summary>
/// R81：后端 FFmpeg 新增导出格式（ProRes 档位 / DNxHR 档位 / MJPEG / WebM / GIF /
/// 图像序列 zip / 纯音频）的契约与真实编码验证。
/// 前半部分是不需要 ffmpeg 的纯函数契约；后半部分用 LIVECLIPPER_TEST_FFMPEG 指向的真 ffmpeg
/// 渲染 2 秒 testsrc2+sine 小片段，再用 ffprobe 断言 codec / profile / pix_fmt / 声道。
/// 找不到 ffmpeg 时按仓库既有约定打印 [skip] 并返回（不引入任何 mock）。
/// </summary>
public sealed class EditorRenderFormatTests(ITestOutputHelper output)
{
    // DNxHR（dnxhd 编码器）拒绝小于 256x120 的输入：
    // "Input dimensions too small, input must be at least 256x120"（ffmpeg 9.0.2 实测）。
    // 320x180 同时满足 256x120 下限与 4:2:2 的偶数宽高要求，因此整组用例共用一个夹具。
    private const int Width = 320;
    private const int Height = 180;
    private const int Fps = 24;
    private const double Seconds = 2;

    // ---------- 纯函数契约（无需 ffmpeg） ----------

    [Fact]
    public void Table_Has24UniqueLowercaseIdsAndExtensions()
    {
        Assert.Equal(24, EditorExportFormats.Table.Length);
        Assert.Equal(EditorExportFormats.Table.Length, EditorExportFormats.Ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var spec in EditorExportFormats.Table)
        {
            Assert.Equal(spec.Id, spec.Id.ToLowerInvariant());
            Assert.Matches("^[a-z0-9-]+$", spec.Id);
            Assert.False(string.IsNullOrWhiteSpace(spec.Label));
            Assert.StartsWith(".", spec.Extension);
            if (spec.Kind == ExportKind.ImageSequence)
            {
                Assert.Equal(".zip", spec.Extension);
                Assert.StartsWith(".", spec.FrameExtension);
                Assert.Empty(spec.AudioArgs);
            }
            else
            {
                Assert.Empty(spec.FrameExtension);
            }
            // 有音轨的格式必须给出音频参数；图像序列/GIF 不许偷偷带音轨参数。
            if (spec.HasAudio)
            {
                Assert.NotEmpty(spec.AudioArgs);
            }
            else
            {
                Assert.Empty(spec.AudioArgs);
            }
        }
    }

    [Theory]
    [InlineData("prores-proxy", ".mov")]
    [InlineData("dnxhr-hqx", ".mov")]
    [InlineData("mjpeg", ".mov")]
    [InlineData("webm", ".webm")]
    [InlineData("gif", ".gif")]
    [InlineData("png", ".zip")]
    [InlineData("exr", ".zip")]
    [InlineData("wav51", ".wav")]
    [InlineData("aiff", ".aiff")]
    [InlineData("aac", ".m4a")]
    [InlineData("flac", ".flac")]
    public void OutputExtension_MatchesDescriptor(string format, string expected)
        => Assert.Equal(expected, EditorRenderService.OutputExtension(format));

    [Fact]
    public void OutputExtension_CoversEveryDescriptorAndKeepsLegacyFour()
    {
        Assert.Equal(".mp4", EditorRenderService.OutputExtension("h264"));
        Assert.Equal(".mp4", EditorRenderService.OutputExtension("hevc"));
        Assert.Equal(".mov", EditorRenderService.OutputExtension("prores"));
        Assert.Equal(".mov", EditorRenderService.OutputExtension("dnxhr"));
        foreach (var spec in EditorExportFormats.Table)
        {
            // 大小写/空白归一后仍能查到同一条（前端可能传出原始大小写）。
            Assert.Equal(spec.Extension, EditorRenderService.OutputExtension(spec.Id.ToUpperInvariant()));
            Assert.Equal(spec.Extension, EditorRenderService.OutputExtension(" " + spec.Id + " "));
        }
    }

    [Fact]
    public void SupportedFormats_KeepsLegacyFourAndAddsEveryDescriptor()
    {
        var supported = EditorRenderService.SupportedFormats;
        foreach (var legacy in new[] { "h264", "hevc", "prores", "dnxhr" })
        {
            Assert.Contains(legacy, supported);
        }
        foreach (var id in EditorExportFormats.Ids)
        {
            Assert.Contains(id, supported);
        }
        Assert.Equal(4 + EditorExportFormats.Ids.Length, supported.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("prores-proxy")]
    [InlineData("prores-lt")]
    [InlineData("prores-422")]
    [InlineData("dnxhr-lb")]
    [InlineData("dnxhr-sq")]
    [InlineData("dnxhr-hq")]
    [InlineData("dnxhr-hqx")]
    [InlineData("mjpeg")]
    [InlineData("webm")]
    [InlineData("gif")]
    public void VideoCodecArgs_NewVideoFormatsComeStraightFromDescriptor(string format)
    {
        var spec = EditorExportFormats.Find(format)!;
        Assert.Equal(EditorExportFormats.VideoArgsOrThrow(format), EditorRenderService.VideoCodecArgs(format, 0));
        if (spec.Kind == ExportKind.AnimatedGif)
        {
            // GIF 没有逐帧编码参数，VideoArgsOrThrow 直接给封装用的 -c:v gif。
            Assert.Equal(new[] { "-c:v", "gif" }, EditorExportFormats.VideoArgsOrThrow(format));
        }
        else
        {
            Assert.Equal(spec.VideoArgs, EditorExportFormats.VideoArgsOrThrow(format));
        }
    }

    [Fact]
    public void VideoCodecArgs_LegacyFourStayByteIdentical()
    {
        // 旧四格式是既有契约，任何改动都会破坏 EditorRenderMixTests/EditorRenderHdrChainTests 的字节断言。
        Assert.Equal(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-pix_fmt", "yuv420p" },
            EditorRenderService.VideoCodecArgs("h264", 0));
        Assert.Equal(new[] { "-c:v", "libx264", "-preset", "veryfast", "-b:v", "8000k", "-pix_fmt", "yuv420p" },
            EditorRenderService.VideoCodecArgs("h264", 8000));
        Assert.Equal(new[] { "-c:v", "prores_ks", "-profile:v", "3", "-vendor", "apl0", "-pix_fmt", "yuv422p10le" },
            EditorRenderService.VideoCodecArgs("prores", 0));
        Assert.Equal(new[] { "-c:v", "dnxhd", "-profile:v", "dnxhr_sq", "-pix_fmt", "yuv422p" },
            EditorRenderService.VideoCodecArgs("dnxhr", 0));
        Assert.Equal(new[] { "-c:v", "dnxhd", "-profile:v", "dnxhr_hqx", "-pix_fmt", "yuv422p10le",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc" },
            EditorRenderService.VideoCodecArgs("dnxhr", 0, colorSpace: "hdr10"));
    }

    [Fact]
    public void Validate_AcceptsEveryDescriptorFormat()
    {
        foreach (var spec in EditorExportFormats.Table)
        {
            EditorRenderService.Validate(Plan(spec.Id));
        }
        // 大小写混写也应放行（NormalizeFormat 会归一；前端 id 一律小写，但历史存档可能不是）。
        EditorRenderService.Validate(Plan("DNXHR-HQX"));
        EditorRenderService.Validate(Plan(" WebM "));
    }

    [Theory]
    [InlineData("av1")]
    [InlineData("apv")]
    [InlineData("mxf-op1a")]
    [InlineData("mxf-opatom")]
    [InlineData("aaf")]
    [InlineData("omf")]
    public void Validate_RejectsFormatsWithoutReliableFfmpegPath(string format)
    {
        var ex = Assert.Throws<ArgumentException>(() => EditorRenderService.Validate(Plan(format)));
        Assert.Contains("h264|hevc|prores|dnxhr", ex.Message);
        // 未实现格式刻意不出现在支持清单里：文案只列真正可用的 id。
        Assert.Contains("与 FFmpeg 原生格式", ex.Message);
    }

    [Fact]
    public void UnsupportedMessage_ListsLegacyFourPlusEveryImplementedId()
    {
        var message = EditorExportFormats.UnsupportedMessage;
        Assert.Contains("h264|hevc|prores|dnxhr", message);
        foreach (var id in EditorExportFormats.Ids)
        {
            Assert.Contains(id, message);
        }
        foreach (var disabled in new[] { "av1", "apv", "mxf-op1a", "mxf-opatom", "aaf", "omf" })
        {
            Assert.DoesNotContain(disabled, message);
        }
    }

    [Fact]
    public void VideoArgsOrThrow_UnknownFormatThrowsWithLegacySubstring()
    {
        var ex = Assert.Throws<ArgumentException>(() => EditorExportFormats.VideoArgsOrThrow("av1"));
        Assert.Contains("h264|hevc|prores|dnxhr", ex.Message);
        // 纯音频格式没有视频参数：调用方（VideoFile/GIF 分支）不该走到这里，走到就抛。
        Assert.Throws<ArgumentException>(() => EditorExportFormats.VideoArgsOrThrow("flac"));
    }

    // ---------- R81 FIXP：DNxHR 画布下限（纯函数，不跑 ffmpeg） ----------

    [Theory]
    [InlineData("dnxhr")]
    [InlineData("dnxhr-lb")]
    [InlineData("dnxhr-sq")]
    [InlineData("dnxhr-hq")]
    [InlineData("dnxhr-hqx")]
    [InlineData("DNXHR-HQX")]
    [InlineData(" dnxhr-hq ")]
    public void RequireDnxhrCanvas_RejectsSubMinimumCanvasWithReadableCode(string format)
    {
        // dnxhd 编码器硬限 256x120：更小的画布只会在编码器内部失败（-22 / Input dimensions too small），
        // 这里断言提前拒绝、并且报错带可读 code 与实际画布尺寸。
        foreach (var (width, height) in new[] { (255, 120), (256, 119), (254, 118), (2, 2), (0, 0), (-4, 0) })
        {
            var ex = Assert.Throws<ArgumentException>(() => EditorExportFormats.RequireDnxhrCanvas(format, width, height));
            Assert.StartsWith("invalid_dnxhr_canvas", ex.Message);
            Assert.Contains("256x120", ex.Message);
            Assert.Contains($"{width}x{height}", ex.Message);
        }
        // 下限本身必须放行，否则正常的 256x120 导出会被误拒。
        EditorExportFormats.RequireDnxhrCanvas(format, EditorExportFormats.DnxhrMinimumWidth, EditorExportFormats.DnxhrMinimumHeight);
        EditorExportFormats.RequireDnxhrCanvas(format, 320, 180);
        EditorExportFormats.RequireDnxhrCanvas(format, 1920, 1080);
    }

    [Fact]
    public void RequireDnxhrCanvas_LeavesEveryOtherFormatAlone()
    {
        // 只有 dnxhr* 受 dnxhd 画布下限约束；其余格式（含旧四档与纯音频/序列）即使极小也不该被这里拦住。
        foreach (var format in new string?[] { null, "", "   ", "h264", "hevc", "prores", "prores-proxy", "mjpeg", "webm", "gif", "png", "exr", "wav", "flac", "aac" })
        {
            EditorExportFormats.RequireDnxhrCanvas(format, 1, 1);
            EditorExportFormats.RequireDnxhrCanvas(format, 0, 0);
        }
        // 归一化契约：历史存档里的大小写/空白写法也要被守卫识别（AssembleAsync 传的就是归一后的 plan.Format）。
        Assert.Equal("dnxhr-hqx", EditorRenderService.NormalizeFormat(" DNXHR-HQX "));
        Assert.Throws<ArgumentException>(() => EditorExportFormats.RequireDnxhrCanvas(EditorRenderService.NormalizeFormat(" DNXHR-HQX "), 200, 100));
        Assert.Throws<ArgumentException>(() => EditorExportFormats.RequireDnxhrCanvas(EditorRenderService.NormalizeFormat("dnxhr"), 213, 120));
    }

    // ---------- 真 ffmpeg 端到端 ----------

    [Theory]
    [InlineData("prores-proxy", "prores", "Proxy", "yuv422p10le", "pcm_s16le")]
    [InlineData("prores-lt", "prores", "LT", "yuv422p10le", "pcm_s16le")]
    [InlineData("prores-422", "prores", "Standard", "yuv422p10le", "pcm_s16le")]
    [InlineData("dnxhr-lb", "dnxhd", "DNXHR LB", "yuv422p", "pcm_s16le")]
    [InlineData("dnxhr-sq", "dnxhd", "DNXHR SQ", "yuv422p", "pcm_s16le")]
    [InlineData("dnxhr-hq", "dnxhd", "DNXHR HQ", "yuv422p", "pcm_s16le")]
    [InlineData("dnxhr-hqx", "dnxhd", "DNXHR HQX", "yuv422p10le", "pcm_s16le")]
    [InlineData("mjpeg", "mjpeg", "Baseline", "yuvj422p", "pcm_s16le")]
    [InlineData("webm", "vp9", null, "yuv420p", "opus")]
    public async Task Render_VideoFile_HasRequestedCodecProfileAndPixFmt(
        string format, string codec, string? profile, string pixFmt, string audioCodec)
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var root = TemporaryRoot();
        EditorRenderService? renderer = null;
        try
        {
            var setup = await SetupAsync(tools, root, token);
            renderer = setup.Renderer;
            var clip = setup.Clip;
            var output = Path.Combine(root, "out" + EditorRenderService.OutputExtension(format));
            await renderer.RenderAsync(clip, Plan(format), output, Path.Combine(root, "work"), token);
            Assert.True(File.Exists(output) && new FileInfo(output).Length > 0, "RenderAsync 没有产出 " + output);

            var streams = await ProbeAsync(tools, output, token);
            var video = Single(streams, "video");
            Assert.Equal(codec, Text(video, "codec_name"));
            if (profile is not null)
            {
                Assert.Equal(profile, Text(video, "profile"));
            }
            Assert.Equal(pixFmt, Text(video, "pix_fmt"));
            Assert.Equal(Width, video.GetProperty("width").GetInt32());
            Assert.Equal(Height, video.GetProperty("height").GetInt32());
            Assert.Equal("24/1", Text(video, "avg_frame_rate"));
            var audio = Single(streams, "audio");
            Assert.Equal(audioCodec, Text(audio, "codec_name"));
            Assert.Equal(2, audio.GetProperty("channels").GetInt32());
        }
        finally
        {
            if (renderer is not null) await renderer.StopAsync();
            DeleteTemporaryRoot(root);
        }
    }

    [Theory]
    [InlineData("wav", "pcm_s16le", "s16", 2)]
    [InlineData("wavlpcm", "pcm_f32le", "flt", 2)]
    [InlineData("wav51", "pcm_s16le", "s16", 6)]
    [InlineData("wavbwf", "pcm_s24le", "s32", 2)]
    [InlineData("aiff", "pcm_s16be", "s16", 2)]
    [InlineData("aac", "aac", "fltp", 2)]
    // FLAC 无损沿用输入位深：音频图是 fltp（volume/amix/alimiter 全在 float 域），
    // ffmpeg 的 flac 编码器因此写出 32-bit 定点，ffprobe sample_fmt = s32（实测）。
    [InlineData("flac", "flac", "s32", 2)]
    public async Task Render_AudioOnly_WritesRequestedCodecSampleFormatAndChannels(
        string format, string codec, string sampleFmt, int channels)
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var root = TemporaryRoot();
        EditorRenderService? renderer = null;
        try
        {
            var setup = await SetupAsync(tools, root, token);
            renderer = setup.Renderer;
            var clip = setup.Clip;
            var output = Path.Combine(root, "out" + EditorRenderService.OutputExtension(format));
            await renderer.RenderAsync(clip, Plan(format), output, Path.Combine(root, "work"), token);
            Assert.True(File.Exists(output) && new FileInfo(output).Length > 0, "RenderAsync 没有产出 " + output);

            var streams = await ProbeAsync(tools, output, token);
            Assert.Empty(streams.Where(x => Text(x, "codec_type") == "video"));
            var audio = Single(streams, "audio");
            Assert.Equal(codec, Text(audio, "codec_name"));
            Assert.Equal(sampleFmt, Text(audio, "sample_fmt"));
            Assert.Equal(channels, audio.GetProperty("channels").GetInt32());
            Assert.Equal("48000", Text(audio, "sample_rate"));
        }
        finally
        {
            if (renderer is not null) await renderer.StopAsync();
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Render_AnimatedGif_UsesPalettePipelineWithoutAudio()
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var root = TemporaryRoot();
        EditorRenderService? renderer = null;
        try
        {
            var setup = await SetupAsync(tools, root, token);
            renderer = setup.Renderer;
            var clip = setup.Clip;
            var output = Path.Combine(root, "out.gif");
            await renderer.RenderAsync(clip, Plan("gif"), output, Path.Combine(root, "work"), token);
            Assert.True(File.Exists(output) && new FileInfo(output).Length > 0, "RenderAsync 没有产出 " + output);

            var streams = await ProbeAsync(tools, output, token);
            var video = Single(streams, "video");
            Assert.Equal("gif", Text(video, "codec_name"));
            Assert.Equal("bgra", Text(video, "pix_fmt"));
            Assert.Equal(Width, video.GetProperty("width").GetInt32());
            Assert.Equal(Height, video.GetProperty("height").GetInt32());
            // GIF 路径按 15fps 封顶，超过 15 的工程 fps 会被降到 15。
            // GIF 的帧延时只能存 1/100 秒（15fps = 6.67cs，实际写 6cs），所以 avg_frame_rate 会被
            // ffprobe 反推成 50/3；契约由「编码器声明速率 + 实际帧数」验证：15fps × 2s = 30 帧。
            Assert.Equal("15/1", Text(video, "r_frame_rate"));
            Assert.Equal((int)(Seconds * 15), int.Parse(Text(video, "nb_frames")!, CultureInfo.InvariantCulture));
            Assert.Empty(streams.Where(x => Text(x, "codec_type") == "audio"));
        }
        finally
        {
            if (renderer is not null) await renderer.StopAsync();
            DeleteTemporaryRoot(root);
        }
    }

    [Theory]
    [InlineData("png", "frame", ".png")]
    [InlineData("tiff", "frame", ".tiff")]
    [InlineData("jpg", "frame", ".jpg")]
    public async Task Render_ImageSequence_PacksNumberedFramesIntoZip(string format, string prefix, string extension)
    {
        var tools = await AvailableToolsAsync();
        if (tools is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var root = TemporaryRoot();
        EditorRenderService? renderer = null;
        try
        {
            var setup = await SetupAsync(tools, root, token);
            renderer = setup.Renderer;
            var clip = setup.Clip;
            var output = Path.Combine(root, "out.zip");
            var work = Path.Combine(root, "work");
            await renderer.RenderAsync(clip, Plan(format), output, work, token);
            Assert.True(File.Exists(output) && new FileInfo(output).Length > 0, "RenderAsync 没有产出 " + output);

            using var zip = ZipFile.OpenRead(output);
            var names = zip.Entries.Select(x => x.FullName).ToArray();
            Assert.Equal((int)(Seconds * Fps), names.Length);
            Assert.All(names, name => Assert.Matches($"^{prefix}\\d{{6}}{extension.Replace(".", "\\.")}$", name));
            Assert.Equal(prefix + "000001" + extension, names[0]);
            var first = zip.Entries[0];
            Assert.True(first.Length > 0, "图像序列的帧是空文件: " + first.FullName);

            // 帧目录是 work 下的临时目录，必须被清理掉（否则 RunAsync finally 的 Directory.Delete 会抛）。
            Assert.False(Directory.Exists(Path.Combine(work, "frames")), "临时帧目录没有被删除");
        }
        finally
        {
            if (renderer is not null) await renderer.StopAsync();
            DeleteTemporaryRoot(root);
        }
    }

    // ---------- helpers ----------

    private static EditorProject Plan(string format) => new()
    {
        Start = 0, End = Seconds, Format = format, Resolution = 480, Fps = Fps,
        Timeline = [new TimelineItem("v1", "clip", 0, Seconds)]
    };

    private static async Task<(EditorRenderService Renderer, Clip Clip)> SetupAsync(
        MediaTools tools, string root, CancellationToken token)
    {
        var store = new Store(Path.Combine(root, "test.db"));
        store.Migrate();
        var studio = new StudioStore(store.DatabasePath);
        studio.Migrate();
        var renderer = new EditorRenderService(store, studio, tools, root);
        var clip = await CreateSourceAsync(tools, store, root, "clip", token);
        return (renderer, clip);
    }

    private static async Task<Clip> CreateSourceAsync(
        MediaTools tools, Store store, string root, string id, CancellationToken token)
    {
        var path = Path.Combine(root, id + ".mp4");
        await tools.RunAsync(tools.Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={Width}x{Height}:rate={Fps}",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", Seconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k", path
        ], token);
        var clip = new Clip(id, "event-" + id, path, null, DateTimeOffset.UtcNow);
        store.SaveClip(clip);
        return clip;
    }

    private async Task<JsonElement[]> ProbeAsync(MediaTools tools, string path, CancellationToken token)
    {
        var log = await tools.RunAsync(tools.Ffprobe,
        [
            "-v", "error", "-show_entries",
            "stream=codec_name,codec_type,profile,pix_fmt,width,height,avg_frame_rate,r_frame_rate,nb_frames,channels,sample_fmt,sample_rate,channel_layout:format=format_name",
            "-of", "json", path
        ], token);
        output.WriteLine($"ffprobe {Path.GetFileName(path)}: {log}");
        using var doc = JsonDocument.Parse(log);
        return doc.RootElement.GetProperty("streams").EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static JsonElement Single(JsonElement[] streams, string type)
        => Assert.Single(streams.Where(x => Text(x, "codec_type") == type));

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) ? value.GetString() : null;

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

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "liveclipper-fmt-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private void DeleteTemporaryRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException ex) { output.WriteLine($"[cleanup] {root}: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { output.WriteLine($"[cleanup] {root}: {ex.Message}"); }
    }
}
