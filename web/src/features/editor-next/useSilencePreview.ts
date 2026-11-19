import { useEffect, useRef, useState } from 'react';
import type { CopySaveSession } from './copyBridge';
import { createSilenceController, initialSilenceState } from './silencePreviewController';
import type { SilenceNative } from './silencePreviewController';
import { detectReviewPauses, verifyReviewPauses } from '../../api/client';
export function useSilencePreview(options: {clipId:string|null;identity:object|null;ready:boolean;duration:number;getSaveSession:()=>CopySaveSession|null;onOpen?:()=>void}) {
 const onOpen=useRef(options.onOpen);onOpen.current=options.onOpen;
 const active=useRef<ReturnType<typeof createSilenceController>|null>(null);
 const [state,setState]=useState(initialSilenceState);
 useEffect(()=>{
  setState(initialSilenceState());
  const save=options.getSaveSession(),native=(window as unknown as {FCBridge?:SilenceNative}).FCBridge;
  if(!options.clipId||!options.ready||!options.identity||!save||!native||!options.duration)return;
  const id=options.clipId;
  const controller=createSilenceController({clipId:id,start:0,end:options.duration,save,native,
   detect:(parameters,project,signal,range)=>detectReviewPauses(id,project,[range],parameters,signal),
   verify:async(preview,project,signal)=>{await verifyReviewPauses(id,project,preview,signal);},onState:setState});
  active.current=controller;
  const offMenu=(native as SilenceNative & {onSilencePreviewRequest?:(callback:()=>void)=>()=>void}).onSilencePreviewRequest?.(()=>{controller.open();onOpen.current?.();});
  const off=(native as SilenceNative & {onDirty?:(callback:()=>void)=>()=>void}).onDirty?.(()=>{
   const phase=controller.getState().phase;if(phase==='ready'||phase==='detecting')controller.invalidate();
  });
  return()=>{offMenu?.();off?.();controller.dispose();if(active.current===controller)active.current=null;};
 },[options.clipId,options.identity,options.ready,options.duration]);
 return {state,open:()=>active.current?.open(),close:()=>active.current?.close(),detect:()=>active.current?.detect(),apply:()=>active.current?.apply(),
  range:(value:Partial<{start:number;end:number}>)=>active.current?.range(value),
  parameters:(value:Parameters<ReturnType<typeof createSilenceController>['parameters']>[0])=>active.current?.parameters(value),
  update:(id:string,value:Parameters<ReturnType<typeof createSilenceController>['update']>[1])=>active.current?.update(id,value),restore:(id?:string)=>active.current?.restore(id)};
}
