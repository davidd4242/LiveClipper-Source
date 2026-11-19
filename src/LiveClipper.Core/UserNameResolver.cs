using System.Collections.Concurrent;
using LiveClipper.Domain;

namespace LiveClipper.Core;

/// <summary>
/// 补全被 B 站打码的弹幕用户名（例如 "最***"）。
/// 匿名弹幕连接拿不到完整用户名，这里按 uid 异步查询一次并缓存，
/// 同时回写数据库里已存过的记录，让历史弹幕也显示真实昵称。
/// </summary>
public sealed class BilibiliUserNameResolver(Store store,Action? changed=null)
{
    private readonly ConcurrentDictionary<long,string> _names=new();
    private readonly ConcurrentDictionary<long,byte> _pending=new();
    private readonly SemaphoreSlim _gate=new(2,2);
    /// <summary>为 null 时不发起网络查询，只用缓存与数据库里已有的昵称。</summary>
    public Func<long,CancellationToken,Task<string>>? Lookup {get;set;}
    /// <summary>最多缓存多少个用户名，避免长时间直播占用过多内存。</summary>
    public int CacheLimit {get;set;}=20000;
    public int Resolved=>_names.Count;

    /// <summary>用缓存里的完整用户名替换打码名；缓存缺失时触发一次后台查询。</summary>
    public DanmakuEvent Decorate(DanmakuEvent ev)
    {
        if(ev.Uid<=0)return ev;
        if(_names.TryGetValue(ev.Uid,out var cached)&&cached.Length>0)
            return cached==ev.UserName?ev:ev with {UserName=cached};
        if(!BilibiliDanmakuClient.IsMasked(ev.UserName))
        {
            if(ev.UserName.Length>0&&_names.Count<CacheLimit)_names[ev.Uid]=ev.UserName;
            return ev;
        }
        Resolve(ev.Uid);
        return ev;
    }

    /// <summary>后台查询一个 uid 的昵称；同一个 uid 只查一次。</summary>
    public void Resolve(long uid)
    {
        if(uid<=0||Lookup is null||_names.ContainsKey(uid)||!_pending.TryAdd(uid,0))return;
        _=Task.Run(async()=>
        {
            try
            {
                await _gate.WaitAsync();
                try
                {
                    var name=await Lookup(uid,CancellationToken.None);
                    if(string.IsNullOrWhiteSpace(name)||BilibiliDanmakuClient.IsMasked(name))return;
                    if(_names.Count<CacheLimit)_names[uid]=name;
                    if(store.UpdateUserName(uid,name)>0)changed?.Invoke();
                }
                finally{_gate.Release();}
            }
            catch{/* 查询失败就保留打码名，下次开播再试 */}
            finally{_pending.TryRemove(uid,out _);}
        });
    }

    /// <summary>为数据库里仍然打码的历史弹幕批量补全用户名，返回发起查询的数量。</summary>
    public int BackfillHistory(int max=200)
    {
        var uids=store.MaskedUserIds(max);
        foreach(var uid in uids)Resolve(uid);
        return uids.Count;
    }
}
