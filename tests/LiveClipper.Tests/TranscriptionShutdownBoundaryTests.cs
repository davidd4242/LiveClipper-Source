using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

[CollectionDefinition("TranscriptionShutdownBoundary", DisableParallelization = true)]
public sealed class TranscriptionShutdownBoundaryCollection;

[Collection("TranscriptionShutdownBoundary")]
public sealed class TranscriptionShutdownBoundaryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task StopDuringStallBackoff_DrainsOrAvoidsRetryDescendants()
    {
        await using var f = new Fixture(output, "0.25");
        var clip = f.Clip("stall-backoff");
        f.ExecuteSql("""
            CREATE TRIGGER spec_retry_pending_save BEFORE UPDATE OF status ON jobs
            WHEN NEW.type='transcribe' AND NEW.retry_count=1 AND NEW.status='pending'
                 AND NEW.message='软件退出，下次启动继续转译'
            BEGIN SELECT RAISE(ABORT,'SPEC_RETRY_PENDING_SAVE'); END;
            """);
        var completed = 0;
        f.Service.Completed += _ => Interlocked.Increment(ref completed);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var parent = f.Service.QueueReceipt(clip);
        await f.WaitAsync(() => f.Starts(clip).Length == 1, "real stalled worker PID", deadline);
        var worker = f.OpenWorker(Assert.Single(f.Starts(clip)));
        await f.WaitAsync(() => f.Find(parent.JobId).Status == "retried", "parent in stall backoff", deadline);

        // No await between the two database observations and the synchronous entry into StopAsync.
        var beforeStop = f.Store.Jobs();
        Assert.True(beforeStop.Single(x => x.Id == parent.JobId).Status == "retried"
            && beforeStop.All(x => x.RetryCount != 1), "SETUP FAILED: retry1 already exists before first Stop.");
        var beforeTasks = f.Tasks();
        var firstStop = f.StopOnce();
        var afterEntry = f.Store.Jobs();
        Assert.True(afterEntry.All(x => x.RetryCount != 1), "SETUP FAILED: retry1 exists immediately after Stop entry.");
        output.WriteLine($"window=established parent={parent.JobId}/retried retry1Before=0 retry1AfterEntry=0 snapshotTasks={beforeTasks.Length}");

        var stopError = await f.ObserveAsync(firstStop, deadline);
        var tasks = await f.ObserveAllTasksAsync(deadline);
        await f.WaitAsync(() => worker.HasExited, "stalled worker PID exit", deadline);
        var jobs = f.Store.Jobs();
        var retry = Assert.Single(jobs, x => x.RetryCount == 1);
        var faults = Faults(tasks);
        output.WriteLine($"firstStop={firstStop.Status} error={stopError?.ToString() ?? "<none>"}");
        output.WriteLine($"allTasks={string.Join(",", tasks.Select(t => t.Status))} faults={JsonSerializer.Serialize(faults.Select(e => e.Message))}");
        output.WriteLine($"jobs={JsonSerializer.Serialize(jobs)} workerPid={worker.Id} exited={worker.HasExited} starts={f.Starts(clip).Length} completed={completed}");

        Assert.Single(beforeTasks);
        Assert.Equal(2, jobs.Count);
        Assert.Equal("retried", f.Find(parent.JobId).Status);
        Assert.Equal("pending", retry.Status);
        Assert.Equal(0, Volatile.Read(ref completed));
        Assert.DoesNotContain(jobs, x => x.Status == "completed");
        Assert.Single(f.Starts(clip));
        Assert.All(tasks, t => Assert.True(t.IsCompleted, "Independent task drain did not finish."));
        f.AssertNoTranscript(clip);
        Assert.All(faults, e => AssertSaveFailure(e, "SPEC_RETRY_PENDING_SAVE"));
        if (stopError is not null) AssertSaveFailure(stopError, "SPEC_RETRY_PENDING_SAVE");
        if (faults.Length > 0) Assert.Null(retry.Message);
        Assert.True(faults.Length == 0 || stopError is SqliteException,
            "First StopAsync completed successfully while a retry descendant faulted with SPEC_RETRY_PENDING_SAVE; shutdown must avoid the descendant or drain it and surface its SQLite save failure.");
    }

    [Fact]
    public async Task InitialSaveFailure_DoesNotRegisterOrStartWork()
    {
        await using var f = new Fixture(output, "600");
        var clip = f.Clip("initial-save");
        f.ExecuteSql("""
            CREATE TRIGGER spec_initial_pending_save BEFORE INSERT ON jobs
            WHEN NEW.type='transcribe' AND NEW.retry_count=0 AND NEW.status='pending'
            BEGIN SELECT RAISE(ABORT,'SPEC_INITIAL_PENDING_SAVE'); END;
            """);
        Assert.Empty(f.Tasks());
        var error = Assert.Throws<SqliteException>(() => f.Service.QueueReceipt(clip));
        AssertSaveFailure(error, "SPEC_INITIAL_PENDING_SAVE");
        Assert.Empty(f.Store.Jobs());
        Assert.Empty(f.Tasks());
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var stopError = await f.ObserveAsync(f.StopOnce(), deadline);
        Assert.Null(stopError);
        Assert.Empty(await f.ObserveAllTasksAsync(deadline));
        Assert.Empty(f.Starts(clip));
        Assert.False(Directory.Exists(f.Output(clip)));
        f.AssertNoTranscript(clip);
        output.WriteLine($"initialSave={error.Message} tasks=0 starts=0 jobs=0 outputExists=false firstStop=RanToCompletion");
    }

    [Fact]
    public async Task StopWriteFailure_StillDrainsSnapshotAndReleasesWorker()
    {
        await using var f = new Fixture(output, "600");
        var clip = f.Clip("stop-save");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var receipt = f.Service.QueueReceipt(clip);
        await f.WaitAsync(() => f.Starts(clip).Length == 1 && f.Find(receipt.JobId).Status == "running",
            "real running worker before stop write failure", deadline);
        var worker = f.OpenWorker(Assert.Single(f.Starts(clip)));
        Assert.False(worker.HasExited);
        var snapshot = f.Tasks();
        Assert.Single(snapshot);
        f.ExecuteSql("""
            CREATE TRIGGER spec_stop_pending_save BEFORE UPDATE OF status ON jobs
            WHEN NEW.type='transcribe' AND NEW.status='pending'
                 AND NEW.message='软件退出，下次启动继续转译'
            BEGIN SELECT RAISE(ABORT,'SPEC_STOP_PENDING_SAVE'); END;
            """);
        var stopError = await f.ObserveAsync(f.StopOnce(), deadline);
        var drainedWhenStopSettled = snapshot.All(t => t.IsCompleted);
        var tasks = await f.ObserveAllTasksAsync(deadline);
        await f.WaitAsync(() => worker.HasExited, "cancelled worker PID exit", deadline);
        output.WriteLine($"firstStopError={stopError?.ToString() ?? "<none>"} snapshotDrainedAtStop={drainedWhenStopSettled} allTasks={string.Join(",", tasks.Select(t => t.Status))}");
        output.WriteLine($"workerPid={worker.Id} exited={worker.HasExited} runningEmpty={f.RunningIsEmpty} semaphoreCount={f.WorkerCount} starts={f.Starts(clip).Length}");

        AssertSaveFailure(stopError, "SPEC_STOP_PENDING_SAVE");
        Assert.True(drainedWhenStopSettled, "Faulting Stop must still await every snapshot task.");
        Assert.Equal(snapshot, tasks);
        Assert.All(tasks, t => Assert.True(t.IsCompleted));
        AssertSaveFailure(Assert.Single(Faults(tasks)), "SPEC_STOP_PENDING_SAVE");
        Assert.True(worker.HasExited, "Worker PID must exit independently of task settlement.");
        Assert.True(f.RunningIsEmpty);
        Assert.Equal(1, f.WorkerCount);
        Assert.Single(f.Starts(clip));
        Assert.Equal("running", f.Find(receipt.JobId).Status);
        f.AssertNoTranscript(clip);
    }

    private static Exception[] Faults(IEnumerable<Task> tasks) => tasks
        .Where(t => t.IsFaulted).SelectMany(t => t.Exception!.Flatten().InnerExceptions).ToArray();

    private static void AssertSaveFailure(Exception? error, string marker)
    {
        var sqlite = Assert.IsType<SqliteException>(error);
        Assert.Equal(19, sqlite.SqliteErrorCode);
        Assert.Contains(marker, sqlite.Message);
    }

    private sealed record WorkerStart(int Pid, string Executable, string Script, string StartedAt);

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Node = @"C:\Program Files\nodejs\node.exe";
        private readonly string root = Path.Combine(Path.GetTempPath(), "lc-shutdown-boundary-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> oldEnvironment = new();
        private readonly List<Process> workers = [];
        private readonly ITestOutputHelper output;
        private Task? firstStop;
        public string Database => Path.Combine(root, "store.db");
        public string Script => Path.Combine(root, "stall-transcribe.cjs");
        public Store Store { get; }
        public TranscriptionService Service { get; }
        public bool RunningIsEmpty => Read<ConcurrentDictionary<string, CancellationTokenSource>>("_running").IsEmpty;
        public int WorkerCount => Read<SemaphoreSlim>("_worker").CurrentCount;

        public Fixture(ITestOutputHelper output, string stallSeconds)
        {
            this.output = output;
            Assert.True(File.Exists(Node), "SETUP FAILED: explicit Node executable is required; this test must not skip.");
            Directory.CreateDirectory(root);
            File.WriteAllText(Script, """
                const fs = require('node:fs'), path = require('node:path');
                const directory = process.argv[3];
                const marker = path.join(directory, 'worker-starts.jsonl');
                fs.appendFileSync(marker, JSON.stringify({Pid: process.pid, Executable: process.execPath, Script: __filename, StartedAt: new Date().toISOString()}) + '\n');
                process.stderr.write('PROGRESS 0.1\n');
                setInterval(() => {}, 1000);
                """);
            Store = new Store(Database);
            Store.Migrate();
            var settings = new LiveSettings
            {
                StorageRoot = root, PythonPath = Node, WhisperModel = "tiny",
                FfmpegPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe"), CleanupOnExit = false
            };
            Service = new TranscriptionService(Store, settings, new MediaTools(settings.FfmpegPath));
            SetEnvironment("LIVECLIPPER_TRANSCRIBE_SCRIPT", Script);
            SetEnvironment("LIVECLIPPER_TRANSCRIBE_STALL_SECONDS", stallSeconds);
            SetEnvironment("LIVECLIPPER_TRANSCRIBE_CHUNK_SECONDS", "1800");
            foreach (var name in new[] { "LIVECLIPPER_SOURCE", "TRACE", "BLESS", "EVIDENCE" }) SetEnvironment(name, null);
            output.WriteLine($"fixtureRoot={root} node={Node} stallSeconds={stallSeconds}");
        }

        public Clip Clip(string id)
        {
            var clip = new Clip(id, "event-" + id, Path.Combine(root, id + ".mp4"), null, DateTimeOffset.UtcNow);
            var ffmpeg=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Links","ffmpeg.exe");
            Assert.True(File.Exists(ffmpeg),"Real audio normalization requires installed FFmpeg");
            new MediaTools(ffmpeg).RunAsync(ffmpeg,["-n","-f","lavfi","-i","sine=frequency=440:duration=1","-c:a","aac",clip.Path],default).GetAwaiter().GetResult();
            Store.SaveClip(clip);
            return clip;
        }

        public string Output(Clip clip) => Path.Combine(root, "transcripts", clip.Id);
        public Job Find(string id) => Assert.Single(Store.Jobs(), x => x.Id == id);
        public Task StopOnce() => firstStop ??= Service.StopAsync();

        private T Read<T>(string name) where T : class => Assert.IsType<T>(typeof(TranscriptionService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service));

        public Task[] Tasks()
        {
            var tasks = Read<List<Task>>("_tasks");
            lock (tasks) return tasks.ToArray();
        }

        private readonly Dictionary<string,WorkerStart[]> observedStarts=new();
        public WorkerStart[] Starts(Clip clip)
        {
            var directory=Path.Combine(root,"render-work","transcription-audio");
            var jobs=Store.Jobs().Where(j=>j.EventId==clip.EventId).Select(j=>Path.Combine(directory,j.Id)).Where(Directory.Exists).ToArray();
            var text=string.Join("",jobs.SelectMany(j=>Directory.GetFiles(j,"worker-starts.jsonl",SearchOption.AllDirectories)).Select(File.ReadAllText));
            // Ignore a trailing partial append until the complete PID record is visible.
            var found=text.Split('\n').SkipLast(1).Where(x=>x.Length>0).Select(x=>JsonSerializer.Deserialize<WorkerStart>(x)!).ToArray();
            var prior=observedStarts.GetValueOrDefault(clip.Id,[]);
            return observedStarts[clip.Id]=prior.Concat(found).DistinctBy(x=>x.Pid).ToArray();
        }

        public Process OpenWorker(WorkerStart start)
        {
            Assert.True(string.Equals(Path.GetFullPath(Node), Path.GetFullPath(start.Executable), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Script, start.Script);
            var process = Process.GetProcessById(start.Pid);
            Assert.True(string.Equals(Path.GetFullPath(Node), process.MainModule!.FileName, StringComparison.OrdinalIgnoreCase));
            _ = process.Handle;
            workers.Add(process);
            output.WriteLine($"workerStarted pid={start.Pid} at={start.StartedAt} executable={start.Executable} script={start.Script}");
            return process;
        }

        public void ExecuteSql(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void AssertNoTranscript(Clip clip)
        {
            Assert.Null(Store.TranscriptJson(clip.EventId));
            if(Directory.Exists(Output(clip)))
            {
                Assert.Empty(Directory.GetFiles(Output(clip),"transcript.json",SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(Output(clip),"transcript.srt",SearchOption.AllDirectories));
            }
        }

        public async Task WaitAsync(Func<bool> predicate, string label, DateTime deadline)
        {
            while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(predicate(), "SETUP FAILED: " + label + " timed out; jobs=" + JsonSerializer.Serialize(Store.Jobs()));
        }

        public async Task<Exception?> ObserveAsync(Task task, DateTime deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            Assert.True(remaining > TimeSpan.Zero, "SETUP FAILED: 15 second deadline exceeded before task observation.");
            try { await task.WaitAsync(remaining); return null; }
            catch (TimeoutException) { throw new TimeoutException("SETUP FAILED: task did not settle within the 15 second deadline."); }
            catch (Exception error) { return error; }
        }

        public async Task<Task[]> ObserveAllTasksAsync(DateTime deadline)
        {
            // Parents can append descendants before settling; reread only after observing the whole prior snapshot.
            while (true)
            {
                var snapshot = Tasks();
                await ObserveAsync(Task.WhenAll(snapshot), deadline);
                var complete = Tasks();
                if (snapshot.SequenceEqual(complete)) return complete;
                Assert.True(DateTime.UtcNow < deadline, "SETUP FAILED: descendant task set did not settle within 15 seconds.");
            }
        }

        private void SetEnvironment(string name, string? value)
        {
            oldEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                var error = await ObserveAsync(StopOnce(), deadline);
                var tasks = await ObserveAllTasksAsync(deadline);
                foreach (var clip in Store.Clips())
                {
                    foreach (var start in Starts(clip))
                    {
                        // A captured handle identifies our original process even if its numeric PID is later reused.
                        var process = workers.FirstOrDefault(p => p.Id == start.Pid);
                        if (process is null)
                        {
                            try { process = OpenWorker(start); }
                            catch (ArgumentException) { output.WriteLine($"cleanup pid={start.Pid} exited=true (no process)"); continue; }
                        }
                        await WaitAsync(() => process.HasExited, "cleanup worker PID exit", deadline);
                        output.WriteLine($"cleanup pid={start.Pid} exited={process.HasExited}");
                    }
                }
                output.WriteLine($"cleanup firstStopCalls=1 firstStopError={error?.Message ?? "<none>"} tasks={tasks.Length} settled={tasks.All(t => t.IsCompleted)} faults={Faults(tasks).Length}");
            }
            finally
            {
                foreach (var pair in oldEnvironment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                foreach (var worker in workers)
                {
                    if (!worker.HasExited)
                    {
                        output.WriteLine($"cleanup forcedKill pid={worker.Id} (verified retained process handle)");
                        worker.Kill(true);
                        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    worker.Dispose();
                }
                ExecuteSql("DROP TRIGGER IF EXISTS spec_retry_pending_save; DROP TRIGGER IF EXISTS spec_initial_pending_save; DROP TRIGGER IF EXISTS spec_stop_pending_save;");
                SqliteConnection.ClearAllPools();
                var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
                Assert.True(Path.GetFullPath(root).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(root).StartsWith("lc-shutdown-boundary-", StringComparison.Ordinal));
                Directory.Delete(root, true);
            }
        }
    }
}
