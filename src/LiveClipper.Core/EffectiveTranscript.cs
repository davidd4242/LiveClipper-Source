using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveClipper.Core;

public sealed record EffectiveTranscriptResult(TranscriptSegment[] Segments,string Source,string Digest,string CacheKey,string CanonicalJson);

public static class EffectiveTranscript
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);

    public static EffectiveTranscriptResult Resolve(EditorProject? project,string sourceId,string? transcriptJson)
    {
        var source="original";
        TranscriptSegment[] segments;
        if(project?.CaptionOverrides is { } overrides&&overrides.TryGetValue(sourceId,out var edited))
        {
            source="override";
            segments=EditorRenderService.NormalizeCaptions(edited);
        }
        else
        {
            try{segments=string.IsNullOrWhiteSpace(transcriptJson)?[]:EditorRenderService.NormalizeCaptions(TranscriptExport.Parse(transcriptJson).Segments);}
            catch(JsonException){segments=[];}
            catch(ArgumentException){segments=[];}
            catch(InvalidOperationException){segments=[];}
            catch(NotSupportedException){segments=[];}
            catch(KeyNotFoundException){segments=[];}
            catch(FormatException){segments=[];}
            catch(OverflowException){segments=[];}
        }
        var canonicalJson=JsonSerializer.Serialize(new {language="",text=string.Join("\n",segments.Select(x=>x.Text)),segments},JsonOptions);
        var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
        var cacheKey=$"effective-transcript-v1:{sourceId.Length}:{sourceId}:{source}:{digest}";
        return new(segments,source,digest,cacheKey,canonicalJson);
    }

    public static EffectiveTranscriptResult FromProjectJson(string? savedProjectJson,string sourceId,string? transcriptJson)
    {
        EditorProject? project=null;
        if(!string.IsNullOrWhiteSpace(savedProjectJson))
        {
            try{project=JsonSerializer.Deserialize<EditorProject>(savedProjectJson,JsonOptions);}
            catch(JsonException){}
            catch(NotSupportedException){}
        }
        return Resolve(project,sourceId,transcriptJson);
    }
}
