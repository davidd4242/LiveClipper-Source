using System;
using System.IO;
using Xunit;

namespace LiveClipper.Tests;

public sealed class StudioMissingMediaGuardTests
{
    private static string Source(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LiveClipper.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
    }

    private static string CommandsRoute()
    {
        var source = Source("src", "LiveClipper.App", "StudioEndpoints.cs");
        const string registration = "app.MapPost(\"/api/studio/clips/{id}/commands\"";
        var start = source.IndexOf(registration, StringComparison.Ordinal);
        Assert.True(start >= 0, "Commands route registration is missing.");
        var next = source.IndexOf("app.Map", start + registration.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static int Position(string source, string marker)
    {
        var position = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(position >= 0, $"Missing source anchor: {marker}");
        return position;
    }

    private static string MissingGuard(string route)
    {
        var start = Position(route, "if(missing.Count>0)");
        var end = Position(route, "var saved=studio.Project(id)");
        Assert.True(end > start, "Missing-media guard must precede project loading.");
        return route[start..end];
    }

    [Fact]
    public void CommandsRoute_MediaGuardRunsAfterEmptyBatchAndBeforeMutation()
    {
        var route = CommandsRoute();
        var emptyGuard = Position(route, "if(body.Commands is null||body.Commands.Count==0)");
        var emptyReturn = Position(route, "code=\"empty-batch\"");
        var snapshot = Position(route, "var mediaIds=");
        var lookup = Position(route, "StudioCommandMediaRefs.FindMissing(body.Commands,mediaIds.Contains)");
        var missingGuard = Position(route, "if(missing.Count>0)");
        var savedProject = Position(route, "var saved=studio.Project(id)");
        var apply = Position(route, "EditorCommandExecutor.ApplyBatch(current,body.Commands)");
        // R51-Y2b：保存改走 EditorRevisionStore.Commit → StudioStore.SaveProjectCas（durable revision CAS）。
        var save = Position(route, "EditorRevisionStore.Commit(studio,id,JsonSerializer.Serialize(next),baseRevision)");
        var history = Position(route, "EditorRevisionStore.RecordWrite(id,");
        var advance = Position(route, "EditorRevisionStore.TryAdvance(id,");

        Assert.True(emptyGuard < emptyReturn);
        Assert.True(emptyReturn < snapshot);
        Assert.True(snapshot < lookup);
        Assert.True(lookup < missingGuard);
        Assert.True(missingGuard < savedProject);
        Assert.True(savedProject < apply);
        Assert.True(apply < save);
        Assert.True(save < history);
        Assert.True(history < advance);
        Assert.Contains("return Results.BadRequest(", MissingGuard(route), StringComparison.Ordinal);
    }

    [Fact]
    public void CommandsRoute_UsesSharedCollectorWithOrdinalMediaSnapshot()
    {
        var route = CommandsRoute();
        Assert.Contains("var mediaIds=store.Clips().Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);", route, StringComparison.Ordinal);
        Assert.Contains("var missing=StudioCommandMediaRefs.FindMissing(body.Commands,mediaIds.Contains);", route, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandsRoute_MissingMediaReturns400WithNestedErrorAndTopLevelCode()
    {
        var route = CommandsRoute();
        var guard = MissingGuard(route);
        Assert.Contains("return Results.BadRequest(new {error=new {code=StudioErrorCodes.MissingMedia,message=\"命令引用了不存在的媒体\",missing},revision=EditorRevisionStore.Current(id),code=StudioErrorCodes.MissingMedia});", route, StringComparison.Ordinal);
        Assert.Contains("error=new {code=StudioErrorCodes.MissingMedia", guard, StringComparison.Ordinal);
        Assert.Contains("message=\"命令引用了不存在的媒体\",missing", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("studio.SaveProject(", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorRevisionStore.RecordWrite(", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorRevisionStore.TryAdvance(", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMediaCode_IsStableSnakeCase()
    {
        var source = Source("src", "LiveClipper.App", "StudioErrorCodes.cs");
        Assert.Contains("public const string MissingMedia = \"missing_media\";", source, StringComparison.Ordinal);
        Assert.Equal("missing_media", StudioErrorCodes.MissingMedia);
    }
}
