using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Tests;

public sealed class TranscriptionCompletionPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedTranscript_DoesNotQueueOrRewriteManualProject(bool autoAnalyze)
    {
        using var fixture = new Fixture(autoAnalyze, saveProject: true);
        var before = fixture.Studio.GetProjectRecord(fixture.Clip.Id)!;
        var backups = fixture.Studio.ProjectRevisions(fixture.Clip.Id).ToArray();
        var revision = EditorRevisionStore.Current(fixture.Studio, fixture.Clip.Id);
        var undo = EditorRevisionStore.SnapshotDepth(fixture.Clip.Id);
        var redo = EditorRevisionStore.RedoDepth(fixture.Clip.Id);
        var settings = JsonSerializer.Serialize(fixture.Settings);
        var transcript = fixture.Store.TranscriptJson(fixture.Clip.EventId);
        var job = Assert.Single(fixture.Store.Jobs());
        var changed = 0;
        fixture.AutoEdit.Changed += _ => changed++;

        var shouldQueue = TranscriptionCompletionPolicy.ShouldQueueAutoEdit(fixture.Settings, fixture.Clip);
        // If the gate regresses, await the real worker instead of racing Queue's background task.
        if (shouldQueue) await fixture.AutoEdit.RunAsync(fixture.Clip, CancellationToken.None);

        var after = fixture.Studio.GetProjectRecord(fixture.Clip.Id)!;
        Assert.Equal(before, after); // JSON, durable revision and updated_at must all be unchanged.
        Assert.Equal(backups, fixture.Studio.ProjectRevisions(fixture.Clip.Id).ToArray());
        Assert.Equal(revision, EditorRevisionStore.Current(fixture.Studio, fixture.Clip.Id));
        Assert.Equal(undo, EditorRevisionStore.SnapshotDepth(fixture.Clip.Id));
        Assert.Equal(redo, EditorRevisionStore.RedoDepth(fixture.Clip.Id));
        var project = JsonSerializer.Deserialize<EditorProject>(after.Json)!;
        Assert.Equal("manual-a", project.Timeline![0].Id);
        Assert.Equal("manual-b", project.Timeline[1].Id);
        Assert.Equal("manual-nested", project.Timeline[2].Id);
        Assert.Equal(new[] { 1 }, project.SelectedSegments);
        Assert.Equal("manual-transition", Assert.Single(project.Transitions!).Id);
        Assert.Equal("manual-sequence", Assert.Single(project.NestedSequences!).Key);
        Assert.False(project.SentenceMode);
        Assert.Equal(settings, JsonSerializer.Serialize(fixture.Settings));
        Assert.Equal(transcript, fixture.Store.TranscriptJson(fixture.Clip.EventId));
        Assert.Equal(job, Assert.Single(fixture.Store.Jobs()));
        Assert.Null(fixture.Studio.Document("auto-edit", fixture.Clip.Id));
        Assert.Null(fixture.Studio.Document("cut-suggestion", fixture.Clip.Id));
        Assert.Equal(0, changed);
        Assert.False(shouldQueue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedTranscript_WithoutProject_DoesNotGenerateOrCreateProject(bool autoAnalyze)
    {
        using var fixture = new Fixture(autoAnalyze, saveProject: false);
        var shouldQueue = TranscriptionCompletionPolicy.ShouldQueueAutoEdit(fixture.Settings, fixture.Clip);
        if (shouldQueue) await fixture.AutoEdit.RunAsync(fixture.Clip, CancellationToken.None);

        Assert.Null(fixture.Studio.GetProjectRecord(fixture.Clip.Id));
        Assert.Equal(0L, fixture.Studio.GetProjectRevision(fixture.Clip.Id));
        Assert.Empty(fixture.Studio.ProjectRevisions(fixture.Clip.Id));
        Assert.Null(fixture.Studio.Document("auto-edit", fixture.Clip.Id));
        Assert.Null(fixture.Studio.Document("cut-suggestion", fixture.Clip.Id));
        Assert.Equal("completed", Assert.Single(fixture.Store.Jobs()).Status);
        Assert.NotNull(fixture.Store.TranscriptJson(fixture.Clip.EventId));
        Assert.False(shouldQueue);
    }

    [Fact]
    public void Program_RegistersOneCompletionHandler_AndOnlyQueuesThroughPolicy()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LiveClipper.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var program = File.ReadAllText(Path.Combine(directory!.FullName, "src", "LiveClipper.App", "Program.cs"));
        var subscriptions = Regex.Matches(program, @"coordinator\.Transcription\.Completed\s*\+=");
        var subscription = Assert.Single(subscriptions.Cast<Match>());
        var handler = Regex.Match(program[subscription.Index..],
            @"\Acoordinator\.Transcription\.Completed\s*\+=\s*clip\s*=>\s*\{(?<body>[^{}]*)\}\s*;");
        Assert.True(handler.Success, "Completion handler must contain only the policy-gated queue.");
        var body = Regex.Replace(handler.Groups["body"].Value, @"\s+", "");
        Assert.Equal("if(TranscriptionCompletionPolicy.ShouldQueueAutoEdit(settings,clip))autoEdit.Queue(clip);", body);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-completion-policy-" + Guid.NewGuid().ToString("N"));

        public Fixture(bool autoAnalyze, bool saveProject)
        {
            Directory.CreateDirectory(_root);
            var database = Path.Combine(_root, "test.db");
            Store = new Store(database);
            Store.Migrate();
            Studio = new StudioStore(database);
            Studio.Migrate();
            Settings = new LiveSettings { StorageRoot = _root, AutoAnalyze = autoAnalyze, AutoTrimSilence = false };
            Clip = new Clip(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Path.Combine(_root, "source.mp4"), null, DateTimeOffset.UtcNow);
            Store.SaveClip(Clip);
            // Completion has already persisted its transcript/job before consulting the policy.
            Store.SaveTranscript(Clip.EventId,
                """{"segments":[{"start":0,"end":2,"text":"保留这段完整内容"},{"start":2,"end":4,"text":"用户手动决定如何剪辑"}]}""",
                Path.Combine(_root, "transcript.srt"));
            Store.SaveJob(new Job(Guid.NewGuid().ToString("N"), "transcribe", Clip.EventId, "completed", 1, null));
            if (saveProject)
            {
                var project = new EditorProject
                {
                    Start = 0, End = 4, SelectedSegments = [1], SentenceMode = false,
                    Timeline =
                    [
                        new TimelineItem("manual-a", Clip.Id, 0, 1),
                        new TimelineItem("manual-b", Clip.Id, 2, 3),
                        new TimelineItem("manual-nested", "", 0, 0, NestedSequenceId: "manual-sequence")
                    ],
                    Transitions = [new EditorTransition("manual-transition", "manual-b", 1, "cross-dissolve", .2, "center")],
                    NestedSequences = new Dictionary<string, NestedSequence>
                    {
                        ["manual-sequence"] = new("manual-sequence", "手工嵌套", [new TimelineItem("nested-child", Clip.Id, 3, 4)])
                    }
                };
                EditorRenderService.Validate(project);
                Studio.SaveProject(Clip.Id, JsonSerializer.Serialize(project));
            }
            var media = new MediaTools(Path.Combine(_root, "missing-ffmpeg"));
            AutoEdit = new AutoEditService(Store, Studio, new StudioMediaService(Store, Studio, media, _root),
                new AiAnalysisService(Store, Studio), new EditPlanningService(Store, Studio), new SilenceTrimService(media), media)
            {
                AutoAnalyze = autoAnalyze, AutoTrimSilence = false
            };
        }

        public Store Store { get; }
        public StudioStore Studio { get; }
        public LiveSettings Settings { get; }
        public Clip Clip { get; }
        public AutoEditService AutoEdit { get; }

        public void Dispose()
        {
            EditorRevisionStore.Forget(Clip.Id);
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
        }
    }
}
