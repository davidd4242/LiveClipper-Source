using System.Text.Json.Nodes;
using LiveClipper.Core;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// R51-E 新增的三条命令：setTransition / setCaptions / addItems。
/// 每条命令都覆盖：成功路径、每一条校验失败、失败时整批不落（原工程对象不被就地改）、
/// 以及 undo 语义依赖的"执行器永不原地改写"性质。
/// </summary>
public sealed class EditorCommandsExtraTests
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

    private static EditorCommandException Rejects(EditorProject project, EditorCommand command)
        => Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(project, [command]));

    // ---------------------------------------------------------------- setTransition 成功路径

    [Fact]
    public void SetTransition_AddsTransitionAtCut()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 2, 4));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setTransition", """{"key":"b","transition":{"kind":"dip-to-black","duration":0.5,"align":"start"}}""")]);

        var transition = Assert.Single(next.Transitions!);
        Assert.Equal("transition-b", transition.Id);
        Assert.Equal("b", transition.ItemId);
        Assert.Equal(1, transition.Track);
        Assert.Equal("dip-to-black", transition.Kind);
        Assert.Equal(0.5, transition.Duration, 6);
        Assert.Equal("start", transition.Align);
        Assert.Equal("b", Assert.Single(records[0].Changed));
        Assert.Equal("transition-b", Assert.Single(records[0].Created));
        Assert.Empty(records[0].Adjusted);
    }

    [Fact]
    public void SetTransition_DefaultsKindAndAlign()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 2, 4));

        var (next, _) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setTransition", """{"key":"b","transition":{"duration":0.5}}""")]);

        var transition = Assert.Single(next.Transitions!);
        Assert.Equal("cross-dissolve", transition.Kind);
        Assert.Equal("center", transition.Align);
    }

    [Fact]
    public void SetTransition_SnapsDurationAndReportsAdjustment()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 2, 4));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setTransition", """{"key":"b","transition":{"duration":0.51}}""")]);

        // 30 fps → 0.51 s 落到第 15 帧 = 0.5 s。
        Assert.Equal(0.5, next.Transitions![0].Duration, 6);
        var adjusted = Assert.Single(records[0].Adjusted);
        Assert.Equal("b", adjusted.Key);
        Assert.Equal("transition.duration", adjusted.Field);
        Assert.Equal(0.5, adjusted.Value, 6);
        Assert.Equal("snap-to-frame", adjusted.Why);
    }

    [Fact]
    public void SetTransition_KeepsRequestedDuration_AndReportsClampedEffectiveDuration()
    {
        // 转场规格 §3：duration 存用户请求值，生效值被两侧片段长度 clamp 后只在 adjusted 里回报。
        var project = Project(Clip("a", 0, 0.5), Clip("b", 0.5, 3));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setTransition", """{"key":"b","transition":{"duration":1}}""")]);

        Assert.Equal(1, next.Transitions![0].Duration, 6);
        var adjusted = Assert.Single(records[0].Adjusted);
        Assert.Equal("transition.effectiveDuration", adjusted.Field);
        Assert.Equal(0.5, adjusted.Value, 6);
        Assert.Equal("受两侧片段长度限制", adjusted.Why);
    }

    [Fact]
    public void SetTransition_EndAlignShrinksBudgetToTwoThirdsOfPreviousItem()
    {
        var project = Project(Clip("a", 0, 0.9), Clip("b", 0.9, 3));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setTransition", """{"key":"b","transition":{"duration":0.9,"align":"end"}}""")]);

        Assert.Equal(0.9, next.Transitions![0].Duration, 6);
        var adjusted = Assert.Single(records[0].Adjusted);
        Assert.Equal("transition.effectiveDuration", adjusted.Field);
        Assert.Equal(0.6, adjusted.Value, 6);
    }

    [Fact]
    public void SetTransition_ReusesExistingIdOnReplace()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 2, 4)) with
        {
            Transitions = [new EditorTransition("legacy-1", "b", 1, "cross-dissolve", 0.3, "center")],
        };

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setTransition", """{"key":"b","transition":{"kind":"dip-to-white","duration":0.6}}""")]);

        var transition = Assert.Single(next.Transitions!);
        Assert.Equal("legacy-1", transition.Id);
        Assert.Equal("dip-to-white", transition.Kind);
        Assert.Equal(0.6, transition.Duration, 6);
        Assert.Equal("b", Assert.Single(records[0].Changed));
        Assert.Empty(records[0].Created);
    }

    [Fact]
    public void SetTransition_NullClearsTransition()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 2, 4)) with
        {
            Transitions = [new EditorTransition("transition-b", "b", 1, "cross-dissolve", 0.3, "center")],
        };

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setTransition", """{"key":"b","transition":null}""")]);

        Assert.Null(next.Transitions);
        Assert.Equal("transition-b", Assert.Single(records[0].Removed));
        Assert.Equal("b", Assert.Single(records[0].Changed));
    }

    [Fact]
    public void SetTransition_NullKeepsOtherCuts()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 2, 4), Clip("c", 4, 6)) with
        {
            Transitions =
            [
                new EditorTransition("transition-b", "b", 1, "cross-dissolve", 0.3, "center"),
                new EditorTransition("transition-c", "c", 1, "dip-to-black", 0.4, "center"),
            ],
        };

        var (next, _) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setTransition", """{"key":"b","transition":null}""")]);

        var transition = Assert.Single(next.Transitions!);
        Assert.Equal("transition-c", transition.Id);
    }

    // ---------------------------------------------------------------- setTransition 校验失败

    [Fact]
    public void SetTransition_RejectsMissingTransitionKey()
    {
        // 键缺失不是"清除"，而是"没说"；静默空操作会让调用方以为写进去了。
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)), Cmd("setTransition", """{"key":"b"}"""));

        Assert.Contains("setTransition 需要 transition", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsClearWhenNothingToClear()
    {
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"b","transition":null}"""));

        Assert.Contains("没有转场可清除", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsNonObjectTransition()
    {
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"b","transition":0.5}"""));

        Assert.Contains("transition 必须是对象或 null", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsUnknownKind()
    {
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"b","transition":{"kind":"wipe","duration":0.5}}"""));

        Assert.Contains("转场类型 wipe 不支持", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsUnknownAlign()
    {
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"b","transition":{"duration":0.5,"align":"middle"}}"""));

        Assert.Contains("转场对齐 middle 不支持", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsMissingDuration()
    {
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"b","transition":{"kind":"cross-dissolve"}}"""));

        Assert.Contains("命令缺少 duration", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsNonPositiveDuration()
    {
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"b","transition":{"duration":0}}"""));

        Assert.Contains("转场时长必须为正", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsFirstItemOnTrack()
    {
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"a","transition":{"duration":0.5}}"""));

        Assert.Contains("是该轨道第一个片段", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsGapBeforeCut()
    {
        var project = Project(Clip("a", 0, 2), new TimelineItem("b", "clip-b", 2.5, 4, GapBefore: 0.5, Track: 1));

        var error = Rejects(project, Cmd("setTransition", """{"key":"b","transition":{"duration":0.5}}"""));

        Assert.Contains("有空隙", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsDisabledNeighbour()
    {
        var project = Project(new TimelineItem("a", "clip-a", 0, 2, Disabled: true, Track: 1), Clip("b", 2, 4));

        var error = Rejects(project, Cmd("setTransition", """{"key":"b","transition":{"duration":0.5}}"""));

        Assert.Contains("已停用", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsDurationBelowOneFrame()
    {
        // 30 fps → 0.01 s 吸附后是 0 帧，存进去会被渲染层静默丢弃。
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"b","transition":{"duration":0.01}}"""));

        Assert.Contains("至少要 1 帧", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsWhenNeighboursTooShortForOneFrame()
    {
        var project = Project(Clip("a", 0, 0.02), Clip("b", 0.02, 0.04));

        var error = Rejects(project, Cmd("setTransition", """{"key":"b","transition":{"duration":0.5}}"""));

        Assert.Contains("放不下 1 帧转场", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsMissingItem()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("setTransition", """{"key":"zzz","transition":{"duration":0.5}}"""));

        Assert.Contains("找不到片段 zzz", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsUnknownArgument()
    {
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"b","track":2,"transition":{"duration":0.5}}"""));

        Assert.Contains("不认识的参数 track", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsUnknownTransitionField()
    {
        // track 由片段自身推导：接受调用方传入只会制造"存了却被渲染层静默丢弃"的转场。
        var error = Rejects(Project(Clip("a", 0, 2), Clip("b", 2, 4)),
            Cmd("setTransition", """{"key":"b","transition":{"duration":0.5,"track":3}}"""));

        Assert.Contains("不认识的参数 track", error.Message);
    }

    // ---------------------------------------------------------------- setCaptions 成功路径

    [Fact]
    public void SetCaptions_WritesOverrideForSource()
    {
        var project = Project(Clip("a", 0, 4));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":0,"end":1,"text":"第一句"},{"start":1,"end":2,"text":"第二句"}]}""")]);

        var segments = next.CaptionOverrides!["clip-a"];
        Assert.Equal(2, segments.Length);
        Assert.Equal("第一句", segments[0].Text);
        Assert.Equal(0, segments[0].Start, 6);
        Assert.Equal(1, segments[0].End, 6);
        Assert.Equal("第二句", segments[1].Text);
        Assert.Equal("clip-a", Assert.Single(records[0].Changed));
        Assert.Empty(records[0].Adjusted);
    }

    [Fact]
    public void SetCaptions_EmptyArrayClearsExplicitly()
    {
        // 模型注释的 own-key 语义：空数组=显式清空，和"键不存在"不是一回事。
        var project = Project(Clip("a", 0, 4));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[]}""")]);

        Assert.True(next.CaptionOverrides!.ContainsKey("clip-a"));
        Assert.Empty(next.CaptionOverrides["clip-a"]);
        Assert.Equal("clip-a", Assert.Single(records[0].Changed));
    }

    [Fact]
    public void SetCaptions_NullRemovesOverride()
    {
        var project = Project(Clip("a", 0, 4)) with
        {
            CaptionOverrides = new Dictionary<string, TranscriptSegment[]>(StringComparer.Ordinal)
            {
                ["clip-a"] = [new TranscriptSegment(0, 1, "旧字幕")],
            },
        };

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":null}""")]);

        Assert.Null(next.CaptionOverrides);
        Assert.Equal("clip-a", Assert.Single(records[0].Removed));
        Assert.Empty(records[0].Changed);
    }

    [Fact]
    public void SetCaptions_NullKeepsOtherSources()
    {
        var project = Project(Clip("a", 0, 4)) with
        {
            CaptionOverrides = new Dictionary<string, TranscriptSegment[]>(StringComparer.Ordinal)
            {
                ["clip-a"] = [new TranscriptSegment(0, 1, "甲")],
                ["clip-other"] = [new TranscriptSegment(0, 1, "乙")],
            },
        };

        var (next, _) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":null}""")]);

        Assert.Single(next.CaptionOverrides!);
        Assert.True(next.CaptionOverrides!.ContainsKey("clip-other"));
    }

    [Fact]
    public void SetCaptions_SnapsSegmentTimesAndReportsAdjustments()
    {
        var project = Project(Clip("a", 0, 4));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":0.51,"end":1.49,"text":"吸附"}]}""")]);

        var segment = Assert.Single(next.CaptionOverrides!["clip-a"]);
        Assert.Equal(0.5, segment.Start, 6);
        Assert.Equal(1.5, segment.End, 6);
        Assert.Equal(2, records[0].Adjusted.Length);
        Assert.Equal("clip-a", records[0].Adjusted[0].Key);
        Assert.Equal("segments[0].start", records[0].Adjusted[0].Field);
        Assert.Equal("segments[0].end", records[0].Adjusted[1].Field);
        Assert.Equal("snap-to-frame", records[0].Adjusted[1].Why);
    }

    [Fact]
    public void SetCaptions_KeepsGivenOrderWithoutDedup()
    {
        // 渲染层的 NormalizeCaptions 不重排也不去重，命令层不自行"帮忙"改变顺序。
        var project = Project(Clip("a", 0, 4));

        var (next, _) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":2,"end":3,"text":"后"},{"start":0,"end":2.5,"text":"前"}]}""")]);

        var segments = next.CaptionOverrides!["clip-a"];
        Assert.Equal("后", segments[0].Text);
        Assert.Equal("前", segments[1].Text);
    }

    // ---------------------------------------------------------------- setCaptions 校验失败

    [Fact]
    public void SetCaptions_RejectsMissingSegmentsKey()
    {
        var error = Rejects(Project(Clip("a", 0, 4)), Cmd("setCaptions", """{"sourceClipId":"clip-a"}"""));

        Assert.Contains("setCaptions 需要 segments", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsSourceNotOnTimeline()
    {
        // 时间线没引用这个源，写进去的覆盖永远读不到。
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"clip-ghost","segments":[]}"""));

        Assert.Contains("时间线上没有引用源 clip-ghost 的片段", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsRemovingMissingOverride()
    {
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":null}"""));

        Assert.Contains("没有字幕覆盖可移除", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsBlankSourceClipId()
    {
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"   ","segments":[]}"""));

        Assert.Contains("命令缺少 sourceClipId", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsNonArraySegments()
    {
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":5}"""));

        Assert.Contains("命令缺少 segments 数组", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsBlankText()
    {
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":0,"end":1,"text":"  "}]}"""));

        Assert.Contains("字幕第 1 条的 text 不能为空", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsOverlongText()
    {
        var longText = new string('a', 501);
        var payload = $$"""{"sourceClipId":"clip-a","segments":[{"start":0,"end":1,"text":"{{longText}}"}]}""";

        var error = Rejects(Project(Clip("a", 0, 4)), Cmd("setCaptions", payload));

        Assert.Contains("不能超过 500 字", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsNegativeStart()
    {
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":-1,"end":1,"text":"负"}]}"""));

        Assert.Contains("字幕第 1 条的 start 不能为负", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsEndNotAfterStart()
    {
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":0,"end":1,"text":"好"},{"start":2,"end":2,"text":"坏"}]}"""));

        Assert.Contains("字幕第 2 条需要 end > start", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsZeroLengthAfterSnap()
    {
        // 0.331 与 0.339 在 30 fps 下都落到第 10 帧，吸附后这条字幕没有时长。
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":0.331,"end":0.339,"text":"太短"}]}"""));

        Assert.Contains("吸附到帧后时长为 0", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsTooManySegments()
    {
        var one = """{"start":0,"end":1,"text":"x"}""";
        var payload = """{"sourceClipId":"clip-a","segments":["""
            + string.Join(",", Enumerable.Repeat(one, 2001))
            + "]}";

        var error = Rejects(Project(Clip("a", 0, 4)), Cmd("setCaptions", payload));

        Assert.Contains("字幕条数不能超过 2000", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsUnknownSegmentField()
    {
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":0,"end":1,"text":"甲","speaker":"主播"}]}"""));

        Assert.Contains("不认识的参数 speaker", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsUnknownArgument()
    {
        var error = Rejects(Project(Clip("a", 0, 4)),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[],"clipId":"a"}"""));

        Assert.Contains("不认识的参数 clipId", error.Message);
    }

    // ---------------------------------------------------------------- addItems 成功路径

    [Fact]
    public void AddItems_AppendsMintedItem()
    {
        var project = Project(Clip("a", 0, 2));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"duration":1.5}]}""")]);

        Assert.Equal(2, next.Timeline!.Length);
        var added = next.Timeline[1];
        Assert.Equal("src-1", added.ClipId);
        Assert.Equal(2, added.Start, 6);
        Assert.Equal(3.5, added.End, 6);
        Assert.Equal("video", added.Kind);
        Assert.Equal(1, added.Track);
        Assert.Equal(0, added.GapBefore, 6);
        Assert.Equal(added.Id, Assert.Single(records[0].Created));
        Assert.Empty(records[0].Changed);
        Assert.Empty(records[0].Adjusted);
    }

    [Fact]
    public void AddItems_MintsIdServerSide()
    {
        // Validate 要求 id 唯一：让调用方传 id 只是把冲突推到校验层，所以这里一律自己 mint。
        var project = Project(Clip("a", 0, 2));

        var (next, _) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"end":3}]}""")]);

        Assert.Equal(32, next.Timeline![1].Id.Length);
        Assert.NotEqual("src-1", next.Timeline[1].Id);
    }

    [Fact]
    public void AddItems_MintsDistinctIdsForEachItem()
    {
        var project = Project(Clip("a", 0, 2));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"end":3},{"clipId":"src-2","start":3,"end":4}]}""")]);

        Assert.Equal(3, next.Timeline!.Length);
        Assert.NotEqual(next.Timeline[1].Id, next.Timeline[2].Id);
        Assert.Equal(2, records[0].Created.Length);
    }

    [Fact]
    public void AddItems_UsesTopLevelTrackAsDefault_AndPerItemOverride()
    {
        var project = Project(Clip("a", 0, 2));

        var (next, _) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("addItems", """{"track":4,"items":[{"clipId":"src-1","start":0,"end":1},{"clipId":"src-2","start":0,"end":1,"track":5}]}""")]);

        Assert.Equal(4, next.Timeline![1].Track);
        Assert.Equal(5, next.Timeline[2].Track);
    }

    [Fact]
    public void AddItems_AcceptsEndInsteadOfDuration()
    {
        var project = Project(Clip("a", 0, 2));

        var (next, _) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"end":5}]}""")]);

        Assert.Equal(5, next.Timeline![1].End, 6);
    }

    [Fact]
    public void AddItems_AcceptsHookKind()
    {
        var project = Project(Clip("a", 0, 2));

        var (next, _) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"end":3,"kind":"hook"}]}""")]);

        Assert.Equal("hook", next.Timeline![1].Kind);
    }

    [Fact]
    public void AddItems_SnapsTimesAndReportsAdjustments()
    {
        var project = Project(Clip("a", 0, 2));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("addItems", """{"items":[{"clipId":"src-1","start":2.51,"end":4.49}]}""")]);

        var added = next.Timeline![1];
        Assert.Equal(2.5, added.Start, 6);
        Assert.Equal(4.5, added.End, 6);
        Assert.Equal(2, records[0].Adjusted.Length);
        Assert.Equal(added.Id, records[0].Adjusted[0].Key);
        Assert.Equal("start", records[0].Adjusted[0].Field);
        Assert.Equal("end", records[0].Adjusted[1].Field);
        Assert.Equal("snap-to-frame", records[0].Adjusted[0].Why);
    }

    [Fact]
    public void AddItems_SnapsGapBefore()
    {
        var project = Project(Clip("a", 0, 2));

        var (next, records) = EditorCommandExecutor.ApplyBatch(project,
            [Cmd("addItems", """{"items":[{"clipId":"src-1","start":3,"end":4,"gapBefore":0.51}]}""")]);

        Assert.Equal(0.5, next.Timeline![1].GapBefore, 6);
        var adjusted = Assert.Single(records[0].Adjusted);
        Assert.Equal("gapBefore", adjusted.Field);
        Assert.Equal(0.5, adjusted.Value, 6);
    }

    // ---------------------------------------------------------------- addItems 校验失败

    [Fact]
    public void AddItems_RejectsEmptyList()
    {
        var error = Rejects(Project(Clip("a", 0, 2)), Cmd("addItems", """{"items":[]}"""));

        Assert.Contains("addItems 需要至少 1 个片段", error.Message);
    }

    [Fact]
    public void AddItems_RejectsMissingItemsArray()
    {
        var error = Rejects(Project(Clip("a", 0, 2)), Cmd("addItems", """{"track":2}"""));

        Assert.Contains("命令缺少 items 数组", error.Message);
    }

    [Fact]
    public void AddItems_RejectsMissingClipId()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"start":2,"end":3}]}"""));

        Assert.Contains("命令缺少 clipId", error.Message);
    }

    [Fact]
    public void AddItems_RejectsBlankClipId()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"  ","start":2,"end":3}]}"""));

        Assert.Contains("第 1 个新片段缺少 clipId", error.Message);
    }

    [Fact]
    public void AddItems_RejectsTopLevelTrackOutOfRange()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"track":7,"items":[{"clipId":"src-1","start":2,"end":3}]}"""));

        Assert.Contains("addItems 的 track 必须在 1–6", error.Message);
    }

    [Fact]
    public void AddItems_RejectsPerItemTrackOutOfRange()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"end":3,"track":0}]}"""));

        Assert.Contains("第 1 个新片段的轨道必须在 1–6", error.Message);
    }

    [Fact]
    public void AddItems_RejectsUnknownKind()
    {
        // 让 Validate 去抛的话，调用方只会看到含糊的"精剪效果参数无效"。
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"end":3,"kind":"title"}]}"""));

        Assert.Contains("第 1 个新片段的 kind title 不支持", error.Message);
    }

    [Fact]
    public void AddItems_RejectsNegativeStart()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":-1,"end":3}]}"""));

        Assert.Contains("第 1 个新片段的起点不能为负", error.Message);
    }

    [Fact]
    public void AddItems_RejectsMissingEndAndDuration()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":2}]}"""));

        Assert.Contains("第 1 个新片段需要 end 或 duration", error.Message);
    }

    [Fact]
    public void AddItems_RejectsNonPositiveDuration()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"duration":0}]}"""));

        Assert.Contains("第 1 个新片段的时长必须为正", error.Message);
    }

    [Fact]
    public void AddItems_RejectsGapBeforeOutOfRange()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":3,"end":4,"gapBefore":21601}]}"""));

        Assert.Contains("第 1 个新片段的 gapBefore 必须在 0–21600", error.Message);
    }

    [Fact]
    public void AddItems_RejectsZeroLengthAfterSnap()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":2.331,"end":2.339}]}"""));

        Assert.Contains("第 1 个新片段吸附到帧后时长为 0", error.Message);
    }

    [Fact]
    public void AddItems_RejectsOverlapOnSameTrack()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":1,"end":3}]}"""));

        Assert.Contains("重叠", error.Message);
    }

    [Fact]
    public void AddItems_RejectsMoreThanFiftyPerCall()
    {
        var one = """{"clipId":"src","start":0,"end":1}""";
        var payload = """{"items":[""" + string.Join(",", Enumerable.Repeat(one, 51)) + "]}";

        var error = Rejects(Project(Clip("a", 0, 2)), Cmd("addItems", payload));

        Assert.Contains("addItems 单次不能超过 50 个片段", error.Message);
    }

    [Fact]
    public void AddItems_RejectsExceedingTimelineCap()
    {
        // Validate 的上限是 200 个片段；预检让调用方看到"当前多少、新增多少"而不是事后的结构错误。
        var existing = Enumerable.Range(0, 200).Select(i => Clip($"x{i}", i, i + 0.5)).ToArray();
        var project = Project(existing) with { End = 420 };

        var error = Rejects(project, Cmd("addItems", """{"items":[{"clipId":"src-1","start":300,"end":301}]}"""));

        Assert.Contains("时间线片段总数不能超过 200", error.Message);
        Assert.Contains("当前 200，本次新增 1", error.Message);
    }

    [Fact]
    public void AddItems_RejectsUnknownItemField()
    {
        // speed 会改变序列长度，和 setItemProps 拒 speed 是同一条分工。
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"end":3,"speed":2}]}"""));

        Assert.Contains("不认识的参数 speed", error.Message);
    }

    [Fact]
    public void AddItems_RejectsUnknownArgument()
    {
        var error = Rejects(Project(Clip("a", 0, 2)),
            Cmd("addItems", """{"mode":"append","items":[{"clipId":"src-1","start":2,"end":3}]}"""));

        Assert.Contains("不认识的参数 mode", error.Message);
    }

    // ---------------------------------------------------------------- 幂等 / 整批不落 / undo

    [Fact]
    public void FailedSetTransition_LeavesProjectUntouched()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 2, 4));

        Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setTransition", """{"key":"b","transition":{"kind":"wipe","duration":0.5}}""")]));

        Assert.Null(project.Transitions);
        Assert.Equal(2, project.Timeline!.Length);
    }

    [Fact]
    public void FailedSetCaptions_LeavesExistingOverridesUntouched()
    {
        var project = Project(Clip("a", 0, 4)) with
        {
            CaptionOverrides = new Dictionary<string, TranscriptSegment[]>(StringComparer.Ordinal)
            {
                ["clip-a"] = [new TranscriptSegment(0, 1, "原字幕")],
            },
        };

        Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(project,
            [Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":0,"end":1,"text":"  "}]}""")]));

        var segment = Assert.Single(project.CaptionOverrides!["clip-a"]);
        Assert.Equal("原字幕", segment.Text);
    }

    [Fact]
    public void FailedAddItems_AddsNothing()
    {
        var project = Project(Clip("a", 0, 2));

        Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(project,
            [Cmd("addItems", """{"items":[{"clipId":"src-1","start":2,"end":3},{"clipId":"src-2","start":2.5,"end":4}]}""")]));

        Assert.Single(project.Timeline!);
    }

    [Fact]
    public void Batch_IsAllOrNothing_AcrossNewCommands()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 2, 4));

        Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(project,
        [
            Cmd("setTransition", """{"key":"b","transition":{"duration":0.5}}"""),
            Cmd("setCaptions", """{"sourceClipId":"clip-ghost","segments":[]}"""),
        ]));

        Assert.Null(project.Transitions);
        Assert.Null(project.CaptionOverrides);
        Assert.Equal(2, project.Timeline!.Length);
    }

    [Fact]
    public void PreviousProjectStaysValidSnapshot_SoUndoRestores()
    {
        // 执行器从不就地改写：撤销栈压的就是调用前这个对象，撤销等于把它放回去。
        var before = Project(Clip("a", 0, 2), Clip("b", 2, 4));

        var (after, _) = EditorCommandExecutor.ApplyBatch(before,
        [
            Cmd("setTransition", """{"key":"b","transition":{"duration":0.5}}"""),
            Cmd("addItems", """{"items":[{"clipId":"src-1","start":4,"end":5}]}"""),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":0,"end":1,"text":"新"}]}"""),
        ]);

        Assert.Single(after.Transitions!);
        Assert.Equal(3, after.Timeline!.Length);
        Assert.Single(after.CaptionOverrides!);

        Assert.Null(before.Transitions);
        Assert.Null(before.CaptionOverrides);
        Assert.Equal(2, before.Timeline!.Length);
        Assert.NotSame(before, after);

        // 快照本身仍是一个合法工程：撤销后不需要再修补就能继续用。
        EditorRenderService.Validate(before);
    }

    [Fact]
    public void SetTransition_IsDeterministic()
    {
        var project = Project(Clip("a", 0, 2), Clip("b", 2, 4));
        var command = Cmd("setTransition", """{"key":"b","transition":{"kind":"dip-to-black","duration":0.4,"align":"end"}}""");

        var (first, _) = EditorCommandExecutor.ApplyBatch(project, [command]);
        var (second, _) = EditorCommandExecutor.ApplyBatch(project, [command]);

        Assert.Equal(first.Transitions![0].Id, second.Transitions![0].Id);
        Assert.Equal(first.Transitions[0].Duration, second.Transitions[0].Duration, 6);
        Assert.Equal(first.Transitions[0].Kind, second.Transitions[0].Kind);
        Assert.Equal(first.Transitions[0].Align, second.Transitions[0].Align);
    }

    [Fact]
    public void SetCaptions_IsDeterministic()
    {
        var project = Project(Clip("a", 0, 4));
        var command = Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":0.51,"end":1.49,"text":"确定性"}]}""");

        var (first, firstRecords) = EditorCommandExecutor.ApplyBatch(project, [command]);
        var (second, secondRecords) = EditorCommandExecutor.ApplyBatch(project, [command]);

        Assert.Equal(first.CaptionOverrides!["clip-a"][0].Start, second.CaptionOverrides!["clip-a"][0].Start, 6);
        Assert.Equal(first.CaptionOverrides["clip-a"][0].End, second.CaptionOverrides["clip-a"][0].End, 6);
        Assert.Equal(firstRecords[0].Adjusted.Length, secondRecords[0].Adjusted.Length);
    }
}
