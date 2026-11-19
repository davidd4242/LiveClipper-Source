using System.Security.Cryptography;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class WinRenderAdjSarTests(ITestOutputHelper output)
{
    private const int Width = 320;
    private const int Height = 180;
    private const int Fps = 30;
    private const int Frames = 90;
    private const int FrameBytes = Width * Height * 3 / 2;
    private const string Base = "scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30";
    // Frozen pre-S1 comparators; only the product side calls VisualFilter.
    private const string Static = Base + ",crop=iw/1.2:ih/1.2,scale=320:180";
    private const string Shake = Base + ",crop=iw/1.12:ih/1.12:(iw-ow)/2+4*sin(t*35):(ih-oh)/2+4*cos(t*31),scale=320:180";
    private const string Geometry = Static + ",pad=352:180:32:0,crop=320:180:0:0,rotate=0.087266:ow=iw:oh=ih:c=black";
    private const string ScaleCurve = "if(lt((on/30),0),1,if(lt((on/30),3),(1+(1)*((on/30)-0)/3),2))";
    private const string PositionCurve = "if(lt((n/30),0),-0.2,if(lt((n/30),3),(-0.2+(0.4)*((n/30)-0)/3),0.2))";
    private const string Position = ",pad=640:360:160:90,crop=320:180:x='160-(" + PositionCurve + ")*320':y='90-(0)*180'";
    private const string Keyed = Base + ",zoompan=z='" + ScaleCurve + "':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s=320x180:fps=30" + Position;

    [Fact]
    public async Task S01_StaticZoomSar()
    {
        await using var f = await Fixture.Create("S01", output);
        var old = await f.Filter("old", Static);
        var actual = await f.Filter("product", EditorRenderService.VisualFilter(f.Item() with { Zoom = 1.2 }, Width, Height, Fps));
        await f.AssertSar(old, "399:400");
        EqualBytes(await f.Raw(old, "old"), await f.Raw(actual, "product"), f, "static zoom");
        await f.AssertSar(actual, "1:1");
    }

    [Fact]
    public async Task S02_StaticGeometryAndAssemble()
    {
        await using var f = await Fixture.Create("S02", output);
        var item = f.Item() with { Zoom = 1.2, PositionX = .1, Rotation = 5, Opacity = .85 };
        var old = await f.Filter("old", Geometry);
        var visual = await f.Filter("visual", EditorRenderService.VisualFilter(item, Width, Height, Fps));
        var beforeOpacity = await f.Raw(old, "old");
        EqualBytes(beforeOpacity, await f.Raw(visual, "visual"), f, "Golden A geometry");
        var assembled = await f.Assemble("assembled", item);
        var actual = await f.Raw(assembled, "assembled");
        var expected = new byte[beforeOpacity.Length];
        for (var i = 0; i < expected.Length; i++)
        {
            var black = i % FrameBytes < Width * Height ? 16 : 128;
            expected[i] = (byte)Math.Floor(black + (beforeOpacity[i] - black) * .85);
        }
        EqualBytes(expected, actual, f, "Golden A independent limited-range opacity oracle");
        await f.AssertSar(old, "399:400");
        await f.AssertSar(visual, "1:1");
        await f.AssertSar(assembled, "1:1");
    }

    [Fact]
    public async Task S03_ShakeSar()
    {
        await using var f = await Fixture.Create("S03", output);
        var old = await f.Filter("old", Shake);
        var actual = await f.Filter("product", EditorRenderService.VisualFilter(f.Item() with { Motion = "shake" }, Width, Height, Fps));
        await f.AssertSar(old, "1287:1280");
        EqualBytes(await f.Raw(old, "old"), await f.Raw(actual, "product"), f, "shake all motion frames");
        await f.AssertSar(actual, "1:1");
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("push")]
    [InlineData("position")]
    [InlineData("rotation")]
    [InlineData("B")]
    [InlineData("track2")]
    public async Task S04_UnchangedPaths(string kind)
    {
        await using var f = await Fixture.Create("S04-" + kind, output);
        var item = f.Item();
        string expected;
        switch (kind)
        {
            case "push":
                item = item with { Motion = "push" };
                expected = Base + ",zoompan=z='1+0.12*min(on/90,1)':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s=320x180:fps=30";
                break;
            case "position":
                item = item with { Keyframes = Keys(("positionX", -.2, .2)) };
                expected = Base + Position;
                break;
            case "rotation":
                item = item with { Keyframes = Keys(("rotation", -5, 5)) };
                expected = Base + ",rotate='(if(lt((n/30),0),-5,if(lt((n/30),3),(-5+(10)*((n/30)-0)/3),5)))*PI/180':ow=iw:oh=ih:c=black";
                break;
            case "B":
                item = item with { Keyframes = Keys(("scale", 1, 2), ("positionX", -.2, .2)) };
                expected = Keyed;
                break;
            case "track2":
                item = item with { Track = 2, Keyframes = Keys(("scale", 1, 2), ("positionX", -.2, .2), ("rotation", -5, 5)) };
                expected = Base;
                break;
            default:
                expected = Base;
                break;
        }
        var graph = EditorRenderService.VisualFilter(item, Width, Height, Fps);
        Assert.Equal(expected, graph);
        var old = await f.Filter("frozen", expected);
        var actual = await f.Filter("product", graph);
        EqualBytes(await f.Raw(old, "frozen"), await f.Raw(actual, "product"), f, kind);
        await f.AssertSar(actual, "1:1");
    }

    [Theory]
    [InlineData("static")]
    [InlineData("shake")]
    [InlineData("keyed")]
    public async Task S05_HighBitDepth(string kind)
    {
        await using var f = await Fixture.Create("S05-" + kind, output, high: true);
        var item = kind switch
        {
            "static" => f.Item() with { Zoom = 1.2 },
            "shake" => f.Item() with { Motion = "shake" },
            _ => f.Item() with { Keyframes = Keys(("scale", 1, 2)) }
        };
        const string curve = "if(lt((N/30),0),1,if(lt((N/30),3),(1+(1)*((N/30)-0)/3),2))";
        const string z = "max(1,(" + curve + "))";
        var expected = "format=yuv420p10le," + (kind switch
        {
            "static" => Static,
            "shake" => Shake,
            _ => Base + ",geq=lum='lum((X-W/2)/" + z + "+W/2,(Y-H/2)/" + z + "+H/2)':cb='cb((X-W/2)/" + z + "+W/2,(Y-H/2)/" + z + "+H/2)':cr='cr((X-W/2)/" + z + "+W/2,(Y-H/2)/" + z + "+H/2)'"
        });
        var graph = EditorRenderService.VisualFilter(item, Width, Height, Fps, highBitDepth: true);
        Assert.DoesNotContain("zoompan", graph);
        if (kind == "keyed") Assert.Equal(expected, graph);
        var old = await f.Filter("old", expected);
        var visual = await f.Filter("visual", graph);
        var oldRaw = await f.Raw(old, "old");
        var actual = await f.Raw(visual, "visual");
        EqualBytes(oldRaw, actual, f, "native 10bit " + kind);
        AssertLowBits(actual, f);
        var assembled = await f.Assemble("assembled", item);
        var assembledRaw = await f.Raw(assembled, "assembled");
        EqualBytes(oldRaw, assembledRaw, f, "HDR real Assemble " + kind);
        await f.AssertHdr(assembled);
        var final = await f.Render("final", item);
        await f.AssertHdr(final);
        var finalRaw = await f.Raw(final, "final");
        AssertLowBits(finalRaw, f);
        await f.AssertSar(old, kind == "static" ? "399:400" : kind == "shake" ? "1287:1280" : "1:1");
        await f.AssertSar(visual, "1:1");
        await f.AssertSar(assembled, "1:1");
        await f.AssertSar(final, "1:1");
    }

    private static Dictionary<string, ParamKeyframe[]> Keys(params (string Name, double From, double To)[] values) =>
        values.ToDictionary(v => v.Name, v => new[] { new ParamKeyframe(0, v.From), new ParamKeyframe(3, v.To) });

    private static void EqualBytes(byte[] expected, byte[] actual, Fixture f, string name)
    {
        var different = expected.Zip(actual).Count(pair => pair.First != pair.Second);
        f.Log($"{name}: expectedBytes={expected.Length}; actualBytes={actual.Length}; differentBytes={different}; sha256={Convert.ToHexString(SHA256.HashData(actual))}");
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(different == 0, $"{name}: {different} raw bytes differ");
    }

    private static void AssertLowBits(byte[] raw, Fixture f)
    {
        var levels = new HashSet<int>();
        var lowBits = 0;
        for (var i = 0; i < Width * Height; i++)
        {
            var value = raw[2 * i] | raw[2 * i + 1] << 8;
            levels.Add(value);
            if (value % 4 != 0) lowBits++;
        }
        f.Log($"10bit uniqueLumaLevels={levels.Count}; nonZeroLowBits={lowBits}");
        Assert.True(levels.Count > 256, $"10bit gradient has only {levels.Count} levels");
        Assert.True(lowBits > 0, "10bit gradient lost all low bits");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));
        private readonly bool high;
        private int command;
        public string Root { get; }
        public string Source { get; private set; } = "";
        public MediaTools Tools { get; }
        public EditorRenderService Renderer { get; private set; } = null!;
        public Clip Clip { get; private set; } = null!;
        private string Format => high ? "yuv420p10le" : "yuv420p";
        private CancellationToken Token => timeout.Token;

        private Fixture(string name, ITestOutputHelper output, bool high)
        {
            this.output = output;
            this.high = high;
            Root = Path.Combine(Path.GetTempPath(), "s1-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var executable = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
            Assert.True(!string.IsNullOrWhiteSpace(executable) && Path.IsPathFullyQualified(executable) && File.Exists(executable), "Explicit absolute LIVECLIPPER_TEST_FFMPEG is required; no skipped renders.");
            Tools = new MediaTools(executable);
            Assert.True(Path.IsPathFullyQualified(Tools.Ffprobe) && File.Exists(Tools.Ffprobe), "Absolute sibling ffprobe is required.");
            Log("artifacts=" + Root);
        }

        public static async Task<Fixture> Create(string name, ITestOutputHelper output, bool high = false)
        {
            var f = new Fixture(name, output, high);
            f.Source = Path.Combine(f.Root, "source.mkv");
            var input = high
                ? "nullsrc=s=320x180:r=30:d=3,format=yuv420p10le,geq=lum='64+mod(X+3*Y+N,877)':cb='480+mod(X+Y+N,65)':cr='480+mod(2*X+Y+N,65)'"
                : "testsrc2=s=320x180:r=30:d=3";
            await f.Run(f.Tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", input,
                "-an", "-c:v", "ffv1", "-level", "3", "-pix_fmt", f.Format, f.Source]);
            var store = new Store(Path.Combine(f.Root, "test.db")); store.Migrate();
            var studio = new StudioStore(store.DatabasePath); studio.Migrate();
            f.Renderer = new EditorRenderService(store, studio, f.Tools, f.Root);
            f.Clip = new Clip("clip", "s1-" + name, f.Source, null, DateTimeOffset.UtcNow); store.SaveClip(f.Clip);
            return f;
        }

        public TimelineItem Item() => new("item", Clip.Id, 0, 3);
        private EditorProject Plan(TimelineItem item) => new()
        {
            Start = 0, End = 3, Fps = Fps, Resolution = 0, Timeline = [item],
            Format = high ? "hevc" : "h264", ColorSpace = high ? "hdr10" : null
        };

        public async Task<string> Filter(string name, string graph)
        {
            Log(name + " VisualFilter=" + graph);
            var destination = Path.Combine(Root, name + ".mkv");
            await Run(Tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-filter_threads", "1", "-i", Source,
                "-vf", graph, "-an", "-c:v", "ffv1", "-level", "3", "-pix_fmt", Format, destination]);
            return destination;
        }

        public async Task<string> Assemble(string name, TimelineItem item)
        {
            var plan = Plan(item);
            Log("AssembleAsync=" + JsonSerializer.Serialize(plan));
            var result = await Renderer.AssembleAsync(Clip, plan, Path.Combine(Root, name), Token);
            Assert.Equal(3d, result.Length);
            Assert.Equal(30d, result.Fps);
            Assert.True(File.Exists(result.Video), "Missing real assembled video");
            return result.Video;
        }

        public async Task<string> Render(string name, TimelineItem item)
        {
            var destination = Path.Combine(Root, name + ".mp4");
            await Renderer.RenderAsync(Clip, Plan(item), destination, Path.Combine(Root, name + "-work"), Token);
            return destination;
        }

        public async Task<byte[]> Raw(string source, string name)
        {
            var destination = Path.Combine(Root, name + "." + Format);
            await Run(Tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-map", "0:v:0", "-an",
                "-pix_fmt", Format, "-c:v", "rawvideo", "-fps_mode", "passthrough", "-f", "rawvideo", destination]);
            var raw = await File.ReadAllBytesAsync(destination, Token);
            Assert.Equal(Frames * FrameBytes * (high ? 2 : 1), raw.Length);
            return raw;
        }

        public async Task AssertSar(string source, string expected)
        {
            var json = await Run(Tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=width,height,pix_fmt,sample_aspect_ratio,display_aspect_ratio:frame=width,height,pix_fmt,sample_aspect_ratio", "-of", "json", source]);
            using var document = JsonDocument.Parse(json);
            var stream = Assert.Single(document.RootElement.GetProperty("streams").EnumerateArray());
            var frames = document.RootElement.GetProperty("frames").EnumerateArray().ToArray();
            Log($"{Path.GetFileName(source)}: frames={frames.Length}; streamSAR={stream.GetProperty("sample_aspect_ratio")}; frameSAR={string.Join(',', frames.Select(frame => frame.GetProperty("sample_aspect_ratio").GetString()).Distinct())}");
            Assert.Equal(Frames, frames.Length);
            foreach (var frame in frames)
            {
                Assert.Equal(Width, frame.GetProperty("width").GetInt32());
                Assert.Equal(Height, frame.GetProperty("height").GetInt32());
                Assert.Equal(Format, frame.GetProperty("pix_fmt").GetString());
                Assert.Equal(expected, frame.GetProperty("sample_aspect_ratio").GetString());
            }
            Assert.Equal(expected, stream.GetProperty("sample_aspect_ratio").GetString());
            if (expected == "1:1") Assert.Equal("16:9", stream.GetProperty("display_aspect_ratio").GetString());
        }

        public async Task AssertHdr(string source)
        {
            var json = await Run(Tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt,color_primaries,color_transfer,color_space", "-of", "json", source]);
            using var document = JsonDocument.Parse(json);
            var stream = Assert.Single(document.RootElement.GetProperty("streams").EnumerateArray());
            Assert.Equal("yuv420p10le", stream.GetProperty("pix_fmt").GetString());
            Assert.Equal("bt2020", stream.GetProperty("color_primaries").GetString());
            Assert.Equal("smpte2084", stream.GetProperty("color_transfer").GetString());
            Assert.Equal("bt2020nc", stream.GetProperty("color_space").GetString());
        }

        public void Log(string message)
        {
            output.WriteLine(message);
            File.AppendAllText(Path.Combine(Root, "measurements.log"), message + Environment.NewLine);
        }

        private async Task<string> Run(string executable, string[] argv)
        {
            var prefix = Path.Combine(Root, $"native-{++command:000}");
            var start = DateTimeOffset.UtcNow;
            using var process = MediaTools.Start(executable, argv);
            var pid = process.Id;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            finally
            {
                await File.WriteAllTextAsync(prefix + ".stdout.log", await stdout);
                await File.WriteAllTextAsync(prefix + ".stderr.log", await stderr);
                await File.WriteAllTextAsync(prefix + ".command.json", JsonSerializer.Serialize(new { executable, argv, pid, start, end = DateTimeOffset.UtcNow, exit = process.ExitCode }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Assert.True(process.ExitCode == 0, $"Native PID={pid} exit={process.ExitCode}: {await stderr}");
            return await stdout;
        }

        public async ValueTask DisposeAsync()
        {
            if (Renderer is not null) await Renderer.StopAsync();
            timeout.Dispose();
            Log("Artifacts retained: " + Root);
        }
    }
}
