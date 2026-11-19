using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
namespace LiveClipper.Core;
public sealed class SemanticInputLimitException(string message):ArgumentException(message);
public sealed record SemanticSelection(string Id,double Start,double End);
public sealed record SemanticAnalysisResult(SemanticReviewSuggestion[] Suggestions,string SourceSignature,string CandidateHash,string TranscriptCacheKey,string SelectionHash);
public static class SemanticReviewAnalysis
{
    public static async Task<SemanticAnalysisResult> AnalyzeAsync(RoughCutPreview preview,SemanticSelection[] selection,bool confirm,string sourceSignature,string candidateHash,string transcriptCacheKey,AiConnection connection,CancellationToken token,HttpClient? transport=null)
    {
        if(connection.Provider is not ("compatible" or "ollama"))throw new ArgumentException("语义复核仅支持 compatible 或本机 Ollama");
        if(connection.Provider=="ollama")
        {
            var endpoint=AiAnalysisService.Endpoint(connection);
            if(!endpoint.IsLoopback||endpoint.Scheme is not ("http" or "https")||endpoint.UserInfo.Length>0)throw new ArgumentException("本地语义复核仅允许本机回环 Ollama 地址");
            if(connection.Model.Contains("cloud",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("本地语义复核不允许 Ollama 云端模型");
        }
        if(!confirm)throw new ArgumentException("请先确认发送已选字幕及可能产生的服务商费用");
        if(preview.SourceSignature!=sourceSignature||preview.CandidateHash!=candidateHash||preview.TranscriptCacheKey!=transcriptCacheKey)throw new InvalidOperationException("源素材、字幕或候选已变化，请重新预览");
        if(selection.Length is <1 or >10||selection.Select(s=>s.Id).Distinct().Count()!=selection.Length)throw new ArgumentException("请选择1至10个不同候选");
        var selectedCues=new Dictionary<string,SemanticCue>();var candidates=new List<SemanticCandidate>();
        foreach(var selected in selection)
        {
            if(!preview.Candidates.Any(c=>c.Id==selected.Id&&c.ClipId==preview.SourceId)||!double.IsFinite(selected.Start)||!double.IsFinite(selected.End)||selected.Start<0||selected.End<=selected.Start||selected.End>preview.Duration)throw new ArgumentException("候选引用或手调范围无效");
            var cues=preview.Transcript.Select((c,i)=>(c,i)).Where(x=>x.c.Start>=selected.Start&&x.c.End<=selected.End).Select(x=>new SemanticCue("cue-"+x.i,x.c.Start,x.c.End,x.c.Text)).ToArray();
            if(cues.Length<2)throw new ArgumentException("每个候选至少需要两句完整源字幕，避免整段删除");
            foreach(var cue in cues)selectedCues[cue.Id]=cue;
            var firstIndex=int.Parse(cues[0].Id[4..]);
            var lastIndex=int.Parse(cues[^1].Id[4..]);
            string? AddContext(int index)
            {
                if(index<0||index>=preview.Transcript.Length)return null;
                var c=preview.Transcript[index];var id="cue-"+index;selectedCues[id]=new(id,c.Start,c.End,c.Text);return id;
            }
            candidates.Add(new(selected.Id,cues.Select(c=>c.Id).ToArray(),AddContext(firstIndex-1),AddContext(lastIndex+1)));
        }
        if(selectedCues.Count>200)throw new ArgumentException("一次最多发送200句字幕，请减少所选片段");
        var input=new SemanticReviewInput(selectedCues.Values.OrderBy(c=>c.Start).ToArray(),candidates.ToArray());
        var prompt=SemanticReviewService.BuildPrompt(input);
        var systemPrompt=AiSkillText.SemanticReviewSystemPrompt;
        if(connection.Provider=="ollama"&&Encoding.UTF8.GetByteCount(systemPrompt)+Encoding.UTF8.GetByteCount(prompt)>24000)throw new SemanticInputLimitException("本地语义输入（含完整技能）最多24000 UTF8字节，请缩小范围；不会截断输入或转云端");
        if(systemPrompt.Length+prompt.Length>40000)throw new ArgumentException("一次提示文本最多40000字符，请减少所选片段");
        using var localClient=connection.Provider=="ollama"&&transport is null?new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(4)}:null;
        var json=await AiAnalysisService.AskAsync(connection,prompt,token,transport??localClient,systemPrompt,attemptLimit:1,localOutputLimit:connection.Provider=="ollama"?4000:null,localContextLimit:connection.Provider=="ollama"?32768:null);
        var result=SemanticReviewService.Parse(input,json);
        var selectionHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(selection)))).ToLowerInvariant();
        return new(result,sourceSignature,candidateHash,transcriptCacheKey,selectionHash);
    }
}
