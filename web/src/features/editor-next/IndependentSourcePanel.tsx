import './IndependentSourcePanel.css';
import { useEffect, useRef, useState } from 'react';
import { getEditor } from '../../api/client';
import { getAiSettings } from '../../api/settings';
import type { FullEngineMediaSpec } from '../../engine/fullEngineSession';
import type { TranscriptPreview } from '../../api/types';
import { adviceConnection } from '../editor/aiAdvice';
import { generateRoughCutPlan, readRoughCutTranscriptState } from './aiApi';
import { getClipTranscript } from './editorNextTranscriptApi';
import { createIndependentSourceController } from './independentSourceController';
import type { IndependentSource, IndependentSourcePorts, IndependentSourceState } from './independentSourceController';
import type { NativeRoughCutBridge } from './useEditorNextRoughCut';

const initial = (): IndependentSourceState => ({ source: null, range: null, status: 'idle', transcript: null, plan: null, message: '请明确选择一个真实素材。' });
export function independentReviewCanApply(state: IndependentSourceState, reviewed: boolean, ready: boolean): boolean {
  return ready && reviewed && state.status === 'ready' && !!state.plan && !!state.source && !!state.range;
}
export function IndependentSourcePanel(props: {
  projectId: string; media: Record<string, FullEngineMediaSpec>; ready: boolean;
  getSaveSession: () => IndependentSourcePorts['save'] | null;
  onOpenAutomatic?: (source:IndependentSource)=>void;
  getBridge: () => NativeRoughCutBridge | null; onApplying: (value: boolean) => void;
}) {
  const latest = useRef(props); latest.current = props;
  const known = useRef(new Map<string, IndependentSource>());
  const controller = useRef<ReturnType<typeof createIndependentSourceController> | null>(null);
  const selection = useRef(0);
  const [state, setState] = useState(initial);
  const [selectedId, setSelectedId] = useState('');
  const [selecting, setSelecting] = useState(false);
  const [error, setError] = useState('');
  const [reviewed, setReviewed] = useState(false);
  const [start, setStart] = useState(0), [end, setEnd] = useState(0);
  useEffect(() => {
    const session = () => { const value = latest.current.getSaveSession(); if (!value) throw new Error('工程保存会话未就绪。'); return value; };
    const c = createIndependentSourceController({ projectId: props.projectId,
      sources: () => [...known.current.values()].filter(source => Object.hasOwn(latest.current.media, source.id)),
      save: { flush: () => session().flush(), readState: () => session().readState(), snapshot: () => session().snapshot() },
      native: { snapshot: () => latest.current.getBridge()?.hostSnapshot() ?? { ok: false },
        append: input => latest.current.getBridge()?.hostPlaceSourceRanges?.(input) ?? { ok: false, error: '原生追加接口未就绪。' } },
      api: {
        transcript: async (id, signal) => {
          const [metadata, transcript] = await Promise.all([readRoughCutTranscriptState(id), getClipTranscript(id, signal)]);
          return { ...metadata, transcript };
        },
        connection: async () => { const value = adviceConnection(await getAiSettings()); if (!value) throw new Error('请先在设置中选择可用 AI 模型。'); return value; },
        generate: generateRoughCutPlan,
        project: async id => { const payload = await getEditor(id); return { projectId: payload.projectIdentity?.id ?? payload.clip?.id ?? '', epoch: payload.epoch ?? '', revision: payload.revision ?? -1 }; },
      },
      onState: next => { setState(next); setReviewed(false); latest.current.onApplying(next.status === 'applying'); },
    });
    controller.current = c; setState(initial());
    return () => { selection.current++; c.dispose(); controller.current = null; latest.current.onApplying(false); };
  }, [props.projectId]);
  const choose = async (id: string) => {
    const token = ++selection.current; setSelectedId(id); controller.current?.select(null); setReviewed(false); setError('');
    if (!id) { setSelecting(false); return; }
    setSelecting(true);
    try {
      const payload = await getEditor(id);
      if (selection.current !== token) return;
      if (!Object.hasOwn(latest.current.media, id) || payload.clip?.id !== id || !payload.clip.eventId || !(payload.duration > 0)) throw new Error('所选项不是当前登记的可分析视频素材。');
      const source = { id, name: payload.clip.title || props.media[id].name || id, eventId: payload.clip.eventId, duration: payload.duration };
      known.current.set(id, source); setStart(0); setEnd(source.duration); controller.current?.select(id, { start: 0, end: source.duration });
    } catch (e) { if (selection.current === token) setError(e instanceof Error ? e.message : String(e)); }
    finally { if (selection.current === token) setSelecting(false); }
  };
  const changeRange = (a: number, b: number) => {
    setStart(a); setEnd(b); setReviewed(false); setError('');
    const id = selectedId;
    // Invalid edits clear the active selection so no request can use the previous range silently.
    if (id && !controller.current?.select(id, { start: a, end: b })) { controller.current?.select(null); setError('范围需在所选素材时长内，且结束大于开始。'); }
  };
  const busy = selecting || ['reading', 'generating', 'applying'].includes(state.status);
  const canRequest = props.ready && !!state.source && !!state.range && !busy && state.status !== 'applied';
  const transcript = state.transcript?.transcript as TranscriptPreview | null | undefined;
  return <section aria-label="独立工程单源人工审核" className="silence-card independent-source-panel">
    <h3>单源粗剪 · 人工审核</h3>
    <p>明确选择素材和源时间范围。读取字幕不会重转写；生成只请求 cut-plan，确认后追加到当前工程，不修改源工程。已有候选可进入下方人工审核／全自动工作台；多源及重转写尚未接入。</p>
    <label>分析素材<select aria-label="分析素材" value={selectedId} disabled={!props.ready || state.status === 'applying'} onChange={event => { void choose(event.target.value); }}>
      <option value="">请选择真实素材</option>{Object.entries(props.media).map(([id, media]) => <option key={id} value={id}>{media.name || id}</option>)}
    </select></label>
    {state.source && <p>当前素材：{state.source.name} · {state.source.duration.toFixed(2)} 秒</p>}
    {props.onOpenAutomatic && <div><button type="button" disabled={!props.ready || !state.source || busy} onClick={()=>{if(state.source)props.onOpenAutomatic?.(state.source);}}>已有候选 · 人工审核／全自动…</button><p>另开所选源的全部已保存候选工作台，不使用下方 cut-plan 分析范围；没有候选时不会伪造。全自动生成独立结果，不改当前工程。</p></div>}
    <label>开始（秒）<input aria-label="分析开始秒" type="number" min={0} step="0.01" value={start} disabled={!selectedId || busy} onChange={e => changeRange(Number(e.target.value), end)} /></label>
    <label>结束（秒）<input aria-label="分析结束秒" type="number" min={0} step="0.01" value={end} disabled={!selectedId || busy} onChange={e => changeRange(start, Number(e.target.value))} /></label>
    <div><button type="button" disabled={!canRequest} onClick={() => { void controller.current?.readTranscript(); }}>读取所选素材字幕</button>
      <button type="button" disabled={!canRequest} onClick={() => { void controller.current?.generate(); }}>生成单源粗剪建议</button></div>
    <p role="status">{selecting ? '正在核对所选素材…' : state.message}</p>{error && <p role="alert">{error}</p>}
    {transcript && <details><summary>只读字幕（{transcript.segments.length} 条）</summary><div style={{ maxHeight: 240, overflowY: 'auto' }}>{transcript.segments.map((cue, index) => <p key={index}>{cue.start.toFixed(2)}–{cue.end.toFixed(2)}：{cue.text}</p>)}</div></details>}
    {state.plan && <div><h4>{state.plan.title}</h4><p>{state.plan.summary}</p>
      <ol>{state.plan.ranges.map((range, index) => <li key={index}>{range.start.toFixed(2)}–{range.end.toFixed(2)} 秒</li>)}</ol>
      <ul>{state.plan.reasons.map((reason, index) => <li key={index}>{reason}</li>)}</ul>
      <p>模型没有提供可校验的逐段置信度。以上建议全部需要人工复核，尤其注意跳句、断词及语义缺失，不能视为已通过听感验收。</p>
      <label><input type="checkbox" checked={reviewed} disabled={busy} onChange={e => setReviewed(e.target.checked)} />我已核对全部区间和理由，并接受可能的剪接风险</label>
      <button type="button" disabled={!independentReviewCanApply(state, reviewed, props.ready)} onClick={() => { void controller.current?.apply(); }}>确认并追加到当前工程</button>
    </div>}
  </section>;
}
