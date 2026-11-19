import { useCallback, useEffect, useRef, useState } from 'react';
import type { ClipDto, EditorPayload, EditorProject, StudioEvent } from '../../api/types';
import { analyzeClip, extractEvents, generateAutoPlan as generateAutoPlanRequest, getClips, getEditor, saveEditor } from '../../api/client';
import { getAiSettings } from '../../api/settings';
import {
  getMusicAssets,
  getMusicJob,
  getMusicStatus,
  startMusicGeneration,
  type MusicJobState,
} from '../../api/music';
import { adviceConnection } from './aiAdvice';
import {
  duckingPatch,
  musicGenerateBody,
  musicOptions,
  selectMusicPatch,
  volumePatch,
  type MusicTrackOption,
} from './musicTrack';
import { normalizeProject } from './project';
import {
  SAVE_KEEPALIVE_LIMIT,
  assessRecoveredDraft,
  buildDraft,
  createSaveQueue,
  describeConflict,
  draftKey,
  parseDraft,
  planLeaveSave,
  readRevision,
  utf8Bytes,
  type DraftReason,
  type SaveConflict,
  type SaveQueue,
  type UnsavedDraft,
} from './saveQueue';

type LoadState = 'loading' | 'ready' | 'empty' | 'error';
type SaveState = 'clean' | 'dirty' | 'saving' | 'error';

interface HistoryEntry {
  project: EditorProject;
  label: string;
}

/** 上次离开页面时留在本地的未保存草稿（载入同一 clip 时暴露给 UI 决定恢复还是丢弃）。 */
export interface RecoveredDraft {
  clipId: string;
  project: EditorProject;
  baseRevision: number | null;
  timestamp: number;
  reason: DraftReason;
  /** 服务器 revision 与草稿基线不一致：恢复会覆盖这期间的其他写入。 */
  stale: boolean;
}

const MAX_HISTORY = 60;
const SAVE_DEBOUNCE_MS = 850;

declare global {
  interface Window {
    /** 导出/E2E 用：强制把 debounce 中的编辑写回服务器，返回是否已全部保存。卸载时删除。 */
    __lcFlushEditor?: () => Promise<boolean>;
  }
}

// 离开编辑器（路由卸载 / 刷新 / 关闭标签）时用 keepalive 请求把 debounce 中的保存发出去。
// 说明：只能尽力而为（浏览器对 keepalive body 有 64KB 上限），正常编辑路径仍走 api/client 的 saveEditor。
const API_BASE = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

function equalProject(a: EditorProject, b: EditorProject): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** localStorage 在隐私模式 / 配额满时会抛异常；草稿是兜底手段，失败不能影响编辑。 */
function writeDraft(clipId: string, project: EditorProject, baseRevision: number | null, reason: DraftReason): boolean {
  try {
    window.localStorage.setItem(draftKey(clipId), JSON.stringify(buildDraft(clipId, project, baseRevision, reason)));
    return true;
  } catch {
    return false;
  }
}

function clearDraft(clipId: string): void {
  try {
    window.localStorage.removeItem(draftKey(clipId));
  } catch {
    // 读不到 / 删不掉都按"没有草稿"处理
  }
}

function readDraft(clipId: string): UnsavedDraft<unknown> | null {
  try {
    return parseDraft(window.localStorage.getItem(draftKey(clipId)), clipId);
  } catch {
    return null;
  }
}

export function useEditorSession() {
  const [clips, setClips] = useState<ClipDto[]>([]);
  const [selectedClipId, setSelectedClipId] = useState<string | null>(null);
  const [payload, setPayload] = useState<EditorPayload | null>(null);
  const [project, setProject] = useState<EditorProject | null>(null);
  const [loadState, setLoadState] = useState<LoadState>('loading');
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saveState, setSaveState] = useState<SaveState>('clean');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [lastSavedAt, setLastSavedAt] = useState<Date | null>(null);
  const [eventsBusy, setEventsBusy] = useState(false);
  const [adviceBusy, setAdviceBusy] = useState(false);
  const [adviceError, setAdviceError] = useState<string | null>(null);
  const [musicAssets, setMusicAssets] = useState<MusicTrackOption[]>([]);
  const [musicBusy, setMusicBusy] = useState(false);
  const [musicError, setMusicError] = useState<string | null>(null);
  const [musicJob, setMusicJob] = useState<MusicJobState | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [saveConflict, setSaveConflict] = useState<SaveConflict | null>(null);
  const [recoveredDraft, setRecoveredDraft] = useState<RecoveredDraft | null>(null);
  const [hasUnsavedChanges, setHasUnsavedChanges] = useState(false);
  const [, rerenderHistory] = useState(0);

  const projectRef = useRef<EditorProject | null>(null);
  const selectedClipRef = useRef<string | null>(null);
  const aiClipVersionRef = useRef(0);
  const musicMountedRef = useRef(false);
  const musicVersionRef = useRef(0);
  const musicLibraryVersionRef = useRef(0);
  const musicGeneratingRef = useRef(false);
  const musicReloadVersionRef = useRef<number | null>(null);
  const pastRef = useRef<HistoryEntry[]>([]);
  const futureRef = useRef<HistoryEntry[]>([]);
  const lastCoalesceRef = useRef<{ key: string; at: number } | null>(null);
  const saveTimerRef = useRef<number | null>(null);
  const lastSavedJsonRef = useRef<string | null>(null);
  const saveQueueRef = useRef<SaveQueue<EditorProject> | null>(null);

  /** P1.7：剪辑页导入素材后重新拉取素材箱（不重置工程、不动选择）。 */
  const refreshClips = useCallback(async () => {
    const items = await getClips();
    setClips(items);
    return items;
  }, []);

  // 握手在 Promise 完成后立即回读，不能等待 React 提交保存状态。
  const getHasUnsavedChanges = useCallback(() => saveQueueRef.current?.hasUnsaved() ?? false, []);

  const syncUnsaved = useCallback(() => {
    setHasUnsavedChanges(saveQueueRef.current?.hasUnsaved() ?? false);
  }, []);

  /**
   * 保存队列（懒建、整个会话复用）：全局同一时刻只有 1 个 PUT 在途，每个 clip 只保留最新快照，
   * 成功返回的 revision 成为下一次的 X-Base-Revision，409 阻塞该 clip 等 resolveConflict。
   */
  const queueOf = useCallback((): SaveQueue<EditorProject> => {
    const existing = saveQueueRef.current;
    if (existing) return existing;
    const queue = createSaveQueue<EditorProject>({
      send: (clipId, project, baseRevision) => saveEditor(clipId, project, baseRevision),
      onEvent: (event) => {
        const selected = event.clipId === selectedClipRef.current;
        if (event.type === 'saving') {
          if (selected) {
            setSaveState('saving');
            setSaveError(null);
          }
        } else if (event.type === 'saved') {
          lastSavedJsonRef.current = JSON.stringify(event.project);
          clearDraft(event.clipId);
          if (selected) {
            setSaveState(event.pending ? 'dirty' : 'clean');
            setLastSavedAt(new Date());
            setSaveError(null);
            setSaveConflict(null);
          }
        } else if (event.type === 'error') {
          if (selected) {
            setSaveState('error');
            setSaveError(event.message);
          }
        } else {
          // 409：本地快照保留，等 resolveConflict('overwrite' | 'reload')，绝不自动覆盖服务器
          if (selected) {
            setSaveState('error');
            setSaveError(describeConflict(event.conflict));
            setSaveConflict(event.conflict);
          }
        }
        syncUnsaved();
      },
    });
    saveQueueRef.current = queue;
    return queue;
  }, [syncUnsaved]);

  const resetMusicSession = useCallback(() => {
    musicVersionRef.current += 1;
    musicLibraryVersionRef.current += 1;
    musicGeneratingRef.current = false;
    musicReloadVersionRef.current = null;
    setMusicBusy(false);
    setMusicError(null);
    setMusicJob(null);
  }, []);

  useEffect(() => {
    musicMountedRef.current = true;
    return () => {
      musicMountedRef.current = false;
      musicVersionRef.current += 1;
      musicLibraryVersionRef.current += 1;
      musicGeneratingRef.current = false;
      musicReloadVersionRef.current = null;
    };
  }, []);

  const applyLoadedProject = useCallback((next: EditorProject) => {
    resetMusicSession();
    projectRef.current = next;
    setProject(next);
    pastRef.current = [];
    futureRef.current = [];
    lastCoalesceRef.current = null;
    lastSavedJsonRef.current = JSON.stringify(next);
    setSaveState('clean');
    setSaveError(null);
    setSaveConflict(null);
    setLastSavedAt(new Date());
    syncUnsaved();
    rerenderHistory((value) => value + 1);
  }, [resetMusicSession, syncUnsaved]);

  useEffect(() => {
    let cancelled = false;
    setLoadState('loading');
    setLoadError(null);
    void getClips()
      .then((items) => {
        if (cancelled) return;
        setClips(items);
        if (items.length === 0) {
          setLoadState('empty');
          return;
        }
        const requested = new URLSearchParams(window.location.search).get('clip');
        const chosen = items.find((item) => item.id === requested) ?? items[0];
        setSelectedClipId(chosen.id);
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        setLoadState('error');
        setLoadError(messageOf(error));
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!selectedClipId) return undefined;
    let cancelled = false;
    selectedClipRef.current = selectedClipId;
    resetMusicSession();
    aiClipVersionRef.current += 1;
    setEventsBusy(false);
    setAdviceBusy(false);
    setActionError(null);
    setAdviceError(null);
    if (saveTimerRef.current) {
      window.clearTimeout(saveTimerRef.current);
      saveTimerRef.current = null;
    }
    // 切 clip 不丢上一个 clip 的未发送快照：交给队列后台发完（队列按 clip 记 revision）。
    saveQueueRef.current?.markDue();
    setSaveConflict(null);
    setRecoveredDraft(null);
    setLoadState('loading');
    setLoadError(null);
    setPayload(null);
    setProject(null);
    projectRef.current = null;

    void getEditor(selectedClipId)
      .then((data) => {
        if (cancelled) return;
        const clipId = selectedClipId;
        // GET 带回的 revision 成为该 clip 的保存基线（服务器不带时按"不检查"处理）。
        const serverRevision = readRevision(data);
        queueOf().setRevision(clipId, serverRevision);
        const normalized = normalizeProject(data.project, data.duration);
        setPayload(data);
        applyLoadedProject(normalized);
        setLoadState('ready');
        // 上次离开时写下的草稿：与服务器一致就丢弃，否则交给 UI 决定恢复还是放弃。
        const draft = readDraft(clipId);
        if (!draft) return;
        const assessment = assessRecoveredDraft({
          draftJson: JSON.stringify(draft.project),
          serverJson: JSON.stringify(normalized),
          draftBaseRevision: draft.baseRevision,
          serverRevision,
        });
        if (assessment.action === 'discard') {
          clearDraft(clipId);
          return;
        }
        setRecoveredDraft({
          clipId,
          project: draft.project as EditorProject,
          baseRevision: draft.baseRevision,
          timestamp: draft.timestamp,
          reason: draft.reason,
          stale: assessment.stale,
        });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        setLoadState('error');
        setLoadError(messageOf(error));
      });

    return () => {
      cancelled = true;
    };
  }, [applyLoadedProject, queueOf, resetMusicSession, selectedClipId]);

  /** 立即保存一份快照并等待队列空闲；返回该 clip 是否已无未保存内容（冲突 / 失败都算 false）。 */
  const performSave = useCallback(async (clipId: string, next: EditorProject): Promise<boolean> => {
    const queue = queueOf();
    queue.stage(clipId, next);
    queue.markDue(clipId);
    syncUnsaved();
    await queue.flush({ retryErrors: true });
    syncUnsaved();
    return !queue.hasUnsaved(clipId);
  }, [queueOf, syncUnsaved]);

  const scheduleSave = useCallback(
    (clipId: string, next: EditorProject) => {
      // 立刻入队（unsaved / 离开兜底立即可见），到期才发；在途期间的编辑合并成一份。
      const queue = queueOf();
      queue.stage(clipId, next);
      syncUnsaved();
      if (saveTimerRef.current) window.clearTimeout(saveTimerRef.current);
      saveTimerRef.current = window.setTimeout(() => {
        saveTimerRef.current = null;
        queue.markDue();
      }, SAVE_DEBOUNCE_MS);
    },
    [queueOf, syncUnsaved],
  );

  /** 立即把 debounce 中的编辑发出去并等待队列空闲；返回是否已全部保存（失败 / 未解决的冲突 → false）。 */
  const flushSave = useCallback(async (): Promise<boolean> => {
    if (saveTimerRef.current) {
      window.clearTimeout(saveTimerRef.current);
      saveTimerRef.current = null;
    }
    const saved = await queueOf().flush({ retryErrors: true });
    syncUnsaved();
    return saved;
  }, [queueOf, syncUnsaved]);

  // 离开编辑器（路由卸载 / 刷新 / 关标签）时：每份未保存快照先写本地草稿兜底，能走 keepalive 的
  // 再带 X-Base-Revision 发一次 PUT；超过 SAVE_KEEPALIVE_LIMIT（60000 字节）或冲突中的只写草稿。
  // 不走 performSave：组件卸载后 setState 无效，且离开阶段拿不到响应。
  const flushSaveOnLeave = useCallback(() => {
    if (saveTimerRef.current) {
      window.clearTimeout(saveTimerRef.current);
      saveTimerRef.current = null;
    }
    const queue = saveQueueRef.current;
    if (!queue) return;
    let budget = SAVE_KEEPALIVE_LIMIT;
    for (const snapshot of queue.unsaved()) {
      const body = JSON.stringify(snapshot.project);
      const bytes = utf8Bytes(body);
      const plan = planLeaveSave({ bytes, state: snapshot.state, remainingBudget: budget });
      // 草稿先落盘：keepalive 的结果这里确认不了，确认不了就只能当没保存成功。
      writeDraft(snapshot.clipId, snapshot.project, snapshot.baseRevision, plan.draftReason);
      if (!plan.keepalive) continue;
      budget -= bytes;
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (snapshot.baseRevision !== null) headers['X-Base-Revision'] = String(snapshot.baseRevision);
      try {
        void fetch(`${API_BASE}/api/studio/clips/${encodeURIComponent(snapshot.clipId)}/editor`, {
          method: 'PUT',
          headers,
          body,
          keepalive: true,
        }).catch(() => undefined);
      } catch {
        // 尽力保存：离开阶段失败不阻塞导航，草稿改记 keepalive-failed
        writeDraft(snapshot.clipId, snapshot.project, snapshot.baseRevision, 'keepalive-failed');
      }
    }
  }, []);

  useEffect(() => {
    const onPageHide = () => flushSaveOnLeave();
    window.addEventListener('pagehide', onPageHide);
    return () => {
      window.removeEventListener('pagehide', onPageHide);
      flushSaveOnLeave();
    };
  }, [flushSaveOnLeave]);

  // 导出 / E2E 用的强制保存钩子；卸载时清除，避免留下指向已卸载会话的函数。
  useEffect(() => {
    window.__lcFlushEditor = flushSave;
    return () => {
      if (window.__lcFlushEditor === flushSave) delete window.__lcFlushEditor;
    };
  }, [flushSave]);

  const updateProject = useCallback(
    (next: EditorProject, label: string, coalesceKey?: string) => {
      const previous = projectRef.current;
      if (!previous || equalProject(previous, next)) return;
      const now = performance.now();
      const coalesce = Boolean(
        coalesceKey
          && lastCoalesceRef.current?.key === coalesceKey
          && now - lastCoalesceRef.current.at < 900,
      );
      if (!coalesce) {
        pastRef.current = [
          ...pastRef.current.slice(-(MAX_HISTORY - 1)),
          { project: previous, label },
        ];
        futureRef.current = [];
      }
      lastCoalesceRef.current = coalesceKey ? { key: coalesceKey, at: now } : null;
      projectRef.current = next;
      setProject(next);
      setSaveState('dirty');
      setSaveError(null);
      rerenderHistory((value) => value + 1);
      if (selectedClipRef.current) scheduleSave(selectedClipRef.current, next);
    },
    [scheduleSave],
  );

  const undo = useCallback(() => {
    const entry = pastRef.current.pop();
    if (!entry || !projectRef.current) return;
    const current = projectRef.current;
    futureRef.current = [{ project: current, label: entry.label }, ...futureRef.current].slice(0, MAX_HISTORY);
    lastCoalesceRef.current = null;
    projectRef.current = entry.project;
    setProject(entry.project);
    setSaveState('dirty');
    setSaveError(null);
    rerenderHistory((value) => value + 1);
    if (selectedClipRef.current) scheduleSave(selectedClipRef.current, entry.project);
  }, [scheduleSave]);

  const redo = useCallback(() => {
    const entry = futureRef.current.shift();
    if (!entry || !projectRef.current) return;
    pastRef.current = [
      ...pastRef.current.slice(-(MAX_HISTORY - 1)),
      { project: projectRef.current, label: entry.label },
    ];
    lastCoalesceRef.current = null;
    projectRef.current = entry.project;
    setProject(entry.project);
    setSaveState('dirty');
    setSaveError(null);
    rerenderHistory((value) => value + 1);
    if (selectedClipRef.current) scheduleSave(selectedClipRef.current, entry.project);
  }, [scheduleSave]);

  const loadProjectSnapshot = useCallback(
    (next: EditorProject, label: string) => {
      const previous = projectRef.current;
      if (!previous) return;
      pastRef.current = [
        ...pastRef.current.slice(-(MAX_HISTORY - 1)),
        { project: previous, label },
      ];
      futureRef.current = [];
      lastCoalesceRef.current = null;
      projectRef.current = next;
      setProject(next);
      setSaveState('dirty');
      setSaveError(null);
      setActionError(null);
      rerenderHistory((value) => value + 1);
      if (selectedClipRef.current) scheduleSave(selectedClipRef.current, next);
    },
    [scheduleSave],
  );

  const selectClip = useCallback(
    (clipId: string) => {
      if (clipId === selectedClipRef.current) return;
      resetMusicSession();
      // 切 clip 不等保存结果：队列按 clip 记 revision，旧 clip 的快照后台发完。
      void flushSave();
      setSelectedClipId(clipId);
    },
    [flushSave, resetMusicSession],
  );

  const retrySave = useCallback(() => {
    const clipId = selectedClipRef.current;
    const current = projectRef.current;
    if (!clipId || !current) return;
    if (saveConflict || queueOf().conflict(clipId)) return;
    void performSave(clipId, current);
  }, [performSave, queueOf, saveConflict]);

  /**
   * 409 冲突二选一：
   * overwrite 以服务器 revision 为基线重发本地快照（覆盖服务器）；
   * reload 丢弃本地快照与草稿、重新 GET 服务器工程。
   */
  const resolveConflict = useCallback(async (choice: 'overwrite' | 'reload'): Promise<boolean> => {
    const clipId = selectedClipRef.current;
    if (!clipId) return false;
    const queue = queueOf();
    if (choice === 'overwrite') {
      const current = projectRef.current;
      if (!current) return false;
      queue.resolveConflict(clipId, 'overwrite', current);
      setSaveConflict(null);
      setSaveError(null);
      return performSave(clipId, current);
    }
    resetMusicSession();
    const version = musicVersionRef.current;
    musicReloadVersionRef.current = version;
    // The old project stays visible during GET, but cannot start a new music edit.
    const isCurrent = () => musicMountedRef.current && version === musicVersionRef.current
      && musicReloadVersionRef.current === version;
    queue.resolveConflict(clipId, 'reload');
    clearDraft(clipId);
    setSaveConflict(null);
    setSaveError(null);
    setRecoveredDraft(null);
    syncUnsaved();
    try {
      const data = await getEditor(clipId);
      if (!isCurrent() || selectedClipRef.current !== clipId) return false;
      queue.setRevision(clipId, readRevision(data));
      setPayload(data);
      applyLoadedProject(normalizeProject(data.project, data.duration));
      setLoadState('ready');
      setLoadError(null);
      return true;
    } catch (error: unknown) {
      if (isCurrent()) setSaveError(messageOf(error));
      return false;
    } finally {
      if (isCurrent()) musicReloadVersionRef.current = null;
    }
  }, [applyLoadedProject, performSave, queueOf, resetMusicSession, syncUnsaved]);

  /** 恢复上次离开时留下的草稿：进历史（可撤销）并立即排队保存，草稿随即删除。 */
  const applyRecoveredDraft = useCallback(() => {
    const draft = recoveredDraft;
    if (!draft || draft.clipId !== selectedClipRef.current) return;
    setRecoveredDraft(null);
    clearDraft(draft.clipId);
    loadProjectSnapshot(draft.project, '恢复未保存的草稿');
  }, [loadProjectSnapshot, recoveredDraft]);

  const discardRecoveredDraft = useCallback(() => {
    const draft = recoveredDraft;
    if (!draft) return;
    setRecoveredDraft(null);
    clearDraft(draft.clipId);
  }, [recoveredDraft]);

  const extractCandidates = useCallback(async () => {
    const clipId = selectedClipRef.current;
    if (!clipId) return;
    const version = aiClipVersionRef.current;
    setEventsBusy(true);
    setActionError(null);
    try {
      const events = await extractEvents(clipId);
      if (version !== aiClipVersionRef.current) return;
      setPayload((current) => (current?.clip?.id === clipId ? { ...current, events } : current));
    } catch (error: unknown) {
      if (version === aiClipVersionRef.current) setActionError(messageOf(error));
    } finally {
      if (version === aiClipVersionRef.current) setEventsBusy(false);
    }
  }, []);

  // 整场 AI 总结：设置里的模型直接复用，密钥由服务端按已保存设置补上。
  const analyzeAdvice = useCallback(async () => {
    const clipId = selectedClipRef.current;
    if (!clipId) return;
    const version = aiClipVersionRef.current;
    setAdviceBusy(true);
    setAdviceError(null);
    try {
      const settings = await getAiSettings();
      if (version !== aiClipVersionRef.current) return;
      const connection = adviceConnection(settings);
      if (!connection) throw new Error('先在设置里选择 AI 模型，再生成总结');
      const result = await analyzeClip(clipId, connection);
      if (version !== aiClipVersionRef.current) return;
      setPayload((current) => (current?.clip?.id === clipId
        ? { ...current, events: result.events.length > 0 ? result.events : current.events, advice: result.advice }
        : current));
      if (result.adviceError) setAdviceError(result.adviceError);
    } catch (error: unknown) {
      if (version === aiClipVersionRef.current) setAdviceError(messageOf(error));
    } finally {
      if (version === aiClipVersionRef.current) setAdviceBusy(false);
    }
  }, []);

  // —— 配乐：整场 BGM 落在工程级 musicAssetId / musicVolume / duckMusic 上 ——
  const loadMusicLibrary = useCallback(async () => {
    if (!musicMountedRef.current || musicGeneratingRef.current) return;
    const version = musicVersionRef.current;
    const libraryVersion = ++musicLibraryVersionRef.current;
    const isCurrent = () => musicMountedRef.current
      && version === musicVersionRef.current && libraryVersion === musicLibraryVersionRef.current;
    try {
      const assets = await getMusicAssets();
      if (!isCurrent()) return;
      setMusicAssets(musicOptions(assets));
      setMusicError(null);
    } catch (error: unknown) {
      if (isCurrent()) setMusicError(messageOf(error));
    }
  }, []);

  const selectMusic = useCallback((assetId: string | null) => {
    const current = projectRef.current;
    if (!current) return;
    updateProject(selectMusicPatch(current, assetId), assetId ? '选择配乐' : '移除配乐');
  }, [updateProject]);

  const setDuckMusic = useCallback((duck: boolean) => {
    const current = projectRef.current;
    if (!current) return;
    updateProject(duckingPatch(current, duck), duck ? '开启人声压低配乐' : '关闭人声压低配乐');
  }, [updateProject]);

  const setMusicVolume = useCallback((percent: number) => {
    const current = projectRef.current;
    if (!current) return;
    // 连续拖动滑杆时合并成一条撤销记录。
    updateProject(volumePatch(current, percent), '配乐音量', 'music-volume');
  }, [updateProject]);

  const generateMusicTrack = useCallback(async (durationSeconds: number) => {
    if (!musicMountedRef.current || musicGeneratingRef.current || musicReloadVersionRef.current !== null || !projectRef.current) return;
    const version = ++musicVersionRef.current;
    const isCurrent = () => musicMountedRef.current && version === musicVersionRef.current;
    musicGeneratingRef.current = true;
    setMusicBusy(true);
    setMusicError(null);
    setMusicJob(null);
    try {
      const status = await getMusicStatus();
      if (!isCurrent()) return;
      const modelId = (status.defaultModelId ?? '').trim();
      if (!modelId) throw new Error('还没有可用的音乐模型，先去「音乐」页下载一个');
      let job = await startMusicGeneration(musicGenerateBody(modelId, Boolean(status.allowDownload), durationSeconds));
      if (!isCurrent()) return;
      setMusicJob(job);
      const deadline = Date.now() + 180_000;
      while (job.status !== 'completed' && job.status !== 'failed' && job.status !== 'cancelled') {
        if (Date.now() > deadline) throw new Error('生成超时了，去「音乐」页看任务状态');
        await new Promise((resolve) => setTimeout(resolve, 1200));
        if (!isCurrent()) return;
        job = await getMusicJob(job.id);
        if (!isCurrent()) return;
        setMusicJob(job);
      }
      if (job.status !== 'completed') throw new Error(job.error ?? '配乐生成失败');
      try {
        const assets = await getMusicAssets();
        if (!isCurrent()) return;
        setMusicAssets(musicOptions(assets));
      } catch (error: unknown) {
        if (isCurrent()) setMusicError(messageOf(error));
      }
      // A library refresh failure does not undo a completed generation.
      if (!isCurrent()) return;
      if (job.assetId) selectMusic(job.assetId);
    } catch (error: unknown) {
      if (isCurrent()) setMusicError(messageOf(error));
    } finally {
      if (isCurrent()) {
        musicGeneratingRef.current = false;
        setMusicBusy(false);
      }
    }
  }, [selectMusic]);

  const generateAutoPlan = useCallback(async () => {
    const clipId = selectedClipRef.current;
    if (!clipId) return null;
    setEventsBusy(true);
    setActionError(null);
    try {
      // Auto-plan overwrites the server-side project. Flush any debounced edit and
      // wait for an in-flight save first, otherwise that older write can land
      // after the auto-plan result and silently revert the new timeline.
      if (!(await flushSave())) {
        setActionError('保存当前编辑失败，已取消生成方案');
        return null;
      }
      const report = await generateAutoPlanRequest(clipId);
      if (selectedClipRef.current !== clipId) return null;
      const data = await getEditor(clipId);
      if (selectedClipRef.current !== clipId) return null;
      // The user may have kept editing while the request was in flight. Drop that
      // debounced snapshot so it cannot overwrite the newly generated project.
      if (saveTimerRef.current) {
        window.clearTimeout(saveTimerRef.current);
        saveTimerRef.current = null;
      }
      const queue = queueOf();
      queue.drop(clipId);
      queue.setRevision(clipId, readRevision(data));
      clearDraft(clipId);
      syncUnsaved();
      setPayload(data);
      applyLoadedProject(normalizeProject(data.project, data.duration));
      setLoadState('ready');
      setLoadError(null);
      return report;
    } catch (error: unknown) {
      setActionError(messageOf(error));
      return null;
    } finally {
      setEventsBusy(false);
    }
  }, [applyLoadedProject, flushSave, queueOf, syncUnsaved]);

  const replacePayload = useCallback((next: EditorPayload) => {
    setPayload(next);
  }, []);

  const mergeTranscript = useCallback((clipId: string, transcript: EditorPayload['transcript']) => {
    setPayload((current) => current?.clip?.id === clipId ? { ...current, transcript } : current);
  }, []);

  const selectedClip = clips.find((clip) => clip.id === selectedClipId) ?? null;
  const canUndo = pastRef.current.length > 0;
  const canRedo = futureRef.current.length > 0;
  const undoLabel = pastRef.current[pastRef.current.length - 1]?.label ?? null;
  const redoLabel = futureRef.current[0]?.label ?? null;

  return {
    clips,
    refreshClips,
    selectedClip,
    payload,
    project,
    loadState,
    loadError,
    saveState,
    saveError,
    lastSavedAt,
    eventsBusy,
    adviceBusy,
    adviceError,
    musicAssets,
    musicBusy,
    musicError,
    musicJob,
    actionError,
    canUndo,
    canRedo,
    undoLabel,
    redoLabel,
    updateProject,
    loadProjectSnapshot,
    undo,
    redo,
    selectClip,
    retrySave,
    analyzeAdvice,
    loadMusicLibrary,
    selectMusic,
    setDuckMusic,
    setMusicVolume,
    generateMusicTrack,
    extractCandidates,
    generateAutoPlan,
    replacePayload,
    mergeTranscript,
    flushSave,
    hasUnsavedChanges,
    getHasUnsavedChanges,
    saveConflict,
    resolveConflict,
    recoveredDraft,
    applyRecoveredDraft,
    discardRecoveredDraft,
    lastSavedJson: lastSavedJsonRef.current,
    events: (payload?.events ?? []) as StudioEvent[],
    advice: payload?.advice ?? null,
  };
}
