using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LiveClipper.Domain;

namespace LiveClipper.Core;

/// <summary>A proposed item change, including the unchanged items which protect manual edits.</summary>
public sealed record AutoEditProposalChange(string Kind,string ItemId,TimelineItem? Before,TimelineItem? After,string Reason);

/// <summary>Parsed transcript indices are local to TranscriptHash; they must never be reused after that hash changes.</summary>
public sealed record AutoEditSegmentReason(int SegmentIndex,double Start,double End,bool Keep,string Reason);

/// <summary>
/// An in-memory preview, not a saved editor project. Timeline is the complete proposed timeline in order;
/// Changes explains each item. Only an explicit ApplyProposal may install it against the captured baseline.
/// </summary>
public sealed record AutoEditProposal(
    string ProposalId,string ClipId,string Epoch,int BaseRevision,string BaseHash,string TranscriptHash,
    string Source,TimelineItem[] Timeline,AutoEditProposalChange[] Changes,AutoEditSegmentReason[] SegmentReasons,
    DateTimeOffset CreatedAt);

/// <summary>Applied=false never writes. Conflict=true means the caller must refresh/recompute, not retry the old proposal.</summary>
public sealed record AutoEditProposalApplyResult(bool Applied,bool Conflict,string Code,int Revision,string? Error=null);

internal static class AutoEditProposalPlanning
{
    // Missing rows hash the empty string, which cannot be a valid stored project/transcript JSON document.
    internal static string Hash(string? json)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json??"")));

    internal static EditorProject ReadProject(string? json,double end)
    {
        if(json is null)return new EditorProject{Start=0,End=end};
        return EditorProjectCodec.Deserialize(json);
    }

    internal static AutoEditProposal Generate(Clip clip,StoredEditorProject? baseline,string epoch,string transcriptJson,CancellationToken token)
    {
        var segments=TranscriptExport.Parse(transcriptJson).Segments;
        var reasons=segments.Select((segment,index)=>
        {
            token.ThrowIfCancellationRequested();
            var valid=double.IsFinite(segment.Start)&&double.IsFinite(segment.End)&&segment.Start>=0&&segment.End>segment.Start;
            var music=Regex.IsMatch(segment.Text??"",@"^[♪♫]|^\s*\[(?:音乐|唱歌|Music)\]",RegexOptions.IgnoreCase);
            var keep=valid&&!music&&SubtitleTools.Clean([segment]).Count>0;
            var reason=!valid?"字幕时间无效，不用于剪辑":music?"明确的音乐标记":!keep?"独立口头禅或空文本":"保留有效叙述；规则方案需人工试听";
            return new AutoEditSegmentReason(index,segment.Start,segment.End,keep,reason);
        }).ToArray();
        var end=reasons.Where(x=>double.IsFinite(x.End)&&x.End>0).Select(x=>x.End).DefaultIfEmpty(0).Max();
        if(!(end>0))throw new ArgumentException("尚无可生成方案的有效字幕");
        var project=ReadProject(baseline?.Json,end);
        EditorRenderService.Validate(project);
        var before=EditorRenderService.Items(project,clip.Id);
        var protectedIds=ProtectedItemIds(project,clip.Id);
        var keptIndices=reasons.Where(x=>x.Keep).Select(x=>x.SegmentIndex).ToArray();
        var timeline=new List<TimelineItem>();
        var changes=new List<AutoEditProposalChange>();
        foreach(var item in before)
        {
            token.ThrowIfCancellationRequested();
            // Non-primary, linked, nested, locked and transition-bound items are not raw transcript ranges.
            if(protectedIds.Contains(item.Id)||item.ClipId!=clip.Id||EditorRenderService.TrackOf(item)!=1||item.Kind!="video"||item.Disabled)
            {
                timeline.Add(item);
                changes.Add(new("preserve",item.Id,item,item,"保留手工结构、转场锚点、嵌套引用、锁定轨、非主素材或多轨同步关系"));
                continue;
            }
            var pieces=EditorRenderService.SentenceTimeline(clip.Id,segments,keptIndices,item.Start,item.End);
            if(pieces.Length==0)
            {
                changes.Add(new("remove",item.Id,item,null,"此片段内没有需要保留的有效字幕；仅提议移除，应用前请试听"));
                continue;
            }
            for(var index=0;index<pieces.Length;index++)
            {
                var piece=pieces[index];
                var next=item with {Id=index==0?item.Id:piece.Id,Start=piece.Start,End=piece.End,GapBefore=index==0?item.GapBefore:0};
                timeline.Add(next);
                var unchanged=index==0&&SameItem(item,next);
                changes.Add(new(index>0?"add":unchanged?"keep":"trim",next.Id,index==0?item:null,next,
                    unchanged?"字幕覆盖整个片段，保持原样":"按保留字幕的源秒范围裁剪；保留原片段效果与速度"));
            }
        }
        var proposed=project with {Timeline=timeline.ToArray()};
        // Never discard markers, effects or transitions merely to make an otherwise invalid proposal saveable.
        EditorRenderService.Validate(proposed);
        EditorRenderService.Validate(EditorRenderService.FlattenProject(proposed));
        return new(Guid.NewGuid().ToString("N"),clip.Id,epoch,checked((int)(baseline?.Revision??0)),Hash(baseline?.Json),Hash(transcriptJson),
            "rules",proposed.Timeline!,changes.ToArray(),reasons,DateTimeOffset.UtcNow);
    }

    internal static HashSet<string> ProtectedItemIds(EditorProject project,string clipId)
    {
        var items=EditorRenderService.Items(project,clipId);
        var locked=(project.LockedTracks??[]).ToHashSet();
        // A raw source-time cut cannot retime overlays/audio or an opaque command archive safely.
        var synchronized=items.Any(x=>EditorRenderService.TrackOf(x)!=1)||project.ReplicaExtension is not null;
        var ids=items.Where(x=>synchronized||x.ClipId!=clipId||x.Kind!="video"||x.Disabled
                ||!string.IsNullOrEmpty(x.NestedSequenceId)||locked.Contains(EditorRenderService.TrackOf(x))
                ||!string.IsNullOrEmpty(x.LinkGroup)||x.AudioDetached||x.FadeIn>0||x.FadeOut>0
                ||x.VolumeKeyframes is {Length:>0}||x.Keyframes is {Count:>0})
            .Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var transition in project.Transitions??[])
        {
            var right=Array.FindIndex(items,x=>x.Id==transition.ItemId);
            if(right<0)continue;
            ids.Add(items[right].Id);
            var track=EditorRenderService.TrackOf(items[right]);
            for(var left=right-1;left>=0;left--)
                if(EditorRenderService.TrackOf(items[left])==track){ids.Add(items[left].Id);break;}
        }
        return ids;
    }

    internal static bool PreservesProtectedItems(EditorProject project,string clipId,TimelineItem[] proposed)
    {
        var before=EditorRenderService.Items(project,clipId);
        foreach(var id in ProtectedItemIds(project,clipId))
        {
            var original=before.Single(x=>x.Id==id);
            var next=proposed.FirstOrDefault(x=>x.Id==id);
            if(next is null||!SameItem(original,next))return false;
        }
        foreach(var transition in project.Transitions??[])
        {
            var right=before.FirstOrDefault(x=>x.Id==transition.ItemId);
            if(right is null)continue;
            var track=EditorRenderService.TrackOf(right);
            string? PreviousId(TimelineItem[] items)
            {
                var sameTrack=items.Where(x=>EditorRenderService.TrackOf(x)==track).ToArray();
                var index=Array.FindIndex(sameTrack,x=>x.Id==transition.ItemId);
                return index>0?sameTrack[index-1].Id:null;
            }
            if(PreviousId(before)!=PreviousId(proposed))return false;
        }
        return true;
    }

    internal static bool SameItem(TimelineItem before,TimelineItem after)=>JsonSerializer.Serialize(before)==JsonSerializer.Serialize(after);

    internal static bool SameTimeline(TimelineItem[] before,TimelineItem[] after)=>JsonSerializer.Serialize(before)==JsonSerializer.Serialize(after);

    /// <summary>Patch only Timeline in the stored JSON so opaque/unrelated root fields survive an apply.</summary>
    internal static string InstallTimeline(string? saved,EditorProject next)
    {
        if(saved is null)return JsonSerializer.Serialize(next);
        var root=JsonNode.Parse(saved) as JsonObject??throw new ArgumentException("工程不是有效对象");
        var keys=root.Select(x=>x.Key).Where(x=>string.Equals(x,nameof(EditorProject.Timeline),StringComparison.OrdinalIgnoreCase)).ToArray();
        if(keys.Length>1)throw new ArgumentException("工程包含重复的 Timeline 字段");
        root[keys.SingleOrDefault()??nameof(EditorProject.Timeline)]=JsonSerializer.SerializeToNode(next.Timeline);
        return root.ToJsonString();
    }
}
