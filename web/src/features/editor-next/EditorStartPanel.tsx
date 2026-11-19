import { useCallback, useEffect, useRef, useState } from 'react';
import type { ChangeEvent, DragEvent } from 'react';
import './EditorStartPanel.css';
import { getClips, getEditor, getVideoUrl } from '../../api/client';
import { importStudioFiles } from '../../api/studio';
import type { StudioImportResult } from '../../api/studio';
import { listEditorProjects } from '../../api/editorProjects';
import type { ClipDto, EditorProjectIdentity } from '../../api/types';
import { navigateApp } from '../../app/appNavigation';
import { PAGES } from '../../app/AppShell';
import { VideoThumbnail } from '../../components/VideoThumbnail';
import { clipTitle, dateLabel } from '../studio/workbench';

const RECENT_LIMIT = 6;
const VIDEO_EXT = ['.mp4', '.mkv', '.mov', '.flv', '.webm', '.m4v', '.ts'];
const SUBTITLE_EXT = ['.srt', '.vtt', '.ass', '.ssa'];
const ACCEPT = [...VIDEO_EXT, ...SUBTITLE_EXT].join(',');
const MAX_FILES = 200;
const MAX_TOTAL_BYTES = 16 * 1024 ** 3;

type StartState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'ready'; clips: ClipDto[] };

type Busy = null | { kind: 'import' } | { kind: 'open'; clipId: string };

interface OpenFailure { clipId: string; message: string }

function timeOf(clip: ClipDto): number {
  const value = Date.parse(clip.createdAt);
  return Number.isNaN(value) ? Number.NEGATIVE_INFINITY : value;
}

export function recentClips(clips: readonly ClipDto[], limit = RECENT_LIMIT): ClipDto[] {
  return [...clips].sort((left, right) => timeOf(right) - timeOf(left)).slice(0, limit);
}

function extOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot < 0 ? '' : name.slice(dot).toLowerCase();
}

/** 返回第一条拒绝原因；通过则 null。规则对齐后端：格式、1-200 个、总 16GiB、同名 400。 */
function rejectReason(files: readonly File[]): string | null {
  if (files.length === 0) return '没有检测到文件。请拖入视频文件，或点“选择文件”。';
  const bad = files.filter((file) => !VIDEO_EXT.includes(extOf(file.name)) && !SUBTITLE_EXT.includes(extOf(file.name)));
  if (bad.length > 0) {
    const shown = bad.slice(0, 3).map((file) => file.name).join('、');
    return `不支持的文件：${shown}${bad.length > 3 ? ` 等 ${bad.length} 个` : ''}。支持视频 ${VIDEO_EXT.join(' ')}，字幕 ${SUBTITLE_EXT.join(' ')}。`;
  }
  if (files.length > MAX_FILES) return `一次最多导入 ${MAX_FILES} 个文件，当前 ${files.length} 个。`;
  if (files.reduce((sum, file) => sum + file.size, 0) > MAX_TOTAL_BYTES) return '一次导入总大小不能超过 16 GiB。';
  const seen = new Set<string>();
  for (const file of files) {
    const key = file.name.toLowerCase();
    if (seen.has(key)) return `同一次导入里有重名文件：${file.name}。请分开导入。`;
    seen.add(key);
  }
  return null;
}

function errText(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

function resultText(result: StudioImportResult): string {
  const parts = [`新增 ${result.added} 个`, `已存在 ${result.existing} 个`];
  parts.push(`匹配字幕 ${result.matchedSubtitles} 个`);
  if (result.missingSubtitles > 0) parts.push(`${result.missingSubtitles} 个视频没有同名字幕`);
  return `导入完成：${parts.join('，')}。`;
}

export function EditorStartPanel({ onNew }: { onNew?: () => void } = {}) {
  const [projects, setProjects] = useState<EditorProjectIdentity[]>([]);
  const [projectsError, setProjectsError] = useState<string | null>(null);
  const [state, setState] = useState<StartState>({ status: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [busy, setBusy] = useState<Busy>(null);
  const busyRef = useRef(false);
  const aliveRef = useRef(true);
  const inputRef = useRef<HTMLInputElement>(null);
  const dragDepth = useRef(0);
  const [dragging, setDragging] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [rejected, setRejected] = useState<string | null>(null);
  const [importError, setImportError] = useState<string | null>(null);
  const [importedIds, setImportedIds] = useState<string[]>([]);
  const [openFailure, setOpenFailure] = useState<OpenFailure | null>(null);

  useEffect(() => {
    aliveRef.current = true;
    return () => { aliveRef.current = false; };
  }, []);

  useEffect(() => {
    let cancelled = false;
    setState({ status: 'loading' });
    setProjectsError(null);
    listEditorProjects().then(items => { if (!cancelled) setProjects(items); }, error => { if (!cancelled) setProjectsError(errText(error, '工程列表读取失败')); });
    getClips().then(
      (clips) => { if (!cancelled) setState({ status: 'ready', clips: recentClips(clips) }); },
      (error: unknown) => {
        if (!cancelled) setState({ status: 'error', message: errText(error, '素材列表读取失败') });
      },
    );
    return () => { cancelled = true; };
  }, [attempt]);

  /** 上传成功不等于可编辑：先 GET 编辑载荷并校验尺寸/时长，再导航。 */
  const open = useCallback(async (clipId: string) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy({ kind: 'open', clipId });
    setOpenFailure(null);
    try {
      const payload = await getEditor(clipId);
      const ok = [payload.width, payload.height].every((value) => Number.isFinite(value) && value > 0) &&
        (payload.projectIdentity ? payload.duration >= 0 : payload.duration > 0);
      if (!ok) throw new Error('素材时长或画面尺寸无效，可能仍在处理或文件已损坏');
      await navigateApp(`/editor?clip=${encodeURIComponent(clipId)}`);
    } catch (error: unknown) {
      if (aliveRef.current) setOpenFailure({ clipId, message: errText(error, '无法读取素材') });
    } finally {
      busyRef.current = false;
      if (aliveRef.current) setBusy(null);
    }
  }, []);

  const startImport = useCallback(async (files: File[]) => {
    if (busyRef.current) return;
    setNotice(null);
    setImportError(null);
    setOpenFailure(null);
    setImportedIds([]);
    const reason = rejectReason(files);
    setRejected(reason);
    if (reason) return;
    busyRef.current = true;
    setBusy({ kind: 'import' });
    let ids: string[] = [];
    try {
      const result = await importStudioFiles(files);
      ids = [...new Set((result.clipIds ?? []).filter((id) => typeof id === 'string' && id !== ''))];
      if (!aliveRef.current) return;
      setNotice(resultText(result) + (ids.length === 0 ? '本次没有可直接打开的视频。' : ''));
      setImportedIds(ids);
      setAttempt((value) => value + 1);
    } catch (error: unknown) {
      if (aliveRef.current) setImportError(errText(error, '导入请求失败'));
      return;
    } finally {
      busyRef.current = false;
      if (aliveRef.current) setBusy(null);
    }
    if (ids.length === 1 && aliveRef.current) void open(ids[0]);
  }, [open]);

  const onPick = (event: ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(event.target.files ?? []);
    event.target.value = '';
    if (files.length === 0) return;
    void startImport(files);
  };

  const hasFiles = (event: DragEvent) => Array.from(event.dataTransfer.types).includes('Files');
  const onDragEnter = (event: DragEvent) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    dragDepth.current += 1;
    setDragging(true);
  };
  const onDragOver = (event: DragEvent) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = busyRef.current ? 'none' : 'copy';
  };
  const onDragLeave = (event: DragEvent) => {
    if (!hasFiles(event)) return;
    dragDepth.current = Math.max(0, dragDepth.current - 1);
    if (dragDepth.current === 0) setDragging(false);
  };
  const onDrop = (event: DragEvent) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    dragDepth.current = 0;
    setDragging(false);
    void startImport(Array.from(event.dataTransfer.files));
  };

  const importing = busy?.kind === 'import';
  const opening = busy?.kind === 'open' ? busy.clipId : null;
  const recent = state.status === 'ready' ? state.clips : [];
  const titleOf = (id: string) => {
    const clip = recent.find((item) => item.id === id);
    return clip ? clipTitle(clip) : id;
  };

  return (
    <section className="editor-start" aria-label="未选择工程">
      <header className="editor-start__head">
        {onNew && <button type="button" className="editor-start__pick" disabled={busy !== null} onClick={onNew}>新建空白工程…</button>}
        <h1 className="editor-start__title">选择素材开始剪辑</h1>
        <button type="button" className="editor-start__link" onClick={() => { void navigateApp(PAGES.studio.href); }}>去素材页</button>
      </header>

      <section aria-label="独立工程">
        <h2 className="editor-start__sub">独立工程</h2>
        {projectsError && <p role="alert">{projectsError}<button type="button" onClick={() => setAttempt(value => value + 1)}>重试</button></p>}
        <ul className="editor-start__projects">{projects.map(project => <li key={project.id}>
          <button type="button" disabled={busy !== null} onClick={() => { void open(project.id); }}><strong title={project.name}>{project.name}</strong><small>{project.width} × {project.height} · {project.fps} fps</small></button>
        </li>)}</ul>
      </section>
      <div
        className={`editor-start__drop${dragging ? ' is-over' : ''}${busy ? ' is-busy' : ''}`}
        onDragEnter={onDragEnter}
        onDragOver={onDragOver}
        onDragLeave={onDragLeave}
        onDrop={onDrop}
      >
        <p className="editor-start__drop-title">拖入视频开始剪辑</p>
        <button type="button" className="editor-start__pick" disabled={busy !== null} onClick={() => inputRef.current?.click()}>
          选择文件
        </button>
        <input ref={inputRef} type="file" hidden multiple accept={ACCEPT} onChange={onPick} />
        <p className="editor-start__hint">可同时附上同名字幕（.srt .vtt .ass .ssa）。视频支持 mp4、mkv、mov、flv、webm、m4v、ts。</p>
        <div className="editor-start__live" aria-live="polite">
          {importing && <span>正在导入…</span>}
          {opening && <span>正在检查并打开素材…</span>}
        </div>
      </div>

      {rejected && <p className="editor-start__note editor-start__note--warn" role="alert">{rejected}</p>}
      {importError && (
        <div className="editor-start__note editor-start__note--warn" role="alert">
          <span>导入失败：{importError}。可能已有部分素材登记，可刷新列表核对后再决定是否重试。</span>
          <button type="button" className="editor-start__link" disabled={busy !== null} onClick={() => setAttempt((value) => value + 1)}>刷新素材列表</button>
        </div>
      )}
      {notice && <p className="editor-start__note" role="status">{notice}</p>}
      {openFailure && (
        <div className="editor-start__note editor-start__note--warn" role="alert">
          <span>素材已导入，但无法打开「{titleOf(openFailure.clipId)}」：{openFailure.message}</span>
          <button type="button" className="editor-start__link" disabled={busy !== null} onClick={() => { void open(openFailure.clipId); }}>重试打开</button>
          <button type="button" className="editor-start__link" onClick={() => { void navigateApp(PAGES.studio.href); }}>去素材页</button>
        </div>
      )}

      {importedIds.length > 1 && (
        <>
          <h2 className="editor-start__sub">本次导入，选择一个打开</h2>
          <ul className="editor-start__picks">
            {importedIds.map((id) => (
              <li key={id}>
                <button type="button" className="editor-start__link" disabled={busy !== null} onClick={() => { void open(id); }}>
                  {titleOf(id)}
                </button>
              </li>
            ))}
          </ul>
        </>
      )}

      <h2 className="editor-start__sub">最近录制</h2>
      {state.status === 'loading' && <p className="editor-start__note" role="status">正在读取最近素材…</p>}
      {state.status === 'error' && (
        <div className="editor-start__note" role="alert">
          <span>最近素材读取失败：{state.message}</span>
          <button type="button" className="editor-start__link" onClick={() => setAttempt((value) => value + 1)}>重试</button>
        </div>
      )}
      {state.status === 'ready' && state.clips.length === 0 && (
        <p className="editor-start__note">还没有录制素材。可拖入视频导入，或到素材页查看。</p>
      )}
      {state.status === 'ready' && state.clips.length > 0 && (
        <ul className="editor-start__grid">
          {state.clips.map((clip) => {
            const title = clipTitle(clip);
            return (
              <li key={clip.id}>
                <button type="button" className="editor-start__card" title={title} disabled={busy !== null} onClick={() => { void open(clip.id); }}>
                  <span className="editor-start__thumb">
                    <span className="editor-start__nopreview">无预览</span>
                    <VideoThumbnail src={getVideoUrl(clip.id)} alt={`${title} 缩略图`} seekRatio={0.18} />
                  </span>
                  <span className="editor-start__name">{title}</span>
                  <span className="editor-start__meta">{dateLabel(clip.createdAt)}</span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
