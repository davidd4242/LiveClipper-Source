using System.Net;
using System.Text.Json;
using LiveClipper.Core;

namespace LiveClipper.Tests;

[Collection("AI required fixes")]
public sealed class AiCompletionTests
{
    private sealed class CaptureHandler(string content, string? finish = "stop", string? reasoning = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason=finish, message=new { content, reasoning_content=reasoning } } },
                usage = new { prompt_tokens=123, completion_tokens=4000, total_tokens=4123, prompt_cache_hit_tokens=23, prompt_cache_miss_tokens=100 }
            })) };
        }
    }
    private sealed class RejectedRequestHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(status) { Content=new StringContent("private-key private-prompt") });
        }
    }
    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ProviderError_ShowsStatusWithoutEchoAndRespectsAttemptLimit(int status)
    {
        using var handler=new RejectedRequestHandler((HttpStatusCode)status);
        using var http=new HttpClient(handler);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>AiAnalysisService.AskAsync(new("compatible","https://example.test/v1","model","private-key"),"private-prompt",default,http,attemptLimit:2));
        Assert.Contains($"HTTP {status}",error.Message);
        Assert.DoesNotContain("private-key",error.Message);
        Assert.DoesNotContain("private-prompt",error.Message);
        Assert.Equal(2,handler.Count);
    }
    [Theory]
    [InlineData("https://api.deepseek.com/v1", "deepseek-flash", true)]
    [InlineData("https://api.deepseek.com", "deepseek-v4-flash", true)]
    [InlineData("https://api.deepseek.com/v1", "deepseek-v4-pro", true)]
    [InlineData("https://another.example/v1", "deepseek-flash", false)]
    [InlineData("https://api.deepseek.com.other.example/v1", "deepseek-flash", false)]
    [InlineData("https://api.deepseek.com/v1", "deepseek-reasoner", false)]
    [InlineData("https://api.deepseek.com:444/v1", "deepseek-flash", false)]
    public async Task BoundedJsonThinkingOverride_OnlyOfficialSupportedDeepSeek(string endpoint,string model,bool changed)
    {
        using var handler=new CaptureHandler("{\"ok\":true}"); using var http=new HttpClient(handler);
        var result=await AiAnalysisService.AskAsync(new("compatible",endpoint,model,"test-key"),"test-prompt",default,http);
        Assert.Equal("{\"ok\":true}",result);
        using var request=JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal(4000,request.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(changed,request.RootElement.TryGetProperty("thinking",out var thinking));
        if(changed) Assert.Equal("disabled",thinking.GetProperty("type").GetString());
    }
    [Theory]
    [InlineData("", "length")]
    [InlineData(" ", "stop")]
    [InlineData("{\"ok\":true}", "length")]
    public async Task EmptyOrTruncatedDeepSeek_IsVisibleNoRetry_PersistsUsageWithoutContent(string content,string finish)
    {
        var folder=Path.Combine(Path.GetTempPath(),"lc-ai-completion-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(folder,"ai-requests.jsonl");
        var previous=Console.Out;
        try
        {
            AiRequestDiagnostics.ConfigureFile(path);Console.SetOut(TextWriter.Null);
            using var handler=new CaptureHandler(content,finish,"private-reasoning");using var http=new HttpClient(handler);
            var error=await Assert.ThrowsAsync<AiCompletionException>(()=>AiAnalysisService.AskAsync(new("compatible","https://api.deepseek.com/v1?private-query","deepseek-flash","private-key"),"private-prompt",default,http));
            Assert.Contains("未自动重试",error.Message);Assert.Contains(finish,error.Message);
            Assert.Single(handler.Requests);
            var log=File.ReadAllText(path);
            foreach(var secret in new[]{"private-reasoning","private-key","private-prompt","private-query"}) Assert.DoesNotContain(secret,log);
            var records=File.ReadAllLines(path).Select(line=>JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            var usage=Assert.Single(records,x=>x.GetProperty("stage").GetString()=="compatible_usage").GetProperty("detail");
            Assert.Equal(4123,usage.GetProperty("totalTokens").GetInt32());
            var metadata=Assert.Single(records,x=>x.GetProperty("stage").GetString()=="completion_metadata").GetProperty("detail");
            Assert.Equal(finish,metadata.GetProperty("finishReason").GetString());Assert.True(metadata.GetProperty("reasoningPresent").GetBoolean());
            Assert.Contains(records,x=>x.GetProperty("stage").GetString()=="failed");
            Assert.DoesNotContain(records,x=>x.GetProperty("stage").GetString()=="completed");
        }
        finally { Console.SetOut(previous);AiRequestDiagnostics.ConfigureFile(null);Directory.Delete(folder,true); }
    }
    [Fact]
    public async Task NonDeepSeek_EmptyContentBehaviorUnchanged()
    {
        using var handler=new CaptureHandler("","length","private-reasoning");using var http=new HttpClient(handler);
        Assert.Equal("",await AiAnalysisService.AskAsync(new("compatible","https://other.example/v1","model","test-key"),"prompt",default,http));
        Assert.Single(handler.Requests);
    }
}
