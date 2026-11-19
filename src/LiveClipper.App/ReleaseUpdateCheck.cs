using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LiveClipper.App;

internal static class ReleaseUpdateCheck
{
    private static readonly HttpClient GitHub = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        BaseAddress = new Uri("https://api.github.com/"),
        Timeout = TimeSpan.FromSeconds(8)
    };

    internal sealed record Result(string Status, string Message, string? ReleaseUrl, string? BaiduUrl);
    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, bool Draft, bool Prerelease, Asset[]? Assets, string? Body);
    private sealed record Asset(string? Name, string? State, long Size);

    private static string? BaiduShare(string? text)
    {
        var url = text?.Trim();
        return url is not null && Regex.IsMatch(url,
            @"\Ahttps://pan\.baidu\.com/(?:s/[-A-Za-z0-9_]+(?:\?pwd=[A-Za-z0-9]+)?|share/init\?surl=[-A-Za-z0-9_]+(?:&pwd=[A-Za-z0-9]+)?)\z")
            ? url : null;
    }

    internal static async Task<Result> CheckAsync(string appVersion, IConfiguration config, CancellationToken cancellation)
    {
        var repository = config["Updates:GitHubRepository"]?.Trim();
        var baidu = BaiduShare(config["Updates:BaiduShareUrl"]);
        Result Reply(string status, string message, string? url = null) => new(status, message, url, baidu);

        if (string.IsNullOrEmpty(repository))
            return Reply("unconfigured", "尚未配置公开的 GitHub 更新源。");
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*\z"))
            return Reply("invalid_source", "更新源配置有误，无法检查版本。");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd($"LiveClipper/{appVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await GitHub.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Reply("unavailable", "更新源不可访问，或尚未发布公开的稳定版。");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized)
                return Reply("restricted", "GitHub 限制了本次访问，请稍后再试或联系发布者。");
            if (!response.IsSuccessStatusCode)
                return Reply("service_error", "GitHub 更新服务暂时不可用，请稍后重试。");

            var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: cancellation);
            var tag = release?.Tag;
            if (release is null || release.Draft || release.Prerelease || tag is null
                || !Regex.IsMatch(tag, @"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")
                || !Version.TryParse(tag[1..], out var latest))
                return Reply("invalid_release", "发布信息中没有可比较的稳定版应用版本号。");

            // 忽略源码归档和校验文件；只认可发布者上传的 Windows 安装包。
            if (release.Assets?.Any(a => a is { State: "uploaded", Size: > 0 }
                && a.Name is ("LiveClipperSetup.exe" or "LiveClipper-win-x64.zip" or "LiveClipper-轻量安装包.zip")) != true)
                return Reply("no_package", $"GitHub 的 {tag} 尚未提供 Windows 安装包。");

            // 仅解析本次 GitHub 正式发布的一行；不开网盘、不渲染远端 Markdown。
            const string label = "Baidu-Share:";
            var mirrorLine = release.Body?.Split('\n').FirstOrDefault(line => line.StartsWith(label, StringComparison.Ordinal));
            if (mirrorLine is not null) baidu ??= BaiduShare(mirrorLine[label.Length..]);

            var local = Version.Parse(appVersion);
            if (latest > local)
                return Reply("available", $"发现新版本 {tag}（当前 v{local}）。", $"https://github.com/{repository}/releases/tag/{tag}");
            if (latest < local)
                return Reply("local_newer", $"当前 v{local} 高于已发布的 {tag}，无需降级。");
            return Reply("current", $"当前 v{local} 已是最新公开稳定版。");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return Reply("timeout", "连接 GitHub 超时，请检查网络后重试。");
        }
        catch (HttpRequestException)
        {
            return Reply("network_error", "无法连接 GitHub，请检查网络后重试。");
        }
        catch (JsonException)
        {
            return Reply("invalid_release", "GitHub 返回的发布信息无法读取。");
        }
    }
}
