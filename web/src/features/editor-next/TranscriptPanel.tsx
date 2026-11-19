import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import type { TranscriptSegment } from '../../api/types';
import { formatTranscriptClock } from './editorNextTranscriptApi';
import type { EditorNextTranscriptState, TranscriptTaskView } from './useEditorNextTranscript';
import './r82-editor-next-rail.css';
import './filmcraft-tool-panels.css';

/**
 * R82 第一批右侧栏：可见的面板触发器（rail）+「转写字幕」面板。
 *
 * - rail 是真正的右侧可见入口，两个面板互斥，沿用 EditorNextPage 现有的 drawer manager
 *   （同一个 state + HostDrawer + Esc/focus 恢复），不新增 store。
 * - 面板读取真实任务/字幕；字幕精修写工程覆盖，SRT 使用服务端生效字幕导出。
 * - 不出现 jobs/srtPath 之类实现细节，也不展示伪进度。
 */

export type EditorNextRailPanel = 'ai' | 'transcript' | 'chapters' | 'music';

export interface EditorNextRailProps {
  active: EditorNextRailPanel | null;
  /** AI 面板要求 editingReady；转写面板可先查看，修改另受就绪态约束。 */
  aiReady: boolean;
  onSelect: (panel: EditorNextRailPanel) => void;
  children?: ReactNode;
}

export function EditorNextRail({ active, aiReady, onSelect, children }: EditorNextRailProps) {
  return (
    <nav className="editor-next-rail" aria-label="编辑器右侧面板">
      <button
        type="button"
        className="editor-next-rail__item"
        data-active={active === 'ai'}
        aria-expanded={active === 'ai'}
        aria-controls="editor-next-ai-drawer"
        aria-haspopup="dialog"
        aria-label="AI 建议面板"
        title="AI 建议"
        disabled={!aiReady}
        onClick={() => onSelect('ai')}
      >
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M11 3.5l1.7 4.3 4.3 1.7-4.3 1.7L11 15.5 9.3 11.2 5 9.5l4.3-1.7z" />
          <path d="M17.8 14.6l.9 2.2 2.3.9-2.3.9-.9 2.2-.9-2.2-2.3-.9 2.3-.9z" />
        </svg>
        <span className="editor-next-rail__caption" aria-hidden="true">AI</span>
      </button>
      <button
        type="button"
        className="editor-next-rail__item"
        data-active={active === 'transcript'}
        aria-expanded={active === 'transcript'}
        aria-controls="editor-next-transcript-drawer"
        aria-haspopup="dialog"
        aria-label="转写字幕面板"
        title="转写字幕"
        onClick={() => onSelect('transcript')}
      >
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <rect x="3" y="5.5" width="18" height="13" rx="2.5" />
          <path d="M7 10.5h3.5M7 14h6M14.5 10.5h2.5M15 14h2" />
        </svg>
        <span className="editor-next-rail__caption" aria-hidden="true">字幕</span>
      </button>
      {(['chapters', 'music'] as const).map(panel => <button key={panel} type="button" className="editor-next-rail__item"
        data-active={active === panel} aria-expanded={active === panel} aria-controls={`editor-next-${panel}-drawer`} aria-haspopup="dialog"
        aria-label={panel === 'chapters' ? '章节事件面板' : '整场配乐面板'} title={panel === 'chapters' ? '章节事件' : '整场配乐'} onClick={() => onSelect(panel)}>
        <span className="editor-next-rail__caption">{panel === 'chapters' ? '章节' : '配乐'}</span>
      </button>)}
      {children}
    </nav>
  );
}

const STATUS_LABELS: Record<string, string> = {
  pending: '排队中',
  running: '转写中',
  completed: '已完成',
  failed: '失败',
  interrupted: '已中断',
  retried: '已重试',
};

function statusLabel(status: string): { text: string; known: boolean } {
  return Object.prototype.hasOwnProperty.call(STATUS_LABELS, status) ? { text: STATUS_LABELS[status], known: true } : { text: status, known: false };
}

function taskSummary(task: TranscriptTaskView): string {
  const label = statusLabel(task.status);
  return label.known ? label.text : `原始状态：${label.text}`;
}

export interface TranscriptPanelProps {
  state: EditorNextTranscriptState;
  eventId: string | null;
  canStart: boolean;
  canRestart: boolean;
  onStart: () => void;
  onRestart: () => void;
  onRefresh: () => void;
  /** 重新载入工程会丢掉本机未保存的改动，所以草稿未保存时由调用方给出原因并禁用。 */
  onReloadProject: () => void;
  reloadBlockReason: string | null;
  onAcknowledgeUpdate: () => void;
  canEdit?: boolean;
  saveBusy?: boolean;
  saveError?: string;
  onSaveCaptions?: (segments: readonly TranscriptSegment[] | null) => Promise<boolean>;
  onDraftChange?: (dirty: boolean) => void;
  srtUrl?: string | null;
  onPlaceCaptions?: () => void;
  canPlaceCaptions?: boolean;
  placeBusy?: boolean;
  placeError?: string | null;
  placeBlockReason?: string | null;
}

export function TranscriptPanel({
  state,
  eventId,
  canStart,
  canRestart,
  onStart,
  onRestart,
  onRefresh,
  onReloadProject,
  reloadBlockReason,
  onAcknowledgeUpdate,
  canEdit = false,
  saveBusy = false,
  saveError = '',
  onSaveCaptions,
  onDraftChange,
  srtUrl,
  onPlaceCaptions,
  canPlaceCaptions,
  placeBusy = false,
  placeError = null,
  placeBlockReason = null,
}: TranscriptPanelProps) {
  const segments = state.transcript?.segments ?? [];
  const [draft, setDraft] = useState<{ start: string; end: string; text: string }[] | null>(null);
  const [draftBase, setDraftBase] = useState('');
  const [editError, setEditError] = useState('');
  const fingerprint = JSON.stringify([state.transcriptVersion, segments]);
  const changedWhileEditing = draft !== null && draftBase !== fingerprint;
  const hasDraft = draft !== null;
  useEffect(() => { onDraftChange?.(hasDraft); }, [hasDraft, onDraftChange]);
  useEffect(() => () => { onDraftChange?.(false); }, [onDraftChange]);
  useEffect(() => {
    if (draft === null) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [draft]);
  const beginEdit = () => {
    setDraft(segments.map(segment => ({ start: String(segment.start), end: String(segment.end), text: segment.text })));
    setDraftBase(fingerprint);
    setEditError('');
  };
  const save = async () => {
    if (!draft || !canEdit || saveBusy || changedWhileEditing || !onSaveCaptions) return;
    const edited = draft.map(segment => ({ start: Number(segment.start), end: Number(segment.end), text: segment.text }));
    if (draft.some(segment => !segment.start.trim() || !segment.end.trim()) || edited.some(segment =>
      !Number.isFinite(segment.start) || !Number.isFinite(segment.end) || segment.start < 0 || segment.end <= segment.start || segment.text.length > 1000)) {
      setEditError('请填写有效的源视频秒数：起点至少为 0，终点大于起点，每段文字最多 1000 字。');
      return;
    }
    setEditError('');
    if (await onSaveCaptions(edited)) setDraft(null);
  };
  const editSegment = (index: number, field: 'start' | 'end' | 'text', value: string) =>
    setDraft(current => current?.map((segment, i) => i === index ? { ...segment, [field]: value } : segment) ?? null);
  const request = state.request;
  return (
    <section className="editor-next-transcript fc-tool-panel" id="editor-next-transcript" aria-label="转写字幕内容"
      aria-busy={state.status === 'loading'} data-transcript-state={state.status}>
      <header className="editor-next-transcript__heading fc-tool-panel__heading">
        <h2>转写字幕</h2>
        <p>生成字幕，校对文字与源时间；不自动修改剪辑。</p>
      </header>

      {state.updated && (
        <div className="editor-next-transcript__notice" role="status" aria-live="polite">
          <p>字幕已更新，当前时间线没有改动。需要同步工程数据时请重新载入。</p>
          <div className="editor-next-transcript__actions">
            <button type="button" onClick={onReloadProject} disabled={reloadBlockReason !== null || draft !== null || saveBusy}
              aria-describedby={reloadBlockReason !== null ? 'editor-next-transcript-reload-block' : undefined}>
              重新载入工程
            </button>
            <button type="button" onClick={onAcknowledgeUpdate}>稍后处理</button>
          </div>
          {reloadBlockReason !== null && (
            <p className="editor-next-transcript__hint" id="editor-next-transcript-reload-block">{reloadBlockReason}</p>
          )}
        </div>
      )}

      <div className="editor-next-transcript__actions">
        <button type="button" className="is-primary" onClick={onStart} disabled={!canStart || draft !== null || saveBusy}
          aria-describedby="editor-next-transcript-hint">
          {state.transcript ? '更新字幕' : '生成字幕'}
        </button>
        <button type="button" onClick={onRestart} disabled={!canRestart || draft !== null || saveBusy}>重启转写</button>
        <button type="button" onClick={onRefresh} disabled={state.status === 'loading' || saveBusy}>刷新</button>
      </div>
      <p className="editor-next-transcript__hint" id="editor-next-transcript-hint">生成会排队转写；重启会取消进行中的任务并重新排队。字幕完成后，时间线保持不变。</p>
      <details className="fc-tool-panel__help"><summary>转写与载入说明</summary><p>完成后写入字幕并更新素材字幕路径；仅在排队时沿用已排队任务。重新载入工程前须先保存工程与字幕草稿。</p></details>
      {!eventId && <p className="editor-next-transcript__hint">当前视频没有可用的事件标识，生成和重启转写不可用。</p>}

      {request.phase !== 'idle' && request.message && (
        <p className="editor-next-transcript__request" role={request.phase === 'error' ? 'alert' : 'status'}
          aria-live="polite" data-request-phase={request.phase}>{request.message}</p>
      )}

      <section className="editor-next-transcript__task" aria-labelledby="editor-next-transcript-task-title"
        data-task-status={state.task?.status ?? 'none'}>
        <h3 id="editor-next-transcript-task-title">转写任务</h3>
        {!eventId
          ? <p className="editor-next-transcript__muted">没有可归属的事件标识，因此不显示任何转写任务。</p>
          : !state.task
            ? <p className="editor-next-transcript__muted">{request.phase === 'accepted' ? '请求已接受，等待任务状态。' : '这个素材事件还没有转写任务记录。'}</p>
            : (
              <div className="editor-next-transcript__task-body">
                <p>
                  <span className="editor-next-transcript__chip">{taskSummary(state.task)}</span>
                  {!statusLabel(state.task.status).known && <span className="editor-next-transcript__muted"> 未识别的状态，按服务端原样显示。</span>}
                </p>
                {state.task.status === 'running' && state.task.progress !== null
                  && Number.isFinite(state.task.progress) && state.task.progress >= 0 && state.task.progress <= 100 && (
                  <p className="editor-next-transcript__progress">
                    <progress max={100} value={state.task.progress} aria-label="转写进度" />
                    <span>{state.task.progress}%</span>
                  </p>
                )}
                {state.task.error && <p role="alert">{state.task.error}</p>}
              </div>
            )}
      </section>

      <section className="editor-next-transcript__text" aria-labelledby="editor-next-transcript-text-title">
        <div className="fc-tool-panel__meta"><h3 id="editor-next-transcript-text-title">字幕内容</h3><span>{segments.length} 段{draft !== null ? ' · 编辑中' : ''}</span></div>
        <div className="editor-next-transcript__actions">
          {draft === null ? <button type="button" onClick={beginEdit} disabled={!canEdit || segments.length === 0}>编辑字幕</button> : <>
            <button type="button" className="fc-tool-panel__primary" onClick={() => { void save(); }} disabled={!canEdit || saveBusy || changedWhileEditing}>
              {saveBusy ? '正在保存字幕…' : '保存字幕并载入工程'}
            </button>
            <button type="button" disabled={saveBusy} onClick={() => { setDraft(null); setEditError(''); }}>取消编辑</button>
          </>}
          {draft === null && state.transcriptSource === 'override' && onSaveCaptions && <button type="button"
            disabled={!canEdit || saveBusy} onClick={() => { void onSaveCaptions(null); }}>恢复原转写并载入</button>}
          {srtUrl && state.transcript && draft === null && !saveBusy
            ? <a href={srtUrl} download>导出 SRT</a>
            : <span className="editor-next-transcript__muted">{draft !== null ? '先保存或取消编辑，再导出 SRT。' : '字幕就绪后可导出 SRT。'}</span>}
          <button type="button" className="fc-tool-panel__primary"
            disabled={!onPlaceCaptions || canPlaceCaptions !== true || placeBusy || saveBusy || draft !== null}
            onClick={() => { if (onPlaceCaptions && canPlaceCaptions === true && !placeBusy && !saveBusy && draft === null) onPlaceCaptions(); }}>
            {placeBusy ? '放置中…' : '放置字幕到时间线'}
          </button>
        </div>
        {draft !== null
          ? <p className="editor-next-transcript__muted">请先保存字幕修改并载入工程，再放置字幕到时间线。</p>
          : placeBlockReason && <p className="editor-next-transcript__muted">{placeBlockReason}</p>}
        {placeError && <p role="alert">{placeError}</p>}
        {draft !== null && <p className="editor-next-transcript__hint">正在编辑本工程的字幕覆盖。时间为源视频秒数，可保留小数；保存后重新载入，ASR 原文保留。关闭面板不会丢弃编辑。</p>}
        {changedWhileEditing && <p role="alert">服务器字幕已变化，当前编辑内容已保留。请复制需要的文字，取消编辑并读取新字幕后再修改。</p>}
        {(editError || saveError) && <p role="alert">{editError || saveError}</p>}
        {draft !== null && <ol className="editor-next-transcript__segments">
          {draft.map((segment, index) => <li key={index}>
            <div className="editor-next-transcript__times">
              <label>起点（秒）<input type="number" min="0" step="any" value={segment.start} disabled={saveBusy}
                aria-label={`第 ${index + 1} 段起点（秒）`} onChange={event => editSegment(index, 'start', event.target.value)} /></label>
              <label>终点（秒）<input type="number" min="0" step="any" value={segment.end} disabled={saveBusy}
                aria-label={`第 ${index + 1} 段终点（秒）`} onChange={event => editSegment(index, 'end', event.target.value)} /></label>
            </div>
            <textarea value={segment.text} maxLength={1000} rows={2} disabled={saveBusy}
              aria-label={`第 ${index + 1} 段字幕文字`} onChange={event => editSegment(index, 'text', event.target.value)} />
          </li>)}
        </ol>}
        {state.status === 'loading' && <p role="status" aria-live="polite">正在读取字幕…</p>}
        {state.status === 'error' && <p role="alert">{state.message || '读取字幕失败。'}</p>}
        {draft === null && (segments.length > 0
          ? (
            <ol className="editor-next-transcript__segments">
              {segments.map((segment, index) => (
                <li key={`${index}:${segment.start}`}>
                  <span className="editor-next-transcript__time">
                    {formatTranscriptClock(segment.start)} – {formatTranscriptClock(segment.end)}
                  </span>
                  <p>{segment.text}</p>
                </li>
              ))}
            </ol>
          )
          : state.transcript?.text
            ? <p className="editor-next-transcript__plain">{state.transcript.text}</p>
            : state.status === 'ready' ? <p className="editor-next-transcript__muted">还没有字幕内容。可以先点「生成字幕」。</p> : null)}
        {state.transcript?.language && <p className="editor-next-transcript__meta">语言：{state.transcript.language}</p>}
      </section>

      <p className="editor-next-transcript__checked" role="status" aria-live="polite">
        {state.checkedAt
          ? `上次检查：${new Date(state.checkedAt).toLocaleTimeString('zh-CN', { hour12: false })}`
          : '尚未读取。'}
      </p>
    </section>
  );
}
