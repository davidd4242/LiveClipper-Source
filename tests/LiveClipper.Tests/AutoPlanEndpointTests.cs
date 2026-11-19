using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LiveClipper.Tests;

public sealed class AutoPlanEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string ProposalRoute = "/api/studio/clips/{id}/auto-plan/proposal";
    private const string ApplyRoute = "/api/studio/clips/{id}/auto-plan/apply";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Proposal_IsReadOnlyWithOrWithoutAnExistingProject(bool saveProject)
    {
        await using var f = new Fixture(saveProject);
        var before = f.Capture();

        var response = await f.InvokeAsync(ProposalRoute);

        Assert.Equal(200, response.Status);
        var proposal = response.Json().Deserialize<AutoEditProposal>(Web)!;
        Assert.Equal(f.Clip.Id, proposal.ClipId);
        Assert.Equal(before.Revision, (long)proposal.BaseRevision);
        Assert.Equal(EditorRevisionStore.EpochFor(f.Studio), response.Epoch);
        Assert.Equal(response.Epoch, proposal.Epoch);
        Assert.NotEmpty(proposal.Timeline);
        Assert.NotEmpty(proposal.Changes);
        Assert.NotEmpty(proposal.SegmentReasons);
        Assert.Equal(before, f.Capture());
        if (!saveProject) Assert.Null(f.Studio.GetProjectRecord(f.Clip.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Apply_ReturnsTheSingleCommittedRevisionAndUndoStep(bool saveProject)
    {
        await using var f = new Fixture(saveProject);
        var proposal = await f.ProposalAsync();
        var before = f.Capture();

        var response = await f.ApplyAsync(proposal);

        Assert.Equal(200, response.Status);
        Assert.True(response.Json().GetProperty("applied").GetBoolean());
        Assert.False(response.Json().GetProperty("conflict").GetBoolean());
        Assert.Equal("applied", response.Json().GetProperty("code").GetString());
        Assert.Equal(before.Revision + 1, response.Json().GetProperty("revision").GetInt64());
        Assert.Equal(before.Revision + 1, f.Studio.GetProjectRevision(f.Clip.Id));
        Assert.Equal(before.Undo + 1, EditorRevisionStore.SnapshotDepth(f.Clip.Id));
        Assert.Equal(EditorRevisionStore.EpochFor(f.Studio), response.Epoch);
        Assert.Equal(JsonSerializer.Serialize(proposal.Timeline, Web), JsonSerializer.Serialize(f.Project.Timeline, Web));
        Assert.Equal(new[] { (0d, 1d), (2d, 3d) }, f.Project.Timeline!.Select(x => (x.Start, x.End)).ToArray());
    }

    [Fact]
    public async Task Apply_ReplayReturns409WithoutAnotherWrite()
    {
        await using var f = new Fixture();
        var proposal = await f.ProposalAsync();
        Assert.Equal(200, (await f.ApplyAsync(proposal)).Status);
        var before = f.Capture();

        var response = await f.ApplyAsync(proposal);

        AssertError(response, 409, "stale-revision", "revision_conflict", before.Revision);
        Assert.Equal(before, f.Capture());
    }

    [Fact]
    public async Task Apply_ChangedProjectBaselineReturns409AndKeepsTheNewerProject()
    {
        await using var f = new Fixture();
        var proposal = await f.ProposalAsync();
        f.Studio.SaveProject(f.Clip.Id, JsonSerializer.Serialize(f.Project with { MasterVolume = .5 }));
        var before = f.Capture();
        Assert.True(before.Revision > proposal.BaseRevision);

        var response = await f.ApplyAsync(proposal);

        AssertError(response, 409, "stale-revision", "revision_conflict", before.Revision);
        Assert.Equal(before, f.Capture());
        Assert.Equal(.5, f.Project.MasterVolume);
    }

    [Theory]
    [InlineData("epoch", "stale-epoch", "stale_epoch")]
    [InlineData("project", "stale-project-hash", "stale_project_hash")]
    [InlineData("transcript", "stale-transcript", "stale_transcript")]
    [InlineData("clip", "proposal-clip-mismatch", "proposal_clip_mismatch")]
    public async Task Apply_BaselineMismatchUsesStructured409(string mismatch, string code, string publicCode)
    {
        await using var f = new Fixture();
        var proposal = await f.ProposalAsync();
        proposal = mismatch switch
        {
            "epoch" => proposal with { Epoch = "previous-process" },
            "project" => proposal with { BaseHash = new string('0', 64) },
            "transcript" => proposal with { TranscriptHash = new string('0', 64) },
            "clip" => proposal with { ClipId = "different-clip" },
            _ => throw new InvalidOperationException(mismatch)
        };
        var before = f.Capture();

        var response = await f.ApplyAsync(proposal);

        AssertError(response, 409, code, publicCode, before.Revision);
        Assert.Equal(EditorRevisionStore.EpochFor(f.Studio), response.Epoch);
        Assert.Equal(before, f.Capture());
    }

    [Fact]
    public async Task Apply_ChangedTranscriptReturns409WithoutWritingProjectOrHistory()
    {
        await using var f = new Fixture();
        var proposal = await f.ProposalAsync();
        f.Store.SaveTranscript(f.Clip.EventId,
            """{"segments":[{"start":0,"end":4,"text":"预览之后修改的字幕"}]}""", Path.Combine(f.Root, "changed.srt"));
        var before = f.Capture();

        var response = await f.ApplyAsync(proposal);

        AssertError(response, 409, "stale-transcript", "stale_transcript", before.Revision);
        Assert.Equal(before, f.Capture());
    }

    [Theory]
    [InlineData(false, "session-mismatch", "session_not_found")]
    [InlineData(true, "session-busy", "revision_conflict")]
    public async Task Apply_ActiveSessionRejectsEvenItsHolder(bool sendHolder, string code, string publicCode)
    {
        await using var f = new Fixture();
        var proposal = await f.ProposalAsync();
        var session = EditorRevisionStore.BeginSession(f.Clip.Id, f.Studio.Project(f.Clip.Id)!);
        try
        {
            var before = f.Capture();
            var response = await f.ApplyAsync(proposal, sendHolder ? session.SessionId : null);
            AssertError(response, 409, code, publicCode, before.Revision);
            Assert.Equal(before, f.Capture());
        }
        finally { EditorRevisionStore.EndSession(f.Clip.Id, session.SessionId); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"baseRevision\":\"not-an-integer\"}")]
    public async Task Apply_InvalidJsonBodyReturnsStructured400AndWritesNothing(string body)
    {
        await using var f = new Fixture();
        var before = f.Capture();

        var response = await f.InvokeAsync(ApplyRoute, body);

        AssertError(response, 400, "invalid-proposal", "invalid_proposal", before.Revision);
        Assert.Equal(before, f.Capture());
    }

    [Theory]
    [InlineData("proposalId")]
    [InlineData("clipId")]
    [InlineData("epoch")]
    [InlineData("baseRevision")]
    [InlineData("baseHash")]
    [InlineData("transcriptHash")]
    [InlineData("source")]
    [InlineData("timeline")]
    [InlineData("changes")]
    [InlineData("segmentReasons")]
    [InlineData("createdAt")]
    public async Task Apply_MissingProposalFieldIs400EvenWhenRevisionZeroCouldMatch(string field)
    {
        await using var f = new Fixture(saveProject: false);
        var proposal = await f.ProposalAsync();
        Assert.Equal(0, proposal.BaseRevision);
        var body = JsonSerializer.SerializeToNode(proposal, Web)!.AsObject();
        Assert.True(body.Remove(field));
        var before = f.Capture();

        var response = await f.InvokeAsync(ApplyRoute, body.ToJsonString());

        AssertError(response, 400, "invalid-proposal", "invalid_proposal", before.Revision);
        Assert.Equal(before, f.Capture());
    }

    [Theory]
    [InlineData("empty-timeline")]
    [InlineData("null-timeline")]
    [InlineData("null-item")]
    [InlineData("invalid-range")]
    [InlineData("negative-revision")]
    [InlineData("null-hash")]
    public async Task Apply_InvalidProposalShapeOrTimelineReturns400(string invalid)
    {
        await using var f = new Fixture();
        var proposal = await f.ProposalAsync();
        proposal = invalid switch
        {
            "empty-timeline" => proposal with { Timeline = [] },
            "null-timeline" => proposal with { Timeline = null! },
            "null-item" => proposal with { Timeline = [null!] },
            "invalid-range" => proposal with { Timeline = [proposal.Timeline[0] with { End = -1 }] },
            "negative-revision" => proposal with { BaseRevision = -1 },
            "null-hash" => proposal with { BaseHash = null! },
            _ => throw new InvalidOperationException(invalid)
        };
        var before = f.Capture();

        var response = await f.ApplyAsync(proposal);

        AssertError(response, 400, "invalid-proposal", "invalid_proposal", before.Revision);
        Assert.Equal(before, f.Capture());
    }

    [Fact]
    public async Task Apply_NonJsonBodyReturnsStructured400()
    {
        await using var f = new Fixture();
        var before = f.Capture();
        var response = await f.InvokeAsync(ApplyRoute, "{}", contentType: "text/plain");
        AssertError(response, 400, "invalid-proposal", "invalid_proposal", before.Revision);
        Assert.Equal(before, f.Capture());
    }

    [Theory]
    [InlineData(ProposalRoute)]
    [InlineData(ApplyRoute)]
    public async Task MissingClipReturns404WithoutWriting(string route)
    {
        await using var f = new Fixture();
        var before = f.Capture();
        var response = await f.InvokeAsync(route, "{}", id: "missing-clip");
        Assert.Equal(404, response.Status);
        Assert.Equal("not_found", response.Json().GetProperty("code").GetString());
        Assert.Equal(before, f.Capture());
    }

    private static void AssertError(Response response, int status, string code, string publicCode, long revision)
    {
        Assert.Equal(status, response.Status);
        var json = response.Json();
        Assert.Equal(code, json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error").GetProperty("message").GetString()));
        Assert.Equal(publicCode, json.GetProperty("code").GetString());
        Assert.Equal(revision, json.GetProperty("revision").GetInt64());
    }

    private sealed record Response(int Status, byte[] Body, string Epoch)
    {
        public JsonElement Json() { using var json = JsonDocument.Parse(Body); return json.RootElement.Clone(); }
    }

    private sealed record Snapshot(StoredEditorProject? Project, long Revision, string DatabaseHash, string Backups, int Undo, int Redo);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "liveclipper-auto-plan-endpoint-" + Guid.NewGuid().ToString("N"));
        public Store Store { get; }
        public StudioStore Studio { get; }
        public Clip Clip { get; }
        public EditorProject Project => EditorProjectCodec.Deserialize(Studio.Project(Clip.Id)!);

        public Fixture(bool saveProject = true)
        {
            Directory.CreateDirectory(Root);
            Store = new Store(Path.Combine(Root, "test.db")); Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath); Studio.Migrate();
            Clip = new Clip(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Path.Combine(Root, "missing-source.mp4"), null, DateTimeOffset.UtcNow);
            Store.SaveClip(Clip);
            Store.SaveTranscript(Clip.EventId,
                """{"segments":[{"start":0,"end":1,"text":"开头有意义的内容"},{"start":1,"end":2,"text":"[音乐]"},{"start":2,"end":3,"text":"最后重要的结论"}]}""",
                Path.Combine(Root, "transcript.srt"));
            if (saveProject)
                Studio.SaveProject(Clip.Id, JsonSerializer.Serialize(new EditorProject { Start = 0, End = 4, Timeline = [new("base", Clip.Id, 0, 4)] }));
            EditorRevisionStore.Current(Studio, Clip.Id);
            var media = new MediaTools(Path.Combine(Root, "missing-ffmpeg"));
            var autoEdit = new AutoEditService(Store, Studio, new StudioMediaService(Store, Studio, media, Root),
                new AiAnalysisService(Store, Studio), new EditPlanningService(Store, Studio), new SilenceTrimService(media), media)
            {
                AutoAnalyze = true, AutoTrimSilence = true,
                ConnectionFactory = () => throw new InvalidOperationException("proposal must not invoke AI")
            };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            AutoPlanEndpoints.Map(_app, Store, Studio, autoEdit);
        }

        public async Task<AutoEditProposal> ProposalAsync()
        {
            var response = await InvokeAsync(ProposalRoute);
            Assert.Equal(200, response.Status);
            return response.Json().Deserialize<AutoEditProposal>(Web)!;
        }

        public Task<Response> ApplyAsync(AutoEditProposal proposal, string? sessionId = null) =>
            InvokeAsync(ApplyRoute, JsonSerializer.Serialize(proposal, Web), sessionId);

        public async Task<Response> InvokeAsync(string pattern, string? body = null, string? sessionId = null, string? id = null, string contentType = "application/json")
        {
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>(),
                x => x.RoutePattern.RawText == pattern && x.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = "POST";
            context.Request.RouteValues["id"] = id ?? Clip.Id;
            if (sessionId is not null) context.Request.Headers["X-Session-Id"] = sessionId;
            var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
            await using var input = new MemoryStream(bytes);
            context.Request.Body = input;
            if (body is not null)
            {
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
                context.Request.ContentType = contentType;
                context.Request.ContentLength = bytes.Length;
            }
            await using var output = new MemoryStream();
            context.Response.Body = output;
            await endpoint.RequestDelegate!(context);
            return new(context.Response.StatusCode, output.ToArray(), context.Response.Headers["X-Editor-Epoch"].ToString());
        }

        public Snapshot Capture() => new(Studio.GetProjectRecord(Clip.Id), Studio.GetProjectRevision(Clip.Id), DatabaseHash(),
            JsonSerializer.Serialize(Studio.ProjectRevisions(Clip.Id)), EditorRevisionStore.SnapshotDepth(Clip.Id), EditorRevisionStore.RedoDepth(Clip.Id));

        // A preview cannot write reports/events/jobs either; compare every real SQLite application table.
        private string DatabaseHash()
        {
            using var connection = new SqliteConnection($"Data Source={Store.DatabasePath}");
            connection.Open();
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var reader = command.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }
            var contents = new List<string>();
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
                using var reader = command.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    rows.Add(JsonSerializer.Serialize(values.Select(x => x is DBNull ? null : x).ToArray()));
                }
                rows.Sort(StringComparer.Ordinal);
                contents.Add(table + ":" + JsonSerializer.Serialize(rows));
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(contents))));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            EditorRevisionStore.Forget(Clip.Id);
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
