/** Pure review algebra. No network, model requests, native timeline mutations, or saving. */
export interface SemanticFingerprints {sourceSignature:string;candidateHash:string;transcriptCacheKey:string}
export interface SemanticOccurrence {candidateId:string;sourceId:string;start:number;end:number;name:string}
/** Groups must be resolved against trusted local subtitle records, never model-provided timestamps. */
export interface TrustedSemanticGroup {candidateId:string;cueIds:string[];start:number;end:number}
export interface SemanticSuggestion {id:string;left:TrustedSemanticGroup;right:TrustedSemanticGroup;riskIds:string[]}
/** Adapter accepts ONLY the server parser's locally resolved evidence, not raw model JSON. */
export interface ParsedSemanticEvidence {candidateId:string;cues:{id:string;start:number;end:number;text:string}[];before?:{id:string;start:number;end:number;text:string}|null;after?:{id:string;start:number;end:number;text:string}|null}
export interface ParsedSemanticSuggestion {id:string;reason?:string;localRiskHints:string[];left:ParsedSemanticEvidence;right:ParsedSemanticEvidence;contextRisks:string[];requiresManualReview:boolean}
export function trustedPlannerSuggestion(parsed:ParsedSemanticSuggestion):SemanticSuggestion {
 const group=(e:ParsedSemanticSuggestion['left']):TrustedSemanticGroup=>({candidateId:e.candidateId,cueIds:e.cues.map(c=>c.id),start:Math.min(...e.cues.map(c=>c.start)),end:Math.max(...e.cues.map(c=>c.end))});
 return {id:parsed.id,left:group(parsed.left),right:group(parsed.right),riskIds:[...parsed.contextRisks]};
}
export type SemanticChoice='keep-both'|'exclude-left'|'exclude-right';
export interface SemanticDecision {choice:SemanticChoice;acknowledgedRiskIds:string[]}
export interface SemanticReviewContext {fingerprints:SemanticFingerprints;occurrences:SemanticOccurrence[];selectedIds:string[];fps:number}
export interface SemanticReviewDraft {baseline:SemanticReviewContext;suggestions:SemanticSuggestion[];decisions:Record<string,SemanticDecision>}
export interface SemanticRetainedPart {candidateId:string;sourceId:string;start:number;end:number;startFrame:number;endFrame:number;name:string;partIndex:number}
export interface SemanticApplicationPlan {parts:SemanticRetainedPart[];removedFrames:number;retainedFrames:number;affectedCandidateIds:string[];exclusions:{candidateId:string;startFrame:number;endFrame:number;suggestionIds:string[]}[]}
export class SemanticReviewError extends Error {constructor(public code:string,message:string){super(message);this.name='SemanticReviewError';}}
const fail=(code:string,message:string):never=>{throw new SemanticReviewError(code,message);};
const clone=<T,>(value:T):T=>JSON.parse(JSON.stringify(value)) as T;
function boundaryKey(context:SemanticReviewContext){return JSON.stringify({fps:context.fps,selectedIds:context.selectedIds,occurrences:[...context.occurrences].sort((a,b)=>a.candidateId.localeCompare(b.candidateId)).map(c=>[c.candidateId,c.sourceId,c.start,c.end])});}
function validateContext(context:SemanticReviewContext){
 if(!Number.isFinite(context.fps)||context.fps<=0)fail('invalid-fps','缺少有效时间线帧率。');
 if(Object.values(context.fingerprints).some(v=>typeof v!=='string'||!v))fail('missing-fingerprint','缺少源文件、候选或字幕指纹。');
 const ids=new Set<string>();
 for(const c of context.occurrences){if(!c.candidateId||!c.sourceId||ids.has(c.candidateId)||!Number.isFinite(c.start)||!Number.isFinite(c.end)||c.start<0||c.end<=c.start)fail('invalid-occurrence','候选编号或边界无效。');ids.add(c.candidateId);}
 if(new Set(context.selectedIds).size!==context.selectedIds.length||context.selectedIds.some(id=>!ids.has(id)))fail('invalid-selection','入选候选必须存在且不能重复编号。');
}
export function createSemanticReview(context:SemanticReviewContext,suggestions:SemanticSuggestion[]):SemanticReviewDraft {
 validateContext(context);const ids=new Set<string>();
 for(const s of suggestions){if(!s.id||ids.has(s.id))fail('invalid-suggestion','建议编号重复或为空。');ids.add(s.id);
  for(const group of [s.left,s.right]){const candidate=context.occurrences.find(c=>c.candidateId===group.candidateId);
   if(!candidate||!group.cueIds.length||new Set(group.cueIds).size!==group.cueIds.length||group.cueIds.some(id=>!id)||!Number.isFinite(group.start)||!Number.isFinite(group.end)||group.start<0||group.end<=group.start)fail('invalid-group','句组必须来自可信候选和有效字幕区间。');
  }
 }
 return {baseline:clone(context),suggestions:clone(suggestions),decisions:Object.fromEntries(suggestions.map(s=>[s.id,{choice:'keep-both',acknowledgedRiskIds:[]}]))};
}
export function setSemanticDecision(draft:SemanticReviewDraft,suggestionId:string,choice:SemanticChoice,acknowledgedRiskIds:string[]=[]):SemanticReviewDraft {
 if(!draft.suggestions.some(s=>s.id===suggestionId))fail('unknown-suggestion','找不到这项建议。');
 if(!['keep-both','exclude-left','exclude-right'].includes(choice))fail('invalid-choice','无效审阅决定。');
 return {...draft,decisions:{...draft.decisions,[suggestionId]:{choice,acknowledgedRiskIds:[...acknowledgedRiskIds]}}};
}
export const restoreSemanticDecision=(draft:SemanticReviewDraft,suggestionId:string)=>setSemanticDecision(draft,suggestionId,'keep-both');
export function buildSemanticApplicationPlan(draft:SemanticReviewDraft,current:SemanticReviewContext,existingClipCount:number):SemanticApplicationPlan {
 validateContext(current);
 const a=draft.baseline.fingerprints,b=current.fingerprints;
 if(a.sourceSignature!==b.sourceSignature||a.candidateHash!==b.candidateHash||a.transcriptCacheKey!==b.transcriptCacheKey||boundaryKey(draft.baseline)!==boundaryKey(current))fail('review-stale','源文件、候选、字幕、边界或选择顺序已变化，必须重新审阅。');
 if(!current.selectedIds.length)fail('empty-selection','请至少选择一个候选。');
 if(!Number.isSafeInteger(existingClipCount)||existingClipCount<0||existingClipCount>200)fail('invalid-existing-count','现有时间线片段数量无效。');
 const rawCuts=new Map<string,{start:number;end:number}[]>();
 const cuts=new Map<string,{start:number;end:number;suggestionIds:string[]}[]>();
 for(const s of draft.suggestions){const decision=draft.decisions[s.id];if(!decision||decision.choice==='keep-both')continue;
  if(!['exclude-left','exclude-right'].includes(decision.choice))fail('invalid-choice','无效审阅决定。');
  if(s.riskIds.some(id=>!decision.acknowledgedRiskIds.includes(id)))fail('risk-unacknowledged','请逐项确认删除句组的上下文风险。');
  const group=decision.choice==='exclude-left'?s.left:s.right;
  if(!current.selectedIds.includes(group.candidateId))fail('target-not-selected','排除决定指向未入选候选，请恢复该决定或重新审阅。');
  const candidate=current.occurrences.find(c=>c.candidateId===group.candidateId)!;
  const rawStart=Math.max(candidate.start,group.start),rawEnd=Math.min(candidate.end,group.end);
  const raw=rawCuts.get(group.candidateId)??[];raw.push({start:rawStart,end:rawEnd});rawCuts.set(group.candidateId,raw);
  const start=Math.round(rawStart*current.fps),end=Math.round(rawEnd*current.fps);
  if(end<=start)fail('empty-exclusion','排除句组与候选没有至少一帧的有效交集。');
  const values=cuts.get(group.candidateId)??[];values.push({start,end,suggestionIds:[s.id]});cuts.set(group.candidateId,values);
 }
 const plan:SemanticApplicationPlan={parts:[],removedFrames:0,retainedFrames:0,affectedCandidateIds:[],exclusions:[]};
 for(const id of current.selectedIds){const candidate=current.occurrences.find(c=>c.candidateId===id)!,startFrame=Math.round(candidate.start*current.fps),endFrame=Math.round(candidate.end*current.fps);
  if(endFrame<=startFrame)fail('zero-frame-candidate','候选不足一帧，不能静默丢弃。');
  let rawAt=candidate.start;
  for(const raw of (rawCuts.get(id)??[]).sort((a,b)=>a.start-b.start)){
   if(raw.start>rawAt&&Math.round(raw.start*current.fps)<=Math.round(rawAt*current.fps))fail('zero-frame-part','保留子片段不足一帧，不能静默丢弃。');
   rawAt=Math.max(rawAt,raw.end);
  }
  if(rawAt<candidate.end&&Math.round(candidate.end*current.fps)<=Math.round(rawAt*current.fps))fail('zero-frame-part','保留子片段不足一帧，不能静默丢弃。');
  const merged:{start:number;end:number;suggestionIds:string[]}[]=[];
  for(const cut of (cuts.get(id)??[]).sort((a,b)=>a.start-b.start||a.end-b.end)){const last=merged.at(-1);if(last&&cut.start<=last.end){last.end=Math.max(last.end,cut.end);last.suggestionIds=[...new Set([...last.suggestionIds,...cut.suggestionIds])];}else merged.push({...cut,suggestionIds:[...cut.suggestionIds]});}
  const retained:{start:number;end:number}[]=[];let at=startFrame;
  for(const cut of merged){if(cut.start>at)retained.push({start:at,end:cut.start});at=Math.max(at,cut.end);plan.removedFrames+=cut.end-cut.start;plan.exclusions.push({candidateId:id,startFrame:cut.start,endFrame:cut.end,suggestionIds:cut.suggestionIds});}
  if(at<endFrame)retained.push({start:at,end:endFrame});
  if(!retained.length)fail('candidate-emptied','排除将清空一个候选，请保留句组或重新选择候选；不会暗中删除整段。');
  if(merged.length)plan.affectedCandidateIds.push(id);
  for(const [index,part] of retained.entries()){const start=part.start/current.fps,end=Math.min(candidate.end,part.end/current.fps);if(end<=start||Math.round(end*current.fps)<=Math.round(start*current.fps))fail('zero-frame-part','保留子片段不足一帧，不能静默丢弃。');
   plan.parts.push({candidateId:id,sourceId:candidate.sourceId,start,end,startFrame:part.start,endFrame:part.end,name:candidate.name,partIndex:index+1});plan.retainedFrames+=part.end-part.start;
  }
 }
 if(existingClipCount+plan.parts.length>200)fail('clip-limit','现有片段加保留子片段超过200，请减少入选内容。');
 return plan;
}
