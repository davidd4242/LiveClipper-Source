import type {MultiClipReviewModel} from './useMultiClipReview';
import {finalReviewRanges,semanticDisclosure} from './multiClipController';
import type {SemanticChoice} from './semanticReviewPlanner';
const riskNames:Record<string,string>={'negation':'否定含义不同','numbers':'数字或单位差异','subject':'主体或指代变化','question-answer':'问答关系','new-information':'新增信息','intentional-repeat':'有意重复或强调'};
export function SemanticReviewSection({model,audition}:{model:MultiClipReviewModel;audition:(start:number,end:number)=>Promise<void>}){
 const {state}=model,session=state.semantic,working=['preparing','requesting'].includes(session.stage),ready=state.phase==='ready'&&!working;
 let parts:ReturnType<typeof finalReviewRanges>=[],error='';try{parts=finalReviewRanges(state);}catch(e){error=e instanceof Error?e.message:String(e);}
 const local=session.connection?.provider==='ollama';
 const captions=semanticDisclosure(state).cues;
 return <section className="semantic-review" aria-label="语义去重建议审阅">
  <div className="silence-card"><h3>语义建议 · 单次模型请求</h3><p>读取候选不会调用模型。只有下方两步确认后，才发送入选范围的源字幕，请先审阅服务与数据范围；在线服务另有费用确认。</p>
   <button type="button" disabled={!ready||state.selectedIds.length<1||state.selectedIds.length>10} onClick={()=>void model.prepareSemantic()}>1. 查看发送范围与服务</button>
   <p>已选 {state.selectedIds.length} 个候选，发送范围含 {captions.length} 条源字幕（含前后上下文）。最多 10 个候选 / 200 条含上下文字幕 / 40000 提示字符；每个候选至少 2 条完整字幕。</p>
   {session.stage==='confirm'&&session.connection&&<div className="semantic-confirm" role="group" aria-label={local?'单次本机推理确认':'付费请求确认'}>
    <strong>尚未发送，请核对</strong><p>{local?'本机服务：':'在线服务：'}{session.connection.provider} · {session.connection.endpoint||'服务地址未显示，请在设置核对'}</p><p>模型：{session.connection.model}</p>
    <p>发送：以下候选 ID、源起止时间、入选范围完整字幕及每段前后各一句源上下文。上下文专用句不参与该候选的排除，不上传视频文件。</p>
    <ul>{state.selectedIds.map(id=>{const c=state.items.find(x=>x.id===id)!;return <li key={id}>{c.title} · {id} · {c.start.toFixed(3)}–{c.end.toFixed(3)} 秒</li>;})}</ul>
    <details><summary>查看将发送的源字幕（{captions.length} 条）</summary>{captions.map((c,i)=><p key={i}>{c.contextOnly?'[仅上下文] ':''}{c.id} · {c.start.toFixed(3)}–{c.end.toFixed(3)} · {c.text}</p>)}</details>
    {local?<p>使用后端配置的本机 Ollama 服务运行模型，字幕范围如上。不调用在线服务，不使用在线 API 计费；会占用本机计算资源并可能耗时。本机上下文设置为 32k（32768 token），输出最多 4000 token。后端精确预检完整 system skill 与用户提示的 UTF-8 总输入不超过 24000 字节（24k）；前端不估算或截断。超过限制会要求缩小范围，不开始推理。仍须满足 200 条含上下文字幕和 40000 提示字符限制。仅一次推理请求，无自动重试，失败不切换在线服务。</p>:<p>使用 compatible 在线服务。可能产生费用，按服务商实际用量收费，失败也可能计费。不提供美元估价。仅一次 HTTP 尝试，无自动重试；兼容接口输出最多 4000 token。</p>}
    <div className="silence-detail-actions"><button type="button" onClick={()=>void model.confirmSemantic()}>{local?'2. 确认发送至本机并推理一次':'2. 确认发送字幕并请求一次（可能收费）'}</button><button type="button" onClick={model.cancelSemanticConfirm}>取消，不发送</button></div>
   </div>}
   {session.message&&<p role={session.stage==='error'||session.stage==='stale'?'alert':'status'}>{session.message}</p>}
   {(session.draft||session.stage==='stale')&&<button type="button" disabled={working} onClick={model.clearSemantic}>放弃语义排除，恢复按原入选区间预览</button>}
  </div>
  {session.suggestions.map((suggestion,index)=>{const decision=session.draft?.decisions[suggestion.id],choice=decision?.choice??'keep-both',ack=decision?.acknowledgedRiskIds??[];return <article className="silence-card semantic-suggestion" key={suggestion.id}>
   <h3>建议 {index+1} · 默认保留两边</h3><p>{suggestion.reason}</p>
   <div className="semantic-evidence">{(['left','right'] as const).map(side=>{const group=suggestion[side];return <section key={side} aria-label={side==='left'?'左句组证据':'右句组证据'}><h4>{side==='left'?'左句组':'右句组'} · {state.items.find(c=>c.id===group.candidateId)?.title??group.candidateId}</h4>{group.before&&<p className="multiclip-context">前句：{group.before.text}</p>}{group.cues.map(c=><p key={c.id}><small>{c.start.toFixed(3)}–{c.end.toFixed(3)} · {c.id}</small><br/>{c.text}</p>)}{group.after&&<p className="multiclip-context">后句：{group.after.text}</p>}<button type="button" disabled={!group.cues.length} onClick={()=>void audition(group.cues[0].start,group.cues.at(-1)!.end)}>▶ 试听{side==='left'?'左':'右'}句组</button></section>;})}</div>
   {!!suggestion.localRiskHints.length&&<ul>{suggestion.localRiskHints.map((hint,i)=><li key={i}>{hint}</li>)}</ul>}
   <div className="semantic-decisions" role="group" aria-label={`建议${index+1}的处理方式`}>{(['keep-both','exclude-left','exclude-right'] as SemanticChoice[]).map(value=><label key={value}><input type="radio" name={`semantic-${suggestion.id}`} checked={choice===value} disabled={!ready||session.stage!=='review'||state.mode==='automatic'} onChange={()=>model.semanticDecision(suggestion.id,value,[])}/>{({'keep-both':'保留两边','exclude-left':'仅排除左句组','exclude-right':'仅排除右句组'})[value]}</label>)}</div>
   {choice!=='keep-both'&&<div className="semantic-risks"><strong>排除只影响该候选内句组，不取消整个候选。</strong>{suggestion.contextRisks.map(risk=><label key={risk}><input type="checkbox" checked={ack.includes(risk)} disabled={!ready||session.stage!=='review'||state.mode==='automatic'} onChange={event=>model.semanticDecision(suggestion.id,choice,event.target.checked?[...ack,risk]:ack.filter(r=>r!==risk))}/>我已核对：{riskNames[risk]??risk}</label>)}<button type="button" disabled={!ready||session.stage!=='review'||state.mode==='automatic'} onClick={()=>model.semanticDecision(suggestion.id,'keep-both',[])}>恢复本项：保留两边</button></div>}
  </article>;})}
  {session.draft&&<section className="silence-card semantic-plan" aria-label="语义排除后的最终保留子片段"><h3>最终保留子片段预览</h3><p>重叠排除只扣一次；恢复一项不恢复其他建议仍在排除的范围。</p>{error?<p role="alert" className="silence-error">{error}</p>:<ol>{parts.map((p,i)=><li key={`${i}:${p.start}`}><span>{p.name} · 子片段 {i+1}<br/>{p.start.toFixed(3)}–{p.end.toFixed(3)} 秒</span><button type="button" onClick={()=>void audition(p.start,p.end)}>试听保留段</button></li>)}</ol>}<p>{state.mode==='automatic'?'仍需底部确认才生成独立工程；原工程不改。':'仍需底部确认才追加。追加和撤销会自动保存。'}</p></section>}
 </section>;
}
