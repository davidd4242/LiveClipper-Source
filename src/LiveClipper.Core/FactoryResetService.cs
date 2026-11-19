using System.Text.Json;

namespace LiveClipper.Core;

public sealed record FactoryResetSummary(int Sessions,int Clips,int Jobs,int Projects,int Assets,string[] StorageRoots);

// Never delete the storage root itself: it may also hold the user's original media.
// publishSettings: the App host passes a delegate that writes through the shared settings gate (StartupDurability).
// Core cannot reference App, so the gate is injected; null keeps the direct DurableFile publish used by tests.
public sealed class FactoryResetService(string databasePath,string configDirectory,string? defaultStorageRoot=null,Action<LiveSettings>? publishSettings=null)
{
    private static readonly string[] ManagedDirectories=["buffer","clips","transcripts","imports","imported-media","edited-transcripts","edited-clips","render-work","stems","studio-exports","audio-library","mix-previews","stickers","cover-frames","covers"];
    public static LiveSettings Defaults(string directory)=>new(){StorageRoot=directory,LocalOutputRoot=Path.Combine(directory,"exports")};
    private string[] Roots=>new[]{Path.GetDirectoryName(Path.GetFullPath(databasePath))!,Path.GetFullPath(defaultStorageRoot??configDirectory)}.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public FactoryResetSummary Summary(Store store,StudioStore studio)=>new(store.Sessions().Count,store.Clips().Count,store.Jobs().Count,store.Clips().Sum(x=>studio.Variants(x.Id).Count+(studio.Project(x.Id) is null?0:1)),studio.Assets().Count(x=>!x.BuiltIn),Roots);
    public void ValidateTargets()
    {
        foreach(var root in Roots)
        {
            CheckParents(root);
            foreach(var name in ManagedDirectories)
            {
                var path=Owned(root,name);if(!Directory.Exists(path))continue;
                CheckTree(path);
            }
        }
    }
    private static void CheckTree(string path)
    {
        foreach(var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("数据目录含链接，无法安全重设："+entry);
            if((attributes&FileAttributes.Directory)!=0)CheckTree(entry);
        }
    }
    public void Execute(IEnumerable<string>? originalFiles=null)
    {
        ValidateTargets();
        var originals=(originalFiles??[]).Select(Path.GetFullPath).Where(path=>!Roots.Any(root=>Within(Path.Combine(root,"imported-media"),path))).ToArray();
        foreach(var root in Roots)
        {
            foreach(var name in ManagedDirectories){var target=Owned(root,name);if(Directory.Exists(target)&&!originals.Any(path=>Within(target,path)))Directory.Delete(target,true);}
            foreach(var name in new[]{"liveclipper.db","liveclipper.db-wal","liveclipper.db-shm"}){var target=Owned(root,name);if(File.Exists(target))File.Delete(target);}
        }
        Directory.CreateDirectory(configDirectory);
        // Unique sibling temp + flush to disk + rename (DurableFile), so a crash never leaves a truncated settings.json.
        var defaults=Defaults(configDirectory);
        if(publishSettings is not null){publishSettings(defaults);return;}
        DurableFile.WriteAllText(Path.Combine(configDirectory,"settings.json"),JsonSerializer.Serialize(defaults,new JsonSerializerOptions{WriteIndented=true}));
    }
    private static bool Within(string directory,string path)=>path.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static string Owned(string root,string name)
    {
        var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var target=Path.GetFullPath(Path.Combine(prefix,name));
        if(!target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("重设路径超出了软件数据目录");
        CheckParents(target);return target;
    }
    private static void CheckParents(string path)
    {
        for(var current=path;current is not null;current=Path.GetDirectoryName(current))
            if((Directory.Exists(current)||File.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("重设路径含链接："+current);
    }
}
