using System.Text.Json.Nodes;
using LiveClipper.Core;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// 命令层（P1.7c 第一步）的行为契约。这些断言对应 agent 真正会踩的点：
/// 帧栅格、重叠、未知参数、revision 冲突。全部走纯函数执行器，不碰 HTTP、不碰磁盘。
/// </summary>
public sealed class EditorCommandTests
{
    private static EditorProject Project(params TimelineItem[] items) => new()
    {
        Start = 0,
        End = 8,
        Fps = 30,
        Timeline = items.Length == 0 ? null : items,
    };

    private static TimelineItem Clip(string id, double start, double end, int track = 1)
        => new(id, $"clip-{id}", start, end, Track: track);

    private static JsonNode Args(string json) => JsonNode.Parse(json)!;

    private static EditorCommand Cmd(string op, string argsJson = "{}") => new(op, Args(argsJson));

    [Fact]
    public void SetRange_WritesStartAndEnd()
    {
        var (project, records) = EditorCommandExecutor.ApplyBatch(Project(Clip("a", 0, 8)), [Cmd("setRange", """{"start":1,"end":7}""")]);

        Assert.Equal(1, project.Start);
        Assert.Equal(7, project.End);
        Assert.Single(records);
        Assert.Equal("setRange", records[0].Command);
        Assert.False(string.IsNullOrWhiteSpace(records[0].OpId));
    }

    [Fact]
    public void MoveItems_SnapsToFrameGrid_AndReportsAdjustment()
    {
        var project = Project(Clip("a", 0, 2));
        var (next, records) = EditorCommandExecutor.ApplyBatch(project, [Cmd("moveItems", """{"moves":[{"key":"a","start":0.51}]}""")]);

        // 30 fps → 0.51 s 落到第 15 帧 = 0.5 s，必须在 adjusted 里说清被改了多少。
        Assert.Equal(0.5, next.Timeline![0].Start, 6);
        var adjusted = records[0].Adjusted.Single();
        Assert.Equal("a", adjusted.Key);
        Assert.Equal("start", adjusted.Field);
        Assert.Equal(0.5, adjusted.Value, 6);
    }

    [Fact]
    public void MoveItems_RejectsOverlapOnSameTrack()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 4, 6));
        var error = Assert.Throws<EditorCommandException>(() =>
            EditorCommandExecutor.ApplyBatch(project, [Cmd("moveItems", """{"moves":[{"key":"b","start":1}]}""")]));

        Assert.Contains("重叠", error.Message);
    }

    [Fact]
    public void TrimItem_AcceptsDurationInsteadOfEnd()
    {
        var (next, _) = EditorCommandExecutor.ApplyBatch(Project(Clip("a", 0, 8)), [Cmd("trimItem", """{"key":"a","start":1,"duration":3}""")]);

        Assert.Equal(1, next.Timeline![0].Start, 6);
        Assert.Equal(4, next.Timeline[0].End, 6);
    }

    [Fact]
    public void SplitItem_ProducesTwoContiguousClips_AndReportsCreated()
    {
        var (next, records) = EditorCommandExecutor.ApplyBatch(Project(Clip("a", 0, 8)), [Cmd("splitItem", """{"key":"a","at":3}""")]);

        Assert.Equal(2, next.Timeline!.Length);
        Assert.Equal(3, next.Timeline[0].End, 6);
        Assert.Equal(3, next.Timeline[1].Start, 6);
        Assert.NotEqual(next.Timeline[0].Id, next.Timeline[1].Id);
        Assert.Equal(next.Timeline[1].Id, records[0].Created.Single());
    }

    [Fact]
    public void SplitItem_RejectsCutOnBoundary()
    {
        var error = Assert.Throws<EditorCommandException>(() =>
            EditorCommandExecutor.ApplyBatch(Project(Clip("a", 0, 8)), [Cmd("splitItem", """{"key":"a","at":0}""")]));

        Assert.Contains("内部", error.Message);
    }

    [Fact]
    public void RemoveItems_RefusesToEmptyTheTimeline()
    {
        var error = Assert.Throws<EditorCommandException>(() =>
            EditorCommandExecutor.ApplyBatch(Project(Clip("a", 0, 8)), [Cmd("removeItems", """{"keys":["a"]}""")]));

        Assert.Contains("至少保留", error.Message);
    }

    [Fact]
    public void UnknownArgument_IsRejectedInsteadOfIgnored()
    {
        var error = Assert.Throws<EditorCommandException>(() =>
            EditorCommandExecutor.ApplyBatch(Project(Clip("a", 0, 8)), [Cmd("setRange", """{"start":1,"end":7,"speed":2}""")]));

        // 这条是命令层的底线：传了没生效必须报错，不能静默吞掉。
        Assert.Contains("不认识的参数", error.Message);
        Assert.Contains("speed", error.Message);
    }

    [Fact]
    public void UnknownOp_IsRejected()
    {
        var error = Assert.Throws<EditorCommandException>(() =>
            EditorCommandExecutor.ApplyBatch(Project(Clip("a", 0, 8)), [Cmd("teleport")]));

        Assert.Contains("未知命令", error.Message);
    }

    [Fact]
    public void Batch_IsAllOrNothing_WhenSecondCommandFails()
    {
        var project = Project(Clip("a", 0, 8));
        Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(project, [
            Cmd("setRange", """{"start":1,"end":7}"""),
            Cmd("splitItem", """{"key":"missing","at":2}"""),
        ]));

        // 抛出即整批不落：原始工程对象没被就地改过（record with 是不可变的）。
        Assert.Equal(0, project.Start);
        Assert.Single(project.Timeline!);
    }

    [Fact]
    public void SetTrackState_WritesMuteAndOpacity()
    {
        // 不透明度必须正好 3 项（V1/V2/V3），少一项会被 Validate 拒——这条顺带把规则钉住。
        var (next, _) = EditorCommandExecutor.ApplyBatch(Project(Clip("a", 0, 8)), [Cmd("setTrackState", """{"mute":[4],"opacity":[1,0.5,1]}""")]);

        Assert.Equal([4], next.MutedTracks!);
        Assert.Equal([1, 0.5, 1], next.TrackOpacities!);
    }

    [Fact]
    public void SetTrackState_RejectsWrongOpacityLength()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            EditorCommandExecutor.ApplyBatch(Project(Clip("a", 0, 8)), [Cmd("setTrackState", """{"opacity":[1,0.5]}""")]));

        Assert.Contains("不透明度", error.Message);
    }

    [Fact]
    public void Snap_OnlyAcceptsKnownFrameRates()
    {
        Assert.Equal(1.0 / 30, EditorCommandExecutor.Snap(0.0333, 30), 6);
        // 未知 fps 一律按 30 处理，避免 0/负数把整条时间线算成 NaN。
        Assert.Equal(1.0 / 30, EditorCommandExecutor.Snap(1.0 / 30, 0), 6);
    }

    [Fact]
    public void RevisionStore_RejectsStaleBaseRevision()
    {
        const string clip = "rev-test";
        EditorRevisionStore.Forget(clip);

        Assert.True(EditorRevisionStore.TryAdvance(clip, null, out var first));
        Assert.Equal(1, first);

        Assert.False(EditorRevisionStore.TryAdvance(clip, first - 1, out var current));
        Assert.Equal(first, current);

        Assert.True(EditorRevisionStore.TryAdvance(clip, first, out var second));
        Assert.Equal(first + 1, second);
        Assert.Equal(second, EditorRevisionStore.Current(clip));
    }

    [Fact]
    public void RevisionStore_KeepsSnapshotsButNotDuplicates()
    {
        const string clip = "snap-test";
        EditorRevisionStore.Forget(clip);

        EditorRevisionStore.PushSnapshot(clip, "a");
        EditorRevisionStore.PushSnapshot(clip, "a");
        EditorRevisionStore.PushSnapshot(clip, "b");

        Assert.Equal(2, EditorRevisionStore.SnapshotDepth(clip));
    }

    [Fact]
    public void SetItemProps_WritesNumericFieldsWithoutTouchingLayout()
    {
        var (project, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","volumeDb":-6,"opacity":0.5,"zoom":1.4,"rotation":12,"positionX":0.2,"positionY":-0.3,"fadeIn":0.4,"disabled":true}]}""")]);

        var item = Assert.Single(project.Timeline!);
        Assert.Equal(-6, item.VolumeDb);
        Assert.Equal(0.5, item.Opacity);
        Assert.Equal(1.4, item.Zoom);
        Assert.Equal(12, item.Rotation);
        Assert.Equal(0.2, item.PositionX);
        Assert.Equal(-0.3, item.PositionY);
        Assert.Equal(0.4, item.FadeIn);
        Assert.True(item.Disabled);
        // 布局字段必须一动不动：改属性不许偷偷改时长
        Assert.Equal(0, item.Start);
        Assert.Equal(8, item.End);
        Assert.Equal(1, item.Track);
        Assert.Empty(records[0].Adjusted);
    }

    [Fact]
    public void SetItemProps_ClampsAndReportsAdjustedValues()
    {
        var (project, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","opacity":3,"volumeDb":99}]}""")]);

        var item = Assert.Single(project.Timeline!);
        Assert.Equal(1, item.Opacity);
        Assert.Equal(12, item.VolumeDb);
        Assert.Equal(2, records[0].Adjusted.Length);
        Assert.Contains(records[0].Adjusted, a => a.Field == "opacity" && a.Value == 1);
        Assert.Contains(records[0].Adjusted, a => a.Field == "volumeDb" && a.Value == 12);
    }

    [Fact]
    public void SetItemProps_RejectsSpeedBecauseItWouldChangeDuration()
    {
        var ex = Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","speed":2}]}""")]));

        // 报错要把允许的键列出来，让人知道该走哪条命令
        Assert.Contains("speed", ex.Message);
        Assert.Contains("volumeDb", ex.Message);
    }

    [Fact]
    public void SetItemProps_UnknownKeyIsRejected()
    {
        var ex = Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","glow":1}]}""")]));
        Assert.Contains("glow", ex.Message);
    }

    [Fact]
    public void SetItemProps_MissingKeyIsRejected()
    {
        var ex = Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"nope","opacity":0.5}]}""")]));
        Assert.Contains("nope", ex.Message);
    }
}
