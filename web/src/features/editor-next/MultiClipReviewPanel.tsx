import {PauseReviewSection} from './PauseReviewSection';
import {AutomaticReviewSection} from './AutomaticReviewSection';
import {SemanticReviewSection} from './SemanticReviewSection';
import type {ReactNode} from 'react';
import {useMemo,useRef,useState} from 'react';
import {HostDrawer} from './HostDrawer';
import {SilenceBoundaryInput} from './SilencePreviewPanel';
import {getVideoUrl} from '../../api/client';
import {literalDuplicates,reviewContext,finalReviewRanges} from './multiClipController';
import type {MultiClipReviewModel} from './useMultiClipReview';
import './multi-clip-review.css';
const time=(s:number)=>`${Math.floor(s/60).toString().padStart(2,'0')}:${(s%60).toFixed(2).padStart(5,'0')}`;
export function MultiClipReviewPanel({model,clipId,drawerId="editor-next-multiclip-drawer",title="自动剪辑 · 多片段",introduction,blocked=false}:{model:MultiClipReviewModel;clipId:string|null;drawerId?:string;title?:string;introduction?:ReactNode;blocked?:boolean}){
 const {state}=model,[activeId,setActiveId]=useState<string|null>(null),[tab,setTab]=useState<'all'|'selected'|'semantic'>('all'),[page,setPage]=useState(0),[playError,setPlayError]=useState('');
 const video=useRef<HTMLVideoElement>(null),stop=useRef<number|null>(null),ready=!blocked&&state.phase==='ready'&&!['preparing','requesting'].includes(state.semantic.stage),busy=blocked||state.phase==='loading'||state.phase==='applying'||['preparing','requesting'].includes(state.semantic.stage);
 const selected=state.selectedIds.map(id=>state.items.find(c=>c.id===id)!),list=tab==='selected'?selected:state.items,pages=Math.max(1,Math.ceil(list.length/30)),currentPage=Math.min(page,pages-1),visible=list.slice(currentPage*30,(currentPage+1)*30);
 const active=state.items.find(c=>c.id===activeId)??visible[0],transcript=state.data?.transcript??[],duration=state.data?.duration??0;
 const context=active?reviewContext(transcript,active.start,active.end):null;
 const duplicates=useMemo(()=>literalDuplicates(state.items,state.data?.transcript??[]),[state.items,state.data]);
 const hints=active?duplicates.filter(p=>p.left===active.id||p.right===active.id):[];
 let ranges:ReturnType<typeof finalReviewRanges>=[],error='';try{ranges=finalReviewRanges(state);}catch(e){error=e instanceof Error?e.message:String(e);}
 const frames=ranges.reduce((n,r)=>n+Math.round(r.end*state.fps)-Math.round(r.start*state.fps),0);
 const previewSeconds=state.mode==='automatic'?ranges.reduce((n,r)=>n+r.end-r.start,0):frames/state.fps;
 const audition=async(start:number,end:number)=>{const player=video.current;if(!player)return;setPlayError('');stop.current=Math.min(duration,end+.5);player.currentTime=Math.max(0,start-.5);try{await player.play();}catch(e){setPlayError(String(e));}};
 return <HostDrawer id={drawerId} className="silence-workbench multiclip-workbench" title={title} open={state.open} onClose={()=>{if(blocked)return;video.current?.pause();model.close();}}>
  {introduction}
  <fieldset disabled={blocked} style={{display:'contents'}}>
  <div className="silence-filters multiclip-mode" role="group" aria-label="自动剪辑模式"><button type="button" aria-pressed={state.mode!=='automatic'} disabled={busy} onClick={()=>model.setMode('manual')}>人工审核</button><button type="button" aria-pressed={state.mode==='automatic'} disabled={busy} onClick={()=>model.setMode('automatic')}>全自动（独立工程）</button></div>
  <div className="silence-intro"><span className="silence-badge">已有候选审阅 · 语义请求需另行确认</span><span>选段 → 调整 → 排序 → 确认追加</span></div>
  <div className="silence-workspace">
   <section className="silence-inspector" aria-label="源视频与上下文审阅">
    <div className="silence-player">{state.open&&clipId&&<video ref={video} src={getVideoUrl(clipId)} controls preload="metadata" onTimeUpdate={()=>{if(video.current&&stop.current!==null&&video.current.currentTime>=stop.current){video.current.pause();stop.current=null;}}}/>}<div className="silence-player-caption"><span>源素材试听</span><span>前后各保留 0.5 秒试听上下文</span></div></div>
    {playError&&<p className="silence-error" role="alert">{playError}</p>}
    {active&&<section className="silence-card"><div className="silence-section-title"><h3>{active.title}</h3><span>{active.source} · 历史候选</span></div><p className="multiclip-summary">{active.summary}</p>
     <div className="silence-parameters">{(['start','end'] as const).map(key=><label key={key}>{key==='start'?'片段起点（源秒）':'片段终点（源秒）'}<span className="silence-number"><SilenceBoundaryInput key={`${active.id}:${key}:${active[key]}`} value={active[key]} disabled={!ready} step={1/state.fps} max={duration} onCommit={value=>model.update(active.id,{[key]:value})}/><span>秒</span></span></label>)}</div>
     <div className="silence-detail-actions"><button type="button" onClick={()=>void audition(active.start,active.end)}>▶ 试听此段</button><button type="button" disabled={!ready||!context?.inside.length} onClick={()=>model.expand(active.id)}>扩到完整句</button><button type="button" disabled={!ready} onClick={()=>model.restore(active.id)}>恢复候选边界</button></div>
     <label className="silence-keep multiclip-select"><input type="checkbox" checked={state.selectedIds.includes(active.id)} disabled={!ready} onChange={()=>model.toggle(active.id)}/>入选此片段（仅审阅草稿）</label>
    </section>}
    {context&&<section className="silence-card multiclip-transcript"><div className="silence-section-title"><h3>字幕上下文</h3><span>前句 / 片段 / 后句</span></div>{context.cutsSentence&&<p className="multiclip-caution">当前边界切入一句话，可先“扩到完整句”再试听。</p>}
     {context.before&&<p className="multiclip-context">前句 · {context.before.text}</p>}
     <div className="multiclip-lines">{context.inside.length?context.inside.map((s,i)=><p key={`${s.start}:${i}`}><time>{time(s.start)}</time>{s.text}</p>):<p>此范围无有效字幕，请以源视频为准。</p>}</div>
     {context.after&&<p className="multiclip-context">后句 · {context.after.text}</p>}
     <p className="multiclip-caution">重排可能改变问答、指代或因果关系，请试听相邻入选片段。</p>
    </section>}
    {active&&!!state.data?.repeatHints?.length&&<section className="silence-card"><h3>源句逐字重复证据</h3>{state.data.repeatHints.filter(h=>[h.firstIndex,h.secondIndex].some(index=>{const sentence=transcript[index];return sentence&&sentence.end>active.start&&sentence.start<active.end;})).slice(0,20).map((hint,i)=><div className="multiclip-hint" key={i}><p>“{hint.text}”</p><p>{hint.reason}。不自动删除。</p><div className="silence-detail-actions">{[hint.firstIndex,hint.secondIndex].map(index=>{const sentence=transcript[index];return sentence&&<button type="button" key={index} onClick={()=>void audition(sentence.start,sentence.end)}>试听 {time(sentence.start)}</button>;})}</div></div>)}</section>}
    {!!hints.length&&<section className="silence-card"><h3>疑似重复提示（{hints.length} 对，展示前 20 对）</h3>{hints.slice(0,20).map((h,i)=>{const other=state.items.find(c=>c.id===(h.left===active?.id?h.right:h.left));return <p className="multiclip-hint" key={i}>{h.reason}。对照：<button type="button" onClick={()=>setActiveId(other!.id)}>{other?.title}</button>。默认不删除任何片段，请逐项决定是否入选。</p>;})}<p className="multiclip-caution">仅字面和时间重叠提示，不是新的语义 AI 分析。</p></section>}
   </section>
   <section className="silence-candidates" aria-label="候选与入选顺序">
    <div className="silence-list-heading"><div><h3>审阅清单 <span>{state.items.length}</span></h3><p>已入选 {selected.length} 段 · 默认不全选</p></div><button type="button" disabled={busy} onClick={()=>void model.load()}>{state.phase==='loading'?'读取中…':state.data?'重新读取':'读取已有候选'}</button></div>
    <div className="silence-filters" role="group" aria-label="候选视图"><button type="button" aria-pressed={tab==='all'} onClick={()=>{setTab('all');setPage(0);}}>全部候选</button><button type="button" aria-pressed={tab==='selected'} onClick={()=>{setTab('selected');setPage(0);}}>入选顺序 · {selected.length}</button><button type="button" aria-pressed={tab==='semantic'} onClick={()=>setTab('semantic')}>语义建议</button></div>
    <div className="silence-list multiclip-main-scroll" role="list" aria-label="片段与审阅内容">
     <div className="multiclip-scroll-notices">
      {state.restoreAvailable&&<section className="silence-card"><h3>此工程有自动剪辑前快照</h3><p>恢复的是此独立工程生成前的所选片段与字幕，不删除工程、不回退原工程。生成后若有手工编辑将拒绝恢复，请先保存副本。</p><button type="button" disabled={busy} onClick={()=>void model.restoreAutomatic()}>撤销自动剪辑：恢复生成前所选片段</button></section>}
      <p className="silence-status" role={state.phase==='error'||state.phase==='stale'?'alert':'status'}>{state.message}</p>
      <details className="multiclip-data-notices"><summary>候选来源与审阅说明{state.data?.warnings.length?`（${state.data.warnings.length} 条提示）`:''}</summary>
       <p>候选为历史保存结果，不保证与当前字幕一致。重新读取会清空本次选择和调整。</p>
       {state.data?.warnings.map((warning,i)=><p key={i}>{warning}</p>)}
      </details>
     </div>
     {state.mode==='automatic'&&<AutomaticReviewSection model={model}/>}
     <PauseReviewSection model={model} audition={audition}/>
     {tab==='semantic'?<SemanticReviewSection model={model} audition={audition}/>:<>
     {!visible.length&&<div className="silence-empty"><span>▤</span><h4>{state.data?'暂无入选片段或可用候选':'从已有分析结果开始'}</h4><p>不重新分析、不调用模型，确认前不改时间线。</p></div>}
     {visible.map(c=>{const order=state.selectedIds.indexOf(c.id);return <div key={c.id} className={`silence-row ${active?.id===c.id?'is-selected':''}`} role="listitem"><label className="silence-row-keep"><input type="checkbox" aria-label={`入选 ${c.title}`} checked={order>=0} disabled={!ready} onChange={()=>model.toggle(c.id)}/>{order>=0?order+1:'–'}</label><button type="button" className="silence-row-select" aria-pressed={active?.id===c.id} onClick={()=>setActiveId(c.id)}><span className="multiclip-row-title">{c.title}</span><span className="silence-row-meta">{time(c.start)} → {time(c.end)} · {(c.end-c.start).toFixed(2)} 秒</span></button><button type="button" className="silence-row-play" aria-label={`试听 ${c.title}`} onClick={()=>{setActiveId(c.id);void audition(c.start,c.end);}}>▶</button>{tab==='selected'&&<div className="multiclip-order"><button type="button" aria-label={`上移 ${c.title}`} disabled={!ready||order===0} onClick={()=>model.move(c.id,-1)}>↑</button><button type="button" aria-label={`下移 ${c.title}`} disabled={!ready||order===selected.length-1} onClick={()=>model.move(c.id,1)}>↓</button></div>}</div>;})}
    </>}
    </div>
    {tab!=='semantic'&&<div className="silence-pagination"><span>{list.length} 段 · {currentPage+1}/{pages} 页</span><div><button type="button" disabled={currentPage===0} onClick={()=>setPage(currentPage-1)}>上一页</button><button type="button" disabled={currentPage+1>=pages} onClick={()=>setPage(currentPage+1)}>下一页</button></div></div>}
   </section>
  </div>
  <footer className="silence-footer"><div className="silence-summary"><strong>入选 {selected.length} 段 · {previewSeconds.toFixed(2)} 秒</strong><span>{state.mode==='automatic'?'约 ':''}{Math.round(previewSeconds*state.fps)} 帧 · {state.mode==='automatic'?'生成独立源片段工程，原多轨包装不带入':'按入选顺序追加，现有多轨不删除'}</span><span>{state.mode==='automatic'?'确认前仅预览，原工程始终不变。':'追加及单步撤销均会自动保存，以保存栏结果为准。'}</span>{error&&<span role="alert" className="silence-error">{error}</span>}</div><button className="silence-apply" type="button" disabled={!ready||!selected.length||!!error||(state.mode==='automatic'&&!state.autoRuleAuthorized)} onClick={()=>{video.current?.pause();void (state.mode==='automatic'?model.createAutomatic():model.apply());}}>{state.phase==='applying'?'正在核对…':state.mode==='automatic'?'确认生成独立粗剪工程':'确认按顺序追加'}<small>{state.mode==='automatic'?'复制素材及源字幕 · 不改原工程':'同步可用源字幕 · 单步撤销'}</small></button></footer>
 </fieldset></HostDrawer>;
}
