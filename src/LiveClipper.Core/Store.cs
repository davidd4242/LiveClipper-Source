using LiveClipper.Domain;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Core;

public sealed class Store(string databasePath) : ISegmentBuffer
{
    public string DatabasePath { get; } = databasePath;
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);
        var db = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        db.Open();
        using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return db;
    }

    public void Migrate()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=1";
        if ((long)command.ExecuteScalar()! == 0)
        {
        using var tx = db.BeginTransaction();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE rooms(id INTEGER PRIMARY KEY, canonical_id INTEGER NOT NULL, title TEXT NOT NULL, streamer TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE sessions(id TEXT PRIMARY KEY, room_id INTEGER NOT NULL, started_at TEXT NOT NULL, ended_at TEXT, state TEXT NOT NULL);
            CREATE TABLE media_segments(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, sequence INTEGER NOT NULL, start REAL NOT NULL, end REAL NOT NULL, path TEXT NOT NULL, locked INTEGER NOT NULL DEFAULT 0, UNIQUE(session_id,sequence));
            CREATE INDEX ix_segments_time ON media_segments(session_id,start,end);
            CREATE TABLE danmaku_events(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, session_time REAL NOT NULL, timestamp TEXT NOT NULL, type TEXT NOT NULL, uid INTEGER NOT NULL, user_name TEXT NOT NULL, text TEXT NOT NULL, weight REAL NOT NULL);
            CREATE INDEX ix_danmaku_time ON danmaku_events(session_id,session_time);
            CREATE TABLE highlight_events(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, source TEXT NOT NULL, trigger_time REAL NOT NULL, start REAL NOT NULL, end REAL NOT NULL, state TEXT NOT NULL, score REAL NOT NULL);
            CREATE TABLE highlight_triggers(id TEXT PRIMARY KEY, event_id TEXT NOT NULL, source TEXT NOT NULL, trigger_time REAL NOT NULL, score REAL NOT NULL);
            CREATE TABLE clips(id TEXT PRIMARY KEY, event_id TEXT NOT NULL UNIQUE, path TEXT NOT NULL, srt_path TEXT, created_at TEXT NOT NULL);
            CREATE TABLE transcripts(id TEXT PRIMARY KEY, event_id TEXT NOT NULL UNIQUE, json TEXT NOT NULL, srt_path TEXT NOT NULL);
            CREATE TABLE clip_plans(id TEXT PRIMARY KEY, event_id TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE jobs(id TEXT PRIMARY KEY, type TEXT NOT NULL, event_id TEXT NOT NULL, status TEXT NOT NULL, progress REAL NOT NULL DEFAULT 0, error TEXT, created_at TEXT NOT NULL, started_at TEXT, finished_at TEXT, retry_count INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE model_configs(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO schema_migrations(version) VALUES(1);
            """;
        command.ExecuteNonQuery();
        tx.Commit();
        command.Transaction=null;
        }
        command.CommandText="SELECT COUNT(*) FROM schema_migrations WHERE version=2";
        if((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText="""
                CREATE TABLE IF NOT EXISTS video_exports(id TEXT PRIMARY KEY, clip_id TEXT NOT NULL, event_id TEXT NOT NULL, output_path TEXT NOT NULL, bitrate INTEGER NOT NULL, resolution INTEGER NOT NULL, fps INTEGER NOT NULL, created_at TEXT NOT NULL);
                INSERT INTO schema_migrations(version) VALUES(2);
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText="SELECT COUNT(*) FROM schema_migrations WHERE version=3";
        if((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText="""
                ALTER TABLE transcripts ADD COLUMN updated_at TEXT NOT NULL DEFAULT '';
                INSERT INTO schema_migrations(version) VALUES(3);
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText="SELECT COUNT(*) FROM schema_migrations WHERE version=4";
        if((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText="""
                ALTER TABLE jobs ADD COLUMN message TEXT;
                INSERT INTO schema_migrations(version) VALUES(4);
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText="SELECT COUNT(*) FROM schema_migrations WHERE version=5";
        if((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText="""
                ALTER TABLE highlight_events ADD COLUMN closed INTEGER NOT NULL DEFAULT 0;
                INSERT INTO schema_migrations(version) VALUES(5);
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText="SELECT COUNT(*) FROM schema_migrations WHERE version=6";
        if((long)command.ExecuteScalar()! == 0)
        {
            var hasPartial=false;
            command.CommandText="PRAGMA table_info(highlight_events)";
            using(var columns=command.ExecuteReader())
                while(columns.Read()) if(columns.GetString(1)=="partial") hasPartial=true;
            if(!hasPartial)
            {
                command.CommandText="ALTER TABLE highlight_events ADD COLUMN partial INTEGER NOT NULL DEFAULT 0;";
                command.ExecuteNonQuery();
            }
            command.CommandText="INSERT OR IGNORE INTO schema_migrations(version) VALUES(6);";
            command.ExecuteNonQuery();
        }
        command.CommandText="SELECT COUNT(*) FROM schema_migrations WHERE version=7";
        if((long)command.ExecuteScalar()! == 0)
        {
            var hasRequestedEnd=false;
            command.CommandText="PRAGMA table_info(highlight_events)";
            using(var columns=command.ExecuteReader())
                while(columns.Read()) if(columns.GetString(1)=="requested_end") hasRequestedEnd=true;
            if(!hasRequestedEnd)
            {
                command.CommandText="ALTER TABLE highlight_events ADD COLUMN requested_end REAL NOT NULL DEFAULT 0;";
                command.ExecuteNonQuery();
            }
            command.CommandText="INSERT OR IGNORE INTO schema_migrations(version) VALUES(7);";
            command.ExecuteNonQuery();
        }
        RecoverJobs(db);
    }
    private static void RecoverJobs(SqliteConnection db)
    {
        using var recovery=db.CreateCommand();
        recovery.CommandText="UPDATE jobs SET status='pending', retry_count=retry_count+1 WHERE status='running'; UPDATE highlight_events SET state='Collecting' WHERE state='Materializing'; UPDATE sessions SET state='Ended', ended_at=COALESCE(ended_at,$now) WHERE state NOT IN ('Offline','Ended');";
        recovery.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        recovery.ExecuteNonQuery();
    }

    private static SqliteCommand Cmd(SqliteConnection db, string sql, params (string key, object? value)[] values)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (key,value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    public void SaveRoom(RoomInfo room)
    {
        using var db = Open(); using var cmd = Cmd(db, "INSERT INTO rooms VALUES($id,$cid,$title,$streamer,$at) ON CONFLICT(id) DO UPDATE SET canonical_id=$cid,title=$title,streamer=$streamer,updated_at=$at", ("$id",room.RoomId),("$cid",room.CanonicalId),("$title",room.Title),("$streamer",room.Streamer),("$at",DateTimeOffset.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();
    }
    public void SaveSession(RecordingSession s)
    {
        using var db=Open(); using var cmd=Cmd(db,"INSERT INTO sessions VALUES($id,$room,$start,$end,$state) ON CONFLICT(id) DO UPDATE SET ended_at=$end,state=$state",("$id",s.Id),("$room",s.RoomId),("$start",s.StartedAt.ToString("O")),("$end",s.EndedAt?.ToString("O")),("$state",s.State.ToString())); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<RecordingSession> Sessions()
    {
        using var db=Open(); using var cmd=Cmd(db,"SELECT id,room_id,started_at,ended_at,state FROM sessions ORDER BY started_at DESC"); using var r=cmd.ExecuteReader(); var list=new List<RecordingSession>();
        while(r.Read()) list.Add(new(r.GetString(0),r.GetInt64(1),DateTimeOffset.Parse(r.GetString(2)),r.IsDBNull(3)?null:DateTimeOffset.Parse(r.GetString(3)),Enum.Parse<RecordingState>(r.GetString(4)))); return list;
    }
    public Task AddAsync(MediaSegment s,CancellationToken token=default)
    {
        using var db=Open(); using var cmd=Cmd(db,"INSERT OR IGNORE INTO media_segments VALUES($id,$session,$seq,$start,$end,$path,$locked)",("$id",s.Id),("$session",s.SessionId),("$seq",s.Sequence),("$start",s.Start),("$end",s.End),("$path",s.Path),("$locked",s.Locked?1:0)); cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    /// <summary>本场录制当前保留下来的回放缓存时长；界面按选中的直播间分别显示。</summary>
    public double BufferSeconds(string sessionId)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT COALESCE(MIN(start),0),COALESCE(MAX(end),0) FROM media_segments WHERE session_id=$id",("$id",sessionId));
        using var r=cmd.ExecuteReader();return r.Read()?Math.Max(0,r.GetDouble(1)-r.GetDouble(0)):0;
    }
    /// <summary>全部录制分片（退出清理时按会话删除缓存文件）。</summary>
    public IReadOnlyList<MediaSegment> AllSegments()
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,session_id,sequence,start,end,path,locked FROM media_segments ORDER BY session_id,sequence");using var r=cmd.ExecuteReader();var list=new List<MediaSegment>();
        while(r.Read()) list.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetDouble(3),r.GetDouble(4),r.GetString(5),r.GetInt32(6)!=0));return list;
    }
    public int ClearAllSegments()
    {
        using var db=Open();using var cmd=Cmd(db,"DELETE FROM media_segments");return cmd.ExecuteNonQuery();
    }
    /// <summary>清空直播间快照（标题、主播名、规范房号）。</summary>
    public int ClearRooms()
    {
        using var db=Open();using var cmd=Cmd(db,"DELETE FROM rooms");return cmd.ExecuteNonQuery();
    }
    /// <summary>清空录制场次索引（退出清理用；不动已保存的切片与高光素材）。</summary>
    public int ClearSessions()
    {
        using var db=Open();using var cmd=Cmd(db,"DELETE FROM sessions");return cmd.ExecuteNonQuery();
    }
    public MediaSegment? LastSegment(string sessionId)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,session_id,sequence,start,end,path,locked FROM media_segments WHERE session_id=$id ORDER BY sequence DESC LIMIT 1",("$id",sessionId));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetDouble(3),r.GetDouble(4),r.GetString(5),r.GetInt32(6)!=0):null;
    }
    public Task<IReadOnlyList<MediaSegment>> GetRangeAsync(string sessionId,double start,double end,CancellationToken token=default)
    {
        using var db=Open(); using var cmd=Cmd(db,"SELECT id,session_id,sequence,start,end,path,locked FROM media_segments WHERE session_id=$id AND end>$start AND start<$end ORDER BY sequence",("$id",sessionId),("$start",start),("$end",end)); using var r=cmd.ExecuteReader(); var list=new List<MediaSegment>();
        while(r.Read()) list.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetDouble(3),r.GetDouble(4),r.GetString(5),r.GetInt32(6)!=0)); return Task.FromResult<IReadOnlyList<MediaSegment>>(list);
    }
    /// <summary>会话最近的若干个分片，按录制顺序返回；直播监控用它拼近实时预览的播放列表。</summary>
    public IReadOnlyList<MediaSegment> LatestSegments(string sessionId,int count)
    {
        using var db=Open(); using var cmd=Cmd(db,"SELECT id,session_id,sequence,start,end,path,locked FROM media_segments WHERE session_id=$id ORDER BY sequence DESC LIMIT $n",("$id",sessionId),("$n",count)); using var r=cmd.ExecuteReader(); var list=new List<MediaSegment>();
        while(r.Read()) list.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetDouble(3),r.GetDouble(4),r.GetString(5),r.GetInt32(6)!=0));
        list.Reverse(); return list;
    }
    /// <summary>按房号取最近一次保存的房间信息（主播名等）；高光命名用它显示主播名。</summary>
    public RoomInfo? Room(long roomId)
    {
        using var db=Open(); using var cmd=Cmd(db,"SELECT canonical_id,title,streamer FROM rooms WHERE id=$id",("$id",roomId)); using var r=cmd.ExecuteReader();
        return r.Read()?new RoomInfo(roomId,r.GetInt64(0),false,r.GetString(1),r.GetString(2)):null;
    }
    public Task LockRangeAsync(string sessionId,double start,double end,CancellationToken token=default)
    {
        using var db=Open(); using var cmd=Cmd(db,"UPDATE media_segments SET locked=1 WHERE session_id=$id AND end>$start AND start<$end",("$id",sessionId),("$start",start),("$end",end)); cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public void ReleaseFinishedPins(string sessionId,double start,double end)
    {
        using var db=Open();using var cmd=Cmd(db,"UPDATE media_segments SET locked=0 WHERE session_id=$session AND end>$start AND start<$end AND NOT EXISTS(SELECT 1 FROM highlight_events e WHERE e.session_id=$session AND e.state IN ('Collecting','Materializing','Failed') AND e.start<media_segments.end AND e.end>media_segments.start)",("$session",sessionId),("$start",start),("$end",end));cmd.ExecuteNonQuery();
    }
    // Historical/full recordings are never eligible for automatic rolling-cache deletion.
    public Task<int> CleanAsync(string sessionId,double latestTime,double bufferSeconds,CancellationToken token=default)
        => Task.FromResult(0);

    public Task<int> CleanAsync(string sessionId,double latestTime,double bufferSeconds,CancellationToken token,string? ownedCacheRoot,bool includeTail=false)
    {
        if(string.IsNullOrWhiteSpace(ownedCacheRoot)||!HighlightCachePaths.HasSession(ownedCacheRoot,sessionId))return Task.FromResult(0);
        using var db=Open(); using var tx=db.BeginTransaction();
        using var cmd=Cmd(db,"SELECT id,path FROM media_segments WHERE session_id=$id AND end<$cutoff AND locked=0 AND ($tail=1 OR sequence<(SELECT MAX(sequence) FROM media_segments WHERE session_id=$id))",("$id",sessionId),("$cutoff",latestTime-bufferSeconds),("$tail",includeTail?1:0));
        cmd.Transaction=tx; using var r=cmd.ExecuteReader(); var expired=new List<(string,string)>(); while(r.Read()) expired.Add((r.GetString(0),r.GetString(1))); r.Close();
        var removed=0;
        foreach(var (id,path) in expired)
        {
            token.ThrowIfCancellationRequested();
            if(!HighlightCachePaths.Owns(ownedCacheRoot,sessionId,path))continue;
            // The transaction serializes pin changes; retain the row if file deletion fails.
            try { if(File.Exists(path)) File.Delete(path); } catch(IOException) { continue; } catch(UnauthorizedAccessException) { continue; }
            using var del=Cmd(db,"DELETE FROM media_segments WHERE id=$id AND locked=0",("$id",id)); del.Transaction=tx; removed+=del.ExecuteNonQuery();
        }
        tx.Commit(); return Task.FromResult(removed);
    }
    public void SaveDanmaku(DanmakuEvent e)
    {
        using var db=Open(); using var cmd=Cmd(db,"INSERT INTO danmaku_events VALUES($id,$session,$time,$at,$type,$uid,$name,$text,$weight)",("$id",e.Id),("$session",e.SessionId),("$time",e.SessionTime),("$at",e.Timestamp.ToString("O")),("$type",e.Type),("$uid",e.Uid),("$name",e.UserName),("$text",e.Text),("$weight",e.Weight));cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<DanmakuEvent> RecentDanmaku(string sessionId,int limit=1000)
    {
        using var db=Open(); using var cmd=Cmd(db,"SELECT id,session_id,session_time,timestamp,type,uid,user_name,text,weight FROM danmaku_events WHERE session_id=$id ORDER BY session_time DESC LIMIT $limit",("$id",sessionId),("$limit",limit));using var r=cmd.ExecuteReader();var list=new List<DanmakuEvent>();while(r.Read()) list.Add(new(r.GetString(0),r.GetString(1),r.GetDouble(2),DateTimeOffset.Parse(r.GetString(3)),r.GetString(4),r.GetInt64(5),r.GetString(6),r.GetString(7),r.GetDouble(8)));return list;
    }
    public bool DeleteDanmaku(string sessionId,string eventId)
    {
        using var db=Open();using var cmd=Cmd(db,"DELETE FROM danmaku_events WHERE session_id=$session AND id=$id",("$session",sessionId),("$id",eventId));return cmd.ExecuteNonQuery()>0;
    }
    public int ClearDanmaku(string sessionId)
    {
        using var db=Open();using var cmd=Cmd(db,"DELETE FROM danmaku_events WHERE session_id=$session",("$session",sessionId));return cmd.ExecuteNonQuery();
    }
    public int ClearAllDanmaku()
    {
        using var db=Open();using var cmd=Cmd(db,"DELETE FROM danmaku_events");return cmd.ExecuteNonQuery();
    }
    /// <summary>
    /// 热点词统计取本场从第一条弹幕开始的全部弹幕（按时间正序），不再使用滚动时间窗口。
    /// limit 仅作为超长直播的安全上限，保留最早出现的记录以保证"从第一次出现开始统计"。
    /// </summary>
    public IReadOnlyList<DanmakuEvent> DanmakuForHotWords(string sessionId,int limit=200000)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,session_id,session_time,timestamp,type,uid,user_name,text,weight FROM danmaku_events WHERE session_id=$id AND type='danmaku' ORDER BY session_time LIMIT $limit",("$id",sessionId),("$limit",limit));using var r=cmd.ExecuteReader();var list=new List<DanmakuEvent>();while(r.Read())list.Add(new(r.GetString(0),r.GetString(1),r.GetDouble(2),DateTimeOffset.Parse(r.GetString(3)),r.GetString(4),r.GetInt64(5),r.GetString(6),r.GetString(7),r.GetDouble(8)));return list;
    }
    /// <summary>补全某个 uid 之前被 B 站打码的用户名（例如 "最***" → "最强导播"）。</summary>
    public int UpdateUserName(long uid,string userName)
    {
        if(uid<=0||string.IsNullOrWhiteSpace(userName))return 0;
        using var db=Open();using var cmd=Cmd(db,"UPDATE danmaku_events SET user_name=$name WHERE uid=$uid AND user_name<>$name",("$uid",uid),("$name",userName));return cmd.ExecuteNonQuery();
    }
    /// <summary>找出仍然被打码的用户名对应的 uid，用于补全历史弹幕。</summary>
    public IReadOnlyList<long> MaskedUserIds(int limit=200)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT uid FROM danmaku_events WHERE uid>0 AND user_name LIKE '%*%' GROUP BY uid ORDER BY MAX(timestamp) DESC LIMIT $limit",("$limit",limit));
        using var r=cmd.ExecuteReader();var list=new List<long>();while(r.Read())list.Add(r.GetInt64(0));return list;
    }
    public IReadOnlyList<DanmakuEvent> DanmakuForHighlight(string sessionId,double start,double end,double triggerTime,int limit)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,session_id,session_time,timestamp,type,uid,user_name,text,weight FROM danmaku_events WHERE session_id=$id AND session_time >= $start AND session_time <= $end ORDER BY ABS(session_time-$trigger),session_time LIMIT $limit",("$id",sessionId),("$start",start),("$end",end),("$trigger",triggerTime),("$limit",limit));using var r=cmd.ExecuteReader();var list=new List<DanmakuEvent>();while(r.Read())list.Add(new(r.GetString(0),r.GetString(1),r.GetDouble(2),DateTimeOffset.Parse(r.GetString(3)),r.GetString(4),r.GetInt64(5),r.GetString(6),r.GetString(7),r.GetDouble(8)));return list.OrderBy(x=>x.SessionTime).ToArray();
    }
    public void SaveHighlight(HighlightEvent e)
    {
        using var db=Open();using var cmd=Cmd(db,"INSERT INTO highlight_events(id,session_id,source,trigger_time,start,end,state,score,closed,partial,requested_end) VALUES($id,$session,$source,$trigger,$start,$end,$state,$score,$closed,$partial,$requestedEnd) ON CONFLICT(id) DO UPDATE SET end=$end,state=$state,score=$score,closed=$closed,partial=$partial,requested_end=$requestedEnd",("$id",e.Id),("$session",e.SessionId),("$source",e.Source),("$trigger",e.TriggerTime),("$start",e.Start),("$end",e.End),("$state",e.State.ToString()),("$score",e.Score),("$closed",e.Closed?1:0),("$partial",e.Partial?1:0),("$requestedEnd",e.RequestedEnd));cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<HighlightEvent> Highlights(string? sessionId=null)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,session_id,source,trigger_time,start,end,state,score,closed,partial,requested_end FROM highlight_events WHERE $id IS NULL OR session_id=$id ORDER BY trigger_time DESC",("$id",sessionId));using var r=cmd.ExecuteReader();var list=new List<HighlightEvent>();while(r.Read())list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetDouble(3),r.GetDouble(4),r.GetDouble(5),Enum.Parse<HighlightState>(r.GetString(6)),r.GetDouble(7)){Closed=r.GetInt32(8)!=0,Partial=r.GetInt32(9)!=0,RequestedEnd=r.GetDouble(10)});return list;
    }
    /// <summary>
    /// 仍有需要保留源分片的高光场次：Collecting/Materializing 等恢复，
    /// Failed 则等用户修好环境后重试，不能在退出清理时把素材删掉。
    /// </summary>
    public IReadOnlyList<string> SessionIdsWithPendingHighlights()
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT DISTINCT session_id FROM highlight_events WHERE state IN ('Collecting','Materializing','Failed') ORDER BY session_id");
        using var r=cmd.ExecuteReader();var list=new List<string>();while(r.Read())list.Add(r.GetString(0));return list;
    }
    /// <summary>只清理一场录制的运行时数据（分片索引/弹幕/场次），不动高光事件、切片与已导出文件。</summary>
    public (int Segments,int Danmaku,int Sessions) DeleteRuntimeDataForSession(string sessionId)
    {
        using var db=Open();using var tx=db.BeginTransaction();
        int segments,danmaku,sessions;
        using(var cmd=Cmd(db,"DELETE FROM media_segments WHERE session_id=$s",("$s",sessionId))){cmd.Transaction=tx;segments=cmd.ExecuteNonQuery();}
        using(var cmd=Cmd(db,"DELETE FROM danmaku_events WHERE session_id=$s",("$s",sessionId))){cmd.Transaction=tx;danmaku=cmd.ExecuteNonQuery();}
        using(var cmd=Cmd(db,"DELETE FROM sessions WHERE id=$s",("$s",sessionId))){cmd.Transaction=tx;sessions=cmd.ExecuteNonQuery();}
        tx.Commit();
        return (segments,danmaku,sessions);
    }
    /// <summary>删除已经没有任何场次引用的直播间快照。</summary>
    public int DeleteUnreferencedRooms()
    {
        using var db=Open();using var cmd=Cmd(db,"DELETE FROM rooms WHERE id NOT IN (SELECT DISTINCT room_id FROM sessions)");return cmd.ExecuteNonQuery();
    }
    public void SaveClip(Clip clip)
    {
        using var db=Open();using var cmd=Cmd(db,"INSERT INTO clips VALUES($id,$event,$path,$srt,$at) ON CONFLICT(event_id) DO UPDATE SET path=$path,srt_path=$srt",("$id",clip.Id),("$event",clip.EventId),("$path",clip.Path),("$srt",clip.SrtPath),("$at",clip.CreatedAt.ToString("O")));cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<Clip> Clips()
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,event_id,path,srt_path,created_at FROM clips ORDER BY created_at DESC");using var r=cmd.ExecuteReader();var list=new List<Clip>();while(r.Read())list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),DateTimeOffset.Parse(r.GetString(4))));return list;
    }
    /// <summary>删除一条切片的记录与衍生数据；仅当素材文件夹位于本软件 clips 目录内才连文件一起删。</summary>
    public void DeleteClip(Clip clip,string? storageRoot)
    {
        using var gate=MediaRegistryGate.EnterWrite();
        using var db=Open();using var tx=db.BeginTransaction();
        foreach(var sql in new[]{
            "DELETE FROM video_exports WHERE clip_id=$id",
            "DELETE FROM transcripts WHERE event_id=$ev",
            "DELETE FROM clip_plans WHERE event_id=$ev",
            "DELETE FROM jobs WHERE event_id=$ev",
            "DELETE FROM highlight_triggers WHERE event_id=$ev",
            "DELETE FROM highlight_events WHERE id=$ev",
            "DELETE FROM clips WHERE id=$id"})
        {using var cmd=Cmd(db,sql,("$id",clip.Id),("$ev",clip.EventId));cmd.Transaction=tx;cmd.ExecuteNonQuery();}
        tx.Commit();
        TryDeleteInside(Path.GetDirectoryName(clip.Path),storageRoot,"clips");
        if(!string.IsNullOrEmpty(clip.SrtPath))TryDeleteInside(clip.SrtPath,storageRoot,null);
    }
    /// <summary>删除一场录制会话：分片与弹幕、全部高光事件及其切片、相关任务，以及本机 buffer 缓存目录。</summary>
    public void DeleteSession(RecordingSession session,string? storageRoot)
    {
        using var gate=MediaRegistryGate.EnterWrite();
        var clips=Clips().Where(c=>Highlights(session.Id).Any(h=>h.Id==c.EventId)).ToList();
        using var db=Open();using var tx=db.BeginTransaction();
        foreach(var sql in new[]{
            "DELETE FROM video_exports WHERE event_id IN (SELECT id FROM highlight_events WHERE session_id=$s)",
            "DELETE FROM transcripts WHERE event_id IN (SELECT id FROM highlight_events WHERE session_id=$s)",
            "DELETE FROM clip_plans WHERE event_id IN (SELECT id FROM highlight_events WHERE session_id=$s)",
            "DELETE FROM jobs WHERE event_id IN (SELECT id FROM highlight_events WHERE session_id=$s)",
            "DELETE FROM clips WHERE event_id IN (SELECT id FROM highlight_events WHERE session_id=$s)",
            "DELETE FROM highlight_triggers WHERE event_id IN (SELECT id FROM highlight_events WHERE session_id=$s)",
            "DELETE FROM highlight_events WHERE session_id=$s",
            "DELETE FROM danmaku_events WHERE session_id=$s",
            "DELETE FROM media_segments WHERE session_id=$s",
            "DELETE FROM sessions WHERE id=$s"})
        {using var cmd=Cmd(db,sql,("$s",session.Id));cmd.Transaction=tx;cmd.ExecuteNonQuery();}
        tx.Commit();
        foreach(var clip in clips)
        {
            TryDeleteInside(Path.GetDirectoryName(clip.Path),storageRoot,"clips");
            if(!string.IsNullOrEmpty(clip.SrtPath))TryDeleteInside(clip.SrtPath,storageRoot,null);
        }
        TryDeleteInside(Path.Combine(storageRoot??"","buffer",session.Id),storageRoot,"buffer");
    }
    /// <summary>只删除位于软件数据目录（可再限定子目录）里的文件或文件夹；用户自己的导出与原始素材不会被碰。</summary>
    static void TryDeleteInside(string? path,string? storageRoot,string? child)
    {
        if(string.IsNullOrEmpty(path)||string.IsNullOrEmpty(storageRoot))return;
        string root=Path.GetFullPath(storageRoot);
        if(!string.IsNullOrEmpty(child))root=Path.Combine(root,child);
        try
        {
            var full=Path.GetFullPath(path);
            var limit=root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!full.StartsWith(limit,StringComparison.OrdinalIgnoreCase))return;
            if(Directory.Exists(full))Directory.Delete(full,true);
            else if(File.Exists(full)){File.SetAttributes(full,FileAttributes.Normal);File.Delete(full);}
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public void SaveVideoExport(VideoExport export)
    {
        using var db=Open();using var cmd=Cmd(db,"INSERT INTO video_exports VALUES($id,$clip,$event,$path,$bitrate,$resolution,$fps,$at)",( "$id",export.Id),("$clip",export.ClipId),("$event",export.EventId),("$path",export.OutputPath),("$bitrate",export.BitrateKbps),("$resolution",export.Resolution),("$fps",export.Fps),("$at",export.CreatedAt.ToString("O")));cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<VideoExport> VideoExports()
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,clip_id,event_id,output_path,bitrate,resolution,fps,created_at FROM video_exports ORDER BY created_at DESC");using var r=cmd.ExecuteReader();var list=new List<VideoExport>();while(r.Read())list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),DateTimeOffset.Parse(r.GetString(7))));return list;
    }
    // Source-SRT import is create-only. The immediate SQLite write transaction serializes
    // competing imports and existing job writes; it does not block a later user-requested ASR.
    public string? InsertSourceTranscriptIfAbsent(Clip clip,string json,string srtPath)
    {
        using var db=Open();using var tx=db.BeginTransaction();
        using(var check=Cmd(db,"SELECT COUNT(*) FROM clips WHERE id=$clip AND event_id=$event",("$clip",clip.Id),("$event",clip.EventId)))
        {check.Transaction=tx;if((long)check.ExecuteScalar()! != 1)return "clip_identity_changed";}
        using(var check=Cmd(db,"SELECT EXISTS(SELECT 1 FROM transcripts WHERE event_id=$event) OR EXISTS(SELECT 1 FROM clips WHERE event_id=$event AND COALESCE(srt_path,'')<>'')",("$event",clip.EventId)))
        {check.Transaction=tx;if((long)check.ExecuteScalar()! != 0)return "source_transcript_exists";}
        using(var check=Cmd(db,"SELECT COUNT(*) FROM jobs WHERE event_id=$event AND type='transcribe' AND status IN ('pending','running','retried')",("$event",clip.EventId)))
        {check.Transaction=tx;if((long)check.ExecuteScalar()! != 0)return "transcribing";}
        using(var insert=Cmd(db,"INSERT INTO transcripts(id,event_id,json,srt_path,updated_at) VALUES($id,$event,$json,$srt,$at); UPDATE clips SET srt_path=$srt WHERE id=$clip AND event_id=$event",("$id",Guid.NewGuid().ToString("N")),("$event",clip.EventId),("$json",json),("$srt",srtPath),("$at",DateTimeOffset.UtcNow.ToString("O")),("$clip",clip.Id)))
        {insert.Transaction=tx;insert.ExecuteNonQuery();}
        tx.Commit();return null;
    }
    public void SaveTranscript(string eventId,string json,string srtPath)
    {
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=Cmd(db,"INSERT INTO transcripts(id,event_id,json,srt_path,updated_at) VALUES($id,$event,$json,$srt,$at) ON CONFLICT(event_id) DO UPDATE SET json=$json,srt_path=$srt,updated_at=$at; UPDATE clips SET srt_path=$srt WHERE event_id=$event",("$id",Guid.NewGuid().ToString("N")),("$event",eventId),("$json",json),("$srt",srtPath),("$at",DateTimeOffset.UtcNow.ToString("O")));cmd.Transaction=tx;cmd.ExecuteNonQuery();tx.Commit();
    }
    public string? TranscriptJson(string eventId)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT json FROM transcripts WHERE event_id=$event",("$event",eventId));return cmd.ExecuteScalar() as string;
    }
    /// <summary>转录版本号：写入时间 + 内容长度，前端据此判断是否需要自动同步编辑器。</summary>
    public string TranscriptVersion(string eventId)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT updated_at,LENGTH(json) FROM transcripts WHERE event_id=$event",("$event",eventId));using var r=cmd.ExecuteReader();return r.Read()?$"{(r.IsDBNull(0)?"":r.GetString(0))}:{(r.IsDBNull(1)?0:r.GetInt64(1))}":"";
    }
    public void SaveJob(Job j)
    {
        // started_at/finished_at 由状态自动维护：running 记录开始时间，结束态记录完成时间；重试沿用首次开始时间。
        using var db=Open();using var cmd=Cmd(db,"INSERT INTO jobs(id,type,event_id,status,progress,error,created_at,started_at,finished_at,retry_count,message) VALUES($id,$type,$event,$status,$progress,$error,$at,CASE WHEN $status='running' THEN $at ELSE NULL END,CASE WHEN $status IN ('completed','failed','interrupted') THEN $at ELSE NULL END,$retry,$message) ON CONFLICT(id) DO UPDATE SET status=$status,progress=$progress,error=$error,message=$message,retry_count=$retry,started_at=CASE WHEN $status='running' THEN COALESCE(started_at,$at) ELSE started_at END,finished_at=CASE WHEN $status IN ('completed','failed','interrupted') THEN $at ELSE finished_at END",("$id",j.Id),("$type",j.Type),("$event",j.EventId),("$status",j.Status),("$progress",j.Progress),("$error",j.Error),("$at",DateTimeOffset.UtcNow.ToString("O")),("$retry",j.RetryCount),("$message",j.Message));cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<Job> Jobs()
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,type,event_id,status,progress,error,started_at,finished_at,retry_count,message FROM jobs ORDER BY created_at DESC");using var r=cmd.ExecuteReader();var list=new List<Job>();while(r.Read())list.Add(ReadJob(r));return list;
    }
    public IReadOnlyList<Job> PendingJobs(string type)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,type,event_id,status,progress,error,started_at,finished_at,retry_count,message FROM jobs WHERE type=$type AND status='pending' ORDER BY created_at",("$type",type));using var r=cmd.ExecuteReader();var list=new List<Job>();while(r.Read())list.Add(ReadJob(r));return list;
    }
    public Job? PendingJob(string type,string eventId)
    {
        using var db=Open();using var cmd=Cmd(db,"SELECT id,type,event_id,status,progress,error,started_at,finished_at,retry_count,message FROM jobs WHERE type=$type AND event_id=$event AND status='pending' ORDER BY created_at LIMIT 1",("$type",type),("$event",eventId));using var r=cmd.ExecuteReader();return r.Read()?ReadJob(r):null;
    }
    private static Job ReadJob(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetDouble(4),r.IsDBNull(5)?null:r.GetString(5),
        r.IsDBNull(6)?null:DateTimeOffset.Parse(r.GetString(6)),r.IsDBNull(7)?null:DateTimeOffset.Parse(r.GetString(7)),r.GetInt32(8),r.IsDBNull(9)?null:r.GetString(9));
}
