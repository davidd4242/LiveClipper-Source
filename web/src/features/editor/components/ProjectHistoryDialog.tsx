import { useEffect, useState } from 'react';
import { History, Loader2, RotateCcw } from 'lucide-react';
import { getProjectRevision, getProjectRevisions } from '../../../api/client';
import type { EditorProject, ProjectRevisionInfo } from '../../../api/types';

interface ProjectHistoryDialogProps {
  clipId: string;
  onRestore: (project: EditorProject) => void;
  onClose: () => void;
}

function formatTime(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN', { hour12: false });
}

function formatSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '—';
  return `${(bytes / 1024).toFixed(1)} KB`;
}

export function ProjectHistoryDialog({ clipId, onRestore, onClose }: ProjectHistoryDialogProps) {
  const [items, setItems] = useState<ProjectRevisionInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [restoring, setRestoring] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    setLoading(true);
    void getProjectRevisions(clipId)
      .then((list) => {
        if (alive) setItems(list);
      })
      .catch((reason: unknown) => {
        if (alive) setError(reason instanceof Error ? reason.message : String(reason));
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [clipId]);

  const restore = async (revision: ProjectRevisionInfo) => {
    setRestoring(revision.fileName);
    setError(null);
    try {
      const project = await getProjectRevision(clipId, revision.fileName);
      onRestore(project);
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setRestoring(null);
    }
  };

  return (
    <div className="help-backdrop" onPointerDown={(event) => event.target === event.currentTarget && onClose()}>
      <section className="help-dialog project-history-dialog" role="dialog" aria-modal="true" aria-label="工程历史">
        <header>
          <div>
            <div className="help-kicker">PROJECT HISTORY</div>
            <h2>工程历史</h2>
          </div>
          <button type="button" className="help-close" aria-label="关闭工程历史" onClick={onClose}>
            ×
          </button>
        </header>

        <p className="sequence-hint project-history-hint">
          每次工程被覆盖前会自动保存上一版，最多保留最近 30 版。恢复会先进入当前撤销栈，不会立刻丢失现在的内容。
        </p>

        <div className="sequence-list-head">
          <span>本地快照</span>
          <span>{items.length} 个</span>
        </div>
        <div className="sequence-list">
          {loading ? (
            <div className="sequence-empty">
              <Loader2 size={14} className="spin" /> 正在读取工程历史…
            </div>
          ) : items.length === 0 ? (
            <div className="sequence-empty">
              <History size={14} /> 还没有备份。下一次保存改动时，会自动保留上一版。
            </div>
          ) : (
            items.map((item) => (
              <div className="sequence-row" key={item.fileName}>
                <div>
                  <strong>{formatTime(item.createdAt)}</strong>
                  <span>{formatSize(item.size)}</span>
                </div>
                <button
                  type="button"
                  className="sequence-load"
                  disabled={restoring !== null}
                  onClick={() => void restore(item)}
                >
                  {restoring === item.fileName ? <Loader2 size={12} className="spin" /> : <RotateCcw size={12} />}
                  {restoring === item.fileName ? '恢复中…' : '恢复'}
                </button>
              </div>
            ))
          )}
        </div>

        {error ? <div className="sequence-error" role="alert">{error}</div> : null}

        <footer>
          <button type="button" className="primary-button" onClick={onClose}>
            关闭
          </button>
        </footer>
      </section>
    </div>
  );
}
