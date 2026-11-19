import type { ReplicaSequence } from '../features/editor/replicaProject';

export interface FullEngineDocument {
  __fcproj: 1;
  sequence: ReplicaSequence & { width?: number; height?: number };
  playhead?: number;
}
export interface FullEngineResult {
  ok: boolean;
  error?: string;
  warnings?: string[];
  generation?: number;
  engineRevision?: number;
}
export interface FullEngineDirty {
  dirty: boolean;
  engineRevision: number;
  generation: number;
  reason?: string;
}
export interface FullEngineRequest extends FullEngineDirty {
  requestId: string;
}
export interface FullEngineCallbacks {
  onDocumentLoaded?: (snapshot: ReturnType<FullEngineBridge['hostSnapshot']>) => void;
  onDirty?: (event: FullEngineDirty) => void;
  onSaveRequest?: (event: FullEngineRequest) => void;
  onCloseRequest?: (event: FullEngineRequest) => void;
}
export interface FullEngineMediaSpec {
  url: string;
  audioUrl?: string;
  name?: string;
  kind?: 'video';
  hasAudio?: boolean;
  durationSec?: number;
  width?: number;
  height?: number;
}
export interface FullEngineLoadOptions {
  clipVolumeDb?: Record<string, number>;
  media?: Record<string, FullEngineMediaSpec>;
}
/** Incremental source registration: probe + bind only, no history/revision change. */
export interface FullEngineRegisterSourcesResult extends FullEngineResult {
  registered?: string[];
  already?: string[];
  failed?: { sourceId: string; error: string }[];
  generation?: number;
}
export interface FullEngineBridge {
  hostSetSequenceSettings?(data: { expected: { generation: number; engineRevision: number }; width: number; height: number; fps: number }): FullEngineResult;
  onSequenceSettingsRequest?(callback: () => void): () => void;
  ready: Promise<unknown>;
  configureHost(options: { persistence: 'host' }): FullEngineResult;
  hostLoadDocument(document: FullEngineDocument, options?: FullEngineLoadOptions): Promise<FullEngineResult>;
  hostSnapshot(): FullEngineResult & { document?: unknown; dirty?: boolean };
  retryMedia?(sourceIds?: string[]): Promise<FullEngineResult & { boundSources?: string[]; missingSources?: string[] }>;
  hostRegisterSources?(data: { expected: { generation: number }; sources: Record<string, FullEngineMediaSpec> }): Promise<FullEngineRegisterSourcesResult>;
  onDirty(callback: (event: FullEngineDirty) => void): () => void;
  onSaveRequest(callback: (event: FullEngineRequest) => void): () => void;
  onCloseRequest(callback: (event: FullEngineRequest) => void): () => void;
  pause(): unknown;
  cancelPending?(): unknown;
}
export interface FullEngineViewport {
  setMode(mode: 'canvas' | 'full'): FullEngineResult;
}
export interface FullEngineConnection {
  loadDocument(document: FullEngineDocument): Promise<FullEngineResult>;
  snapshot(): ReturnType<FullEngineBridge['hostSnapshot']>;
  release(): void;
}

export function requireFullEngineResult(result: FullEngineResult, fallback: string): FullEngineResult {
  if (!result || result.ok !== true) throw new Error(result?.error || fallback);
  return result;
}

// The singleton bridge restores asynchronously and rejects overlapping loads.
let documentLoads: Promise<unknown> = Promise.resolve();

export function waitForFullEngineDocuments(): Promise<unknown> {
  return documentLoads;
}

export function connectFullEngine(
  bridge: FullEngineBridge | undefined,
  viewport: FullEngineViewport | undefined,
  callbacks: FullEngineCallbacks,
  options?: FullEngineLoadOptions,
): FullEngineConnection {
  if (!bridge || ['configureHost', 'hostLoadDocument', 'hostSnapshot', 'onDirty', 'onSaveRequest', 'onCloseRequest', 'pause']
    .some((key) => typeof bridge[key as keyof FullEngineBridge] !== 'function')) {
    throw new Error('engine_full_host_api_missing');
  }
  if (!viewport || typeof viewport.setMode !== 'function') throw new Error('engine_full_viewport_api_missing');
  requireFullEngineResult(viewport.setMode('full'), 'engine_full_mode_failed');
  let active = true;
  const subscriptions: (() => void)[] = [];
  const release = () => {
    if (!active) return;
    active = false;
    for (const off of subscriptions.splice(0)) {
      try { off(); } catch { /* Release the remaining subscriptions even if one engine listener fails. */ }
    }
    try { bridge.cancelPending?.(); } finally { bridge.pause(); }
  };
  try {
    subscriptions.push(bridge.onDirty((event) => { if (active) callbacks.onDirty?.(event); }));
    subscriptions.push(bridge.onSaveRequest((event) => { if (active) callbacks.onSaveRequest?.(event); }));
    subscriptions.push(bridge.onCloseRequest((event) => { if (active) callbacks.onCloseRequest?.(event); }));
  } catch (error) { release(); throw error; }
  return {
    loadDocument(document) {
      const load = documentLoads.then(async () => {
        if (!active) throw new Error('engine_full_host_released');
        requireFullEngineResult(bridge.configureHost({ persistence: 'host' }), 'engine_host_persistence_failed');
        const result = await bridge.hostLoadDocument(document, options);
        if (!active) throw new Error('engine_full_host_released');
        requireFullEngineResult(result, 'engine_host_document_load_failed');
        // Loading replaces the native generation without an edit/dirty notification.
        callbacks.onDocumentLoaded?.(bridge.hostSnapshot());
        return result;
      });
      documentLoads = load.catch(() => undefined);
      return load;
    },
    snapshot() {
      if (!active) throw new Error('engine_full_host_released');
      const result = bridge.hostSnapshot();
      requireFullEngineResult(result, 'engine_host_snapshot_failed');
      return result;
    },
    release,
  };
}
