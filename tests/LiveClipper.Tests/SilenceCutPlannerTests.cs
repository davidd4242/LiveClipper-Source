using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class SilenceCutPlannerTests
{
    [Fact]
    public void CoarseCaptionInMiddleIsProtectedAndLeavesTwoSafeGaps()
    {
        var plan=SilenceCutPlanner.Plan([new(2,5)],[new(0,2,"before"),new(3,4,"quiet word"),new(5,7,"after")]);
        Assert.Equal(2,plan.Length);Assert.All(plan,x=>Assert.True(x.AutoEligible));
        Assert.True(plan[0].CutEnd<=2.88);Assert.True(plan[1].CutStart>=4.12);
        Assert.NotEqual(plan[0].Id,plan[1].Id);
    }
    [Fact]
    public void UncertainCaptionCoverageAndNoTranscriptNeverAutoCut()
    {
        Assert.All(SilenceCutPlanner.Plan([new(2,5),new(8,10)],[]),x=>Assert.False(x.AutoEligible));
        var one=Assert.Single(SilenceCutPlanner.Plan([new(2,5)],[new(0,2,"before")]));
        Assert.Contains("transcript-coverage-uncertain",one.RiskCodes!);
        Assert.False(one.AutoEligible);
    }
    [Theory]
    [InlineData(25)] [InlineData(30)] [InlineData(60)]
    public void CaptionEdgesAndInwardFrameRoundingNeverInvadeProtection(int fps)
    {
        var c=Assert.Single(SilenceCutPlanner.Plan([new(2.01,5.01)],[new(0,2.01,"before"),new(5.01,6,"after")],fps));
        Assert.True(c.AutoEligible);Assert.True(c.CutStart>=2.13);Assert.True(c.CutEnd<=4.89);
        Assert.Equal(Math.Round(c.CutStart*fps),c.CutStart*fps,6);
        Assert.Equal(Math.Round(c.CutEnd*fps),c.CutEnd*fps,6);
    }
    [Fact]
    public void ShortNonSilentFragmentsSurviveEvenWhenLegacyMinimumRequested()
    {
        var input=new[]{new TimelineItem("a","source",0,2)};
        var output=SilenceTrimService.Trim(input,[new(.1,1)],0,.18);
        Assert.Equal(2,output.Length);Assert.Equal(.1,output[0].End);Assert.Equal(0,output[0].Start);
        Assert.Equal(1,output[1].Start);Assert.Equal(2,output[1].End);
    }
    [Fact]
    public void BothConsumersUseIdenticalConservativeCutsAndSubtitleChangeInvalidatesKey()
    {
        var transcript=new[]{new TranscriptSegment(0,2,"before"),new TranscriptSegment(5,8,"after")};
        var candidates=SilenceCutPlanner.Plan([new(2,5)],transcript);
        var raw=SourceSilencePreviewService.Parse("silence_start: 2\nsilence_end: 5",0,8,.6);
        var preview=SilenceCutPlanner.Plan(raw.Select(x=>new SilenceRange(x.Start,x.End)),transcript);
        Assert.Equal(SilenceCutPlanner.AutomaticCuts(candidates),SilenceCutPlanner.AutomaticCuts(preview));
        Assert.NotEqual(SilenceCutPlanner.TranscriptKey("same",25),SilenceCutPlanner.TranscriptKey("same",30));
    }
    [Fact]
    public void EmptyTextAndLongSentenceAreNotWordAlignment()
    {
        var empty=Assert.Single(SilenceCutPlanner.Plan([new(2,5)],[new(0,2,""),new(5,6," ")]));Assert.False(empty.AutoEligible);
        var sentence=Assert.Single(SilenceCutPlanner.Plan([new(2,5)],[new(0,10,"A long sentence containing a pause")]));
        Assert.False(sentence.AutoEligible);Assert.Contains("subtitle-overlap",sentence.RiskCodes!);
    }
    [Fact]
    public void ExistingSourceBoundaryEvidenceDoesNotCutLastSyllableOrUncaptionedTail()
    {
        var plan=SilenceCutPlanner.Plan([new(4.453896,5.357521),new(750,770)],
            [new(.53,4.53,"first sentence"),new(5.53,6.53,"second sentence"),new(740,745.67,"last sentence")]);
        Assert.True(plan[0].AutoEligible);Assert.True(plan[0].CutStart>=4.65);
        Assert.False(plan[1].AutoEligible);
        Assert.Contains("transcript-coverage-uncertain",plan[1].RiskCodes!);
        var input=new[]{new TimelineItem("source","source",0,773.84)};
        var trimmed=SilenceTrimService.Trim(input,SilenceCutPlanner.AutomaticCuts(plan),0);
        Assert.Equal(773.84,trimmed[^1].End);Assert.True(trimmed[0].End>=4.65);
    }
    [Fact]
    public void CaptionEditsInvalidatePlanningKeyAndSourceOverridesStaySeparate()
    {
        var p=new EditorProject{CaptionOverrides=new(){["a"]=[new(0,2,"a")],["b"]=[new(4,6,"b")]}};
        var a=EffectiveTranscript.Resolve(p,"a",null);var b=EffectiveTranscript.Resolve(p,"b",null);
        Assert.NotEqual(SilenceCutPlanner.TranscriptKey(a.CacheKey,30),SilenceCutPlanner.TranscriptKey(b.CacheKey,30));
        var changed=EffectiveTranscript.Resolve(p with {CaptionOverrides=new(){["a"]=[new(0,3,"a")]}},"a",null);
        Assert.NotEqual(SilenceCutPlanner.TranscriptKey(a.CacheKey,30),SilenceCutPlanner.TranscriptKey(changed.CacheKey,30));
    }
    [Theory]
    [InlineData(4.545438,5.459292,2.76,4.6,5.4,6.36,4.733333333333333,5.266666666666667)]
    [InlineData(13.469979,14.683562,11.32,13.44,14.64,16.4,13.6,14.5)]
    public void RealCoarseCaptionEdgesLeaveOnlyGuardedInnerGap(double start,double end,double a,double b,double c,double d,double expectedStart,double expectedEnd)
    {
        var cut=Assert.Single(SilenceCutPlanner.Plan([new(start,end)],[new(a,b,"before"),new(c,d,"after")]));
        Assert.True(cut.AutoEligible);Assert.Equal(expectedStart,cut.CutStart,6);Assert.Equal(expectedEnd,cut.CutEnd,6);
    }
    [Fact]
    public void OverlappingUnsortedProtectionIsSubtractedAsUnionAndTinyRemainderIsNotDeleted()
    {
        var blocked=Assert.Single(SilenceCutPlanner.Plan([new(2,5)],[new(3,6,"b"),new(0,4,"a")]));
        Assert.False(blocked.AutoEligible);Assert.Contains("subtitle-overlap",blocked.RiskCodes!);
        var tiny=Assert.Single(SilenceCutPlanner.Plan([new(2,3)],[new(0,2.3,"a"),new(2.7,5,"b")]));
        Assert.False(tiny.AutoEligible);Assert.Contains("insufficient-cut",tiny.RiskCodes!);
    }
    [Theory]
    [InlineData(2.66,true)]
    [InlineData(2.65,false)]
    public void MinimumCutIsCheckedAfterInwardFrameQuantization(double nextCaptionStart,bool eligible)
    {
        var cut=Assert.Single(SilenceCutPlanner.Plan([new(2,3)],[new(0,2.2,"before"),new(nextCaptionStart,5,"after")]));
        Assert.Equal(eligible,cut.AutoEligible);
        if(eligible)Assert.Equal(.2,cut.CutEnd-cut.CutStart,6);
        else Assert.Contains("insufficient-cut",cut.RiskCodes!);
    }
}
