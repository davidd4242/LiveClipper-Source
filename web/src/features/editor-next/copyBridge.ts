import { copyEditor } from '../../api/client';
import type { CopyEditorResult } from '../../api/client';
import type { ReplicaReverseResult } from '../../engine/replicaSnapshotToProject';
export interface CopyStamp { generation: number; engineRevision: number }
export interface SaveAsHold { permit(target: string): void; complete(): void; release(): void }
export interface CopySaveSession {
  beginSaveAs?(expected: CopyStamp): SaveAsHold;
  readState(): { revision: number | null; saving?: boolean; conflict: boolean; error?: string };
  projectCurrentSnapshot(expected: CopyStamp): ReplicaReverseResult;
}
export interface CopyState { mode: 'copy' | 'saveAs'; open: boolean; busy: boolean; name: string; error: string | null; result: CopyEditorResult | null }
export const initialCopyState = (): CopyState => ({ mode: 'copy', open: false, busy: false, name: '', error: null, result: null });
export function createEditorNextCopyController(options: {
  clipId: string; epoch: string; name: string; save: CopySaveSession;
  switchTo?: (result: CopyEditorResult, hold: SaveAsHold) => Promise<boolean>;
  api?: typeof copyEditor; onState?: (state: CopyState) => void;
}) {
  let hold: SaveAsHold | null = null;
  let switching = false;
  let state = initialCopyState(), stamp: CopyStamp | null = null, disposed = false;
  const publish = (patch: Partial<CopyState>) => {
    if (disposed) return;
    state = { ...state, ...patch }; options.onState?.({ ...state });
  };
  return {
    getState: () => ({ ...state }),
    open(expected: CopyStamp, mode: 'copy' | 'saveAs' = 'copy') {
      if (disposed || state.busy) return;
      hold?.release(); hold = null; stamp = null;
      try {
        if (mode === 'saveAs') {
          if (!options.save.beginSaveAs) throw new Error('save_as_not_available');
          hold = options.save.beginSaveAs(expected);
        }
      } catch (error) { publish({ open: true, mode, error: String(error), result: null }); return; }
      stamp = { ...expected };
      publish({ open: true, mode, name: `${options.name} 副本`, error: null, result: null });
    },
    setName: (name: string) => { if (!state.busy) publish({ name }); },
    close: () => { if (!state.busy) { hold?.release(); hold = null; publish({ open: false }); } },
    async create() {
      if (disposed || state.busy || !stamp || state.result) return false;
      const name = state.name.trim();
      if (!name) { publish({ error: '请输入副本名称。' }); return false; }
      publish({ busy: true, error: null, result: null });
      try {
        const saved = options.save.readState();
        if (saved.saving) throw new Error('原工程正在保存，请稍后重新选择“保存副本”。');
        if (saved.conflict) throw new Error('原工程存在保存冲突，请处理后重新创建副本。');
        if (saved.revision === null || !options.epoch) throw new Error('缺少工程修订或纪元，未创建副本。');
        const projection = options.save.projectCurrentSnapshot(stamp);
        if (!projection.ok) throw new Error('当前编辑内容无法完整映射，未创建副本。');
        // Only read the live projection. Never flush, save, or acknowledge the original.
        const result = await (options.api ?? copyEditor)(options.clipId, name, projection.project, saved.revision, options.epoch);
        if (disposed) { hold?.release(); hold = null; return false; }
        publish({ result });
        if (state.mode === 'saveAs') {
          if (!hold || !options.switchTo) throw new Error('save_as_switch_unavailable');
          const activeHold = hold;
          switching = true;
          const switched = await options.switchTo(result, activeHold);
          switching = false;
          if (!switched) throw new Error('save_as_copy_created_switch_blocked');
          activeHold.complete(); hold = null;
        }
        publish({ busy: false });
        return true;
      } catch (error) {
        switching = false; hold?.release(); hold = null;
        const value = error as { status?: number; message?: string };
        const message = value.status === 409 ? '原工程修订或纪元已变化，未创建副本；请重新载入后再试。'
          : value.message === 'stale_engine_revision' ? '编辑内容已变化，请关闭后重新选择“保存副本”。'
          : value.message || String(error);
        publish({ busy: false, error: message }); return false;
      }
    },
    dispose() { disposed = true; if (!switching) { hold?.release(); hold = null; } },
  };
}
