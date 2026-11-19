using System.Text.Json;
namespace LiveClipper.Core;

public sealed record SemanticCue(string Id,double Start,double End,string Text);
public sealed record SemanticCandidate(string Id,string[] CueIds,string? BeforeCueId=null,string? AfterCueId=null);
public sealed record SemanticReviewInput(SemanticCue[] Cues,SemanticCandidate[] Candidates);
public sealed record SemanticReviewEvidence(string CandidateId,SemanticCue[] Cues,SemanticCue? Before,SemanticCue? After);
public sealed record SemanticReviewSuggestion(string Id,SemanticReviewEvidence Left,SemanticReviewEvidence Right,string Reason,string[] ContextRisks,string[] LocalRiskHints,bool RequiresManualReview=true);
/// <summary>Offline semantic review contract. Never sends requests, deletes ranges or saves a project.</summary>
public static class SemanticReviewService
{
    private static readonly HashSet<string> Risks=new(StringComparer.Ordinal){"negation","numbers","subject","question-answer","new-information","intentional-repeat"};
    public static string BuildPrompt(SemanticReviewInput input)
    {
        Validate(input);
        return JsonSerializer.Serialize(input);
    }
    public static SemanticReviewSuggestion[] Parse(SemanticReviewInput input,string json)
    {
        Validate(input);
        using var doc=JsonDocument.Parse(json);
        RequireKeys(doc.RootElement,["suggestions"]);
        var rows=doc.RootElement.GetProperty("suggestions");
        if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>20)throw new ArgumentException("语义建议数量无效");
        var cues=input.Cues.ToDictionary(c=>c.Id,StringComparer.Ordinal);
        var results=new List<SemanticReviewSuggestion>();
        foreach(var row in rows.EnumerateArray())
        {
            RequireKeys(row,["leftCandidateId","rightCandidateId","leftCueIds","rightCueIds","reason","contextRisks"]);
            var leftEvidence=Evidence(row.GetProperty("leftCandidateId").GetString()!,ReadIds(row.GetProperty("leftCueIds")));
            var rightEvidence=Evidence(row.GetProperty("rightCandidateId").GetString()!,ReadIds(row.GetProperty("rightCueIds")));
            if(leftEvidence.Cues.Any(a=>rightEvidence.Cues.Any(b=>a.Id==b.Id||a.Start<b.End&&b.Start<a.End)))throw new ArgumentException("左右证据重叠");
            if(new[]{leftEvidence,rightEvidence}.Any(side=>side.Cues.Length==input.Candidates.Single(c=>c.Id==side.CandidateId).CueIds.Length))throw new ArgumentException("不得建议移除整个候选");

            var reason=row.GetProperty("reason");
            if(reason.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(reason.GetString())||reason.GetString()!.Length>1000)throw new ArgumentException("语义建议理由无效");
            var risks=ReadIds(row.GetProperty("contextRisks"),allowEmpty:true);
            if(risks.Any(r=>!Risks.Contains(r)))throw new ArgumentException("未知上下文风险标签");
            var local=new List<string>(risks);
            var left=string.Join("",leftEvidence.Cues.Select(c=>c.Text));var right=string.Join("",rightEvidence.Cues.Select(c=>c.Text));
            if(new[]{"不","没","无","未","别","not","never"}.Any(w=>left.Contains(w,StringComparison.OrdinalIgnoreCase)!=right.Contains(w,StringComparison.OrdinalIgnoreCase)))local.Add("negation");
            string Numbers(string text)=>string.Join("|",System.Text.RegularExpressions.Regex.Matches(text,@"[+\-−负正]?[0-9一二三四五六七八九十百千万零两]+(?:[.．][0-9]+)?[%％]?").Select(m=>m.Value));
            if(Numbers(left)!=Numbers(right))local.Add("numbers");
            if(new[]{"?","？","吗","为什么","怎么"}.Any(w=>left.Contains(w)||right.Contains(w)))local.Add("question-answer");
            results.Add(new(SuggestionId(leftEvidence,rightEvidence),leftEvidence,rightEvidence,reason.GetString()!,local.Distinct().ToArray(),local.Skip(risks.Length).Distinct().Select(r=>"本地启发式提示（非充分语义判断）："+r).ToArray()));
        }

        return results.ToArray();
        SemanticReviewEvidence Evidence(string candidateId,string[] ids)
        {
            if(ids.Length>3||ids.Any(id=>!cues.ContainsKey(id)))throw new ArgumentException("证据必须引用输入中的最多三句字幕");
            var owners=input.Candidates.Where(c=>c.Id==candidateId&&ids.All(id=>c.CueIds.Contains(id,StringComparer.Ordinal))).ToArray();
            if(owners.Length!=1)throw new ArgumentException("证据必须唯一属于一个候选");
            var owner=owners[0];var first=Array.IndexOf(owner.CueIds,ids[0]);
            if(!owner.CueIds.Skip(first).Take(ids.Length).SequenceEqual(ids))throw new ArgumentException("证据必须按顺序连续引用");
            var selected=ids.Select(id=>cues[id]).ToArray();
            if(selected[^1].End-selected[0].Start>15)throw new ArgumentException("证据跨度超过15秒");
            return new(owner.Id,selected,first>0?cues[owner.CueIds[first-1]]:owner.BeforeCueId is {} before?cues[before]:null,first+ids.Length<owner.CueIds.Length?cues[owner.CueIds[first+ids.Length]]:owner.AfterCueId is {} after?cues[after]:null);
        }
    }
    private static string SuggestionId(SemanticReviewEvidence left,SemanticReviewEvidence right)
    {
        var canonical=JsonSerializer.Serialize(new {leftCandidateId=left.CandidateId,leftCueIds=left.Cues.Select(c=>c.Id),rightCandidateId=right.CandidateId,rightCueIds=right.Cues.Select(c=>c.Id)});
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    private static string[] ReadIds(JsonElement e,bool allowEmpty=false)
    {
        if(e.ValueKind!=JsonValueKind.Array)throw new ArgumentException("证据或风险标签必须是数组");
        var values=e.EnumerateArray().Select(x=>x.ValueKind==JsonValueKind.String?x.GetString()!:throw new ArgumentException("证据ID必须为字符串")).ToArray();
        if((!allowEmpty&&values.Length==0)||values.Any(string.IsNullOrWhiteSpace)||values.Distinct(StringComparer.Ordinal).Count()!=values.Length)throw new ArgumentException("证据ID为空或重复");
        return values;
    }
    private static void RequireKeys(JsonElement e,string[] keys)
    {
        if(e.ValueKind!=JsonValueKind.Object||!e.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(keys.Order()))throw new ArgumentException("语义建议结构含缺失、重复或未支持字段");
    }
    private static void Validate(SemanticReviewInput input)
    {
        if(input.Cues.Length==0||input.Candidates.Length==0)throw new ArgumentException("请选择含源字幕的候选");
        if(input.Cues.Select(c=>c.Id).Distinct().Count()!=input.Cues.Length||input.Candidates.Select(c=>c.Id).Distinct().Count()!=input.Candidates.Length)throw new ArgumentException("输入ID重复");
        var cues=input.Cues.ToDictionary(c=>c.Id);
        if(input.Cues.Any(c=>string.IsNullOrWhiteSpace(c.Id)||!double.IsFinite(c.Start)||!double.IsFinite(c.End)||c.Start<0||c.End<=c.Start||string.IsNullOrWhiteSpace(c.Text)))throw new ArgumentException("源字幕无效");
        foreach(var candidate in input.Candidates)
        {
            if(string.IsNullOrWhiteSpace(candidate.Id)||candidate.CueIds.Length==0||candidate.CueIds.Distinct().Count()!=candidate.CueIds.Length||candidate.CueIds.Any(id=>!cues.ContainsKey(id)))throw new ArgumentException("候选引用无效");
            if(new[]{candidate.BeforeCueId,candidate.AfterCueId}.Any(id=>id is not null&&(!cues.ContainsKey(id)||candidate.CueIds.Contains(id))))throw new ArgumentException("候选外上下文引用无效");
            var selected=candidate.CueIds.Select(id=>cues[id]).ToArray();
            if(selected.Zip(selected.Skip(1)).Any(p=>p.First.Start>p.Second.Start||p.First.End>p.Second.End))throw new ArgumentException("候选字幕顺序无效");
        }
    }
}
