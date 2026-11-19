using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveClipper.Domain;
namespace LiveClipper.Core;
public sealed record LiteralRepeatHint(int FirstIndex,int SecondIndex,string Text,string Reason);
public sealed record RoughCutPreview(string Mode,string SourceId,string SourceSignature,string CandidateHash,string TranscriptCacheKey,double Duration,StudioEvent[] Candidates,TranscriptSegment[] Transcript,LiteralRepeatHint[] RepeatHints,string[] Warnings);
/// <summary>Existing candidates and literal text evidence only; no model calls or writes.</summary>
public sealed class RoughCutPreviewService(Store store,StudioStore studio)
{
    public RoughCutPreview Preview(Clip clip,EditorProject project,double duration)
    {
        var fallback=store.TranscriptJson(clip.EventId);
        var transcript=ResolveTranscript(project,clip.Id,fallback);
        var candidates=studio.Events(clip.Id).OrderBy(x=>x.Start).ThenBy(x=>x.Id,StringComparer.Ordinal).ToArray();
        return new("cached-candidates-and-text-rules",clip.Id,SourceSilencePreviewService.Signature(clip.Path),Hash(candidates),Hash(new {sourceId=clip.Id,transcript}),duration,candidates,transcript,FindRepeats(transcript),
            ["这些是历史已存候选，尚未证明与当前字幕一致，请核对原文和边界。","重复提示仅比较邻近字幕的字面文本，不是 AI 语义去重；不会默认删除任何片段。","本次预览不调用 AI，也不修改工程。确认追加及撤销会沿用编辑器自动保存。"]);
    }
    internal static TranscriptSegment[] ResolveTranscript(EditorProject project,string sourceId,string? fallback)
    {
        if(project.CaptionOverrides?.TryGetValue(sourceId,out var edited)==true)return EditorRenderService.NormalizeCaptions(edited);
        if(project.SourceTranscripts?.TryGetValue(sourceId,out var frozen)==true)return frozen.ToArray();
        return fallback is null?[]:TranscriptExport.Parse(fallback).Segments.ToArray();
    }
    internal static LiteralRepeatHint[] FindRepeats(TranscriptSegment[] transcript)
    {
        var result=new List<LiteralRepeatHint>();
        for(var j=0;j<transcript.Length;j++)
        {
            var text=transcript[j].Text.Trim();
            if(text.Length<4)continue;
            for(var i=j-1;i>=0;i--)
            {
                var delta=transcript[j].Start-transcript[i].Start;
                if(delta<0||delta>30)continue;
                if(string.Equals(transcript[i].Text.Trim(),text,StringComparison.Ordinal))
                {result.Add(new(i,j,text,"30 秒内逐字相同（仅忽略首尾空白）；可能是强调或回应，请人工核对"));break;}
            }
        }
        return result.ToArray();
    }
    private static string Hash<T>(T value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
}
