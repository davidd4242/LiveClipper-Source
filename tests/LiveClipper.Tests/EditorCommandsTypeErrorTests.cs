using System.Text.Json.Nodes;
using LiveClipper.Core;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// R51-Q：两类既有缺陷的回归测试。
///
/// 一、参数类型错必须是 EditorCommandException（端点转 400），不能是 InvalidOperationException /
///    FormatException / InvalidCastException（端点不认，漏成 500）。Assert.Throws&lt;T&gt; 是精确类型匹配，
///    所以每个 Rejects() 本身就在证明"没漏成框架异常"。
///
/// 二、setItemProps 的夹取区间必须与 EditorRenderService.Validate 一致。
///    旧区间（zoom [0.1,4]、rotation [±360]、positionX/Y [±1]）比 Validate（zoom [1,2]、
///    rotation [±180]、positionX/Y [±0.5]）宽，后果是命令回执说"已限制"、紧接着 Validate 又抛
///    ArgumentException：调用方拿到的 adjusted 是假的。下面每个夹取用例在旧代码上都会因 Validate 抛错而失败。
/// </summary>
public sealed class EditorCommandsTypeErrorTests
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

    // ---------------------------------------------------------------- 类型错：字符串位上收到别的

    [Fact]
    public void SetItemProps_RejectsNumericKeyInsteadOfThrowingFrameworkException()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setItemProps", """{"items":[{"key":5}]}"""));

        Assert.Contains("参数 key 必须是字符串", error.Message);
        Assert.Contains("数字", error.Message);
    }

    [Fact]
    public void SetItemProps_RejectsObjectWhereStringExpected()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setItemProps", """{"items":[{"key":{"a":1}}]}"""));

        Assert.Contains("参数 key 必须是字符串", error.Message);
        Assert.Contains("对象", error.Message);
    }

    [Fact]
    public void SetRenderSettings_RejectsNumericFormat()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setRenderSettings", """{"format":5}"""));

        Assert.Contains("参数 format 必须是字符串", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsNumericSourceClipId()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setCaptions", """{"sourceClipId":5,"segments":[]}"""));

        Assert.Contains("参数 sourceClipId 必须是字符串", error.Message);
    }

    [Fact]
    public void MoveItems_RejectsNumericKey()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("moveItems", """{"moves":[{"key":5,"start":1}]}"""));

        Assert.Contains("参数 key 必须是字符串", error.Message);
    }

    [Fact]
    public void AddItems_RejectsNumericClipId()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("addItems", """{"items":[{"clipId":5,"start":0,"end":2}]}"""));

        Assert.Contains("参数 clipId 必须是字符串", error.Message);
    }

    [Fact]
    public void AddItems_RejectsNumericKind()
    {
        var error = Rejects(
            Project(Clip("a", 0, 8)),
            Cmd("addItems", """{"items":[{"clipId":"c1","start":0,"end":2,"kind":7}]}"""));

        Assert.Contains("参数 kind 必须是字符串", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsNumericKind()
    {
        var error = Rejects(
            Project(Clip("a", 0, 4), Clip("b", 4, 8)),
            Cmd("setTransition", """{"key":"b","transition":{"kind":5,"duration":0.4}}"""));

        Assert.Contains("参数 kind 必须是字符串", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsNumericAlign()
    {
        var error = Rejects(
            Project(Clip("a", 0, 4), Clip("b", 4, 8)),
            Cmd("setTransition", """{"key":"b","transition":{"kind":"cross-dissolve","align":9,"duration":0.4}}"""));

        Assert.Contains("参数 align 必须是字符串", error.Message);
    }

    // ---------------------------------------------------------------- 类型错：数字位上收到别的

    [Fact]
    public void SetRange_RejectsStringStart()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setRange", """{"start":"0","end":8}"""));

        Assert.Contains("参数 start 必须是数字", error.Message);
        Assert.Contains("字符串", error.Message);
    }

    [Fact]
    public void SetItemProps_RejectsStringZoom()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setItemProps", """{"items":[{"key":"a","zoom":"1.5"}]}"""));

        Assert.Contains("参数 zoom 必须是数字", error.Message);
    }

    [Fact]
    public void SetItemProps_RejectsBooleanOpacity()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setItemProps", """{"items":[{"key":"a","opacity":true}]}"""));

        Assert.Contains("参数 opacity 必须是数字", error.Message);
        Assert.Contains("布尔值", error.Message);
    }

    [Fact]
    public void SetTransition_RejectsStringDuration()
    {
        var error = Rejects(
            Project(Clip("a", 0, 4), Clip("b", 4, 8)),
            Cmd("setTransition", """{"key":"b","transition":{"kind":"cross-dissolve","duration":"0.4"}}"""));

        Assert.Contains("参数 duration 必须是数字", error.Message);
    }

    [Fact]
    public void SetCaptions_RejectsStringSegmentStart()
    {
        var error = Rejects(
            Project(Clip("a", 0, 8)),
            Cmd("setCaptions", """{"sourceClipId":"clip-a","segments":[{"start":"0","end":1,"text":"hi"}]}"""));

        Assert.Contains("参数 start 必须是数字", error.Message);
    }

    [Fact]
    public void SetTrackState_RejectsStringInsideMuteArray()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setTrackState", """{"mute":["4"]}"""));

        Assert.Contains("数组元素必须是数字", error.Message);
    }

    [Fact]
    public void SetTrackState_RejectsStringInsideOpacityArray()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setTrackState", """{"opacity":["1"]}"""));

        Assert.Contains("数组元素必须是数字", error.Message);
    }

    // ---------------------------------------------------------------- 类型错：布尔位 / 数组元素 / 反序列化

    [Fact]
    public void SetItemProps_RejectsStringDisabled()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setItemProps", """{"items":[{"key":"a","disabled":"true"}]}"""));

        Assert.Contains("参数 disabled 必须是 true/false", error.Message);
        Assert.Contains("字符串", error.Message);
    }

    [Fact]
    public void RemoveItems_RejectsNumericKeyElement()
    {
        var error = Rejects(
            Project(Clip("a", 0, 4), Clip("b", 4, 8)),
            Cmd("removeItems", """{"keys":[5]}"""));

        Assert.Contains("removeItems 的 keys 必须是字符串数组", error.Message);
    }

    [Fact]
    public void ReplaceProject_RejectsNonObjectProject()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("replaceProject", """{"project":5}"""));

        // 结构不符的 JSON 会让 Deserialize 抛 JsonException；那也必须变成被拒，而不是 500。
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    // ---------------------------------------------------------------- 类型错不改原工程

    [Fact]
    public void TypeError_LeavesOriginalProjectUntouched()
    {
        var project = Project(Clip("a", 0, 8));

        Rejects(project, Cmd("setItemProps", """{"items":[{"key":"a","zoom":"1.5"}]}"""));

        Assert.Equal(1d, project.Timeline![0].Zoom);
        Assert.Equal(0d, project.Start);
        Assert.Equal(8d, project.End);
    }

    [Fact]
    public void TypeErrorInSecondCommand_RejectsWholeBatch()
    {
        var project = Project(Clip("a", 0, 8));

        Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(project, [
            Cmd("setRange", """{"start":0,"end":6}"""),
            Cmd("setItemProps", """{"items":[{"key":"a","zoom":"1.5"}]}"""),
        ]));

        Assert.Equal(8d, project.End);
    }

    // ---------------------------------------------------------------- 夹取区间与 Validate 对齐

    [Fact]
    public void SetItemProps_ClampsZoomDownToValidateMax()
    {
        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","zoom":3}]}""")]);

        Assert.Equal(2d, next.Timeline![0].Zoom);
        Assert.Equal(1, records[0].Adjusted.Length);
        var adjusted = records[0].Adjusted[0];
        Assert.Equal("a", adjusted.Key);
        Assert.Equal("zoom", adjusted.Field);
        Assert.Equal(2d, adjusted.Value);
        Assert.Contains("已限制在", adjusted.Why);
    }

    [Fact]
    public void SetItemProps_ClampsZoomUpToValidateMin()
    {
        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","zoom":0.5}]}""")]);

        Assert.Equal(1d, next.Timeline![0].Zoom);
        Assert.Equal(1, records[0].Adjusted.Length);
        Assert.Equal("zoom", records[0].Adjusted[0].Field);
        Assert.Equal(1d, records[0].Adjusted[0].Value);
    }

    [Fact]
    public void SetItemProps_ClampsRotationToValidateRange()
    {
        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","rotation":270}]}""")]);

        Assert.Equal(180d, next.Timeline![0].Rotation);
        Assert.Equal(1, records[0].Adjusted.Length);
        Assert.Equal("rotation", records[0].Adjusted[0].Field);
        Assert.Equal(180d, records[0].Adjusted[0].Value);
    }

    [Fact]
    public void SetItemProps_ClampsNegativeRotationToValidateRange()
    {
        var (next, _) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","rotation":-270}]}""")]);

        Assert.Equal(-180d, next.Timeline![0].Rotation);
    }

    [Fact]
    public void SetItemProps_ClampsPositionToValidateRange()
    {
        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","positionX":0.9,"positionY":-0.9}]}""")]);

        Assert.Equal(0.5d, next.Timeline![0].PositionX);
        Assert.Equal(-0.5d, next.Timeline![0].PositionY);
        Assert.Equal(2, records[0].Adjusted.Length);
        Assert.Contains(records[0].Adjusted, x => x.Field == "positionX" && x.Value == 0.5d);
        Assert.Contains(records[0].Adjusted, x => x.Field == "positionY" && x.Value == -0.5d);
    }

    [Fact]
    public void SetItemProps_KeepsValuesInsideValidateRangeUnadjusted()
    {
        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","zoom":1.5,"rotation":90,"positionX":0.3,"positionY":-0.4}]}""")]);

        Assert.Equal(1.5d, next.Timeline![0].Zoom);
        Assert.Equal(90d, next.Timeline![0].Rotation);
        Assert.Equal(0.3d, next.Timeline![0].PositionX);
        Assert.Equal(-0.4d, next.Timeline![0].PositionY);
        Assert.Empty(records[0].Adjusted);
    }

    [Fact]
    public void SetItemProps_ClampedProjectSurvivesValidate()
    {
        // 这是缺陷 2 的核心：旧区间下 zoom 4 / positionX 1 会"回执说已限制"但 Validate 随后抛错，
        // ApplyBatch 根本返回不了。能走到这里并拿到回执，就说明夹取后的工程是真的合法。
        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 8)),
            [Cmd("setItemProps", """{"items":[{"key":"a","zoom":4,"rotation":360,"positionX":1,"positionY":-1}]}""")]);

        Assert.Equal(4, records[0].Adjusted.Length);
        EditorRenderService.Validate(next);
    }

    [Fact]
    public void SetTrackState_RejectsNumericMuteInsteadOfIgnoringIt()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setTrackState", """{"mute":5}"""));

        Assert.Equal("参数 mute 必须是数组，收到数字", error.Message);
    }

    [Fact]
    public void SetTrackState_RejectsStringSoloInsteadOfIgnoringIt()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setTrackState", """{"solo":"4"}"""));

        Assert.Equal("参数 solo 必须是数组，收到字符串", error.Message);
    }

    [Fact]
    public void SetTrackState_RejectsObjectHideInsteadOfIgnoringIt()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setTrackState", """{"hide":{}}"""));

        Assert.Equal("参数 hide 必须是数组，收到对象", error.Message);
    }

    [Fact]
    public void SetTrackState_RejectsBooleanLockInsteadOfIgnoringIt()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setTrackState", """{"lock":false}"""));

        Assert.Equal("参数 lock 必须是数组，收到布尔值", error.Message);
    }

    [Fact]
    public void SetTrackState_RejectsNumericOpacityInsteadOfIgnoringIt()
    {
        var error = Rejects(Project(Clip("a", 0, 8)), Cmd("setTrackState", """{"opacity":0.5}"""));

        Assert.Equal("参数 opacity 必须是数组，收到数字", error.Message);
    }

    [Fact]
    public void SetTrackState_OmittedArraysKeepExistingState()
    {
        var project = Project(Clip("a", 0, 8)) with
        {
            MutedTracks = [4],
            SoloedTracks = [5],
            HiddenTracks = [1],
            LockedTracks = [6],
            TrackOpacities = [1d, 0.5d, 1d],
        };

        var (next, _) = EditorCommandExecutor.ApplyBatch(project, [Cmd("setTrackState")]);

        Assert.Same(project.MutedTracks, next.MutedTracks);
        Assert.Same(project.SoloedTracks, next.SoloedTracks);
        Assert.Same(project.HiddenTracks, next.HiddenTracks);
        Assert.Same(project.LockedTracks, next.LockedTracks);
        Assert.Same(project.TrackOpacities, next.TrackOpacities);
    }

    [Fact]
    public void SetTrackState_NullArraysKeepExistingState()
    {
        var project = Project(Clip("a", 0, 8)) with
        {
            MutedTracks = [4],
            SoloedTracks = [5],
            HiddenTracks = [1],
            LockedTracks = [6],
            TrackOpacities = [1d, 0.5d, 1d],
        };

        var (next, _) = EditorCommandExecutor.ApplyBatch(
            project,
            [Cmd("setTrackState", """{"mute":null,"solo":null,"hide":null,"lock":null,"opacity":null}""")]);

        Assert.Same(project.MutedTracks, next.MutedTracks);
        Assert.Same(project.SoloedTracks, next.SoloedTracks);
        Assert.Same(project.HiddenTracks, next.HiddenTracks);
        Assert.Same(project.LockedTracks, next.LockedTracks);
        Assert.Same(project.TrackOpacities, next.TrackOpacities);
    }

    [Fact]
    public void SetTrackState_EmptyArraysStillClearExistingState()
    {
        var project = Project(Clip("a", 0, 8)) with
        {
            MutedTracks = [4],
            SoloedTracks = [5],
            HiddenTracks = [1],
            LockedTracks = [6],
            TrackOpacities = [1d, 0.5d, 1d],
        };

        var (next, _) = EditorCommandExecutor.ApplyBatch(
            project,
            [Cmd("setTrackState", """{"mute":[],"solo":[],"hide":[],"lock":[],"opacity":[]}""")]);

        Assert.NotNull(next.MutedTracks);
        Assert.Empty(next.MutedTracks);
        Assert.NotNull(next.SoloedTracks);
        Assert.Empty(next.SoloedTracks);
        Assert.NotNull(next.HiddenTracks);
        Assert.Empty(next.HiddenTracks);
        Assert.NotNull(next.LockedTracks);
        Assert.Empty(next.LockedTracks);
        Assert.NotNull(next.TrackOpacities);
        Assert.Empty(next.TrackOpacities);
    }

    [Fact]
    public void SetTrackState_WrongArrayTypeRejectsBatchWithoutChangingOriginalState()
    {
        var project = Project(Clip("a", 0, 8)) with { MutedTracks = [4], SoloedTracks = [5] };

        var error = Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(project, [
            Cmd("setRange", """{"start":0,"end":6}"""),
            Cmd("setTrackState", """{"mute":[6],"solo":5}"""),
        ]));

        Assert.Equal("参数 solo 必须是数组，收到数字", error.Message);
        Assert.Equal(8d, project.End);
        Assert.Equal(4, Assert.Single(project.MutedTracks!));
        Assert.Equal(5, Assert.Single(project.SoloedTracks!));
    }

    [Fact]
    public void SetItemProps_RejectsFadeTotalLongerThanItem()
    {
        var error = Rejects(
            Project(Clip("a", 0, 2)),
            Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":1.2,"fadeOut":1}]}"""));

        Assert.Contains("片段 a", error.Message);
        Assert.Contains("淡入淡出时长无效", error.Message);
        Assert.Contains("fadeIn + fadeOut", error.Message);
    }

    [Fact]
    public void SetItemProps_FadeInCombinesWithExistingFadeOut()
    {
        var item = Clip("a", 0, 2) with { FadeOut = 1.25d };

        var error = Rejects(Project(item), Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":1}]}"""));

        Assert.Contains("淡入淡出时长无效", error.Message);
        Assert.Equal(0d, item.FadeIn);
        Assert.Equal(1.25d, item.FadeOut);
    }

    [Fact]
    public void SetItemProps_FadeOutCombinesWithExistingFadeIn()
    {
        var item = Clip("a", 0, 2) with { FadeIn = 1.25d };

        var error = Rejects(Project(item), Cmd("setItemProps", """{"items":[{"key":"a","fadeOut":1}]}"""));

        Assert.Contains("淡入淡出时长无效", error.Message);
        Assert.Equal(1.25d, item.FadeIn);
        Assert.Equal(0d, item.FadeOut);
    }

    [Fact]
    public void SetItemProps_FadeTotalUsesSpeedAdjustedOutputDuration()
    {
        var item = Clip("a", 0, 8) with { Speed = 4d };

        var error = Rejects(Project(item), Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":1.25,"fadeOut":1}]}"""));

        Assert.Contains("片段输出时长", error.Message);
    }

    [Fact]
    public void SetItemProps_FadeTotalCanUseSlowMotionOutputDuration()
    {
        var item = Clip("a", 0, 2) with { Speed = 0.5d };

        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(item),
            [Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":2,"fadeOut":2}]}""")]);

        var result = Assert.Single(next.Timeline!);
        Assert.Equal(2d, result.FadeIn);
        Assert.Equal(2d, result.FadeOut);
        Assert.Empty(Assert.Single(records).Adjusted);
        EditorRenderService.Validate(next);
    }

    [Fact]
    public void SetItemProps_FadeTotalAllowsValidateToleranceBoundary()
    {
        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 2)),
            [Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":2.001,"fadeOut":0}]}""")]);

        Assert.Equal(2.001d, Assert.Single(next.Timeline!).FadeIn);
        Assert.Empty(Assert.Single(records).Adjusted);
        EditorRenderService.Validate(next);
    }

    [Fact]
    public void SetItemProps_FadeTotalRejectsBeyondValidateTolerance()
    {
        var error = Rejects(
            Project(Clip("a", 0, 2)),
            Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":2.0011}]}"""));

        Assert.Contains("淡入淡出时长无效", error.Message);
    }

    [Fact]
    public void SetItemProps_FadeTotalChecksClampedResultRatherThanRequestedValues()
    {
        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(Clip("a", 0, 6)),
            [Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":99,"fadeOut":1}]}""")]);

        var result = Assert.Single(next.Timeline!);
        Assert.Equal(5d, result.FadeIn);
        Assert.Equal(1d, result.FadeOut);
        var adjusted = Assert.Single(Assert.Single(records).Adjusted);
        Assert.Equal("fadeIn", adjusted.Field);
        Assert.Equal(5d, adjusted.Value);
        EditorRenderService.Validate(next);
    }

    [Fact]
    public void SetItemProps_FadeTotalStillRejectsWhenClampedResultIsTooLong()
    {
        var error = Rejects(
            Project(Clip("a", 0, 2)),
            Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":99,"fadeOut":1}]}"""));

        Assert.Contains("淡入淡出时长无效", error.Message);
    }

    [Fact]
    public void SetItemProps_FadeTotalChecksFinalStateAfterRepeatedItemEntries()
    {
        var item = Clip("a", 0, 2) with { FadeIn = 1.5d, FadeOut = 0.5d };

        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(item),
            [Cmd("setItemProps", """{"items":[{"key":"a","fadeOut":1.5},{"key":"a","fadeIn":0.5}]}""")]);

        var result = Assert.Single(next.Timeline!);
        Assert.Equal(0.5d, result.FadeIn);
        Assert.Equal(1.5d, result.FadeOut);
        Assert.Empty(Assert.Single(records).Adjusted);
        EditorRenderService.Validate(next);
    }

    [Fact]
    public void SetItemProps_InvalidFadeOnLaterItemLeavesOriginalItemsUntouched()
    {
        var first = Clip("a", 0, 2);
        var second = Clip("b", 2, 4);
        var project = Project(first, second);

        var error = Rejects(
            project,
            Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":0.5},{"key":"b","fadeIn":1.5,"fadeOut":1}]}"""));

        Assert.Contains("片段 b", error.Message);
        Assert.Same(first, project.Timeline![0]);
        Assert.Same(second, project.Timeline[1]);
        Assert.Equal(0d, first.FadeIn);
        Assert.Equal(0d, second.FadeIn);
        Assert.Equal(0d, second.FadeOut);
    }

    [Fact]
    public void SetItemProps_InvalidFadeInSecondCommandRejectsWholeBatch()
    {
        var project = Project(Clip("a", 0, 2));

        var error = Assert.Throws<EditorCommandException>(() => EditorCommandExecutor.ApplyBatch(project, [
            Cmd("setRange", """{"start":0,"end":6}"""),
            Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":1.5,"fadeOut":1}]}"""),
        ]));

        Assert.Contains("淡入淡出时长无效", error.Message);
        Assert.Equal(8d, project.End);
        Assert.Equal(0d, project.Timeline![0].FadeIn);
        Assert.Equal(0d, project.Timeline[0].FadeOut);
    }

    [Fact]
    public void SetItemProps_DisabledItemStillRejectsInvalidFadeTotal()
    {
        var item = Clip("a", 0, 2) with { Disabled = true };

        var error = Rejects(Project(item), Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":1.5,"fadeOut":1}]}"""));

        Assert.Contains("淡入淡出时长无效", error.Message);
    }

    [Fact]
    public void SetItemProps_FadeTotalKeepsValidateNestedSequenceExemption()
    {
        var item = Clip("a", 0, 2) with { NestedSequenceId = "nested-a" };

        var (next, records) = EditorCommandExecutor.ApplyBatch(
            Project(item),
            [Cmd("setItemProps", """{"items":[{"key":"a","fadeIn":1.5,"fadeOut":1}]}""")]);

        var result = Assert.Single(next.Timeline!);
        Assert.Equal(1.5d, result.FadeIn);
        Assert.Equal(1d, result.FadeOut);
        Assert.Empty(Assert.Single(records).Adjusted);
        EditorRenderService.Validate(next);
    }
}
