using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class StudioExportMuteSoloTests
{
    public static TheoryData<string,int[]?,int[]?,int[]> AudioMatrix=>new()
    {
        {"no toggles (null)",null,null,[4,5,6]},
        {"no toggles (empty)",[],[],[4,5,6]},
        {"mute only",[4,6],[],[5]},
        {"solo only",[],[5],[5]},
        {"same-track mute and solo",[4],[4],[4]},
        {"mute and solo on different tracks",[5],[4],[4]},
        {"all audio muted",[4,5,6],[],[]},
        {"multiple solos override all mutes",[4,5,6],[4,6],[4,6]}
    };

    private static TimelineItem Item(int track,bool disabled=false)=>new(
        $"item-{track}",$"source-{track}",0,2,Kind:track>=4?"audio":"video",Track:track,Disabled:disabled);

    private static EditorProject Plan(int[]? muted,int[]? soloed)=>new()
    {
        Start=0,End=2,Fps=30,
        Timeline=Enumerable.Range(1,6).Select(track=>Item(track)).ToArray(),
        MutedTracks=muted,SoloedTracks=soloed
    };

    [Theory]
    [MemberData(nameof(AudioMatrix))]
    public void BuildManifest_AudioPartsMatchRendererAndExpectedTracks(string scenario,int[]? muted,int[]? soloed,int[] expectedTracks)
    {
        var (parts,duration)=StudioExportService.BuildManifest(Plan(muted,soloed),"source-1");
        var audio=parts.Where(part=>part.File.StartsWith("audio-",StringComparison.Ordinal)).ToArray();
        var actualTracks=Enumerable.Range(4,3).Where(track=>audio.Any(part=>part.SourceClipId==$"source-{track}")).ToArray();
        Assert.Equal(expectedTracks,actualTracks);
        Assert.Equal(expectedTracks.Select(track=>$"source-{track}"),audio.Select(part=>part.SourceClipId));
        Assert.Equal(Enumerable.Range(0,expectedTracks.Length).Select(index=>$"audio-{index:000}.mka"),audio.Select(part=>part.File));
        foreach(var track in Enumerable.Range(4,3))
            Assert.True(EditorRenderService.IncludeTrackAudio(track,muted,soloed)==actualTracks.Contains(track),$"{scenario}: track {track} differs from renderer admission");
        Assert.Equal(new[]{"part-000.mkv","overlay-000.mkv","overlay-001.mkv"},parts.Where(part=>part.Kind=="video").Select(part=>part.File));
        Assert.Equal(2,duration,6);
        Assert.All(audio,part=>
        {
            Assert.Equal("audio",part.Kind);
            Assert.Equal(0,part.Start,6);
            Assert.Equal(2,part.Duration,6);
        });
    }

    [Fact]
    public void BuildManifest_HiddenVideoAndDisabledAudioKeepTheirExistingBehavior()
    {
        var plan=Plan([4],[4,5]) with
        {
            HiddenTracks=[2],
            Timeline=[Item(1),Item(2),Item(3),Item(4),Item(5,disabled:true),Item(6)]
        };
        var (parts,duration)=StudioExportService.BuildManifest(plan,"source-1");
        Assert.Equal(new[]{"part-000.mkv","overlay-000.mkv","audio-000.mka"},parts.Select(part=>part.File));
        Assert.Equal(new[]{"source-1","source-3","source-4"},parts.Select(part=>part.SourceClipId));
        Assert.True(EditorRenderService.IncludeTrackAudio(4,plan.MutedTracks,plan.SoloedTracks));
        Assert.Equal(2,duration,6);
    }

    [Fact]
    public void BuildManifest_EmptySoloTrackDoesNotFallBackToNonSoloAudio()
    {
        var plan=Plan(null,[6]) with {Timeline=[Item(1),Item(4),Item(5)]};
        var (parts,_)=StudioExportService.BuildManifest(plan,"source-1");
        Assert.DoesNotContain(parts,part=>part.File.StartsWith("audio-",StringComparison.Ordinal));
        Assert.Equal("part-000.mkv",Assert.Single(parts).File);
        Assert.False(EditorRenderService.IncludeTrackAudio(1,plan.MutedTracks,plan.SoloedTracks));
        Assert.False(EditorRenderService.IncludeTrackAudio(4,plan.MutedTracks,plan.SoloedTracks));
        Assert.False(EditorRenderService.IncludeTrackAudio(5,plan.MutedTracks,plan.SoloedTracks));
    }
}
