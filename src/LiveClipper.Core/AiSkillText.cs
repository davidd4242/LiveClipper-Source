using System.Security.Cryptography;
using System.Text;

namespace LiveClipper.Core;

/// <summary>Embedded in-app AI skill prompts. Resources are fixed; nothing is read from disk at runtime.</summary>
public static class AiSkillText
{
    private sealed record Skill(string Id,string Version,string SystemPrompt);

    private const string SubtitleId="subtitle-analysis";
    private const string SemanticReviewId="semantic-review";
    private static readonly Lazy<Skill> CutPlan=new(()=>Load("cut-plan","LiveClipper.Core.AiSkills.cut-plan.SKILL.md"),LazyThreadSafetyMode.ExecutionAndPublication);
    public static string CutPlanSystemPrompt=>CutPlan.Value.SystemPrompt;
    public static string CutPlanVersion=>CutPlan.Value.Version;

    private const string RoughCutId="rough-cut";

    private static readonly Lazy<Skill> Subtitle=new(()=>Load(SubtitleId,"LiveClipper.Core.AiSkills.subtitle-analysis.SKILL.md"),LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Skill> RoughCut=new(()=>Load(RoughCutId,"LiveClipper.Core.AiSkills.rough-cut.SKILL.md"),LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Skill> SemanticReview=new(()=>Load(SemanticReviewId,"LiveClipper.Core.AiSkills.semantic-review.SKILL.md"),LazyThreadSafetyMode.ExecutionAndPublication);
    public static string SemanticReviewSystemPrompt=>SemanticReview.Value.SystemPrompt;
    public static string SemanticReviewVersion=>SemanticReview.Value.Version;

    public static string SubtitleAnalysisSystemPrompt=>Subtitle.Value.SystemPrompt;
    public static string SubtitleAnalysisVersion=>Subtitle.Value.Version;
    public static string RoughCutSystemPrompt=>RoughCut.Value.SystemPrompt;
    public static string RoughCutVersion=>RoughCut.Value.Version;

    private static Skill Load(string id,string resource)
    {
        using var stream=typeof(AiSkillText).Assembly.GetManifestResourceStream(resource)
            ??throw new InvalidOperationException($"缺少内嵌技能资源：{resource}");
        using var reader=new StreamReader(stream,new UTF8Encoding(false),true);
        var raw=reader.ReadToEnd().TrimStart('\uFEFF').Replace("\r\n","\n").Replace('\r','\n');
        var body=StripFrontmatter(raw,resource).Trim('\n',' ','\t');
        if(body.Length==0)throw new InvalidOperationException($"技能正文为空：{resource}");
        var hash=Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(body))).ToLowerInvariant();
        var version=$"{id}@sha256:{hash}";
        return new(id,version,$"[技能 {id} 版本 {version}]\n\n{body}");
    }

    private static string StripFrontmatter(string text,string resource)
    {
        if(!text.StartsWith("---\n",StringComparison.Ordinal)&&text!="---")return text;
        var lines=text.Split('\n');
        for(var i=1;i<lines.Length;i++)
            if(lines[i].TrimEnd()=="---")return string.Join('\n',lines.Skip(i+1));
        throw new InvalidOperationException($"技能 frontmatter 未闭合：{resource}");
    }
}
