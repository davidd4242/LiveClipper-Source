import {useEffect,useRef,useState} from 'react';
import './NewProjectDialog.css';
import type {FullEngineBridge} from '../../engine/fullEngineSession';
import {applySequenceSettings} from './sequenceSettings';
import type {SequenceSettingsSnapshot} from './sequenceSettings';
export function SequenceSettingsDialog({initial,bridge,save,onClose}:{initial:SequenceSettingsSnapshot;bridge:FullEngineBridge;save:()=>Promise<boolean>;onClose:()=>void}){
 const dialog=useRef<HTMLDialogElement>(null),lock=useRef(false);
 const [width,setWidth]=useState(initial.width),[height,setHeight]=useState(initial.height),[fps,setFps]=useState(initial.fps);
 const [busy,setBusy]=useState(false),[applied,setApplied]=useState(false),[error,setError]=useState('');
 useEffect(()=>{dialog.current?.showModal();},[]);
 const apply=async()=>{if(lock.current)return;lock.current=true;setBusy(true);setError('');try{
  if(!applied){applySequenceSettings(bridge,initial,{width,height,fps});setApplied(true);}
  if(await save())onClose();else setError('设置已应用，但尚未保存。请重试保存，或关闭后查看保存栏；不会重复应用设置。');
 }catch(e){setError(e instanceof Error?e.message:String(e));}finally{lock.current=false;setBusy(false);}};
 return <dialog ref={dialog} className="new-project-dialog" aria-label="序列设置" onCancel={event=>{event.preventDefault();if(!busy)onClose();}}>
  <h2>序列设置</h2><p>调整工程画布，不裁切或改写原素材。帧率变更保留秒级时序并对齐新帧网格；可一次撤销。</p>
  <fieldset disabled={busy||applied}>
   <label>画布宽度<input aria-label="画布宽度" type="number" min={16} max={16384} step={2} value={width} onChange={e=>setWidth(Number(e.target.value))}/></label>
   <label>画布高度<input aria-label="画布高度" type="number" min={16} max={16384} step={2} value={height} onChange={e=>setHeight(Number(e.target.value))}/></label>
   <label>帧率<select aria-label="帧率" value={fps} onChange={e=>setFps(Number(e.target.value))}>{[24,25,30,50,60].map(value=><option key={value} value={value}>{value} fps</option>)}</select></label>
  </fieldset>
  <p>竖屏可设 1080 × 1920，横屏可设 1920 × 1080。MP4 编码要求宽高为偶数。</p>
  {error&&<p role="alert">{error}</p>}
  <button type="button" disabled={busy} onClick={()=>void apply()}>{busy?'正在保存…':applied?'重试保存':'应用并保存'}</button>
  <button type="button" disabled={busy} onClick={onClose}>{applied?'关闭':'取消'}</button>
 </dialog>;
}
