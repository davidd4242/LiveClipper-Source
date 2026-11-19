/// <reference types="node" />
import assert from 'node:assert/strict';
import test from 'node:test';
import { AUDIO_EFFECT_TYPES, createAudioEffect, normalizeAudioEffects } from './audioEffects.ts';

// parametric_eq 的 35 个键由 ParametricEqControls 维护，通用规格表是空的。
// 归一化一旦按空表重建参数，用户拖过的 EQ 会在下一次读取时被清空——这条用例锁住它。

test('parametric_eq is addable like the other audio effects', () => {
  assert.deepEqual(AUDIO_EFFECT_TYPES, ['compressor', 'noiseGate', 'parametric_eq']);
  const effect = createAudioEffect('parametric_eq', []);
  assert.equal(effect.type, 'parametric_eq');
  assert.equal(effect.enabled, true);
  assert.deepEqual(effect.params, {});
});

test('normalizeAudioEffects keeps every parametric_eq key it is given', () => {
  const params = { master_gain: 6, hp_on: 1, hp_freq: 80, hp_slope: 2, low_gain: 3, b2_freq: 1000, b2_gain: 9, b2_q: 2 };
  const [effect] = normalizeAudioEffects({ effects: [{ id: 'eq', type: 'parametric_eq', enabled: true, params }] });
  assert.deepEqual(effect.params, params);
});

test('normalizeAudioEffects drops non-finite parametric_eq values but keeps the rest', () => {
  const [effect] = normalizeAudioEffects({
    effects: [{ id: 'eq', type: 'parametric_eq', enabled: true, params: { master_gain: 4, hp_freq: Number.NaN } }],
  });
  assert.deepEqual(effect.params, { master_gain: 4 });
});

test('compressor still clamps to its own frozen table', () => {
  const [effect] = normalizeAudioEffects({
    effects: [{ id: 'c', type: 'compressor', enabled: true, params: { thresholdDb: -999, ratio: 99 } }],
  });
  assert.equal(effect.params.thresholdDb, -60);
  assert.equal(effect.params.ratio, 20);
  assert.deepEqual(Object.keys(effect.params).sort(), ['attackMs', 'kneeDb', 'makeupDb', 'ratio', 'releaseMs', 'thresholdDb']);
});
