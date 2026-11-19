export interface PauseParameters {minSeconds:number;paddingBefore:number;paddingAfter:number;noiseDb:number}
export const defaultPauseParameters:PauseParameters={minSeconds:.6,paddingBefore:.12,paddingAfter:.12,noiseDb:-32};
export interface CaptionVoiceConflict {start:number;end:number;voiceSeconds:number;coverageRatio:number;reviewThreshold:number}
export interface PauseCandidate {captionConflicts?:CaptionVoiceConflict[]|null;id:string;start:number;end:number;cutStart:number;cutEnd:number;autoEligible:boolean;riskCodes:string[]}
export interface PausePreview {evidenceProvider?:string;policyVersion?:string;sourceId:string;sourceSignature:string;transcriptCacheKey:string;duration:number;silences:PauseCandidate[];warnings:string[]}
export interface PauseReview {enabled:boolean;phase:'idle'|'detecting'|'ready'|'error';parameters:PauseParameters;data:PausePreview|null;excludedIds:string[];reviewedRiskIds:string[];message:string}
export const initialPauseReview=():PauseReview=>({enabled:false,phase:'idle',parameters:{...defaultPauseParameters},data:null,excludedIds:[],reviewedRiskIds:[],message:''});
export const pauseRiskLabels:Record<string,string>={'manual-boundary-change':'人工修改了检测边界，需重新试听并确认风险','subtitle-overlap':'删除区间与字幕及保护边界重叠','transcript-unavailable':'旧能量规划缺少字幕证据，请重新检测以使用当前语音规则','transcript-coverage-uncertain':'缺少前后字幕覆盖证据','insufficient-cut':'可删区间过短','vad-unavailable':'本地语音检测不可用，仅有低音量证据，默认保留','no-speech-context':'缺少前后明确人声，默认保留','uncertain-speech':'检测到不确定语音，默认保留','pts-gap':'媒体时间戳存在间隙，默认保留','caption-vad-conflict':'字幕区间内检测到的语音覆盖不足，默认保留供人工复核','fragmented-pause-review':'此候选可能形成过短的保留片段，默认保留并人工审核'};
/** All times here remain source seconds. Only the native append/copy computes timeline positions. */
export function applyPauseReview(ranges:{start:number;end:number;name:string}[],review:PauseReview,mode:'manual'|'automatic',fps:number){
 if(!review.enabled)return ranges;
 if(review.phase!=='ready'||!review.data)throw Error('请先重新生成本地停顿预览，旧计划不能应用。');
 const cuts=review.data.silences.filter(c=>mode==='automatic'?c.autoEligible:review.excludedIds.includes(c.id));
 for(const c of cuts)if(!c.autoEligible&&(mode==='automatic'||!review.reviewedRiskIds.includes(c.id)))throw Error('低置信停顿必须先明确确认风险，才可人工删除。');
 return ranges.flatMap(range=>{
  // Never let an audio candidate cross a selected candidate boundary. Cut rounding goes inward.
  const local=cuts.filter(c=>c.cutStart>=range.start&&c.cutEnd<=range.end).map(c=>({start:Math.ceil(c.cutStart*fps-1e-7)/fps,end:Math.floor(c.cutEnd*fps+1e-7)/fps})).filter(c=>c.end>c.start).sort((a,b)=>a.start-b.start);
  const kept:{start:number;end:number;name:string}[]=[];let at=range.start;
  for(const cut of local){if(cut.start>at)kept.push({start:at,end:cut.start,name:range.name});at=Math.max(at,cut.end);}
  if(at<range.end)kept.push({start:at,end:range.end,name:range.name});return kept;
 });
}

export function pauseEvidenceLabel(provider?:string){return provider==='silero-v6-local-v1'?'证据：本地 Silero VAD 人声活动；字幕辅助核对':provider==='energy-only-fallback'?'证据：仅低音量回退，未获得语音检测；全部默认保留，不自动剪除':'尚未取得本次语音检测证据';}

export const captionConflictLabels=(candidate:PauseCandidate)=>(candidate.captionConflicts??[]).map(c=>
 `字幕源区间 ${c.start.toFixed(3)}–${c.end.toFixed(3)} 秒：人声及不确定语音覆盖 ${c.voiceSeconds.toFixed(3)} 秒（${(c.coverageRatio*100).toFixed(1)}%），低于人工复核阈值 ${(c.reviewThreshold*100).toFixed(0)}%。这是保守规则，不是模型置信概率。`);
