using System.Text.Json;
using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class EditorLongGapTests
{
    [Fact]
    public void CandidateAfterLongV3Original_RoundTripsAndKeepsBothTracks()
    {
        // R41 real baseline: a 773.842778-second original on V3, candidate 186.72..315.51 on V1.
        // FilmCraft projects source/timeline timing at 30 fps before saving.
        var gap = Math.Round(773.842778 * 30) / 30;
        var start = Math.Round(186.72 * 30) / 30;
        var end = Math.Round(315.51 * 30) / 30;
        var plan = new EditorProject { Fps=30, Timeline=[
            new("original","copy-source",0,773.842778,Track:3),
            new("candidate","copy-source",start,end,GapBefore:gap,Track:1)] };
        EditorRenderService.Validate(plan);
        Assert.Equal(gap + end - start, EditorRenderService.TimelineDuration(plan.Timeline), 8);
        var restored = EditorProjectCodec.Deserialize(JsonSerializer.Serialize(plan));
        EditorRenderService.Validate(restored);
        Assert.Equal(773.842778, restored.Timeline![0].End);
        Assert.Equal(3, restored.Timeline[0].Track);
        Assert.Equal(gap, restored.Timeline[1].GapBefore);
        Assert.Equal(start, restored.Timeline[1].Start);
        Assert.Equal(end, restored.Timeline[1].End);
        Assert.Equal(1, restored.Timeline[1].Track);
    }
    [Fact]
    public void SixHourSequenceBoundaryStillAppliesIncludingAllGaps()
    {
        var item = new TimelineItem("item","source",0,1,GapBefore:21599);
        EditorRenderService.Validate(new EditorProject {Timeline=[item]});
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(new EditorProject {Timeline=[item with {GapBefore=21600}]}));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(new EditorProject {Timeline=[
            new("a","source",0,1,GapBefore:11000),new("b","source",0,1,GapBefore:11000)]}));
    }
    [Theory]
    [InlineData(-1)] [InlineData(21601)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidGapIsStillRejected(double gap) => Assert.Throws<ArgumentException>(()=>
        EditorRenderService.Validate(new EditorProject {Timeline=[new("a","source",0,1,GapBefore:gap)]}));
}
