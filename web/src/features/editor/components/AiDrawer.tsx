import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  Check,
  Crosshair,
  Download,
  ListTree,
  LoaderCircle,
  RefreshCw,
  Search,
  Sparkles,
  X,
} from 'lucide-react';
import type { AiAdvice, EditorPayload, StudioEvent, TranscriptPreview, TranscriptSegment } from '../../../api/types';
import { adviceView } from '../aiAdvice';
import type { MusicJobState } from '../../../api/music';
import type { MusicTrackOption } from '../musicTrack';
import { MusicPanel } from './MusicPanel';
import { formatClock, formatTimecode } from '../project';

export type AiTab = 'candidates' | 'transcript' | 'chapters' | 'music';

interface AiDrawerProps {
  open: boolean;
  tab: AiTab;
  clipId: string | null;
  onTranscriptReady: (clipId: string, transcript: EditorPayload['transcript']) => void;
  transcript: TranscriptPreview | null;
  transcriptError: string | null;
  transcriptBusy: boolean;
  events: StudioEvent[];
  adopted: Set<string>;
  activeEventId: string | null;
  eventsBusy: boolean;
  eventError: string | null;
  advice: AiAdvice | null;
  adviceBusy: boolean;
  adviceError: string | null;
  musicAssets: MusicTrackOption[];
  musicBusy: boolean;
  musicError: string | null;
  musicJob: MusicJobState | null;
  musicAssetId: string | null;
  project: import('../../../api/types').EditorProject | null;
  duckMusic: boolean;
  musicVolume: number;
  fps: number;
  onTabChange: (tab: AiTab) => void;
  onClose: () => void;
  onLocateSource: (time: number) => void;
  onAdoptEvent: (event: StudioEvent) => void;
  onApply: () => void;
  onExtract: () => void;
  onAutoPlan: () => void;
  onAnalyze: () => void;
  onRetryTranscript: () => void;
  onGenerateMusic: (durationSeconds: number) => void;
  onRefreshMusic: () => void;
  onSelectMusic: (assetId: string | null) => void;
  onToggleDuckMusic: (value: boolean) => void;
  onMusicVolume: (percent: number) => void;
}

async function transcriptionRequest<T>(clipId: string, resource: 'transcribe' | 'editor', signal: AbortSignal): Promise<T> {
  const base = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');
  const response = await fetch(`${base}/api/studio/clips/${encodeURIComponent(clipId)}/${resource}`, {
    method: resource === 'transcribe' ? 'POST' : 'GET',
    headers: { Accept: 'application/json' },
    cache: 'no-store',
    signal,
  });
  if (response.status === 404) throw new Error('找不到这个视频');
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(body?.error ? `转写请求失败：${body.error}` : `转写请求失败（HTTP ${response.status}），请稍后重试`);
  }
  return response.json().catch(() => { throw new Error('后端返回的转写数据格式有误，请稍后重试'); });
}

function waitForTranscriptPoll(signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    signal.throwIfAborted();
    const cancel = () => { window.clearTimeout(timer); reject(signal.reason); };
    const timer = window.setTimeout(() => {
      signal.removeEventListener('abort', cancel);
      resolve();
    }, 1500);
    signal.addEventListener('abort', cancel, { once: true });
  });
}

function eventSourceLabel(source: string): string {
  if (source === 'subtitle') return '字幕事件';
  if (source === 'danmaku') return '弹幕事件';
  return source || '本地事件';
}

function highlight(text: string, query: string) {
  const needle = query.trim();
  if (!needle) return text;
  const index = text.toLowerCase().indexOf(needle.toLowerCase());
  if (index < 0) return text;
  return (
    <>
      {text.slice(0, index)}
      <mark>{text.slice(index, index + needle.length)}</mark>
      {text.slice(index + needle.length)}
    </>
  );
}

function srtTime(seconds: number): string {
  const total = Math.max(0, Math.round(seconds * 1000));
  const hours = Math.floor(total / 3_600_000);
  const minutes = Math.floor((total % 3_600_000) / 60_000);
  const secs = Math.floor((total % 60_000) / 1000);
  const millis = total % 1000;
  return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')},${String(millis).padStart(3, '0')}`;
}

function downloadTranscript(transcript: TranscriptPreview, title: string) {
  if (transcript.segments.length === 0) return;
  const body = transcript.segments
    .map((segment, index) => `${index + 1}\n${srtTime(segment.start)} --> ${srtTime(segment.end)}\n${segment.text}\n`)
    .join('\n');
  const blob = new Blob([body], { type: 'text/plain;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `${title.replace(/[\\/:*?"<>|]/g, '_') || 'transcript'}.srt`;
  anchor.click();
  URL.revokeObjectURL(url);
}

function CandidateCard({
  event,
  adopted,
  active,
  fps,
  onLocate,
  onAdopt,
}: {
  event: StudioEvent;
  adopted: boolean;
  active: boolean;
  fps: number;
  onLocate: () => void;
  onAdopt: () => void;
}) {
  return (
    <article className={`candidate-card${adopted ? ' is-adopted' : ''}${active ? ' is-active' : ''}`}>
      <div className="candidate-topline">
        <span className="candidate-time">
          {formatClock(event.start)} → {formatClock(event.end)}
        </span>
        {adopted ? (
          <span className="candidate-adopted">
            <Check size={10} /> 已采用
          </span>
        ) : (
          <span className="candidate-source">{eventSourceLabel(event.source)}</span>
        )}
      </div>
      <h3 title={event.title}>{event.title || '未命名候选'}</h3>
      {event.summary ? (
        <p className="candidate-rationale" title={event.summary}>
          {event.summary}
        </p>
      ) : null}
      {event.hook ? <blockquote>{event.hook}</blockquote> : null}
      <div className="candidate-actions">
        <span>{formatTimecode(event.start, fps)}</span>
        <button type="button" className="text-button" onClick={onLocate}>
          <Crosshair size={10} /> 定位
        </button>
        <button
          type="button"
          className={`text-button is-primary${adopted ? ' is-adopted' : ''}`}
          onClick={onAdopt}
        >
          {adopted ? '撤销采用' : '采用'}
        </button>
      </div>
    </article>
  );
}

function AdviceCard({
  advice,
  busy,
  error,
  onAnalyze,
}: {
  advice: AiAdvice | null;
  busy: boolean;
  error: string | null;
  onAnalyze: () => void;
}) {
  const view = adviceView(advice);
  return (
    <section className="ai-advice" aria-label="AI 总结">
      <header className="ai-advice-head">
        <span>
          <Sparkles size={11} /> AI 总结
        </span>
        <button type="button" className="text-button ai-advice-cta" onClick={onAnalyze} disabled={busy}
          title="用设置里的模型跑一次整场分析">
          {busy ? <LoaderCircle size={10} className="spin" /> : <RefreshCw size={10} />}
          {busy ? '分析中…' : view.empty ? '生成总结' : '重新生成'}
        </button>
      </header>
      {error ? <p className="ai-error">{error}</p> : null}
      {view.empty ? (
        <>
          <p className="ai-advice-empty">点「生成总结」，拿到整场概览、文案草稿与切片建议。</p>
          <p className="ai-advice-hint">用的是「设置 → AI」里选好的模型；没配过就先在那里配。</p>
        </>
      ) : (
        <>
          {view.summary ? <p className="ai-advice-summary">{view.summary}</p> : null}
          {view.captions.length > 0 ? (
            <ul className="ai-advice-list" aria-label="可用文案">
              {view.captions.map((caption, index) => (
                <li key={`caption-${index}`}>{caption}</li>
              ))}
            </ul>
          ) : null}
          {view.tips.length > 0 ? (
            <ul className="ai-advice-list is-tips" aria-label="切片建议">
              {view.tips.map((tip, index) => (
                <li key={`tip-${index}`}>{tip}</li>
              ))}
            </ul>
          ) : null}
        </>
      )}
    </section>
  );
}

function CandidateView({
  events,
  adopted,
  activeEventId,
  eventsBusy,
  eventError,
  advice,
  adviceBusy,
  adviceError,
  hasTranscript,
  transcriptionEntry,
  fps,
  onLocate,
  onAdopt,
  onExtract,
  onAutoPlan,
  onAnalyze,
}: {
  events: StudioEvent[];
  adopted: Set<string>;
  activeEventId: string | null;
  eventsBusy: boolean;
  eventError: string | null;
  advice: AiAdvice | null;
  adviceBusy: boolean;
  adviceError: string | null;
  hasTranscript: boolean;
  transcriptionEntry: ReactNode;
  fps: number;
  onLocate: (event: StudioEvent) => void;
  onAdopt: (event: StudioEvent) => void;
  onExtract: () => void;
  onAutoPlan: () => void;
  onAnalyze: () => void;
}) {
  return (
    <div className="ai-view">
      <AdviceCard advice={advice} busy={adviceBusy} error={adviceError} onAnalyze={onAnalyze} />
      <div className="ai-view-toolbar">
        <span>
          后端事件 <b>{events.length}</b> 个
        </span>
        <button
          type="button"
          className="text-button"
          onClick={onAutoPlan}
          disabled={eventsBusy || !hasTranscript}
          title={hasTranscript ? '按转录和候选生成自动剪辑方案' : '需要先完成转写'}
        >
          {eventsBusy ? <LoaderCircle size={10} className="spin" /> : <Sparkles size={10} />}
          {eventsBusy ? '生成中…' : '生成方案'}
        </button>
        <button
          type="button"
          className="text-button"
          onClick={onExtract}
          disabled={eventsBusy || !hasTranscript}
          title={hasTranscript ? '从已有转写字幕提取候选' : '需要先完成转写'}
        >
          {eventsBusy ? <LoaderCircle size={10} className="spin" /> : <RefreshCw size={10} />}
          {eventsBusy ? '提取中…' : '提取候选'}
        </button>
      </div>
      <div className="ai-summary">
        <span>
          <Sparkles size={11} /> {events.length > 0 ? `找到 ${events.length} 个候选` : '还没有候选'}
        </span>
        <span className="ai-summary-note">
          {hasTranscript ? '来自真实字幕' : '等待转写'} · {adopted.size} 已采用
        </span>
      </div>
      {eventError ? <p className="ai-error">{eventError}</p> : null}
      <div className="candidate-list">
        {events.map((event) => (
          <CandidateCard
            key={event.id}
            event={event}
            adopted={adopted.has(event.id)}
            active={event.id === activeEventId}
            fps={fps}
            onLocate={() => onLocate(event)}
            onAdopt={() => onAdopt(event)}
          />
        ))}
        {events.length === 0 && !eventsBusy ? (
          <div className="ai-empty">
            <Sparkles size={16} />
            <strong>{hasTranscript ? '字幕已就绪，可以提取候选' : '这个切片还没有转写'}</strong>
            <p>
              {hasTranscript
                ? '点击“提取候选”，后端会从字幕中生成真实的高光事件。'
                : '点击“开始转写”，Whisper 完成后即可读取文稿并生成候选。'}
            </p>
            {!hasTranscript ? transcriptionEntry : null}
          </div>
        ) : null}
        {!hasTranscript && (events.length > 0 || eventsBusy) ? transcriptionEntry : null}
      </div>
      <div className="ai-note">
        <Sparkles size={11} />
        <span>AI 只生成候选。采用后仍需点击「整理为时间线」，不会直接覆盖工程。</span>
      </div>
    </div>
  );
}

function TranscriptView({
  transcript,
  error,
  busy,
  transcribing,
  transcriptionEntry,
  onRetry,
  onLocate,
}: {
  transcript: TranscriptPreview | null;
  error: string | null;
  busy: boolean;
  transcribing: boolean;
  transcriptionEntry: ReactNode;
  onRetry: () => void;
  onLocate: (segment: TranscriptSegment) => void;
}) {
  const [query, setQuery] = useState('');
  const [selectedStart, setSelectedStart] = useState<number | null>(null);
  const filtered = useMemo(() => {
    const segments = transcript?.segments ?? [];
    const needle = query.trim().toLowerCase();
    if (!needle) return segments;
    return segments.filter((segment) => segment.text.toLowerCase().includes(needle));
  }, [query, transcript]);

  if (!transcript || transcript.segments.length === 0) {
    return (
      <div className="ai-view transcript-view">
        <div className={`ai-empty${error ? ' is-error' : ''}`}>
          <ListTree size={16} />
          <strong>{error ? '转写没读出来' : busy ? '正在读取转写…' : '还没有转写'}</strong>
          {error ? <p>{error}</p> : null}
          {!error && !busy ? <p>这个切片目前没有字幕。点击“开始转写”，完成后字幕会自动显示。</p> : null}
        </div>
        {transcriptionEntry}
        <div className="ai-empty-actions">
          <button type="button" className="text-button" onClick={onRetry} disabled={busy || transcribing}>
            {busy ? <LoaderCircle size={10} className="spin" /> : <RefreshCw size={10} />}
            {busy ? '读取中…' : '重新读取'}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="ai-view transcript-view">
      <label className="ai-search">
        <Search size={11} />
        <input
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="搜索对白或关键词"
        />
      </label>
      <div className="transcript-meta">
        <span>{transcript.language || '未知语言'} · Whisper</span>
        <span>
          显示 {filtered.length} / {transcript.segments.length} 段
        </span>
      </div>
      <div className="transcript-list">
        {filtered.map((segment) => (
          <button
            key={`${segment.start}-${segment.end}`}
            type="button"
            className={`transcript-cue is-real${segment.start === selectedStart ? ' is-active' : ''}`}
            onClick={() => {
              setSelectedStart(segment.start);
              onLocate(segment);
            }}
          >
            <span className="cue-time">{formatClock(segment.start)}</span>
            <span className="cue-text">{highlight(segment.text, query)}</span>
          </button>
        ))}
        {filtered.length === 0 ? <p className="empty-state">没有匹配的转写片段。</p> : null}
      </div>
      <div className="ai-note">
        <ListTree size={11} />
        <span>共 {transcript.segments.length} 段 · 点击片段可定位到节目画面</span>
      </div>
    </div>
  );
}

function ChapterView({
  events,
  onLocate,
}: {
  events: StudioEvent[];
  onLocate: (event: StudioEvent) => void;
}) {
  const sorted = [...events].sort((a, b) => a.start - b.start);
  return (
    <div className="ai-view chapter-view">
      <div className="ai-view-toolbar">
        <span>
          字幕事件 <b>{sorted.length} 个</b>
        </span>
        <span className="chapter-unit">按时间排序</span>
      </div>
      {sorted.length > 0 ? (
        <div className="chapter-list">
          {sorted.map((event, index) => (
            <button key={event.id} type="button" className="chapter-row" onClick={() => onLocate(event)}>
              <span className="chapter-index">{String(index + 1).padStart(2, '0')}</span>
              <span className="chapter-main">
                <span className="chapter-title">
                  {event.title || '未命名事件'}
                  <span className="chapter-note">{event.summary}</span>
                </span>
              </span>
              <span className="chapter-time">{formatClock(event.start)}</span>
              <span className="chapter-score">{Math.max(1, Math.round(event.end - event.start))}s</span>
            </button>
          ))}
        </div>
      ) : (
        <div className="ai-empty">
          <ListTree size={16} />
          <strong>还没有事件</strong>
          <p>先在“候选”里从字幕提取事件，章节会按同一批真实数据展示。</p>
        </div>
      )}
      <div className="ai-note">
        <ListTree size={11} />
        <span>章节由已有字幕事件生成，不会伪装成独立分析结果。</span>
      </div>
    </div>
  );
}

export function AiDrawer({
  open,
  tab,
  clipId,
  onTranscriptReady,
  transcript,
  transcriptError,
  transcriptBusy,
  events,
  adopted,
  activeEventId,
  eventsBusy,
  eventError,
  advice,
  adviceBusy,
  adviceError,
  musicAssets,
  musicBusy,
  musicError,
  musicJob,
  project,
  fps,
  onTabChange,
  onClose,
  onLocateSource,
  onAdoptEvent,
  onApply,
  onExtract,
  onAutoPlan,
  onAnalyze,
  onRetryTranscript,
  onGenerateMusic,
  onRefreshMusic,
  onSelectMusic,
  onToggleDuckMusic,
  onMusicVolume,
}: AiDrawerProps) {
  const segmentCount = transcript?.segments.length ?? 0;
  const [transcribing, setTranscribing] = useState(false);
  const [transcriptionError, setTranscriptionError] = useState<string | null>(null);
  const transcriptionRequestRef = useRef<AbortController | null>(null);
  useEffect(() => {
    setTranscribing(false);
    setTranscriptionError(null);
    return () => {
      const request = transcriptionRequestRef.current;
      transcriptionRequestRef.current = null;
      request?.abort();
    };
  }, [clipId]);

  const startTranscription = async () => {
    if (!clipId || segmentCount > 0 || transcriptBusy || transcriptionRequestRef.current) return;
    const request = new AbortController();
    transcriptionRequestRef.current = request;
    setTranscribing(true);
    setTranscriptionError(null);
    const timeout = window.setTimeout(() => request.abort(new DOMException('timeout', 'TimeoutError')), 90_000);
    try {
      const queued = await transcriptionRequest<{ queued?: boolean }>(clipId, 'transcribe', request.signal);
      if (queued?.queued !== true) throw new Error('后端没有确认转写已排队，请稍后重试');
      while (!request.signal.aborted) {
        const data: EditorPayload = await transcriptionRequest<EditorPayload>(clipId, 'editor', request.signal);
        if (data?.clip?.id !== clipId) throw new Error('后端返回的视频不匹配，请重新选择视频后重试');
        if (data.transcript && Array.isArray(data.transcript.segments) && data.transcript.segments.length > 0) {
          request.signal.throwIfAborted();
          if (transcriptionRequestRef.current !== request) return;
          onTranscriptReady(clipId, data.transcript);
          return;
        }
        await waitForTranscriptPoll(request.signal);
      }
      request.signal.throwIfAborted();
    } catch (error) {
      if (transcriptionRequestRef.current !== request) return;
      setTranscriptionError(request.signal.reason?.name === 'TimeoutError'
        ? '转写还没完成，可以稍后在列表里刷新'
        : error instanceof TypeError
          ? '转写请求没发出去，检查后端是否在跑'
          : error instanceof Error ? error.message : '转写请求失败，请稍后重试');
    } finally {
      window.clearTimeout(timeout);
      if (transcriptionRequestRef.current === request) {
        transcriptionRequestRef.current = null;
        setTranscribing(false);
      }
    }
  };
  const transcriptionEntry = (
    <div className="ai-empty-actions" style={{ flexDirection: 'column', alignItems: 'center', gap: 7, color: 'var(--lc-text-secondary)' }}>
      <button type="button" className="primary-button" onClick={() => { void startTranscription(); }}
        disabled={!clipId || transcriptBusy || transcribing} aria-busy={transcribing}>
        {transcribing ? <LoaderCircle size={11} className="spin" /> : <ListTree size={11} />}
        {transcribing ? '转写中…' : '开始转写'}
      </button>
      {transcribing ? <p role="status">正在等待 Whisper 转写结果…</p> : null}
      {transcriptionError ? <p className="ai-error" role="alert" style={{ color: 'var(--lc-danger)' }}>{transcriptionError}</p> : null}
    </div>
  );
  const tabs = [
    ['candidates', '候选', events.length],
    ['transcript', '转写', segmentCount],
    ['chapters', '章节', events.length],
    ['music', '音乐', musicAssets.length],
  ] as const;

  return (
    <aside className={`ai-drawer${open ? ' is-open' : ''}`} aria-label="AI 助手" aria-hidden={!open} inert={!open}>
      <div className="ai-drawer-head">
        <div className="ai-drawer-title">
          <span className="ai-brand-dot">
            <Sparkles size={12} />
          </span>
          <strong>AI 助手</strong>
          <span className="ai-version">{transcript ? '真实字幕' : '等待转写'}</span>
        </div>
        <div className="ai-drawer-head-actions">
          <kbd>Esc</kbd>
          <button type="button" className="icon-button" onClick={onClose} aria-label="收起 AI 助手">
            <X size={12} />
          </button>
        </div>
      </div>

      <div className="ai-tabs" role="tablist" aria-label="AI 视图">
        {tabs.map(([id, label, count]) => (
          <button
            key={id}
            type="button"
            role="tab"
            className={`ai-tab${tab === id ? ' is-active' : ''}`}
            onClick={() => onTabChange(id)}
            aria-selected={tab === id}
          >
            {label}
            <span>{count}</span>
          </button>
        ))}
      </div>

      {tab === 'candidates' ? (
        <CandidateView
          events={events}
          adopted={adopted}
          activeEventId={activeEventId}
          eventsBusy={eventsBusy}
          eventError={eventError}
          advice={advice}
          adviceBusy={adviceBusy}
          adviceError={adviceError}
          hasTranscript={segmentCount > 0}
          transcriptionEntry={transcriptionEntry}
          fps={fps}
          onLocate={(event) => onLocateSource(event.start)}
          onAdopt={onAdoptEvent}
          onExtract={onExtract}
          onAutoPlan={onAutoPlan}
          onAnalyze={onAnalyze}
        />
      ) : null}
      {tab === 'transcript' ? (
        <TranscriptView
          transcript={transcript}
          error={transcriptError}
          busy={transcriptBusy}
          transcribing={transcribing}
          transcriptionEntry={transcriptionEntry}
          onRetry={onRetryTranscript}
          onLocate={(segment) => onLocateSource(segment.start)}
        />
      ) : null}
      {tab === 'chapters' ? (
        <ChapterView events={events} onLocate={(event) => onLocateSource(event.start)} />
      ) : null}
      {tab === 'music' ? (
        <MusicPanel
          project={project}
          options={musicAssets}
          busy={musicBusy}
          error={musicError}
          job={musicJob}
          onGenerate={onGenerateMusic}
          onRefresh={onRefreshMusic}
          onSelect={onSelectMusic}
          onToggleDuck={onToggleDuckMusic}
          onVolume={onMusicVolume}
        />
      ) : null}

      <div className="ai-footer">
        {tab === 'candidates' ? (
          <>
            <span className="ai-footer-note">
              已采用 <b>{adopted.size}</b> / {events.length} · 不会直接覆盖工程
            </span>
            <button
              type="button"
              className="primary-button ai-apply"
              onClick={onApply}
              disabled={adopted.size === 0}
            >
              整理为时间线
            </button>
          </>
        ) : null}
        {tab === 'transcript' ? (
          <>
            <span className="ai-footer-note">
              {segmentCount} 段 · {transcript?.language || '未知语言'}
            </span>
            <button
              type="button"
              className="primary-button ai-apply"
              onClick={() => {
                if (transcript) downloadTranscript(transcript, 'subtitle');
              }}
              disabled={!transcript || segmentCount === 0}
            >
              <Download size={11} /> 导出字幕
            </button>
          </>
        ) : null}
        {tab === 'chapters' ? (
          <>
            <span className="ai-footer-note">{events.length} 个事件 · 来自字幕</span>
          </>
        ) : null}
        {tab === 'music' ? (
          <>
            <span className="ai-footer-note">共 {musicAssets.length} 段可选配乐 · 工程级 BGM</span>
          </>
        ) : null}
      </div>
    </aside>
  );
}
