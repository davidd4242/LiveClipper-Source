import type { EditorProject } from '../../api/types';

export function independentRenderBlockReason(project: EditorProject): string | null {
  if (!project.timeline?.length) return '独立工程时间线为空，请先添加 V1 视频片段。';
  if (project.timeline.some(item => (item.kind ?? 'video') !== 'video' || (item.track ?? 1) !== 1 || !item.clipId || !!item.nestedSequenceId))
    return '独立工程暂仅支持 V1 真实视频和字幕，不支持其他轨道、图形或嵌套序列。';
  if (project.voiceOnly || project.effectAssetId)
    return '独立工程 MP4 暂不支持仅人声或全局音效。';
  return null;
}
