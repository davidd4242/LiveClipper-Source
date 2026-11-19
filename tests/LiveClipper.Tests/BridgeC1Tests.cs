using System.ComponentModel;
using LiveClipper.Core.Bridge;

namespace LiveClipper.Tests;

/// <summary>C1 MVP：临时工程路径安全、TTL 清理、FilmCraft 安装探测与错误态（不启动真实 GUI）。</summary>
public sealed class BridgeC1Tests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "liveclipper-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData("inside.fcproj", true)]
    [InlineData("sub/dir/demo.fcproj", true)]
    [InlineData("../outside.fcproj", false)]
    [InlineData("..\\outside.fcproj", false)]
    [InlineData("", false)]
    public void IsUnderBridgeRoundtrip_RejectsTraversal(string relative, bool expected)
    {
        var storage = TempDir();
        try
        {
            var root = FcprojExportService.BridgeRoundtripRoot(storage);
            var candidate = relative.Length == 0 ? "" : Path.GetFullPath(Path.Combine(root, relative));
            Assert.Equal(expected, FcprojExportService.IsUnderBridgeRoundtrip(storage, candidate));
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [Fact]
    public void IsUnderBridgeRoundtrip_RejectsSiblingDirectoryAndOutsideAbsolute()
    {
        var storage = TempDir();
        try
        {
            Assert.False(FcprojExportService.IsUnderBridgeRoundtrip(
                storage, Path.Combine(storage, "bridge-roundtrip-evil", "x.fcproj")));
            Assert.False(FcprojExportService.IsUnderBridgeRoundtrip(
                storage, Path.Combine(Path.GetTempPath(), "evil-" + Guid.NewGuid().ToString("N"), "x.fcproj")));
            Assert.False(FcprojExportService.IsUnderBridgeRoundtrip(storage, "bridge-roundtrip/relative.fcproj"));
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [Fact]
    public void SanitizeClipId_StripsSeparatorsAndCapsLength()
    {
        Assert.Equal("a_b_c", FcprojExportService.SanitizeClipId("a/b\\c"));
        Assert.Equal("__", FcprojExportService.SanitizeClipId(".."));
        Assert.Equal(80, FcprojExportService.SanitizeClipId(new string('x', 120)).Length);
    }

    [Fact]
    public void BuildWorkDir_IsInsideBridgeRoundtripAndCollisionSuffixes()
    {
        var storage = TempDir();
        try
        {
            var timestamp = new DateTimeOffset(2026, 10, 27, 1, 2, 3, TimeSpan.Zero);
            var first = FcprojExportService.CreateWorkDir(storage, "clip/1", timestamp);
            var second = FcprojExportService.CreateWorkDir(storage, "clip/1", timestamp);
            Assert.NotEqual(first, second);
            Assert.True(FcprojExportService.IsUnderBridgeRoundtrip(storage, Path.Combine(first, "map", "demo.fcproj")));
            Assert.True(FcprojExportService.IsUnderBridgeRoundtrip(storage, Path.Combine(second, "meta.json")));
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [Fact]
    public void Locate_WithExplicitMissingPath_ReportsNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-filmcraft-" + Guid.NewGuid().ToString("N"), "filmcraft-cli.exe");
        var probe = FilmCraftLocator.Locate(overridePath: missing);
        Assert.False(probe.Found);
        Assert.Null(probe.Install);
        Assert.Contains(probe.Searched, path => path.Contains("filmcraft-cli.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Locate_WithFakeCli_ReportsVersionMismatch()
    {
        var dir = TempDir();
        try
        {
            var cli = Path.Combine(dir, "filmcraft-cli.cmd");
            File.WriteAllText(cli, "@echo off\r\necho filmcraft-cli 0.12.0\r\n");
            var probe = FilmCraftLocator.Locate(overridePath: cli);
            Assert.True(probe.Found);
            Assert.NotNull(probe.Install);
            Assert.False(probe.Install!.VersionOk);
            Assert.Equal("0.12.0", probe.Install.Version);
            Assert.Contains("0.13.0", probe.Install.Reason);
            Assert.False(probe.Install.GuiExists);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Locate_WithPinnedFakeCli_IsVersionOk()
    {
        var dir = TempDir();
        try
        {
            var cli = Path.Combine(dir, "filmcraft-cli.cmd");
            File.WriteAllText(cli, "@echo off\r\necho filmcraft-cli 0.13.0\r\n");
            var probe = FilmCraftLocator.Locate(overridePath: cli);
            Assert.True(probe.Found);
            Assert.NotNull(probe.Install);
            Assert.True(probe.Install!.VersionOk);
            Assert.Equal("0.13.0", probe.Install.Version);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Locate_WithGuiOverride_ProbesCliInSameDirectory_NeverGui()
    {
        var dir = TempDir();
        try
        {
            var gui = Path.Combine(dir, "filmcraft.exe");
            var cli = Path.Combine(dir, "filmcraft-cli.exe");
            File.WriteAllText(gui, "fake-gui");
            File.WriteAllText(cli, "fake-cli");

            string? probed = null;
            var probe = FilmCraftLocator.Locate(
                overridePath: gui,
                versionProbe: (path, _) =>
                {
                    probed = path;
                    return "0.13.0";
                });

            Assert.Equal(cli, probed);
            Assert.NotNull(probe.Install);
            Assert.True(probe.Install!.VersionOk);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Locate_WithOnlyGui_DoesNotProbeAndReportsMissingCli()
    {
        var dir = TempDir();
        try
        {
            var gui = Path.Combine(dir, "filmcraft.exe");
            File.WriteAllText(gui, "fake-gui");

            var probe = FilmCraftLocator.Locate(
                overridePath: gui,
                versionProbe: (_, _) => throw new InvalidOperationException("不允许执行 GUI 探测"));

            Assert.True(probe.Found);
            Assert.NotNull(probe.Install);
            Assert.False(probe.Install!.VersionOk);
            Assert.Null(probe.Install.Version);
            Assert.Contains("filmcraft-cli", probe.Install.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProbeVersion_OnTimeout_KillsChildAndReturnsNull()
    {
        var dir = TempDir();
        try
        {
            var cli = Path.Combine(dir, "filmcraft-cli.cmd");
            File.WriteAllText(cli, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var probe = FilmCraftLocator.Locate(
                overridePath: cli,
                versionTimeout: TimeSpan.FromMilliseconds(300));
            watch.Stop();

            Assert.Null(probe.Install!.Version);
            Assert.False(probe.Install.VersionOk);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"超时未及时返回：{watch.Elapsed}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateWorkDir_WithSpacesAndChinese_IsSafeAndInsideRoot()
    {
        var storage = TempDir();
        try
        {
            var clipId = "中文 片段 01/..\\bad";
            var workDir = FcprojExportService.CreateWorkDir(
                storage, clipId, new DateTimeOffset(2026, 10, 27, 2, 0, 0, TimeSpan.Zero));
            Assert.True(Directory.Exists(workDir));
            Assert.Contains("中文", workDir);
            Assert.DoesNotContain(' ', Path.GetFileName(workDir));
            Assert.True(FcprojExportService.IsUnderBridgeRoundtrip(storage, Path.Combine(workDir, "map", "demo.fcproj")));
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [Fact]
    public void PruneWorkDirs_KeepsNewest20AndDropsExpired()
    {
        var storage = TempDir();
        try
        {
            var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
            var created = new List<string>();
            for (var index = 0; index < 23; index++)
                created.Add(FcprojExportService.CreateWorkDir(storage, "clip-a", now.AddMinutes(-index)));

            var removed = FcprojExportService.PruneWorkDirs(storage, "clip-a", now);
            Assert.Equal(3, removed.Count);
            Assert.True(Directory.Exists(created[0]), "最新一份应保留");
            Assert.False(Directory.Exists(created[22]), "第 21 份之后应删除");
            Assert.Equal(20, Directory.GetDirectories(Path.Combine(
                FcprojExportService.BridgeRoundtripRoot(storage), "clip-a")).Length);

            // 取严第二条：未满 20 份但超过 7 天的旧工程也要清掉。
            var old = FcprojExportService.CreateWorkDir(storage, "clip-b", now.AddDays(-8));
            var fresh = FcprojExportService.CreateWorkDir(storage, "clip-b", now.AddMinutes(-1));
            var removedOld = FcprojExportService.PruneWorkDirs(storage, "clip-b", now);
            Assert.Contains(old, removedOld);
            Assert.True(Directory.Exists(fresh));
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [Fact]
    public void Classify_NotInstalled_Returns404FilmCraftNotFound()
    {
        var probe = new FilmCraftProbe(false, null, new[] { "C:\\A", "C:\\B" });
        var failure = FilmCraftOpenClassifier.Classify(probe);
        Assert.NotNull(failure);
        Assert.Equal(404, failure!.StatusCode);
        Assert.Equal("FilmCraftNotFound", failure.ErrorClass);
        Assert.Contains("0.13.0", failure.Message);
        Assert.Contains("C:\\A", failure.Detail);
    }

    [Fact]
    public void Classify_VersionTooLow_Returns409VersionMismatch()
    {
        var install = new FilmCraftInstall(
            "C:\\FC\\filmcraft.exe", true, "C:\\FC\\filmcraft-cli.exe", "0.12.0", false,
            "版本不符：检测到 0.12.0，需要 0.13.0");
        var failure = FilmCraftOpenClassifier.Classify(new FilmCraftProbe(true, install, new[] { "C:\\FC" }));
        Assert.NotNull(failure);
        Assert.Equal(409, failure!.StatusCode);
        Assert.Equal("VersionMismatch", failure.ErrorClass);
        Assert.Contains("0.12.0", failure.Message);
        Assert.Contains("0.13.0", failure.Detail);
    }

    [Fact]
    public void Classify_GuiMissing_Returns404FilmCraftNotFoundWithGuiPath()
    {
        var install = new FilmCraftInstall(
            "C:\\FC\\filmcraft.exe", false, "C:\\FC\\filmcraft-cli.exe", "0.13.0", true, "检测到 FilmCraft 0.13.0");
        var failure = FilmCraftOpenClassifier.Classify(new FilmCraftProbe(true, install, new[] { "C:\\FC" }));
        Assert.NotNull(failure);
        Assert.Equal(404, failure!.StatusCode);
        Assert.Equal("FilmCraftNotFound", failure.ErrorClass);
        Assert.Equal("C:\\FC\\filmcraft.exe", failure.Detail);
    }

    [Fact]
    public void Launch_WhenProcessStartThrows_ReturnsFilmCraftError()
    {
        var result = FilmCraftGuiLauncher.Launch(
            "C:\\fake\\filmcraft.exe",
            _ => throw new Win32Exception(5, "Access is denied."));
        Assert.Null(result.Pid);
        Assert.Equal("FilmCraftError", result.ErrorClass);
        Assert.Contains("无法启动", result.Message);
        Assert.Contains("denied", result.Detail);
    }

    [Fact]
    public void Launch_WithInjectedStarter_ReturnsPid()
    {
        var result = FilmCraftGuiLauncher.Launch("C:\\fake\\filmcraft.exe", _ => 4242);
        Assert.Equal(4242, result.Pid);
        Assert.Null(result.ErrorClass);
    }
}
