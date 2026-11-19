using LiveClipper.Core;
using LiveClipper.Domain;

namespace LiveClipper.Tests;

/// <summary>
/// P0：回放缓存 A。正常关窗只收完整事件；崩溃恢复按最后分片收尾并标 partial；
/// 退出清理必须保留仍有待定高光的场次，否则重启后无片可补。
/// </summary>
public sealed class ReplayRetentionTests
{
    private static (Store Store,string Root) NewStore()
    {
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-replay-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new Store(Path.Combine(root,"test.db"));store.Migrate();
        return (store,root);
    }

    private static async Task AddSegmentAsync(Store store,string sessionId,int sequence,double start,double end,string root)
    {
        var path=Path.Combine(root,$"{sessionId}-{sequence}.ts");
        await File.WriteAllTextAsync(path,"media");
        await store.AddAsync(new MediaSegment(Guid.NewGuid().ToString("N"),sessionId,sequence,start,end,path,false));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(100)]
    public async Task AdjacentDanmakuHighlightContinuesPendingOverlapButNeverTouchesReadyFile(double triggerAt)
    {
        var (store,root)=NewStore();
        try
        {
            var session=new RecordingSession("merge",1,DateTimeOffset.UtcNow,null,RecordingState.Recording);store.SaveSession(session);
            var service=new HighlightService(store,new MediaTools("missing-ffmpeg"),root,preRoll:20,postRoll:20);
            store.SaveHighlight(new HighlightEvent("pending","merge","danmaku",50,30,80,HighlightState.Collecting,1){Closed=true});
            store.SaveHighlight(new HighlightEvent("ready","merge","danmaku",40,20,100,HighlightState.Ready,1){Closed=true});
            var continued=await service.MarkAtAsync(session,"danmaku",triggerAt);
            Assert.Equal("pending",continued.Id);Assert.False(continued.Closed);Assert.Equal(triggerAt+20,continued.End);
            Assert.Equal(100,store.Highlights("merge").Single(x=>x.Id=="ready").End);
            var distinct=await service.MarkAtAsync(session,"danmaku",300);
            Assert.NotEqual("pending",distinct.Id);Assert.Equal(3,store.Highlights("merge").Count);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Default200SecondPendingOverlapCountsOnlyOneAutomaticHighlight()
    {
        var (store,root)=NewStore();
        try
        {
            var session=new RecordingSession("merge200",1,DateTimeOffset.UtcNow,null,RecordingState.Recording);store.SaveSession(session);
            var service=new HighlightService(store,new MediaTools("missing-ffmpeg"),root,preRoll:200,postRoll:200);
            var heat=new DanmakuHeat();
            var first=await service.MarkAtAsync(session,"danmaku",700);heat.RegisterHighlight(first.Id,first.Source,700);
            await service.CloseDanmakuAsync(session,first.Id,710);
            var next=await service.MarkAtAsync(session,"danmaku",900);heat.RegisterHighlight(next.Id,next.Source,900);
            Assert.Equal(first.Id,next.Id);Assert.Equal(1,heat.RecentTriggerCount);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task LegacyPartial_RetryKeepsPartialAndUnknownRequestedEnd()
    {
        var (store,root)=NewStore();
        try
        {
            // 迁移 6 时代的行：Partial=true，但没有 requested_end；End 已经被截短。
            store.SaveHighlight(new HighlightEvent("e","s","manual",2,0,2,HighlightState.Failed,1){Partial=true});
            await AddSegmentAsync(store,"s",0,0,2,root);
            var service=new HighlightService(store,new MediaTools("ffmpeg"),root);

            await Assert.ThrowsAnyAsync<Exception>(()=>service.RetryAsync("e"));

            var saved=Assert.Single(store.Highlights("s"));
            Assert.True(saved.Partial);
            Assert.Equal(0,saved.RequestedEnd);
            Assert.Equal(HighlightState.Failed,saved.State);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Recovery_NoUsableSegments_KeepsExistingPartialFlag()
    {
        var (store,root)=NewStore();
        try
        {
            store.SaveHighlight(new HighlightEvent("e","s","manual",4,0,2,HighlightState.Collecting,1){Partial=true,RequestedEnd=4});
            var service=new HighlightService(store,new MediaTools("ffmpeg"),root);

            await service.FinalizePendingAsync("s",allowPartial:true);

            var saved=Assert.Single(store.Highlights("s"));
            Assert.Equal(HighlightState.Failed,saved.State);
            Assert.True(saved.Partial);
            Assert.Equal(4,saved.RequestedEnd);
            Assert.True(saved.Closed);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void Store_RoundTripsPartialFlag()
    {
        var (store,root)=NewStore();
        try
        {
            store.SaveHighlight(new HighlightEvent("e","s","manual",4,0,4,HighlightState.Collecting,1){Partial=true,RequestedEnd=4});
            var saved=Assert.Single(store.Highlights("s"));
            Assert.True(saved.Partial);
            Assert.Equal(4,saved.RequestedEnd);

            store.SaveHighlight(saved with {Partial=false});
            Assert.False(Assert.Single(store.Highlights("s")).Partial);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task NormalShutdown_LeavesIncompleteManualEventCollecting()
    {
        var (store,root)=NewStore();
        try
        {
            var session=new RecordingSession("s",6,DateTimeOffset.UtcNow,null,RecordingState.Recording);
            store.SaveSession(session);
            store.SaveHighlight(new HighlightEvent("e","s","manual",4,0,4,HighlightState.Collecting,1));
            await AddSegmentAsync(store,"s",0,0,1,root);
            await AddSegmentAsync(store,"s",1,1,2,root);

            var service=new HighlightService(store,new MediaTools("ffmpeg"),root);
            await service.FinalizePendingAsync("s",allowPartial:false);

            var saved=Assert.Single(store.Highlights("s"));
            Assert.Equal(HighlightState.Collecting,saved.State);
            Assert.False(saved.Closed);
            Assert.False(saved.Partial);
            Assert.Equal(4,saved.End);
            Assert.Empty(store.Clips());
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Recovery_TruncatesToLastSegment_AndWritesPartialMetadata()
    {
        var (store,root)=NewStore();
        try
        {
            var session=new RecordingSession("s",6,DateTimeOffset.UtcNow,null,RecordingState.Recording);
            store.SaveSession(session);
            store.SaveHighlight(new HighlightEvent("e","s","manual",4,0,4,HighlightState.Collecting,1));
            await AddSegmentAsync(store,"s",0,0,1,root);
            await AddSegmentAsync(store,"s",1,1,2,root);

            var service=new HighlightService(store,new MediaTools("ffmpeg"),root);
            // 假分片无法真正 concat；metadata 在拼接前已落盘，这里只验证“截断 + 标 partial”这两个事实。
            try{await service.FinalizePendingAsync("s",allowPartial:true);}catch{}

            var saved=Assert.Single(store.Highlights("s"));
            Assert.Equal(2,saved.End);
            Assert.Equal(4,saved.RequestedEnd);
            Assert.True(saved.Closed);
            Assert.True(saved.Partial);

            var metadata=Directory.GetFiles(Path.Combine(root,"clips"),"metadata.json",SearchOption.AllDirectories);
            var text=Assert.Single(metadata);
            var json=await File.ReadAllTextAsync(text);
            Assert.Contains("\"partial\":true",json);
            Assert.Contains("\"requestedEnd\":4",json);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Cleanup_KeepsPendingSession_AndDeletesFinishedSession()
    {
        var (store,root)=NewStore();
        try
        {
            var at=DateTimeOffset.UtcNow;
            store.SaveRoom(new RoomInfo(6,6,true,"标题","主播"));
            store.SaveSession(new RecordingSession("pending",6,at,null,RecordingState.Recording));
            store.SaveSession(new RecordingSession("finished",6,at,null,RecordingState.Ended));


            var pendingDir=HighlightCachePaths.Create(root,"pending");
            File.WriteAllText(Path.Combine(pendingDir,"segment_0.ts"),"pending");
            var finishedDir=HighlightCachePaths.Create(root,"finished");
            File.WriteAllText(Path.Combine(finishedDir,"segment_0.ts"),"finished");

            store.SaveDanmaku(new DanmakuEvent("d1","pending",1,at,"danmaku",1,"u","pending",1));
            store.SaveDanmaku(new DanmakuEvent("d2","finished",1,at,"danmaku",1,"u","finished",1));
            store.SaveHighlight(new HighlightEvent("e-pending","pending","manual",4,0,4,HighlightState.Collecting,1));
            store.SaveHighlight(new HighlightEvent("e-finished","finished","manual",4,0,4,HighlightState.Ready,1));
            store.SaveClip(new Clip("c-finished","e-finished",Path.Combine(root,"clips","finished.mp4"),null,at));

            await store.AddAsync(new MediaSegment("pending-seg","pending",0,0,1,Path.Combine(pendingDir,"segment_0.ts"),true));
            await store.AddAsync(new MediaSegment("finished-seg","finished",0,0,1,Path.Combine(finishedDir,"segment_0.ts"),false));
            var report=new SessionCleanupService(store).RunHighlightCachesOnly();

            Assert.True(File.Exists(Path.Combine(pendingDir,"segment_0.ts")));
            Assert.False(File.Exists(Path.Combine(finishedDir,"segment_0.ts")));
            Assert.Equal(1,report.Sessions);
            Assert.Equal(1,report.Segments);
            Assert.Equal(1,report.Danmaku);
            Assert.Equal(1,report.BufferFiles);
            Assert.Single(store.Sessions());
            Assert.Single(store.AllSegments());
            Assert.Single(store.RecentDanmaku("pending"));
            Assert.Empty(store.RecentDanmaku("finished"));
            Assert.NotNull(store.Room(6));
            Assert.Single(store.Clips());
            Assert.Equal(2,store.Highlights().Count);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task SecondRecovery_KeepsPartialAndOriginalRequestedEnd()
    {
        var (store,root)=NewStore();
        try
        {
            var session=new RecordingSession("s",6,DateTimeOffset.UtcNow,null,RecordingState.Recording);
            store.SaveSession(session);
            store.SaveHighlight(new HighlightEvent("e","s","manual",4,0,4,HighlightState.Collecting,1));
            await AddSegmentAsync(store,"s",0,0,1,root);
            await AddSegmentAsync(store,"s",1,1,2,root);
            var service=new HighlightService(store,new MediaTools("ffmpeg"),root);
            try{await service.FinalizePendingAsync("s",allowPartial:true);}catch{}

            var first=Assert.Single(store.Highlights("s"));
            Assert.Equal(2,first.End);
            Assert.Equal(4,first.RequestedEnd);
            Assert.True(first.Partial);

            // 模拟“已经截短并标 partial，但物化中崩溃”：迁移只把 Materializing 改回 Collecting，
            // 不能把 End 当成用户原始终点，否则第二次恢复会误判为完整。
            store.SaveHighlight(first with {State=HighlightState.Materializing});
            var reopened=new Store(Path.Combine(root,"test.db"));reopened.Migrate();
            var retry=new HighlightService(reopened,new MediaTools("ffmpeg"),root);
            try{await retry.FinalizePendingAsync("s",allowPartial:true);}catch{}

            var second=Assert.Single(reopened.Highlights("s"));
            Assert.Equal(2,second.End);
            Assert.Equal(4,second.RequestedEnd);
            Assert.True(second.Partial);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task RuntimeCleanup_KeepsSegmentsSharedWithFailedHighlight()
    {
        var (store,root)=NewStore();
        try
        {
            var at=DateTimeOffset.UtcNow;
            store.SaveSession(new RecordingSession("s",6,at,null,RecordingState.Ended));
            await AddSegmentAsync(store,"s",0,0,2,root);

            // A 失败、B 成功，但两者覆盖同一段素材。B 收尾时不能把 A 还要重试的
            // 分片解锁，否则运行期 CleanAsync 会把文件删掉。
            store.SaveHighlight(new HighlightEvent("failed","s","manual",4,0,4,HighlightState.Failed,1){RequestedEnd=4});
            store.SaveHighlight(new HighlightEvent("ready","s","manual",1,0,1,HighlightState.Ready,1){Closed=true});
            await store.LockRangeAsync("s",0,2);

            store.ReleaseFinishedPins("s",0,2);
            Assert.True(Assert.Single(store.AllSegments()).Locked);

            await store.CleanAsync("s",100,0);
            Assert.Single(store.AllSegments());
            Assert.True(File.Exists(Path.Combine(root,"s-0.ts")));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Cleanup_KeepsRetryableFailedSession()
    {
        var (store,root)=NewStore();
        try
        {
            var at=DateTimeOffset.UtcNow;
            store.SaveSession(new RecordingSession("failed",6,at,null,RecordingState.Ended));
            await AddSegmentAsync(store,"failed",0,0,1,root);
            var buffer=Path.Combine(root,"buffer","failed");Directory.CreateDirectory(buffer);
            File.WriteAllText(Path.Combine(buffer,"segment_0.ts"),"failed");
            store.SaveHighlight(new HighlightEvent("e","failed","manual",4,0,4,HighlightState.Failed,1){RequestedEnd=4});

            new SessionCleanupService(store).Run(root);

            Assert.Single(store.Sessions(),x=>x.Id=="failed");
            Assert.Single(store.AllSegments());
            Assert.True(File.Exists(Path.Combine(buffer,"segment_0.ts")));
        }
        finally{Directory.Delete(root,true);}
    }
}
