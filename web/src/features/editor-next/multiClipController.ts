import {initialPauseReview,applyPauseReview} from './pauseReviewPlanner';
import type {PauseReview,PauseParameters,PausePreview} from './pauseReviewPlanner';
import {isLocalOllamaEndpoint} from './semanticConnection';
import {planConservativeAutomaticReview,planSceneDissolves} from './semanticAutoPlanner';
import type {AiConnectionBody} from '../editor/aiAdvice';
import {createSemanticReview,trustedPlannerSuggestion,setSemanticDecision,buildSemanticApplicationPlan} from './semanticReviewPlanner';
import type {ParsedSemanticSuggestion,SemanticReviewDraft,SemanticChoice,SemanticReviewContext} from './semanticReviewPlanner';
export interface AutomaticCopyResult {clipId:string;name:string;revision:number;naturalTransitions?:ReturnType<typeof planSceneDissolves>}
export interface SemanticOnlineResult {suggestions:ParsedSemanticSuggestion[];sourceSignature:string;candidateHash:string;transcriptCacheKey:string;selectionHash:string}
export interface SemanticSession {stage:'idle'|'preparing'|'confirm'|'requesting'|'review'|'error'|'stale';connection:AiConnectionBody|null;draft:SemanticReviewDraft|null;suggestions:ParsedSemanticSuggestion[];message:string;selectionHash:string|null}
const initialSemantic=():SemanticSession=>({stage:'idle',connection:null,draft:null,suggestions:[],message:'',selectionHash:null});
import type { EditorProject } from '../../api/types';
import type { CopySaveSession, CopyStamp } from './copyBridge';
import type { SilenceNative } from './silencePreviewController';
export interface ReviewCandidate { id:string;start:number;end:number;title:string;summary:string;hook:string;source:string }
export interface ReviewSentence { start:number;end:number;text:string }
export interface MultiClipPreview {sourceId:string;sourceSignature:string;candidateHash:string;transcriptCacheKey:string;duration:number;candidates:ReviewCandidate[];transcript:ReviewSentence[];repeatHints?:{firstIndex:number;secondIndex:number;text:string;reason:string}[];warnings:string[]}
export interface ReviewItem extends ReviewCandidate {originalStart:number;originalEnd:number}
export interface MultiClipState {pause:PauseReview;open:boolean;phase:'idle'|'loading'|'ready'|'applying'|'applied'|'stale'|'error';data:MultiClipPreview|null;items:ReviewItem[];selectedIds:string[];message:string;fps:number;semantic:SemanticSession;existingClipCount:number;mode:'manual'|'automatic';autoRuleAuthorized:boolean;automaticResult:AutomaticCopyResult|null;restoreAvailable:boolean}
export const initialMultiClipState=():MultiClipState=>({pause:initialPauseReview(),open:false,phase:'idle',data:null,items:[],selectedIds:[],message:'读取已保存候选与字幕，不调用 AI。选择和调整仅为审阅草稿。',fps:30,semantic:initialSemantic(),existingClipCount:0,mode:'manual',autoRuleAuthorized:false,automaticResult:null,restoreAvailable:false});
export function reviewContext(sentences:ReviewSentence[],start:number,end:number){
 const inside=sentences.filter(s=>s.end>start&&s.start<end);
 const before=sentences.filter(s=>s.end<=start).at(-1),after=sentences.find(s=>s.start>=end);
 return {inside,before,after,cutsSentence:inside.some(s=>s.start<start||s.end>end)};
}
export function literalDuplicates(items:ReviewItem[],sentences:ReviewSentence[]){
 const normalize=(text:string)=>text.trim();
 const text=items.map(item=>normalize(sentences.filter(s=>s.end>item.start&&s.start<item.end).map(s=>s.text).join('\n')));
 const pairs:{left:string;right:string;reason:string}[]=[];
 for(let i=0;i<items.length;i++)for(let j=i+1;j<items.length;j++){
  if(text[i].length>=12&&text[i]===text[j])pairs.push({left:items[i].id,right:items[j].id,reason:'所覆盖字幕逐字相同（仅去首尾空白），不是语义分析'});
  else if(Math.min(items[i].end,items[j].end)>Math.max(items[i].start,items[j].start))pairs.push({left:items[i].id,right:items[j].id,reason:'源时间范围重叠，内容不一定重复'});
 }
 return pairs;
}
export function reviewRanges(state:MultiClipState){
 if(!state.data)return [];
 return state.selectedIds.map(id=>{
  const item=state.items.find(c=>c.id===id)!;
  if(!Number.isFinite(item.start)||!Number.isFinite(item.end)||item.start<0||item.end>state.data!.duration||item.end<=item.start)throw Error('入选片段边界无效，请检查源起止时间。');
  const start=Math.round(item.start*state.fps)/state.fps,end=Math.min(state.data!.duration,Math.round(item.end*state.fps)/state.fps);
  if(Math.round(end*state.fps)<=Math.round(start*state.fps))throw Error('入选片段不足一帧，请调整边界或取消选择。');
  return {start,end,name:item.title};
 });
}
/** Mirrors server SemanticReviewAnalysis: complete cues + adjacent source-index context per candidate. */
export function semanticDisclosure(state:MultiClipState){
 const transcript=state.data?.transcript??[],included=new Set<number>(),complete=new Set<number>(),counts:number[]=[];
 for(const id of state.selectedIds){const item=state.items.find(c=>c.id===id)!;const indices=transcript.map((c,i)=>({c,i})).filter(x=>x.c.start>=item.start&&x.c.end<=item.end).map(x=>x.i);counts.push(indices.length);
  for(const i of indices){included.add(i);complete.add(i);}
  if(indices.length){for(const i of [indices[0]-1,indices.at(-1)!+1])if(i>=0&&i<transcript.length)included.add(i);}
 }
 return {counts,cues:[...included].sort((a,b)=>a-b).map(i=>({...transcript[i],id:`cue-${i}`,contextOnly:!complete.has(i)}))};
}
export function multiClipSemanticContext(state:MultiClipState):SemanticReviewContext {
 if(!state.data)throw Error('请先读取候选。');
 return {fingerprints:{sourceSignature:state.data.sourceSignature,candidateHash:state.data.candidateHash,transcriptCacheKey:state.data.transcriptCacheKey},occurrences:state.items.map(c=>({candidateId:c.id,sourceId:state.data!.sourceId,start:c.start,end:c.end,name:c.title})),selectedIds:[...state.selectedIds],fps:state.fps};
}
export function automaticReview(state:MultiClipState){
 if(state.semantic.stage==='stale')throw Error('语义审阅已过期，请重新审阅。');
 const context=multiClipSemanticContext(state);
 if(state.semantic.draft)buildSemanticApplicationPlan({...state.semantic.draft,decisions:Object.fromEntries(state.semantic.draft.suggestions.map(s=>[s.id,{choice:'keep-both' as const,acknowledgedRiskIds:[]}]))},context,0);
 return planConservativeAutomaticReview(context,state.semantic.suggestions,state.autoRuleAuthorized);
}
export function baseReviewRanges(state:MultiClipState){
 if(state.mode==='automatic')return automaticReview(state).plan.parts.map(p=>({start:p.start,end:p.end,name:p.name}));
 if(state.semantic.stage==='stale')throw Error('语义审阅已过期，请重新审阅或明确放弃语义排除。');
 if(!state.semantic.draft)return reviewRanges(state);
 return buildSemanticApplicationPlan(state.semantic.draft,multiClipSemanticContext(state),state.existingClipCount).parts.map(p=>({start:p.start,end:p.end,name:p.name}));
}
export function finalReviewRanges(state:MultiClipState){return applyPauseReview(baseReviewRanges(state),state.pause??initialPauseReview(),state.mode,state.fps);}
export function createMultiClipController(options:{clipId:string;save:CopySaveSession;native:SilenceNative;planPauses?:(project:EditorProject,ranges:{start:number;end:number;name:string}[],parameters:PauseParameters,signal:AbortSignal)=>Promise<PausePreview>;verifyPauses?:(project:EditorProject,preview:PausePreview,signal:AbortSignal)=>Promise<void>;load:(project:EditorProject,signal:AbortSignal)=>Promise<MultiClipPreview>;verify:(data:MultiClipPreview,project:EditorProject,signal:AbortSignal)=>Promise<void>;connection?:()=>Promise<AiConnectionBody|null>;semanticRequest?:(project:EditorProject,state:MultiClipState,connection:AiConnectionBody,signal:AbortSignal)=>Promise<SemanticOnlineResult>;restoreStatus?:()=>Promise<{available:boolean}>;restore?:(revision:number)=>Promise<unknown>;restoreBlocked?:()=>boolean;onRestored?:()=>void;copy?:(project:EditorProject,state:MultiClipState,ranges:{start:number;end:number}[],revision:number)=>Promise<AutomaticCopyResult>;onState?:(state:MultiClipState)=>void}){
 let state=initialMultiClipState(),baseline:{stamp:CopyStamp;revision:number}|null=null,pending:AbortController|null=null,disposed=false;let confirmationContext:string|null=null;
 const publish=(patch:Partial<MultiClipState>)=>{if(!disposed){
  if((patch.items||patch.selectedIds)&&!patch.semantic&&state.semantic.stage!=='idle')patch.semantic={...state.semantic,stage:'stale',message:'候选边界、选择或顺序已变化，必须重新审阅；旧决定保留但不可应用。'};
  if(patch.items||patch.selectedIds||patch.semantic)patch.autoRuleAuthorized=false;
  if(!patch.pause&&(patch.items||patch.selectedIds||patch.semantic||patch.mode||patch.autoRuleAuthorized!==undefined))patch.pause={...state.pause,data:null,phase:'idle',excludedIds:[],reviewedRiskIds:[],message:'范围、模式或语义决策已变化，请重新检测停顿。'};
  state={...state,...patch};options.onState?.(state);}};
 const read=()=>{const saved=options.save.readState(),native=options.native.hostSnapshot();
  if(saved.saving||saved.conflict||saved.error||saved.revision===null)throw Error('请先处理工程保存状态。');
  if(!native.ok||native.generation===undefined||native.engineRevision===undefined)throw Error('时间线尚未就绪。');
  const stamp={generation:native.generation,engineRevision:native.engineRevision};
  const projection=options.save.projectCurrentSnapshot(stamp);if(!projection.ok)throw Error('当前工程无法完整映射，未修改。');
  const fps=native.document?.sequence?.fps;if(!fps||fps<=0)throw Error('缺少时间线帧率。');const seq=native.document?.sequence as {videoTracks?:{clips:unknown[]}[];audioTracks?:{clips:unknown[]}[]}|undefined;
  const existingClipCount=[...(seq?.videoTracks??[]),...(seq?.audioTracks??[])].reduce((sum,t)=>sum+t.clips.length,0);publish({fps,existingClipCount});
  return {stamp,revision:saved.revision,project:projection.project};};
 const same=()=>{const now=read();return !!baseline&&now.revision===baseline.revision&&now.stamp.generation===baseline.stamp.generation&&now.stamp.engineRevision===baseline.stamp.engineRevision;};
 const cancel=()=>{pending?.abort();pending=null;};
 return {getState:()=>state,
  setPauseEnabled(enabled:boolean){if(pending)return;publish({pause:{...initialPauseReview(),enabled,parameters:state.pause.parameters}});},
  pauseParameters(parameters:Partial<PauseParameters>){if(pending)return;publish({pause:{...state.pause,parameters:{...state.pause.parameters,...parameters},data:null,phase:'idle',excludedIds:[],reviewedRiskIds:[]}});},
  pauseDecision(id:string,remove:boolean,acknowledged:boolean){if(pending||state.mode!=='manual'||state.pause.phase!=='ready')return;const candidate=state.pause.data?.silences.find(c=>c.id===id);if(!candidate)return;if(remove&&!candidate.autoEligible&&!acknowledged)return;publish({pause:{...state.pause,excludedIds:remove?[...new Set([...state.pause.excludedIds,id])]:state.pause.excludedIds.filter(x=>x!==id),reviewedRiskIds:acknowledged?[...new Set([...state.pause.reviewedRiskIds,id])]:state.pause.reviewedRiskIds.filter(x=>x!==id)}});},
  async planPauses(){if(pending||state.phase!=='ready'||!state.pause.enabled||!options.planPauses)return false;const request=new AbortController();pending=request;publish({phase:'loading',pause:{...state.pause,phase:'detecting',data:null,message:'本地音频与字幕区间联合检测，不调用模型。'}});
   try{if(!same())throw Error('工程已变化，请重新读取。');const ranges=baseReviewRanges(state);if(!ranges.length)throw Error('请先选择候选片段。');const result=await options.planPauses(read().project,ranges,state.pause.parameters,request.signal);if(disposed||pending!==request)return false;if(!same())throw Error('工程已变化，请重新检测。');if(result.sourceId!==options.clipId||result.sourceSignature!==state.data!.sourceSignature)throw Error('源签名已变化，请重新读取。');if(!options.verifyPauses)throw Error('缺少停顿计划核验接口。');await options.verifyPauses(read().project,result,request.signal);if(disposed||pending!==request)return false;if(!same())throw Error('核验期间工程已变化，请重新读取。');const scoped={...result,silences:result.silences.filter(c=>ranges.some(r=>c.cutStart>=r.start&&c.cutEnd<=r.end))};publish({phase:'ready',pause:{...state.pause,phase:'ready',data:scoped,excludedIds:scoped.silences.filter(c=>c.autoEligible).map(c=>c.id),reviewedRiskIds:[],message:scoped.silences.some(c=>c.autoEligible)?'符合保守自动规则的候选默认删除；低置信保留。人工可试听并明确确认风险后调整。':'没有安全自动删除候选，全部保留，不代表已剪除停顿。'}});return true;
   }catch(e){if(pending===request)publish({phase:'ready',pause:{...state.pause,phase:'error',data:null,message:e instanceof Error?e.message:String(e)}});return false;}finally{if(pending===request)pending=null;}
  },setMode(mode:'manual'|'automatic'){if(pending)return;publish({mode,autoRuleAuthorized:false});},authorizeAutomatic(value:boolean){if(!pending&&state.phase==='ready')publish({autoRuleAuthorized:value});},open(){publish({open:true});if(options.restoreStatus)void options.restoreStatus().then(result=>publish({restoreAvailable:result.available})).catch(()=>publish({restoreAvailable:false}));},close(){if(state.phase==='applying'||state.semantic.stage==='requesting')return;cancel();publish({open:false,phase:state.phase==='loading'?'idle':state.phase});},
  toggle(id:string){if(pending||state.phase!=='ready'||!state.items.some(c=>c.id===id))return;publish({selectedIds:state.selectedIds.includes(id)?state.selectedIds.filter(x=>x!==id):[...state.selectedIds,id]});},
  update(id:string,patch:{start?:number;end?:number}){if(!pending&&state.phase==='ready')publish({items:state.items.map(c=>c.id===id?{...c,...patch}:c)});},
  restore(id:string){if(!pending&&state.phase==='ready')publish({items:state.items.map(c=>c.id===id?{...c,start:c.originalStart,end:c.originalEnd}:c)});},
  expand(id:string){if(pending||state.phase!=='ready'||!state.data)return;publish({items:state.items.map(c=>{if(c.id!==id)return c;const context=reviewContext(state.data!.transcript,c.start,c.end);return {...c,start:Math.max(0,Math.min(c.start,...context.inside.map(s=>s.start))),end:Math.min(state.data!.duration,Math.max(c.end,...context.inside.map(s=>s.end)))};})});},
  move(id:string,delta:-1|1){if(pending||state.phase!=='ready')return;const ids=[...state.selectedIds],at=ids.indexOf(id),to=at+delta;if(at<0||to<0||to>=ids.length)return;[ids[at],ids[to]]=[ids[to],ids[at]];publish({selectedIds:ids});},
  invalidate(){cancel();baseline=null;publish({semantic:{...state.semantic,stage:state.semantic.stage==='idle'?'idle':'stale'},phase:'stale',message:'工程或数据基线已变化，请重新读取候选，旧审阅不可应用。'});},
  async load(){if(pending||state.phase==='applying')return;const request=new AbortController();pending=request;publish({phase:'loading',message:'正在读取已保存候选与字幕，不调用模型…'});
   try{const current=read();baseline=current;const data=await options.load(current.project,request.signal);if(disposed||pending!==request)return;if(!same())throw Error('工程已变化，请重新读取。');if(data.sourceId!==options.clipId)throw Error('候选来源不匹配。');publish({phase:'ready',data,items:data.candidates.map(c=>({...c,originalStart:c.start,originalEnd:c.end})),selectedIds:[],semantic:initialSemantic(),message:'请选择片段；勾选、边界调整和重排尚未修改工程。顺序按入选顺序，可上移下移。'});}catch(e){if(pending===request)publish({phase:'error',message:e instanceof Error?e.message:String(e)});}finally{if(pending===request)pending=null;}
  },
  async prepareSemantic(){
   if(pending||state.phase!=='ready')return;
   if(state.selectedIds.length<1||state.selectedIds.length>10){publish({semantic:{...state.semantic,stage:'error',message:'请选择1–10个候选后再请求语义建议。'}});return;}
   publish({semantic:{...state.semantic,stage:'preparing',message:'正在读取所选服务与模型，仅本地准备，不发送字幕。'}});
   try{if(!same())throw Error('工程已变化，请重新读取候选。');reviewRanges(state);const disclosure=semanticDisclosure(state);if(disclosure.counts.some(count=>count<2)||disclosure.cues.length>200)throw Error('每个入选候选需至少2条完整字幕，含前后上下文总计最多200条。');const pinned=JSON.stringify(multiClipSemanticContext(state));const connection=await options.connection?.();if(disposed)return;if(!connection)throw Error('请先在设置中选择服务和模型。');if(connection.provider==='ollama'&&!isLocalOllamaEndpoint(connection.endpoint))throw Error('本地 Ollama 地址必须是后端配置的 loopback 本机地址，请检查设置。');if(connection.provider==='ollama'&&/cloud/i.test(connection.model))throw Error('本地语义复核不允许 Ollama 云端模型，请选择已安装的本机模型。');if(!connection.endpoint.trim())throw Error('服务地址未显示，请在设置核对明确接收方后再请求。');if(pinned!==JSON.stringify(multiClipSemanticContext(state)))throw Error('选择已变化，请重新准备。');confirmationContext=pinned;publish({semantic:{...state.semantic,stage:'confirm',connection,message:connection.provider==='ollama'?'尚未发送。请确认本机服务、模型与字幕范围，再执行一次本地推理。':'尚未发送。请确认目的地、数据范围与费用后再发出一次请求。'}});}catch(e){publish({semantic:{...state.semantic,stage:'error',message:e instanceof Error?e.message:String(e)}});}
  },
  cancelSemanticConfirm(){confirmationContext=null;publish({semantic:{...state.semantic,stage:state.semantic.draft?'review':'idle'}});},
  clearSemantic(){if(pending)return;confirmationContext=null;publish({semantic:initialSemantic()});},
  semanticDecision(id:string,choice:SemanticChoice,acknowledgedRiskIds:string[]=[]){if(pending||state.semantic.stage!=='review'||!state.semantic.draft)return;publish({semantic:{...state.semantic,draft:setSemanticDecision(state.semantic.draft,id,choice,acknowledgedRiskIds)}});},
  async confirmSemantic(){
   if(pending||state.phase!=='ready'||state.semantic.stage!=='confirm'||!state.semantic.connection||!options.semanticRequest)return false;
   const request=new AbortController();pending=request;const connection=state.semantic.connection,context=multiClipSemanticContext(state);publish({semantic:{...state.semantic,stage:'requesting',message:connection.provider==='ollama'?'正在执行一次本机 Ollama 推理；不会自动重试或切换在线服务。':'正在发送一次模型请求；不会自动重试，失败也可能产生费用。'}});
   try{if(!same()||confirmationContext!==JSON.stringify(context))throw Error('基线已变化，尚未请求；请重新准备。');confirmationContext=null;
    const result=await options.semanticRequest(read().project,state,connection,request.signal);if(disposed||pending!==request)return false;
    if(!same()||JSON.stringify(context)!==JSON.stringify(multiClipSemanticContext(state)))throw Error('工程或候选已变化，返回建议不能应用。');
    for(const key of ['sourceSignature','candidateHash','transcriptCacheKey'] as const)if(result[key]!==context.fingerprints[key])throw Error('语义建议指纹不匹配，不能应用。');
    const draft=createSemanticReview(context,result.suggestions.map(trustedPlannerSuggestion));publish({semantic:{stage:'review',connection,draft,suggestions:result.suggestions,selectionHash:result.selectionHash,message:result.suggestions.length?`收到 ${result.suggestions.length} 项建议，默认两边全部保留。选择排除及确认风险后才改变最终区间预览。`:'本次模型未给出可复核的去重建议，不代表没有重复；已全部保留，可继续手动审阅。'}});return true;
   }catch(e){if(pending===request)publish({semantic:{...state.semantic,stage:'error',message:`${e instanceof Error?e.message:String(e)} ${connection.provider==='ollama'?((e as {code?:string;requestSent?:boolean})?.code==='semantic_input_limit'&&(e as {requestSent?:boolean})?.requestSent===false?'后端输入预检未通过，尚未开始本地推理，未发送外部服务或产生在线费用。请缩小选择范围后重新确认；不会截断或自动重试。':'本地推理未自动重试，也不会切换在线服务；已选片段和已有决定保留。'):'不自动重试；已选片段和已有决定保留，费用以服务商实际用量为准。'}`}});return false;}finally{if(pending===request)pending=null;}
  },
  async restoreAutomatic(){
   if(pending||!state.restoreAvailable||!options.restore)return false;
   if(options.restoreBlocked?.()){publish({message:'当前有未保存修改或草稿，请先另存副本；未恢复。'});return false;}
   const request=new AbortController();pending=request;publish({phase:'applying',message:'正在恢复新工程到生成前所选源片段序列…'});
   try{const current=read();await options.restore(current.revision);const now=options.native.hostSnapshot();if(now.generation!==current.stamp.generation||now.engineRevision!==current.stamp.engineRevision){publish({phase:'stale',message:'服务器已恢复，但本地又有编辑，未自动重载；请先保留本地副本再重载。'});return true;}publish({phase:'applied',message:'新工程已恢复到生成前所选区间，正在重载。原工程未修改。'});options.onRestored?.();return true;}catch(e){publish({phase:'error',message:`${e instanceof Error?e.message:String(e)} 未自动重试，也未覆盖本地内容。`});return false;}finally{if(pending===request)pending=null;}
  },
  async createAutomatic(){
   if(state.mode!=='automatic'||!state.autoRuleAuthorized||state.phase!=='ready'||pending||!state.data||!options.copy)return false;
   const request=new AbortController();pending=request;publish({phase:'applying',message:'正在核对并创建独立粗剪工程，原时间线不变…'});
   try{if(!same())throw Error('工程已变化，请重新读取。');const ranges=finalReviewRanges(state);if(!ranges.length)throw Error('请至少选择一个片段。');const current=read();await options.verify(state.data,current.project,request.signal);if(state.pause.enabled&&state.pause.data){if(!options.verifyPauses)throw Error('缺少停顿计划核验接口。');await options.verifyPauses(current.project,state.pause.data,request.signal);}if(disposed||pending!==request)return false;if(!same())throw Error('工程已变化，请重新读取。');const result=await options.copy(current.project,state,ranges,current.revision);publish({phase:'applied',automaticResult:result,message:'独立粗剪工程已创建。原工程未修改；未自动打开新工程。'});return true;}catch(e){publish({phase:'error',message:`${e instanceof Error?e.message:String(e)} 未自动重试；如创建结果不确定，请先检查工程列表。`});return false;}finally{if(pending===request)pending=null;}
  },
  async apply(){if(state.mode==='automatic')return false;if(state.phase!=='ready'||pending||!state.data||!baseline)return false;const request=new AbortController();pending=request;publish({phase:'applying',message:'核对源文件、候选、字幕及工程基线…'});
   try{if(!same())throw Error('工程已变化，请重新读取。');const ranges=finalReviewRanges(state);if(state.existingClipCount+ranges.length>200)throw Error('追加后超过200片段，请减少选择。');if(!ranges.length)throw Error('请至少选择一个片段。');await options.verify(state.data,read().project,request.signal);if(state.pause.enabled&&state.pause.data){if(!options.verifyPauses)throw Error('缺少停顿计划核验接口。');await options.verifyPauses(read().project,state.pause.data,request.signal);}if(disposed||pending!==request)return false;if(!same())throw Error('工程已变化，请重新读取。');const placed=options.native.hostPlaceSourceRanges({sourceId:options.clipId,ranges,expected:baseline.stamp,includeSourceCaptions:true});if(!placed.ok)throw Error(placed.error||'追加未确认。');publish({phase:'applied',message:`已按审阅顺序追加 ${ranges.length} 段，新增字幕 ${placed.captionCount??0} 条。现有多轨未删除，可单步撤销；追加与撤销都会自动保存，保存结果请看状态栏。`});return true;}catch(e){if(pending===request)publish({phase:'stale',message:e instanceof Error?e.message:String(e)});return false;}finally{if(pending===request)pending=null;}
  },dispose(){disposed=true;cancel();}
 };
}
