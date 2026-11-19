using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

public sealed class FactoryResetTests
{
    [Fact]
    public void Reset_ClearsBothDatabasesAndOwnedDataButPreservesOriginalsAndTools()
    {
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-reset-"+Guid.NewGuid().ToString("N"));
        var active=Path.Combine(root,"active");var defaults=Path.Combine(root,"defaults");Directory.CreateDirectory(active);Directory.CreateDirectory(defaults);
        try
        {
            var store=new Store(Path.Combine(active,"liveclipper.db"));store.Migrate();var studio=new StudioStore(store.DatabasePath);studio.Migrate();store.SaveClip(new Clip("clip","event",Path.Combine(active,"original.mp4"),null,DateTimeOffset.UtcNow));studio.SaveProject("clip","{}");
            new Store(Path.Combine(defaults,"liveclipper.db")).Migrate();
            Directory.CreateDirectory(Path.Combine(active,"render-work","job"));File.WriteAllText(Path.Combine(active,"render-work","job","temporary.mp4"),"cache");
            File.WriteAllText(Path.Combine(active,"original.mp4"),"original");Directory.CreateDirectory(Path.Combine(defaults,"tools"));File.WriteAllText(Path.Combine(defaults,"tools","ffmpeg.exe"),"tool");File.WriteAllText(Path.Combine(defaults,"settings.json"),"old settings");
            Directory.CreateDirectory(Path.Combine(active,"clips"));var importedOriginal=Path.Combine(active,"clips","my-original.mp4");File.WriteAllText(importedOriginal,"imported original");
            var reset=new FactoryResetService(store.DatabasePath,defaults);var summary=reset.Summary(store,studio);Assert.Equal(1,summary.Clips);Assert.Equal(1,summary.Projects);reset.Execute([importedOriginal]);
            Assert.False(File.Exists(store.DatabasePath));Assert.False(File.Exists(Path.Combine(defaults,"liveclipper.db")));Assert.False(Directory.Exists(Path.Combine(active,"render-work")));Assert.Equal("original",File.ReadAllText(Path.Combine(active,"original.mp4")));Assert.Equal("tool",File.ReadAllText(Path.Combine(defaults,"tools","ffmpeg.exe")));
            Assert.Equal("imported original",File.ReadAllText(importedOriginal));
            var settings=JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(Path.Combine(defaults,"settings.json")))!;Assert.Equal("ollama",settings.AiProvider);Assert.Equal(1000,settings.DanmakuRecordLimit);Assert.Equal(defaults,settings.StorageRoot);
            store.Migrate();studio.Migrate();Assert.Empty(store.Clips());Assert.Null(studio.Project("clip"));
        }
        finally{Directory.Delete(root,true);}
    }
}
