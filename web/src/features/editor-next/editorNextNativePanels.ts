/**
 * R82：右侧「属性」入口 + 底部真实状态的 bridge 层（纯逻辑，无 React、无项目内 import）。
 *
 * 只读已发布的 `window.FCBridge`（filmcraft-preview/wwwroot/js/fc-host-bridge.js）：
 * - `getProjectSummary()` → { fps, width, height, tracks, clips }（fps/宽高来自真实序列）
 * - `getPlayhead()`       → **秒**（bridge :835 `engine.playhead / fps()`），不是帧
 * - `getSelection()`      → clip.key 数组（本文件只数数量，不和原生 `name@start` 混用）
 * - `isDirty()`           → 宿主脏标记；保存状态仍由 EditorNextPage 的 save 状态提供
 * - `on(event, cb)`       → playhead {frame, seconds} / dirty {dirty, revision} / project summary 快照
 * - `openPanel(id)`       → 由 fc-host-bridge-panels.js 追加；白名单 properties | effectControls
 *
 * 任何缺失字段一律是 null，由展示层写成「—」；不在这里补 30fps / 100% / 0 帧这类假值。
 * bridge 抛的是普通对象 `{ ok:false, error:"engine_not_ready" }`（bridge :16-21），这里统一读成 reason。
 */

export type NativePanelId = 'properties' | 'effectControls';

export interface OpenPanelResult {
  readonly ok: boolean;
  readonly panel?: NativePanelId;
  readonly error?: string;
}

export interface NativeEngineStatus {
  /** ok=真实读数；not_ready=引擎当前拒绝读取；unavailable=页面没有这个 bridge/方法 */
  readonly phase: 'ok' | 'not_ready' | 'unavailable';
  readonly reason: string | null;
  readonly fps: number | null;
  readonly width: number | null;
  readonly height: number | null;
  /** 秒，来自 getPlayhead() */
  readonly seconds: number | null;
  /** 帧。event=playhead 事件自带；derived=由 seconds×fps 取整；null=没有可靠来源 */
  readonly frame: number | null;
  readonly frameSource: 'event' | 'derived' | null;
  readonly selectionCount: number | null;
  readonly dirty: boolean | null;
}

interface NativePanelsBridge {
  getProjectSummary?: () => unknown;
  getPlayhead?: () => unknown;
  getSelection?: () => unknown;
  isDirty?: () => unknown;
  on?: (event: string, callback: (payload: unknown) => void) => unknown;
  openPanel?: (id: string) => unknown;
}

const PANEL_WHITELIST: readonly NativePanelId[] = ['properties', 'effectControls'];
const STATUS_EVENTS = ['playhead', 'dirty', 'project', 'selection'] as const;

function bridgeRoot(): Record<string, unknown> | null {
  if (typeof window !== 'undefined' && window) return window as unknown as Record<string, unknown>;
  const root = globalThis as unknown as Record<string, unknown>;
  return root ?? null;
}

export function nativeEngBridge(): NativePanelsBridge | null {
  const root = bridgeRoot();
  const value = root === null ? null : root.FCBridge;
  return value && typeof value === 'object' ? (value as NativePanelsBridge) : null;
}

/** bridge 的 requireReady() 抛普通对象；这里只取它的 error 字符串。 */
export function bridgeReason(error: unknown): string {
  if (typeof error === 'string' && error) return error;
  if (error && typeof error === 'object') {
    const record = error as { error?: unknown; message?: unknown };
    if (typeof record.error === 'string' && record.error) return record.error;
    if (typeof record.message === 'string' && record.message) return record.message;
  }
  return 'unknown_error';
}

function isPanelId(value: string): value is NativePanelId {
  return (PANEL_WHITELIST as readonly string[]).includes(value);
}

/**
 * 调一次原生面板激活。ok:true 只在 bridge 确认面板真的激活时返回；
 * 白名单外、bridge 缺失、以及 bridge 尚未打补丁（没有 openPanel）都给出真实原因。
 */
export function openNativePanel(id: string): OpenPanelResult {
  if (!isPanelId(id)) return { ok: false, error: 'panel_not_whitelisted' };
  const bridge = nativeEngBridge();
  if (!bridge) return { ok: false, error: 'bridge_missing' };
  if (typeof bridge.openPanel !== 'function') return { ok: false, error: 'open_panel_unavailable' };
  let result: unknown;
  try {
    result = bridge.openPanel(id);
  } catch (error) {
    return { ok: false, error: bridgeReason(error) };
  }
  // 只接受结构化确认；裸 true 视为不可验证的盲返回，按失败处理。
  if (result && typeof result === 'object' && (result as { ok?: unknown }).ok === true) {
    const panel = (result as { panel?: unknown }).panel;
    if (panel === undefined || panel === id) return { ok: true, panel: id };
    return { ok: false, error: 'panel_mismatch' };
  }
  if (result && typeof result === 'object' && typeof (result as { error?: unknown }).error === 'string') {
    return { ok: false, error: (result as { error: string }).error };
  }
  return { ok: false, error: 'panel_not_confirmed' };
}

/**
 * 入口可用性探测（不触发任何激活）。不可用时给出真实原因：
 * bridge_missing=页面没有 FCBridge；open_panel_unavailable=bridge 存在但没打 fc-host-bridge-panels.js 补丁。
 */
export function nativePanelEntryState(): { available: boolean; reason: string | null } {
  const bridge = nativeEngBridge();
  if (!bridge) return { available: false, reason: 'bridge_missing' };
  if (typeof bridge.openPanel !== 'function') return { available: false, reason: 'open_panel_unavailable' };
  return { available: true, reason: null };
}

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function positiveNumber(value: unknown): number | null {
  const number = finiteNumber(value);
  return number !== null && number > 0 ? number : null;
}

interface SummaryFields {
  fps: number | null;
  width: number | null;
  height: number | null;
}

function summaryFields(payload: unknown): SummaryFields {
  if (!payload || typeof payload !== 'object') return { fps: null, width: null, height: null };
  const record = payload as { fps?: unknown; width?: unknown; height?: unknown };
  return { fps: positiveNumber(record.fps), width: positiveNumber(record.width), height: positiveNumber(record.height) };
}

function selectionCount(value: unknown): number | null {
  if (Array.isArray(value)) return value.length;
  if (value && typeof value === 'object') {
    const record = value as { keys?: unknown; size?: unknown };
    if (Array.isArray(record.keys)) return record.keys.length;
    if (typeof record.size === 'number' && Number.isSafeInteger(record.size) && record.size >= 0) return record.size;
  }
  return null;
}

/** 由秒换算帧，用的是 bridge 自己的取整规则（:28 `Math.round(value * fps())`）。 */
function derivedFrame(seconds: number | null, fps: number | null): number | null {
  if (seconds === null || fps === null) return null;
  return Math.round(seconds * fps);
}

export const EMPTY_NATIVE_STATUS: NativeEngineStatus = {
  phase: 'unavailable',
  reason: null,
  fps: null,
  width: null,
  height: null,
  seconds: null,
  frame: null,
  frameSource: null,
  selectionCount: null,
  dirty: null,
};

/**
 * 读一次真实快照。不订阅、不重试；读取失败只降级为 phase/reason，
 * 其余字段保持 null（展示层写「—」）。
 */
export function readNativeStatus(): NativeEngineStatus {
  const bridge = nativeEngBridge();
  if (!bridge || typeof bridge.getProjectSummary !== 'function' || typeof bridge.getPlayhead !== 'function') {
    return { ...EMPTY_NATIVE_STATUS, phase: 'unavailable', reason: 'bridge_missing' };
  }
  let summary: unknown;
  let position: unknown;
  let picked: unknown = undefined;
  let dirty: unknown = undefined;
  try {
    summary = bridge.getProjectSummary();
    position = bridge.getPlayhead();
    if (typeof bridge.isDirty === 'function') dirty = bridge.isDirty();
    if (typeof bridge.getSelection === 'function') picked = bridge.getSelection();
  } catch (error) {
    return { ...EMPTY_NATIVE_STATUS, phase: 'not_ready', reason: bridgeReason(error) };
  }
  const fields = summaryFields(summary);
  const seconds = finiteNumber(position);
  return {
    phase: 'ok',
    reason: null,
    fps: fields.fps,
    width: fields.width,
    height: fields.height,
    seconds,
    frame: derivedFrame(seconds, fields.fps),
    frameSource: derivedFrame(seconds, fields.fps) === null ? null : 'derived',
    selectionCount: selectionCount(picked),
    dirty: typeof dirty === 'boolean' ? dirty : null,
  };
}

export interface NativeStatusHandlers {
  /** 只带变化的字段；host 侧负责和上一帧合并。 */
  onPatch: (patch: Partial<NativeEngineStatus>) => void;
  onUnavailable?: (reason: string) => void;
}

/**
 * 订阅 playhead / dirty / project / selection。
 * 返回的函数注销**本次订阅产生的全部**监听（bridge 的 unsubscribe 只移除自己那一个，:1005）。
 * 引擎未就绪（requireReady 抛错）时不抛给调用方，走 onUnavailable。
 */
export function subscribeNativeStatus(handlers: NativeStatusHandlers): () => void {
  const bridge = nativeEngBridge();
  if (!bridge || typeof bridge.on !== 'function') {
    handlers.onUnavailable?.('bridge_missing');
    return () => undefined;
  }
  const offs: Array<() => void> = [];
  let active = true;
  const stop = () => {
    if (!active) return;
    active = false;
    for (const off of offs.splice(0)) {
      try {
        off();
      } catch {
        /* 继续注销其余监听 */
      }
    }
  };
  for (const event of STATUS_EVENTS) {
    try {
      const off = bridge.on(event, payload => {
        if (!active) return; // epoch 切换/卸载后的迟到回调直接丢弃
        handlers.onPatch(patchFor(event, payload));
      });
      if (typeof off === 'function') offs.push(off as () => void);
    } catch (error) {
      stop();
      handlers.onUnavailable?.(bridgeReason(error));
      return () => undefined;
    }
  }
  return stop;
}

export interface NativeStatusSubscription {
  readonly snapshot: NativeEngineStatus;
  readonly stop: () => void;
}

/**
 * 切换 epoch / 重新挂载时用：先注销上一代订阅，再读新快照并订阅。
 * 顺序很重要 —— 先停旧的能保证旧引擎的迟到回调不会写进新状态。
 */
export function swapNativeStatus(
  previous: NativeStatusSubscription | null,
  handlers: NativeStatusHandlers,
): NativeStatusSubscription {
  if (previous) previous.stop();
  const snapshot = readNativeStatus();
  const stop = subscribeNativeStatus(handlers);
  return { snapshot, stop };
}

function patchFor(event: (typeof STATUS_EVENTS)[number], payload: unknown): Partial<NativeEngineStatus> {
  if (event === 'playhead') {
    const record = payload && typeof payload === 'object' ? (payload as { frame?: unknown; seconds?: unknown }) : {};
    const frame = finiteNumber(record.frame);
    const seconds = finiteNumber(record.seconds);
    return { frame, frameSource: frame === null ? null : 'event', seconds };
  }
  if (event === 'project') {
    const fields = summaryFields(payload);
    return { fps: fields.fps, width: fields.width, height: fields.height };
  }
  if (event === 'dirty') {
    const record = payload && typeof payload === 'object' ? (payload as { dirty?: unknown }) : {};
    return { dirty: typeof record.dirty === 'boolean' ? record.dirty : null };
  }
  return { selectionCount: selectionCount(payload) };
}

/** 展示用：null/非有限数一律「—」，绝不回填默认值。 */
export function statusValue(value: number | null, digits = 0): string {
  if (value === null || !Number.isFinite(value)) return '—';
  return digits > 0 ? value.toFixed(digits) : String(Math.round(value));
}

export function statusSeconds(seconds: number | null): string {
  return seconds === null || !Number.isFinite(seconds) ? '—' : `${seconds.toFixed(2)} s`;
}

export function statusCanvas(width: number | null, height: number | null): string {
  if (width === null || height === null) return '—';
  return `${Math.round(width)}×${Math.round(height)}`;
}

export function statusDirty(dirty: boolean | null): string {
  if (dirty === null) return '—';
  return dirty ? '未保存' : '已同步';
}
