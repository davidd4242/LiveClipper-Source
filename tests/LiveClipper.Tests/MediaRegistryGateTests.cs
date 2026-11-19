using LiveClipper.Core;
using LiveClipper.Domain;
using Xunit;

namespace LiveClipper.Tests;

public sealed class MediaRegistryGateTests
{
    private static bool Blocked(Task task) => !task.Wait(TimeSpan.FromMilliseconds(400));

    [Fact]
    public void DeleteClip_WaitsForInFlightReader_ThenCompletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "lc-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new Store(Path.Combine(root, "t.db"));
        store.Migrate();
        var clip = new Clip("gate-clip", "gate-event", Path.Combine(root, "x.mp4"), null, DateTimeOffset.UtcNow);
        store.SaveClip(clip);
        var reader = MediaRegistryGate.EnterRead();
        var delete = Task.Run(() => store.DeleteClip(clip, root));
        Assert.True(Blocked(delete), "DeleteClip must not run while a PUT holds the read gate");
        Assert.Contains(store.Clips(), c => c.Id == clip.Id);
        reader.Dispose();
        Assert.True(delete.Wait(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(store.Clips(), c => c.Id == clip.Id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ReadersRunConcurrently()
    {
        using var a = MediaRegistryGate.EnterRead();
        var second = Task.Run(() => { using var b = MediaRegistryGate.EnterRead(); });
        Assert.True(second.Wait(TimeSpan.FromSeconds(2)));
    }
}
