import type { EngineResult } from './types';

export interface EngineDomAdapterOptions {
  onError?: (error: string) => void;
}

export interface EngineDomAdapter {
  dispose(): void;
}

export function createEngineDomAdapter(
  host: HTMLElement,
  requestRender: () => Promise<EngineResult>,
  options: EngineDomAdapterOptions = {},
): EngineDomAdapter {
  const hostWindow = host.ownerDocument.defaultView;
  if (!hostWindow) throw new Error('engine_host_window_missing');
  const view = hostWindow;
  let disposed = false;
  let queued = false;
  let dpr = view.devicePixelRatio;
  let query: MediaQueryList | undefined;
  let width: number | undefined;
  let height: number | undefined;

  const redraw = () => {
    if (disposed || queued) return;
    queued = true;
    queueMicrotask(() => {
      queued = false;
      if (disposed) return;
      void Promise.resolve().then(() => disposed ? undefined : requestRender()).then((result) => {
        if (result && !result.ok && !disposed) options.onError?.(result.error ?? 'request_render_failed');
      }, (error: unknown) => {
        if (!disposed) options.onError?.(error instanceof Error ? error.message : String(error));
      });
    });
  };
  const removeQuery = () => {
    if (query?.removeEventListener) query.removeEventListener('change', onDprChange);
    else query?.removeListener(onDprChange);
    query = undefined;
  };
  const watchDpr = () => {
    removeQuery();
    if (typeof view.matchMedia !== 'function') return;
    query = view.matchMedia(`(resolution: ${dpr}dppx)`);
    if (query.addEventListener) query.addEventListener('change', onDprChange);
    else query.addListener(onDprChange);
  };
  function onDprChange() {
    if (disposed) return;
    const next = view.devicePixelRatio;
    if (next !== dpr) {
      dpr = next;
      redraw();
    }
    watchDpr();
  }
  const onWindowResize = () => {
    onDprChange();
    // ResizeObserver is absent in some embedded hosts; resize still invalidates the engine.
    if (!observer) redraw();
  };
  const Resize = (view as Window & { ResizeObserver?: typeof ResizeObserver }).ResizeObserver;
  const observer = Resize ? new Resize((entries) => {
    const entry = entries.find((item) => item.target === host);
    if (!entry || disposed) return;
    if (entry.contentRect.width === width && entry.contentRect.height === height) return;
    width = entry.contentRect.width;
    height = entry.contentRect.height;
    redraw();
  }) : undefined;
  observer?.observe(host);
  watchDpr();
  view.addEventListener('resize', onWindowResize);
  redraw();

  return {
    dispose() {
      if (disposed) return;
      disposed = true;
      observer?.disconnect();
      removeQuery();
      view.removeEventListener('resize', onWindowResize);
    },
  };
}
