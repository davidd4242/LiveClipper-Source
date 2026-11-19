/// <reference types="node" />
import assert from 'node:assert/strict';
import { test } from 'node:test';

import { dbToLinear, eqAuditionPlan } from './eqAuditionPlan.ts';

const defaults = (): Record<string, number> => ({
  master_gain: 0,
  hp_on: 0, hp_freq: 30, hp_slope: 0,
  low_on: 1, low_freq: 100, low_gain: 0, low_q: 0.707,
  b1_on: 1, b1_freq: 200, b1_gain: 0, b1_q: 1,
  b2_on: 1, b2_freq: 1000, b2_gain: 0, b2_q: 1,
  mid_on: 1, mid_freq: 3000, mid_gain: 0, mid_q: 1,
  b4_on: 1, b4_freq: 6000, b4_gain: 0, b4_q: 1,
  b5_on: 1, b5_freq: 12000, b5_gain: 0, b5_q: 1,
  high_on: 1, high_freq: 10000, high_gain: 0, high_q: 0.707,
  lp_on: 0, lp_freq: 20000, lp_slope: 0,
});

test('默认参数：不起 HP/LP，五个 bell 与两端 shelf 都在，主增益 0 时不额外加增益级', () => {
  const plan = eqAuditionPlan(defaults());
  assert.deepEqual(plan.map((node) => node.kind), ['lowshelf', 'peaking', 'peaking', 'peaking', 'peaking', 'peaking', 'highshelf']);
  assert.equal(plan.some((node) => node.kind === 'gain'), false);
});

test('顺序与后端一致：highpass → low shelf → b1/b2/mid/b4/b5 → high shelf → lowpass → gain', () => {
  const params = defaults();
  params.hp_on = 1; params.lp_on = 1; params.master_gain = 6;
  const plan = eqAuditionPlan(params);
  assert.deepEqual(plan.map((node) => node.kind), [
    'highpass', 'lowshelf', 'peaking', 'peaking', 'peaking', 'peaking', 'peaking', 'highshelf', 'lowpass', 'gain',
  ]);
  assert.deepEqual(plan.filter((node) => node.kind === 'peaking').map((node) => node.kind === 'peaking' && node.band), ['b1', 'b2', 'mid', 'b4', 'b5']);
  assert.deepEqual(plan.at(-1), { kind: 'gain', db: 6 });
});

test('开关按 _on 生效：关掉 b2 与 high 就少两级', () => {
  const params = defaults();
  params.b2_on = 0; params.high_on = 0;
  const plan = eqAuditionPlan(params);
  assert.equal(plan.some((node) => node.kind === 'peaking' && node.band === 'b2'), false);
  assert.equal(plan.some((node) => node.kind === 'highshelf'), false);
});

test('斜率 0..3 映射成 1..4 级级联，Q 固定 0.707107', () => {
  for (const [slope, stages] of [[0, 1], [1, 2], [2, 3], [3, 4]] as const) {
    const params = defaults();
    params.hp_on = 1; params.hp_slope = slope;
    const hp = eqAuditionPlan(params)[0];
    assert.equal(hp.kind, 'highpass');
    assert.deepEqual({ stages: hp.kind === 'highpass' && hp.stages, q: hp.kind === 'highpass' && hp.q }, { stages, q: 0.707107 });
  }
});

test('参数夹到规格表的 min/max：b2_gain 200 → 30，master_gain -99 → -30', () => {
  const params = defaults();
  params.b2_gain = 200; params.master_gain = -99;
  const plan = eqAuditionPlan(params);
  const b2 = plan.find((node) => node.kind === 'peaking' && node.band === 'b2');
  assert.equal(b2?.kind === 'peaking' && b2.gain, 30);
  assert.deepEqual(plan.at(-1), { kind: 'gain', db: -30 });
});

test('缺参/非数值退回规格表默认值，不抛错', () => {
  const plan = eqAuditionPlan({ low_on: 1 } as Record<string, unknown>);
  const low = plan.find((node) => node.kind === 'lowshelf');
  assert.deepEqual(low, { kind: 'lowshelf', freq: 100, gain: 0 });
});

test('没有参数（未加 EQ）时返回空计划 = 直通', () => {
  assert.deepEqual(eqAuditionPlan(null), []);
  assert.deepEqual(eqAuditionPlan(undefined), []);
});

test('dbToLinear：0 dB=1、+6 dB≈1.995、−6 dB≈0.501', () => {
  assert.equal(dbToLinear(0), 1);
  assert.ok(Math.abs(dbToLinear(6) - 1.9952623149688795) < 1e-12);
  assert.ok(Math.abs(dbToLinear(-6) - 0.5011872336272722) < 1e-12);
});
