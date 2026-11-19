import { useEffect, useRef } from 'react';
import type { CSSProperties } from 'react';
import { loadEngine } from './loader';
import type { EngineHandle } from './types';

export interface EngineHostProps {
  className?: string;
  style?: CSSProperties;
  onReady?: (root: HTMLElement) => void;
  onError?: (error: Error) => void;
}

export function EngineHost({ className, style, onReady, onError }: EngineHostProps) {
  const hostRef = useRef<HTMLDivElement>(null);
  const callbacks = useRef({ onReady, onError });
  callbacks.current = { onReady, onError };
  const generation = useRef(0);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const current = ++generation.current;
    let active = true;
    let handle: EngineHandle | undefined;
    loadEngine(host).then((loaded) => {
      handle = loaded;
      if (active) callbacks.current.onReady?.(loaded.root);
      else if (generation.current === current) loaded.dispose();
    }, (error: unknown) => {
      if (active) callbacks.current.onError?.(error instanceof Error ? error : new Error(String(error)));
    });
    return () => {
      active = false;
      // StrictMode runs setup again before this microtask; real unmounts release the handle.
      queueMicrotask(() => { if (generation.current === current) handle?.dispose(); });
    };
  }, []);

  return <div ref={hostRef} className={className} style={style} />;
}
