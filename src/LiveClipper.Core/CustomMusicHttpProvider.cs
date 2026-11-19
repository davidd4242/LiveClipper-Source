using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LiveClipper.Core;

/// <summary>自定义音乐 HTTP 协议 v1：POST 用户保存的完整 URL，仅接受 200 + audio/wav 原始 WAV。</summary>
public sealed class CustomMusicHttpProvider(MediaTools media)
{
    public const string Protocol = "liveclipper.music.v1";
    public const long MaxBytes = 80_000_000;
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(150);

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>成功即 outputPath 已是通过 RIFF/WAVE 头 + ffprobe 音频流/有限正时长校验的文件；失败抛 InvalidOperationException（消息安全），取消抛 OperationCanceledException。</summary>
    public async Task GenerateToFileAsync(MusicCreateRequest request, string generationUrl, string model, string? bearerToken, string outputPath, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(generationUrl, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("自定义音乐接口地址无效：仅支持 HTTPS 或本机 HTTP，且不能带参数、凭据或片段");
        if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("音乐输出路径无效");
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output)) throw new InvalidOperationException("音乐输出文件已存在");
        var part = output + ".part";
        var directory = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("音乐输出路径无效");
        Directory.CreateDirectory(directory);

        var body = JsonSerializer.Serialize(new
        {
            protocol = Protocol,
            model,
            genre = request.Genre,
            style = request.Style,
            content = request.Content,
            theme = request.Theme,
            duration_seconds = request.DurationSeconds
        });

        using var timeout = new CancellationTokenSource(TotalTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var ct = linked.Token;
        FileStream? stream = null;
        var created = false;
        var userCancelled = false;
        try
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (!string.IsNullOrWhiteSpace(bearerToken)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
                using var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
                var status = (int)response.StatusCode;
                if (status == 202) throw new InvalidOperationException("自定义音乐接口返回 202：暂不支持异步任务，请直接返回 200 audio/wav");
                if (status != 200) throw new InvalidOperationException("自定义音乐接口返回 HTTP " + status.ToString(CultureInfo.InvariantCulture));
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(mediaType, "audio/wav", StringComparison.OrdinalIgnoreCase) && !string.Equals(mediaType, "audio/x-wav", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("自定义音乐接口必须返回 audio/wav 原始 WAV");
                var declared = response.Content.Headers.ContentLength;
                if (declared is { } length && (length < 1 || length > MaxBytes)) throw new InvalidOperationException("自定义音乐接口返回的音频大小超出限制");

                try { stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); created = true; }
                catch (IOException) { throw new InvalidOperationException("音乐临时文件已存在或无法创建"); }
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBytes) throw new InvalidOperationException("自定义音乐接口返回的音频大小超出限制");
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total < 1) throw new InvalidOperationException("自定义音乐接口返回空音频");
                await stream.FlushAsync(ct);
                await stream.DisposeAsync();
                stream = null;
            }
            catch (HttpRequestException) { throw new InvalidOperationException("自定义音乐接口请求失败"); }

            await ValidateWavAsync(part, ct);
            ct.ThrowIfCancellationRequested();
            if (File.Exists(output)) throw new InvalidOperationException("音乐输出文件已存在");
            File.Move(part, output, false);
            created = false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new InvalidOperationException("自定义音乐接口请求超时");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            userCancelled = true;
            throw;
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync();
            if (created)
            {
                string? cleanupError = null;
                try { File.Delete(part); }
                catch (IOException) { cleanupError = "IO 错误"; }
                catch (UnauthorizedAccessException) { cleanupError = "权限不足"; }
                if (cleanupError is not null)
                    throw new InvalidOperationException("自定义音乐临时文件清理失败（" + cleanupError + "），请手动清理 music/generated 目录下的 .part 文件" + (userCancelled ? "；任务本身已取消" : ""));
            }
        }
    }

    private async Task ValidateWavAsync(string path, CancellationToken token)
    {
        var header = new byte[12];
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var got = 0;
            while (got < 12) { var n = await file.ReadAsync(header.AsMemory(got), token); if (n == 0) break; got += n; }
            if (got < 12 || Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
                throw new InvalidOperationException("自定义音乐接口返回的不是 WAV 文件");
        }
        string json;
        try { json = await media.RunAsync(media.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_type:format=duration", "-of", "json", path], token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new InvalidOperationException("无法校验音乐文件：ffprobe 不可用或文件无法解码"); }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var hasAudio = root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array
                && streams.EnumerateArray().Any(x => x.TryGetProperty("codec_type", out var t) && t.GetString() == "audio");
            var ok = root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && double.IsFinite(seconds) && seconds > 0;
            if (!hasAudio || !ok) throw new InvalidOperationException("自定义音乐接口返回的 WAV 没有有效音频或时长");
        }
        catch (JsonException) { throw new InvalidOperationException("无法校验音乐文件：ffprobe 输出无效"); }
    }
}
