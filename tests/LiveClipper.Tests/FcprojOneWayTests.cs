using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.App;
using LiveClipper.Core;
using LiveClipper.Core.Bridge;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class FcprojOneWayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Export_NativeSchema15_RoundTripsThroughJsonAndRealEngine()
    {
        var root = Path.Combine(Path.GetTempPath(), "liveclipper-fcproj-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var media = Path.Combine(root, "source clip.mp4");
            await Run(Tool("ffmpeg"), "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", media);

            // The real mapper writes a fixed research report relative to its own location.
            // Stage its unchanged bytes so concurrent tests never overwrite the workspace report.
            var mapper = Path.Combine(root, "scripts", "filmcraft-b1-map.mjs");
            Directory.CreateDirectory(Path.GetDirectoryName(mapper)!);
            File.Copy(Path.Combine(RepoRoot(), "scripts", "filmcraft-b1-map.mjs"), mapper);
            var plan = new EditorProject
            {
                Start = 0.4, End = 1.4, Fps = 30, Resolution = 0, BurnSubtitles = false,
                Timeline = [new TimelineItem("v1", "clip-a", 0.4, 1.4)],
            };
            var before = JsonSerializer.Serialize(plan);
            var service = new FcprojExportService(id => id == "clip-a" ? media : null,
                "node", mapper, Tool("ffprobe"), root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = service.Export("clip-a", null, timeout.Token, plan);

            Assert.True(FcprojExportService.IsUnderBridgeRoundtrip(root, result.FcprojPath));
            var json = File.ReadAllText(result.FcprojPath);
            FcprojExportService.ValidateRenderInput(json);
            using var document = JsonDocument.Parse(json);
            var envelope = document.RootElement;
            Assert.Equal("filmcraft.project", envelope.GetProperty("format").GetString());
            Assert.Equal(15, envelope.GetProperty("schema_version").GetInt32());
            Assert.Equal("filmcraft.project", FcprojExportService.ProjectFormat);
            Assert.Equal(15, FcprojExportService.SchemaVersion);
            Assert.False(string.IsNullOrWhiteSpace(envelope.GetProperty("generator").GetString()));
            var project = envelope.GetProperty("project");
            Assert.False(string.IsNullOrWhiteSpace(project.GetProperty("name").GetString()));
            Assert.Equal(JsonValueKind.Object, project.GetProperty("settings").ValueKind);
            Assert.Equal(JsonValueKind.Object, project.GetProperty("root").ValueKind);
            var items = project.GetProperty("items").EnumerateObject().Select(p => p.Value).ToArray();
            Assert.True(project.GetProperty("next_id").GetInt64() > items.Max(i => i.GetProperty("id").GetInt64()));
            var sequenceItem = Assert.Single(items, i => i.GetProperty("kind").TryGetProperty("Sequence", out _));
            var sequenceId = sequenceItem.GetProperty("id").GetInt64();
            Assert.Equal(sequenceId, envelope.GetProperty("view").GetProperty("active_sequence").GetInt64());
            var sequence = sequenceItem.GetProperty("kind").GetProperty("Sequence");
            var settings = sequence.GetProperty("settings");
            Assert.Equal(320, settings.GetProperty("width").GetInt32());
            Assert.Equal(180, settings.GetProperty("height").GetInt32());
            Assert.Equal(30, settings.GetProperty("frame_rate").GetProperty("num").GetInt32());
            Assert.Equal(1, settings.GetProperty("frame_rate").GetProperty("den").GetInt32());
            Assert.Equal(new[] { 1, 1 }, settings.GetProperty("par").EnumerateArray().Select(v => v.GetInt32()));
            Assert.Equal(48000, settings.GetProperty("sample_rate").GetInt32());
            Assert.Equal(0, sequence.GetProperty("start_timecode").GetInt64());
            Assert.Equal(JsonValueKind.Array, sequence.GetProperty("markers").ValueKind);
            var video = Assert.Single(sequence.GetProperty("video_tracks").EnumerateArray()
                .SelectMany(t => t.GetProperty("items").EnumerateArray()));
            Assert.Equal(101_606_400_000L, video.GetProperty("source_in").GetInt64());
            Assert.Equal(254_016_000_000L, video.GetProperty("duration").GetInt64());
            Assert.Single(sequence.GetProperty("audio_tracks").EnumerateArray()
                .SelectMany(t => t.GetProperty("items").EnumerateArray()));
            var mediaItem = Assert.Single(items, i => i.GetProperty("kind").TryGetProperty("Media", out _));
            var nativeMedia = mediaItem.GetProperty("kind").GetProperty("Media");
            Assert.Equal(Path.GetFullPath(media), nativeMedia.GetProperty("media").GetProperty("File").GetProperty("path").GetString());
            Assert.Equal(JsonValueKind.Object, nativeMedia.GetProperty("info").ValueKind);

            var cli = FilmCraftCli();
            using var inspectedProject = JsonDocument.Parse(await Run(cli, "--project", result.FcprojPath, "inspect", "project"));
            Assert.Equal(sequenceId, inspectedProject.RootElement.GetProperty("activeSequence").GetInt64());
            using var inspectedSequence = JsonDocument.Parse(await Run(cli, "--project", result.FcprojPath, "inspect", "sequence"));
            var actual = inspectedSequence.RootElement;
            Assert.Equal(30, actual.GetProperty("durationFrames").GetInt32());
            var inspectedClip = Assert.Single(actual.GetProperty("video").EnumerateArray()
                .SelectMany(t => t.GetProperty("items").EnumerateArray()));
            Assert.Equal(101_606_400_000L, inspectedClip.GetProperty("sourceIn").GetInt64());
            Assert.Equal(30, inspectedClip.GetProperty("durationFrames").GetInt32());
            Assert.Equal(before, JsonSerializer.Serialize(plan));
            output.WriteLine("Export -> schema 15 JSON -> FilmCraft inspect project/sequence: 320x180, 30fps, 30 frames, sourceIn=0.4s; host unchanged.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"filmcraft-project\",\"version\":1,\"sequence\":{}}")]
    [InlineData("{\"format\":\"filmcraft.project\",\"schema_version\":14,\"project\":{\"items\":{}}}")]
    [InlineData("{\"format\":\"filmcraft.project\",\"schema_version\":16,\"project\":{\"items\":{}}}")]
    [InlineData("{\"format\":\"filmcraft.project\",\"schema_version\":\"15\",\"project\":{\"items\":{}}}")]
    [InlineData("{\"format\":\"filmcraft.project\",\"schema_version\":15}")]
    [InlineData("{\"format\":\"filmcraft.project\",\"schema_version\":15,\"project\":{\"items\":[]}}")]
    public void RenderContract_RejectsInvalidOrDriftingMapperOutput(string json)
    {
        var error = Assert.Throws<BridgeExecutionException>(() => FcprojExportService.ValidateRenderInput(json));
        Assert.Equal("MappingError", error.Error.Class);
    }

    [Fact]
    public void Production_HasNoFcprojToHostWritebackCaller()
    {
        Assert.False(typeof(FcprojImporter).IsVisible);
        Assert.False(typeof(BridgeImportService).IsVisible);
        var sourceRoot = Path.Combine(RepoRoot(), "src");
        var retainedDiagnostics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LiveClipper.Core/Bridge/FcprojImporter.cs",
            "LiveClipper.Core/Bridge/BridgeImportService.cs",
        };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "bin" or "obj") || retainedDiagnostics.Contains(relative)) continue;
            if (HasReverseWriteReference(File.ReadAllText(file))) offenders.Add(relative);
        }
        Assert.True(offenders.Count == 0, "Production references to retained reverse-write diagnostics: " + string.Join(", ", offenders));
    }

    [Fact]
    public void WritebackScan_DetectsCodeAfterComments()
    {
        Assert.True(HasReverseWriteReference("// header\nvar service = new BridgeImportService(clips, read, save);"));
        Assert.True(HasReverseWriteReference("/* diagnostic note\ncontinued */\nFcprojImporter.Import(json);"));
        Assert.False(HasReverseWriteReference("/* BridgeImportService */\n// FcprojImporter\nnamespace Example;"));
    }

    private static bool HasReverseWriteReference(string source)
    {
        var code = Regex.Replace(source, @"/\*(?s:.*?)\*/|(?m:^[\t ]*//[^\r\n]*)", "");
        return Regex.IsMatch(code, @"\b(?:FcprojImporter|BridgeImportService)\b");
    }

    private async Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var text = await stdout;
        var errors = await stderr;
        Assert.True(process.ExitCode == 0, $"{executable} {string.Join(' ', arguments)}\nexit={process.ExitCode}\n{text}\n{errors}");
        return text;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LiveClipper.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LiveClipper.sln");
    }

    private static string Tool(string name)
    {
        var fromEnv = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_" + name.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveClipper", "tools", "ffmpeg", "bin", name + ".exe");
        return File.Exists(bundled) ? bundled : name;
    }

    private static string FilmCraftCli()
    {
        var direct = Environment.GetEnvironmentVariable("FILMCRAFT_CLI");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        var home = Environment.GetEnvironmentVariable("FILMCRAFT_HOME");
        if (!string.IsNullOrWhiteSpace(home)) return Path.Combine(home, OperatingSystem.IsWindows() ? "filmcraft-cli.exe" : "filmcraft-cli");
        return FilmCraftCliRunner.FindCli() ?? "filmcraft-cli";
    }
}
