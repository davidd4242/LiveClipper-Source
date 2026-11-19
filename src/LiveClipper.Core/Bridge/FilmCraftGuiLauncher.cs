using System.ComponentModel;
using System.Diagnostics;

namespace LiveClipper.Core.Bridge;

/// <summary>
/// C1：启动 FilmCraft GUI。默认直接 Process.Start(guiPath)，但允许注入 starter，
/// 这样错误态（Process.Start 抛异常）可以在单测里覆盖而不真的弹窗。
/// </summary>
public sealed record FilmCraftLaunchResult(int? Pid, string? ErrorClass, string? Message, string? Detail);

public static class FilmCraftGuiLauncher
{
    public static FilmCraftLaunchResult Launch(string guiPath, Func<string, int?>? starter = null)
    {
        if (string.IsNullOrWhiteSpace(guiPath))
            return new FilmCraftLaunchResult(null, "FilmCraftError", "无法启动 FilmCraft", "缺少 filmcraft.exe 路径");

        try
        {
            if (starter is not null)
                return new FilmCraftLaunchResult(starter(guiPath), null, null, null);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = guiPath,
                UseShellExecute = false,
            });
            return new FilmCraftLaunchResult(process?.Id, null, null, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new FilmCraftLaunchResult(null, "FilmCraftError", "无法启动 FilmCraft", ex.Message);
        }
    }
}
