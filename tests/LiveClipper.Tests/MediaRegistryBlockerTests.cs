using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
using Microsoft.Extensions.Primitives;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

[CollectionDefinition("Media registry blocker isolation", DisableParallelization = true)]
public sealed class MediaRegistryBlockerCollection;

[Collection("Media registry blocker isolation")]
public sealed class MediaRegistryBlockerTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData("editor", false, false)]
    [InlineData("commands", false, false)]
    [InlineData("editor", true, false)]
    [InlineData("commands", true, false)]
    [InlineData("editor", false, true)]
    [InlineData("commands", false, true)]
    public async Task DeletedTarget_AfterOuterLookup_Is404WithoutCommit(string route, bool session, bool existing)
    {
        var owner = new TeardownOwner(output);
        Action? finalAssertions = null;
        try
        {
            using var call = owner.Enter("B01.body");
            var fixture = owner.CreateFixture(existing);
            owner.InstallTrace(_ => { });
            var pause = new Pause(owner);
            var before = fixture.Studio.GetProjectRecord(fixture.Clip.Id);
            var undo = EditorRevisionStore.SnapshotDepth(fixture.Clip.Id);
            var save = owner.StartAsync("save", () => fixture.RequestAsync(route, pause));
            pause.WaitUntilReached(); // EpochHeader follows ClipById and commands' DurationAsync.
            if (session) fixture.Store.DeleteSession(fixture.Session, fixture.Root);
            else fixture.Store.DeleteClip(fixture.Clip, fixture.Root);
            Assert.DoesNotContain(fixture.Store.Clips(), clip => clip.Id == fixture.Clip.Id);
            finalAssertions = () =>
            {
                var reply = save.GetAwaiter().GetResult();
                var after = fixture.Studio.GetProjectRecord(fixture.Clip.Id);
                output.WriteLine($"B-01 route={route} session={session} existing={existing} HTTP={reply.Status} beforeRevision={before?.Revision ?? 0} afterRevision={after?.Revision ?? 0} orphan={after is not null && !fixture.Store.Clips().Any(c => c.Id == fixture.Clip.Id)}");
                Assert.Multiple(
                    () => Assert.Equal(StatusCodes.Status404NotFound, reply.Status),
                    () => Assert.Equal(StudioErrorCodes.NotFound, reply.Body.GetProperty("code").GetString()),
                    () => Assert.Equal(before, after),
                    () => Assert.Equal(undo, EditorRevisionStore.SnapshotDepth(fixture.Clip.Id)));
            };
        }
        catch (Exception ex) { owner.Capture("B01 body", ex); }
        finally { await owner.FinishAsync(finalAssertions); }
        owner.ThrowIfFailed();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LocalDelete_CannotBypassInFlightPut_OrLeaveAnOrphan(bool afterCommit) =>
        VerifyLocalDelete(afterCommit, withDecoy: false);

    [Fact]
    public Task DecoyContention_DoesNotProveEntryGateArrival() =>
        VerifyLocalDelete(afterCommit: false, withDecoy: true);

    [Fact]
    public Task Calibration_RearmedThreadRejectsBufferedEvent() => VerifyLocalDelete(afterCommit: false, withDecoy: true, rearmWithBufferedEvent: true);

    private async Task<TeardownOwner> VerifyLocalDelete(bool afterCommit, bool withDecoy, bool rearmWithBufferedEvent = false, Probe? probe = null, bool captureErrors = false)
    {
        var owner = new TeardownOwner(output, probe);
        Action? finalAssertions = null;
        try
        {
        using var call = owner.Enter("B02.body");
        var fixture = owner.CreateFixture(existing: afterCommit);
        var entries = typeof(EditorRevisionStore).GetField("Entries", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IDictionary
            ?? throw new InvalidOperationException("INCOMPLETE: EditorRevisionStore.Entries is unavailable.");
        var entry = entries[fixture.Clip.Id] ?? throw new InvalidOperationException("INCOMPLETE: clip Entry is unavailable.");
        var exactGate = entry.GetType().GetField("Gate", BindingFlags.Instance | BindingFlags.Public)?.GetValue(entry)
            ?? throw new InvalidOperationException("INCOMPLETE: Entry.Gate is unavailable.");
        owner.Keep(entry); owner.Keep(exactGate);
        var decoy = withDecoy ? owner.Keep(new object()) : null;
        var pause = new Pause(owner, exactGate, decoy);
        var cleanupReached = owner.Event("cleanupReached", new ManualResetEventSlim());
        var rearmed = owner.Event("rearmed", new ManualResetEventSlim());
        var continueDeletion = owner.Event("continueDeletion", new ManualResetEventSlim());
        owner.ReleaseWith("continueDeletion", continueDeletion.Set);
        var buffered = rearmWithBufferedEvent ? new BufferedContention(owner) : null;
        var contention = owner.Observe(buffered);
        var calibration = new GateCalibration(owner, exactGate);
        var trace = owner.InstallTrace(sql =>
        {
            if (afterCommit && SqlTrace.Operation.Value == "put") output.WriteLine("PUT SQL profile: " + sql);
            if (SqlTrace.Operation.Value == "put" &&
                (afterCommit ? sql.TrimEnd(';').Trim().Equals("COMMIT", StringComparison.OrdinalIgnoreCase) : sql.StartsWith("SELECT id,name,kind,style,path,built_in FROM audio_assets", StringComparison.Ordinal)))
                pause.Stop();
            if (SqlTrace.Operation.Value == "delete" && sql == "DELETE FROM local_videos WHERE clip_id=$id")
                cleanupReached.Set();
        }, pause.Invalidate);
        var beforeRevision = fixture.Studio.GetProjectRevision(fixture.Clip.Id);
        var save = owner.StartAsync("save", async () =>
        {
            SqlTrace.Operation.Value = "put";
            try { return await fixture.RequestAsync("editor"); }
            finally { SqlTrace.Operation.Value = null; }
        });
        Task? deletion = null;
        long duringRevision = -1;
        bool cleanupWhileReading = false;
            pause.WaitUntilReached();
            Assert.True(pause.HeldRead, "The real PUT must hold the registry read gate at the SQL barrier.");
            Assert.True(pause.HeldExactGate, "INCOMPLETE: the SQL barrier must own the reflected Entry.Gate.");
            calibration.Start(contention, pause);
            duringRevision = fixture.Studio.GetProjectRevision(fixture.Clip.Id);
            Assert.Equal(afterCommit ? beforeRevision + 1 : 0, duringRevision);
            deletion = owner.Start("deletion", () =>
            {
                SqlTrace.Operation.Value = "delete";
                var previousActivity = EventSource.CurrentThreadActivityId;
                try
                {
                    contention.WatchCurrentThread(pause);
                    if (decoy is not null) { Monitor.Enter(decoy); Monitor.Exit(decoy); }
                    if (buffered is not null)
                    {
                        contention.WatchCurrentThread(pause);
                        rearmed.Set();
                        if (!continueDeletion.Wait(pause.Remaining)) throw new InvalidOperationException("INCOMPLETE: rearmed delete worker was not released.");
                    }
                    // Preserve the original route's schedule; coordinated deletion makes the trailing lookup a no-op.
                    fixture.Studio.DeleteLocalVideo(fixture.Clip.Id);
                    var clip = fixture.Store.Clips().FirstOrDefault(c => c.Id == fixture.Clip.Id);
                    if (clip is not null) fixture.Store.DeleteClip(clip, fixture.Root);
                }
                finally
                {
                    owner.Try("delete StopWatching", contention.StopWatching);
                    owner.Try("delete activity restore", () => EventSource.SetCurrentThreadActivityId(previousActivity));
                    SqlTrace.Operation.Value = null;
                }
            });
            if (buffered is not null)
            {
                Assert.True(buffered.Captured.Wait(pause.Remaining), "INCOMPLETE: no real contention event was buffered.");
                pause.ReleaseDecoy();
                Assert.True(rearmed.Wait(pause.Remaining), "INCOMPLETE: the deletion thread did not rearm.");
                buffered.Release();
                var staleObserved = WaitHandle.WaitAny([contention.StaleRejected.WaitHandle, contention.Failed.WaitHandle], pause.Remaining);
                contention.RequireSignal(staleObserved);
                pause.AssertActive();
                Assert.Multiple(
                    () => Assert.True(contention.StaleRejected.IsSet, "The buffered old generation must be rejected."),
                    () => Assert.False(contention.Processed.IsSet, "An old generation must not count as current decoy processing."),
                    () => Assert.False(contention.Reached.IsSet, "An old generation must not count as exact Entry.Gate arrival."),
                    () => Assert.False(cleanupReached.IsSet));
                output.WriteLine("T01 staleRejected=True processed=False exactReached=False; continuing real delete.");
                continueDeletion.Set();
            }
            else if (withDecoy)
            {
                var decoyObserved = WaitHandle.WaitAny([contention.Processed.WaitHandle, contention.Failed.WaitHandle], pause.Remaining);
                contention.RequireSignal(decoyObserved);
                pause.AssertActive();
                Assert.True(contention.Processed.IsSet, "INCOMPLETE: no decoy contention was processed.");
                output.WriteLine($"T01 decoyProcessed={contention.Processed.IsSet} exactReached={contention.Reached.IsSet} cleanup={cleanupReached.IsSet} heldExact={pause.HeldExactGate} heldRead={pause.HeldRead} owner={pause.OwnerThreadId} active={pause.Active}");
                Assert.Multiple(
                    () => Assert.False(contention.Reached.IsSet, "Decoy contention must not count as exact Entry.Gate arrival."),
                    () => Assert.False(cleanupReached.IsSet),
                    () => Assert.True(pause.Active));
                pause.ReleaseDecoy();
            }
            var observed = WaitHandle.WaitAny([cleanupReached.WaitHandle, contention.Reached.WaitHandle, contention.Failed.WaitHandle], pause.Remaining);
            contention.RequireSignal(observed);
            Assert.NotEqual(WaitHandle.WaitTimeout, observed);
            pause.AssertActive();
            cleanupWhileReading = cleanupReached.IsSet;
            duringRevision = fixture.Studio.GetProjectRevision(fixture.Clip.Id);
            pause.AssertActive();
            if (!cleanupWhileReading) Assert.True(contention.Reached.IsSet, "INCOMPLETE: deletion did not reach exact E.");
            output.WriteLine($"T01 exactReached={contention.Reached.IsSet} cleanup={cleanupWhileReading} active={pause.Active} window={pause.Window} remainingMs={pause.Remaining.TotalMilliseconds:F0}");
        finalAssertions = () =>
        {
        var reply = save.GetAwaiter().GetResult();
        trace.ThrowIfCallbackFailed();
        contention.ThrowIfIncomplete();
        GC.KeepAlive(exactGate);
        var orphan = fixture.Studio.GetProjectRecord(fixture.Clip.Id);
        output.WriteLine($"B-02 afterCommit={afterCommit} cleanupWhileReadHeld={cleanupWhileReading} revisionDuringDelete={duringRevision} HTTP={reply.Status} finalClip={fixture.Store.Clips().Any(c => c.Id == fixture.Clip.Id)} finalProjectRevision={orphan?.Revision ?? 0}");
        Assert.Multiple(
            () => Assert.False(cleanupWhileReading, "Local deletion bypassed the PUT's E/R boundary and deleted editor state while its read lock was held."),
            () => Assert.Equal(afterCommit ? beforeRevision + 1 : 0, duringRevision),
            () => Assert.Equal(StatusCodes.Status200OK, reply.Status),
            () => Assert.DoesNotContain(fixture.Store.Clips(), c => c.Id == fixture.Clip.Id),
            () => Assert.Null(orphan));
        };
        }
        catch (Exception ex) { owner.Capture("B02 body", ex); }
        finally { await owner.FinishAsync(finalAssertions); }
        if (!captureErrors) owner.ThrowIfFailed();
        return owner;
    }

    [Fact]
    public async Task Teardown_DelayedWorkerRetainsResourcesUntilExit()
    {
        var probe = new Probe { DelayWorker = true };
        var owner = new TeardownOwner(output, probe);
        Task<Reply>? original = null;
        Task? finish = null;
        try
        {
            using (owner.Enter("probe.setup"))
            {
                var fixture = owner.CreateFixture(existing: false);
                owner.InstallTrace(_ => { });
                original = owner.StartAsync("probe.put", () => fixture.RequestAsync("editor"));
            }
            Assert.True(probe.Held.Wait(Budget), "INCOMPLETE: real worker did not reach controlled exit hold.");
            finish = owner.FinishAsync(() => Assert.Equal(StatusCodes.Status200OK, original.GetAwaiter().GetResult().Status));
            var draining = await probe.Draining.Task.WaitAsync(Budget);
            Assert.Same(owner, draining);
            Assert.False(original.IsCompleted);
            Assert.True(owner.IsRooted);
            Assert.False(owner.EventsDisposed);
            Assert.False(owner.RootsReleased);
            Assert.False(owner.FixtureDisposed);
        }
        catch (Exception ex) { owner.Capture("delayed-worker controller", ex); }
        finally
        {
            probe.Release();
            if (finish is null) finish = owner.FinishAsync(null);
            await finish;
        }
        Assert.True(owner.AllStartedTasksTerminal);
        Assert.True(owner.EventsDisposed);
        Assert.True(owner.RootsReleased);
        Assert.True(owner.FixtureDisposed);
        AssertCleanupOrder(owner);
        owner.ThrowIfFailed();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Teardown_CallbackFaultDrainsBeforeDisposal(bool inInit)
    {
        var probe = new Probe { FailInit = inInit, FailProfile = !inInit };
        var owner = new TeardownOwner(output, probe);
        try
        {
            using var call = owner.Enter("callback-fault.body");
            var fixture = owner.CreateFixture(existing: false);
            owner.InstallTrace(_ => { }, _ => throw new InjectedFault("notifier"));
            // Real unpooled SQLite open/profile/close, not a direct invocation of the delegate.
            fixture.Studio.GetProjectRecord(fixture.Clip.Id);
        }
        catch (Exception ex) { owner.Capture("callback-fault body", ex); }
        finally { probe.Release(); await owner.FinishAsync(null); }
        Assert.True(probe.Draining.Task.IsCompletedSuccessfully);
        Assert.True(owner.WorkersDrained);
        Assert.True(owner.FixtureDisposed);
        Assert.False(owner.IsIncomplete);
        AssertCleanupOrder(owner);
        var errors = new AggregateException(owner.Errors).Flatten().ToString();
        Assert.Contains("T02 injected " + (inInit ? "init" : "profile"), errors);
        Assert.Contains("T02 injected notifier", errors);
        // Only the precise injected cause and its SQLite-open wrapper are allowed; not timeout/INCOMPLETE.
        foreach (var error in owner.Errors)
        {
            Assert.DoesNotContain("INCOMPLETE", error.ToString());
            Assert.DoesNotContain("TimeoutException", error.ToString());
            Assert.True(ContainsInjected(error) || (inInit && error.InnerException is SqliteException), error.ToString());
        }
    }

    private static bool ContainsInjected(Exception error) => error is InjectedFault ||
        (error is AggregateException aggregate && aggregate.InnerExceptions.Any(ContainsInjected)) ||
        (error.InnerException is { } inner && ContainsInjected(inner));

    [Fact]
    public async Task Teardown_PartialCalibrationStillJoinsStartedThread()
    {
        var probe = new Probe { PartialCalibration = true };
        TeardownOwner? owner = null;
        try { owner = await VerifyLocalDelete(false, false, probe: probe, captureErrors: true); }
        finally { probe.Release(); }
        Assert.NotNull(owner);
        Assert.False(owner.IsIncomplete);
        Assert.True(owner.AllStartedThreadsJoined);
        Assert.True(owner.FixtureDisposed);
        Assert.NotEmpty(owner.Errors);
        Assert.All(owner.Errors, error => Assert.True(ContainsInjected(error), error.ToString()));
        Assert.Contains(owner.Journal, line => line.Contains("thread.register calibration", StringComparison.Ordinal));
        Assert.Contains(owner.Journal, line => line.Contains("thread.joined calibration", StringComparison.Ordinal));
        AssertCleanupOrder(owner);
    }

    private static void AssertCleanupOrder(TeardownOwner owner)
    {
        var lines = owner.Journal.ToArray();
        int At(string marker)
        {
            var index = Array.FindIndex(lines, line => line.Contains(marker, StringComparison.Ordinal));
            Assert.True(index >= 0, "Missing cleanup evidence: " + marker);
            return index;
        }
        var closed = At("admission.closed");
        var draining = At("drain.started");
        var returned = At("final.calls.returned=True");
        var cancel = At("native.cancel.return");
        var proof = At("native.return.proof=True");
        var events = At("event.disposed");
        var roots = At("owner.root.released");
        var app = At("fixture.app.disposed");
        var forget = At("fixture.forget.returned");
        var pools = At("fixture.pools.cleared");
        Assert.True(closed < draining && draining < returned && returned < cancel && cancel < proof);
        Assert.True(proof < events && events < roots && roots < app && app < forget && forget < pools);
        if (owner.Listener is not null)
        {
            var stopped = At("listener.subscription.stopped");
            var drained = At("listener.admitted.callbacks.drained=True");
            Assert.True(proof < stopped && stopped < drained && drained < events);
        }
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains("task.terminal", StringComparison.Ordinal) || lines[i].Contains("thread.joined", StringComparison.Ordinal))
                Assert.True(draining < i && i < returned, lines[i]);
    }

    private sealed class InjectedFault(string point) : Exception("T02 injected " + point);

    // Controllers own only probe holds. They never pretend to cancel a product operation.
    private sealed class Probe : IDisposable
    {
        public bool DelayWorker;
        public bool PartialCalibration;
        public bool FailInit;
        public bool FailProfile;
        public readonly ManualResetEventSlim Held = new();
        public readonly ManualResetEventSlim AllowExit = new();
        public readonly TaskCompletionSource<TeardownOwner> Draining = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _init, _profile;
        public void BeforeWorkerExit()
        {
            if (!DelayWorker) return;
            Held.Set();
            if (!AllowExit.Wait(Budget)) throw new TimeoutException("INCOMPLETE: controlled worker hold was not released.");
        }
        public void InitFault() { if (FailInit && Interlocked.Exchange(ref _init, 1) == 0) throw new InjectedFault("init"); }
        public void ProfileFault() { if (FailProfile && Interlocked.Exchange(ref _profile, 1) == 0) throw new InjectedFault("profile"); }
        private readonly object _sync = new();
        private bool _disposed;
        public void Release() { lock (_sync) { if (!_disposed) AllowExit.Set(); } }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                AllowExit.Set(); Held.Dispose(); AllowExit.Dispose(); _disposed = true;
            }
        }
    }

    private sealed class TeardownOwner
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TeardownOwner> Roots = new();
        private readonly object _sync = new();
        private readonly List<(string Name, Action Release)> _releases = [];
        private readonly List<(string Name, IDisposable Value)> _events = [];
        private sealed class Worker(string name, Task task) { public readonly string Name = name; public readonly Task Task = task; public bool Started; }
        private sealed class OwnedThread(string name, Thread thread) { public readonly string Name = name; public readonly Thread Thread = thread; public bool Attempted; }
        private readonly List<Worker> _tasks = [];
        private readonly List<OwnedThread> _threads = [];
        private readonly List<Exception> _errors = [];
        private readonly List<object> _keep = [];
        private readonly List<string> _journal = [];
        private readonly ITestOutputHelper _output;
        private long _seq;
        private int _calls, _recordLost, _incomplete;
        private bool _closed, _finished;
        private readonly Guid _id = Guid.NewGuid();
        public Fixture? Fixture { get; private set; }
        public SqlTrace? Trace { get; private set; }
        public DeleteContention? Listener { get; private set; }
        public Probe? Probe { get; }
        public bool WorkersDrained { get; private set; }
        public bool FinalCallsReturned { get; private set; }
        public bool EventsDisposed { get; private set; }
        public bool RootsReleased { get; private set; }
        public bool FixtureDisposed { get; private set; }
        public bool IsIncomplete => Volatile.Read(ref _incomplete) != 0;
        public bool IsRooted => Roots.ContainsKey(_id);
        public bool AllStartedTasksTerminal { get { lock (_sync) return _tasks.All(x => !x.Started || x.Task.IsCompleted); } }
        public bool AllStartedThreadsJoined { get; private set; }
        public int ActiveCalls => Volatile.Read(ref _calls);
        public IReadOnlyList<Exception> Errors { get { lock (_sync) return _errors.ToArray(); } }
        public IReadOnlyList<string> Journal { get { lock (_sync) return _journal.ToArray(); } }
        public TeardownOwner(ITestOutputHelper output, Probe? probe = null)
        {
            _output = output; Probe = probe;
            if (!Roots.TryAdd(_id, this)) throw new InvalidOperationException("Owner identity collision.");
            if (probe is not null) Event("probe", probe); // Controller releases; owner disposes only after actual workers return.
            Note("owner.rooted");
        }
        public void Note(string text)
        {
            try { lock (_sync) _journal.Add($"{++_seq:D5} tick={Stopwatch.GetTimestamp()} {text}"); }
            catch { Interlocked.Exchange(ref _recordLost, 1); }
        }
        public void Capture(string stage, Exception error)
        {
            // Called from native boundaries as well: recording must not throw a second exception.
            try { lock (_sync) { _errors.Add(new InvalidOperationException(stage, error)); _journal.Add($"{++_seq:D5} tick={Stopwatch.GetTimestamp()} ERROR {stage}: {error.Message}"); } }
            catch { Interlocked.Exchange(ref _recordLost, 1); }
        }
        public void Incomplete(string detail)
        {
            Interlocked.Exchange(ref _incomplete, 1);
            try { Capture("INCOMPLETE", new InvalidOperationException("INCOMPLETE: " + detail)); }
            catch { Interlocked.Exchange(ref _recordLost, 1); }
        }
        public void Try(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { Capture(name, ex); }
            catch { Incomplete("non-CLS failure at " + name); }
        }
        public T Keep<T>(T value) where T : class { lock (_sync) _keep.Add(value); return value; }
        public T Event<T>(string name, T value) where T : IDisposable { lock (_sync) _events.Add((name, value)); return value; }
        public void ReleaseWith(string name, Action action) { lock (_sync) _releases.Add((name, action)); }
        public Fixture CreateFixture(bool existing)
        {
            Fixture = new Fixture(this); // Resource shell belongs to the owner before initialization can fail.
            using var call = Enter("fixture.initialize");
            Fixture.Initialize(_output, existing);
            return Fixture;
        }
        public SqlTrace InstallTrace(Action<string> profile, Action<Exception>? failed = null)
        {
            Trace = new SqlTrace(this, profile, failed);
            Trace.Register();
            return Trace;
        }
        public DeleteContention Observe(BufferedContention? buffer)
        {
            Listener = new DeleteContention(this, buffer);
            Listener.Initialize();
            return Listener;
        }
        public NativeCall Enter(string name) => new(this, name);
        public sealed class NativeCall : IDisposable
        {
            public static readonly AsyncLocal<NativeCall?> Current = new();
            private readonly NativeCall? _previous;
            private readonly string _name;
            public TeardownOwner Owner { get; }
            private int _returned;
            public bool Active => Volatile.Read(ref _returned) == 0;
            public NativeCall(TeardownOwner owner, string name)
            {
                Owner = owner; _name = name; _previous = Current.Value;
                Interlocked.Increment(ref owner._calls); Current.Value = this;
                owner.Note("call.enter " + name);
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _returned, 1) != 0) return;
                Current.Value = _previous;
                Interlocked.Decrement(ref Owner._calls);
                Owner.Note("call.return " + _name);
            }
        }
        public Task<T> StartAsync<T>(string name, Func<Task<T>> body)
        {
            lock (_sync)
            {
                if (_closed) throw new InvalidOperationException("Owner start admission closed.");
                var launcher = new Task<Task<T>>(async () =>
                {
                    using var call = Enter(name);
                    try { return await body(); }
                    finally { Try(name + ".probe", () => Probe?.BeforeWorkerExit()); }
                }, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                var original = launcher.Unwrap();
                var outer = new Worker(name + ".launcher", launcher);
                var chain = new Worker(name + ".unwrapped", original);
                _tasks.Add(outer); _tasks.Add(chain);
                Note("task.register " + name + " launcher+Unwrap");
                try { launcher.Start(TaskScheduler.Default); outer.Started = chain.Started = true; Note("task.start " + name); }
                catch { outer.Started = chain.Started = launcher.Status != TaskStatus.Created; throw; }
                return original;
            }
        }
        public Task Start(string name, Action body)
        {
            lock (_sync)
            {
                if (_closed) throw new InvalidOperationException("Owner start admission closed.");
                var task = new Task(() => { using var call = Enter(name); body(); }, CancellationToken.None, TaskCreationOptions.LongRunning);
                var worker = new Worker(name, task); _tasks.Add(worker); Note("task.register " + name);
                try { task.Start(TaskScheduler.Default); worker.Started = true; Note("task.start " + name); }
                catch { worker.Started = task.Status != TaskStatus.Created; throw; }
                return task;
            }
        }
        public void StartThread(string name, Thread thread)
        {
            lock (_sync)
            {
                if (_closed) throw new InvalidOperationException("Owner start admission closed.");
                var worker = new OwnedThread(name, thread); _threads.Add(worker); Note("thread.register " + name);
                worker.Attempted = true;
                thread.Start();
                Note("thread.start " + name);
            }
        }
        public async Task FinishAsync(Action? finalAssertions)
        {
            try { await FinishCoreAsync(finalAssertions); }
            catch (Exception ex)
            {
                Capture("unexpected teardown failure", ex);
                Incomplete("teardown did not complete; all roots and remaining resources retained");
                Roots.TryAdd(_id, this);
                foreach (var item in _releases) Try("emergency release " + item.Name, item.Release);
            }
            catch
            {
                Incomplete("non-CLS teardown failure; all roots and remaining resources retained");
                Roots.TryAdd(_id, this);
                foreach (var item in _releases) Try("emergency release " + item.Name, item.Release);
            }
        }
        private async Task FinishCoreAsync(Action? finalAssertions)
        {
            if (_finished) { Capture("finish", new InvalidOperationException("Owner finish called twice.")); return; }
            _finished = true;
            lock (_sync) _closed = true;
            Note("admission.closed");
            foreach (var item in _releases) Try("release " + item.Name, () => { item.Release(); Note("release " + item.Name); });
            Note("drain.started"); Probe?.Draining.TrySetResult(this);
            foreach (var item in _tasks)
            {
                if (!item.Started) { Note("task.never-started " + item.Name); continue; }
                try { await item.Task.WaitAsync(Budget); }
                catch (Exception ex)
                {
                    Capture("worker wait " + item.Name, ex); // A timeout racing terminal state remains a failure.
                    if (!item.Task.IsCompleted) Incomplete("original task still running: " + item.Name);
                }
                if (item.Task.IsCompleted)
                {
                    if (item.Task.Exception is { } all) foreach (var error in all.Flatten().InnerExceptions) Capture("worker terminal " + item.Name, error);
                    Note("task.terminal " + item.Name + " " + item.Task.Status);
                }
            }
            bool joined = true;
            foreach (var item in _threads)
            {
                if (!item.Attempted || (item.Thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0) { Note("thread.never-started " + item.Name); continue; }
                try
                {
                    if (!item.Thread.Join(Budget)) { joined = false; Incomplete("thread Join did not return terminal: " + item.Name); }
                    else Note("thread.joined " + item.Name);
                }
                catch (Exception ex) { joined = false; Capture("Join " + item.Name, ex); Incomplete("Join unproved: " + item.Name); }
            }
            AllStartedThreadsJoined = joined;
            WorkersDrained = AllStartedTasksTerminal && joined;
            if (WorkersDrained)
            {
                if (finalAssertions is not null)
                {
                    try { using var call = Enter("final.assertions"); finalAssertions(); }
                    catch (Exception ex) { Capture("final assertions", ex); }
                }
                FinalCallsReturned = ActiveCalls == 0;
                Note("final.calls.returned=" + FinalCallsReturned);
            }
            else Note("final.assertions.notexecuted: worker return unproved");
            // Cancel only future registration even on failure; never detach a saved db pointer.
            bool nativeSafe = false, listenerSafe = Listener is null;
            if (Trace is not null) Try("native shutdown", () => nativeSafe = Trace.Stop(WorkersDrained && FinalCallsReturned));
            else nativeSafe = WorkersDrained && FinalCallsReturned;
            if (Listener is not null) Try("listener stop/drain", () => listenerSafe = Listener.StopAndDrain());
            if (!nativeSafe || !listenerSafe || !WorkersDrained || !FinalCallsReturned)
                Incomplete($"retained owner={_id}; workers={WorkersDrained} calls={ActiveCalls} native={nativeSafe} listener={listenerSafe}");
            if (Volatile.Read(ref _recordLost) != 0) Incomplete("an error/journal record could not be stored");
            if (nativeSafe && listenerSafe && WorkersDrained && FinalCallsReturned && !IsIncomplete)
            {
                bool eventsSafe = true;
                foreach (var item in _events)
                {
                    try { item.Value.Dispose(); Note("event.disposed " + item.Name); }
                    catch (Exception ex) { eventsSafe = false; Capture("event dispose " + item.Name, ex); }
                }
                EventsDisposed = eventsSafe;
                if (eventsSafe)
                {
                    if (Roots.TryRemove(_id, out _)) { RootsReleased = true; Note("owner.root.released"); }
                    if (Fixture is not null)
                    {
                        FixtureDisposed = await Fixture.DisposeOwnedAsync();
                        if (!FixtureDisposed)
                        {
                            Roots.TryAdd(_id, this); // Keep the failed fixture reachable; never retry unsafe stages implicitly.
                            Incomplete("fixture cleanup failed; remaining fixture retained");
                        }
                    }
                }
                else Incomplete("event disposal failed; retaining owner and fixture");
            }
            if (Volatile.Read(ref _recordLost) != 0) Incomplete("an error/journal record could not be stored");
            foreach (var line in Journal) Try("output", () => _output.WriteLine("T02 " + line));
            if (Listener is not null) foreach (var line in Listener.Diagnostics) Try("contention diagnostics", () => _output.WriteLine(line));
        }
        public void ThrowIfFailed()
        {
            if (Volatile.Read(ref _recordLost) != 0) Incomplete("error recording was incomplete");
            var errors = Errors;
            if (errors.Count != 0) throw new AggregateException(IsIncomplete ? "INCOMPLETE: owner retained or return proof failed." : "T02 body/worker/callback/cleanup failures.", errors);
            if (IsIncomplete) throw new InvalidOperationException("INCOMPLETE: lifecycle return proof or failure recording unavailable.");
        }
    }

    private sealed class Pause
    {
        private readonly object? exactGate, decoy;
        private readonly ManualResetEventSlim _reached, _release, _releaseDecoy;
        public Pause(TeardownOwner owner, object? exactGate = null, object? decoy = null)
        {
            this.exactGate = exactGate; this.decoy = decoy;
            _reached = owner.Event("pause.reached", new ManualResetEventSlim());
            _release = owner.Event("pause.release", new ManualResetEventSlim());
            _releaseDecoy = owner.Event("pause.decoy", new ManualResetEventSlim());
            owner.Keep(this);
            owner.ReleaseWith("pause.main", _release.Set);
            owner.ReleaseWith("pause.decoy", _releaseDecoy.Set);
            owner.ReleaseWith("pause.inactive", () => Volatile.Write(ref _active, 0));
        }
        private static long _nextWindow;
        private int _once;
        private int _active;
        private long _started;
        private Exception? _error;
        public bool HeldRead { get; private set; }
        public bool HeldExactGate { get; private set; }
        public uint OwnerThreadId { get; private set; }
        public long Window { get; private set; }
        public bool Active => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _error) is null && Remaining > TimeSpan.Zero;
        public TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0, (Budget - Stopwatch.GetElapsedTime(_started)).Ticks));
        public void Stop()
        {
            if (Interlocked.Exchange(ref _once, 1) != 0) return;
            var gate = (ReaderWriterLockSlim)typeof(MediaRegistryGate).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            HeldRead = gate.IsReadLockHeld;
            HeldExactGate = exactGate is not null && Monitor.IsEntered(exactGate);
            OwnerThreadId = GetCurrentThreadId();
            _started = Stopwatch.GetTimestamp();
            Window = Interlocked.Increment(ref _nextWindow);
            Volatile.Write(ref _active, 1);
            try
            {
                if (exactGate is not null && (!HeldExactGate || !HeldRead))
                    throw new InvalidOperationException("INCOMPLETE: SQL callback does not own exact E and R.");
                if (decoy is not null) Monitor.Enter(decoy);
                try
                {
                    _reached.Set();
                    if (decoy is not null && !_releaseDecoy.Wait(Remaining)) throw new TimeoutException("Decoy barrier was not released.");
                }
                finally { if (decoy is not null) Monitor.Exit(decoy); }
                if (!_release.Wait(Remaining)) throw new TimeoutException("SQL/header barrier was not released.");
            }
            catch (Exception ex) { Invalidate(ex); throw; }
            finally { Volatile.Write(ref _active, 0); }
        }
        public void WaitUntilReached()
        {
            Assert.True(_reached.Wait(Budget), "The real operation did not reach the selected barrier.");
            AssertActive();
        }
        public void AssertActive()
        {
            if (!Active) throw new InvalidOperationException("INCOMPLETE: SQL/header pause is no longer a valid observation window.", _error);
        }
        public void Invalidate(Exception error)
        {
            Interlocked.CompareExchange(ref _error, error, null);
            Volatile.Write(ref _active, 0);
            _reached.Set();
            Release();
        }
        public void ReleaseDecoy() => _releaseDecoy.Set();
        public void Release() { Volatile.Write(ref _active, 0); _releaseDecoy.Set(); _release.Set(); }
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }

    // The calibration contender touches only the reflected Gate after its native-tid handshake.
    private sealed class GateCalibration
    {
        private readonly TeardownOwner _owner;
        private readonly object exactGate;
        private readonly ManualResetEventSlim _ready;
        private readonly AutoResetEvent _go;
        public GateCalibration(TeardownOwner owner, object gate)
        {
            _owner = owner; exactGate = gate;
            _ready = owner.Event("calibration.ready", new ManualResetEventSlim());
            _go = owner.Event("calibration.go", new AutoResetEvent(false));
            owner.Keep(this); owner.ReleaseWith("calibration.go", () => _go.Set());
        }
        private Thread? _thread;
        private uint _tid;
        private Exception? _error;
        private Guid _activity;
        public void Start(DeleteContention listener, Pause pause)
        {
            _thread = new Thread(() =>
            {
                var previousActivity = EventSource.CurrentThreadActivityId;
                try
                {
                    var warm = new object();
                    Monitor.Enter(warm); Monitor.Exit(warm);
                    _tid = GetCurrentThreadId();
                    _ready.Set();
                    _go.WaitOne();
                    EventSource.SetCurrentThreadActivityId(_activity);
                    Monitor.Enter(exactGate);
                    Monitor.Exit(exactGate);
                }
                catch (Exception ex) { _error = ex; _owner.Capture("calibration worker", ex); _owner.Try("calibration ready", _ready.Set); }
                finally { _owner.Try("calibration activity restore", () => EventSource.SetCurrentThreadActivityId(previousActivity)); }
            }) { Name = "T01 Gate calibration" };
            _owner.StartThread("calibration", _thread);
            Assert.True(_ready.Wait(pause.Remaining), "INCOMPLETE: calibration tid handshake was not received.");
            if (_error is not null) throw new InvalidOperationException("INCOMPLETE: calibration worker failed.", _error);
            if (_owner.Probe?.PartialCalibration == true) throw new InjectedFault("partial-calibration");
            _activity = listener.ArmCalibration(_tid, pause);
            _go.Set();
            listener.WaitUntilCalibrated(pause);
        }
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }

    // Delay a real runtime event across rearming; never synthesize or replace its payload.
    private sealed class BufferedContention
    {
        private readonly ManualResetEventSlim _release;
        private int _once;
        public ManualResetEventSlim Captured { get; }
        public BufferedContention(TeardownOwner owner)
        {
            _release = owner.Event("buffer.release", new ManualResetEventSlim());
            Captured = owner.Event("buffer.captured", new ManualResetEventSlim());
            owner.Keep(this); owner.ReleaseWith("buffer", Release);
        }
        public void Hold(Pause pause)
        {
            if (Interlocked.Exchange(ref _once, 1) != 0) return;
            Captured.Set();
            if (!_release.Wait(pause.Remaining)) throw new InvalidOperationException("Buffered contention exceeded the SQL window.");
        }
        public void Release() => _release.Set();
    }

    // A native tid alone can identify a decoy contention; only a same-window calibrated LockID proves E.
    private sealed class DeleteContention : EventListener
    {
        private sealed record Arm(uint ThreadId, long Generation, long Window, Guid Activity, Pause Pause);
        private sealed record Identity(IntPtr LockId, ushort ClrInstance, ulong Owner);
        private readonly object _sync = new();
        private ManualResetEventSlim _calibrated = null!;
        private readonly TeardownOwner _owner;
        private readonly BufferedContention? buffered;
        private const long Closed = long.MinValue;
        private long _admission;
        private int _enabled;
        private bool _subscriptionStopped;
        private readonly HashSet<EventSource> _sources = [];
        public DeleteContention(TeardownOwner owner, BufferedContention? buffer)
        {
            _owner = owner; buffered = buffer;
        }
        public void Initialize()
        {
            // The owner has assigned Listener before any event allocation or subscription can fail.
            _calibrated = _owner.Event("listener.calibrated", new ManualResetEventSlim());
            Reached = _owner.Event("listener.reached", new ManualResetEventSlim());
            Processed = _owner.Event("listener.processed", new ManualResetEventSlim());
            StaleRejected = _owner.Event("listener.stale", new ManualResetEventSlim());
            Failed = _owner.Event("listener.failed", new ManualResetEventSlim());
            Volatile.Write(ref _enabled, 1);
            foreach (var source in EventSource.GetSources()) OnEventSourceCreated(source);
        }
        private bool Admit()
        {
            while (true)
            {
                var current = Volatile.Read(ref _admission);
                if (current < 0) return false;
                if (Interlocked.CompareExchange(ref _admission, current + 1, current) == current) return true;
            }
        }
        private void Leave() => Interlocked.Decrement(ref _admission);
        private void Fail(Exception ex)
        {
            _owner.Capture("listener callback", ex);
            Interlocked.CompareExchange(ref _error, ex, null);
            _owner.Try("listener failure signal", Failed.Set);
        }
        private Arm? _calibration;
        private Arm? _deletion;
        private Identity? _identity;
        private Exception? _error;
        private long _generation;
        private long _arrivalOrder;
        public ManualResetEventSlim Reached { get; private set; } = null!;
        public ManualResetEventSlim Processed { get; private set; } = null!;
        public ManualResetEventSlim StaleRejected { get; private set; } = null!;
        public ManualResetEventSlim Failed { get; private set; } = null!;
        public System.Collections.Concurrent.ConcurrentQueue<string> Diagnostics { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        {
            // EventListener's base constructor can call this before any derived fields exist.
            if (Volatile.Read(ref _enabled) == 0 || !Admit()) return;
            try
            {
                lock (_sync)
                    if (Volatile.Read(ref _admission) >= 0 && source.Name == "Microsoft-Windows-DotNETRuntime" && _sources.Add(source))
                        EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x4000);
            }
            catch (Exception ex) { Fail(ex); }
            finally { Leave(); }
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (Volatile.Read(ref _enabled) == 0 || !Admit()) return;
            try { ProcessEvent(data); }
            catch (Exception ex) { Fail(ex); }
            finally { Leave(); }
        }
        private void ProcessEvent(EventWrittenEventArgs data)
        {
            if (data.EventName?.StartsWith("ContentionStart", StringComparison.Ordinal) != true) return;
            var calibration = Volatile.Read(ref _calibration);
            var deletion = Volatile.Read(ref _deletion);
            var arm = data.OSThreadId == calibration?.ThreadId ? calibration : data.OSThreadId == deletion?.ThreadId ? deletion : null;
            if (arm is null) return;
            var arrival = Interlocked.Increment(ref _arrivalOrder);
            try { if (ReferenceEquals(arm, deletion)) buffered?.Hold(arm.Pause); }
            catch (Exception ex) { Fail(ex); return; }
            lock (_sync)
            {
                try
                {
                    // Reload after any buffered delivery: the same native tid may already have a new arm.
                    arm = data.OSThreadId == _calibration?.ThreadId ? _calibration : data.OSThreadId == _deletion?.ThreadId ? _deletion : null;
                    if (arm is null) return;
                    var isCalibration = ReferenceEquals(arm, _calibration);
                    Diagnostics.Enqueue($"arrival={arrival} event={data.EventName} id={data.EventId} version={data.Version} tid={data.OSThreadId} generation={arm.Generation} window={arm.Window} calibration={isCalibration} eventActivity={data.ActivityId} armActivity={arm.Activity} timestamp={data.TimeStamp:O} kind={data.TimeStamp.Kind}");
                    // EventPipe timestamps use a session QPC anchor, not the arm's current UTC clock.
                    // An event carries the nonce installed on its emitter after this arm's handshake.
                    if (data.ActivityId != arm.Activity)
                    {
                        if (data.ActivityId == Guid.Empty) throw new InvalidOperationException("Missing runtime activity generation.");
                        Diagnostics.Enqueue("Ignored stale event from a different arm generation.");
                        StaleRejected.Set();
                        return;
                    }
                    if (data.EventName != "ContentionStart" || data.EventId != 81 || data.Version != 2)
                        throw new InvalidOperationException("Expected the measured net9.0 ContentionStart V2 schema (event 81).");
                    var flags = Field<byte>(data, "ContentionFlags");
                    var clr = Field<ushort>(data, "ClrInstanceID");
                    var lockId = Field<IntPtr>(data, "LockID");
                    var objectId = Field<IntPtr>(data, "AssociatedObjectID"); // Diagnostic only: an object address is not LockID.
                    var owner = Field<ulong>(data, "LockOwnerThreadID");
                    Diagnostics.Enqueue($"LockID={lockId} AssociatedObjectID={objectId} ClrInstanceID={clr} owner={owner} flags={flags}");
                    if (flags != 0 || lockId == IntPtr.Zero || clr == 0)
                        throw new InvalidOperationException("Expected managed contention with nonzero LockID and ClrInstanceID.");
                    if (!arm.Pause.Active || arm.Pause.Window != arm.Window)
                        throw new InvalidOperationException("SQL pause window expired or failed before contention was observed.");
                    if (owner != arm.Pause.OwnerThreadId)
                    {
                        Diagnostics.Enqueue("Ignored unrelated lock owner.");
                        return;
                    }
                    var identity = new Identity(lockId, clr, owner);
                    if (isCalibration)
                    {
                        if (_identity is not null) throw new InvalidOperationException("Ambiguous calibration: more than one candidate event.");
                        _identity = identity;
                        Diagnostics.Enqueue($"CALIBRATED LockID={lockId} ClrInstanceID={clr} owner={owner} window={arm.Window}");
                        _calibrated.Set();
                    }
                    else if (_identity == identity)
                    {
                        arm.Pause.AssertActive();
                        Diagnostics.Enqueue($"REACHED exact E LockID={lockId} generation={arm.Generation} window={arm.Window}");
                        Reached.Set();
                    }
                    else
                    {
                        Diagnostics.Enqueue($"Ignored non-E LockID={lockId}; calibrated={_identity?.LockId}.");
                        if (_identity?.ClrInstance == clr) Processed.Set();
                    }
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
            }
        }
        private static T Field<T>(EventWrittenEventArgs data, string name)
        {
            if (data.PayloadNames is null || data.Payload is null || data.PayloadNames.Count != data.Payload.Count)
                throw new InvalidOperationException("Missing or inconsistent contention payload.");
            var matches = data.PayloadNames.Select((value, index) => (value, index)).Where(p => p.value == name).ToArray();
            if (matches.Length != 1 || data.Payload[matches[0].index] is not T value)
                throw new InvalidOperationException($"Missing, ambiguous or wrongly typed {name}; expected {typeof(T).Name}.");
            return value;
        }
        public Guid ArmCalibration(uint tid, Pause pause)
        {
            pause.AssertActive();
            lock (_sync)
            {
                if (Volatile.Read(ref _admission) < 0) throw new InvalidOperationException("Listener arm admission closed.");
                if (_calibration is not null) throw new InvalidOperationException("INCOMPLETE: calibration was already armed.");
                _calibration = new(tid, ++_generation, pause.Window, Guid.NewGuid(), pause);
                Diagnostics.Enqueue($"ARM calibration tid={tid} generation={_calibration.Generation} activity={_calibration.Activity} window={pause.Window}");
                return _calibration.Activity;
            }
        }
        public void WaitUntilCalibrated(Pause pause)
        {
            var observed = WaitHandle.WaitAny([_calibrated.WaitHandle, Failed.WaitHandle], pause.Remaining);
            RequireSignal(observed);
            pause.AssertActive();
        }
        public void WatchCurrentThread(Pause pause)
        {
            var warm = new object();
            Monitor.Enter(warm); Monitor.Exit(warm);
            var tid = GetCurrentThreadId();
            pause.AssertActive();
            lock (_sync)
            {
                if (Volatile.Read(ref _admission) < 0) throw new InvalidOperationException("Listener arm admission closed.");
                if (_identity is null) throw new InvalidOperationException("INCOMPLETE: deletion cannot arm without Gate calibration.");
                Reached.Reset(); Processed.Reset(); StaleRejected.Reset();
                _deletion = new(tid, ++_generation, pause.Window, Guid.NewGuid(), pause);
                EventSource.SetCurrentThreadActivityId(_deletion.Activity);
                Diagnostics.Enqueue($"ARM deletion tid={tid} generation={_deletion.Generation} activity={_deletion.Activity} window={pause.Window}");
            }
        }
        public void StopWatching() { lock (_sync) _deletion = null; }
        public void RequireSignal(int observed)
        {
            ThrowIfIncomplete();
            if (observed == WaitHandle.WaitTimeout) throw new InvalidOperationException("INCOMPLETE: contention observation exceeded the shared 20s SQL window.");
        }
        public void ThrowIfIncomplete()
        {
            if (_error is not null) throw new InvalidOperationException("INCOMPLETE: Gate contention evidence is invalid.", _error);
        }
        public bool StopAndDrain()
        {
            Interlocked.Or(ref _admission, Closed);
            lock (_sync) { _calibration = null; _deletion = null; }
            try { base.Dispose(); _subscriptionStopped = true; _owner.Note("listener.subscription.stopped"); }
            catch (Exception ex) { _owner.Capture("listener unsubscribe", ex); }
            var start = Stopwatch.GetTimestamp();
            var spin = new SpinWait();
            while (Volatile.Read(ref _admission) != Closed && Stopwatch.GetElapsedTime(start) < Budget) spin.SpinOnce();
            bool drained = _subscriptionStopped && Volatile.Read(ref _admission) == Closed;
            _owner.Note("listener.admitted.callbacks.drained=" + drained);
            return drained;
        }
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }

    // The init trampoline never captures an owner. SQLite may retain a copied init pointer
    // across cancel_auto_extension; its process-lifetime root intentionally is not freed.
    private sealed class SqlTrace
    {
        public static readonly AsyncLocal<string?> Operation = new();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init(IntPtr db, IntPtr error, IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TraceCallback(uint kind, IntPtr context, IntPtr argument, IntPtr detail);
        private static readonly Init InitRoot = DispatchInit;
        private static readonly IntPtr InitPointer = Marshal.GetFunctionPointerForDelegate(InitRoot);
        private static readonly object RegistrationGate = new();
        private static SqlTrace? _registeredOwner;
        private static int _globalInitInFlight;
        private readonly TeardownOwner _owner;
        private readonly Action<string> _profile;
        private readonly Action<Exception>? _callbackFailed;
        private readonly TraceCallback _trace;
        private readonly object _sync = new();
        private readonly HashSet<IntPtr> _live = [];
        private int _initInFlight, _traceInFlight;
        private bool _attempted, _registrationReturned, _registered, _cancelReturned, _uncertain;
        private int _registrationResult = -1, _cancelResult = -1;
        private Exception? _error;
        public SqlTrace(TeardownOwner owner, Action<string> profile, Action<Exception>? callbackFailed)
        { _owner = owner; _profile = profile; _callbackFailed = callbackFailed; _trace = OnTrace; }
        public void Register()
        {
            lock (RegistrationGate)
            {
                if (_registeredOwner is not null) throw new InvalidOperationException("INCOMPLETE: another trace registration is retained.");
                // The owner's static root and both delegate fields exist before the first native registration.
                _registeredOwner = this; _attempted = true;
                _owner.Note("native.register.begin root=" + _owner.IsRooted);
                try
                {
                    _registrationResult = sqlite3_auto_extension(InitPointer);
                    _registrationReturned = true; _registered = _registrationResult == 0;
                    _owner.Note("native.register.return rc=" + _registrationResult);
                    Assert.Equal(0, _registrationResult);
                }
                catch { if (!_registrationReturned) _uncertain = true; throw; }
            }
        }
        private static int DispatchInit(IntPtr db, IntPtr error, IntPtr api)
        {
            Interlocked.Increment(ref _globalInitInFlight);
            TeardownOwner? owner = null;
            try
            {
                var call = TeardownOwner.NativeCall.Current.Value;
                if (call is null || !call.Active) return 0;
                owner = call.Owner;
                var trace = owner.Trace;
                return trace is null ? 0 : trace.OnInit(db);
            }
            catch (Exception ex) { owner?.Capture("native init dispatch", ex); return 1; }
            catch { owner?.Incomplete("non-CLS native dispatch failure"); return 1; }
            finally { Interlocked.Decrement(ref _globalInitInFlight); }
        }
        private int OnInit(IntPtr db)
        {
            Interlocked.Increment(ref _initInFlight);
            try
            {
                var call = TeardownOwner.NativeCall.Current.Value;
                if (call is null || !call.Active || !ReferenceEquals(call.Owner, _owner)) return 0;
                if (!_registered) { _uncertain = true; throw new InvalidOperationException("Owned open arrived outside registration lifetime."); }
                _owner.Probe?.InitFault();
                lock (_sync)
                {
                    if (!_live.Add(db)) { _uncertain = true; throw new InvalidOperationException("Duplicate live SQLite connection."); }
                    int rc;
                    try { rc = sqlite3_trace_v2(db, 2 | 8, _trace, IntPtr.Zero); }
                    catch { _uncertain = true; throw; }
                    if (rc != 0) { _uncertain = true; throw new InvalidOperationException("sqlite3_trace_v2 rc=" + rc); }
                    _owner.Note("native.trace.install.return live=" + _live.Count);
                }
                return 0;
            }
            catch (Exception ex) { Record(ex); return 1; }
            catch { _uncertain = true; _owner.Incomplete("Non-CLS native init error."); return 1; }
            finally { Interlocked.Decrement(ref _initInFlight); }
        }
        private int OnTrace(uint kind, IntPtr context, IntPtr argument, IntPtr detail)
        {
            Interlocked.Increment(ref _traceInFlight);
            try
            {
                var call = TeardownOwner.NativeCall.Current.Value;
                if (call is null || !call.Active || !ReferenceEquals(call.Owner, _owner))
                {
                    _uncertain = true;
                    throw new InvalidOperationException("Trace callback lacks an active owned native-return scope.");
                }
                if (kind == 8)
                {
                    // CLOSE argument is a db, not a statement. Never call trace_v2 on this saved pointer.
                    lock (_sync)
                    {
                        if (!_live.Remove(argument)) { _uncertain = true; throw new InvalidOperationException("CLOSE for unknown owned db."); }
                        _owner.Note("native.connection.close live=" + _live.Count);
                    }
                }
                else if (kind == 2)
                {
                    _owner.Probe?.ProfileFault();
                    _profile(Marshal.PtrToStringUTF8(sqlite3_sql(argument)) ?? "");
                }
                return 0;
            }
            catch (Exception ex) { Record(ex); return 0; }
            catch { _uncertain = true; _owner.Incomplete("Non-CLS native trace error."); return 0; }
            finally { Interlocked.Decrement(ref _traceInFlight); }
        }
        private void Record(Exception error)
        {
            try
            {
                Interlocked.CompareExchange(ref _error, error, null);
                _owner.Capture("native callback", error);
                try
                {
                    _callbackFailed?.Invoke(error);
                }
                catch (Exception notify) { _owner.Capture("native failure notifier", notify); }
                catch { _uncertain = true; }
            }
            catch { _uncertain = true; }
        }
        public void ThrowIfCallbackFailed() { if (_error is not null) throw new InvalidOperationException("SQLite observation callback failed.", _error); }
        public bool Stop(bool nativeCallsReturned)
        {
            lock (RegistrationGate)
            {
                if (_attempted)
                {
                    try
                    {
                        _cancelResult = sqlite3_cancel_auto_extension(InitPointer); _cancelReturned = true;
                        _owner.Note($"native.cancel.return registered={_registered} result={_cancelResult} globalInitDiagnostic={Volatile.Read(ref _globalInitInFlight)}");
                        if (_registered && _cancelResult != 1) _uncertain = true;
                        if (!_registered && _registrationReturned && _registrationResult != 0 && _cancelResult != 0) _uncertain = true;
                        _registered = false;
                    }
                    catch (Exception ex) { _uncertain = true; _owner.Capture("native cancel", ex); }
                }
                lock (_sync)
                {
                    // Zero counts alone are NOT a return proof. The scopes/terminal tasks/Join are separate conjuncts.
                    bool safe = nativeCallsReturned && _owner.ActiveCalls == 0 && !_uncertain && !_registered &&
                        (!_attempted || (_registrationReturned && _cancelReturned)) && _live.Count == 0 &&
                        Volatile.Read(ref _initInFlight) == 0 && Volatile.Read(ref _traceInFlight) == 0;
                    _owner.Note($"native.return.proof={safe} callsReturned={nativeCallsReturned} live={_live.Count} init={_initInFlight} trace={_traceInFlight}");
                    if (safe && ReferenceEquals(_registeredOwner, this)) _registeredOwner = null;
                    return safe;
                }
            }
        }
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_auto_extension(IntPtr init);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_cancel_auto_extension(IntPtr init);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_trace_v2(IntPtr db, uint mask, TraceCallback callback, IntPtr context);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_sql(IntPtr statement);
    }

    private sealed record Reply(int Status, JsonElement Body);
    private sealed class Fixture
    {
        private WebApplication _app = null!;
        private readonly TeardownOwner _owner;
        public string Root { get; private set; } = null!;
        public Store Store { get; private set; } = null!;
        public StudioStore Studio { get; private set; } = null!;
        public Clip Clip { get; private set; } = null!;
        public RecordingSession Session { get; private set; } = null!;
        public Fixture(TeardownOwner owner) { _owner = owner; }
        public void Initialize(ITestOutputHelper output, bool existing)
        {
            Root = RunRoot();
            Directory.CreateDirectory(Root);
            output.WriteLine("Isolated retained fixture: " + Root);
            Store = new Store(Path.Combine(Root, "test.db")); Store.Migrate();
            Studio = new StudioStore(Store.DatabasePath); Studio.Migrate();
            var id = Guid.NewGuid().ToString("N");
            Session = new RecordingSession("session-" + id, 1, DateTimeOffset.UtcNow, null, RecordingState.Ended);
            Store.SaveSession(Session);
            var highlight = new HighlightEvent("event-" + id, Session.Id, "manual", 0, 0, 4, HighlightState.Ready, 1);
            Store.SaveHighlight(highlight);
            Clip = new Clip(id, highlight.Id, Path.Combine(Root, "offline.mp4"), null, DateTimeOffset.UtcNow);
            Store.SaveClip(Clip);
            Studio.SaveLocalVideo(new LocalVideo(Clip.Id, Clip.Path, "race", Root, DateTimeOffset.UtcNow));
            if (existing) Studio.SaveProject(Clip.Id, JsonSerializer.Serialize(new EditorProject { End = 3 }));
            EditorRevisionStore.Current(Studio, Clip.Id);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            _app = builder.Build();
            StudioEndpoints.Map(_app, Store, Studio, null!, null!, null!, null!, null!, new MediaTools(), null!, new LiveSettings { StorageRoot = Root });
        }
        public async Task<Reply> RequestAsync(string route, Pause? pause = null)
        {
            var method = route == "editor" ? "PUT" : "POST";
            var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>(),
                e => e.RoutePattern.RawText == "/api/studio/clips/{id}/" + route && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            if (pause is not null) context.Features.Set<IHttpResponseFeature>(new HttpResponseFeature { Headers = new PauseHeaders(pause) });
            context.Request.Method = method;
            context.Request.Path = "/api/studio/clips/" + Clip.Id + "/" + route;
            context.Request.RouteValues["id"] = Clip.Id;
            context.Request.Headers["X-Base-Revision"] = Studio.GetProjectRevision(Clip.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);
            // No explicit references: implicit primary is the blocker, not the invalid Timeline=[] shape.
            var body = route == "editor" ? "{\"start\":0,\"end\":1,\"timeline\":null}" : "{\"commands\":[{\"op\":\"setRange\",\"args\":{\"start\":0,\"end\":1}}]}";
            var bytes = Encoding.UTF8.GetBytes(body);
            await using var input = new MemoryStream(bytes);
            await using var response = new MemoryStream();
            context.Request.Body = input;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
            context.Response.Body = response;
            await endpoint.RequestDelegate!(context);
            using var json = JsonDocument.Parse(response.ToArray());
            return new(context.Response.StatusCode, json.RootElement.Clone());
        }
        private static string RunRoot()
        {
            var configured = Environment.GetEnvironmentVariable("LIVECLIPPER_T02_RUN_ROOT");
            var parent = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Path.GetTempPath(), "LiveClipper-T02", "run-" + Guid.NewGuid().ToString("N"))
                : configured;
            if (!Path.IsPathFullyQualified(parent) || parent.IndexOfAny(['[', ']']) >= 0)
                throw new InvalidOperationException("LIVECLIPPER_T02_RUN_ROOT must be a fully qualified path without placeholders.");
            parent = Path.GetFullPath(parent);
            var parts = parent.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(x => x.StartsWith("CARD03-T01", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("T01-REVIEW-T02-PREFLIGHT-1915", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("T02-PATCH-2150", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("src", StringComparison.OrdinalIgnoreCase) || x.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("bin", StringComparison.OrdinalIgnoreCase) || x.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Run root must not target source, compiled artifacts or retained evidence.");
            for (var directory = new DirectoryInfo(parent); directory is not null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Run root must not traverse a reparse point.");
            return Path.Combine(parent, "fixtures", Guid.NewGuid().ToString("N"));
        }
        public async Task<bool> DisposeOwnedAsync()
        {
            bool safe = true;
            if (_app is not null)
            {
                try { await _app.DisposeAsync(); _owner.Note("fixture.app.disposed"); }
                catch (Exception ex) { safe = false; _owner.Capture("fixture app dispose", ex); }
            }
            // A failed app disposal does not prove that app-owned work has stopped.
            if (!safe) { _owner.Incomplete("app disposal failed; Forget and pools retained"); return false; }
            if (Clip is not null)
            {
                try { EditorRevisionStore.Forget(Clip.Id); _owner.Note("fixture.forget.returned"); }
                catch (Exception ex) { safe = false; _owner.Capture("fixture Forget", ex); }
            }
            if (safe)
            {
                try { SqliteConnection.ClearAllPools(); _owner.Note("fixture.pools.cleared"); }
                catch (Exception ex) { safe = false; _owner.Capture("fixture pools", ex); }
            }
            return safe;
        }
    }
    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    private sealed class PauseHeaders(Pause pause) : IHeaderDictionary
    {
        private readonly HeaderDictionary _headers = new();
        public StringValues this[string key]
        {
            get => _headers[key];
            set { if (key == "X-Editor-Epoch") pause.Stop(); _headers[key] = value; }
        }
        public long? ContentLength { get => _headers.ContentLength; set => _headers.ContentLength = value; }
        public ICollection<string> Keys => _headers.Keys;
        public ICollection<StringValues> Values => _headers.Values;
        public int Count => _headers.Count;
        public bool IsReadOnly => false;
        public void Add(string key, StringValues value) => _headers.Add(key, value);
        public void Add(KeyValuePair<string, StringValues> item) => ((ICollection<KeyValuePair<string, StringValues>>)_headers).Add(item);
        public void Clear() => _headers.Clear();
        public bool Contains(KeyValuePair<string, StringValues> item) => ((ICollection<KeyValuePair<string, StringValues>>)_headers).Contains(item);
        public bool ContainsKey(string key) => _headers.ContainsKey(key);
        public void CopyTo(KeyValuePair<string, StringValues>[] array, int index) => ((ICollection<KeyValuePair<string, StringValues>>)_headers).CopyTo(array, index);
        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() => _headers.GetEnumerator();
        public bool Remove(string key) => _headers.Remove(key);
        public bool Remove(KeyValuePair<string, StringValues> item) => ((ICollection<KeyValuePair<string, StringValues>>)_headers).Remove(item);
        public bool TryGetValue(string key, out StringValues value) => _headers.TryGetValue(key, out value);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
