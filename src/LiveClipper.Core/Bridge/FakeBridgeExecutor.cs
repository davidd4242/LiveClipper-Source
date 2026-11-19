using System.Text;

namespace LiveClipper.Core.Bridge;

/// <summary>
/// 开发/契约测试用执行器：不依赖 FilmCraft，模拟 600ms 分步进度并写出一个占位 mp4。
/// 服务用 --bridge-fake 或 LIVECLIPPER_BRIDGE_FAKE=1 启用；能力版本串故意保留 "0.13.0" 以让 harness 走真实模式子集。
/// </summary>
public sealed class FakeBridgeExecutor : IBridgeExecutor
{
    private readonly int _durationMs;

    public FakeBridgeExecutor(int durationMs = 600)
    {
        _durationMs = Math.Max(20, durationMs);
    }

    public BridgeCapabilitiesInfo Capabilities { get; } = new(
        "0.13.0",
        15,
        ["transitions", "speed", "keyframes", "fades", "masks", "captions", "multiTrack"],
        BridgeJobService.SupportedVariants,
        BridgeJobService.SupportedFormats);

    public async Task<string> RunAsync(BridgeJobContext context, CancellationToken token)
    {
        var steps = 5;
        for (var step = 1; step <= steps; step++)
        {
            await Task.Delay(Math.Max(1, _durationMs / steps), token).ConfigureAwait(false);
            context.ReportProgress(step * 100 / steps);
        }
        var directory = Path.GetDirectoryName(context.ResultPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var placeholder = Encoding.ASCII.GetBytes("fake-mp4-placeholder-for-contract-tests\n");
        await File.WriteAllBytesAsync(context.ResultPath, placeholder, token).ConfigureAwait(false);
        return context.ResultPath;
    }
}
