import type { JobDto, TranscriptPreview, TranscriptSegment } from '../../api/types';
import { createEditorJsonRequest } from '../editor/api/commandSessionClient';
import { setClipCaptions } from '../editor/captionOverrides';
import type { ExportSnapshot } from './exportBridge';

/**
 * R82 第一批：右侧栏「转写字幕」面板用到的真实接口。
 *
 * 全部路径/响应形状来自 src/LiveClipper.App（不要凭记忆加字段）：
 * - GET  /api/studio/clips/{id}/transcript-state   Program.cs:855-861
 * - GET  /api/clips/{id}/transcript                Program.cs:1329-1334（404 = 尚未生成）
 * - POST /api/studio/clips/{id}/transcribe         StudioEndpoints.cs（空 body，queued/jobId/disposition）
 * - POST /api/transcribe/{eventId}/restart         Program.cs（queued/restarted/jobId/disposition）
 * - GET  /api/jobs                                 StudioEndpoints.cs:466-476（无 createdAt）
 *
 * 新后端回 jobId/disposition，仍需用 jobs 验证归属；旧后端回退到发前后 id 差集。
 * 不能把旧的 completed 行当成本次成功。
 */

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

export class TranscriptApiError extends Error {
  readonly status: number;
  readonly code: string | null;

  constructor(message: string, status = 0, code: string | null = null) {
    super(message);
    this.name = 'TranscriptApiError';
    this.status = status;
    this.code = code;
  }
}

/** GET /api/studio/clips/{id}/transcript-state 的字段（后端实际返回的项目见上）。 */
export interface TranscriptStateDto {
  clipId: string;
  transcriptVersion: string;
  transcriptSource?: string | null;
  transcriptDigest?: string | null;
  transcriptCacheKey?: string | null;
  autoStatus?: string | null;
  autoUpdatedAt?: string | null;
  autoKeepCount?: number;
  autoTrimmedSeconds?: number;
}

export interface TranscriptionAccepted {
  queued: boolean;
  restarted?: boolean;
  /** 仅作为候选 id；仍须在 jobs 中验证 eventId/type。缺失时兼容旧后端。 */
  jobId?: string;
  disposition?: 'queued' | 'already-pending';
}

function isAbort(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { name?: unknown }).name === 'AbortError';
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${apiBase}${path}`, {
      ...init,
      headers: { Accept: 'application/json', ...(init?.headers ?? {}) },
    });
  } catch (error) {
    if (isAbort(error)) throw error;
    const detail = error instanceof Error ? error.message : String(error);
    throw new TranscriptApiError(`无法连接 LiveClipper 后端：${detail}`);
  }
  const text = await response.text().catch(() => '');
  let body: unknown = null;
  if (text.trim()) {
    try {
      body = JSON.parse(text);
    } catch {
      body = null;
    }
  }
  if (!response.ok) {
    const record = typeof body === 'object' && body !== null && !Array.isArray(body)
      ? (body as Record<string, unknown>)
      : null;
    const rawError = record ? record['error'] : undefined;
    const message = typeof rawError === 'string' && rawError.trim()
      ? rawError
      : `请求失败（HTTP ${response.status}）`;
    const code = record && typeof record['code'] === 'string' ? record['code'] : null;
    throw new TranscriptApiError(message, response.status, code);
  }
  return body as T;
}

export function getTranscriptState(clipId: string, signal?: AbortSignal): Promise<TranscriptStateDto> {
  return requestJson<TranscriptStateDto>(
    `/api/studio/clips/${encodeURIComponent(clipId)}/transcript-state`,
    { signal },
  );
}

/** 404 是正常状态（还没有字幕），返回 null；其余错误原样抛出。 */
export async function getClipTranscript(clipId: string, signal?: AbortSignal): Promise<TranscriptPreview | null> {
  try {
    return await requestJson<TranscriptPreview>(`/api/clips/${encodeURIComponent(clipId)}/transcript`, { signal });
  } catch (error) {
    if (error instanceof TranscriptApiError && error.status === 404) return null;
    throw error;
  }
}

export function startTranscription(clipId: string, signal?: AbortSignal): Promise<TranscriptionAccepted> {
  return requestJson<TranscriptionAccepted>(
    `/api/studio/clips/${encodeURIComponent(clipId)}/transcribe`,
    { method: 'POST', signal },
  );
}

export function restartTranscription(eventId: string, signal?: AbortSignal): Promise<TranscriptionAccepted> {
  return requestJson<TranscriptionAccepted>(
    `/api/transcribe/${encodeURIComponent(eventId)}/restart`,
    { method: 'POST', signal },
  );
}

/** 精修写工程覆盖，不覆盖 ASR 原文；与宿主保存使用同一修订/纪元协议。 */
export async function saveClipCaptions(snapshot: ExportSnapshot, segments: readonly TranscriptSegment[] | null, signal?: AbortSignal): Promise<void> {
  const request = createEditorJsonRequest();
  const result = await request<{ saved: boolean; revision?: number; epoch?: string }>(
    `/api/studio/clips/${encodeURIComponent(snapshot.clipId)}/editor`,
    { method: 'PUT', signal,
      headers: { 'X-Base-Revision': String(snapshot.serverRevision), 'X-Editor-Epoch': snapshot.epoch },
      body: JSON.stringify(setClipCaptions(snapshot.project, snapshot.clipId, segments)) },
  );
  if (result.saved !== true || !Number.isSafeInteger(result.revision) || result.revision! <= snapshot.serverRevision
    || (result.epoch !== undefined && result.epoch !== snapshot.epoch)) {
    throw new Error('服务器尚未确认字幕保存，请保留编辑内容并重新载入后核对。');
  }
}

export function transcriptSrtUrl(clipId: string): string {
  return `${apiBase}/api/clips/${encodeURIComponent(clipId)}/transcript/export?format=srt`;
}

export const TRANSCRIPTION_JOB_TYPE = 'transcribe';

export function isTranscriptionJob(job: JobDto, eventId: string): boolean {
  return job.type === TRANSCRIPTION_JOB_TYPE && job.eventId === eventId;
}

/**
 * 任务归属只看 eventId（transcribe job 没有 clipId），并按服务端返回顺序取第一条。
 * 服务端已按 created_at DESC 返回，JobDto 没有 createdAt，这里不排序、不补字段。
 */
export function pickTranscriptionJob(jobs: JobDto[], eventId: string): JobDto | null {
  for (const job of jobs) {
    if (isTranscriptionJob(job, eventId)) return job;
  }
  return null;
}

/** 发请求之后新出现的同事件任务（用于把「请求已接受」关联到真正的 job）。 */
export function pickNewTranscriptionJob(
  jobs: JobDto[],
  eventId: string,
  before: ReadonlySet<string>,
): JobDto | null {
  for (const job of jobs) {
    if (isTranscriptionJob(job, eventId) && !before.has(job.id)) return job;
  }
  return null;
}

/**
 * 只把 pending/running 当进行中：retried 是真实存在但语义不明的状态，
 * 不当作 running（避免伪进度），未知状态按原值展示。
 */
export function isActiveTranscriptionStatus(status: string | null | undefined): boolean {
  return status === 'pending' || status === 'running';
}

export function isActiveTranscriptionJob(job: JobDto | null): boolean {
  return job !== null && isActiveTranscriptionStatus(job.status);
}

/**
 * 重复触发的唯一已知信号：LiveCoordinator.cs:484 抛 InvalidOperationException("该高光正在转写")，
 * 由 Program.cs:334-343 全局处理成 500 {error:"该高光正在转写",code:"internal_error"}。
 * 只认这条精确文本（码为 internal_error 或缺失）；其它 500 一律原样报错，不当成功。
 */
export function isTranscribingConflict(error: unknown): boolean {
  if (!(error instanceof TranscriptApiError)) return false;
  if (error.status !== 500) return false;
  if (error.code !== null && error.code !== 'internal_error') return false;
  return error.message.trim() === '该高光正在转写';
}

export const TRANSCRIBING_CONFLICT_TEXT = '该素材事件已有一个转写任务在排队或进行中。';

/** 真实进度才展示；不明就返回 null，不编造百分比。 */
export function jobProgressPercent(job: JobDto | null): number | null {
  if (!job || job.status !== 'running') return null;
  const value = job.progress;
  if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0 || value > 1) return null;
  return Math.round(value * 100);
}

/** 秒 → mm:ss（超过一小时才带小时）。 */
export function formatTranscriptClock(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '00:00';
  const total = Math.floor(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = total % 60;
  const mm = String(minutes).padStart(2, '0');
  const ss = String(rest).padStart(2, '0');
  return hours > 0 ? `${hours}:${mm}:${ss}` : `${mm}:${ss}`;
}
