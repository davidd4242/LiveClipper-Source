using System;
using System.IO;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// Program.cs 全局异常处理器（app.UseExceptionHandler）的错误体契约。
/// 处理器写在顶层语句里，没有进程内宿主可测，沿用 StudioEndpointErrorContractTests 的源码锚点做法：
/// 只声明普通 JSON；ArgumentException 与 BadHttpRequestException 走 400，其余 500；{error} 保留且多一个顶层 code。
/// </summary>
public sealed class ProgramErrorHandlerTests
{
    private static string Source(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LiveClipper.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
    }

    /// <summary>从唯一的 app.UseExceptionHandler( 截到它的 "}));"，即处理器本体。</summary>
    private static string HandlerBlock()
    {
        var program = Source("src", "LiveClipper.App", "Program.cs");
        var start = program.IndexOf("app.UseExceptionHandler(", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 app.UseExceptionHandler(");
        Assert.Equal(start, program.LastIndexOf("app.UseExceptionHandler(", StringComparison.Ordinal));
        var end = program.IndexOf("}));", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到异常处理器的结尾 }));");
        return program[start..end];
    }

    private static string WriteLine() =>
        Assert.Single(HandlerBlock().Split('\n'), line => line.Contains("WriteAsJsonAsync(", StringComparison.Ordinal));

    [Fact]
    public void ExceptionHandler_DeclaresPlainJson_NotProblemDetails()
    {
        // 体是 {error,code}，不是 RFC 7807 ProblemDetails，不能再声明 problem+json。
        var block = HandlerBlock();
        Assert.DoesNotContain("problem+json", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("application/json", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionHandler_KeepsArgumentExceptionAs400_OtherwiseAs500()
    {
        var block = HandlerBlock();
        Assert.Contains("ex is ArgumentException", block, StringComparison.Ordinal);
        Assert.Contains("?400:500", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionHandler_BodyKeepsErrorMessage_AndAddsTopLevelCode()
    {
        var write = WriteLine();
        Assert.Contains("error=ex?.Message", write, StringComparison.Ordinal);
        Assert.Contains("code=", write, StringComparison.Ordinal);
        // code 是顶层字段，error 仍是字符串，不能改成嵌套的 error 对象。
        Assert.DoesNotContain("error=new", write, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionHandler_CodeFollowsStatus_InvalidRequestThenInternalError()
    {
        // 和 ?400:500 同序：ArgumentException 分支是 invalid_request，其它是 internal_error。
        var write = WriteLine();
        var invalid = write.IndexOf("invalid_request", StringComparison.Ordinal);
        var internalError = write.IndexOf("internal_error", StringComparison.Ordinal);
        Assert.True(invalid >= 0, write);
        Assert.True(internalError > invalid, write);
    }

    [Fact]
    public void ExceptionHandler_TreatsBadHttpRequestAsInvalidRequest()
    {
        // 读不出请求体（BadHttpRequestException）也是调用方的错：和 ArgumentException 同走 400 / invalid_request，不算 500。
        var line = Assert.Single(HandlerBlock().Split('\n'), text => text.Contains("var invalidRequest=", StringComparison.Ordinal));
        Assert.Contains("ex is ArgumentException or ", line, StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.Http.BadHttpRequestException", line, StringComparison.Ordinal);
    }
}
