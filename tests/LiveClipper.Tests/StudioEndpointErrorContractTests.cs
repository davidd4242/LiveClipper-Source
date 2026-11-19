using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// /api/studio/clips/{id} 编辑接口错误体的机器可读 code 契约。
/// 端点没有进程内宿主可测，这里沿用 StudioCommandContractTests 的源码锚点做法：
/// 每个非 2xx 出口都带 StudioErrorCodes 常量、状态码调用不变，常量值是稳定的 snake_case。
/// </summary>
public sealed class StudioEndpointErrorContractTests
{
    private static readonly string[] ErrorResultMarkers =
    {
        "Results.NotFound(",
        "Results.BadRequest(",
        "Results.Conflict(",
        "Results.Json(",
    };

    private static string Source(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LiveClipper.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
    }

    private static string Endpoints() => Source("src", "LiveClipper.App", "StudioEndpoints.cs");

    private static bool IsErrorExit(string text) =>
        ErrorResultMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal));

    /// <summary>从路由注册处截到下一个 app.Map，作为该端点的处理体。</summary>
    private static string RouteBody(string source, string routeRegistration)
    {
        var start = source.IndexOf(routeRegistration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到路由注册：{routeRegistration}");
        var next = source.IndexOf("app.Map", start + 1, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    /// <summary>同时包含 marker 和错误出口调用的那一行，必须恰好一行。</summary>
    private static string ErrorExitLine(string marker, string? statusCall = null)
    {
        var matches = Endpoints().Split('\n')
            .Where(text => text.Contains(marker, StringComparison.Ordinal) && IsErrorExit(text) && (statusCall is null || text.Contains(statusCall, StringComparison.Ordinal)))
            .ToArray();
        return Assert.Single(matches);
    }

    [Fact]
    public void ErrorCodeConstants_AreStableSnakeCase()
    {
        Assert.Equal("revision_conflict", StudioErrorCodes.RevisionConflict);
        Assert.Equal("invalid_project", StudioErrorCodes.InvalidProject);
        Assert.Equal("invalid_command", StudioErrorCodes.InvalidCommand);
        Assert.Equal("session_not_found", StudioErrorCodes.SessionNotFound);
        Assert.Equal("nothing_to_undo", StudioErrorCodes.NothingToUndo);
        Assert.Equal("nothing_to_redo", StudioErrorCodes.NothingToRedo);
        Assert.Equal("not_found", StudioErrorCodes.NotFound);
        Assert.Equal("missing_media", StudioErrorCodes.MissingMedia);
        Assert.Equal("precondition_required", StudioErrorCodes.PreconditionRequired);
        Assert.Equal("invalid_revision", StudioErrorCodes.InvalidRevision);
        Assert.Equal("stale_epoch", StudioErrorCodes.StaleEpoch);
        Assert.Equal("project_schema_too_new", StudioErrorCodes.ProjectSchemaTooNew);
        Assert.Equal("project_schema_invalid", StudioErrorCodes.ProjectSchemaInvalid);
        Assert.Equal("backup_invalid", StudioErrorCodes.BackupInvalid);
        Assert.Equal("storage_schema_too_new", StudioErrorCodes.StorageSchemaTooNew);

        var values = typeof(StudioErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
        // 新增 code 时同步更新上面的逐项断言和这里的数量。
        Assert.Equal(15, values.Length);
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        foreach (var value in values)
            Assert.Matches("^[a-z]+(_[a-z]+)*$", value);
    }

    [Theory]
    [InlineData("session-mismatch", "session_not_found")]
    [InlineData("session-busy", "revision_conflict")]
    [InlineData("stale-revision", "revision_conflict")]
    [InlineData("some-future-code", "revision_conflict")]
    public void ConflictExceptionCodes_MapToTopLevelCodes(string conflictCode, string expected)
    {
        Assert.Equal(expected, StudioErrorCodes.ForConflict(conflictCode));
    }

    [Fact]
    public void ConflictMapping_TracksRevisionStoreCodes()
    {
        // ForConflict 按字符串匹配 EditorRevisionStore 的 kebab-case；那边改名必须同步 StudioErrorCodes。
        var store = Source("src", "LiveClipper.Core", "EditorRevisionStore.cs");
        Assert.Contains("\"session-mismatch\"", store, StringComparison.Ordinal);
        Assert.Contains("\"session-busy\"", store, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app.MapPut(\"/api/studio/clips/{id}/editor\"")]
    [InlineData("app.MapPost(\"/api/studio/clips/{id}/commands\"")]
    [InlineData("app.MapPost(\"/api/studio/clips/{id}/sessions\"")]
    [InlineData("app.MapPost(\"/api/studio/clips/{id}/sessions/{sid}/end\"")]
    [InlineData("app.MapPost(\"/api/studio/clips/{id}/undo\"")]
    [InlineData("app.MapPost(\"/api/studio/clips/{id}/redo\"")]
    public void EditorRoutes_EveryErrorExitCarriesCode(string routeRegistration)
    {
        var lines = RouteBody(Endpoints(), routeRegistration).Split('\n');
        var errorLines = lines.Where(IsErrorExit).ToArray();
        Assert.NotEmpty(errorLines);
        foreach (var errorLine in errorLines)
            Assert.Contains("code=StudioErrorCodes.", errorLine, StringComparison.Ordinal);
        foreach (var notFoundLine in lines.Where(text => text.Contains("Results.NotFound(", StringComparison.Ordinal)))
            Assert.Contains("code=StudioErrorCodes.NotFound", notFoundLine, StringComparison.Ordinal);
        // 成功体不变：Results.Ok 行不带 code。
        foreach (var okLine in lines.Where(text => text.Contains("Results.Ok(", StringComparison.Ordinal)))
            Assert.DoesNotContain("StudioErrorCodes", okLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("holder=ex.Holder", "Results.Conflict(", "code=StudioErrorCodes.ForConflict(ex.Code)")]
    [InlineData("\"stale-revision\"", "Results.Conflict(", "code=StudioErrorCodes.RevisionConflict")]
    [InlineData("\"empty-batch\"", "Results.BadRequest(", "code=StudioErrorCodes.InvalidCommand")]
    [InlineData("\"invalid-command\"", "Results.BadRequest(", "code=StudioErrorCodes.InvalidCommand")]
    [InlineData("\"nothing-to-undo\"", "Results.Conflict(", "code=StudioErrorCodes.NothingToUndo")]
    [InlineData("\"nothing-to-redo\"", "Results.Conflict(", "code=StudioErrorCodes.NothingToRedo")]
    [InlineData("error=projectInvalid.Message", "Results.Json(", "code=StudioErrorCodes.InvalidProject")]
    [InlineData("error=projectInvalid.Message", "Results.BadRequest(", "code=StudioErrorCodes.InvalidProject")]
    public void ErrorSites_KeepStatusCallAndAddCode(string marker, string statusCall, string code)
    {
        var line = ErrorExitLine(marker, statusCall);
        Assert.Contains(statusCall, line, StringComparison.Ordinal);
        Assert.Contains(code, line, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectValidation_StaysA400WithInvalidProject()
    {
        var put = RouteBody(Endpoints(), "app.MapPut(\"/api/studio/clips/{id}/editor\"");
        // 与 Program.cs 全局处理器同口径：只有 ArgumentException 是 400，其余异常照旧交给全局处理器。
        Assert.Contains("try{plan=EditorProjectCodec.Deserialize(submitted);EditorRenderService.Validate(plan);}", put, StringComparison.Ordinal);
        Assert.Contains("catch(Exception projectInvalid) when(projectInvalid is ArgumentException or JsonException or NotSupportedException)", put, StringComparison.Ordinal);
        Assert.Contains("statusCode:StatusCodes.Status400BadRequest", ErrorExitLine("error=projectInvalid.Message", "Results.Json("), StringComparison.Ordinal);
    }
}
