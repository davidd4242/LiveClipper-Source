import type { TimelineItem } from '../../api/types';
import {
  itemOutputDuration,
  itemSequenceStart,
  itemSpeed,
  trackItems,
  trackOf,
} from './project';

/**
 * FilmCraft「编辑点」v1 模型（会话态，不进 EditorProject —— 新增字段会被后端 DTO round-trip 丢弃）。
 *
 * 语义来源：docs/projects/FC-P2-tools-trim-spec.md §2.7 / §3。
 * - edge='in'  = 片段入点；edge='out' = 片段出点。
 * - kind='trim'   = 普通修剪（下游保持原位，靠 gapBefore 补偿）
 * - kind='ripple' = 波纹修剪（下游跟随，序列变长/变短）
 * - kind='roll'   = 滚动编辑（切点移动，总长不变；要求相邻无空隙）
 */
export type EditPointEdge = 'in' | 'out';
export type EditPointKind = 'trim' | 'ripple' | 'roll';
export type EditPoint = { clipId: string; edge: EditPointEdge; kind: EditPointKind };

export type TrimApplyOptions = {
  fps: number;
  sourceDuration?: number;
};

export type TrimApplyResult =
  | { ok: true; items: TimelineItem[]; frames: number; label: string }
  | { ok: false; reason: string };

export const EDIT_POINT_KIND_LABELS: Record<EditPointKind, string> = {
  trim: '修剪',
  ripple: '波纹修剪',
  roll: '滚动编辑',
};

const roundFrameAt = (value: number, fps: number) => Math.round(value * fps) / fps;
const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

/** 编辑点种类循环：修剪 → 波纹 → 滚动 → 修剪（FilmCraft Ctrl+T）。 */
export function nextEditPointKind(kind: EditPointKind): EditPointKind {
  if (kind === 'trim') return 'ripple';
  if (kind === 'ripple') return 'roll';
  return 'trim';
}

/** 「大修剪」帧数：FC 默认 5 帧，音频轨 100 帧（trim info.largeTrimOffset）。 */
export function largeTrimFrames(track: number): number {
  return track >= 4 ? 100 : 5;
}

/** 编辑点当前对应的序列时间（in = 片段起点，out = 片段终点）。 */
export function editPointSequenceTime(
  items: TimelineItem[],
  editPoint: EditPoint,
  fps: number,
  transitions?: unknown,
  sourceDurations?: Record<string, number>,
): number | null {
  const item = items.find((entry) => entry.id === editPoint.clipId);
  if (!item) return null;
  const start = itemSequenceStart(
    items,
    item.id,
    transitions as never,
    fps,
    sourceDurations,
  );
  return editPoint.edge === 'in' ? start : start + itemOutputDuration(item);
}

/**
 * 所有「真实剪切点」（按序列时间升序），供 Shift+T 选最近 / Q、W 跳切点。
 *
 * 只收同一轨道上相邻且无空隙的两个片段之间的切点，归属前一段的出点（edge='out'）。
 * 序列首尾不是剪切点，因此不会把「入点=0 且左侧是 (gap)」的无效滚动编辑选进来。
 */
export function collectEditPoints(
  items: TimelineItem[],
  fps: number,
  transitions?: unknown,
  sourceDurations?: Record<string, number>,
): Array<{ editPoint: EditPoint; time: number }> {
  const lanes = new Map<number, TimelineItem[]>();
  for (const item of items) {
    if (item.disabled) continue;
    const track = trackOf(item);
    const lane = lanes.get(track);
    if (lane) lane.push(item);
    else lanes.set(track, [item]);
  }
  const candidates: Array<{ editPoint: EditPoint; time: number }> = [];
  for (const lane of lanes.values()) {
    for (let index = 0; index < lane.length - 1; index += 1) {
      const left = lane[index]!;
      const right = lane[index + 1]!;
      if (Math.max(0, right.gapBefore ?? 0) > 0.001) continue;
      const start = itemSequenceStart(
        items,
        left.id,
        transitions as never,
        fps,
        sourceDurations,
      );
      candidates.push({
        editPoint: { clipId: left.id, edge: 'out', kind: 'roll' },
        time: start + itemOutputDuration(left),
      });
    }
  }
  return candidates.sort((a, b) => a.time - b.time);
}

/** 找离给定时间最近的编辑点（FilmCraft `trim.selectNearest`）。 */
export function nearestEditPoint(
  items: TimelineItem[],
  time: number,
  fps: number,
  transitions?: unknown,
  sourceDurations?: Record<string, number>,
): EditPoint | null {
  const candidates = collectEditPoints(items, fps, transitions, sourceDurations);
  if (candidates.length === 0) return null;
  let best = candidates[0];
  let bestDistance = Math.abs(best.time - time);
  for (const candidate of candidates) {
    const distance = Math.abs(candidate.time - time);
    if (distance < bestDistance) {
      best = candidate;
      bestDistance = distance;
    }
  }
  return best.editPoint;
}

/**
 * 把某个编辑点按「输出帧数」偏移，返回新的 items 数组（纯函数，不产生副作用）。
 * 预览与提交都必须走这里，保证「预览 = 提交」同源（fc-spec §2.11 #5）。
 */
export function applyEditPointTrim(
  items: TimelineItem[],
  editPoint: EditPoint,
  frames: number,
  options: TrimApplyOptions,
): TrimApplyResult {
  const fps = options.fps > 0 ? options.fps : 30;
  const minLen = Math.max(1 / fps, 0.001);
  const item = items.find((entry) => entry.id === editPoint.clipId);
  if (!item) return { ok: false, reason: '该编辑点所属片段已不存在' };
  if (item.disabled) return { ok: false, reason: '片段已禁用：先启用再修剪' };
  if (Math.abs(frames) < 0.001) return { ok: false, reason: '没有可提交的修剪变化' };

  const track = trackOf(item);
  const lane = trackItems(items, track);
  const index = lane.findIndex((entry) => entry.id === item.id);
  if (index < 0) return { ok: false, reason: '找不到该片段所在轨道' };
  const deltaSeconds = frames / fps;
  const sourceDelta = deltaSeconds * itemSpeed(item);
  const sourceLimit = Math.max(item.end, options.sourceDuration || item.end);

  // ── 滚动编辑：切点移动，左右片段长度互补，序列总长不变 ─────────────────
  if (editPoint.kind === 'roll') {
    const left = editPoint.edge === 'in' ? lane[index - 1] : item;
    const right = editPoint.edge === 'in' ? item : lane[index + 1];
    if (!left || !right) {
      return {
        ok: false,
        reason:
          editPoint.edge === 'in'
            ? '这是轨道第一个片段，前面没有切点可滚动'
            : '这是轨道最后一个片段，后面没有切点可滚动',
      };
    }
    if (Math.max(0, right.gapBefore ?? 0) > 0.001) {
      return { ok: false, reason: '切点前有空隙：先闭合空隙再滚动编辑' };
    }
    const leftSpeed = itemSpeed(left);
    const rightSpeed = itemSpeed(right);
    const leftOut = itemOutputDuration(left);
    const rightSourceLen = right.end - right.start;
    const leftTail = Math.max(0, (options.sourceDuration || left.end) - left.end);
    const lower = Math.max(-(leftOut - minLen) / leftSpeed, -right.start / rightSpeed);
    const upper = Math.min(leftTail / leftSpeed, (rightSourceLen - minLen) / rightSpeed);
    const delta = clamp(roundFrameAt(deltaSeconds, fps), lower, upper);
    if (Math.abs(delta) < 0.0005) return { ok: false, reason: '切点已到可移动范围的边缘' };
    const nextLeftEnd = roundFrameAt(left.end + delta * leftSpeed, fps);
    const nextRightStart = roundFrameAt(right.start + delta * rightSpeed, fps);
    if (Math.abs(nextLeftEnd - left.end) < 0.0005 || Math.abs(nextRightStart - right.start) < 0.0005) {
      return { ok: false, reason: '切点已到可移动范围的边缘' };
    }
    return {
      ok: true,
      frames: Math.round(delta * fps),
      label: '动态滚动编辑',
      items: items.map((entry) => {
        if (entry.id === left.id) return { ...entry, end: nextLeftEnd };
        if (entry.id === right.id) return { ...entry, start: nextRightStart };
        return entry;
      }),
    };
  }

  // ── 普通 / 波纹修剪 ─────────────────────────────────────────────────
  let nextStart = item.start;
  let nextEnd = item.end;
  if (editPoint.edge === 'in') {
    nextStart = clamp(
      roundFrameAt(clamp(item.start + sourceDelta, 0, item.end - minLen), fps),
      0,
      Math.max(0, item.end - minLen),
    );
  } else {
    nextEnd = clamp(
      roundFrameAt(clamp(item.end + sourceDelta, item.start + minLen, sourceLimit), fps),
      item.start + minLen,
      sourceLimit,
    );
  }
  if (Math.abs(nextStart - item.start) < 0.0005 && Math.abs(nextEnd - item.end) < 0.0005) {
    return { ok: false, reason: '已到可修剪范围的边缘' };
  }

  const linkIds = new Set<string>([
    item.id,
    ...(item.linkGroup
      ? items.filter((entry) => entry.linkGroup === item.linkGroup).map((entry) => entry.id)
      : []),
  ]);
  const deltaLen =
    itemOutputDuration({ ...item, start: nextStart, end: nextEnd }) - itemOutputDuration(item);
  const gapDelta = -deltaLen;
  const gapAdjust = new Map<string, number>();
  if (editPoint.kind === 'trim' && editPoint.edge === 'out') {
    for (const trimmed of items.filter((entry) => linkIds.has(entry.id))) {
      const trimmedLane = trackItems(items, trackOf(trimmed));
      const trimmedIndex = trimmedLane.findIndex((entry) => entry.id === trimmed.id);
      const next = trimmedIndex >= 0 ? trimmedLane[trimmedIndex + 1] : undefined;
      if (next) gapAdjust.set(next.id, (gapAdjust.get(next.id) ?? 0) + gapDelta);
    }
  }
  const nextItems = items.map((entry) => {
    const isTrimmed = linkIds.has(entry.id);
    const base = isTrimmed ? { ...entry, start: nextStart, end: nextEnd } : entry;
    let adjust = gapAdjust.get(entry.id) ?? 0;
    if (editPoint.kind === 'trim' && editPoint.edge === 'in' && isTrimmed) adjust += gapDelta;
    if (Math.abs(adjust) < 0.0005) return base;
    return {
      ...base,
      gapBefore: Math.max(0, roundFrameAt((entry.gapBefore ?? 0) + adjust, fps)),
    };
  });
  const label =
    editPoint.kind === 'ripple'
      ? '动态波纹裁剪'
      : editPoint.edge === 'in'
        ? '动态裁剪入点'
        : '动态裁剪出点';
  return { ok: true, items: nextItems, frames: Math.round(frames), label };
}

export type TrimMonitorSide = {
  item: TimelineItem | null;
  /** 视频从素材内哪个秒数取帧：出点=最后一帧，入点=第一帧。 */
  frameSourceTime: number;
  track: number | null;
  gap: boolean;
};

export type TrimMonitorSides = {
  cutTime: number;
  out: TrimMonitorSide;
  in: TrimMonitorSide;
  kind: EditPointKind;
};

/**
 * Trim Monitor 两侧画面要显示的内容（fc-spec §3.2）：
 * 左 = 出点所属片（显示该片最后一帧 end-1/fps），右 = 入点所属片（第一帧 start）。
 * 某侧是 gap 时返回 gap=true，由 UI 画黑框 + (gap)。
 */
export function editPointSides(
  items: TimelineItem[],
  editPoint: EditPoint,
  fps: number,
  transitions?: unknown,
  sourceDurations?: Record<string, number>,
): TrimMonitorSides | null {
  const item = items.find((entry) => entry.id === editPoint.clipId);
  if (!item) return null;
  const lane = trackItems(items, trackOf(item));
  const index = lane.findIndex((entry) => entry.id === item.id);
  if (index < 0) return null;
  const safeFps = fps > 0 ? fps : 30;
  const frame = 1 / safeFps;
  let outgoing: TimelineItem | null = null;
  let incoming: TimelineItem | null = null;
  if (editPoint.kind === 'roll') {
    outgoing = editPoint.edge === 'in' ? lane[index - 1] ?? null : item;
    incoming = editPoint.edge === 'in' ? item : lane[index + 1] ?? null;
  } else if (editPoint.edge === 'out') {
    outgoing = item;
    incoming = lane[index + 1] ?? null;
  } else {
    outgoing = lane[index - 1] ?? null;
    incoming = item;
  }
  const startOf = (entry: TimelineItem) =>
    itemSequenceStart(items, entry.id, transitions as never, safeFps, sourceDurations);
  const cutTime = outgoing
    ? startOf(outgoing) + itemOutputDuration(outgoing)
    : incoming
      ? startOf(incoming)
      : 0;
  return {
    cutTime,
    kind: editPoint.kind,
    out: {
      item: outgoing,
      gap: !outgoing,
      track: outgoing ? trackOf(outgoing) : null,
      frameSourceTime: outgoing ? Math.max(outgoing.start, outgoing.end - frame) : 0,
    },
    in: {
      item: incoming,
      gap: !incoming,
      track: incoming ? trackOf(incoming) : null,
      frameSourceTime: incoming ? incoming.start : 0,
    },
  };
}
