using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LiveClipper.Core;

namespace LiveClipper.Tests;

/// <summary>
/// Real embedded-resource checks plus wire-level request contracts for the two skills.
/// These tests prove what is stored and sent; they say nothing about how any model reacts to the skill text.
/// Shares the "AI required fixes" collection because the Ollama case sets the process-wide LIVECLIPPER_OLLAMA_URL.
/// </summary>
[Collection("AI required fixes")]
public sealed class AiSkillTextTests
{
    private static readonly string SubtitleResource = "LiveClipper.Core.AiSkills.subtitle-analysis.SKILL.md";
    private static readonly string RoughCutResource = "LiveClipper.Core.AiSkills.rough-cut.SKILL.md";

    private static string ReadResource(string name)
    {
        using var stream = typeof(AiSkillText).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!, new UTF8Encoding(false), true);
        return reader.ReadToEnd();
    }

    // Independent re-derivation (not a call into the loader) of "body after frontmatter, normalized".
    private static string BodyOf(string raw)
    {
        var text = raw.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        Assert.StartsWith("---\n", text, StringComparison.Ordinal);
        var lines = text.Split('\n');
        var close = Array.FindIndex(lines, 1, l => l.TrimEnd() == "---");
        Assert.True(close > 0, "frontmatter must close");
        return string.Join('\n', lines.Skip(close + 1)).Trim('\n', ' ', '\t');
    }

    public static IEnumerable<object[]> Skills()
    {
        yield return ["subtitle-analysis", "LiveClipper.Core.AiSkills.subtitle-analysis.SKILL.md"];
        yield return ["rough-cut", "LiveClipper.Core.AiSkills.rough-cut.SKILL.md"];
    }

    [Fact]
    public void CoreAssembly_ContainsExactlyTheFourShippingSkillResources()
    {
        var names = typeof(AiSkillText).Assembly.GetManifestResourceNames();
        string[] expected=[SubtitleResource,RoughCutResource,"LiveClipper.Core.AiSkills.semantic-review.SKILL.md","LiveClipper.Core.AiSkills.cut-plan.SKILL.md"];
        Assert.Equal(expected.Order(StringComparer.Ordinal),names.Where(n=>n.StartsWith("LiveClipper.Core.AiSkills.",StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Skills))]
    public void EmbeddedResource_BodyHasNoFrontmatter_AndVersionIsIdPlusBodySha(string id, string resource)
    {
        var raw = ReadResource(resource);
        var body = BodyOf(raw);
        Assert.NotEmpty(body);
        var hash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(body))).ToLowerInvariant();
        var (prompt, version) = id == "rough-cut"
            ? (AiSkillText.RoughCutSystemPrompt, AiSkillText.RoughCutVersion)
            : (AiSkillText.SubtitleAnalysisSystemPrompt, AiSkillText.SubtitleAnalysisVersion);
        Assert.Equal($"{id}@sha256:{hash}", version);
        Assert.Matches(new Regex("^[a-z-]+@sha256:[0-9a-f]{64}$"), version);
        Assert.EndsWith("\n\n" + body, prompt, StringComparison.Ordinal);
        Assert.Contains(version, prompt, StringComparison.Ordinal);
        Assert.Contains(id, prompt.Split('\n')[0], StringComparison.Ordinal);
        // Frontmatter keys must not leak into the system prompt.
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n').Skip(1).TakeWhile(l => l.TrimEnd() != "---"))
            Assert.DoesNotContain(line, prompt.Split('\n').Skip(1), StringComparer.Ordinal);
    }

    [Fact]
    public void TheTwoSkills_AreNonEmptyAndDistinct()
    {
        string[] all = [AiSkillText.SubtitleAnalysisSystemPrompt, AiSkillText.SubtitleAnalysisVersion, AiSkillText.RoughCutSystemPrompt, AiSkillText.RoughCutVersion];
        Assert.All(all, x => Assert.False(string.IsNullOrWhiteSpace(x)));
        Assert.NotEqual(AiSkillText.SubtitleAnalysisSystemPrompt, AiSkillText.RoughCutSystemPrompt);
        Assert.NotEqual(AiSkillText.SubtitleAnalysisVersion, AiSkillText.RoughCutVersion);
        Assert.StartsWith("subtitle-analysis@sha256:", AiSkillText.SubtitleAnalysisVersion, StringComparison.Ordinal);
        Assert.StartsWith("rough-cut@sha256:", AiSkillText.RoughCutVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void RoughCutSkill_RequiresHardCutsAndPreservesManualTransitions()
    {
        var prompt=AiSkillText.RoughCutSystemPrompt;
        Assert.Contains("人工审核与全自动粗剪均不自动添加任何转场",prompt);
        Assert.Contains("生成结果使用硬切，不生成场景转场边界或转场时长请求",prompt);
        Assert.Contains("不清除、重置或替换当前工程已有的手工转场、剪点、字幕及其他编辑",prompt);
        Assert.Contains("时间线手工添加、调整转场仍保留",prompt);
        Assert.Contains("不扩展cut/fine模型JSON合同",prompt);
    }

    public static IEnumerable<object[]> Protocols()
    {
        foreach (var protocol in new[] { "compatible", "ollama" })
            foreach (var skill in new[] { "subtitle", "rough", "none" })
                yield return [protocol, skill];
    }

    // Request contract, both wire protocols, real AskAsync, real Kestrel on 127.0.0.1:{port}, guard refuses everything else.
    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task AskAsync_SendsSkillAsSystemThenUnchangedUser_OrSingleUserWithoutSkill(string protocol, string skill)
    {
        await using var server = new LoopbackAiServer();
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard);
        var previous = Environment.GetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL");
        const string prompt = "原阶段 user 提示：只输出 JSON {\"events\":[]}";
        string? system = skill switch { "subtitle" => AiSkillText.SubtitleAnalysisSystemPrompt, "rough" => AiSkillText.RoughCutSystemPrompt, _ => null };
        try
        {
            AiConnection connection;
            if (protocol == "ollama")
            {
                Environment.SetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL", server.Origin + "/");
                connection = new AiConnection("ollama", "http://ignored.invalid/v1", "model-x", null);
            }
            else connection = new AiConnection("compatible", server.Origin + "/v1", "model-x", "fixture-key");

            var reply = await AiAnalysisService.AskAsync(connection, prompt, default, http, system);

            Assert.Contains("events", reply, StringComparison.Ordinal);
            var sent = Assert.Single(server.Requests, r => r.Method == "POST");
            Assert.Equal(protocol == "ollama" ? "/api/chat" : "/v1/chat/completions", sent.Path);
            Assert.Equal("model-x", sent.Model);
            Assert.Equal(protocol == "ollama" ? null : "Bearer fixture-key", sent.Authorization);
            var messages = sent.Messages();
            if (system is null)
                Assert.Equal(new[] { ("user", prompt) }, messages);
            else
                Assert.Equal(new[] { ("system", system), ("user", prompt) }, messages);
            Assert.All(server.Requests, r => Assert.True(r.Method == "POST" || (protocol == "ollama" && r.Path == "/api/version")));
            Assert.Equal(0, guard.Blocked);
        }
        finally { Environment.SetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL", previous); }
    }

    [Fact]
    public async Task AskAsync_EmptySystemPrompt_IsTreatedAsNoSkill()
    {
        await using var server = new LoopbackAiServer();
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard);
        await AiAnalysisService.AskAsync(new AiConnection("compatible", server.Origin + "/v1", "m", "k"), "p", default, http, "");
        Assert.Equal(new[] { ("user", "p") }, Assert.Single(server.Requests).Messages());
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public async Task AskAsync_DiagnosticsContainMilestonesAndNoSensitiveContent()
    {
        await using var server = new LoopbackAiServer { LoadDurationNs = 123456, ReplyContent = "response-secret-r31" };
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard);
        var previousUrl = Environment.GetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL");
        var previousOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL", server.Origin + "/");
            Console.SetOut(output);
            var result = await AiAnalysisService.AskAsync(new AiConnection("ollama", "ignored", "private-model-name", "private-key"), "prompt-secret-r31", default, http, "system-secret-r31");
            Assert.Equal("response-secret-r31", result);
        }
        finally { Console.SetOut(previousOut); Environment.SetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL", previousUrl); }
        var log = output.ToString();
        foreach (var stage in new[] { "request_started", "runtime_ready", "request_sent", "response_received", "ollama_timings", "completed" }) Assert.Contains(stage, log);
        Assert.Contains("123456", log);
        foreach (var secret in new[] { "private-model-name", "private-key", "prompt-secret-r31", "system-secret-r31", "response-secret-r31" }) Assert.DoesNotContain(secret, log);
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => x.StartsWith("ai-request ")).ToArray();
        var ids = lines.Select(x => System.Text.Json.JsonDocument.Parse(x[11..]).RootElement.GetProperty("requestId").GetString()).Distinct().ToArray();
        Assert.Single(ids);
        Assert.Single(server.Requests, r => r.Method == "POST");
        Assert.Equal(0, guard.Blocked);
    }

    [Theory]
    [InlineData(false, "timeout")]
    [InlineData(true, "caller_cancelled")]
    public async Task AskAsync_DiagnosticsDistinguishTimeoutFromCancellationWithoutRetry(bool callerCancel, string expectedKind)
    {
        await using var server = new LoopbackAiServer { ResponseDelayMilliseconds = 1500 };
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard) { Timeout = TimeSpan.FromMilliseconds(callerCancel ? 5000 : 200) };
        using var stop = new CancellationTokenSource();
        if (callerCancel) stop.CancelAfter(200);
        var previousOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AiAnalysisService.AskAsync(new AiConnection("compatible", server.Origin + "/v1", "m", "test-secret-r31"), "prompt", stop.Token, http));
        }
        finally { Console.SetOut(previousOut); }
        Assert.Contains(expectedKind, output.ToString());
        Assert.Contains("awaiting_response", output.ToString());
        Assert.DoesNotContain("test-secret-r31", output.ToString());
        Assert.Single(server.Requests, r => r.Method == "POST");
        Assert.Equal(0, guard.Blocked);
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task AskAsync_CompatibleUsageIsMetadataOnlyAndDoesNotInventMissingValues(string mode)
    {
        await using var server=new LoopbackAiServer { ReplyContent="private-response-r34", Usage=mode switch {
            "valid" => new {prompt_tokens=120,completion_tokens=30,total_tokens=150,prompt_cache_hit_tokens=80,prompt_cache_miss_tokens=40,private_extra="private-usage-r34"},
            "malformed" => (object)new {prompt_tokens=-1,completion_tokens="30",total_tokens=1.5},
            _ => null } };
        using var guard=new LoopbackOnlyHandler(server.Port);
        using var http=new HttpClient(guard);
        var previous=Console.Out;
        using var output=new StringWriter();
        try {
            Console.SetOut(output);
            Assert.Equal("private-response-r34",await AiAnalysisService.AskAsync(new AiConnection("compatible",server.Origin+"/v1","private-model-r34","private-key-r34"),"private-prompt-r34",default,http));
        } finally {Console.SetOut(previous);}
        var log=output.ToString();
        var records=log.Split('\n',StringSplitOptions.RemoveEmptyEntries).Where(x=>x.StartsWith("ai-request ")).Select(x=>System.Text.Json.JsonDocument.Parse(x[11..]).RootElement.Clone()).ToArray();
        var usage=Assert.Single(records,x=>x.GetProperty("stage").GetString()=="compatible_usage").GetProperty("detail");
        Assert.Equal(mode!="missing",usage.GetProperty("available").GetBoolean());
        if(mode=="valid") {
            Assert.Equal(120,usage.GetProperty("promptTokens").GetInt64());
            Assert.Equal(30,usage.GetProperty("completionTokens").GetInt64());
            Assert.Equal(150,usage.GetProperty("totalTokens").GetInt64());
            Assert.Equal(80,usage.GetProperty("promptCacheHitTokens").GetInt64());
            Assert.Equal(40,usage.GetProperty("promptCacheMissTokens").GetInt64());
        } else foreach(var field in new[]{"promptTokens","completionTokens","totalTokens","promptCacheHitTokens","promptCacheMissTokens"}) Assert.Equal(System.Text.Json.JsonValueKind.Null,usage.GetProperty(field).ValueKind);
        foreach(var secret in new[]{"private-response-r34","private-usage-r34","private-model-r34","private-key-r34","private-prompt-r34"}) Assert.DoesNotContain(secret,log);
        Assert.Single(records.Select(x=>x.GetProperty("requestId").GetString()).Distinct());
        Assert.Single(server.Requests);
        Assert.Equal(0,guard.Blocked);
    }

}
