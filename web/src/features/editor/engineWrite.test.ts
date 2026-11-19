/// <reference types="node" />
import assert from 'node:assert/strict';
import test from 'node:test';
import { applyInspectorEdit, toClipPatch } from './engineWrite.ts';
import type { ClipPatch, EngineResult } from '../../engine/types.ts';

const okResult: EngineResult = { ok: true, via: 'bridge' };

function recorder(result: EngineResult = okResult) {
  const calls: Array<{ key: string; patch: ClipPatch }> = [];
  const adapter = {
    updateClip(key: string, patch: ClipPatch) {
      calls.push({ key, patch });
      return Promise.resolve(result);
    },
  };
  return { calls, adapter };
}

test('宿主字段按表映射：titleText→name、start→sourceIn、speed→speed', () => {
  const { patch, dropped } = toClipPatch({ titleText: '开场', start: 4.5, speed: 1.5 });
  assert.deepEqual(patch, { name: '开场', sourceIn: 4.5, speed: 1.5 });
  assert.deepEqual(dropped, []);
});

test('宿主用 disabled 表达，引擎用 enabled，方向必须取反', () => {
  assert.deepEqual(toClipPatch({ disabled: true }).patch, { enabled: false });
  assert.deepEqual(toClipPatch({ disabled: false }).patch, { enabled: true });
});

test('没有引擎写入口的字段进 dropped，且不进 patch', () => {
  const { patch, dropped } = toClipPatch({ gapBefore: 0.5, zoom: 1.4, brightness: 12, position: { x: 1, y: 2 } });
  assert.deepEqual(patch, {});
  assert.deepEqual(dropped.sort(), ['brightness', 'gapBefore', 'position', 'zoom']);
});

test('effects 只接受数组，transition 接受 null 或对象', () => {
  const effects = [{ effect: 'brightness_contrast', params: { brightness: 0.2 } }];
  assert.deepEqual(toClipPatch({ effects }).patch, { effects });
  assert.deepEqual(toClipPatch({ effects: { a: 1 } }).dropped, ['effects']);
  assert.deepEqual(toClipPatch({ transition: null }).patch, { transition: null });
  assert.deepEqual(toClipPatch({ transition: { id: 'push', params: { duration: 0.6 } } }).patch,
    { transition: { id: 'push', params: { duration: 0.6 } } });
  assert.deepEqual(toClipPatch({ transition: 3 }).dropped, ['transition']);
});

test('非有限数值不写进引擎', () => {
  assert.deepEqual(toClipPatch({ start: Number.NaN }).dropped, ['start']);
  assert.deepEqual(toClipPatch({ speed: Number.POSITIVE_INFINITY }).dropped, ['speed']);
});

test('applyInspectorEdit 把归一化后的 patch 原样发给引擎', async () => {
  const { calls, adapter } = recorder();
  const result = await applyInspectorEdit(adapter, 'V1:0:0', { titleText: '改名', gapBefore: 0.5, start: 2 });
  assert.equal(result.ok, true);
  assert.deepEqual(calls, [{ key: 'V1:0:0', patch: { name: '改名', sourceIn: 2 } }]);
  assert.deepEqual(result.sent, { name: '改名', sourceIn: 2 });
  assert.deepEqual(result.dropped, ['gapBefore']);
});

test('没有可映射字段时不调用引擎', async () => {
  const { calls, adapter } = recorder();
  const result = await applyInspectorEdit(adapter, 'V1:0:0', { gapBefore: 0.5 });
  assert.equal(result.ok, true);
  assert.equal(calls.length, 0);
  assert.deepEqual(result.dropped, ['gapBefore']);
});

test('没有片段键时直接失败，不调用引擎', async () => {
  const { calls, adapter } = recorder();
  const result = await applyInspectorEdit(adapter, '', { titleText: 'x' });
  assert.equal(result.ok, false);
  assert.equal(result.code, 'missing_clip_key');
  assert.equal(calls.length, 0);
});

test('引擎拒绝时原样转出它的错误码与消息', async () => {
  const { adapter } = recorder({ ok: false, code: 'ambiguous_key', message: '键重复，已拒绝写入。', via: 'bridge' });
  const result = await applyInspectorEdit(adapter, 'V1:0:0', { titleText: 'x' });
  assert.equal(result.ok, false);
  assert.equal(result.code, 'ambiguous_key');
  assert.equal(result.message, '键重复，已拒绝写入。');
});

test('适配器抛异常时转成 engine_write_threw，不冒泡', async () => {
  const adapter = { updateClip() { return Promise.reject(new Error('bridge gone')); } };
  const result = await applyInspectorEdit(adapter, 'V1:0:0', { titleText: 'x' });
  assert.equal(result.ok, false);
  assert.equal(result.code, 'engine_write_threw');
  assert.match(result.message ?? '', /bridge gone/);
});
