import type { MusicGenerateInput, MusicJobState, MusicLibraryAsset, MusicStatus } from '../../api/music';

export type MusicApi = ReturnType<typeof createEditorNextMusicApi>;
export class MusicRequestError extends Error {
  constructor(message: string, public readonly status: number) { super(message); }
}

export function createEditorNextMusicApi(base = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, ''), transport: typeof fetch = fetch) {
  async function request<T>(path: string, signal: AbortSignal, input?: unknown): Promise<T> {
    const response = await transport(`${base}${path}`, { signal, method: input === undefined ? 'GET' : 'POST',
      headers: { Accept: 'application/json', ...(input === undefined ? {} : { 'Content-Type': 'application/json' }) },
      ...(input === undefined ? {} : { body: JSON.stringify(input) }) });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { error?: unknown } | null;
      throw new MusicRequestError(typeof body?.error === 'string' ? body.error : `音乐请求失败（HTTP ${response.status}）`, response.status);
    }
    return response.json() as Promise<T>;
  }
  const getAudioFileUrl = (id: string) => `${base}/api/assets/${encodeURIComponent(id)}/file`;
  return {
    getMusicAssets: (signal: AbortSignal) => request<MusicLibraryAsset[]>('/api/music/assets', signal),
    getMusicStatus: (signal: AbortSignal) => request<MusicStatus>('/api/music/status', signal),
    startMusicGeneration: (input: MusicGenerateInput, signal: AbortSignal) => request<MusicJobState>('/api/music/generate', signal, input),
    getMusicJob: (id: string, signal: AbortSignal) => request<MusicJobState>(`/api/music/jobs/${encodeURIComponent(id)}`, signal),
    getAudioFileUrl,
    async checkAudioFile(id: string, signal: AbortSignal): Promise<string> {
      const url = getAudioFileUrl(id);
      const response = await transport(url, { signal, headers: { Range: 'bytes=0-0' }, cache: 'no-store' });
      if (!response.ok || ![200, 206].includes(response.status)) {
        void response.body?.cancel();
        throw new MusicRequestError(response.status === 404 ? '音频文件不存在，未应用配乐。' : `音频文件不可读（HTTP ${response.status}），未应用配乐。`, response.status);
      }
      // A success header is insufficient: reject an empty/missing file before mutating the engine.
      const reader = response.body?.getReader();
      if (!reader) throw new MusicRequestError('音频文件没有可读取内容，未应用配乐。', response.status);
      try {
        const chunk = await reader.read();
        if (!chunk.value?.byteLength) throw new MusicRequestError('音频文件为空，未应用配乐。', response.status);
      } finally { await reader.cancel().catch(() => undefined); }
      return url;
    },
  };
}

export function musicGenerationBlock(status: MusicStatus | null, input: MusicGenerateInput): string | null {
  if (!status) return '尚未读取音乐模型状态。';
  const online = input.provider === 'custom-http';
  // 线上来源：只校验 httpConfigured、四字段与时长；跳过本地 models/installed/Python/download。
  if (online) {
    if (status.httpConfigured !== true) return '线上 API 尚未配置，请先到设置填写生成地址、模型名与 API key。';
  } else {
    if (typeof status.allowDownload !== 'boolean' || input.allowDownload !== status.allowDownload) return '模型下载授权状态未知或已变化，请刷新模型状态。';
    const model = status.models.find(m => m.id === input.modelId);
    if (!model) return '请选择状态接口提供的模型。';
    if (!model.installed) return '所选模型尚未安装；本面板不会自动下载模型。';
    if (!model.builtIn && !status.pythonAvailable) return 'Python 不可用，所选模型无法生成音乐。';
    if (status.download && !['idle', 'completed', 'cancelled', 'failed'].includes(status.download.state)) {
      return `模型下载状态：${status.download.state}；当前不能生成。`;
    }
  }
  for (const [field, label] of [['genre', '流派'], ['style', '风格'], ['content', '内容'], ['theme', '主题']] as const) {
    if (typeof input[field] !== 'string' || !input[field].trim()) return `请填写${label}。`;
    if (input[field].length > 500) return `${label}不能超过 500 字符。`;
  }
  if (!Number.isFinite(input.durationSeconds) || input.durationSeconds < 5 || input.durationSeconds > 180) return '生成时长必须为 5–180 秒。';
  if (!online && typeof input.allowDownload !== 'boolean') return '下载授权状态未知，不能生成。';
  return null;
}

export const editorNextMusicApi = createEditorNextMusicApi();
