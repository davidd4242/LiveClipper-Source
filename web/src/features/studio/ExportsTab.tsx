import { getRenderFileUrl } from '../../api/client';
import type { ClipDto, RenderDto } from '../../api/types';
import { getStudioOutputFileUrl, type StudioOutput } from '../../api/studio';
import type { ExportAction, StudioDetail } from './types';
import {
  clipTitle,
  dateLabel,
  fileName,
  formatPercent,
  outputKindLabel,
  outputStatusMeta,
  renderStatusMeta,
} from './workbench';

export type RenderControlAction = 'cancel' | 'pause' | 'resume' | 'retry';

interface ExportsTabProps {
  renders: RenderDto[];
  outputs: StudioOutput[];
  clips: ClipDto[];
  selectedClip: ClipDto | null;
  detail: StudioDetail;
  loading: boolean;
  error: string | null;
  exportBusy: ExportAction | null;
  controlBusy: string | null;
  onExport: (action: ExportAction) => void;
  onControlRender: (id: string, action: RenderControlAction) => void;
  onRefresh: () => void;
}

export function ExportsTab({
  renders,
  outputs,
  clips,
  selectedClip,
  detail,
  loading,
  error,
  exportBusy,
  controlBusy,
  onExport,
  onControlRender,
  onRefresh,
}: ExportsTabProps) {
  const canExport = Boolean(selectedClip && detail.editor?.project) && !exportBusy;
  const renderRows = [...renders].sort(
    (left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime(),
  );
  const outputRows = [...outputs].sort(
    (left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime(),
  );
  const completedRenders = renders.filter((render) => render.status === 'completed').length;

  const clipName = (clipId: string): string => {
    const clip = clips.find((item) => item.id === clipId);
    return clip ? clipTitle(clip) : clipId.slice(0, 8);
  };

  const renderActions = (render: RenderDto) => {
    const busy = controlBusy === render.id;
    switch (render.status) {
      case 'completed':
        return (
          <a className="wb-button wb-button--ghost" href={getRenderFileUrl(render.id)} download>
            下载
          </a>
        );
      case 'running':
        return (
          <>
            <button
              type="button"
              className="wb-button wb-button--ghost"
              disabled={busy}
              onClick={() => onControlRender(render.id, 'pause')}
            >
              暂停
            </button>
            <button
              type="button"
              className="wb-button wb-button--ghost"
              disabled={busy}
              onClick={() => onControlRender(render.id, 'cancel')}
            >
              取消
            </button>
          </>
        );
      case 'pending':
        return (
          <button
            type="button"
            className="wb-button wb-button--ghost"
            disabled={busy}
            onClick={() => onControlRender(render.id, 'cancel')}
          >
            取消
          </button>
        );
      case 'paused':
        return (
          <>
            <button
              type="button"
              className="wb-button wb-button--ghost"
              disabled={busy}
              onClick={() => onControlRender(render.id, 'resume')}
            >
              继续
            </button>
            <button
              type="button"
              className="wb-button wb-button--ghost"
              disabled={busy}
              onClick={() => onControlRender(render.id, 'cancel')}
            >
              取消
            </button>
          </>
        );
      case 'failed':
      case 'cancelled':
        return (
          <button
            type="button"
            className="wb-button wb-button--ghost"
            disabled={busy}
            onClick={() => onControlRender(render.id, 'retry')}
          >
            重试
          </button>
        );
      default:
        return <span className="wb-muted">—</span>;
    }
  };

  return (
    <section className="wb-tab" aria-label="导出队列">
      <header className="wb-detail-head">
        <div className="wb-detail-title">
          <span className="wb-kicker">EXPORTS</span>
          <h2>导出队列</h2>
          <p className="wb-detail-path">
            成片来自 /api/studio/renders，素材包与旧 V1 烘焙草稿（非 v3）来自 /api/studio/outputs；每 4 秒自动刷新一次。
          </p>
        </div>
        <div className="wb-actions">
          <span className="wb-count">
            {renders.length} 渲染 · {completedRenders} 完成 · {outputs.length} 导出
          </span>
          <button type="button" className="wb-button wb-button--ghost" onClick={onRefresh}>
            刷新队列
          </button>
        </div>
      </header>

      {error ? (
        <div className="wb-error">
          <strong>导出队列读取失败</strong>
          <span>{error}</span>
        </div>
      ) : null}

      <div className="wb-action-strip">
        <div className="wb-action-strip-title">
          <span className="wb-kicker">TRIGGER</span>
          <span>{selectedClip ? `为「${clipTitle(selectedClip)}」提交新的导出任务` : '先在左栏选择项目'}</span>
        </div>
        <div className="wb-actions">
          <button
            type="button"
            className="wb-button wb-button--primary"
            disabled={!canExport}
            onClick={() => onExport('render')}
          >
            {exportBusy === 'render' ? '提交中…' : '渲染成片'}
          </button>
          <button type="button" className="wb-button" disabled={!canExport} onClick={() => onExport('package')}>
            {exportBusy === 'package' ? '提交中…' : '素材包'}
          </button>
          <button type="button" className="wb-button" disabled={!canExport} onClick={() => onExport('draft')}>
            {exportBusy === 'draft' ? '提交中…' : '旧 V1 烘焙草稿（非 v3）'}
          </button>
        </div>
      </div>

      <div className="wb-section">
        <header className="wb-section-head">
          <h3>成片渲染</h3>
          <span>来源 GET /api/studio/renders · 下载 /api/studio/renders/&#123;id&#125;/file</span>
        </header>
        {loading && renderRows.length === 0 ? (
          <div className="wb-empty">
            <strong>正在读取渲染队列…</strong>
          </div>
        ) : renderRows.length === 0 ? (
          <div className="wb-empty">
            <strong>还没有渲染任务</strong>
            <span>在上方点击「渲染成片」，任务会出现在这里。</span>
          </div>
        ) : (
          <div className="wb-table-wrap">
            <table className="wb-table">
              <thead>
                <tr>
                  <th>状态</th>
                  <th>项目</th>
                  <th>输出文件</th>
                  <th>创建时间</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {renderRows.map((render) => {
                  const meta = renderStatusMeta(render.status);
                  return (
                    <tr key={render.id}>
                      <td>
                        <span className={`wb-badge wb-badge--${meta.tone}`}>{meta.label}</span>
                      </td>
                      <td className="wb-ellipsis" title={clipName(render.clipId)}>
                        {clipName(render.clipId)}
                      </td>
                      <td className="wb-ellipsis wb-mono" title={render.outputPath}>
                        {fileName(render.outputPath)}
                      </td>
                      <td>{dateLabel(render.createdAt)}</td>
                      <td>
                        <div className="wb-row-actions">{renderActions(render)}</div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="wb-section">
        <header className="wb-section-head">
          <h3>素材包与旧 V1 烘焙草稿（非 v3）</h3>
          <span>来源 GET /api/studio/outputs · 下载 /api/studio/packages/&#123;id&#125;/file</span>
        </header>
        {loading && outputRows.length === 0 ? (
          <div className="wb-empty">
            <strong>正在读取导出任务…</strong>
          </div>
        ) : outputRows.length === 0 ? (
          <div className="wb-empty">
            <strong>还没有素材包 / 草稿导出</strong>
            <span>在上方点击「素材包」或「旧 V1 烘焙草稿（非 v3）」提交任务。</span>
          </div>
        ) : (
          <div className="wb-table-wrap">
            <table className="wb-table">
              <thead>
                <tr>
                  <th>类型</th>
                  <th>状态</th>
                  <th>进度</th>
                  <th>项目</th>
                  <th>创建时间</th>
                  <th>说明</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {outputRows.map((output) => {
                  const meta = outputStatusMeta(output.status);
                  return (
                    <tr key={output.id}>
                      <td>{outputKindLabel(output.kind)}</td>
                      <td>
                        <span className={`wb-badge wb-badge--${meta.tone}`}>{meta.label}</span>
                      </td>
                      <td className="wb-mono">{formatPercent(output.progress)}</td>
                      <td className="wb-ellipsis" title={clipName(output.clipId)}>
                        {clipName(output.clipId)}
                      </td>
                      <td>{dateLabel(output.createdAt)}</td>
                      <td className="wb-output-note" title={output.error ?? ''}>
                        {output.error ? output.error : '—'}
                      </td>
                      <td>
                        {output.status === 'completed' ? (
                          <a className="wb-button wb-button--ghost" href={getStudioOutputFileUrl(output.id)} download>
                            下载
                          </a>
                        ) : (
                          <span className="wb-muted">—</span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}
