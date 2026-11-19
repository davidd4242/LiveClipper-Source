import {detectReviewPauses,verifyReviewPauses} from '../../api/client';
import type {PauseParameters} from './pauseReviewPlanner';
import {getAiSettings} from '../../api/settings';
import {semanticConnection} from './semanticConnection';
import {requestSemanticReview,createAutomaticRoughcut,getAutomaticRestore,restoreAutomaticRoughcut} from '../../api/client';
import type {SemanticChoice} from './semanticReviewPlanner';
import {useEffect,useRef,useState} from 'react';
import type {CopySaveSession} from './copyBridge';
import type {SilenceNative} from './silencePreviewController';
import {createMultiClipController,initialMultiClipState,reviewRanges} from './multiClipController';
import {readMultiClipPreview,verifyMultiClipPreview} from '../../api/client';
export function useMultiClipReview(options:{clipId:string|null;identity:object|null;ready:boolean;epoch:string;reload:()=>void;restoreBlocked:()=>boolean;getSaveSession:()=>CopySaveSession|null}) {
 const liveOptions=useRef(options);liveOptions.current=options;
 const active=useRef<ReturnType<typeof createMultiClipController>|null>(null),[state,setState]=useState(initialMultiClipState);
 useEffect(()=>{setState(initialMultiClipState());const save=options.getSaveSession(),native=(window as unknown as {FCBridge?:SilenceNative&{onMultiClipReviewRequest?:(cb:()=>void)=>()=>void;onDirty?:(cb:()=>void)=>()=>void}}).FCBridge;
 if(!options.ready||!options.identity||!options.clipId||!save||!native)return;
 const id=options.clipId,controller=createMultiClipController({clipId:id,save,native,planPauses:(project,ranges,parameters,signal)=>detectReviewPauses(id,project,ranges,parameters,signal),verifyPauses:async(project,preview,signal)=>{await verifyReviewPauses(id,project,preview,signal);},load:(project,signal)=>readMultiClipPreview(id,project,signal),verify:async(data,project,signal)=>{await verifyMultiClipPreview(id,project,data,signal);},connection:async()=>semanticConnection(await getAiSettings()),semanticRequest:(project,state,connection,signal)=>requestSemanticReview(id,project,state,connection,signal),copy:(project,state,ranges,revision)=>createAutomaticRoughcut(id,'全自动粗剪',project,state.data!,ranges,revision,options.epoch,reviewRanges(state)),restoreStatus:()=>getAutomaticRestore(id),restore:revision=>restoreAutomaticRoughcut(id,revision,options.epoch),restoreBlocked:()=>liveOptions.current.restoreBlocked(),onRestored:()=>liveOptions.current.reload(),onState:setState});active.current=controller;
 const offMenu=native.onMultiClipReviewRequest?.(()=>controller.open()),offDirty=native.onDirty?.(()=>{if(['ready','loading'].includes(controller.getState().phase))controller.invalidate();});
 return()=>{offMenu?.();offDirty?.();controller.dispose();if(active.current===controller)active.current=null;};
 },[options.clipId,options.identity,options.ready,options.epoch]);
 return {state,setPauseEnabled:(enabled:boolean)=>active.current?.setPauseEnabled(enabled),pauseParameters:(p:Partial<PauseParameters>)=>active.current?.pauseParameters(p),pauseDecision:(id:string,remove:boolean,acknowledged:boolean)=>active.current?.pauseDecision(id,remove,acknowledged),planPauses:()=>active.current?.planPauses(),restoreAutomatic:()=>active.current?.restoreAutomatic(),setMode:(mode:'manual'|'automatic')=>active.current?.setMode(mode),authorizeAutomatic:(value:boolean)=>active.current?.authorizeAutomatic(value),createAutomatic:()=>active.current?.createAutomatic(),prepareSemantic:()=>active.current?.prepareSemantic(),confirmSemantic:()=>active.current?.confirmSemantic(),cancelSemanticConfirm:()=>active.current?.cancelSemanticConfirm(),clearSemantic:()=>active.current?.clearSemantic(),semanticDecision:(id:string,choice:SemanticChoice,risks:string[])=>active.current?.semanticDecision(id,choice,risks),close:()=>active.current?.close(),load:()=>active.current?.load(),apply:()=>active.current?.apply(),toggle:(id:string)=>active.current?.toggle(id),update:(id:string,patch:{start?:number;end?:number})=>active.current?.update(id,patch),restore:(id:string)=>active.current?.restore(id),expand:(id:string)=>active.current?.expand(id),move:(id:string,delta:-1|1)=>active.current?.move(id,delta)};
}

/** UI commands may be implemented by either the media or independent-project controller. */
export type MultiClipReviewModel = {
 [K in keyof ReturnType<typeof useMultiClipReview>]: ReturnType<typeof useMultiClipReview>[K] extends (...args:infer A)=>unknown
  ? (...args:A)=>unknown : ReturnType<typeof useMultiClipReview>[K];
};
