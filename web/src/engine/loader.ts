import type { EngineDisposeReport, EngineHandle, EngineLoadOptions, EngineManifest } from './types';
import { waitForFullEngineDocuments } from './fullEngineSession.ts';

const ADAPTER_PENDING = 'Engine adapter is not installed (W1b loader only)';
const RETAINED = [
  'Engine top-level const bindings and singleton objects: page reload required to release',
  'Same engine root and cached style nodes: reused on the next mount',
  'Engine-owned global listeners, timers, animation frames and caches without teardown',
];
// These are the body-level portal roots listed by the generated manifest.
const PORTALS = '.drag-ghost,.ctx-menu.ctx-sub,.ch-tip,.clips-tr-picker,.tlset-menu,.fxm-mask,div[aria-hidden="true"]:has(.mon-panel)';

interface Runtime {
  root: HTMLElement;
  manifest: EngineManifest;
  styles: HTMLStyleElement[];
  portalStyle?: HTMLStyleElement;
  scripts: HTMLScriptElement[];
  portals: Set<Element>;
  bodyBefore: Set<Element>;
  observer?: MutationObserver;
  parking?: HTMLDivElement;
  addedBodyClass: boolean;
  warnings: string[];
  owner?: object;
  loadedScripts: Set<string>;
}
interface Mount {
  token: object;
  promise: Promise<EngineHandle>;
}
interface LoaderState {
  manifestUrl: string;
  host: HTMLElement;
  viewportMode: 'canvas' | 'full';
  runtime?: Runtime;
  ready: Promise<Runtime>;
  mounts: WeakMap<HTMLElement, Mount>;
  warnings: string[];
}
type EngineWindow = Window & {
  FCBridge?: { configureHost?: (options: { persistence: 'host' | 'local' }) => { ok: boolean; error?: string } };
  FCViewport?: { setMode: (mode: 'canvas' | 'full') => { ok: boolean; error?: string } };
  __FC_ENGINE_LOADER__?: LoaderState;
  __FC_ENGINE_RECOVERY__?: { manifestUrl: string; runtime: Runtime };
  __FC_ENGINE_BOOTSTRAP__?: Promise<unknown>;
};
const page = () => window as EngineWindow;
const assetUrl = (path: string) => `/${path.replace(/^\/+/, '')}`;

export async function waitForEngine<T>(ready: Promise<T>, timeoutMs: number, code: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_resolve, reject) => {
    timer = setTimeout(() => reject(Object.assign(new Error(code), { code })), timeoutMs);
  });
  try { return await Promise.race([ready, timeout]); }
  finally { clearTimeout(timer); }
}

function resetFailedState(state: LoaderState) {
  if (state.runtime) {
    detach(state.runtime);
    if (page().__FC_ENGINE_LOADER__ === state) page().__FC_ENGINE_RECOVERY__ = { manifestUrl: state.manifestUrl, runtime: state.runtime };
  }
  state.runtime = undefined;
  if (page().__FC_ENGINE_LOADER__ === state) {
    delete page().__FC_ENGINE_LOADER__;
    delete page().__FC_ENGINE_BOOTSTRAP__;
  }
}

function runtimeFor(api: object, host?: HTMLElement): Runtime | undefined {
  if (typeof window === 'undefined') return;
  const runtime = page().__FC_ENGINE_LOADER__?.runtime;
  if (runtime && (page().FCBridge === api || (host && (host === runtime.root || host.contains(runtime.root) || runtime.root.contains(host))))) return runtime;
}

export function engineWarnings(api: object, host?: HTMLElement): readonly string[] {
  return runtimeFor(api, host)?.warnings ?? [];
}

export function engineFailureCleanup(api: object, host?: HTMLElement): () => void {
  const runtime = runtimeFor(api, host);
  const state = typeof window === 'undefined' ? undefined : page().__FC_ENGINE_LOADER__;
  return () => {
    if (state && runtime && page().__FC_ENGINE_LOADER__ === state && state.runtime === runtime) resetFailedState(state);
  };
}

function loadFailure(error: unknown): Error {
  if (error instanceof Error && 'code' in error && typeof error.code === 'string' && error.code.startsWith('engine_')) return error;
  return Object.assign(new Error(`engine_load_failed: ${error instanceof Error ? error.message : String(error)}`), { code: 'engine_load_failed' });
}

async function fetchText(url: string): Promise<string> {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Engine fetch failed: ${url} (HTTP ${response.status})`);
  return response.text();
}

function collectPortals(runtime: Runtime) {
  for (const element of document.body.children) {
    if (!runtime.bodyBefore.has(element) && element !== runtime.root && element.matches(PORTALS)) {
      runtime.portals.add(element);
    }
  }
}

function keepConnected(runtime: Runtime) {
  if (runtime.root.isConnected) return;
  runtime.parking ??= document.createElement('div');
  runtime.parking.hidden = true;
  document.body.appendChild(runtime.parking);
  runtime.parking.appendChild(runtime.root);
}

function applyViewportMode(root: HTMLElement, mode: 'canvas' | 'full') {
  const previous = root.getAttribute('data-fc-viewport-mode');
  root.setAttribute('data-fc-viewport-mode', mode);
  try {
    const result = page().FCViewport?.setMode(mode);
    if (result && !result.ok) throw new Error(result.error ?? 'engine_viewport_mode_failed');
    if (mode === 'canvas' && previous === 'full') {
      const configured = page().FCBridge?.configureHost?.({ persistence: 'local' });
      if (configured && !configured.ok) throw new Error(configured.error ?? 'engine_persistence_mode_failed');
    }
  } catch (error) {
    if (previous === null) root.removeAttribute('data-fc-viewport-mode');
    else root.setAttribute('data-fc-viewport-mode', previous);
    throw error;
  }
}

function attach(runtime: Runtime, host: HTMLElement, token: object) {
  runtime.owner = token;
  if (runtime.root.parentNode !== host) host.appendChild(runtime.root);
  if (host.isConnected) { runtime.parking?.remove(); runtime.parking = undefined; }
  keepConnected(runtime);
  for (const style of runtime.styles) if (!style.parentNode) runtime.root.appendChild(style);
  if (runtime.portalStyle && !runtime.portalStyle.parentNode) document.head.appendChild(runtime.portalStyle);
  if (!document.body.classList.contains(runtime.manifest.portal.bodyClass)) {
    document.body.classList.add(runtime.manifest.portal.bodyClass);
    runtime.addedBodyClass = true;
  }
  if (!runtime.observer) {
    runtime.observer = new MutationObserver(() => { keepConnected(runtime); collectPortals(runtime); });
    runtime.observer.observe(document.body, { childList: true, subtree: true });
  }
}

function detach(runtime: Runtime): EngineDisposeReport {
  collectPortals(runtime);
  runtime.observer?.disconnect();
  runtime.observer = undefined;
  const report: EngineDisposeReport = {
    removedStyles: 0, removedScripts: 0, detachedPortals: 0, retained: [...RETAINED],
  };
  for (const style of [...runtime.styles, runtime.portalStyle]) {
    if (style?.parentNode) { style.remove(); report.removedStyles++; }
  }
  for (const script of runtime.scripts) {
    script.onload = null;
    script.onerror = null;
    if (script.parentNode) { script.remove(); report.removedScripts++; }
  }
  for (const portal of runtime.portals) {
    if (portal.parentNode === document.body) { portal.remove(); report.detachedPortals++; }
  }
  runtime.portals.clear();
  runtime.root.remove();
  runtime.parking?.remove();
  runtime.parking = undefined;
  if (runtime.addedBodyClass) document.body.classList.remove(runtime.manifest.portal.bodyClass);
  runtime.addedBodyClass = false;
  runtime.owner = undefined;
  return report;
}

function loadScript(runtime: Runtime, src: string, label: string, timeoutMs: number, classic = false): Promise<void> {
  if (classic && runtime.loadedScripts.has(src)) return Promise.resolve();
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.async = false;
    script.defer = false;
    script.src = assetUrl(src);
    script.dataset.fcEngineScript = label;
    runtime.scripts.push(script);
    const finish = (error?: string) => {
      clearTimeout(timer);
      script.onload = null;
      script.onerror = null;
      window.removeEventListener('error', onRuntimeError);
      if (error) reject(new Error(`Engine ${label} failed: ${src}: ${error}`));
      else { if (classic) runtime.loadedScripts.add(src); resolve(); }
    };
    const onRuntimeError = (event: ErrorEvent) => {
      if (event.filename === script.src) finish(event.message);
    };
    const timer = window.setTimeout(() => finish(`timeout after ${timeoutMs}ms`), timeoutMs);
    window.addEventListener('error', onRuntimeError);
    script.onload = () => finish();
    script.onerror = () => finish('load error');
    document.head.appendChild(script);
  });
}

async function createRuntime(manifest: EngineManifest, warnings: string[]): Promise<Runtime> {
  const template = document.createElement('template');
  template.innerHTML = await fetchText(assetUrl(manifest.skeleton.src));
  let root: HTMLElement;
  if (manifest.skeleton.includesRoot === true) {
    const roots = template.content.querySelectorAll<HTMLElement>('#fc-engine-root');
    if (roots.length !== 1) throw new Error('Engine skeleton must contain exactly one #fc-engine-root');
    root = roots[0];
  } else {
    root = document.createElement('div');
    root.id = manifest.rootId;
    root.className = 'fc-engine-root';
    root.appendChild(template.content);
  }
  if (!root.querySelector('#app')) throw new Error('Engine skeleton is missing #app');
  return {
    root, manifest, styles: [], scripts: [], portals: new Set(), loadedScripts: new Set(),
    bodyBefore: new Set(document.body.children), addedBodyClass: false, warnings,
  };
}

async function initialize(state: LoaderState, options: EngineLoadOptions): Promise<Runtime> {
  const manifest = JSON.parse(await fetchText(state.manifestUrl)) as EngineManifest;
  if (manifest.schema !== 1 || manifest.rootId !== 'fc-engine-root') throw new Error('Unsupported engine manifest');
  if (document.getElementById(manifest.rootId)) throw new Error('Engine root already exists outside this loader');
  const recovery = page().__FC_ENGINE_RECOVERY__;
  if (recovery && recovery.manifestUrl !== state.manifestUrl) throw new Error('Engine recovery uses a different manifest; reload the page to switch');
  const runtime = recovery?.runtime ?? await createRuntime(manifest, state.warnings);
  runtime.warnings = state.warnings;
  runtime.warnings.push(...manifest.warnings.map((warning) => typeof warning === 'string' ? warning : JSON.stringify(warning)));
  state.runtime = runtime;
  delete page().__FC_ENGINE_RECOVERY__;
  try {
    applyViewportMode(runtime.root, state.viewportMode);
    state.host.appendChild(runtime.root);
    for (const entry of manifest.styles) {
      if (runtime.styles.some((style) => style.dataset.src === entry.dataSrc)) continue;
      const style = document.createElement('style');
      style.dataset.src = entry.dataSrc;
      style.textContent = await fetchText(assetUrl(entry.out));
      runtime.styles.push(style);
    }
    if (!runtime.portalStyle) {
      const style = document.createElement('style');
      style.dataset.src = manifest.portal.src;
      style.textContent = await fetchText(assetUrl(manifest.portal.src));
      runtime.portalStyle = style;
    }
    attach(runtime, state.host, {});
    // Dynamic app.js must miss DOMContentLoaded; bootstrap alone calls App.init().
    if (document.readyState === 'loading') {
      await new Promise<void>((resolve) => document.addEventListener('DOMContentLoaded', () => resolve(), { once: true }));
    }
    const timeoutMs = options.scriptTimeoutMs ?? 30000;
    for (const [index, entry] of manifest.scripts.entries()) {
      await loadScript(runtime, entry.out, `script ${index + 1}/${manifest.scripts.length} (${entry.src})`, timeoutMs, true);
    }
    delete page().__FC_ENGINE_BOOTSTRAP__;
    await loadScript(runtime, manifest.bootstrap.src, 'bootstrap', timeoutMs);
    const bootstrap = page().__FC_ENGINE_BOOTSTRAP__;
    if (!bootstrap || typeof bootstrap.then !== 'function') throw new Error('Engine bootstrap readiness Promise is missing');
    await waitForEngine(bootstrap, 15000, 'engine_bootstrap_timeout');
    return runtime;
  } catch (error) {
    resetFailedState(state);
    throw error;
  }
}

export function loadEngine(host: HTMLElement, options: EngineLoadOptions = {}): Promise<EngineHandle> {
  if (host.ownerDocument !== document) return Promise.reject(new Error('Engine host must belong to the current document'));
  const manifestUrl = options.manifestUrl ?? '/engine/manifest.json';
  let state = page().__FC_ENGINE_LOADER__;
  if (state && state.manifestUrl !== manifestUrl) return Promise.reject(new Error('Engine already uses a different manifest; reload the page to switch'));
  if (!state) {
    const created: LoaderState = {
      manifestUrl, host, viewportMode: options.viewportMode ?? 'canvas', ready: Promise.resolve().then(() => initialize(created, options)).catch((error: unknown) => {
        resetFailedState(created);
        throw loadFailure(error);
      }), mounts: new WeakMap(), warnings: [],
    };
    state = created;
    page().__FC_ENGINE_LOADER__ = state;
  } else if (state.host !== host) {
    console.warn('engine_rehosted');
    if (!state.warnings.includes('engine_rehosted')) state.warnings.push('engine_rehosted');
    state.mounts.delete(state.host);
    state.host = host;
    if (state.runtime) {
      state.runtime.owner = {};
      host.appendChild(state.runtime.root);
      keepConnected(state.runtime);
    }
  }
  const viewportMode = options.viewportMode ?? 'canvas';
  state.viewportMode = viewportMode;
  const previous = state.mounts.get(host);
  if (previous) return previous.promise;
  const token = {};
  const mounts = state.mounts;
  const currentState = state;
  const promise = state.ready.then(async (runtime): Promise<EngineHandle> => {
    // A full document restore cannot be cancelled. Finish it before returning to the legacy canvas host.
    await waitForFullEngineDocuments();
    applyViewportMode(runtime.root, currentState.viewportMode);
    attach(runtime, host, token);
    let disposed = false;
    const emptyReport = (): EngineDisposeReport => ({ removedStyles: 0, removedScripts: 0, detachedPortals: 0, retained: [...RETAINED] });
    return {
      root: runtime.root,
      warnings: runtime.warnings,
      request: async () => ({ ok: false, via: 'direct', error: ADAPTER_PENDING, warnings: [ADAPTER_PENDING] }),
      on: () => {
        if (!runtime.warnings.includes(ADAPTER_PENDING)) runtime.warnings.push(ADAPTER_PENDING);
        return () => {};
      },
      snapshot: () => { throw new Error(ADAPTER_PENDING); },
      dispose: () => {
        if (disposed) return emptyReport();
        disposed = true;
        if (mounts.get(host)?.token === token) mounts.delete(host);
        const report = runtime.owner === token ? detach(runtime) : emptyReport();
        options.onDispose?.(report);
        return report;
      },
    };
  }).catch((error: unknown) => {
    if (mounts.get(host)?.token === token) {
      mounts.delete(host);
      if (currentState.host === host && currentState.runtime) detach(currentState.runtime);
    }
    throw error;
  });
  mounts.set(host, { token, promise });
  return promise;
}
