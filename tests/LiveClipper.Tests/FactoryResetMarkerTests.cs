using System.Globalization;
using System.Text.Json;
using LiveClipper.App;
using LiveClipper.Core;
using Xunit.Abstractions;

namespace LiveClipper.Tests;

public sealed class FactoryResetMarkerTests : IDisposable
{
    private const string TokenA="aabbccddeeff00112233445566778899";
    private const string TokenB="11223344556677889900aabbccddeeff";
    private static readonly DateTimeOffset Now=new(2026,1,1,0,0,0,TimeSpan.Zero);
    private readonly string _root=Path.Combine(Path.GetTempPath(),"liveclipper-reset-tests-"+Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private string DirectoryPath=>FactoryResetMarker.PrivateDirectory(_root);
    private static FactoryResetMarkerIdentity Owner=>new(42,TokenA,Now.AddMinutes(-1));

    public FactoryResetMarkerTests(ITestOutputHelper output)
    {
        _output=output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if(Directory.Exists(_root))Directory.Delete(_root,true);
    }

    [Fact]
    public void PrivateDirectory_UsesInjectedRoot_AndIdentitySeparatesRuns()
    {
        Assert.Equal(Path.Combine(_root,"LiveClipper","ipc","factory-reset"),DirectoryPath);
        var path=FactoryResetMarker.MarkerPath(DirectoryPath,Owner);
        Assert.Equal(DirectoryPath,Path.GetDirectoryName(path));
        Assert.Equal($"factory-reset-{Owner.Pid.ToString(CultureInfo.InvariantCulture)}-{TokenA}.json",Path.GetFileName(path));
        Assert.NotEqual(path,FactoryResetMarker.MarkerPath(DirectoryPath,Owner with {Pid=43}));
        Assert.NotEqual(path,FactoryResetMarker.MarkerPath(DirectoryPath,Owner with {Token=TokenB}));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative-root")]
    public void PrivateDirectory_RejectsMissingOrRelativeRoot_InsteadOfFallingBack(string root)
        =>Assert.Throws<ArgumentException>(()=>FactoryResetMarker.PrivateDirectory(root));

    [Theory]
    [InlineData(0,TokenA)]
    [InlineData(-1,TokenA)]
    [InlineData(42,"")]
    [InlineData(42,"../../outside")]
    [InlineData(42,"not-a-guid")]
    public void MarkerPath_RejectsInvalidOrPathShapedIdentity(int pid,string token)
        =>Assert.Throws<ArgumentException>(()=>FactoryResetMarker.MarkerPath(DirectoryPath,new(pid,token,Now)));

    [Fact]
    public void Publish_ReadsRealFileWithLiveProcess_AndNormalizesUtc()
    {
        var owner=Owner with {Pid=Environment.ProcessId};
        FactoryResetMarker.Publish(DirectoryPath,owner,Now.ToOffset(TimeSpan.FromHours(8)));
        var path=FactoryResetMarker.MarkerPath(DirectoryPath,owner);
        using(var json=JsonDocument.Parse(File.ReadAllText(path)))
        {
            var payload=json.RootElement;
            Assert.Equal(3,payload.EnumerateObject().Count());
            Assert.Equal(owner.Pid,payload.GetProperty("pid").GetInt32());
            Assert.Equal(owner.Token,payload.GetProperty("token").GetString());
            var created=payload.GetProperty("createdUtc").GetDateTimeOffset();
            Assert.Equal(Now,created);
            Assert.Equal(TimeSpan.Zero,created.Offset);
        }
        Assert.True(FactoryResetMarker.IsProcessAlive(Environment.ProcessId));
        Assert.False(FactoryResetMarker.IsProcessAlive(0));
        Assert.False(FactoryResetMarker.IsProcessAlive(-1));
        Assert.True(FactoryResetMarker.ReadCurrent(DirectoryPath,owner,Now,FactoryResetMarker.IsProcessAlive));
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(DirectoryPath).Where(file=>DurableFile.IsTempName(Path.GetFileName(file))));
    }

    [Fact]
    public void Publish_RejectsTimestampBeforeRun_WithoutCreatingMarker()
    {
        Assert.Throws<ArgumentOutOfRangeException>(()=>FactoryResetMarker.Publish(DirectoryPath,Owner,Owner.StartedUtc.AddTicks(-1)));
        Assert.False(File.Exists(FactoryResetMarker.MarkerPath(DirectoryPath,Owner)));
    }

    [Fact]
    public void ValidPayload_RequiresExactPidTokenAndLiveProcess()
    {
        var probes=new List<int>();
        Assert.True(FactoryResetMarker.IsValid(Json(Owner,Now),Owner,Now,pid=>{probes.Add(pid);return true;}));
        Assert.Equal(new[]{Owner.Pid},probes);
        Assert.False(FactoryResetMarker.IsValid(Json(Owner with {Pid=43},Now),Owner,Now,_=>true));
        Assert.False(FactoryResetMarker.IsValid(Json(Owner with {Token=TokenB},Now),Owner,Now,_=>true));
        Assert.False(FactoryResetMarker.IsValid(Json(Owner with {Token=TokenA.ToUpperInvariant()},Now),Owner,Now,_=>true));
        Assert.False(FactoryResetMarker.IsValid(Json(Owner,Now),Owner,Now,_=>false));
    }

    [Theory]
    [InlineData("2026-01-01T00:00:00Z")]
    [InlineData("2026-01-01T00:00:00+00:00")]
    public void ValidPayload_AcceptsExplicitUtcSuffix(string createdUtc)
    {
        var json=JsonSerializer.Serialize(new {pid=Owner.Pid,token=Owner.Token,createdUtc});
        Assert.True(FactoryResetMarker.IsValid(json,Owner,Now,_=>true));
    }

    [Fact]
    public void ValidPayload_RejectsFutureExpiredAndPreRunTimestamps_WithExactBoundary()
    {
        var owner=Owner with {StartedUtc=Now.AddHours(-1)};
        Assert.True(FactoryResetMarker.IsValid(Json(owner,Now-FactoryResetMarker.MaximumAge),owner,Now,_=>true));
        Assert.False(FactoryResetMarker.IsValid(Json(owner,(Now-FactoryResetMarker.MaximumAge).AddTicks(-1)),owner,Now,_=>true));
        Assert.False(FactoryResetMarker.IsValid(Json(owner,Now.AddTicks(1)),owner,Now,_=>true));
        Assert.True(FactoryResetMarker.IsValid(Json(Owner,Owner.StartedUtc),Owner,Now,_=>true));
        Assert.False(FactoryResetMarker.IsValid(Json(Owner,Owner.StartedUtc.AddTicks(-1)),Owner,Now,_=>true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42,\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\"}")]
    [InlineData("{\"pid\":\"42\",\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":0,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":2147483648,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42.5,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42,\"token\":null,\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42,\"token\":42,\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42,\"token\":\"bad\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":0}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"not-a-date\"}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00\"}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01\"}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00-00:00\"}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T08:00:00+08:00\"}")]
    [InlineData("{\"pid\":42,\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00Z\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":42,\"token\":\"aabbccddeeff00112233445566778899\",\"createdUtc\":\"2026-01-01T00:00:00Z\",\"extra\":true}")]
    public void StrictPayload_RejectsMalformedOrAmbiguousJson_BeforeProcessProbe(string json)
        =>Assert.False(FactoryResetMarker.IsValid(json,Owner,Now,_=>throw new InvalidOperationException("Invalid JSON reached process probe")));

    [Fact]
    public void ReadCurrent_MissingDirectoryOrFile_DoesNotCreateAnything()
    {
        Assert.False(FactoryResetMarker.ReadCurrent(DirectoryPath,Owner,Now,_=>true));
        Assert.False(Directory.Exists(DirectoryPath));
        Directory.CreateDirectory(DirectoryPath);
        Assert.False(FactoryResetMarker.ReadCurrent(DirectoryPath,Owner,Now,_=>true));
        Assert.Empty(Directory.EnumerateFiles(DirectoryPath));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-pid")]
    [InlineData("wrong-token")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("pre-run")]
    [InlineData("dead")]
    public void ReadCurrent_InvalidMarker_IsIgnoredAndDeleted(string reason)
    {
        var json=reason switch
        {
            "malformed"=>"{",
            "wrong-pid"=>Json(Owner with {Pid=43},Now),
            "wrong-token"=>Json(Owner with {Token=TokenB},Now),
            "expired"=>Json(Owner,Now.AddMinutes(-6)),
            "future"=>Json(Owner,Now.AddTicks(1)),
            "pre-run"=>Json(Owner,Owner.StartedUtc.AddTicks(-1)),
            "dead"=>Json(Owner,Now),
            _=>throw new ArgumentException("Unknown scenario",nameof(reason))
        };
        var path=Write(Owner,json);
        Assert.False(FactoryResetMarker.ReadCurrent(DirectoryPath,Owner,Now,_=>reason!="dead"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ClearStale_RemovesOnlyInvalidExpiredDeadOrPreviousRunMarkers()
    {
        var current=Write(Owner,Json(Owner,Now));
        var active=Owner with {Pid=43};
        var activePath=Write(active,Json(active,Now));
        var oldRun=Owner with {Token=TokenB};
        var oldRunPath=Write(oldRun,Json(oldRun,Now));
        var expired=Owner with {Pid=44};
        var expiredPath=Write(expired,Json(expired,Now.AddMinutes(-6)));
        var dead=Owner with {Pid=45};
        var deadPath=Write(dead,Json(dead,Now));
        var malformedPath=Write(Owner with {Pid=46},"{");
        var mismatchedPath=Write(Owner with {Pid=47},Json(active,Now));
        var unrelated=Path.Combine(DirectoryPath,"unrelated.json");
        File.WriteAllText(unrelated,"keep");
        var nested=Path.Combine(DirectoryPath,"nested");
        Directory.CreateDirectory(nested);
        var nestedPath=Path.Combine(nested,"factory-reset-nested.json");
        File.WriteAllText(nestedPath,"{");
        bool IsAlive(int pid)=>pid!=dead.Pid;
        Assert.True(FactoryResetMarker.ClearStale(DirectoryPath,Owner,Now,IsAlive));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(activePath));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(nestedPath));
        foreach(var removed in new[]{oldRunPath,expiredPath,deadPath,malformedPath,mismatchedPath})Assert.False(File.Exists(removed),removed);
        Assert.False(FactoryResetMarker.ClearStale(DirectoryPath,Owner,Now,IsAlive));
    }

    [Fact]
    public void ClearStale_RejectsCurrentPreRunMarker_AndMissingDirectoryIsHarmless()
    {
        Assert.False(FactoryResetMarker.ClearStale(DirectoryPath,Owner,Now,_=>true));
        var path=Write(Owner,Json(Owner,Owner.StartedUtc.AddTicks(-1)));
        Assert.True(FactoryResetMarker.ClearStale(DirectoryPath,Owner,Now,_=>true));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DeleteCurrent_DoesNotDeleteAnotherPidOrToken()
    {
        var current=Write(Owner,Json(Owner,Now));
        var otherPid=Owner with {Pid=43};
        var otherPidPath=Write(otherPid,Json(otherPid,Now));
        var otherToken=Owner with {Token=TokenB};
        var otherTokenPath=Write(otherToken,Json(otherToken,Now));
        Assert.True(FactoryResetMarker.DeleteCurrent(DirectoryPath,Owner));
        Assert.False(File.Exists(current));
        Assert.True(File.Exists(otherPidPath));
        Assert.True(File.Exists(otherTokenPath));
        Assert.False(FactoryResetMarker.DeleteCurrent(DirectoryPath,Owner));
    }

    [Fact]
    public void PublishFailure_KeepsPreviousCompleteMarker_AndRemovesTemporaryFile()
    {
        if(!OperatingSystem.IsWindows())
        {
            _output.WriteLine("[skip] 非 Windows 不具有本例要求的文件删除共享锁语义；标记原子发布失败分支未执行。");
            return;
        }
        FactoryResetMarker.Publish(DirectoryPath,Owner,Now);
        var path=FactoryResetMarker.MarkerPath(DirectoryPath,Owner);
        var before=File.ReadAllBytes(path);
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var ex=Assert.ThrowsAny<Exception>(()=>FactoryResetMarker.Publish(DirectoryPath,Owner,Now.AddSeconds(1)));
            Assert.True(ex is IOException or UnauthorizedAccessException,ex.GetType().FullName);
            Assert.Equal(before,File.ReadAllBytes(path));
            Assert.True(FactoryResetMarker.ReadCurrent(DirectoryPath,Owner,Now,_=>true));
            Assert.Empty(Directory.EnumerateFiles(DirectoryPath).Where(file=>DurableFile.IsTempName(Path.GetFileName(file))));
        }
        FactoryResetMarker.Publish(DirectoryPath,Owner,Now.AddSeconds(1));
        Assert.True(FactoryResetMarker.ReadCurrent(DirectoryPath,Owner,Now.AddSeconds(1),_=>true));
    }

    [Fact]
    public void UnreadableMarker_FailsClosed_AndDeletesInvalidPayloadAfterLockRelease()
    {
        if(!OperatingSystem.IsWindows())
        {
            _output.WriteLine("[skip] 非 Windows 不保证 FileShare.None 阻止其他读取；标记读取锁定分支未执行。");
            return;
        }
        var path=Write(Owner,"{");
        using(var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            Assert.False(FactoryResetMarker.ReadCurrent(DirectoryPath,Owner,Now,_=>true));
            Assert.False(FactoryResetMarker.ClearStale(DirectoryPath,Owner,Now,_=>true));
            Assert.False(FactoryResetMarker.DeleteCurrent(DirectoryPath,Owner));
            Assert.True(File.Exists(path));
        }
        Assert.False(FactoryResetMarker.ReadCurrent(DirectoryPath,Owner,Now,_=>true));
        Assert.False(File.Exists(path));
    }

    private string Write(FactoryResetMarkerIdentity owner,string json)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path=FactoryResetMarker.MarkerPath(DirectoryPath,owner);
        File.WriteAllText(path,json);
        return path;
    }

    private static string Json(FactoryResetMarkerIdentity owner,DateTimeOffset created)
        =>JsonSerializer.Serialize(new {pid=owner.Pid,token=owner.Token,createdUtc=created});
}
