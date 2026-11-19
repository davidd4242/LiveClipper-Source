import {getEditor,saveEditor} from '../../api/client';
import {commitPrimaryTranscriptReplacement} from '../editor/captionOverrides';
import { useEffect, useRef, useState } from 'react';

export function SourceSrtImport({ clipId, disabled, onBusy, onImported }: {
  clipId: string; disabled: boolean; onBusy: (busy: boolean) => void; onImported: () => void;
}) {
  const input = useRef<HTMLInputElement>(null);
  const live = useRef(true);
  const pending = useRef(false);
  const controller = useRef<AbortController | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  useEffect(() => { live.current = true; return () => { live.current = false; controller.current?.abort(); onBusy(false); }; }, [onBusy]);
  const upload = async (file: File | undefined) => {
    if (!file || disabled || pending.current || !live.current) return;
    if (!file.name.toLowerCase().endsWith('.srt') || file.size < 1 || file.size > 50_000_000) {
      setMessage('请选择不超过 50 MB 的 SRT 文件。'); return;
    }
    pending.current = true; setBusy(true); onBusy(true); setMessage('正在导入源 SRT…');
    const abort = new AbortController(); controller.current = abort;
    try {
      const form = new FormData(); form.append('file', file);
      // Match existing studio/music upload: materialize file bytes for WebView2 Request.Content.
      const multipart = new Response(form);
      const bytes = await multipart.arrayBuffer();
      if (!live.current || abort.signal.aborted) return;
      const base = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');
      const response = await fetch(`${base}/api/studio/clips/${encodeURIComponent(clipId)}/source-srt`, { method: 'POST', headers: Object.fromEntries(multipart.headers), body: bytes, signal: abort.signal });
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || `HTTP ${response.status}`);
      if (live.current) { setMessage('源 SRT 已导入；时间线未改动。请重新载入工程后提取事件。'); onImported(); }
    } catch (error) {
      if (live.current) setMessage(`导入未确认完成：${error instanceof Error ? error.message : String(error)}。请刷新核对，未自动重试。`);
    } finally {
      pending.current = false;
      if (live.current) { setBusy(false); onBusy(false); }
    }
  };
  return <section aria-label="导入源 SRT" aria-busy={busy}>
    <input ref={input} type="file" accept=".srt" hidden onChange={event => { const file = event.currentTarget.files?.[0]; event.currentTarget.value = ''; void upload(file); }} />
    <button type="button" disabled={disabled || busy} onClick={() => input.current?.click()}>导入源 SRT（仅无源字幕时）</button>
    <p>只关联当前视频，不替换已有源字幕，不自动修改时间线，也不调用模型。</p>
    {message && <p role="status">{message}</p>}
  </section>;
}

export function ReplaceSourceCaptions({clipId,epoch,disabled,getSession,onBusy,onReload}:{clipId:string;epoch:string;disabled:boolean;getSession:()=>Parameters<typeof commitPrimaryTranscriptReplacement>[0]['session']|null;onBusy:(busy:boolean)=>void;onReload:()=>void}){
 const [confirmed,setConfirmed]=useState(false),[busy,setBusy]=useState(false),[message,setMessage]=useState('');const live=useRef(true),lock=useRef(false);
 useEffect(()=>{live.current=true;return()=>{live.current=false;};},[clipId,epoch]);
 const replace=async()=>{if(disabled||!confirmed||lock.current)return;const session=getSession();if(!session)return;lock.current=true;setBusy(true);onBusy(true);setMessage('正在读取当前源最新成功转录，尚未替换…');try{const ok=await commitPrimaryTranscriptReplacement({clipId,epoch,session,getLatest:async()=>{const payload=await getEditor(clipId);if(!payload.clip)throw new Error('独立工程未指定主素材，不能替换源字幕。');return {...payload,clip:payload.clip};},isCurrent:()=>live.current,confirm:count=>window.confirm(`确认用当前源最新 ${count} 句转录替换本工程该源的全部旧字幕？\n\n包括该源已手工修改的时间线字幕；保留视频剪点、其他源字幕、其他工程及弹幕聊天记录。不会创建副本，也不会重新识别。`),put:(project,revision,e)=>saveEditor(clipId,project,revision,e),reload:onReload});if(live.current)setMessage(ok?'本工程主源字幕已替换并保存。':'已取消，旧字幕未替换。');}catch(e){if(live.current)setMessage(`${e instanceof Error?e.message:String(e)} 未自动重试。`);}finally{lock.current=false;onBusy(false);if(live.current)setBusy(false);}};
 return <section className="silence-card" aria-label="用最新转录替换本工程字幕"><h3>替换本工程字幕</h3><p>先完成“更新字幕”本地转录，再执行替换。读取最新转录结果，不使用工程中冻结的旧字幕。仅替换当前主源（含旧手工字幕），不改视频、不碰其他源或弹幕聊天记录，不创建副本。</p><label><input type="checkbox" checked={confirmed} disabled={disabled||busy} onChange={e=>setConfirmed(e.target.checked)}/>我确认当前源已完成新的本地转录，并同意覆盖该源旧字幕及手工字幕</label><button type="button" disabled={disabled||busy||!confirmed} onClick={()=>void replace()}>{busy?'正在核对并替换…':'用最新转录替换本工程字幕'}</button>{message&&<p role="status">{message}</p>}</section>;
}
