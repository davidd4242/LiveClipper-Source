import { useState } from 'react';
import { AlertTriangle, History } from 'lucide-react';
import type { SaveConflict } from '../saveQueue';
import type { RecoveredDraft } from '../useEditorSession';
import './SaveStatusBars.css';

/**
 * R51-EP2 · 保存通路的两条提示条（J3 useEditorSession 的 saveConflict / recoveredDraft）。
 * - 冲突条：409 后队列阻塞该 clip，用户必须选「用我的版本覆盖」或「放弃本地，重新读取」才能继续保存。
 * - 草稿恢复条：上次离开时留下的本地草稿，恢复 / 丢弃；stale 时额外警告会覆盖服务器上更新的修改。
 * - 未保存标记：hasUnsavedChanges 为真但顶栏保存状态仍是 clean（例如被冲突阻塞）时补一个标记；
 *   其余情况顶栏的保存状态已经在说，不重复。
 */

export type ConflictChoice = 'overwrite' | 'reload';

export interface SaveStatusBarsProps {
  conflict: SaveConflict | null;
  onResolveConflict: (choice: ConflictChoice) => Promise<boolean>;
  recoveredDraft: RecoveredDraft | null;
  onApplyDraft: () => void;
  onDiscardDraft: () => void;
  hasUnsavedChanges: boolean;
  /** 顶栏保存状态已经显示「未保存 / 保存中 / 失败」时为 true，未保存标记就不再出现。 */
  saveStateShown: boolean;
}

const DRAFT_REASON_LABEL: Record<RecoveredDraft['reason'], string> = {
  'too-large': '工程过大，离开时无法即时上传',
  conflict: '离开时与服务器版本冲突',
  error: '离开时保存失败',
  unconfirmed: '离开时保存未确认',
  'keepalive-failed': '离开时后台保存没有发出',
};

export function conflictText(conflict: SaveConflict): string {
  const revision = conflict.serverRevision === null ? '' : `（服务器 revision ${conflict.serverRevision}）`;
  if (conflict.holder) {
    return `工程正被别的会话占用（${conflict.holder}），本次保存被拒绝${revision}。本地修改已保留。`;
  }
  return `服务器上的工程已被别处修改${revision}，本次保存被拒绝。本地修改已保留。`;
}

export function draftText(draft: RecoveredDraft): string {
  const when = new Date(draft.timestamp).toLocaleString('zh-CN', { hour12: false });
  return `上次离开时有未保存的修改（${DRAFT_REASON_LABEL[draft.reason] ?? draft.reason}，${when}）。`;
}

export function SaveStatusBars({
  conflict,
  onResolveConflict,
  recoveredDraft,
  onApplyDraft,
  onDiscardDraft,
  hasUnsavedChanges,
  saveStateShown,
}: SaveStatusBarsProps) {
  const [resolving, setResolving] = useState<ConflictChoice | null>(null);

  const resolve = (choice: ConflictChoice) => {
    if (resolving) return;
    setResolving(choice);
    void onResolveConflict(choice).finally(() => setResolving(null));
  };

  const showUnsaved = hasUnsavedChanges && !saveStateShown && !conflict;
  if (!conflict && !recoveredDraft && !showUnsaved) return null;

  return (
    <div className="save-status-bars">
      {conflict ? (
        <div className="save-status-bar save-status-bar--conflict" role="alert">
          <AlertTriangle size={12} aria-hidden="true" />
          <span className="save-status-bar__text" title={conflict.message}>
            {conflictText(conflict)}
          </span>
          <span className="save-status-bar__actions">
            <button
              type="button"
              className="save-status-bar__button save-status-bar__button--primary"
              disabled={resolving !== null}
              onClick={() => resolve('overwrite')}
            >
              {resolving === 'overwrite' ? '覆盖中…' : '用我的版本覆盖'}
            </button>
            <button
              type="button"
              className="save-status-bar__button"
              disabled={resolving !== null}
              onClick={() => resolve('reload')}
            >
              {resolving === 'reload' ? '读取中…' : '放弃本地，重新读取'}
            </button>
          </span>
        </div>
      ) : null}
      {recoveredDraft ? (
        <div className="save-status-bar save-status-bar--draft" role="status">
          <History size={12} aria-hidden="true" />
          <span className="save-status-bar__text">
            {draftText(recoveredDraft)}
            {recoveredDraft.stale ? (
              <strong className="save-status-bar__warn">
                服务器上的工程在这期间被改过，恢复会覆盖那些更新的修改。
              </strong>
            ) : null}
          </span>
          <span className="save-status-bar__actions">
            <button
              type="button"
              className="save-status-bar__button save-status-bar__button--primary"
              onClick={onApplyDraft}
            >
              恢复
            </button>
            <button type="button" className="save-status-bar__button" onClick={onDiscardDraft}>
              丢弃
            </button>
          </span>
        </div>
      ) : null}
      {showUnsaved ? (
        <div className="save-status-bar save-status-bar--unsaved" role="status" data-unsaved="true">
          <span className="save-status-bar__dot" aria-hidden="true" />
          <span className="save-status-bar__text">有未保存的修改</span>
        </div>
      ) : null}
    </div>
  );
}
