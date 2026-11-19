using System.IO;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

[assembly: WebApplicationFactoryContentRoot("LiveClipper.App", ".", "LiveClipper.App.dll", "0")]

namespace LiveClipper.Desktop;

/// <summary>
/// Starts the existing LiveClipper API inside the desktop process with TestServer.
/// No Kestrel listener and no child server process: WebView2 reads through an
/// in-memory HttpClient while the same host keeps the real endpoint implementations.
/// </summary>
internal sealed class InProcessApiHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    private InProcessApiHost(WebApplicationFactory<Program> factory, HttpClient client)
    {
        _factory = factory;
        _client = client;
    }

    public HttpClient Client => _client;

    public static async Task<(InProcessApiHost? Host, string? Error)> TryStartAsync()
    {
        NoPortFactory? factory = null;
        HttpClient? client = null;
        try
        {
            Console.Error.WriteLine("[inproc] begin");
            // WebApplicationFactory finds the content root from the attribute above;
            // the relative "." is resolved against the process working directory.
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            var port = Program.ResolvePort(Environment.GetEnvironmentVariable("LIVECLIPPER_PORT")).ToString(System.Globalization.CultureInfo.InvariantCulture);
            factory = new NoPortFactory();
            // Host startup does synchronous work; keep it off the WPF UI thread so
            // OnLoaded can continue and WebView2 can initialize.
            client = await Task.Run(() => factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri($"http://127.0.0.1:{port}")
            }));
            Console.Error.WriteLine($"[inproc] client ready, server={factory.Server.GetType().FullName}");
            // Do not put a global timeout on this client: it also carries the SSE
            // danmaku stream and long export requests. The health probe below gets
            // its own short deadline instead.
            client.Timeout = Timeout.InfiniteTimeSpan;

            Console.Error.WriteLine("[inproc] health begin");
            using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var health = await client.GetAsync("/api/health", healthTimeout.Token);
            Console.Error.WriteLine($"[inproc] health {(int)health.StatusCode}");
            if (!health.IsSuccessStatusCode)
                return (null, $"in-process API health returned {(int)health.StatusCode}");

            var host = new InProcessApiHost(factory, client);
            factory = null;
            client = null;
            return (host, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[inproc] failed: {ex}");
            return (null, ex.GetBaseException().Message);
        }
        finally
        {
            // Any failure after the host has started must release the factory too:
            // it may already have started recovery/recording. Returning a null host
            // without disposal would leave an unowned host behind.
            if (client is not null) { try { client.Dispose(); } catch { } }
            if (factory is not null)
            {
                try { await Task.Run(() => factory.Dispose()); } catch { }
            }
        }
    }

    private readonly object _disposeLock=new();
    private Task? _disposeTask;

    public async ValueTask DisposeAsync()
    {
        // Cache only the underlying factory disposal. Applying the two-minute
        // limit per close attempt lets a later close observe eventual completion
        // after a timeout, without starting a second Dispose of the same factory.
        Task dispose;
        lock(_disposeLock)
        {
            dispose=_disposeTask??=Task.Run(()=>_factory.Dispose());
        }
        try
        {
            await dispose.WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally
        {
            try { _client.Dispose(); } catch { }
        }
    }

    private sealed class NoPortFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseTestServer();
        }
    }
}
