using LiveClipper.Core;

namespace LiveClipper.Tests;

/// <summary>
/// R51-T：把 EditorRenderService.Validate 的四组轨道范围钉在 EditorRenderService.cs:445-450 的现状上。
/// mute/solo 只收 A1–A3（4..6）是有意设计，不是漏网的 bug：UI 只在音频轨头渲染 M/S
/// （Timeline.tsx 的 track.kind === 'video' 分支只有眼睛按钮，WorkspacePanelWindow.tsx 硬编码 [4, 5, 6]），
/// 放宽到 V 轨会产生用户看不见、也关不掉的静音；客户端 normalizeProject 也会把 V 轨号静默剥掉。
/// hidden 只收 V1–V3（1..3），locked 收全部六条轨；三组范围互不相同，所以必须分别锁死。
/// </summary>
public sealed class EditorRenderValidationTracksTests
{
    /// <summary>一条主画面片段 + 一条 A1 片段的最小合法工程（与 EditorTests 的 withAudio 同形）。</summary>
    private static EditorProject TrackPlan()=>new EditorProject{Timeline=[
        new("base","source",0,2),
        new("voice","source",0,1,GapBefore:2,Track:4),
    ]};

    private static ArgumentException Rejects(EditorProject plan)
    {
        return Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(plan));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void MutedAndSoloedTracks_AcceptEveryAudioTrack(int track)
    {
        var plan=TrackPlan();
        EditorRenderService.Validate(plan with {MutedTracks=[track]});
        EditorRenderService.Validate(plan with {SoloedTracks=[track]});
        EditorRenderService.Validate(plan with {MutedTracks=[track],SoloedTracks=[track]});
    }

    [Fact]
    public void MutedAndSoloedTracks_AcceptTheWholeAudioBusAndEmptySets()
    {
        var plan=TrackPlan();
        EditorRenderService.Validate(plan);
        EditorRenderService.Validate(plan with {MutedTracks=[4,5,6]});
        EditorRenderService.Validate(plan with {SoloedTracks=[4,5,6]});
        EditorRenderService.Validate(plan with {MutedTracks=[6,4],SoloedTracks=[5]});
        EditorRenderService.Validate(plan with {MutedTracks=[],SoloedTracks=[]});
    }

    /// <summary>1 和 2 是 V1/V2：UI 根本不给它们 M 按钮，所以服务端必须拒收。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void MutedTracks_RejectAnythingOutsideA1ToA3(int track)
    {
        var error=Rejects(TrackPlan() with {MutedTracks=[track]});
        Assert.Equal("轨道静音/独奏参数无效",error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void SoloedTracks_RejectAnythingOutsideA1ToA3(int track)
    {
        var error=Rejects(TrackPlan() with {SoloedTracks=[track]});
        Assert.Equal("轨道静音/独奏参数无效",error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HiddenTracks_AcceptEveryVideoTrack(int track)
    {
        EditorRenderService.Validate(TrackPlan() with {HiddenTracks=[track]});
    }

    [Fact]
    public void HiddenTracks_AcceptAllThreeVideoTracksTogether()
    {
        var plan=TrackPlan();
        EditorRenderService.Validate(plan with {HiddenTracks=[1,2,3]});
        EditorRenderService.Validate(plan with {HiddenTracks=[3,1]});
        EditorRenderService.Validate(plan with {HiddenTracks=[]});
    }

    /// <summary>4..6 是音频轨：它们走 mute/solo，不走 hidden，所以 hidden 的上界是 3 而不是 6。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void HiddenTracks_RejectAnythingOutsideV1ToV3(int track)
    {
        var error=Rejects(TrackPlan() with {HiddenTracks=[track]});
        Assert.Equal("轨道隐藏参数无效",error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void LockedTracks_AcceptEveryVideoAndAudioTrack(int track)
    {
        EditorRenderService.Validate(TrackPlan() with {LockedTracks=[track]});
    }

    [Fact]
    public void LockedTracks_AcceptAllSixTracksTogether()
    {
        var plan=TrackPlan();
        EditorRenderService.Validate(plan with {LockedTracks=[1,2,3,4,5,6]});
        EditorRenderService.Validate(plan with {LockedTracks=[6,1]});
        EditorRenderService.Validate(plan with {LockedTracks=[]});
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void LockedTracks_RejectAnythingOutsideV1ToA3(int track)
    {
        var error=Rejects(TrackPlan() with {LockedTracks=[track]});
        Assert.Equal("轨道锁定参数无效",error.Message);
    }

    /// <summary>四组集合都查重：重复轨号会让前端的切换按钮和服务端状态对不上。</summary>
    [Fact]
    public void EveryTrackCollection_RejectsDuplicateEntries()
    {
        var plan=TrackPlan();
        Assert.Equal("轨道静音/独奏参数无效",Rejects(plan with {MutedTracks=[4,4]}).Message);
        Assert.Equal("轨道静音/独奏参数无效",Rejects(plan with {SoloedTracks=[5,5]}).Message);
        Assert.Equal("轨道静音/独奏参数无效",Rejects(plan with {MutedTracks=[4,5,4]}).Message);
        Assert.Equal("轨道隐藏参数无效",Rejects(plan with {HiddenTracks=[1,1]}).Message);
        Assert.Equal("轨道锁定参数无效",Rejects(plan with {LockedTracks=[6,6]}).Message);
    }

    /// <summary>hidden 与 mute/solo 的范围不能互换：这正是 R51-P 查实的前后端契约。</summary>
    [Fact]
    public void HiddenAndMuteRangesStayAsymmetric()
    {
        var plan=TrackPlan();
        EditorRenderService.Validate(plan with {HiddenTracks=[1],MutedTracks=[4]});
        Assert.Equal("轨道隐藏参数无效",Rejects(plan with {HiddenTracks=[4]}).Message);
        Assert.Equal("轨道静音/独奏参数无效",Rejects(plan with {MutedTracks=[1]}).Message);
        Assert.Equal("轨道静音/独奏参数无效",Rejects(plan with {SoloedTracks=[1]}).Message);
    }
}
