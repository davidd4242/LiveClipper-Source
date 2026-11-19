import type {MultiClipReviewModel} from './useMultiClipReview';
import {captionConflictLabels,pauseRiskLabels,pauseEvidenceLabel} from './pauseReviewPlanner';
export function PauseReviewSection({model,audition}:{model:MultiClipReviewModel;audition:(start:number,end:number)=>unknown}){
 const {state}=model,p=state.pause,busy=state.phase!=='ready';
 return <section className="silence-card" aria-label="本地停顿统一规划"><h3>本地停顿规划（人声主导）</h3>
 <label><input type="checkbox" checked={p.enabled} disabled={busy} onChange={e=>model.setPauseEnabled(e.target.checked)}/>在选中片段内剪除可确认的长停顿</label>
 <p>使用本地语音活动检测，不调用在线模型。字幕辅助核对，不是字级对齐；低置信默认保留。前后保留 120ms，不给剪气口接点添加转场。</p>
 {p.enabled&&<><label>最短非语音间隙（秒）<input type="number" min="0.2" max="30" step="0.1" value={p.parameters.minSeconds} disabled={busy} onChange={e=>model.pauseParameters({minSeconds:Number(e.target.value)})}/></label>
 <button type="button" disabled={busy||!state.selectedIds.length||(state.mode==='automatic'&&!state.autoRuleAuthorized)} onClick={()=>void model.planPauses()}>检测并预览停顿</button><p role={p.phase==='error'?'alert':'status'}>{p.message}</p>
 <p>{pauseEvidenceLabel(p.data?.evidenceProvider)}</p>
 {p.data?.warnings.map((w,i)=><p key={i}>{w}</p>)}
 {p.data?.silences.map(c=>{const removed=state.mode==='automatic'?c.autoEligible:p.excludedIds.includes(c.id),ack=p.reviewedRiskIds.includes(c.id);return <div key={c.id}><p>源 {c.start.toFixed(3)}–{c.end.toFixed(3)} 秒；可删 {c.cutStart.toFixed(3)}–{c.cutEnd.toFixed(3)} 秒 · {c.autoEligible?'符合保守自动规则':'低置信，默认保留'}</p><p>{c.riskCodes.map(r=>pauseRiskLabels[r]||r).join('；')}</p>{captionConflictLabels(c).map((text,index)=><p key={index}>{text}</p>)}<button type="button" onClick={()=>void audition(Math.max(0,c.start-.4),c.end+.4)}>试听此接点</button>
 {state.mode==='manual'&&!c.autoEligible&&<label><input type="checkbox" disabled={busy} checked={ack} onChange={e=>model.pauseDecision(c.id,false,e.target.checked)}/>已试听并确认上述风险，允许人工删除</label>}
 <label><input type="checkbox" checked={removed} disabled={busy||state.mode==='automatic'||(!c.autoEligible&&!ack)} onChange={e=>model.pauseDecision(c.id,e.target.checked,ack)}/>删除此停顿</label></div>;})}
 {!p.data?.silences.length&&p.phase==='ready'&&<p>没有停顿候选；保留选中内容，未执行剪气口。</p>}</>}
 </section>;
}
