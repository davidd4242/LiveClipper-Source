import type { TimelineItem } from '../../../../api/types';
import { formatTimecode, itemOutputDuration, itemSequenceStart, trackOf } from '../../project';
import { Field, Note, Section } from './ui';
import { trackLabel, type WorkspaceClip, type WorkspaceProject } from './types';

const FILTER_LABELS: Record<string, string> = { none: '原片', warm: '暖色', cool: '冷色', mono: '黑白', vivid: '鲜艳' };
const MOTION_LABELS: Record<string, string> = { none: '无', push: '推近', shake: '抖动' };
const KIND_LABELS: Record<string, string> = { video: '视频', hook: '钩子', insert: '插入' };
const POSITION_LABELS: Record<'top' | 'middle' | 'bottom', string> = { top: '顶部', middle: '中部', bottom: '底部' };

type MetadataPanelProps = {
  project: WorkspaceProject | null;
  selectedClip: WorkspaceClip | null;
  selectedItem: TimelineItem | null;
  items: TimelineItem[];
  fps: number;
  totalDuration: number;
  sequenceTime: number;
  transcriptCount: number;
  onSeek: (time: number) => void;
  onNotice: (message: string) => void;
};

/**
 * 元数据窗口：只读展示真实工程/素材/片段字段，并可复制一份 JSON 快照。
 * 所有数据来自 EditorPage 传入的 project、clips、items，不虚构占位值。
 */
export function MetadataPanel({
  project,
  selectedClip,
  selectedItem,
  items,
  fps,
  totalDuration,
  sequenceTime,
  transcriptCount,
  onSeek,
  onNotice,
}: MetadataPanelProps) {
  const itemIndex = selectedItem ? items.findIndex((item) => item.id === selectedItem.id) : -1;
  const sequenceStart = selectedItem
    ? itemSequenceStart(items, selectedItem.id, project?.transitions, project?.fps ?? fps)
    : 0;
  const sequenceEnd = selectedItem ? sequenceStart + itemOutputDuration(selectedItem) : 0;
  const resolution = project?.resolution;
  const subtitlePosition = project?.subtitlePosition ?? 'bottom';

  const copyMetadata = async () => {
    const payload = {
      project: {
        resolution: resolution ?? null,
        fps: project?.fps ?? null,
        bitrateKbps: project?.bitrateKbps ?? null,
        masterVolume: project?.masterVolume ?? null,
        burnSubtitles: project?.burnSubtitles ?? null,
        subtitleFont: project?.subtitleFont ?? null,
        subtitleSize: project?.subtitleSize ?? null,
        subtitleColor: project?.subtitleColor ?? null,
        subtitlePosition,
        subtitleOutline: project?.subtitleOutline ?? null,
        subtitleMargin: project?.subtitleMargin ?? null,
        sentenceMode: project?.sentenceMode ?? null,
        duckMusic: project?.duckMusic ?? null,
        voiceOnly: project?.voiceOnly ?? null,
      },
      sequence: {
        duration: totalDuration,
        itemCount: items.length,
        markerCount: project?.markers?.length ?? 0,
        playhead: sequenceTime,
      },
      clip: selectedClip
        ? {
            id: selectedClip.id,
            title: selectedClip.title ?? null,
            path: selectedClip.path ?? null,
            createdAt: selectedClip.createdAt ?? null,
          }
        : null,
      item: selectedItem ?? null,
    };
    const text = JSON.stringify(payload, null, 2);
    try {
      await navigator.clipboard.writeText(text);
      onNotice('已复制元数据 JSON');
    } catch {
      onNotice('浏览器拒绝访问剪贴板，元数据未复制');
    }
  };

  return (
    <div className="wsx-stack">
      <Note>元数据来自本机工程的真实字段，只读展示；片段级修改请用「属性」或「基本图形」窗口。</Note>

      {!project ? <Note tone="warning">工程还没载入，下面显示的是默认值。</Note> : null}

      <Section title="工程" hint="序列与输出">
        <Field label="分辨率" value={resolution ? `${resolution}p` : '默认 720p'} />
        <Field label="帧率" value={`${project?.fps ?? fps} fps`} />
        <Field label="目标码率" value={project?.bitrateKbps ? `${project.bitrateKbps} kbps` : '自动'} />
        <Field
          label="主输出"
          value={
            project?.masterVolume === null || project?.masterVolume === undefined
              ? '默认 100%'
              : `${Math.round(project.masterVolume * 100)}%`
          }
        />
        <Field label="序列时长" value={formatTimecode(totalDuration, fps)} />
        <Field label="片段 / 标记" value={`${items.length} / ${project?.markers?.length ?? 0}`} />
        <Field label="播放头" value={formatTimecode(sequenceTime, fps)} />
      </Section>

      <Section title="字幕" hint={transcriptCount ? `${transcriptCount} 段转写` : '无转写'}>
        <Field label="烧录字幕" value={project?.burnSubtitles ? '是' : '否'} />
        <Field
          label="字体 / 字号"
          value={`${project?.subtitleFont || '默认'} · ${project?.subtitleSize ?? 34}`}
        />
        <Field
          label="颜色 / 位置"
          value={`${project?.subtitleColor ?? '#FFFFFF'} · ${POSITION_LABELS[subtitlePosition]}`}
        />
        <Field label="描边 / 边距" value={`${project?.subtitleOutline ?? 0} / ${project?.subtitleMargin ?? 0}`} />
        <Field
          label="断句 / 压低音乐"
          value={`${project?.sentenceMode ? '开' : '关'} / ${project?.duckMusic ? '开' : '关'}`}
        />
      </Section>

      <Section title="素材">
        {selectedClip ? (
          <>
            <Field label="标题" value={selectedClip.title || '未命名'} />
            <Field label="素材 ID" value={selectedClip.id} />
            <Field label="路径" value={selectedClip.path || '—'} />
            <Field
              label="创建时间"
              value={selectedClip.createdAt ? new Date(selectedClip.createdAt).toLocaleString('zh-CN') : '—'}
            />
            <Field label="转写段数" value={`${transcriptCount}`} />
          </>
        ) : (
          <p className="window-muted">还没有选择素材。先在媒体区点一个素材。</p>
        )}
      </Section>

      <Section title="选中片段" hint={selectedItem ? `第 ${itemIndex + 1} 个` : '未选中'}>
        {selectedItem ? (
          <>
            <Field
              label="类型 / 轨道"
              value={`${KIND_LABELS[selectedItem.kind] ?? selectedItem.kind} · ${trackLabel(trackOf(selectedItem))}`}
            />
            <Field
              label="源入点 / 出点"
              value={`${formatTimecode(selectedItem.start, fps)} → ${formatTimecode(selectedItem.end, fps)}`}
            />
            <Field
              label="序列位置"
              value={`${formatTimecode(sequenceStart, fps)} → ${formatTimecode(sequenceEnd, fps)}`}
            />
            <Field
              label="速度 / 音量"
              value={`${selectedItem.speed ?? 1}× · ${selectedItem.volumeDb ?? 0} dB`}
            />
            <Field
              label="滤镜 / 运动"
              value={`${FILTER_LABELS[selectedItem.filter] ?? selectedItem.filter} · ${
                MOTION_LABELS[selectedItem.motion] ?? selectedItem.motion
              }`}
            />
            <Field
              label="淡入 / 淡出"
              value={`${(selectedItem.fadeIn ?? 0).toFixed(1)}s / ${(selectedItem.fadeOut ?? 0).toFixed(1)}s`}
            />
            <Field label="标题文本" value={selectedItem.titleText || '—'} />
            <div className="window-actions">
              <button type="button" className="window-button" onClick={() => onSeek(sequenceStart)}>
                定位片段
              </button>
            </div>
          </>
        ) : (
          <p className="window-muted">先选中时间线上的片段，这里会显示真实字段。</p>
        )}
      </Section>

      <div className="window-actions">
        <button type="button" className="primary-button" onClick={() => void copyMetadata()}>
          复制元数据 JSON
        </button>
      </div>
    </div>
  );
}
