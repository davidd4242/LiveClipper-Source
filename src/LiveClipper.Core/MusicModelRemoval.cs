using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveClipper.Core;

/// <summary>Read-only preview. Never carries file names or absolute paths. IdentityFingerprint is "" when nothing is removable.</summary>
public sealed record MusicModelRemovalPreview(string ModelId,bool Exists,bool Installed,long ActualBytes,int FileCount,bool CanRemove,bool NotInstalled,bool DownloadBusy,bool LocalModelBusy,string Reason,string IdentityFingerprint);
/// <summary>Status is "removed" or "notInstalled". "removed" is only returned after the directory is gone and IsModelInstalled is false.</summary>
public sealed record MusicModelRemovalResult(string ModelId,string Status,bool DirectoryExists,bool Installed,long RemovedBytes,int RemovedFiles);
/// <summary>Thrown when a confirmed delete did not finish cleanly. Status is "partial" (something remains) or "unknown" (post-state could not be read).</summary>
public sealed class MusicModelRemovalException(string status,string message,bool? directoryExists,bool? installed,Exception? inner=null):InvalidOperationException(message,inner)
{
    public string Status{get;}=status;
    public bool? DirectoryExists{get;}=directoryExists;
    public bool? Installed{get;}=installed;
}

/// <summary>
/// Dedicated uninstall of a catalog MusicGen directory (StorageRoot/runtime/models/music/&lt;safe id&gt;). Lives in a partial of the service because
/// the busy gate (_gate, _downloadStop, _generationTask) and ModelDirectory are private and the identity re-check plus delete must share one lock.
/// </summary>
public sealed partial class MusicGenerationService
{
    private const string ModelMarkerName=".liveclipper-model.json";

    private sealed record Scan(bool Ok,string Reason,long Bytes,int Files,string Fingerprint);

    public MusicModelRemovalPreview PreviewModelRemoval(string modelId)
    {
        var model=RemovableModel(modelId);
        lock(_gate)return Inspect(model);
    }

    public MusicModelRemovalResult RemoveModel(string modelId,string? expectedFingerprint,bool confirmed)
    {
        var model=RemovableModel(modelId);
        if(!confirmed)throw new ArgumentException("需要明确确认后才能卸载模型");
        if(string.IsNullOrWhiteSpace(expectedFingerprint))throw new ArgumentException("缺少卸载预览指纹，请先重新预览");
        lock(_gate)
        {
            var now=Inspect(model);
            if(now.DownloadBusy||now.LocalModelBusy)throw new InvalidOperationException(now.Reason);
            if(now.NotInstalled)return new(model.Id,"notInstalled",false,IsModelInstalled(model.Id),0,0);
            if(!now.CanRemove)throw new InvalidOperationException(now.Reason);
            if(!string.Equals(now.IdentityFingerprint,expectedFingerprint.Trim(),StringComparison.Ordinal))
                throw new InvalidOperationException("模型目录自预览后已改变，请重新预览确认");
            var directory=ModelDirectory(model.Id);
            Exception? failure=null;
            // Directory.Delete is not atomic: a failure can leave part of the tree behind; the state is re-read below, never assumed.
            try{Directory.Delete(directory,true);}catch(Exception ex){failure=ex;}
            bool exists;bool installed;
            try{exists=Directory.Exists(directory);installed=IsModelInstalled(model.Id);}
            catch(Exception ex){throw new MusicModelRemovalException("unknown","卸载后无法读取模型目录状态："+ex.Message,null,null,failure??ex);}
            if(failure is null&&!exists&&!installed)return new(model.Id,"removed",false,false,now.ActualBytes,now.FileCount);
            // Delete threw but the directory now reads as absent: Directory.Exists returns false on access errors too, so this is not confirmed removal.
            if(failure is not null&&!exists&&!installed)
                throw new MusicModelRemovalException("unknown","卸载过程出现异常，且无法确认目录已删除（目录读取为不存在，但可能是无法访问）："+failure.Message,false,false,failure);
            throw new MusicModelRemovalException("partial",failure is null?"卸载未完成：模型目录仍然存在":"卸载未完成："+failure.Message,exists,installed,failure);
        }
    }

    private static MusicModelInfo RemovableModel(string modelId)
    {
        var model=Catalog.FirstOrDefault(x=>string.Equals(x.Id,modelId,StringComparison.OrdinalIgnoreCase))??throw new ArgumentException("未知的音乐模型");
        if(model.BuiltIn||model.Engine!="musicgen")throw new ArgumentException("内置合成器没有可卸载的模型");
        return model;
    }

    // Caller holds _gate.
    private MusicModelRemovalPreview Inspect(MusicModelInfo model)
    {
        var downloadBusy=_downloadStop is not null;
        var localBusy=_generationLocal&&_generationTask is {IsCompleted:false};
        MusicModelRemovalPreview Refuse(bool exists,string reason,bool notInstalled=false,long bytes=0,int files=0)=>
            new(model.Id,exists,IsModelInstalled(model.Id),bytes,files,false,notInstalled,downloadBusy,localBusy,reason,"");
        if(downloadBusy)return Refuse(true,"模型正在下载或下载工作进程尚未退出，暂时不能卸载");
        if(localBusy)return Refuse(true,"本地音乐正在生成或工作进程尚未退出，暂时不能卸载");
        try
        {
            var root=Path.GetFullPath(Path.Combine(settings.StorageRoot,"runtime","models","music"));
            var directory=Path.GetFullPath(ModelDirectory(model.Id));
            if(!string.Equals(Path.GetDirectoryName(directory),root.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
                return Refuse(false,"模型目录不在应用管理的模型根目录内，已拒绝");
            // Every existing segment between StorageRoot and the model directory must be a real directory, not a link.
            var storage=Path.GetFullPath(settings.StorageRoot).TrimEnd(Path.DirectorySeparatorChar);
            var walk=directory;
            while(walk.Length>storage.Length&&walk.StartsWith(storage,StringComparison.OrdinalIgnoreCase))
            {
                if((File.Exists(walk)||Directory.Exists(walk))&&(File.GetAttributes(walk)&FileAttributes.ReparsePoint)!=0)
                    return Refuse(true,"模型目录或其上级是链接/重解析点，已拒绝（不会跟随）");
                walk=Path.GetDirectoryName(walk)??"";
            }
            // The loop stops above StorageRoot itself; a root that is a link/junction is refused too.
            if((File.Exists(storage)||Directory.Exists(storage))&&(File.GetAttributes(storage)&FileAttributes.ReparsePoint)!=0)
                return Refuse(true,"存储根目录是链接/重解析点，已拒绝（不会跟随）");
            if(!Directory.Exists(directory))return Refuse(false,"模型未安装，没有可卸载的内容",true);
            var marker=Path.Combine(directory,ModelMarkerName);
            if(!File.Exists(marker))return Refuse(true,"目录缺少应用安装清单，不是受管模型目录（可能是失败的下载残留），已拒绝");
            string markerText;
            try
            {
                var info=new FileInfo(marker);
                if(info.Length>65536)return Refuse(true,"安装清单异常过大，已拒绝");
                markerText=File.ReadAllText(marker,Encoding.UTF8);
                using var document=JsonDocument.Parse(markerText);
                var rootElement=document.RootElement;
                if(rootElement.ValueKind!=JsonValueKind.Object
                    ||!rootElement.TryGetProperty("model",out var owner)||owner.ValueKind!=JsonValueKind.String
                    ||!string.Equals(owner.GetString(),model.Id,StringComparison.OrdinalIgnoreCase))
                    return Refuse(true,"安装清单与模型 ID 不符，已拒绝");
                if(!rootElement.TryGetProperty("installed",out var flag)||flag.ValueKind!=JsonValueKind.True)
                    return Refuse(true,"安装清单未标记为已安装，已拒绝");
            }
            catch(JsonException){return Refuse(true,"安装清单无法解析，已拒绝");}
            var first=ScanDirectory(directory,model.Id,markerText);
            if(!first.Ok)return Refuse(true,first.Reason);
            // Re-read once: a directory that is still changing is not a stable identity.
            var second=ScanDirectory(directory,model.Id,markerText);
            if(!second.Ok)return Refuse(true,second.Reason);
            if(first.Fingerprint!=second.Fingerprint)return Refuse(true,"模型目录内容仍在变化，无法确认身份");
            return new(model.Id,true,IsModelInstalled(model.Id),first.Bytes,first.Files,true,false,false,false,"可以卸载",first.Fingerprint);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {return Refuse(true,"无法读取模型目录，状态未知："+ex.Message);}
    }

    private static Scan ScanDirectory(string directory,string modelId,string markerText)
    {
        var lines=new List<string>();long bytes=0;var files=0;
        var pending=new Stack<string>();pending.Push(directory);
        while(pending.Count>0)
        {
            var current=pending.Pop();
            foreach(var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes=File.GetAttributes(entry);
                var relative=Path.GetRelativePath(directory,entry);
                if((attributes&FileAttributes.ReparsePoint)!=0)return new(false,"模型目录内含链接/重解析点，已拒绝（不会跟随）",0,0,"");
                if((attributes&FileAttributes.Directory)!=0){lines.Add("d|"+relative);pending.Push(entry);continue;}
                var info=new FileInfo(entry);
                bytes+=info.Length;files++;
                lines.Add($"f|{relative}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }
        lines.Sort(StringComparer.Ordinal);
        var text=modelId+"\n"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markerText)))+"\n"+string.Join("\n",lines);
        return new(true,"",bytes,files,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
    }
}
