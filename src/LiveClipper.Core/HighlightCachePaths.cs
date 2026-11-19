namespace LiveClipper.Core;

/// <summary>Ownership of newly-created disposable highlight buffers, never old recordings.</summary>
public static class HighlightCachePaths
{
    private const string Marker=".liveclipper-highlight-cache";
    private static string Identity(string sessionId)=>"LiveClipper highlight cache v1\n"+sessionId;
    private static string SessionPath(string root,string sessionId)
    {
        if(string.IsNullOrWhiteSpace(sessionId)||Path.GetFileName(sessionId)!=sessionId||sessionId is "." or "..")throw new ArgumentException("Invalid cache session identity.");
        return Path.Combine(RenderStoragePaths.NormalizeDirectory(root),"buffer",sessionId);
    }
    private static bool NoLinks(string path)
    {
        for(var current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)return false;
        return true;
    }
    public static string Create(string root,string sessionId)
    {
        var directory=SessionPath(root,sessionId);
        if(!NoLinks(directory))throw new IOException("Highlight cache path contains a link.");
        if(Directory.Exists(directory))
        {
            if(!HasSession(root,sessionId))throw new IOException("Existing directory is not an owned highlight cache.");
            return directory;
        }
        Directory.CreateDirectory(directory);
        using var file=new FileStream(Path.Combine(directory,Marker),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        using var writer=new StreamWriter(file);writer.Write(Identity(sessionId));
        return directory;
    }
    public static bool HasSession(string root,string sessionId)
    {
        var directory=SessionPath(root,sessionId);var marker=Path.Combine(directory,Marker);
        return NoLinks(marker)&&File.Exists(marker)&&File.ReadAllText(marker)==Identity(sessionId);
    }
    public static bool Owns(string root,string sessionId,string path)
    {
        var full=Path.GetFullPath(path);var directory=SessionPath(root,sessionId);
        return full.StartsWith(directory+Path.DirectorySeparatorChar,RenderStoragePaths.PathComparison)&&HasSession(root,sessionId)&&NoLinks(full);
    }
    public static string? RootForSegment(string sessionId,string path)
    {
        for(var current=Path.GetDirectoryName(Path.GetFullPath(path));current is not null;current=Path.GetDirectoryName(current))
        {
            if(Path.GetFileName(current)!=sessionId)continue;
            var buffer=Path.GetDirectoryName(current);var root=buffer is null?null:Path.GetDirectoryName(buffer);
            if(root is not null&&Path.GetFileName(buffer)=="buffer"&&Owns(root,sessionId,path))return root;
        }
        return null;
    }
}
