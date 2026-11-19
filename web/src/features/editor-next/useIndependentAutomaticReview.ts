import {detectReviewPauses,verifyReviewPauses} from '../../api/client';
import type {PauseParameters} from './pauseReviewPlanner';
import {createIndependentOperationGate} from './independentOperationGate';
import {createIndependentCandidateAnalysis,initialCandidateAnalysis} from './independentCandidateAnalysis';
import {analyzeIndependentSource} from '../../api/client';
import {adviceConnection} from '../editor/aiAdvice';
import {useEffect,useRef,useState} from 'react';
import {getAiSettings} from '../../api/settings';
import {readIndependentMultiClipPreview,verifyIndependentMultiClipPreview,requestIndependentSemanticReview,createIndependentAutomaticRoughcut,getAutomaticRestore,restoreAutomaticRoughcut} from '../../api/client';
import {semanticConnection} from './semanticConnection';
import {initialMultiClipState} from './multiClipController';
import {createIndependentAutomaticController} from './independentAutomaticController';
import type {IndependentAutomaticPorts} from './independentAutomaticController';
import type {SemanticChoice} from './semanticReviewPlanner';
import type {SilenceNative} from './silencePreviewController';

export function useIndependentAutomaticReview(options:{projectId:string|null;sourceId:string|null;epoch:string;generation:number|undefined;identity:object|null;ready:boolean;registered:()=>boolean;getSaveSession:()=>IndependentAutomaticPorts['save']|null;reload:()=>void;restoreBlocked:()=>boolean;onBusy:(busy:boolean)=>void}){
 const gate=useRef(createIndependentOperationGate());
 const latest=useRef(options);latest.current=options;
 const candidate=useRef<ReturnType<typeof createIndependentCandidateAnalysis>|null>(null),[analysis,setAnalysis]=useState(initialCandidateAnalysis);
 const active=useRef<ReturnType<typeof createIndependentAutomaticController>|null>(null),[state,setState]=useState(initialMultiClipState);
 const publishBusy=()=>{const review=active.current?.getState(),analysis=candidate.current?.getState();latest.current.onBusy(gate.current.isBusy()||review?.phase==='applying'||review?.phase==='loading'||['preparing','requesting'].includes(review?.semantic.stage??'')||['preparing','running'].includes(analysis?.phase??''));};
 useEffect(()=>{
  gate.current.reset();setState(initialMultiClipState());setAnalysis(initialCandidateAnalysis());
  const save=options.getSaveSession(),native=(window as unknown as {FCBridge?:SilenceNative&{onDirty?:(cb:()=>void)=>()=>void}}).FCBridge;
  if(!options.ready||!options.identity||!options.projectId||!options.sourceId||!save||!native||!options.registered())return;
  const c=createIndependentAutomaticController({projectId:options.projectId,sourceId:options.sourceId,epoch:options.epoch,registered:()=>latest.current.registered(),save,native,
   api:{planPauses:(pid,sid,project,ranges,parameters,signal)=>detectReviewPauses(sid,project,ranges,parameters,signal,pid),verifyPauses:(pid,sid,project,preview,signal)=>verifyReviewPauses(sid,project,preview,signal,pid),load:readIndependentMultiClipPreview,verify:verifyIndependentMultiClipPreview,connection:async()=>semanticConnection(await getAiSettings()),semantic:requestIndependentSemanticReview,
    copy:(projectId,sourceId,project,state,ranges,revision,before)=>createIndependentAutomaticRoughcut(projectId,sourceId,'全自动单源粗剪',project,state.data!,ranges,revision,options.epoch,before),
    restoreStatus:getAutomaticRestore,restore:(id,revision)=>restoreAutomaticRoughcut(id,revision,options.epoch)},
   restoreBlocked:()=>latest.current.restoreBlocked(),onRestored:()=>{if(active.current===c)latest.current.reload();},
   onState:next=>{if(active.current!==c)return;setState(next);publishBusy();}});
  active.current=c;c.open();
  const analysisController=createIndependentCandidateAnalysis({projectId:options.projectId,sourceId:options.sourceId,epoch:options.epoch,save,native,registered:()=>latest.current.registered(),
   connection:async()=>adviceConnection(await getAiSettings()),transcript:async project=>{const data=await readIndependentMultiClipPreview(options.projectId!,options.sourceId!,project,new AbortController().signal);return {segments:data.transcript,cacheKey:data.transcriptCacheKey};},analyze:(project,connection,revision)=>analyzeIndependentSource(options.projectId!,options.sourceId!,project,connection,revision,options.epoch),
   onState:next=>{if(candidate.current!==analysisController)return;setAnalysis(next);publishBusy();},onComplete:async()=>{if(active.current!==c)return;c.invalidate();await c.load();}});
  candidate.current=analysisController;
  const off=native.onDirty?.(()=>{if(['ready','loading'].includes(c.getState().phase))c.invalidate();});
  return()=>{off?.();analysisController.dispose();if(candidate.current===analysisController)candidate.current=null;c.dispose();if(active.current===c)active.current=null;gate.current.reset();publishBusy();};
 },[options.projectId,options.sourceId,options.epoch,options.generation,options.identity,options.ready]);
 const run=async(action:()=>Promise<unknown>|undefined)=>{const owner=active.current;if(!owner)return false;try{return await gate.current.run(action,publishBusy);}catch(e){if(active.current===owner)setState(previous=>({...previous,phase:'error',message:e instanceof Error?e.message:String(e)}));return false;}};
 return {state,analysis,setPauseEnabled:(enabled:boolean)=>active.current?.setPauseEnabled(enabled),pauseParameters:(p:Partial<PauseParameters>)=>active.current?.pauseParameters(p),pauseDecision:(id:string,remove:boolean,acknowledged:boolean)=>active.current?.pauseDecision(id,remove,acknowledged),planPauses:()=>run(()=>active.current?.planPauses()),open:()=>active.current?.open(),prepareCandidates:()=>run(()=>candidate.current?.prepare()),confirmCandidates:()=>run(()=>candidate.current?.confirm()),cancelCandidates:()=>{if(!gate.current.isBusy())candidate.current?.cancel();},restoreAutomatic:()=>run(()=>active.current?.restoreAutomatic()),setMode:(mode:'manual'|'automatic')=>active.current?.setMode(mode),authorizeAutomatic:(value:boolean)=>active.current?.authorizeAutomatic(value),createAutomatic:()=>run(()=>active.current?.createAutomatic()),prepareSemantic:()=>run(()=>active.current?.prepareSemantic()),confirmSemantic:()=>run(()=>active.current?.confirmSemantic()),cancelSemanticConfirm:()=>active.current?.cancelSemanticConfirm(),clearSemantic:()=>active.current?.clearSemantic(),semanticDecision:(id:string,choice:SemanticChoice,risks:string[])=>active.current?.semanticDecision(id,choice,risks),close:()=>active.current?.close(),load:()=>run(()=>active.current?.load()),apply:()=>run(()=>active.current?.apply()),toggle:(id:string)=>active.current?.toggle(id),update:(id:string,patch:{start?:number;end?:number})=>active.current?.update(id,patch),restore:(id:string)=>active.current?.restore(id),expand:(id:string)=>active.current?.expand(id),move:(id:string,delta:-1|1)=>active.current?.move(id,delta)};
}
