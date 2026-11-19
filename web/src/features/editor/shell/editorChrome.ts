/**
 * 编辑器外壳（P0）共用的小工具：保存态文案、连接态文案。
 *
 * 这里不做任何数据获取；调用方（EditorTopbar / EditorStatusbar）只传
 * EditorPage 已有状态，拿不到的领域显示「—」，不伪造。
 */

import type { SaveConflict } from '../saveQueue';
import type { RecoveredDraft } from '../useEditorSession';

export type EditorSaveState = 'clean' | 'dirty' | 'saving' | 'error';

export type EditorLoadState = 'loading' | 'ready' | 'empty' | 'error';

/** 保存态文案：与旧 StatusBar 保持一致（已保存 · 时间 / 保存中… / 未保存的修改 / 保存失败）。 */
export function savedLabel(state: EditorSaveState, date: Date | null): string {
  if (state === 'saving') return '保存中…';
  if (state === 'dirty') return '未保存的修改';
  if (state === 'error') return '保存失败';
  if (!date) return '已保存';
  return `已保存 · ${date.toLocaleTimeString('zh-CN', { hour12: false })}`;
}

/** 保存态语义标签（title / aria 用）。 */
export function saveStateTitle(state: EditorSaveState): string {
  switch (state) {
    case 'saving':
      return '正在自动保存工程';
    case 'dirty':
      return '有修改尚未保存，稍后会自动保存';
    case 'error':
      return '自动保存失败，可从「文件」菜单重试';
    default:
      return '工程已保存';
  }
}

export interface EditorSaveStatusInput {
  loadState: EditorLoadState;
  saveState: EditorSaveState;
  lastSavedAt: Date | null;
  saveError: string | null;
  saveConflict: Pick<SaveConflict, 'message'> | null;
  recoveredDraft: Pick<RecoveredDraft, 'stale'> | null;
  hasUnsavedChanges: boolean;
}

export interface EditorSaveStatus {
  state: 'clean' | 'saving' | 'conflict' | 'draft' | 'error' | 'idle';
  label: string;
  title: string;
}

/** Session facts only: a clean initial hook state does not prove an unloaded project is saved. */
export function editorSaveStatus({
  loadState,
  saveState,
  lastSavedAt,
  saveError,
  saveConflict,
  recoveredDraft,
  hasUnsavedChanges,
}: EditorSaveStatusInput): EditorSaveStatus {
  if (saveConflict) {
    return {
      state: 'conflict',
      label: '保存冲突',
      title: saveError ?? `保存冲突：${saveConflict.message}；本地修改已保留，请选择覆盖服务器或重新读取`,
    };
  }
  if (saveState === 'saving') {
    return { state: 'saving', label: '保存中…', title: saveStateTitle(saveState) };
  }
  if (saveState === 'error') {
    return { state: 'error', label: '保存失败', title: saveError ?? saveStateTitle(saveState) };
  }
  if (recoveredDraft) {
    return {
      state: 'draft',
      label: '草稿待恢复',
      title: recoveredDraft.stale
        ? '有本地草稿待恢复；服务器已有修改，请先确认再恢复'
        : '有上次未保存的本地草稿，可恢复或丢弃',
    };
  }
  if (saveState === 'dirty' || hasUnsavedChanges) {
    return { state: 'draft', label: '草稿 · 未保存', title: '有修改尚未保存到服务器' };
  }
  if (loadState === 'error') {
    return { state: 'error', label: '保存状态不可用', title: '工程载入失败，无法确认保存状态' };
  }
  if (loadState !== 'ready') {
    return {
      state: 'idle',
      label: loadState === 'loading' ? '载入中…' : '无工程',
      title: loadState === 'loading' ? '正在载入工程，尚未确认保存状态' : '未载入可保存的工程',
    };
  }
  return { state: 'clean', label: savedLabel('clean', lastSavedAt), title: saveStateTitle('clean') };
}

/** 后端连接态文案：由 EditorPage 的 loadState 直接映射，不新造探测。 */
export function connectionLabel(state: EditorLoadState): string {
  if (state === 'loading') return '连接中…';
  if (state === 'error') return '连接失败';
  return '已连接';
}

/** 连接态语义标签。 */
export function connectionTone(state: EditorLoadState): 'ok' | 'warn' | 'error' {
  if (state === 'loading') return 'warn';
  if (state === 'error') return 'error';
  return 'ok';
}

/** 拿不到的领域值统一显示「—」。 */
export function orDash(value: string | number | null | undefined): string {
  if (value === null || value === undefined) return '—';
  if (typeof value === 'string' && value.length === 0) return '—';
  return String(value);
}
