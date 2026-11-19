using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class VoiceActivityPauseTests
{
    static VoiceActivityEvidence Evidence(SilenceRange[] speech,SilenceRange[]? uncertain=null,VoiceGap[]? gaps=null)=>
        new(VoiceActivityPauseService.Provider,0,10,16000,speech,uncertain??[],gaps??[],[]);
    [Fact]
    public void FragmentedPauseRemainsReviewOnlyWhenOneSideIsTooShortToCut()
    {
        var cuts=SilenceCutPlanner.PlanVoice(Evidence([new(0,2),new(8,10)],[new(2.34,2.38)]),[]);
        Assert.Equal(2,cuts.Length);
        Assert.All(cuts,c=>{Assert.False(c.AutoEligible);Assert.Contains("fragmented-pause-review",c.RiskCodes!);});
        Assert.Contains(cuts,c=>c.RiskCodes!.Contains("insufficient-cut"));
    }
    [Fact]
    public void CaptionCoverageOnlyUsesMeasuredWindowIntersection()
    {
        var evidence=Evidence([new(0,3),new(5,10)]);
        Assert.True(Assert.Single(SilenceCutPlanner.PlanVoice(evidence,[new(0,100,"caption extends beyond requested range")])).AutoEligible);
    }
    [Theory]
    [InlineData(.359, false)]
    [InlineData(.36, true)]
    [InlineData(.361, true)]
    public void ProtectedIslandPointSixSecondBoundary(double weakDuration,bool eligible)
    {
        var evidence=Evidence([new(0,2),new(8,10)],[new(4,4+weakDuration)]);
        var cuts=SilenceCutPlanner.PlanVoice(evidence,[]);
        Assert.Equal(2,cuts.Length);Assert.All(cuts,c=>Assert.Equal(eligible,c.AutoEligible));
    }
    [Fact]
    public void CaptionBoundaryTinyIslandAllowsOnlyOneLongSupportedCaptionGap()
    {
        var evidence=new VoiceActivityEvidence(VoiceActivityPauseService.Provider,22.688,31.136,16000,
            [new(22.688,24.832),new(26.72,31.136)],
            [new(24.832,24.864),new(26.688,26.72)],[],[]);
        var cuts=SilenceCutPlanner.PlanVoice(evidence,[new(22.68,24.76,"before"),new(26.08,27.72,"after")]);
        Assert.Equal(2,cuts.Length);
        Assert.True(cuts[0].AutoEligible);Assert.Equal(25,cuts[0].CutStart);
        Assert.False(cuts[1].AutoEligible);Assert.Contains("fragmented-pause-review",cuts[1].RiskCodes!);
        Assert.Equal(25.933333333,cuts[0].CutEnd,6);Assert.Equal(26.2,cuts[1].CutStart,6);
    }
    [Fact]
    public void MostlyUnvoicedSentenceMakesBothRealReportedCutsReviewOnly()
    {
        var evidence=new VoiceActivityEvidence(VoiceActivityPauseService.Provider,14.72,24.832,16000,
            [new(14.72,17.536),new(21.12,21.952),new(22.688,24.832)],
            [new(17.536,17.568),new(21.088,21.12),new(21.952,21.984),new(22.56,22.688)],[],[]);
        var cuts=SilenceCutPlanner.PlanVoice(evidence,[new(17.8,22.68,"sentence with uncertain voice coverage"),new(22.68,24.76,"next sentence")]);
        Assert.Equal(2,cuts.Length);
        Assert.All(cuts,c=>{Assert.False(c.AutoEligible);Assert.Contains("caption-vad-conflict",c.RiskCodes!);});
        Assert.Equal(17.933333333,cuts[0].CutStart,6);Assert.Equal(20.966666667,cuts[0].CutEnd,6);
        Assert.Equal(22.133333333,cuts[1].CutStart,6);Assert.Equal(22.433333333,cuts[1].CutEnd,6);
    }
    [Fact]
    public void CaptionCoverageUsesUnionAndWeakVoiceAndDoesNotBanEveryInternalPause()
    {
        var reliable=Evidence([new(0,3),new(5,10)],[new(0,3)]);
        Assert.True(Assert.Single(SilenceCutPlanner.PlanVoice(reliable,[new(0,10,"normal sentence")])).AutoEligible);
        var weak=Evidence([new(0,1),new(9,10)],[new(1,3),new(7,9)]);
        Assert.True(Assert.Single(SilenceCutPlanner.PlanVoice(weak,[new(0,10,"weak voice protected")])).AutoEligible);
        var low=Evidence([new(0,1),new(9,10)],[new(0,1),new(0,1)]);
        Assert.False(Assert.Single(SilenceCutPlanner.PlanVoice(low,[new(0,10,"contradiction")])).AutoEligible);
        Assert.True(Assert.Single(SilenceCutPlanner.PlanVoice(low,[])).AutoEligible);
    }
    [Fact]
    public void ReliableVoiceOnBothSidesAllowsInternalSentencePauseAndNoTranscript()
    {
        var evidence=Evidence([new(0,3),new(5,10)]);
        var absent=Assert.Single(SilenceCutPlanner.PlanVoice(evidence,[]));Assert.True(absent.AutoEligible);
        var sentence=Assert.Single(SilenceCutPlanner.PlanVoice(evidence,[new(0,10,"one long sentence")]));
        Assert.True(sentence.AutoEligible);Assert.Equal(3.133333333,sentence.CutStart,6);Assert.Equal(4.866666667,sentence.CutEnd,6);
    }
    [Fact]
    public void WholeCueWithNoDetectedVoiceIsConflictAndNotRemoved()
    {
        var cut=SilenceCutPlanner.PlanVoice(Evidence([new(0,3),new(7,10)]),[new(4,6,"possible quiet speech")]);
        Assert.All(cut,x=>{Assert.False(x.AutoEligible);Assert.Contains("caption-vad-conflict",x.RiskCodes!);});
    }
    [Fact]
    public void WeakVoiceAndLargeGapPreventAutomaticDeletionButSmallPtsGapDoesNotShiftTime()
    {
        var speech=new[]{new SilenceRange(0,3),new SilenceRange(5,10)};
        var weak=SilenceCutPlanner.PlanVoice(Evidence(speech,[new(4,4.04)]),[]);
        Assert.Equal(2,weak.Length);Assert.All(weak,x=>{Assert.False(x.AutoEligible);Assert.Contains("fragmented-pause-review",x.RiskCodes!);});
        Assert.All(weak,x=>Assert.True(x.CutEnd<=3.88||x.CutStart>=4.16));
        var ambiguous=Assert.Single(SilenceCutPlanner.PlanVoice(Evidence(speech,[new(3,5)]),[]));
        Assert.False(ambiguous.AutoEligible);Assert.Contains("uncertain-speech",ambiguous.RiskCodes!);
        var large=Assert.Single(SilenceCutPlanner.PlanVoice(Evidence(speech,gaps:[new(4,4.15,"missing")]),[]));
        Assert.False(large.AutoEligible);Assert.Contains("pts-gap",large.RiskCodes!);
        var small=Assert.Single(SilenceCutPlanner.PlanVoice(Evidence(speech,gaps:[new(4,4.0635,"missing")]),[]));
        Assert.True(small.AutoEligible);Assert.Equal(4.866666667,small.CutEnd,6);
    }
    [Fact]
    public void ShortVoicedIslandIsNeverBridgedAndNoVoiceNeverDeletesWholeSource()
    {
        var cuts=SilenceCutPlanner.PlanVoice(Evidence([new(0,2),new(3,3.032),new(5,10)]),[]);
        Assert.Equal(2,cuts.Length);Assert.All(cuts,x=>Assert.True(x.AutoEligible));
        Assert.True(cuts[0].CutEnd<3);Assert.True(cuts[1].CutStart>3.032);
        var blank=Assert.Single(SilenceCutPlanner.PlanVoice(Evidence([]),[]));
        Assert.False(blank.AutoEligible);Assert.Contains("no-speech-context",blank.RiskCodes!);
    }
    [Fact]
    public void LargeGapRemainsRiskEvenInsideLongCaptionAndLeadingTrailingRemain()
    {
        var cuts=SilenceCutPlanner.PlanVoice(Evidence([new(1,3),new(5,9)],gaps:[new(3.1,4.5,"gap")]),[new(0,10,"long caption")]);
        Assert.All(cuts,x=>Assert.False(x.AutoEligible));Assert.Contains(cuts,x=>x.RiskCodes!.Contains("pts-gap"));
    }

    [Theory]
    [InlineData(2.85,false)] [InlineData(2.86,true)] [InlineData(2.87,true)]
    public void SingleFragmentExceptionRequiresPointSixAfterInwardFrameRounding(double captionStart,bool eligible)
    {
        var cuts=SilenceCutPlanner.PlanVoice(Evidence([new(0,2),new(4,10)]),[new(0,2,"before"),new(captionStart,6,"after")]);
        Assert.Equal(2,cuts.Length);Assert.Equal(eligible,cuts[0].AutoEligible);Assert.False(cuts[1].AutoEligible);
        Assert.True(cuts[0].CutEnd<=captionStart-.12+1e-9);
    }
    [Fact]
    public void FragmentExceptionNeverReleasesWeakVoiceIslandOrOverlappingCaption()
    {
        var captions=new[]{new TranscriptSegment(0,2,"before"),new TranscriptSegment(3,6,"after")};
        var weak=SilenceCutPlanner.PlanVoice(Evidence([new(0,2),new(4,10)],[new(2.95,3.05)]),captions);
        Assert.All(weak,c=>Assert.False(c.AutoEligible));
        Assert.All(weak,c=>Assert.True(c.CutEnd<=2.83+1e-9||c.CutStart>=3.17-1e-9));
        var conflicting=SilenceCutPlanner.PlanVoice(Evidence([new(0,2),new(4,10)]),[..captions,new(2.2,2.7,"possible quiet words")]);
        Assert.All(conflicting,c=>Assert.False(c.AutoEligible));
        var oneSided=SilenceCutPlanner.PlanVoice(Evidence([new(0,2),new(4,10)]),[captions[1]]);
        Assert.All(oneSided,c=>Assert.False(c.AutoEligible));
        var sparse=SilenceCutPlanner.PlanVoice(Evidence([new(0,.4),new(1.9,2),new(4,10)]),captions).Where(c=>c.Start==2).ToArray();
        Assert.All(sparse,c=>Assert.False(c.AutoEligible));
    }
    [Fact]
    public void FullRealFirst34SecondsAddsOnlyTheSingleCaptionGapAndKeepsRiskyTail()
    {
        var speech=new SilenceRange[]{new(0,4.704),new(5.568,5.952),new(6.4,9.632),new(11.392,13.536),new(14.72,17.536),new(21.12,21.952),new(22.688,24.832),new(26.72,31.136),new(33.6,34)};
        var weak=new SilenceRange[]{new(4.704,4.736),new(5.44,5.568),new(6.144,6.4),new(9.632,9.664),new(11.36,11.392),new(13.536,13.568),new(14.688,14.72),new(17.536,17.568),new(21.088,21.12),new(21.952,21.984),new(22.56,22.688),new(24.832,24.864),new(26.688,26.72),new(31.136,31.2),new(33.568,33.6)};
        var captionRanges=new[]{(0d,1.64),(1.64,2.76),(2.76,4.6),(5.4,6.36),(6.36,8.44),(8.44,9.56),(11.32,13.44),(14.64,16.4),(16.4,17.36),(17.8,22.68),(22.68,24.76),(26.08,27.72),(27.72,29.56),(29.56,31d),(31d,33.76)};
        var captions=captionRanges.Select(c=>new TranscriptSegment(c.Item1,c.Item2,"spoken sentence")).ToArray();
        var evidence=new VoiceActivityEvidence(VoiceActivityPauseService.Provider,0,34,16000,speech,weak,[new(26.2683125,26.3318125,"small-pts-gap")],[]);
        var cuts=SilenceCutPlanner.PlanVoice(evidence,captions);
        var eligible=cuts.Where(c=>c.AutoEligible).ToArray();Assert.Equal(4,eligible.Length);Assert.Equal(3.533333333,eligible.Sum(c=>c.CutEnd-c.CutStart),6);Assert.Equal(8,cuts.Length);
        var expected=new[]{(4.8666666667,5.2666666667),(9.8,11.2),(13.7,14.5),(25d,25.9333333333)};
        for(var n=0;n<expected.Length;n++){Assert.Equal(expected[n].Item1,eligible[n].CutStart,6);Assert.Equal(expected[n].Item2,eligible[n].CutEnd,6);}
        Assert.All(cuts.Where(c=>c.CutStart>=17&&c.CutStart<24),c=>{Assert.False(c.AutoEligible);Assert.Contains("caption-vad-conflict",c.RiskCodes!);});
        Assert.All(cuts.Where(c=>c.CutStart>=26),c=>Assert.False(c.AutoEligible));
        var kept=cuts.Where(c=>!c.AutoEligible).ToArray();
        var expectedKept=new[]{(17.933333333,20.966666667),(22.133333333,22.433333333),(26.2,26.566666667),(31.333333333,33.433333333)};
        Assert.Equal(expectedKept.Length,kept.Length);
        for(var n=0;n<kept.Length;n++){Assert.Equal(expectedKept[n].Item1,kept[n].CutStart,6);Assert.Equal(expectedKept[n].Item2,kept[n].CutEnd,6);}
        Assert.DoesNotContain(eligible,c=>weak.Any(w=>c.CutStart<w.End+.12&&c.CutEnd>w.Start-.12));
        Assert.Contains("voice-activity-pause-v5",SilenceCutPlanner.TranscriptKey("caption-key",30));
        var largeGap=SilenceCutPlanner.PlanVoice(evidence with{Gaps=[new(26.1,26.4,"missing")]},captions);
        Assert.All(largeGap.Where(c=>c.Start==24.832),c=>Assert.False(c.AutoEligible));
        Assert.All(SilenceCutPlanner.PlanVoice(evidence,[]).Where(c=>c.RiskCodes!.Contains("fragmented-pause-review")),c=>Assert.False(c.AutoEligible));
    }

    [Fact]
    public void UnreliableNearestCaptionIsNotSkippedForFartherReliableCue()
    {
        var evidence=Evidence([new(0,2),new(4.5,10)]);
        var captions=new[]{new TranscriptSegment(0,2,"reliable distant prior"),new TranscriptSegment(1.9,2.4,"nearest prior lacks speech"),new TranscriptSegment(3.5,7,"following")};
        var cuts=SilenceCutPlanner.PlanVoice(evidence,captions);
        Assert.Contains(cuts,c=>c.CutStart>=2.5&&c.CutEnd-c.CutStart>=.6);
        Assert.All(cuts,c=>Assert.False(c.AutoEligible));
    }
}
