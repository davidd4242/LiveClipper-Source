namespace LiveClipper.Core;

public sealed record OllamaInstallationState(
    string State,
    string Stage,
    string Version,
    string? TaskId = null,
    string? PrivateRoot = null,
    long? BytesReceived = null,
    long? TotalBytes = null,
    string? PackagePath = null,
    string? StagingDirectory = null,
    string? Reason = null,
    bool CancellationRequested = false,
    string? ManagedStatus = null);

public sealed record OllamaInstallationRequestResult(int StatusCode, string Outcome, OllamaInstallationState Installation);

// StorageRoot is a restart-required setting, like the host's database path. Only the
// explicitly confirmed runtime override may switch the managed runtime within a session.
public sealed class OllamaRuntimeLocation(LiveSettings settings)
{
    public string DefaultBaseDirectory {get;}=settings.StorageRoot;
    public string EffectiveBase()=>settings.OllamaRuntimeBaseDirectory??DefaultBaseDirectory;
}

public sealed class OllamaInstallationService
{
    private readonly object gate = new();
    private OllamaInstallationState state = new("idle", "idle", OllamaManagedInstallation.SupportedVersion);
    private CancellationTokenSource? cancellation;
    private Task? running;
    private bool stopping;
    private bool managementBusy;
    public IDisposable? TryAcquireManagement()
    {
        lock (gate)
        {
            if (stopping || managementBusy || running is { IsCompleted: false }) return null;
            managementBusy = true;
            return new ManagementLease(this);
        }
    }
    private sealed class ManagementLease(OllamaInstallationService owner) : IDisposable
    {
        public void Dispose() { lock (owner.gate) owner.managementBusy = false; }
    }

    public OllamaInstallationState Snapshot()
    {
        lock (gate)
            return state.State == "cancelled" && running is { IsCompleted: false }
                ? state with { State = "cancelling" }
                : state;
    }

    public static string ResolvePrivateRoot(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        // Reject before combining: a volume or UNC share root must never host the private root.
        if (!Path.IsPathFullyQualified(storageRoot))
            throw new ArgumentException("StorageRoot must be an absolute path.", nameof(storageRoot));
        var normalized = Path.GetFullPath(storageRoot);
        var pathRoot = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(pathRoot) || string.Equals(
                Path.TrimEndingDirectorySeparator(normalized), Path.TrimEndingDirectorySeparator(pathRoot),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("StorageRoot must not be a volume or share root.", nameof(storageRoot));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(normalized, "runtime", "ollama-managed")));
    }

    internal static bool RootMatches(string actual,string? expected)=>expected is null || string.Equals(actual,expected,RenderStoragePaths.PathComparison);
    public (int Code,string Outcome) ChangeLocation(bool confirmed,string? directory,Func<string> currentBase,Action<string?> persist,string? expectedRoot=null)
    {
        if(!confirmed)return (400,"confirmation-required");
        using var lease=TryAcquireManagement();
        if(lease is null)return (409,"private-runtime-in-use");
        if(expectedRoot is not null && !RootMatches(ResolvePrivateRoot(currentBase()),expectedRoot))return (409,"private-root-changed");
        string? normalized=null;
        if(!string.IsNullOrWhiteSpace(directory))
        {
            if(OperatingSystem.IsWindows() && !System.Text.RegularExpressions.Regex.IsMatch(directory.Trim(),@"^[A-Za-z]:[\\/]"))return (400,"invalid-base-directory");
            try { normalized=RenderStoragePaths.NormalizeDirectory(directory); }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or IOException){return (400,"invalid-base-directory");}
        }
        try { persist(normalized); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return (500,"location-save-failed");}
        // The management lease excludes live installs/daemons. Keep same-root refusals visible.
        lock(gate)
            if(state.PrivateRoot is {} previous && !string.Equals(previous,ResolvePrivateRoot(currentBase()),RenderStoragePaths.PathComparison))
                state=new("idle","idle",OllamaManagedInstallation.SupportedVersion);
        return (200,"location-saved");
    }
    // Resolve the persisted choice only after admission, so an overlapping location update cannot stale it.
    public Task<OllamaInstallationRequestResult> StartAsync(string storageRoot, bool confirmed, string? expectedVersion)=>StartAsync(()=>storageRoot,confirmed,expectedVersion);
    public Task<OllamaInstallationRequestResult> StartAsync(Func<string> storageBase, bool confirmed, string? expectedVersion,string? expectedRoot=null)
    {
        lock (gate)
        {
            if (!confirmed) return Result(400, "confirmation-required");
            if (!string.Equals(expectedVersion, OllamaManagedInstallation.SupportedVersion, StringComparison.Ordinal))
                return Result(400, "version-mismatch");
            if (stopping) return Result(409, "application-stopping");
            if (managementBusy) return Result(409, "private-runtime-in-use");
            if (running is { IsCompleted: false }) return Result(409, "installation-in-progress");

            string root;
            try { root = ResolvePrivateRoot(storageBase()); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or
                UnauthorizedAccessException or System.Security.SecurityException)
            {
                return Result(409, "private-root-invalid");
            }

            if(!RootMatches(root,expectedRoot))return Result(409,"private-root-changed");
            var source = new CancellationTokenSource();
            cancellation = source;
            state = new("verifying", "inspecting-existing", OllamaManagedInstallation.SupportedVersion,
                Guid.NewGuid().ToString("N"), root);
            var admission = new TaskCompletionSource<OllamaInstallationRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            running = Task.Run(() => RunAsync(root, source, admission));
            return admission.Task;
        }
    }

    public OllamaInstallationRequestResult RequestCancellation()
    {
        lock (gate)
        {
            if (running is not { IsCompleted: false } || cancellation is null || IsTerminal(state.State))
                return new(409, "no-active-installation", Snapshot());
            SignalCancellation();
            return new(202, "cancellation-requested", state);
        }
    }

    public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken token = default)
    {
        Task? task;
        lock (gate)
        {
            stopping = true;
            task = running;
            if (task is { IsCompleted: false } && cancellation is not null && !IsTerminal(state.State))
                SignalCancellation();
        }
        if (task is null) return true;
        try
        {
            await task.WaitAsync(timeout, token).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) when (task.IsCanceled) { return true; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
    }

    private Task<OllamaInstallationRequestResult> Result(int statusCode, string outcome) =>
        Task.FromResult(new OllamaInstallationRequestResult(statusCode, outcome, Snapshot()));

    private void SignalCancellation()
    {
        state = state with { State = "cancelling", CancellationRequested = true, Reason = "cancellation-requested" };
        // The same lock protects Cancel and Dispose; no caller retains the CTS.
        cancellation!.Cancel();
    }

    private async Task RunAsync(string root, CancellationTokenSource source,
        TaskCompletionSource<OllamaInstallationRequestResult> admission)
    {
        var token = source.Token;
        var published = false;
        try
        {
            var existing = await OllamaManagedInstallation.InspectAsync(root, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (existing.Status == OllamaManagedInstallation.ManagedValid)
            {
                Update(s => s with { State = "idle", Stage = "already-installed", Reason = "already-installed", ManagedStatus = existing.Status });
                admission.TrySetResult(new(200, "already-installed", Snapshot()));
                return;
            }
            // A missing manifest does not prove that the program path is absent.
            if (existing.Status != OllamaManagedInstallation.NotInstalled || ProgramPathExists(root))
            {
                Update(s => s with { State = "failed", Stage = "inspection-refused", Reason = "existing-program-not-managed-valid", ManagedStatus = existing.Status });
                admission.TrySetResult(new(409, "existing-program-refused", Snapshot()));
                return;
            }
            token.ThrowIfCancellationRequested();
            Update(s => s with { State = "downloading", Stage = "preparing-download", ManagedStatus = existing.Status });
            admission.TrySetResult(new(202, "installation-accepted", Snapshot()));
            var package = await OllamaPackageDownload.DownloadAsync(root, new DownloadProgress(this), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Update(s => s with { State = "installing", Stage = "installing-package", PackagePath = package });
            var installed = await OllamaManagedInstaller.InstallFromPackageAsync(root, package, token).ConfigureAwait(false);
            Update(s => s with { StagingDirectory = installed.StagingDirectory });
            if (installed.Status == OllamaManagedInstaller.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("installer-cancelled-without-request");
            }
            if (installed.Status != OllamaManagedInstaller.Installed)
            {
                Update(s => s with { State = "failed", Stage = "installation-failed", Reason = "installer-failed" });
                return;
            }
            published = true;
            Update(s => s with { State = "verifying", Stage = "inspecting-installed" });
            var identity = await OllamaManagedInstallation.InspectAsync(root, token).ConfigureAwait(false);
            Update(s => s with
            {
                State = identity.Status == OllamaManagedInstallation.ManagedValid ? "completed" : "failed",
                Stage = "installation-inspected",
                ManagedStatus = identity.Status,
                Reason = identity.Status == OllamaManagedInstallation.ManagedValid ? "program-files-installed" : "installed-inspection-failed"
            });
        }
        catch (OllamaPackageDownloadCanceledException error) when (token.IsCancellationRequested)
        {
            Update(s => s with { State = "cancelled", PackagePath = error.RetainedPath ?? s.PackagePath, Reason = "cancelled" });
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Update(s => s with { State = "cancelled", Reason = published ? "cancelled-after-publication-inspection-required" : "cancelled" });
            throw;
        }
        catch (OllamaPackageDownloadException error)
        {
            Update(s => s with { State = "failed", Reason = "package-download-failed", PackagePath = error.RetainedPath ?? s.PackagePath });
        }
        catch (OperationCanceledException)
        {
            Update(s => s with { State = "failed", Reason = "unexpected-cancellation" });
            throw;
        }
        catch (Exception)
        {
            // Exception messages may include redirect URLs, filesystem input, or HTTP details.
            Update(s => s with { State = "failed", Reason = "installation-task-failed" });
        }
        finally
        {
            lock (gate)
            {
                cancellation = null;
                source.Dispose();
                admission.TrySetResult(new(409,
                    state.State == "cancelled" ? "installation-cancelled" : "installation-refused", Snapshot()));
            }
        }
    }

    private static bool ProgramPathExists(string root)
    {
        try
        {
            _ = File.GetAttributes(Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName));
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsTerminal(string value) => value is "idle" or "completed" or "cancelled" or "failed";

    private void Update(Func<OllamaInstallationState, OllamaInstallationState> update)
    {
        lock (gate)
        {
            var next = update(state);
            // Progress never overwrites an acknowledged cancellation request before the task exits.
            state = state.CancellationRequested && !IsTerminal(next.State) ? next with { State = "cancelling" } : next;
        }
    }

    private sealed class DownloadProgress(OllamaInstallationService owner) : IProgress<OllamaPackageDownloadProgress>
    {
        public void Report(OllamaPackageDownloadProgress value) => owner.Update(s => s with
        {
            State = value.Stage == OllamaPackageDownload.Downloading ? "downloading" : "verifying",
            Stage = value.Stage,
            BytesReceived = value.BytesReceived,
            TotalBytes = value.TotalBytes,
            PackagePath = value.FilePath
        });
    }
}
