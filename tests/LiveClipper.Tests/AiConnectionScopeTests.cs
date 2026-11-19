using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace LiveClipper.Tests;

public sealed class AiConnectionScopeTests
{
    private const string SavedKey = "s-conn-fixture-key";
    private const string SavedEndpoint = "https://api.openai.com/v1";
    private const string SavedModel = "saved-model";

    private static LiveSettings Settings() => new()
    {
        AiProvider = "openai",
        AiEndpoint = SavedEndpoint,
        AiModel = SavedModel,
        AiApiKey = CookieProtector.Protect(SavedKey)
    };

    [Fact]
    public void DifferentEndpoint_DoesNotReuseSavedKey()
    {
        var requested = new AiConnection("openai", "https://evil.example/v1", "x", null);

        var resolved = Resolve(requested, Settings());

        Assert.Null(resolved.ApiKey);
        Assert.Same(requested, resolved);
    }

    [Fact]
    public void EquivalentEndpoint_ReusesKeyAndKeepsRequestedEndpointAndModel()
    {
        var settings = Settings();
        var requested = new AiConnection("openai", "  https://API.OPENAI.COM/v1/  ", "别的", null);

        var resolved = Resolve(requested, settings);

        Assert.Equal(SavedKey, resolved.ApiKey);
        Assert.Equal(requested.Endpoint, resolved.Endpoint);
        Assert.Equal("别的", resolved.Model);
    }

    [Fact]
    public void SameNondefaultPortEndpoint_ReusesKeyAndKeepsRequestedEndpointAndModel()
    {
        var settings = Settings();
        settings.AiEndpoint = "http://127.0.0.1:8787/v1";
        var requested = new AiConnection("openai", settings.AiEndpoint, "request-model", null);

        var resolved = Resolve(requested, settings);

        Assert.Equal(SavedKey, resolved.ApiKey);
        Assert.Equal(requested.Endpoint, resolved.Endpoint);
        Assert.Equal("request-model", resolved.Model);
    }

    [Fact]
    public void DifferentNondefaultPortEndpoint_DoesNotReuseSavedKey()
    {
        var settings = Settings();
        settings.AiEndpoint = "http://127.0.0.1:8787/v1";
        var requested = new AiConnection("openai", "http://127.0.0.1:9999/v1", "request-model", null);

        var resolved = Resolve(requested, settings);

        Assert.Null(resolved.ApiKey);
        Assert.Equal(requested.Endpoint, resolved.Endpoint);
        Assert.NotEqual(settings.AiEndpoint, resolved.Endpoint);
    }

    [Fact]
    public void TrailingSlashEndpoint_ReusesKeyAndKeepsRequestedEndpointAndModel()
    {
        var settings = Settings();
        settings.AiEndpoint = "https://api.example.com/v1";
        var requested = new AiConnection("openai", "https://api.example.com/v1/", "request-model", null);

        var resolved = Resolve(requested, settings);

        Assert.Equal(SavedKey, resolved.ApiKey);
        Assert.Equal("https://api.example.com/v1/", resolved.Endpoint);
        Assert.Equal("request-model", resolved.Model);
    }

    [Fact]
    public void ExplicitApiKey_PreservesEntireRequest()
    {
        var requested = new AiConnection("openai", "https://evil.example/v1", "request-model", "explicit-fixture-key");

        var resolved = Resolve(requested, Settings());

        Assert.Same(requested, resolved);
        Assert.Equal("explicit-fixture-key", resolved.ApiKey);
        Assert.Equal("https://evil.example/v1", resolved.Endpoint);
        Assert.Equal("request-model", resolved.Model);
    }

    [Theory]
    [InlineData("http://api.openai.com/v1")]
    [InlineData("https://user@api.openai.com/v1")]
    [InlineData("https://api.openai.com:8443/v1")]
    [InlineData("https://api.openai.com/v2")]
    [InlineData("https://api.openai.com/v1?target=other")]
    [InlineData("not-a-url")]
    public void DifferentOrInvalidUrl_DoesNotReuseSavedKey(string endpoint)
    {
        var requested = new AiConnection("openai", endpoint, "x", null);

        var resolved = Resolve(requested, Settings());

        Assert.Null(resolved.ApiKey);
        Assert.Same(requested, resolved);
    }

    [Theory]
    [InlineData("https://user@api.openai.com/v1")]
    [InlineData("not-a-url")]
    public void UserInfoOrInvalidSavedUrl_IsNeverEligible(string endpoint)
    {
        var settings = Settings();
        settings.AiEndpoint = endpoint;
        var requested = new AiConnection("openai", endpoint, "x", null);

        var resolved = Resolve(requested, settings);

        Assert.Null(resolved.ApiKey);
        Assert.Same(requested, resolved);
    }

    [Theory]
    [InlineData("https://api.openai.com:443/v1")]
    [InlineData("https://API.OPENAI.COM/v1///")]
    public void DefaultPortAndNormalizedPath_ReuseKeyAndKeepRequest(string endpoint)
    {
        var requested = new AiConnection("OPENAI", endpoint, "  other-model/  ", " ");

        var resolved = Resolve(requested, Settings());

        Assert.Equal(SavedKey, resolved.ApiKey);
        Assert.Equal(endpoint, resolved.Endpoint);
        Assert.Equal("  other-model/  ", resolved.Model);
    }

    [Theory]
    [InlineData("https://API.OPENAI.COM/V1///")]
    [InlineData("https://api.openai.com/V1")]
    public void PathCaseDifference_DoesNotReuseSavedKey(string endpoint)
    {
        var requested = new AiConnection("openai", endpoint, "x", null);

        var resolved = Resolve(requested, Settings());

        Assert.Null(resolved.ApiKey);
        Assert.Same(requested, resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void IncompleteSavedConfig_SavedModelBlank_DoesNotReuseKey(string savedModel)
    {
        var settings = Settings();
        settings.AiModel = savedModel;
        var requested = new AiConnection("openai", SavedEndpoint, "request-model", null);

        var resolved = Resolve(requested, settings);

        Assert.Null(resolved.ApiKey);
        Assert.Same(requested, resolved);
    }

    [Fact]
    public void EmptyRequestedModel_StillReusesKey_ButIsNotInvented()
    {
        var resolved = Resolve(new AiConnection("openai", SavedEndpoint, "", null), Settings());

        Assert.Equal(SavedKey, resolved.ApiKey);
        Assert.Equal("", resolved.Model);
    }

    // D56：本地推理端点（LM Studio :1234、Ollama :11434、自建网关 :8787）几乎都在非默认端口上。
    // 端口不参与安全判定——判据是"归一化后与存档值完全相等"；只补 Key，Endpoint/Model 保持请求原值。
    [Fact]
    public void NonDefaultPortMatchingSavedEndpoint_ReusesSavedKey()
    {
        var settings = Settings();
        settings.AiEndpoint = "http://127.0.0.1:1234/v1";
        var resolved = Resolve(new AiConnection("openai", "http://127.0.0.1:1234/v1/", "x", null), settings);

        Assert.Equal(SavedKey, resolved.ApiKey);
        Assert.Equal("http://127.0.0.1:1234/v1/", resolved.Endpoint);
    }

    [Fact]
    public void DifferentPort_DoesNotReuseSavedKey()
    {
        var settings = Settings();
        settings.AiEndpoint = "http://127.0.0.1:1234/v1";
        var requested = new AiConnection("openai", "http://127.0.0.1:4321/v1", "x", null);

        var resolved = Resolve(requested, settings);

        Assert.Null(resolved.ApiKey);
        Assert.Same(requested, resolved);
    }

    [Fact]
    public void DifferentProvider_PreservesRequestWithoutSavedKey()
    {
        var requested = new AiConnection("other-provider", SavedEndpoint, SavedModel, null);

        var resolved = Resolve(requested, Settings());

        Assert.Null(resolved.ApiKey);
        Assert.Same(requested, resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("dpapi:not-base64")]
    public void MissingOrUnreadableSavedKey_PreservesRequest(string storedKey)
    {
        var settings = Settings();
        settings.AiApiKey = storedKey;
        var requested = new AiConnection("openai", SavedEndpoint, "request-model", null);

        var resolved = Resolve(requested, settings);

        Assert.Null(resolved.ApiKey);
        Assert.Same(requested, resolved);
    }

    [Theory]
    [InlineData("cut-plan")]
    [InlineData("fine-plan")]
    public async Task PlanningRoutes_DifferentEndpoint_DoesNotUseSavedKey(string route)
    {
        await using var saved = new LoopbackAiServer();
        await using var other = new LoopbackAiServer();
        using var guard = new LoopbackOnlyHandler(saved.Port, other.Port);
        using var http = new HttpClient(guard);
        await using var fixture = new PlanningFixture(http);
        fixture.Settings.AiEndpoint = $"{saved.Origin}/v1";
        var requested = new AiConnection("compatible", $"{other.Origin}/v1", "request-model", null);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.PostAsync(route, requested));

        Assert.Equal("请输入 API Key", error.Message);
        Assert.Empty(fixture.Studio.Events("clip-1"));
        Assert.Empty(saved.Requests);
        Assert.Empty(other.Requests);
        Assert.Equal(0, guard.Blocked);
    }

    [Theory]
    [InlineData("cut-plan")]
    [InlineData("fine-plan")]
    public async Task PlanningRoutes_IncompleteSavedConfig_DoesNotBorrowKey(string route)
    {
        await using var server = new LoopbackAiServer();
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard);
        await using var fixture = new PlanningFixture(http);
        fixture.Settings.AiEndpoint = $"{server.Origin}/v1";
        fixture.Settings.AiModel = "";
        var requested = new AiConnection("compatible", $"  {server.Origin}/v1/  ", "request-model", null);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.PostAsync(route, requested));

        Assert.Equal("请输入 API Key", error.Message);
        Assert.Empty(fixture.Studio.Events("clip-1"));
        Assert.Empty(server.Requests);
        Assert.Equal(0, guard.Blocked);
    }

    [Theory]
    [InlineData("cut-plan")]
    [InlineData("fine-plan")]
    public async Task PlanningRoutes_EquivalentEndpoint_SendsRequestedModelWithSavedKeyThroughInjectedTransport(string route)
    {
        await using var server = new LoopbackAiServer();
        server.ReplyContent = route == "cut-plan"
            ? """{"title":"话题","summary":"简介","start":0,"end":4,"keep":[0],"hooks":[0],"reasons":["完整"]}"""
            : """{"effects":[{"id":"source","zoom":1.2,"filter":"vivid","motion":"push","stickerAssetId":null,"soundAssetId":null}]}""";
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard);
        await using var fixture = new PlanningFixture(http);
        fixture.Settings.AiEndpoint = $"{server.Origin}/v1";
        var requested = new AiConnection("compatible", $"  {server.Origin}/v1/  ", "request-model", null);

        var result = await fixture.PostAsync(route, requested);

        Assert.Equal("ai", result.GetProperty("source").GetString());
        if (route == "cut-plan")
            Assert.Equal(0, result.GetProperty("keep")[0].GetInt32());
        else
        {
            var item = result.GetProperty("timeline")[0];
            Assert.Equal(1.2, item.GetProperty("zoom").GetDouble());
            Assert.Equal("vivid", item.GetProperty("filter").GetString());
            Assert.Equal("push", item.GetProperty("motion").GetString());
        }
        var sent = Assert.Single(server.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/v1/chat/completions", sent.Path);
        Assert.Equal("request-model", sent.Model);
        Assert.True(sent.Authorization == "Bearer " + SavedKey, "authorization header did not match saved key");
        // Request contract: explicit-connection planning sends the rough-cut skill as system, original stage prompt as the only user message.
        var messages = sent.Messages();
        Assert.Equal(2, messages.Count);
        Assert.Equal(("system", AiSkillText.RoughCutSystemPrompt), messages[0]);
        Assert.Equal("user", messages[1].Role);
        Assert.DoesNotContain(AiSkillText.RoughCutSystemPrompt, messages[1].Content, StringComparison.Ordinal);
        Assert.NotEqual(AiSkillText.SubtitleAnalysisSystemPrompt, messages[0].Content);
        Assert.Contains(route == "cut-plan" ? "你是直播短视频剪辑师" : "你是精剪包装师", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains(route == "cut-plan" ? "\"keep\":[保留的编号],\"hooks\":[钩子编号]" : "{\"effects\":[{\"id\":\"已有id\"", messages[1].Content, StringComparison.Ordinal);
        Assert.Equal(0, guard.Blocked);
    }

    private static LiveSettings LoopbackSettings(int port)
    {
        var settings = Settings();
        settings.AiProvider = "compatible";
        settings.AiEndpoint = $"http://127.0.0.1:{port}/v1";
        return settings;
    }

    private sealed class AnalyzeRig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-s-conn-" + Guid.NewGuid().ToString("N"));
        public Clip Clip { get; }
        public AiAnalysisService Service { get; }
        public StudioStore Studio { get; }

        public AnalyzeRig(HttpClient transport)
        {
            Directory.CreateDirectory(_root);
            var store = new Store(Path.Combine(_root, "test.db"));
            store.Migrate();
            Studio = new StudioStore(store.DatabasePath);
            Studio.Migrate();
            Clip = new Clip("clip-1", "event-1", Path.Combine(_root, "source.mp4"), null, DateTimeOffset.UtcNow);
            store.SaveClip(Clip);
            store.SaveTranscript("event-1", """
                {"language":"zh","segments":[{"start":0,"end":4,"text":"为什么这个片段精彩？"}]}
                """, Path.Combine(_root, "source.srt"));
            Service = new AiAnalysisService(store, Studio, null, transport);
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Loopback_EquivalentEndpoint_SendsRequestedModelWithSavedKey()
    {
        await using var server = new LoopbackAiServer();
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard);
        await using var rig = new AnalyzeRig(http);
        var settings = LoopbackSettings(server.Port);
        var requested = new AiConnection("compatible", $"  {server.Origin}/v1/  ", "request-model", null);

        var resolved = Resolve(requested, settings);
        var result = await rig.Service.AnalyzeAsync(rig.Clip, resolved, default);

        Assert.NotEmpty(result.Events);
        var sent = server.Requests;
        Assert.NotEmpty(sent);
        Assert.All(sent, r =>
        {
            Assert.Equal("POST", r.Method);
            Assert.Equal("/v1/chat/completions", r.Path);
            Assert.Equal("request-model", r.Model);
            Assert.True(r.Authorization == "Bearer " + SavedKey, "authorization header did not match saved key");
            var messages = r.Messages();
            Assert.Equal(2, messages.Count);
            Assert.Equal(("system", AiSkillText.SubtitleAnalysisSystemPrompt), messages[0]);
            Assert.Equal("user", messages[1].Role);
            Assert.DoesNotContain(AiSkillText.SubtitleAnalysisSystemPrompt, messages[1].Content, StringComparison.Ordinal);
        });
        // Analyze path (no pipeline): events stage then advice stage, each user prompt keeps its own schema.
        Assert.Equal(2, sent.Count);
        Assert.Contains("{\"events\":[{\"topic\":\"话题主题\"", sent[0].Messages()[1].Content, StringComparison.Ordinal);
        Assert.Contains("{\"summary\":\"...\",\"captions\":[\"...\"],\"tips\":[\"...\"]}", sent[1].Messages()[1].Content, StringComparison.Ordinal);
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public async Task Loopback_ExplicitKey_IsSentInsteadOfSavedKey()
    {
        await using var server = new LoopbackAiServer();
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard);
        await using var rig = new AnalyzeRig(http);
        var requested = new AiConnection("compatible", $"{server.Origin}/v1", "request-model", "explicit-fixture-key");

        var resolved = Resolve(requested, LoopbackSettings(server.Port));
        await rig.Service.AnalyzeAsync(rig.Clip, resolved, default);

        Assert.NotEmpty(server.Requests);
        Assert.All(server.Requests, r => Assert.True(r.Authorization == "Bearer explicit-fixture-key"));
    }

    [Theory]
    [InlineData("path-case")]
    [InlineData("other-port")]
    [InlineData("incomplete-saved")]
    [InlineData("empty-requested-model")]
    public async Task Loopback_NegativeCases_SendZeroRequests(string kind)
    {
        await using var saved = new LoopbackAiServer();
        await using var other = new LoopbackAiServer();
        using var guard = new LoopbackOnlyHandler(saved.Port, other.Port);
        using var http = new HttpClient(guard);
        await using var rig = new AnalyzeRig(http);
        var settings = LoopbackSettings(saved.Port);
        var requested = kind switch
        {
            "path-case" => new AiConnection("compatible", $"{saved.Origin}/V1", "request-model", null),
            "other-port" => new AiConnection("compatible", $"{other.Origin}/v1", "request-model", null),
            "incomplete-saved" => new AiConnection("compatible", $"{saved.Origin}/v1", "request-model", null),
            _ => new AiConnection("compatible", $"{saved.Origin}/v1", "", null)
        };
        if (kind == "incomplete-saved") settings.AiModel = "";

        var resolved = Resolve(requested, settings);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.AnalyzeAsync(rig.Clip, resolved, default));

        Assert.Equal(kind == "empty-requested-model" ? "请选择 AI 模型" : "请输入 API Key", error.Message);
        Assert.Empty(saved.Requests);
        Assert.Empty(other.Requests);
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public async Task Loopback_Redirect_IsNotFollowed()
    {
        await using var target = new LoopbackAiServer();
        await using var origin = new LoopbackAiServer();
        origin.RedirectTo = target.Origin + "/v1/chat/completions";
        using var guard = new LoopbackOnlyHandler(origin.Port, target.Port);
        using var http = new HttpClient(guard);
        await using var rig = new AnalyzeRig(http);
        var requested = new AiConnection("compatible", $"{origin.Origin}/v1", "request-model", null);

        var resolved = Resolve(requested, LoopbackSettings(origin.Port));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.AnalyzeAsync(rig.Clip, resolved, default));

        Assert.Contains("302", error.Message);
        Assert.NotEmpty(origin.Requests);
        Assert.Empty(target.Requests);
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public async Task Loopback_Guard_RefusesNonFixtureTargets_WithoutSending()
    {
        await using var server = new LoopbackAiServer();
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard);

        foreach (var url in new[] { "https://api.openai.com/v1/models", "http://localhost:" + server.Port + "/v1/models", "http://127.0.0.1:" + (server.Port + 1) + "/v1/models", "http://[::1]:" + server.Port + "/" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync(url));

        Assert.Equal(4, guard.Blocked);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("cut-plan")]
    [InlineData("fine-plan")]
    public async Task PlanningRoutes_NullConnection_PreservesRulesMode(string route)
    {
        await using var server = new LoopbackAiServer();
        using var guard = new LoopbackOnlyHandler(server.Port);
        using var http = new HttpClient(guard);
        await using var fixture = new PlanningFixture(http);

        var result = await fixture.PostAsync(route, null);

        Assert.Equal("rules", result.GetProperty("source").GetString());
        Assert.Empty(server.Requests);
        Assert.Equal(0, guard.Blocked);
    }

    private static AiConnection Resolve(AiConnection requested, LiveSettings settings) =>
        AiConnectionResolver.Resolve(requested, settings);

    private sealed class PlanningFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-s-conn-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;
        public LiveSettings Settings { get; } = AiConnectionScopeTests.Settings();
        public StudioStore Studio { get; }

        public PlanningFixture(HttpClient transport)
        {
            Directory.CreateDirectory(_root);
            var store = new Store(Path.Combine(_root, "test.db"));
            store.Migrate();
            Studio = new StudioStore(store.DatabasePath);
            Studio.Migrate();
            store.SaveClip(new Clip("clip-1", "event-1", Path.Combine(_root, "source.mp4"), null, DateTimeOffset.UtcNow));
            store.SaveTranscript("event-1", """
                {"language":"zh","segments":[{"start":0,"end":4,"text":"为什么这个片段精彩？"}]}
                """, Path.Combine(_root, "source.srt"));
            Settings.AiProvider = "compatible";
            Settings.StorageRoot = _root;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            AdvancedStudioEndpoints.Map(_app, store, Studio, null!, null!, null!, Settings, null!, transport);
        }

        public async Task<JsonElement> PostAsync(string route, AiConnection? connection)
        {
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources
                .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == "/api/studio/clips/{id}/" + route);
            Assert.Equal(new[] { "POST" }, endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            object payload = route == "cut-plan"
                ? new CutPlanRequest(0, 4, connection)
                : new FinePlanRequest(new EditorProject { Start = 0, End = 4 }, connection);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());
            context.Request.Method = "POST";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.RouteValues["id"] = "clip-1";
            await using var input = new MemoryStream(bytes);
            context.Request.Body = input;
            await using var output = new MemoryStream();
            context.Response.Body = output;
            await endpoint.RequestDelegate!(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            output.Position = 0;
            using var json = await JsonDocument.ParseAsync(output);
            return json.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }

    private sealed class BodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
