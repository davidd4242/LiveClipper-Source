import type { EditorProject, NestedSequence, TimelineItem } from '../../api/types';
import { itemOutputDuration, sequenceDuration, trackOf } from './project';

/** 后端 flatten 的嵌套深度上限（docs/filmcraft-parity-next5.md §4）；前端 v1 只读预览只展开一层。 */
export const MAX_NESTED_DEPTH = 4;

/** 面包屑的一段：序列 01 之后、子序列名之前。 */
export interface NestedBreadcrumbItem {
  id: string;
  name: string;
}

/** 时间线片段是否引用嵌套序列（后端 TimelineItem.NestedSequenceId）。 */
export function isNestedClip(item: TimelineItem | null | undefined): boolean {
  return Boolean(item && typeof item.nestedSequenceId === 'string' && item.nestedSequenceId.trim());
}

/** 按 id 在工程的 nestedSequences 字典里查子序列；找不到返回 null（不抛错）。 */
export function nestedSequenceFor(
  project: EditorProject | null | undefined,
  sequenceId: string | null | undefined,
): NestedSequence | null {
  const id = sequenceId?.trim();
  if (!project || !id) return null;
  return project.nestedSequences?.[id] ?? null;
}

/** 子序列自身的时间线时长（不含父时间线位置）。 */
export function nestedSequenceDuration(
  sequence: NestedSequence | null | undefined,
  fallbackFps = 30,
): number {
  if (!sequence) return 0;
  const fps = Number.isFinite(sequence.fps) && sequence.fps > 0 ? sequence.fps : fallbackFps;
  const duration = sequenceDuration(sequence.timeline ?? [], sequence.transitions ?? [], fps);
  return Number.isFinite(duration) && duration > 0 ? duration : 0;
}

/**
 * 嵌套片段在父时间线上的显示时长：
 * v1 后端契约没有 duration 字段，时长由子序列计算得到；子序列缺失/为空时退回片段自身输出时长。
 */
export function nestedClipDuration(
  project: EditorProject | null | undefined,
  item: TimelineItem | null | undefined,
): number {
  if (!item) return 0;
  const sequence = nestedSequenceFor(project, item.nestedSequenceId);
  const duration = nestedSequenceDuration(sequence, project?.fps || 30);
  return duration > 0 ? duration : itemOutputDuration(item);
}

/** 面包屑路径：path 为从主序列进入的 id 栈（v1 最多一层）。 */
export function nestedBreadcrumb(
  project: EditorProject | null | undefined,
  path: string[],
): NestedBreadcrumbItem[] {
  return path.map((id, index) => ({
    id,
    name: nestedSequenceFor(project, id)?.name ?? `子序列 ${index + 1}`,
  }));
}

/**
 * 为只读预览构造工程副本：timeline/transitions 换成子序列内容，清掉子序列自身的 nestedSequences，
 * 并重置标记/轨道开关，避免父序列状态影响子序列画面。绝不写回工程。
 */
export function buildNestedPreviewProject(
  project: EditorProject | null | undefined,
  sequence: NestedSequence | null | undefined,
): EditorProject | null {
  if (!project || !sequence) return null;
  const duration = nestedSequenceDuration(sequence, project.fps || 30);
  return {
    ...project,
    start: 0,
    end: duration > 0 ? duration : project.end,
    timeline: sequence.timeline,
    transitions: sequence.transitions ?? [],
    nestedSequences: null,
    markers: [],
    hiddenTracks: [],
    mutedTracks: [],
    soloedTracks: [],
    lockedTracks: [],
    trackOpacities: null,
    silenceTrimBackup: null,
    silenceTrimmed: false,
    silenceTrimmedSeconds: 0,
  };
}

/** 子序列预览时用哪个素材作为 <video> 源：优先 V1 首个片段，其次任意片段。 */
export function nestedPreviewClipId(items: TimelineItem[], fallbackClipId: string | null): string | null {
  const videoItems = items.filter((item) => trackOf(item) <= 3);
  const base = videoItems.find((item) => trackOf(item) === 1) ?? videoItems[0];
  const clipId = base?.clipId?.trim();
  return clipId || fallbackClipId;
}

/** 子序列是否引用了多个不同素材：单 <video> 预览只能播放其中一个，需提示。 */
export function nestedHasMultipleSources(items: TimelineItem[]): boolean {
  const ids = new Set(
    items
      .filter((item) => trackOf(item) <= 6)
      .map((item) => item.clipId)
      .filter((id) => typeof id === 'string' && id && id !== 'source'),
  );
  return ids.size > 1;
}

/** 只读子序列里的片段标签：优先用片段序号，避免继承父序列的 AI 事件标题。 */
export function nestedItemLabels(items: TimelineItem[]): Record<string, string> {
  const labels: Record<string, string> = {};
  items.forEach((item, index) => {
    labels[item.id] = isNestedClip(item) ? `嵌套片段 ${index + 1}` : `片段 ${String(index + 1).padStart(2, '0')}`;
  });
  return labels;
}
/** 创建嵌套序列的结果：project 为修改后的副本，sequenceId 为新子序列 id。 */
export interface CreateNestedResult {
  project: EditorProject;
  sequenceId: string;
  sequenceName: string;
}

/**
 * 把时间线上的单个片段封装成一级嵌套序列（v1.5 可创建）：
 * 子序列里放一份片段的完整副本（保留源入出点/速度/关键帧/蒙版/包络），
 * 父片段只保留 nestedSequenceId 与缩略素材 id；后端渲染时 flatten 展开子序列。
 * 返回 null 表示片段不存在、已经是嵌套片段、或在只读子序列视图里。
 */
export function createNestedSequence(
  project: EditorProject | null | undefined,
  itemId: string | null | undefined,
): CreateNestedResult | null {
  if (!project || !itemId) return null;
  const item = (project.timeline ?? []).find((entry) => entry.id === itemId);
  if (!item || isNestedClip(item)) return null;

  const existing = project.nestedSequences ?? {};
  let index = 1;
  while (existing[`seq-${index}`]) index += 1;
  const sequenceId = `seq-${index}`;
  const sequenceName = `子序列 ${index}`;

  const childItem: TimelineItem = {
    ...item,
    id: `${item.id}-child`,
    gapBefore: 0,
    linkGroup: null,
    nestedSequenceId: null,
  };
  const sequence: NestedSequence = {
    id: sequenceId,
    name: sequenceName,
    timeline: [childItem],
    transitions: [],
    fps: Number.isFinite(project.fps) && project.fps > 0 ? project.fps : 30,
    resolution: Number.isFinite(project.resolution) && project.resolution > 0 ? project.resolution : 720,
  };
  const parentItem: TimelineItem = {
    ...item,
    nestedSequenceId: sequenceId,
    linkGroup: null,
  };
  return {
    project: {
      ...project,
      timeline: (project.timeline ?? []).map((entry) => (entry.id === item.id ? parentItem : entry)),
      nestedSequences: { ...existing, [sequenceId]: sequence },
    },
    sequenceId,
    sequenceName,
  };
}
