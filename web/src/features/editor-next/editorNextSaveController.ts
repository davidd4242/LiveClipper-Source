import type { EditorPayload } from '../../api/types';
import type { FullEngineConnection, FullEngineDirty, FullEngineDocument } from '../../engine/fullEngineSession';
import { applyHostPreserveWindows, cloneReplicaJson, replicaSnapshotToProject, validateReplicaExtension } from '../../engine/replicaSnapshotToProject';
import type { ReplicaEditorProject, ReplicaProjectionBase, ReplicaSaveWarning } from '../../engine/replicaSnapshotToProject';
import { createSaveQueue, SAVE_KEEPALIVE_LIMIT, utf8Bytes } from '../editor/saveQueue';
import { createEditorJsonRequest } from '../editor/api/commandSessionClient';
import type { EditorJsonRequest } from '../editor/api/commandSessionClient';
import { createEditorNextDraftStore, isEditorNextDraftCurrent, isEditorNextDraftRecoverable, EDITOR_NEXT_DRAFT_THROTTLE_MS } from './editorNextDraftStorage';
import type { EditorNextDraftEntry, EditorNextDraftStorage } from './editorNextDraftStorage';

export type EditorNextSaveStatus = 'saved' | 'saving' | 'unsaved' | 'conflict' | 'error';
export interface EditorNextDraftNotice { status: 'available' | 'expired' | 'invalid' | 'restoring' | 'blocked'; message: string }
export interface EditorNextSaveState {
  status: EditorNextSaveStatus;
  revision: number | null;
  message: string;
  warnings: string[];
  archiveWarnings: ReplicaSaveWarning[];
  draft: EditorNextDraftNotice | null;
  draftError: string;
}
export interface EditorNextSaveAck { generation: number; engineRevision: number; revision: number }
export interface EditorNextAckBridge {
  ackSaved?: (ack: EditorNextSaveAck) => { ok: boolean; dirty?: boolean; reason?: string } | Promise<{ ok: boolean; dirty?: boolean; reason?: string }>;
}
interface Stamp { generation: number; engineRevision: number }
interface SaveTicket { serial: number; stamp?: Stamp; document?: unknown; project?: ReplicaEditorProject }
interface SaveResponse { saved: boolean; revision?: number; epoch?: string }
export interface EditorNextSaveOptions {
  clipId: string;
  payload: EditorPayload;
  registeredSourceIds: readonly string[];
  request?: EditorJsonRequest;
  getBridge?: () => EditorNextAckBridge | undefined;
  onState?: (state: EditorNextSaveState) => void;
  debounceMs?: number;
  draftStorage?: EditorNextDraftStorage | null;
}
const stampOf = (value: { generation?: number; engineRevision?: number }): Stamp => {
  if (!Number.isSafeInteger(value.generation) || !Number.isSafeInteger(value.engineRevision) || value.generation! < 0 || value.engineRevision! < 0) throw new Error('engine_snapshot_revision_missing');
  return { generation: value.generation!, engineRevision: value.engineRevision! };
};
const matches = (a: Stamp | null | undefined, b: Stamp | null | undefined) => !!a && !!b && a.generation === b.generation && a.engineRevision === b.engineRevision;

/** Queue metadata only; FCBridge remains the sole editable document owner. */
export function createEditorNextSaveController(options: EditorNextSaveOptions) {
  const { clipId } = options;
  const request = options.request ?? createEditorJsonRequest();
  const epoch = options.payload.epoch;
  let connection: FullEngineConnection | null = null;
  let ownerSerial = 0;
  let base: ReplicaProjectionBase | null = null;
  let confirmed: Stamp | null = null;
  let serial = 0;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let draftTimer: ReturnType<typeof setTimeout> | null = null;
  let disposed = false;
  let abandoned = false;
  let saveAsHold: { stamp: Stamp; target: string | null; departing: boolean } | null = null;
  let engineDirty = false;
  let flushing = 0;
  let leaving = false;
  let leaveBudget = SAVE_KEEPALIVE_LIMIT;
  let recovering = false;
  let candidate: EditorNextDraftEntry | null = null;
  let preserveBackupRevision: number | null = null;
  let restoredDraft: EditorNextDraftEntry | null = null;
  const ownedDrafts = new Map<string, EditorNextDraftEntry>();
  let state: EditorNextSaveState = { status: 'saved', revision: options.payload.revision ?? null, message: '', warnings: [], archiveWarnings: [], draft: null, draftError: '' };
  // 导出面板只列「…archived_not_rendered」这一条：每个仅预览图形一条，消息里已带模板中文名、种类和片段 id。
  // 同一次导出里 <code>:<kind> 那条是保存期的预览提示，从归档列表里排除，避免同一图形出现两遍。
  const archivedOnly = (issue: ReplicaSaveWarning) => issue.code.endsWith('_archived_not_rendered');
  const archiveWarnings = (issues: ReplicaSaveWarning[]) => cloneReplicaJson(issues.filter(archivedOnly));
  const stateCopy = () => ({ ...state, draft: state.draft ? { ...state.draft } : null, warnings: [...state.warnings], archiveWarnings: cloneReplicaJson(state.archiveWarnings) });
  const notify = (patch: Partial<EditorNextSaveState> = {}) => {
    if (disposed) return;
    state = { ...state, ...patch, revision: queue.getRevision(clipId) };
    options.onState?.(stateCopy());
  };
  const warning = (message: string) => {
    if (!state.warnings.includes(message)) state = { ...state, warnings: [...state.warnings, message] };
  };
  const drafts = createEditorNextDraftStore({ storage: options.draftStorage, onError: draftError => notify({ draftError }) });
  const cancelDebounce = () => { if (timer !== null) clearTimeout(timer); timer = null; };
  const cancelDraftTimer = () => { if (draftTimer !== null) clearTimeout(draftTimer); draftTimer = null; };
  const read = () => {
    if (!connection) throw new Error('engine_not_ready');
    const snapshot = connection.snapshot();
    if (!snapshot.ok || snapshot.document === undefined) throw new Error(snapshot.error ?? 'engine_snapshot_missing');
    return snapshot;
  };
  const hasUnsaved = (): boolean => {
    if (recovering || queue.hasUnsaved(clipId)) return true;
    if (!connection || disposed) return engineDirty;
    try {
      const current = read();
      return (engineDirty || current.dirty === true) && !matches(stampOf(current), confirmed);
    } catch { return true; }
  };
  const persistDraft = () => {
    cancelDraftTimer();
    if (disposed || abandoned || candidate || recovering || !connection || !hasUnsaved()) return;
    try {
      const revision = queue.getRevision(clipId);

      if (!Number.isSafeInteger(revision) || revision! < 0 || typeof epoch !== 'string' || !epoch) throw new Error('missing_revision_or_epoch');
      const snapshot = read();
      const entry = drafts.write({ version: 1, clipId, baseRevision: revision!, epoch, updatedAt: Date.now(), document: cloneReplicaJson(snapshot.document) as FullEngineDocument }, preserveBackupRevision === revision);
      if (entry) {
        for (const [key, old] of ownedDrafts) if (key !== entry.key && drafts.remove(old)) ownedDrafts.delete(key);
        ownedDrafts.set(entry.key, entry);
        notify({ draftError: '' });
      }
    } catch { notify({ draftError: '本地草稿写入失败：快照或服务器修订不可用；请保持页面打开并手动保存。' }); }
  };
  const clearOwnedDrafts = () => {
    cancelDraftTimer();
    for (const [key, entry] of ownedDrafts) if (drafts.remove(entry)) ownedDrafts.delete(key);
    if (restoredDraft && drafts.remove(restoredDraft)) restoredDraft = null;
  };
  const scheduleDraft = () => {
    if (draftTimer === null) draftTimer = setTimeout(() => { draftTimer = null; persistDraft(); }, EDITOR_NEXT_DRAFT_THROTTLE_MS);
  };
  const queue = createSaveQueue<SaveTicket>({
    async send(id, ticket, baseRevision) {
      if (disposed || !base || candidate || recovering) throw new Error('engine_save_session_inactive');
      if (!Number.isSafeInteger(baseRevision) || baseRevision! < 0) throw new Error('缺少服务器 revision，需重新载入；未发送无条件写入。');
      if (typeof epoch !== 'string' || !epoch) throw new Error('缺少服务器 epoch，需重新载入；未发送无纪元写入。');
      const snapshot = read();
      ticket.stamp = stampOf(snapshot);
      ticket.document = cloneReplicaJson(snapshot.document);
      const reversed = replicaSnapshotToProject(ticket.document, base);
      for (const issue of reversed.warnings) if (!issue.code.endsWith('_archived_not_rendered')) warning(`${issue.message}（${issue.path}）`);
      for (const issue of snapshot.warnings ?? []) warning(`引擎提示：${issue}`);
      notify();
      if (!reversed.ok) throw new Error(reversed.warnings.map(issue => issue.message).join('；'));
      ticket.project = reversed.project;
      const body = JSON.stringify(ticket.project), keepalive = leaving;
      if (keepalive) {
        const bytes = utf8Bytes(body);
        if (bytes > leaveBudget) throw new Error('工程超过离开保存的大小上限；本地草稿已保留，请返回页面手动保存。');
        leaveBudget -= bytes;
      }
      const result = await request<SaveResponse>(`/api/studio/clips/${encodeURIComponent(id)}/editor`, {
        method: 'PUT', headers: { 'X-Base-Revision': String(baseRevision), 'X-Editor-Epoch': epoch }, body,
        ...(keepalive ? { keepalive: true } : {}),
      });
      if (result.saved !== true || !Number.isSafeInteger(result.revision) || result.revision! <= baseRevision!) throw new Error('服务器未确认新的 revision；保留未保存状态。');
      if (result.epoch !== undefined && result.epoch !== epoch) throw Object.assign(new Error('服务器纪元已变化，请重新载入。'), { status: 409, code: 'stale-epoch', revision: result.revision });
      if (disposed) return result;
      // Advance only after confirmed PUT; never substitute a conflicting server revision.
      base = { ...base, project: cloneReplicaJson(ticket.project), document: cloneReplicaJson(ticket.document) };
      confirmed = ticket.stamp;
      state = { ...state, archiveWarnings: archiveWarnings(reversed.warnings) };
      try { engineDirty = !matches(stampOf(read()), confirmed); }
      catch { engineDirty = true; warning('服务器已确认保存，但引擎状态读取失败；关闭时保持未保存判断。'); }
      const bridge = options.getBridge?.();
      if (!bridge?.ackSaved) warning('引擎暂不提供 ackSaved；服务器已确认保存，使用代次与修订号比对，未强制清除引擎脏标记。');
      else {
        try {
          const ack = await bridge.ackSaved({ ...ticket.stamp, revision: result.revision! });
          if (!ack?.ok) warning(`引擎保存确认未接纳：${ack?.reason ?? 'unknown'}；保留较新编辑的脏状态。`);
        } catch (error) { warning(`引擎保存确认失败：${error instanceof Error ? error.message : String(error)}；未强制清除脏标记。`); }
      }
      return result;
    },
    onEvent(event) {
      if (disposed) return;
      if (event.type === 'saving') notify({ status: 'saving', message: '正在写入工程…' });
      if (event.type === 'saved') {
        if (hasUnsaved()) persistDraft(); else clearOwnedDrafts();
        notify({ status: hasUnsaved() ? 'unsaved' : 'saved', message: '' });
      }
      if (event.type === 'error') { persistDraft(); notify({ status: 'error', message: event.message }); }
      if (event.type === 'conflict') {
        cancelDebounce(); persistDraft();
        notify({ status: 'conflict', message: `版本冲突：${event.conflict.message}。本地修改已保留；只能重新载入或放弃本地修改，不会覆盖服务器。` });
      }
    },
  });
  queue.setRevision(clipId, Number.isSafeInteger(options.payload.revision) ? options.payload.revision! : null);
  const clearSavedDrafts = () => {
    const entries = drafts.list(clipId);
    if (!entries.length) return;
    let document: unknown;
    try {
      const extension = (options.payload.project as unknown as Record<string, unknown>).replicaExtension;
      if (!extension) return;
      document = validateReplicaExtension(extension).document;
    } catch { return; }
    for (const entry of entries) drafts.removeSaved(entry, queue.getRevision(clipId), epoch, document);
  };
  clearSavedDrafts();
  const refreshCandidate = () => {
    const entries = drafts.list(clipId);
    candidate = entries.find(entry => isEditorNextDraftCurrent(entry, queue.getRevision(clipId), epoch)) ??
      entries.find(entry => isEditorNextDraftRecoverable(entry, clipId, queue.getRevision(clipId), epoch)) ?? entries[0] ?? null;
    notify({ draft: !candidate ? null : !candidate.draft ? { status: 'invalid', message: '本地草稿损坏，不能恢复；可丢弃后继续编辑。' }
      : isEditorNextDraftCurrent(candidate, queue.getRevision(clipId), epoch) ? { status: 'available', message: '发现未保存的本地草稿。恢复后将通过版本校验保存；也可丢弃后继续。' }
        : isEditorNextDraftRecoverable(candidate, clipId, queue.getRevision(clipId), epoch) ? { status: 'available', message: '发现上次会话的未保存草稿，服务器版本仍一致。仅在点击恢复后校验来源与文档，并在当前会话载入、校验保存；不会自动应用。' }
          : { status: 'expired', message: '草稿已过期：服务器版本已变化或当前会话纪元不可用，不会自动应用；可丢弃后继续编辑。' } });
  };
  refreshCandidate();
  const stage = () => {
    if (disposed || candidate || recovering) return;
    queue.stage(clipId, { serial: ++serial });
    scheduleDraft();
    notify({ status: queue.conflict(clipId) ? 'conflict' : 'unsaved', ...(queue.conflict(clipId) ? {} : { message: '' }) });
  };
  const flush = async (retryErrors = false): Promise<boolean> => {
    if (disposed || abandoned || candidate || recovering || saveAsHold) return false;
    cancelDebounce();
    // A bridge can detect mutations at snapshot time before an onDirty notification arrives.
    if (!queue.hasUnsaved(clipId) && hasUnsaved()) stage();
    persistDraft();
    flushing++;
    try {
      const flushed = await queue.flush({ retryErrors });
      const clean = flushed && !hasUnsaved();
      if (state.status !== 'conflict' && state.status !== 'error') notify({ status: clean ? 'saved' : 'unsaved' });
      return clean;
    } finally { flushing--; }
  };
  return {
    attach(value: FullEngineConnection) {
      if (disposed) return;
      ownerSerial++; connection = value; recovering = false;
      if (candidate) refreshCandidate();
      try {
        const snapshot = read();
        confirmed = stampOf(snapshot);
        base = { project: cloneReplicaJson(options.payload.project), document: cloneReplicaJson(snapshot.document), registeredSourceIds: options.registeredSourceIds, independentProject: !!options.payload.projectIdentity };
        engineDirty = snapshot.dirty === true;
        if (engineDirty) { confirmed = null; stage(); }
        else notify({ status: 'saved', archiveWarnings: archiveWarnings(replicaSnapshotToProject(snapshot.document, base).warnings) });
      } catch (error) { engineDirty = true; notify({ status: 'error', message: error instanceof Error ? error.message : String(error) }); }
    },
    onDirty(event: FullEngineDirty) {
      if (disposed || abandoned || candidate || recovering) return;
      if (connection) {
        try { if (event.generation !== stampOf(read()).generation) return; } catch { /* A failed snapshot must remain dirty. */ }
      }
      engineDirty = event.dirty;
      if (!event.dirty) { notify({ status: queue.hasUnsaved(clipId) ? state.status : hasUnsaved() ? 'unsaved' : 'saved' }); return; }
      stage();
      cancelDebounce();
      if (!saveAsHold && !queue.conflict(clipId)) {
        if (flushing > 0) queue.markDue(clipId);
        else timer = setTimeout(() => { timer = null; queue.markDue(clipId); }, options.debounceMs ?? 600);
      }
    },
    async restoreDraft(backupRaw?: string): Promise<boolean> {
      if (disposed || recovering || queue.conflict(clipId) || state.draft?.status === 'blocked' || !connection || !candidate ||
        !isEditorNextDraftRecoverable(candidate, clipId, queue.getRevision(clipId), epoch)) return false;
      let entry = candidate;
      const owner = connection, restoringOwner = ownerSerial;
      const isOwner = () => !disposed && connection === owner && ownerSerial === restoringOwner;
      let document: FullEngineDocument;
      try {
        if (backupRaw !== undefined) entry = drafts.backup(backupRaw,clipId,queue.getRevision(clipId));
        if (!base) throw new Error('engine_base_missing');
        if ((options.payload.projectIdentity?.id ?? options.payload.clip?.id) !== clipId) throw new Error('draft_primary_source_mismatch');
        document = cloneReplicaJson(entry.draft!.document);
        const native = document as unknown as Record<string, unknown>;
        if (native.bridgeSchema !== undefined && native.bridgeSchema !== 2) throw new Error('unsupported_document_schema:bridgeSchema');
        const sequence = native.sequence as Record<string, unknown>;
        const originalSequence = (base.document as unknown as {sequence:Record<string,unknown>}).sequence;
        for (const field of ['id','mogrt','mogrtActive','tracks']) if (JSON.stringify(sequence[field]) !== JSON.stringify(originalSequence[field])) throw new Error(`unimplemented_sequence_edit:${field}`);
        const registered = new Set(options.registeredSourceIds);
        const validateReferences = (value: unknown, path: string): void => {
          if (typeof value === 'string' && /^(blob:|data:)/i.test(value)) throw new Error(`ephemeral_media_url:${path}`);
          if (value === null || typeof value !== 'object') return;
          if (Array.isArray(value)) { value.forEach((item,index)=>validateReferences(item,`${path}[${index}]`)); return; }
          const record = value as Record<string,unknown>;
          if (record.sourceId !== undefined && record.sourceId !== '' && !registered.has(String(record.sourceId))) throw new Error(`unregistered_media:${path}.sourceId`);
          for (const [key,item] of Object.entries(record)) validateReferences(item,`${path}.${key}`);
        };
        validateReferences(document,'document');
        applyHostPreserveWindows(base.project, base.document as FullEngineDocument, document);
        // Native recovery preserves the complete document. Backend projection is a separate save gate.
        // hostLoadDocument validates native structure and media binding before editing is unlocked.
      } catch (error) {
        const message = `草稿恢复校验失败：${error instanceof Error ? error.message : String(error)}；当前工程未改变，草稿仍保留。`;
        notify({ message, draft: { status: 'available', message } });
        return false;
      }
      recovering = true; cancelDebounce(); cancelDraftTimer();
      notify({ draft: { status: 'restoring', message: '正在恢复完整原生草稿；恢复后另行校验保存…' } });
      let nativeRestored = false;
      try {
        const result = await owner.loadDocument(document);
        if (!isOwner()) return false;
        if (!result.ok) throw new Error(result.error ?? 'draft_load_failed');
        nativeRestored = true;
        candidate = null; recovering = false; confirmed = null; engineDirty = true;
        // Keep the original across epoch rebinding and failed PUTs; only a confirmed save clears it.
        if (backupRaw !== undefined) preserveBackupRevision = entry.draft!.baseRevision;
        restoredDraft = backupRaw === undefined ? entry : null;
        notify({ draft: null });
        stage();
        const saved = await flush();
        if (isOwner() && !saved) notify({ message: `完整原生草稿已恢复，但尚未保存到服务器；本地草稿仍保留。${state.message}` });
        return isOwner() && saved;
      } catch (error) {
        if (!isOwner()) return false;
        if (nativeRestored) {
          persistDraft();
          notify({ status: queue.conflict(clipId) ? 'conflict' : 'error', message: `完整原生草稿已恢复，但尚未保存到服务器；本地草稿仍保留。${error instanceof Error ? error.message : String(error)}` });
          return false;
        }
        // A rejected bridge load can already have changed its document. Never unlock that partial state.
        try {
          if (!base) throw new Error('engine_base_missing');
          const rollback = await owner.loadDocument(cloneReplicaJson(base.document) as FullEngineDocument);
          if (!isOwner()) return false;
          if (!rollback.ok) throw new Error(rollback.error ?? 'draft_rollback_failed');
          const snapshot = read();
          confirmed = stampOf(snapshot); engineDirty = false;
          base = { ...base, document: cloneReplicaJson(snapshot.document) };
          refreshCandidate();
          const message = `草稿恢复失败：${error instanceof Error ? error.message : String(error)}；已恢复服务器文档，草稿仍保留。`;
          notify({ message, ...(state.draft ? { draft: { ...state.draft, message } } : {}) });
        } catch {
          if (isOwner()) notify({ draft: { status: 'blocked', message: '草稿恢复失败，服务器文档也未能重新载入。草稿仍保留；请重新载入工程后再选择，当前已锁定编辑和保存。' } });
        }
        return false;
      } finally { if (isOwner()) recovering = false; }
    },
    discardDraft() {
      if (disposed || recovering || state.draft?.status === 'blocked' || !candidate) return;
      if (drafts.remove(candidate)) refreshCandidate();
    },
    pageHide(): Promise<boolean> {
      if (disposed || candidate || recovering || !hasUnsaved()) return Promise.resolve(false);
      cancelDebounce(); persistDraft();
      if (queue.conflict(clipId) || leaving || saveAsHold) return Promise.resolve(false);
      leaving = true; leaveBudget = SAVE_KEEPALIVE_LIMIT;
      return flush(true).finally(() => { leaving = false; });
    },
    flush,
    retry: () => flush(true),
    hasUnsaved,
    getState: stateCopy,
    /** Read-only copy of the exact server-acknowledged projection, never a new editable owner. */
    projectCurrentSnapshot(expected: Stamp) {
      if (disposed || abandoned || candidate || recovering || !connection || !base) throw new Error('engine_save_session_inactive');
      const snapshot = read();
      if (!matches(stampOf(snapshot), expected)) throw new Error('stale_engine_revision');
      return replicaSnapshotToProject(snapshot.document, base);
    },
    beginSaveAs(expected: Stamp) {
      if (saveAsHold || disposed || abandoned || candidate || recovering || state.status === 'saving' || flushing || !connection || !base)
        throw new Error('save_as_session_busy');
      if (!matches(stampOf(read()), expected)) throw new Error('stale_engine_revision');
      const hold = { stamp: { ...expected }, target: null as string | null, departing: false };
      saveAsHold = hold; cancelDebounce();
      return {
        permit(target: string) {
          if (saveAsHold !== hold || disposed || !matches(stampOf(read()), hold.stamp)) throw new Error('stale_engine_revision');
          hold.target = target;
        },
        complete() {
          abandoned = true; saveAsHold = null; cancelDebounce(); clearOwnedDrafts(); queue.drop(clipId);
        },
        release() {
          if (saveAsHold !== hold) return;
          saveAsHold = null;
          if (!disposed && !abandoned && hasUnsaved() && !queue.conflict(clipId))
            timer = setTimeout(() => { timer = null; queue.markDue(clipId); }, options.debounceMs ?? 600);
        },
      };
    },
    canLeaveForSaveAs(target: string) {
      const allowed = !!saveAsHold && saveAsHold.target === target && !disposed && matches(stampOf(read()), saveAsHold.stamp);
      if (allowed) saveAsHold!.departing = true;
      return allowed;
    },
    saveAsPending: () => !!saveAsHold,
    getExportSnapshot() {
      if (disposed || candidate || recovering || !connection || !base || !confirmed) throw new Error('工程尚未就绪，导出已禁用。');
      if (state.status !== 'saved' || hasUnsaved()) throw new Error(state.message || '工程尚未保存，导出已禁用。');
      const current = stampOf(read()), revision = queue.getRevision(clipId);
      if (!matches(current, confirmed)) throw new Error('引擎修订尚未确认，导出已禁用。');
      if (!Number.isSafeInteger(revision) || revision! < 0 || typeof epoch !== 'string' || !epoch) throw new Error('缺少服务器修订或纪元，导出已禁用。');
      return { clipId, epoch, serverRevision: revision!, ...confirmed,
        project: cloneReplicaJson(base.project), document: cloneReplicaJson(base.document), archiveWarnings: cloneReplicaJson(state.archiveWarnings) };
    },
    /** Both conflict choices load a fresh GET in Page. No overwrite path is exposed. */
    abandonForReload() { abandoned = true; cancelDebounce(); clearOwnedDrafts(); queue.drop(clipId); },
    dispose() { if (!saveAsHold?.departing) persistDraft(); disposed = true; cancelDebounce(); cancelDraftTimer(); queue.drop(clipId); connection = null; base = null; },
  };
}
export type EditorNextSaveController = ReturnType<typeof createEditorNextSaveController>;
