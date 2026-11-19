using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LiveClipper.App;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class SettingsWriteGateTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly string _root=Path.Combine(Path.GetTempPath(),"liveclipper-settings-gate-"+Guid.NewGuid().ToString("N"));
    private string ConfigPath=>Path.Combine(_root,"settings.json");

    public SettingsWriteGateTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output=output;
        Directory.CreateDirectory(_root);
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}

    [Fact]
    public void Source_AllSrcSettingsPaths_RejectDirectFileWrites()
    {
        var root=FindRepoRoot();
        var offenders=new List<string>();
        foreach(var file in Directory.EnumerateFiles(Path.Combine(root,"src"),"*.cs",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(root,file).Replace('\\','/');
            if(relative.Split('/').Any(part=>part is "bin" or "obj"))continue;
            if(relative=="src/LiveClipper.App/StartupDurability.cs")continue;
            foreach(var write in SettingsWrites(File.ReadAllText(file),includeDurable:false))
                offenders.Add(relative+": "+write);
        }
        Assert.True(offenders.Count==0,string.Join(Environment.NewLine,offenders));
    }

    [Theory]
    [InlineData("File.WriteAllText(configPath, json);",true)]
    [InlineData("await System.IO.File.WriteAllTextAsync( settingsPath , json);",true)]
    [InlineData("File.WriteAllText(Path.Combine(root, \"settings.json\"), json);",true)]
    [InlineData("var path=Path.Combine(root, \"settings.json\"); var target=path; File.WriteAllText(target,node.ToJsonString());",true)]
    [InlineData("DurableFile.WriteAllText(configPath, json);",false)]
    [InlineData("File.WriteAllText(marker, \"reset\");",false)]
    [InlineData("// File.WriteAllText(configPath, json);",false)]
    [InlineData("var target=Path.GetFullPath(configPath); File.WriteAllText(target,json);",true)]
    [InlineData("var settings=LoadSettings(configPath); var marker=MarkerFor(settings); File.WriteAllText(marker,json);",false)]
    [InlineData("File.WriteAllText(Path.Combine(configDirectory, \"factory-reset-error.txt\"), json);",false)]
    public void SourceGate_DistinguishesDirectWritesAndDurableWrites(string source,bool expected)
    {
        Assert.Equal(expected,SettingsWrites(source,includeDurable:false).Any());
    }

    [Fact]
    public void Source_ProgramAndMusic_UseSharedHelperRatherThanPrivateFileWriters()
    {
        var root=FindRepoRoot();
        var program=File.ReadAllText(Path.Combine(root,"src","LiveClipper.App","Program.cs"));
        var music=File.ReadAllText(Path.Combine(root,"src","LiveClipper.App","MusicEndpoints.cs"));
        Assert.Empty(SettingsWrites(program,includeDurable:true));
        Assert.DoesNotMatch(@"\bDurableFile\s*\.\s*WriteAllText\s*\(",music);
        Assert.Contains("StartupDurability.UpdateSettings(configPath,settings",program);
        Assert.Contains("StartupDurability.UpdateSettingsAsync(configPath,settings",program);
        Assert.Contains("StartupDurability.UpdateSettingsAsync(path,settings",music);
        Assert.Contains("Save(configPath,settings,()=>music.SetPreferences(",music);
        // A PATCH snapshot taken before entering the gate reintroduces stale-field overwrite.
        var patch=program[program.IndexOf("app.MapMethods(\"/api/settings\"",StringComparison.Ordinal)..];
        var gate=patch.IndexOf("TryUpdateSettingsDurably(()=>",StringComparison.Ordinal);
        var snapshot=patch.IndexOf("LiveSettingsMutations.TryApplyPatch(settings",StringComparison.Ordinal);
        Assert.True(gate>=0&&snapshot>gate,"PATCH must read its snapshot inside the shared settings transaction");
        Assert.Contains("UpdateSettingsDurablyAsync(()=>coordinator.StopAsync(roomId))",program);
    }

    [Fact]
    public async Task ConcurrentSyncAndAsyncWriters_SerializeSnapshots_AndPreserveBothFields()
    {
        var settings=new LiveSettings {RoomId=10,BilibiliUid=20};
        StartupDurability.UpdateSettings(ConfigPath,settings,()=>true);
        var firstEntered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondAttempting=new ManualResetEventSlim();
        using var secondEntered=new ManualResetEventSlim();
        var secondReadRoomId=-1L;
        // Exercise aliases of the same config path and sync/async users of the very same gate.
        var alias=Path.Combine(_root,".","settings.json");
        var first=StartupDurability.UpdateSettingsAsync(ConfigPath,settings,async ()=>
        {
            var copy=LiveSettingsMutations.Clone(settings);
            firstEntered.SetResult(true);
            await releaseFirst.Task;
            copy.RoomId=101;
            settings.RoomId=copy.RoomId;settings.BilibiliUid=copy.BilibiliUid;
            return true;
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second=Task.Run(()=>
        {
            secondAttempting.Set();
            return StartupDurability.UpdateSettings(alias,settings,()=>
            {
                secondEntered.Set();
                var copy=LiveSettingsMutations.Clone(settings);
                secondReadRoomId=copy.RoomId;
                copy.BilibiliUid=202;
                settings.RoomId=copy.RoomId;settings.BilibiliUid=copy.BilibiliUid;
                return true;
            });
        });
        bool enteredWhileFirstHeld;
        try
        {
            Assert.True(secondAttempting.Wait(TimeSpan.FromSeconds(10)));
            enteredWhileFirstHeld=secondEntered.Wait(TimeSpan.FromMilliseconds(200));
        }
        finally
        {
            releaseFirst.TrySetResult(true);
            await Task.WhenAll(first,second).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False(enteredWhileFirstHeld);
        Assert.Equal(101L,secondReadRoomId);
        var saved=JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!;
        Assert.Equal(101L,saved.RoomId);
        Assert.Equal(202L,saved.BilibiliUid);
        Assert.Equal(101L,settings.RoomId);
        Assert.Equal(202L,settings.BilibiliUid);
        AssertNoTemps();
    }

    [Fact]
    public void RejectedMutation_DoesNotPublish_AndReleasesGate()
    {
        var settings=new LiveSettings {RoomId=10};
        StartupDurability.UpdateSettings(ConfigPath,settings,()=>true);
        var before=File.ReadAllBytes(ConfigPath);
        Assert.False(StartupDurability.UpdateSettings(ConfigPath,settings,()=>false));
        Assert.Equal(before,File.ReadAllBytes(ConfigPath));
        Assert.True(StartupDurability.UpdateSettings(ConfigPath,settings,()=>{settings.RoomId=11;return true;}));
        Assert.Equal(11L,JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!.RoomId);
    }

    [Fact]
    public async Task ThrowingMutation_LeavesOriginalBytes_AndReleasesAsyncGate()
    {
        var settings=new LiveSettings {RoomId=10};
        StartupDurability.UpdateSettings(ConfigPath,settings,()=>true);
        var before=File.ReadAllBytes(ConfigPath);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>StartupDurability.UpdateSettingsAsync(ConfigPath,settings,async ()=>
        {
            await Task.Yield();
            throw new InvalidOperationException("mutation failed");
        }));
        Assert.Equal(before,File.ReadAllBytes(ConfigPath));
        Assert.True(StartupDurability.UpdateSettings(ConfigPath,settings,()=>true));
        AssertNoTemps();
    }

    [Fact]
    public void SerializationFailure_AfterPartialJson_LeavesOriginalBytes_AndNoTemp()
    {
        var settings=new LiveSettings {RoomId=10};
        StartupDurability.UpdateSettings(ConfigPath,settings,()=>true);
        var before=File.ReadAllBytes(ConfigPath);
        var converter=new ThrowAfterPrefixConverter();
        var options=new JsonSerializerOptions();options.Converters.Add(converter);
        Assert.Throws<IOException>(()=>StartupDurability.UpdateSettings(ConfigPath,settings,()=>true,options));
        Assert.True(converter.WrotePrefix);
        Assert.Equal(before,File.ReadAllBytes(ConfigPath));
        Assert.Equal(10L,JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!.RoomId);
        AssertNoTemps();
        Assert.True(StartupDurability.UpdateSettings(ConfigPath,settings,()=>true));
    }

    [Fact]
    public void PublishFailure_AfterTempWrite_LeavesOriginalBytes_AndCleansTemp()
    {
        // Windows sharing semantics prevent the final rename, after DurableFile has written and flushed its sibling temp.
        if(!OperatingSystem.IsWindows())
        {
            _output.WriteLine("[skip] 非 Windows 平台不支持此测试依赖的文件删除共享锁语义，设置发布失败与临时文件清理测试跳过");
            return;
        }
        var settings=new LiveSettings {RoomId=10};
        StartupDurability.UpdateSettings(ConfigPath,settings,()=>true);
        var before=File.ReadAllBytes(ConfigPath);
        using(var held=new FileStream(ConfigPath,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var ex=Assert.ThrowsAny<Exception>(()=>StartupDurability.UpdateSettings(ConfigPath,settings,()=>{settings.RoomId=77;return true;}));
            Assert.True(ex is IOException||ex is UnauthorizedAccessException,ex.GetType().FullName);
            Assert.Equal(before,File.ReadAllBytes(ConfigPath));
            Assert.Equal(10L,JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!.RoomId);
            Assert.Equal(10L,settings.RoomId);
            AssertNoTemps();
        }
        Assert.True(StartupDurability.UpdateSettings(ConfigPath,settings,()=>true));
        Assert.Equal(10L,JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!.RoomId);
    }

    [Theory]
    [InlineData("{\"RoomId\":12, broken")]
    [InlineData("null")]
    public void CorruptOriginal_IsBackedUpBeforeExplicitSave_WhileStartupNeverOverwrites(string corrupt)
    {
        File.WriteAllText(ConfigPath,corrupt);
        var load=StartupDurability.LoadSettings(ConfigPath,()=>new LiveSettings {RoomId=12});
        Assert.False(load.Writable);
        Assert.NotNull(load.Warning);
        Assert.NotNull(load.PreservedCorruptPath);
        var backup=load.PreservedCorruptPath!;
        Assert.StartsWith(Path.GetFullPath(ConfigPath)+".corrupt-",backup);
        Assert.Equal(corrupt,File.ReadAllText(backup));
        StartupDurability.UpdateSettings(ConfigPath,load.Settings,()=>{load.Settings.SettingsVersion=4;return true;},persist:load.Writable);
        Assert.Equal(corrupt,File.ReadAllText(ConfigPath));
        StartupDurability.UpdateSettings(ConfigPath,load.Settings,()=>{load.Settings.RoomId=99;return true;});
        Assert.Equal(corrupt,File.ReadAllText(backup));
        Assert.Equal(99L,JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!.RoomId);
        Assert.All(Directory.EnumerateFiles(_root,"settings.json.corrupt-*"),file=>Assert.Equal(corrupt,File.ReadAllText(file)));
        AssertNoTemps();
    }

    [Fact]
    public void CorruptionAfterLoad_IsPreservedBeforeNextSave()
    {
        var settings=new LiveSettings {RoomId=10};
        StartupDurability.UpdateSettings(ConfigPath,settings,()=>true);
        const string corrupt="{\"RoomId\": broken after startup";
        File.WriteAllText(ConfigPath,corrupt);
        StartupDurability.UpdateSettings(ConfigPath,settings,()=>{settings.RoomId=11;return true;});
        var backup=Assert.Single(Directory.EnumerateFiles(_root,"settings.json.corrupt-*"));
        Assert.Equal(corrupt,File.ReadAllText(backup));
        Assert.Equal(11L,JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(ConfigPath))!.RoomId);
    }

    private void AssertNoTemps()=>Assert.Empty(Directory.EnumerateFiles(_root).Where(path=>DurableFile.IsTempName(Path.GetFileName(path))));

    private sealed class ThrowAfterPrefixConverter : JsonConverter<LiveSettings>
    {
        public bool WrotePrefix {get;private set;}
        public override LiveSettings? Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)=>throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer,LiveSettings value,JsonSerializerOptions options)
        {
            writer.WriteStartObject();writer.WriteNumber("RoomId",value.RoomId);writer.Flush();
            WrotePrefix=true;
            throw new IOException("serialization failed after JSON prefix");
        }
    }

    // Follow config/settings identifiers plus aliases assigned from a settings.json literal.
    // The first-argument scan supports nested Path.Combine and multi-line/async calls; comments do not create offenders.
    private static IReadOnlyList<string> SettingsWrites(string source,bool includeDurable)
    {
        source=Regex.Replace(source,"(?m)^\\s*//.*$|/\\*[\\s\\S]*?\\*/","");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"configPath","settingsPath","settingsFile","settingsFilePath"};
        var assignments=Regex.Matches(source,@"\b(?<name>[A-Za-z_]\w*)\s*=\s*(?<value>[^;\r\n]+)");
        bool added;
        do
        {
            added=false;
            foreach(Match assignment in assignments)
            {
                var value=assignment.Groups["value"].Value;
                if(IsSettingsPath(value,paths))added|=paths.Add(assignment.Groups["name"].Value);
            }
        }while(added);
        var offenders=new List<string>();
        var owners=includeDurable?@"(?:System\s*\.\s*IO\s*\.\s*)?File|DurableFile":@"(?:System\s*\.\s*IO\s*\.\s*)?File";
        foreach(Match call in Regex.Matches(source,@"(?<![\w])(?:"+owners+@")\s*\.\s*WriteAllText(?:Async)?\s*\("))
        {
            var start=call.Index+call.Length;
            var end=start;var depth=0;var quoted=false;var escaped=false;
            for(;end<source.Length;end++)
            {
                var ch=source[end];
                if(quoted)
                {
                    if(escaped){escaped=false;continue;}
                    if(ch=='\\'){escaped=true;continue;}
                    if(ch=='"')quoted=false;
                    continue;
                }
                if(ch=='"'){quoted=true;continue;}
                if(ch=='(')depth++;
                else if(ch==')'){if(depth==0)break;depth--;}
                else if(ch==','&&depth==0)break;
            }
            var argument=source[start..end];
            if(IsSettingsPath(argument,paths))offenders.Add(call.Value+argument);
        }
        return offenders;
    }

    private static bool IsSettingsPath(string expression,HashSet<string> paths)
    {
        expression=expression.Trim();
        if(paths.Contains(expression))return true;
        // Propagate path values, not every object produced by a method that takes a path.
        // LoadSettings(configPath) returns settings, not a path; tainting it would also taint reset/error filenames.
        var literal=expression.StartsWith("\"",StringComparison.Ordinal)||expression.StartsWith("@\"",StringComparison.Ordinal)
            ||expression.StartsWith("$\"",StringComparison.Ordinal)||expression.StartsWith("$@\"",StringComparison.Ordinal);
        var pathCall=Regex.IsMatch(expression,@"^(?:System\s*\.\s*IO\s*\.\s*)?Path\s*\.\s*(?:Combine|Join|GetFullPath)\s*\(");
        if(!literal&&!pathCall)return false;
        return expression.Contains("settings.json",StringComparison.OrdinalIgnoreCase)
            ||pathCall&&paths.Any(path=>Regex.IsMatch(expression,@"\b"+Regex.Escape(path)+@"\b",RegexOptions.IgnoreCase));
    }

    private static string FindRepoRoot()
    {
        for(var dir=new DirectoryInfo(AppContext.BaseDirectory);dir is not null;dir=dir.Parent)
            if(File.Exists(Path.Combine(dir.FullName,"src","LiveClipper.App","StartupDurability.cs")))return dir.FullName;
        throw new DirectoryNotFoundException("LiveClipper repository root not found");
    }
}
