import {useEffect,useRef,useState} from 'react';
import {resolveBilibiliUid,resolveLiveInput} from './liveApi';
import {createRoomLookup} from './autoRoomLookup';
import type {RoomLookupState} from './autoRoomLookup';
export function RoomEntry({busy,input,onInputChange,onStart}:{busy:boolean;input:string;onInputChange:(input:string)=>void;onStart:(roomId:number,autoRecord:boolean,cacheEnabled:boolean)=>void}){
 const [cacheEnabled,setCacheEnabled]=useState(true);
 const [autoRecord,setAutoRecord]=useState(false);
 const [state,setState]=useState<RoomLookupState>({input:'',busy:false,candidates:[],selected:null,error:'',warnings:[]});
 const controller=useRef<ReturnType<typeof createRoomLookup>|null>(null);
 useEffect(()=>{const current=createRoomLookup({room:resolveLiveInput,uid:resolveBilibiliUid},setState);controller.current=current;return()=>{current.dispose();if(controller.current===current)controller.current=null;};},[]);
 useEffect(()=>{controller.current?.change(input);setAutoRecord(false);setCacheEnabled(true);},[input]);
 return <section className="lw-room-entry" aria-label="添加直播间">
  <form className="lw-add" onSubmit={event=>{event.preventDefault();void controller.current?.lookup();}}>
   <input aria-label="直播间房号、B站用户 UID 或链接" inputMode="text" placeholder="房间号、UID 或 B站链接" value={input} onChange={event=>{controller.current?.change(event.target.value);onInputChange(event.target.value);}}/>
   <button type="submit" className="live-btn live-btn--primary" disabled={busy||state.busy||!input.trim()}>{state.busy?'识别中…':'识别直播间'}</button>
  </form>
  <p className="lw-room__hint">识别仅查询，不会开始监控或录制；数字同时核对房间号与 UID。</p>
  {state.error&&<p role="alert" className="lw-room-error">{state.error}</p>}
  {state.warnings.map(warning=><p role="status" key={warning}>{warning}</p>)}
  {state.candidates.length>0&&<div>
   {state.candidates.length>1&&<p>该数字对应不同直播间，请明确选择目标：</p>}
   {state.candidates.map(candidate=><label key={candidate.roomId} className="lw-room-candidate">
    {state.candidates.length>1&&<input type="radio" name="resolved-live-room" checked={state.selected===candidate.roomId} onChange={()=>controller.current?.select(candidate.roomId)}/>}
    <span>{candidate.name||'主播名称未返回'} · 房间 {candidate.roomId} · {candidate.status}（{candidate.from.join(' / ')}）{candidate.title&&` · ${candidate.title}`}</span>
   </label>)}
   <p className="lw-room__hint">默认只保存高光：高光临时缓存写入 F 盘并自动过期，不保留整场。两项都关闭可纯预览，不写媒体文件。</p>
   <label><input type="checkbox" checked={cacheEnabled} disabled={busy} onChange={event=>setCacheEnabled(event.target.checked)}/>高光临时缓存（F盘写入、自动过期）</label>
   <label><input type="checkbox" checked={autoRecord} disabled={busy} onChange={event=>setAutoRecord(event.target.checked)}/>保留整场自动录播</label>
   <button type="button" className="live-btn live-btn--primary" disabled={busy||state.busy||state.selected===null} onClick={()=>{if(state.selected!==null)onStart(state.selected,autoRecord,cacheEnabled);}}>{autoRecord?'开始监控并保留整场':cacheEnabled?'开始监控并缓存高光':'仅在线预览（不写媒体文件）'}</button>
  </div>}
 </section>;
}
