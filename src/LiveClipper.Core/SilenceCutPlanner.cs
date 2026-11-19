namespace LiveClipper.Core;

/// <summary>Source-time low-energy evidence, constrained by coarse caption protection; never word alignment.</summary>
public static class SilenceCutPlanner
{
    public const double NoiseDb=-32,MinSeconds=.6,Padding=.12,MinimumCutSeconds=.2;
    public const string Version="voice-activity-pause-v5";
    public static string TranscriptKey(string key,int fps)=>$"{key}:{Version}:fps={fps}";
    public static SourceSilenceCandidate[] Plan(IEnumerable<SilenceRange> ranges,IReadOnlyList<TranscriptSegment>? transcript,int fps=30,double padding=Padding)
    {
        var captions=(transcript??[]).Where(x=>double.IsFinite(x.Start)&&double.IsFinite(x.End)&&x.Start>=0&&x.End>x.Start&&!string.IsNullOrWhiteSpace(x.Text)).OrderBy(x=>x.Start).ToArray();
        var frameRate=Math.Max(1,fps);
        var result=new List<SourceSilenceCandidate>();
        foreach(var range in ranges.Where(x=>double.IsFinite(x.Start)&&double.IsFinite(x.End)&&x.End>x.Start).OrderBy(x=>x.Start))
        {
            var guard=Math.Max(Padding,padding);
            var rawStart=range.Start+guard;var rawEnd=range.End-guard;
            var pieces=new List<(double Start,double End)>{(rawStart,rawEnd)};
            foreach(var caption in captions)
            {
                var protectStart=caption.Start-guard;var protectEnd=caption.End+guard;
                var next=new List<(double Start,double End)>();
                foreach(var piece in pieces)
                {
                    if(protectEnd<=piece.Start||protectStart>=piece.End){next.Add(piece);continue;}
                    if(protectStart>piece.Start)next.Add((piece.Start,protectStart));
                    if(protectEnd<piece.End)next.Add((protectEnd,piece.End));
                }
                pieces=next;
            }
            var originId=FormattableString.Invariant($"silence-{range.Start:R}-{range.End:R}");
            if(pieces.Count==0)
            {
                result.Add(new(originId,range.Start,range.End,rawStart,rawEnd,false,["subtitle-overlap"]));
                continue;
            }
            for(var index=0;index<pieces.Count;index++)
            {
                var piece=pieces[index];
                var start=Math.Ceiling(piece.Start*frameRate-1e-9)/frameRate;
                var end=Math.Floor(piece.End*frameRate+1e-9)/frameRate;
                var risks=new List<string>();
                if(end-start+1e-9<MinimumCutSeconds)risks.Add("insufficient-cut");
                if(captions.Length==0)risks.Add("transcript-unavailable");
                else if(!captions.Any(x=>x.End<=start&&start-x.End<=2)
                    ||!captions.Any(x=>x.Start>=end&&x.Start-end<=2))risks.Add("transcript-coverage-uncertain");
                var id=pieces.Count==1?originId:$"{originId}-part-{index}";
                result.Add(new(id,range.Start,range.End,start,end,risks.Count==0,risks.ToArray()));
            }
        }
        return result.ToArray();
    }
    public static SourceSilenceCandidate[] PlanVoice(VoiceActivityEvidence evidence,IReadOnlyList<TranscriptSegment>? transcript,int fps=30,double minSeconds=MinSeconds)
    {
        var speech=VoiceActivityPauseService.Merge(evidence.Speech);
        var captions=(transcript??[]).Where(x=>double.IsFinite(x.Start)&&double.IsFinite(x.End)&&x.Start>=0&&x.End>x.Start&&!string.IsNullOrWhiteSpace(x.Text)).OrderBy(x=>x.Start).ToArray();
        // Sentence captions cannot locate words, but mostly-unvoiced text is contradictory evidence.
        // Count weak voice as protected evidence too; ordinary sentence-interior pauses remain valid.
        var voiceCoverage=VoiceActivityPauseService.Merge(evidence.Speech.Concat(evidence.Uncertain));
        // 50% is a conservative review heuristic, not a model probability or calibrated confidence.
        var conflictedCaptions=captions.Select(c=>new TranscriptSegment(Math.Max(c.Start,evidence.SourceStart),Math.Min(c.End,evidence.SourceEnd),c.Text)).Where(c=>c.End>c.Start).Select(c=>{
            var seconds=voiceCoverage.Sum(v=>Math.Max(0,Math.Min(v.End,c.End)-Math.Max(v.Start,c.Start)));
            return new CaptionVoiceConflict(c.Start,c.End,seconds,seconds/(c.End-c.Start));
        }).Where(c=>c.CoverageRatio<.5-1e-9).ToArray();
        var gaps=new List<SilenceRange>();var cursor=evidence.SourceStart;
        foreach(var voice in speech)
        {
            if(voice.Start>cursor)gaps.Add(new(cursor,Math.Min(voice.Start,evidence.SourceEnd)));
            cursor=Math.Max(cursor,voice.End);
        }
        if(cursor<evidence.SourceEnd)gaps.Add(new(cursor,evidence.SourceEnd));
        var result=new List<SourceSilenceCandidate>();
        foreach(var gap in gaps.Where(x=>x.Seconds+1e-9>=minSeconds))
        {
            var risks=new List<string>();
            if(!speech.Any(x=>Math.Abs(x.End-gap.Start)<.001)||!speech.Any(x=>Math.Abs(x.Start-gap.End)<.001))risks.Add("no-speech-context");
            if(evidence.Gaps.Any(x=>x.End-x.Start>.100001&&x.Start<gap.End&&x.End>gap.Start))risks.Add("pts-gap");
            // Entire text cue without detected voice is a conflict; an internal pause in a long cue is not.
            if(captions.Any(x=>x.Start>=gap.Start&&x.End<=gap.End))risks.Add("caption-vad-conflict");
            var pieces=new List<SilenceRange>{new(gap.Start+Padding,gap.End-Padding)};
            var protection=evidence.Uncertain.Select(x=>new SilenceRange(x.Start-Padding,x.End+Padding))
                .Concat(captions.SelectMany(x=>new[]{new SilenceRange(x.Start-Padding,x.Start+Padding),new SilenceRange(x.End-Padding,x.End+Padding)}));
            foreach(var zone in protection)
            {
                var next=new List<SilenceRange>();
                foreach(var piece in pieces)
                {
                    if(zone.End<=piece.Start||zone.Start>=piece.End){next.Add(piece);continue;}
                    if(zone.Start>piece.Start)next.Add(new(piece.Start,zone.Start));
                    if(zone.End<piece.End)next.Add(new(zone.End,piece.End));
                }
                pieces=next;
            }
            if(pieces.Count==0)
            {
                var reason=evidence.Uncertain.Any(x=>x.Start<gap.End&&x.End>gap.Start)?"uncertain-speech":"caption-vad-conflict";
                result.Add(new(FormattableString.Invariant($"voice-gap-{gap.Start:R}-{gap.End:R}-protected"),gap.Start,gap.End,gap.Start+Padding,gap.End-Padding,false,risks.Append(reason).ToArray()));
                continue;
            }
            // Do not create two jumps around a tiny protected island inside the same VAD pause.
            // Default to review for the group; only one long caption-gap piece with dual evidence
            // may be released below. Never erase the protected island to merge cuts.
            var fragmented=pieces.Zip(pieces.Skip(1),(left,right)=>right.Start-left.End)
                .Any(kept=>kept>=0&&kept<MinSeconds-1e-9);
            var quantized=pieces.Select(piece=>new SilenceRange(
                Math.Ceiling(piece.Start*Math.Max(1,fps)-1e-9)/Math.Max(1,fps),
                Math.Floor(piece.End*Math.Max(1,fps)+1e-9)/Math.Max(1,fps))).ToArray();
            // A weak-voice island is never eligible for this exception. Endpoint uncertainty
            // still trims the cut normally; only interior protected islands are considered here.
            var weakIsland=pieces.Zip(pieces.Skip(1),(left,right)=>(Start:left.End,End:right.Start))
                .Any(island=>evidence.Uncertain.Any(v=>v.Start-Padding<island.End-1e-9&&v.End+Padding>island.Start+1e-9));
            bool ReliableCaption(TranscriptSegment caption)
            {
                var from=Math.Max(caption.Start,evidence.SourceStart);var to=Math.Min(caption.End,evidence.SourceEnd);
                if(to<=from||!speech.Any(v=>v.Start<to&&v.End>from))return false;
                var voiced=voiceCoverage.Sum(v=>Math.Max(0,Math.Min(v.End,to)-Math.Max(v.Start,from)));
                return voiced/(to-from)>=.5-1e-9;
            }
            bool SupportedCaptionGap(SilenceRange cut)
            {
                if(cut.Seconds+1e-9<MinSeconds||captions.Any(c=>c.Start<cut.End&&c.End>cut.Start))return false;
                var before=captions.Where(c=>c.End<=cut.Start).OrderByDescending(c=>c.End).FirstOrDefault();
                var after=captions.Where(c=>c.Start>=cut.End).OrderBy(c=>c.Start).FirstOrDefault();
                return before is not null&&after is not null
                    &&cut.Start-before.End<=2&&after.Start-cut.End<=2
                    &&cut.Start+1e-9>=before.End+Padding&&cut.End-1e-9<=after.Start-Padding
                    &&ReliableCaption(before)&&ReliableCaption(after);
            }
            // At most one sufficiently long, fully caption-free piece may be cut. Never
            // merge across the protected island, and never release a sentence-interior piece.
            var singleGapIndex=fragmented&&risks.Count==0&&!weakIsland
                ?quantized.Select((cut,index)=>(Cut:cut,Index:index)).Where(x=>SupportedCaptionGap(x.Cut))
                    .OrderByDescending(x=>x.Cut.Seconds).ThenBy(x=>x.Cut.Start).Select(x=>x.Index).DefaultIfEmpty(-1).First()
                :-1;
            for(var i=0;i<pieces.Count;i++)
            {
                var local=new List<string>(risks);var start=quantized[i].Start;var end=quantized[i].End;
                if(fragmented&&i!=singleGapIndex)local.Add("fragmented-pause-review");
                if(end-start+1e-9<MinimumCutSeconds)local.Add("insufficient-cut");
                if(conflictedCaptions.Any(c=>c.Start<end&&c.End>start)&&!local.Contains("caption-vad-conflict"))local.Add("caption-vad-conflict");
                if(captions.Any(x=>Math.Abs(x.Start-(start+end)/2)<Padding||Math.Abs(x.End-(start+end)/2)<Padding))local.Add("caption-vad-conflict");
                result.Add(new(FormattableString.Invariant($"voice-gap-{gap.Start:R}-{gap.End:R}-{i}"),gap.Start,gap.End,start,end,local.Count==0,local.ToArray(),conflictedCaptions.Where(c=>c.Start<end&&c.End>start).ToArray()));
            }
        }
        return result.ToArray();
    }
    public static SilenceRange[] AutomaticCuts(IEnumerable<SourceSilenceCandidate> plan)=>plan.Where(x=>x.AutoEligible).Select(x=>new SilenceRange(x.CutStart,x.CutEnd)).ToArray();
}
