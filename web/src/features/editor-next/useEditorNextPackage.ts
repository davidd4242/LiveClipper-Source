import { useEffect, useRef, useState } from 'react';
import { createPackageController, initialPackageState } from './packageBridge';
import type { CopySaveSession, CopyStamp } from './copyBridge';
interface PackageBridge {
  onPackageSaveRequest?(callback: (event: CopyStamp) => void): () => void;
  onPackageOpenRequest?(callback: () => void): () => void;
}
export function useEditorNextPackage(options: { clipId: string | null; identity: object | null; ready: boolean;
  epoch: string; name: string; getSaveSession(): CopySaveSession | null }) {
  const active = useRef<ReturnType<typeof createPackageController> | null>(null);
  const [state, setState] = useState(initialPackageState);
  const latest = useRef(options); latest.current = options;
  useEffect(() => {
    setState(initialPackageState());
    const controller = createPackageController({ ...options, getSaveSession: () => latest.current.getSaveSession(),
      origin: window.location.origin, onState: setState });
    active.current = controller;
    const bridge = (window as unknown as { FCBridge?: PackageBridge }).FCBridge;
    const offSave = options.ready && options.clipId ? bridge?.onPackageSaveRequest?.(event => { if (latest.current.ready) controller.openSave(event); }) : undefined;
    const offOpen = bridge?.onPackageOpenRequest?.(() => controller.openImport());
    return () => { offSave?.(); offOpen?.(); controller.dispose(); if (active.current === controller) active.current = null; };
  }, [options.clipId, options.identity, options.ready]);
  return { state, cancelImport: () => active.current?.cancelImport(), openImport: () => active.current?.openImport(), setName: (name: string) => active.current?.setName(name),
    close: () => active.current?.close(), save: () => active.current?.save(), importFile: (file: File) => active.current?.import(file) };
}
