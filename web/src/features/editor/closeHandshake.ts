/**
 * 关窗保存握手（页面侧）。桌面壳 src/LiveClipper.Desktop/MainWindow.xaml.cs 关窗前：
 * 1. 执行 CloseHandshake.ProbeScript，读 globalThis.__lcCloseHandshake.installed；为 0 说明当前页面
 *    没有响应者（不是编辑器页），照旧关窗；
 * 2. 否则 PostWebMessageAsJson({ type: 'lc-close-request', requestId })；
 * 3. 这里在 CLOSE_FLUSH_TIMEOUT_MS 内跑完 flush，回 { type: 'lc-close-ack', requestId, dirty, flushed }；
 * 4. 桌面壳只有 dirty === false 且 flushed === true 才直接关，否则弹确认框。
 * 协议常量必须与 src/LiveClipper.Desktop/CloseHandshake.cs 一致（tests/merge/r51-close-handshake.test.mjs 对照两边源码）。
 * 本模块不接线：由持有保存队列的地方调用 installCloseHandshake，卸载时调用它返回的函数。
 */

export const CLOSE_REQUEST_TYPE = 'lc-close-request';
export const CLOSE_ACK_TYPE = 'lc-close-ack';
/** 页面侧 flush 上限。桌面壳等 ack 的上限（CloseHandshake.AckTimeout = 3s）必须比它长。 */
export const CLOSE_FLUSH_TIMEOUT_MS = 2500;
/** 桌面壳探测脚本读取的全局标记（CloseHandshake.MarkerName）。 */
export const CLOSE_HANDSHAKE_MARKER = '__lcCloseHandshake';

export interface CloseHandshakeState {
  /** 还有没确认写入的修改。flush 之后会再读一次。 */
  dirty: boolean;
  /** 尽力把排队的修改写出去；resolve(true) 表示已经没有未保存内容。 */
  flush: () => Promise<boolean>;
}

export interface CloseAck {
  type: typeof CLOSE_ACK_TYPE;
  requestId: string | null;
  dirty: boolean;
  flushed: boolean;
}

export interface CloseRequest {
  requestId: string | null;
}

export interface WebViewMessageEvent {
  data: unknown;
}

export type WebViewMessageListener = (event: WebViewMessageEvent) => void;

/** window.chrome.webview 里本模块用到的部分。 */
export interface WebViewBridge {
  postMessage: (message: unknown) => void;
  addEventListener: (type: 'message', listener: WebViewMessageListener) => void;
  removeEventListener: (type: 'message', listener: WebViewMessageListener) => void;
}

export interface CloseHandshakeOptions {
  /** 测试注入用；默认取 window.chrome.webview。传 null 等于不在 WebView2 里。 */
  webview?: WebViewBridge | null;
  /** flush 上限，默认 CLOSE_FLUSH_TIMEOUT_MS。 */
  timeoutMs?: number;
}

interface Registration {
  getState: () => CloseHandshakeState;
  timeoutMs: number;
}

interface Responder {
  registrations: Set<Registration>;
  listener: WebViewMessageListener;
}

/** 每个通道只挂一个监听：一次请求只回一个 ack，汇总本页所有登记的编辑器。 */
const responders = new WeakMap<WebViewBridge, Responder>();

function readProperty(target: unknown, key: string): unknown {
  if (target === null || target === undefined) return undefined;
  if (typeof target !== 'object' && typeof target !== 'function') return undefined;
  try {
    return Reflect.get(target, key);
  } catch {
    return undefined;
  }
}

function isWebViewBridge(value: unknown): value is WebViewBridge {
  return (
    typeof readProperty(value, 'postMessage') === 'function' &&
    typeof readProperty(value, 'addEventListener') === 'function' &&
    typeof readProperty(value, 'removeEventListener') === 'function'
  );
}

/** 当前页面的 WebView2 通道；普通浏览器、测试或没有 window 的环境返回 null。 */
export function resolveWebView(): WebViewBridge | null {
  const candidate = readProperty(readProperty(globalThis, 'chrome'), 'webview');
  return isWebViewBridge(candidate) ? candidate : null;
}

function installedCount(): number {
  const installed = readProperty(readProperty(globalThis, CLOSE_HANDSHAKE_MARKER), 'installed');
  return typeof installed === 'number' && Number.isFinite(installed) && installed > 0 ? installed : 0;
}

function adjustMarker(delta: number): void {
  try {
    Reflect.set(globalThis, CLOSE_HANDSHAKE_MARKER, { installed: Math.max(0, installedCount() + delta), version: 1 });
  } catch {
    // 标记写不进去只影响桌面壳的探测：它会当成没有编辑器，照旧关窗。
  }
}

/** 解析桌面壳发来的消息；不是 lc-close-request 时返回 null。 */
export function readCloseRequest(data: unknown): CloseRequest | null {
  let message: unknown = data;
  if (typeof message === 'string') {
    try {
      message = JSON.parse(message);
    } catch {
      return null;
    }
  }
  if (readProperty(message, 'type') !== CLOSE_REQUEST_TYPE) return null;
  const requestId = readProperty(message, 'requestId');
  return { requestId: typeof requestId === 'string' ? requestId : null };
}

function flushWithin(flush: () => Promise<boolean>, timeoutMs: number): Promise<boolean> {
  return new Promise<boolean>((resolve) => {
    let settled = false;
    const finish = (flushed: boolean): void => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(flushed);
    };
    const timer = setTimeout(() => finish(false), timeoutMs);
    try {
      void Promise.resolve(flush()).then(
        (flushed) => finish(flushed === true),
        () => finish(false),
      );
    } catch {
      finish(false);
    }
  });
}

/**
 * 跑一次 flush（有上限）并生成 ack。永不 reject：任何异常都报成没写完 / 还脏，绝不假装干净。
 */
export async function buildCloseAck(
  getState: () => CloseHandshakeState,
  requestId: string | null,
  timeoutMs: number = CLOSE_FLUSH_TIMEOUT_MS,
): Promise<CloseAck> {
  let flushed = false;
  try {
    const state = getState();
    flushed = await flushWithin(() => state.flush(), timeoutMs);
  } catch {
    flushed = false;
  }
  let dirty = true;
  try {
    // flush 之后重新取状态：flush 期间可能又进了新修改，也可能刚好写完。
    dirty = getState().dirty !== false;
  } catch {
    dirty = true;
  }
  return { type: CLOSE_ACK_TYPE, requestId, dirty, flushed };
}

function createResponder(webview: WebViewBridge): Responder {
  const registrations = new Set<Registration>();
  const listener: WebViewMessageListener = (event) => {
    const request = readCloseRequest(readProperty(event, 'data'));
    if (!request) return;
    const pending = [...registrations].map((entry) => buildCloseAck(entry.getState, request.requestId, entry.timeoutMs));
    void Promise.all(pending).then((acks) => {
      const ack: CloseAck = {
        type: CLOSE_ACK_TYPE,
        requestId: request.requestId,
        dirty: acks.some((item) => item.dirty),
        flushed: acks.every((item) => item.flushed),
      };
      try {
        webview.postMessage(ack);
      } catch {
        // 通道已经没了（窗口正在销毁）：桌面壳按超时处理，会让用户确认。
      }
    });
  };
  return { registrations, listener };
}

function normalizeTimeout(timeoutMs: number | undefined): number {
  return typeof timeoutMs === 'number' && Number.isFinite(timeoutMs) && timeoutMs >= 0 ? timeoutMs : CLOSE_FLUSH_TIMEOUT_MS;
}

/**
 * 在 WebView2 里响应桌面壳的关窗请求；返回卸载函数（可重复调用）。
 * 不在 WebView2 里（普通浏览器、测试）时什么都不做，也不写全局标记。
 */
export function installCloseHandshake(
  getState: () => CloseHandshakeState,
  options: CloseHandshakeOptions = {},
): () => void {
  const webview = options.webview === undefined ? resolveWebView() : options.webview;
  if (!webview) return () => {};
  let responder = responders.get(webview);
  if (!responder) {
    const created = createResponder(webview);
    try {
      webview.addEventListener('message', created.listener);
    } catch {
      return () => {};
    }
    responders.set(webview, created);
    responder = created;
  }
  const owner = responder;
  const registration: Registration = { getState, timeoutMs: normalizeTimeout(options.timeoutMs) };
  owner.registrations.add(registration);
  adjustMarker(1);
  let disposed = false;
  return () => {
    if (disposed) return;
    disposed = true;
    owner.registrations.delete(registration);
    adjustMarker(-1);
    if (owner.registrations.size > 0) return;
    if (responders.get(webview) === owner) responders.delete(webview);
    try {
      webview.removeEventListener('message', owner.listener);
    } catch {
      // 通道已销毁时监听器也随之失效。
    }
  };
}
