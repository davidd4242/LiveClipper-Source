using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LiveClipper.Tests;

public sealed class RankingStaleKeyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Get_ChangedCaptionOverrideReturnsNotFoundWithoutDeletingRanking()
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [new(1, 2, "分析时的覆盖字幕")] } });
        var cached = f.SaveRanking(f.Effective().CacheKey);
        Assert.Equal(200, (await f.GetAsync()).Status);
        f.SaveProject(f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [new(1, 2, "编辑后的覆盖字幕")] } });
        var project = f.Studio.Project(f.Clip.Id);

        var response = await f.GetAsync();

        Assert.Equal(404, response.Status);
        Assert.Equal("字幕已更新，请重新分析", response.Body.GetProperty("error").GetString());
        Assert.Equal(cached, f.Studio.Document("clip-ranking", f.Clip.Id));
        Assert.Equal(project, f.Studio.Project(f.Clip.Id));
    }

    [Fact]
    public async Task Get_UnchangedEffectiveOverrideStillHitsAfterRawAndFrozenChanges()
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [new(1, 2, "保持不变的覆盖字幕")] } });
        var key = f.Effective().CacheKey;
        f.SaveRanking(key);
        Assert.Equal(200, (await f.GetAsync()).Status);
        f.Transcript(new TranscriptSegment(0, 4, "更新后的 ASR 不应绕过覆盖"));
        f.SaveProject(f.Project with { SourceTranscripts = new() { [f.Clip.Id] = [new(0, 4, "更新后的冻结字幕")] } });

        var response = await f.GetAsync();

        Assert.Equal(200, response.Status);
        Assert.Equal(key, response.Body.GetProperty("TranscriptCacheKey").GetString());
        Assert.Equal("cached candidate", Assert.Single(response.Body.GetProperty("Candidates").EnumerateArray()).GetProperty("Title").GetString());
    }

    [Fact]
    public async Task Get_ChangedOriginalWithoutOverrideReturnsNotFound()
    {
        await using var f = new Fixture();
        f.SaveRanking(f.Effective().CacheKey);
        Assert.Equal(200, (await f.GetAsync()).Status);
        f.Transcript(new TranscriptSegment(0, 4, "真正改变的原始字幕"));

        var response = await f.GetAsync();

        Assert.Equal(404, response.Status);
        Assert.Equal("字幕已更新，请重新分析", response.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_LegacyRankingWithoutKeyRemainsReadableAndPreservesPayload()
    {
        await using var f = new Fixture();
        var cached = JsonSerializer.Serialize(new
        {
            ClipId = f.Clip.Id,
            Model = "legacy-model",
            Candidates = new[] { Fixture.Candidate },
            DanmakuSource = (string?)null,
            UpdatedAt = DateTimeOffset.UtcNow,
            SkillVersion = AiSkillText.SubtitleAnalysisVersion,
            FutureField = "preserve-legacy-payload"
        });
        f.Studio.SaveDocument("clip-ranking", f.Clip.Id, cached);
        using (var stored = JsonDocument.Parse(cached))
            Assert.False(stored.RootElement.TryGetProperty("TranscriptCacheKey", out _));
        f.SaveProject(f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [new(1, 2, "后续编辑的覆盖字幕")] } });

        var response = await f.GetAsync();

        Assert.Equal(200, response.Status);
        Assert.False(response.Body.TryGetProperty("TranscriptCacheKey", out _));
        Assert.Equal("preserve-legacy-payload", response.Body.GetProperty("FutureField").GetString());
        Assert.Equal("cached candidate", Assert.Single(response.Body.GetProperty("Candidates").EnumerateArray()).GetProperty("Title").GetString());
        Assert.Equal(cached, f.Studio.Document("clip-ranking", f.Clip.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("subtitle-analysis@sha256:0000stale")]
    public async Task Get_RankingWithMissingOrOldSkillVersionReturnsNotFoundAndKeepsPayload(string? skillVersion)
    {
        await using var f = new Fixture();
        var cached = f.SaveRanking(f.Effective().CacheKey, skillVersion);

        var response = await f.GetAsync();

        Assert.Equal(404, response.Status);
        Assert.Equal("分析技能已更新，请重新分析", response.Body.GetProperty("error").GetString());
        Assert.Equal(cached, f.Studio.Document("clip-ranking", f.Clip.Id));
        f.SaveRanking(f.Effective().CacheKey);
        Assert.Equal(200, (await f.GetAsync()).Status);
    }

    [Fact]
    public async Task Get_LegacyRankingWithoutSkillVersionIsNotServedButNotDeleted()
    {
        await using var f = new Fixture();
        var cached = JsonSerializer.Serialize(new
        {
            ClipId = f.Clip.Id, Model = "legacy-model", Candidates = new[] { Fixture.Candidate },
            DanmakuSource = (string?)null, UpdatedAt = DateTimeOffset.UtcNow, FutureField = "preserve-legacy-payload"
        });
        f.Studio.SaveDocument("clip-ranking", f.Clip.Id, cached);

        var response = await f.GetAsync();

        Assert.Equal(404, response.Status);
        Assert.Equal("分析技能已更新，请重新分析", response.Body.GetProperty("error").GetString());
        Assert.Equal(cached, f.Studio.Document("clip-ranking", f.Clip.Id));
    }

    [Fact]
    public async Task Get_NullOptionalKeyRemainsReadable()
    {
        await using var f = new Fixture();
        f.SaveRanking(null);
        f.SaveProject(f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [] } });

        var response = await f.GetAsync();

        Assert.Equal(200, response.Status);
        Assert.Equal(JsonValueKind.Null, response.Body.GetProperty("TranscriptCacheKey").ValueKind);
        Assert.Single(response.Body.GetProperty("Candidates").EnumerateArray());
    }

    [Fact]
    public async Task Get_ExplicitEmptyOverrideInvalidatesOriginalKeyInsteadOfFallingBack()
    {
        await using var f = new Fixture();
        var originalKey = f.Effective().CacheKey;
        f.SaveRanking(originalKey);
        Assert.Equal(200, (await f.GetAsync()).Status);
        f.SaveProject(f.Project with
        {
            CaptionOverrides = new() { [f.Clip.Id] = [] },
            SourceTranscripts = new() { [f.Clip.Id] = [new(0, 4, "不能回落到冻结字幕")] }
        });
        var empty = f.Effective();
        Assert.Empty(empty.Segments);
        Assert.Equal("override", empty.Source);
        Assert.NotEqual(originalKey, empty.CacheKey);

        var response = await f.GetAsync();

        Assert.Equal(404, response.Status);
        Assert.Equal("字幕已更新，请重新分析", response.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_UnchangedExplicitEmptyOverrideHitsUntilOverrideIsRemoved()
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [] } });
        var emptyKey = f.Effective().CacheKey;
        f.SaveRanking(emptyKey);
        Assert.Equal(200, (await f.GetAsync()).Status);
        f.Transcript(new TranscriptSegment(0, 4, "新的原始字幕仍不能替代空覆盖"));
        f.SaveProject(f.Project with { SourceTranscripts = new() { [f.Clip.Id] = [new(0, 4, "新的冻结字幕")] } });

        var hit = await f.GetAsync();

        Assert.Equal(200, hit.Status);
        Assert.Equal(emptyKey, hit.Body.GetProperty("TranscriptCacheKey").GetString());
        f.SaveProject(f.Project with { CaptionOverrides = null });
        Assert.Equal(404, (await f.GetAsync()).Status);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"TranscriptCacheKey\":42}")]
    public async Task Get_InvalidRankingReturnsNotFound(string cached)
    {
        await using var f = new Fixture();
        f.Studio.SaveDocument("clip-ranking", f.Clip.Id, cached);

        var response = await f.GetAsync();

        Assert.Equal(404, response.Status);
        Assert.Equal("排序结果已过期，请重新分析", response.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_MissingRankingReturnsNotFound()
    {
        await using var f = new Fixture();

        var response = await f.GetAsync();

        Assert.Equal(404, response.Status);
        Assert.Equal("还没有排序结果，请先运行 AI 分析", response.Body.GetProperty("error").GetString());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string CurrentSkill = "<current>";
        public static readonly RankedCandidate Candidate = new(0, 3, "cached candidate", "summary", "hook", .9, 90, "reason", "test");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-ranking-stale-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;
        public Store Store { get; }
        public StudioStore Studio { get; }
        public Clip Clip { get; }
        public EditorProject Project => JsonSerializer.Deserialize<EditorProject>(Studio.Project(Clip.Id)!, Web)!;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Store = new Store(Path.Combine(_root, "test.db"));
            Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath);
            Studio.Migrate();
            Clip = new Clip(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Path.Combine(_root, "source.mp4"), null, DateTimeOffset.UtcNow);
            Store.SaveClip(Clip);
            Transcript(new TranscriptSegment(0, 3, "原始字幕"));
            SaveProject(new EditorProject { Start = 0, End = 4, Timeline = [new("base", Clip.Id, 0, 4)] });
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            StudioEndpoints.Map(_app, Store, Studio, null!, null!, null!, null!, null!, null!, null!, new LiveSettings { StorageRoot = _root });
        }

        public void SaveProject(EditorProject project) => Studio.SaveProject(Clip.Id, JsonSerializer.Serialize(project));
        public void Transcript(params TranscriptSegment[] segments) => Store.SaveTranscript(Clip.EventId, JsonSerializer.Serialize(new { language = "zh", segments }, Web), Path.Combine(_root, "source.srt"));
        public EffectiveTranscriptResult Effective() => EffectiveTranscript.FromProjectJson(Studio.Project(Clip.Id), Clip.Id, Store.TranscriptJson(Clip.EventId));
        public string SaveRanking(string? key, string? skillVersion = CurrentSkill)
        {
            var json = JsonSerializer.Serialize(new ClipRanking(Clip.Id, "test-model", [Candidate], null, DateTimeOffset.UtcNow, key, skillVersion == CurrentSkill ? AiSkillText.SubtitleAnalysisVersion : skillVersion));
            Studio.SaveDocument("clip-ranking", Clip.Id, json);
            return json;
        }

        public async Task<(int Status, JsonElement Body)> GetAsync()
        {
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == "/api/studio/clips/{id}/ranking"
                    && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = "GET";
            context.Request.RouteValues["id"] = Clip.Id;
            await using var output = new MemoryStream();
            context.Response.Body = output;
            await endpoint.RequestDelegate!(context);
            output.Position = 0;
            using var json = await JsonDocument.ParseAsync(output);
            return (context.Response.StatusCode, json.RootElement.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
        }
    }
}
