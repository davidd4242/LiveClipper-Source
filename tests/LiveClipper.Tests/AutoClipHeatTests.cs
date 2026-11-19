using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

/// <summary>
/// 弹幕热度自动切片的状态机：10 分钟基准、140% 触发、持续后推、120% 释放 + 200 秒回落确认、冷却与延展上限；
/// 以及 A3 的基准重锚、会话重置与跨场次隔离。
/// </summary>
public sealed class AutoClipHeatTests
{
    private static DanmakuEvent Msg(double t)=>new(Guid.NewGuid().ToString("N"),"s",t,DateTimeOffset.UnixEpoch.AddSeconds(t),"danmaku",1,"u","弹幕",1);

    private static void Baseline(DanmakuHeat heat,int count=120,double step=5)
    {
        // 默认策略下 120 条 / 600 秒 = 0.2 条/秒基准。
        for(var i=0;i<count;i++)heat.Add(Msg(i*step));
    }

    [Fact]
    public void Heat_TriggerOnceThenExtend_AndCloseAfterReleaseDwell()
    {
        var heat=new DanmakuHeat();
        Baseline(heat);
        var kinds=new List<HeatSignalKind>();
        for(var i=0;i<60;i++)kinds.Add(heat.Add(Msg(800+i*.1)).Kind);
        Assert.Equal(1,kinds.Count(k=>k==HeatSignalKind.Trigger));
        Assert.Contains(HeatSignalKind.Extend,kinds);
        Assert.True(heat.Active);
        // A2：跌破释放线后先进入 200 秒回落确认期，不立即收尾。
        Assert.Equal(HeatSignalKind.Extend,heat.Advance(1000).Kind);
        Assert.True(heat.Active);
        Assert.Contains("确认中",heat.Reason);
        Assert.Equal(HeatSignalKind.Extend,heat.Advance(1199).Kind);
        var close=heat.Advance(1200);
        Assert.Equal(HeatSignalKind.Close,close.Kind);
        Assert.False(heat.Active);
        Assert.Contains("回落已确认",heat.Reason);
    }

    [Fact]
    public void Heat_ReleaseDwellRestartsWhenHeatRebounds()
    {
        var heat=new DanmakuHeat();
        Baseline(heat);
        for(var i=0;i<60;i++)heat.Add(Msg(800+i*.1));
        Assert.True(heat.Active);
        Assert.Equal(HeatSignalKind.Extend,heat.Advance(1000).Kind);
        Assert.Contains("确认中",heat.Reason);
        // 确认期内回升到释放线以上：确认计时清零，结束点继续后推。
        for(var i=0;i<30;i++)heat.Add(Msg(1100+i*.1));
        Assert.True(heat.Active);
        Assert.DoesNotContain("确认中",heat.Reason);
        for(var t=1300;t<1500;t++)Assert.NotEqual(HeatSignalKind.Close,heat.Advance(t).Kind);
        // 1300 重新开始确认，200 秒后（1500）才收尾。
        Assert.Equal(HeatSignalKind.Close,heat.Advance(1500).Kind);
    }

    [Fact]
    public void Heat_CoolDownBlocksImmediateRetrigger()
    {
        var heat=new DanmakuHeat();
        Baseline(heat);
        for(var i=0;i<60;i++)heat.Add(Msg(800+i*.1));
        Assert.True(heat.Active);
        // 回落确认 200 秒后才收尾；收尾时刻开始 60 秒冷却。
        Assert.Equal(HeatSignalKind.Extend,heat.Advance(1000).Kind);
        Assert.Equal(HeatSignalKind.Close,heat.Advance(1200).Kind);
        // 冷却 60 秒内再次爆发：不触发。
        var cooling=false;
        for(var i=0;i<40;i++)cooling|=heat.Add(Msg(1210+i*.1)).Kind!=HeatSignalKind.None;
        Assert.False(cooling);
        Assert.Contains("冷却",heat.Reason);
        // 冷却结束后同样的爆发可以再次触发。
        var retrigger=false;
        for(var i=0;i<40;i++)retrigger|=heat.Add(Msg(1280+i*.1)).Kind==HeatSignalKind.Trigger;
        Assert.True(retrigger);
    }

    [Fact]
    public void Heat_MaxExtensionForcesCloseWhileStillHot()
    {
        var heat=new DanmakuHeat(new DanmakuHeatOptions{MaxExtensionSeconds=60});
        Baseline(heat);
        for(var i=0;i<60;i++)heat.Add(Msg(800+i*.1));
        Assert.True(heat.Active);
        Assert.Equal(HeatSignalKind.Extend,heat.Advance(860).Kind);
        // 仍然高于释放线，但已超过 60 秒延展上限 → 强制收尾。
        Assert.Equal(HeatSignalKind.Close,heat.Advance(870).Kind);
        Assert.Contains("上限",heat.Reason);
    }

    [Fact]
    public void Heat_ReleaseSecondsZeroClosesImmediately()
    {
        // ReleaseSeconds=0 恢复旧行为：跌破释放线立即收尾，供需要快速回调的旧配置使用。
        var heat=new DanmakuHeat(new DanmakuHeatOptions{ReleaseSeconds=0});
        Baseline(heat);
        for(var i=0;i<60;i++)heat.Add(Msg(800+i*.1));
        Assert.Equal(HeatSignalKind.Close,heat.Advance(1000).Kind);
    }

    [Fact]
    public void Heat_RebasesQuietBaselineAfterTimeout()
    {
        var heat=new DanmakuHeat();
        Baseline(heat);
        Assert.Equal(0.2,heat.Baseline,3);
        // 基准建立后安静期：只有 10 条弹幕，低于触发线，不触发也不重锚。
        for(var i=0;i<12;i++)heat.Add(Msg(610+i*50));
        Assert.Equal(0,heat.RebaseCount);
        // 距上次锚点（610）满 600 秒：用最近 10 分钟窗口重锚。
        // 窗口按 (now-600, now] 取，边界上的 610 秒那条不计入，实际 9 条 → 基准从 0.2/s 降到 9/600。
        Assert.Equal(HeatSignalKind.None,heat.Advance(1211).Kind);
        Assert.Equal(1,heat.RebaseCount);
        Assert.Equal(.2*.75+(5/300d)*.25,heat.Baseline,6);
        Assert.Contains("重评",heat.Reason);
        // 新触发线约 0.023/s；旧基准下 0.25/s 也会触发，但这里验证重锚后仍能按新基准触发。
        var triggered=false;
        for(var i=0;i<30;i++)triggered|=heat.Add(Msg(1300+i*.1)).Kind==HeatSignalKind.Trigger;
        Assert.True(triggered);
    }

    [Fact]
    public void Heat_LowBaselineRequiresStrictlyMoreThan600Seconds_AndUses300SecondSamples()
    {
        var heat=new DanmakuHeat();Baseline(heat);heat.Advance(600);
        heat.Advance(720); // Empty comparison window begins continuous below-baseline period.
        for(var t=1030;t<=1280;t+=50)heat.Add(Msg(t));
        heat.Advance(1320);Assert.Equal(0,heat.RebaseCount);
        heat.Advance(1320.01);Assert.Equal(1,heat.RebaseCount);
        Assert.Equal(.2*.75+(6/300d)*.25,heat.Baseline,6);
        Assert.Contains("超过600秒",heat.RebaseReason);
        Assert.Equal(1320.01,heat.LastRebaseTime);
        heat.Advance(1320.02);Assert.Equal(1,heat.RebaseCount);
    }
    [Fact]
    public void Heat_ReachingExactly100PercentResetsQuietTimerWithoutTriggeringAt100()
    {
        var heat=new DanmakuHeat();Baseline(heat);heat.Advance(600);heat.Advance(720);
        for(var i=0;i<24;i++)Assert.NotEqual(HeatSignalKind.Trigger,heat.Add(Msg(1300+i*.001)).Kind);
        Assert.Equal(100,heat.BaselinePercent,6);heat.Advance(1321);
        Assert.Equal(0,heat.RebaseCount);Assert.Equal("尚未重评",heat.RebaseReason);
    }
    [Fact]
    public void Heat_IndependentHighlightsExpireAtExactly1200Seconds_AndManualDoesNotCount()
    {
        var heat=new DanmakuHeat();Baseline(heat);heat.Advance(600);
        heat.RegisterHighlight("a","danmaku",700);
        heat.RegisterHighlight("a","danmaku",750);
        heat.RegisterHighlight("manual","manual",800);
        heat.RegisterHighlight("b","danmaku",1800);
        heat.RegisterHighlight("c","danmaku",1900);
        Assert.Equal(2,heat.RecentTriggerCount);Assert.Equal(0,heat.RebaseCount);
        var another=new DanmakuHeat();Assert.Equal(0,another.RecentTriggerCount);
        heat.Reset(true);Assert.Equal(0,heat.RecentTriggerCount);
    }
    [Fact]
    public void Heat_QuietReassessmentAlsoAppliesDuringLongReleaseDwell()
    {
        var heat=new DanmakuHeat(new(){BaselineSeconds=60,ComparisonSeconds=10,ReleaseSeconds=1800,MaxExtensionSeconds=7200});Baseline(heat,30,2);
        for(var i=0;i<10;i++)heat.Add(Msg(100+i*.01));heat.Advance(111);Assert.True(heat.Active);
        for(var i=0;i<5;i++)heat.Add(Msg(500+i*20));
        heat.Advance(711);Assert.Equal(0,heat.RebaseCount);
        heat.Advance(711.01);Assert.Equal(1,heat.RebaseCount);Assert.True(heat.Active);
        var reassessed=heat.LastRebaseTime;heat.Advance(2200);
        Assert.False(heat.Active);Assert.Equal(reassessed,heat.LastRebaseTime);
    }

    [Fact]
    public void Heat_SparseInitialBaselineCanRecoverOnlyAfterEnoughRecentSamples()
    {
        var heat=new DanmakuHeat();heat.Add(Msg(1));heat.Advance(600);heat.Advance(900);
        Assert.Equal(0,heat.RebaseCount);Assert.Contains("至少5条",heat.RebaseReason);
        for(var i=0;i<5;i++)heat.Add(Msg(1000+i*20));
        heat.Advance(1200);Assert.Equal(1,heat.RebaseCount);Assert.Equal(5/300d,heat.Baseline,6);
        Assert.Contains("首次有效基准",heat.RebaseReason);
    }

    [Fact]
    public void Heat_DuplicateEventDoesNotIncreaseSamplesOrReevaluationTriggers()
    {
        var heat=new DanmakuHeat();var message=Msg(1);
        for(var i=0;i<20;i++)heat.Add(message);
        Assert.Equal(1,heat.BaselineSamples);Assert.Equal(0,heat.RecentTriggerCount);
        Assert.Equal(1/120d,heat.MessageRate,6);
    }
    [Fact]
    public void Heat_ThirdIndependentAutoHighlightReevaluatesAndFourthCannotReuseBatch()
    {
        var heat=new DanmakuHeat();Baseline(heat);heat.Advance(600);
        for(var i=0;i<10;i++)heat.Add(Msg(700+i));
        heat.RegisterHighlight("a","danmaku",710);heat.RegisterHighlight("a","danmaku",711);
        heat.RegisterHighlight("b","danmaku",720);Assert.Equal(0,heat.RebaseCount);
        heat.RegisterHighlight("c","danmaku",730);Assert.Equal(1,heat.RebaseCount);
        Assert.Equal(0,heat.RecentTriggerCount);Assert.Contains("第3个",heat.RebaseReason);
        Assert.Contains(heat.RebaseReason,heat.Reason);
        heat.RegisterHighlight("d","danmaku",1040);Assert.Equal(1,heat.RebaseCount);
        heat.RegisterHighlight("a","danmaku",1041);Assert.Equal(1,heat.RecentTriggerCount);
    }
    [Fact]
    public void Heat_InitialGreetingSpikeIsLimitedButSustainedOrSparseTrafficIsNot()
    {
        var normal=new DanmakuHeat();var spike=new DanmakuHeat();var sustained=new DanmakuHeat();
        for(var bucket=0;bucket<20;bucket++)
        {
            for(var n=0;n<6;n++){normal.Add(Msg(bucket*30+n));spike.Add(Msg(bucket*30+n));}
            for(var n=0;n<30;n++)sustained.Add(Msg(bucket*30+n*.1));
        }
        // Separate chronological instance for the transient early burst.
        spike=new DanmakuHeat();
        for(var bucket=0;bucket<20;bucket++)for(var n=0;n<(bucket==0?100:6);n++)spike.Add(Msg(bucket*30+n*.01));
        normal.Advance(600);spike.Advance(600);sustained.Advance(600);
        Assert.Equal(normal.Baseline,spike.Baseline,6);Assert.Equal(1,sustained.Baseline,6);
        var sparse=new DanmakuHeat();for(var n=0;n<100;n++)sparse.Add(Msg(n*.01));sparse.Advance(600);
        Assert.Equal(100/600d,sparse.Baseline,6);
    }
    [Fact]
    public void Heat_RebaseSkippedWhenRecentWindowTooSparse()
    {
        var heat=new DanmakuHeat();
        Baseline(heat);
        heat.Add(Msg(900));
        heat.Add(Msg(1500));
        // 最近窗口只有 2 条（<5），不足以下结论：保留旧基准、不重锚。
        Assert.Equal(HeatSignalKind.None,heat.Advance(1810).Kind);
        Assert.Equal(0,heat.RebaseCount);
        Assert.Equal(0.2,heat.Baseline,3);
    }

    [Fact]
    public void Heat_ResetClearsCrossSessionState()
    {
        var heat=new DanmakuHeat();
        Baseline(heat);
        for(var i=0;i<60;i++)heat.Add(Msg(800+i*.1));
        Assert.True(heat.Active);
        heat.Reset(anchoredAtStreamStart:true);
        Assert.False(heat.Active);
        Assert.True(heat.InBaseline);
        Assert.False(heat.BaselineIncomplete);
        Assert.Equal(0,heat.Baseline);
        Assert.Equal(0,heat.Score);
        Assert.Equal(-1,heat.LastSignalTime);
        // 新场次重新采集基准：上一场的触发与冷却不会拦截本场触发。
        Baseline(heat);
        var kinds=new List<HeatSignalKind>();
        for(var i=0;i<60;i++)kinds.Add(heat.Add(Msg(800+i*.1)).Kind);
        Assert.Equal(1,kinds.Count(k=>k==HeatSignalKind.Trigger));
    }

    [Fact]
    public void Heat_ResetMarksMidStreamBaselineIncomplete()
    {
        var heat=new DanmakuHeat();
        heat.Reset(anchoredAtStreamStart:false);
        Assert.True(heat.BaselineIncomplete);
        Assert.Contains("中途加入",heat.Reason);
        heat.Add(Msg(60));
        Assert.Contains("中途加入",heat.Reason);
    }

    [Fact]
    public void Heat_BaselineTooSmall_NeverTriggers()
    {
        var heat=new DanmakuHeat();
        heat.Add(Msg(0));heat.Add(Msg(300));heat.Add(Msg(590));
        var triggered=false;
        for(var i=0;i<60;i++)triggered|=heat.Add(Msg(800+i*.1)).Kind!=HeatSignalKind.None;
        Assert.False(triggered);
        Assert.Contains("样本不足",heat.Reason);
    }

    [Fact]
    public void HeatOptions_ClampKeepsReleaseBelowTrigger()
    {
        var clamped=new DanmakuHeatOptions{BaselineRatio=1.4,ReleaseRatio=1.9,BaselineSeconds=10,ComparisonSeconds=5,ReleaseSeconds=9999,RebaseSeconds=10}.Clamp();
        Assert.Equal(60,clamped.BaselineSeconds);
        Assert.Equal(10,clamped.ComparisonSeconds);
        Assert.Equal(1.39,clamped.ReleaseRatio,3);
        Assert.Equal(1800,clamped.MaxExtensionSeconds);
        Assert.Equal(1800,clamped.ReleaseSeconds);
        Assert.Equal(60,clamped.RebaseSeconds);
        var defaults=new DanmakuHeatOptions().Clamp();
        Assert.Equal(200,defaults.ReleaseSeconds);
        Assert.Equal(600,defaults.RebaseSeconds);
    }
}
