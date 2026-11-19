import type {PauseParameters,PausePreview} from './pauseReviewPlanner';
import type { AiConnectionBody } from '../editor/aiAdvice';
import type { EditorProject } from '../../api/types';
import type { CopySaveSession } from './copyBridge';
import type { SilenceNative } from './silencePreviewController';
import { createMultiClipController, reviewRanges } from './multiClipController';
import type { AutomaticCopyResult, MultiClipPreview, MultiClipState, SemanticOnlineResult } from './multiClipController';

export interface IndependentAutomaticPorts {
 projectId:string; sourceId:string; epoch:string; registered:()=>boolean;
 save:Omit<CopySaveSession,'readState'> & {flush:()=>Promise<boolean>;snapshot:()=>{clipId:string;epoch:string};readState:()=>ReturnType<CopySaveSession['readState']>&{hasUnsaved?:boolean}};
 native:SilenceNative;
 api:{
  planPauses?:(projectId:string,sourceId:string,project:EditorProject,ranges:{start:number;end:number;name:string}[],parameters:PauseParameters,signal:AbortSignal)=>Promise<PausePreview>;
  verifyPauses?:(projectId:string,sourceId:string,project:EditorProject,preview:PausePreview,signal:AbortSignal)=>Promise<unknown>;
  load:(projectId:string,sourceId:string,project:EditorProject,signal:AbortSignal)=>Promise<MultiClipPreview>;
  verify:(projectId:string,sourceId:string,project:EditorProject,data:MultiClipPreview,signal:AbortSignal)=>Promise<unknown>;
  connection:()=>Promise<AiConnectionBody|null>;
  semantic:(projectId:string,sourceId:string,project:EditorProject,state:MultiClipState,connection:AiConnectionBody,signal:AbortSignal)=>Promise<SemanticOnlineResult>;
  copy:(projectId:string,sourceId:string,project:EditorProject,state:MultiClipState,ranges:{start:number;end:number}[],revision:number,beforeRanges:{start:number;end:number}[])=>Promise<AutomaticCopyResult>;
  restoreStatus:(projectId:string)=>Promise<{available:boolean}>;
  restore:(projectId:string,revision:number)=>Promise<unknown>;
 };
 restoreBlocked:()=>boolean; onRestored:()=>void; onState:(state:MultiClipState)=>void;
}
/** The legacy planner's clipId remains the explicit source, never the persistence identity. */
export function createIndependentAutomaticController(p:IndependentAutomaticPorts){
 let alive=true,operation=0,preparing=false;
 const check=()=>{
  if(!alive)throw Error('旧工作台已关闭。');
  const identity=p.save.snapshot(),saved=p.save.readState();
  if(!p.registered()||identity.clipId!==p.projectId||identity.epoch!==p.epoch)throw Error('工程或所选素材已变化，请重新选择。');
  if(saved.hasUnsaved)throw Error('请等待当前工程保存完成，再读取或生成独立结果。');
 };
 const controller=createMultiClipController({clipId:p.sourceId,
  save:{readState:()=>{check();return p.save.readState();},projectCurrentSnapshot:stamp=>{check();return p.save.projectCurrentSnapshot(stamp);}},
  native:p.native,
  planPauses:p.api.planPauses?(project,ranges,parameters,signal)=>{check();return p.api.planPauses!(p.projectId,p.sourceId,project,ranges,parameters,signal);}:undefined,
  verifyPauses:p.api.verifyPauses?async(project,preview,signal)=>{check();await p.api.verifyPauses!(p.projectId,p.sourceId,project,preview,signal);}:undefined,
  load:(project,signal)=>p.api.load(p.projectId,p.sourceId,project,signal),
  verify:async(data,project,signal)=>{check();await p.api.verify(p.projectId,p.sourceId,project,data,signal);},
  connection:p.api.connection,
  semanticRequest:(project,state,connection,signal)=>{check();return p.api.semantic(p.projectId,p.sourceId,project,state,connection,signal);},
  copy:(project,state,ranges,revision)=>{check();return p.api.copy(p.projectId,p.sourceId,project,state,ranges,revision,reviewRanges(state));},
  restoreStatus:()=>p.api.restoreStatus(p.projectId),
  restore:revision=>{check();return p.api.restore(p.projectId,revision);},
  restoreBlocked:p.restoreBlocked,onRestored:()=>{if(alive)p.onRestored();},onState:p.onState,
 });
 const perform=async<T>(action:()=>Promise<T>)=>{
  if(!alive||preparing)return false;
  const ticket=++operation;preparing=true;
  try{
   const identity=p.save.snapshot(),stamp=p.native.hostSnapshot();
   if(!p.registered()||identity.clipId!==p.projectId||identity.epoch!==p.epoch)throw Error('工程或所选素材已变化。');
   const saved=await p.save.flush();
   if(!alive||ticket!==operation)return false;
   if(!saved)throw Error('当前工程保存失败，未发出请求。');
   check();const after=p.native.hostSnapshot();
   if(stamp.generation!==after.generation||stamp.engineRevision!==after.engineRevision)throw Error('保存期间时间线已变化，请重新读取。');
   return await action();
  }catch(e){if(!alive||ticket!==operation)return false;throw e;}
  finally{if(ticket===operation)preparing=false;}
 };
 return {...controller,
  planPauses:()=>perform(()=>controller.planPauses()),
  load:()=>perform(()=>controller.load()),
  prepareSemantic:()=>perform(()=>controller.prepareSemantic()),
  createAutomatic:()=>perform(()=>controller.createAutomatic()),
  apply:()=>perform(()=>controller.apply()),
  restoreAutomatic:()=>perform(()=>controller.restoreAutomatic()),
  close(){if(controller.getState().phase==='applying'||controller.getState().semantic.stage==='requesting')return;operation++;preparing=false;controller.close();},
  dispose(){alive=false;operation++;preparing=false;controller.dispose();},
 };
}
