using System.Text.Json;
using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class SemanticReviewTests
{
    private static SemanticReviewInput Input()=>new([new("a",0,2,"我买了三个"),new("b",2,4,"后续上下文"),new("c",10,12,"我没买四个吗？"),new("d",12,14,"补充上下文")],[new("A",["a","b"]),new("B",["c","d"])]);
    private static string Reply(string[]? left=null,string[]? right=null,string candidate="A")=>JsonSerializer.Serialize(new {suggestions=new[]{new {leftCandidateId=candidate,rightCandidateId="B",leftCueIds=left??["a"],rightCueIds=right??["c"],reason="可能重复，必须核对差异",contextRisks=Array.Empty<string>()}}});
    [Fact]
    public void TrustedEvidenceAndLocalRisks_DefaultManual_NoModelTimes()
    {
        var input=Input();var before=JsonSerializer.Serialize(input);var r=Assert.Single(SemanticReviewService.Parse(input,Reply()));
        Assert.Equal("我买了三个",r.Left.Cues[0].Text);Assert.Equal(10,r.Right.Cues[0].Start);Assert.Equal("b",r.Left.After!.Id);Assert.True(r.RequiresManualReview);
        Assert.Contains("negation",r.ContextRisks);Assert.Contains("numbers",r.ContextRisks);Assert.Contains("question-answer",r.ContextRisks);Assert.Equal(before,JsonSerializer.Serialize(input));
        Assert.Contains("Candidates",SemanticReviewService.BuildPrompt(input));
        Assert.Empty(SemanticReviewService.Parse(input,"{\"suggestions\":[]}"));
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("c")]
    public void UnknownAndWrongCandidateCueRejected(string id)=>Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(Input(),Reply([id])));
    [Fact]
    public void WholeCandidateDuplicateIdModelTimeAndSharedEvidenceRejected()
    {
        Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(Input(),Reply(["a","b"])));
        Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(Input(),Reply(["a","a"])));
        Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(Input(),Reply().Replace("\"reason\":","\"start\":0,\"reason\":")));
        var input=Input() with {Candidates=[new("A",["a","b"]),new("B",["a","d"])]};
        Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(input,Reply(right:["a"])));
        Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(Input(),Reply(candidate:"B")));
    }
    [Theory]
    [InlineData("-1","1")]
    [InlineData("1.5","15")]
    [InlineData("10%","10")]
    public void LocalNumberDifferencesAndStableSuggestionId(string a,string b)
    {
        var input=Input();input.Cues[0]=input.Cues[0] with {Text=a};input.Cues[2]=input.Cues[2] with {Text=b};
        var first=Assert.Single(SemanticReviewService.Parse(input,Reply()));
        Assert.Contains("numbers",first.ContextRisks);Assert.Equal(first.Id,Assert.Single(SemanticReviewService.Parse(input,Reply())).Id);
    }
    [Fact]
    public void ExternalContextIsReturnedButCannotBeSelectedAsEvidence()
    {
        var input=Input() with {Cues=[..Input().Cues,new("before",0,.1,"外部问题"),new("after",15,16,"后续回答")],Candidates=[new("A",["a","b"],"before",null),new("B",["c","d"],null,"after")]};
        var result=Assert.Single(SemanticReviewService.Parse(input,Reply()));Assert.Equal("before",result.Left.Before!.Id);
        var tail=Assert.Single(SemanticReviewService.Parse(input,Reply(right:["d"])));Assert.Equal("after",tail.Right.After!.Id);
        Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(input,Reply(["before"])));
    }
    [Fact]
    public void ActualRepeatedPhrase_SameCandidatePairsRemainManual_EmptyIsLegal()
    {
        const string candidate="d3b59052aa00428aa3c846d5f2f7ac9e";
        var input=new SemanticReviewInput([
            new("cue-81",218.74,219.74,"要不認輸吧"),
            new("cue-82",225.98,227.02,"要不認輸吧"),
            new("cue-83",227.94,229.46,"要不認輸吧都已經這樣了"),
            new("cue-84",232.77,234.29,"要不認輸吧都已經這樣了")
        ],[new(candidate,["cue-81","cue-82","cue-83","cue-84"])]);
        var before=JsonSerializer.Serialize(input);
        Assert.Empty(SemanticReviewService.Parse(input,"{\"suggestions\":[]}"));
        var json=JsonSerializer.Serialize(new {suggestions=new[]{
            new {leftCandidateId=candidate,rightCandidateId=candidate,leftCueIds=new[]{"cue-81"},rightCueIds=new[]{"cue-82"},reason="可能是有意重复，需试听",contextRisks=new[]{"intentional-repeat"}},
            new {leftCandidateId=candidate,rightCandidateId=candidate,leftCueIds=new[]{"cue-83"},rightCueIds=new[]{"cue-84"},reason="可能强化情绪，不能仅凭文字判定可删",contextRisks=new[]{"intentional-repeat"}}
        }});
        var result=SemanticReviewService.Parse(input,json);
        Assert.Equal(2,result.Length);
        Assert.All(result,r=>{Assert.Equal(candidate,r.Left.CandidateId);Assert.Equal(candidate,r.Right.CandidateId);Assert.Contains("intentional-repeat",r.ContextRisks);Assert.True(r.RequiresManualReview);});
        Assert.Equal(before,JsonSerializer.Serialize(input));
    }
    [Fact]
    public void PromptExplicitlyComparesWithinAndAcrossCandidatesWithoutTreatingRetakesAsProtected()
    {
        var prompt=AiSkillText.SemanticReviewSystemPrompt;
        Assert.Contains("同一候选内及不同候选之间",prompt);
        Assert.Contains("每侧内部必须连续且属于其声明的候选",prompt);
        Assert.Contains("明确重录或重复表达同一组事实，可以提出待人工审阅建议",prompt);
        Assert.Contains("问答回应、强调、情绪递进以及伴随不同现场动作",prompt);
        Assert.Contains("否定、数字、主体变化或新增信息不得当作等价内容",prompt);
        Assert.Contains("默认两侧保留",prompt);Assert.Contains("不能选择删除哪一侧",prompt);
        Assert.Contains("每侧最多3句、15秒",prompt);Assert.Contains("不得建议移除整个候选",prompt);
        Assert.DoesNotContain("采样率",prompt);Assert.DoesNotContain("四万八千",prompt);
    }
    [Fact]
    public void EmbeddedSkillEvalFixturesAreConcreteValidInputs_NotModelQualityClaims()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null&&!File.Exists(Path.Combine(dir.FullName,"src","LiveClipper.Core","AiSkills","semantic-review","evals","evals.json")))dir=dir.Parent;
        Assert.NotNull(dir);
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"src","LiveClipper.Core","AiSkills","semantic-review","evals","evals.json")));
        var cases=doc.RootElement.GetProperty("cases");Assert.Equal(8,cases.GetArrayLength());
        foreach(var item in cases.EnumerateArray())
        {
            Assert.False(item.GetProperty("modelQualityExecuted").GetBoolean());
            var input=JsonSerializer.Deserialize<SemanticReviewInput>(item.GetProperty("input").GetRawText())!;
            Assert.NotEmpty(SemanticReviewService.BuildPrompt(input));
            Assert.Empty(SemanticReviewService.Parse(input,"{\"suggestions\":[]}"));
            if(item.GetProperty("expectedRelation").GetString()=="possible-repeat-manual-review")
            {
                var left=input.Candidates.Single(c=>c.CueIds.Contains("a1")).Id;
                var right=input.Candidates.Single(c=>c.CueIds.Contains("b1")).Id;
                var reply=JsonSerializer.Serialize(new {suggestions=new[]{new {leftCandidateId=left,rightCandidateId=right,leftCueIds=new[]{"a1"},rightCueIds=new[]{"b1"},reason="仅合同样例，不是模型质量结果",contextRisks=Array.Empty<string>()}}});
                Assert.Single(SemanticReviewService.Parse(input,reply));
            }
        }
    }
    [Fact]
    public void NoncontiguousReversedAndLongEvidenceRejected()
    {
        var input=new SemanticReviewInput([new("a",0,1,"第一句"),new("b",2,3,"第二句"),new("c",20,22,"第三句"),new("d",23,24,"第四句"),new("e",30,31,"第五句"),new("f",32,33,"第六句")],[new("A",["a","b","c","d"]),new("B",["e","f"])]);
        Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(input,Reply(["a","c"],["e"])));
        Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(input,Reply(["b","a"],["e"])));
        Assert.Throws<ArgumentException>(()=>SemanticReviewService.Parse(input,Reply(["b","c"],["e"])));
    }
}
