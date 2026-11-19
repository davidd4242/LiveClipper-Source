namespace LiveClipper.Core.Bridge;

/// <summary>
/// Bridge 任务日志轮转（B12-4）：不引入依赖，按「最旧优先」删除，
/// 超过 7 天或总数超过 50 个的 *.log 会被清理；写日志后顺带执行一次，目录通常很小。
/// </summary>
public static class BridgeLogRotation
{
    public const int MaxAgeDays = 7;
    public const int MaxFiles = 50;

    public static int Prune(string directory, DateTimeOffset? now = null, int maxAgeDays = MaxAgeDays, int maxFiles = MaxFiles)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
        var current = now ?? DateTimeOffset.UtcNow;
        var logs = Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToList();
        var deleted = 0;
        for (var index = 0; index < logs.Count; index++)
        {
            var age = current - new DateTimeOffset(DateTime.SpecifyKind(logs[index].LastWriteTimeUtc, DateTimeKind.Utc));
            if (index < maxFiles && age.TotalDays <= maxAgeDays) continue;
            try
            {
                logs[index].Delete();
                deleted++;
            }
            catch (IOException)
            {
                // 被占用时跳过，下次再清理。
            }
            catch (UnauthorizedAccessException)
            {
                // 没有权限时跳过。
            }
        }
        return deleted;
    }
}
