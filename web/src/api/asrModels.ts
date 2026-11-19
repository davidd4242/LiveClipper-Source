import { ApiError } from './client';

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

export type AsrPrepareStatus = 'idle' | 'preparing' | 'completed' | 'failed' | 'cancelled';
export type AsrPrepareSource = 'auto' | 'official' | 'mirror';

export interface AsrPrepareState {
  status: AsrPrepareStatus | string;
  model: string;
  revision: string | null;
  source: string;
  phase: string | null;
  detail: string | null;
  modelPath: string | null;
}

export interface AsrModelResponse {
  model: string;
  state: AsrPrepareState;
}

export interface AsrPrepareAccepted {
  accepted?: boolean;
  model?: string;
  revision?: string | null;
  state?: AsrPrepareState;
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
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    const detail = error instanceof Error ? error.message : String(error);
    throw new ApiError(`无法连接 LiveClipper 后端：${detail}`);
  }
  if (!response.ok) {
    let message = `请求失败（HTTP ${response.status}）`;
    const text = await response.text().catch(() => '');
    if (text.trim()) {
      try {
        const body = JSON.parse(text) as { error?: unknown };
        if (typeof body.error === 'string' && body.error.trim()) message = body.error;
        else if (body.error === undefined) message = text.trim().slice(0, 240);
      } catch {
        message = text.trim().slice(0, 240);
      }
    }
    throw new ApiError(message, response.status);
  }
  return (await response.json()) as T;
}

export function getAsrModel(signal?: AbortSignal): Promise<AsrModelResponse> {
  return request<AsrModelResponse>('/api/asr/model', { signal });
}

export function prepareAsrModel(
  body: { source: AsrPrepareSource; revision?: string },
  signal?: AbortSignal,
): Promise<AsrPrepareAccepted> {
  return request<AsrPrepareAccepted>('/api/asr/model/prepare', {
    method: 'POST',
    body: JSON.stringify(body),
    signal,
  });
}

export function cancelAsrModelPrepare(signal?: AbortSignal): Promise<{ cancelRequested?: boolean }> {
  return request<{ cancelRequested?: boolean }>('/api/asr/model/prepare/cancel', {
    method: 'POST',
    signal,
  });
}
