import { exportJianying } from '../../api/client';
import type { JianyingExportResult } from '../../api/client';
import type { CopyStamp, CopySaveSession } from './copyBridge';
export interface JianyingState { open: boolean; busy: boolean; name: string; error: string | null; result: JianyingExportResult | null }
export const initialJianyingState = (): JianyingState => ({ open: false, busy: false, name: '', error: null, result: null });
export function createEditorNextJianyingController(options: {
  clipId: string; epoch: string; name: string; save: CopySaveSession;
  api?: typeof exportJianying; onState?: (state: JianyingState) => void;
}) {
  let state = initialJianyingState(), stamp: CopyStamp | null = null, disposed = false;
  const publish = (patch: Partial<JianyingState>) => {
    if (disposed) return;
    state = { ...state, ...patch }; options.onState?.({ ...state });
  };
  return {
    getState: () => ({ ...state }),
    open(expected: CopyStamp) {
      if (disposed || state.busy) return;
      stamp = { ...expected };
      publish({ open: true, name: `${options.name} 剪映 v3 候选草稿`, error: null, result: null });
    },
    setName: (name: string) => { if (!state.busy) publish({ name }); },
    close: () => { if (!state.busy) publish({ open: false }); },
    async create() {
      if (disposed || state.busy || !stamp) return false;
      const name = state.name.trim();
      if (!name) { publish({ error: '请输入剪映 v3 候选草稿名称。' }); return false; }
      publish({ busy: true, error: null, result: null });
      try {
        const saved = options.save.readState();
        if (saved.saving) throw new Error('原工程正在保存，请稍后重新选择“保存剪映 v3 候选草稿”。');
        if (saved.conflict) throw new Error('原工程存在保存冲突，请处理后重新创建剪映 v3 候选草稿。');
        if (saved.revision === null || !options.epoch) throw new Error('缺少工程修订或纪元，未创建剪映 v3 候选草稿。');
        const projection = options.save.projectCurrentSnapshot(stamp);
        if (!projection.ok) throw new Error('当前编辑内容无法完整映射，未创建剪映 v3 候选草稿。');
        if (projection.project.timeline?.length === 0) throw new Error('当前时间线为空，不能导出交换工程。请先添加片段。');
        // Only read the live projection. Never flush, save, or acknowledge the original.
        const result = await (options.api ?? exportJianying)(options.clipId, name, projection.project, saved.revision, options.epoch);
        publish({ busy: false, result });
        return true;
      } catch (error) {
        const value = error as { status?: number; message?: string };
        const message = value.status === 409 ? '原工程修订或纪元已变化，未创建剪映 v3 候选草稿；请重新载入后再试。'
          : value.message === 'stale_engine_revision' ? '编辑内容已变化，请关闭后重新选择“保存剪映 v3 候选草稿”。'
          : value.message || String(error);
        publish({ busy: false, error: message }); return false;
      }
    },
    dispose() { disposed = true; },
  };
}
