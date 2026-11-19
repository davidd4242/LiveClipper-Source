namespace LiveClipper.Core;

/// <summary>Machine-local output paths; empty settings retain the existing per-project defaults.</summary>
public static class RenderStoragePaths
{
    public static string NormalizeDirectory(string? configured)
    {
        if(string.IsNullOrWhiteSpace(configured))return "";
        var path=configured.Trim();
        if(!Path.IsPathFullyQualified(path)||path.Split([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar]).Any(part=>part is "." or ".."))
            throw new ArgumentException("请选择完整绝对目录，不可包含 . 或 .. 路径段");
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(string.Equals(full,Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!),PathComparison)||File.Exists(full))
            throw new ArgumentException("请选择专用文件夹，不能使用磁盘根目录或文件");
        return full;
    }
    public static bool IsValidDirectory(string? path)
    {try{NormalizeDirectory(path);return true;}catch(Exception ex) when(ex is ArgumentException or NotSupportedException or IOException){return false;}}
    internal static StringComparison PathComparison=>OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
    public static string WorkRoot(string storageRoot,string? configured)=>NormalizeDirectory(configured) is {Length:>0} custom?custom:Path.GetFullPath(Path.Combine(storageRoot,"render-work"));
    public static string OutputRoot(string storageRoot,string? projectOutput,string? configured)=>NormalizeDirectory(configured) is {Length:>0} custom?custom:projectOutput??Path.Combine(storageRoot,"edited-clips");
}

/// <summary>A scheduled attempt owns only its fixed job directory, never the configured parent or other jobs.</summary>
internal sealed class RenderWorkScope
{
    public string Root {get;}
    public string Work {get;}
    private readonly string jobId;
    private readonly string output;
    private bool ownsDirectory;
    private string Marker=>Path.Combine(Work,".liveclipper-render-owner");
    private RenderWorkScope(string root,string work,string id,string target){Root=root;Work=work;jobId=id;output=target;}
    public static RenderWorkScope Capture(string storageRoot,string? configured,string jobId,string output)
    {
        var root=RenderStoragePaths.WorkRoot(storageRoot,configured);
        if(string.IsNullOrEmpty(jobId)||Path.GetFileName(jobId)!=jobId||jobId is "." or ".."||jobId.IndexOfAny(Path.GetInvalidFileNameChars())>=0)
            throw new ArgumentException("渲染任务目录名称无效");
        var work=Path.GetFullPath(Path.Combine(root,jobId));
        if(!string.Equals(Path.GetDirectoryName(work),root,RenderStoragePaths.PathComparison))throw new ArgumentException("工作目录必须是任务专属直属目录");
        if(!Path.IsPathFullyQualified(output)||Path.GetFullPath(output)!=output||Directory.Exists(output)||
            string.Equals(output,work,RenderStoragePaths.PathComparison)||output.StartsWith(work+Path.DirectorySeparatorChar,RenderStoragePaths.PathComparison))
            throw new ArgumentException("成片必须写入独立的绝对文件路径，不能位于本任务临时目录内");
        return new(root,work,jobId,output);
    }
    public void Create()
    {
        NoLinks(Root);
        if(Directory.Exists(Work))
        {
            NoLinks(Work);
            if(!File.Exists(Marker)||File.ReadAllText(Marker)!=jobId)throw new IOException("任务目录已有未归属文件，未覆盖或清理，请使用另一临时目录");
            ownsDirectory=true;return;
        }
        Directory.CreateDirectory(Work);NoLinks(Work);
        using var marker=new FileStream(Marker,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new StreamWriter(marker);writer.Write(jobId);writer.Flush();ownsDirectory=true;
    }
    public void DeleteIncompleteOutput()
    {
        // Called only after this attempt entered RenderAsync; queued cancellation cannot delete an existing output.
        NoLinks(Path.GetDirectoryName(output)!);
        if(File.Exists(output))
        {
            if((File.GetAttributes(output)&FileAttributes.ReparsePoint)!=0)throw new IOException("成片路径是链接，未删除");
            File.Delete(output);
        }
    }
    public void Cleanup()
    {
        if(!ownsDirectory)return;
        NoLinks(Root);
        if(!Directory.Exists(Work))return;
        NoLinks(Work);
        if(!File.Exists(Marker)||File.ReadAllText(Marker)!=jobId)throw new IOException("工作目录归属无法确认，未删除");
        var pending=new Stack<string>();pending.Push(Work);
        while(pending.TryPop(out var directory))
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes=File.GetAttributes(entry);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("任务目录含链接，未递归删除");
                if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);
            }
        Directory.Delete(Work,true);
    }
    private static void NoLinks(string path)
    {
        for(var current=new DirectoryInfo(path);current is not null;current=current.Parent)
            if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("渲染目录不能经过符号链接或目录联接");
    }
}
