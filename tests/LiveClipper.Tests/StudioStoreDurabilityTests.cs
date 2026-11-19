using System.Text.Json;
using LiveClipper.Core;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Tests;

// R51-X2: durable editor project storage. Real temp-dir SQLite and real files only; persistence is never mocked.
public sealed class StudioStoreDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "liveclipper-r51x2-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "liveclipper.db");

    public StudioStoreDurabilityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private StudioStore NewStudio()
    {
        var store = new Store(DatabasePath);
        store.Migrate();
        var studio = new StudioStore(DatabasePath);
        studio.Migrate();
        return studio;
    }

    private void Exec(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Count(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private string BackupDirectory(string clipId) => Path.Combine(_root, "project-backups", clipId);

    // ---- EditorProjectCodec ----

    [Fact]
    public void Codec_MissingSchemaVersion_IsLegacyZero()
    {
        Assert.Equal(EditorProjectCodec.LegacySchemaVersion, EditorProjectCodec.ReadSchemaVersion("""{"Tracks":[]}"""));
        Assert.Equal(0, EditorProjectCodec.EnsureSupported("{}"));
        Assert.True(EditorProjectCodec.IsSupported(0));
        Assert.True(EditorProjectCodec.IsSupported(EditorProjectCodec.CurrentSchemaVersion));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"SchemaVersion":1,"Tracks":[]}""")]
    public void Codec_ReadsCurrentVersion_InEitherSpelling(string json)
    {
        Assert.Equal(1, EditorProjectCodec.ReadSchemaVersion(json));
        Assert.Equal(EditorProjectCodec.CurrentSchemaVersion, EditorProjectCodec.EnsureSupported(json));
    }

    [Fact]
    public void Codec_TooNew_ThrowsTypedFailClosedException()
    {
        var tooNew = EditorProjectCodec.CurrentSchemaVersion + 1;
        var json = JsonSerializer.Serialize(new { schemaVersion = tooNew, Tracks = Array.Empty<int>() });
        var ex = Assert.Throws<EditorProjectSchemaTooNewException>(() => EditorProjectCodec.EnsureSupported(json));
        Assert.Equal("project-schema-too-new", ex.Code);
        Assert.Equal(EditorProjectSchemaErrors.TooNew, ex.Code);
        Assert.Equal(tooNew, ex.SchemaVersion);
        Assert.Equal(EditorProjectCodec.CurrentSchemaVersion, ex.SupportedVersion);
        Assert.Throws<EditorProjectSchemaTooNewException>(() => EditorProjectCodec.StampSchemaVersion(json));
        Assert.False(EditorProjectCodec.IsSupported(tooNew));
        Assert.Equal(tooNew, EditorProjectCodec.ReadSchemaVersion(json));
    }

    [Theory]
    [InlineData("""{"schemaVersion":null}""")]
    [InlineData("""{"schemaVersion":"1"}""")]
    [InlineData("""{"schemaVersion":-1}""")]
    [InlineData("""{"schemaVersion":1.5}""")]
    [InlineData("""{"schemaVersion":1.0}""")]
    [InlineData("""{"schemaVersion":1e0}""")]
    [InlineData("""{"schemaVersion":true}""")]
    [InlineData("""{"schemaVersion":99999999999}""")]
    [InlineData("""{"schemaVersion":1,"SchemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":2}""")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{not json")]
    public void Codec_MalformedSchemaVersion_IsRejected(string json)
    {
        var ex = Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.ReadSchemaVersion(json));
        Assert.Equal(EditorProjectSchemaErrors.Invalid, ex.Code);
        Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.EnsureSupported(json));
        Assert.Throws<EditorProjectSchemaException>(() => EditorProjectCodec.StampSchemaVersion(json));
    }

    [Fact]
    public void Codec_StampLegacy_AddsCurrentVersion_AndKeepsContent()
    {
        var stamped = EditorProjectCodec.StampSchemaVersion("""{"Tracks":[{"Id":"t1"}],"Duration":12.5}""");
        Assert.Equal(EditorProjectCodec.CurrentSchemaVersion, EditorProjectCodec.ReadSchemaVersion(stamped));
        using var document = JsonDocument.Parse(stamped);
        Assert.Equal(EditorProjectCodec.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("t1", document.RootElement.GetProperty("Tracks")[0].GetProperty("Id").GetString());
        Assert.Equal(12.5, document.RootElement.GetProperty("Duration").GetDouble());
    }

    [Fact]
    public void Codec_StampCurrent_IsUnchanged_AndExistingSpellingIsReused()
    {
        const string current = """{"schemaVersion":1,"Tracks":[]}""";
        Assert.Equal(current, EditorProjectCodec.StampSchemaVersion(current));

        var stamped = EditorProjectCodec.StampSchemaVersion("""{"SchemaVersion":0,"Tracks":[]}""");
        using var document = JsonDocument.Parse(stamped);
        var spellings = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => string.Equals(name, "schemaVersion", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal("SchemaVersion", Assert.Single(spellings));
        Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    // ---- Migration 8 and the supported-max schema guard ----

    [Fact]
    public void Migrate_AddsRevisionColumnAndMarker_AndIsIdempotent()
    {
        var studio = NewStudio();
        studio.Migrate();
        new Store(DatabasePath).Migrate();
        new StudioStore(DatabasePath).Migrate();
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM pragma_table_info('editor_projects') WHERE name='revision'"));
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM schema_migrations WHERE version=8"));
        Assert.Equal(StudioStore.SupportedSchemaVersion, Count("SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(StudioStore.SupportedSchemaVersion, StudioStore.ProbeSchemaVersion(DatabasePath));
        StudioStore.EnsureSchemaSupported(DatabasePath);
    }

    [Fact]
    public void Migrate_StudioFirst_ThenStore_StillRunsStoreMigrations()
    {
        new StudioStore(DatabasePath).Migrate();
        new Store(DatabasePath).Migrate();
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM schema_migrations WHERE version=1"));
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM schema_migrations WHERE version=7"));
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM schema_migrations WHERE version=8"));
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rooms'"));
    }

    [Fact]
    public void Migrate_LegacyRows_BecomeRevisionOne_WithJsonUntouched()
    {
        new Store(DatabasePath).Migrate();
        Exec("""CREATE TABLE editor_projects(clip_id TEXT PRIMARY KEY,json TEXT NOT NULL,updated_at TEXT NOT NULL); INSERT INTO editor_projects VALUES('legacy','{"Tracks":[]}','2026-01-01T00:00:00.0000000+00:00');""");
        var studio = new StudioStore(DatabasePath);
        studio.Migrate();

        Assert.Equal(1L, studio.GetProjectRevision("legacy"));
        var record = studio.GetProjectRecord("legacy");
        Assert.NotNull(record);
        Assert.Equal("""{"Tracks":[]}""", record!.Json);
        Assert.Equal(1L, record.Revision);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), record.UpdatedAt);
        Assert.Equal("""{"Tracks":[]}""", studio.Project("legacy"));
        Assert.Equal(0L, studio.GetProjectRevision("missing"));
        Assert.Null(studio.GetProjectRecord("missing"));

        var next = studio.SaveProjectCas("legacy", """{"Tracks":[1]}""", 1);
        Assert.True(next.Ok);
        Assert.Equal(2L, next.Revision);
    }

    [Fact]
    public void NewerDatabase_IsRefused_BeforeAnyChange()
    {
        NewStudio();
        Exec($"INSERT INTO schema_migrations(version) VALUES({StudioStore.SupportedSchemaVersion+1});");
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(DatabasePath);

        var probe = Assert.Throws<StorageSchemaTooNewException>(() => StudioStore.EnsureSchemaSupported(DatabasePath));
        Assert.Equal("storage-schema-too-new", probe.Code);
        Assert.Equal(StorageSchemaTooNewException.ErrorCode, probe.Code);
        Assert.Equal(StudioStore.SupportedSchemaVersion+1, probe.SchemaVersion);
        Assert.Equal(StudioStore.SupportedSchemaVersion, probe.SupportedVersion);

        var fresh = new StudioStore(DatabasePath);
        Assert.Throws<StorageSchemaTooNewException>(() => fresh.Migrate());
        Assert.Throws<StorageSchemaTooNewException>(() => fresh.SaveProjectCas("clip", "{}", null));
        Assert.Throws<StorageSchemaTooNewException>(() => fresh.Project("clip"));

        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
        Assert.Equal(StudioStore.SupportedSchemaVersion+1, StudioStore.ProbeSchemaVersion(DatabasePath));
    }

    [Fact]
    public void Probe_MissingDatabase_ReturnsZero_AndDoesNotCreateIt()
    {
        var path = Path.Combine(_root, "none.db");
        Assert.Equal(0L, StudioStore.ProbeSchemaVersion(path));
        StudioStore.EnsureSchemaSupported(path);
        Assert.False(File.Exists(path));
    }

    // ---- SaveProjectCas ----

    [Fact]
    public void Cas_FirstInsertExpectsZero_StaleRevisionConflicts_AndWritesNothing()
    {
        var studio = NewStudio();
        Assert.Equal(0L, studio.GetProjectRevision("clip"));

        var first = studio.SaveProjectCas("clip", """{"v":1}""", 0);
        Assert.True(first.Ok);
        Assert.Equal(1L, first.Revision);

        var second = studio.SaveProjectCas("clip", """{"v":2}""", 1);
        Assert.True(second.Ok);
        Assert.Equal(2L, second.Revision);

        var stale = studio.SaveProjectCas("clip", """{"v":3}""", 1);
        Assert.False(stale.Ok);
        Assert.Equal(2L, stale.Revision);
        Assert.Equal("""{"v":2}""", studio.Project("clip"));
        Assert.Equal(2L, studio.GetProjectRevision("clip"));
        Assert.Single(studio.ProjectRevisions("clip"));

        var missing = studio.SaveProjectCas("other", "{}", 5);
        Assert.False(missing.Ok);
        Assert.Equal(0L, missing.Revision);
        Assert.Null(studio.Project("other"));
    }

    [Fact]
    public void Cas_Unconditional_AndLegacySaveProject_BumpRevisionEveryCommit()
    {
        var studio = NewStudio();
        studio.SaveProject("clip", "{}");
        Assert.Equal(1L, studio.GetProjectRevision("clip"));
        studio.SaveProject("clip", "{}");
        Assert.Equal(2L, studio.GetProjectRevision("clip"));

        var forced = studio.SaveProjectCas("clip", """{"x":1}""", null);
        Assert.True(forced.Ok);
        Assert.Equal(3L, forced.Revision);
        Assert.Null(forced.BackupError);
        Assert.Equal("""{"x":1}""", studio.Project("clip"));
        var record = studio.GetProjectRecord("clip");
        Assert.NotNull(record);
        Assert.Equal(3L, record!.Revision);
        Assert.Equal("""{"x":1}""", record.Json);
    }

    [Fact]
    public void Cas_RejectsOutOfRangeExpectedRevision()
    {
        var studio = NewStudio();
        Assert.Throws<ArgumentOutOfRangeException>(() => studio.SaveProjectCas("clip", "{}", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => studio.SaveProjectCas("clip", "{}", StudioStore.MaxProjectRevision + 1));
        Assert.Null(studio.Project("clip"));
    }

    [Fact]
    public void Cas_RevisionCeiling_FailsClosed()
    {
        var studio = NewStudio();
        studio.SaveProject("clip", "{}");
        Exec($"UPDATE editor_projects SET revision={StudioStore.MaxProjectRevision} WHERE clip_id='clip'");
        Assert.Throws<InvalidOperationException>(() => studio.SaveProjectCas("clip", """{"x":1}""", StudioStore.MaxProjectRevision));
        Assert.Equal("{}", studio.Project("clip"));
        Assert.Equal(StudioStore.MaxProjectRevision, studio.GetProjectRevision("clip"));
    }

    [Fact]
    public void Cas_FailedWrite_RollsBackJsonAndRevisionTogether_AndWritesNoBackup()
    {
        var studio = NewStudio();
        studio.SaveProject("clip", """{"v":1}""");
        Exec("CREATE TRIGGER fail_editor_update BEFORE UPDATE ON editor_projects BEGIN SELECT RAISE(ABORT, 'r51x2-save-failure'); END;");

        Assert.Throws<SqliteException>(() => studio.SaveProjectCas("clip", """{"v":2}""", 1));
        Assert.Equal("""{"v":1}""", studio.Project("clip"));
        Assert.Equal(1L, studio.GetProjectRevision("clip"));
        Assert.Empty(studio.ProjectRevisions("clip"));

        Exec("DROP TRIGGER fail_editor_update;");
        var retry = studio.SaveProjectCas("clip", """{"v":2}""", 1);
        Assert.True(retry.Ok);
        Assert.Equal(2L, retry.Revision);
        Assert.Single(studio.ProjectRevisions("clip"));
    }

    [Fact]
    public void Cas_TwoStoresWithSameExpectedRevision_OnlyOneWins()
    {
        var studio = NewStudio();
        studio.SaveProject("clip", """{"v":0}""");
        var other = new StudioStore(DatabasePath);

        var a = studio.SaveProjectCas("clip", """{"v":"a"}""", 1);
        var b = other.SaveProjectCas("clip", """{"v":"b"}""", 1);

        Assert.True(a.Ok);
        Assert.False(b.Ok);
        Assert.Equal(2L, b.Revision);
        Assert.Equal("""{"v":"a"}""", other.Project("clip"));
    }

    [Fact]
    public async Task Cas_ParallelWritersWithSameExpectedRevision_ExactlyOneCommits()
    {
        var studio = NewStudio();
        studio.SaveProject("clip", """{"v":0}""");
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
        {
            start.Wait();
            return new StudioStore(DatabasePath).SaveProjectCas("clip", JsonSerializer.Serialize(new { v = i }), 1);
        })).ToArray();
        start.Set();

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Ok);
        Assert.All(results.Where(result => !result.Ok), result => Assert.Equal(2L, result.Revision));
        Assert.Equal(2L, studio.GetProjectRevision("clip"));
    }

    // ---- Project backups ----

    [Fact]
    public void Backup_IsPublishedAfterCommit_WithoutTempLeftovers()
    {
        var studio = NewStudio();
        studio.SaveProject("clip", """{"v":1}""");
        Assert.Empty(studio.ProjectRevisions("clip"));

        var saved = studio.SaveProjectCas("clip", """{"v":2}""", 1);
        Assert.True(saved.Ok);
        Assert.Null(saved.BackupError);

        var backup = Assert.Single(studio.ProjectRevisions("clip"));
        Assert.Equal("""{"v":1}""", studio.ProjectRevision("clip", backup.FileName));
        Assert.Empty(Directory.GetFiles(BackupDirectory("clip"), "*" + DurableFile.TempMarker + "*"));
    }

    [Fact]
    public void BackupFailure_AfterCommit_DoesNotMisreportTheSave()
    {
        var studio = NewStudio();
        studio.SaveProject("clip", """{"v":1}""");
        Directory.CreateDirectory(Path.Combine(_root, "project-backups"));
        File.WriteAllText(BackupDirectory("clip"), "a file where the backup directory should be");

        var saved = studio.SaveProjectCas("clip", """{"v":2}""", 1);

        Assert.True(saved.Ok);
        Assert.Equal(2L, saved.Revision);
        Assert.NotNull(saved.BackupError);
        Assert.Equal(saved.BackupError, studio.LastBackupError);
        Assert.Equal("""{"v":2}""", studio.Project("clip"));
        Assert.Equal(2L, studio.GetProjectRevision("clip"));
    }

    [Fact]
    public void Backups_IgnoreTempNames_PruneToRetention_AndCleanOnlyStaleOwnedTemps()
    {
        var studio = NewStudio();
        var directory = BackupDirectory("clip");
        Directory.CreateDirectory(directory);
        var staleTemp = Path.Combine(directory, "20000101-000000000-stale.json" + DurableFile.TempMarker + "0.tmp");
        var freshTemp = Path.Combine(directory, "20000101-000000000-fresh.json" + DurableFile.TempMarker + "1.tmp");
        File.WriteAllText(staleTemp, "partial");
        File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(freshTemp, "partial");

        for (var i = 0; i <= StudioStore.ProjectBackupRetention + 2; i++)
            studio.SaveProject("clip", JsonSerializer.Serialize(new { v = i }));

        var revisions = studio.ProjectRevisions("clip");
        Assert.Equal(StudioStore.ProjectBackupRetention, revisions.Count);
        Assert.All(revisions, revision => Assert.DoesNotContain(DurableFile.TempMarker, revision.FileName));
        Assert.False(File.Exists(staleTemp));
        Assert.True(File.Exists(freshTemp));
    }

    // ---- DurableFile (settings and backup writes) ----

    [Fact]
    public void DurableFile_ReplacesContent_AndLeavesNoTemp()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "old");
        DurableFile.WriteAllText(path, """{"StorageRoot":"x"}""");
        Assert.Equal("""{"StorageRoot":"x"}""", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*" + DurableFile.TempMarker + "*"));

        var nested = Path.Combine(_root, "new-dir", "file.json");
        DurableFile.WriteAllText(nested, "[]");
        Assert.Equal("[]", File.ReadAllText(nested));
    }

    [Fact]
    public void DurableFile_FailedPublish_RemovesTemp_AndLeavesDestination()
    {
        var destination = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");

        var error = Record.Exception(() => DurableFile.WriteAllText(destination, "new"));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.Empty(Directory.GetFiles(_root, "*" + DurableFile.TempMarker + "*"));
    }

    [Fact]
    public void ReadJson_CorruptFile_IsPreservedAndReported_NotDefaulted()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{ truncated");

        var load = DurableFile.ReadJson<LiveSettings>(path);

        Assert.True(load.Exists);
        Assert.True(load.Corrupt);
        Assert.False(load.Ok);
        Assert.Null(load.Value);
        Assert.NotNull(load.Error);
        Assert.NotNull(load.PreservedCorruptPath);
        Assert.StartsWith(Path.GetFullPath(path) + ".corrupt-", load.PreservedCorruptPath);
        Assert.Equal("{ truncated", File.ReadAllText(load.PreservedCorruptPath!));
        Assert.Equal("{ truncated", File.ReadAllText(path));

        var nullPath = Path.Combine(_root, "null.json");
        File.WriteAllText(nullPath, "null");
        var nullLoad = DurableFile.ReadJson<LiveSettings>(nullPath);
        Assert.True(nullLoad.Corrupt);
        Assert.NotNull(nullLoad.PreservedCorruptPath);
    }

    [Fact]
    public void ReadJson_MissingAndValidFiles()
    {
        var missing = DurableFile.ReadJson<LiveSettings>(Path.Combine(_root, "absent.json"));
        Assert.False(missing.Exists);
        Assert.False(missing.Corrupt);
        Assert.Null(missing.PreservedCorruptPath);

        var path = Path.Combine(_root, "settings.json");
        DurableFile.WriteAllText(path, JsonSerializer.Serialize(new LiveSettings { StorageRoot = _root }));
        var load = DurableFile.ReadJson<LiveSettings>(path);
        Assert.True(load.Ok);
        Assert.Equal(_root, load.Value!.StorageRoot);
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*"));
    }
}
