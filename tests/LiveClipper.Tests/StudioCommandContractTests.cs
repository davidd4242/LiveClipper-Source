using System.IO;
using LiveClipper.Core;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// 命令层的"接线还在"契约。行为由 EditorCommandTests 覆盖，这里只钉接口形状——
/// 免得哪天重构把路由或 revision 语义悄悄删掉，而单测全绿看不出来。
/// </summary>
public sealed class StudioCommandContractTests
{
    private static string Source(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LiveClipper.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
    }

    [Fact]
    public void StudioEndpoints_ExposeTheCommandsRoute()
    {
        var source = Source("src", "LiveClipper.App", "StudioEndpoints.cs");
        Assert.Contains("/api/studio/clips/{id}/commands", source, StringComparison.Ordinal);
        Assert.Contains("EditorCommandExecutor.ApplyBatch", source, StringComparison.Ordinal);
        Assert.Contains("\"invalid-command\"", source, StringComparison.Ordinal);
        Assert.Contains("\"stale-revision\"", source, StringComparison.Ordinal);
        Assert.Contains("\"empty-batch\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorEndpoints_CarryRevisionOnReadAndWrite()
    {
        var source = Source("src", "LiveClipper.App", "StudioEndpoints.cs");
        Assert.Contains("revision=EditorRevisionStore.Current(id)", source, StringComparison.Ordinal);
        Assert.Contains("X-Base-Revision", source, StringComparison.Ordinal);
        // 整份 PUT 也必须推进 revision，否则命令层读到的 revision 永远追不上真实写入。
        Assert.Contains("EditorRevisionStore.TryAdvance(id,baseRevision,out var revision)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandExecutor_StaysPure()
    {
        var source = Source("src", "LiveClipper.Core", "EditorCommands.cs");
        // 纯函数是这一步的地基：一旦它开始碰文件/网络/revision，测试与重放都会失真。
        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorRevisionStore", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StudioEndpoints_ExposeTheUndoRoute()
    {
        var source = Source("src", "LiveClipper.App", "StudioEndpoints.cs");
        Assert.Contains("/api/studio/clips/{id}/undo", source, StringComparison.Ordinal);
        Assert.Contains("EditorRevisionStore.PopSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("\"nothing-to-undo\"", source, StringComparison.Ordinal);
        // 版本不匹配时必须把快照塞回去，否则用户的撤销历史被静默吃掉
        Assert.Contains("PushSnapshot(id,snapshot)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionStore_PopsInLifoOrderAndDedupes()
    {
        var id = "clip-undo-" + System.Guid.NewGuid().ToString("N");
        EditorRevisionStore.Forget(id);
        EditorRevisionStore.PushSnapshot(id, "A");
        EditorRevisionStore.PushSnapshot(id, "B");
        EditorRevisionStore.PushSnapshot(id, "B");   // 与栈顶相同，不重复入栈

        Assert.Equal(2, EditorRevisionStore.SnapshotDepth(id));
        Assert.Equal("B", EditorRevisionStore.PopSnapshot(id));
        Assert.Equal("A", EditorRevisionStore.PopSnapshot(id));
        Assert.Null(EditorRevisionStore.PopSnapshot(id));
        Assert.Equal(0, EditorRevisionStore.SnapshotDepth(id));
        EditorRevisionStore.Forget(id);
    }
}
