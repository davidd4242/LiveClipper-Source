using System.Text;

namespace LiveClipper.Core;

public sealed record HotWord(string Text,int Count,int UserCount);

public static class DanmakuHotWords
{
    public static IReadOnlyList<HotWord> Analyze(IEnumerable<LiveClipper.Domain.DanmakuEvent> events,int limit=8)
    {
        var counts=new Dictionary<string,(int Count,HashSet<long> Users)>(StringComparer.Ordinal);
        foreach(var item in events.Where(x=>x.Type=="danmaku"))
        {
            var candidates=new HashSet<string>(StringComparer.Ordinal);
            var token=new StringBuilder();
            void Flush()
            {
                var word=token.ToString();token.Clear();
                if(word.Length<2)return;
                if(word.Length<=12)candidates.Add(word);
                for(var size=2;size<=Math.Min(4,word.Length);size++)
                    for(var i=0;i+size<=word.Length;i++)candidates.Add(word.Substring(i,size));
            }
            foreach(var c in item.Text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            {
                if(char.IsLetterOrDigit(c))token.Append(c);
                else Flush();
            }
            Flush();
            foreach(var candidate in candidates)
            {
                if(!counts.TryGetValue(candidate,out var current))current=(0,new HashSet<long>());
                current.Users.Add(item.Uid);
                counts[candidate]=(current.Count+1,current.Users);
            }
        }
        var ranked=counts.Where(x=>x.Value.Count>=2 && x.Value.Users.Count>=2)
            .Select(x=>new HotWord(x.Key,x.Value.Count,x.Value.Users.Count))
            .OrderByDescending(x=>x.Count).ThenByDescending(x=>x.UserCount).ThenByDescending(x=>x.Text.Length).ThenBy(x=>x.Text,StringComparer.Ordinal);
        var result=new List<HotWord>();
        foreach(var word in ranked)
        {
            if(result.Any(x=>x.Text.Contains(word.Text,StringComparison.Ordinal) && x.Count==word.Count))continue;
            result.Add(word);
            if(result.Count>=limit)break;
        }
        return result;
    }
}
