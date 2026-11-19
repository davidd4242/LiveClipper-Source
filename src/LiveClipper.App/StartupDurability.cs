using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveClipper.Core;

namespace LiveClipper.App;

/// <summary>Result of loading settings.json at startup. A corrupt file is preserved and reported, never silently replaced.</summary>
internal sealed record SettingsLoadResult(LiveSettings Settings,bool Loaded,string? Warning,string? PreservedCorruptPath)
{
    /// <summary>True only when the settings came from a readable, valid settings.json, or no file existed yet.</summary>
    public bool Writable => Warning is null;
}

/// <summary>
/// Startup durability helpers used by Program.cs (top-level statements cannot be unit tested directly).
/// All methods are pure apart from the file system / named-mutex calls they are named after.
/// </summary>
internal static class StartupDurability
{
    public const string DataRootMutexPrefix=@"Local\LiveClipper-Data-";
    /// <summary>A DurableFile temp younger than this may belong to a write still in progress and is left alone.</summary>
    public static readonly TimeSpan StaleTempAge=TimeSpan.FromHours(1);

    /// <summary>Full path, unified separators, no trailing separator (except a bare root), lowercased (Windows paths are case-insensitive).</summary>
    public static string NormalizeDataRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var full=Path.GetFullPath(dataRoot).Replace(Path.AltDirectorySeparatorChar,Path.DirectorySeparatorChar);
        var root=Path.GetPathRoot(full)??"";
        if(full.Length>root.Length)full=full.TrimEnd(Path.DirectorySeparatorChar);
        return full.ToLowerInvariant();
    }

    /// <summary><c>Local\LiveClipper-Data-&lt;first 16 hex of sha256(normalized root), lowercase&gt;</c>.</summary>
    public static string DataRootMutexName(string dataRoot)
    {
        var hash=SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeDataRoot(dataRoot)));
        return DataRootMutexPrefix+Convert.ToHexString(hash,0,8).ToLowerInvariant();
    }

    /// <summary>
    /// Takes the per-data-root singleton mutex without blocking. Returns null when another process holds it.
    /// An abandoned mutex (previous owner crashed) counts as acquired.
    /// </summary>
    public static Mutex? TryAcquireDataRootMutex(string dataRoot)
    {
        var mutex=new Mutex(false,DataRootMutexName(dataRoot));
        try
        {
            if(mutex.WaitOne(0))return mutex;
        }
        catch(AbandonedMutexException)
        {
            return mutex;
        }
        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Removes temps left by a crashed DurableFile.WriteAllText (names containing DurableFile.TempMarker and ending in .tmp)
    /// whose last write is at least <paramref name="olderThan"/> before <paramref name="nowUtc"/>.
    /// Only the top level of <paramref name="directory"/> is scanned; other files (including user *.tmp files) are never touched.
    /// </summary>
    public static int DeleteStaleTemps(string directory,TimeSpan olderThan,DateTime nowUtc)
    {
        if(!Directory.Exists(directory))return 0;
        var cutoff=nowUtc-olderThan;
        var removed=0;
        foreach(var file in Directory.EnumerateFiles(directory,"*"+DurableFile.TempMarker+"*.tmp"))
        {
            var name=Path.GetFileName(file);
            if(!DurableFile.IsTempName(name)||!name.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase))continue;
            try
            {
                if(File.GetLastWriteTimeUtc(file)>cutoff)continue;
                File.Delete(file);
                removed++;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        }
        return removed;
    }

    /// <summary>
    /// Loads settings.json. Missing -> defaults (writable). Corrupt -> the bad file is copied to a .corrupt-&lt;timestamp&gt; sibling,
    /// the original is left in place, defaults are used for this run and a warning is returned (not writable).
    /// Unreadable (I/O / access error) -> defaults plus a warning (not writable). Never throws for these cases.
    /// </summary>
    public static SettingsLoadResult LoadSettings(string configPath,Func<LiveSettings> defaults)
    {
        DurableJsonLoad<LiveSettings> load;
        try
        {
            load=DurableFile.ReadJson<LiveSettings>(configPath);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            return new(defaults(),false,$"设置文件 {configPath} 无法读取（{ex.Message}），本次使用默认设置启动，原文件未改动",null);
        }
        if(load.Ok)return new(load.Value!,true,null,null);
        if(!load.Exists)return new(defaults(),false,null,null);
        return new(defaults(),false,$"设置文件 {configPath} 无法解析（{load.Error}），已备份到 {load.PreservedCorruptPath}，本次使用默认设置启动",load.PreservedCorruptPath);
    }

    private static readonly ConcurrentDictionary<string,SemaphoreSlim> SettingsGates=new(StringComparer.Ordinal);

    private static SemaphoreSlim SettingsGate(string configPath)=>
        SettingsGates.GetOrAdd(NormalizeDataRoot(configPath),static _=>new SemaphoreSlim(1,1));

    /// <summary>
    /// Serializes the entire read/modify/write callback, including snapshots used by PATCH, with every writer for this path.
    /// The callback must use the shared live settings object and must not enter another settings transaction.
    /// Return false to reject a change without publishing. Rejection or failure restores the same live object before releasing the gate.
    /// Async callers use the same gate, including across awaits. Side effects outside LiveSettings are the callback's responsibility.
    /// </summary>
    public static bool UpdateSettings(string configPath,LiveSettings settings,Func<bool> update,JsonSerializerOptions? options=null,bool persist=true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(update);
        var gate=SettingsGate(configPath);
        gate.Wait();
        try
        {
            var snapshot=new SettingsSnapshot(settings);
            var accepted=false;
            try
            {
                if(!update())return false;
                if(persist)WriteSettings(configPath,settings,options);
                accepted=true;
                return true;
            }
            finally{if(!accepted)snapshot.Restore(settings);}
        }
        finally{gate.Release();}
    }

    public static async Task<bool> UpdateSettingsAsync(string configPath,LiveSettings settings,Func<Task<bool>> update,JsonSerializerOptions? options=null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(update);
        var gate=SettingsGate(configPath);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot=new SettingsSnapshot(settings);
            var accepted=false;
            try
            {
                if(!await update().ConfigureAwait(false))return false;
                WriteSettings(configPath,settings,options);
                accepted=true;
                return true;
            }
            finally{if(!accepted)snapshot.Restore(settings);}
        }
        finally{gate.Release();}
    }

    /// <summary>
    /// Short read-only snapshot taken under the same per-path gate as every writer, so no writer is mid-callback.
    /// The callback must only copy values out of the live settings: no I/O, no awaits, no nested settings transaction.
    /// </summary>
    public static async Task<T> ReadSettingsAsync<T>(string configPath,Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var gate=SettingsGate(configPath);
        await gate.WaitAsync().ConfigureAwait(false);
        try{return read();}
        finally{gate.Release();}
    }

    private sealed class SettingsSnapshot
    {
        // LiveSettings consists of writable scalar/string properties and one mutable list. Keep list aliases intact too.
        private static readonly System.Reflection.PropertyInfo[] Properties=typeof(LiveSettings).GetProperties()
            .Where(property=>property.CanRead&&property.CanWrite).ToArray();
        private readonly object?[] _values;
        private readonly List<long>? _rooms;
        private readonly long[]? _roomIds;

        public SettingsSnapshot(LiveSettings settings)
        {
            _values=Properties.Select(property=>property.GetValue(settings)).ToArray();
            _rooms=settings.RoomIds;
            _roomIds=_rooms?.ToArray();
        }

        public void Restore(LiveSettings settings)
        {
            for(var i=0;i<Properties.Length;i++)Properties[i].SetValue(settings,_values[i]);
            if(_rooms is null)return;
            _rooms.Clear();
            _rooms.AddRange(_roomIds!);
        }
    }

    // Only this method publishes settings. Serialize first: a conversion failure leaves the destination untouched.
    private static void WriteSettings(string configPath,LiveSettings settings,JsonSerializerOptions? options)
    {
        var json=JsonSerializer.Serialize(settings,options);
        // Explicit user saves may repair a corrupt original, but only after preserving its exact bytes.
        // I/O and backup errors propagate; a file that could not be preserved must never be overwritten.
        _=DurableFile.ReadJson<LiveSettings>(configPath);
        DurableFile.WriteAllText(configPath,json);
    }

    /// <summary>
    /// Fail-closed schema guard: must run before Store.Migrate(). Throws StorageSchemaTooNewException for a database
    /// written by a newer build. Read-only: never creates, migrates or downgrades the file.
    /// </summary>
    public static void EnsureDatabaseSupported(string databasePath)=>StudioStore.EnsureSchemaSupported(databasePath);
}
