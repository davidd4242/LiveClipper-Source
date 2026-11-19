import { useEffect, useRef, useState } from 'react';
import './NewProjectDialog.css';
import { createEditorProject } from '../../api/editorProjects';
import type { EditorProjectIdentity } from '../../api/types';

export interface NewProjectDialogProps {
  onClose: () => void;
  onCreated: (project: EditorProjectIdentity) => Promise<boolean>;
}
/** Creating this dialog never creates a project. Only the explicit Create action does. */
export function NewProjectDialog({ onClose, onCreated }: NewProjectDialogProps) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => { dialog.current?.showModal(); }, []);
  const [name, setName] = useState('未命名工程');
  const [width, setWidth] = useState(1920);
  const [height, setHeight] = useState(1080);
  const [fps, setFps] = useState(30);
  const [busy, setBusy] = useState(false);
  const [created, setCreated] = useState<EditorProjectIdentity | null>(null);
  const [error, setError] = useState('');
  const open = async (project: EditorProjectIdentity) => {
    try { if (!await onCreated(project)) setError('工程已创建，但当前页面未能切换。请打开已创建的工程，不必重复新建。'); }
    catch (e) { setError(`工程已创建，打开失败：${e instanceof Error ? e.message : String(e)}`); }
  };
  const create = async () => {
    if (busy || created) return;
    if (!name.trim() || !Number.isInteger(width) || !Number.isInteger(height) || width < 16 || height < 16 || width > 16384 || height > 16384 || ![24, 25, 30, 50, 60].includes(fps)) {
      setError('请输入工程名称、有效的画布尺寸和帧率。'); return;
    }
    setBusy(true); setError('');
    try { const project = await createEditorProject({ name: name.trim(), width, height, fps }); setCreated(project); await open(project); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };
  return <dialog ref={dialog} aria-label="新建空白工程" className="new-project-dialog" onCancel={event => { event.preventDefault(); if (!busy) onClose(); }}>
    <h2>新建空白工程</h2>
    <p>只创建工程和空时间线，不复制素材、不生成占位视频。之后可导入媒体继续剪辑。</p>
    <fieldset disabled={busy || !!created}>
      <label>工程名称 <input aria-label="工程名称" maxLength={100} value={name} onChange={e => setName(e.target.value)} /></label>
      <label>画布宽度 <input aria-label="画布宽度" type="number" min={16} max={16384} step={1} value={width} onChange={e => setWidth(Number(e.target.value))} /></label>
      <label>画布高度 <input aria-label="画布高度" type="number" min={16} max={16384} step={1} value={height} onChange={e => setHeight(Number(e.target.value))} /></label>
      <label>帧率 <select aria-label="帧率" value={fps} onChange={e => setFps(Number(e.target.value))}>{[24, 25, 30, 50, 60].map(value => <option key={value} value={value}>{value} fps</option>)}</select></label>
    </fieldset>
    {error && <p role="alert">{error}</p>}
    {created ? <button type="button" disabled={busy} onClick={() => { void open(created); }}>打开已创建的工程</button>
      : <button type="button" disabled={busy} onClick={() => { void create(); }}>{busy ? '正在创建…' : '创建空白工程'}</button>}
    <button type="button" disabled={busy} onClick={onClose}>{created ? '关闭' : '取消'}</button>
  </dialog>;
}
