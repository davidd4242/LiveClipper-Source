using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace LiveClipper.Core;

public sealed record EditorSession(string SessionId, int CheckpointRevision);

/// <summary>带外写入的结果：Saved=false 表示内容没变、什么也没写；EndedSessionId 是被这次外部写入结束的会话。</summary>
public sealed record EditorExternalWrite(bool Saved, int Revision, string Label, string? EndedSessionId);

/// <summary>Commit 的结果：Ok=false 表示 CAS 冲突、什么也没写，Revision 是库里的当前 revision；BackupError 只是告警，保存已提交。</summary>
public readonly record struct EditorCommit(bool Ok, int Revision, string? BackupError = null);

public sealed class EditorRevisionConflictException(string code, string message, int revision, string? holder = null) : Exception(message)
{
    public string Code { get; } = code;
    public int Revision { get; } = revision;
    public string? Holder { get; } = holder;
}

/// <summary>
/// per-clip 临界区、revision 与最多 30 份 undo/redo 快照。
/// R51-Y2b：clip 一旦绑定到 StudioStore（Serialized(studio,…) / Current(studio,…) / Commit / SaveExternal），
/// revision 就以 editor_projects.revision 为准（重启后继续递增，不归零），所有保存都走 StudioStore.SaveProjectCas；
/// undo/redo/会话仍只在内存里（重启即丢）。未绑定的 clip 保留旧的纯内存 revision（只供纯单测使用）。
/// 每个 StudioStore 实例有一个随机 Epoch（生产中每进程一个），客户端用 X-Editor-Epoch 发现服务端重启。
/// </summary>
public static class EditorRevisionStore
{
    private sealed class Session(string id, int checkpointRevision, string checkpoint, DateTimeOffset now)
    {
        public readonly string Id = id;
        public readonly int CheckpointRevision = checkpointRevision;
        public readonly string Checkpoint = checkpoint;
        public DateTimeOffset LastActivity = now;
        public bool HasWrites;
    }

    private sealed class Entry(string id)
    {
        public readonly string Id = id;
        public readonly object Gate = new();
        public int Revision;
        public StudioStore? Studio;
        // Commit 成功后、TryAdvance 读取前的已提交 revision；只在同一临界区内有效。
        public int? CommittedRevision;
        public readonly List<string> Undo = [];
        public readonly List<string> Redo = [];
        public Session? ActiveSession;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<StudioStore, string> Epochs = new();
    private const int MaxSnapshots = 30;
    // 五分钟无成功写入即释放：容许正常编辑停顿，同时避免崩溃的客户端永久占住 clip。
    public const int SessionIdleTimeoutSeconds = 300;

    private static Entry For(string clipId) => Entries.GetOrAdd(clipId, id => new Entry(id));

    /// <summary>StudioStore 实例的 epoch：实例创建后第一次取值时生成的随机 GUID（N 格式），实例存活期间不变。</summary>
    public static string EpochFor(StudioStore studio)
    {
        ArgumentNullException.ThrowIfNull(studio);
        return Epochs.GetValue(studio, _ => Guid.NewGuid().ToString("N"));
    }

    /// <summary>回调必须同步完成；把读、改、校验、持久化、history 和 revision 全放在同一临界区。</summary>
    public static T Serialized<T>(string clipId, Func<T> action)
    {
        var entry = For(clipId);
        lock (entry.Gate) return action();
    }

    /// <summary>同 Serialized，并先把 clip 绑定到 studio（之后 revision 以库为准）。</summary>
    public static T Serialized<T>(StudioStore studio, string clipId, Func<T> action)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            Bind(entry, studio);
            return action();
        }
    }

    public static int Current(string clipId)
    {
        var entry = For(clipId);
        lock (entry.Gate) return Rev(entry);
    }

    public static int Current(StudioStore studio, string clipId)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            Bind(entry, studio);
            return Rev(entry);
        }
    }

    /// <summary>
    /// 未绑定：baseRevision 匹配（null 不检查）则内存 revision+1。
    /// 已绑定：revision 已由 Commit 在库里推进，这里只报告同一临界区内刚提交的 revision 并返回 true；
    /// 没有待报告的提交则返回 false（revision=库中当前值），绝不在内存里另行递增。
    /// </summary>
    public static bool TryAdvance(string clipId, int? baseRevision, out int revision)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            if (entry.Studio is not null)
            {
                if (entry.CommittedRevision is { } committed)
                {
                    entry.CommittedRevision = null;
                    revision = committed;
                    return true;
                }
                revision = Rev(entry);
                return false;
            }
            if (baseRevision is { } expected && expected != entry.Revision)
            {
                revision = entry.Revision;
                return false;
            }
            EnsureCanAdvance(entry.Revision);
            entry.Revision += 1;
            revision = entry.Revision;
            return true;
        }
    }

    /// <summary>
    /// 唯一的交互式保存入口：绑定 studio、盖 schemaVersion、StudioStore.SaveProjectCas(clipId, json, baseRevision)。
    /// 冲突时什么也不写，返回 Ok=false 与库中当前 revision。成功后记住已提交 revision 供随后的 TryAdvance 报告。
    /// BackupError 不为 null 时保存已提交，只告警。SQLite 异常原样上抛。
    /// </summary>
    public static EditorCommit Commit(StudioStore studio, string clipId, string json, int? baseRevision)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            Bind(entry, studio);
            entry.CommittedRevision = null;
            var expected = Rev(entry);
            if (baseRevision is { } requested && requested != expected)
                return new EditorCommit(false, expected);
            EnsureCanAdvance(expected);
            var stamped = EditorProjectCodec.StampSchemaVersion(json);
            var result = studio.SaveProjectCas(clipId, stamped, expected);
            if (!result.Ok) return new EditorCommit(false, ToInt(result.Revision));
            var revision = ToInt(result.Revision);
            entry.Revision = revision;
            entry.CommittedRevision = revision;
            if (result.BackupError is { } warning) System.Diagnostics.Trace.TraceWarning(warning);
            return new EditorCommit(true, revision, result.BackupError);
        }
    }

    public static EditorSession BeginSession(string clipId, string checkpoint, DateTimeOffset? now = null)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            var at = now ?? DateTimeOffset.UtcNow;
            ExpireSession(entry, at);
            if (entry.ActiveSession is { } active) throw Busy(entry, active);
            var session = new Session(Guid.NewGuid().ToString("N"), Rev(entry), checkpoint, at);
            entry.ActiveSession = session;
            return new EditorSession(session.Id, session.CheckpointRevision);
        }
    }

    /// <summary>活动会话拒绝缺少或不匹配的 ID；history 必须等会话结束，不能把中途快照混入分组。</summary>
    public static void EnsureSession(string clipId, string? sessionId, bool history = false, DateTimeOffset? now = null)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            ExpireSession(entry, now ?? DateTimeOffset.UtcNow);
            if (entry.ActiveSession is not { } active)
            {
                if (sessionId is not null) throw Mismatch(entry);
                return;
            }
            if (!string.Equals(active.Id, sessionId, StringComparison.Ordinal)) throw Mismatch(entry);
            if (history) throw Busy(entry, active);
        }
    }

    /// <summary>PUT 准入把过期会话视为不存在，但失败请求不得消费 idle 闭合。</summary>
    public static void EnsureSessionForPut(string clipId, string? sessionId, DateTimeOffset admittedAt)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            if (entry.ActiveSession is not { } active || admittedAt - active.LastActivity >= TimeSpan.FromSeconds(SessionIdleTimeoutSeconds))
            {
                if (sessionId is not null) throw Mismatch(entry);
                return;
            }
            if (!string.Equals(active.Id, sessionId, StringComparison.Ordinal)) throw Mismatch(entry);
        }
    }

    /// <summary>与 PUT 准入同持 Serialized 锁，仅在 Commit.Ok 后、RecordWrite 前按入场时刻闭合。</summary>
    public static void CompletePutSession(string clipId, DateTimeOffset admittedAt)
    {
        var entry = For(clipId);
        lock (entry.Gate) ExpireSession(entry, admittedAt);
    }

    public static EditorSession EndSession(string clipId, string sessionId, DateTimeOffset? now = null)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            ExpireSession(entry, now ?? DateTimeOffset.UtcNow);
            if (entry.ActiveSession is not { } active || !string.Equals(active.Id, sessionId, StringComparison.Ordinal))
                throw Mismatch(entry);
            FinishSession(entry, active);
            return new EditorSession(active.Id, active.CheckpointRevision);
        }
    }

    /// <summary>只在保存成功后记录写入；会话只记 checkpoint，任何新写入都使 redo 分支失效。</summary>
    public static void RecordWrite(string clipId, string? previous, DateTimeOffset? now = null)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            // 准入已在同一临界区检查；保存耗时不应把一次合法的会话写入拆出分组。
            if (entry.ActiveSession is { } active)
            {
                active.HasWrites = true;
                active.LastActivity = now ?? DateTimeOffset.UtcNow;
            }
            else if (previous is not null) AddSnapshot(entry.Undo, previous);
            entry.Redo.Clear();
        }
    }

    /// <summary>低层压栈不清 redo，供失败的 history 操作恢复原栈；新写入走 RecordWrite。</summary>
    public static void PushSnapshot(string clipId, string json)
    {
        var entry = For(clipId);
        lock (entry.Gate) AddSnapshot(entry.Undo, json);
    }

    public static string? PopSnapshot(string clipId)
    {
        var entry = For(clipId);
        lock (entry.Gate) return Pop(entry.Undo);
    }

    public static void PushRedo(string clipId, string json)
    {
        var entry = For(clipId);
        lock (entry.Gate) AddSnapshot(entry.Redo, json);
    }

    public static string? PopRedo(string clipId)
    {
        var entry = For(clipId);
        lock (entry.Gate) return Pop(entry.Redo);
    }

    public static int SnapshotDepth(string clipId)
    {
        var entry = For(clipId);
        lock (entry.Gate) return entry.Undo.Count;
    }

    public static int RedoDepth(string clipId)
    {
        var entry = For(clipId);
        lock (entry.Gate) return entry.Redo.Count;
    }

    private static void AddSnapshot(List<string> stack, string json)
    {
        if (stack.Count > 0 && stack[^1] == json) return;
        stack.Add(json);
        if (stack.Count > MaxSnapshots) stack.RemoveRange(0, stack.Count - MaxSnapshots);
    }

    private static string? Pop(List<string> stack)
    {
        if (stack.Count == 0) return null;
        var last = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return last;
    }

    /// <summary>
    /// 在 entry.Gate 内调用。同一实例：无操作。换了 StudioStore 实例（= 进程/宿主重启）：内存 history 与会话作废，
    /// revision 从库里接着走。
    /// </summary>
    private static void Bind(Entry entry, StudioStore studio)
    {
        ArgumentNullException.ThrowIfNull(studio);
        if (ReferenceEquals(entry.Studio, studio)) return;
        var boundRevision = ToInt(studio.GetProjectRevision(entry.Id));
        if (entry.Studio is not null)
        {
            entry.Undo.Clear();
            entry.Redo.Clear();
            entry.ActiveSession = null;
        }
        entry.Studio = studio;
        entry.CommittedRevision = null;
        entry.Revision = boundRevision;
    }

    /// <summary>在 entry.Gate 内调用：已绑定读库（能看到别的进程的提交），未绑定读内存。</summary>
    private static int Rev(Entry entry)
    {
        if (entry.Studio is { } studio) entry.Revision = ToInt(studio.GetProjectRevision(entry.Id));
        return entry.Revision;
    }

    private static int ToInt(long revision)
    {
        if (revision < 0 || revision > int.MaxValue) throw RevisionLimit(revision);
        return (int)revision;
    }

    private static void EnsureCanAdvance(long revision)
    {
        _ = ToInt(revision);
        if (revision == int.MaxValue) throw RevisionLimit(revision);
    }

    private static EditorRevisionConflictException RevisionLimit(long revision)
        => new("revision-limit", $"当前编辑器不能继续推进 revision {revision}，请升级编辑器后重试",
            (int)Math.Clamp(revision, 0L, (long)int.MaxValue));

    private static void ExpireSession(Entry entry, DateTimeOffset now)
    {
        if (entry.ActiveSession is { } active && now - active.LastActivity >= TimeSpan.FromSeconds(SessionIdleTimeoutSeconds))
            FinishSession(entry, active);
    }

    private static void FinishSession(Entry entry, Session active)
    {
        if (active.HasWrites) AddSnapshot(entry.Undo, active.Checkpoint);
        entry.ActiveSession = null;
    }

    private static EditorRevisionConflictException Busy(Entry entry, Session active)
        => new("session-busy", "该工程已有编辑会话，请结束当前会话或等待空闲超时", Rev(entry), active.Id[..8]);

    private static EditorRevisionConflictException Mismatch(Entry entry)
        => new("session-mismatch", "sessionId 不匹配或已过期，请重新开始会话", Rev(entry));

    /// <summary>
    /// 带外写者（AutoEdit、配乐 ducking）的写入口：与 PUT/commands 取同一把 clip 锁，在锁内读最新工程交给 update 算出新内容。
    /// 读到的工程 schemaVersion 过新 → EditorProjectSchemaTooNewException（fail closed，update 不会被调用）。
    /// update 返回 null 或与当前内容相同 = 不写（revision、history、会话都不动）；保存抛异常原样上抛，状态不变。
    /// 保存走 SaveProjectCas(expected = 读到的库 revision)：别的进程抢先提交 → EditorRevisionConflictException("stale-revision")，什么也没写。
    /// 保存成功后：活动会话按「外部写入结束会话」处理（有写入的 checkpoint 先入 undo、会话关闭，旧 sessionId 之后得到 session-mismatch），
    /// 再把写入前的工程压入 undo、清空 redo；revision = 库里的新 revision。不校验客户端 baseRevision：持旧 revision 的客户端得到 stale-revision。
    /// 回调必须同步完成。
    /// </summary>
    public static EditorExternalWrite SaveExternal(StudioStore studio, string clipId, string label, Func<string?, string?> update)
    {
        var entry = For(clipId);
        lock (entry.Gate)
        {
            Bind(entry, studio);
            var record = studio.GetProjectRecord(clipId);
            var previous = record?.Json;
            var expected = record?.Revision ?? 0;
            var current = ToInt(expected);
            if (previous is not null) EditorProjectCodec.EnsureSupported(previous);
            var next = update(previous);
            if (next is null || string.Equals(next, previous, StringComparison.Ordinal))
                return new EditorExternalWrite(false, current, label, null);

            EnsureCanAdvance(expected);
            var result = studio.SaveProjectCas(clipId, EditorProjectCodec.StampSchemaVersion(next), expected);
            if (!result.Ok)
                throw new EditorRevisionConflictException("stale-revision", $"工程已被其他写入者修改（当前 revision {result.Revision}）", ToInt(result.Revision));
            var newRevision = ToInt(result.Revision);
            if (result.BackupError is { } warning) System.Diagnostics.Trace.TraceWarning(warning);
            string? endedSessionId = null;
            if (entry.ActiveSession is { } active)
            {
                endedSessionId = active.Id;
                FinishSession(entry, active);
            }
            if (previous is not null) AddSnapshot(entry.Undo, previous);
            entry.Redo.Clear();
            entry.CommittedRevision = null;
            entry.Revision = newRevision;
            return new EditorExternalWrite(true, entry.Revision, label, endedSessionId);
        }
    }

    /// <summary>仅测试清理用；调用方必须确保没有在途操作。</summary>
    public static void Forget(string clipId) => Entries.TryRemove(clipId, out _);
}
