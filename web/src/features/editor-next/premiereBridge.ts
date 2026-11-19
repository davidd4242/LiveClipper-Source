import { exportPremiere } from '../../api/client';
import type { PremiereExportResult } from '../../api/client';
import type { CopyStamp, CopySaveSession } from './copyBridge';
export interface PremiereState { open: boolean; busy: boolean; normalizeVariableFrameRate: boolean; name: string; error: string | null; result: PremiereExportResult | null }
export const initialPremiereState = (): PremiereState => ({ open: false, busy: false, normalizeVariableFrameRate: false, name: '', error: null, result: null });
export function createEditorNextPremiereController(options: {
  clipId: string; epoch: string; name: string; save: CopySaveSession;
  api?: typeof exportPremiere; onState?: (state: PremiereState) => void;
}) {
  let state = initialPremiereState(), stamp: CopyStamp | null = null, disposed = false;
  const publish = (patch: Partial<PremiereState>) => {
    if (disposed) return;
    state = { ...state, ...patch }; options.onState?.({ ...state });
  };
  return {
    getState: () => ({ ...state }),
    open(expected: CopyStamp) {
      if (disposed || state.busy) return;
      stamp = { ...expected };
      publish({ open: true, normalizeVariableFrameRate: false, name: `${options.name} Premiere 交换包`, error: null, result: null });
    },
    setNormalizeVariableFrameRate: (value: boolean) => { if (!state.busy) publish({ normalizeVariableFrameRate: value }); },
    setName: (name: string) => { if (!state.busy) publish({ name }); },
    close: () => { if (!state.busy) publish({ open: false }); },
    async create() {
      if (disposed || state.busy || !stamp) return false;
      const name = state.name.trim();
      if (!name) { publish({ error: '请输入Premiere 交换包名称。' }); return false; }
      publish({ busy: true, error: null, result: null });
      try {
        const saved = options.save.readState();
        if (saved.saving) throw new Error('原工程正在保存，请稍后重新选择“保存Premiere 交换包”。');
        if (saved.conflict) throw new Error('原工程存在保存冲突，请处理后重新创建Premiere 交换包。');
        if (saved.revision === null || !options.epoch) throw new Error('缺少工程修订或纪元，未创建Premiere 交换包。');
        const projection = options.save.projectCurrentSnapshot(stamp);
        if (!projection.ok) throw new Error('当前编辑内容无法完整映射，未创建Premiere 交换包。');
        if (projection.project.timeline?.length === 0) throw new Error('当前时间线为空，不能导出交换工程。请先添加片段。');
        // Only read the live projection. Never flush, save, or acknowledge the original.
        const result = await (options.api ?? exportPremiere)(options.clipId, name, projection.project, saved.revision, options.epoch, state.normalizeVariableFrameRate);
        publish({ busy: false, result });
        return true;
      } catch (error) {
        const value = error as { status?: number; message?: string };
        const message = value.status === 409 ? '原工程修订或纪元已变化，未创建Premiere 交换包；请重新载入后再试。'
          : value.message === 'stale_engine_revision' ? '编辑内容已变化，请关闭后重新选择“保存Premiere 交换包”。'
          : value.message || String(error);
        publish({ busy: false, error: message }); return false;
      }
    },
    dispose() { disposed = true; },
  };
}
