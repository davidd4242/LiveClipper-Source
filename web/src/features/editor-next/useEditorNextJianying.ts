import { useEffect, useRef, useState } from 'react';
import { createEditorNextJianyingController, initialJianyingState } from './jianyingBridge';
import type { CopySaveSession, CopyStamp } from './copyBridge';
interface JianyingBridge { onJianyingRequest?: (callback: (event: CopyStamp) => void) => () => void }
export function useEditorNextJianying(options: {
  clipId: string | null; identity: object | null; ready: boolean; epoch: string; name: string;
  getSaveSession: () => CopySaveSession | null;
}) {
  const active = useRef<ReturnType<typeof createEditorNextJianyingController> | null>(null);
  const latest = useRef(options); latest.current = options;
  const [state, setState] = useState(initialJianyingState);
  useEffect(() => {
    setState(initialJianyingState());
    if (!options.ready || !options.clipId || !options.identity) return;
    const save = options.getSaveSession(); if (!save) return;
    const identity = options.identity;
    const controller = createEditorNextJianyingController({ clipId: options.clipId, epoch: options.epoch, name: options.name, save,
      onState: next => { if (latest.current.identity === identity && latest.current.ready) setState(next); } });
    active.current = controller;
    const unsubscribe = (window as unknown as { FCBridge?: JianyingBridge }).FCBridge?.onJianyingRequest?.(event => {
      if (active.current === controller && latest.current.identity === identity && latest.current.ready) controller.open(event);
    });
    return () => { unsubscribe?.(); controller.dispose(); if (active.current === controller) active.current = null; };
  }, [options.clipId, options.identity, options.ready]);
  return { state, setName: (name: string) => active.current?.setName(name), close: () => active.current?.close(),
    create: () => active.current?.create() ?? Promise.resolve(false) };
}
