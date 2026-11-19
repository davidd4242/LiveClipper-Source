import { PARAMETRIC_EQ_SPECS, type ParametricEqSpec } from './eqSpecs.ts';

/**
 * EQ 试听计划（D94）。
 *
 * 背景：`parametric_eq` 目前只有导出侧生效——宿主 `<video>` 直连扬声器，预览时听不到 EQ。
 * 这里只做**纯函数**：把 35 个参数翻译成一串 WebAudio 节点描述，不含任何浏览器 API，
 * 因此可以被单测穷举。真正把节点接起来的运行时代码放在 `eqAudition.ts`，接不接由调用方决定。
 *
 * 顺序必须和后端 `EditorRenderService.AudioEffectFilters` 一致：
 * highpass → low shelf → b1/b2/mid/b4/b5 → high shelf → lowpass → 主增益。
 * 参数只能来自 `PARAMETRIC_EQ_SPECS`（唯一的规格表），不在这里另写一份默认值。
 */

export type EqAuditionNode =
  | { kind: 'highpass'; freq: number; q: number; stages: number }
  | { kind: 'lowshelf'; freq: number; gain: number }
  | { kind: 'peaking'; freq: number; gain: number; q: number; band: string }
  | { kind: 'highshelf'; freq: number; gain: number }
  | { kind: 'lowpass'; freq: number; q: number; stages: number }
  | { kind: 'gain'; db: number };

const SLOPE_STAGES = [1, 2, 3, 4] as const; // hp_slope/lp_slope: 0..3 → 1..4 级两级滤波
const CASCADE_Q = 0.707107; // 与后端一致的固定 Q（近似，不是逐级 Butterworth）

const specById = new Map<string, ParametricEqSpec>(PARAMETRIC_EQ_SPECS.map((spec) => [spec.id, spec]));

function numeric(params: Record<string, unknown>, id: string): number {
  const spec = specById.get(id);
  if (!spec) throw new Error(`parametric_eq 缺少参数定义：${id}`);
  const raw = params[id];
  const value = typeof raw === 'number' && Number.isFinite(raw) ? raw : raw === true ? 1 : raw === false ? 0 : spec.default;
  return Math.min(spec.max, Math.max(spec.min, value));
}

function toggle(params: Record<string, unknown>, id: string): boolean {
  return numeric(params, id) >= 0.5;
}

export function eqAuditionPlan(params: Record<string, unknown> | null | undefined): EqAuditionNode[] {
  if (!params) return [];
  const plan: EqAuditionNode[] = [];

  if (toggle(params, 'hp_on')) {
    const slope = Math.round(numeric(params, 'hp_slope'));
    plan.push({ kind: 'highpass', freq: numeric(params, 'hp_freq'), q: CASCADE_Q, stages: SLOPE_STAGES[Math.min(3, Math.max(0, slope))] });
  }

  if (toggle(params, 'low_on')) {
    plan.push({ kind: 'lowshelf', freq: numeric(params, 'low_freq'), gain: numeric(params, 'low_gain') });
  }

  for (const band of ['b1', 'b2', 'mid', 'b4', 'b5'] as const) {
    if (!toggle(params, `${band}_on`)) continue;
    plan.push({
      kind: 'peaking',
      band,
      freq: numeric(params, `${band}_freq`),
      gain: numeric(params, `${band}_gain`),
      q: numeric(params, `${band}_q`),
    });
  }

  if (toggle(params, 'high_on')) {
    plan.push({ kind: 'highshelf', freq: numeric(params, 'high_freq'), gain: numeric(params, 'high_gain') });
  }

  if (toggle(params, 'lp_on')) {
    const slope = Math.round(numeric(params, 'lp_slope'));
    plan.push({ kind: 'lowpass', freq: numeric(params, 'lp_freq'), q: CASCADE_Q, stages: SLOPE_STAGES[Math.min(3, Math.max(0, slope))] });
  }

  const master = numeric(params, 'master_gain');
  if (master !== 0) plan.push({ kind: 'gain', db: master });

  return plan;
}

/** 试听增益：dB → 线性，供 GainNode 使用。 */
export function dbToLinear(db: number): number {
  return Math.pow(10, db / 20);
}
