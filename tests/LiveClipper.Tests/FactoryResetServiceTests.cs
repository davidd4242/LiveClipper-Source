using System.Text.Json;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class FactoryResetServiceTests
{
    [Fact]
    public void Execute_RemovesManagedDataAndRewritesDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "liveclipper-reset-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            foreach (var name in new[] { "buffer", "clips", "transcripts", "imported-media", "render-work" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                File.WriteAllText(Path.Combine(root, name, "data.bin"), name);
            }
            var unrelated = Path.Combine(root, "keep-me.txt");
            File.WriteAllText(unrelated, "keep");
            var database = Path.Combine(root, "liveclipper.db");
            File.WriteAllText(database, "db");
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"StorageRoot\":\"C:\\\\old\"}");

            var service = new FactoryResetService(database, root, root);
            service.Execute();

            foreach (var name in new[] { "buffer", "clips", "transcripts", "imported-media", "render-work" })
                Assert.False(Directory.Exists(Path.Combine(root, name)), name);
            Assert.False(File.Exists(database));
            Assert.True(File.Exists(unrelated));

            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "settings.json")));
            Assert.Equal(root, settings.RootElement.GetProperty("StorageRoot").GetString());
            Assert.Equal(Path.Combine(root, "exports"), settings.RootElement.GetProperty("LocalOutputRoot").GetString());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Execute_PreservesAnExplicitOriginalInsideAManagedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "liveclipper-reset-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clips = Path.Combine(root, "clips");
            Directory.CreateDirectory(clips);
            var original = Path.Combine(clips, "original.mp4");
            File.WriteAllText(original, "original");
            File.WriteAllText(Path.Combine(clips, "transcoded.mp4"), "temp");

            var service = new FactoryResetService(Path.Combine(root, "liveclipper.db"), root, root);
            service.Execute(new[] { original });

            Assert.True(File.Exists(original));
            // The service protects an explicit original by skipping its managed directory,
            // so sibling temporary files in that directory are preserved as well.
            Assert.True(File.Exists(Path.Combine(clips, "transcoded.mp4")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
