import { useEffect, useRef, useState } from 'react';
import type { EditorPayload } from '../../api/types';
import type { FullEngineConnection, FullEngineDirty } from '../../engine/fullEngineSession';
import { installCloseHandshake } from '../editor/closeHandshake';
import { createEditorNextSaveController } from './editorNextSaveController';
import type { EditorNextAckBridge, EditorNextSaveController, EditorNextSaveState } from './editorNextSaveController';

interface SaveSession { clipId: string; payload: EditorPayload; sourceIds: readonly string[] }
export function useEditorNextSave(session: SaveSession | null) {
  const active = useRef<EditorNextSaveController | null>(null);
  const identity = useRef<EditorPayload | null>(null);
  const [state, setState] = useState<EditorNextSaveState>({ status: 'saved', revision: null, message: '', warnings: [], archiveWarnings: [], draft: null, draftError: '' });
  useEffect(() => {
    if (!session) { active.current = null; identity.current = null; return; }
    const controller = createEditorNextSaveController({ clipId: session.clipId, payload: session.payload,
      registeredSourceIds: session.sourceIds, onState: setState,
      getBridge: () => (window as unknown as { FCBridge?: EditorNextAckBridge }).FCBridge });
    active.current = controller;
    identity.current = session.payload;
    setState(controller.getState());
    return () => { controller.dispose(); if (active.current === controller) active.current = null; };
  }, [session?.clipId, session?.payload]);
  useEffect(() => {
    // Getter is evaluated again after flush; no render-time dirty boolean is captured.
    const uninstall = installCloseHandshake(() => ({ dirty: active.current?.hasUnsaved() ?? false,
      flush: () => active.current?.flush() ?? Promise.resolve(true) }));
    const shortcut = (event: KeyboardEvent) => {
      // Engine global shortcuts must not mutate the server document while a draft decision is pending.
      if (active.current?.getState().draft) {
        event.stopImmediatePropagation();
        if (event.key !== 'Tab' && event.key !== 'Enter' && event.key !== ' ') event.preventDefault();
        return;
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
        event.preventDefault(); event.stopImmediatePropagation();
        // Copy is a separate host request, never a save/flush of the original.
        if (event.altKey && !event.shiftKey) (window as unknown as { FCBridge?: { requestCopy?: () => unknown } }).FCBridge?.requestCopy?.();
        else if (!event.altKey && event.shiftKey) (window as unknown as { FCBridge?: { requestSaveAs?: () => unknown } }).FCBridge?.requestSaveAs?.();
        else if (!event.altKey && !event.shiftKey) void active.current?.retry();
      }
    };
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (!active.current?.hasUnsaved()) return;
      event.preventDefault(); event.returnValue = '';
    };
    const pageHide = () => { void active.current?.pageHide(); };
    window.addEventListener('keydown', shortcut, true);
    window.addEventListener('beforeunload', beforeUnload);
    window.addEventListener('pagehide', pageHide);
    return () => { uninstall(); window.removeEventListener('keydown', shortcut, true); window.removeEventListener('beforeunload', beforeUnload); window.removeEventListener('pagehide', pageHide); };
  }, []);
  return {
    state,
    onReady: (connection: FullEngineConnection) => { if (session && identity.current === session.payload) active.current?.attach(connection); },
    onDirty: (event: FullEngineDirty) => active.current?.onDirty(event),
    save: () => active.current?.retry() ?? Promise.resolve(false),
    restoreDraft: (raw?: string) => active.current?.restoreDraft(raw) ?? Promise.resolve(false),
    discardDraft: () => active.current?.discardDraft(),
    // Capture the controller, not the mutable ref: an old export must never flush a new project.
    exportSession: () => {
      const controller = session && identity.current === session.payload ? active.current : null;
      return controller ? {
        flush: () => controller.flush(),
        readState: () => {
          const current = controller.getState();
          return { revision: current.revision, hasUnsaved: controller.hasUnsaved(), conflict: current.status === 'conflict',
            saving: current.status === 'saving', error: current.status === 'error' ? current.message : undefined };
        },
        snapshot: () => controller.getExportSnapshot(),
        beginSaveAs: (expected: { generation: number; engineRevision: number }) => controller.beginSaveAs(expected),
        canLeaveForSaveAs: (target: string) => controller.canLeaveForSaveAs(target),
        saveAsPending: () => controller.saveAsPending(),
        projectCurrentSnapshot: (expected: { generation: number; engineRevision: number }) => controller.projectCurrentSnapshot(expected),
      } : null;
    },
    onSaveRequest: () => { void active.current?.retry(); },
    onCloseRequest: async () => {
      const clean = await (active.current?.flush() ?? Promise.resolve(true));
      if (clean && !(active.current?.hasUnsaved() ?? false)) window.close();
    },
    abandonForReload: () => active.current?.abandonForReload(),
  };
}
