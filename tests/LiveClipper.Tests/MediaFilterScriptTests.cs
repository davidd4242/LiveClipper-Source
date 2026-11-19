using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using LiveClipper.Core;

namespace LiveClipper.Tests;

[CollectionDefinition("R76FS_FilterScriptLifetime", DisableParallelization = true)]
public sealed class R76FSFilterScriptCollection { }

[Collection("R76FS_FilterScriptLifetime")]
public sealed class MediaFilterScriptTests
{
    private const string Source = "nullsrc=s=16x16:r=1:d=1,geq=lum='X*8+Y':cb=100:cr=150";
    private const string ShortVideo = "hflip,crop=w='iw':h='ih'";

    [Fact]
    public void R76FS_ShortCommandPreservesEveryArgumentAndCreatesNoFiles()
    {
        using var root = new TestDirectory();
        var tools = new MediaTools("ffmpeg");
        string[] args = ["-y", "-i", "输入 with spaces.mp4", "-vf", "drawtext=text='quote\\: \"中文\"',format=yuv420p", "-af", "volume=0.5", "-map", "0:v", "out.mp4"];
        var original = args.ToArray();
        using var prepared = tools.PrepareFilterScripts(tools.Ffmpeg, args, root.Path);
        Assert.Equal(original, prepared.Arguments);
        Assert.Equal(original, args);
        Assert.Null(prepared.DirectoryPath);
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void R76FS_ThresholdIncludesExecutableSeparatorsAndQuotes(int over, bool rewritten)
    {
        using var root = new TestDirectory();
        var tools = new MediaTools("ffmpeg");
        var overhead = MediaTools.WindowsCommandLineLength(tools.Ffmpeg, ["-vf", "x"]) - 1;
        string[] args = ["-vf", new string('x', (int)(MediaTools.FilterScriptThreshold - overhead + over))];
        Assert.Equal((long)MediaTools.FilterScriptThreshold + over, MediaTools.WindowsCommandLineLength(tools.Ffmpeg, args));
        using var prepared = tools.PrepareFilterScripts(tools.Ffmpeg, args, root.Path);
        Assert.Equal(rewritten ? "-/vf" : "-vf", prepared.Arguments[0]);
        Assert.Equal(rewritten, prepared.DirectoryPath is not null);
        if (rewritten) Assert.Equal(args[1], File.ReadAllText(prepared.Arguments[1]));
        else Assert.Equal(args, prepared.Arguments);
    }

    [Fact]
    public void R76FS_WindowsLengthAccountsForDoubleQuotesAndTrailingBackslashes()
    {
        Assert.Equal(6L, MediaTools.WindowsCommandLineLength("x", ["a"]));
        Assert.Equal(7L, MediaTools.WindowsCommandLineLength("x", [""]));
        Assert.Equal(10L, MediaTools.WindowsCommandLineLength("x", ["a b"]));
        Assert.Equal(11L, MediaTools.WindowsCommandLineLength("x", ["a\"b"]));
        Assert.Equal(12L, MediaTools.WindowsCommandLineLength("x", ["a b\\"]));
        var tools = new MediaTools("ffmpeg");
        string[] args = ["-vf", "drawtext=text='" + new string('"', 13000) + "'"];
        Assert.True(args.Sum(x => x.Length) < MediaTools.FilterScriptThreshold);
        Assert.True(MediaTools.WindowsCommandLineLength(tools.Ffmpeg, args) > MediaTools.FilterScriptThreshold);
        using var prepared = tools.PrepareFilterScripts(tools.Ffmpeg, args);
        Assert.Equal("-/vf", prepared.Arguments[0]);
        Assert.Equal(args[1], File.ReadAllText(prepared.Arguments[1]));
    }

    [Theory]
    [InlineData("-vf", "-/vf")]
    [InlineData("-af", "-/af")]
    [InlineData("-filter_complex", "-/filter_complex")]
    [InlineData("-filter", "-/filter")]
    [InlineData("-filter:v:0", "-/filter:v:0")]
    [InlineData("-filter:a:0", "-/filter:a:0")]
    public void R76FS_LongGraphWritesExactUtf8WithoutBomAndCleansUp(string option, string replacement)
    {
        using var root = new TestDirectory();
        var tools = new MediaTools("ffmpeg");
        var graph = "[0:v]drawtext=text='中文\\: \"quoted\"':fontfile='C\\:/字体/font.ttf',\r\n" + LongVideo() + "[v]\n";
        string[] args = ["-i", "input path.mp4", option, graph, "-map", "[v]", "output path.mp4"];
        var original = args.ToArray();
        var prepared = tools.PrepareFilterScripts(tools.Ffmpeg, args, root.Path);
        var script = prepared.Arguments[3];
        try
        {
            Assert.Equal(original, args);
            Assert.Equal(["-i", "input path.mp4", replacement, script, "-map", "[v]", "output path.mp4"], prepared.Arguments);
            Assert.Equal(Encoding.UTF8.GetBytes(graph), File.ReadAllBytes(script));
            Assert.Equal(graph, File.ReadAllText(script, Encoding.UTF8));
            Assert.Single(Directory.GetFiles(prepared.DirectoryPath!));
            Assert.True(MediaTools.WindowsCommandLineLength(tools.Ffmpeg, prepared.Arguments) < MediaTools.FilterScriptThreshold);
        }
        finally { prepared.Dispose(); }
        Assert.False(File.Exists(script));
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
        prepared.Dispose();
    }

    [Fact]
    public void R76FS_CumulativeLengthRewritesMultipleGraphsWithDistinctFiles()
    {
        using var root = new TestDirectory();
        var tools = new MediaTools("ffmpeg");
        var first = "null@" + new string('a', 13000);
        var second = "anull@" + new string('b', 13000);
        Assert.True(first.Length < MediaTools.FilterScriptThreshold && second.Length < MediaTools.FilterScriptThreshold);
        string[] args = ["-vf", first, "-af", second, "-vf", "hflip", "out.mp4"];
        using var prepared = tools.PrepareFilterScripts(tools.Ffmpeg, args, root.Path);
        Assert.Equal("-/vf", prepared.Arguments[0]);
        Assert.Equal("-/af", prepared.Arguments[2]);
        Assert.Equal("-/vf", prepared.Arguments[4]);
        Assert.Equal(3, Directory.GetFiles(prepared.DirectoryPath!).Length);
        Assert.NotEqual(prepared.Arguments[1], prepared.Arguments[3]);
        Assert.Equal(first, File.ReadAllText(prepared.Arguments[1]));
        Assert.Equal(second, File.ReadAllText(prepared.Arguments[3]));
        Assert.Equal("hflip", File.ReadAllText(prepared.Arguments[5]));
    }

    [Fact]
    public void R76FS_NonFfmpegAndExistingScriptsRemainUntouched()
    {
        using var root = new TestDirectory();
        var tools = new MediaTools("configured-ffmpeg");
        string[] longFilter = ["-vf", LongVideo()];
        using (var other = tools.PrepareFilterScripts("python", longFilter, root.Path))
        {
            Assert.Equal(longFilter, other.Arguments);
            Assert.Null(other.DirectoryPath);
        }
        string[] scripts = ["-/vf", "existing-v.txt", "-/af", "existing-a.txt", "-/filter_complex", "existing-complex.txt", "-metadata", new string('x', 41000)];
        using var alreadyScripted = tools.PrepareFilterScripts(tools.Ffmpeg, scripts, root.Path);
        Assert.Equal(scripts, alreadyScripted.Arguments);
        Assert.Null(alreadyScripted.DirectoryPath);
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Fact]
    public void R76FS_HdrRewritePrecedesScriptMaterialization()
    {
        using var root = new TestDirectory();
        var tools = new MediaTools("ffmpeg");
        var args = new List<string> { "-vf", LongVideo() + ",format=rgb24", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "out.mp4" };
        EditorRenderService.ConfigureIntermediateArgs(args, new EditorProject { Format = "prores" });
        var expected = args[1];
        Assert.EndsWith("format=gbrp16le", expected);
        using var prepared = tools.PrepareFilterScripts(tools.Ffmpeg, args, root.Path);
        Assert.Equal(expected, File.ReadAllText(prepared.Arguments[1]));
        Assert.Equal(expected, args[1]);
        Assert.Equal(args.Skip(2), prepared.Arguments.Skip(2));
    }

    [Fact]
    public void R76FS_LeaseCleansUpWhenItsConsumerThrows()
    {
        using var root = new TestDirectory();
        var tools = new MediaTools("ffmpeg");
        Assert.Throws<OperationCanceledException>((Action)(() =>
        {
            using var prepared = tools.PrepareFilterScripts(tools.Ffmpeg, ["-vf", LongVideo()], root.Path);
            Assert.True(File.Exists(prepared.Arguments[1]));
            throw new OperationCanceledException();
        }));
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Fact]
    public void R76FS_PreparationFailureDoesNotDeleteAnUnrelatedFile()
    {
        using var root = new TestDirectory();
        var blocked = System.IO.Path.Combine(root.Path, "not-a-directory");
        File.WriteAllText(blocked, "keep");
        var tools = new MediaTools("ffmpeg");
        Assert.ThrowsAny<IOException>(() => tools.PrepareFilterScripts(tools.Ffmpeg, ["-vf", LongVideo()], blocked));
        Assert.Equal("keep", File.ReadAllText(blocked));
        Assert.Single(Directory.GetFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData("-vf")]
    [InlineData("-filter:v:0")]
    [InlineData("-filter_complex")]
    public async Task R76FS_RealMediaToolsLongVideoMatchesShortGraphAndDeletesScripts(string option)
    {
        using var root = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tools = new MediaTools(Ffmpeg());
        var shortPath = System.IO.Path.Combine(root.Path, "short.yuv");
        var longPath = System.IO.Path.Combine(root.Path, "long.yuv");
        await tools.RunAsync(tools.Ffmpeg, VideoArgs("-vf", ShortVideo, shortPath), timeout.Token);
        var graph = option == "-filter_complex" ? "[0:v]\n" + LongVideo() + "\n[v]" : LongVideo() + "\n";
        Assert.True(graph.Length > 40000);
        var original = VideoArgs(option, graph, longPath);
        var result = await tools.RunAsync(tools.Ffmpeg, original, timeout.Token);
        Assert.Equal(graph, original[8]);
        Assert.Equal(384, new FileInfo(shortPath).Length);
        Assert.Equal(File.ReadAllBytes(shortPath), File.ReadAllBytes(longPath));
        AssertScriptsRemovedFromLog(result);
    }

    [Theory]
    [InlineData("-af")]
    [InlineData("-filter:a:0")]
    public async Task R76FS_RealMediaToolsLongAudioMatchesShortPcm(string option)
    {
        using var root = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tools = new MediaTools(Ffmpeg());
        var shortPath = System.IO.Path.Combine(root.Path, "short.f32");
        var longPath = System.IO.Path.Combine(root.Path, "long.f32");
        string[] Args(string filterOption, string graph, string path) => ["-y", "-loglevel", "debug", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000:duration=0.01", filterOption, graph, "-c:a", "pcm_f32le", "-f", "f32le", path];
        await tools.RunAsync(tools.Ffmpeg, Args("-af", "volume='0.5'", shortPath), timeout.Token);
        var graph = string.Join(',', Enumerable.Range(0, 100).Select(i => "anull@node" + i + new string('x', 420))) + ",volume='0.5'\n";
        Assert.True(graph.Length > 40000);
        var result = await tools.RunAsync(tools.Ffmpeg, Args(option, graph, longPath), timeout.Token);
        Assert.Equal(1920, new FileInfo(shortPath).Length);
        Assert.Equal(File.ReadAllBytes(shortPath), File.ReadAllBytes(longPath));
        AssertScriptsRemovedFromLog(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R76FS_RealMediaToolsCapturePreservesExitSemanticsAndCleansUp(bool fail)
    {
        using var root = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tools = new MediaTools(Ffmpeg());
        var graph = LongVideo() + (fail ? ",r76_missing_filter" : "");
        var result = await tools.CaptureAsync(tools.Ffmpeg, VideoArgs("-vf", graph, System.IO.Path.Combine(root.Path, "capture.yuv")), timeout.Token);
        AssertScriptsRemovedFromLog(result);
        if (fail) Assert.Contains("No such filter", result);
        else Assert.Equal(384, new FileInfo(System.IO.Path.Combine(root.Path, "capture.yuv")).Length);
    }

    [Fact]
    public async Task R76FS_RunAsyncStartFailureAndNonzeroExitCleanUpScripts()
    {
        using var root = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var before = ScriptDirectories();
        var missing = new MediaTools(System.IO.Path.Combine(root.Path, "missing-ffmpeg.exe"));
        await Assert.ThrowsAsync<Win32Exception>(() => missing.RunAsync(missing.Ffmpeg, ["-vf", LongVideo()], timeout.Token));
        AssertNoNewScriptDirectories(before);
        var tools = new MediaTools(Ffmpeg());
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.RunAsync(tools.Ffmpeg,
            VideoArgs("-vf", LongVideo() + ",r76_missing_filter", System.IO.Path.Combine(root.Path, "fail.yuv")), timeout.Token));
        AssertNoNewScriptDirectories(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R76FS_PreCanceledCallCreatesNoScriptsAndDoesNotStart(bool capture)
    {
        using var root = new TestDirectory();
        var tools = new MediaTools(System.IO.Path.Combine(root.Path, "missing-ffmpeg.exe"));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var before = ScriptDirectories();
        var running = capture
            ? tools.CaptureAsync(tools.Ffmpeg, ["-vf", LongVideo()], stop.Token)
            : tools.RunAsync(tools.Ffmpeg, ["-vf", LongVideo()], stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        AssertNoNewScriptDirectories(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R76FS_RealCancellationWaitsForExitAndDeletesScripts(bool capture)
    {
        var tools = new MediaTools(Ffmpeg());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var before = ScriptDirectories();
        string[] args = ["-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "testsrc2=s=16x16:r=1", "-vf", LongVideo(), "-f", "null", "-"];
        var running = capture ? tools.CaptureAsync(tools.Ffmpeg, args, stop.Token) : tools.RunAsync(tools.Ffmpeg, args, stop.Token);
        try { Assert.NotEmpty(ScriptDirectories().Except(before)); }
        finally { stop.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        AssertNoNewScriptDirectories(before);
    }

    private static string LongVideo() => string.Join(',', Enumerable.Range(0, 100).Select(i => "null@node" + i + new string('x', 420))) + "," + ShortVideo;

    private static string[] VideoArgs(string option, string graph, string path) =>
        ["-y", "-loglevel", "debug", "-f", "lavfi", "-i", Source, option, graph,
        .. (option == "-filter_complex" ? new[] { "-map", "[v]" } : Array.Empty<string>()),
        "-frames:v", "1", "-pix_fmt", "yuv420p", "-f", "rawvideo", path];

    private static string Ffmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var bundled = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveClipper", "tools", "ffmpeg", "bin", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }

    private static void AssertScriptsRemovedFromLog(string output)
    {
        var paths = Regex.Matches(output, @"with argument '([^'\r\n]*liveclipper-filters-[^'\r\n]+\.txt)'\.")
            .Cast<Match>().Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            Assert.False(File.Exists(path), "Leaked filter script: " + path);
            Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(path)), "Leaked script directory: " + path);
        }
    }

    private static string[] ScriptDirectories() => Directory.GetDirectories(System.IO.Path.GetTempPath(), "liveclipper-filters-*");
    private static void AssertNoNewScriptDirectories(string[] before) => Assert.Empty(ScriptDirectories().Except(before));

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "r76-fs 图 " + Guid.NewGuid().ToString("N"));
        internal TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
