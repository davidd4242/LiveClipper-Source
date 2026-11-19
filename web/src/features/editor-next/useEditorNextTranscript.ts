import { useCallback, useEffect, useRef, useState } from 'react';
import { getJobs } from '../../api/client';
import type { JobDto, TranscriptPreview, TranscriptSegment } from '../../api/types';
import type { ExportSaveSession } from './exportBridge';
import {
  getClipTranscript,
  getTranscriptState,
  isActiveTranscriptionJob,
  isActiveTranscriptionStatus,
  isTranscribingConflict,
  isTranscriptionJob,
  jobProgressPercent,
  pickNewTranscriptionJob,
  pickTranscriptionJob,
  restartTranscription,
  startTranscription,
  saveClipCaptions,
  transcriptSrtUrl,
  TRANSCRIBING_CONFLICT_TEXT,
} from './editorNextTranscriptApi';

/**
 * R82 第一批：右侧栏「转写字幕」面板的状态机。
 *
 * 契约要点（与 RAIL-FIRST-CHECK/CORRECTIONS.md 对齐）：
 * - POST 回执须匹配 /api/jobs 同 eventId/type 的真实行；旧后端用发前后 id 差集。
 *   找不到就等待并禁止重复提交，不猜 completed、不设超时判失败。
 * - 归属只看 eventId（transcribe job 没有 clipId），任务按服务端返回顺序取首条。
 * - 未知 status 原样展示，retried 不当作 running，不用 transcriptVersion 当成功信号。
 * - 身份隔离：clipId + eventId + epoch 变化即中止旧请求、清空状态，旧响应不会串到新工程。
 * - 轮询 3s，页面隐藏时暂停，重新可见时立即补一次，卸载/切换工程时清理。
 * - 字幕版本变化只提示「重新载入后才会应用」，不静默重载工程、不改 AI 建议。
 */

export const TRANSCRIPT_POLL_MS = 3000;
/** 关联不上新任务时，经过多少个轮询周期后换成「可手动刷新」的提示（仍然不判失败）。 */
const AWAITING_HINT_TICKS = 10;

export type TranscriptRequestPhase = 'idle' | 'starting' | 'accepted' | 'linked' | 'error';

export interface TranscriptTaskView {
  id: string;
  /** 服务端原样状态（pending/running/completed/failed/interrupted/retried/未知值）。 */
  status: string;
  /** 只有 running 且有真实 progress 时才是百分比，否则 null。 */
  progress: number | null;
  error: string | null;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface EditorNextTranscriptState {
  status: 'idle' | 'loading' | 'ready' | 'error';
  message: string;
  transcript: TranscriptPreview | null;
  transcriptVersion: string | null;
  transcriptSource: string | null;
  task: TranscriptTaskView | null;
  request: { phase: TranscriptRequestPhase; message: string; code: string | null };
  /** POST 已接受但尚未看到对应真实任务：与 submit 的同步 ref 锁对应。 */
  waiting: boolean;
  /** 面板打开后字幕版本变过：只提示，等用户显式重新载入。 */
  updated: boolean;
  checkedAt: number | null;
}

export function initialTranscriptState(): EditorNextTranscriptState {
  return {
    status: 'idle',
    message: '打开面板后读取当前视频的字幕。',
    transcript: null,
    transcriptVersion: null,
    transcriptSource: null,
    task: null,
    request: { phase: 'idle', message: '', code: null },
    waiting: false,
    updated: false,
    checkedAt: null,
  };
}

export interface EditorNextTranscriptApi {
  state: typeof getTranscriptState;
  transcript: typeof getClipTranscript;
  start: typeof startTranscription;
  restart: typeof restartTranscription;
  jobs: typeof getJobs;
}

const defaultApi: EditorNextTranscriptApi = {
  state: getTranscriptState,
  transcript: getClipTranscript,
  start: startTranscription,
  restart: restartTranscription,
  jobs: getJobs,
};

function toTaskView(job: JobDto | null): TranscriptTaskView | null {
  if (!job) return null;
  return {
    id: job.id,
    status: job.status,
    progress: jobProgressPercent(job),
    error: job.error ?? null,
    startedAt: job.startedAt ?? null,
    finishedAt: job.finishedAt ?? null,
  };
}

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export interface EditorNextTranscriptOptions {
  clipId: string | null;
  /** payload.clip.eventId；为空时面板不展示任何任务、不启用重启。 */
  eventId: string | null;
  /** EditorPayload.epoch（可能没有）；与服务端 epoch 不一致时旧响应自动失效。 */
  epoch: string | null;
  /** editingReady：clip 就绪且没有待处理草稿，才允许发起/重启转写。 */
  ready: boolean;
  getSaveSession?: () => ExportSaveSession | null;
  reload?: () => void;
  api?: EditorNextTranscriptApi;
}

export function useEditorNextTranscript(options: EditorNextTranscriptOptions) {
  const { clipId, eventId, epoch, ready } = options;
  const identity = JSON.stringify([clipId, eventId, epoch]);
  const [state, setState] = useState<EditorNextTranscriptState>(initialTranscriptState);
  const latest = useRef(options);
  latest.current = options;
  const apiRef = useRef<EditorNextTranscriptApi>(options.api ?? defaultApi);
  if (options.api) apiRef.current = options.api;
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const sequence = useRef(0);
  const abort = useRef<AbortController | null>(null);
  const operation = useRef<'load' | 'submit' | 'save' | null>(null);
  const [saveState, setSaveState] = useState<{ busy: boolean; error: string }>({ busy: false, error: '' });
  const mounted = useRef(false);
  const awaiting = useRef<{ eventId: string; before: Set<string>; ticks: number; jobId: string | null; reused: boolean } | null>(null);
  const baselineVersion = useRef<string | null | undefined>(undefined);

  const load = useCallback(async (mode: 'initial' | 'poll' | 'manual') => {
    const clip = clipId;
    const event = eventId;
    if (!clip || !mounted.current || identityRef.current !== identity || abort.current) return;
    operation.current = 'load';
    const seq = ++sequence.current;
    const controller = new AbortController();
    abort.current = controller;
    const fresh = () => mounted.current && identityRef.current === identity && sequence.current === seq
      && abort.current === controller && !controller.signal.aborted;
    if (mode === 'initial') {
      setState(previous => ({ ...previous, status: 'loading', message: '正在读取字幕与任务状态…' }));
    }
    try {
      const [jobs, snapshot, transcript] = await Promise.all([
        apiRef.current.jobs(),
        apiRef.current.state(clip, controller.signal),
        apiRef.current.transcript(clip, controller.signal),
      ]);
      if (!fresh()) return;
      const currentJob = event ? pickTranscriptionJob(jobs, event) : null;
      let linked: JobDto | null = null;
      let hint = '';
      const waiting = awaiting.current;
      if (waiting && event && waiting.eventId === event) {
        linked = waiting.jobId
          ? jobs.find(job => job.id === waiting.jobId && isTranscriptionJob(job, event)) ?? null
          : pickNewTranscriptionJob(jobs, event, waiting.before);
        if (linked) {
          awaiting.current = null;
        } else {
          waiting.ticks += 1;
          if (waiting.ticks >= AWAITING_HINT_TICKS) {
            hint = '请求已接受，暂时还没有看到对应的转写任务。可手动刷新，或稍后再看。';
          }
        }
      }
      const version = snapshot.transcriptVersion ?? null;
      const changed = baselineVersion.current !== undefined && version !== baselineVersion.current;
      if (baselineVersion.current === undefined) baselineVersion.current = version;
      setState(previous => ({
        ...previous,
        status: 'ready',
        message: transcript ? '' : '当前视频还没有字幕。',
        transcript,
        transcriptVersion: snapshot.transcriptVersion ?? null,
        transcriptSource: snapshot.transcriptSource ?? null,
        task: toTaskView(waiting ? linked : currentJob),
        waiting: awaiting.current !== null,
        updated: previous.updated || changed,
        checkedAt: Date.now(),
        request: linked
          ? { phase: 'linked', message: waiting?.reused ? '沿用已排队的转写任务' : '已关联到新的转写任务。', code: null }
          : hint ? { ...previous.request, phase: 'accepted', message: hint } : previous.request,
      }));
    } catch (error) {
      if ((error as { name?: string } | null)?.name === 'AbortError') return;
      if (!fresh()) return;
      const wasWaiting = awaiting.current !== null;
      awaiting.current = null;
      setState(previous => ({
        ...previous,
        status: 'error',
        message: errorText(error),
        waiting: false,
        request: wasWaiting ? { phase: 'error', message: errorText(error), code: null } : previous.request,
      }));
    } finally {
      // An aborted predecessor cannot release its successor's ownership.
      if (abort.current === controller) { abort.current = null; operation.current = null; }
    }
  }, [clipId, eventId, identity]);

  useEffect(() => {
    mounted.current = true;
    baselineVersion.current = undefined;
    awaiting.current = null;
    sequence.current += 1;
    abort.current?.abort();
    abort.current = null;
    operation.current = null;
    setState(initialTranscriptState());
    setSaveState({ busy: false, error: '' });
    if (clipId) void load('initial');
    return () => {
      mounted.current = false;
      sequence.current += 1;
      abort.current?.abort();
      abort.current = null;
      operation.current = null;
    };
  }, [clipId, identity, load]);

  /* 轮询：3s 一次；隐藏时暂停，重新可见时立即补一次。 */
  useEffect(() => {
    if (!clipId) return undefined;
    if (typeof document === 'undefined' || typeof window === 'undefined') return undefined;
    let timer: number | null = null;
    const tick = () => {
      if (document.visibilityState === 'hidden') return;
      void load('poll');
    };
    const start = () => {
      if (timer === null) timer = window.setInterval(tick, TRANSCRIPT_POLL_MS);
    };
    const stop = () => {
      if (timer !== null) {
        window.clearInterval(timer);
        timer = null;
      }
    };
    const onVisibility = () => {
      if (document.visibilityState === 'visible') {
        tick();
        start();
      } else {
        stop();
      }
    };
    if (document.visibilityState !== 'hidden') start();
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      stop();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [clipId, load]);

  const submit = useCallback(async (kind: 'start' | 'restart') => {
    const clip = clipId;
    const event = eventId;
    if (!clip || !event || !latest.current.ready || !mounted.current || identityRef.current !== identity) return;
    // Submit may replace a read, never another submit (including rapid double clicks).
    if (operation.current === 'submit' || operation.current === 'save' || awaiting.current) return;
    abort.current?.abort();
    const seq = ++sequence.current;
    const controller = new AbortController();
    abort.current = controller;
    operation.current = 'submit';
    const fresh = () => mounted.current && identityRef.current === identity && sequence.current === seq
      && abort.current === controller && !controller.signal.aborted;
    awaiting.current = null;
    setState(previous => ({
      ...previous,
      request: { phase: 'starting', message: kind === 'start' ? '正在发送转写请求…' : '正在重启转写…', code: null },
    }));
    try {
      /* 保留发前 id 集合兼容无回执的旧后端，不把旧 completed 当本次成功。 */
      const before = new Set((await apiRef.current.jobs()).map(job => job.id));
      if (!fresh()) return;
      if (!latest.current.ready) {
        setState(previous => ({ ...previous, request: { phase: 'error', message: '编辑器尚未就绪，未发送转写请求。', code: null } }));
        return;
      }
      const receipt = kind === 'start'
        ? await apiRef.current.start(clip, controller.signal)
        : await apiRef.current.restart(event, controller.signal);
      if (!fresh()) return;
      const jobId = typeof receipt?.jobId === 'string' && receipt.jobId.trim() ? receipt.jobId : null;
      awaiting.current = { eventId: event, before, ticks: 0, jobId, reused: !!jobId && receipt.disposition === 'already-pending' };
      setState(previous => ({ ...previous, task: null, waiting: true, request: { phase: 'accepted', message: '请求已接受，等待任务状态。', code: null } }));
      abort.current = null;
      operation.current = null;
      void load('manual');
    } catch (error) {
      if ((error as { name?: string } | null)?.name === 'AbortError' || !fresh()) return;
      /* 重复触发：只有「精确的错误文本 + 重新查 jobs 确证 pending/running」才提示已在转写。 */
      if (kind === 'start' && event && isTranscribingConflict(error)) {
        let confirmed = false;
        try {
          const jobs = await apiRef.current.jobs();
          if (!fresh()) return;
          const active = pickTranscriptionJob(jobs, event);
          confirmed = isActiveTranscriptionJob(active);
          if (confirmed) {
            setState(previous => ({
              ...previous,
              status: 'ready',
              task: toTaskView(active),
              checkedAt: Date.now(),
              request: { phase: 'accepted', message: TRANSCRIBING_CONFLICT_TEXT, code: null },
            }));
          }
        } catch {
          confirmed = false;
        }
        if (confirmed) return;
        if (!fresh()) return;
      }
      setState(previous => ({
        ...previous,
        request: {
          phase: 'error',
          message: errorText(error),
          code: error instanceof Error && 'code' in error ? ((error as { code?: string | null }).code ?? null) : null,
        },
      }));
    } finally {
      // An aborted predecessor cannot release its successor's ownership.
      if (abort.current === controller) { abort.current = null; operation.current = null; }
    }
  }, [clipId, eventId, identity, load]);

  const saveCaptions = useCallback(async (segments: readonly TranscriptSegment[] | null): Promise<boolean> => {
    if (!clipId || !latest.current.reload || !latest.current.ready || !mounted.current || identityRef.current !== identity
      || operation.current === 'submit' || operation.current === 'save' || awaiting.current
      || isActiveTranscriptionStatus(state.task?.status)) return false;
    abort.current?.abort();
    const controller = new AbortController();
    const seq = ++sequence.current;
    abort.current = controller;
    operation.current = 'save';
    const fresh = () => mounted.current && identityRef.current === identity && sequence.current === seq
      && abort.current === controller && !controller.signal.aborted;
    setSaveState({ busy: true, error: '' });
    try {
      const session = latest.current.getSaveSession?.();
      if (!session || !await session.flush()) throw new Error('请先保存工程并处理保存错误，再保存字幕。');
      if (!fresh() || !latest.current.ready) return false;
      const snapshot = session.snapshot();
      if (snapshot.clipId !== clipId || snapshot.epoch !== epoch) throw new Error('工程身份已变化，请保留编辑内容并重新载入。');
      await saveClipCaptions(snapshot, segments, controller.signal);
      if (!fresh()) return false;
      // 新工程修订不交给旧保存队列继续写入：确认后重新 GET 载入。
      latest.current.reload?.();
      return true;
    } catch (error) {
      if (fresh()) setSaveState({ busy: false, error: errorText(error) });
      return false;
    } finally {
      if (fresh()) setSaveState(previous => ({ ...previous, busy: false }));
      if (abort.current === controller) { abort.current = null; operation.current = null; }
    }
  }, [clipId, epoch, identity, state.task?.status]);

  const refresh = useCallback(() => {
    void load('manual');
  }, [load]);

  const acknowledgeUpdate = useCallback(() => {
    if (!mounted.current || identityRef.current !== identity) return;
    baselineVersion.current = state.transcriptVersion;
    setState(previous => ({ ...previous, updated: false }));
  }, [identity, state.transcriptVersion]);

  return {
    state,
    saveState,
    saveCaptions,
    srtUrl: clipId ? transcriptSrtUrl(clipId) : null,
    canEdit: ready && !!clipId && !!latest.current.getSaveSession && !!latest.current.reload && !saveState.busy
      && state.status === 'ready' && state.request.phase !== 'starting' && !state.waiting
      && !isActiveTranscriptionStatus(state.task?.status),
    /** 面板/按钮是否可用（editingReady）。 */
    ready,
    /** 有真实 eventId 才允许重启，不臆断 LocalVideo 一定有/没有。 */
    canRestart: ready && !saveState.busy && !!clipId && !!eventId && state.request.phase !== 'starting' && !state.waiting,
    canStart: ready && !saveState.busy && !!clipId && !!eventId && state.request.phase !== 'starting' && !state.waiting
      && !isActiveTranscriptionStatus(state.task?.status),
    start: () => submit('start'),
    restart: () => submit('restart'),
    refresh,
    acknowledgeUpdate,
  };
}
