import {navigateApp} from '../../app/appNavigation';
import {automaticReview} from './multiClipController';
import type {MultiClipReviewModel} from './useMultiClipReview';
export function AutomaticReviewSection({model}:{model:MultiClipReviewModel}){
 const {state}=model;let preview:ReturnType<typeof automaticReview>|null=null,error='';try{if(state.data)preview=automaticReview(state);}catch(e){error=e instanceof Error?e.message:String(e);}
 return <section className="silence-card automatic-review" aria-label="全自动独立工程规则与预览">
 <h3>全自动 · 保守规则 · 独立工程</h3>
 <p><strong>只输出所选源片段和可映射字幕，不带入原多轨包装、效果、转场或配乐。原工程不删改。</strong>生成媒体副本可能耗时并占用磁盘空间。</p>
 <p>模型建议本身仍标为需要人工审阅。此模式不是模型自动批准删除：只有你授权下方独立规则后，才对至少 8 字、前后句均等价、严格逐字等价且无风险的句组保留源时间先出现的一组，排除后出现的一组。短反应句、数字、否定、问答、有意重复、上下文缺失或冲突及需试听音画证据全部保留。没有建议时全部保留。</p>
 <p>切换模式或生成工程不会调用模型。若需要新的语义建议，仍须在“语义建议”中单独完成服务、数据和费用的两步确认。</p>
 <label className="silence-keep"><input type="checkbox" checked={state.autoRuleAuthorized} disabled={state.phase!=='ready'} onChange={e=>model.authorizeAutomatic(e.target.checked)}/>我授权仅按上述逐字等价规则生成独立粗剪预览</label>
 <button type="button" disabled={!state.autoRuleAuthorized||state.phase!=='ready'} onClick={()=>model.authorizeAutomatic(false)}>恢复生成前预览：全部保留</button>
 {preview&&<><h4>预览：保留 {preview.plan.parts.length} 段 · 排除 {(preview.plan.removedFrames/state.fps).toFixed(2)} 秒</h4><details><summary>查看规则决定与最终源区间</summary>{preview.decisions.map(d=><p key={d.id}>{d.action==='keep-both'?'保留两边':'排除后出现句组'} · {d.reason}</p>)}<ol>{preview.plan.parts.map((p,i)=><li key={i}>{p.name} · {p.start.toFixed(3)}–{p.end.toFixed(3)} 秒</li>)}</ol></details></>}
 <p>AI 剪辑暂不自动添加转场；人工审核与全自动均使用硬切连接。已有手工转场及编辑不会被清除。</p>
 {error&&<p role="alert">{error}</p>}
 <p>预览可恢复，不写工程；最后确认才创建新工程。创建不是原时间线的 Ctrl+Z 操作。</p>
 {state.automaticResult&&<div role="status"><strong>已生成：{state.automaticResult.name}</strong>{state.automaticResult.naturalTransitions&&<p>服务端实际应用 {state.automaticResult.naturalTransitions.boundaries.length} 个连接，缩短 {state.automaticResult.naturalTransitions.totalShortening.toFixed(3)} 秒，结果总长 {state.automaticResult.naturalTransitions.duration.toFixed(3)} 秒。请打开核对画面、音频混合和字幕。</p>}<p>打开新工程会走现有离开保护；在新工程的此面板可“撤销自动剪辑”恢复生成前所选片段，不删除工程。返回原工程不等于撤销创建。</p><button type="button" onClick={()=>void navigateApp(`/editor?clip=${encodeURIComponent(state.automaticResult!.clipId)}`)}>打开独立粗剪工程</button></div>}
 </section>;
}
