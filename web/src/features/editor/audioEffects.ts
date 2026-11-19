import type { AudioEffectInstance, AudioEffectType } from '../../api/types';

/**
 * 音频效果 v1 冻结参数表（与 docs/filmcraft-parity-next5.md §1.5.4 一致，键名对齐后端 camelCase）。
 * 效果控件面板与效果库共用这一份，避免两处参数范围漂移。
 */
export interface AudioEffectParamSpec {
  key: string;
  label: string;
  unit: string;
  min: number;
  max: number;
  step: number;
  defaultValue: number;
  digits: number;
}

export interface AudioEffectSpec {
  label: string;
  filter: string;
  summary: string;
  params: AudioEffectParamSpec[];
}

export const AUDIO_EFFECT_SPECS: Record<AudioEffectType, AudioEffectSpec> = {
  compressor: {
    label: '压缩器',
    filter: 'acompressor',
    summary: '压低超过阈值的峰值，提升整体可听响度，适合人声与现场录音。',
    params: [
      { key: 'thresholdDb', label: '阈值', unit: 'dB', min: -60, max: 0, step: 0.5, defaultValue: -20, digits: 1 },
      { key: 'ratio', label: '比率', unit: ':1', min: 1, max: 20, step: 0.1, defaultValue: 4, digits: 1 },
      { key: 'attackMs', label: '启动', unit: 'ms', min: 0.1, max: 300, step: 1, defaultValue: 10, digits: 0 },
      { key: 'releaseMs', label: '释放', unit: 'ms', min: 1, max: 3000, step: 1, defaultValue: 100, digits: 0 },
      { key: 'makeupDb', label: '补偿增益', unit: 'dB', min: 0, max: 24, step: 0.5, defaultValue: 0, digits: 1 },
      { key: 'kneeDb', label: '拐点', unit: 'dB', min: 1, max: 8, step: 0.5, defaultValue: 6, digits: 1 },
    ],
  },
  noiseGate: {
    label: '噪声门',
    filter: 'agate',
    summary: '低于阈值的底噪静音，减少房间声与呼吸声，适合对白与旁白。',
    params: [
      { key: 'thresholdDb', label: '阈值', unit: 'dB', min: -80, max: 0, step: 0.5, defaultValue: -50, digits: 1 },
      { key: 'ratio', label: '比率', unit: ':1', min: 1, max: 20, step: 0.1, defaultValue: 2, digits: 1 },
      { key: 'attackMs', label: '启动', unit: 'ms', min: 0.1, max: 100, step: 1, defaultValue: 2, digits: 0 },
      { key: 'releaseMs', label: '释放', unit: 'ms', min: 1, max: 3000, step: 1, defaultValue: 100, digits: 0 },
      { key: 'rangeDb', label: '衰减范围', unit: 'dB', min: 0, max: 60, step: 0.5, defaultValue: 24, digits: 1 },
    ],
  },
  parametric_eq: {
    label: '参数均衡',
    filter: 'anequalizer',
    summary: '八段参数均衡：高通、低架、五个钟形、高架、低通，外加总增益。参数由专用控件写入 35 个键，后端按同一份键名映射到 FFmpeg。',
    // 参数不由通用控件渲染：35 个键的 min/max/单位都在 ParametricEqControls 里维护。
    params: [],
  },
};

export const AUDIO_EFFECT_TYPES: AudioEffectType[] = ['compressor', 'noiseGate', 'parametric_eq'];
export const MAX_AUDIO_EFFECTS = 3;

export function clampAudioParam(spec: AudioEffectParamSpec, value: number): number {
  if (!Number.isFinite(value)) return spec.defaultValue;
  const clamped = Math.min(spec.max, Math.max(spec.min, value));
  const factor = 10 ** spec.digits;
  return Math.round(clamped * factor) / factor;
}

export function defaultAudioParams(type: AudioEffectType): Record<string, number> {
  const params: Record<string, number> = {};
  for (const spec of AUDIO_EFFECT_SPECS[type].params) params[spec.key] = spec.defaultValue;
  return params;
}

/** 生成一个带唯一 id 的新效果实例（默认启用、参数为冻结默认值）。 */
export function createAudioEffect(type: AudioEffectType, existing: AudioEffectInstance[]): AudioEffectInstance {
  const used = new Set(existing.map((effect) => effect.id));
  let index = existing.length + 1;
  let id = `fx-${type}-${index}`;
  while (used.has(id)) {
    index += 1;
    id = `fx-${type}-${index}`;
  }
  return { id, type, enabled: true, params: defaultAudioParams(type) };
}

/** 归一化效果链：未知 type 丢弃、参数按冻结表 clamp、缺省补默认、v1 最多 3 个。 */
export function normalizeAudioEffects(item: { effects?: AudioEffectInstance[] | null }): AudioEffectInstance[] {
  const source = Array.isArray(item.effects) ? item.effects : [];
  const effects: AudioEffectInstance[] = [];
  for (const entry of source) {
    if (!entry || !(AUDIO_EFFECT_TYPES as string[]).includes(entry.type)) continue;
    const type = entry.type as AudioEffectType;
    const params: Record<string, number> = {};
    const raw = entry.params ?? {};
    for (const spec of AUDIO_EFFECT_SPECS[type].params) {
      const value = raw[spec.key];
      params[spec.key] = clampAudioParam(spec, Number.isFinite(value) ? Number(value) : spec.defaultValue);
    }
    // parametric_eq 的参数表是空的（35 个键由 ParametricEqControls 维护），
    // 这里必须原样保留，否则归一化一次就把用户拖的 EQ 全清掉。
    if (AUDIO_EFFECT_SPECS[type].params.length === 0) {
      for (const [key, value] of Object.entries(raw)) {
        if (Number.isFinite(value)) params[key] = Number(value);
      }
    }
    effects.push({
      id: entry.id || `fx-${type}-${effects.length + 1}`,
      type,
      enabled: entry.enabled !== false,
      params,
    });
    if (effects.length >= MAX_AUDIO_EFFECTS) break;
  }
  return effects;
}
