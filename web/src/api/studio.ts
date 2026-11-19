import { ApiError } from './client';
import type { EditorProject } from './types';

/**
 * Studio 工作台用到的后端辅助接口。
 * 全部字段来自 src/LiveClipper.App 的真实端点，未接入的字段不要在这里编造。
 */

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${apiBase}${path}`, {
      ...init,
      headers: {
        Accept: 'application/json',
        ...(init?.body && typeof init.body === 'string' ? { 'Content-Type': 'application/json' } : {}),
        ...init?.headers,
      },
    });
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new ApiError(`无法连接 LiveClipper 后端：${detail}`);
  }

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

/** GET /api/settings 里工作台会展示的字段（后端返回完整 LiveSettings，其余忽略）。 */
export interface StudioSettings {
  roomId: number;
  roomIds: number[];
  bilibiliUid: number;
  storageRoot: string;
  ffmpegPath: string;
  pythonPath: string;
  whisperModel: string;
  autoTranscribe: boolean;
  autoHighlight: boolean;
  autoAnalyze: boolean;
  autoTrimSilence: boolean;
  highlightPreRollSeconds: number;
  highlightPostRollSeconds: number;
  manualPreRollSeconds: number;
  manualPostRollSeconds: number;
  danmakuRecordLimit: number;
  aiProvider: string;
  aiEndpoint: string;
  aiModel: string;
  localOutputRoot: string;
  musicModelDownloadAllowed: boolean;
  musicModelId: string;
  cleanupOnExit: boolean;
}

/** GET /api/studio/outputs 的真实条目（素材包 / 剪映草稿）。 */
export interface StudioOutput {
  id: string;
  clipId: string;
  kind: 'package' | 'draft';
  outputPath: string;
  createdAt: string;
  status: string | null;
  error: string | null;
  progress: number | null;
}

/** POST /api/studio/import-files 的响应（StudioMediaService.ImportResult）。 */
export interface StudioImportResult {
  added: number;
  existing: number;
  matchedSubtitles: number;
  missingSubtitles: number;
  clipIds?: string[] | null;
}

/** GET /api/studio/clips/{id}/transcript-state。 */
export interface StudioTranscriptState {
  clipId: string;
  transcriptVersion: string;
  autoStatus: string | null;
  autoUpdatedAt: string | null;
  autoKeepCount: number;
  autoTrimmedSeconds: number;
}

/** GET /api/studio/videos 的条目。 */
export interface StudioVideo {
  clipId: string;
  path: string;
  title: string;
  outputDirectory: string;
  importedAt: string;
}

/** GET /api/ffmpeg/download。 */
export interface FfmpegDownloadState {
  state: string;
  progress: number;
  error: string | null;
  path: string | null;
}

export function getStudioSettings(): Promise<StudioSettings> {
  return request<StudioSettings>('/api/settings');
}

export function getStudioOutputs(): Promise<StudioOutput[]> {
  return request<StudioOutput[]>('/api/studio/outputs');
}

export function getStudioVideos(): Promise<StudioVideo[]> {
  return request<StudioVideo[]>('/api/studio/videos');
}

export function getStudioVersion(): Promise<{ appVersion: string }> {
  return request<{ appVersion: string }>('/api/version');
}

export function getTranscriptState(clipId: string): Promise<StudioTranscriptState> {
  return request<StudioTranscriptState>(`/api/studio/clips/${encodeURIComponent(clipId)}/transcript-state`);
}

export function getFfmpegDownloadState(): Promise<FfmpegDownloadState> {
  return request<FfmpegDownloadState>('/api/ffmpeg/download');
}

/**
 * 把浏览器里选择的视频/字幕文件交给 /api/studio/import-files（multipart）。
 * 后端会先落盘到 imported-media 再走 ImportPathsAsync，返回 ImportResult。
 */
export async function importStudioFiles(files: File[]): Promise<StudioImportResult> {
  const body = new FormData();
  for (const file of files) body.append('files', file);
  // WebView2 Request.Content omits file-backed multipart bytes (WebView2Feedback#2162).
  const multipart = new Response(body);
  const bytes = await multipart.arrayBuffer();
  return request<StudioImportResult>('/api/studio/import-files', {
    method: 'POST',
    headers: Object.fromEntries(multipart.headers),
    body: bytes,
  });
}

/**
 * 把桌面选择器桥返回的本机绝对路径交给 /api/studio/import-paths（后端 ImportPathsAsync，与 import-files 同一落点）。
 * 只有桌面端会产生真实选中的路径；浏览器流程仍走 importStudioFiles。
 */
export async function importStudioPaths(paths: string[]): Promise<StudioImportResult> {
  return request<StudioImportResult>('/api/studio/import-paths', {
    method: 'POST',
    body: JSON.stringify({ paths }),
  });
}

/** 在 Studio 内触发素材包 / 剪映草稿导出。 */
export function queueStudioOutput(
  clipId: string,
  project: EditorProject,
  kind: 'package' | 'draft',
): Promise<{ id: string }> {
  return request<{ id: string }>(`/api/studio/clips/${encodeURIComponent(clipId)}/${kind}`, {
    method: 'POST',
    body: JSON.stringify(project),
  });
}

/** 素材包 / 剪映草稿的下载地址（同一个端点，后端按 kind 决定文件名）。 */
export function getStudioOutputFileUrl(id: string): string {
  return `${apiBase}/api/studio/packages/${encodeURIComponent(id)}/file`;
}

/* ── B13-2：FilmCraft .fcproj 工程导入 ───────────────────────────── */

/** POST /api/bridge/import 的请求体；targetClipId 是 UI 选中的目标切片（可选）。 */
export interface FcprojImportRequest {
  fcprojPath: string;
  clipMapping?: Record<string, string> | null;
  targetClipId?: string | null;
}

/** 导入成功摘要：projectId 是写入 editor project 的切片 id（可直接打开编辑器）。 */
export interface FcprojImportSummary {
  projectId: string;
  targetClipId: string;
  timelineItems: number;
  durationSeconds: number;
  warnings: string[];
  ignored: Record<string, number>;
  resolvedMedia: Record<string, string>;
}

/**
 * 把 FilmCraft 工程导入为 LiveClipper 的 editor project。
 * 错误体是 { error: { class, message, detail } }，这里拼成可读的 ApiError。
 */
export async function importFcproj(payload: FcprojImportRequest): Promise<FcprojImportSummary> {
  let response: Response;
  try {
    response = await fetch(`${apiBase}/api/bridge/import`, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new ApiError(`无法连接 LiveClipper 后端：${detail}`);
  }

  const text = await response.text().catch(() => '');
  let body: unknown = null;
  if (text.trim()) {
    try {
      body = JSON.parse(text) as unknown;
    } catch {
      body = null;
    }
  }

  if (!response.ok) {
    const structured = (body as {
      error?: { class?: string; message?: string; detail?: string | null };
    } | null)?.error;
    if (structured?.message) {
      const classLabel = structured.class ? `${structured.class} · ` : '';
      const detail = structured.detail ? `（${structured.detail}）` : '';
      throw new ApiError(`${classLabel}${structured.message}${detail}`, response.status);
    }
    throw new ApiError(text.trim().slice(0, 240) || `请求失败（HTTP ${response.status}）`, response.status);
  }

  return body as FcprojImportSummary;
}
