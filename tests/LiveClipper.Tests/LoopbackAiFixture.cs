using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveClipper.Tests;

public sealed record LoopbackRequest(string Method, string Path, string? Authorization, string? Model, string? Body = null)
{
    /// <summary>Chat messages exactly as sent on the wire (role + content), in order.</summary>
    public IReadOnlyList<(string Role, string Content)> Messages()
    {
        using var doc = JsonDocument.Parse(Body ?? "{}");
        return doc.RootElement.TryGetProperty("messages", out var messages)
            ? messages.EnumerateArray().Select(m => (m.GetProperty("role").GetString() ?? "", m.GetProperty("content").GetString() ?? "")).ToArray()
            : [];
    }
}

/// <summary>Real Kestrel server on numeric 127.0.0.1 with an OS-assigned port; records requests.</summary>
public sealed class LoopbackAiServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<LoopbackRequest> _requests = [];
    public int Port { get; }
    public string Origin => $"http://127.0.0.1:{Port}";
    public string? RedirectTo { get; set; }
    /// <summary>Fixed model message content (a JSON string in the model's own schema); default is the analyze-events reply.</summary>
    public string? ReplyContent { get; set; }
    public int ResponseDelayMilliseconds { get; set; }
    public long? LoadDurationNs { get; set; }
    public object? Usage { get; set; }

    public LoopbackAiServer()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(async context =>
        {
            string? model = null;
            string? bodyText = null;
            using (var reader = new StreamReader(context.Request.Body))
            {
                var text = await reader.ReadToEndAsync(); bodyText = text;
                try { using var doc = JsonDocument.Parse(text); model = doc.RootElement.GetProperty("model").GetString(); } catch { }
            }
            lock (_requests) _requests.Add(new(context.Request.Method, context.Request.Path.Value ?? "", context.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null, model, bodyText));
            if (RedirectTo is not null)
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = RedirectTo;
                return;
            }
            if (context.Request.Method == "POST" && ResponseDelayMilliseconds > 0)
                await Task.Delay(ResponseDelayMilliseconds, context.RequestAborted);
            context.Response.ContentType = "application/json";
            var content = ReplyContent ?? JsonSerializer.Serialize(new { events = new[] { new { topic = "t", description = "d", start = 0, end = 3, hook = "h" } } });
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { version = "fixture", usage = Usage, load_duration = LoadDurationNs, message = new { content }, choices = new[] { new { message = new { content } } } }));
        });
        _app.Start();
        var address = _app.Urls.Single();
        var uri = new Uri(address);
        Assert.Equal("127.0.0.1", uri.Host);
        Port = uri.Port;
    }

    public IReadOnlyList<LoopbackRequest> Requests { get { lock (_requests) return _requests.ToArray(); } }
    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

/// <summary>Real handler that refuses everything except http://127.0.0.1:{port}, rechecked on every send; no redirect/cookie/proxy.</summary>
public sealed class LoopbackOnlyHandler : DelegatingHandler
{
    private readonly HashSet<int> _ports;
    private int _blocked;
    public int Blocked => _blocked;

    public LoopbackOnlyHandler(params int[] allowedPorts)
        : base(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        => _ports = [.. allowedPorts];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.Loopback.Equals(ip) || !_ports.Contains(uri.Port))
        {
            Interlocked.Increment(ref _blocked);
            throw new InvalidOperationException("fixture transport refused non-fixture target");
        }
        return base.SendAsync(request, token);
    }
}
