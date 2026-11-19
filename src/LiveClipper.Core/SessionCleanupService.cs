namespace LiveClipper.Core;

/// <summary>退出清理的结果统计，写入日志便于确认清理范围。</summary>
public sealed record CleanupReport(int BufferFiles,long BufferBytes,int Danmaku,int Rooms,int Sessions,int Segments,string? Warning=null)
{
    public double BufferMegabytes=>Math.Round(BufferBytes/1024d/1024d,2);
}

/// <summary>
/// 关闭软件后的自动清理（可在设置里关闭）。只动本机缓存，不碰用户素材：
/// 非待定场次的回放缓存目录与分片索引、弹幕记录、直播间快照、录制场次索引。
/// 仍有 Collecting/Materializing/Failed 高光的场次整场保留：前两者等下次启动恢复，
/// Failed 等用户修好环境后重试；已保存的高光 MP4、切片、转写结果与导出成片全部保留。
/// </summary>
public sealed class SessionCleanupService(Store store)
{
    /// <summary>New policy: only marked, owned highlight caches expire. All pre-existing recordings remain intact.</summary>
    public CleanupReport RunHighlightCachesOnly()
    {
        var pending=store.SessionIdsWithPendingHighlights().ToHashSet(StringComparer.Ordinal);
        var sessions=0;var segments=0;var messages=0;var files=0;long bytes=0;
        foreach(var group in store.AllSegments().GroupBy(s=>s.SessionId))
        {
            var owned=group.Select(s=>(Segment:s,Root:HighlightCachePaths.RootForSegment(s.SessionId,s.Path))).ToArray();
            if(owned.Any(x=>x.Root is null))continue;
            var roots=owned.Select(x=>x.Root!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var root in roots)
            {
                var before=owned.Where(x=>x.Root==root&&File.Exists(x.Segment.Path)).ToDictionary(x=>x.Segment.Path,x=>new FileInfo(x.Segment.Path).Length);
                segments+=store.CleanAsync(group.Key,double.MaxValue,0,CancellationToken.None,root,includeTail:true).GetAwaiter().GetResult();
                foreach(var file in before)if(!File.Exists(file.Key)){files++;bytes+=file.Value;}
            }
            if(pending.Contains(group.Key)||store.AllSegments().Any(s=>s.SessionId==group.Key))continue;
            var removed=store.DeleteRuntimeDataForSession(group.Key);sessions+=removed.Sessions;messages+=removed.Danmaku;
        }
        return new CleanupReport(files,bytes,messages,store.DeleteUnreferencedRooms(),sessions,segments);
    }

    /// <summary>清理非待定场次的运行时数据；返回各部分的删除数量。</summary>
    public CleanupReport Run(string? storageRoot)
    {
        var pending=store.SessionIdsWithPendingHighlights().ToHashSet(StringComparer.Ordinal);
        // Independent recording roots are durable recordings, not this root's disposable cache.
        // Keep their session/segment/danmaku records as well as the files, including older roots.
        if(!string.IsNullOrWhiteSpace(storageRoot))
        {
            var cacheRoot=Path.GetFullPath(Path.Combine(storageRoot,"buffer"))+Path.DirectorySeparatorChar;
            foreach(var segment in store.AllSegments())
                if(!Path.GetFullPath(segment.Path).StartsWith(cacheRoot,StringComparison.OrdinalIgnoreCase))pending.Add(segment.SessionId);
        }
        var (files,bytes,warning)=DeleteBufferExcept(storageRoot??"",pending);

        var danmaku=0;
        var sessions=0;
        var segments=0;
        foreach(var session in store.Sessions().Where(x=>!pending.Contains(x.Id)))
        {
            var removed=store.DeleteRuntimeDataForSession(session.Id);
            segments+=removed.Segments;
            danmaku+=removed.Danmaku;
            sessions+=removed.Sessions;
        }
        var rooms=store.DeleteUnreferencedRooms();
        return new CleanupReport(files,bytes,danmaku,rooms,sessions,segments,warning);
    }

    /// <summary>
    /// 删除 buffer 下除待定场次以外的内容；待定场次必须保留分片，否则重启后无法补出 partial。
    /// 只允许操作保存目录下的 buffer 子目录。
    /// </summary>
    private static (int Files,long Bytes,string? Warning) DeleteBufferExcept(string storageRoot,ISet<string> keepSessionIds)
    {
        if(string.IsNullOrWhiteSpace(storageRoot))return (0,0,"没有配置保存目录，已跳过缓存清理");
        string root,target;
        try
        {
            root=Path.GetFullPath(storageRoot);
            target=Path.GetFullPath(Path.Combine(root,"buffer"));
            // 只允许删除保存目录下的子目录，避免把用户自己的文件夹清掉。
            if(!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                return (0,0,"缓存目录不在保存目录内，已跳过");
        }
        catch(Exception ex){return (0,0,"缓存目录无效："+ex.Message);}
        if(!Directory.Exists(target))return (0,0,null);

        var files=0;long bytes=0;string? warning=null;
        foreach(var directory in Directory.EnumerateDirectories(target))
        {
            var name=Path.GetFileName(directory);
            if(keepSessionIds.Contains(name))continue;
            DeleteTree(directory,ref files,ref bytes,ref warning);
        }
        // buffer 根下如果还有散落文件（旧版本/异常退出），也一并清掉。
        foreach(var file in Directory.EnumerateFiles(target))
            DeleteFile(file,ref files,ref bytes,ref warning);
        try
        {
            if(!Directory.EnumerateFileSystemEntries(target).Any())Directory.Delete(target);
        }
        catch(DirectoryNotFoundException){ }
        catch(Exception ex){warning??=ex.Message;}
        return (files,bytes,warning);
    }

    private static void DeleteTree(string directory,ref int files,ref long bytes,ref string? warning)
    {
        foreach(var file in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories))
            DeleteFile(file,ref files,ref bytes,ref warning);
        try{Directory.Delete(directory,true);}
        catch(DirectoryNotFoundException){ }
        catch(Exception ex){warning??=ex.Message;}
    }

    private static void DeleteFile(string file,ref int files,ref long bytes,ref string? warning)
    {
        try
        {
            var length=new FileInfo(file).Length;
            File.SetAttributes(file,FileAttributes.Normal);
            File.Delete(file);
            files++;bytes+=length;
        }
        catch(Exception ex){warning??=ex.Message;}
    }
}
