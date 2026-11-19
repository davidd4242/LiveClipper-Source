import type { ClipDto } from '../../api/types';
import type { AudioAsset } from '../../api/music';
import type { ExportAction, StudioDetail } from './types';
import {
  clipTitle,
  dateLabel,
  fileName,
  formatPercent,
  formatSeconds,
  tracksInUse,
} from './workbench';

interface ProjectDetailTabProps {
  clip: ClipDto | null;
  detail: StudioDetail;
  assets: AudioAsset[];
  exportBusy: ExportAction | null;
  onExport: (action: ExportAction) => void;
  onOpenEditor: (clipId: string) => void;
  onRefreshDetail: () => void;
}

function Kv({ label, value, title }: { label: string; value: string; title?: string }) {
  return (
    <div className="wb-kv" title={title}>
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

export function ProjectDetailTab({
  clip,
  detail,
  assets,
  exportBusy,
  onExport,
  onOpenEditor,
  onRefreshDetail,
}: ProjectDetailTabProps) {
  const editor = detail.editor;
  const project = editor?.project ?? null;
  const timeline = project?.timeline ?? [];
  const tracks = tracksInUse(timeline.map((item) => item.track ?? 1));
  const transcriptState = detail.transcriptState;
  const transcript = editor?.transcript ?? null;
  const latestBackup = detail.backups[0] ?? null;

  const transcriptLabel = transcript
    ? `${transcript.segments.length} 段 · ${transcript.language || '语言未标注'}`
    : transcriptState?.transcriptVersion
      ? `版本 ${transcriptState.transcriptVersion.slice(0, 8)}`
      : '无转写';

  const subtitleLabel = clip?.srtPath ? `已挂载 ${fileName(clip.srtPath)}` : '无外挂字幕';
  const musicAsset = project?.musicAssetId
    ? assets.find((asset) => asset.id === project.musicAssetId)?.name ?? '未知素材'
    : '未配置';
  const effectAsset = project?.effectAssetId
    ? assets.find((asset) => asset.id === project.effectAssetId)?.name ?? '未知素材'
    : '未配置';
  const stemsLabel = editor?.stems ? '有分离记录' : '未接入';

  const canExport = Boolean(project) && !detail.loading && !exportBusy;

  return (
    <section className="wb-tab" aria-label="项目详情">
      <header className="wb-detail-head">
        <div className="wb-detail-title">
          <span className="wb-kicker">PROJECT DETAIL</span>
          <h2>{clip ? clipTitle(clip) : '未选择项目'}</h2>
          <p className="wb-detail-path" title={clip?.path}>
            {clip?.path ?? '从左栏选择一个项目，或先导入素材'}
          </p>
        </div>
        <div className="wb-actions">
          <button
            type="button"
            className="wb-button"
            disabled={!clip || !editor}
            onClick={() => clip && onOpenEditor(clip.id)}
          >
            在编辑器中打开
          </button>
          <button type="button" className="wb-button wb-button--ghost" disabled={!clip} onClick={onRefreshDetail}>
            重新载入详情
          </button>
        </div>
      </header>

      {!clip ? (
        <div className="wb-empty">
          <strong>还没有选中项目</strong>
          <span>左侧「项目」列表来自 /api/clips；选中后会加载编辑器的真实工程数据。</span>
        </div>
      ) : detail.loading && !editor ? (
        <div className="wb-empty">
          <strong>正在读取工程…</strong>
          <span>/api/studio/clips/{clip.id.slice(0, 8)}/editor</span>
        </div>
      ) : detail.error ? (
        <div className="wb-error">
          <strong>工程读取失败</strong>
          <span>{detail.error}</span>
          <button type="button" className="wb-button" onClick={onRefreshDetail}>
            重试
          </button>
        </div>
      ) : !editor || !project ? (
        <div className="wb-empty">
          <strong>项目没有可用工程</strong>
          <span>后端返回的 editor 数据为空，无法展示工程详情。</span>
        </div>
      ) : (
        <>
          <div className="wb-action-strip">
            <div className="wb-action-strip-title">
              <span className="wb-kicker">EXPORT</span>
              <span>导出使用选中项目当前保存的工程文件</span>
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
              <button
                type="button"
                className="wb-button"
                disabled={!canExport}
                onClick={() => onExport('package')}
              >
                {exportBusy === 'package' ? '提交中…' : '素材包'}
              </button>
              <button
                type="button"
                className="wb-button"
                disabled={!canExport}
                onClick={() => onExport('draft')}
              >
                {exportBusy === 'draft' ? '提交中…' : '旧 V1 烘焙草稿（非 v3）'}
              </button>
            </div>
          </div>

          <div className="wb-section">
            <header className="wb-section-head">
              <h3>基础信息</h3>
              <span>来源 /api/clips + /api/studio/clips/{clip.id.slice(0, 8)}/editor</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="标题" value={clipTitle(clip)} />
              <Kv label="素材 ID" value={clip.id} title={clip.id} />
              <Kv label="创建时间" value={dateLabel(clip.createdAt)} />
              <Kv label="文件路径" value={clip.path} title={clip.path} />
              <Kv label="时长" value={formatSeconds(editor.duration)} />
              <Kv label="分辨率" value={`${editor.width} × ${editor.height}`} />
              <Kv label="帧率" value={`${project.fps} fps`} />
              <Kv label="工程分辨率" value={`${project.resolution}p`} />
              <Kv label="目标码率" value={project.bitrateKbps > 0 ? `${project.bitrateKbps} kbps` : '自动'} />
              <Kv label="工程时间范围" value={`${formatSeconds(project.start)} – ${formatSeconds(project.end)}`} />
            </div>
          </div>

          <div className="wb-section">
            <header className="wb-section-head">
              <h3>转写与字幕</h3>
              <span>来源 /transcript-state + editor.transcript</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="转写状态" value={transcriptLabel} />
              <Kv label="转写版本" value={transcriptState?.transcriptVersion || '未接入'} />
              <Kv label="自动转写方案" value={transcriptState?.autoStatus || '未接入'} />
              <Kv label="自动保留片段" value={`${transcriptState?.autoKeepCount ?? 0} 段`} />
              <Kv label="自动裁剪时长" value={formatSeconds(transcriptState?.autoTrimmedSeconds ?? 0)} />
              <Kv label="外挂字幕" value={subtitleLabel} title={clip.srtPath ?? undefined} />
              <Kv label="烧录字幕" value={project.burnSubtitles ? '开' : '关'} />
              <Kv label="字幕字体" value={`${project.subtitleFont} · ${project.subtitleSize}px`} />
            </div>
          </div>

          <div className="wb-section">
            <header className="wb-section-head">
              <h3>时间线与工程</h3>
              <span>来源 editor.project</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="时间线片段" value={`${timeline.length} 段`} />
              <Kv label="使用轨道" value={tracks} />
              <Kv label="转场" value={`${project.transitions?.length ?? 0} 个`} />
              <Kv label="标记" value={`${project.markers?.length ?? 0} 个`} />
              <Kv label="事件片段" value={`${editor.events.length} 个`} />
              <Kv label="工程快照" value={`${detail.backups.length} 份`} />
              <Kv label="最近修改" value={latestBackup ? dateLabel(latestBackup.createdAt) : '未接入'} />
              <Kv label="序列变体" value={`${detail.variants.length} 个`} />
            </div>
          </div>

          <div className="wb-section">
            <header className="wb-section-head">
              <h3>音频与素材</h3>
              <span>来源 editor.project + /api/assets</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="配乐" value={musicAsset} title={project.musicAssetId ?? undefined} />
              <Kv label="音效" value={effectAsset} title={project.effectAssetId ?? undefined} />
              <Kv label="音乐音量" value={formatPercent(project.musicVolume)} />
              <Kv label="音效音量" value={formatPercent(project.effectVolume)} />
              <Kv label="人声分离" value={stemsLabel} />
              <Kv label="语音为主" value={project.voiceOnly ? '开' : '关'} />
              <Kv label="压低配乐" value={project.duckMusic ? '开' : '关'} />
              <Kv label="裁剪余量" value={`${project.subtitleMargin}`} />
            </div>
          </div>
        </>
      )}
    </section>
  );
}
