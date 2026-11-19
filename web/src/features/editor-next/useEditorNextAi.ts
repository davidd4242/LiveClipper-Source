import { useEffect, useRef, useState } from 'react';
import { applyAutoPlanProposal, generateAutoPlanProposal } from './aiApi';
import type { AutoPlanProposal } from './aiApi';

export type EditorNextAiStatus = 'idle' | 'generating' | 'ready' | 'flushing' | 'applying' | 'stale' | 'error' | 'applied';
export interface EditorNextAiState {
  status: EditorNextAiStatus;
  proposal: AutoPlanProposal | null;
  message: string;
  code: string | null;
}
export type EditorNextAiEvent =
  | { type: 'generate' }
  | { type: 'generated'; proposal: AutoPlanProposal }
  | { type: 'apply' }
  | { type: 'save-blocked'; message?: string }
  | { type: 'flushed' }
  | { type: 'applied'; revision: number }
  | { type: 'failed'; status?: number; code?: string; message: string };

export const initialAiState = (): EditorNextAiState => ({ status: 'idle', proposal: null, message: '生成建议只预览，不会修改工程。', code: null });
export const aiIsBusy = (state: EditorNextAiState) => ['generating', 'flushing', 'applying'].includes(state.status);

/**
 * 409 的真实原因要读结构化的 error.code（aiApi.ts:81 已优先返回 error.code）：
 * - session-busy / session-mismatch：会话被占用或身份不匹配，不是「建议过期」；
 * - stale-*（如 stale-revision）：建议基线过期；
 * - 其它 409：工程状态已变化，仍按「需要重新生成」处理，文案保持中性。
 * 只做分类与文案，不新增活动会话查询、不改服务端、不加请求头。
 */
export type AiConflictKind = 'none' | 'session' | 'stale' | 'changed';
const AI_SESSION_CONFLICT_CODES = new Set(['session-busy', 'session-mismatch']);
export function classifyAiConflict(status?: number, code?: string | null): AiConflictKind {
  if (status !== 409) return 'none';
  const normalized = (code ?? '').trim().toLowerCase();
  if (AI_SESSION_CONFLICT_CODES.has(normalized)) return 'session';
  if (normalized.startsWith('stale')) return 'stale';
  return 'changed';
}

export function editorNextAiReducer(state: EditorNextAiState, event: EditorNextAiEvent): EditorNextAiState {
  switch (event.type) {
    case 'generate':
      return aiIsBusy(state) || state.status === 'applied' ? state
        : { status: 'generating', proposal: null, message: '正在读取已保存的工程并生成建议…', code: null };
    case 'generated':
      return state.status === 'generating' ? { status: 'ready', proposal: event.proposal, message: '建议已生成，尚未应用。请先审阅改动与理由。', code: null } : state;
    case 'apply':
      return state.status === 'ready' && state.proposal?.timeline.length
        ? { ...state, status: 'flushing', message: '正在保存待写入的修改；保存成功后才会应用建议。' } : state;
    case 'save-blocked':
      return state.status === 'flushing' ? { ...state, status: 'ready', code: 'save-blocked',
        message: `工程尚未保存成功，未发送应用请求。${event.message ?? ''} 请先处理保存状态，再重试应用此建议；不会自动重试。` } : state;
    case 'flushed':
      return state.status === 'flushing' ? { ...state, status: 'applying', message: '正在校验建议基线并应用…' } : state;
    case 'applied':
      return state.status === 'applying' ? { status: 'applied', proposal: null, code: null, message: `建议已应用（修订 ${event.revision}），正在重新载入工程…` } : state;
    case 'failed': {
      if (!aiIsBusy(state)) return state;
      const code = event.code ?? null;
      const conflict = classifyAiConflict(event.status, code);
      if (conflict === 'session') return { ...state, status: 'error', code,
        message: `这个工程正被另一个编辑会话占用或身份不匹配，建议未生成或未应用。${event.message} 关闭其它编辑会话后重新生成；不会自动重试或覆盖。` };
      if (conflict === 'stale') return { ...state, status: 'stale', code,
        message: `建议已过期：工程在生成建议之后已经变化。${event.message} 请重新生成；不会自动重试或覆盖。` };
      if (conflict === 'changed') return { ...state, status: 'stale', code,
        message: `工程状态已变化，建议没有应用。${event.message} 请重新生成；不会自动重试或覆盖。` };
      return { ...state, status: 'error', code, message: event.message };
    }
  }
}

export interface AiSaveSession {
  flush: () => Promise<boolean>;
  readState: () => { hasUnsaved: boolean; conflict: boolean; saving?: boolean; error?: string };
}
interface AiControllerOptions {
  clipId: string;
  save: AiSaveSession;
  reload: () => void;
  onState?: (state: EditorNextAiState) => void;
  api?: { generate: typeof generateAutoPlanProposal; apply: typeof applyAutoPlanProposal };
}

export function createEditorNextAiController(options: AiControllerOptions) {
  let state = initialAiState();
  let disposed = false;
  let pending: AbortController | null = null;
  const api = options.api ?? { generate: generateAutoPlanProposal, apply: applyAutoPlanProposal };
  const send = (event: EditorNextAiEvent) => {
    if (disposed) return;
    state = editorNextAiReducer(state, event);
    options.onState?.(state);
  };
  const current = (request: AbortController) => !disposed && pending === request;
  const fail = (cause: unknown) => {
    const detail = cause as { status?: number; code?: string; message?: string } | null;
    send({ type: 'failed', status: detail?.status, code: detail?.code,
      message: detail?.message || '请求失败，请检查连接后重新生成建议。' });
  };
  return {
    getState: () => state,
    async generate(): Promise<boolean> {
      if (disposed || aiIsBusy(state) || state.status === 'applied') return false;
      send({ type: 'generate' });
      const request = new AbortController(); pending = request;
      try {
        const proposal = await api.generate(options.clipId, request.signal);
        if (!current(request)) return false;
        if (proposal.clipId !== options.clipId) throw new Error('建议不属于当前工程，请重新生成。');
        send({ type: 'generated', proposal });
        return true;
      } catch (cause) { if (current(request)) fail(cause); return false; }
      finally { if (pending === request) pending = null; }
    },
    async apply(): Promise<boolean> {
      if (disposed || state.status !== 'ready' || !state.proposal?.timeline.length) return false;
      const proposal = state.proposal;
      send({ type: 'apply' });
      const request = new AbortController(); pending = request;
      try {
        const clean = await options.save.flush();
        if (!current(request)) return false;
        const saved = options.save.readState();
        if (!clean || saved.hasUnsaved || saved.conflict || saved.saving || saved.error) {
          send({ type: 'save-blocked', message: saved.error });
          return false;
        }
        send({ type: 'flushed' });
        const result = await api.apply(options.clipId, proposal, request.signal);
        if (!current(request)) return false;
        send({ type: 'applied', revision: result.revision });
        options.reload();
        return true;
      } catch (cause) { if (current(request)) fail(cause); return false; }
      finally { if (pending === request) pending = null; }
    },
    dispose() { disposed = true; pending?.abort(); pending = null; },
  };
}

export function useEditorNextAi(options: {
  clipId: string | null;
  identity: object | null;
  ready: boolean;
  getSaveSession: () => AiSaveSession | null;
  reload: () => void;
}) {
  const active = useRef<{ identity: object; controller: ReturnType<typeof createEditorNextAiController> } | null>(null);
  const latest = useRef(options); latest.current = options;
  const [state, setState] = useState(initialAiState);
  useEffect(() => {
    setState(initialAiState());
    if (!options.ready || !options.clipId || !options.identity) return;
    const save = latest.current.getSaveSession();
    if (!save) { setState({ ...initialAiState(), status: 'error', message: '保存会话尚未就绪，请重新载入工程。' }); return; }
    const identity = options.identity;
    const controller = createEditorNextAiController({ clipId: options.clipId, save,
      reload: () => { if (latest.current.identity === identity && latest.current.ready) latest.current.reload(); },
      onState: next => { if (latest.current.identity === identity && latest.current.ready) setState(next); } });
    active.current = { identity, controller };
    return () => { controller.dispose(); if (active.current?.controller === controller) active.current = null; };
  }, [options.clipId, options.identity, options.ready]);
  const controller = () => options.ready && active.current?.identity === options.identity ? active.current.controller : null;
  return {
    state,
    generate: () => controller()?.generate() ?? Promise.resolve(false),
    apply: () => controller()?.apply() ?? Promise.resolve(false),
    invalidate: () => { active.current?.controller.dispose(); active.current = null; setState(initialAiState()); },
  };
}
