import { useEffect, useState } from 'react';
import { LoaderCircle } from 'lucide-react';
import type { EditorProject, EditorSequenceSummary } from '../../../api/types';
import {
  createEditorSequence,
  deleteEditorSequence,
  getEditorSequence,
  getEditorSequences,
} from '../../../api/client';

interface SequenceDialogProps {
  open: boolean;
  clipId: string | null;
  project: EditorProject | null;
  onClose: () => void;
  onLoad: (project: EditorProject, name: string) => void;
}

function dateLabel(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '未知时间';
  return date.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  });
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export function SequenceDialog({ open, clipId, project, onClose, onLoad }: SequenceDialogProps) {
  const [items, setItems] = useState<EditorSequenceSummary[]>([]);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [loadingId, setLoadingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const refresh = async (id: string) => {
    const next = await getEditorSequences(id);
    setItems(next);
    return next;
  };

  useEffect(() => {
    if (!open || !clipId) return undefined;
    let cancelled = false;
    setError(null);
    setNotice(null);
    setBusy(true);
    void getEditorSequences(clipId)
      .then((next) => {
        if (cancelled) return;
        setItems(next);
        setName(`序列 ${next.length + 1}`);
      })
      .catch((reason: unknown) => {
        if (!cancelled) setError(messageOf(reason));
      })
      .finally(() => {
        if (!cancelled) setBusy(false);
      });
    return () => {
      cancelled = true;
    };
  }, [clipId, open]);

  useEffect(() => {
    if (!open) return undefined;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [onClose, open]);

  if (!open) return null;

  const create = async () => {
    if (!clipId || !project) return;
    const trimmed = name.trim();
    if (!trimmed) {
      setError('请给新序列起一个名字');
      return;
    }
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await createEditorSequence(clipId, trimmed, project);
      const next = await refresh(clipId);
      setName(`序列 ${next.length + 1}`);
      setNotice(`已保存为「${trimmed}」`);
    } catch (reason: unknown) {
      setError(messageOf(reason));
    } finally {
      setBusy(false);
    }
  };

  const load = async (item: EditorSequenceSummary) => {
    setLoadingId(item.id);
    setError(null);
    try {
      const payload = await getEditorSequence(item.id);
      onLoad(payload.project, item.name);
    } catch (reason: unknown) {
      setError(messageOf(reason));
    } finally {
      setLoadingId(null);
    }
  };

  const remove = async (item: EditorSequenceSummary) => {
    if (!clipId) return;
    if (!window.confirm(`删除序列「${item.name}」？此操作不会影响当前工程。`)) return;
    setLoadingId(item.id);
    setError(null);
    setNotice(null);
    try {
      await deleteEditorSequence(item.id);
      const next = await refresh(clipId);
      setName(`序列 ${next.length + 1}`);
      setNotice(`已删除「${item.name}」`);
    } catch (reason: unknown) {
      setError(messageOf(reason));
    } finally {
      setLoadingId(null);
    }
  };

  return (
    <div
      className="help-backdrop"
      role="presentation"
      onPointerDown={(event) => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <section className="help-dialog sequence-dialog" role="dialog" aria-modal="true" aria-label="管理序列">
        <header>
          <div>
            <span className="help-kicker">STUDIO · SEQUENCES</span>
            <h2>管理序列</h2>
          </div>
          <button type="button" className="help-close" onClick={onClose} aria-label="关闭">×</button>
        </header>
        <div className="sequence-create">
          <label htmlFor="sequence-name">把当前时间线保存为新序列</label>
          <div className="sequence-create-row">
            <input
              id="sequence-name"
              className="text-input"
              value={name}
              maxLength={100}
              placeholder="例如：直播回放 · 预告版"
              onChange={(event) => setName(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') void create();
              }}
            />
            <button type="button" className="primary-button" disabled={busy || !project} onClick={() => void create()}>
              {busy ? <LoaderCircle size={13} className="spin" /> : null}
              保存为新序列
            </button>
          </div>
          <p className="sequence-hint">新序列会保存当前时间线、字幕和效果参数，不会改动原工程。</p>
        </div>
        <div className="sequence-list-head">
          <span>已保存序列</span>
          <span>{items.length} 个</span>
        </div>
        <div className="sequence-list">
          {busy && items.length === 0 ? (
            <div className="sequence-empty"><LoaderCircle size={14} className="spin" /> 正在读取序列…</div>
          ) : items.length === 0 ? (
            <div className="sequence-empty">还没有保存过序列。先保存一个，之后可以把不同剪法分开管理。</div>
          ) : (
            items.map((item) => (
              <div className="sequence-row" key={item.id}>
                <div>
                  <strong>{item.name}</strong>
                  <span>创建于 {dateLabel(item.createdAt)}</span>
                </div>
                <button
                  type="button"
                  className="sequence-load"
                  disabled={loadingId !== null}
                  onClick={() => void load(item)}
                >
                  {loadingId === item.id ? <LoaderCircle size={12} className="spin" /> : null}
                  载入
                </button>
                <button
                  type="button"
                  className="sequence-delete"
                  disabled={loadingId !== null}
                  onClick={() => void remove(item)}
                  aria-label={`删除序列 ${item.name}`}
                >
                  删除
                </button>
              </div>
            ))
          )}
        </div>
        {notice ? <div className="sequence-notice" role="status">{notice}</div> : null}
        {error ? <div className="sequence-error" role="alert">{error}</div> : null}
        <footer>
          <button type="button" className="primary-button" onClick={onClose}>关闭</button>
        </footer>
      </section>
    </div>
  );
}
