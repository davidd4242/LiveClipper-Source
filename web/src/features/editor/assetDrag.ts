/**
 * 拖放载荷：素材库 → 时间线（素材）与效果面板 → 剪点（转场）。
 *
 * HTML5 dataTransfer 在 dragover 阶段读不到 getData（浏览器安全限制），
 * 但预览 ghost 需要提前知道素材时长 / 转场类型，所以额外用模块级变量保存当前拖拽内容。
 * 拖拽在 dragstart 时写入、dragend / drop 时清空，避免残留。
 */

import type { EditorTransition } from '../../api/types';

export const ASSET_DRAG_MIME = 'application/x-liveclipper-asset';
export const TRANSITION_DRAG_MIME = 'application/x-liveclipper-transition';

export interface DraggedAssetPayload {
  clipId: string;
  title: string;
  /** 已知时长（秒）；未知时为 null，落点后由 EditorPage 通过 getEditor 补齐。 */
  duration: number | null;
}

export interface DraggedTransitionPayload {
  kind: EditorTransition['kind'];
  label: string;
}

let currentDraggedAsset: DraggedAssetPayload | null = null;
let currentDraggedTransition: DraggedTransitionPayload | null = null;

export function beginAssetDrag(payload: DraggedAssetPayload): DraggedAssetPayload {
  currentDraggedAsset = payload;
  return payload;
}

export function readDraggedAsset(): DraggedAssetPayload | null {
  return currentDraggedAsset;
}

export function endAssetDrag(): void {
  currentDraggedAsset = null;
}

/** 从 dataTransfer 的字符串里解析载荷；格式非法时返回 null。 */
export function parseDraggedAssetData(raw: string | null): DraggedAssetPayload | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<DraggedAssetPayload> | null;
    if (!parsed || typeof parsed.clipId !== 'string' || !parsed.clipId) return null;
    return {
      clipId: parsed.clipId,
      title: typeof parsed.title === 'string' ? parsed.title : parsed.clipId,
      duration: typeof parsed.duration === 'number' && Number.isFinite(parsed.duration) ? parsed.duration : null,
    };
  } catch {
    return null;
  }
}

export function beginTransitionDrag(payload: DraggedTransitionPayload): DraggedTransitionPayload {
  currentDraggedTransition = payload;
  return payload;
}

export function readDraggedTransition(): DraggedTransitionPayload | null {
  return currentDraggedTransition;
}

export function endTransitionDrag(): void {
  currentDraggedTransition = null;
}

/** 从 dataTransfer 的字符串里解析转场载荷；kind 非法时返回 null。 */
export function parseDraggedTransitionData(raw: string | null): DraggedTransitionPayload | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<DraggedTransitionPayload> | null;
    if (!parsed) return null;
    const kind = parsed.kind;
    if (kind !== 'cross-dissolve' && kind !== 'dip-to-black' && kind !== 'dip-to-white') return null;
    return {
      kind,
      label: typeof parsed.label === 'string' && parsed.label ? parsed.label : kind,
    };
  } catch {
    return null;
  }
}
