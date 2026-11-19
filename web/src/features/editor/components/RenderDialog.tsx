import { useEffect, useMemo, useReducer, useRef, useState } from 'react';
import {
  AlertTriangle,
  CheckCircle2,
  Download,
  Film,
  LoaderCircle,
  RotateCw,
  X,
} from 'lucide-react';
import {
  controlRender,
  getJobs,
  getRenderFileUrl,
  getRenders,
  queueRender,
} from '../../../api/client';
import type { EditorProject, ExportFormat, JobDto, RenderDto } from '../../../api/types';
import { EXPORT_FORMATS, exportFormatSpec, normalizeExportFormat, withExportExtension } from '../exportFormats';
import { formatClock } from '../project';
import { RENDER_RESOLUTIONS, defaultResolution, isResolutionDowngraded, resolutionHint } from '../renderResolution';
import { generateFcproj } from '../../../api/bridge';
import { getExportJob, reduceExportJobs } from '../exportJobIdentity';
import type { ExportJobIdentity, ExportJobResult } from '../exportJobIdentity';

interface RenderDialogProps {
  open: boolean;
  clipId: string | null;
  clipTitle: string;
  project: EditorProject | null;
  duration: number;
  hasTranscript: boolean;
  /** 主素材/序列的真实高度；渲染只降不升，对话框据此给出可达的分辨率并说明。 */
  sourceHeight?: number;
  onClose: () => void;
  onApplySettings: (project: EditorProject) => void;
}

const ACTIVE_STATUSES = new Set(['pending', 'running', 'paused']);
const TERMINAL_STATUSES = new Set(['completed', 'failed', 'cancelled']);

const STATUS_LABEL: Record<string, string> = {
  pending: '排队中',
  running: '正在渲染',
  paused: '已暂停',
  completed: '渲染完成',
  failed: '渲染失败',
  cancelled: '已取消',
  unknown: '状态未知',
};

function basename(path: string): string {
  const parts = path.split(/[\\\\/]/);
  return parts[parts.length - 1] || path;
}

function dateLabel(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString('zh-CN', { hour12: false });
}

export function RenderDialog({
  open,
  clipId,
  clipTitle,
  project,
  duration,
  hasTranscript,
  sourceHeight = 0,
  onClose,
  onApplySettings,
}: RenderDialogProps) {
  const [backend, setBackend] = useState<'local' | 'filmcraft'>('local');
  const [cliJobs, dispatchCli] = useReducer(reduceExportJobs, {});
  const cliJob = getExportJob(cliJobs, clipId);
  const cliStatus = cliJob?.status ?? 'idle';
  const cliWarnings = cliJob?.warnings ?? [];
  const cliResult = cliJob?.result ?? null;
  const cliError = cliJob?.error ?? null;
  const cliControllers = useRef(new Map<string, { generation: number; controller: AbortController }>());
  const cliGeneration = useRef(0);
  const cliBusy = cliStatus === 'generating' || cliStatus === 'exporting';
  useEffect(() => () => {
    for (const { controller } of cliControllers.current.values()) controller.abort();
  }, []);
  const cancelCli = () => {
    if (!cliJob) return;
    const running = cliControllers.current.get(cliJob.identity.clipId);
    if (!running || running.generation !== cliJob.identity.generation) return;
    running.controller.abort();
    cliControllers.current.delete(cliJob.identity.clipId);
    dispatchCli({ type: 'update', identity: cliJob.identity, patch: { status: 'cancelled' } });
  };
  const [resolution, setResolution] = useState(720);
  const [fps, setFps] = useState(30);
  const [format, setFormat] = useState<ExportFormat>('h264');
  const [burnSubtitles, setBurnSubtitles] = useState(false);
  const [history, setHistory] = useState<RenderDto[]>([]);
  const [active, setActive] = useState<RenderDto | null>(null);
  const [job, setJob] = useState<JobDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [actionBusy, setActionBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [activeId, setActiveId] = useState<string | null>(null);

  const status = active?.status ?? job?.status ?? null;
  const activeStatus = status ?? '';
  const isActive = ACTIVE_STATUSES.has(activeStatus);
  const isDone = backend === 'local' && activeStatus === 'completed';
  const isFailed = activeStatus === 'failed';
  const isCancelled = activeStatus === 'cancelled';
  const progress = Math.max(0, Math.min(100, Math.round((job?.progress ?? 0) * 100)));

  useEffect(() => {
    if (!open) return;
    // D76：默认选到主素材还扛得住的最高档。沿用上次保存的值会让 1080p 工程默认导出 720p（onApplySettings 会回写这个字段）。
    setResolution(defaultResolution(sourceHeight));
    setFps(project?.fps || 30);
    setFormat(normalizeExportFormat(project?.format));
    setBurnSubtitles(Boolean(project?.burnSubtitles && hasTranscript));
    setError(null);
  }, [open, clipId, sourceHeight, project?.fps, project?.format, project?.burnSubtitles, hasTranscript]);

  useEffect(() => {
    if (!open) return undefined;
    let cancelled = false;
    void getRenders()
      .then((items) => {
        if (!cancelled) setHistory(items.slice(0, 5));
      })
      .catch(() => {
        if (!cancelled) setHistory([]);
      });
    return () => {
      cancelled = true;
    };
  }, [open]);

  useEffect(() => {
    if (!open || !activeId) return undefined;
    let cancelled = false;
    let timer = 0;

    const poll = async () => {
      try {
        const [renders, jobs] = await Promise.all([getRenders(), getJobs()]);
        if (cancelled) return;
        setHistory(renders.slice(0, 5));
        const current = renders.find((item) => item.id === activeId);
        if (current) {
          setActive(current);
          if (TERMINAL_STATUSES.has(current.status)) {
            setBusy(false);
            return;
          }
        }
        setJob(jobs.find((item) => item.id === activeId) ?? null);
      } catch (pollError) {
        if (!cancelled) setError(pollError instanceof Error ? pollError.message : String(pollError));
      }
      if (!cancelled) timer = window.setTimeout(() => void poll(), 1300);
    };

    void poll();
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [open, activeId]);

  const resetForAnotherRender = () => {
    setActiveId(null);
    setActive(null);
    setJob(null);
    setError(null);
    setBusy(false);
  };

  const startRender = async () => {
    if (!clipId || !project || busy || cliBusy || isActive) return;
    if (backend === 'filmcraft' && cliControllers.current.has(clipId)) return;
    setError(null);
    const nextProject: EditorProject = {
      ...structuredClone(project),
      resolution,
      fps,
      format,
      burnSubtitles: hasTranscript && burnSubtitles,
      bitrateKbps: 0,
    };
    onApplySettings(nextProject);
    if (backend === 'filmcraft') {
      const identity: ExportJobIdentity = { clipId, generation: ++cliGeneration.current, title: clipTitle };
      const controller = new AbortController();
      cliControllers.current.set(clipId, { generation: identity.generation, controller });
      dispatchCli({ type: 'start', identity });
      try {
        const generated = await generateFcproj(clipId, undefined, nextProject);
        if (controller.signal.aborted) return;
        dispatchCli({ type: 'update', identity, patch: { warnings: generated.warnings ?? [], status: 'exporting' } });
        const response = await fetch('/api/filmcraft/cli/export', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          signal: controller.signal,
          body: JSON.stringify({
            projectPath: generated.fcprojPath,
            outputPath: withExportExtension(
              generated.workDir.replace(/[\\\\/]$/, '') + '/render.mp4',
              format,
            ),
            start: 0,
            end: duration,
            format,
            timeoutSec: 1800,
            burnCaptions: nextProject.burnSubtitles,
          }),
        });
        const text = await response.text();
        if (controller.signal.aborted) return;
        let result: ExportJobResult;
        try { result = JSON.parse(text) as ExportJobResult; }
        catch { throw new Error(`FilmCraft CLI HTTP ${response.status}: ${text || '响应为空'}`); }
        dispatchCli({ type: 'update', identity, patch: { result } });
        if (!response.ok || !result.ok) throw new Error(result.message || result.code || `FilmCraft CLI HTTP ${response.status}`);
        dispatchCli({ type: 'update', identity, patch: { status: 'completed' } });
      } catch (cliFailure) {
        dispatchCli({ type: 'update', identity, patch: controller.signal.aborted
          ? { status: 'cancelled' }
          : { status: 'failed', error: cliFailure instanceof Error ? cliFailure.message : String(cliFailure) } });
      } finally {
        if (cliControllers.current.get(identity.clipId)?.controller === controller) {
          cliControllers.current.delete(identity.clipId);
        }
      }
      return;
    }
    setBusy(true);
    try {
      const queued = await queueRender(clipId, nextProject);
      const pendingRender: RenderDto = { ...queued, status: 'pending', error: null };
      setActiveId(queued.id);
      setActive(pendingRender);
      setJob({ id: queued.id, type: 'edit-render', eventId: '', status: 'pending', progress: 0, error: null });
      setHistory((items) => [
        pendingRender,
        ...items.filter((item) => item.id !== queued.id),
      ].slice(0, 5));
    } catch (renderError) {
      setError(renderError instanceof Error ? renderError.message : String(renderError));
      setBusy(false);
    }
  };

  const handleControl = async (action: 'cancel' | 'retry') => {
    if (!active) return;
    setActionBusy(true);
    setError(null);
    try {
      await controlRender(active.id, action);
      if (action === 'retry') {
        setActiveId(active.id);
        setActive({ ...active, status: 'pending', error: null });
        setJob({ id: active.id, type: 'edit-render', eventId: '', status: 'pending', progress: 0, error: null });
      }
    } catch (controlError) {
      setError(controlError instanceof Error ? controlError.message : String(controlError));
    } finally {
      setActionBusy(false);
    }
  };

  const fileUrl = useMemo(() => (active && isDone ? getRenderFileUrl(active.id) : null), [active, isDone]);

  if (!open) return null;

  return (
    <div className="render-overlay" role="presentation" onMouseDown={(event) => {
      if (event.target === event.currentTarget && !isActive && !cliBusy) onClose();
    }}>
      <section className="render-dialog" role="dialog" aria-modal="true" aria-labelledby="render-title">
        <header className="render-dialog-head">
          <div className="render-dialog-title">
            <Film size={15} />
            <div>
              <h2 id="render-title">导出影片</h2>
              <p>{clipTitle || '当前序列'} · {formatClock(duration)}</p>
            </div>
          </div>
          <button type="button" className="render-icon-button" onClick={onClose} aria-label="关闭导出窗口">
            <X size={14} />
          </button>
        </header>

        <label className="render-field">
          <span>渲染后端</span>
          <select value={backend} disabled={busy || cliBusy || isActive} onChange={(event) => setBackend(event.target.value as 'local' | 'filmcraft')}>
            <option value="local">原本地渲染（FFmpeg）</option>
            <option value="filmcraft">FilmCraft CLI · 套用所选编码</option>
          </select>
        </label>
        {cliStatus !== 'idle' ? (
          <div className={`render-status render-status-${cliStatus}`} aria-live="polite">
            <strong>{{ generating: '正在生成当前工程快照', exporting: 'FilmCraft CLI 正在渲染', completed: 'FilmCraft CLI 导出完成', failed: 'FilmCraft CLI 导出失败', cancelled: '取消请求已发送' }[cliStatus]}</strong>
            <p>任务：{cliJob?.identity.title || '未命名序列'} · clip {cliJob?.identity.clipId} · #{cliJob?.identity.generation}</p>
            {cliBusy ? <p>处理当前快照；后续编辑不影响本次导出。关闭窗口后可重开查看结果。</p> : null}
            {cliStatus === 'cancelled' ? <p>客户端请求已中止；未收到完成回执，不标记为导出成功。</p> : null}
            {cliWarnings.length ? <ul aria-label="工程转换警告">{cliWarnings.map((warning, index) => <li key={index}>{warning}</li>)}</ul> : null}
            {cliResult?.output ? <p className="render-output-path">{cliResult.output}</p> : null}
            {typeof cliResult?.bytes === 'number' ? <p>{cliResult.bytes.toLocaleString()} bytes</p> : null}
            {cliResult?.code ? <p>code: {cliResult.code}</p> : null}
            {typeof cliResult?.exitCode === 'number' ? <p>exitCode: {cliResult.exitCode}</p> : null}
            {cliError ? <p role="alert" className="render-error-text">{cliError}</p> : null}
            {cliBusy ? <button type="button" className="secondary-button" onClick={cancelCli}>取消 CLI 导出</button> : null}
          </div>
        ) : null}
        {backend === 'local' && active ? (
          <div className={`render-status render-status-${activeStatus}`}>
            <div className="render-status-line">
              {isDone ? <CheckCircle2 size={16} /> : isFailed ? <AlertTriangle size={16} /> : isCancelled ? <X size={16} /> : <LoaderCircle size={16} className="spin" />}
              <strong>{STATUS_LABEL[activeStatus] ?? '处理中'}</strong>
              {isActive ? <span className="render-status-pct">{progress}%</span> : null}
            </div>
            {isActive ? (
              <>
                <div className="render-progress-track" aria-hidden="true">
                  <div className="render-progress-fill" style={{ width: `${Math.max(7, progress)}%` }} />
                </div>
                <p>正在合成画面、对白与字幕。长切片可能需要几分钟，可以关掉弹窗，任务会继续。</p>
                <div className="render-status-actions">
                  <button type="button" className="secondary-button" onClick={() => void handleControl('cancel')} disabled={actionBusy}>
                    取消渲染
                  </button>
                </div>
              </>
            ) : null}
            {isDone ? (
              <>
                <p className="render-output-path">{basename(active.outputPath)}</p>
                <div className="render-status-actions">
                  {fileUrl ? (
                    <a className="primary-button" href={fileUrl} download>
                      <Download size={12} /> 下载 MP4
                    </a>
                  ) : null}
                  <button type="button" className="secondary-button" onClick={resetForAnotherRender}>
                    再导一版
                  </button>
                </div>
              </>
            ) : null}
            {isFailed || isCancelled ? (
              <>
                <p className="render-error-text">{active.error || (isCancelled ? '任务已取消。' : '渲染没有完成，请检查源素材或 FFmpeg。')}</p>
                <div className="render-status-actions">
                  <button type="button" className="primary-button" onClick={() => void handleControl('retry')} disabled={actionBusy}>
                    <RotateCw size={12} /> 重试
                  </button>
                  <button type="button" className="secondary-button" onClick={resetForAnotherRender}>重新设置</button>
                </div>
              </>
            ) : null}
          </div>
        ) : null}

        {!isActive && !isDone ? (
          <div className="render-settings">
            <div className="render-field-row">
              <label className="render-field">
                <span>分辨率</span>
                <select value={resolution} onChange={(event) => setResolution(Number(event.target.value))} disabled={busy || cliBusy}>
                  {RENDER_RESOLUTIONS.map((value) => (
                    <option key={value} value={value} disabled={isResolutionDowngraded(value, sourceHeight)}>
                      {value}p
                    </option>
                  ))}
                </select>
                <span className="render-field-hint">{resolutionHint(resolution, sourceHeight)}</span>
              </label>
              <label className="render-field">
                <span>帧率</span>
                <select value={fps} onChange={(event) => setFps(Number(event.target.value))} disabled={busy || cliBusy}>
                  <option value={30}>30 fps</option>
                  <option value={60}>60 fps</option>
                </select>
              </label>
              <label className="render-field">
                <span>编码</span>
                <select
                  value={format}
                  onChange={(event) => setFormat(event.target.value as ExportFormat)}
                  disabled={busy || cliBusy}
                >
                  {EXPORT_FORMATS.map((spec) => (
                    <option key={spec.value} value={spec.value}>
                      {spec.label}
                    </option>
                  ))}
                </select>
              </label>
            </div>
            <label className="render-check">
              <input
                type="checkbox"
                checked={burnSubtitles}
                onChange={(event) => setBurnSubtitles(event.target.checked)}
                disabled={!hasTranscript || busy || cliBusy}
              />
              <span>烧录字幕到画面</span>
              <em>{hasTranscript ? '使用当前时间线字幕样式' : '当前素材没有字幕'}</em>
            </label>
            <div className="render-summary">
              <span>输出</span>
              <strong>{exportFormatSpec(format).summary}</strong>
              <span>路径</span>
              <strong className="render-summary-path">自动保存到剪辑输出目录</strong>
            </div>
          </div>
        ) : null}

        {error ? (
          <div className="render-inline-error" role="alert">
            <AlertTriangle size={13} /> {error}
          </div>
        ) : null}

        {history.length > 0 ? (
          <div className="render-history">
            <div className="render-history-title">最近导出</div>
            {history.map((item) => (
              <div className="render-history-row" key={item.id}>
                <span className={`render-history-dot render-history-dot-${item.status}`} />
                <span className="render-history-name">{basename(item.outputPath)}</span>
                <span className="render-history-meta">{STATUS_LABEL[item.status] ?? item.status}</span>
                {item.status === 'completed' ? (
                  <a className="render-history-download" href={getRenderFileUrl(item.id)} download title="下载成片">
                    <Download size={12} />
                  </a>
                ) : null}
              </div>
            ))}
          </div>
        ) : null}

        <footer className="render-dialog-foot">
          <span>{active ? dateLabel(active.createdAt) : '本地渲染 · 素材不会上传'}</span>
          <div className="render-dialog-actions">
            <button type="button" className="secondary-button" onClick={onClose} disabled={false}>关闭</button>
            {!isActive && !isDone ? (
              <button
                type="button"
                className="primary-button"
                onClick={() => void startRender()}
                disabled={busy || cliBusy || !clipId || !project}
              >
                {busy ? <LoaderCircle size={12} className="spin" /> : <Film size={12} />}
                {busy || cliBusy ? '处理中…' : backend === 'filmcraft' ? '使用 FilmCraft CLI 导出' : '开始本地渲染'}
              </button>
            ) : null}
          </div>
        </footer>
      </section>
    </div>
  );
}
