import { useEffect, useRef, useState } from 'react';
import { createEditorNextPremiereController, initialPremiereState } from './premiereBridge';
import type { CopySaveSession, CopyStamp } from './copyBridge';
interface PremiereBridge { onPremiereRequest?: (callback: (event: CopyStamp) => void) => () => void }
export function useEditorNextPremiere(options: {
  clipId: string | null; identity: object | null; ready: boolean; epoch: string; name: string;
  getSaveSession: () => CopySaveSession | null;
}) {
  const active = useRef<ReturnType<typeof createEditorNextPremiereController> | null>(null);
  const latest = useRef(options); latest.current = options;
  const [state, setState] = useState(initialPremiereState);
  useEffect(() => {
    setState(initialPremiereState());
    if (!options.ready || !options.clipId || !options.identity) return;
    const save = options.getSaveSession(); if (!save) return;
    const identity = options.identity;
    const controller = createEditorNextPremiereController({ clipId: options.clipId, epoch: options.epoch, name: options.name, save,
      onState: next => { if (latest.current.identity === identity && latest.current.ready) setState(next); } });
    active.current = controller;
    const unsubscribe = (window as unknown as { FCBridge?: PremiereBridge }).FCBridge?.onPremiereRequest?.(event => {
      if (active.current === controller && latest.current.identity === identity && latest.current.ready) controller.open(event);
    });
    return () => { unsubscribe?.(); controller.dispose(); if (active.current === controller) active.current = null; };
  }, [options.clipId, options.identity, options.ready]);
  return { state, setNormalizeVariableFrameRate: (value: boolean) => active.current?.setNormalizeVariableFrameRate(value), setName: (name: string) => active.current?.setName(name), close: () => active.current?.close(),
    create: () => active.current?.create() ?? Promise.resolve(false) };
}
