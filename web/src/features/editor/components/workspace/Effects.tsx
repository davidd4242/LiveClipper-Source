import type { ChangeEvent } from 'react';

import { PARAMETRIC_EQ_SPECS, type ParametricEqSpec } from '../../eqSpecs';

export type { ParametricEqSpec } from '../../eqSpecs';

export interface ParametricEqAudio {
  specs(id: string): readonly ParametricEqSpec[];
}


export { PARAMETRIC_EQ_SPECS } from '../../eqSpecs';

export type LegacyEqMapping = { old: string; target: string | null; reason?: string };

// Deliberate slot migration, not string replacement or fuzzy matching. A ninth (mid) band is new.
export const PARAMETRIC_EQ_LEGACY_MAP: readonly LegacyEqMapping[] = [
  { old: 'b1.on', target: 'hp_on' },
  { old: 'b1.type', target: null, reason: '高通类型固定；旧滤波器类型引擎不支持，不参与渲染' },
  { old: 'b1.freq', target: 'hp_freq' },
  { old: 'b1.gain', target: null, reason: '高通无独立增益；引擎不支持，不参与渲染' },
  { old: 'b1.q', target: null, reason: '高通采用斜率选项；旧 Q 引擎不支持，不参与渲染' },
  { old: 'b2.on', target: 'low_on' },
  { old: 'b2.type', target: null, reason: '低架类型固定；旧滤波器类型引擎不支持，不参与渲染' },
  { old: 'b2.freq', target: 'low_freq' },
  { old: 'b2.gain', target: 'low_gain' },
  { old: 'b2.q', target: 'low_q' },
  { old: 'b3.on', target: 'b1_on' },
  { old: 'b3.type', target: null, reason: 'Band 1 固定峰值；旧滤波器类型引擎不支持，不参与渲染' },
  { old: 'b3.freq', target: 'b1_freq' },
  { old: 'b3.gain', target: 'b1_gain' },
  { old: 'b3.q', target: 'b1_q' },
  { old: 'b4.on', target: 'b2_on' },
  { old: 'b4.type', target: null, reason: 'Band 2 固定峰值；旧滤波器类型引擎不支持，不参与渲染' },
  { old: 'b4.freq', target: 'b2_freq' },
  { old: 'b4.gain', target: 'b2_gain' },
  { old: 'b4.q', target: 'b2_q' },
  { old: 'b5.on', target: 'b4_on' },
  { old: 'b5.type', target: null, reason: 'Band 4 固定峰值；旧滤波器类型引擎不支持，不参与渲染' },
  { old: 'b5.freq', target: 'b4_freq' },
  { old: 'b5.gain', target: 'b4_gain' },
  { old: 'b5.q', target: 'b4_q' },
  { old: 'b6.on', target: 'b5_on' },
  { old: 'b6.type', target: null, reason: 'Band 5 固定峰值；旧滤波器类型引擎不支持，不参与渲染' },
  { old: 'b6.freq', target: 'b5_freq' },
  { old: 'b6.gain', target: 'b5_gain' },
  { old: 'b6.q', target: 'b5_q' },
  { old: 'b7.on', target: 'high_on' },
  { old: 'b7.type', target: null, reason: '高架类型固定；旧滤波器类型引擎不支持，不参与渲染' },
  { old: 'b7.freq', target: 'high_freq' },
  { old: 'b7.gain', target: 'high_gain' },
  { old: 'b7.q', target: 'high_q' },
  { old: 'b8.on', target: 'lp_on' },
  { old: 'b8.type', target: null, reason: '低通类型固定；旧滤波器类型引擎不支持，不参与渲染' },
  { old: 'b8.freq', target: 'lp_freq' },
  { old: 'b8.gain', target: null, reason: '低通无独立增益；引擎不支持，不参与渲染' },
  { old: 'b8.q', target: null, reason: '低通采用斜率选项；旧 Q 引擎不支持，不参与渲染' },
  { old: 'output', target: 'master_gain' },
];

export function parametricEqSpecs(audio: ParametricEqAudio): readonly ParametricEqSpec[] {
  // specs('parametric_eq') short-circuits to the old DSP. Query the processor id explicitly.
  const specs = audio.specs('parametric_eq_full');
  const fields = ['id', 'name', 'min', 'max', 'default', 'unit', 'log', 'choices'] as const;
  if (specs.length !== PARAMETRIC_EQ_SPECS.length || specs.some((spec, index) =>
    fields.some((field) => JSON.stringify(spec[field]) !== JSON.stringify(PARAMETRIC_EQ_SPECS[index][field])))) {
    throw new Error('parametric_eq_full 参数契约已变化，请更新宿主控件。');
  }
  return specs;
}

function eqValue(spec: ParametricEqSpec, raw: unknown): number {
  const numeric = raw === true ? 1 : raw === false ? 0 : raw;
  const parsed = typeof numeric === 'number' && Number.isFinite(numeric) ? numeric : spec.default;
  const value = Math.min(spec.max, Math.max(spec.min, parsed));
  return spec.unit === 'toggle' || spec.unit === 'choice' ? Math.round(value) : value;
}

export function readParametricEqValues(
  specs: readonly ParametricEqSpec[], raw: Readonly<Record<string, unknown>>,
): Record<string, number> {
  const values: Record<string, number> = {};
  for (const spec of specs) {
    const old = PARAMETRIC_EQ_LEGACY_MAP.find((row) => row.target === spec.id);
    const value = Object.hasOwn(raw, spec.id) ? raw[spec.id] : old ? raw[old.old] : undefined;
    values[spec.id] = eqValue(spec, value);
  }
  return values;
}

export function changeParametricEqValue(
  specs: readonly ParametricEqSpec[], raw: Readonly<Record<string, unknown>>, key: string, value: number,
): Record<string, number> {
  const spec = specs.find((entry) => entry.id === key);
  if (!spec) throw new Error(`parametric_eq_full 不支持参数：${key}`);
  return { ...readParametricEqValues(specs, raw), [key]: eqValue(spec, value) };
}

export function unsupportedParametricEqValues(raw: Readonly<Record<string, unknown>>): string[] {
  const known = new Set([...PARAMETRIC_EQ_SPECS.map((spec) => spec.id), ...PARAMETRIC_EQ_LEGACY_MAP.map((row) => row.old)]);
  return [
    ...PARAMETRIC_EQ_LEGACY_MAP.filter((row) => row.target === null && Object.hasOwn(raw, row.old))
      .map((row) => `${row.old} = ${String(raw[row.old])}：${row.reason}`),
    ...Object.keys(raw).filter((key) => !known.has(key))
      .map((key) => `${key} = ${String(raw[key])}：引擎不支持，不参与渲染`),
  ];
}

export type ParametricEqControlsProps = {
  audio: ParametricEqAudio;
  params: Readonly<Record<string, unknown>>;
  onChange: (params: Record<string, number>) => void;
};

export function ParametricEqControls({ audio, params, onChange }: ParametricEqControlsProps) {
  let specs: readonly ParametricEqSpec[];
  try { specs = parametricEqSpecs(audio); }
  catch (error) { return <p role="alert">{String(error)}</p>; }
  const values = readParametricEqValues(specs, params);
  const unsupported = unsupportedParametricEqValues(params);
  const change = (spec: ParametricEqSpec, value: number) => {
    onChange(changeParametricEqValue(specs, params, spec.id, value));
  };
  return (
    <div className="audio-effect-params" data-effect="parametric_eq" data-processor="parametric_eq_full">
      <p className="window-muted">
        35 项引擎参数。旧滤波器类型及高通/低通增益、Q 已移除：引擎不支持，不参与渲染。
        高通/低通请使用斜率；新增 Band 3 使用独立控件。
      </p>
      {unsupported.length > 0 && <div role="note" className="window-note is-warning">{unsupported.map((message) => <p key={message}>{message}</p>)}</div>}
      {specs.map((spec) => {
        const value = values[spec.id];
        if (spec.unit === 'toggle') return (
          <label className="audio-effect-param" key={spec.id}>
            <span>{spec.name}</span>
            <input type="checkbox" aria-label={spec.id} checked={value === 1}
              onChange={(event: ChangeEvent<HTMLInputElement>) => change(spec, event.currentTarget.checked ? 1 : 0)} />
          </label>
        );
        if (spec.unit === 'choice') return (
          <label className="audio-effect-param" key={spec.id}>
            <span>{spec.name}</span>
            <select aria-label={spec.id} value={value}
              onChange={(event) => change(spec, Number(event.currentTarget.value))}>
              {spec.choices?.map((label, index) => <option key={label} value={index}>{label}</option>)}
            </select>
          </label>
        );
        return (
          <label className="audio-effect-param" key={spec.id}>
            <span>{spec.name} ({spec.unit})</span>
            <input type="range" aria-label={`${spec.id} slider`} min={spec.min} max={spec.max} step="any" value={value}
              onChange={(event) => change(spec, Number(event.currentTarget.value))} />
            <input type="number" aria-label={spec.id} min={spec.min} max={spec.max} step="any" value={value}
              onChange={(event) => { if (event.currentTarget.value !== '') change(spec, Number(event.currentTarget.value)); }} />
          </label>
        );
      })}
    </div>
  );
}
