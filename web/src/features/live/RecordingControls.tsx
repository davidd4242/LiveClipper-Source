import {pickRecordingDirectory} from './desktopPickRecordingDirectory';
import {useEffect,useRef,useState} from 'react';
import {getRecordingDirectory,saveRecordingDirectory,setRoomRecording,setRoomCache} from './liveApi';
import type {LegacyRoomStatus} from './liveApi';

export function RecordingDirectory(){
 const [path,setPath]=useState('');const [saved,setSaved]=useState<string|null>(null);const [busy,setBusy]=useState(false);const [message,setMessage]=useState('');const lock=useRef(false);
 useEffect(()=>{let active=true;void getRecordingDirectory().then(result=>{if(active){setPath(result.recordingDirectory);setSaved(result.recordingDirectory);}},error=>{if(active)setMessage(String(error));});return()=>{active=false;};},[]);
 const browse=async()=>{if(lock.current)return;lock.current=true;setBusy(true);setMessage('');try{const chosen=await pickRecordingDirectory();if(chosen!==null)setPath(chosen);}catch(error){setMessage(error instanceof Error?error.message:String(error));}finally{lock.current=false;setBusy(false);}};
 const save=async()=>{if(lock.current)return;lock.current=true;setBusy(true);setMessage('');try{const requested=path.trim();await saveRecordingDirectory(requested);const actual=await getRecordingDirectory();setSaved(actual.recordingDirectory);setPath(actual.recordingDirectory);setMessage(actual.recordingDirectory===requested?'录制目录已保存并回读确认；不移动已有录制文件，不更改数据库目录。':`服务器返回的生效目录为：${actual.recordingDirectory||'未设置'}，与提交值不同，请核对。`);}catch(error){setMessage(String(error));}finally{lock.current=false;setBusy(false);}};
 return <section className="lw-recording-directory" aria-label="独立录制目录"><label>录制目录<span className="lw-recording-directory__input-row"><input aria-label="录制目录" value={path} disabled={busy||saved===null} placeholder="例如 F:\LiveClipperRecordings" onChange={e=>setPath(e.target.value)}/><button type="button" className="live-btn" disabled={busy||saved===null} onClick={()=>void browse()}>浏览…</button></span></label><button type="button" className="live-btn" disabled={busy||saved===null||!path.trim()||path.trim()===saved} onClick={()=>void save()}>{busy?'处理中…':'保存录制目录'}</button><p className="lw-room__hint">{saved===null?'正在读取目录':saved?`当前录制目录：${saved}`:'尚未设置录制目录，不能启用高光缓存或整场录播。'} 请先关闭各房高光缓存和整场录播再修改目录；这里只修改录制目录，不切换数据库或工程根目录。</p>{message&&<p role="status">{message}</p>}</section>;
}
export function RoomRecordingControl({room,onChanged,compact=false}:{room:LegacyRoomStatus;compact?:boolean;onChanged:()=>void|Promise<unknown>}){
 const [busy,setBusy]=useState(false);const [message,setMessage]=useState('');const lock=useRef(false);
 const toggle=async(kind:'full'|'cache')=>{if(lock.current)return;lock.current=true;setBusy(true);setMessage('');try{
  if(kind==='full')await setRoomRecording(room.roomId,!room.autoRecord);else await setRoomCache(room.roomId,!room.cacheEnabled);
  await onChanged();setMessage(kind==='full'?(room.autoRecord?'整场留存已关闭，已有文件保留；是否继续高光临时缓存按缓存开关执行。':'整场自动录播已开启，开播时保留整场。'):(room.cacheEnabled?'高光临时缓存已关闭；整场开关也关闭时仅在线预览，不写媒体文件。':'高光临时缓存已开启，将写入设置的 F 盘目录并自动过期清理，不保留整场。'));
 }catch(error){setMessage(String(error));}finally{lock.current=false;setBusy(false);}};
 return <div className="lw-recording-control" onClick={event=>event.stopPropagation()}>
  <span hidden={compact}>{room.captureMode==='full-recording'?'整场留存':room.captureMode==='highlight-cache'?'仅高光临时缓存':'纯预览 · 不写媒体文件'}{room.cacheActive?' · 缓存采集中':''}</span>
  <button type="button" className="live-btn" disabled={busy||!room.monitoring} aria-label={`${room.cacheEnabled?'关闭':'开启'}房间 ${room.roomId} 高光临时缓存`} onClick={()=>void toggle('cache')}>{busy?'正在切换…':`高光缓存：${room.cacheEnabled?'开':'关'}`}</button>
  <button type="button" className="live-btn" disabled={busy||!room.monitoring} aria-label={`${room.autoRecord?'关闭':'开启'}房间 ${room.roomId} 整场自动录播`} onClick={()=>void toggle('full')}>{busy?'正在切换…':`整场自动录播：${room.autoRecord?'开':'关'}`}</button>
  <p hidden={compact} className="lw-room__hint">高光缓存写入 F 盘临时目录并自动过期，仅保留触发的高光；整场开关独立。两者都关为纯预览，不写媒体文件（不代表应用零磁盘写入）。已有录播不会删除。</p>
  {message&&<p role="status">房间 {room.roomId}：{message}</p>}
 </div>;
}
export function monitorStartedMessage(roomId:number,autoRecord:boolean,cacheEnabled:boolean):string{
 return `房间 ${roomId} 已开始监控：${autoRecord?'整场自动录播已开启，开播时保留整场。':cacheEnabled?'高光临时缓存已开启，写入 F 盘并自动过期，不保留整场。':'纯预览，不写媒体文件。'}`;
}
