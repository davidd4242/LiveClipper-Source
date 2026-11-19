using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LiveClipper.Core;

namespace LiveClipper.App;

internal readonly record struct FactoryResetMarkerIdentity(int Pid,string Token,DateTimeOffset StartedUtc);

internal static class FactoryResetMarker
{
    internal static readonly TimeSpan MaximumAge=TimeSpan.FromMinutes(5);
    internal static readonly FactoryResetMarkerIdentity Current=new(Environment.ProcessId,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow);
    private sealed record Payload(int Pid,string Token,DateTimeOffset CreatedUtc);

    internal static string PrivateDirectory(string localAppData)
    {
        if(string.IsNullOrWhiteSpace(localAppData)||!Path.IsPathFullyQualified(localAppData))
            throw new ArgumentException("LocalApplicationData must be an absolute path",nameof(localAppData));
        return Path.Combine(localAppData,"LiveClipper","ipc","factory-reset");
    }

    internal static string MarkerPath(string directory,FactoryResetMarkerIdentity owner)
    {
        if(owner.Pid<=0||!IsToken(owner.Token))throw new ArgumentException("Invalid factory reset marker identity",nameof(owner));
        return Path.Combine(directory,$"factory-reset-{owner.Pid.ToString(CultureInfo.InvariantCulture)}-{owner.Token}.json");
    }

    internal static void Publish(string directory,FactoryResetMarkerIdentity owner,DateTimeOffset now)
    {
        if(now<owner.StartedUtc)throw new ArgumentOutOfRangeException(nameof(now));
        var path=MarkerPath(directory,owner);
        DurableFile.WriteAllText(path,JsonSerializer.Serialize(new {pid=owner.Pid,token=owner.Token,createdUtc=now.ToUniversalTime()}));
    }

    internal static bool IsValid(string json,FactoryResetMarkerIdentity expected,DateTimeOffset now,Func<int,bool> isProcessAlive)
        =>TryParse(json,out var payload)&&IsFresh(payload,now)&&payload.Pid==expected.Pid
            &&string.Equals(payload.Token,expected.Token,StringComparison.Ordinal)&&payload.CreatedUtc>=expected.StartedUtc
            &&isProcessAlive(payload.Pid);

    internal static bool ReadCurrent(string directory,FactoryResetMarkerIdentity expected,DateTimeOffset now,Func<int,bool> isProcessAlive)
    {
        var path=MarkerPath(directory,expected);
        try
        {
            if(IsValid(File.ReadAllText(path),expected,now,isProcessAlive))return true;
        }
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return false;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return false;}
        TryDelete(path);
        return false;
    }

    internal static bool DeleteCurrent(string directory,FactoryResetMarkerIdentity current)
        =>TryDelete(MarkerPath(directory,current));

    internal static bool ClearStale(string directory,FactoryResetMarkerIdentity current,DateTimeOffset now,Func<int,bool> isProcessAlive)
    {
        var removed=false;
        try
        {
            if(!Directory.Exists(directory))return false;
            foreach(var path in Directory.EnumerateFiles(directory,"factory-reset-*.json",SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if(TryParse(File.ReadAllText(path),out var payload)&&IsFresh(payload,now)
                        &&string.Equals(Path.GetFileName(path),Path.GetFileName(MarkerPath(directory,new(payload.Pid,payload.Token,default))),StringComparison.Ordinal)
                        &&isProcessAlive(payload.Pid)
                        &&(payload.Pid!=current.Pid||string.Equals(payload.Token,current.Token,StringComparison.Ordinal)&&payload.CreatedUtc>=current.StartedUtc))
                        continue;
                    removed=TryDelete(path)||removed;
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){ }
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){ }
        return removed;
    }

    internal static bool IsProcessAlive(int pid)
    {
        if(pid<=0)return false;
        try
        {
            using var process=Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException){return false;}
    }

    private static bool TryParse(string json,out Payload payload)
    {
        payload=new(0,"",default);
        try
        {
            using var document=JsonDocument.Parse(json);
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return false;
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in root.EnumerateObject())
                if(property.Name is not ("pid" or "token" or "createdUtc")||!names.Add(property.Name))return false;
            if(names.Count!=3
                ||!root.TryGetProperty("pid",out var pid)||pid.ValueKind!=JsonValueKind.Number||!pid.TryGetInt32(out var value)||value<=0
                ||!root.TryGetProperty("token",out var token)||token.ValueKind!=JsonValueKind.String||!IsToken(token.GetString())
                ||!root.TryGetProperty("createdUtc",out var created)||created.ValueKind!=JsonValueKind.String
                ||!created.TryGetDateTimeOffset(out var timestamp)||timestamp.Offset!=TimeSpan.Zero)
                return false;
            var createdText=created.GetString()!;
            if(!createdText.EndsWith("Z",StringComparison.Ordinal)&&!createdText.EndsWith("+00:00",StringComparison.Ordinal))return false;
            payload=new(value,token.GetString()!,timestamp);
            return true;
        }
        catch(JsonException){return false;}
    }

    private static bool IsToken(string? token)=>token is not null&&token.Length==32&&Guid.TryParseExact(token,"N",out _);
    private static bool IsFresh(Payload payload,DateTimeOffset now)=>payload.CreatedUtc<=now&&now-payload.CreatedUtc<=MaximumAge;

    private static bool TryDelete(string path)
    {
        try
        {
            if(!File.Exists(path))return false;
            File.Delete(path);
            return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return false;}
    }
}
