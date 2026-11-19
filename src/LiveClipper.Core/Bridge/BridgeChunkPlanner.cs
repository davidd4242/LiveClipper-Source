namespace LiveClipper.Core.Bridge;

/// <summary>分段导出计划：单段开始时间与时长（秒）。</summary>
public readonly record struct BridgeChunk(double StartSeconds, double LengthSeconds);

/// <summary>
/// 长片导出分块策略（B12-4）。FilmCraft 0.13.0 是 32 位进程，长片一次性导出会 OOM；
/// 30 秒以内保持单段，超过则按 9 秒（30fps 下 270 帧，低于实测 OOM 阈值）切段，
/// 由执行器逐段导出后 ffmpeg concat。上限 30 分钟避免无界任务。
/// </summary>
public static class BridgeChunkPlanner
{
    public const double SingleSegmentLimitSeconds = 30.0;
    public const double ChunkSeconds = 9.0;
    public const double MaxDurationSeconds = 1800.0;

    public static IReadOnlyList<BridgeChunk> Plan(double durationSeconds, double chunkSeconds = ChunkSeconds)
    {
        var total = durationSeconds > 0 ? Math.Min(durationSeconds, MaxDurationSeconds) : 4.0;
        if (total <= SingleSegmentLimitSeconds)
            return [new BridgeChunk(0, total)];

        var size = chunkSeconds > 0.1 ? chunkSeconds : ChunkSeconds;
        var chunks = new List<BridgeChunk>();
        for (var start = 0.0; start < total - 1e-9; start += size)
            chunks.Add(new BridgeChunk(start, Math.Min(size, total - start)));
        return chunks;
    }
}
