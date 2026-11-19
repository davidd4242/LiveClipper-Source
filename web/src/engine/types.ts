export interface EngineClipEffect {
  effect: string;
  enabled?: boolean;
  params?: Record<string, unknown>;
}
export type EngineClipTransition = (
  | { id: string; type?: string; name?: string }
  | { id?: string; type: string; name?: string }
  | { id?: string; type?: string; name: string }
) & { params?: { duration?: number; align?: 'start' | 'end' | 'center' } };

export type ClipPatch = {
  sourceIn?: number;
  start?: number;
  end?: number;
  trackId?: string;
  speed?: number;
  name?: string;
  enabled?: boolean;
  effects?: EngineClipEffect[];
  transition?: EngineClipTransition;
};

export type EngineRequest =
  | { type: 'openProject'; file: File }
  | { type: 'openDemo' }
  | { type: 'seek'; frame: number }
  | { type: 'select'; keys: string[]; mode: 'replace' | 'add' | 'toggle' }
  | { type: 'addAssets'; assetIds: string[]; at: number; trackId: string }
  | { type: 'delete'; keys: string[]; ripple?: boolean }
  | { type: 'updateClip'; key: string; patch: ClipPatch }
  | { type: 'renderFrame'; frame: number }
  | { type: 'exportWav'; name: string; sampleRate?: number };

export interface EngineSnapshot {
  playhead: number;
  duration: number;
  playing: boolean;
  selectedKeys: string[];
  revision: number | null;
}

export type EngineEventMap = {
  playhead:          { frame: number; seconds: number };
  playstate:         { playing: boolean };
  selection:         { keys: string[]; revision: number };
  'project-dirty':   { dirty: boolean; revision: number | null };
  'render-progress': { jobId: string; percent: number; stage: string };
  'media/added':     { key: string | null; name: string; durationFrames: number | null };
};
export type EngineEvent = keyof EngineEventMap;
export type EngineEventCallback<E extends EngineEvent> = (payload: EngineEventMap[E]) => void;
export type EngineResult<T = unknown> = {
  ok: boolean;
  data?: T;
  error?: string;
  code?: string;
  message?: string;
  via: 'bridge' | 'direct';
  warnings?: string[];
};

export type EngineClipType = 'video' | 'audio' | 'adjust' | 'title' | 'image';
export interface EngineClip {
  key: string;
  trackId: string;
  start: number;
  end: number;
  duration: number;
  name: string;
  type: EngineClipType;
  fps?: number;
  effects?: EngineClipEffect[] | null;
  transition?: EngineClipTransition | null;
}
export interface EngineWavOptions {
  name?: string;
  sampleRate?: number;
}
export interface EngineClipInput {
  trackId: string;
  start: number;
  duration: number;
  name: string;
  type: EngineClipType;
}
export interface EngineProjectSummary {
  name: string;
  duration: number;
  width: number;
  height: number;
  tracks: number;
  clips: number;
  fps: number;
  revision?: number;
}
export type EngineAssetHandle = string | { assetId?: string; id?: string };

export interface EngineAdapter {
  readonly warnings: string[];
  version(): Promise<EngineResult<string>>;
  ready(): Promise<EngineResult>;
  request(request: EngineRequest): Promise<EngineResult>;
  on<E extends EngineEvent>(event: E, callback: EngineEventCallback<E>): () => void;
  snapshot(): EngineSnapshot;
  openProject(project: unknown): Promise<EngineResult>;
  openDemo(): Promise<EngineResult>;
  closeProject(): Promise<EngineResult>;
  getProjectSummary(): Promise<EngineResult<EngineProjectSummary>>;
  /** 秒（D44）：与桥、与宿主 React 状态同口径；帧只出现在持久化文档。 */
  setPlayhead(seconds: number): Promise<EngineResult>;
  getPlayhead(): Promise<EngineResult<number>>;
  play(): Promise<EngineResult>;
  pause(): Promise<EngineResult>;
  isPlaying(): Promise<EngineResult<boolean>>;
  getSelection(): Promise<EngineResult<string[]>>;
  selectClips(keys: string[], mode?: 'replace' | 'add' | 'toggle'): Promise<EngineResult>;
  deselectAll(): Promise<EngineResult>;
  listClips(): Promise<EngineResult<EngineClip[]>>;
  updateClip(key: string, patch: ClipPatch): Promise<EngineResult>;
  addClip(clip: EngineClipInput): Promise<EngineResult>;
  removeClips(keys: string[], ripple?: boolean): Promise<EngineResult>;
  undo(): Promise<EngineResult>;
  redo(): Promise<EngineResult>;
  canUndo(): Promise<EngineResult<boolean>>;
  canRedo(): Promise<EngineResult<boolean>>;
  isDirty(): Promise<EngineResult<boolean>>;
  clearDirty(): Promise<EngineResult>;
  importAssets(handles: EngineAssetHandle[]): Promise<EngineResult>;
  requestRender(): Promise<EngineResult>;
  exportWav(options?: EngineWavOptions): Promise<EngineResult>;
  renderFrame(frame: number): Promise<EngineResult>;
  dispose(): Promise<EngineResult>;
}

export interface EngineManifest {
  schema: number;
  rootId: string;
  styles: { src: string; out: string; dataSrc: string }[];
  scripts: { src: string; out: string }[];
  bootstrap: { src: string; ready?: string };
  skeleton: { src: string; includesRoot: boolean };
  portal: { src: string; bodyClass: string };
  portalRules: unknown[];
  warnings: unknown[];
}

export interface EngineHandle extends Pick<EngineAdapter, 'request' | 'on' | 'snapshot'> {
  root: HTMLElement;
  warnings: string[];
  dispose(): EngineDisposeReport;
}

export interface EngineDisposeReport {
  removedStyles: number;
  removedScripts: number;
  detachedPortals: number;
  retained: string[];
}

export interface EngineLoadOptions {
  viewportMode?: 'canvas' | 'full';
  manifestUrl?: string;
  scriptTimeoutMs?: number;
  onDispose?: (report: EngineDisposeReport) => void;
}

export interface EngineDiagnostics {
  status: 'idle' | 'loading' | 'ready' | 'disposed' | 'failed';
  scriptsLoaded: number;
  scriptExecutions: number;
  mounts: number;
  lastDispose?: EngineDisposeReport;
}
