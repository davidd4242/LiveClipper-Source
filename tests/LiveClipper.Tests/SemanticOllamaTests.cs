using System.Net;
using System.Text;
using System.Text.Json;
using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class SemanticOllamaTests
{
    private sealed class Handler(string reason="stop",string content="{\"suggestions\":[]}",bool done=true,bool fail=false):HttpMessageHandler
    {
        public int Calls;public string? Body;public Uri? Uri;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Assert.Null(request.Headers.Authorization);Assert.False(request.Headers.Contains("X-Api-Key"));
            Calls++;Uri=request.RequestUri;Body=await request.Content!.ReadAsStringAsync(token);
            return new(fail?HttpStatusCode.InternalServerError:HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {message=new {role="assistant",content},done,done_reason=reason,prompt_eval_count=200,eval_count=10,total_duration=1}),Encoding.UTF8,"application/json")};
        }
    }
    private static RoughCutPreview Preview()=>new("cached-candidates-and-text-rules","s","s-hash","c-hash","t-hash",10,[new("event","s",0,10,"title","summary","hook","manual")],[new(0,1,"原句甲"),new(2,3,"原句乙")],[],[]);
    private static Task<SemanticAnalysisResult> Run(HttpClient client,string model="qwen2.5:7b-instruct-q4_K_M")
    {
        var p=Preview();return SemanticReviewAnalysis.AnalyzeAsync(p,[new("event",0,10)],true,p.SourceSignature,p.CandidateHash,p.TranscriptCacheKey,new("ollama","http://not-used.invalid",model,"mock-stored-remote-key-must-not-be-sent"),default,client);
    }
    [Fact]
    public async Task LocalSingleCallUsesRealRuntimeEndpointSystemSkillAndBoundedOutput()
    {
        var handler=new Handler();using var client=new HttpClient(handler);var result=await Run(client);
        Assert.Empty(result.Suggestions);Assert.Equal(1,handler.Calls);Assert.Equal(new Uri(OllamaRuntime.BaseUri,"api/chat"),handler.Uri);Assert.True(handler.Uri!.IsLoopback);
        Assert.DoesNotContain("mock-stored-remote-key-must-not-be-sent",handler.Body!);
        using var json=JsonDocument.Parse(handler.Body!);var root=json.RootElement;
        Assert.Equal(4000,root.GetProperty("options").GetProperty("num_predict").GetInt32());Assert.Equal(32768,root.GetProperty("options").GetProperty("num_ctx").GetInt32());Assert.False(root.GetProperty("stream").GetBoolean());Assert.Equal("json",root.GetProperty("format").GetString());
        Assert.Equal("system",root.GetProperty("messages")[0].GetProperty("role").GetString());Assert.Equal(AiSkillText.SemanticReviewSystemPrompt,root.GetProperty("messages")[0].GetProperty("content").GetString());
    }
    [Fact]
    public async Task FullSystemPlusUserUtf8CapRejectsWithoutSendingOrTruncating()
    {
        var p=Preview() with {Transcript=[new(0,1,new string('字',8000)),new(2,3,"保留上下文")]};
        var handler=new Handler();using var client=new HttpClient(handler);
        await Assert.ThrowsAsync<SemanticInputLimitException>(()=>SemanticReviewAnalysis.AnalyzeAsync(p,[new("event",0,10)],true,p.SourceSignature,p.CandidateHash,p.TranscriptCacheKey,new("ollama","", "qwen2.5:7b-instruct-q4_K_M",null),default,client));
        Assert.Equal(0,handler.Calls);Assert.Equal(8000,p.Transcript[0].Text.Length);
    }
    [Theory]
    [InlineData("length","{\"suggestions\":[]}",true)]
    [InlineData("stop","",true)]
    [InlineData("stop","{\"suggestions\":[]}",false)]
    public async Task EmptyTruncatedIncompleteResponsesNeverRetry(string reason,string content,bool done)
    {
        var handler=new Handler(reason,content,done);using var client=new HttpClient(handler);
        await Assert.ThrowsAsync<AiCompletionException>(()=>Run(client));Assert.Equal(1,handler.Calls);
    }
    [Fact]
    public async Task HttpFailureSingleAttemptAndCloudModelRejectedBeforeSending()
    {
        var handler=new Handler(fail:true);using var client=new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Run(client));Assert.Equal(1,handler.Calls);
        var cloud=new Handler();using var cloudClient=new HttpClient(cloud);await Assert.ThrowsAsync<ArgumentException>(()=>Run(cloudClient,"qwen-cloud"));Assert.Equal(0,cloud.Calls);
    }
}
