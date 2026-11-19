/// <reference types="node" />
/**
 * D75/D76 的分辨率选择逻辑必须有测试：它改的是"默认导出多大"，
 * 出错不会崩，只会静默导错尺寸——这类错误最该由断言兜住。
 */
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  RENDER_RESOLUTIONS,
  defaultResolution,
  isResolutionDowngraded,
  resolutionCap,
  resolutionHint,
} from './renderResolution.ts';

test('默认档取主素材能承受的最高档', () => {
  assert.equal(defaultResolution(1080), 1080);
  assert.equal(defaultResolution(2160), 2160);
  assert.equal(defaultResolution(720), 720);
  assert.equal(defaultResolution(480), 480);
});

test('源高介于两档之间时向下取整到较低档', () => {
  // 1440 的源：2160 一定被降级，所以只能选 1080。
  assert.equal(defaultResolution(1440), 1080);
  assert.equal(defaultResolution(1079), 720);
  assert.equal(defaultResolution(721), 720);
});

test('源高未知时保守按 1080，不默认跳 2160', () => {
  for (const unknown of [0, null, undefined, Number.NaN, Number.POSITIVE_INFINITY]) {
    assert.equal(resolutionCap(unknown), 1080, String(unknown));
    assert.equal(defaultResolution(unknown), 1080, String(unknown));
  }
});

test('比最低档还小的源退回 480', () => {
  assert.equal(defaultResolution(360), 480);
  assert.equal(defaultResolution(1), 480);
});

test('超源高的档一律算降级，等于或低于的不算', () => {
  assert.equal(isResolutionDowngraded(2160, 1080), true);
  assert.equal(isResolutionDowngraded(1080, 1080), false);
  assert.equal(isResolutionDowngraded(720, 1080), false);
  assert.equal(isResolutionDowngraded(2160, null), true, '源高未知时 2160 仍算降级');
});

test('提示文案说清实际输出；被压低时说明是主素材限制', () => {
  assert.equal(resolutionHint(1080, 1080), '实际输出 1080p');
  assert.equal(resolutionHint(720, 1080), '实际输出 720p');
  assert.equal(resolutionHint(2160, 1080), '实际输出 1080p：主素材只有 1080p，渲染只降不升');
  // 源高未知时只报实际输出，不能编造"主素材只有 1080p"——那是在替用户猜素材尺寸。
  assert.equal(resolutionHint(2160, null), '实际输出 1080p');
});

test('档位表是升序且不含重复', () => {
  const sorted = [...RENDER_RESOLUTIONS].sort((a, b) => a - b);
  assert.deepEqual([...RENDER_RESOLUTIONS], sorted);
  assert.equal(new Set(RENDER_RESOLUTIONS).size, RENDER_RESOLUTIONS.length);
});
