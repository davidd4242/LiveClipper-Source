import { ApiError } from './client';

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

/* ── Audio library ─────────────────────────────────────────── */

export type AudioAssetKind = 'music' | 'effect';

/** GET /api/assets — 本地音频库素材（内置合成音频 + 导入/生成的文件）。 */
export interface AudioAsset {
  id: string;
  name: string;
  kind: AudioAssetKind;
  style: string;
  path: string;
  builtIn: boolean;
}

/* ── Music generation status ───────────────────────────────── */

export interface MusicModelStatus {
  id: string;
  name: string;
  engine: string;
  description: string;
  approxBytes: number;
  builtIn: boolean;
  installed: boolean;
  license?: string | null;
  /** 仅当后端真实加载并生成过才为 true；旧后端缺省视为未验证。 */
  compatibilityVerified?: boolean;
}

export type MusicDownloadStateValue =
  | 'idle'
  | 'preparing'
  | 'downloading'
  /** 已请求取消、worker 尚未退出；非终态，需继续轮询直到 cancelled / failed。 */
  | 'cancelling'
  | 'completed'
  | 'cancelled'
  | 'failed';

export interface MusicDownloadState {
  state: MusicDownloadStateValue | string;
  modelId: string;
  progress: number;
  detail: string;
  error: string | null;
}

/** GET /api/music/status */
export interface MusicStatus {
  models: MusicModelStatus[];
  download: MusicDownloadState;
  allowDownload: boolean;
  defaultModelId: string;
  pythonAvailable: boolean;
  engineNote: string;
  /** 已保存的生成来源；旧后端缺省。线上来源不依赖 Python / 本地安装状态。 */
  generationProvider?: MusicGenerateProvider;
  /** 线上 API 是否已配置完整（URL / 模型 / key）；不代表远端可达。 */
  httpConfigured?: boolean;
  /** 已保存的线上模型名；未配置时为空串。 */
  apiModel?: string;
  /** 后端真实占用下载 worker（含 failed 后尚未退出）；旧后端缺省，需与 download.state 取 OR。 */
  downloadBusy?: boolean;
  /** 本地音乐生成 worker 仍在运行或退出中；旧后端缺省。 */
  localModelBusy?: boolean;
}

export type MusicGenerateProvider = 'local' | 'custom-http';

export type MusicJobStatus = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled';

/** POST /api/music/generate + GET /api/music/jobs/{id} */
export interface MusicJobState {
  id: string;
  status: MusicJobStatus | string;
  progress: number;
  detail: string;
  assetId: string | null;
  error: string | null;
  updatedAt: string;
  /** 生成完成后附带的新素材分析（BPM / 响度）；分析失败或缺省时为 null。 */
  analysis?: MusicAssetAnalysis | null;
}

export interface MusicGenerateInput {
  modelId: string;
  genre: string;
  style: string;
  content: string;
  theme: string;
  durationSeconds: number;
  allowDownload: boolean;
  /** 省略 = 旧本地行为；'custom-http' 走已保存的线上配置（modelId 可为空，不传 URL/key）。 */
  provider?: MusicGenerateProvider;
}

/* ── Online audio search ───────────────────────────────────── */

export type OnlineAudioSource = 'all' | 'jamendo' | 'freesound';
export type OnlineAudioLicense = 'usable' | 'cc0';

export interface OnlineAudio {
  id: string;
  title: string;
  creator: string;
  source: string;
  kind: AudioAssetKind;
  seconds: number;
  license: string;
  licenseUrl: string;
  landingUrl: string;
  audioUrl: string;
  attribution: string;
}

export interface OnlineAudioSearchResult {
  page: number;
  pageCount: number;
  total: number;
  query: string;
  results: OnlineAudio[];
}

export interface OnlineAudioQuery {
  query: string;
  kind: AudioAssetKind;
  source: OnlineAudioSource;
  license: OnlineAudioLicense;
  page: number;
}

/* ── HTTP plumbing ─────────────────────────────────────────── */

async function parseResponse<T>(response: Response): Promise<T> {
  if (!response.ok) {
    let message = `请求失败（HTTP ${response.status}）`;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) message = body.error;
    } catch {
      const text = await response.text().catch(() => '');
      if (text.trim()) message = text.trim().slice(0, 240);
    }
    throw new ApiError(message, response.status);
  }
  return (await response.json()) as T;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${apiBase}${path}`, {
      ...init,
      headers: {
        Accept: 'application/json',
        ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
        ...init?.headers,
      },
    });
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new ApiError(`无法连接 LiveClipper 后端：${detail}`);
  }
  return parseResponse<T>(response);
}

/* ── Music endpoints ───────────────────────────────────────── */

export function getMusicStatus(): Promise<MusicStatus> {
  return request<MusicStatus>('/api/music/status');
}

export function setMusicPreferences(allowDownload: boolean, modelId: string): Promise<MusicStatus> {
  return request<MusicStatus>('/api/music/preferences', {
    method: 'POST',
    body: JSON.stringify({ allowDownload, modelId }),
  });
}

export function importMusicModelDirectory(path: string): Promise<MusicStatus> {
  return request<MusicStatus>('/api/music/models/directory', {method:'POST',body:JSON.stringify({path})});
}

export function startModelDownload(modelId: string, allowDownload: boolean): Promise<MusicDownloadState> {
  return request<MusicDownloadState>('/api/music/models/download', {
    method: 'POST',
    body: JSON.stringify({ modelId, allowDownload }),
  });
}

/** 是否仍占用下载 worker（含取消中）；用于所有 download busy 判定。 */
export function isMusicDownloadBusy(state: string | undefined | null): boolean {
  return state === 'preparing' || state === 'downloading' || state === 'cancelling';
}

/**
 * 本地模型是否被任何后台任务占用（下载 / 取消中 / 本地生成）。
 * 新后端的 downloadBusy / localModelBusy 与旧的 download.state 取 OR：state 为 failed 但 worker 仍在退出时仍算 busy。
 */
export function isMusicModelBusy(status: MusicStatus | null | undefined): boolean {
  if (!status) return false;
  return (
    status.downloadBusy === true ||
    status.localModelBusy === true ||
    isMusicDownloadBusy(status.download?.state)
  );
}

export interface MusicDownloadCancelResponse {
  cancelRequested: boolean;
  download: MusicDownloadState;
}

/** POST /api/music/models/download/cancel（无 body）。仅取消本地模型下载；无任务时后端 409。 */
export function cancelModelDownload(): Promise<MusicDownloadCancelResponse> {
  return request<MusicDownloadCancelResponse>('/api/music/models/download/cancel', {
    method: 'POST',
  });
}

/* ── Local model uninstall (preview → confirmed delete) ─────── */

/** POST /api/music/models/uninstall-preview：只读，不删除任何内容。 */
export interface MusicModelRemovalPreview {
  modelId: string;
  exists: boolean;
  installed: boolean;
  actualBytes: number;
  fileCount: number;
  canRemove: boolean;
  notInstalled: boolean;
  downloadBusy: boolean;
  localModelBusy: boolean;
  reason: string;
  /** 不可删除时为空串。确认删除必须原样回传。 */
  identityFingerprint: string;
}

/** 200 响应：status 仅 removed / notInstalled；removed 才代表目录已消失且 installed=false。 */
export interface MusicModelRemovalResult {
  modelId: string;
  status: 'removed' | 'notInstalled' | string;
  directoryExists: boolean;
  installed: boolean;
  removedBytes: number;
  removedFiles: number;
}

/** 卸载请求失败。500 时 removalStatus 为 partial / unknown，directoryExists / installed 为后端读到的真实状态（可为 null=未读到）。 */
export class MusicModelRemovalError extends ApiError {
  readonly removalStatus: string | null;
  readonly directoryExists: boolean | null;
  readonly installed: boolean | null;

  constructor(
    message: string,
    httpStatus: number,
    removalStatus: string | null,
    directoryExists: boolean | null,
    installed: boolean | null,
  ) {
    super(message, httpStatus);
    this.name = 'MusicModelRemovalError';
    this.removalStatus = removalStatus;
    this.directoryExists = directoryExists;
    this.installed = installed;
  }
}

export function previewMusicModelUninstall(modelId: string): Promise<MusicModelRemovalPreview> {
  return request<MusicModelRemovalPreview>('/api/music/models/uninstall-preview', {
    method: 'POST',
    body: JSON.stringify({ modelId }),
  });
}

/** 只在用户点击「确认卸载」后调用；expectedFingerprint 必须是刚才预览返回的值。 */
export async function uninstallMusicModel(
  modelId: string,
  expectedFingerprint: string,
): Promise<MusicModelRemovalResult> {
  let response: Response;
  try {
    response = await fetch(`${apiBase}/api/music/models/uninstall`, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify({ modelId, expectedFingerprint, confirmed: true }),
    });
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new ApiError(`无法连接 LiveClipper 后端：${detail}`);
  }
  if (response.ok) return (await response.json()) as MusicModelRemovalResult;
  let message = `请求失败（HTTP ${response.status}）`;
  let removalStatus: string | null = null;
  let directoryExists: boolean | null = null;
  let installed: boolean | null = null;
  try {
    const body = (await response.json()) as {
      error?: string;
      status?: string;
      directoryExists?: boolean | null;
      installed?: boolean | null;
    };
    if (body.error) message = body.error;
    if (typeof body.status === 'string') removalStatus = body.status;
    if (typeof body.directoryExists === 'boolean') directoryExists = body.directoryExists;
    if (typeof body.installed === 'boolean') installed = body.installed;
  } catch {
    /* 非 JSON 响应：保留通用提示 */
  }
  throw new MusicModelRemovalError(message, response.status, removalStatus, directoryExists, installed);
}

export function startMusicGeneration(input: MusicGenerateInput): Promise<MusicJobState> {
  return request<MusicJobState>('/api/music/generate', {
    method: 'POST',
    body: JSON.stringify(input),
  });
}

export function getMusicJob(jobId: string): Promise<MusicJobState> {
  return request<MusicJobState>(`/api/music/jobs/${encodeURIComponent(jobId)}`);
}

export function cancelMusicJob(jobId: string): Promise<{ cancelled: boolean }> {
  return request<{ cancelled: boolean }>(`/api/music/jobs/${encodeURIComponent(jobId)}/cancel`, {
    method: 'POST',
  });
}

/* ── Audio asset endpoints ─────────────────────────────────── */

export function getAudioAssets(): Promise<AudioAsset[]> {
  return request<AudioAsset[]>('/api/assets');
}

/** 本地音频库素材的播放地址（支持 Range，可直接给 <audio> 使用）。 */
export function getAudioFileUrl(assetId: string): string {
  return `${apiBase}/api/assets/${encodeURIComponent(assetId)}/file`;
}

/* ── Music v1.1 data plane: BPM / beats / ducking / loudness ── */

/** 素材的节拍与响度分析；字段可为 null（降级）。 */
export interface MusicAssetAnalysis {
  bpm: number | null;
  beats: number[];
  method: string;
  confidence: number;
  durationSeconds: number;
  measuredLufs: number | null;
  targetLufs: number | null;
  loudnessMethod: string | null;
  analyzedAt: string | null;
}

/** GET /api/music/assets 返回的素材（在 AudioAsset 基础上带分析结果）。 */
export interface MusicLibraryAsset extends AudioAsset {
  analysis: MusicAssetAnalysis | null;
}

export interface MusicBeatLoop {
  start: number;
  end: number;
  seconds: number;
  beatCount: number;
  barCount: number;
  bpm: number;
  method: string;
}

/** GET /api/music/assets/{id}/beats — bpm 为 null 时表示降级。 */
export interface MusicBeats {
  assetId: string;
  bpm: number | null;
  beatInterval?: number;
  beats: number[];
  beatCount?: number;
  bars?: number;
  loop?: MusicBeatLoop | null;
  alignedCut?: { beatSeconds: number; relativeSeconds: number } | null;
  method?: string;
  confidence?: number;
  durationSeconds?: number;
  degraded?: boolean;
  reason?: string;
}

export interface MusicDuckingVoiceInterval {
  start: number;
  end: number;
}

export interface MusicDuckingEnvelope {
  clipId: string;
  musicAssetId: string | null;
  target: string;
  targetDb: number;
  voiceIntervals: MusicDuckingVoiceInterval[];
  keyframes: Array<{ time: number; db: number }>;
  appliedItemIds: string[];
  warnings: string[];
  createdAt: string;
}

export interface MusicDuckingResponse {
  envelope?: MusicDuckingEnvelope;
  saved: boolean;
  degraded: boolean;
  reason?: string;
  voiceIntervals?: number;
  keyframeCount?: number;
  target?: string;
}

/** GET /api/music/assets — 音频素材 + BPM/beat/响度分析。 */
export function getMusicAssets(): Promise<MusicLibraryAsset[]> {
  return request<MusicLibraryAsset[]>('/api/music/assets');
}

/** POST /api/music/assets/{id}/analyze — 用 ffmpeg 解码 + 自相关分析节拍（可降级为 null）。 */
export function analyzeMusicAsset(assetId: string): Promise<MusicAssetAnalysis | { assetId: string; bpm: null; beats: number[]; degraded: true; reason?: string }> {
  return request(`/api/music/assets/${encodeURIComponent(assetId)}/analyze`, { method: 'POST' });
}

/** GET /api/music/assets/{id}/beats — 按节拍对齐的循环/裁切建议。 */
export function getMusicBeats(assetId: string, targetSeconds = 30): Promise<MusicBeats> {
  const query = new URLSearchParams({ targetSeconds: String(targetSeconds) });
  return request<MusicBeats>(`/api/music/assets/${encodeURIComponent(assetId)}/beats?${query.toString()}`);
}

/** POST /api/music/projects/{clipId}/ducking — 生成并写入工程配乐 ducking 包络。 */
export function generateMusicDucking(
  clipId: string,
  options: { targetDb?: number; attackMs?: number; releaseMs?: number } = {},
): Promise<MusicDuckingResponse> {
  return request<MusicDuckingResponse>(
    `/api/music/projects/${encodeURIComponent(clipId)}/ducking`,
    { method: 'POST', body: JSON.stringify(options) },
  );
}

/** GET /api/music/projects/{clipId}/ducking — 已保存的 ducking 包络（404 时调用方按 null 处理）。 */
export function getMusicDucking(clipId: string): Promise<MusicDuckingEnvelope> {
  return request<MusicDuckingEnvelope>(`/api/music/projects/${encodeURIComponent(clipId)}/ducking`);
}

/**
 * POST /api/assets/import（multipart）。
 * 后端签名是 (IFormFile file, string kind, string? style)，简单类型默认从
 * query 绑定，因此 kind/style 放 query，文件放 form。
 */
export async function importAudioFile(
  file: File,
  kind: AudioAssetKind,
  style: string,
): Promise<AudioAsset> {
  const query = new URLSearchParams({ kind });
  if (style.trim()) query.set('style', style.trim());
  const form = new FormData();
  form.append('file', file);
  form.append('kind', kind);
  if (style.trim()) form.append('style', style.trim());

  let response: Response;
  try {
    // WebView2 Request.Content omits file-backed multipart bytes (WebView2Feedback#2162).
    const multipart = new Response(form);
    const bytes = await multipart.arrayBuffer();
    response = await fetch(`${apiBase}/api/assets/import?${query.toString()}`, {
      method: 'POST',
      headers: { ...Object.fromEntries(multipart.headers), Accept: 'application/json' },
      body: bytes,
    });
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new ApiError(`无法连接 LiveClipper 后端：${detail}`);
  }
  return parseResponse<AudioAsset>(response);
}

/* ── Online search endpoints ───────────────────────────────── */

export function searchOnlineAudio(params: OnlineAudioQuery): Promise<OnlineAudioSearchResult> {
  return request<OnlineAudioSearchResult>('/api/audio/online/search', {
    method: 'POST',
    body: JSON.stringify({
      query: params.query,
      kind: params.kind,
      source: params.source,
      license: params.license,
      page: params.page,
    }),
  });
}

export function importOnlineAudio(id: string, kind: AudioAssetKind): Promise<AudioAsset> {
  return request<AudioAsset>('/api/audio/online/import', {
    method: 'POST',
    body: JSON.stringify({ id, kind }),
  });
}
