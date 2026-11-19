import { useEffect, useRef, useState } from 'react';
import type { MusicGenerateInput, MusicJobState, MusicLibraryAsset, MusicStatus } from '../../api/music';
import { editorNextMusicApi, musicGenerationBlock } from './editorNextMusicApi';
import type { MusicApi } from './editorNextMusicApi';

export interface GlobalMusic { musicAssetId: string | null; musicVolume: number; duckMusic: boolean }
export type MusicContext = Readonly<{ clipId: string; epoch: string; generation: number }>;
export interface MusicHostState { context: MusicContext; mix: GlobalMusic; engineGeneration: number; engineRevision: number }
export interface MusicHostPort {
  read: () => MusicHostState | null;
  blockReason: () => string | null;
  setGlobalMusic: (patch: Partial<GlobalMusic>, expected: { generation: number; engineRevision: number }, signal: AbortSignal) => Promise<{ ok: boolean; error?: string }> | { ok: boolean; error?: string };
  flushSave: (expected: MusicContext) => Promise<boolean>;
}
export interface NativeMusicBridge {
  getGlobalMusic?: () => { ok: boolean; error?: string; mix?: GlobalMusic; generation?: number; engineRevision?: number };
  setGlobalMusic?: MusicHostPort['setGlobalMusic'];
}
/** Bind the native transaction to a captured save session; never flush a later project's mutable ref. */
export function createNativeMusicHost(options: {
  context: MusicContext; getBridge: () => NativeMusicBridge | null; isCurrent: () => boolean;
  blockReason: () => string | null; getSaveSession: () => { flush: () => Promise<boolean> } | null;
}): MusicHostPort {
  let pending: { context: MusicContext; flush: () => Promise<boolean> } | null = null;
  const read = (): MusicHostState | null => {
    if (!options.isCurrent()) return null;
    const result = options.getBridge()?.getGlobalMusic?.();
    return result?.ok && result.mix && result.generation === options.context.generation && Number.isSafeInteger(result.engineRevision) ?
      { context: options.context, mix: result.mix, engineGeneration: result.generation, engineRevision: result.engineRevision! } : null;
  };
  return { read, blockReason: () => options.blockReason() || (!options.getBridge()?.setGlobalMusic || !read() ? '缺少或尚未就绪的 FCBridge.setGlobalMusic 原生事务。' : null),
    async setGlobalMusic(patch, expected, signal) {
      const current = read(), bridge = options.getBridge(), session = options.getSaveSession();
      if (!current || current.engineGeneration !== expected.generation || current.engineRevision !== expected.engineRevision ||
        !bridge?.setGlobalMusic || !session || signal.aborted || options.blockReason()) return { ok: false, error: '当前工程或原生配乐修订已变化，未应用。' };
      pending = { context: { ...options.context }, flush: session.flush };
      const result = await bridge.setGlobalMusic(patch, expected, signal);
      if (!result.ok || !read() || signal.aborted) { pending = null; return { ok: false, error: result.error || '配乐事务所属工程已变化。' }; }
      return result;
    },
    async flushSave(expected) {
      const captured = pending; pending = null;
      if (!captured || !read() || !sameContext(captured.context, expected)) return false;
      const saved = await captured.flush();
      return !!saved && !!read() && sameContext(options.context, expected);
    },
  };
}

export interface EditorNextMusicState {
  status: 'idle' | 'loading' | 'ready' | 'error';
  assets: MusicLibraryAsset[];
  modelStatus: MusicStatus | null;
  message: string;
  job: MusicJobState | null;
  generating: boolean;
  candidateId: string | null;
  preview: { assetId: string; url: string } | null;
  action: { phase: 'idle' | 'checking' | 'saving' | 'saved' | 'conflict' | 'error'; message: string };
}
const initialState = (): EditorNextMusicState => ({ status: 'idle', assets: [], modelStatus: null, message: '', job: null,
  generating: false, candidateId: null, preview: null, action: { phase: 'idle', message: '' } });
const messageOf = (error: unknown) => error instanceof Error ? error.message : String(error);
const sameContext = (a: MusicContext, b: MusicContext) => a.clipId === b.clipId && a.epoch === b.epoch && a.generation === b.generation;

function pause(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(new DOMException('已取消', 'AbortError')); return; }
    const abort = () => { clearTimeout(timer); reject(new DOMException('已取消', 'AbortError')); };
    const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve(); }, ms);
    signal.addEventListener('abort', abort, { once: true });
  });
}

/** Owns only library/task UI. All document writes go through the one native history and host save queue. */
export function createEditorNextMusicController(host: MusicHostPort, api: MusicApi = editorNextMusicApi) {
  let state = initialState(), alive = true, serial = 0;
  const listeners = new Set<(state: EditorNextMusicState) => void>();
  const work = new Map<string, { serial: number; abort: AbortController; context: MusicContext }>();
  const emit = (patch: Partial<EditorNextMusicState>) => { if (!alive) return; state = { ...state, ...patch }; listeners.forEach(fn => fn(state)); };
  const current = (op: ReturnType<typeof begin>) => !!op && alive && !op.abort.signal.aborted && work.get(op.kind)?.serial === op.serial &&
    !!host.read() && sameContext(op.context, host.read()!.context);
  function begin(kind: string) {
    const baseline = host.read();
    if (!baseline || !alive) return null;
    work.get(kind)?.abort.abort();
    const operation = { serial: ++serial, abort: new AbortController(), context: { ...baseline.context }, kind };
    work.set(kind, operation); return operation;
  }
  function finish(op: NonNullable<ReturnType<typeof begin>>) { if (work.get(op.kind)?.serial === op.serial) work.delete(op.kind); }
  async function refresh() {
    const op = begin('refresh'); if (!op) return;
    emit({ status: 'loading', message: '' });
    try {
      const [assets, modelStatus] = await Promise.all([api.getMusicAssets(op.abort.signal), api.getMusicStatus(op.abort.signal)]);
      if (current(op)) emit({ assets: assets.filter(a => a.kind === 'music'), modelStatus, status: 'ready' });
    } catch (error) { if (current(op)) emit({ status: 'error', message: messageOf(error) }); }
    finally { finish(op); }
  }
  async function preview(assetId: string) {
    const op = begin('preview'); if (!op) return;
    try {
      if (!state.assets.some(a => a.id === assetId && a.kind === 'music')) throw new Error('素材不在当前音乐库中。');
      const url = await api.checkAudioFile(assetId, op.abort.signal);
      if (current(op)) emit({ preview: { assetId, url }, message: '' });
    } catch (error) { if (current(op)) emit({ preview: null, message: messageOf(error) }); }
    finally { finish(op); }
  }
  async function apply(patch: Partial<GlobalMusic>): Promise<boolean> {
    if ([...work.keys()].includes('apply')) return false;
    const blocked = host.blockReason();
    if (blocked) { emit({ action: { phase: 'error', message: blocked } }); return false; }
    const baseline = host.read(), op = begin('apply'); if (!baseline || !op) return false;
    emit({ action: { phase: 'checking', message: '正在检查配乐。' } });
    try {
      if (Object.keys(patch).some(key => !['musicAssetId', 'musicVolume', 'duckMusic'].includes(key))) throw new Error('不支持的配乐字段。');
      if ('musicAssetId' in patch && patch.musicAssetId !== null) {
        if (!state.assets.some(a => a.id === patch.musicAssetId && a.kind === 'music')) throw new Error('素材不在当前音乐库中，未应用配乐。');
        await api.checkAudioFile(patch.musicAssetId!, op.abort.signal);
      }
      if ('musicVolume' in patch && (typeof patch.musicVolume !== 'number' || !Number.isFinite(patch.musicVolume) || patch.musicVolume < 0 || patch.musicVolume > 1)) throw new Error('配乐音量必须在 0–1 之间。');
      if ('duckMusic' in patch && typeof patch.duckMusic !== 'boolean') throw new Error('压低配乐状态无效。');
      if (!current(op)) return false;
      const result = await host.setGlobalMusic(patch, { generation: baseline.engineGeneration, engineRevision: baseline.engineRevision }, op.abort.signal);
      if (!result.ok) throw new Error(result.error || '原生配乐事务失败。');
      if (!current(op)) return false;
      emit({ action: { phase: 'saving', message: '预览已更新，正在通过当前工程保存队列保存。' } });
      const saved = await host.flushSave(op.context);
      if (!current(op)) return false;
      emit({ action: saved ? { phase: 'saved', message: '配乐改动已由当前工程保存队列确认。' } :
        { phase: 'conflict', message: '配乐已在本地历史中更新，但保存未确认；请在保存栏处理冲突或错误。' } });
      return saved;
    } catch (error) { if (current(op)) emit({ action: { phase: 'error', message: messageOf(error) } }); return false; }
    finally { finish(op); }
  }
  async function generate(input: MusicGenerateInput): Promise<void> {
    if (state.generating) return;
    const block = musicGenerationBlock(state.modelStatus, input);
    if (block) { emit({ message: block }); return; }
    const op = begin('generate'); if (!op) return;
    emit({ generating: true, job: null, candidateId: null, message: '' });
    try {
      let job = await api.startMusicGeneration({ ...input }, op.abort.signal);
      const deadline = Date.now() + 180000;
      while (current(op)) {
        emit({ job });
        if (job.status === 'completed') {
          if (!job.assetId) throw new Error('生成任务已完成，但没有返回音频素材标识。');
          const assets = await api.getMusicAssets(op.abort.signal);
          if (current(op)) emit({ assets: assets.filter(a => a.kind === 'music'), candidateId: job.assetId,
            message: assets.some(a => a.id === job.assetId && a.kind === 'music') ? '生成完成，已刷新音乐库。请试听并明确应用；当前工程未自动修改。' :
              '生成完成，但返回素材尚未出现在音乐库中；当前工程未修改，请刷新检查。' });
          break;
        }
        if (job.status === 'failed' || job.status === 'cancelled') throw new Error(job.error || job.detail || `生成任务状态：${job.status}`);
        if (!['queued', 'running'].includes(job.status)) throw new Error(`未知音乐任务状态：${job.status}，停止轮询。`);
        if (Date.now() >= deadline) throw new Error('等待音乐任务超时；任务可能仍在后端运行，请刷新检查。');
        await pause(1200, op.abort.signal);
        if (!current(op)) break;
        job = await api.getMusicJob(job.id, op.abort.signal);
      }
    } catch (error) { if (current(op)) emit({ message: messageOf(error) }); }
    finally { if (current(op)) emit({ generating: false }); finish(op); }
  }
  return {
    getState: () => state, subscribe(fn: (state: EditorNextMusicState) => void) { listeners.add(fn); return () => { listeners.delete(fn); }; },
    refresh, preview, apply, generate,
    stopPreview() { work.get('preview')?.abort.abort(); work.delete('preview'); emit({ preview: null }); },
    previewFailed() { emit({ preview: null, message: '音频文件无法播放，请刷新素材库并检查文件。' }); },
    invalidate() { work.forEach(op => op.abort.abort()); work.clear(); emit(initialState()); },
    dispose() { alive = false; work.forEach(op => op.abort.abort()); work.clear(); listeners.clear(); },
  };
}

export function useEditorNextMusic(options: { context: MusicContext | null; host: MusicHostPort; api?: MusicApi }) {
  const [state, setState] = useState<EditorNextMusicState>(initialState);
  const ref = useRef<ReturnType<typeof createEditorNextMusicController> | null>(null);
  const host = useRef(options.host); host.current = options.host;
  const { clipId, epoch, generation } = options.context ?? {};
  useEffect(() => {
    const controller = createEditorNextMusicController({ read: () => host.current.read(), blockReason: () => host.current.blockReason(),
      setGlobalMusic: (patch, expected, signal) => host.current.setGlobalMusic(patch, expected, signal), flushSave: expected => host.current.flushSave(expected) }, options.api);
    ref.current = controller; setState(controller.getState());
    const off = controller.subscribe(setState);
    if (clipId && epoch !== undefined && generation !== undefined) void controller.refresh();
    return () => { off(); controller.dispose(); if (ref.current === controller) ref.current = null; };
  }, [clipId, epoch, generation, options.api]);
  return { state, refresh: () => ref.current?.refresh(), preview: (id: string) => ref.current?.preview(id),
    stopPreview: () => ref.current?.stopPreview(), previewFailed: () => ref.current?.previewFailed(),
    apply: (patch: Partial<GlobalMusic>) => ref.current?.apply(patch), generate: (input: MusicGenerateInput) => ref.current?.generate(input),
    invalidate: () => ref.current?.invalidate() };
}
