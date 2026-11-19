import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { analyzeClip, extractEvents } from '../../api/client';
import { getAiSettings } from '../../api/settings';
import type { AiAdvice, StudioEvent } from '../../api/types';
import { adviceConnection, adviceView } from '../editor/aiAdvice';
import './r67-editor-next-ai.css';
import './filmcraft-tool-panels.css';
import { aiIsBusy } from './useEditorNextAi';
import type { EditorNextAiState } from './useEditorNextAi';
import { roughCutIsBusy } from './useEditorNextRoughCut';
import type { EditorNextRoughCutState } from './useEditorNextRoughCut';
import type { RoughCutRange } from './aiApi';

const changeLabels: Record<string, string> = { preserve: '保护手工结构', keep: '保持原样', trim: '裁剪', add: '新增片段', remove: '移除' };
const seconds = (value: number) => value.toLocaleString('zh-CN', { maximumFractionDigits: 3 });
export type AiPanelView = 'all' | 'analysis' | 'events' | 'rough-cut';

export interface PlacedEvent {
  eventId: string;
  start: number;
  end: number;
  name?: string;
}
export interface EventPlacementResult {
  ok: boolean;
  message: string;
  selectedIds: string[];
}
export interface CandidateContext {
  clipId: string;
  events: readonly StudioEvent[];
  placedEvents: readonly PlacedEvent[];
  placementError: string | null;
  hasTranscript: boolean;
  onEvents: (events: StudioEvent[]) => void;
  placeEnabled: boolean;
  onPlace: (selectedIds: readonly string[]) => EventPlacementResult;
}

export interface AiAnalysisPanelProps {
  hasTranscript: boolean;
  eventCount: number;
  busy: 'rules' | 'analysis' | null;
  blocked: boolean;
  message: string;
  analysisError: string | null;
  advice: AiAdvice | null;
  adviceError: string | null;
  onAnalyze: () => void;
  onExtract: () => void;
}

export function AiAnalysisPanel({ hasTranscript, eventCount, busy, blocked, message, analysisError, advice, adviceError, onAnalyze, onExtract }: AiAnalysisPanelProps) {
  const view = adviceView(advice);
  return <section id="editor-next-ai-analysis" className="editor-next-ai fc-tool-panel" aria-label="AI 分析" aria-busy={busy !== null}>
    <header className="fc-tool-panel__heading"><h2>AI 分析</h2><p>提取事件，审阅总结与切片建议；不自动修改时间线。</p></header>
    {!hasTranscript ? <div className="fc-tool-panel__empty"><strong>先准备源字幕</strong><p>到「转写字幕」生成字幕，再重新载入工程后开始分析。</p></div> : <div className="editor-next-ai-actions">
      <button type="button" className="fc-tool-panel__primary" disabled={busy !== null || blocked} onClick={onAnalyze}>{busy === 'analysis' ? 'AI 分析中…' : '开始 AI 分析'}</button>
      <button type="button" disabled={busy !== null || blocked} onClick={onExtract}>{busy === 'rules' ? '规则提取中…' : '规则提取候选'}</button>
    </div>}
    <p className="fc-tool-panel__status" role="status" aria-live="polite" aria-atomic="true">{message || (eventCount ? `已有 ${eventCount} 个事件，可到「选择事件」审阅与放置。` : '尚未分析。结果将显示在这里，事件会同步到「选择事件」。')}</p>
    {analysisError && <p role="alert">AI 分析错误：{analysisError}</p>}
    {(advice || adviceError) && <section className="fc-tool-panel__group" aria-label="AI 总结与建议">
      <h3>分析结果</h3>
      {adviceError && <p role="alert">总结与建议错误：{adviceError}</p>}
      {view.summary && <p>{view.summary}</p>}
      {view.captions.length > 0 && <><h4>可用文案</h4><ul>{view.captions.map((caption, index) => <li key={index}>{caption}</li>)}</ul></>}
      {view.tips.length > 0 && <><h4>切片建议</h4><ul>{view.tips.map((tip, index) => <li key={index}>{tip}</li>)}</ul></>}
      {view.empty && !adviceError && <p>本次没有返回总结、文案或切片建议。</p>}
    </section>}
    <details className="fc-tool-panel__help"><summary>分析方式与数据说明</summary><p>AI 分析使用「设置 → AI」中保存的连接与模型，密钥由服务端补齐。规则提取只从已保存源字幕分组，不调用模型。两种操作都会保存独立事件，放置仍需手动选择。</p></details>
  </section>;
}

export interface EventSelectionPanelProps {
  events: readonly StudioEvent[];
  placedEvents: readonly PlacedEvent[];
  selectedIds: readonly string[];
  disabled: boolean;
  message: string;
  error?: string | null;
  onSelect: (id: string) => void;
  onPlace: () => void;
}

export function EventSelectionPanel({ events, placedEvents, selectedIds, disabled, message, error, onSelect, onPlace }: EventSelectionPanelProps) {
  const unmatched = placedEvents.filter(event => !events.some(candidate => candidate.id === event.eventId));
  return <section id="editor-next-ai-candidates" className="editor-next-ai fc-tool-panel" aria-label="选择事件">
    <header className="fc-tool-panel__heading"><h2>选择事件</h2><p>首次勾选后批量放置，替换当前主源的原始整段；之后勾选只新增，取消只移除对应事件。</p></header>
    <div className="fc-tool-panel__meta"><span>{events.length} 个候选</span><span>已选择 {selectedIds.length} 个 · 已放置 {placedEvents.length} 个</span></div>
    {!events.length && <div className="fc-tool-panel__empty"><strong>还没有候选事件</strong><p>先到「AI 分析」分析或规则提取；没有源字幕时，先完成「转写字幕」。这里不会自动调用模型。</p></div>}
    <ul className="fc-tool-panel__events">{events.map(event => <li key={event.id} data-selected={selectedIds.includes(event.id)}>
      <label><input type="checkbox" name="event-placement" checked={selectedIds.includes(event.id)} disabled={disabled} onChange={() => onSelect(event.id)} />
        <span><strong>{event.title}</strong><span className="fc-tool-panel__time">{seconds(event.start)}–{seconds(event.end)} 秒</span><span className="fc-tool-panel__source">来源：{event.source}{placedEvents.some(placed => placed.eventId === event.id) ? ' · 已放置' : ''}</span></span>
      </label>
      {event.summary && <p>{event.summary}</p>}
      {event.hook && <blockquote>{event.hook}</blockquote>}
    </li>)}</ul>
    {unmatched.length > 0 && <section className="fc-tool-panel__group" aria-label="候选外的已放置事件"><h3>已放置但不在本次候选中</h3><p>刷新分析不会移除这些事件。只有明确取消勾选才会移除。</p><ul>{unmatched.map(event => <li key={event.eventId}><label><input type="checkbox" checked={selectedIds.includes(event.eventId)} disabled={disabled} onChange={() => onSelect(event.eventId)} /><span>{event.name || event.eventId} · 已放置</span></label></li>)}</ul></section>}
    <div className="fc-tool-panel__group"><button type="button" className="fc-tool-panel__primary" disabled={!selectedIds.length || disabled} onClick={onPlace}>{placedEvents.length ? '同步选中事件' : '放置选中事件'}</button><p className="fc-tool-panel__muted">首次放置前勾选仅为草稿；放置后更改勾选会一次同步。手工片段和其它来源保持不动，可撤销；保存沿用工程保存栏。</p></div>
    {error && <p role="alert">{error}</p>}
    {message && <p className="fc-tool-panel__status" role="status" aria-live="polite" aria-atomic="true">{message}</p>}
  </section>;
}

function CandidatePanel({ context, blocked, onBusyChange, renderPanels }: {
  context: CandidateContext;
  blocked: boolean;
  onBusyChange: (busy: boolean) => void;
  renderPanels: (analysis: ReactNode, events: ReactNode) => ReactNode;
}) {
  const [busy, setBusy] = useState<'rules' | 'analysis' | null>(null);
  const [message, setMessage] = useState('');
  const [placementMessage, setPlacementMessage] = useState('');
  const [analysisError, setAnalysisError] = useState<string | null>(null);
  const [advice, setAdvice] = useState<AiAdvice | null>(null);
  const [adviceError, setAdviceError] = useState<string | null>(null);
  const latestContext = useRef(context);
  latestContext.current = context;
  const [selectedIds, setSelectedIds] = useState<string[]>(() => context.placedEvents.map(event => event.eventId));
  const [placementBusy, setPlacementBusy] = useState(false);
  const [placementFailed, setPlacementFailed] = useState(false);
  const placedIdsKey = JSON.stringify(context.placedEvents.map(event => event.eventId).sort());
  const previousPlacedIds = useRef(placedIdsKey);
  useEffect(() => {
    if (previousPlacedIds.current === placedIdsKey) return;
    previousPlacedIds.current = placedIdsKey;
    setSelectedIds(JSON.parse(placedIdsKey) as string[]);
  }, [placedIdsKey]);
  const pending = useRef(false);
  const placing = useRef(false);
  const mounted = useRef(false);
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; onBusyChange(false); };
  }, [onBusyChange]);
  const place = (ids: readonly string[] = selectedIds) => {
    if (pending.current || placing.current || blocked || !context.placeEnabled) return;
    placing.current = true;
    setPlacementBusy(true);
    try {
      const result = context.onPlace(ids);
      setSelectedIds(result.selectedIds);
      setPlacementFailed(!result.ok);
      setPlacementMessage(result.message);
    } finally {
      placing.current = false;
      setPlacementBusy(false);
    }
  };
  const select = (id: string) => {
    if (pending.current || placing.current || blocked || !context.placeEnabled) return;
    const next = selectedIds.includes(id) ? selectedIds.filter(value => value !== id) : [...selectedIds, id];
    if (context.placedEvents.length) place(next);
    else { setSelectedIds(next); setPlacementFailed(false); setPlacementMessage('选择尚未放置，点击「放置选中事件」一次同步。'); }
  };
  const extract = async () => {
    if (pending.current || blocked || !context.hasTranscript) return;
    pending.current = true;
    onBusyChange(true);
    setBusy('rules');
    setAnalysisError(null);
    setAdvice(null);
    setAdviceError(null);
    setMessage('正在按规则从已保存的源字幕提取候选，不调用模型。');
    try {
      const events = await extractEvents(context.clipId);
      if (!mounted.current) return;
      context.onEvents(events);
      setSelectedIds(latestContext.current.placedEvents.map(event => event.eventId));
      setPlacementMessage('');
      setPlacementFailed(false);
      setMessage(`规则提取返回 ${events.length} 个候选。工程时间线未修改。`);
    } catch (error) {
      if (mounted.current) {
        setAnalysisError(error instanceof Error ? error.message : String(error));
        setMessage('规则提取失败，现有候选未替换。');
      }
    } finally {
      pending.current = false;
      if (mounted.current) { setBusy(null); onBusyChange(false); }
    }
  };
  const analyze = async () => {
    if (pending.current || blocked || !context.hasTranscript) return;
    pending.current = true;
    onBusyChange(true);
    setBusy('analysis');
    setAnalysisError(null);
    setAdvice(null);
    setAdviceError(null);
    setMessage('正在使用已保存的 AI 连接分析当前素材…');
    try {
      const settings = await getAiSettings();
      if (!mounted.current) return;
      const connection = adviceConnection(settings);
      if (!connection) throw new Error('请先在设置里选择 AI 模型，再进行真实 AI 分析。');
      const result = await analyzeClip(context.clipId, connection);
      if (!mounted.current) return;
      context.onEvents(result.events);
      setSelectedIds(latestContext.current.placedEvents.map(event => event.eventId));
      setPlacementMessage('');
      setPlacementFailed(false);
      setAdvice(result.advice);
      setAdviceError(result.adviceError);
      setMessage(`AI 分析接口返回 ${result.events.length} 个候选。${result.adviceError ? '总结与建议生成失败，详见下方错误。' : ''}工程时间线未修改。`);
    } catch (error) {
      if (mounted.current) {
        setAnalysisError(error instanceof Error ? error.message : String(error));
        setMessage('真实 AI 分析失败；现有候选未替换，未使用规则结果回退。');
      }
    } finally {
      pending.current = false;
      if (mounted.current) { setBusy(null); onBusyChange(false); }
    }
  };
  return <>{renderPanels(
    <AiAnalysisPanel hasTranscript={context.hasTranscript} eventCount={context.events.length} busy={busy} blocked={blocked} message={message} analysisError={analysisError} advice={advice} adviceError={adviceError} onAnalyze={() => { void analyze(); }} onExtract={() => { void extract(); }} />,
    <EventSelectionPanel events={context.events} placedEvents={context.placedEvents} selectedIds={selectedIds} disabled={busy !== null || placementBusy || blocked || !context.placeEnabled} message={placementFailed ? '' : placementMessage} error={context.placementError || (placementFailed ? placementMessage : null)} onSelect={select} onPlace={() => place()} />,
  )}</>;
}

export interface AiRoughCutContext {
  state: EditorNextRoughCutState;
  sourceRange: RoughCutRange | null;
  onGenerate: () => void;
  onApply: () => void;
}
export interface AiRoughCutPanelProps {
  context: AiRoughCutContext;
  blocked: boolean;
  onGenerate: () => void;
  onApply: () => void;
}
export function AiRoughCutPanel({ context: roughCut, blocked, onGenerate, onApply }: AiRoughCutPanelProps) {
  const roughBusy = roughCutIsBusy(roughCut.state);
  return <section id="editor-next-ai-rough-cut" className="editor-next-ai fc-tool-panel" aria-label="AI 粗剪" aria-busy={roughBusy}>
    <header className="fc-tool-panel__heading"><h2>AI 粗剪</h2><p>先生成源区间，审阅后确认追加；保留现有多轨剪辑。</p></header>
    <p className="fc-tool-panel__time">{roughCut.sourceRange ? `素材源范围 ${seconds(roughCut.sourceRange.start)}–${seconds(roughCut.sourceRange.end)} 秒` : '当前素材源范围尚未就绪。'}</p>
    <div className="editor-next-ai-actions"><button type="button" className="fc-tool-panel__primary" disabled={blocked || roughBusy || !roughCut.sourceRange} onClick={onGenerate}>{roughCut.state.status === 'generating' ? 'AI 粗剪生成中…' : '生成粗剪区间'}</button></div>
    <p className="fc-tool-panel__status" role={roughCut.state.status === 'error' || roughCut.state.status === 'stale' ? 'alert' : 'status'} aria-live="polite" aria-atomic="true">{roughCut.state.message}</p>
    {!roughCut.state.plan && <div className="fc-tool-panel__empty"><strong>等待区间预览</strong><p>生成后会列出每个源区间，确认前不会追加片段。</p></div>}
    {roughCut.state.plan && <div className="editor-next-ai-review fc-tool-panel__group" aria-label="粗剪源区间预览">
      <h3>{roughCut.state.plan.title}</h3><p>{roughCut.state.plan.summary}</p>
      <div className="fc-tool-panel__meta"><span>来源：{roughCut.state.plan.source === 'ai' ? 'AI' : `${roughCut.state.plan.source}（非 AI 结果）`}</span><span>{roughCut.state.plan.ranges.length} 个区间</span></div>
      <p>建议主题范围：{seconds(roughCut.state.plan.start)}–{seconds(roughCut.state.plan.end)} 秒；实际追加以下服务端返回区间。</p>
      <ol className="fc-tool-panel__ranges">{roughCut.state.plan.ranges.map((range, index) => <li key={`${index}:${range.start}:${range.end}`}><span>区间 {index + 1}</span><span className="fc-tool-panel__time">{seconds(range.start)}–{seconds(range.end)} 秒</span></li>)}</ol>
      {roughCut.state.plan.ranges.length === 0 && <p>本次没有可追加源区间，请重新生成。</p>}
      <details className="fc-tool-panel__help"><summary>粗剪理由与钩子字幕</summary><ul>{roughCut.state.plan.reasons.map((reason, index) => <li key={index}>{reason}</li>)}</ul><p>钩子字幕编号（仅显示，不复制、不重排）：{roughCut.state.plan.hooks.join('、') || '无'}。</p></details>
    </div>}
    <div className="fc-tool-panel__group"><p className="fc-tool-panel__consequence">确认后批量追加到工程末尾，不替换现有片段。</p><button type="button" className="fc-tool-panel__primary" disabled={blocked || roughCut.state.status !== 'ready' || roughCut.state.plan?.source !== 'ai' || !roughCut.state.plan.ranges.length} onClick={onApply}>确认并追加到工程末尾</button></div>
    <details className="fc-tool-panel__help"><summary>追加方式</summary><p>使用已保存 AI 连接生成独立源区间。追加位置为序列时长与全轨末端的较大值之后，不覆盖或重剪现有多轨。分析候选与本次粗剪区间相互独立。</p></details>
  </section>;
}

export interface AiPanelProps {
  state: EditorNextAiState;
  onGenerate: () => void;
  onApply: () => void;
  candidates?: CandidateContext;
  roughCut?: AiRoughCutContext;
  view?: AiPanelView;
  /** Keep this controller mounted; the owner supplies one drawer around the current content. */
  renderView?: (content: ReactNode, view: AiPanelView) => ReactNode;
}

export function AiPanel({ state, onGenerate, onApply, candidates, roughCut, view = 'all', renderView }: AiPanelProps) {
  const dialog = useRef<HTMLDialogElement>(null);
  const candidatePending = useRef(false);
  const [candidateBusy, setCandidateBusy] = useState(false);
  const changeCandidateBusy = useCallback((value: boolean) => {
    candidatePending.current = value;
    setCandidateBusy(value);
  }, []);
  const roughBusy = !!roughCut && roughCutIsBusy(roughCut.state);
  const busy = aiIsBusy(state) || roughBusy;
  const writing = state.status === 'flushing' || state.status === 'applying' || roughCut?.state.status === 'applying';
  const progressMessage = roughCut?.state.status === 'applying' ? roughCut.state.message : state.message;
  const proposal = state.proposal;
  useEffect(() => {
    const element = dialog.current;
    if (writing && element && !element.open) element.showModal();
    if (!writing && element?.open) element.close();
    return () => { if (element?.open) element.close(); };
  }, [writing]);
  const roughContent = roughCut && <AiRoughCutPanel context={roughCut} blocked={busy || candidateBusy}
    onGenerate={() => { if (!candidatePending.current && !busy) roughCut.onGenerate(); }}
    onApply={() => { if (!candidatePending.current && !busy) roughCut.onApply(); }} />;
  const rulesContent = <section className="editor-next-ai fc-tool-panel" aria-labelledby="editor-next-ai-title" data-ai-state={state.status}>
    <header className="fc-tool-panel__heading"><h2 id="editor-next-ai-title">规则剪辑建议</h2><p>先审阅，再应用；应用前先保存本地修改。</p></header>
    <p id="editor-next-ai-help">依据已保存工程与字幕生成规则建议，不调用外部模型。生成不会修改工程。</p>
    <p className="fc-tool-panel__status" id="editor-next-ai-status" role="status" aria-live="polite" aria-atomic="true">{state.message}{state.code ? `（${state.code}）` : ''}</p>
    <div className="editor-next-ai-actions">
      <button type="button" disabled={busy || candidateBusy || state.status === 'applied'} aria-describedby="editor-next-ai-help editor-next-ai-status" onClick={() => { if (!candidatePending.current) onGenerate(); }}>{state.status === 'generating' ? '规则生成中…' : state.status === 'idle' ? '生成规则建议' : '重新生成规则建议'}</button>
      <button type="button" className="fc-tool-panel__primary" disabled={roughBusy || candidateBusy || state.status !== 'ready' || !proposal?.timeline.length} aria-describedby="editor-next-ai-status" onClick={() => { if (!candidatePending.current) onApply(); }}>{writing ? '应用中…' : '应用规则建议'}</button>
    </div>
    {proposal && <div className="editor-next-ai-review fc-tool-panel__group" aria-label="建议摘要">
      <p>基线修订 {proposal.baseRevision} · {proposal.source === 'rules' ? '规则建议' : proposal.source} · 提议保留 {proposal.timeline.length} 个时间线片段</p>
      <p>字幕片段：保留 {proposal.segmentReasons.filter(segment => segment.keep).length} / {proposal.segmentReasons.length}。请试听后再决定是否应用。</p>
      {proposal.timeline.length === 0 && <p role="alert">建议没有可用片段，不能应用。请检查字幕后重新生成。</p>}
      <details open><summary>改动摘要（{proposal.changes.length}）</summary>{proposal.changes.length ? <ul>{proposal.changes.map((change, index) => <li key={`${change.itemId}:${index}`}><strong>{changeLabels[change.kind] ?? change.kind}</strong> · {change.itemId}：{change.reason}{change.after && <span>（源时间 {seconds(change.after.start)}–{seconds(change.after.end)} 秒）</span>}</li>)}</ul> : <p>没有需要说明的改动。</p>}</details>
      <details><summary>字幕取舍理由（{proposal.segmentReasons.length}）</summary><ul>{proposal.segmentReasons.map((segment, index) => <li key={`${segment.segmentIndex}:${index}`}>{segment.keep ? '保留' : '排除'} · {seconds(segment.start)}–{seconds(segment.end)} 秒：{segment.reason}</li>)}</ul></details>
    </div>}
  </section>;
  const content = (analysis: ReactNode, events: ReactNode) => {
    const selected = view === 'analysis' ? analysis : view === 'events' ? events : view === 'rough-cut' ? roughContent : <>{analysis}{events}{roughContent}{rulesContent}</>;
    return renderView ? renderView(selected, view) : selected;
  };
  return <>
    {candidates ? <CandidatePanel key={candidates.clipId} context={candidates} blocked={busy} onBusyChange={changeCandidateBusy} renderPanels={content} /> : content(null, null)}
    <div className="editor-next-ai fc-tool-panel__progress-host"><dialog className="editor-next-ai-progress" ref={dialog} aria-label="正在应用剪辑建议" aria-modal="true" onCancel={event => event.preventDefault()} onKeyDownCapture={event => event.stopPropagation()}>
      <p role="status" aria-live="polite" aria-atomic="true" tabIndex={0} aria-label="应用进度">{progressMessage}</p>
      <p>{roughCut?.state.status === 'applying' ? '批量追加期间暂停编辑；保留现有多轨和原生撤销，不重新载入工程。' : '应用期间暂停编辑，成功后会重新载入工程。'}</p>
    </dialog></div>
  </>;
}
