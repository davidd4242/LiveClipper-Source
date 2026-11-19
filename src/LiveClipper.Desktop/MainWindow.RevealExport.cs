using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveClipper.Desktop;

// 导出面板「在文件夹中显示」：网页只传 jobId，路径由进程内 API 的真实 render 记录给出。
// 请求 { type:'reveal-export', requestId, jobId }，应答 { type:'reveal-export-result', requestId, ok, error }。
// 来源校验（app origin）在 WebMessageReceived 里已做；这里不接受任何网页给的路径。
public partial class MainWindow
{
    private static readonly Regex RevealJobId = new("^[A-Za-z0-9-]{1,64}$", RegexOptions.Compiled);

    private async Task HandleRevealExportAsync(JsonElement root, string? requestId)
    {
        string? error = null;
        try
        {
            var jobId = root.TryGetProperty("jobId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            if (jobId is null || !RevealJobId.IsMatch(jobId)) error = "导出任务编号无效。";
            else error = await RevealExportCoreAsync(jobId);
        }
        catch (Exception ex) { error = "无法打开所在文件夹：" + ex.Message; }
        PostToPage(new { type = "reveal-export-result", requestId, ok = error is null, error });
    }

    private async Task<string?> RevealExportCoreAsync(string jobId)
    {
        using var response = await Api.GetAsync("/api/studio/renders");
        if (!response.IsSuccessStatusCode) return "无法读取导出任务（HTTP " + (int)response.StatusCode + "）。";
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() != jobId) continue;
            if (item.GetProperty("status").GetString() != "completed") return "该导出任务尚未完成。";
            var path = item.GetProperty("outputPath").GetString();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('"')) return "导出记录中的成片路径无效。";
            if (!File.Exists(path)) return "成片文件不存在，可能已被移动或删除：" + path;
            // /select, 由 explorer 自己解析；参数是已校验的真实磁盘文件，不经过 shell。
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Path.GetFullPath(path) + "\"") { UseShellExecute = false });
            return null;
        }
        return "找不到该导出任务。";
    }
}
