import {pauseRiskLabels,pauseEvidenceLabel,captionConflictLabels} from './pauseReviewPlanner';
import { useEffect, useRef, useState } from 'react';
import { getVideoUrl } from '../../api/client';
import { silenceKeptRanges } from './silencePreviewController';
import type { useSilencePreview } from './useSilencePreview';
import './silence-preview.css';
const PAGE_SIZE = 40;
const clock = (seconds: number) => `${Math.floor(seconds / 60).toString().padStart(2, '0')}:${(seconds % 60).toFixed(2).padStart(5, '0')}`;
export function SilenceBoundaryInput({value,disabled,max,onCommit}:{value:number;disabled:boolean;step:number;max:number;onCommit:(value:number)=>void}) {
 const [text,setText]=useState<string|null>(null),changed=useRef(false);
 return <input type="number" step="any" min={0} max={max} value={text??value.toFixed(3)} disabled={disabled}
  onFocus={()=>{changed.current=false;setText(value.toFixed(3));}}
  onChange={event=>{changed.current=true;setText(event.target.value);}}
  onBlur={()=>{if(changed.current&&text!==null&&text.trim()!==''&&Number.isFinite(Number(text)))onCommit(Number(text));changed.current=false;setText(null);}}
  onKeyDown={event=>{if(event.key==='Enter')event.currentTarget.blur();}} />;
}
export function SilencePreviewPanel({model,clipId,duration}:{model:ReturnType<typeof useSilencePreview>;clipId:string|null;duration:number;ready:boolean}) {
 const {state}=model,video=useRef<HTMLVideoElement>(null),endAt=useRef<number|null>(null);
 useEffect(()=>{const player=video.current;return()=>{player?.pause();};},[]);
 const [playError,setPlayError]=useState(''),[selectedId,setSelectedId]=useState<string|null>(null);
 const [filter,setFilter]=useState<'all'|'cut'|'keep'>('all'),[page,setPage]=useState(0);
 const busy=state.phase==='detecting'||state.phase==='applying',editable=state.phase==='ready';
 const filtered=state.candidates.filter(c=>filter==='all'||(filter==='keep'?c.keep:!c.keep));
 const pages=Math.max(1,Math.ceil(filtered.length/PAGE_SIZE)),pageIndex=Math.min(page,pages-1);
 const visible=filtered.slice(pageIndex*PAGE_SIZE,(pageIndex+1)*PAGE_SIZE);
 const selected=state.candidates.find(c=>c.id===selectedId)??visible[0];
 const preserveCount=state.candidates.filter(c=>c.keep).length;
 let kept:{start:number;end:number}[]=[],rangeError='';
 try {kept=silenceKeptRanges(state.sourceRange.start,state.sourceRange.end,state.candidates,state.fps);}catch(error){rangeError=String(error instanceof Error?error.message:error);}
 const retained=kept.reduce((sum,r)=>sum+(Math.round(r.end*state.fps)-Math.round(r.start*state.fps)),0),removed=Math.round((state.sourceRange.end-state.sourceRange.start)*state.fps)-retained;
 const audition=async(start:number,end:number)=>{const player=video.current;if(!player)return;setPlayError('');endAt.current=Math.min(state.sourceRange.end,end+.4);player.currentTime=Math.max(state.sourceRange.start,start-.4);try{await player.play();}catch(error){setPlayError(String(error));}};
 return <section className="silence-workbench silence-embedded" aria-label="人声剪停顿">
  <div className="silence-intro"><span className="silence-badge">本地语音检测 · 无在线 AI 调用</span><span>审阅后追加，不替换原时间线</span></div>
  <div className="silence-workspace">
   <section className="silence-inspector" aria-label="源预览与检测参数">
    <div className="silence-player">
     {state.open&&clipId?<video ref={video} src={getVideoUrl(clipId)} controls preload="metadata" onTimeUpdate={()=>{if(video.current&&endAt.current!==null&&video.current.currentTime>=endAt.current){video.current.pause();endAt.current=null;}}}/>:<span>源素材预览</span>}
     <div className="silence-player-caption"><span>源素材 · {clock(duration)}</span><span>试听包含前后 0.4 秒</span></div>
    </div>
    {playError&&<p className="silence-error" role="alert">{playError}</p>}
    <section className="silence-card" aria-label="检测设置">
     <div className="silence-section-title"><h3>检测设置</h3><span>修改后需重新检测</span></div>
     <div className="silence-parameters">{(['start','end'] as const).map(key=><label key={key}>{key==='start'?'源范围开始（秒）':'源范围结束（秒）'}<input type="number" min={0} max={duration} step="any" value={state.sourceRange[key]} disabled={state.phase==='applying'} onChange={event=>model.range({[key]:Number(event.target.value)})}/></label>)}</div>
     <p>仅检测并追加源 {state.sourceRange.start.toFixed(3)}–{state.sourceRange.end.toFixed(3)} 秒；边界向内取整到帧。范围外不追加，原时间线保留。</p>
     <div className="silence-parameters"><label>最短非语音间隙（秒）<input aria-label="最短非语音间隙（秒）" type="number" step={.05} min={.2} max={30} value={state.parameters.minSeconds} disabled={state.phase==='applying'} onChange={event=>model.parameters({minSeconds:Number(event.target.value)})}/></label></div>
     <p>本地人声活动为主，字幕辅助核对而非字级对齐。前后保留120ms；不确定语音、证据冲突默认保留。不调用在线模型。</p>
     <p>{pauseEvidenceLabel(state.evidenceProvider)}</p>
     <button className="silence-detect" type="button" disabled={busy} onClick={()=>void model.detect()}>{state.phase==='detecting'?'正在检测…':state.candidates.length?'重新检测':'开始检测长停顿'}</button>
    </section>
    {selected&&<section className="silence-card silence-detail" aria-label="选中候选详细编辑">
     <div className="silence-section-title"><h3>当前停顿</h3><span>{clock(selected.start)} → {clock(selected.end)}</span></div>
     <p>{selected.riskCodes.map(r=>pauseRiskLabels[r]||r).join("；")}</p>
     {captionConflictLabels(selected).map((text,index)=><p key={index}>{text}</p>)}
     {!selected.autoEligible&&<label><input type="checkbox" checked={selected.acknowledged} disabled={!editable} onChange={event=>model.update(selected.id,{acknowledged:event.target.checked})}/>已试听并确认风险，允许人工删除</label>}
     <label className="silence-keep"><input type="checkbox" checked={selected.keep} disabled={!editable||(!selected.autoEligible&&!selected.acknowledged)} onChange={event=>model.update(selected.id,{keep:event.target.checked})}/>保留这个有意停顿</label>
     <div className="silence-parameters">{(['cutStart','cutEnd'] as const).map(key=><label key={key}>{key==='cutStart'?'删除起点（源秒）':'删除终点（源秒）'}<span className="silence-number"><SilenceBoundaryInput key={`${selected.id}:${key}:${selected[key]}`} value={selected[key]} step={1/state.fps} max={duration} disabled={!editable||selected.keep} onCommit={value=>model.update(selected.id,{[key]:value})}/><span>秒</span></span></label>)}</div>
     <div className="silence-detail-actions"><button type="button" onClick={()=>void audition(selected.start,selected.end)}>▶ 试听源停顿及前后气口</button><button type="button" disabled={!editable} onClick={()=>model.restore(selected.id)}>恢复此项检测值</button></div>
    </section>}
    <details className="silence-help"><summary>应用范围与字幕说明</summary><p>仅追加源区间，不删除或覆盖现有多轨。确认追加和后续撤销都会按编辑器自动保存；可 Ctrl+Z 单步撤销。已加载的源字幕会同步追加，无源字幕时不生成新字幕，现有字幕不变。静音不一定是无用内容，请试听后决定。</p></details>
   </section>
   <section className="silence-candidates" aria-label="停顿候选清单">
    <div className="silence-list-heading"><div><h3>停顿候选 <span>{state.candidates.length}</span></h3><p>{preserveCount} 项保留 · {state.candidates.length-preserveCount} 项待剪除</p></div><button type="button" disabled={!editable||!state.candidates.length} onClick={()=>model.restore()}>恢复全部</button></div>
    <div className="silence-filters" role="group" aria-label="筛选候选">{(['all','cut','keep'] as const).map(value=><button key={value} type="button" aria-pressed={filter===value} onClick={()=>{setFilter(value);setPage(0);setSelectedId(null);}}>{({all:'全部',cut:'待剪除',keep:'已保留'})[value]}</button>)}</div>
    <p className={`silence-status ${state.phase==='error'||state.phase==='stale'?'silence-error':''}`} role="status">{state.message}</p>
    {state.warnings.map((warning,i)=><p className="silence-status" key={i}>{warning}</p>)}
    <div className="silence-list" role="list" aria-label="可审阅的源停顿">
     {!visible.length&&<div className="silence-empty"><span>{state.phase==='detecting'?'◌':'◷'}</span><h4>{state.phase==='detecting'?'正在寻找长停顿':state.candidates.length?'没有符合筛选的候选':'先检测，再决定剪哪里'}</h4><p>候选只会出现在这里。原时间线保持不变。</p></div>}
     {visible.map(c=><div className={`silence-row ${selected?.id===c.id?'is-selected':''}`} key={c.id} role="listitem">
      <button className="silence-row-select" type="button" aria-pressed={selected?.id===c.id} onClick={()=>setSelectedId(c.id)}><span className="silence-row-time">{clock(c.start)} <span>→</span> {clock(c.end)}</span><span className="silence-row-meta">停顿 {(c.end-c.start).toFixed(2)} 秒 <span className={c.keep?'is-kept':''}>{c.keep?'保留':`拟剪 ${Math.max(0,(Math.round(c.cutEnd*state.fps)-Math.round(c.cutStart*state.fps))/state.fps).toFixed(2)} 秒`}</span></span></button>
      <button className="silence-row-play" type="button" aria-label={`试听 ${clock(c.start)} 的停顿`} title="试听源停顿" onClick={()=>{setSelectedId(c.id);void audition(c.start,c.end);}}>▶</button>
      <label className="silence-row-keep"><input type="checkbox" aria-label={`保留 ${clock(c.start)} 的停顿`} checked={c.keep} disabled={!editable} onChange={event=>model.update(c.id,{keep:event.target.checked})}/>保留</label>
     </div>)}
    </div>
    <div className="silence-pagination"><span>{filtered.length?`${pageIndex*PAGE_SIZE+1}–${Math.min((pageIndex+1)*PAGE_SIZE,filtered.length)} / ${filtered.length} 项`:'0 项'}</span><div><button type="button" disabled={pageIndex===0} onClick={()=>setPage(pageIndex-1)}>上一页</button><span>{pageIndex+1} / {pages}</span><button type="button" disabled={pageIndex+1>=pages} onClick={()=>setPage(pageIndex+1)}>下一页</button></div></div>
   </section>
  </div>
  <footer className="silence-footer">
   <div className="silence-summary"><strong>{editable?`可缩短 ${(removed/state.fps).toFixed(2)} 秒`:'审阅完成后再应用'}</strong><span>{editable?`剪除 ${removed} 帧 · 追加 ${kept.length} 段 / ${retained} 帧 · ${state.fps} fps`:'预览不改工程 · 应用后自动保存 · 可撤销'}</span>{rangeError&&<span className="silence-error" role="alert">{rangeError}</span>}{editable&&removed<=0&&<span>没有实际缩短，不追加整片。</span>}</div>
   <button className="silence-apply" type="button" disabled={!editable||!!rangeError||kept.length===0||removed<=0} onClick={()=>{video.current?.pause();void model.apply();}}>{state.phase==='applying'?'正在核对并追加…':'确认追加到末尾'}<small>自动保存 · 可单步撤销</small></button>
  </footer>
 </section>;
}
