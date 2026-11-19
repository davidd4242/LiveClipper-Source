using LiveClipper.Core;
using LiveClipper.Domain;
using System.Buffers.Binary;
using System.Text;

namespace LiveClipper.Tests;

public sealed class RoomHeatConcurrencyTests
{
    private sealed class Fixture:IDisposable
    {
        readonly string dir=Path.Combine(Path.GetTempPath(),"liveclipper-heat-"+Guid.NewGuid().ToString("N"));
        internal readonly Store Store;
        internal Fixture(){Directory.CreateDirectory(dir);Store=new Store(Path.Combine(dir,"test.db"));Store.Migrate();}
        internal RoomMonitor Room(long id,Action<HighlightEvent>? ready=null)=>new(id,Store,new MediaTools("missing-ffmpeg"),new BilibiliLiveSource(),null,new LiveSettings{StorageRoot=dir},()=>{},ready??(_=>{}),e=>e);
        public void Dispose()=>Directory.Delete(dir,true);
    }
    [Fact]
    public async Task AwaitedTransitionSerializesAdvanceAndConfigureWhileOtherRoomProceeds()
    {
        using var f=new Fixture();var room=f.Room(1);var other=f.Room(2);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=room.RunHeatAsync(async heat=>{entered.SetResult();await resume.Task;heat.RegisterHighlight("event","danmaku",700);});
        await entered.Task;
        var advanceRan=false;
        var advance=room.RunHeatAsync(heat=>{Assert.Equal(1,heat.RecentTriggerCount);heat.Advance(701);advanceRan=true;return Task.CompletedTask;});
        Assert.False(advance.IsCompleted);Assert.False(advanceRan);
        await other.RunHeatAsync(heat=>{heat.Advance(701);return Task.CompletedTask;});
        resume.SetResult();await Task.WhenAll(first,advance);
        Assert.True(advanceRan);
        entered=new(TaskCreationOptions.RunContinuationsAsynchronously);resume=new(TaskCreationOptions.RunContinuationsAsynchronously);
        first=room.RunHeatAsync(async heat=>{entered.SetResult();await resume.Task;});await entered.Task;
        var configure=Task.Run(room.ApplyHeatOptions);await Task.Delay(20);Assert.False(configure.IsCompleted);
        resume.SetResult();await Task.WhenAll(first,configure);Assert.Equal(0,room.Snapshot().HeatRecentTriggers);
    }
    [Fact]
    public async Task ConcurrentMessagesAndSegmentsCompleteWithoutCollectionRaces()
    {
        using var f=new Fixture();var room=f.Room(1);
        var tasks=Enumerable.Range(0,300).Select(i=>Task.Run(()=>room.RunHeatAsync(heat=>{
            heat.Add(new DanmakuEvent(i.ToString(),"s",i,DateTimeOffset.UtcNow,"danmaku",1,"a","x",1));
            heat.Advance(i);return Task.CompletedTask;
        })));
        await Task.WhenAll(tasks);Assert.Equal(300/600d,room.Snapshot().Baseline,6);
    }
    [Fact]
    public async Task HighlightNotificationCanReenterSettingsAndHighlightGateAfterTransition()
    {
        using var f=new Fixture();var room=f.Room(1);
        var session=new RecordingSession("notify",1,DateTimeOffset.UtcNow,null,RecordingState.Recording);f.Store.SaveSession(session);
        var invoked=0;
        room.Highlights.Changed+=ev=>{
            if(Interlocked.Increment(ref invoked)!=1)return;
            room.ApplyHeatOptions();
            room.Highlights.MarkAtAsync(session,"manual",701).GetAwaiter().GetResult();
        };
        // Manual notifications must not hold the highlighter gate either.
        await Task.Run(()=>room.Highlights.MarkAtAsync(session,"manual",700)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2,invoked);
    }

    [Fact]
    public async Task AutomaticHighlightNotificationRunsAfterRoomGateReleased()
    {
        using var f=new Fixture();RoomMonitor room=null!;var notified=false;
        room=f.Room(1,_=>{room.ApplyHeatOptions();notified=true;});
        var session=new RecordingSession("auto-notify",1,DateTimeOffset.UtcNow,null,RecordingState.Recording);f.Store.SaveSession(session);
        await Task.Run(()=>room.RunHeatAsync(async heat=>{
            var ev=await room.Highlights.MarkAtAsync(session,"danmaku",700);
            Assert.False(notified);heat.RegisterHighlight(ev.Id,ev.Source,700);
        })).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(notified);
    }

    [Fact]
    public async Task BusinessInvalidOperationIsNotSwallowedByPacketParser()
    {
        var client=new BilibiliDanmakuClient(new BilibiliLiveSource());
        string? reported=null;client.Error+=message=>reported=message;
        client.Received+=_=>throw new InvalidOperationException("heat business failure");
        var body=Encoding.UTF8.GetBytes("{\"cmd\":\"DANMU_MSG\",\"info\":[[],\"hello\",[1,\"a\"]]}");
        var packet=new byte[16+body.Length];BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0,4),packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4,2),16);BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8,4),5);body.CopyTo(packet,16);
        var ex=await Assert.ThrowsAsync<InvalidOperationException>(()=>client.ProcessPacketAsync(packet,new RecordingSession("s",1,DateTimeOffset.UtcNow,null,RecordingState.Recording)));
        Assert.Equal("heat business failure",ex.Message);
        Assert.Equal("弹幕业务处理失败：heat business failure",reported);
    }
}
