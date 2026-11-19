using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

/// <summary>
/// Studio 打包/草稿导出清单的 part 偏移：必须使用压缩后的序列起点与计入 speed 的生效时长，
/// 与渲染器的时间换算（docs/transitions-spec.md §4）及成片总时长保持一致。
/// </summary>
public sealed class StudioExportTests
{
    private static TimelineItem Item(string id,double start,double end,string clipId="source",int track=1,double gap=0,double speed=1,bool disabled=false)
        =>new(id,clipId,start,end,Track:track,GapBefore:gap,Speed:speed,Disabled:disabled);
    private static EditorTransition Transition(string itemId,string kind="cross-dissolve",double duration=.5,string align="center",int track=1)
        =>new($"transition-{itemId}",itemId,track,kind,duration,align);
    private static EditorProject Plan(TimelineItem[] timeline,EditorTransition[]? transitions=null,int fps=30)
        =>new(){Start=0,End=8,Timeline=timeline,Transitions=transitions,Fps=fps};

    [Fact]
    public void NoTransitions_KeepTheOriginalPartLayout()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8),Item("c",8,12)};var plan=Plan(items);
        var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
        // 无 speed、无空隙、无转场时与原公式完全一致：offset 依次累加源区间长度。
        Assert.Equal(new[]{"part-000.mkv","part-001.mkv","part-002.mkv"},parts.Select(x=>x.File));
        Assert.Equal(new[]{0d,4d,8d},parts.Select(x=>x.Start));
        Assert.Equal(new[]{4d,4d,4d},parts.Select(x=>x.Duration));
        Assert.Equal(12,duration,6);
        Assert.Equal(EditorRenderService.TimelineDuration(items),duration,6);
        for(var i=1;i<parts.Length;i++)Assert.Equal(parts[i].Start,parts[i-1].Start+parts[i-1].Duration,6);
        Assert.Equal(parts[^1].Start+parts[^1].Duration,duration,6);
        Assert.Equal("source",parts[0].SourceClipId);Assert.Equal(8,parts[1].SourceEnd,6);Assert.Equal("video",parts[0].Kind);
    }
    [Fact]
    public void Speed_CompressesDurationsAndFollowingOffsets()
    {
        // a：8 秒源区间 2 倍速 → 4 秒；b：2 秒源区间 0.5 倍速 → 4 秒。
        var items=new[]{Item("a",0,8,speed:2),Item("b",0,2,speed:.5)};var plan=Plan(items);
        var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
        Assert.Equal(new[]{0d,4d},parts.Select(x=>x.Start));
        Assert.Equal(new[]{4d,4d},parts.Select(x=>x.Duration));
        Assert.Equal(8,duration,6);
        Assert.Equal(EditorRenderService.TimelineDuration(items),duration,6);
        Assert.Equal(parts[0].Start+parts[0].Duration,parts[1].Start,6);
    }
    [Fact]
    public void SpeedAndGap_PutThePartAtItsRealSequencePosition()
    {
        var items=new[]{Item("a",0,8,speed:2),Item("b",0,4,gap:1.5)};var plan=Plan(items);
        var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
        Assert.Equal(new[]{0d,5.5d},parts.Select(x=>x.Start));
        Assert.Equal(new[]{4d,4d},parts.Select(x=>x.Duration));
        Assert.Equal(9.5,duration,6);
        Assert.Equal(EditorRenderService.TimelineDuration(items),duration,6);
    }
    [Fact]
    public void HalfSecondTransition_CompressesTheFollowingPartStart()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8)};var plan=Plan(items,[Transition("b")]);
        var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
        Assert.Equal(new[]{0d,3.5d},parts.Select(x=>x.Start));
        Assert.Equal(new[]{4d,4d},parts.Select(x=>x.Duration));
        Assert.Equal(7.5,duration,6);
        Assert.Equal(EditorRenderService.SequenceDuration(plan),duration,6);
        // 两个 part 在剪点重叠 0.5 秒，最后一个 part 的结束正好是成片总时长。
        Assert.Equal(.5,parts[0].Start+parts[0].Duration-parts[1].Start,6);
        Assert.Equal(duration,parts[^1].Start+parts[^1].Duration,6);
    }
    [Fact]
    public void SpeedAndTransition_CombineBothCompressions()
    {
        var items=new[]{Item("a",0,8,speed:2),Item("b",0,4,speed:1)};var plan=Plan(items,[Transition("b")]);
        var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
        Assert.Equal(new[]{0d,3.5d},parts.Select(x=>x.Start));
        Assert.Equal(new[]{4d,4d},parts.Select(x=>x.Duration));
        Assert.Equal(7.5,duration,6);
        Assert.Equal(EditorRenderService.SequenceDuration(plan),duration,6);
        Assert.Equal(.5,parts[0].Start+parts[0].Duration-parts[1].Start,6);
        Assert.Equal(duration,parts[^1].Start+parts[^1].Duration,6);
    }
    [Fact]
    public void MultiTrackParts_UseTheirOwnTrackStartsAndTheLongestTrackDuration()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8),Item("o",0,2,track:2),Item("t",0,1,track:3)};
        var plan=Plan(items,[Transition("b")]);
        var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
        Assert.Equal(4,parts.Length);
        Assert.Equal(new[]{0d,3.5d,0d,0d},parts.Select(x=>x.Start));
        Assert.Equal(new[]{4d,4d,2d,1d},parts.Select(x=>x.Duration));
        Assert.Equal(7.5,duration,6);
        Assert.Equal(EditorRenderService.SequenceDuration(plan),duration,6);
    }
    [Fact]
    public void ManifestParts_SerializeWithTheLowercaseKeysTheDraftExporterReads()
    {
        var items=new[]{Item("a",0,4),Item("b",4,8)};var plan=Plan(items,[Transition("b")]);
        var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
        var json=JsonSerializer.Serialize(new{parts,duration});
        using var document=JsonDocument.Parse(json);
        var root=document.RootElement;
        Assert.Equal(7.5,root.GetProperty("duration").GetDouble(),6);
        var part=root.GetProperty("parts")[1];
        Assert.Equal("part-001.mkv",part.GetProperty("file").GetString());
        Assert.Equal(3.5,part.GetProperty("start").GetDouble(),6);
        Assert.Equal(4,part.GetProperty("duration").GetDouble(),6);
        Assert.Equal("video",part.GetProperty("kind").GetString());
        Assert.Equal("source",part.GetProperty("sourceClipId").GetString());
        Assert.Equal(4,part.GetProperty("sourceStart").GetDouble(),6);
        Assert.Equal(8,part.GetProperty("sourceEnd").GetDouble(),6);
        Assert.False(part.TryGetProperty("Start",out _));
    }
    [Fact]
    public void ManifestParts_UseTheRealRenderedFileNamesPerTrack()
    {
        // 渲染器产出的中间文件：V1 → part-NNN.mkv，叠加轨 → overlay-NNN.mkv，音频轨 → audio-NNN.mka（FFV1/PCM）。
        var items=new[]{Item("a",0,4),Item("b",4,8),Item("overlay",0,2,track:2),Item("music",0,3,track:4)};
        var plan=Plan(items);
        var (parts,duration)=StudioExportService.BuildManifest(plan,"source");
        Assert.Equal(new[]{"part-000.mkv","part-001.mkv","overlay-000.mkv","audio-000.mka"},parts.Select(x=>x.File));
        Assert.Equal(new[]{0d,4d,0d,0d},parts.Select(x=>x.Start));
        Assert.Equal(new[]{4d,4d,2d,3d},parts.Select(x=>x.Duration));
        Assert.Equal(8,duration,6);
        Assert.Equal(EditorRenderService.TimelineDuration(items),duration,6);
    }
    [Fact]
    public void ManifestParts_SkipHiddenAndDisabledOverlayTracksLikeTheRenderer()
    {
        var items=new[]{Item("a",0,4),Item("o1",0,2,track:2),Item("o2",0,2,track:3),Item("o3",0,2,track:2,disabled:true)};
        var plan=Plan(items) with {HiddenTracks=[3]};
        var (parts,_)=StudioExportService.BuildManifest(plan,"source");
        Assert.Equal(new[]{"part-000.mkv","overlay-000.mkv"},parts.Select(x=>x.File));
    }
}
