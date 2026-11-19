using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LiveClipper.Tests;

public sealed class EffectiveTranscriptWiringTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AutoProposal_GenerateAndApplyHashTheSameEffectiveSnapshot()
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [new(1, 2, "覆盖后的重点")] } });
        var effective = EffectiveTranscript.Resolve(f.Project, f.Clip.Id, f.Store.TranscriptJson(f.Clip.EventId));

        var proposal = f.AutoEdit.GeneratePlan(f.Clip);

        Assert.Equal(effective.Digest.ToUpperInvariant(), proposal.TranscriptHash);
        var reason = Assert.Single(proposal.SegmentReasons);
        Assert.Equal(1, reason.Start);
        Assert.Equal(2, reason.End);
        Assert.True(reason.Keep);
        f.Transcript(f.Clip, new TranscriptSegment(0, 4, "ASR 已变化，但当前覆盖没有变化"));
        var applied = f.AutoEdit.ApplyProposal(f.Clip, proposal);
        Assert.True(applied.Applied, applied.Error);
        Assert.Equal("覆盖后的重点", Assert.Single(f.Project.CaptionOverrides![f.Clip.Id]).Text);
    }

    [Fact]
    public async Task AutoProposal_OriginalCanonicalContentRatherThanJsonFormattingDefinesHash()
    {
        await using var f = new Fixture();
        var proposal = f.AutoEdit.GeneratePlan(f.Clip);
        f.Store.SaveTranscript(f.Clip.EventId,
            """{ "ignored": 42, "language": "different", "segments": [ { "text": "原始平淡内容", "end": 1, "start": 0 }, { "text": "原始结论", "start": 2, "end": 3 } ] }""", f.SrtPath);

        Assert.True(f.AutoEdit.ApplyProposal(f.Clip, proposal).Applied);
        var next = f.AutoEdit.GeneratePlan(f.Clip);
        f.Transcript(f.Clip, new TranscriptSegment(0, 4, "真正不同的原始字幕"));
        var before = f.Studio.Project(f.Clip.Id);
        var rejected = f.AutoEdit.ApplyProposal(f.Clip, next);
        Assert.False(rejected.Applied);
        Assert.Equal("stale-transcript", rejected.Code);
        Assert.Equal(before, f.Studio.Project(f.Clip.Id));
    }

    [Fact]
    public async Task ExplicitEmpty_RejectsProposalAndCutWithoutFallingBackToRawOrFrozen()
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with
        {
            CaptionOverrides = new() { [f.Clip.Id] = [] },
            SourceTranscripts = new() { [f.Clip.Id] = [new(0, 4, "冻结字幕也不能回落")] }
        });
        var before = f.Studio.Project(f.Clip.Id);
        Assert.Throws<ArgumentException>(() => f.AutoEdit.GeneratePlan(f.Clip));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Planning.CutAsync(f.Clip, 0, 4, null, default));
        Assert.Equal(before, f.Studio.Project(f.Clip.Id));
        Assert.Null(f.Studio.Document("cut-suggestion", f.Clip.Id));
        f.AutoEdit.AutoAnalyze = false;
        f.AutoEdit.AutoTrimSilence = false;
        var report = await f.AutoEdit.RunAsync(f.Clip, default);
        Assert.Equal("empty", report.Status);
        Assert.Equal(before, f.Studio.Project(f.Clip.Id));
    }

    [Fact]
    public async Task LegacyAutoEdit_KeepsOriginalParsedSelectionIndicesWithoutCreatingAnOverride()
    {
        await using var f = new Fixture();
        f.Transcript(f.Clip, new TranscriptSegment(0, 0, "无效时间仍占原始下标"), new TranscriptSegment(1, 2, "有效的重点内容"));
        f.AutoEdit.AutoAnalyze = false;
        f.AutoEdit.AutoTrimSilence = false;

        var report = await f.AutoEdit.RunAsync(f.Clip, default);

        Assert.Equal("ready", report.Status);
        Assert.Equal(new[] { 1 }, report.Keep);
        Assert.Equal(new[] { 1 }, f.Project.SelectedSegments);
        Assert.False(EditorRenderService.HasCaptionOverride(f.Project, f.Clip.Id));
        var rendered = EditorRenderService.MapSubtitles(f.Project.Timeline!,
            _ => TranscriptExport.Parse(f.Store.TranscriptJson(f.Clip.EventId)!).Segments, f.Clip.Id, f.Project.SelectedSegments);
        Assert.Equal("有效的重点内容", Assert.Single(rendered).Text);
    }

    [Fact]
    public async Task Planning_CutBindsIndicesToSavedOrExplicitEffectiveSnapshot()
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [new(1, 2, "保存覆盖为什么精彩？")] } });
        var saved = await f.Planning.CutAsync(f.Clip, 0, 4, null, default);
        Assert.Contains("保存覆盖", saved.Title);
        Assert.Equal(new[] { 0 }, saved.Keep);
        Assert.Equal(EffectiveTranscript.Resolve(f.Project, f.Clip.Id, null).CacheKey, saved.TranscriptCacheKey);
        var explicitPlan = f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [new(2, 3, "显式工程的重点")] } };
        var explicitCut = await f.Planning.CutAsync(f.Clip, 0, 4, null, default, explicitPlan);
        Assert.Contains("显式工程", explicitCut.Title);
        Assert.NotEqual(saved.TranscriptCacheKey, explicitCut.TranscriptCacheKey);
        using var stored = JsonDocument.Parse(f.Studio.Document("cut-suggestion", f.Clip.Id)!);
        Assert.Equal(explicitCut.TranscriptCacheKey, stored.RootElement.GetProperty("TranscriptCacheKey").GetString());
    }

    [Fact]
    public async Task Planning_FineResolvesEachSourceAndNeverUsesFrozenRenderTranscripts()
    {
        await using var f = new Fixture();
        f.Transcript(f.Clip, new TranscriptSegment(0, 1, "原始主源竟然精彩！"));
        f.Transcript(f.Secondary, new TranscriptSegment(0, 1, "第二源竟然精彩！"));
        var plan = f.Project with
        {
            Timeline = [new("primary", f.Clip.Id, 0, 1), new("secondary", f.Secondary.Id, 0, 1)],
            CaptionOverrides = new() { [f.Clip.Id] = [] },
            SourceTranscripts = new() { [f.Clip.Id] = [new(0, 1, "冻结主源精彩！")], [f.Secondary.Id] = [new(0, 1, "冻结次源平淡")] }
        };
        var result = await f.Planning.FineAsync(f.Clip, plan, null, default);
        Assert.Equal(1, result.Timeline[0].Zoom);
        Assert.Equal(1.15, result.Timeline[1].Zoom);
        var emptySecondary = plan with { CaptionOverrides = new() { [f.Clip.Id] = [new(0, 1, "覆盖主源精彩！")], [f.Secondary.Id] = [] } };
        var changed = await f.Planning.FineAsync(f.Clip, emptySecondary, null, default);
        Assert.Equal(1.15, changed.Timeline[0].Zoom);
        Assert.Equal(1, changed.Timeline[1].Zoom);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("txt")]
    [InlineData("docx")]
    [InlineData("srt")]
    [InlineData("srt-direct")]
    public async Task Exports_AllReadTheSameEditedCaptionInsteadOfOriginalFiles(string format)
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with { CaptionOverrides = new() { [f.Clip.Id] = [new(1, 2, "EDITED-CAPTION")] } });
        var response = await f.ExportAsync(format);
        Assert.Equal(200, response.Status);
        var text = ExportText(response, format);
        Assert.Contains("EDITED-CAPTION", text);
        Assert.DoesNotContain("原始", text);
        Assert.DoesNotContain("ORIGINAL-SRT-MARKER", text);
        if (format is "srt" or "srt-direct") Assert.Contains("00:00:01,000 --> 00:00:02,000", text);
        if (format == "preview") Assert.Single(response.Json().GetProperty("segments").EnumerateArray());
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("txt")]
    [InlineData("docx")]
    [InlineData("srt")]
    [InlineData("srt-direct")]
    public async Task Exports_ExplicitEmptyIsEmptyAndDoesNotLeakRawOrFrozenCaptions(string format)
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with
        {
            CaptionOverrides = new() { [f.Clip.Id] = [] },
            SourceTranscripts = new() { [f.Clip.Id] = [new(0, 1, "FROZEN-CAPTION")] }
        });
        var response = await f.ExportAsync(format);
        Assert.Equal(200, response.Status);
        var text = ExportText(response, format);
        Assert.DoesNotContain("原始", text);
        Assert.DoesNotContain("FROZEN-CAPTION", text);
        Assert.DoesNotContain("ORIGINAL-SRT-MARKER", text);
        if (format == "docx") Assert.Empty(System.Xml.Linq.XDocument.Parse(text).Descendants(System.Xml.Linq.XName.Get("t", "http://schemas.openxmlformats.org/wordprocessingml/2006/main")));
        else Assert.Empty(text);
        if (format == "preview") Assert.Empty(response.Json().GetProperty("segments").EnumerateArray());
    }

    [Fact]
    public async Task Exports_OverrideWorksWithoutAsrOrAnSrtFile()
    {
        await using var f = new Fixture();
        var clip = f.AddClip();
        f.Studio.SaveProject(clip.Id, JsonSerializer.Serialize(new EditorProject
        {
            Start = 0, End = 4, CaptionOverrides = new() { [clip.Id] = [new(1, 2, "ONLY-OVERRIDE")] }
        }));
        var txt = await f.InvokeAsync("/api/clips/{id}/transcript/export", "GET", clip.Id, format: "txt");
        var srt = await f.InvokeAsync("/api/clips/{id}/transcript/export", "GET", clip.Id, format: "srt");
        Assert.Equal(200, txt.Status);
        Assert.Equal("ONLY-OVERRIDE", Encoding.UTF8.GetString(txt.Body));
        Assert.Equal(200, srt.Status);
        Assert.Contains("ONLY-OVERRIDE", Encoding.UTF8.GetString(srt.Body));
    }

    [Fact]
    public async Task Ducking_MultiSourceSourceSecondsBecomeSequenceAndItemOutputSeconds()
    {
        await using var f = new Fixture();
        f.Transcript(f.Clip, new TranscriptSegment(0, 1, "错误的原始时间"));
        f.Transcript(f.Secondary, new TranscriptSegment(21, 22, "第二源人声"));
        f.SaveProject(f.Project with
        {
            End = 24, MusicAssetId = f.MusicId, SelectedSegments = [999],
            Timeline = [new("a", f.Clip.Id, 10, 14, Speed: 2, GapBefore: 1, SoundAssetId: f.MusicId),
                new("b", f.Secondary.Id, 20, 24, GapBefore: 2, SoundAssetId: f.MusicId)],
            CaptionOverrides = new() { [f.Clip.Id] = [new(11, 13, "有效主源人声")] },
            SourceTranscripts = new() { [f.Secondary.Id] = [new(23, 24, "不能读取旧冻结字幕")] }
        });
        var response = await f.DuckAsync(new(-14.129, 100, 200));
        Assert.Equal(200, response.Status);
        var json = response.Json();
        Assert.True(json.GetProperty("saved").GetBoolean());
        var envelope = json.GetProperty("envelope");
        Assert.Equal(-14.13, envelope.GetProperty("targetDb").GetDouble(), 6);
        AssertVoices(envelope, (1.5, 2.5), (6, 7));
        var a = Assert.Single(f.Project.Timeline!, x => x.Id == "a");
        var b = Assert.Single(f.Project.Timeline!, x => x.Id == "b");
        Assert.Contains(a.VolumeKeyframes!, x => Math.Abs(x.Time - .5) < 1e-6 && Math.Abs(x.Db + 14.13) < 1e-6);
        Assert.Contains(b.VolumeKeyframes!, x => Math.Abs(x.Time - 1) < 1e-6 && Math.Abs(x.Db + 14.13) < 1e-6);
        Assert.All(a.VolumeKeyframes!, x => Assert.InRange(x.Time, 0, 2));
        Assert.All(b.VolumeKeyframes!, x => Assert.InRange(x.Time, 0, 4));
        Assert.Equal(Math.Pow(10, -14.13 / 20), EditorRenderService.VolumeGainAt(b, 1.5), 6);
        using var stored = JsonDocument.Parse(f.Studio.Document("music-ducking", f.Clip.Id)!);
        Assert.NotEmpty(stored.RootElement.GetProperty("transcriptCacheKey").GetString()!);
        Assert.NotEmpty(stored.RootElement.GetProperty("geometryKey").GetString()!);
        Assert.Equal(200, (await f.DuckGetAsync()).Status);
        // A raw-ASR change hidden by an unchanged override is not an effective change.
        f.Transcript(f.Clip, new TranscriptSegment(0, 1, "又一版原始字幕"));
        Assert.Equal(200, (await f.DuckGetAsync()).Status);
    }

    [Fact]
    public async Task Ducking_ExplicitEmptyDoesNotFallBackAndDoesNotWriteAnything()
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with
        {
            MusicAssetId = f.MusicId, Timeline = [new("a", f.Clip.Id, 0, 4, SoundAssetId: f.MusicId)],
            CaptionOverrides = new() { [f.Clip.Id] = [] },
            SourceTranscripts = new() { [f.Clip.Id] = [new(0, 4, "冻结的人声")] }
        });
        var before = f.Studio.GetProjectRecord(f.Clip.Id);
        var response = await f.DuckAsync();
        Assert.Equal(200, response.Status);
        Assert.False(response.Json().GetProperty("saved").GetBoolean());
        Assert.Equal(before, f.Studio.GetProjectRecord(f.Clip.Id));
        Assert.Null(f.Studio.Document("music-ducking", f.Clip.Id));
    }

    [Theory]
    [InlineData("caption")]
    [InlineData("geometry")]
    [InlineData("selection")]
    public async Task Ducking_GetRejectsStaleEffectiveCaptionOrGeometry(string change)
    {
        await using var f = new Fixture();
        f.SaveProject(f.Project with { MusicAssetId = f.MusicId, Timeline = [new("a", f.Clip.Id, 0, 4, SoundAssetId: f.MusicId)] });
        Assert.Equal(200, (await f.DuckAsync()).Status);
        Assert.Equal(200, (await f.DuckGetAsync()).Status);
        if (change == "caption") f.Transcript(f.Clip, new TranscriptSegment(1, 2, "当前字幕变化"));
        else f.SaveProject(change == "geometry" ? f.Project with { Timeline = [f.Project.Timeline![0] with { Speed = 2 }] }
            : f.Project with { SelectedSegments = [1] });
        Assert.Equal(404, (await f.DuckGetAsync()).Status);
    }

    [Fact]
    public async Task Ducking_OriginalSelectionAndFrozenRenderSnapshotKeepTheirOriginalParsedIndices()
    {
        await using var f = new Fixture();
        f.Transcript(f.Clip, new TranscriptSegment(0, 0, "无效时间但有原索引"), new(1, 2, "选中句"), new(2, 3, "未选句"));
        f.SaveProject(f.Project with
        {
            MusicAssetId = f.MusicId, SelectedSegments = [1],
            Timeline = [new("a", f.Clip.Id, 0, 4, SoundAssetId: f.MusicId)]
        });
        var renderer = new EditorRenderService(f.Store, f.Studio, f.Media, f.Root);
        var frozen = renderer.Snapshot(f.Clip, f.Project);
        Assert.Equal(3, frozen.SourceTranscripts![f.Clip.Id].Length);
        Assert.Equal(0, frozen.SourceTranscripts[f.Clip.Id][0].End);
        var response = await f.DuckAsync();
        Assert.Equal(200, response.Status);
        AssertVoices(response.Json().GetProperty("envelope"), (1, 2));
        f.Transcript(f.Clip, new TranscriptSegment(0, 4, "重转录不能改变已冻结的渲染"));
        var rendered = EditorRenderService.MapSubtitles(frozen.Timeline!, id => frozen.SourceTranscripts[id], f.Clip.Id, frozen.SelectedSegments);
        Assert.Equal("选中句", Assert.Single(rendered).Text);
        Assert.Equal(new[] { 1 }, frozen.SelectedSegments);
    }

    [Fact]
    public async Task Ducking_TransitionsAndParallelTracksUseRendererSequenceStarts()
    {
        await using var f = new Fixture();
        f.Transcript(f.Clip, new TranscriptSegment(1, 2, "主源"));
        f.Transcript(f.Secondary, new TranscriptSegment(1, 2, "次源"));
        f.SaveProject(f.Project with
        {
            End = 3, MusicAssetId = f.MusicId,
            Timeline = [new("a", f.Clip.Id, 0, 3, SoundAssetId: f.MusicId), new("b", f.Secondary.Id, 0, 3, SoundAssetId: f.MusicId),
                new("parallel", f.Clip.Id, 0, 2, GapBefore: 1, Track: 2)],
            Transitions = [new("cut", "b", 1, "cross-dissolve", .5, "center")]
        });
        var cuts = EditorRenderService.ResolveTransitions(f.Project.Timeline!, f.Project.Transitions, f.Project.Fps);
        Assert.Single(cuts);
        Assert.Equal(2.5, EditorRenderService.SequenceStarts(f.Project.Timeline!, cuts)["b"], 6);
        var response = await f.DuckAsync();
        Assert.Equal(200, response.Status);
        AssertVoices(response.Json().GetProperty("envelope"), (1, 3), (3.5, 4.5));
    }

    [Theory]
    [InlineData("caption")]
    [InlineData("geometry")]
    public async Task Ducking_RevalidatesTheCapturedSnapshotInsideTheProjectSaveLock(string change)
    {
        await using var f = new Fixture();
        // Warm the actual delegate and JSON body binder without writing an envelope (no music selected yet).
        Assert.Equal(200, (await f.DuckAsync()).Status);
        f.SaveProject(f.Project with { MusicAssetId = f.MusicId, Timeline = [new("a", f.Clip.Id, 0, 4, SoundAssetId: f.MusicId)] });
        Response? response = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { response = f.DuckAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        try
        {
            EditorRevisionStore.Serialized(f.Studio, f.Clip.Id, () =>
            {
                worker.Start();
                // MemoryStream request binding completes synchronously; this worker waits at the held editor Monitor.
                Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)), "ducking never reached the held save lock");
                Assert.Null(response);
                Assert.Null(f.Studio.Document("music-ducking", f.Clip.Id));
                if (change == "caption") f.Transcript(f.Clip, new TranscriptSegment(1, 2, "锁内更新的原始字幕"));
                else f.SaveProject(f.Project with { Timeline = [f.Project.Timeline![0] with { GapBefore = 1 }] });
                return 0;
            });
        }
        finally { if ((worker.ThreadState & ThreadState.Unstarted) == 0) Assert.True(worker.Join(TimeSpan.FromSeconds(10))); }
        Assert.Null(failure);
        Assert.NotNull(response);
        Assert.Equal(409, response!.Status);
        Assert.Null(f.Studio.Document("music-ducking", f.Clip.Id));
        Assert.Null(f.Project.Timeline![0].VolumeKeyframes);
    }

    private static string ExportText(Response response, string format)
    {
        if (format == "preview") return response.Json().GetProperty("text").GetString()!;
        if (format != "docx") return Encoding.UTF8.GetString(response.Body);
        using var stream = new MemoryStream(response.Body);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        return reader.ReadToEnd();
    }

    private static void AssertVoices(JsonElement envelope, params (double Start, double End)[] expected)
    {
        var voices = envelope.GetProperty("voiceIntervals").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, voices.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Start, voices[i].GetProperty("start").GetDouble(), 6);
            Assert.Equal(expected[i].End, voices[i].GetProperty("end").GetDouble(), 6);
        }
    }

    private sealed record Response(int Status, byte[] Body)
    {
        public JsonElement Json() { using var json = JsonDocument.Parse(Body); return json.RootElement.Clone(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "liveclipper-effective-wiring-" + Guid.NewGuid().ToString("N"));
        public string SrtPath => Path.Combine(Root, "original.srt");
        public string MusicId { get; } = "music-" + Guid.NewGuid().ToString("N");
        public Store Store { get; }
        public StudioStore Studio { get; }
        public Clip Clip { get; }
        public Clip Secondary { get; }
        public AutoEditService AutoEdit { get; }
        public EditPlanningService Planning { get; }
        public MediaTools Media { get; }
        public EditorProject Project => JsonSerializer.Deserialize<EditorProject>(Studio.Project(Clip.Id)!, Web)!;

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new Store(Path.Combine(Root, "test.db")); Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath); Studio.Migrate();
            File.WriteAllText(SrtPath, "ORIGINAL-SRT-MARKER");
            Clip = AddClip(SrtPath);
            Secondary = AddClip();
            Transcript(Clip, new(0, 1, "原始平淡内容"), new(2, 3, "原始结论"));
            Transcript(Secondary, new TranscriptSegment(0, 1, "第二源平淡内容"));
            SaveProject(new EditorProject { Start = 0, End = 4, Timeline = [new("base", Clip.Id, 0, 4)] });
            Studio.SaveAsset(new AudioAsset(MusicId, "test music", "music", "", Path.Combine(Root, "music.wav"), false));
            Media = new MediaTools(Path.Combine(Root, "missing-ffmpeg"));
            Planning = new EditPlanningService(Store, Studio);
            AutoEdit = new AutoEditService(Store, Studio, new StudioMediaService(Store, Studio, Media, Root),
                new AiAnalysisService(Store, Studio), Planning, new SilenceTrimService(Media), Media);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            TranscriptReadEndpoints.Map(_app, Store, Studio);
            var settings = new LiveSettings { StorageRoot = Root };
            var music = new MusicGenerationService(settings, new AudioLibraryService(Studio, Root), Studio, Media);
            MusicEndpoints.Map(_app, music, settings, Path.Combine(Root, "settings.json"), Store, Studio, Media);
        }

        public Clip AddClip(string? srt = null)
        {
            var clip = new Clip(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Path.Combine(Root, Guid.NewGuid().ToString("N") + ".mp4"), srt, DateTimeOffset.UtcNow);
            Store.SaveClip(clip);
            return clip;
        }

        public void SaveProject(EditorProject project) => Studio.SaveProject(Clip.Id, JsonSerializer.Serialize(project));
        public void Transcript(Clip clip, params TranscriptSegment[] segments) => Store.SaveTranscript(clip.EventId, JsonSerializer.Serialize(new { language = "zh", segments }, Web), SrtPath);
        public Task<Response> DuckAsync(MusicDuckingRequest? body = null) => InvokeAsync("/api/music/projects/{clipId}/ducking", "POST", Clip.Id, body ?? new(-14, 100, 200));
        public Task<Response> DuckGetAsync() => InvokeAsync("/api/music/projects/{clipId}/ducking", "GET", Clip.Id);
        public Task<Response> ExportAsync(string format) => InvokeAsync(format == "srt-direct" ? "/api/clips/{id}/srt"
            : format == "preview" ? "/api/clips/{id}/transcript" : "/api/clips/{id}/transcript/export", "GET", Clip.Id, format: format);

        public async Task<Response> InvokeAsync(string pattern, string method, string id, object? body = null, string? format = null)
        {
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>(),
                x => x.RoutePattern.RawText == pattern && x.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = method;
            context.Request.RouteValues[pattern.Contains("{clipId}") ? "clipId" : "id"] = id;
            if (format is not null) context.Request.QueryString = new QueryString("?format=" + format);
            var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, Web);
            await using var input = new MemoryStream(bytes);
            context.Request.Body = input;
            if (body is not null)
            {
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
            }
            await using var output = new MemoryStream();
            context.Response.Body = output;
            await endpoint.RequestDelegate!(context);
            return new(context.Response.StatusCode, output.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            EditorRevisionStore.Forget(Clip.Id);
            EditorRevisionStore.Forget(Secondary.Id);
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
