import type { AiAdvice } from '../../api/types';
import type { AiSettings } from '../../api/settings';

/** 面板直接消费的归一化结果：空白字符串与空数组一律丢掉，避免渲染出空标题。 */
export interface AdviceView {
  summary: string;
  captions: string[];
  tips: string[];
  empty: boolean;
}

const cleanList = (list?: string[] | null): string[] =>
  (list ?? []).map((item) => (item ?? '').trim()).filter(Boolean);

export function adviceView(advice?: AiAdvice | null): AdviceView {
  const summary = (advice?.summary ?? '').trim();
  const captions = cleanList(advice?.captions);
  const tips = cleanList(advice?.tips);
  return { summary, captions, tips, empty: !summary && captions.length === 0 && tips.length === 0 };
}

export interface AiConnectionBody {
  provider: 'ollama' | 'compatible';
  endpoint: string;
  model: string;
}

/**
 * 把设置页的 AI 配置转成请求体。密钥不在响应里（只回 hasApiKey），
 * 服务端会在 provider 与已保存设置一致时补上密钥，前端不能也不需要持有它。
 */
export function adviceConnection(settings: AiSettings | null | undefined): AiConnectionBody | null {
  if (!settings) return null;
  const provider: AiConnectionBody['provider'] = settings.provider === 'compatible' ? 'compatible' : 'ollama';
  const endpoint = (
    provider === 'ollama'
      ? settings.endpoint || settings.ollamaEndpoint || ''
      : settings.endpoint || ''
  ).trim();
  const model = (settings.model || settings.defaultModel || '').trim();
  if (!model) return null;
  if (provider === 'compatible' && !endpoint) return null;
  return { provider, endpoint, model };
}
