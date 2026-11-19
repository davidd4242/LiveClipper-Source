import type { AiConnectionBody } from '../editor/aiAdvice';
import type { AiAnalyzeResponse, EditorProject } from '../../api/types';
import type { IndependentAutomaticPorts } from './independentAutomaticController';
export interface CandidateAnalysisState {phase:'idle'|'preparing'|'confirm'|'running'|'done'|'error';connection:AiConnectionBody|null;cueCount:number;range:{start:number;end:number}|null;message:string}
export const initialCandidateAnalysis=():CandidateAnalysisState=>({phase:'idle',connection:null,cueCount:0,range:null,message:'生成会更新所选源素材的候选和分析缓存，不修改任何工程时间线。'});
type Cue={start:number;end:number;text:string};
export function createIndependentCandidateAnalysis(p:{projectId:string;sourceId:string;epoch:string;save:IndependentAutomaticPorts['save'];native:IndependentAutomaticPorts['native'];registered:()=>boolean;
 connection:()=>Promise<AiConnectionBody|null>;transcript:(project:EditorProject)=>Promise<{segments:Cue[];cacheKey:string}>;analyze:(project:EditorProject,connection:AiConnectionBody,revision:number)=>Promise<AiAnalyzeResponse>;
 onState:(state:CandidateAnalysisState)=>void;onComplete:()=>Promise<unknown>}){
 let state=initialCandidateAnalysis(),disposed=false,pending=false,prepared:{identity:string;transcript:string;connection:AiConnectionBody}|null=null;
 const emit=(patch:Partial<CandidateAnalysisState>)=>{if(!disposed){state={...state,...patch};p.onState(state);}};
 const identity=()=>{
  const saved=p.save.snapshot(),status=p.save.readState(),stamp=p.native.hostSnapshot();
  if(!p.registered()||saved.clipId!==p.projectId||saved.epoch!==p.epoch||!stamp.ok||status.hasUnsaved||status.saving||status.conflict||status.error)throw Error('工程、素材或保存状态已变化，请重新准备。');
  return JSON.stringify({projectId:p.projectId,sourceId:p.sourceId,epoch:p.epoch,revision:status.revision,generation:stamp.generation,engineRevision:stamp.engineRevision});
 };
 const checkTranscript=async()=>{
  const stamp=p.native.hostSnapshot();
  if(stamp.generation===undefined||stamp.engineRevision===undefined)throw Error('时间线未就绪。');
  const projection=p.save.projectCurrentSnapshot({generation:stamp.generation,engineRevision:stamp.engineRevision});
  if(!projection.ok)throw Error('工程快照无法完整读取。');
  const source=await p.transcript(projection.project);
  if(!source.segments.length)throw Error('所选源暂无字幕，请先在素材页转写。不会自动重转写。');
  return {...source,project:projection.project};
 };
 return {getState:()=>state,
  async prepare(){if(pending||disposed)return false;pending=true;prepared=null;emit({phase:'preparing',connection:null,message:'正在核对所选源字幕及当前服务，尚未发送模型。'});
   try{const saved=await p.save.flush();if(disposed)return false;if(!saved)throw Error('当前工程保存失败，未分析。');const baseline=identity(),source=await checkTranscript(),connection=await p.connection();if(disposed)return false;if(!connection)throw Error('请先配置可用的模型。');if(identity()!==baseline)throw Error('准备期间工程或素材已变化。');prepared={identity:baseline,transcript:source.cacheKey,connection};emit({phase:'confirm',connection,cueCount:source.segments.length,range:{start:Math.min(...source.segments.map(c=>c.start)),end:Math.max(...source.segments.map(c=>c.end))},message:'将分析所选源在当前工程中的全部有效字幕，并更新该素材候选及分析缓存；不修改当前时间线。'});return true;}catch(e){emit({phase:'error',message:e instanceof Error?e.message:String(e)});return false;}finally{pending=false;}},
  async confirm(){if(pending||disposed||!prepared||state.phase!=='confirm')return false;pending=true;const captured=prepared;prepared=null;emit({phase:'running',message:'正在真实分析所选源字幕，不使用固定事件或规则回退。'});
   try{const saved=await p.save.flush();if(disposed)return false;if(!saved)throw Error('当前工程保存失败，未分析。');const source=await checkTranscript();if(disposed)return false;if(identity()!==captured.identity||source.cacheKey!==captured.transcript)throw Error('工程或源字幕已变化，请重新准备。');const revision=p.save.readState().revision;if(revision===null)throw Error('缺少当前工程修订。');const result=await p.analyze(source.project,captured.connection,revision);if(disposed)return false;
    const count=result.events.length;emit({phase:'done',message:`本次真实分析返回 ${count} 个候选。${count===0?'没有可用新候选，不会伪造或自动改用规则。':'已更新所选素材候选。'}${result.adviceError?'总结与建议阶段失败：'+result.adviceError:''} 时间线未修改；下方重新读取的是当前已保存列表，不是额外生成。`});await p.onComplete();return true;
   }catch(e){emit({phase:'error',message:`${e instanceof Error?e.message:String(e)} 未自动重试、未使用规则回退；若请求已发出，请读取素材候选核对实际结果。`});return false;}finally{pending=false;}},
  cancel(){if(!pending){prepared=null;emit(initialCandidateAnalysis());}},dispose(){disposed=true;prepared=null;},
 };
}
