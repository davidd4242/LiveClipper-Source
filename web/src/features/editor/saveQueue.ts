/**
 * R51-J：剪辑页保存通路的纯逻辑。
 * 不依赖 React / DOM 存储 / import.meta，Node 原生 strip-types 可直接导入测试（tests/merge/r51-save-path.test.mjs）。
 *
 * 不变量：
 * 1. 全局同一时刻最多 1 个 PUT 在途；
 * 2. 每个 clip 只保留最新一份未发送快照；在途期间的编辑合并成一份，在途结束后（且已到期）只再发一次；
 * 3. 每次成功 PUT 返回的 revision 成为该 clip 下一次的 baseRevision；GET 得到的 revision 由调用方 setRevision；
 * 4. 409（stale-revision / 会话占用）不自动重试、不自动采用服务器 revision：快照保留并阻塞该 clip，
 *    直到调用方显式 resolveConflict('overwrite' | 'reload')。
 */

/** keepalive 请求体上限（浏览器约 64KB，留余量）。 */
export const SAVE_KEEPALIVE_LIMIT = 60000;
export const UNSAVED_DRAFT_PREFIX = 'lc.unsavedEditor.';
export const UNSAVED_DRAFT_VERSION = 1;

/**
 * 草稿写入原因：too-large 超出 keepalive 上限或剩余预算；conflict 409 未解决；error 保存失败；
 * unconfirmed 离开页面时无法确认 keepalive 结果；keepalive-failed keepalive 请求失败或被拒绝。
 */
export type DraftReason = 'too-large' | 'conflict' | 'error' | 'unconfirmed' | 'keepalive-failed';

/**
 * 未保存快照状态：pending 待发送；in-flight 正在发送；behind-in-flight 同 clip 有在途请求、本快照排在其后；
 * error / conflict 已被阻塞。
 */
export type UnsavedState = 'pending' | 'in-flight' | 'behind-in-flight' | 'error' | 'conflict';

export interface ConflictInfo {
  code: string | null;
  serverRevision: number | null;
  holder: string | null;
  message: string;
}

export interface SaveConflict extends ConflictInfo {
  clipId: string;
  /** 被拒绝的那次请求所用的 baseRevision。 */
  baseRevision: number | null;
}

export interface SaveSendResult {
  saved: boolean;
  revision?: number;
}

export type SaveSend<P> = (clipId: string, project: P, baseRevision: number | null) => Promise<SaveSendResult>;

export type SaveQueueEvent<P> =
  | { type: 'saving'; clipId: string; project: P; baseRevision: number | null }
  | { type: 'saved'; clipId: string; project: P; revision: number | null; pending: boolean }
  | { type: 'error'; clipId: string; project: P; message: string; error: unknown }
  | { type: 'conflict'; clipId: string; project: P; conflict: SaveConflict };

export interface UnsavedSnapshot<P> {
  clipId: string;
  project: P;
  baseRevision: number | null;
  state: UnsavedState;
}

export interface SaveQueueOptions<P> {
  send: SaveSend<P>;
  onEvent?: (event: SaveQueueEvent<P>) => void;
}

export interface SaveQueue<P> {
  /** 记录该 clip 的最新快照（覆盖旧快照）；只解除 error 阻塞，冲突阻塞保留到 resolveConflict。 */
  stage(clipId: string, project: P): void;
  /** debounce 到期或主动保存：标记为可发送并启动发送循环；不传 clipId 表示全部。 */
  markDue(clipId?: string): void;
  /** 发送全部快照并等待队列空闲；返回是否已无未保存内容（冲突快照不会被自动发送）。 */
  flush(options?: { retryErrors?: boolean }): Promise<boolean>;
  hasUnsaved(clipId?: string): boolean;
  latest(clipId: string): P | null;
  conflict(clipId: string): SaveConflict | null;
  /** overwrite：以服务器 revision 为基线重发（可传入要覆盖的新快照）；reload：丢弃本地快照，由调用方重新 GET。 */
  resolveConflict(clipId: string, choice: 'overwrite' | 'reload', project?: P): boolean;
  getRevision(clipId: string): number | null;
  setRevision(clipId: string, revision: number | null): void;
  /** 离开页面时需要兜底的全部快照（含在途请求）。 */
  unsaved(): UnsavedSnapshot<P>[];
  /** 队列外（keepalive）已确认保存：若仍是同一份快照则移出队列，并采用返回的 revision。 */
  acknowledge(clipId: string, project: P, revision: number | null): void;
  /** 快照已转存为本地草稿后移出队列（不影响在途请求）。 */
  drop(clipId: string): void;
}

type QueueBlock = { kind: 'error'; message: string } | { kind: 'conflict'; conflict: SaveConflict };

interface InFlight<P> {
  clipId: string;
  project: P;
  baseRevision: number | null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function nonEmptyString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null;
}

export function draftKey(clipId: string): string {
  return `${UNSAVED_DRAFT_PREFIX}${clipId}`;
}

/** 从 GET / PUT 响应（或任意对象）读取有限数值 revision。 */
export function readRevision(value: unknown): number | null {
  return isRecord(value) ? finiteNumber(value['revision']) : null;
}

/** 鸭子类型识别 409（ApiError / EditorApiError 等带 status/code/revision/holder 的错误）。 */
export function conflictFromError(error: unknown): ConflictInfo | null {
  if (!isRecord(error) || error['status'] !== 409) return null;
  return {
    code: nonEmptyString(error['code']),
    serverRevision: finiteNumber(error['revision']),
    holder: nonEmptyString(error['holder']),
    message: nonEmptyString(error['message']) ?? '工程已被其他写入修改',
  };
}

export function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === 'string' && error.trim()) return error;
  return '保存失败';
}

export function describeConflict(conflict: ConflictInfo): string {
  const holder = conflict.holder ? `（占用会话：${conflict.holder}）` : '';
  const revision = conflict.serverRevision === null ? '' : `服务器 revision ${conflict.serverRevision}；`;
  return `与服务器版本冲突：${conflict.message}${holder}。${revision}本地修改已保留，需选择覆盖服务器或重新读取。`;
}

export function utf8Bytes(text: string): number {
  return new TextEncoder().encode(text).byteLength;
}

export interface LeavePlan {
  keepalive: boolean;
  draftReason: DraftReason;
}

/**
 * 离开页面时单个快照的处理方式。离开阶段拿不到 keepalive 结果，所以一律先写草稿兜底（成功后由调用方删除）：
 * - conflict：只写草稿，绝不用 keepalive 覆盖服务器；
 * - 超过 SAVE_KEEPALIVE_LIMIT 或本次离开剩余的 keepalive 预算：只写草稿；
 * - 其余：keepalive PUT（带 X-Base-Revision）+ 草稿。
 */
export function planLeaveSave(input: { bytes: number; state: UnsavedState; remainingBudget?: number }): LeavePlan {
  if (input.state === 'conflict') return { keepalive: false, draftReason: 'conflict' };
  const budget = Math.min(SAVE_KEEPALIVE_LIMIT, input.remainingBudget ?? SAVE_KEEPALIVE_LIMIT);
  if (!(input.bytes <= budget)) return { keepalive: false, draftReason: 'too-large' };
  return { keepalive: true, draftReason: input.state === 'error' ? 'error' : 'unconfirmed' };
}

export interface UnsavedDraft<P> {
  version: typeof UNSAVED_DRAFT_VERSION;
  clipId: string;
  project: P;
  baseRevision: number | null;
  timestamp: number;
  reason: DraftReason;
}

const DRAFT_REASONS: readonly string[] = ['too-large', 'conflict', 'error', 'unconfirmed', 'keepalive-failed'];

function isDraftReason(value: unknown): value is DraftReason {
  return typeof value === 'string' && DRAFT_REASONS.includes(value);
}

export function buildDraft<P>(
  clipId: string,
  project: P,
  baseRevision: number | null,
  reason: DraftReason,
  timestamp: number = Date.now(),
): UnsavedDraft<P> {
  return { version: UNSAVED_DRAFT_VERSION, clipId, project, baseRevision, timestamp, reason };
}

/** 解析 `lc.unsavedEditor.<clipId>` 里的草稿；格式不符或 clipId 不一致返回 null。 */
export function parseDraft(raw: string | null | undefined, clipId: string): UnsavedDraft<unknown> | null {
  if (!raw) return null;
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!isRecord(value) || value['version'] !== UNSAVED_DRAFT_VERSION || value['clipId'] !== clipId) return null;
  const project = value['project'];
  const timestamp = finiteNumber(value['timestamp']);
  if (!isRecord(project) || timestamp === null) return null;
  const rawBase = value['baseRevision'];
  const baseRevision = finiteNumber(rawBase);
  if (baseRevision === null && rawBase !== null && rawBase !== undefined) return null;
  const reason = value['reason'];
  return {
    version: UNSAVED_DRAFT_VERSION,
    clipId,
    project,
    baseRevision,
    timestamp,
    reason: isDraftReason(reason) ? reason : 'error',
  };
}

export interface DraftAssessment {
  action: 'discard' | 'recover';
  /** 服务器 revision 与草稿基线不一致（或任一未知）：恢复草稿会覆盖期间的其他写入。 */
  stale: boolean;
}

/** 下次载入同一 clip 时判断草稿：内容与服务器一致则丢弃，否则交给调用方以 recoveredDraft 暴露。 */
export function assessRecoveredDraft(input: {
  draftJson: string;
  serverJson: string;
  draftBaseRevision: number | null;
  serverRevision: number | null;
}): DraftAssessment {
  if (input.draftJson === input.serverJson) return { action: 'discard', stale: false };
  const stale =
    input.draftBaseRevision === null || input.serverRevision === null || input.draftBaseRevision !== input.serverRevision;
  return { action: 'recover', stale };
}

export function createSaveQueue<P>(options: SaveQueueOptions<P>): SaveQueue<P> {
  const revisions = new Map<string, number>();
  const staged = new Map<string, P>();
  const due = new Set<string>();
  const blocked = new Map<string, QueueBlock>();
  let current: InFlight<P> | null = null;
  let active = false;
  let loop: Promise<void> | null = null;

  const emit = (event: SaveQueueEvent<P>): void => {
    if (!options.onEvent) return;
    try {
      options.onEvent(event);
    } catch {
      // 监听方（React setState 等）出错不能打断保存队列
    }
  };

  const baseOf = (clipId: string): number | null => revisions.get(clipId) ?? null;

  const nextClip = (): string | null => {
    for (const clipId of due) {
      if (!staged.has(clipId)) {
        due.delete(clipId);
        continue;
      }
      if (!blocked.has(clipId)) return clipId;
    }
    return null;
  };

  const sendOne = async (clipId: string): Promise<void> => {
    const project = staged.get(clipId) as P;
    staged.delete(clipId);
    due.delete(clipId);
    const baseRevision = baseOf(clipId);
    current = { clipId, project, baseRevision };
    emit({ type: 'saving', clipId, project, baseRevision });
    let result: SaveSendResult | null = null;
    let failed = false;
    let failure: unknown = null;
    try {
      result = await options.send(clipId, project, baseRevision);
      if (!result || result.saved === false) {
        failed = true;
        failure = new Error('服务器未确认保存');
      }
    } catch (error: unknown) {
      failed = true;
      failure = error;
    }
    current = null;
    if (!failed) {
      const revision = readRevision(result);
      if (revision !== null) revisions.set(clipId, revision);
      emit({ type: 'saved', clipId, project, revision, pending: staged.has(clipId) });
      return;
    }
    // 失败：期间已有更新的快照就保留更新的那份，否则把这份放回，绝不丢最后一次编辑
    if (!staged.has(clipId)) staged.set(clipId, project);
    const latestProject = staged.get(clipId) as P;
    const info = conflictFromError(failure);
    if (info) {
      const conflict: SaveConflict = { ...info, clipId, baseRevision };
      blocked.set(clipId, { kind: 'conflict', conflict });
      emit({ type: 'conflict', clipId, project: latestProject, conflict });
      return;
    }
    const message = errorMessage(failure);
    blocked.set(clipId, { kind: 'error', message });
    emit({ type: 'error', clipId, project: latestProject, message, error: failure });
  };

  const drain = async (): Promise<void> => {
    for (let clipId = nextClip(); clipId !== null; clipId = nextClip()) {
      await sendOne(clipId);
    }
  };

  const kick = (): void => {
    if (active || nextClip() === null) return;
    active = true;
    loop = (async () => {
      try {
        await drain();
      } finally {
        active = false;
        loop = null;
        // 循环结束与新快照到期之间的空窗：再检查一次
        kick();
      }
    })();
  };

  const hasUnsaved = (clipId?: string): boolean => {
    if (clipId === undefined) return staged.size > 0 || blocked.size > 0 || current !== null;
    return staged.has(clipId) || blocked.has(clipId) || (current !== null && current.clipId === clipId);
  };

  const stage = (clipId: string, project: P): void => {
    staged.set(clipId, project);
    const block = blocked.get(clipId);
    if (block && block.kind === 'error') blocked.delete(clipId);
  };

  const markDue = (clipId?: string): void => {
    if (clipId === undefined) {
      for (const id of staged.keys()) due.add(id);
    } else if (staged.has(clipId)) {
      due.add(clipId);
    }
    kick();
  };

  const flush = async (flushOptions?: { retryErrors?: boolean }): Promise<boolean> => {
    if (flushOptions?.retryErrors) {
      for (const [clipId, block] of blocked) {
        if (block.kind === 'error') blocked.delete(clipId);
      }
    }
    for (const clipId of staged.keys()) due.add(clipId);
    kick();
    while (loop) await loop;
    return !hasUnsaved();
  };

  const latest = (clipId: string): P | null => {
    if (staged.has(clipId)) return staged.get(clipId) as P;
    return current !== null && current.clipId === clipId ? current.project : null;
  };

  const conflict = (clipId: string): SaveConflict | null => {
    const block = blocked.get(clipId);
    return block && block.kind === 'conflict' ? block.conflict : null;
  };

  const setRevision = (clipId: string, revision: number | null): void => {
    if (revision === null || !Number.isFinite(revision)) revisions.delete(clipId);
    else revisions.set(clipId, revision);
  };

  const resolveConflict = (clipId: string, choice: 'overwrite' | 'reload', project?: P): boolean => {
    const block = blocked.get(clipId);
    if (!block || block.kind !== 'conflict') return false;
    blocked.delete(clipId);
    setRevision(clipId, block.conflict.serverRevision);
    if (choice === 'reload') {
      staged.delete(clipId);
      due.delete(clipId);
      return true;
    }
    if (project !== undefined) staged.set(clipId, project);
    if (staged.has(clipId)) {
      due.add(clipId);
      kick();
    }
    return true;
  };

  const unsaved = (): UnsavedSnapshot<P>[] => {
    const list: UnsavedSnapshot<P>[] = [];
    for (const [clipId, project] of staged) {
      const block = blocked.get(clipId);
      const state: UnsavedState = block
        ? block.kind
        : current !== null && current.clipId === clipId
          ? 'behind-in-flight'
          : 'pending';
      list.push({ clipId, project, baseRevision: baseOf(clipId), state });
    }
    if (current !== null && !staged.has(current.clipId)) {
      list.push({ clipId: current.clipId, project: current.project, baseRevision: current.baseRevision, state: 'in-flight' });
    }
    return list;
  };

  const acknowledge = (clipId: string, project: P, revision: number | null): void => {
    const cleared = staged.has(clipId) && staged.get(clipId) === project;
    if (cleared) {
      staged.delete(clipId);
      due.delete(clipId);
      blocked.delete(clipId);
    }
    if (revision !== null && Number.isFinite(revision)) revisions.set(clipId, revision);
    if (cleared) emit({ type: 'saved', clipId, project, revision, pending: false });
  };

  const drop = (clipId: string): void => {
    staged.delete(clipId);
    due.delete(clipId);
    blocked.delete(clipId);
  };

  return {
    stage,
    markDue,
    flush,
    hasUnsaved,
    latest,
    conflict,
    resolveConflict,
    getRevision: baseOf,
    setRevision,
    unsaved,
    acknowledge,
    drop,
  };
}
