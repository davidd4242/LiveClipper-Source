import type { FullEngineDocument } from '../../engine/fullEngineSession';

export const EDITOR_NEXT_DRAFT_PREFIX = 'liveclipper:editor-next:draft:v1:';
export const EDITOR_NEXT_DRAFT_THROTTLE_MS = 750;
export interface EditorNextDraft {
  version: 1;
  clipId: string;
  baseRevision: number;
  epoch: string;
  updatedAt: number;
  document: FullEngineDocument;
}
export interface EditorNextDraftEntry { key: string; raw: string; draft: EditorNextDraft | null }
export type EditorNextDraftStorage = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
const clipPrefix = (clipId: string) => `${EDITOR_NEXT_DRAFT_PREFIX}${encodeURIComponent(clipId)}:`;
export const editorNextDraftKey = (clipId: string, baseRevision: number, epoch: string) =>
  `${clipPrefix(clipId)}${baseRevision}:${encodeURIComponent(epoch)}`;
const record = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
export function parseEditorNextDraft(raw: string, key: string, clipId: string): EditorNextDraft | null {
  try {
    const value: unknown = JSON.parse(raw);
    if (!record(value) || value.version !== 1 || value.clipId !== clipId || !Number.isSafeInteger(value.baseRevision) || Number(value.baseRevision) < 0 ||
      typeof value.epoch !== 'string' || !value.epoch || typeof value.updatedAt !== 'number' || !Number.isFinite(value.updatedAt) ||
      !record(value.document) || value.document.__fcproj !== 1 || !record(value.document.sequence) ||
      ![editorNextDraftKey(clipId, Number(value.baseRevision), value.epoch),editorNextDraftKey(clipId, Number(value.baseRevision), value.epoch)+':backup-file'].includes(key)) return null;
    return value as unknown as EditorNextDraft;
  } catch { return null; }
}
export function isEditorNextDraftCurrent(entry: EditorNextDraftEntry, revision: number | null, epoch: string | undefined): boolean {
  return !!entry.draft && entry.draft.baseRevision === revision && entry.draft.epoch === epoch;
}
export function isEditorNextDraftRecoverable(entry: EditorNextDraftEntry, clipId: string, revision: number | null, epoch: string | undefined): boolean {
  return !!entry.draft && entry.draft.clipId === clipId && Number.isSafeInteger(revision) && revision! >= 0 &&
    entry.draft.baseRevision === revision && typeof epoch === 'string' && epoch.length > 0;
}

function sameDraftContent(left: unknown, right: unknown, root = true): boolean {
  if (left === right) return true;
  if (Array.isArray(left) && Array.isArray(right)) return left.length === right.length && left.every((value, index) => sameDraftContent(value, right[index], false));
  if (!record(left) || !record(right)) return false;
  // FC documentFor stamps every snapshot; nested savedAt and all archived content still count.
  const keys = (value: Record<string, unknown>) => Object.keys(value).filter(key => !root || key !== 'savedAt');
  const leftKeys = keys(left), rightKeys = keys(right);
  return leftKeys.length === rightKeys.length && leftKeys.every(key => Object.hasOwn(right, key) && sameDraftContent(left[key], right[key], false));
}

export function createEditorNextDraftStore(options: { storage?: EditorNextDraftStorage | null; onError: (message: string) => void }) {
  const storage = () => {
    const value = options.storage === undefined ? globalThis.localStorage : options.storage;
    if (!value) throw new Error('storage_unavailable');
    return value;
  };
  const failed = (action: string) => options.onError(`本地草稿${action}失败（浏览器存储不可用或空间不足）；请保持页面打开并手动保存。`);
  return {
    backup(raw: string, clipId: string, revision: number | null): EditorNextDraftEntry {
      const data=JSON.parse(raw);
      if(data?.clipId!==clipId || data.baseRevision!==revision) throw new Error('备份与当前工程或基础修订不一致');
      const key=editorNextDraftKey(clipId,data.baseRevision,data.epoch);
      const draft=parseEditorNextDraft(raw,key,clipId);
      if(!draft) throw new Error('不是完整的原生工程草稿备份');
      return {key,raw,draft};
    },
    list(clipId: string): EditorNextDraftEntry[] {
      try {
        const target = storage(), entries: EditorNextDraftEntry[] = [];
        for (let index = 0; index < target.length; index++) {
          const key = target.key(index);
          if (!key?.startsWith(clipPrefix(clipId))) continue;
          const raw = target.getItem(key);
          if (raw !== null) entries.push({ key, raw, draft: parseEditorNextDraft(raw, key, clipId) });
        }
        return entries.sort((a, b) => (b.draft?.updatedAt ?? 0) - (a.draft?.updatedAt ?? 0));
      } catch { failed('读取'); return []; }
    },
    write(draft: EditorNextDraft, fromBackup=false): EditorNextDraftEntry | null {
      try {
        const key = editorNextDraftKey(draft.clipId, draft.baseRevision, draft.epoch) + (fromBackup ? ":backup-file" : ""), raw = JSON.stringify(draft);
        if (!parseEditorNextDraft(raw, key, draft.clipId)) throw new Error('draft_invalid');
        storage().setItem(key, raw);
        return { key, raw, draft };
      } catch { failed('写入'); return null; }
    },
    removeSaved(entry: EditorNextDraftEntry, revision: number | null, epoch: string | undefined, document: unknown): boolean {
      if (!entry.draft || !Number.isSafeInteger(revision) || revision! <= entry.draft.baseRevision || entry.draft.epoch !== epoch) return false;
      try {
        if (!record(document) || document.__fcproj !== 1 || !record(document.sequence) || !sameDraftContent(entry.draft.document, document)) return false;
      } catch { return false; }
      return this.remove(entry);
    },
    remove(entry: EditorNextDraftEntry): boolean {
      try {
        const target = storage();
        // A delayed acknowledgement must not delete a newer write from another tab.
        if (target.getItem(entry.key) === entry.raw) target.removeItem(entry.key);
        return true;
      } catch { failed('清除'); return false; }
    },
  };
}
