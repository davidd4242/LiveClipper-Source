using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Core.Bridge;

namespace LiveClipper.App;

/// <summary>
/// /api/bridge/* 最小端点：契约见 docs/bridge-api-v1.md，由 scripts/bridge-contract-test.mjs 验收。
/// 错误体统一 { error: { class, message, detail } }，class ∈ ValidationError|NotFound|Conflict|MappingError|FilmCraftError|BridgeTimeout。
/// </summary>
public static class BridgeEndpoints
{
    public static void Map(WebApplication app, BridgeJobService jobs, FcprojExportService fcproj)
    {
        app.MapGet("/api/bridge/capabilities", () => Results.Ok(new
        {
            filmcraft = new
            {
                version = jobs.Capabilities.FilmCraftVersion,
                schemaVersion = jobs.Capabilities.SchemaVersion
            },
            mapper = new { supports = jobs.Capabilities.Supports },
            export = new
            {
                variants = jobs.Capabilities.Variants,
                formats = jobs.Capabilities.Formats,
                audio = true,
                // B8-3：转场时长补偿默认开启；options.compensate=false 可关闭（响应 warnings 会说明）。
                compensate = new
                {
                    @default = jobs.Capabilities.CompensateDefault,
                    option = "options.compensate",
                    note = "默认开启：按 LC 转场压缩语义补正 FC 时间轴总时长；关闭则保留 FC 时间轴"
                }
            },
            limits = new
            {
                concurrentJobs = BridgeJobService.MaxConcurrentJobs,
                timeoutSeconds = 1800,
                chunkFrames = 450
            }
        }));

        app.MapPost("/api/bridge/export", async (HttpContext context) =>
        {
            BridgeExportRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<BridgeExportRequest>(context.RequestAborted);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return Error(400, "ValidationError", "请求体不是合法 JSON", ex.Message);
            }
            var (job, error, statusCode) = jobs.Enqueue(request ?? new BridgeExportRequest());
            return job is null
                ? Error(statusCode, error?.Class ?? "ValidationError", error?.Message ?? "请求无效", error?.Detail)
                : Results.Json(new
                {
                    jobId = job.Id,
                    status = job.Status,
                    createdAt = job.CreatedAt,
                    resultPath = job.ResultPath,
                    warnings = job.Warnings
                }, statusCode: 202);
        });

        app.MapGet("/api/bridge/jobs", (int? limit) =>
        {
            var list = jobs.List(limit ?? 50);
            return Results.Ok(new { jobs = list.Select(JobDto).ToArray(), count = list.Count });
        });

        app.MapGet("/api/bridge/jobs/{id}", (string id) =>
        {
            var (job, error, statusCode) = jobs.Get(id);
            return job is null
                ? Error(statusCode, error?.Class ?? "NotFound", error?.Message ?? "任务不存在", error?.Detail)
                : Results.Ok(JobDto(job));
        });

        app.MapPost("/api/bridge/jobs/{id}/cancel", (string id) =>
        {
            var (job, error, statusCode) = jobs.Cancel(id);
            return job is null
                ? Error(statusCode, error?.Class ?? "Conflict", error?.Message ?? "无法取消", error?.Detail)
                : Results.Ok(JobDto(job));
        });

        // C1 MVP：把编辑器工程生成为临时 FilmCraft 工程（同步、秒级；不回写、不导出）。
        app.MapPost("/api/bridge/fcproj", async (HttpContext context) =>
        {
            BridgeFcprojRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<BridgeFcprojRequest>(context.RequestAborted);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return Error(400, "ValidationError", "请求体不是合法 JSON", ex.Message);
            }
            if (request is null || string.IsNullOrWhiteSpace(request.ClipId))
                return Error(400, "ValidationError", "clipId 不能为空", null);
            try
            {
                var result = fcproj.Export(request.ClipId, request.Variant, context.RequestAborted, request.Project);
                return Results.Ok(new
                {
                    fcprojPath = result.FcprojPath,
                    workDir = result.WorkDir,
                    editorProjectPath = result.EditorProjectPath,
                    clipId = result.ClipId,
                    warnings = result.Warnings
                });
            }
            catch (BridgeExecutionException ex)
            {
                return Error(StatusForC1(ex.Error.Class), ex.Error.Class, ex.Error.Message, ex.Error.Detail);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(500, "MappingError", "生成临时工程失败", ex.Message);
            }
        });

        // C1 MVP：只允许打开 bridge-roundtrip 下生成的工程；无 FilmCraft 时返回 404，前端用路径降级。
        app.MapPost("/api/bridge/open", (BridgeOpenRequest? request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.FcprojPath))
                return Error(400, "ValidationError", "fcprojPath 不能为空", null);
            var fcprojPath = request.FcprojPath.Trim();
            if (!FcprojExportService.IsUnderBridgeRoundtrip(fcproj.StorageRoot, fcprojPath))
                return Error(400, "ValidationError", "fcprojPath 必须位于 bridge-roundtrip 目录下", fcprojPath);
            if (!File.Exists(fcprojPath))
                return Error(404, "NotFound", "临时工程不存在，请重新生成", fcprojPath);

            var probe = FilmCraftLocator.Locate();
            var failure = FilmCraftOpenClassifier.Classify(probe);
            if (failure is not null)
            {
                if (failure.ErrorClass == "VersionMismatch")
                {
                    return Results.Json(new
                    {
                        error = new { @class = failure.ErrorClass, message = failure.Message, detail = failure.Detail },
                        guiPath = probe.Install?.GuiPath,
                        actualVersion = probe.Install?.Version,
                        expectedVersion = FilmCraftLocator.ExpectedVersion,
                        fcprojPath
                    }, statusCode: failure.StatusCode);
                }

                if (probe.Install is null)
                {
                    return Results.Json(new
                    {
                        error = new { @class = failure.ErrorClass, message = failure.Message, detail = failure.Detail },
                        guiPath = (string?)null,
                        fcprojPath,
                        searchedPaths = probe.Searched
                    }, statusCode: failure.StatusCode);
                }

                return Results.Json(new
                {
                    error = new { @class = failure.ErrorClass, message = failure.Message, detail = failure.Detail },
                    guiPath = probe.Install.GuiPath,
                    fcprojPath
                }, statusCode: failure.StatusCode);
            }

            var install = probe.Install!;
            var launch = FilmCraftGuiLauncher.Launch(install.GuiPath);
            if (launch.ErrorClass is not null)
                return Error(500, launch.ErrorClass, launch.Message ?? "无法启动 FilmCraft", launch.Detail);
            return Results.Ok(new
            {
                launched = true,
                pid = launch.Pid,
                guiPath = install.GuiPath,
                fcprojPath,
                mode = "gui",
                message = "FilmCraft 已启动；如未自动打开工程，请用「文件 → 打开」选择该路径。"
            });
        });
    }

    private static int StatusForC1(string errorClass) => errorClass switch
    {
        "NotFound" => 404,
        "MappingError" or "ValidationError" => 422,
        "BridgeTimeout" => 504,
        _ => 500
    };

    private static object JobDto(BridgeJobSnapshot job) => new
    {
        id = job.Id,
        clipId = job.ClipId,
        variant = job.Variant,
        format = job.Format,
        status = job.Status,
        progress = job.Progress,
        resultPath = job.ResultPath,
        error = job.Error is null ? null : new { @class = job.Error.Class, message = job.Error.Message, detail = job.Error.Detail },
        createdAt = job.CreatedAt,
        startedAt = job.StartedAt,
        finishedAt = job.FinishedAt,
        warnings = job.Warnings
    };

    private static IResult Error(int statusCode, string errorClass, string message, string? detail) =>
        Results.Json(new { error = new { @class = errorClass, message, detail } }, statusCode: statusCode);
}

/// <summary>C1：生成临时 FilmCraft 工程请求。</summary>
public sealed record BridgeFcprojRequest(string? ClipId, string? Variant, EditorProject? Project = null);

/// <summary>C1：打开临时 FilmCraft 工程请求（路径必须位于 bridge-roundtrip 下）。</summary>
public sealed record BridgeOpenRequest(string? FcprojPath);

