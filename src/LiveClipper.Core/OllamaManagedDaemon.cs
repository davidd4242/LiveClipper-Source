using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LiveClipper.Core;

public sealed record OllamaDaemonState(string State, bool Ready, bool Owned, bool Selected,
    string? Endpoint, int? ProcessId, string? PrivateRoot, string? RetiredDirectory, string? Reason);
public sealed record OllamaDaemonResult(int StatusCode, string Outcome, OllamaDaemonState Daemon);

// Session-only ownership: never adopt, stop, or replace a discovered external daemon.
public sealed class OllamaManagedDaemon(OllamaInstallationService installation)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
    private Process? process;
    private IDisposable? installationLease;
    private string? endpoint, root, retired, reason;
    private bool ready, selected, alive;
    private int? processId;
    private string? originalUrl, originalExecutable;

    private OllamaDaemonState snapshot = new("stopped", false, false, false, null, null, null, null, null);
    public OllamaDaemonState Snapshot() => Volatile.Read(ref snapshot);
    // All writers hold gate; readers receive one coherent immutable snapshot.
    private void Publish() => Volatile.Write(ref snapshot,
        new(alive ? "running" : "stopped", alive && ready, alive, selected,
            endpoint, alive ? processId : null, root, retired, reason));
    private OllamaDaemonResult Result(int code, string outcome)
    { reason = outcome; Publish(); return new(code, outcome, Snapshot()); }

    public Task<OllamaDaemonResult> StartAsync(string storageRoot, bool confirmed, CancellationToken token)=>StartAsync(()=>storageRoot,confirmed,token);
    public async Task<OllamaDaemonResult> StartAsync(Func<string> storageBase, bool confirmed, CancellationToken token,string? expectedRoot=null)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!confirmed) return Result(400, "confirmation-required");
            if (process is { HasExited: false }) return OllamaInstallationService.RootMatches(root!,expectedRoot)?Result(200,"already-running"):Result(409,"private-root-changed");
            Release();
            installationLease = installation.TryAcquireManagement();
            if (installationLease is null) return Result(409, "installation-or-management-active");
            root = OllamaInstallationService.ResolvePrivateRoot(storageBase());
            if(!OllamaInstallationService.RootMatches(root,expectedRoot)){Release();return Result(409,"private-root-changed");}
            var identity = await OllamaManagedInstallation.InspectAsync(root, token);
            if (identity.Status != OllamaManagedInstallation.ManagedValid)
            { Release(); return Result(409, "managed-program-not-valid"); }
            // Select an unused loopback port; do not bind or probe the external 11434 service.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            endpoint = $"http://127.0.0.1:{port}/";
            var start = new ProcessStartInfo(identity.Executable!)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
              WorkingDirectory = Path.GetDirectoryName(identity.Executable!)! };
            start.ArgumentList.Add("serve");
            start.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
            start.Environment["OLLAMA_MODELS"] = Path.Combine(root, OllamaManagedInstallation.ModelsDirectoryName);
            process = Process.Start(start) ?? throw new IOException("private-process-start-failed");
            alive = true; processId = process.Id; Publish();
            var startedProcess = process;
            process.EnableRaisingEvents = true;
            process.Exited += async (_, _) =>
            {
                await gate.WaitAsync();
                try { if (ReferenceEquals(process, startedProcess)) Release(); }
                finally { gate.Release(); }
            };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                try
                {
                    using var response = await client.GetAsync(endpoint + "api/version", token);
                    if (response.IsSuccessStatusCode)
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                        if (json.RootElement.TryGetProperty("version", out _) && !process.HasExited)
                        { ready = true; return Result(200, "private-runtime-ready"); }
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(200, token);
            }
            await StopOwnedAsync();
            return Result(409, "private-runtime-not-ready");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or JsonException)
        { await StopOwnedAsync(); return Result(409, "private-runtime-start-failed"); }
        catch (OperationCanceledException) { await StopOwnedAsync(); throw; }
        finally { gate.Release(); }
    }

    public async Task<OllamaDaemonResult> SelectAsync(bool confirmed, string target, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!confirmed) return Result(400, "confirmation-required");
            if (target == "original") { RestoreConnection(); return Result(200, "original-connection-restored"); }
            if (target != "private") return Result(400, "unknown-target");
            if (!Snapshot().Ready) return Result(409, "private-runtime-not-ready");
            if (!selected)
            {
                originalUrl = Environment.GetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL");
                originalExecutable = Environment.GetEnvironmentVariable("LIVECLIPPER_OLLAMA_EXE");
                Environment.SetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL", endpoint);
                Environment.SetEnvironmentVariable("LIVECLIPPER_OLLAMA_EXE", process!.StartInfo.FileName);
                selected = true;
            }
            return Result(200, "private-connection-selected-for-session");
        }
        finally { gate.Release(); }
    }

    public async Task<OllamaDaemonResult> StopAsync(bool confirmed, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!confirmed) return Result(400, "confirmation-required");
            await StopOwnedAsync();
            return Result(200, "private-runtime-stopped");
        }
        finally { gate.Release(); }
    }

    public Task<OllamaDaemonResult> UninstallAsync(string storageRoot, bool confirmed, string? version, CancellationToken token)=>UninstallAsync(()=>storageRoot,confirmed,version,token);
    public async Task<OllamaDaemonResult> UninstallAsync(Func<string> storageBase, bool confirmed, string? version, CancellationToken token,string? expectedRoot=null)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!confirmed) return Result(400, "confirmation-required");
            if (version != OllamaManagedInstallation.SupportedVersion) return Result(400, "version-mismatch");
            if (process is { HasExited: false }) return Result(409, "stop-private-runtime-first");
            Release();
            using var lease = installation.TryAcquireManagement();
            if (lease is null) return Result(409, "installation-or-management-active");
            root = OllamaInstallationService.ResolvePrivateRoot(storageBase());
            if(!OllamaInstallationService.RootMatches(root,expectedRoot))return Result(409,"private-root-changed");
            var plan = await OllamaManagedRemovalPreview.PreviewAsync(root, token);
            if (!plan.PlanValid) return Result(409, "managed-removal-plan-invalid");
            var program = Path.GetFullPath(Path.Combine(root, OllamaManagedInstallation.ProgramDirectoryName));
            retired = Path.GetFullPath(Path.Combine(root, "retired-program-" + Guid.NewGuid().ToString("N")));
            // Move only the verified program child. Models/cache and external installations stay untouched.
            if (Path.GetDirectoryName(program) != root || Path.GetDirectoryName(retired) != root)
                return Result(409, "managed-path-invalid");
            Directory.Move(program, retired);
            return Result(200, "program-retired-recoverable");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return Result(409, "managed-program-in-use-or-move-failed"); }
        finally { gate.Release(); }
    }
    private void RestoreConnection()
    {
        if (!selected) return;
        Environment.SetEnvironmentVariable("LIVECLIPPER_OLLAMA_URL", originalUrl);
        Environment.SetEnvironmentVariable("LIVECLIPPER_OLLAMA_EXE", originalExecutable);
        selected = false;
    }
    private void Release()
    {
        RestoreConnection(); ready = false; alive = false; processId = null;
        process?.Dispose(); process = null;
        installationLease?.Dispose(); installationLease = null; Publish();
    }
    private async Task StopOwnedAsync()
    {
        RestoreConnection();
        if (process is { HasExited: false })
        { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        Release();
    }
}
