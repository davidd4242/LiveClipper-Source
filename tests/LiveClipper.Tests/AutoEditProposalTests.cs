using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Tests;

public sealed class AutoEditProposalTests
{
    [Fact]
    public void GeneratePlan_ProducesReasonedChanges_WithoutWritingAnyStorage()
    {
        using var fixture = new Fixture();
        EditorRevisionStore.PushSnapshot(fixture.Clip.Id, "existing-undo");
        EditorRevisionStore.PushRedo(fixture.Clip.Id, "existing-redo");
        var before = fixture.Capture();
        var changed = 0;
        fixture.AutoEdit.Changed += _ => changed++;

        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);

        Assert.Equal(before, fixture.Capture()); // Includes stored JSON hash/revision, all SQLite tables, backups and history.
        Assert.Equal(before.ProjectHash, proposal.BaseHash);
        Assert.Equal(before.Revision, (long)proposal.BaseRevision);
        Assert.Equal(fixture.Clip.Id, proposal.ClipId);
        Assert.Equal(EditorRevisionStore.EpochFor(fixture.Studio), proposal.Epoch);
        Assert.Equal("rules", proposal.Source);
        Assert.NotEmpty(proposal.ProposalId);
        Assert.NotEqual(JsonSerializer.Serialize(fixture.Project.Timeline), JsonSerializer.Serialize(proposal.Timeline));
        Assert.Contains(proposal.Changes, x => x.Kind == "trim" && x.ItemId == "editable");
        Assert.All(proposal.Changes, x => Assert.False(string.IsNullOrWhiteSpace(x.Reason)));
        Assert.Equal(5, proposal.SegmentReasons.Length);
        Assert.Contains(proposal.SegmentReasons, x => !x.Keep && x.Reason.Contains("音乐"));
        Assert.Contains(proposal.SegmentReasons, x => !x.Keep && x.Reason.Contains("口头禅"));
        Assert.All(proposal.SegmentReasons, x => Assert.False(string.IsNullOrWhiteSpace(x.Reason)));
        Assert.Equal(0, changed);
        Assert.Null(fixture.Studio.Document("auto-edit", fixture.Clip.Id));
        Assert.Null(fixture.Studio.Document("cut-suggestion", fixture.Clip.Id));
    }

    [Fact]
    public void GeneratePlan_WithoutProject_DoesNotCreateProjectOrBackup()
    {
        using var fixture = new Fixture(saveProject: false);
        var before = fixture.Capture();

        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);

        Assert.Equal(before, fixture.Capture());
        Assert.Equal(0, proposal.BaseRevision);
        Assert.Equal(Hash(""), proposal.BaseHash);
        Assert.NotEmpty(proposal.Timeline);
        Assert.Null(fixture.Studio.GetProjectRecord(fixture.Clip.Id));
        Assert.Empty(fixture.Studio.ProjectRevisions(fixture.Clip.Id));
    }

    [Fact]
    public void ApplyProposal_AdvancesExactlyOnce_PreservesTransitionsNestedSequencesAndUnrelatedFields()
    {
        using var fixture = new Fixture();
        var before = fixture.Capture();
        var original = fixture.Project;
        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);

        var result = fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal);

        Assert.True(result.Applied, result.Error);
        Assert.False(result.Conflict);
        Assert.Equal("applied", result.Code);
        Assert.Equal(before.Revision + 1, (long)result.Revision);
        Assert.Equal(before.Revision + 1, fixture.Studio.GetProjectRevision(fixture.Clip.Id));
        Assert.Equal(before.Undo + 1, EditorRevisionStore.SnapshotDepth(fixture.Clip.Id));
        var saved = fixture.Project;
        Assert.Equal(JsonSerializer.Serialize(proposal.Timeline), JsonSerializer.Serialize(saved.Timeline));
        Assert.Equal(JsonSerializer.Serialize(original.Transitions), JsonSerializer.Serialize(saved.Transitions));
        Assert.Equal(JsonSerializer.Serialize(original.NestedSequences), JsonSerializer.Serialize(saved.NestedSequences));
        Assert.Equal(JsonSerializer.Serialize(original.SelectedSegments), JsonSerializer.Serialize(saved.SelectedSegments));
        Assert.Equal(JsonSerializer.Serialize(original.Markers), JsonSerializer.Serialize(saved.Markers));
        Assert.Equal(original.SentenceMode, saved.SentenceMode);
        Assert.Equal(original.SubtitleFont, saved.SubtitleFont);
        Assert.Equal(original.MasterVolume, saved.MasterVolume);
        Assert.Equal(0, saved.Start);
        Assert.Equal(8, saved.End);
        using var stored = JsonDocument.Parse(fixture.Studio.Project(fixture.Clip.Id)!);
        Assert.Equal("keep-me", stored.RootElement.GetProperty("unrelatedFutureField").GetProperty("value").GetString());
        Assert.Equal(1.25, stored.RootElement.GetProperty("unrelatedFutureField").GetProperty("scale").GetDouble());
        Assert.Equal("transition", Assert.Single(saved.Transitions!).Id);
        Assert.Equal("sequence", Assert.Single(saved.NestedSequences!).Key);
        Assert.Equal("nested", Assert.Single(saved.Timeline!, x => x.NestedSequenceId == "sequence").Id);
        Assert.Equal(5, Assert.Single(saved.Timeline!, x => x.Id == "editable").Start);
        EditorRenderService.Validate(EditorRenderService.FlattenProject(saved));
        // One undo checkpoint must contain the exact pre-apply JSON, not a partial or already-mutated project.
        Assert.Equal(before.Project!.Json, EditorRevisionStore.PopSnapshot(fixture.Clip.Id));
    }

    [Fact]
    public void ApplyProposal_WithoutProject_CreatesExactlyOneRevision()
    {
        using var fixture = new Fixture(saveProject: false);
        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);

        var result = fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal);

        Assert.True(result.Applied, result.Error);
        Assert.Equal(1, result.Revision);
        Assert.Equal(1L, fixture.Studio.GetProjectRevision(fixture.Clip.Id));
        Assert.Equal(1, EditorRevisionStore.SnapshotDepth(fixture.Clip.Id));
        Assert.Equal(JsonSerializer.Serialize(proposal.Timeline), JsonSerializer.Serialize(fixture.Project.Timeline));
        var once = fixture.Capture();
        Assert.False(fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal).Applied);
        Assert.Equal(once, fixture.Capture());
    }

    [Fact]
    public void ApplyProposal_StaleRevision_ReturnsConflictAndWritesNothing()
    {
        using var fixture = new Fixture();
        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);
        fixture.Studio.SaveProject(fixture.Clip.Id, JsonSerializer.Serialize(fixture.Project with { MasterVolume = .7 }));
        var before = fixture.Capture();

        var result = fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal);

        Assert.False(result.Applied);
        Assert.True(result.Conflict);
        Assert.Equal("stale-revision", result.Code);
        Assert.Equal(before.Revision, (long)result.Revision);
        Assert.Equal(before, fixture.Capture());
    }

    [Fact]
    public void ApplyProposal_RepeatedApply_IsRejectedWithoutSecondWrite()
    {
        using var fixture = new Fixture();
        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);
        Assert.True(fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal).Applied);
        var once = fixture.Capture();

        var repeated = fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal);

        Assert.False(repeated.Applied);
        Assert.True(repeated.Conflict);
        Assert.Equal("stale-revision", repeated.Code);
        Assert.Equal(once, fixture.Capture());
    }

    [Theory]
    [InlineData("hash", "stale-project-hash")]
    [InlineData("epoch", "stale-epoch")]
    [InlineData("clip", "proposal-clip-mismatch")]
    [InlineData("transcript", "stale-transcript")]
    public void ApplyProposal_MismatchedBaseline_WritesNothing(string mismatch, string code)
    {
        using var fixture = new Fixture();
        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);
        proposal = mismatch switch
        {
            "hash" => proposal with { BaseHash = "not-the-project-hash" },
            "epoch" => proposal with { Epoch = "old-process" },
            "clip" => proposal with { ClipId = "another-clip" },
            "transcript" => proposal with { TranscriptHash = "old-transcript" },
            _ => throw new InvalidOperationException(mismatch)
        };
        var before = fixture.Capture();

        var result = fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal);

        Assert.False(result.Applied);
        Assert.True(result.Conflict);
        Assert.Equal(code, result.Code);
        Assert.Equal(before, fixture.Capture());
    }

    [Fact]
    public void ApplyProposal_TranscriptActuallyChanged_RejectsOldPreview()
    {
        using var fixture = new Fixture();
        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);
        fixture.Store.SaveTranscript(fixture.Clip.EventId,
            """{"segments":[{"start":0,"end":8,"text":"字幕在预览之后发生变化"}]}""", "changed.srt");
        var before = fixture.Capture();

        var result = fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal);

        Assert.False(result.Applied);
        Assert.Equal("stale-transcript", result.Code);
        Assert.True(result.Conflict);
        Assert.Equal(before, fixture.Capture());
    }

    [Fact]
    public void ApplyProposal_ActiveSessionEvenWithHolderId_DoesNotMergeIntoDragHistory()
    {
        using var fixture = new Fixture();
        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);
        var session = EditorRevisionStore.BeginSession(fixture.Clip.Id, fixture.Studio.Project(fixture.Clip.Id)!);
        var before = fixture.Capture();

        var missing = fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal);
        var holder = fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal, session.SessionId);

        Assert.False(missing.Applied);
        Assert.True(missing.Conflict);
        Assert.False(holder.Applied);
        Assert.True(holder.Conflict);
        Assert.Equal(before, fixture.Capture());
        EditorRevisionStore.EndSession(fixture.Clip.Id, session.SessionId);
    }

    [Fact]
    public void ApplyProposal_ChangedTransitionAnchor_IsRejectedWithoutWrite()
    {
        using var fixture = new Fixture();
        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);
        proposal = proposal with { Timeline = proposal.Timeline.Where(x => x.Id != "manual-left").ToArray() };
        var before = fixture.Capture();

        var result = fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal);

        Assert.False(result.Applied);
        Assert.Equal("protected-timeline", result.Code);
        Assert.Equal(before, fixture.Capture());
    }

    [Fact]
    public void ApplyProposal_SaveFailure_LeavesProjectRevisionBackupsAndHistoryUnchanged()
    {
        using var fixture = new Fixture();
        var proposal = fixture.AutoEdit.GeneratePlan(fixture.Clip);
        fixture.SetWriteFailure();
        var before = fixture.Capture();

        var error = Assert.Throws<SqliteException>(() => fixture.AutoEdit.ApplyProposal(fixture.Clip, proposal));

        Assert.Contains("auto-edit-proposal-save-failure", error.Message);
        Assert.Equal(before, fixture.Capture());
    }

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private sealed record Snapshot(StoredEditorProject? Project, string ProjectHash, long Revision, string DatabaseHash, string Backups, int Undo, int Redo);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-auto-proposal-" + Guid.NewGuid().ToString("N"));
        private readonly string _database;

        public Fixture(bool saveProject = true)
        {
            Directory.CreateDirectory(_root);
            _database = Path.Combine(_root, "test.db");
            Store = new Store(_database);
            Store.Migrate();
            Studio = new StudioStore(_database);
            Studio.Migrate();
            Clip = new Clip(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Path.Combine(_root, "source.mp4"), null, DateTimeOffset.UtcNow);
            Store.SaveClip(Clip);
            Store.SaveTranscript(Clip.EventId,
                """{"segments":[{"start":0,"end":2,"text":"保留手工转场附近的内容"},{"start":2,"end":4,"text":"[音乐]"},{"start":4,"end":5,"text":"嗯"},{"start":5,"end":6,"text":"需要保留的重点"},{"start":7,"end":8,"text":"最后一段重点"}]}""",
                Path.Combine(_root, "transcript.srt"));
            Store.SaveJob(new Job(Guid.NewGuid().ToString("N"), "transcribe", Clip.EventId, "completed", 1, null));
            if (saveProject)
            {
                var project = new EditorProject
                {
                    Start = 0, End = 8, SentenceMode = false, SelectedSegments = [4], Markers = [.25], MasterVolume = .8,
                    Timeline =
                    [
                        new TimelineItem("manual-left", Clip.Id, 0, 1),
                        new TimelineItem("manual-right", Clip.Id, 1, 2),
                        new TimelineItem("nested", "", 0, 1, NestedSequenceId: "sequence"),
                        new TimelineItem("editable", Clip.Id, 4, 8, Grade: new ColorGrade(.1))
                    ],
                    Transitions = [new EditorTransition("transition", "manual-right", 1, "cross-dissolve", .2, "center")],
                    NestedSequences = new Dictionary<string, NestedSequence>
                    {
                        ["sequence"] = new("sequence", "手工序列", [new TimelineItem("child", Clip.Id, 2, 3)])
                    }
                };
                EditorRenderService.Validate(EditorRenderService.FlattenProject(project));
                var json = JsonSerializer.SerializeToNode(project)!.AsObject();
                json["unrelatedFutureField"] = new JsonObject { ["value"] = "keep-me", ["scale"] = 1.25 };
                Studio.SaveProject(Clip.Id, json.ToJsonString());
            }
            EditorRevisionStore.Current(Studio, Clip.Id);
            var media = new MediaTools(Path.Combine(_root, "missing-ffmpeg"));
            AutoEdit = new AutoEditService(Store, Studio, new StudioMediaService(Store, Studio, media, _root),
                new AiAnalysisService(Store, Studio), new EditPlanningService(Store, Studio), new SilenceTrimService(media), media)
            {
                AutoAnalyze = true, AutoTrimSilence = true
            };
        }

        public Store Store { get; }
        public StudioStore Studio { get; }
        public Clip Clip { get; }
        public AutoEditService AutoEdit { get; }
        public EditorProject Project => EditorProjectCodec.Deserialize(Studio.Project(Clip.Id)!);

        public Snapshot Capture()
        {
            var project = Studio.GetProjectRecord(Clip.Id);
            return new(project, Hash(project?.Json ?? ""), Studio.GetProjectRevision(Clip.Id), DatabaseHash(),
                JsonSerializer.Serialize(Studio.ProjectRevisions(Clip.Id)),
                EditorRevisionStore.SnapshotDepth(Clip.Id), EditorRevisionStore.RedoDepth(Clip.Id));
        }

        // Hash every real application table, not just the project row: a preview must not save reports/events/jobs either.
        private string DatabaseHash()
        {
            using var connection = new SqliteConnection($"Data Source={_database}");
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
            return Hash(JsonSerializer.Serialize(contents));
        }

        public void SetWriteFailure()
        {
            using var connection = new SqliteConnection($"Data Source={_database}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_auto_proposal_update BEFORE UPDATE ON editor_projects BEGIN SELECT RAISE(ABORT, 'auto-edit-proposal-save-failure'); END;";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            EditorRevisionStore.Forget(Clip.Id);
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
        }
    }
}
