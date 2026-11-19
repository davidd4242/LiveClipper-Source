import { ApiError } from './client';

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
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
    // AbortController 超时：保留错误类型，交给调用方映射成「超时」分类。
    if (init?.signal?.aborted) {
      const abort = new Error('请求已取消（超时）');
      abort.name = 'AbortError';
      throw abort;
    }
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

export type AiProvider = 'ollama' | 'compatible';

/** GET /api/settings/ai 的响应；密钥只以 hasApiKey 暴露，不下发明文或密文。 */
export interface AiSettings {
  provider: AiProvider;
  endpoint: string;
  model: string;
  hasApiKey: boolean;
  /** 本机 Ollama 实际使用的地址（LIVECLIPPER_OLLAMA_URL 可覆盖）。 */
  ollamaEndpoint: string;
  defaultModel: string;
  settingsVersion: number;
}

export interface AiSettingsPatch {
  provider: AiProvider;
  endpoint: string;
  model: string;
  /** 传空/不传表示保持已保存的密钥。 */
  apiKey?: string;
  clearApiKey?: boolean;
}

/** 测试连接错误分类（A9 P0-4）：ok / model_not_found / network / timeout / invalid / unknown。 */
export type AiTestCode = 'ok' | 'model_not_found' | 'network' | 'timeout' | 'invalid' | 'unknown';

export interface AiTestResult {
  ok: boolean;
  code?: AiTestCode;
  models: string[];
  elapsedMs: number;
  modelAvailable: boolean;
  error?: string | null;
}

/** GET /api/settings 中设置页只读展示需要的字段。 */
export interface SettingsOverview {
  storageRoot: string;
  renderWorkDirectory: string;
  exportOutputDirectory: string;
  ffmpegPath: string;
  pythonPath: string;
  whisperModel: string;
  localOutputRoot: string;
  aiProvider: string;
  aiEndpoint: string;
  aiModel: string;
  settingsVersion: number;
}

export interface AiProviderPreset {
  id: string;
  name: string;
  endpoint: string;
  model: string;
}

/** 在线 OpenAI 兼容服务预设；Endpoint 可直接编辑，自定义时保留当前值。 */
export const COMPATIBLE_PRESETS: AiProviderPreset[] = [
  { id: 'deepseek', name: 'DeepSeek', endpoint: 'https://api.deepseek.com/v1', model: 'deepseek-chat' },
  {
    id: 'dashscope',
    name: '千问 DashScope',
    endpoint: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
    model: 'qwen-plus',
  },
  { id: 'openai', name: 'OpenAI', endpoint: 'https://api.openai.com/v1', model: 'gpt-4o-mini' },
];

/** 本地 Ollama 推荐模型；7B 为默认，机器较弱时可换 3B / 1.5B。 */
export const OLLAMA_MODEL_PRESETS = ['qwen2.5:7b-instruct', 'qwen2.5:3b-instruct', 'qwen2.5:1.5b-instruct'];

export function getAiSettings(): Promise<AiSettings> {
  return requestJson<AiSettings>('/api/settings/ai');
}

export function saveAiSettings(patch: AiSettingsPatch): Promise<{ saved: boolean; ai: AiSettings }> {
  return requestJson<{ saved: boolean; ai: AiSettings }>('/api/settings/ai', {
    method: 'POST',
    body: JSON.stringify(patch),
  });
}

/** 测试连接并返回可用模型列表（「拉取模型列表」复用同一端点）。默认 8 秒客户端超时，与服务端一致。 */
export async function testAiConnection(patch: AiSettingsPatch, timeoutMs = 8000): Promise<AiTestResult> {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await requestJson<AiTestResult>('/api/settings/ai/test', {
      method: 'POST',
      body: JSON.stringify(patch),
      signal: controller.signal,
    });
  } catch (error) {
    if ((error as { name?: string })?.name === 'AbortError' || controller.signal.aborted) {
      return {
        ok: false,
        code: 'timeout',
        models: [],
        elapsedMs: timeoutMs,
        modelAvailable: false,
        error: `连接超时（${Math.round(timeoutMs / 1000)} 秒），请检查地址与网络`,
      };
    }
    throw error;
  } finally {
    window.clearTimeout(timer);
  }
}

export async function getSettingsOverview(): Promise<SettingsOverview> {
  const raw = await requestJson<Partial<SettingsOverview>>('/api/settings');
  return {
    storageRoot: raw.storageRoot ?? '',
    renderWorkDirectory: raw.renderWorkDirectory ?? '',
    exportOutputDirectory: raw.exportOutputDirectory ?? '',
    ffmpegPath: raw.ffmpegPath ?? '',
    pythonPath: raw.pythonPath ?? '',
    whisperModel: raw.whisperModel ?? '',
    localOutputRoot: raw.localOutputRoot ?? '',
    aiProvider: raw.aiProvider ?? '',
    aiEndpoint: raw.aiEndpoint ?? '',
    aiModel: raw.aiModel ?? '',
    settingsVersion: raw.settingsVersion ?? 0,
  };
}

/* ── 环境 / 自动切片：PATCH 局部保存（task-154） ───────────────────────── */

/** 设置页「自动切片与本地环境」可编辑字段（PATCH /api/settings 可写字段子集）。 */
export interface SettingsEnvValues {
  autoHighlight: boolean;
  autoHighlightCooldownSeconds: number;
  highlightPreRollSeconds: number;
  highlightPostRollSeconds: number;
  storageRoot: string;
  renderWorkDirectory: string;
  exportOutputDirectory: string;
  whisperModel: string;
  ffmpegPath: string;
  pythonPath: string;
}

export interface SettingsSaveResponse {
  saved: boolean;
  restartRequired: boolean;
}

/** PATCH 校验失败（400）：fields 为非法字段名（camelCase，对应 SettingsEnvValues 的键）。 */
export class SettingsPatchError extends Error {
  readonly fields: string[];

  constructor(message: string, fields: string[] = []) {
    super(message);
    this.name = 'SettingsPatchError';
    this.fields = fields;
  }
}

/** 转写模型可选项；与后端 WhisperModel 字段一致。 */
export const WHISPER_MODELS = ['tiny', 'base', 'small', 'medium', 'large-v3', 'large-v3-turbo'] as const;

/** 需要重启才生效的字段（与后端 restartRequired 判定一致）。 */
export const RESTART_FIELDS = ['storageRoot', 'whisperModel', 'ffmpegPath', 'pythonPath'] as const;

/** 读取设置页环境/自动切片字段（GET /api/settings，只取本页需要的子集）。 */
export async function getSettingsEnv(): Promise<SettingsEnvValues> {
  const raw = await requestJson<Partial<SettingsEnvValues>>('/api/settings');
  return {
    autoHighlight: raw.autoHighlight ?? true,
    autoHighlightCooldownSeconds: raw.autoHighlightCooldownSeconds ?? 60,
    highlightPreRollSeconds: raw.highlightPreRollSeconds ?? 200,
    highlightPostRollSeconds: raw.highlightPostRollSeconds ?? 200,
    storageRoot: raw.storageRoot ?? '',
    renderWorkDirectory: raw.renderWorkDirectory ?? '',
    exportOutputDirectory: raw.exportOutputDirectory ?? '',
    whisperModel: raw.whisperModel ?? 'large-v3-turbo',
    ffmpegPath: raw.ffmpegPath ?? '',
    pythonPath: raw.pythonPath ?? '',
  };
}

/**
 * PATCH /api/settings：只提交出现过的字段，未出现字段保持原值。
 * 成功返回 restartRequired；400 时抛 SettingsPatchError 并带 fields（用于逐字段展示）。
 */
export async function patchSettings(partial: Partial<SettingsEnvValues>): Promise<SettingsSaveResponse> {
  let response: Response;
  try {
    response = await fetch(`${apiBase}/api/settings`, {
      method: 'PATCH',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify(partial),
    });
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new ApiError(`无法连接 LiveClipper 后端：${detail}`);
  }

  if (!response.ok) {
    let fields: string[] = [];
    let message = `保存失败（HTTP ${response.status}）`;
    try {
      const body = (await response.json()) as { error?: unknown };
      const raised = body.error as { class?: string; fields?: unknown } | string | undefined;
      if (raised && typeof raised === 'object') {
        fields = Array.isArray(raised.fields) ? raised.fields.filter((item): item is string => typeof item === 'string') : [];
        message = fields.length > 0 ? `设置值无效：${fields.join('、')}` : '设置值无效，请检查输入';
      } else if (typeof raised === 'string' && raised.trim()) {
        message = raised.trim().slice(0, 240);
      }
    } catch {
      // 非 JSON 错误体：保留 HTTP 状态文案。
    }
    throw new SettingsPatchError(message, fields);
  }

  return (await response.json()) as SettingsSaveResponse;
}

export interface OllamaModelDownloadState {
  state: 'idle' | 'downloading' | 'completed' | 'cancelled' | 'failed';
  model: string;
  progress: number;
  detail: string | null;
}
export function getOllamaModelDownload(): Promise<OllamaModelDownloadState> {
  return requestJson<OllamaModelDownloadState>('/api/ai/download');
}
export function startOllamaModelDownload(model: string): Promise<OllamaModelDownloadState> {
  return requestJson<OllamaModelDownloadState>('/api/ai/download', { method: 'POST', body: JSON.stringify({ model }) });
}
export function cancelOllamaModelDownload(): Promise<{ cancelled: boolean }> {
  return requestJson<{ cancelled: boolean }>('/api/ai/download/cancel', { method: 'POST' });
}
