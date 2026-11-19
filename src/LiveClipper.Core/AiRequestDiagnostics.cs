using System.Diagnostics;
using System.Text.Json;

namespace LiveClipper.Core;

// Fixed metadata only. Never log model prompts, response text, endpoint queries or credentials.
public static class AiRequestDiagnostics
{
    private static readonly object Gate=new();
    private static string? logPath;
    public static void ConfigureFile(string? path)
    {
        lock(Gate)
        {
            if(path is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            logPath=path;
        }
    }
    internal static void Write(string requestId,string stage,object detail)
    {
        var record=JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,traceId=Activity.Current?.TraceId.ToString(),requestId,stage,detail});
        lock(Gate)
        {
            if(logPath is not null) File.AppendAllText(logPath,record+Environment.NewLine);
            Console.WriteLine("ai-request "+record);
        }
    }
    internal static void WriteUsage(string requestId,JsonElement root)
    {
        var present=root.TryGetProperty("usage",out var usage)&&usage.ValueKind==JsonValueKind.Object;
        AiRequestDiagnostics.Write(requestId,"compatible_usage",new {
            available=present,
            promptTokens=present?Duration(usage,"prompt_tokens"):null,
            completionTokens=present?Duration(usage,"completion_tokens"):null,
            totalTokens=present?Duration(usage,"total_tokens"):null,
            promptCacheHitTokens=present?Duration(usage,"prompt_cache_hit_tokens"):null,
            promptCacheMissTokens=present?Duration(usage,"prompt_cache_miss_tokens"):null});
    }
    internal static long? Duration(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out var duration)&&duration>=0?duration:null;
}
