import {defaultPauseParameters} from './pauseReviewPlanner';
import type {PausePreview,PauseCandidate} from './pauseReviewPlanner';
import type {EditorProject} from '../../api/types';
import type { CopyStamp, CopySaveSession } from './copyBridge';
export interface SilenceParameters { minSeconds: number; paddingBefore: number; paddingAfter: number; noiseDb: number }
export interface SilenceCandidate extends PauseCandidate {keep:boolean;acknowledged:boolean;originalCutStart:number;originalCutEnd:number;originalEligible:boolean;originalRisks:string[]}
export interface SilencePreviewResult extends PausePreview {}
export interface SilenceNative {
  hostSnapshot(): { ok: boolean; generation?: number; engineRevision?: number; document?: {sequence?: {fps?: number}} };
  hostPlaceSourceRanges(input: { sourceId: string; ranges: { start: number; end: number; name: string }[]; expected: CopyStamp; includeSourceCaptions: true }): { ok: boolean; error?: string; keys?: string[]; captionCount?: number };
}
export interface SilenceState { evidenceProvider?:string; open: boolean; phase: 'idle' | 'detecting' | 'ready' | 'applying' | 'applied' | 'stale' | 'error'; parameters: SilenceParameters; sourceRange:{start:number;end:number}; candidates: SilenceCandidate[]; message: string; warnings: string[]; fps: number }
export const initialSilenceState = (): SilenceState => ({ open: false, phase: 'idle', parameters: {...defaultPauseParameters}, sourceRange:{start:0,end:0}, candidates: [], message: '检测源素材长停顿，审阅后仅追加保留片段到时间线末尾。预览不改工程；确认追加后会按编辑器自动保存，可单步撤销。', warnings: [], fps: 30 });
export function silenceKeptRanges(start: number, end: number, candidates: SilenceCandidate[], fps = 30) {
  const chosen=candidates.filter(c=>!c.keep);if(chosen.some(c=>!c.autoEligible&&!c.acknowledged))throw Error('低置信停顿需明确确认风险，未追加。');
  const cuts = chosen.map(c => ({ start: Math.ceil(c.cutStart*fps-1e-7)/fps, end: Math.floor(c.cutEnd*fps+1e-7)/fps })).sort((a,b) => a.start-b.start);
  let at = start; const kept: { start: number; end: number }[] = [];
  for (const cut of cuts) {
    if (!Number.isFinite(cut.start) || !Number.isFinite(cut.end) || cut.start < start || cut.end > end || cut.end <= cut.start) throw Error('删除区间须在检测范围内，且结束晚于开始。');
    if (cut.start > at) kept.push({start:at,end:cut.start});
    at=Math.max(at,cut.end);
  }
  if(at<end)kept.push({start:at,end});
  return kept.map(range=>({start:Math.round(range.start*fps)/fps,end:Math.min(end,Math.round(range.end*fps)/fps)})).filter(range=>range.end>range.start);
}
export function createSilenceController(options: {
 clipId: string; start: number; end: number; save: CopySaveSession; native: SilenceNative;
 detect: (parameters: SilenceParameters, project:EditorProject,signal: AbortSignal, range:{start:number;end:number}) => Promise<SilencePreviewResult>;
 verify: (preview:SilencePreviewResult,project:EditorProject, signal: AbortSignal) => Promise<void>;
 onState?: (state: SilenceState) => void;
}) {
 let state={...initialSilenceState(),sourceRange:{start:options.start,end:options.end}}, disposed=false, pending: AbortController|null=null;
 let baseline: {stamp:CopyStamp;revision:number}|null=null, result:SilencePreviewResult|null=null;
 const publish=(patch:Partial<SilenceState>)=>{if(!disposed){state={...state,...patch};options.onState?.(state);}};
 const read=()=>{
  const saved=options.save.readState(); if(saved.saving||saved.conflict||saved.revision===null)throw Error('请先处理保存中或冲突状态。');
  const native=options.native.hostSnapshot(); if(!native.ok||native.generation===undefined||native.engineRevision===undefined)throw Error('原生时间线尚未就绪。');
  const stamp={generation:native.generation,engineRevision:native.engineRevision};
  const projection=options.save.projectCurrentSnapshot(stamp);if(!projection.ok)throw Error('当前工程无法完整映射，未修改。');
  const fps=native.document?.sequence?.fps; if(!fps||fps<=0)throw Error('缺少时间线帧率。');
  publish({fps}); return {stamp,revision:saved.revision,project:projection.project};
 };
 const same=()=>{const now=read();return !!baseline&&now.revision===baseline.revision&&now.stamp.generation===baseline.stamp.generation&&now.stamp.engineRevision===baseline.stamp.engineRevision;};
 const cancel=()=>{pending?.abort();pending=null;};
 const resetCandidate=(c:SilenceCandidate)=>({...c,cutStart:c.originalCutStart,cutEnd:c.originalCutEnd,autoEligible:c.originalEligible,riskCodes:c.originalRisks,acknowledged:false,keep:!c.originalEligible});
 return {
 getState:()=>state,
 open(){publish({open:true});}, close(){if(state.phase==='applying')return;cancel();publish({open:false,phase:state.phase==='detecting'?'idle':state.phase});},
 range(value:Partial<{start:number;end:number}>){if(state.phase==='applying')return;cancel();baseline=null;result=null;publish({sourceRange:{...state.sourceRange,...value},candidates:[],evidenceProvider:undefined,phase:'idle',message:'源范围已改变，请重新检测；仅追加所选范围，原时间线不变。'});},
 parameters(value:Partial<SilenceParameters>){if(state.phase==='applying')return;cancel();baseline=null;result=null;publish({parameters:{...state.parameters,...value},candidates:[],evidenceProvider:undefined,phase:'idle',message:'参数已变更，请重新检测；旧预览不可应用。'});},
 update(id:string,value:Partial<Pick<SilenceCandidate,'cutStart'|'cutEnd'|'keep'|'acknowledged'>>){if(state.phase!=='ready')return;publish({candidates:state.candidates.map(c=>{if(c.id!==id)return c;if(value.keep===false&&!c.autoEligible&&!c.acknowledged)return c;if(value.cutStart!==undefined||value.cutEnd!==undefined)return {...c,...value,autoEligible:false,riskCodes:['manual-boundary-change'],keep:true,acknowledged:false};return {...c,...value,...(value.acknowledged===false?{keep:true}:{})};})});},
 restore(id?:string){if(state.phase!=='ready')return;publish({candidates:state.candidates.map(c=>!id||c.id===id?resetCandidate(c):c)});},
 invalidate(){cancel();baseline=null;result=null;publish({phase:'stale',message:'工程或源范围已变化，请重新检测；未追加。'});},
 async detect(){
  if(state.phase==='applying'||pending)return;const request=new AbortController();pending=request;
  publish({phase:'detecting',candidates:[],evidenceProvider:undefined,message:'本地检测静音中，不调用 AI，不保存工程…'});
  try {const p=state.parameters;
   if(!Number.isFinite(p.minSeconds)||p.minSeconds<.2||p.minSeconds>30||!Number.isFinite(p.noiseDb)||p.noiseDb< -90||p.noiseDb>0||![p.paddingBefore,p.paddingAfter].every(v=>Number.isFinite(v)&&v>=0&&v<=1))throw Error('阈值须为0.2–30秒，气口为0–1秒，音量为-90至0dB。');
   const current=read();const raw=state.sourceRange;if(!Number.isFinite(raw.start)||!Number.isFinite(raw.end)||raw.start<options.start||raw.end>options.end||raw.end<=raw.start)throw Error('源范围须在素材内，结束必须晚于开始。');const range={start:Math.ceil(raw.start*state.fps-1e-7)/state.fps,end:Math.floor(raw.end*state.fps+1e-7)/state.fps};if(range.end<=range.start)throw Error('源范围不足一帧。');publish({sourceRange:range});baseline=current;const value=await options.detect(state.parameters,current.project,request.signal,range);if(disposed||pending!==request)return;
   if(!same())throw Error('工程已变化，请重新检测。');if(value.sourceId!==options.clipId||value.silences.some(c=>c.start<range.start-1e-7||c.end>range.end+1e-7||c.cutStart<range.start-1e-7||c.cutEnd>range.end+1e-7))throw Error('检测结果超出所选源范围，未应用。');result=value;
   publish({phase:'ready',evidenceProvider:value.evidenceProvider,candidates:value.silences.map(c=>resetCandidate({...c,originalCutStart:c.cutStart,originalCutEnd:c.cutEnd,originalEligible:c.autoEligible,originalRisks:c.riskCodes,keep:!c.autoEligible,acknowledged:false})),warnings:value.warnings,message:'请试听源素材并审阅。勾选保留可保留有意停顿，修改删除边界或恢复检测值；确认后追加，不替换现有内容。'});
  }catch(error){if(!disposed&&pending===request)publish({phase:'error',message:String(error instanceof Error?error.message:error)});}finally{if(pending===request)pending=null;}
 },
 async apply(){
  if(state.phase!=='ready'||pending||!result||!baseline)return false;
  const request=new AbortController();pending=request;publish({phase:'applying',message:'正在核对源素材与工程基线，尚未追加…'});
  try{
   if(!same())throw Error('工程已变化，请重新检测。');const ranges=silenceKeptRanges(state.sourceRange.start,state.sourceRange.end,state.candidates,state.fps);
   if(!ranges.length)throw Error('没有剩余可追加区间，请保留至少一个有效区间。');
   const retained=ranges.reduce((sum,r)=>sum+(Math.round(r.end*state.fps)-Math.round(r.start*state.fps)),0);
   if(retained>=Math.round((state.sourceRange.end-state.sourceRange.start)*state.fps))throw Error('当前没有实际剪除的帧，不追加整片副本；请调整候选或参数。');
   await options.verify(result,read().project,request.signal);if(disposed||pending!==request)return false;
   if(!same())throw Error('工程已变化，请重新检测。');
   const placed=options.native.hostPlaceSourceRanges({sourceId:options.clipId,ranges:ranges.map(r=>({...r,name:'去长停顿粗剪'})),expected:baseline.stamp,includeSourceCaptions:true});
   if(!placed.ok)throw Error(placed.error||'追加失败，未确认修改。');
   publish({phase:'applied',message:`已追加 ${placed.keys?.length??ranges.length} 段到时间线末尾；原有多轨未删除，可 Ctrl+Z 单步撤销；追加和撤销都会按编辑器自动保存，保存结果请看状态栏。${placed.captionCount ? `新增同步字幕 ${placed.captionCount} 条。` : '没有可用源字幕，未新增字幕。'}`});return true;
  }catch(error){if(!disposed&&pending===request)publish({phase:'stale',message:String(error instanceof Error?error.message:error)});return false;}finally{if(pending===request)pending=null;}
 },dispose(){disposed=true;cancel();}
 };
}
