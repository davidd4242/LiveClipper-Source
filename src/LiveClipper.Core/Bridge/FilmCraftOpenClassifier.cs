namespace LiveClipper.Core.Bridge;

/// <summary>
/// C1 open 端点的失败分类：把 FilmCraft 探测结果映射成 HTTP 状态 + 错误体 class，
/// 与 BridgeEndpoints 的错误体约定一致（{ error: { class, message, detail } }）。
/// 单独成纯函数便于单测，不启动 GUI。
/// </summary>
public sealed record FilmCraftOpenFailure(int StatusCode, string ErrorClass, string Message, string? Detail);

public static class FilmCraftOpenClassifier
{
    public static FilmCraftOpenFailure? Classify(FilmCraftProbe probe)
    {
        if (!probe.Found || probe.Install is null)
        {
            var detail = probe.Searched.Count == 0
                ? null
                : string.Join("; ", probe.Searched.Take(6));
            return new FilmCraftOpenFailure(404, "FilmCraftNotFound",
                "未检测到 FilmCraft 0.13.0；可复制工程路径后手动打开", detail);
        }

        if (!probe.Install.VersionOk)
        {
            return new FilmCraftOpenFailure(409, "VersionMismatch", probe.Install.Reason,
                $"需要 {FilmCraftLocator.ExpectedVersion}");
        }

        if (!probe.Install.GuiExists)
        {
            return new FilmCraftOpenFailure(404, "FilmCraftNotFound",
                "找到 FilmCraft CLI，但缺少 filmcraft.exe", probe.Install.GuiPath);
        }

        return null;
    }
}
