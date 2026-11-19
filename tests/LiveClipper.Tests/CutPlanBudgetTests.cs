using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

public sealed class CutPlanBudgetTests
{
    private static readonly AiConnection Local=new("ollama","http://127.0.0.1:11434/","test-local",null);
    private static (TranscriptSegment Segment,int Index)[] Cues(int count,int offset=0,int textLength=20)=>
        Enumerable.Range(offset,count).Select(i=>(new TranscriptSegment(i,i+1,new string('字',textLength)+i),i)).ToArray();

    [Fact]
    public void EveryCueAndGlobalIndexSurviveDenseAndLongTextBatching()
    {
        foreach(var length in new[]{10,500})
        {
            var cues=Cues(100,31,length);var batches=CutPlanBatches.Create(cues);
            Assert.Equal(cues,batches.SelectMany(x=>x).ToArray());
            Assert.All(batches,b=>
            {
                Assert.InRange(b.Length,1,16);
                Assert.True(b.Sum(c=>CutPlanBatches.Line(c.Segment,c.Index).Length+1)<=1800);
            });
        }
    }

    [Fact]
    public void BatchDiagnosticsContainOnlyBoundedOperationalMetadata()
    {
        var connection=new AiConnection("ollama","http://example.invalid/?secret=never-log","qwen-test","secret-key-never-log");
        var cues=new[]{(new TranscriptSegment(0,1,"private-caption-never-log"),57)};
        var json=JsonSerializer.Serialize(CutPlanBatches.Metadata(connection,cues,2,7,44,555));
        using var doc=JsonDocument.Parse(json);var detail=doc.RootElement;
        Assert.Equal(2,detail.GetProperty("batchNumber").GetInt32());
        Assert.Equal(7,detail.GetProperty("batchCount").GetInt32());
        Assert.Equal(1,detail.GetProperty("cueCount").GetInt32());
        Assert.Equal(57,detail.GetProperty("firstIndex").GetInt32());
        Assert.Equal(57,detail.GetProperty("lastIndex").GetInt32());
        Assert.Equal(44,detail.GetProperty("inputCharacters").GetInt32());
        Assert.Equal(555,detail.GetProperty("promptCharacters").GetInt32());
        Assert.Equal("qwen-test",detail.GetProperty("model").GetString());
        Assert.Equal(1024,detail.GetProperty("outputTokens").GetInt32());
        Assert.Equal(8192,detail.GetProperty("contextTokens").GetInt32());
        Assert.DoesNotContain("private-caption",json);Assert.DoesNotContain("secret",json);Assert.DoesNotContain("example.invalid",json);
    }

    [Fact]
    public void OversizeCueFailsBeforeAnyPartialBatchCanBeSubmitted()
    {
        var cues=Cues(32).Concat(Cues(1,32,2000));
        var error=Assert.Throws<ArgumentException>(()=>CutPlanBatches.Create(cues));
        Assert.Contains("32",error.Message);Assert.Contains("未截断",error.Message);
    }

    [Fact]
    public void TimestampsAreInvariantAndPromptHasOnlyCutResponsibilities()
    {
        var old=CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture=new CultureInfo("fr-FR");Assert.Contains("1.25-2.75",CutPlanBatches.Line(new(1.25,2.75,"text"),7)); }
        finally { CultureInfo.CurrentCulture=old; }
        Assert.Contains(AiSkillText.CutPlanVersion,AiSkillText.CutPlanSystemPrompt);
        Assert.True(AiSkillText.CutPlanSystemPrompt.Length<1100);
        Assert.DoesNotContain("拖动",AiSkillText.CutPlanSystemPrompt);
    }

    [Fact]
    public void CutPlanIdsAcceptOnlyIntegersAndDecimalIntegerStringsWithinTheBatch()
    {
        using var ids=JsonDocument.Parse("""[1,"2","999"]""");
        Assert.Equal(new[]{1,2},EditPlanningService.SelectedIds(ids.RootElement,[1,2]).ToArray());
        using var text=JsonDocument.Parse("""["2.5"]""");
        Assert.Throws<FormatException>(()=>EditPlanningService.SelectedIds(text.RootElement,[2]).ToArray());
    }

    private sealed class Handler(int failOn=0,bool local=true):HttpMessageHandler
    {
        public readonly List<JsonElement> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root=doc.RootElement.Clone();Requests.Add(root);
            var input=root.GetProperty("messages")[1].GetProperty("content").GetString()!;
            var rows=input.Split('\n').Skip(1).Where(x=>x.Length>0).Select(x=>x.Split(" | ")).ToArray();
            var ids=rows.Select(x=>int.Parse(x[0],CultureInfo.InvariantCulture)).ToArray();
            var output=JsonSerializer.Serialize(new {title="t",summary="s",start=ids.First(),end=ids.Last()+1,keep=ids.Reverse(),hooks=Array.Empty<int>(),reasons=Array.Empty<string>()});
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(local?(object)new {done=true,done_reason=Requests.Count==failOn?"length":"stop",message=new {content=output}}:new {choices=new[]{new {finish_reason="stop",message=new {content=output}}}}),Encoding.UTF8,"application/json")};
        }
    }

    [Theory]
    [InlineData(false,true)]
    [InlineData(true,true)]
    [InlineData(false,false)]
    public async Task HundredCuesUseAllBatchesAndPersistOnlyCompletePlan(bool truncateSecond,bool local)
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"CutPlanBudget-"+Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var store=new Store(Path.Combine(root,"test.db"));store.Migrate();var studio=new StudioStore(store.DatabasePath);studio.Migrate();
            var clip=new Clip("source","event",Path.Combine(root,"unused.mp4"),null,DateTimeOffset.UtcNow);
            var project=new EditorProject {Start=0,End=200};studio.SaveProject(clip.Id,JsonSerializer.Serialize(project));
            var original=studio.GetProjectRecord(clip.Id);
            var cues=Cues(120);var transcript=new EffectiveTranscriptResult(cues.Select(c=>c.Segment).ToArray(),"test","digest","cache","");
            var handler=new Handler(truncateSecond?2:0,local);using var client=new HttpClient(handler);
            var planning=new EditPlanningService(store,studio,client);
            Task<CutSuggestion> Run()=>planning.CutWithTranscriptAsync(clip,10,110,local?Local:new AiConnection("compatible","https://mock.invalid/v1","test","test-not-a-real-key"),default,transcript);
            if(truncateSecond)
            {
                await Assert.ThrowsAsync<AiCompletionException>(Run);
                Assert.Equal(2,handler.Requests.Count);Assert.Null(studio.Document("cut-suggestion",clip.Id));
            }
            else
            {
                var result=await Run();Assert.Equal(Enumerable.Range(10,100),result.Keep);
                Assert.Equal("cache",result.TranscriptCacheKey);Assert.Equal(local?7:2,handler.Requests.Count);
                var lines=handler.Requests.SelectMany(r=>r.GetProperty("messages")[1].GetProperty("content").GetString()!.Split('\n').Skip(1));
                Assert.Equal(cues.Skip(10).Take(100).Select(c=>CutPlanBatches.Line(c.Segment,c.Index)),lines);
                Assert.NotNull(studio.Document("cut-suggestion",clip.Id));
            }
            Assert.Equal(original,studio.GetProjectRecord(clip.Id));
            Assert.All(handler.Requests,r=>
            {
                if(local)
                {
                Assert.Equal(1024,r.GetProperty("options").GetProperty("num_predict").GetInt32());
                Assert.Equal(8192,r.GetProperty("options").GetProperty("num_ctx").GetInt32());
                }
                else Assert.False(r.TryGetProperty("options",out _));
                Assert.Equal(AiSkillText.CutPlanSystemPrompt,r.GetProperty("messages")[0].GetProperty("content").GetString());
            });
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }

    [Fact]
    public async Task OutputLimitDoesNotImplicitlySetContextWindow()
    {
        using var client=new HttpClient(new FixedHandler());
        await AiAnalysisService.AskAsync(Local,"test",default,client,attemptLimit:1,localOutputLimit:512);
    }
    private sealed class FixedHandler:HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var options=doc.RootElement.GetProperty("options");Assert.Equal(512,options.GetProperty("num_predict").GetInt32());Assert.False(options.TryGetProperty("num_ctx",out _));
            return new(HttpStatusCode.OK){Content=new StringContent("{\"done\":true,\"done_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}",Encoding.UTF8,"application/json")};
        }
    }
}
