import { createEngineDomAdapter } from './dom-adapter.ts';
import { engineFailureCleanup, engineWarnings, waitForEngine } from './loader.ts';
import type { EngineDomAdapter } from './dom-adapter.ts';
import type {
  ClipPatch, EngineAdapter, EngineAssetHandle, EngineClip, EngineClipInput, EngineClipType,
  EngineEvent, EngineEventCallback, EngineEventMap, EngineProjectSummary, EngineRequest, EngineResult, EngineWavOptions,
} from './types';

export type FCBridgeEvent = 'playhead' | 'playstate' | 'selection' | 'project' | 'media' | 'dirty' | 'render-progress';
export interface FCBridgeSummary {
  name: string;
  duration: number;
  width: number;
  height: number;
  tracks: number;
  clips: number;
  fps?: number;
  revision?: number;
}
export interface FCBridgeClip {
  key: string;
  track: string;
  start: number;
  duration: number;
  name: string;
  type: EngineClipType;
  fps?: number;
  effects?: EngineClip['effects'];
  transition?: EngineClip['transition'];
}
export interface FCBridge {
  version: string;
  ready: Promise<unknown>;
  openProject(project: unknown): unknown;
  openDemo(): unknown;
  closeProject(): unknown;
  getProjectSummary(): FCBridgeSummary;
  /** 收**帧**（内部 /fps 转秒后交给桥，见 :390）；不是秒。参数名曾误标 seconds。 */
  setPlayhead(frame: number): unknown;
  /** 返回**帧**（桥给秒，内部 ×fps，见 :391）。 */
  getPlayhead(): number;
  play(): unknown;
  pause(): unknown;
  isPlaying(): boolean;
  getSelection(): string[];
  selectClips(keys: string[]): unknown;
  deselectAll(): unknown;
  listClips(): FCBridgeClip[];
  updateClip(key: string, patch: Record<string, unknown>): unknown;
  addClip(clip: { track: string; start: number; duration: number; name: string; type: EngineClipType }): unknown;
  removeClips(keys: string[]): unknown;
  undo(): unknown;
  redo(): unknown;
  canUndo(): boolean;
  canRedo(): boolean;
  isDirty(): boolean;
  clearDirty(): unknown;
  importAssets(handles: EngineAssetHandle[]): unknown;
  on(event: FCBridgeEvent, callback: (payload: Record<string, unknown>) => void): () => void;
  requestRender(): unknown;
  exportWav?(options: { sampleRate?: number }): unknown;
  cancelPending?(): unknown;
  dispose(): unknown;
}
export interface EngineAdapterOptions {
  bridge?: FCBridge;
  fps?: number | (() => number);
  host?: HTMLElement;
  onWarning?: (warning: string) => void;
  // The loader reuses one classic-script engine; terminal bridge disposal is opt-in.
  disposeBridge?: boolean;
}

const pendingOpenOwners = new WeakMap<FCBridge, { adapter: EngineAdapter }>();

function object(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' ? value as Record<string, unknown> : {};
}
function errorText(error: unknown): string {
  const value = object(error);
  return String(value.error ?? value.message ?? error);
}
function check(condition: unknown, error: string): asserts condition {
  if (!condition) throw { ok: false, error };
}
function frame(value: number) {
  check(Number.isSafeInteger(value) && value >= 0, 'invalid_frame');
  return value;
}
/** D44：适配器对外的时间参数一律是秒（与桥、与宿主 React 状态一致），只做范围校验、不做换算。 */
function seconds(value: number) {
  check(typeof value === 'number' && Number.isFinite(value) && value >= 0, 'invalid_seconds');
  return value;
}
function clock(fps: number) {
  check(Number.isFinite(fps) && fps > 0, 'invalid_frame_rate');
  return {
    toSeconds: (value: number) => frame(value) / fps,
    toFrames: (value: number) => {
      check(typeof value === 'number' && Number.isFinite(value) && value >= 0, 'invalid_seconds');
      return frame(Math.round(value * fps));
    },
  };
}
function projectRate(project: unknown): number | undefined {
  const doc = object(project);
  if (doc.__fcproj === 1 || doc.format === 'filmcraft-project') return object(doc.sequence).fps as number | undefined;
  if (doc.format !== 'filmcraft.project' || doc.schema_version !== 15) return undefined;
  const items = object(object(doc.project).items);
  const active = items[String(object(doc.view).active_sequence)];
  const sequence = object(object(active).kind).Sequence
    ?? Object.values(items).map((item) => object(object(item).kind).Sequence).find(Boolean);
  const rate = object(object(object(sequence).settings).frame_rate);
  return typeof rate.num === 'number' && typeof rate.den === 'number' ? rate.num / rate.den : undefined;
}

export function createEngineAdapter(options: EngineAdapterOptions = {}): EngineAdapter {
  let disposed = false;
  let connected = false;
  let revision: number | null = null;
  let revisionWarningReported = false;
  let selectionRevision = 0;
  let projectGeneration = 0;
  let knownFps: number | undefined;
  let dom: EngineDomAdapter | undefined;
  let disposal: Promise<EngineResult> | undefined;
  const subscriptions = new Set<() => void>();
  const warnings: string[] = [];
  const listeners: { [E in EngineEvent]: Set<EngineEventCallback<E>> } = {
    playhead: new Set(), playstate: new Set(), selection: new Set(),
    'project-dirty': new Set(), 'render-progress': new Set(), 'media/added': new Set(),
  };
  const warn = (message: string) => {
    if (warnings.includes(message)) return;
    warnings.push(message);
    try { options.onWarning?.(message); } catch { /* Warning observers do not own engine commands. */ }
  };
  const bridge = () => {
    check(!disposed, 'adapter_disposed');
    const value = options.bridge ?? (typeof window === 'undefined' ? undefined
      : (window as Window & { FCBridge?: FCBridge }).FCBridge);
    check(value, 'engine_not_ready');
    for (const warning of engineWarnings(value, options.host)) warn(warning);
    return value;
  };
  const rate = (api: FCBridge) => {
    const summary = api.getProjectSummary();
    const value = summary.fps ?? (typeof options.fps === 'function' ? options.fps() : options.fps) ?? knownFps;
    check(value !== undefined, 'frame_rate_unavailable');
    clock(value);
    knownFps = value;
    return value;
  };
  const emit = <E extends EngineEvent>(event: E, payload: EngineEventMap[E]) => {
    if (disposed) return;
    const stamped = Object.assign({}, payload, { t: performance.now() });
    for (const callback of [...listeners[event]]) {
      try { callback(stamped); } catch (error) { warn(`event_callback:${errorText(error)}`); }
    }
  };
  const offAll = () => {
    for (const off of [...subscriptions]) off();
    connected = false;
  };
  const syncRevision = (value: FCBridgeSummary) => {
    if (Number.isSafeInteger(value.revision) && (value.revision as number) >= 0) {
      const next = value.revision as number;
      revision = revision === null ? next : Math.max(revision, next);
    } else warn('snapshot_revision_unavailable');
  };
  const connect = (api: FCBridge) => {
    if (connected) return;
    syncRevision(api.getProjectSummary());
    const handlers: Record<FCBridgeEvent, (payload: Record<string, unknown>) => void> = {
      playhead(value) {
        if (typeof value.frame === 'number' && typeof value.seconds === 'number' && value.seconds > 0) {
          knownFps = value.frame / value.seconds;
        }
        const time = clock(rate(api));
        const current = typeof value.frame === 'number' ? frame(Math.round(value.frame)) : time.toFrames(value.seconds as number);
        emit('playhead', { frame: current, seconds: time.toSeconds(current) });
      },
      playstate: (value) => emit('playstate', { playing: value.playing === true }),
      selection(value) {
        const keys = Array.isArray(value.keys) ? value.keys.filter((key): key is string => typeof key === 'string') : [];
        selectionRevision = Math.max(selectionRevision + 1, Number.isSafeInteger(value.revision) ? value.revision as number : 0);
        emit('selection', { keys: [...keys], revision: selectionRevision });
      },
      project(value) {
        if (typeof value.fps === 'number') knownFps = value.fps;
        if (Number.isSafeInteger(value.revision)) revision = value.revision as number;
      },
      dirty(value) {
        if (Number.isSafeInteger(value.revision)) revision = value.revision as number;
        emit('project-dirty', { dirty: value.dirty === true, revision });
      },
      media(value) {
        const key = [value.key, value.id].find((item): item is string => typeof item === 'string' && item.length > 0) ?? null;
        const generation = projectGeneration;
        const publish = () => {
          if (disposed || generation !== projectGeneration) return;
          if (key === null) warn('media_key_null');
          const duration = typeof value.durationFrames === 'number' ? frame(Math.round(value.durationFrames))
            : typeof value.duration === 'number' && value.duration > 0 ? clock(rate(api)).toFrames(value.duration) : null;
          emit('media/added', { key, name: typeof value.name === 'string' ? value.name : '', durationFrames: duration });
        };
        if (typeof value.durationFrames === 'number' || (typeof value.duration === 'number' && value.duration > 0)) {
          publish();
          return;
        }
        // Registration precedes load/decode, so inspect the live asset in the next microtask.
        void Promise.resolve().then(() => {
          if (disposed || generation !== projectGeneration) return;
          if (value.decoding) return value.decoding;
          if (typeof value.load === 'function') return value.load();
          if (typeof value.decode === 'function') return value.decode();
        }).then(publish, (error) => {
          if (disposed || generation !== projectGeneration) return;
          warn(`media_load:${errorText(error)}`);
          publish();
        }).catch((error) => {
          if (!disposed && generation === projectGeneration) warn(`media_event:${errorText(error)}`);
        });
      },
      'render-progress'(value) {
        check(typeof value.jobId === 'string' && typeof value.percent === 'number' && Number.isFinite(value.percent)
          && typeof value.stage === 'string', 'invalid_render_progress');
        emit('render-progress', { jobId: value.jobId, percent: value.percent, stage: value.stage });
      },
    };
    try {
      for (const event of Object.keys(handlers) as FCBridgeEvent[]) {
        let remove: () => void;
        try { remove = api.on(event, (value) => { if (!disposed) handlers[event](value); }); }
        catch (error) {
          if (event !== 'render-progress') throw error;
          warn('unsupported_event:render-progress');
          continue;
        }
        check(typeof remove === 'function', 'invalid_unsubscribe');
        let active = true;
        const off = () => {
          if (!active) return;
          active = false;
          subscriptions.delete(off);
          try { remove(); } catch (error) { warn(`unsubscribe:${errorText(error)}`); }
        };
        subscriptions.add(off);
      }
      connected = true;
    } catch (error) {
      offAll();
      throw error;
    }
  };
  const result = <T>(value: unknown): EngineResult<T> => {
    const envelope = object(value);
    const messages = Array.isArray(envelope.warnings) ? envelope.warnings.map(String) : [];
    const fresh = warnings.filter((warning) => warning !== 'snapshot_revision_unavailable' || !revisionWarningReported);
    const combined = [...new Set([...fresh, ...messages])];
    if (combined.includes('snapshot_revision_unavailable')) revisionWarningReported = true;
    const extra = combined.length ? { warnings: combined } : {};
    if (envelope.ok === false) return {
      ok: false, via: 'bridge', error: errorText(value),
      ...(typeof envelope.code === 'string' ? { code: envelope.code } : {}),
      ...(typeof envelope.message === 'string' ? { message: envelope.message } : {}), ...extra,
    };
    return { ok: true, via: 'bridge', ...(value === undefined ? {} : { data: value as T }), ...extra };
  };
  const perform = async <T = unknown>(action: (api: FCBridge) => unknown): Promise<EngineResult<T>> => {
    try {
      const api = bridge();
      const value = await action(api);
      if (object(value).ok !== false && !disposed) {
        try { connect(api); } catch (error) { warn(`subscription:${errorText(error)}`); }
      }
      return result<T>(value);
    } catch (error) {
      return result<T>({ ...object(error), ok: false, error: errorText(error) });
    }
  };
  const summary = (api: FCBridge): EngineProjectSummary => {
    const value = api.getProjectSummary();
    syncRevision(value);
    const fps = rate(api);
    // D44：summary.duration 与桥同口径（秒）；换算成帧只发生在持久化文档里。
    return { ...value, fps, duration: seconds(value.duration) };
  };
  const clips = (api: FCBridge): EngineClip[] => {
    const time = clock(rate(api));
    return api.listClips().map((clip) => {
      const start = time.toFrames(clip.start);
      const duration = time.toFrames(clip.duration);
      const { track, ...fields } = clip;
      return { ...fields, trackId: track, start, duration, end: start + duration };
    });
  };
  const select = (api: FCBridge, keys: string[], mode: 'replace' | 'add' | 'toggle') => {
    check(Array.isArray(keys) && keys.every((key) => typeof key === 'string'), 'invalid_keys');
    check(['replace', 'add', 'toggle'].includes(mode), 'invalid_selection_mode');
    const selected = new Set(mode === 'replace' ? [] : api.getSelection());
    for (const key of new Set(keys)) {
      if (mode === 'toggle' && selected.has(key)) selected.delete(key);
      else selected.add(key);
    }
    return api.selectClips([...selected]);
  };
  const update = (api: FCBridge, key: string, patch: ClipPatch) => {
    check(patch && typeof patch === 'object' && !Array.isArray(patch), 'invalid_patch');
    const allowed = ['start', 'end', 'sourceIn', 'speed', 'trackId', 'name', 'enabled', 'effects', 'transition'];
    const unsupported = Object.keys(patch).filter((name) => !allowed.includes(name) && object(patch)[name] !== undefined);
    check(!unsupported.length, `unsupported_patch_fields:${unsupported.join(',')}`);
    const mapped: Record<string, unknown> = {};
    if (patch.start !== undefined) mapped.start = seconds(patch.start);
    if (patch.sourceIn !== undefined) mapped.sourceIn = seconds(patch.sourceIn);
    if (patch.speed !== undefined) mapped.speed = patch.speed;
    if (patch.end !== undefined) {
      const current = api.listClips().find((clip) => clip.key === key);
      check(current, 'clip_not_found');
      const start = patch.start ?? seconds(current.start);
      check(seconds(patch.end) > seconds(start), 'invalid_clip_duration');
      mapped.duration = seconds(patch.end - start);
    }
    if (patch.trackId !== undefined) mapped.track = patch.trackId;
    for (const field of ['name', 'enabled', 'effects', 'transition'] as const) {
      if (patch[field] !== undefined) mapped[field] = patch[field];
    }
    return api.updateClip(key, mapped);
  };
  // EngineRequest 的时间字段（seek/renderFrame/addAssets.at）是**帧**（历史契约）；
  // 适配器对外的传输方法（setPlayhead/getPlayhead/patch）是**秒**（D44）。换算只在这里。
  const render = (api: FCBridge, value: number) => {
    const at = clock(rate(api)).toSeconds(frame(value));
    const paused = api.pause();
    if (object(paused).ok === false) return paused;
    const seeked = api.setPlayhead(at);
    if (object(seeked).ok === false) return seeked;
    return api.requestRender();
  };
  const addAssets = (api: FCBridge, request: Extract<EngineRequest, { type: 'addAssets' }>) => {
    check(typeof request.trackId === 'string' && request.trackId.length > 0, 'invalid_track');
    const sought = api.setPlayhead(clock(rate(api)).toSeconds(frame(request.at)));
    if (object(sought).ok === false) return sought;
    const imported = api.importAssets(request.assetIds);
    if (object(imported).ok === false) return imported;
    const keys = object(imported).keys;
    check(Array.isArray(keys) && keys.every((key) => typeof key === 'string'), 'invalid_import_result');
    for (const key of keys) {
      const current = api.listClips().find((clip) => clip.key === key);
      check(current, 'imported_clip_not_found');
      if (current.track === request.trackId) continue;
      const moved = api.updateClip(key, { track: request.trackId });
      if (object(moved).ok === false) {
        warn('addAssets_partial_import:imported_clips_retained');
        return moved;
      }
    }
    return imported;
  };

  const adapter: EngineAdapter = {
    warnings,
    version: () => perform<string>((api) => api.version),
    ready: () => perform(async (api) => {
      const release = engineFailureCleanup(api, options.host);
      try {
        await waitForEngine(api.ready, 20000, 'engine_ready_timeout');
        check(!disposed, 'adapter_disposed');
        connect(api);
      } catch (error) {
        if (!disposed) { dom?.dispose(); offAll(); release(); }
        throw error;
      }
    }),
    openProject(project) {
      const generation = ++projectGeneration;
      return perform(async (api) => {
        const json = typeof File !== 'undefined' && project instanceof File ? JSON.parse(await project.text()) as unknown : project;
        check(!disposed, 'adapter_disposed');
        check(generation === projectGeneration, 'request_superseded');
        const pending = { adapter };
        pendingOpenOwners.set(api, pending);
        try {
          const opened = await api.openProject(json);
          if (object(opened).ok !== false) knownFps = projectRate(json);
          return opened;
        } finally {
          if (pendingOpenOwners.get(api) === pending) pendingOpenOwners.delete(api);
        }
      });
    },
    openDemo() {
      projectGeneration++;
      return perform((api) => {
        const opened = api.openDemo();
        if (object(opened).ok !== false) knownFps = undefined;
        return opened;
      });
    },
    closeProject() {
      projectGeneration++;
      return perform((api) => api.closeProject());
    },
    getProjectSummary: () => perform<EngineProjectSummary>(summary),
    setPlayhead: (value) => perform((api) => api.setPlayhead(seconds(value))),
    getPlayhead: () => perform<number>((api) => seconds(api.getPlayhead())),
    play: () => perform((api) => api.play()),
    pause: () => perform((api) => api.pause()),
    isPlaying: () => perform<boolean>((api) => api.isPlaying()),
    getSelection: () => perform<string[]>((api) => [...api.getSelection()]),
    selectClips: (keys, mode = 'replace') => perform((api) => select(api, keys, mode)),
    deselectAll: () => perform((api) => api.deselectAll()),
    listClips: () => perform<EngineClip[]>(clips),
    updateClip: (key, patch) => perform((api) => update(api, key, patch)),
    addClip: (clip: EngineClipInput) => perform((api) => {
      check(seconds(clip.duration) > 0, 'invalid_clip_duration');
      return api.addClip({ track: clip.trackId, start: seconds(clip.start), duration: seconds(clip.duration), name: clip.name, type: clip.type });
    }),
    removeClips: (keys, ripple = false) => perform((api) => {
      check(!ripple, 'unsupported_request:ripple_delete');
      return api.removeClips(keys);
    }),
    undo: () => perform((api) => api.undo()),
    redo: () => perform((api) => api.redo()),
    canUndo: () => perform<boolean>((api) => api.canUndo()),
    canRedo: () => perform<boolean>((api) => api.canRedo()),
    isDirty: () => perform<boolean>((api) => api.isDirty()),
    clearDirty: () => perform((api) => api.clearDirty()),
    importAssets: (handles) => perform((api) => api.importAssets(handles)),
    requestRender: () => perform((api) => api.requestRender()),
    exportWav: (input: EngineWavOptions = {}) => perform((api) => {
      check(input && typeof input === 'object' && !Array.isArray(input), 'invalid_export_options');
      check(input.name === undefined || typeof input.name === 'string', 'invalid_export_name');
      check(input.sampleRate === undefined || Number.isSafeInteger(input.sampleRate) && input.sampleRate > 0, 'invalid_sample_rate');
      check(typeof api.exportWav === 'function', 'unsupported_request:exportWav');
      connect(api);
      const exported = api.exportWav(input.sampleRate === undefined ? {} : { sampleRate: input.sampleRate });
      if (object(exported).ok === false) return exported;
      const data = object(exported);
      return { ...data, ...(input.name === undefined ? {} : { name: input.name }),
        ...(typeof data.duration === 'number' ? { duration: clock(rate(api)).toFrames(data.duration) } : {}) };
    }),
    renderFrame: (value) => perform((api) => render(api, value)),
    request(request: EngineRequest) {
      if (!request || typeof request !== 'object') {
        return perform(() => { throw { ok: false, error: 'invalid_request' }; });
      }
      switch (request.type) {
        case 'openProject': return adapter.openProject(request.file);
        case 'openDemo': return adapter.openDemo();
        case 'seek': return adapter.setPlayhead(request.frame);
        case 'select': return adapter.selectClips(request.keys, request.mode);
        case 'addAssets': return perform((api) => addAssets(api, request));
        case 'delete': return adapter.removeClips(request.keys, request.ripple);
        case 'updateClip': return adapter.updateClip(request.key, request.patch);
        case 'renderFrame': return adapter.renderFrame(request.frame);
        case 'exportWav': return adapter.exportWav({ name: request.name, sampleRate: request.sampleRate });
        default: return perform(() => { throw { ok: false, error: 'unsupported_request' }; });
      }
    },
    on<E extends EngineEvent>(event: E, callback: EngineEventCallback<E>) {
      let active = !disposed;
      const callbacks = listeners[event];
      if (!active) return () => {};
      check(callbacks && typeof callback === 'function', 'invalid_event');
      callbacks.add(callback);
      try { connect(bridge()); } catch (error) { warn(`subscription:${errorText(error)}`); }
      return () => {
        if (!active) return;
        active = false;
        callbacks.delete(callback);
        // Internal observers stay attached until dispose so snapshot revision remains live.
      };
    },
    snapshot() {
      const api = bridge();
      connect(api);
      const current = summary(api);
      return {
        playhead: seconds(api.getPlayhead()), duration: current.duration,
        playing: api.isPlaying(), selectedKeys: [...api.getSelection()], revision,
      };
    },
    dispose() {
      if (disposal) return disposal;
      dom?.dispose();
      offAll();
      for (const callbacks of Object.values(listeners)) callbacks.clear();
      let api: FCBridge | undefined;
      try { api = bridge(); } catch { /* A missing bridge still permits local cleanup. */ }
      disposed = true;
      disposal = (async () => {
        try {
          if (!api) return result(undefined);
          const pending = pendingOpenOwners.get(api);
          if (!pending || pending.adapter === adapter) {
            if (typeof api.cancelPending === 'function') api.cancelPending();
          }
          return result(options.disposeBridge ? await api.dispose() : await api.pause());
        }
        catch (error) { return result({ ok: false, error: errorText(error) }); }
      })();
      return disposal;
    },
  };
  if (options.host) dom = createEngineDomAdapter(options.host, () => adapter.requestRender(), { onError: warn });
  return adapter;
}
