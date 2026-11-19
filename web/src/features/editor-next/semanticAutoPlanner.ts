import {buildSemanticApplicationPlan,createSemanticReview,setSemanticDecision,trustedPlannerSuggestion,type ParsedSemanticSuggestion,type SemanticReviewContext,type SemanticReviewDraft} from './semanticReviewPlanner';

/** Separate user-authorized text rule, never treats model review flags as deletion permission. */
export function planConservativeAutomaticReview(context:SemanticReviewContext,suggestions:ParsedSemanticSuggestion[],authorizeExactTextRule=false){
 let draft:SemanticReviewDraft=createSemanticReview(context,suggestions.map(trustedPlannerSuggestion));
 const decisions:{id:string;action:'keep-both'|'exclude-left'|'exclude-right';reason:string}[]=[];
 const span=(g:ParsedSemanticSuggestion['left'])=>({start:Math.min(...g.cues.map(c=>c.start)),end:Math.max(...g.cues.map(c=>c.end))});
 const overlaps=(a:ParsedSemanticSuggestion['left'],b:ParsedSemanticSuggestion['left'])=>a.candidateId===b.candidateId&&span(a).start<span(b).end&&span(b).start<span(a).end;
 for(const s of suggestions){
  let reason='';const left=span(s.left),right=span(s.right);
  if(!authorizeExactTextRule)reason='尚未授权逐字等价自动规则；模型建议仍需人工审阅。';
  else if(s.contextRisks.length||s.localRiskHints.length)reason='存在保护标签或风险提示，保留两边。';
  else if(/试听|音画|画面|语气|語氣|现场|現場|反应|反應|有意|故意|强调|強調|audio|visual|intentional/i.test(s.reason??''))reason='可能需要音画或现场证据，保留两边。';
  else if([s.left,s.right].some(g=>g.cues.length<1||g.cues.length>3||span(g).end-span(g).start>15))reason='句组超过自动规则范围。';
  else if(JSON.stringify(s.left.cues.map(c=>c.text.trim()))!==JSON.stringify(s.right.cues.map(c=>c.text.trim()))||s.left.cues.some(c=>!c.text.trim()))reason='不是严格逐字等价，不自动排除。';
  else if(s.left.cues.map(c=>c.text.trim()).join('').length<8)reason='短句或现场反应不足以确定冗余，保留两边。';
  else if(!s.left.before||!s.left.after||!s.right.before||!s.right.after||s.left.before.text.trim()!==s.right.before.text.trim()||s.left.after.text.trim()!==s.right.after.text.trim())reason='前后上下文缺失或不等价，保留两边。';
  else if(s.left.cues.some(c=>/[0-9０-９一二三四五六七八九十百千万萬亿億零两兩不没沒无無未非勿莫否别別甭？?]|不要|不能/.test(c.text)))reason='包含数字、否定或疑问保护内容，保留两边。';
  else if(left.start===right.start||left.start<right.end&&right.start<left.end)reason='源时间相同或重叠，保留两边。';
  else if(suggestions.some(other=>other.id!==s.id&&[s.left,s.right].some(a=>[other.left,other.right].some(b=>overlaps(a,b)))))reason='与其他建议句组冲突，保留两边。';
  const choice=left.start<right.start?'exclude-right':'exclude-left';const target=choice==='exclude-left'?s.left:s.right,candidate=context.occurrences.find(c=>c.candidateId===target.candidateId),cut=span(target);
  if(!reason&&(!candidate||!context.selectedIds.includes(s.left.candidateId)||!context.selectedIds.includes(s.right.candidateId)||cut.start<candidate.start||cut.end>candidate.end||cut.start<=candidate.start&&cut.end>=candidate.end))reason='范围越界、未选中或会删除完整候选，保留两边。';
  if(!reason){const next=setSemanticDecision(draft,s.id,choice);try{buildSemanticApplicationPlan(next,context,0);draft=next;}catch{reason='无法生成完整有效的帧级保留计划，保留两边。';}}
  decisions.push({id:s.id,action:reason?'keep-both':choice,reason:reason||'用户授权的逐字等价规则：保留源时间先出现句组，排除后出现句组；不是模型自动批准。'});
 }
 return {draft,decisions,plan:buildSemanticApplicationPlan(draft,context,0)};
}

/** User-marked scene changes only. Does not detect scenes or infer breath cuts. */
export function planSceneDissolves(ranges:{start:number;end:number}[],fps:number,boundaryIndexes:number[],durationSeconds=.8){
 if(!Number.isFinite(fps)||fps<=0||!Number.isFinite(durationSeconds)||durationSeconds<=0||durationSeconds>2)throw Error('叠化帧率或时长无效。');
 if(new Set(boundaryIndexes).size!==boundaryIndexes.length)throw Error('同一换场边界不能重复叠化。');
 const lengths=ranges.map(r=>Math.floor((r.end-r.start)*fps+1e-6));
 const boundaries=boundaryIndexes.map(index=>{
  if(!Number.isInteger(index)||index<1||index>=ranges.length)throw Error('换场边界已过期，请重新选择。');
  if(Math.abs(ranges[index-1].end-ranges[index].start)<=1e-6)throw Error('连续源片段连接不作为换场叠化。');
  const maxFrames=Math.floor((Math.min(lengths[index-1],lengths[index])-1)/2);
  if(maxFrames<1)throw Error('片段过短，无法保留至少一帧未混合画面，请取消该换场连接。');
  const frames=Math.min(maxFrames,Math.max(1,Math.round(durationSeconds*fps)));
  return {boundaryIndex:index,frames,duration:frames/fps};
 }).sort((a,b)=>a.boundaryIndex-b.boundaryIndex);
 const totalShortening=boundaries.reduce((n,b)=>n+b.duration,0);
 return {boundaries,totalShortening,duration:ranges.reduce((n,r)=>n+r.end-r.start,0)-totalShortening};
}
