// 参数规格表的唯一出处（从 workspace/Effects.tsx 抽出，见 D94）。
// 抽出来的原因：试听计划 eqAuditionPlan.ts 需要它，而那个模块必须能被 node --test 直接加载，
// 不能依赖带 JSX 的 .tsx。

export type ParametricEqSpec = {
  id: string;
  name: string;
  min: number;
  max: number;
  default: number;
  unit: string;
  log: boolean;
  choices: readonly string[] | null;
};

const SLOPES = ['12 dB/oct', '24 dB/oct', '36 dB/oct', '48 dB/oct'] as const;

// Exact copy of fullParametricEqParams(), c-eq.js:327-357. All wire values are numbers.
export const PARAMETRIC_EQ_SPECS: readonly ParametricEqSpec[] = [
  { id: 'master_gain', name: 'Master Gain', min: -30, max: 30, default: 0, unit: 'dB', log: false, choices: null },
  { id: 'hp_on', name: 'High Pass On', min: 0, max: 1, default: 0, unit: 'toggle', log: false, choices: null },
  { id: 'hp_freq', name: 'High Pass Frequency', min: 10, max: 20000, default: 30, unit: 'Hz', log: true, choices: null },
  { id: 'hp_slope', name: 'High Pass Slope', min: 0, max: 3, default: 0, unit: 'choice', log: false, choices: SLOPES },
  { id: 'low_on', name: 'Low Shelf On', min: 0, max: 1, default: 1, unit: 'toggle', log: false, choices: null },
  { id: 'low_freq', name: 'Low Shelf Frequency', min: 20, max: 20000, default: 100, unit: 'Hz', log: true, choices: null },
  { id: 'low_gain', name: 'Low Shelf Gain', min: -30, max: 30, default: 0, unit: 'dB', log: false, choices: null },
  { id: 'low_q', name: 'Low Shelf Q', min: 0.1, max: 30, default: 0.707, unit: 'Q', log: true, choices: null },
  { id: 'b1_on', name: 'Band 1 On', min: 0, max: 1, default: 1, unit: 'toggle', log: false, choices: null },
  { id: 'b1_freq', name: 'Band 1 Frequency', min: 20, max: 20000, default: 200, unit: 'Hz', log: true, choices: null },
  { id: 'b1_gain', name: 'Band 1 Gain', min: -30, max: 30, default: 0, unit: 'dB', log: false, choices: null },
  { id: 'b1_q', name: 'Band 1 Q', min: 0.1, max: 30, default: 1, unit: 'Q', log: true, choices: null },
  { id: 'b2_on', name: 'Band 2 On', min: 0, max: 1, default: 1, unit: 'toggle', log: false, choices: null },
  { id: 'b2_freq', name: 'Band 2 Frequency', min: 20, max: 20000, default: 500, unit: 'Hz', log: true, choices: null },
  { id: 'b2_gain', name: 'Band 2 Gain', min: -30, max: 30, default: 0, unit: 'dB', log: false, choices: null },
  { id: 'b2_q', name: 'Band 2 Q', min: 0.1, max: 30, default: 1, unit: 'Q', log: true, choices: null },
  { id: 'mid_on', name: 'Band 3 On', min: 0, max: 1, default: 1, unit: 'toggle', log: false, choices: null },
  { id: 'mid_freq', name: 'Band 3 Frequency', min: 20, max: 20000, default: 1000, unit: 'Hz', log: true, choices: null },
  { id: 'mid_gain', name: 'Band 3 Gain', min: -30, max: 30, default: 0, unit: 'dB', log: false, choices: null },
  { id: 'mid_q', name: 'Band 3 Q', min: 0.1, max: 30, default: 1, unit: 'Q', log: true, choices: null },
  { id: 'b4_on', name: 'Band 4 On', min: 0, max: 1, default: 1, unit: 'toggle', log: false, choices: null },
  { id: 'b4_freq', name: 'Band 4 Frequency', min: 20, max: 20000, default: 2000, unit: 'Hz', log: true, choices: null },
  { id: 'b4_gain', name: 'Band 4 Gain', min: -30, max: 30, default: 0, unit: 'dB', log: false, choices: null },
  { id: 'b4_q', name: 'Band 4 Q', min: 0.1, max: 30, default: 1, unit: 'Q', log: true, choices: null },
  { id: 'b5_on', name: 'Band 5 On', min: 0, max: 1, default: 1, unit: 'toggle', log: false, choices: null },
  { id: 'b5_freq', name: 'Band 5 Frequency', min: 20, max: 20000, default: 5000, unit: 'Hz', log: true, choices: null },
  { id: 'b5_gain', name: 'Band 5 Gain', min: -30, max: 30, default: 0, unit: 'dB', log: false, choices: null },
  { id: 'b5_q', name: 'Band 5 Q', min: 0.1, max: 30, default: 1, unit: 'Q', log: true, choices: null },
  { id: 'high_on', name: 'High Shelf On', min: 0, max: 1, default: 1, unit: 'toggle', log: false, choices: null },
  { id: 'high_freq', name: 'High Shelf Frequency', min: 20, max: 20000, default: 8000, unit: 'Hz', log: true, choices: null },
  { id: 'high_gain', name: 'High Shelf Gain', min: -30, max: 30, default: 0, unit: 'dB', log: false, choices: null },
  { id: 'high_q', name: 'High Shelf Q', min: 0.1, max: 30, default: 0.707, unit: 'Q', log: true, choices: null },
  { id: 'lp_on', name: 'Low Pass On', min: 0, max: 1, default: 0, unit: 'toggle', log: false, choices: null },
  { id: 'lp_freq', name: 'Low Pass Frequency', min: 20, max: 22000, default: 18000, unit: 'Hz', log: true, choices: null },
  { id: 'lp_slope', name: 'Low Pass Slope', min: 0, max: 3, default: 0, unit: 'choice', log: false, choices: SLOPES },
];
