using LiveClipper.Core;
using Xunit;

namespace LiveClipper.Tests;

public sealed class MusicModelDirectoryTests
{
    [Fact]
    public void ImportPersistsReadOnlyReferenceAndNeverTakesOwnership()
    {
        var root=Directory.CreateTempSubdirectory("lc-music-directory-");
        try
        {
            var external=root.CreateSubdirectory("source").FullName;
            var db=Path.Combine(root.FullName,"test.db");
            var studio=new StudioStore(db);studio.Migrate();
            var settings=new LiveSettings{StorageRoot=Path.Combine(root.FullName,"managed")};
            MusicGenerationService Open()=>new(settings,new AudioLibraryService(studio,settings.StorageRoot),studio);
            var music=Open();
            Assert.Throws<ArgumentException>(()=>music.ImportModelDirectory(external));
            foreach(var file in new[]{"preprocessor_config.json","tokenizer_config.json","tokenizer.json","model.safetensors"})File.WriteAllText(Path.Combine(external,file),"fixture");
            File.WriteAllText(Path.Combine(external,"config.json"),"{\"model_type\":\"t5\"}");
            Assert.Throws<ArgumentException>(()=>music.ImportModelDirectory(external));
            File.WriteAllText(Path.Combine(external,"config.json"),"{\"model_type\":\"musicgen\"}");
            var before=Directory.GetFiles(external).Select(File.ReadAllText).ToArray();
            music.ImportModelDirectory(external);
            var reopened=Open();
            var model=Assert.Single(reopened.Status().Models,x=>x.Id=="local/musicgen-directory");
            Assert.True(model.Installed);Assert.False(model.CompatibilityVerified);
            Assert.Throws<InvalidOperationException>(()=>reopened.StartModelDownload(model.Id,true));
            Assert.Throws<ArgumentException>(()=>reopened.PreviewModelRemoval(model.Id));
            Assert.Throws<ArgumentException>(()=>reopened.RemoveModel(model.Id,"irrelevant",true));
            Assert.Equal(before,Directory.GetFiles(external).Select(File.ReadAllText).ToArray());
            Assert.NotEqual(model.Id,settings.MusicModelId);
            File.Delete(Path.Combine(external,"tokenizer.json"));
            Assert.False(reopened.IsModelInstalled(model.Id));
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();root.Delete(true);}
    }
}
