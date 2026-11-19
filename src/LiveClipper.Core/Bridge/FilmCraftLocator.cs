using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LiveClipper.Core.Bridge;

/// <summary>C1 MVP：FilmCraft 安装探测结果。VersionOk=false 表示装了但版本不符（0.13.0 pin）。</summary>
public sealed record FilmCraftInstall(
    string GuiPath,
    bool GuiExists,
    string CliPath,
    string? Version,
    bool VersionOk,
    string Reason);

public sealed record FilmCraftProbe(
    bool Found,
    FilmCraftInstall? Install,
    IReadOnlyList<string> Searched);

/// <summary>
/// C1：定位 FilmCraft 0.13.0（GUI + CLI）。顺序：环境变量/显式覆盖 → 注册表（卸载信息/App Paths）→ 常见安装目录。
/// 显式覆盖（LIVECLIPPER_FILMCRAFT_CLI 或参数）只认该路径，找不到就 NotFound，便于测试/隔离；
/// 版本不符不静默降级，由端点返回 VersionMismatch。
/// </summary>
public static class FilmCraftLocator
{
    public const string ExpectedVersion = "0.13.0";

    public static FilmCraftProbe Locate(
        string? overridePath = null,
        TimeSpan? versionTimeout = null,
        Func<string, TimeSpan, string?>? versionProbe = null)
    {
        var timeout = versionTimeout ?? TimeSpan.FromSeconds(10);
        var probeVersion = versionProbe ?? ProbeVersion;
        var searched = new List<string>();
        var explicitPath = string.IsNullOrWhiteSpace(overridePath)
            ? Environment.GetEnvironmentVariable("LIVECLIPPER_FILMCRAFT_CLI")
            : overridePath;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // 显式指定即视为隔离/测试：不再回退到自动探测，避免“配错了却悄悄用了别处安装”。
            candidates.Add(Path.GetFullPath(explicitPath.Trim()));
        }
        else
        {
            candidates.AddRange(RegistryCandidates());
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(programFilesX86, "FilmCraft"));
            candidates.Add(Path.Combine(programFiles, "FilmCraft"));
            candidates.Add(Path.Combine(localAppData, "Programs", "FilmCraft"));
            candidates.Add(Path.Combine(localAppData, "FilmCraft"));
        }

        FilmCraftProbe? guiOnly = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            searched.Add(candidate);
            var resolved = ResolveCandidate(candidate);
            if (resolved is null) continue;
            var (directory, cliPath, guiPath) = resolved.Value;
            var guiExists = File.Exists(guiPath);
            if (!File.Exists(cliPath))
            {
                // App Paths 可能直接指向 filmcraft.exe：绝不能拿 GUI 去 --version（GUI 子系统会弹窗且不退出）。
                if (guiExists)
                {
                    guiOnly ??= new FilmCraftProbe(
                        true,
                        new FilmCraftInstall(guiPath, true, cliPath, null, false,
                            "找到 FilmCraft 安装，但缺少 filmcraft-cli.exe：无法安全读取版本（不会启动 GUI 探测）"),
                        searched.ToList());
                }
                continue;
            }

            var version = probeVersion(cliPath, timeout);
            var versionOk = version is not null && string.Equals(version, ExpectedVersion, StringComparison.Ordinal);
            var reason = versionOk
                ? "检测到 FilmCraft " + version
                : version is null
                    ? "无法读取 FilmCraft CLI 版本"
                    : $"版本不符：检测到 {version}，需要 {ExpectedVersion}";
            return new FilmCraftProbe(
                true,
                new FilmCraftInstall(guiPath, guiExists, cliPath, version, versionOk, reason),
                searched);
        }

        if (guiOnly is not null) return guiOnly;
        return new FilmCraftProbe(false, null, searched);
    }

    /// <summary>
    /// 把候选归一成 (安装目录, CLI 路径, GUI 路径)。候选是目录（注册表 InstallLocation/常见目录）、
    /// 显式 CLI 路径、或 App Paths 指向的 filmcraft.exe 都覆盖；只有 CLI 会被执行版本探测。
    /// </summary>
    private static (string Directory, string CliPath, string GuiPath)? ResolveCandidate(string candidate)
    {
        string directory;
        string cliPath;
        if (Directory.Exists(candidate))
        {
            directory = candidate;
            cliPath = Path.Combine(candidate, "filmcraft-cli.exe");
        }
        else if (File.Exists(candidate))
        {
            directory = Path.GetDirectoryName(candidate) ?? candidate;
            var name = Path.GetFileName(candidate);
            cliPath = string.Equals(name, "filmcraft.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(directory, "filmcraft-cli.exe")
                : candidate;
        }
        else
        {
            directory = Path.GetDirectoryName(candidate) ?? candidate;
            cliPath = Path.Combine(directory, "filmcraft-cli.exe");
        }

        if (string.IsNullOrWhiteSpace(directory)) return null;
        return (directory, cliPath, Path.Combine(directory, "filmcraft.exe"));
    }

    private static IEnumerable<string> RegistryCandidates()
    {
        var results = new List<string>();
        if (!OperatingSystem.IsWindows()) return results;
        TryReadRegistryValue(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FilmCraft", "InstallLocation", results);
        TryReadRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FilmCraft", "InstallLocation", results);
        TryReadRegistryValue(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\FilmCraft", "InstallLocation", results);
        TryReadRegistryValue(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\filmcraft.exe", null, results);
        TryReadRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\filmcraft.exe", null, results);
        return results.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void TryReadRegistryValue(RegistryKey hive, string keyPath, string? valueName, List<string> results)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key is null) return;
            var value = valueName is null ? key.GetValue(null) as string : key.GetValue(valueName) as string;
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim().Trim('"');
            // DisplayIcon 可能是 "C:\...\filmcraft.exe,0"。
            var comma = value.IndexOf(',');
            if (comma > 0) value = value[..comma];
            if (value.Length > 0) results.Add(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // 注册表不可读时跳过，继续常见路径。
        }
    }

    private static string? ProbeVersion(string cliPath, TimeSpan timeout)
    {
        // 兜底：只允许 filmcraft-cli*（或显式 .cmd/.bat）被探测；GUI 可执行文件永不启动。
        var fileName = Path.GetFileName(cliPath);
        if (string.Equals(fileName, "filmcraft.exe", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var start = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(cliPath) ?? Environment.CurrentDirectory,
            };
            if (cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || cliPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add(cliPath);
                start.ArgumentList.Add("--version");
            }
            else
            {
                start.FileName = cliPath;
                start.ArgumentList.Add("--version");
            }

            using var process = Process.Start(start);
            if (process is null) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds)))
            {
                // 超时必须杀掉整棵进程树，避免留下 --version 孤儿（旧实现只 WaitForExit 不 kill）。
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return null;
            }

            string output;
            string error;
            try
            {
                output = outputTask.GetAwaiter().GetResult();
                error = errorTask.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return null;
            }

            var match = Regex.Match(output + error, @"\d+\.\d+\.\d+");
            return match.Success ? match.Value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
