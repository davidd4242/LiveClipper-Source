using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

/// <summary>
/// 转场布局与整数帧网格的对应关系（interface-contract §3.2 / §5.4 跨端探针夹具同形：30fps，A=src 0..2s，B=src 5..8s，12 帧 center 转场）。
/// 期望值是「帧」，由 30fps 逐项换算，不依赖布局代码里的任何常量。
/// </summary>
public sealed class TransitionFrameGridTests
{
    private const int Fps = 30;
    private static long Frame(double seconds) => (long)Math.Round(seconds * Fps);

    [Fact]
    public void CenterDissolveOf12Frames_LandsOnIntegerFrames_CutAtB_StartAndWindow42To54()
    {
        // B 的源入点(5s)与 A 的出点(2s)不连续——布局只看时长，不看源位置。
        var items = new[]
        {
            new TimelineItem("a", "source", 0, 2, Track: 1),
            new TimelineItem("b", "source", 5, 8, Track: 1),
        };
        var transitions = new[] { new EditorTransition("t1", "b", 1, "cross-dissolve", 12.0 / Fps, "center") };

        var cut = Assert.Single(EditorRenderService.ResolveTransitions(items, transitions, Fps));
        var starts = EditorRenderService.SequenceStarts(items, [cut]);

        // cut.Start 是「剪辑点」（= B 的序列起点），center 窗口以它为中心：[cut-d/2, cut+d/2)
        var span = EditorRenderService.TransitionSpan("center", cut.EffectiveDuration);
        Assert.Equal(12, Frame(cut.EffectiveDuration));
        Assert.Equal(48, Frame(cut.Start));                          // cut 帧
        Assert.Equal(42, Frame(cut.Start + span.Start));             // 窗口起点帧
        Assert.Equal(54, Frame(cut.Start + span.End));               // 窗口终点帧（不含）
        Assert.Equal(48, Frame(starts["b"]));                         // B 的 timelineStart = 60 - 12
        Assert.Equal(0, Frame(starts["a"]));
        Assert.Equal(138, Frame(EditorRenderService.SequenceDuration(items, [cut]))); // 60 + 90 - 12
        // 所有边界必须是整数帧（不允许小数帧漂移）
        foreach (var s in new[] { cut.Start, cut.EffectiveDuration, starts["b"], cut.Start + span.Start, cut.Start + span.End })
            Assert.True(Math.Abs(s * Fps - Math.Round(s * Fps)) < 1e-6, $"{s}s is not on the {Fps}fps grid");
    }

    // ---- 契约 §3.1：请求时长量化到整数帧 ----
    private static TransitionCut ResolveOne(double requestedSeconds, double bLengthSeconds = 3)
    {
        var items = new[]
        {
            new TimelineItem("a", "source", 0, 2, Track: 1),
            new TimelineItem("b", "source", 5, 5 + bLengthSeconds, Track: 1),
        };
        var transitions = new[] { new EditorTransition("t1", "b", 1, "cross-dissolve", requestedSeconds, "center") };
        return Assert.Single(EditorRenderService.ResolveTransitions(items, transitions, Fps));
    }

    private static void AssertFrames(int expectedFrames, TransitionCut cut)
    {
        Assert.True(Math.Abs(cut.EffectiveDuration - (double)expectedFrames / Fps) < 1e-9,
            $"expected {expectedFrames} frames ({(double)expectedFrames / Fps}s), got {cut.EffectiveDuration}s");
        Assert.True(Math.Abs(cut.EffectiveDuration * Fps - Math.Round(cut.EffectiveDuration * Fps)) < 1e-6);
    }

    [Fact]
    public void NonIntegerFrameRequest_IsRoundedToNearestFrame()
    {
        // 0.41s @30fps = 12.3 帧 → 12 帧 = 0.4s
        var cut = ResolveOne(0.41);
        AssertFrames(12, cut);
        Assert.True(Math.Abs(cut.EffectiveDuration - 0.4) < 1e-9);
    }

    [Fact]
    public void MidpointRequest_RoundsAwayFromZero_14NotBankers13()
    {
        // 0.45s @30fps = 13.5 帧，恰在中点：向上进位得 14，而不是截断/向下取整得到的 13。
        // （注：13.5 的最近偶数也是 14，所以这条不能区分 AwayFromZero 与银行家舍入；区分见下一条 12.5 帧用例。）
        var cut = ResolveOne(0.45);
        AssertFrames(14, cut);
    }

    [Fact]
    public void MidpointRequest_OddEvenSeparates_AwayFromZeroFromBankers()
    {
        // 12.5 帧：AwayFromZero → 13；银行家舍入（ToEven）→ 12。只有这条能真正区分两种中点规则。
        var cut = ResolveOne(12.5 / Fps);
        AssertFrames(13, cut);
    }

    [Fact]
    public void TinyRequest_ClampsToOneFrame_NeverZero()
    {
        // 0.001s = 0.03 帧，四舍五入为 0，必须被抬到最短 1 帧
        var cut = ResolveOne(0.001);
        AssertFrames(1, cut);
        Assert.True(Math.Abs(cut.EffectiveDuration - 1.0 / Fps) < 1e-9);
    }

    [Fact]
    public void LargeRequest_IsCappedByAvailableClip_AndStaysOnIntegerFrames()
    {
        // B 只有 5.1 帧（0.17s）：上限向下取整为 5 帧，请求 1s（30 帧）必须被封顶到 5 帧，且不能被取整进位到 6。
        var cut = ResolveOne(1.0, bLengthSeconds: 0.17);
        AssertFrames(5, cut);
        Assert.True(cut.EffectiveDuration <= 0.17 + 1e-9);
    }
}
