using System.Globalization;
using LiveClipper.Domain;

namespace LiveClipper.Core;

internal static class CutPlanBatches
{
    // Bound both dense short subtitles and long dialogue. Never cut a cue or renumber it.
    internal const int MaxCues=16;
    internal const int MaxCharacters=1800;
    internal static string Line(TranscriptSegment segment,int index)=>
        FormattableString.Invariant($"{index} | {segment.Start:0.###}-{segment.End:0.###} | {segment.Text}");

    internal static object Metadata(AiConnection connection,(TranscriptSegment Segment,int Index)[] batch,
        int batchNumber,int batchCount,int inputCharacters,int promptCharacters)=>new
    {
        batchNumber,batchCount,cueCount=batch.Length,firstIndex=batch[0].Index,lastIndex=batch[^1].Index,
        inputCharacters,promptCharacters,provider=connection.Provider,model=connection.Model,
        maxCues=connection.Provider=="ollama"?MaxCues:80,
        maxInputCharacters=connection.Provider=="ollama"?(int?)MaxCharacters:null,
        outputTokens=connection.Provider=="ollama"?1024:4000,
        contextTokens=connection.Provider=="ollama"?(int?)8192:null
    };

    internal static IReadOnlyList<(TranscriptSegment Segment,int Index)[]> Create(IEnumerable<(TranscriptSegment Segment,int Index)> cues)
    {
        var result=new List<(TranscriptSegment Segment,int Index)[]>();
        var batch=new List<(TranscriptSegment Segment,int Index)>();
        var characters=0;
        foreach(var cue in cues)
        {
            var length=Line(cue.Segment,cue.Index).Length+1;
            // A cue cannot be split without changing the keep-index contract. Report the
            // unsupported input before any request instead of silently losing its text.
            if(length>MaxCharacters)throw new ArgumentException($"字幕第 {cue.Index} 条超过单批 {MaxCharacters} 字符预算，请先拆分此条字幕；未截断或提交任何字幕。");
            if(batch.Count==MaxCues||characters+length>MaxCharacters)
            {
                result.Add(batch.ToArray());batch.Clear();characters=0;
            }
            batch.Add(cue);characters+=length;
        }
        if(batch.Count>0)result.Add(batch.ToArray());
        return result;
    }
}
