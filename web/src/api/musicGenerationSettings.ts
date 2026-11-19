import { ApiError } from './client';

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

export type MusicGenerationProvider = 'local' | 'custom-http';
export type MusicGenerationAuthMode = 'none' | 'bearer';

/** GET /api/settings/music-generation 的响应；永远不含 key 明文。 */
export interface MusicGenerationSettings {
  provider: MusicGenerationProvider;
  generationUrl: string;
  model: string;
  authMode: MusicGenerationAuthMode;
  hasApiKey: boolean;
}

/**
 * POST 请求体。apiKey 省略 = 保留已保存 key；
 * clearApiKey: true = 明确清除（不得与新 apiKey 同发，也不得在 custom-http + bearer 下使用）。
 */
export interface MusicGenerationSettingsInput {
  provider: MusicGenerationProvider;
  generationUrl: string;
  model: string;
  authMode: MusicGenerationAuthMode;
  apiKey?: string;
  clearApiKey?: true;
}

export interface MusicGenerationSaveResponse {
  saved: true;
  music: MusicGenerationSettings;
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

export function getMusicGenerationSettings(signal?: AbortSignal): Promise<MusicGenerationSettings> {
  return request<MusicGenerationSettings>('/api/settings/music-generation', { signal });
}

export function saveMusicGenerationSettings(
  body: MusicGenerationSettingsInput,
  signal?: AbortSignal,
): Promise<MusicGenerationSaveResponse> {
  return request<MusicGenerationSaveResponse>('/api/settings/music-generation', {
    method: 'POST',
    body: JSON.stringify(body),
    signal,
  });
}
