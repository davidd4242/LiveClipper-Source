import { useEffect, useRef, useState } from 'react';
import { createEditorNextCopyController, initialCopyState } from './copyBridge';
import type { CopySaveSession, CopyStamp, SaveAsHold } from './copyBridge';
interface CopyBridge { onSaveAsRequest?: (callback: (event: CopyStamp) => void) => () => void; onCopyRequest?: (callback: (event: CopyStamp) => void) => () => void }
export function useEditorNextCopy(options: {
  clipId: string | null; identity: object | null; ready: boolean; epoch: string; name: string;
  getSaveSession: () => CopySaveSession | null;
  switchTo?: (result: { clipId: string; name: string; revision: number }, hold: SaveAsHold) => Promise<boolean>;
}) {
  const active = useRef<ReturnType<typeof createEditorNextCopyController> | null>(null);
  const latest = useRef(options); latest.current = options;
  const [state, setState] = useState(initialCopyState);
  useEffect(() => {
    setState(initialCopyState());
    if (!options.ready || !options.clipId || !options.identity) return;
    const save = options.getSaveSession(); if (!save) return;
    const identity = options.identity;
    const controller = createEditorNextCopyController({ clipId: options.clipId, epoch: options.epoch, name: options.name, save, switchTo: (result, hold) => latest.current.switchTo?.(result, hold) ?? Promise.resolve(false),
      onState: next => { if (latest.current.identity === identity && latest.current.ready) setState(next); } });
    active.current = controller;
    const unsubscribe = (window as unknown as { FCBridge?: CopyBridge }).FCBridge?.onCopyRequest?.(event => {
      if (active.current === controller && latest.current.identity === identity && latest.current.ready) controller.open(event);
    });
    const unsubscribeSaveAs = (window as unknown as { FCBridge?: CopyBridge }).FCBridge?.onSaveAsRequest?.(event => {
      if (active.current === controller && latest.current.identity === identity && latest.current.ready) controller.open(event, 'saveAs');
    });
    return () => { unsubscribeSaveAs?.(); unsubscribe?.(); controller.dispose(); if (active.current === controller) active.current = null; };
  }, [options.clipId, options.identity, options.ready]);
  return { state, setName: (name: string) => active.current?.setName(name), close: () => active.current?.close(),
    create: () => active.current?.create() ?? Promise.resolve(false) };
}
