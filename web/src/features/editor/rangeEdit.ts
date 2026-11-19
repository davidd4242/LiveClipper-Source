import type { EditorTransition, TimelineItem } from '../../api/types';
import {
  itemOutputDuration,
  itemSpeed,
  sequenceLayout,
  trackOf,
} from './project';

/**
 * FilmCraft `sequence.lift` / `sequence.extract`（提升 / 提取）。
 *
 * 语义（crates/engine/src/commands.rs:2177-2205）：
 * - lift（提升，`;`）：删掉 In–Out 区间内的内容，区间变成空隙，下游片段位置不变，In/Out 保留。
 * - extract（提取，`'`）：删掉区间内容并把下游合拢，In/Out 清空，播放头回到区间起点。
 *
 * LiveClipper 的序列位置由「片段顺序 + gapBefore」推导，所以这里先把目标轨拆成
 * 「绝对序列 offset + 片段」的临时列表，做区间切分后再按 offset 重算 gapBefore 链。
 * 只改目标轨（默认 V1 / 目标视频轨）；其它轨道原样保留。
 */
export interface RangeEditResult {
  items: TimelineItem[];
  transitions: EditorTransition[];
  removedDuration: number;
  remainingItems: number;
}

const EPS = 1e-4;

function roundAt(value: number, fps: number): number {
  const rate = Number.isFinite(fps) && fps > 0 ? fps : 30;
  return Math.round(value * rate) / rate;
}

interface LanePiece {
  item: TimelineItem;
  offset: number;
}

export function isRangeUsable(from: number | null, to: number | null, fps: number): boolean {
  if (from === null || to === null) return false;
  const rate = Number.isFinite(fps) && fps > 0 ? fps : 30;
  return to - from > 1 / rate - EPS;
}

export function spliceRange(
  items: TimelineItem[],
  transitions: EditorTransition[],
  tracks: number[],
  from: number,
  to: number,
  fps: number,
  mode: 'lift' | 'extract',
  sourceDurations: Record<string, number> = {},
): RangeEditResult | null {
  const start = Math.max(0, Math.min(from, to));
  const end = Math.max(from, to);
  const removed = end - start;
  if (!(removed > EPS)) return null;
  if (!tracks.length) return null;

  const layout = sequenceLayout(items, transitions, fps, sourceDurations);
  const stamp = Date.now().toString(36);
  const targetSet = new Set(tracks);
  const kept: TimelineItem[] = [];
  const rebuiltByTrack = new Map<number, TimelineItem[]>();

  for (const track of targetSet) {
    const pieces: LanePiece[] = [];
    for (const item of items) {
      if (trackOf(item) !== track) continue;
      const offset = layout.starts.get(item.id) ?? 0;
      const length = itemOutputDuration(item);
      if (length <= EPS) continue;
      const pieceStart = offset;
      const pieceEnd = offset + length;
      if (pieceEnd <= start + EPS) {
        pieces.push({ item, offset });
        continue;
      }
      if (pieceStart >= end - EPS) {
        pieces.push({ item, offset: mode === 'extract' ? Math.max(0, offset - removed) : offset });
        continue;
      }
      const speed = Math.max(0.01, itemSpeed(item));
      const leftLength = Math.max(0, start - pieceStart);
      const rightLength = Math.max(0, pieceEnd - end);
      if (leftLength > EPS) {
        const cutEnd = roundAt(item.start + leftLength * speed, fps);
        pieces.push({
          item: { ...item, id: `${item.id}-cut-left-${stamp}`, end: Math.max(item.start, cutEnd), gapBefore: 0 },
          offset: pieceStart,
        });
      }
      if (rightLength > EPS) {
        const cutStart = roundAt(item.end - rightLength * speed, fps);
        pieces.push({
          item: {
            ...item,
            id: `${item.id}-cut-right-${stamp}`,
            start: Math.min(item.end, Math.max(item.start, cutStart)),
            gapBefore: 0,
          },
          offset: mode === 'extract' ? start : end,
        });
      }
    }
    pieces.sort((a, b) => a.offset - b.offset);
    let cursor = 0;
    const lane: TimelineItem[] = [];
    for (const piece of pieces) {
      const gapBefore = Math.max(0, roundAt(piece.offset - cursor, fps));
      const next = { ...piece.item, gapBefore };
      lane.push(next);
      cursor = piece.offset + itemOutputDuration(next);
    }
    rebuiltByTrack.set(track, lane);
  }

  const survivorIds = new Set<string>();
  for (const track of targetSet) {
    for (const item of rebuiltByTrack.get(track) ?? []) survivorIds.add(item.id);
  }
  for (const item of items) {
    if (!targetSet.has(trackOf(item))) kept.push(item);
  }
  for (const lane of rebuiltByTrack.values()) kept.push(...lane);

  const nextTransitions = transitions.filter((transition) => survivorIds.has(transition.itemId));
  return {
    items: kept,
    transitions: nextTransitions,
    removedDuration: removed,
    remainingItems: kept.length,
  };
}
