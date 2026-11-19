using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class PreserveTransitionTests
{
    private static TimelineItem Item(string id,double length,double gap=0,int track=1)=>new(id,"source",0,length,Track:track,GapBefore:gap);
    [Theory]
    [InlineData("start",0)] [InlineData("center",12)] [InlineData("end",25)]
    public void PreserveKeepsTimelineAndFrameQuantizedWindow(string align,int beforeFrames)
    {
        var items=new[]{Item("a",2),Item("b",3)};
        var cut=Assert.Single(EditorRenderService.ResolveTransitions(items,[new("t","b",1,"cross-dissolve",25d/30,align,"preserve")],30,new Dictionary<string,double>{{"source",3}}));
        Assert.Equal(2,cut.Start);Assert.Equal(0,EditorRenderService.CutShift(cut));Assert.Equal(0,EditorRenderService.TransitionTailAdjust(cut));
        Assert.Equal(beforeFrames/30d,EditorRenderService.PreserveBefore(cut,30),8);
        Assert.Equal(2,EditorRenderService.SequenceStarts(items,[cut])["b"]);Assert.Equal(5,EditorRenderService.SequenceDuration(items,[cut]));
    }
    [Fact] public void PreserveRejectsGapsAndOtherTracks()
    {
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ResolveTransitions([Item("a",2),Item("b",2,1)],[new("t","b",1,"cross-dissolve",.8,"center","preserve")],30));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ResolveTransitions([Item("a",2,0,2),Item("b",2,0,2)],[new("t","b",2,"cross-dissolve",.8,"center","preserve")],30));
    }
    [Fact] public void PreserveRejectsIntersectingWindowsButAllowsTouching()
    {
        var items=new[]{Item("a",2),Item("b",1),Item("c",2)};
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ResolveTransitions(items,[new("x","b",1,"cross-dissolve",1,"start","preserve"),new("y","c",1,"cross-dissolve",1,"end","preserve")],30));
        Assert.Equal(2,EditorRenderService.ResolveTransitions(items,[new("x","b",1,"cross-dissolve",1,"center","preserve"),new("y","c",1,"cross-dissolve",1,"center","preserve")],30).Length);
    }

    [Fact] public void LayoutPersistsAndExchangeRefusesUnsupportedTransition()
    {
        var p=new EditorProject{Start=0,End=4,Fps=30,Timeline=[Item("a",2),Item("b",2)],Transitions=[new("t","b",1,"cross-dissolve",.8,"center","preserve")]};
        var copy=System.Text.Json.JsonSerializer.Deserialize<EditorProject>(System.Text.Json.JsonSerializer.Serialize(p))!;
        Assert.Equal("preserve",copy.Transitions![0].Layout);
        Assert.Throws<NotSupportedException>(()=>PremiereExportService.ValidateBasics(copy));
        Assert.Throws<NotSupportedException>(()=>JianyingExportService.Validate(copy));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(p with{Transitions=[p.Transitions![0] with{Layout="unknown"}]}));
    }
}
