using System.Text.Json;
using System.Text.Json.Serialization;
using LiveClipper.App;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class SettingsWriteRollbackTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly string _root=Path.Combine(Path.GetTempPath(),"liveclipper-settings-rollback-"+Guid.NewGuid().ToString("N"));
    private string ConfigPath=>Path.Combine(_root,"settings.json");

    public SettingsWriteRollbackTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output=output;
        Directory.CreateDirectory(_root);
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishFailure_RestoresSharedSettingsAndList_AndNextSaveDoesNotPublishFailedValues(bool useAsync)
    {
        // Denying delete sharing prevents publication after DurableFile has flushed its sibling temp.
        if(!OperatingSystem.IsWindows())
        {
            _output.WriteLine("[skip] 非 Windows 平台不支持此测试依赖的文件删除共享锁语义，设置发布失败后的内存与列表回滚测试跳过");
            return;
        }
        var settings=InitialSettings();
        var shared=settings;
        var rooms=settings.RoomIds;
        Assert.True(StartupDurability.UpdateSettings(ConfigPath,settings,()=>true));
        var before=File.ReadAllBytes(ConfigPath);
        using(var held=new FileStream(ConfigPath,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var ex=await Assert.ThrowsAnyAsync<Exception>(()=>Update(useAsync,settings,()=>
            {
                settings.RoomId=77;
                settings.BilibiliUid=88;
                settings.RoomIds.Add(77);
                settings.RoomIds=[99];
                return true;
            }));
            Assert.True(ex is IOException or UnauthorizedAccessException,ex.GetType().FullName);
            Assert.Same(shared,settings);
            Assert.Same(rooms,settings.RoomIds);
            AssertInitialSettings(settings);
            Assert.Equal(before,File.ReadAllBytes(ConfigPath));
            AssertNoTemps();
        }
        Assert.True(await Update(useAsync,settings,()=>true));
        AssertInitialSettings(JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!);
        Assert.True(await Update(useAsync,settings,()=>{settings.RoomId=12;return true;}));
        Assert.Equal(12L,settings.RoomId);
        Assert.Equal(12L,JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!.RoomId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerializationFailure_RestoresMemory_WithoutChangingDisk(bool useAsync)
    {
        var settings=InitialSettings();
        Assert.True(StartupDurability.UpdateSettings(ConfigPath,settings,()=>true));
        var before=File.ReadAllBytes(ConfigPath);
        var converter=new FailingSettingsConverter();
        var options=new JsonSerializerOptions();
        options.Converters.Add(converter);
        var ex=await Assert.ThrowsAsync<IOException>(()=>Update(useAsync,settings,()=>
        {
            settings.RoomId=77;
            settings.RoomIds.Clear();
            return true;
        },options));
        Assert.Equal("settings serialization failed",ex.Message);
        Assert.True(converter.Entered);
        AssertInitialSettings(settings);
        Assert.Equal(before,File.ReadAllBytes(ConfigPath));
        AssertNoTemps();
        Assert.True(await Update(useAsync,settings,()=>true));
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task RejectedOrThrowingMutation_RestoresMemory_AndReleasesGate(bool useAsync,bool throws)
    {
        var settings=InitialSettings();
        Assert.True(StartupDurability.UpdateSettings(ConfigPath,settings,()=>true));
        var before=File.ReadAllBytes(ConfigPath);
        bool Mutate()
        {
            settings.RoomId=77;
            settings.RoomIds.Clear();
            if(throws)throw new InvalidOperationException("mutation failed");
            return false;
        }
        if(throws)
            await Assert.ThrowsAsync<InvalidOperationException>(()=>Update(useAsync,settings,Mutate));
        else
            Assert.False(await Update(useAsync,settings,Mutate));
        AssertInitialSettings(settings);
        Assert.Equal(before,File.ReadAllBytes(ConfigPath));
        Assert.True(await Update(useAsync,settings,()=>true));
        AssertInitialSettings(JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!);
    }

    [Fact]
    public void NonPersistentSuccessfulMutation_KeepsMemoryAndDoesNotPublish()
    {
        var settings=InitialSettings();
        Assert.True(StartupDurability.UpdateSettings(ConfigPath,settings,()=>
        {
            settings.RoomId=77;
            return true;
        },persist:false));
        Assert.Equal(77L,settings.RoomId);
        Assert.False(File.Exists(ConfigPath));
    }

    private Task<bool> Update(bool useAsync,LiveSettings settings,Func<bool> update,JsonSerializerOptions? options=null)
    {
        if(useAsync)return StartupDurability.UpdateSettingsAsync(ConfigPath,settings,async ()=>
        {
            await Task.Yield();
            return update();
        },options);
        // Run the sync API in a Task so both branches expose exceptions through the same async assertion.
        return Task.Run(()=>StartupDurability.UpdateSettings(ConfigPath,settings,update,options));
    }

    private static LiveSettings InitialSettings()=>new(){RoomId=10,BilibiliUid=20,RoomIds=[10,11]};
    private static void AssertInitialSettings(LiveSettings settings)
    {
        Assert.Equal(10L,settings.RoomId);
        Assert.Equal(20L,settings.BilibiliUid);
        Assert.Equal(new long[]{10,11},settings.RoomIds);
    }
    private void AssertNoTemps()=>Assert.Empty(Directory.EnumerateFiles(_root).Where(path=>DurableFile.IsTempName(Path.GetFileName(path))));

    private sealed class FailingSettingsConverter : JsonConverter<LiveSettings>
    {
        public bool Entered {get;private set;}
        public override LiveSettings? Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)=>throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer,LiveSettings value,JsonSerializerOptions options)
        {
            Entered=true;
            writer.WriteStartObject();
            writer.WriteNumber("RoomId",value.RoomId);
            writer.Flush();
            throw new IOException("settings serialization failed");
        }
    }
}
