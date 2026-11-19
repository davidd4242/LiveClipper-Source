import assert from 'node:assert/strict';
import test from 'node:test';

import { createEqAudition, type AudioEnv, type AudioTarget } from './eqAudition.ts';

type Call = string;

function makeEnv(log: Call[], options: { failSource?: boolean; failFilter?: boolean } = {}) {
  const target = (name: string): AudioTarget => ({
    connect: (node: AudioTarget) => { log.push(`${name}->${(node as unknown as { name: string }).name ?? '?'}`); return node; },
    disconnect: () => { log.push(`${name}.disconnect`); },
  });

  const env: AudioEnv = {
    createContext: () => {
      log.push('createContext');
      const ctx = {
        state: 'running' as string,
        destination: { ...target('destination'), name: 'destination' } as AudioTarget,
        createMediaElementSource: () => {
          log.push('createMediaElementSource');
          if (options.failSource) throw new Error('no media element source');
          return { ...target('source'), name: 'source' } as AudioTarget & { name: string };
        },
        createBiquadFilter: () => {
          if (options.failFilter) throw new Error('no biquad');
          const f = {
            name: `biquad#${log.filter((l) => l === 'createBiquadFilter').length}`,
            type: '',
            frequency: { value: 0 },
            Q: { value: 0 },
            gain: { value: 0 },
            connect: (node: AudioTarget) => { log.push('biquad.connect'); return node; },
            disconnect: () => { log.push('biquad.disconnect'); },
          };
          log.push('createBiquadFilter');
          return f as never;
        },
        createGain: () => {
          log.push('createGain');
          return {
            name: 'gain',
            gain: { value: 1 },
            connect: (node: AudioTarget) => { log.push('gain.connect'); return node; },
            disconnect: () => { log.push('gain.disconnect'); },
          };
        },
        resume: async () => { log.push('resume'); },
        close: async () => { log.push('close'); },
      };
      return ctx as never;
    },
  };
  return env;
}

const eqParams = { master_gain: 6, low_gain: 3, b1_gain: 2 };

test('no EQ: never creates an AudioContext, audio path untouched', () => {
  const log: Call[] = [];
  const audition = createEqAudition(makeEnv(log), {});
  const state = audition.apply(null);
  assert.equal(state.active, false);
  assert.equal(state.nodes, 0);
  assert.equal(state.error, null);
  assert.deepEqual(log, [], '无 EQ 时不得创建 AudioContext 或碰任何音频节点');
  assert.equal(audition.state.active, false);
});

test('empty plan after having an EQ tears down and returns to direct output', () => {
  const log: Call[] = [];
  const audition = createEqAudition(makeEnv(log), {});
  const on = audition.apply(eqParams);
  assert.equal(on.active, true);
  assert.ok(on.nodes > 0);
  log.length = 0;

  const off = audition.apply(null);
  assert.equal(off.active, false);
  assert.equal(off.nodes, 0);
  assert.ok(log.includes('source.disconnect'), '关掉 EQ 必须断开链路');
  assert.ok(log.includes('source->destination'), '关掉 EQ 必须直连 destination，否则用户会静音');
});

test('switching EQ settings rebuilds the chain instead of stacking it', () => {
  const log: Call[] = [];
  const audition = createEqAudition(makeEnv(log), {});
  const first = audition.apply({ master_gain: 0 });
  const second = audition.apply({ master_gain: 6, b2_gain: 9 });
  assert.equal(second.active, true);
  assert.equal(log.filter((l) => l === 'createContext').length, 1, 'AudioContext 只创建一次');
  assert.equal(log.filter((l) => l === 'createMediaElementSource').length, 1, 'media element source 只创建一次');
  assert.ok(first.nodes > 0 && second.nodes > 0);
  assert.ok(log.filter((l) => l === 'biquad.disconnect').length > 0, '重建前要断开旧链');
});

test('failure falls back to direct output and reports the reason', () => {
  const log: Call[] = [];
  const audition = createEqAudition(makeEnv(log, { failFilter: true }), {});
  const state = audition.apply(eqParams);
  assert.equal(state.active, false);
  assert.match(String(state.error), /^eq_audition_failed: /);
  assert.equal(audition.state.active, false);
});

test('dispose closes the context and resets state', () => {
  const log: Call[] = [];
  const audition = createEqAudition(makeEnv(log), {});
  audition.apply(eqParams);
  audition.dispose();
  assert.ok(log.includes('close'));
  assert.equal(audition.state.active, false);
  assert.equal(audition.state.nodes, 0);
});
