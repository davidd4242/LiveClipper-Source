import { useEffect, useRef, useState } from 'react';
import type { CSSProperties, PointerEvent } from 'react';
import './FullEngineHost.css';
import { loadEngine } from './loader';
import type { EngineHandle } from './types';
import { createFullEngineGeneration } from './fullEngineGeneration';
import { connectFullEngine } from './fullEngineSession';
import type {
  FullEngineBridge, FullEngineCallbacks, FullEngineConnection, FullEngineDocument, FullEngineLoadOptions, FullEngineViewport,
} from './fullEngineSession';

export interface FullEngineHostProps extends FullEngineCallbacks {
  document: FullEngineDocument;
  options?: FullEngineLoadOptions;
  className?: string;
  style?: CSSProperties;
  inert?: boolean;
  onReady?: (connection: FullEngineConnection) => void;
  onError?: (error: Error) => void;
  onWarnings?: (warnings: string[]) => void;
}
type FullEngineWindow = Window & { FCBridge?: FullEngineBridge; FCViewport?: FullEngineViewport };
const hostStyle: CSSProperties = { width: '100%', minWidth: 0, minHeight: 0, flex: '1 1 0', overflow: 'hidden' };

function focusNativeWorkbench(event: PointerEvent<HTMLDivElement>) {
  if (event.button !== 0 || !(event.target instanceof Element)) return;
  const root = event.currentTarget.querySelector<HTMLElement>('#fc-engine-root');
  if (!root?.contains(event.target)) return;
  const control = event.target.closest('input, textarea, select, button, a[href], [contenteditable]:not([contenteditable="false"]), [tabindex]');
  if (control && control !== root) return;
  // Native hotkeys accept events only from inside the engine, not the React host.
  root.tabIndex = -1;
  root.focus({ preventScroll: true });
}

export function FullEngineHost({ document: snapshot, options, className, style, inert = false, onReady, onError, onWarnings, ...events }: FullEngineHostProps) {
  const hostRef = useRef<HTMLDivElement>(null);
  const callbacks = useRef({ onReady, onError, onWarnings, ...events });
  callbacks.current = { onReady, onError, onWarnings, ...events };
  const generation = useRef(createFullEngineGeneration());
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const current = generation.current.begin();
    let active = true;
    let handle: EngineHandle | undefined;
    let connection: FullEngineConnection | undefined;
    setError(null);
    const isActive = () => active && generation.current.isCurrent(current);
    const detach = () => { if (generation.current.isCurrent(current)) handle?.dispose(); };
    void (async () => {
      handle = await loadEngine(host, { viewportMode: 'full' });
      if (!isActive()) { detach(); return; }
      const runtime = window as FullEngineWindow;
      await runtime.FCBridge?.ready;
      if (!isActive()) { detach(); return; }
      connection = connectFullEngine(runtime.FCBridge, runtime.FCViewport, {
        onDocumentLoaded: (snapshot) => { if (isActive()) callbacks.current.onDocumentLoaded?.(snapshot); },
        onDirty: (event) => { if (isActive()) callbacks.current.onDirty?.(event); },
        onSaveRequest: (event) => { if (isActive()) callbacks.current.onSaveRequest?.(event); },
        onCloseRequest: (event) => { if (isActive()) callbacks.current.onCloseRequest?.(event); },
      }, options);
      const result = await connection.loadDocument(snapshot);
      if (isActive()) {
        callbacks.current.onWarnings?.(result.warnings ?? []);
        callbacks.current.onReady?.(connection);
      }
    })().catch((cause: unknown) => {
      if (!isActive()) return;
      connection?.release();
      detach();
      const failure = cause instanceof Error ? cause : new Error(String(cause));
      setError(failure.message);
      callbacks.current.onError?.(failure);
    });
    return () => {
      active = false;
      try { connection?.release(); }
      finally {
        // StrictMode reuses a loader handle before this microtask; only its latest owner detaches.
        queueMicrotask(detach);
      }
    };
  }, [snapshot, options]);

  return (
    <>
      {error !== null && !onError && <p role="alert">整页编辑器载入失败：{error}</p>}
      <div ref={hostRef} className={className ? `full-engine-host ${className}` : 'full-engine-host'}
        style={{ ...hostStyle, ...style }} inert={inert} data-fc-viewport-mode="full"
        onPointerDownCapture={focusNativeWorkbench} />
    </>
  );
}
