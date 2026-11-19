import { useEffect, useState } from 'react';
import { HostDrawer } from './HostDrawer';
import type { EditorProject } from '../../api/types';
import { BACKEND_EXPORT_FORMATS, UNIMPLEMENTED_EXPORT_FORMATS, exportBusy, exportSelectionBlockReason, isBackendExportFormat } from './exportBridge';
import type { ExportColorSpace, ExportSelection, ExportState } from './exportBridge';
import { canRevealExport, revealExportInFolder } from './desktopReveal';
import './r66-editor-next-export.css';

const statusLabel: Record<string, string> = { pending: '等待渲染', queued: '等待渲染', running: '正在渲染', paused: '已暂停',
  completed: '渲染完成', failed: '渲染失败', cancelled: '已取消' };
export function ExportPanel(props: {
  state: ExportState; project: EditorProject; independentProject?: boolean; blockReason: string | null; bridgeWarning: string | null;
  downloadUrl: string | null; onClose: () => void; onStart: (selection: ExportSelection) => void; onCancel: () => void;
}) {
  const [format, setFormat] = useState<string>(() => !props.independentProject && isBackendExportFormat(props.project.format ?? '') ? props.project.format! : 'h264');
  const [colorSpace, setColorSpace] = useState<ExportColorSpace>(() => {
    if (props.independentProject) return null;
    const value = (props.project as EditorProject & { colorSpace?: string }).colorSpace;
    if (props.project.format === 'hevc' && (value === 'hdr10' || value === 'bt709')) return value;
    if (props.project.format === 'dnxhr' && value === 'hdr10') return value;
    return null;
  });
  const [burnSubtitles, setBurnSubtitles] = useState<boolean>(() => !!props.project.burnSubtitles);
  const [revealing, setRevealing] = useState(false);
  const [revealError, setRevealError] = useState<string | null>(null);
  const { state } = props;
  const busy = exportBusy(state);
  const blocked = props.blockReason ?? exportSelectionBlockReason({ format, colorSpace });
  const progress = Math.round(Math.max(0, Math.min(1, state.job?.progress ?? 0)) * 100);
  const desktop = canRevealExport();
  const jobId = state.render?.status === 'completed' ? state.render.id : null;
  useEffect(() => { setRevealError(null); }, [jobId]);
  const reveal = () => {
    if (!jobId || revealing) return;
    setRevealing(true); setRevealError(null);
    revealExportInFolder(jobId).catch((error: unknown) => setRevealError(error instanceof Error ? error.message : '无法打开所在文件夹。'))
      .finally(() => setRevealing(false));
  };
  const burnText = burnSubtitles
    ? `字幕烧录：开启（${Array.isArray(props.project.timelineCaptions) ? '独立字幕轨' : '源文稿字幕'}）。仅此次导出，不修改工程设置。`
    : '字幕烧录：关闭。字幕不会烧入成片画面；仅此次导出，不修改工程设置。';
  const taskText = state.preparing ? '正在保存并确认工程…' : state.enqueuing ? '正在提交后端渲染任务…' : state.cancelling ? '正在取消任务…'
    : state.render ? (statusLabel[state.render.status] ?? `任务状态：${state.render.status}`) : '尚未开始导出';
  return <HostDrawer id="editor-next-export" className="editor-next-export" title="导出视频"
    open={state.open} onClose={props.onClose} restoreFocus={false} describedBy="editor-next-export-intro">
      <p id="editor-next-export-intro" className="editor-next-export-intro">使用已确认保存的工程，由 LiveClipper / FFmpeg 后端渲染，不调用浏览器编码器。</p>
      {state.archiveWarnings.map(issue => <p key={`${issue.code}:${issue.clipIds?.[0] ?? issue.path}`} role="status" aria-live="polite" className="editor-next-export-warning">
        {issue.code.startsWith('graphics_preview_only')
          ? <>{issue.message}</>
          : <>本次导出不渲染已归档的{issue.code === 'effects_archived_not_rendered' ? '片段效果' : 'GPU 效果'}；工程中仍保留这些编辑。片段：{issue.clipIds?.join('、')}</>}
      </p>)}
      <div className="editor-next-export-body">
        <fieldset disabled={busy} className="editor-next-export-settings">
          <legend>编码设置</legend>
          <label htmlFor="editor-next-export-format">输出格式</label>
          <select id="editor-next-export-format" value={format} onChange={event => { setFormat(event.target.value); setColorSpace(null); }}>
            <optgroup label="后端已支持">
              {BACKEND_EXPORT_FORMATS.filter(spec => !props.independentProject || spec.value === 'h264').map(spec => <option key={spec.value} value={spec.value}>{spec.label}</option>)}
            </optgroup>
            <optgroup label="复刻格式 · 未接入后端">
              {UNIMPLEMENTED_EXPORT_FORMATS.map(spec => <option key={spec.value} value={spec.value} disabled={spec.disabled}>{spec.label} — {spec.reason}</option>)}
            </optgroup>
          </select>
          {(format === 'hevc' || format === 'dnxhr') && <>
            <label htmlFor="editor-next-export-color">色彩与位深</label>
            <select id="editor-next-export-color" value={colorSpace ?? ''} onChange={event => setColorSpace(event.target.value === '' ? null : event.target.value as ExportColorSpace)}>
              <option value="">8-bit · 不新增色彩标记{format === 'dnxhr' ? ' · SQ' : ''}</option>
              {format === 'hevc' && <option value="bt709">8-bit · BT.709</option>}
              <option value="hdr10">10-bit 输出 · HDR10 / BT.2020 PQ{format === 'dnxhr' ? ' · HQX' : ''}</option>
            </select>
          </>}
          <p className="editor-next-export-note">分辨率、帧率、码率与音频沿用已保存工程。</p>
          <label className="editor-next-export-burn" htmlFor="editor-next-export-burn">
            <input id="editor-next-export-burn" type="checkbox" checked={burnSubtitles} onChange={event => setBurnSubtitles(event.target.checked)} />
            烧录字幕（仅本次导出）
          </label>
          <p className="editor-next-export-note" data-burn-subtitles={burnSubtitles ? 'on' : 'off'}>{burnText}</p>
        </fieldset>
        <section className="editor-next-export-task" aria-label="导出任务" aria-live="polite" aria-atomic="true">
          <h3>导出任务</h3>
          <p className="editor-next-export-status" data-status={state.render?.status ?? 'idle'}>{taskText}</p>
          {state.render && <p className="editor-next-export-job">任务：{state.render.id}</p>}
          {state.job && state.render?.status !== 'completed' && <label>渲染进度：{progress}%<progress aria-label={`渲染进度：${progress}%`} max={100} value={progress} /></label>}
          {state.pin && <p className="editor-next-export-note">已固定保存确认的修订 {state.pin.serverRevision} · 引擎修订 {state.pin.engineRevision}。导出中的后续编辑不会替换此任务内容。</p>}
        </section>
      </div>
      {props.bridgeWarning && <p role="status">{props.bridgeWarning}</p>}
      {blocked && <p id="editor-next-export-block" role="status" className="editor-next-export-warning">{blocked}</p>}
      {state.error && <p role="alert" className="editor-next-export-error">{state.error}</p>}
      {state.pollError && <p role="status" className="editor-next-export-warning">{state.pollError}</p>}
      {revealError && <p role="alert" className="editor-next-export-error">{revealError}</p>}
      <footer>
        {busy ? <button type="button" disabled={state.cancelling} onClick={props.onCancel}>取消导出</button>
          : <button type="button" className="editor-next-export-primary" disabled={!!blocked} aria-describedby={blocked ? 'editor-next-export-block' : undefined}
            onClick={() => props.onStart({ format, colorSpace, burnSubtitles })}>开始导出</button>}
        {jobId && desktop && <button type="button" className="editor-next-export-download" disabled={revealing} onClick={reveal}>{revealing ? '正在打开…' : '在文件夹中显示'}</button>}
        {props.downloadUrl && !desktop && <a className="editor-next-export-download" href={props.downloadUrl} download>下载成片</a>}
      </footer>
      <details className="editor-next-export-details"><summary>技术详情</summary>
        <p>10-bit / HDR 走后端高位深渲染路径，不使用浏览器的 HDR 标签替代编码；不会执行 SDR → HDR 色调映射。DNxHR 默认 SQ 8-bit，选择 HDR10 后使用后端 HQX 10-bit。编码可用性以服务端校验及 FFmpeg 返回为准，实际源素材的色彩保真需核验成片。</p>
        <p>未接入的复刻格式：</p>
        <ul>{UNIMPLEMENTED_EXPORT_FORMATS.map(spec => <li key={spec.value}>{spec.label} — {spec.reason}</li>)}</ul>
      </details>
    </HostDrawer>;
}
