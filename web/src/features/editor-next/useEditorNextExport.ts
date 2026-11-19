import { useEffect, useRef, useState } from 'react';
import { createEditorNextExportController, initialExportState } from './exportBridge';
import type { EditorNextExportController, ExportSaveSession, ExportSelection } from './exportBridge';

interface ExportRequestBridge {
  onExportRequest?: (callback: (event: { reason?: string }) => void) => () => void;
}
export function useEditorNextExport(options: {
  clipId: string | null; independentProject?: boolean; identity: object | null; ready: boolean; getSaveSession: () => ExportSaveSession | null;
}) {
  const active = useRef<{ clipId: string; identity: object; controller: EditorNextExportController } | null>(null);
  const latest = useRef(options); latest.current = options;
  const [state, setState] = useState(initialExportState);
  const [bridgeWarning, setBridgeWarning] = useState<string | null>(null);
  useEffect(() => {
    setState(initialExportState()); setBridgeWarning(null);
    if (!options.ready || !options.clipId || !options.identity) return;
    const save = latest.current.getSaveSession();
    if (!save) { setBridgeWarning('保存会话尚未就绪，导出已禁用。'); return; }
    const identity = options.identity, clipId = options.clipId;
    const controller = createEditorNextExportController({ clipId, save, independentProject: options.independentProject, onState: next => {
      if (latest.current.clipId === clipId && latest.current.identity === identity && latest.current.ready) setState(next);
    } });
    active.current = { clipId, identity, controller };
    const bridge = (window as unknown as { FCBridge?: ExportRequestBridge }).FCBridge;
    let unsubscribe: (() => void) | undefined;
    if (bridge?.onExportRequest) {
      unsubscribe = bridge.onExportRequest(event => {
        if (active.current?.controller !== controller || latest.current.clipId !== clipId || latest.current.identity !== identity || !latest.current.ready) return;
        setBridgeWarning(event.reason === 'frame' ? '单帧导出未实现(浏览器内编码,未接入)；此面板仅提供后端视频导出。' : null);
        void controller.open();
      });
    } else setBridgeWarning('引擎导出回调未就绪；请更新引擎资源后使用菜单导出。宿主按钮仍使用后端渲染。');
    return () => {
      unsubscribe?.(); controller.dispose();
      if (active.current?.controller === controller) active.current = null;
    };
  }, [options.clipId, options.identity, options.ready]);
  const controller = () => latest.current.ready && active.current?.clipId === latest.current.clipId &&
    active.current?.identity === latest.current.identity ? active.current.controller : null;
  const scoped = controller();
  return {
    state: scoped ? state : initialExportState(), bridgeWarning: scoped ? bridgeWarning : null,
    open: () => controller()?.open() ?? Promise.resolve(false),
    close: () => controller()?.close(),
    start: (selection: ExportSelection) => controller()?.start(selection) ?? Promise.resolve(false),
    cancel: () => controller()?.cancel() ?? Promise.resolve(),
    downloadUrl: controller()?.downloadUrl() ?? null,
    invalidate: () => { active.current?.controller.dispose(); active.current = null; setState(initialExportState()); },
  };
}
