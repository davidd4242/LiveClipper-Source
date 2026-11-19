import { getTranscriptState } from '../../api/studio';
import type { StudioTranscriptState } from '../../api/studio';
import type { AiConnectionBody } from '../editor/aiAdvice';

export interface RoughCutRange { start: number; end: number }
export interface RoughCutPlan {
  start: number;
  end: number;
  title: string;
  summary: string;
  keep: number[];
  hooks: number[];
  reasons: string[];
  source: string;
  transcriptCacheKey: string;
  ranges: RoughCutRange[];
}

export async function generateRoughCutPlan(clipId: string, input: RoughCutRange & { connection: AiConnectionBody }, signal?: AbortSignal): Promise<RoughCutPlan> {
  const response = await fetch(`${apiBase}/api/studio/clips/${encodeURIComponent(clipId)}/cut-plan`, {
    method: 'POST', credentials: 'same-origin', headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify(input), signal,
  });
  const body: unknown = await response.json();
  if (!response.ok) {
    const error = record(body) ? body.error : null;
    throw new AutoPlanApiError(response.status, record(error) && typeof error.code === 'string' ? error.code : 'cut-plan-failed',
      typeof error === 'string' ? error : record(error) && typeof error.message === 'string' ? error.message : `粗剪请求失败（${response.status}）。`);
  }
  if (!record(body) || typeof body.start !== 'number' || typeof body.end !== 'number'
    || !['title', 'summary', 'source', 'transcriptCacheKey'].every(key => typeof body[key] === 'string')
    || !Array.isArray(body.ranges) || !body.ranges.every(range => record(range)
      && typeof range.start === 'number' && Number.isFinite(range.start) && range.start >= input.start
      && typeof range.end === 'number' && Number.isFinite(range.end) && range.end > range.start && range.end <= input.end)
    || !Array.isArray(body.keep) || !body.keep.every(Number.isSafeInteger)
    || !Array.isArray(body.hooks) || !body.hooks.every(Number.isSafeInteger)
    || !Array.isArray(body.reasons) || !body.reasons.every(reason => typeof reason === 'string')) {
    throw new AutoPlanApiError(200, 'invalid-cut-plan', '粗剪响应缺少有效 ranges、source 或 transcriptCacheKey，请重新生成。');
  }
  return body as unknown as RoughCutPlan;
}

// The endpoint already returns this field; the shared Studio type predates it.
export async function readRoughCutTranscriptState(clipId: string): Promise<StudioTranscriptState & { transcriptCacheKey: string }> {
  const state = await getTranscriptState(clipId) as StudioTranscriptState & { transcriptCacheKey: string };
  if (state.clipId !== clipId || typeof state.transcriptCacheKey !== 'string' || !state.transcriptCacheKey) {
    throw new Error('字幕状态缺少 transcriptCacheKey，请更新接口后重新生成。');
  }
  return state;
}

export type AutoPlanTimelineItem = {
  id: string;
  clipId: string;
  start: number;
  end: number;
  [key: string]: unknown;
};

export type AutoPlanChange = {
  kind: string;
  itemId: string;
  before: AutoPlanTimelineItem | null;
  after: AutoPlanTimelineItem | null;
  reason: string;
};

export type AutoPlanSegmentReason = {
  segmentIndex: number;
  start: number;
  end: number;
  keep: boolean;
  reason: string;
};

export type AutoPlanProposal = {
  proposalId: string;
  clipId: string;
  epoch: string;
  baseRevision: number;
  baseHash: string;
  transcriptHash: string;
  source: string;
  timeline: AutoPlanTimelineItem[];
  changes: AutoPlanChange[];
  segmentReasons: AutoPlanSegmentReason[];
  createdAt: string;
};

export type AutoPlanApplyResult = {
  applied: boolean;
  conflict: boolean;
  code: string;
  revision: number;
};

export class AutoPlanApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = 'AutoPlanApiError';
    this.status = status;
    this.code = code;
  }
}

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

function timelineItem(value: unknown): value is AutoPlanTimelineItem {
  return record(value) && typeof value.id === 'string' && typeof value.clipId === 'string'
    && typeof value.start === 'number' && Number.isFinite(value.start)
    && typeof value.end === 'number' && Number.isFinite(value.end) && value.start >= 0 && value.end > value.start;
}

async function postAutoPlan(clipId: string, action: 'proposal' | 'apply', proposal?: AutoPlanProposal, signal?: AbortSignal): Promise<unknown> {
  const response = await fetch(`${apiBase}/api/studio/clips/${encodeURIComponent(clipId)}/auto-plan/${action}`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { Accept: 'application/json', ...(proposal ? { 'Content-Type': 'application/json' } : {}) },
    signal,
    ...(proposal ? { body: JSON.stringify(proposal) } : {}),
  });
  const body: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const error = record(body) && record(body.error) ? body.error : null;
    const code = typeof error?.code === 'string' ? error.code : record(body) && typeof body.code === 'string' ? body.code : 'request-failed';
    const message = typeof error?.message === 'string' ? error.message
      : response.status === 404 ? '工程不存在或已被移除。'
        : response.status === 400 ? '建议内容无效，请重新生成。' : `建议请求失败（${response.status}）。`;
    throw new AutoPlanApiError(response.status, code, message);
  }
  return body;
}

export async function generateAutoPlanProposal(clipId: string, signal?: AbortSignal): Promise<AutoPlanProposal> {
  const body = await postAutoPlan(clipId, 'proposal', undefined, signal);
  if (!record(body) || !['proposalId', 'clipId', 'epoch', 'baseHash', 'transcriptHash', 'source', 'createdAt'].every(key => typeof body[key] === 'string')
    || !Number.isSafeInteger(body.baseRevision) || Number(body.baseRevision) < 0
    || !Array.isArray(body.timeline) || !body.timeline.every(timelineItem)
    || !Array.isArray(body.changes) || !body.changes.every(change => record(change)
      && typeof change.kind === 'string' && typeof change.itemId === 'string' && typeof change.reason === 'string'
      && (change.before === null || timelineItem(change.before)) && (change.after === null || timelineItem(change.after)))
    || !Array.isArray(body.segmentReasons) || !body.segmentReasons.every(segment => record(segment)
      && Number.isSafeInteger(segment.segmentIndex) && typeof segment.start === 'number' && Number.isFinite(segment.start)
      && typeof segment.end === 'number' && Number.isFinite(segment.end) && typeof segment.keep === 'boolean' && typeof segment.reason === 'string')) {
    throw new AutoPlanApiError(200, 'invalid-response', '建议响应格式无效，请重新生成。');
  }
  return body as AutoPlanProposal;
}

export async function applyAutoPlanProposal(clipId: string, proposal: AutoPlanProposal, signal?: AbortSignal): Promise<AutoPlanApplyResult> {
  const body = await postAutoPlan(clipId, 'apply', proposal, signal);
  if (!record(body) || body.applied !== true || body.conflict !== false || body.code !== 'applied' || !Number.isSafeInteger(body.revision)) {
    throw new AutoPlanApiError(200, 'invalid-response', '应用结果未确认，请重新载入工程核对。');
  }
  return body as AutoPlanApplyResult;
}
