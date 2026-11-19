import {
  forwardRef,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
  type CSSProperties,
  type DragEvent as ReactDragEvent,
  type MouseEvent as ReactMouseEvent,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from 'react';
import {
  ArrowLeftRight,
  AudioLines,
  Copy,
  Eye,
  EyeOff,
  Film,
  Flag,
  Link2,
  Lock,
  LockOpen,
  Magnet,
  MousePointer2,
  Redo2,
  Scissors,
  Trash2,
  Undo2,
  ZoomIn,
} from 'lucide-react';
import type { EditorTransition, ParamKeyframeProperty, TimelineItem, VolumeKeyframe } from '../../../api/types';
import { getVideoUrl, getWaveformUrl } from '../../../api/client';
import { TimelineToolbar } from './TimelineToolbar';
import { PerceptionMarkers, PerceptionStatusBar, type TimelinePerception } from './PerceptionMarkers';
import { VideoThumbnail } from '../../../components/VideoThumbnail';
import { maskEditor } from './Monitor';
import {
  cutAvailability,
  dbToGain,
  envelopeDbAt,
  formatClock,
  formatTimecode as formatTimecodeAt,
  findTransition,
  itemOutputDuration,
  itemSequenceStart,
  itemSpeed,
  audioTrackForVideoTrack,
  linkAudioCompanion,
  normalizeVolumeKeyframes,
  PARAM_KEYFRAME_LABELS,
  PARAM_KEYFRAME_PROPS,
  paramKeyframesFor,
  normalizeParamKeyframes,
  paramValueAtItem,
  sequenceLayout,
  trackOf,
  transitionCuts,
  transitionKindLabel,
  transitionMinDuration,
} from '../project';
import type { EditorTool } from './ToolRail';
import {
  ASSET_DRAG_MIME,
  TRANSITION_DRAG_MIME,
  endAssetDrag,
  endTransitionDrag,
  parseDraggedAssetData,
  parseDraggedTransitionData,
  readDraggedAsset,
  readDraggedTransition,
} from '../assetDrag';
import { EditorContextMenu, type ContextMenuItem } from './EditorContextMenu';
import {
  EDIT_POINT_KIND_LABELS,
  nearestEditPoint,
  nextEditPointKind,
  type EditPoint,
} from '../trimOps';

/** 嵌套片段 v1：不能直接裁剪长度（时长由子序列决定），两侧边条只提示原因、不响应拖拽。 */
const NESTED_TRIM_LOCK_HINT = '嵌套片段不可裁剪（v1）：双击进入子序列编辑';

export interface TimelineHandle {
  copy: () => void;
  cut: () => void;
  paste: (mode?: 'insert' | 'overwrite') => void;
  addItem: (item: TimelineItem, mode?: 'insert' | 'overwrite') => boolean;
  /** 素材库拖放：直接落到指定轨道 + 序列时间（overwrite 语义，不推动下游）。 */
  addAssetAt: (item: TimelineItem, track: number, time: number, mode?: 'insert' | 'overwrite', label?: string) => boolean;
  /** 放入视频并自动建匹配音频片段（视频 audioDetached、共享 linkGroup，音频轨显示波形与音量线）。 */
  addAssetWithAudioAt: (
    video: TimelineItem,
    audio: TimelineItem,
    videoTrack: number,
    audioTrack: number,
    time: number,
    mode?: 'insert' | 'overwrite',
    label?: string,
  ) => boolean;
  /** 三点编辑：读当前序列 In/Out（Program 监视器 In 优先于播放头）。 */
  getWorkArea: () => { in: number | null; out: number | null };
  /** 监视器播放控制条：设置/清除序列 In/Out（传 null 清除；FilmCraft 的 I / O）。 */
  setWorkArea: (edge: 'in' | 'out', value: number | null) => void;
  /** 当前目标轨选择（三点编辑落点用）。 */
  getTargetTracks: () => { video: number; audio: number };
  duplicate: () => void;
  deleteSelection: () => void;
  slip: (frames: number) => void;
  roll: (frames: number) => void;
  toggleDisabled: () => void;
  zoomIn: () => void;
  zoomOut: () => void;
  zoomReset: () => void;
}

interface TimelineProps {
  items: TimelineItem[];
  totalDuration: number;
  sourceDuration: number;
  fps: number;
  sequenceTime: number;
  selectedItemId: string | null;
  selectedTransitionId: string | null;
  transitions: EditorTransition[];
  sourceDurations?: Record<string, number>;
  onSelectTransition: (transitionId: string | null) => void;
  onApplyTransition: (itemId: string) => void;
  onRemoveTransition: (transitionId: string) => void;
  onUpdateTransition: (
    transitionId: string,
    patch: Partial<Pick<EditorTransition, 'duration' | 'kind' | 'align'>>,
  ) => void;
  itemLabels: Record<string, string>;
  canUndo: boolean;
  canRedo: boolean;
  undoLabel: string | null;
  redoLabel: string | null;
  onSeek: (time: number) => void;
  onSelectItem: (itemId: string | null) => void;
  onUpdateItems: (items: TimelineItem[], label: string) => void;
  onUndo: () => void;
  onRedo: () => void;
  onOpenTab: (tab: 'transcript' | 'candidates') => void;
  /** 时间线缩放完成时回传百分比，供状态栏显示真实值（FC 用 pps 显示，这里用百分比）。 */
  onZoomChange?: (percent: number) => void;
  markers: number[];
  onAddMarker: (time?: number) => void;
  activeTool: EditorTool;
  onToolChange: (tool: EditorTool) => void;
  snapEnabled: boolean;
  onToggleSnap: () => void;
  perception: TimelinePerception;
  baseAudioMuted?: boolean;
  onToggleBaseAudioMute?: () => void;
  mutedTracks: number[];
  soloedTracks: number[];
  hiddenTracks: number[];
  lockedTracks: number[];
  onToggleTrackMute: (track: number) => void;
  onToggleTrackSolo: (track: number) => void;
  onToggleTrackHidden: (track: number) => void;
  onToggleTrackLocked: (track: number) => void;
  onClearMarkers?: () => void;
  trackOpacities?: number[];
  onSetTrackOpacity?: (trackIndex: number, value: number) => void;
  onOpenTrackProperties?: () => void;
  /** 只读子序列预览：禁止拖动/裁剪/删除/关键帧编辑（v1 嵌套限制）。 */
  readOnly?: boolean;
  /** 嵌套序列 id → 名称（时间线片段角标与提示用）。 */
  nestedNames?: Record<string, string>;
  /** 双击嵌套片段进入只读子序列预览。 */
  onEnterNested?: (itemId: string) => void;
  /** 把片段创建为一级嵌套序列（右键菜单入口）。 */
  onCreateNested?: (itemId: string) => void;
  onNotify: (message: string) => void;
  /** FilmCraft 编辑点（Trim Monitor）：命中修剪手柄时上报，点击片段/空白/Esc 时清空。 */
  editPoint?: EditPoint | null;
  onEditPoint?: (editPoint: EditPoint | null) => void;
  /** 素材库 → 时间线：落在指定轨道与序列时间（EditorPage 负责取时长并建 TimelineItem）。 */
  onDropAsset?: (payload: AssetDropPayload) => void;
  /** 效果面板的转场卡拖到剪点：Timeline 负责找最近的合法剪点。 */
  onDropTransition?: (payload: TransitionDropPayload) => void;
  /** In/Out 工作区变化（监视器播放控制条与提升/提取按钮状态用）。 */
  onWorkAreaChange?: (workArea: { in: number | null; out: number | null }) => void;
  /** 序列标签条（嵌套序列 v2）：渲染在「01 时间线」panel-head 的下一行（28px）。 */
  tabsBar?: ReactNode;
  /** 空时间线占位文案覆盖：子序列引用缺失时显示「子序列内容未加载…」。 */
  emptyHint?: ReactNode;
}

export interface AssetDropPayload {
  clipId: string;
  title: string;
  track: number;
  time: number;
  duration: number | null;
}

export interface TransitionDropPayload {
  kind: EditorTransition['kind'];
  /** 剪点右侧片段 id（applyTransitionAtItem 以它为锚点）。 */
  itemId: string;
  track: number;
  /** 剪点中心（序列秒），只用于提示文案。 */
  time: number;
}

interface Placement {
  item: TimelineItem;
  index: number;
  offset: number;
  gapStart: number;
  gap: number;
  length: number;
  sourceLength: number;
  track: number;
}

type ClipDrag =
  | {
      kind: 'move';
      id: string;
      /** 框选组拖：集合内所有片段一起平移（预览只用 dx，提交时按 lane 首片调整 gapBefore）。 */
      groupIds?: string[];
      startX: number;
      width: number;
      offset: number;
      length: number;
      dx: number;
    }
  | {
      kind: 'trim';
      id: string;
      edge: 'start' | 'end';
      startX: number;
      width: number;
      dx: number;
      /** Alt 拖边缘：只动本片段，不带动链接组（FC Drag::Trim{linked:false}）。 */
      unlink?: boolean;
    }
  | {
      kind: 'slip';
      id: string;
      startX: number;
      width: number;
      length: number;
      dx: number;
    }
  | {
      kind: 'ripple-trim';
      id: string;
      edge: 'start' | 'end';
      startX: number;
      width: number;
      dx: number;
    }
  | {
      kind: 'ripple-move';
      id: string;
      startX: number;
      width: number;
      offset: number;
      length: number;
      dx: number;
    }
  | {
      kind: 'rate';
      id: string;
      edge: 'start' | 'end';
      startX: number;
      width: number;
      dx: number;
    }
  | {
      kind: 'rolling';
      id: string;
      edge: 'start' | 'end';
      startX: number;
      width: number;
      dx: number;
    }
  | {
      kind: 'slide';
      id: string;
      startX: number;
      width: number;
      offset: number;
      length: number;
      dx: number;
    }
  | {
      kind: 'track';
      id: string;
      startX: number;
      width: number;
      offset: number;
      length: number;
      dx: number;
    };

/** 拖拽落点预览：FilmCraft 的 drag preview overlay（提交前不写工程）。 */
type DropPreview = {
  track: number;
  offset: number;
  length: number;
  invalid: boolean;
};

/** FilmCraft SNAP_PX=9：拖拽/裁剪/滑移/框选共用同一个像素阈值。 */
const SNAP_PX = 9;
/** FilmCraft 拖拽边缘自动滚动：20px 判定带、每帧 6px。 */
const AUTO_SCROLL_EDGE = 20;
const AUTO_SCROLL_PX = 6;
/** 片段音量（音量线）范围与显示：与包络 editor 同一 -60..+12 dB 标尺。 */
const GAIN_MIN_DB = -60;
const GAIN_MAX_DB = 12;
const GAIN_SPAN_DB = GAIN_MAX_DB - GAIN_MIN_DB;
const clampGainDb = (db: number) => Math.min(GAIN_MAX_DB, Math.max(GAIN_MIN_DB, db));
const formatGainDb = (db: number) =>
  db <= GAIN_MIN_DB ? '-∞ dB' : `${db >= 0 ? '+' : ''}${db.toFixed(2)} dB`;
/** 音量线在片段内的纵向比例：0 = 顶（+12 dB），1 = 底（-60 dB）。 */
const gainLineRatio = (db: number) => 1 - (clampGainDb(db) - GAIN_MIN_DB) / GAIN_SPAN_DB;

const tracks = [
  { id: 'V3', number: 3, name: '图形与标题', kind: 'video' as const },
  { id: 'V2', number: 2, name: '叠加层', kind: 'video' as const },
  { id: 'V1', number: 1, name: '主画面', kind: 'video' as const },
  { id: 'A1', number: 4, name: '人声', kind: 'audio' as const },
  { id: 'A2', number: 5, name: '音乐', kind: 'audio' as const },
  { id: 'A3', number: 6, name: '音效', kind: 'audio' as const },
];

const TOOL_STATUS_LABELS: Record<EditorTool, string> = {
  select: '选择：拖动排序 / 拖边缘裁剪',
  track: '轨道选择：拖片段整体平移轨道',
  ripple: '波纹：拖边缘裁剪或拖主体，后续片段自动跟随',
  rolling: '滚动：拖边缘移动切点，序列总长不变',
  rate: '比率拉伸：拖边缘改变速率 0.25×–4×',
  razor: '刀片：点击片段在指针处拆分',
  slip: '滑移：拖主体改变素材入出点',
  slide: '幻灯片：拖主体，前后片段自动补偿',
  pen: '钢笔：点击片段在指针处添加关键帧',
  hand: '手形：拖动平移时间线视图',
  zoom: '缩放：点击放大（Alt+点击缩小）',
  marker: '标记：点标尺添加标记',
};

const toolStatusLabel = (tool: EditorTool) => TOOL_STATUS_LABELS[tool];

const clipPalette = ['clip-video-a', 'clip-video-b', 'clip-video-c'];

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

function moveItemToTime(
  items: TimelineItem[],
  itemId: string,
  targetTime: number,
  track: number,
): TimelineItem[] {
  const dragged = items.find((item) => item.id === itemId);
  if (!dragged) return items;
  const moved = trackOf(dragged) === track ? dragged : { ...dragged, track };
  const rest = items.filter((item) => item.id !== itemId);
  let cursor = 0;
  let insertAt = rest.length;
  for (let index = 0; index < rest.length; index += 1) {
    const item = rest[index];
    if (trackOf(item) !== track) continue;
    const gap = Math.max(0, item.gapBefore ?? 0);
    const length = Math.max(0.02, itemOutputDuration(item));
    const start = cursor + gap;
    if (targetTime < start + length / 2) {
      insertAt = index;
      break;
    }
    cursor = start + length;
  }
  return [...rest.slice(0, insertAt), moved, ...rest.slice(insertAt)];
}

function placeItemAtTime(
  items: TimelineItem[],
  itemId: string,
  targetTime: number,
  track: number,
): TimelineItem[] {
  let cursor = 0;
  return items.map((item) => {
    if (trackOf(item) !== track) return item;
    const gap = Math.max(0, item.gapBefore ?? 0);
    const length = Math.max(0.02, itemOutputDuration(item));
    if (item.id === itemId) {
      const nextGap = Math.max(0, Math.round((targetTime - cursor) * 100) / 100);
      cursor += nextGap + length;
      return { ...item, gapBefore: nextGap };
    }
    cursor += gap + length;
    return item;
  });
}

type PositionedItem = { item: TimelineItem; start: number };

/** 用绝对序列位置重建一条轨，gapBefore 只在最后统一计算。 */
function replaceTrackLane(items: TimelineItem[], track: number, positioned: PositionedItem[]): TimelineItem[] {
  const ordered = [...positioned].sort((a, b) => a.start - b.start);
  let cursor = 0;
  const lane = ordered.map(({ item, start }) => {
    const safeStart = Math.max(cursor, start);
    const next = { ...item, track, gapBefore: Math.max(0, safeStart - cursor) };
    cursor = safeStart + itemOutputDuration(next);
    return next;
  });
  const first = items.findIndex((item) => trackOf(item) === track);
  const withoutLane = items.filter((item) => trackOf(item) !== track);
  withoutLane.splice(first < 0 ? withoutLane.length : first, 0, ...lane);
  return withoutLane;
}

function trackLaneAt(items: TimelineItem[], track: number): PositionedItem[] {
  let cursor = 0;
  return items.filter((item) => trackOf(item) === track).map((item) => {
    const start = cursor + Math.max(0, item.gapBefore ?? 0);
    cursor = start + itemOutputDuration(item);
    return { item, start };
  });
}

/** 插入始终向后推开目标轨；播放头落在片段内时先切成左右两段。 */
function insertItemOnTrack(items: TimelineItem[], source: TimelineItem, track: number, time: number): TimelineItem[] {
  const id = `${source.id}-insert-${Date.now().toString(36)}`;
  const inserted = { ...source, id, track, gapBefore: 0 };
  const length = itemOutputDuration(inserted);
  const positioned: PositionedItem[] = [];
  for (const { item, start } of trackLaneAt(items, track)) {
    const end = start + itemOutputDuration(item);
    if (end <= time + 0.0005) positioned.push({ item, start });
    else if (start >= time - 0.0005) positioned.push({ item, start: start + length });
    else {
      const sourceCut = item.start + (time - start) * itemSpeed(item);
      positioned.push({ item: { ...item, end: sourceCut }, start });
      positioned.push({
        item: { ...item, id: `${item.id}-insert-right-${Date.now().toString(36)}`, start: sourceCut, gapBefore: 0 },
        start: time + length,
      });
    }
  }
  positioned.push({ item: inserted, start: Math.max(0, time) });
  return replaceTrackLane(items, track, positioned);
}

/** 覆盖只替换目标轨与新区间相交的部分；其他轨完全不变。 */
function overwriteItemOnTrack(items: TimelineItem[], source: TimelineItem, track: number, time: number): TimelineItem[] {
  const id = `${source.id}-overwrite-${Date.now().toString(36)}`;
  const inserted = { ...source, id, track, gapBefore: 0 };
  const length = itemOutputDuration(inserted);
  const endTime = time + length;
  const positioned: PositionedItem[] = [];
  for (const { item, start } of trackLaneAt(items, track)) {
    const end = start + itemOutputDuration(item);
    if (end <= time + 0.0005 || start >= endTime - 0.0005) {
      positioned.push({ item, start });
      continue;
    }
    if (start < time - 0.0005) {
      positioned.push({ item: { ...item, end: item.start + (time - start) * itemSpeed(item) }, start });
    }
    if (end > endTime + 0.0005) {
      positioned.push({
        item: {
          ...item,
          id: `${item.id}-overwrite-right-${Date.now().toString(36)}`,
          start: item.start + (endTime - start) * itemSpeed(item),
          gapBefore: 0,
        },
        start: endTime,
      });
    }
  }
  positioned.push({ item: inserted, start: Math.max(0, time) });
  return replaceTrackLane(items, track, positioned);
}

function Clip({
  item,
  index,
  left,
  width,
  label,
  selected,
  fps,
  drag,
  audio = false,
  readOnly = false,
  marqueeSelected = false,
  nestedName = null,
  gainDb = 0,
  gainActive = false,
  perception,
  onGainPointerDown,
  onPointerDown,
  onPointerMove,
  onPointerUp,
  onPointerCancel,
  onContextMenu,
  onDoubleClick,
}: {
  item: TimelineItem;
  index: number;
  left: number;
  width: number;
  label: string;
  selected: boolean;
  fps: number;
  drag: ClipDrag | null;
  audio?: boolean;
  readOnly?: boolean;
  marqueeSelected?: boolean;
  nestedName?: string | null;
  /** 音量线显示用的 dB：拖动中为预览值，否则为片段 volumeDb。 */
  gainDb?: number;
  gainActive?: boolean;
  perception: TimelinePerception;
  onGainPointerDown?: (event: ReactPointerEvent<HTMLSpanElement>) => void;
  onPointerDown: (event: ReactPointerEvent<HTMLButtonElement>) => void;
  onPointerMove: (event: ReactPointerEvent<HTMLButtonElement>) => void;
  onPointerUp: (event: ReactPointerEvent<HTMLButtonElement>) => void;
  onPointerCancel: () => void;
  onContextMenu: (event: ReactMouseEvent<HTMLButtonElement>) => void;
  onDoubleClick: (event: ReactMouseEvent<HTMLButtonElement>) => void;
}) {
  const isGroupMember = drag?.kind === 'move' && Boolean(drag.groupIds?.includes(item.id));
  const activeDrag = drag && (drag.id === item.id || isGroupMember) ? drag : null;
  const dragKind = activeDrag?.kind ?? null;
  const dragging =
    dragKind === 'move' ||
    dragKind === 'slip' ||
    dragKind === 'ripple-move' ||
    dragKind === 'slide' ||
    dragKind === 'track';
  const trimming =
    dragKind === 'trim' || dragKind === 'ripple-trim' || dragKind === 'rate' || dragKind === 'rolling';
  const trimEdge = trimming && activeDrag && 'edge' in activeDrag ? activeDrag.edge : null;
  const style: CSSProperties = {
    left: `${left}%`,
    width: `${Math.max(width, 0.6)}%`,
  };

  if (activeDrag && dragging) {
    style.transform = `translateX(${activeDrag.dx}px)`;
    style.zIndex = 5;
  }
  if (activeDrag && trimming && trimEdge === 'start') {
    style.left = `calc(${left}% + ${activeDrag.dx}px)`;
    style.width = `calc(${Math.max(width, 0.6)}% - ${activeDrag.dx}px)`;
  }
  if (activeDrag && trimming && trimEdge === 'end') {
    style.width = `calc(${Math.max(width, 0.6)}% + ${activeDrag.dx}px)`;
  }

  const outputDuration = Math.max(0.001, itemOutputDuration(item));
  const envelopePoints = normalizeVolumeKeyframes(item).map((point) => {
    const x = Math.min(100, Math.max(0, (point.time / outputDuration) * 100));
    const y = 100 - ((Math.min(12, Math.max(-60, point.db)) + 60) / 72) * 100;
    return { x, y };
  });
  const envelopePolyline = envelopePoints.map((point) => `${point.x.toFixed(2)},${point.y.toFixed(2)}`).join(' ');

  return (
    <button
      type="button"
      data-clip-id={item.id}
      className={`timeline-clip ${audio ? 'clip-audio' : clipPalette[index % clipPalette.length]}${selected ? ' is-selected' : ''}${marqueeSelected ? ' is-marquee-selected' : ''}${activeDrag && dragging ? ' is-dragging' : ''}${activeDrag && dragKind === 'slip' ? ' is-slipping' : ''}${item.disabled ? ' is-disabled' : ''}${item.linkGroup ? ' is-linked' : ''}${nestedName ? ' is-nested' : ''}${readOnly ? ' is-readonly' : ''}`}
      style={style}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerCancel}
      onContextMenu={onContextMenu}
      onDoubleClick={onDoubleClick}
      aria-pressed={selected}
      aria-label={
        nestedName
          ? `${label}，嵌套序列 ${nestedName}，不可裁剪，双击进入子序列编辑`
          : `${label}${formatTimecodeAt(item.end - item.start, fps)}，时长 ${formatTimecodeAt(item.end - item.start, fps)}，${formatClock(item.start)} 到 ${formatClock(item.end)}，可拖动排序或裁剪`
      }
      title={
        nestedName
          ? `${label} · 嵌套序列 ${nestedName} · 双击进入子序列编辑`
          : `${label} · 源 ${formatTimecodeAt(item.start, fps)} → ${formatTimecodeAt(item.end, fps)} · 拖动排序，左右边缘裁剪，双击添加音量关键帧`
      }
    >
      {readOnly ? null : nestedName ? (
        // 嵌套片段 v1：不能直接裁剪长度（时长由子序列决定），保留一条不可见边条提示原因。
        <span className="trim-lock trim-start" data-trim-lock="start" title={NESTED_TRIM_LOCK_HINT} aria-hidden="true" />
      ) : (
        <span
          className={`trim-handle trim-start${trimEdge === 'start' ? ' is-active' : ''}`}
          data-trim="start"
          aria-hidden="true"
        />
      )}
      {audio ? (
        <span
          className="clip-waveform"
          style={{
            backgroundImage: `url("${getWaveformUrl(item.clipId)}")`,
            // FilmCraft 波形按片段音量缩放：0 dB 时保持基线 3.2×，音量越小波形越扁。
            transform: `scaleY(${(3.2 * Math.min(1.5, Math.max(0.05, dbToGain(gainDb)))).toFixed(3)})`,
          }}
          aria-hidden="true"
        />
      ) : (
        <VideoThumbnail src={getVideoUrl(item.clipId)} alt="" className="clip-thumb" seekRatio={0.18} />
      )}
      {perception.enabled && perception.status === 'ready' && perception.clipId === item.clipId && !item.nestedSequenceId ? (
        <PerceptionMarkers sourceStart={item.start} sourceEnd={item.end} shots={perception.shots} loudness={perception.loudness} />
      ) : null}
      {audio && !readOnly ? (
        <>
          <span
            className={`clip-gain-line${gainActive ? ' is-active' : ''}`}
            style={{ top: `calc(19px + (100% - 22px) * ${gainLineRatio(gainDb).toFixed(4)})` }}
            aria-hidden="true"
          />
          <span
            className={`clip-gain-hit${gainActive ? ' is-active' : ''}`}
            data-testid="clip-gain-line"
            data-clip-id={item.id}
            style={{ top: `calc(19px + (100% - 22px) * ${gainLineRatio(gainDb).toFixed(4)})` }}
            onPointerDown={onGainPointerDown}
            title={`拖动调整片段音量（当前 ${formatGainDb(gainDb)}）`}
          />
        </>
      ) : null}
      {envelopePoints.length > 0 ? (
        <svg
          className="clip-envelope"
          viewBox="0 0 100 100"
          preserveAspectRatio="none"
          aria-hidden="true"
        >
          <polyline points={envelopePolyline} vectorEffect="non-scaling-stroke" />
          {envelopePoints.map((point, pointIndex) => (
            <circle key={`${pointIndex}-${point.x.toFixed(1)}`} cx={point.x} cy={point.y} r={1.4} />
          ))}
        </svg>
      ) : null}
      {nestedName ? (
        <span className="clip-nested-badge" title={`嵌套序列 ${nestedName}（双击进入子序列编辑）`}>
          序列
        </span>
      ) : null}
      <span className="clip-label">{label}</span>
      {item.effects?.length ? (
        <span className="clip-fx-badge" title={`${item.effects.length} 个音频效果`} aria-hidden="true">
          fx
        </span>
      ) : null}
      {item.masks?.length ? (
        <span className="clip-fx-badge is-intrinsic" title={`${item.masks.length} 个蒙版`} aria-hidden="true">
          fx
        </span>
      ) : null}
      <span className="clip-timecode" aria-hidden="true">
        {formatTimecodeAt(item.end - item.start, fps)}
      </span>
      {item.linkGroup ? (
        <span className="clip-link" title="已链接同源画面/音频" aria-label="已链接">
          <Link2 size={10} />
        </span>
      ) : null}
      {readOnly ? null : nestedName ? (
        <span className="trim-lock trim-end" data-trim-lock="end" title={NESTED_TRIM_LOCK_HINT} aria-hidden="true" />
      ) : (
        <span
          className={`trim-handle trim-end${trimEdge === 'end' ? ' is-active' : ''}`}
          data-trim="end"
          aria-hidden="true"
        />
      )}
    </button>
  );
}

export const Timeline = forwardRef<TimelineHandle, TimelineProps>(function Timeline({
  items,
  totalDuration,
  sourceDuration,
  fps,
  sequenceTime,
  selectedItemId,
  selectedTransitionId,
  transitions,
  sourceDurations,
  onSelectTransition,
  onApplyTransition,
  onRemoveTransition,
  onUpdateTransition,
  itemLabels,
  canUndo,
  canRedo,
  undoLabel,
  redoLabel,
  onSeek,
  onSelectItem,
  onUpdateItems,
  onUndo,
  onRedo,
  onOpenTab,
  onZoomChange,
  markers,
  onAddMarker,
  activeTool,
  onToolChange,
  snapEnabled,
  onToggleSnap,
  perception,
  baseAudioMuted = false,
  onToggleBaseAudioMute,
  mutedTracks,
  soloedTracks,
  hiddenTracks,
  lockedTracks,
  onToggleTrackMute,
  onToggleTrackSolo,
  onToggleTrackHidden,
  onToggleTrackLocked,
  onClearMarkers,
  trackOpacities,
  onSetTrackOpacity,
  onOpenTrackProperties,
  readOnly = false,
  nestedNames,
  onEnterNested,
  onCreateNested,
  onNotify,
  editPoint = null,
  onEditPoint,
  onDropAsset,
  onDropTransition,
  onWorkAreaChange,
  tabsBar,
  emptyHint,
}, ref) {
  const tracksRef = useRef<HTMLDivElement>(null);
  const rulerRef = useRef<HTMLDivElement>(null);
  const rulerViewportRef = useRef<HTMLDivElement>(null);
  const tracksViewportRef = useRef<HTMLDivElement>(null);
  const hScrollRef = useRef<HTMLDivElement>(null);
  const hScrollTrackRef = useRef<HTMLDivElement>(null);
  const hScrollThumbRef = useRef<HTMLDivElement>(null);
  const hScrollDragRef = useRef<{ pointerId: number; startX: number; startLeft: number } | null>(null);
  // 横向内容未超出视口时隐藏底部滚动条，避免留一条空轨道占高度。
  const [hScrollable, setHScrollable] = useState(false);
  // FilmCraft：点击时间码直接输入跳转（HH:MM:SS:FF / MM:SS:FF / 秒都接受）。
  const [editingTimecode, setEditingTimecode] = useState(false);
  const [timecodeDraft, setTimecodeDraft] = useState('');
  // FilmCraft：In/Out 工作区（I/O 标记，Shift+I/O 清除）。v1 只存会话态（新增工程字段会被后端 DTO 丢弃）。
  const [inOut, setInOut] = useState<{ in: number | null; out: number | null }>({ in: null, out: null });
  const inOutRef = useRef(inOut);
  inOutRef.current = inOut;
  // FilmCraft：In/Out 工作区由监视器播放控制条与本组件共同维护，变化统一走这里回传。
  const applyWorkArea = (next: { in: number | null; out: number | null }) => {
    inOutRef.current = next;
    setInOut(next);
    onWorkAreaChange?.(next);
  };

  // 底部横向滚动条自绘滑块：原生滚动条在 headless/部分平台会隐藏，自绘可稳定可见。
  const syncHScrollThumb = () => {
    const bar = hScrollRef.current;
    const thumb = hScrollThumbRef.current;
    if (!bar || !thumb) return;
    const trackWidth = bar.clientWidth;
    const max = bar.scrollWidth - trackWidth;
    if (max <= 1) {
      thumb.style.width = '100%';
      thumb.style.transform = 'translateX(0px)';
      return;
    }
    const thumbWidth = Math.max(28, trackWidth * (trackWidth / bar.scrollWidth));
    const left = (bar.scrollLeft / max) * Math.max(0, trackWidth - thumbWidth);
    thumb.style.width = `${thumbWidth}px`;
    thumb.style.transform = `translateX(${left}px)`;
  };
  const [marquee, setMarquee] = useState<{
    x0: number;
    y0: number;
    x1: number;
    y1: number;
    left: number;
    top: number;
    base: string[];
    moved: boolean;
  } | null>(null);
  const [marqueeIds, setMarqueeIds] = useState<string[]>([]);
  const clipboardRef = useRef<TimelineItem | null>(null);
  const [drag, setDrag] = useState<ClipDrag | null>(null);
  // FilmCraft：pps 缩放用指数缓动 k = 1 - exp(-dt*20)，指针为锚点（panels/timeline.rs:425）。
  const [zoom, setZoom] = useState(1);
  const [zoomRender, setZoomRender] = useState(1);
  const zoomRenderRef = useRef(1);
  zoomRenderRef.current = zoomRender;
  const zoomAnchorRef = useRef<{ time: number; offsetPx: number } | null>(null);
  // FilmCraft 拖拽边缘自动滚动：距视口边缘 20px 内每帧滚 6px（panels/timeline.rs 同常数）。
  const autoScrollRef = useRef<{ dir: number; raf: number } | null>(null);
  const dragScrollRef = useRef(0);
  const dragPointerRef = useRef<{ clientX: number; clientY: number; pointerId: number } | null>(null);
  const dragSourceElRef = useRef<HTMLElement | null>(null);
  const [snapGuide, setSnapGuide] = useState<number | null>(null);
  // FilmCraft 式 drag preview：拖动中只画落点幽灵框，松手才 execute 一条命令。
  const [dropPreview, setDropPreview] = useState<DropPreview | null>(null);
  // 素材库 → 时间线拖拽：hover 幽灵框（时长未知时用固定宽度提示块）。
  const [assetPreview, setAssetPreview] = useState<{
    track: number;
    offset: number;
    length: number;
    title: string;
    invalid: boolean;
  } | null>(null);
  // 刀片工具跟随指针的白色切割预览线。
  const [razorGuide, setRazorGuide] = useState<number | null>(null);
  // FilmCraft 式「音频片段音量线」拖拽：在片段上直接上下拖音量线改片段音量，拖动中显示 dB 提示。
  const [gainDrag, setGainDrag] = useState<{
    itemId: string;
    baseDb: number;
    startY: number;
    bandHeight: number;
    db: number;
    clientX: number;
    clientY: number;
    keys: boolean;
  } | null>(null);
  const gainDragRef = useRef<typeof gainDrag>(null);
  gainDragRef.current = gainDrag;
  const gainCleanupRef = useRef<(() => void) | null>(null);
  // 转场卡拖到剪点时的落点预览（剪点中心，序列秒）。
  const [transitionPreview, setTransitionPreview] = useState<{
    existing?: boolean;
    track: number;
    time: number;
    label: string;
    invalid: boolean;
  } | null>(null);
  // 点空白/点空隙后的「空隙选择」：Delete 直接闭合（FilmCraft 式 gap 操作）。
  const [selectedGap, setSelectedGap] = useState<{ track: number; itemId: string; gap: number } | null>(null);
  const [trackMenu, setTrackMenu] = useState<{ x: number; y: number; track: number } | null>(null);
  const [gapMenu, setGapMenu] = useState<{ x: number; y: number; track: number; itemId: string; gap: number } | null>(null);
  const [rulerMenu, setRulerMenu] = useState<{ x: number; y: number; time: number } | null>(null);
  const [contextMenu, setContextMenu] = useState<{
    x: number;
    y: number;
    itemId: string;
    sourceTime: number;
  } | null>(null);
  // task-144：右键菜单关闭后把焦点还给触发元素，并支持 Esc/点外部关闭。
  const contextMenuTriggerRef = useRef<HTMLButtonElement | null>(null);
  const clipMenuRef = useRef<HTMLDivElement | null>(null);
  const closeClipMenu = () => {
    setContextMenu(null);
    const trigger = contextMenuTriggerRef.current;
    window.requestAnimationFrame(() => {
      if (trigger && trigger.isConnected) trigger.focus();
    });
  };
  const [transitionMenu, setTransitionMenu] = useState<{
    x: number;
    y: number;
    transitionId: string;
  } | null>(null);
  const [transitionResize, setTransitionResize] = useState<{
    transitionId: string;
    edge: 'start' | 'end';
    startX: number;
    baseDuration: number;
    pixelsPerSecond: number;
  } | null>(null);
  const [transitionResizePreview, setTransitionResizePreview] = useState<number | null>(null);
  const transitionResizeCleanupRef = useRef<(() => void) | null>(null);
  const [transitionDialog, setTransitionDialog] = useState<{ transitionId: string; value: string } | null>(
    null,
  );
  const [envelopeDrag, setEnvelopeDrag] = useState<{
    itemId: string;
    index: number;
    duration: number;
    offset: number;
    points: VolumeKeyframe[];
    rect: { left: number; top: number; width: number; height: number };
  } | null>(null);
  const [envelopePreview, setEnvelopePreview] = useState<{ itemId: string; points: VolumeKeyframe[] } | null>(
    null,
  );
  const [envelopeLineDrag, setEnvelopeLineDrag] = useState<{
    itemId: string;
    startY: number;
    basePoints: VolumeKeyframe[];
    rect: { top: number; height: number };
  } | null>(null);
  const [selectedEnvelope, setSelectedEnvelope] = useState<{ itemId: string; index: number } | null>(null);
  const [keyframeMarkDrag, setKeyframeMarkDrag] = useState<{
    itemId: string;
    startTime: number;
    time: number;
    duration: number;
    offset: number;
    rectLeft: number;
    rectWidth: number;
    moved: boolean;
  } | null>(null);
  // T9：拖动改为 window 级 pointermove/pointerup 监听（pointer capture 在某些宿主/合成事件下
  // 不会把 move 送到小圆点，导致拖关键帧“看起来按下了但不动”）。ref 镜像最新状态供监听器读取。
  const envelopeDragRef = useRef(envelopeDrag);
  envelopeDragRef.current = envelopeDrag;
  const envelopeLineDragRef = useRef(envelopeLineDrag);
  envelopeLineDragRef.current = envelopeLineDrag;
  const keyframeMarkDragRef = useRef(keyframeMarkDrag);
  keyframeMarkDragRef.current = keyframeMarkDrag;
  const envelopeCleanupRef = useRef<(() => void) | null>(null);
  const [envelopeMenu, setEnvelopeMenu] = useState<{
    x: number;
    y: number;
    itemId: string;
    index: number;
  } | null>(null);
  const total = Math.max(totalDuration, 0.01);
  const formatTimecode = (value: number) => formatTimecodeAt(value, fps);
  /** 解析时间码输入，与时间码显示口径一致：HH:MM:SS:FF（≥1h）/ MM:SS:FF / MM:SS / 秒。 */
  const parseTimecodeInput = (raw: string): number | null => {
    const text = raw.trim().replace(/[,\uFF0C]/g, '');
    if (!text) return null;
    const parts = text.split(/[:;\uFF1A]/).map((part) => part.trim());
    if (parts.some((part) => part === '' || !/^\d+(\.\d+)?$/.test(part))) return null;
    const nums = parts.map((part) => Number(part));
    if (nums.length === 4) return nums[0]! * 3600 + nums[1]! * 60 + nums[2]! + nums[3]! / fps;
    if (nums.length === 3) return nums[0]! * 60 + nums[1]! + nums[2]! / fps;
    if (nums.length === 2) return nums[0]! * 60 + nums[1]!;
    if (nums.length === 1) return nums[0]!;
    return null;
  };
  const commitTimecodeInput = (raw: string) => {
    const parsed = parseTimecodeInput(raw);
    setEditingTimecode(false);
    if (parsed === null) {
      onNotify('时间码无法识别：请用 HH:MM:SS:FF 或 MM:SS:FF');
      return;
    }
    const target = clamp(parsed, 0, Math.max(0, totalDuration));
    onSeek(target);
    onNotify(`播放头已跳到 ${formatTimecode(target)}`);
  };
  const layout = sequenceLayout(items, transitions, fps, sourceDurations);
  const placements = items.map((item, index) => {
    const track = trackOf(item);
    const gap = Math.max(0, item.gapBefore ?? 0);
    const offset = layout.starts.get(item.id) ?? 0;
    const gapStart = Math.max(0, offset - gap);
    const sourceLength = Math.max(0, item.end - item.start);
    const length = Math.max(0.02, itemOutputDuration(item));
    return { item, index, offset, gapStart, gap, length, sourceLength, track };
  });
  const cutPlacements = transitionCuts(items, transitions, fps, sourceDurations);
  // 目标轨决定粘贴、素材插入/覆盖的落点；不再只是选中轨的视觉高亮。
  const [targetTracks, setTargetTracks] = useState({ video: 1, audio: 4 });
  const targetTracksRef = useRef(targetTracks);
  targetTracksRef.current = targetTracks;
  const targetTrackFor = (item: TimelineItem) => (trackOf(item) >= 4 ? targetTracks.audio : targetTracks.video);
  const chooseTargetTrack = (track: number) => {
    if (lockedTracks.includes(track)) {
      onNotify(`${trackName(track)}已锁定，不能设为目标轨`);
      return;
    }
    setTargetTracks((current) => ({ ...current, [track >= 4 ? 'audio' : 'video']: track }));
    onNotify(`目标轨：${trackName(track)}`);
  };
  const menuClip = contextMenu
    ? items.find((entry) => entry.id === contextMenu.itemId) ?? null
    : null;
  // 嵌套片段 v1：涉及嵌套片段的剪点不能添加转场（后端同样拒绝跨嵌套转场）。
  const menuCutAvailability = menuClip
    ? cutAvailability(items, menuClip.id, fps, undefined, sourceDurations)
    : null;
  const menuCutNested = Boolean(
    menuClip?.nestedSequenceId ||
      menuCutAvailability?.previous?.nestedSequenceId ||
      menuCutAvailability?.next?.nestedSequenceId,
  );
  const menuTransition = transitionMenu
    ? transitions.find((entry) => entry.id === transitionMenu.transitionId) ?? null
    : null;
  const dialogCut = transitionDialog
    ? cutPlacements.find((cut) => cut.transition.id === transitionDialog.transitionId) ?? null
    : null;
  const dialogTransition = transitionDialog
    ? transitions.find((entry) => entry.id === transitionDialog.transitionId) ?? null
    : null;
  const transitionFloor = transitionMinDuration(fps);
  const dialogCeiling = dialogCut
    ? Math.min(itemOutputDuration(dialogCut.previous), itemOutputDuration(dialogCut.next))
    : 2;
  const transitionMax = (cut: (typeof cutPlacements)[number]) =>
    Math.max(transitionFloor, Math.min(itemOutputDuration(cut.previous), itemOutputDuration(cut.next)));

  const beginTransitionResize = (
    event: ReactPointerEvent<HTMLButtonElement>,
    cut: (typeof cutPlacements)[number],
  ) => {
    event.stopPropagation();
    const rect = event.currentTarget.getBoundingClientRect();
    const offsetX = event.clientX - rect.left;
    const edge: 'start' | 'end' | null =
      offsetX <= 8 ? 'start' : offsetX >= rect.width - 8 ? 'end' : null;
    if (!edge) {
      onSelectTransition(cut.transition.id);
      return;
    }
    event.preventDefault();
    const startClientX = event.clientX;
    const startDuration = cut.effective;
    const pixelsPerSecond = Math.max(1, rect.width) / Math.max(0.01, startDuration);
    const clampDuration = (value: number) =>
      Math.min(transitionMax(cut), Math.max(transitionFloor, Math.round(value * 100) / 100));
    let latest = startDuration;
    setTransitionResize({
      transitionId: cut.transition.id,
      edge,
      startX: startClientX,
      baseDuration: startDuration,
      pixelsPerSecond,
    });
    setTransitionResizePreview(startDuration);
    // 用 window 监听代替元素上的 pointermove/up：转场被选中会触发父级重渲染，
    // 元素级 capture 在重渲染后可能收不到 move（彩排 #11 的根因），window 监听最稳。
    const onMove = (moveEvent: PointerEvent) => {
      if (!moveEvent.buttons) return;
      const delta = (moveEvent.clientX - startClientX) / pixelsPerSecond;
      const raw = edge === 'end' ? startDuration + delta : startDuration - delta;
      latest = clampDuration(raw);
      setTransitionResizePreview(latest);
    };
    const finish = (commit: boolean) => {
      transitionResizeCleanupRef.current?.();
      transitionResizeCleanupRef.current = null;
      setTransitionResize(null);
      setTransitionResizePreview(null);
      if (commit && Math.abs(latest - startDuration) > 0.001) {
        onUpdateTransition(cut.transition.id, { duration: latest });
        onNotify(`转场时长 ${latest.toFixed(2)} 秒`);
      }
    };
    const onUp = () => finish(true);
    const onCancel = () => finish(false);
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onCancel);
    transitionResizeCleanupRef.current = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onCancel);
    };
    // 选中会触发父级重渲染：放在监听器挂载之后，且不使用元素级 pointer capture，
    // 避免重渲染导致元素失联/捕获丢失（彩排 #11 根因）。
    onSelectTransition(cut.transition.id);
  };

  useEffect(() => () => transitionResizeCleanupRef.current?.(), []);

  // task-144：片段右键菜单 Esc 关闭（捕获阶段，避免被编辑器其它 Esc 处理抢先）并聚焦首个菜单项。
  useEffect(() => {
    if (!contextMenu) return undefined;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.isComposing || event.keyCode === 229) return;
      if (event.target instanceof Element && event.target.closest('#editor-refinement, .refinement-panel')) return;
      if (event.target instanceof HTMLElement && event.target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])')) return;
      if (event.key !== 'Escape') return;
      event.preventDefault();
      event.stopPropagation();
      closeClipMenu();
    };
    window.addEventListener('keydown', onKeyDown, true);
    const frame = window.requestAnimationFrame(() => {
      clipMenuRef.current?.querySelector<HTMLButtonElement>('button')?.focus();
    });
    return () => {
      window.removeEventListener('keydown', onKeyDown, true);
      window.cancelAnimationFrame(frame);
    };
  }, [contextMenu]);

  const submitTransitionDialog = () => {
    if (!transitionDialog || !dialogCut) {
      setTransitionDialog(null);
      return;
    }
    const parsed = Number.parseFloat(transitionDialog.value);
    if (!Number.isFinite(parsed) || parsed < transitionFloor) {
      onNotify(`转场时长至少 ${transitionFloor.toFixed(2)} 秒`);
      return;
    }
    const next = Math.min(dialogCeiling, Math.max(transitionFloor, parsed));
    onUpdateTransition(transitionDialog.transitionId, { duration: next });
    setTransitionDialog(null);
    onNotify(`转场时长已设为 ${next.toFixed(2)} 秒`);
  };
  // P1a 视觉换肤：标尺密度随缩放与序列时长自适应（P1a 规格：默认 10s，放大 5s/2s）。
  const rulerStepCandidates = [1, 2, 5, 10, 15, 30, 60];
  const rulerTargetTicks = zoom < 1.6 ? 7 : zoom < 2.8 ? 10 : 14;
  const rulerStep = rulerStepCandidates.reduce(
    (best, candidate) =>
      Math.abs(total / candidate - rulerTargetTicks) < Math.abs(total / best - rulerTargetTicks)
        ? candidate
        : best,
    rulerStepCandidates[0],
  );
  const ruler = Array.from(
    { length: Math.max(2, Math.round(total / rulerStep) + 1) },
    (_, index) => index * rulerStep,
  );
  const rulerLabelEvery = ruler.length <= 8 ? 1 : 2;
  const playhead = Math.min(100, Math.max(0, (sequenceTime / total) * 100));

  type SnapResult = { value: number; hit: number | null };

  const collectSnapTargets = (excludeIds: string[], includePlayhead = true): number[] => {
    const targets = new Set<number>([0, total, ...markers]);
    if (includePlayhead) targets.add(sequenceTime);
    // FilmCraft：In/Out 也是吸附候选。
    if (inOut.in !== null) targets.add(inOut.in);
    if (inOut.out !== null) targets.add(inOut.out);
    for (const candidate of placements) {
      if (excludeIds.includes(candidate.item.id)) continue;
      targets.add(candidate.offset);
      targets.add(candidate.offset + candidate.length);
    }
    return [...targets];
  };

  const snapEdge = (
    value: number,
    excludeIds: string[],
    threshold: number,
    includePlayhead = false,
  ): SnapResult => {
    if (!snapEnabled) return { value, hit: null };
    let best: number | null = null;
    let bestDistance = threshold;
    for (const candidate of collectSnapTargets(excludeIds, includePlayhead)) {
      const distance = Math.abs(value - candidate);
      if (distance < bestDistance) {
        best = candidate;
        bestDistance = distance;
      }
    }
    return best === null ? { value, hit: null } : { value: Math.max(0, best), hit: best };
  };

  const snapMoveEdge = (
    start: number,
    length: number,
    excludeIds: string[],
    threshold: number,
  ): SnapResult => {
    if (!snapEnabled) return { value: start, hit: null };
    let best = start;
    let hit: number | null = null;
    let bestDistance = threshold;
    for (const candidate of collectSnapTargets(excludeIds, true)) {
      const startDistance = Math.abs(start - candidate);
      if (startDistance < bestDistance) {
        best = candidate;
        bestDistance = startDistance;
        hit = candidate;
      }
      const endDistance = Math.abs(start + length - candidate);
      if (endDistance < bestDistance) {
        best = candidate - length;
        bestDistance = endDistance;
        hit = candidate;
      }
    }
    return { value: Math.max(0, best), hit };
  };

  // T9：关键帧/包络点拖拽的时间吸附。序列时间候选 = 播放头 + 标记 + In/Out + 片段首尾；
  // 阈值按「片段内像素尺度」换算，保证 9px 手感与片段拖拽一致。
  const snapKeyframeSequence = (
    sequence: number,
    pixelWidth: number,
    pixelDuration: number,
  ): SnapResult => {
    if (!snapEnabled) return { value: sequence, hit: null };
    const threshold = (SNAP_PX / Math.max(1, pixelWidth)) * Math.max(0.001, pixelDuration);
    return snapEdge(sequence, [], threshold, true);
  };

  // FilmCraft 的自动化关键帧支持「吸附到 0 dB 中线」：像素近中线时归零，抖动不会把 0 dB 改掉。
  const snapEnvelopeDb = (db: number, heightPx: number): number => {
    const zeroDbThisPixel = 72 / Math.max(1, heightPx);
    // 0 dB 中线吸附窗口按像素算（4px），避免包络高度变化时吸附半径变味。
    if (Math.abs(db) <= zeroDbThisPixel * 4) return 0;
    return Math.round(db * 10) / 10;
  };

  // P2（task-128）：组拖的刚体约束与整组吸附。
  const groupMembersOf = (ids: string[]) => placements.filter((candidate) => ids.includes(candidate.item.id));

  const groupDeltaBounds = (members: Placement[], ids: Set<string>) => {
    let min = Number.NEGATIVE_INFINITY;
    let max = Number.POSITIVE_INFINITY;
    for (const member of members) {
      // 左侧不得越过 0（右侧允许扩展序列总长，与单片段拖动一致）。
      min = Math.max(min, -member.offset);
      for (const other of placements) {
        if (other.track !== member.track || ids.has(other.item.id)) continue;
        const otherEnd = other.offset + other.length;
        if (otherEnd <= member.offset + 0.0001) min = Math.max(min, otherEnd - member.offset);
        if (other.offset >= member.offset + member.length - 0.0001) {
          max = Math.min(max, other.offset - (member.offset + member.length));
        }
      }
    }
    return { min, max };
  };

  const clampGroupDelta = (members: Placement[], ids: Set<string>, delta: number) => {
    const bounds = groupDeltaBounds(members, ids);
    if (bounds.min > bounds.max) return { delta: 0, bounds };
    return { delta: clamp(delta, bounds.min, bounds.max), bounds };
  };

  const snapGroupDelta = (members: Placement[], ids: Set<string>, delta: number, threshold: number) => {
    if (!snapEnabled) return { delta, hit: null as number | null };
    let best = delta;
    let hit: number | null = null;
    let bestDistance = threshold;
    const targets = collectSnapTargets([...ids], true);
    for (const member of members) {
      for (const target of targets) {
        for (const anchorTime of [member.offset, member.offset + member.length]) {
          const candidateDelta = target - anchorTime;
          const distance = Math.abs(candidateDelta - delta);
          if (distance < bestDistance) {
            bestDistance = distance;
            best = candidateDelta;
            hit = target;
          }
        }
      }
    }
    return { delta: best, hit };
  };

  const seekAt = (clientX: number, container: HTMLElement | null) => {
    if (!container) return;
    const rect = container.getBoundingClientRect();
    if (rect.width <= 0) return;
    onSeek(snapTime(clamp((clientX - rect.left) / rect.width, 0, 1) * total, rect.width));
  };

  const timeAt = (clientX: number, container: HTMLElement | null) => {
    if (!container) return 0;
    const rect = container.getBoundingClientRect();
    if (rect.width <= 0) return 0;
    return snapTime(clamp((clientX - rect.left) / rect.width, 0, 1) * total, rect.width);
  };

  // 手形工具 / 缩放工具（FilmCraft H / Z）：H 拖动平移视图，Z 点击缩放并以指针为锚点。
  const panRef = useRef<{ clientX: number; scrollLeft: number; moved: boolean } | null>(null);

  const zoomAbout = (clientX: number, factor: number, container: HTMLElement | null) => {
    const viewport = tracksViewportRef.current;
    const element = container ?? tracksRef.current;
    if (!viewport || !element) return;
    const rect = element.getBoundingClientRect();
    if (rect.width <= 0) return;
    const offsetPx = clamp(clientX - rect.left, 0, rect.width);
    const contentWidth = viewport.clientWidth * zoomRenderRef.current;
    const ratio = contentWidth > 0 ? clamp((viewport.scrollLeft + offsetPx) / contentWidth, 0, 1) : 0;
    zoomAnchorRef.current = { time: ratio * total, offsetPx };
    setZoom((current) => clamp(current * factor, 1, 4));
  };

  const startPan = (event: ReactPointerEvent<HTMLElement>) => {
    event.preventDefault();
    const viewport = tracksViewportRef.current;
    if (!viewport) return;
    (tracksRef.current ?? event.currentTarget).setPointerCapture(event.pointerId);
    panRef.current = { clientX: event.clientX, scrollLeft: viewport.scrollLeft, moved: false };
  };

  const updatePan = (event: ReactPointerEvent<HTMLElement>): boolean => {
    const pan = panRef.current;
    const viewport = tracksViewportRef.current;
    if (!pan || !viewport) return false;
    const dx = event.clientX - pan.clientX;
    if (Math.abs(dx) > 2) pan.moved = true;
    const next = Math.max(0, pan.scrollLeft - dx);
    viewport.scrollLeft = next;
    if (rulerViewportRef.current) rulerViewportRef.current.scrollLeft = next;
    window.requestAnimationFrame(syncHScrollThumb);
    return true;
  };

  const endPan = (event: ReactPointerEvent<HTMLElement>) => {
    if (!panRef.current) return false;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    panRef.current = null;
    return true;
  };

  const beginScrub = (event: ReactPointerEvent<HTMLElement>) => {
    if (event.button !== 0) return;
    event.preventDefault();
    if (activeTool === 'zoom') {
      // FilmCraft Zoom 工具：点击放大，Alt+点击缩小，锚点为指针位置。
      zoomAbout(event.clientX, event.altKey ? 1 / 1.5 : 1.5, event.currentTarget);
      return;
    }
    if (activeTool === 'hand') {
      startPan(event);
      return;
    }
    if (activeTool === 'marker') {
      const time = timeAt(event.clientX, tracksRef.current);
      onSeek(time);
      onAddMarker(time);
      onNotify(`已在 ${formatClock(time)} 添加标记`);
      return;
    }
    event.currentTarget.setPointerCapture(event.pointerId);
    seekAt(event.clientX, event.currentTarget);
  };

  const moveScrub = (event: ReactPointerEvent<HTMLElement>) => {
    if (!event.currentTarget.hasPointerCapture(event.pointerId)) return;
    seekAt(event.clientX, event.currentTarget);
  };

  const endScrub = (event: ReactPointerEvent<HTMLElement>) => {
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
  };

  // P1c：空白处拖拽框选（选择工具下）。点按空白仍保留定位播放头 + 清空选择。
  const collectMarqueeHits = (rect: { left: number; top: number; right: number; bottom: number }) => {
    const tracks = tracksRef.current;
    if (!tracks) return [] as string[];
    const hits: string[] = [];
    tracks.querySelectorAll<HTMLElement>('.timeline-clip[data-clip-id]').forEach((el) => {
      const r = el.getBoundingClientRect();
      if (r.right >= rect.left && r.left <= rect.right && r.bottom >= rect.top && r.top <= rect.bottom) {
        const id = el.dataset.clipId;
        if (id) hits.push(id);
      }
    });
    return hits;
  };

  const beginMarquee = (event: ReactPointerEvent<HTMLElement>) => {
    if (event.button !== 0) return;
    // 手形/缩放工具：整片区域都可平移或缩放，不进入框选。
    if (activeTool === 'hand') {
      startPan(event);
      return;
    }
    if (activeTool === 'zoom') {
      event.preventDefault();
      zoomAbout(event.clientX, event.altKey ? 1 / 1.5 : 1.5, tracksRef.current);
      return;
    }
    // 标记工具与只读/其他工具保持原有行为（标尺加点、定位播放头）。
    if (activeTool !== 'select' || readOnly) {
      beginScrub(event);
      return;
    }
    event.preventDefault();
    const tracks = tracksRef.current;
    if (!tracks) return;
    tracks.setPointerCapture(event.pointerId);
    const rect = tracks.getBoundingClientRect();
    const base = event.shiftKey ? (marqueeIds.length ? marqueeIds : selectedItemId ? [selectedItemId] : []) : [];
    setMarquee({
      x0: event.clientX,
      y0: event.clientY,
      x1: event.clientX,
      y1: event.clientY,
      left: rect.left,
      top: rect.top,
      base,
      moved: false,
    });
    setMarqueeIds(base);
  };

  const moveMarquee = (event: ReactPointerEvent<HTMLElement>) => {
    if (updatePan(event)) return;
    if (!marquee) return;
    const x1 = event.clientX;
    const y1 = event.clientY;
    const moved = marquee.moved || Math.hypot(x1 - marquee.x0, y1 - marquee.y0) > 3;
    const next = { ...marquee, x1, y1, moved };
    setMarquee(next);
    if (!moved) return;
    const sel = {
      left: Math.min(next.x0, x1),
      top: Math.min(next.y0, y1),
      right: Math.max(next.x0, x1),
      bottom: Math.max(next.y0, y1),
    };
    const hits = collectMarqueeHits(sel);
    setMarqueeIds(Array.from(new Set([...next.base, ...hits])));
  };

  const endMarquee = (event: ReactPointerEvent<HTMLElement>) => {
    if (endPan(event)) return;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    if (!marquee) return;
    const wasMoved = marquee.moved;
    setMarquee(null);
    if (!wasMoved) {
      // 空白单击：定位播放头并清空选择（与原有单击行为一致）。
      seekAt(event.clientX, event.currentTarget);
      onSelectItem(null);
      setMarqueeIds([]);
      onEditPoint?.(null);
    } else if (marqueeIds.length > 0) {
      onNotify(`框选 ${marqueeIds.length} 个片段`);
    }
  };

  const cancelMarquee = () => {
    setMarquee(null);
    setMarqueeIds([]);
  };

  const beginPlayheadDrag = (event: ReactPointerEvent<HTMLButtonElement>) => {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    tracksRef.current?.setPointerCapture(event.pointerId);
    seekAt(event.clientX, tracksRef.current);
  };

  /** 指针落点所在轨道号：用 elementsFromPoint 穿透正在拖动的片段，跨轨时才拿到目标轨。 */
  const trackAtPoint = (clientX: number, clientY: number): number | null => {
    const stack = document.elementsFromPoint(clientX, clientY);
    for (const element of stack) {
      const lane = element.closest?.<HTMLElement>('.track[data-track]');
      if (lane) return Number(lane.dataset.track);
    }
    return null;
  };

  /** 停止拖拽边缘自动滚动。 */
  const stopAutoScroll = () => {
    const state = autoScrollRef.current;
    if (!state) return;
    window.cancelAnimationFrame(state.raf);
    autoScrollRef.current = null;
  };
  /** 每帧滚动 6px，并合成 pointermove 让幽灵框/吸附线跟着内容走。 */
  const tickAutoScroll = () => {
    const state = autoScrollRef.current;
    const viewport = tracksViewportRef.current;
    if (!state || !viewport) return;
    const next = Math.max(0, viewport.scrollLeft + state.dir * AUTO_SCROLL_PX);
    if (next !== viewport.scrollLeft) viewport.scrollLeft = next;
    const pointer = dragPointerRef.current;
    const source = dragSourceElRef.current;
    if (pointer && source) {
      source.dispatchEvent(
        new PointerEvent('pointermove', {
          bubbles: true,
          pointerId: pointer.pointerId,
          clientX: pointer.clientX,
          clientY: pointer.clientY,
          buttons: 1,
        }),
      );
    }
    state.raf = window.requestAnimationFrame(tickAutoScroll);
  };
  const maybeAutoScroll = (clientX: number) => {
    const viewport = tracksViewportRef.current;
    if (!viewport) return;
    const rect = viewport.getBoundingClientRect();
    const dir = clientX < rect.left + AUTO_SCROLL_EDGE ? -1 : clientX > rect.right - AUTO_SCROLL_EDGE ? 1 : 0;
    const state = autoScrollRef.current;
    if (!dir) {
      if (state) stopAutoScroll();
      return;
    }
    if (state) {
      if (state.dir === dir) return;
      stopAutoScroll();
    }
    autoScrollRef.current = { dir, raf: window.requestAnimationFrame(tickAutoScroll) };
  };

  /** Esc / pointercancel 统一取消：清空拖拽、吸附线、落点预览三件套。 */
  const cancelActiveDrag = () => {
    stopAutoScroll();
    dragPointerRef.current = null;
    dragSourceElRef.current = null;
    setDrag(null);
    setSnapGuide(null);
    setDropPreview(null);
  };

  /** 素材库拖到时间线：把指针位置换算成 {轨道, 序列时间}（帧对齐）。 */
  const computeAssetDrop = (event: { clientX: number; clientY: number }) => {
    const track = trackAtPoint(event.clientX, event.clientY);
    if (track === null) return null;
    const raw = timeAt(event.clientX, tracksRef.current);
    return { track, time: Math.max(0, Math.round(raw * fps) / fps) };
  };

  const handleAssetDragOver = (event: ReactDragEvent<HTMLDivElement>) => {
    if (event.dataTransfer.types.includes(TRANSITION_DRAG_MIME)) {
      handleTransitionDragOver(event);
      return;
    }
    if (!event.dataTransfer.types.includes(ASSET_DRAG_MIME)) return;
    if (readOnly) {
      event.dataTransfer.dropEffect = 'none';
      return;
    }
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    const drop = computeAssetDrop(event);
    if (!drop) {
      setAssetPreview(null);
      return;
    }
    const dragged = readDraggedAsset();
    const invalid = drop.track >= 4 || lockedTracks.includes(drop.track);
    setAssetPreview({
      track: drop.track,
      offset: drop.time,
      length: dragged?.duration ?? 0,
      title: dragged?.title ?? '素材',
      invalid,
    });
  };

  const handleAssetDragLeave = (event: ReactDragEvent<HTMLDivElement>) => {
    const next = event.relatedTarget as Node | null;
    if (next && event.currentTarget.contains(next)) return;
    setAssetPreview(null);
    setTransitionPreview(null);
  };

  /** 转场落点：在该轨的合法剪点里找离指针最近的一个；超过 36px 视为没有落点。 */
  const nearestCutForTrack = (track: number, time: number) => {
    const width = tracksRef.current?.getBoundingClientRect().width ?? 0;
    const pxPerSecond = width > 0 && total > 0 ? width / total : 0;
    const threshold = pxPerSecond > 0 ? 36 / pxPerSecond : 0.5;
    const candidates: Array<{ itemId: string; track: number; time: number; existing: boolean }> = [];
    // 已有转场的剪点：拖入只是“替换类型”，锚点仍是右侧片段。
    const withTransition = new Set<string>();
    for (const cut of cutPlacements) {
      if (trackOf(cut.next) !== track) continue;
      withTransition.add(cut.next.id);
      candidates.push({ itemId: cut.next.id, track, time: cut.start + cut.effective / 2, existing: true });
    }
    // 还没有转场的合法剪点：复用 EditorPage 的 legalCuts 判定口径。
    for (const placement of placements) {
      const item = placement.item;
      if (trackOf(item) !== track || withTransition.has(item.id)) continue;
      if (!cutAvailability(items, item.id, fps, undefined, sourceDurations).available) continue;
      candidates.push({
        itemId: item.id,
        track,
        time: itemSequenceStart(items, item.id, transitions, fps, sourceDurations),
        existing: false,
      });
    }
    let best: { itemId: string; track: number; time: number; existing: boolean } | null = null;
    let bestDistance = Number.POSITIVE_INFINITY;
    for (const candidate of candidates) {
      const distance = Math.abs(candidate.time - time);
      if (distance < bestDistance) {
        best = candidate;
        bestDistance = distance;
      }
    }
    if (!best || bestDistance > threshold) return null;
    return best;
  };

  const handleTransitionDragOver = (event: ReactDragEvent<HTMLDivElement>) => {
    if (readOnly) {
      event.dataTransfer.dropEffect = 'none';
      return;
    }
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    setAssetPreview(null);
    const drop = computeAssetDrop(event);
    if (!drop) {
      setTransitionPreview(null);
      return;
    }
    const dragged = readDraggedTransition();
    const locked = lockedTracks.includes(drop.track);
    const nearest = locked ? null : nearestCutForTrack(drop.track, drop.time);
    setTransitionPreview(
      nearest
        ? { track: drop.track, time: nearest.time, label: dragged?.label ?? '转场', invalid: false, existing: nearest.existing }
        : { track: drop.track, time: drop.time, label: dragged?.label ?? '转场', invalid: true },
    );
  };

  const handleTransitionDrop = (event: ReactDragEvent<HTMLDivElement>) => {
    const dragged = parseDraggedTransitionData(event.dataTransfer.getData(TRANSITION_DRAG_MIME)) ?? readDraggedTransition();
    const drop = computeAssetDrop(event);
    setTransitionPreview(null);
    endTransitionDrag();
    if (!dragged || !drop) return;
    event.preventDefault();
    if (readOnly) {
      onNotify('只读预览：不能应用转场');
      return;
    }
    if (lockedTracks.includes(drop.track)) {
      onNotify(`${trackName(drop.track)}已锁定，请先解锁轨道再应用转场`);
      return;
    }
    const nearest = nearestCutForTrack(drop.track, drop.time);
    if (!nearest) {
      onNotify('这里没有可放转场的剪点：拖到同一轨道相邻两个片段的交界处');
      return;
    }
    onDropTransition?.({
      kind: dragged.kind,
      itemId: nearest.itemId,
      track: drop.track,
      time: nearest.time,
    });
  };

  const handleAssetDrop = (event: ReactDragEvent<HTMLDivElement>) => {
    if (event.dataTransfer.types.includes(TRANSITION_DRAG_MIME)) {
      handleTransitionDrop(event);
      return;
    }
    const dragged = parseDraggedAssetData(event.dataTransfer.getData(ASSET_DRAG_MIME)) ?? readDraggedAsset();
    const drop = computeAssetDrop(event);
    setAssetPreview(null);
    endAssetDrag();
    if (!dragged || !drop) return;
    event.preventDefault();
    if (readOnly) {
      onNotify('只读预览：不能放入素材');
      return;
    }
    if (drop.track >= 4) {
      onNotify('画面素材请放到视频轨（V1–V3）');
      return;
    }
    if (lockedTracks.includes(drop.track)) {
      onNotify(`${trackName(drop.track)}已锁定，请先解锁轨道再放入素材`);
      return;
    }
    onDropAsset?.({
      clipId: dragged.clipId,
      title: dragged.title,
      duration: dragged.duration,
      track: drop.track,
      time: drop.time,
    });
  };

  const beginClipDrag = (event: ReactPointerEvent<HTMLButtonElement>, placement: Placement) => {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    setSelectedGap(null);
    if (readOnly) {
      onSelectItem(placement.item.id);
      onNotify('只读预览：子序列片段不能拖动、裁剪、删除或添加关键帧（v1 限制）');
      return;
    }
    // FilmCraft：锁定的轨道只能选中查看，不能按下拖动/裁剪/切割。
    if (lockedTracks.includes(placement.track)) {
      onSelectItem(placement.item.id);
      onNotify(`${trackName(placement.track)} 已锁定：先解锁轨道再编辑`);
      return;
    }
    // 手形 / 缩放 / 钢笔在片段上也要生效：H 平移、Z 缩放、P 加点。
    if (activeTool === 'hand') {
      startPan(event);
      return;
    }
    if (activeTool === 'zoom') {
      zoomAbout(event.clientX, event.altKey ? 1 / 1.5 : 1.5, tracksRef.current);
      return;
    }
    if (activeTool === 'pen') {
      onSelectItem(placement.item.id);
      addVolumeKeyframeAtSequence(placement.item.id, timeAt(event.clientX, tracksRef.current));
      return;
    }
    const width = tracksRef.current?.getBoundingClientRect().width ?? 0;
    if (width <= 0) return;
    // 自动滚动基准：拖拽起点的滚动位置、源元素与指针，供 rAF 补发预览事件。
    dragScrollRef.current = tracksViewportRef.current?.scrollLeft ?? 0;
    dragSourceElRef.current = event.currentTarget;
    dragPointerRef.current = { clientX: event.clientX, clientY: event.clientY, pointerId: event.pointerId };
    // 边缘命中提前判定：修饰键分流只在「命中边缘」时生效（body 拖动仍走移动/加选）。
    const hitRect = event.currentTarget.getBoundingClientRect();
    const trimHit = (event.target as HTMLElement).closest<HTMLElement>('[data-trim]')?.dataset.trim;
    // Very short clips are intentionally body-draggable only: a 6px handle would
    // otherwise swallow the whole clip and make it impossible to reorder.
    const trim = hitRect.width > 24 && (trimHit === 'start' || trimHit === 'end') ? trimHit : null;
    // FilmCraft Trim Monitor：命中修剪手柄就把这个切点设为编辑点，节目监视器切换为修剪监视器。
    // 有相邻片段且无空隙 → 默认滚动编辑（FC 默认 trim 在切点上退化为 roll），Alt=只动本片。
    if (activeTool === 'select' && trim) {
      const lanePlacements = placements.filter((entry) => entry.track === placement.track);
      const laneIndex = lanePlacements.findIndex((entry) => entry.item.id === placement.item.id);
      const leftItem = trim === 'start' ? lanePlacements[laneIndex - 1]?.item : placement.item;
      const rightItem = trim === 'start' ? placement.item : lanePlacements[laneIndex + 1]?.item;
      const gapBefore =
        trim === 'start'
          ? Math.max(0, placement.item.gapBefore ?? 0)
          : Math.max(0, rightItem?.gapBefore ?? 0);
      const kind = event.altKey ? 'trim' : leftItem && rightItem && gapBefore <= 0.001 ? 'roll' : 'trim';
      onEditPoint?.({ clipId: placement.item.id, edge: trim === 'start' ? 'in' : 'out', kind });
    }
    // Shift+点击：与框选共用 marqueeIds 集合，加选/减选切换；Alt+点击保留链接隔离语义。
    if (event.shiftKey && !event.altKey && !trim) {
      const base = marqueeIds.length ? marqueeIds : selectedItemId ? [selectedItemId] : [];
      const next = base.includes(placement.item.id)
        ? base.filter((id) => id !== placement.item.id)
        : [...base, placement.item.id];
      setMarqueeIds(next);
      onSelectItem(next.includes(placement.item.id) ? placement.item.id : (next[next.length - 1] ?? null));
      onNotify(next.length > 1 ? `已选 ${next.length} 个片段` : next.length === 1 ? '已选 1 个片段' : '已清空片段选择');
      return;
    }
    // P2（task-128）：框选集合内拖动 → 整组平移。Alt 保留链接隔离/个体语义。
    if (!event.altKey && !trim && activeTool === 'select' && marqueeIds.length > 1 && marqueeIds.includes(placement.item.id)) {
      // 不在这里 onSelectItem：父级重渲染会导致元素级 pointer capture 丢失（#11 同类问题），
      // 组拖的选中态由 marqueeIds 表达，提交后再收敛 selectedItem。
      event.currentTarget.setPointerCapture(event.pointerId);
      setDrag({
        kind: 'move',
        id: placement.item.id,
        groupIds: marqueeIds.slice(),
        startX: event.clientX,
        width,
        offset: placement.offset,
        length: placement.length,
        dx: 0,
      });
      return;
    }
    // 普通点击 / Alt+点击都收敛为单片段选择；Alt 仍保留链接隔离语义。
    if (marqueeIds.length > 0) setMarqueeIds([]);
    if (activeTool === 'select') onEditPoint?.(null);
    onSelectItem(placement.item.id);
    // V2/V3/A1-A3 use their own sequential lane order. Reordering is allowed
    // within the lane; razor/slip/rate/rolling/ripple/slide remain exclusive to
    // V1 until their source range semantics are verified for overlay and audio
    // clips. Track select and marker work on every lane.
    if (placement.track !== 1 && activeTool !== 'select' && activeTool !== 'track' && activeTool !== 'razor') return;
    if (activeTool === 'razor') {
      const rect = event.currentTarget.getBoundingClientRect();
      const ratio = clamp((event.clientX - rect.left) / rect.width, 0, 1);
      let splitSource = placement.item.start + ratio * (placement.item.end - placement.item.start);
      // FilmCraft：刀片切点先帧取整，再吸附到片边/播放头/标记（SNAP_PX=9）。
      splitSource = Math.round(splitSource * fps) / fps;
      // 吸附候选是「序列时间」，而 splitSource 是「素材内时间」，必须先换算到序列时间再吸。
      const razorSpeed = placement.item.speed ?? 1;
      if (snapEnabled) {
        const seqTime = placement.offset + (splitSource - placement.item.start) / razorSpeed;
        const edgeSnap = snapEdge(seqTime, [], (SNAP_PX / Math.max(1, rect.width)) * total);
        if (edgeSnap.hit !== null) {
          splitSource = placement.item.start + (edgeSnap.value - placement.offset) * razorSpeed;
        }
      }
      const razorMinLen = Math.max(1 / fps, 0.001);
      splitSource = clamp(splitSource, placement.item.start + razorMinLen, placement.item.end - razorMinLen);
      // Shift+刀片：在全部未锁定轨道上同一时间点切割（FC razor Shift 全轨）。
      if (event.shiftKey) {
        splitClipsAt(splitSource);
        return;
      }
      splitItemAt(placement, splitSource);
      return;
    }
    event.currentTarget.setPointerCapture(event.pointerId);
    const startDrag = (drag: ClipDrag) => {
      setDrag(drag);
    };
    // FilmCraft 修饰键分流（timeline_hit.rs:selection_trim_kind）：
    // Cmd/Ctrl=波纹修剪、Cmd/Ctrl+Shift=滚动编辑、Alt=只动本片（断开链接语义），无修饰=普通裁剪。
    // 滑移只走 Y 工具，不再占用 Alt。
    if (activeTool === 'select' && trim) {
      if ((event.ctrlKey || event.metaKey) && event.shiftKey) {
        startDrag({ kind: 'rolling', id: placement.item.id, edge: trim, startX: event.clientX, width, dx: 0 });
        return;
      }
      if (event.ctrlKey || event.metaKey) {
        startDrag({ kind: 'ripple-trim', id: placement.item.id, edge: trim, startX: event.clientX, width, dx: 0 });
        return;
      }
      if (event.altKey) {
        startDrag({
          kind: 'trim',
          id: placement.item.id,
          edge: trim,
          startX: event.clientX,
          width,
          dx: 0,
          unlink: true,
        });
        return;
      }
    }
    if (activeTool === 'track') {
      startDrag({
        kind: 'track',
        id: placement.item.id,
        startX: event.clientX,
        width,
        offset: placement.offset,
        length: placement.length,
        dx: 0,
      });
      return;
    }
    if (activeTool === 'slip') {
      startDrag({ kind: 'slip', id: placement.item.id, startX: event.clientX, width, length: placement.sourceLength, dx: 0 });
      return;
    }
    if (activeTool === 'rate') {
      if (trim !== 'start' && trim !== 'end') {
        onNotify('比率拉伸：拖动片段左右边缘改变速率');
        return;
      }
      startDrag({ kind: 'rate', id: placement.item.id, edge: trim, startX: event.clientX, width, dx: 0 });
      return;
    }
    if (activeTool === 'rolling') {
      if (trim !== 'start' && trim !== 'end') {
        onNotify('滚动编辑：拖动片段边缘移动与相邻片段的切点');
        return;
      }
      startDrag({ kind: 'rolling', id: placement.item.id, edge: trim, startX: event.clientX, width, dx: 0 });
      return;
    }
    if (activeTool === 'ripple') {
      if (trim === 'start' || trim === 'end') {
        startDrag({ kind: 'ripple-trim', id: placement.item.id, edge: trim, startX: event.clientX, width, dx: 0 });
      } else {
        startDrag({
          kind: 'ripple-move',
          id: placement.item.id,
          startX: event.clientX,
          width,
          offset: placement.offset,
          length: placement.length,
          dx: 0,
        });
      }
      return;
    }
    if (activeTool === 'slide') {
      if (trim === 'start' || trim === 'end') {
        startDrag({ kind: 'trim', id: placement.item.id, edge: trim, startX: event.clientX, width, dx: 0 });
      } else {
        startDrag({
          kind: 'slide',
          id: placement.item.id,
          startX: event.clientX,
          width,
          offset: placement.offset,
          length: placement.length,
          dx: 0,
        });
      }
      return;
    }
    if (trim === 'start' || trim === 'end') {
      startDrag({ kind: 'trim', id: placement.item.id, edge: trim, startX: event.clientX, width, dx: 0 });
      return;
    }
    startDrag({
      kind: 'move',
      id: placement.item.id,
      startX: event.clientX,
      width,
      offset: placement.offset,
      length: placement.length,
      dx: 0,
    });
  };

  const moveClipDrag = (event: ReactPointerEvent<HTMLButtonElement>) => {
    if (!drag) return;
    if (!event.currentTarget.hasPointerCapture(event.pointerId)) return;
    dragPointerRef.current = { clientX: event.clientX, clientY: event.clientY, pointerId: event.pointerId };
    // 内容滚动会让同一指针位置对应更靠后的时间点，dx 必须补上滚动差。
    const dx =
      event.clientX - drag.startX + ((tracksViewportRef.current?.scrollLeft ?? 0) - dragScrollRef.current);
    maybeAutoScroll(event.clientX);
    const threshold = (SNAP_PX / drag.width) * total;
    const deltaTime = (dx / drag.width) * total;
    if (drag.kind === 'move') {
      if (drag.groupIds && drag.groupIds.length > 1) {
        const ids = new Set(drag.groupIds);
        const members = groupMembersOf(drag.groupIds);
        if (!members.length) {
          setDrag({ ...drag, dx });
          return;
        }
        const first = clampGroupDelta(members, ids, deltaTime).delta;
        const snapped = snapGroupDelta(members, ids, first, threshold);
        const nextDelta = clampGroupDelta(members, ids, snapped.delta).delta;
        setSnapGuide(snapped.hit);
        setDropPreview(null);
        setDrag({ ...drag, dx: (nextDelta / total) * drag.width });
        return;
      }
      const item = items.find((candidate) => candidate.id === drag.id);
      if (!item) {
        setSnapGuide(null);
        setDropPreview(null);
        setDrag({ ...drag, dx });
        return;
      }
      const linkedIds = item.linkGroup
        ? items.filter((candidate) => candidate.linkGroup === item.linkGroup).map((candidate) => candidate.id)
        : [item.id];
      const rawOffset = Math.max(0, drag.offset + deltaTime);
      const result: { value: number; hit: number | null } = snapEnabled
        ? snapMoveEdge(rawOffset, drag.length, linkedIds, threshold)
        : { value: rawOffset, hit: null };
      setSnapGuide(result.hit);
      // FilmCraft drag preview：拖动中只画落点幽灵框与目标轨高亮，工程数据不动。
      const sourceTrack = trackOf(item);
      const targetTrack = trackAtPoint(event.clientX, event.clientY) ?? sourceTrack;
      const invalid =
        (sourceTrack >= 4) !== (targetTrack >= 4) || lockedTracks.includes(targetTrack);
      setDropPreview(
        Math.abs(result.value - drag.offset) < 0.001 && targetTrack === sourceTrack
          ? null
          : { track: targetTrack, offset: result.value, length: drag.length, invalid },
      );
      setDrag({ ...drag, dx: ((result.value - drag.offset) / total) * drag.width });
      return;
    }
    if (drag.kind === 'ripple-move' || drag.kind === 'slide') {
      if (!snapEnabled) {
        setSnapGuide(null);
        setDrag({ ...drag, dx });
        return;
      }
      const item = items.find((candidate) => candidate.id === drag.id);
      const linkedIds = item?.linkGroup
        ? items.filter((candidate) => candidate.linkGroup === item.linkGroup).map((candidate) => candidate.id)
        : item
          ? [item.id]
          : [];
      const result = snapMoveEdge(Math.max(0, drag.offset + deltaTime), drag.length, linkedIds, threshold);
      setSnapGuide(result.hit);
      setDrag({ ...drag, dx: ((result.value - drag.offset) / total) * drag.width });
      return;
    }
    if (drag.kind === 'trim' || drag.kind === 'ripple-trim' || drag.kind === 'rate') {
      const placement = placements.find((candidate) => candidate.item.id === drag.id);
      if (!placement || !snapEnabled || drag.kind === 'rate') {
        setSnapGuide(null);
        setDrag({ ...drag, dx });
        return;
      }
      const speed = itemSpeed(placement.item);
      const minLen = Math.max(1 / fps, 0.001);
      const sourceDelta = deltaTime * speed;
      const excludeIds = placement.item.linkGroup
        ? items.filter((candidate) => candidate.linkGroup === placement.item.linkGroup).map((candidate) => candidate.id)
        : [placement.item.id];
      const sourceLimit = Math.max(placement.item.end, sourceDuration || placement.item.end);
      const rawEdge =
        drag.edge === 'start'
          ? clamp(placement.item.start + sourceDelta, 0, placement.item.end - minLen)
          : clamp(placement.item.end + sourceDelta, placement.item.start + minLen, sourceLimit);
      const result = snapEdge(rawEdge, excludeIds, threshold);
      setSnapGuide(result.hit);
      const anchor = drag.edge === 'start' ? placement.item.start : placement.item.end;
      setDrag({ ...drag, dx: (((result.value - anchor) / speed) / total) * drag.width });
      return;
    }
    setSnapGuide(null);
    setDrag({ ...drag, dx });
  };

  const lanePlacements = (track: number) =>
    placements.filter((candidate) => candidate.track === track).sort((a, b) => a.offset - b.offset);
  const roundFrame = (value: number) => Math.round(value * fps) / fps;

  const endClipDrag = (event: ReactPointerEvent<HTMLButtonElement>, placement: Placement) => {
    if (!drag || drag.id !== placement.item.id) return;
    event.preventDefault();
    event.stopPropagation();
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    setSnapGuide(null);
    setDropPreview(null);
    stopAutoScroll();
    dragPointerRef.current = null;
    dragSourceElRef.current = null;
    const dx =
      event.clientX - drag.startX + ((tracksViewportRef.current?.scrollLeft ?? 0) - dragScrollRef.current);
    if (drag.kind === 'move') {
      if (drag.groupIds && drag.groupIds.length > 1) {
        const ids = new Set(drag.groupIds);
        const members = groupMembersOf(drag.groupIds);
        const rawDelta = (dx / drag.width) * total;
        const first = clampGroupDelta(members, ids, rawDelta).delta;
        const snapped = snapGroupDelta(members, ids, first, (SNAP_PX / drag.width) * total);
        const delta = clampGroupDelta(members, ids, snapped.delta).delta;
        setSnapGuide(null);
        setDrag(null);
        if (Math.abs(delta) < 0.001) {
          onNotify('整组移动未超过吸附阈值');
          return;
        }
        const laneLead = new Map<number, string>();
        for (const member of members) {
          const current = laneLead.get(member.track);
          if (!current) laneLead.set(member.track, member.item.id);
        }
        const next = items.map((item) => {
          for (const [track, leadId] of laneLead) {
            if (trackOf(item) !== track || item.id !== leadId) continue;
            const gap = Math.max(0, Math.round((((item.gapBefore ?? 0) + delta) * 100)) / 100);
            return { ...item, gapBefore: gap };
          }
          return item;
        });
        onUpdateItems(next, delta > 0 ? '组拖（右移）' : '组拖（左移）');
        onSelectItem(placement.item.id);
        onNotify(`已整组平移 ${members.length} 个片段（${delta > 0 ? '+' : ''}${delta.toFixed(2)}s）`);
        return;
      }
      const targetTrack = trackAtPoint(event.clientX, event.clientY) ?? placement.track;
      if (Math.abs(dx) < 4 && targetTrack === placement.track) {
        setDrag(null);
        onSeek(placement.offset);
        return;
      }
      const deltaTime = (dx / drag.width) * total;
      const linkedIds = placement.item.linkGroup
        ? items
            .filter((item) => item.linkGroup === placement.item.linkGroup)
            .map((item) => item.id)
        : [placement.item.id];
      const targetTime = snapMoveEdge(
        Math.max(0, drag.offset + deltaTime),
        drag.length,
        linkedIds,
        (SNAP_PX / drag.width) * total,
      ).value;
      if (!Number.isInteger(targetTrack) || targetTrack < 1 || targetTrack > 6) {
        setDrag(null);
        return;
      }
      if ((placement.track >= 4) !== (targetTrack >= 4)) {
        setDrag(null);
        onNotify(placement.track >= 4 ? '音频片段只能移动到 A1–A3' : '画面片段请用右键“分离音频”再移到音频轨');
        return;
      }
      if (lockedTracks.includes(targetTrack)) {
        setDrag(null);
        onNotify('目标轨道已锁定');
        return;
      }
      const dropTime = targetTime;
      let next = moveItemToTime(items, placement.item.id, dropTime + drag.length / 2, targetTrack);
      next = placeItemAtTime(next, placement.item.id, dropTime, targetTrack);
      const moved = next.find((item) => item.id === placement.item.id);
      if (moved?.linkGroup) {
        for (const linked of next.filter(
          (item) => item.linkGroup === moved.linkGroup && item.id !== moved.id,
        )) {
          const linkedTrack = trackOf(linked);
          next = moveItemToTime(
            next,
            linked.id,
            dropTime + Math.max(0.02, itemOutputDuration(linked)) / 2,
            linkedTrack,
          );
          next = placeItemAtTime(next, linked.id, dropTime, linkedTrack);
        }
      }
      setDrag(null);
      if (
        next.some(
          (candidate, index) =>
            candidate.id !== items[index]?.id ||
            trackOf(candidate) !== trackOf(items[index]!) ||
            Math.abs((candidate.gapBefore ?? 0) - (items[index]?.gapBefore ?? 0)) > 0.001,
        )
      ) {
        onUpdateItems(next, targetTrack === placement.track ? '移动片段' : '跨轨移动片段');
        if (targetTrack !== placement.track) {
          onNotify(`已移动到 ${tracks.find((track) => track.number === targetTrack)?.id ?? '轨道'}`);
        }
      } else {
        onSeek(placement.offset);
      }
      return;
    }

    if (drag.kind === 'slip') {
      const speed = itemSpeed(placement.item);
      const deltaTime = (dx / drag.width) * total;
      const sourceDelta = deltaTime * speed;
      const limit = Math.max(0, (sourceDuration || placement.item.end) - drag.length);
      // FC lib.rs:997-1008：内容跟指针走——向右拖 = 素材入点变小（start 减）。
      const nextStart = Math.round(clamp(placement.item.start - sourceDelta, 0, limit) * fps) / fps;
      const nextEnd = nextStart + drag.length;
      setDrag(null);
      if (Math.abs(nextStart - placement.item.start) < 0.001) return;
      onUpdateItems(
        items.map((item) =>
          item.id === placement.item.id ? { ...item, start: nextStart, end: nextEnd } : item,
        ),
        '滑移片段',
      );
      onNotify(`已滑移片段内容 ${Math.round(-deltaTime * fps)} 帧`);
      return;
    }

    if (drag.kind === 'rate') {
      setDrag(null);
      const minLen = Math.max(1 / fps, 0.001);
      const deltaTime = (dx / drag.width) * total;
      const sourceLen = placement.item.end - placement.item.start;
      const oldOut = itemOutputDuration(placement.item);
      const rawOut = clamp(oldOut + deltaTime, Math.max(minLen, sourceLen / 4), Math.min(sourceLen / 0.25, 300));
      const speed = clamp(sourceLen / rawOut, 0.25, 4);
      const rounded = Math.round(speed * 100) / 100;
      if (Math.abs(rounded - itemSpeed(placement.item)) < 0.005) return;
      const newOut = sourceLen / rounded;
      const patch: Partial<TimelineItem> = { speed: rounded };
      if (drag.edge === 'start') {
        patch.gapBefore = Math.max(0, roundFrame((placement.item.gapBefore ?? 0) + (oldOut - newOut)));
      }
      onUpdateItems(
        items.map((item) => (item.id === placement.item.id ? { ...item, ...patch } : item)),
        `比率拉伸 ${rounded.toFixed(2)}×`,
      );
      onNotify(`片段速率 ${rounded.toFixed(2)}×，输出时长 ${newOut.toFixed(2)} 秒`);
      return;
    }

    if (drag.kind === 'rolling') {
      setDrag(null);
      const minLen = Math.max(1 / fps, 0.001);
      const deltaTime = (dx / drag.width) * total;
      const lane = lanePlacements(placement.track);
      const index = lane.findIndex((candidate) => candidate.item.id === placement.item.id);
      if (index < 0) return;
      const left = drag.edge === 'start' ? lane[index - 1] : lane[index];
      const right = drag.edge === 'start' ? lane[index] : lane[index + 1];
      if (!left || !right) {
        onNotify(drag.edge === 'start' ? '这是轨道第一个片段，前面没有切点可滚动' : '这是轨道最后一个片段，后面没有切点可滚动');
        return;
      }
      const leftSpeed = itemSpeed(left.item);
      const rightSpeed = itemSpeed(right.item);
      const leftOut = itemOutputDuration(left.item);
      const rightSourceLen = right.item.end - right.item.start;
      const leftTail = Math.max(0, (sourceDuration || left.item.end) - left.item.end);
      const lower = Math.max(-(leftOut - minLen) / leftSpeed, -right.item.start / rightSpeed);
      const upper = Math.min(leftTail / leftSpeed, (rightSourceLen - minLen) / rightSpeed);
      const delta = clamp(roundFrame(deltaTime), lower, upper);
      if (Math.abs(delta) < 0.0005) {
        onNotify('切点已到可移动范围的边缘');
        return;
      }
      const nextLeftEnd = roundFrame(left.item.end + delta * leftSpeed);
      const nextRightStart = roundFrame(right.item.start + delta * rightSpeed);
      onUpdateItems(
        items.map((item) => {
          if (item.id === left.item.id) return { ...item, end: nextLeftEnd };
          if (item.id === right.item.id) return { ...item, start: nextRightStart };
          return item;
        }),
        '滚动编辑切点',
      );
      onNotify(`切点移动 ${Math.round(Math.abs(delta) * fps)} 帧，序列总时长不变`);
      return;
    }

    if (drag.kind === 'ripple-move') {
      setDrag(null);
      const deltaTime = (dx / drag.width) * total;
      const before = placement.item.gapBefore ?? 0;
      const nextGap = Math.max(0, roundFrame(before + deltaTime));
      if (Math.abs(nextGap - before) < 0.0005) {
        onNotify('前方没有空间了，可用滚动编辑或滑移工具继续微调');
        return;
      }
      onUpdateItems(
        items.map((item) => (item.id === placement.item.id ? { ...item, gapBefore: nextGap } : item)),
        '波纹移动片段',
      );
      const shift = nextGap - before;
      onNotify(`片段及其后内容${shift > 0 ? '右移' : '左移'} ${Math.abs(shift).toFixed(2)} 秒，无空隙残留`);
      return;
    }

    if (drag.kind === 'slide') {
      setDrag(null);
      const minLen = Math.max(1 / fps, 0.001);
      const deltaTime = (dx / drag.width) * total;
      const lane = lanePlacements(placement.track);
      const index = lane.findIndex((candidate) => candidate.item.id === placement.item.id);
      if (index < 0) return;
      const previous = index > 0 ? lane[index - 1] : null;
      const next = index < lane.length - 1 ? lane[index + 1] : null;
      if (!previous || !next) {
        onNotify('滑片需要前后都有相邻片段才能补偿');
        return;
      }
      const previousSpeed = itemSpeed(previous.item);
      const nextSpeed = itemSpeed(next.item);
      const previousTail =
        Math.max(0, (sourceDuration || previous.item.end) - previous.item.end) / previousSpeed;
      const nextHead = next.item.start / nextSpeed;
      const lower = Math.max(-(itemOutputDuration(previous.item) - minLen) / previousSpeed, -nextHead);
      const upper = Math.min(previousTail, (next.item.end - next.item.start - minLen) / nextSpeed);
      const delta = clamp(roundFrame(deltaTime), lower, upper);
      if (Math.abs(delta) < 0.0005) {
        onNotify('相邻片段素材已到边缘，无法继续幻灯片移动');
        return;
      }
      // FC lib.rs:1011-1068：本片段位移 δ 完全由前后邻居补偿；
      // 不要同时改自身 gapBefore（旧实现 = 双倍位移 bug）。
      onUpdateItems(
        items.map((item) => {
          if (item.id === previous.item.id) {
            return { ...item, end: roundFrame(item.end + delta * previousSpeed) };
          }
          if (item.id === next.item.id) {
            return { ...item, start: roundFrame(item.start + delta * nextSpeed) };
          }
          return item;
        }),
        '幻灯片移动片段',
      );
      onNotify(`幻灯片移动 ${Math.round(Math.abs(delta) * fps)} 帧，相邻片段已补偿`);
      return;
    }

    if (drag.kind === 'track') {
      setDrag(null);
      const deltaTime = (dx / drag.width) * total;
      const lane = lanePlacements(placement.track);
      const first = lane[0];
      if (!first) return;
      const before = first.item.gapBefore ?? 0;
      const nextGap = Math.max(0, roundFrame(before + deltaTime));
      if (Math.abs(nextGap - before) < 0.0005) {
        onNotify('轨道已到时间线起点');
        return;
      }
      onUpdateItems(
        items.map((item) => (item.id === first.item.id ? { ...item, gapBefore: nextGap } : item)),
        '轨道整体位移',
      );
      const shift = nextGap - before;
      onNotify(`${trackName(placement.track)} 整体${shift > 0 ? '右移' : '左移'} ${Math.abs(shift).toFixed(2)} 秒`);
      return;
    }

    // FilmCraft「普通裁剪」：只改本片段入/出点，后续片段整体保持原位（靠 gapBefore 补偿）。
    if (drag.kind === 'trim') {
      const speed = itemSpeed(placement.item);
      const minLen = Math.max(1 / fps, 0.001);
      const deltaTime = (dx / drag.width) * total;
      const sourceDelta = deltaTime * speed;
      const trimExcludeIds =
        placement.item.linkGroup && !drag.unlink
          ? items.filter((item) => item.linkGroup === placement.item.linkGroup).map((item) => item.id)
          : [placement.item.id];
      const trimSnapThreshold = (SNAP_PX / drag.width) * total;
      const sourceLimit = Math.max(placement.item.end, sourceDuration || placement.item.end);
      let nextStart = placement.item.start;
      let nextEnd = placement.item.end;
      if (drag.edge === 'start') {
        const rawStart = clamp(placement.item.start + sourceDelta, 0, placement.item.end - minLen);
        nextStart = clamp(
          snapEdge(rawStart, trimExcludeIds, trimSnapThreshold).value,
          0,
          placement.item.end - minLen,
        );
      } else {
        const rawEnd = clamp(
          placement.item.end + sourceDelta,
          placement.item.start + minLen,
          sourceLimit,
        );
        nextEnd = clamp(
          snapEdge(rawEnd, trimExcludeIds, trimSnapThreshold).value,
          placement.item.start + minLen,
          sourceLimit,
        );
      }
      setDrag(null);
      if (Math.abs(nextStart - placement.item.start) < 0.001 && Math.abs(nextEnd - placement.item.end) < 0.001) {
        return;
      }
      const deltaLen =
        itemOutputDuration({ ...placement.item, start: nextStart, end: nextEnd }) -
        itemOutputDuration(placement.item);
      const gapDelta = -deltaLen;
      const trimmedIds = new Set<string>([
        placement.item.id,
        ...(placement.item.linkGroup && !drag.unlink
          ? items.filter((item) => item.linkGroup === placement.item.linkGroup).map((item) => item.id)
          : []),
      ]);
      const gapAdjust = new Map<string, number>();
      if (drag.edge === 'end') {
        for (const trimmed of placements.filter((candidate) => trimmedIds.has(candidate.item.id))) {
          const ordered = lanePlacements(trimmed.track);
          const index = ordered.findIndex((candidate) => candidate.item.id === trimmed.item.id);
          const next = index >= 0 ? ordered[index + 1] : undefined;
          if (next) gapAdjust.set(next.item.id, (gapAdjust.get(next.item.id) ?? 0) + gapDelta);
        }
      }
      onUpdateItems(
        items.map((item) => {
          const isTrimmed = trimmedIds.has(item.id);
          const base = isTrimmed ? { ...item, start: nextStart, end: nextEnd } : item;
          const adjust =
            (isTrimmed && drag.edge === 'start' ? gapDelta : 0) + (gapAdjust.get(item.id) ?? 0);
          if (Math.abs(adjust) < 0.0005) return base;
          return { ...base, gapBefore: Math.max(0, roundFrame((item.gapBefore ?? 0) + adjust)) };
        }),
        drag.edge === 'start' ? '裁剪片段入点' : '裁剪片段出点',
      );
      onNotify(
        `裁剪${drag.edge === 'start' ? '入点' : '出点'} ${Math.abs(deltaLen).toFixed(2)} 秒：后续片段保持原位`,
      );
      return;
    }

    // FilmCraft「波纹裁剪」：入/出点变化后后续片段自动跟随，序列保持连续。
    if (drag.kind === 'ripple-trim') {
      const speed = itemSpeed(placement.item);
      const minLen = Math.max(1 / fps, 0.001);
      const deltaTime = (dx / drag.width) * total;
      const sourceDelta = deltaTime * speed;
      const trimExcludeIds = placement.item.linkGroup
        ? items.filter((item) => item.linkGroup === placement.item.linkGroup).map((item) => item.id)
        : [placement.item.id];
      const trimSnapThreshold = (SNAP_PX / drag.width) * total;
      const sourceLimit = Math.max(placement.item.end, sourceDuration || placement.item.end);
      let nextStart = placement.item.start;
      let nextEnd = placement.item.end;
      if (drag.edge === 'start') {
        const rawStart = clamp(placement.item.start + sourceDelta, 0, placement.item.end - minLen);
        nextStart = clamp(
          snapEdge(rawStart, trimExcludeIds, trimSnapThreshold).value,
          0,
          placement.item.end - minLen,
        );
      } else {
        const rawEnd = clamp(
          placement.item.end + sourceDelta,
          placement.item.start + minLen,
          sourceLimit,
        );
        nextEnd = clamp(
          snapEdge(rawEnd, trimExcludeIds, trimSnapThreshold).value,
          placement.item.start + minLen,
          sourceLimit,
        );
      }
      setDrag(null);
      if (Math.abs(nextStart - placement.item.start) < 0.001 && Math.abs(nextEnd - placement.item.end) < 0.001) {
        return;
      }
      onUpdateItems(
        items.map((item) => {
          if (item.id === placement.item.id) return { ...item, start: nextStart, end: nextEnd };
          if (placement.item.linkGroup && item.linkGroup === placement.item.linkGroup) {
            return { ...item, start: nextStart, end: nextEnd };
          }
          return item;
        }),
        drag.edge === 'start' ? '波纹裁剪入点' : '波纹裁剪出点',
      );
      const deltaLen =
        itemOutputDuration({ ...placement.item, start: nextStart, end: nextEnd }) -
        itemOutputDuration(placement.item);
      onNotify(
        `波纹裁剪${drag.edge === 'start' ? '入点' : '出点'}：后续片段自动跟随，序列总长${deltaLen < 0 ? '缩短' : '增加'} ${Math.abs(deltaLen).toFixed(2)} 秒`,
      );
      return;
    }
  };

  const snapTime = (value: number, widthPx?: number): number => {
    if (!snapEnabled) return value;
    const snapWidth = Math.max(1, widthPx ?? rulerRef.current?.getBoundingClientRect().width ?? 1);
    const edgeSnap = snapEdge(value, [], (SNAP_PX / snapWidth) * total);
    if (edgeSnap.hit !== null) return edgeSnap.value;
    const step = total > 900 ? 5 : total > 300 ? 2 : 1;
    const nearest = Math.round(value / step) * step;
    return Math.abs(value - nearest) < step * 0.32 ? nearest : value;
  };

  /**
   * FilmCraft razor（timeline.rs:2913-2940）：切点帧取整→吸附，切链接组时级联切画面/音频；
   * 右半段使用全新的 linkGroup（不继承旧组），切完不改选区。
   * 传 placement 时只切该片段所在的链接组；不传则在所有未锁定轨道同一时间点全切（Shift+刀片）。
   */
  const splitClipsAt = (splitSource: number, placement?: (typeof placements)[number]) => {
    const minLen = Math.max(1 / fps, 0.001);
    const candidates = items.filter((item) => {
      if (placement) {
        return placement.item.linkGroup
          ? item.linkGroup === placement.item.linkGroup
          : item.id === placement.item.id;
      }
      return !lockedTracks.includes(trackOf(item));
    });
    const inside = candidates.filter((item) => {
      const local = splitSource - item.start;
      const sourceLength = Math.max(0, item.end - item.start);
      return local >= minLen && local <= sourceLength - minLen;
    });
    if (!inside.length) {
      onNotify('把切点移到片段内部再拆分');
      return;
    }
    // 按原链接组分组，右半段每组生成一个新的 linkGroup，避免跨组串链接。
    const groups = new Map<string, TimelineItem[]>();
    for (const item of inside) {
      const key = item.linkGroup ?? `solo:${item.id}`;
      const bucket = groups.get(key) ?? [];
      bucket.push(item);
      groups.set(key, bucket);
    }
    const stamp = Date.now().toString(36);
    const rightGroupByItem = new Map<string, string>();
    groups.forEach((members, key) => {
      const rightGroup = members.length > 1 || !key.startsWith('solo:') ? `${key}-r${stamp}` : '';
      for (const member of members) rightGroupByItem.set(member.id, rightGroup);
    });
    const next = items.flatMap((item) => {
      if (!inside.some((candidate) => candidate.id === item.id)) return [item];
      const rightId = `${item.id}-split-${stamp}`;
      const rightGroup = rightGroupByItem.get(item.id) ?? '';
      return [
        { ...item, end: splitSource },
        {
          ...item,
          id: rightId,
          start: splitSource,
          gapBefore: 0,
          ...(rightGroup ? { linkGroup: rightGroup } : {}),
        },
      ];
    });
    onUpdateItems(next, inside.length > 1 ? '拆分片段（含链接）' : '拆分片段');
    const refItem = placement?.item ?? inside[0]!;
    const refPlacement = placement ?? placements.find((candidate) => candidate.item.id === refItem.id);
    const timelineTime = refPlacement
      ? refPlacement.offset + (splitSource - refItem.start) / itemSpeed(refItem)
      : splitSource;
    onNotify(
      `已在 ${formatClock(timelineTime)} 拆分${inside.length > 1 ? ` ${inside.length} 段片段` : '片段'}`,
    );
  };

  const splitItemAt = (placement: (typeof placements)[number], splitSource: number) => {
    splitClipsAt(splitSource, placement);
  };

  const openClipMenu = (event: ReactMouseEvent<HTMLButtonElement>, placement: (typeof placements)[number]) => {
    event.preventDefault();
    event.stopPropagation();
    contextMenuTriggerRef.current = event.currentTarget;
    onSelectItem(placement.item.id);
    const rect = event.currentTarget.getBoundingClientRect();
    const ratio = rect.width > 0 ? clamp((event.clientX - rect.left) / rect.width, 0, 1) : 0.5;
    setContextMenu({
      x: Math.min(event.clientX, window.innerWidth - 230),
      y: Math.min(event.clientY, window.innerHeight - 560),
      itemId: placement.item.id,
      sourceTime: placement.item.start + ratio * placement.sourceLength,
    });
  };

  const runContextAction = (action: () => void) => {
    closeClipMenu();
    action();
  };

  const trackMenuTitle = (trackNumber: number) => {
    const track = tracks.find((candidate) => candidate.number === trackNumber);
    if (!track) return '轨道';
    return `${track.kind === 'video' ? '视频轨' : '音频轨'} ${track.id} · ${track.name}`;
  };

  const buildTrackMenuItems = (trackNumber: number): ContextMenuItem[] => {
    const track = tracks.find((candidate) => candidate.number === trackNumber);
    if (!track) return [];
    const items: ContextMenuItem[] = [];
    if (track.kind === 'video') {
      items.push({
        kind: 'item',
        label: '隐藏该轨道',
        checked: hiddenTracks.includes(track.number),
        onSelect: () => onToggleTrackHidden(track.number),
      });
      items.push({
        kind: 'item',
        label: '锁定该轨道',
        checked: lockedTracks.includes(track.number),
        onSelect: () => onToggleTrackLocked(track.number),
      });
      items.push({ kind: 'heading', label: '轨道不透明度' });
      const current = Math.round((trackOpacities?.[track.number - 1] ?? 1) * 100);
      [100, 75, 50, 25, 0].forEach((percent) => {
        items.push({
          kind: 'item',
          label: `${percent}%`,
          checked: current === percent,
          disabled: !onSetTrackOpacity,
          disabledReason: '尚未接入轨道不透明度写入',
          onSelect: () => onSetTrackOpacity?.(track.number - 1, percent / 100),
        });
      });
      items.push({
        kind: 'item',
        label: '视频轨属性…',
        disabled: !onOpenTrackProperties,
        disabledReason: '尚未接入轨道属性面板',
        onSelect: () => onOpenTrackProperties?.(),
      });
    } else {
      items.push({
        kind: 'item',
        label: '静音该轨道',
        checked: mutedTracks.includes(track.number),
        onSelect: () => onToggleTrackMute(track.number),
      });
      items.push({
        kind: 'item',
        label: '独奏该轨道',
        checked: soloedTracks.includes(track.number),
        onSelect: () => onToggleTrackSolo(track.number),
      });
      items.push({
        kind: 'item',
        label: '锁定该轨道',
        checked: lockedTracks.includes(track.number),
        onSelect: () => onToggleTrackLocked(track.number),
      });
      items.push({ kind: 'separator' });
      items.push({ kind: 'heading', label: '轨道不透明度' });
      items.push({
        kind: 'item',
        label: '不透明度（仅视频轨）',
        disabled: true,
        disabledReason: '音频轨没有不透明度属性',
      });
    }
    items.push({ kind: 'separator' });
    items.push({
      kind: 'item',
      label: '向上移动轨道',
      disabled: true,
      disabledReason: '当前版本轨道顺序固定为 V3 / V2 / V1 / A1–A3，不支持移动轨道',
    });
    items.push({
      kind: 'item',
      label: '向下移动轨道',
      disabled: true,
      disabledReason: '当前版本轨道顺序固定为 V3 / V2 / V1 / A1–A3，不支持移动轨道',
    });
    return items;
  };

  const buildRulerMenuItems = (time: number): ContextMenuItem[] => {
    const sortedMarkers = [...markers].sort((a, b) => a - b);
    const previousMarker = [...sortedMarkers].reverse().find((marker) => marker < time - 0.001);
    const nextMarker = sortedMarkers.find((marker) => marker > time + 0.001);
    return [
      { kind: 'item', label: '在此处添加标记', onSelect: () => onAddMarker(time) },
      { kind: 'item', label: '在播放头添加标记', shortcut: 'M', onSelect: () => onAddMarker() },
      {
        kind: 'item',
        label: '清除全部标记',
        disabled: markers.length === 0,
        disabledReason: '时间线上还没有标记',
        onSelect: () => onClearMarkers?.(),
      },
      { kind: 'separator' },
      {
        kind: 'item',
        label: '跳到上一标记',
        disabled: previousMarker === undefined,
        disabledReason: '播放头前面没有标记',
        onSelect: () => {
          if (previousMarker !== undefined) onSeek(previousMarker);
        },
      },
      {
        kind: 'item',
        label: '跳到下一标记',
        disabled: nextMarker === undefined,
        disabledReason: '播放头后面没有标记',
        onSelect: () => {
          if (nextMarker !== undefined) onSeek(nextMarker);
        },
      },
      { kind: 'separator' },
      {
        kind: 'item',
        label: '吸附',
        shortcut: 'S',
        checked: snapEnabled,
        onSelect: onToggleSnap,
      },
    ];
  };

  const adjustGap = (seconds: number) => {
    const targetId = contextMenu?.itemId ?? selectedItemId;
    const target = targetId ? items.find((item) => item.id === targetId) : undefined;
    if (!target) {
      onNotify('先选中一个片段，再调整空隙');
      return;
    }
    const nextGap = Math.max(0, Number(((target.gapBefore ?? 0) + seconds).toFixed(2)));
    onUpdateItems(
      items.map((item) => (item.id === target.id ? { ...item, gapBefore: nextGap } : item)),
      nextGap > 0 ? '增加片段前置空隙' : '清除片段前置空隙',
    );
    onNotify(nextGap > 0 ? `前置空隙已设为 ${nextGap.toFixed(1)} 秒` : '已清除前置空隙');
  };

  const selectedItem = selectedItemId ? items.find((item) => item.id === selectedItemId) : undefined;
  const selectedIndex = selectedItem ? items.findIndex((item) => item.id === selectedItem.id) : -1;

  const trackName = (track: number) => {
    if (track === 1) return 'V1 主画面';
    if (track === 2) return 'V2 叠加轨';
    if (track === 3) return 'V3 图形轨';
    if (track === 4) return 'A1 人声';
    if (track === 5) return 'A2 音乐';
    return 'A3 音效';
  };

  const linkSelectedToCounterpart = () => {
    if (!selectedItem) {
      onNotify('先选中一个片段，再链接音视频');
      return;
    }
    if (selectedItem.linkGroup) {
      onNotify('当前片段已经链接，先解除链接再重新配对');
      return;
    }
    const selectedTrack = trackOf(selectedItem);
    const counterpartTrack = selectedTrack <= 3 ? [4, 5, 6] : [1, 2, 3];
    const counterpart = items.find(
      (item) =>
        counterpartTrack.includes(trackOf(item)) &&
        !item.linkGroup &&
        item.clipId === selectedItem.clipId &&
        Math.min(item.end, selectedItem.end) - Math.max(item.start, selectedItem.start) > 0.001,
    );
    if (!counterpart) {
      onNotify('同一素材上找不到可链接的画面/音频片段');
      return;
    }
    const group = `link-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`;
    onUpdateItems(
      items.map((item) =>
        item.id === selectedItem.id || item.id === counterpart.id
          ? { ...item, linkGroup: group }
          : item,
      ),
      '链接音视频片段',
    );
    onNotify('已链接同源画面与音频，拖动或裁剪会保持同步');
  };

  const detachSelectedAudio = () => {
    if (!selectedItem) {
      onNotify('先选中一个画面片段，再分离音频');
      return;
    }
    if (trackOf(selectedItem) > 3) {
      onNotify('音频片段已经是独立轨道，不需要再分离');
      return;
    }
    if (selectedItem.audioDetached) {
      onNotify('这个片段的音频已经分离到 A1');
      return;
    }
    const group = `link-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`;
    const audioId = `${selectedItem.id}-audio-${Date.now().toString(36)}`;
    const audioItem: TimelineItem = {
      ...selectedItem,
      id: audioId,
      track: 4,
      gapBefore: 0,
      linkGroup: group,
      audioDetached: false,
    };
    const detachedVideo = items.map((item) =>
      item.id === selectedItem.id ? { ...item, audioDetached: true, linkGroup: group } : item,
    );
    const withAudio = [...detachedVideo, audioItem];
    const next = moveItemToTime(
      withAudio,
      audioId,
      itemSequenceStart(withAudio, selectedItem.id, transitions, fps, sourceDurations),
      4,
    );
    onUpdateItems(next, '分离音视频');
    onSelectItem(audioId);
    onNotify('已将音频分离到 A1，画面与音频保持链接');
  };

  /** 为已经存在的画面片段补建匹配音频（素材放入前就在时间线上的片段用得上）。 */
  const attachCompanionAudio = () => {
    if (!selectedItem) {
      onNotify('先选中一个画面片段，再匹配音频');
      return;
    }
    if (trackOf(selectedItem) > 3) {
      onNotify('音频片段不需要再匹配音频');
      return;
    }
    if (selectedItem.audioDetached) {
      onNotify('这个片段已经带匹配音频了');
      return;
    }
    const audioTrack = audioTrackForVideoTrack(trackOf(selectedItem));
    if (lockedTracks.includes(audioTrack)) {
      onNotify(`${trackName(audioTrack)}已锁定，请先解锁轨道再匹配音频`);
      return;
    }
    const { video, audio } = linkAudioCompanion(selectedItem, audioTrack);
    const withCompanion = items.map((entry) => (entry.id === selectedItem.id ? video : entry));
    const sequenceStart = itemSequenceStart(items, selectedItem.id, transitions, fps, sourceDurations);
    const positioned = moveItemToTime([...withCompanion, audio], audio.id, sequenceStart, audioTrack);
    onUpdateItems(positioned, '匹配音频片段');
    onNotify(`已为片段补建匹配音频到 ${trackName(audioTrack)}`);
  };

  const unlinkSelected = () => {    if (!selectedItem?.linkGroup) {
      onNotify('当前片段没有链接');
      return;
    }
    const group = selectedItem.linkGroup;
    onUpdateItems(
      items.map((item) => (item.linkGroup === group ? { ...item, linkGroup: null } : item)),
      '解除音视频链接',
    );
    onNotify('已解除链接，画面与音频可以独立编辑');
  };

  const moveSelectedToTrack = (track: number) => {
    if (!selectedItem) {
      onNotify('先选中一个片段，再移动到轨道');
      return;
    }
    if (trackOf(selectedItem) === track) {
      onNotify(`片段已经在 ${trackName(track)}`);
      return;
    }
    if ((trackOf(selectedItem) >= 4) !== (track >= 4)) {
      onNotify(trackOf(selectedItem) >= 4 ? '音频片段只能移动到 A1–A3' : '画面片段请先用“分离音频”再移到音频轨');
      return;
    }
    onUpdateItems(
      items.map((item) => (item.id === selectedItem.id ? { ...item, track, gapBefore: 0 } : item)),
      `移动片段到 ${trackName(track)}`,
    );
    onNotify(`已移动片段到 ${trackName(track)}`);
  };

  const deleteSelected = () => {
    // P2（task-128）：删除优先使用框选集合；同时保持 linkGroup 成组删除与一次 undo。
    const baseIds = marqueeIds.length > 0 ? marqueeIds : selectedItem ? [selectedItem.id] : [];
    if (!baseIds.length) {
      onNotify('先选中一个片段，再执行波纹删除');
      return;
    }
    const removeIds = new Set<string>();
    for (const id of baseIds) {
      const item = items.find((candidate) => candidate.id === id);
      if (!item) continue;
      removeIds.add(item.id);
      if (item.linkGroup) {
        for (const linked of items) {
          if (linked.linkGroup === item.linkGroup) removeIds.add(linked.id);
        }
      }
    }
    if (!removeIds.size) return;
    if (items.length - removeIds.size <= 0) {
      // task-144：后端 Validate 要求 1–200 段，删空会 PUT 400；UI 直接拦截。
      onNotify('工程至少保留 1 个片段');
      return;
    }
    onUpdateItems(
      items.filter((item) => !removeIds.has(item.id)),
      removeIds.size > 1 ? '波纹删除选中片段' : '波纹删除片段',
    );
    onSelectItem(null);
    setMarqueeIds([]);
    onNotify(
      removeIds.size > 1
        ? `已波纹删除 ${removeIds.size} 个片段，后续片段自动闭合`
        : baseIds.some((id) => items.find((item) => item.id === id)?.linkGroup)
          ? '已波纹删除链接片段'
          : '已波纹删除片段，后续片段自动闭合',
    );
  };

  // FilmCraft 式 gap 操作：点空隙选中它，Delete 直接闭合（一次 undo）。
  const closeGap = (itemId: string, gap: number) => {
    const target = items.find((candidate) => candidate.id === itemId);
    if (!target) return;
    const current = Math.max(0, target.gapBefore ?? 0);
    if (current <= 0.001) {
      onNotify('该空隙已闭合');
      return;
    }
    const closed = Math.min(current, Math.max(0, gap));
    const next = items.map((item) =>
      item.id === itemId ? { ...item, gapBefore: Math.max(0, roundFrame(current - closed)) } : item,
    );
    onUpdateItems(next, '闭合空隙');
    onNotify(`已闭合 ${closed.toFixed(2)}s 空隙`);
  };
  const closeSelectedGap = () => {
    if (!selectedGap) return;
    closeGap(selectedGap.itemId, selectedGap.gap);
    setSelectedGap(null);
  };

  /** Ctrl+A：选中当前序列全部片段（FilmCraft 的 All 选择来源）。 */
  const selectAllItems = () => {
    if (placements.length === 0) {
      onNotify('时间线还没有片段');
      return;
    }
    const ids = placements.map((placement) => placement.item.id);
    setMarqueeIds(ids);
    setSelectedGap(null);
    onSelectItem(ids[ids.length - 1] ?? null);
    onNotify(`已选 ${ids.length} 个片段`);
  };

  const buildGapMenuItems = (entry: { track: number; itemId: string; gap: number }): ContextMenuItem[] => {
    const lane = placements.filter((placement) => placement.track === entry.track);
    const index = lane.findIndex((placement) => placement.item.id === entry.itemId);
    const nextPlacement = index >= 0 ? lane[index + 1] : undefined;
    return [
      { kind: 'heading', label: `${trackName(entry.track)} 空隙 ${formatClock(entry.gap)}` },
      {
        kind: 'item',
        label: '闭合空隙',
        shortcut: 'Delete',
        onSelect: () => closeGap(entry.itemId, entry.gap),
      },
      {
        kind: 'item',
        label: '闭合该轨道全部空隙',
        onSelect: () => {
          const laneIds = new Set(lane.map((placement) => placement.item.id));
          const nextItems = items.map((item) => (laneIds.has(item.id) ? { ...item, gapBefore: 0 } : item));
          onUpdateItems(nextItems, '闭合轨道空隙');
          onNotify(`已闭合 ${trackName(entry.track)} 的全部空隙`);
        },
      },
      { kind: 'separator' },
      {
        kind: 'item',
        label: '选中空隙后的片段',
        disabled: !nextPlacement,
        disabledReason: '该空隙后面没有片段',
        onSelect: () => {
          if (!nextPlacement) return;
          onSelectItem(nextPlacement.item.id);
          onSeek(nextPlacement.offset + 0.01);
        },
      },
    ];
  };

  const copySelected = (cut = false) => {
    if (!selectedItem) {
      onNotify('先选中一个片段，再复制');
      return;
    }
    clipboardRef.current = { ...selectedItem };
    if (cut) {
      onUpdateItems(items.filter((item) => item.id !== selectedItem.id), '剪切片段');
      onSelectItem(null);
      onNotify('已剪切片段');
    } else {
      onNotify('已复制片段');
    }
  };

  const insertAfterSelection = (source: TimelineItem, label: string) => {
    const copy: TimelineItem = {
      ...source,
      id: `${source.id}-copy-${Date.now().toString(36)}`,
    };
    const insertAt = selectedIndex >= 0 ? selectedIndex + 1 : items.length;
    const next = [...items.slice(0, insertAt), copy, ...items.slice(insertAt)];
    onUpdateItems(next, label);
    onSelectItem(copy.id);
    onNotify(label);
  };

  const addItemToTarget = (source: TimelineItem, mode: 'insert' | 'overwrite' = 'insert'): boolean => {    if (readOnly) {
      onNotify('只读预览：不能插入或覆盖片段');
      return false;
    }
    const track = targetTrackFor(source);
    if (lockedTracks.includes(track)) {
      onNotify(`${trackName(track)}已锁定，请先解锁或切换目标轨`);
      return false;
    }
    const next = mode === 'overwrite'
      ? overwriteItemOnTrack(items, source, track, sequenceTime)
      : insertItemOnTrack(items, source, track, sequenceTime);
    const prefix = `${source.id}-${mode}-`;
    const created = next.find((item) => item.id.startsWith(prefix));
    onUpdateItems(next, `${mode === 'overwrite' ? '覆盖' : '插入'}到 ${trackName(track)}`);
    if (created) onSelectItem(created.id);
    onNotify(`已${mode === 'overwrite' ? '覆盖' : '插入'}到 ${trackName(track)} · ${formatTimecode(sequenceTime)}`);
    return true;
  };

  /** 素材库拖放落点：显式轨道 + 显式序列时间（overwrite 不推动下游，insert 会推开）。 */
  const addAssetToTrack = (
    source: TimelineItem,
    track: number,
    time: number,
    mode: 'insert' | 'overwrite' = 'insert',
    label?: string,
  ): boolean => {
    if (readOnly) {
      onNotify('只读预览：不能插入或覆盖片段');
      return false;
    }
    if (lockedTracks.includes(track)) {
      onNotify(`${trackName(track)}已锁定，请先解锁轨道再放入素材`);
      return false;
    }
    const at = Math.max(0, roundFrame(time));
    const next = mode === 'overwrite'
      ? overwriteItemOnTrack(items, source, track, at)
      : insertItemOnTrack(items, source, track, at);
    const prefix = `${source.id}-${mode}-`;
    const created = next.find((item) => item.id.startsWith(prefix));
    onUpdateItems(next, label ?? `放入素材到 ${trackName(track)}`);
    if (created) onSelectItem(created.id);
    onNotify(`已放入 ${trackName(track)} · ${formatTimecode(at)}`);
    return true;
  };

  /**
   * 素材库/素材「加入时间线」：放入视频片段的同时建一条匹配音频片段。
   * FilmCraft 落带音频的片段会自动生成配对音轨——这样音频轨上立刻能看到波形与音量线。
   */
  const addAssetWithAudioAt = (
    video: TimelineItem,
    audio: TimelineItem,
    videoTrack: number,
    audioTrack: number,
    time: number,
    mode: 'insert' | 'overwrite' = 'insert',
    label?: string,
  ): boolean => {
    if (readOnly) {
      onNotify('只读预览：不能插入或覆盖片段');
      return false;
    }
    const locked = [videoTrack, audioTrack].find((track) => lockedTracks.includes(track));
    if (locked !== undefined) {
      onNotify(`${trackName(locked)}已锁定，请先解锁轨道再放入素材`);
      return false;
    }
    const at = Math.max(0, roundFrame(time));
    const withVideo = mode === 'overwrite'
      ? overwriteItemOnTrack(items, video, videoTrack, at)
      : insertItemOnTrack(items, video, videoTrack, at);
    const next = mode === 'overwrite'
      ? overwriteItemOnTrack(withVideo, audio, audioTrack, at)
      : insertItemOnTrack(withVideo, audio, audioTrack, at);
    const videoCreated = next.find((item) => item.id.startsWith(`${video.id}-${mode}-`));
    const audioCreated = next.find((item) => item.id.startsWith(`${audio.id}-${mode}-`));
    onUpdateItems(next, label ?? `放入素材到 ${trackName(videoTrack)} + 匹配音频 ${trackName(audioTrack)}`);
    if (videoCreated) onSelectItem(videoCreated.id);
    onNotify(
      `已放入 ${trackName(videoTrack)}${audioCreated ? `，并自动匹配音频到 ${trackName(audioTrack)}` : ''} · ${formatTimecode(at)}`,
    );
    return true;
  };

  const duplicateSelected = () => {
    if (!selectedItem) {
      onNotify('先选中一个片段，再复制片段');
      return;
    }
    insertAfterSelection(selectedItem, '已复制片段到下一段');
  };

  const toggleDisabled = () => {
    if (!selectedItem) {
      onNotify('先选中一个片段，再启用或禁用');
      return;
    }
    const next = !selectedItem.disabled;
    onUpdateItems(
      items.map((item) => (item.id === selectedItem.id ? { ...item, disabled: next } : item)),
      next ? '禁用片段' : '启用片段',
    );
    onNotify(next ? '片段已禁用，保留时间长度并输出黑场静音' : '片段已启用');
  };

  const updateVolumeKeyframes = (item: TimelineItem, points: VolumeKeyframe[], label: string) => {
    onUpdateItems(
      items.map((entry) =>
        entry.id === item.id ? { ...entry, volumeKeyframes: points.length > 0 ? points : null } : entry,
      ),
      label,
    );
  };

  const addVolumeKeyframeAtSequence = (itemId: string, sequence: number) => {
    const item = items.find((entry) => entry.id === itemId);
    const placement = placements.find((entry) => entry.item.id === itemId);
    if (!item || !placement) return;
    const outputDuration = Math.max(0.001, itemOutputDuration(item));
    const localTime = Math.min(outputDuration, Math.max(0, sequence - placement.offset));
    const points = normalizeVolumeKeyframes(item);
    const db = envelopeDbAt(item, localTime);
    const next = normalizeVolumeKeyframes({
      ...item,
      volumeKeyframes: [...points.filter((point) => Math.abs(point.time - localTime) > 0.02), { time: localTime, db }],
    });
    updateVolumeKeyframes(item, next, '添加音量关键帧');
    onNotify(
      `已在 ${formatClock(placement.offset + localTime)} 添加关键帧（片段内 ${localTime.toFixed(2)} 秒，${db.toFixed(1)} dB）`,
    );
  };

  const addVolumeKeyframeAtPlayhead = (itemId: string) => {
    addVolumeKeyframeAtSequence(itemId, sequenceTime);
  };

  const clearVolumeEnvelope = (itemId: string) => {
    const item = items.find((entry) => entry.id === itemId);
    if (!item) return;
    updateVolumeKeyframes(item, [], '清除音量包络');
    onNotify('已清除该片段的音量包络');
  };

  const envelopeItemFor = (itemId: string) => items.find((entry) => entry.id === itemId) ?? null;

  const selectedEnvelopePoints = () => {
    if (!selectedEnvelope) return null;
    const item = envelopeItemFor(selectedEnvelope.itemId);
    if (!item) return null;
    const points = normalizeVolumeKeyframes(item);
    if (!points[selectedEnvelope.index]) return null;
    return { item, points };
  };

  const nudgeEnvelopePoint = (axis: 'db' | 'time', direction: -1 | 1, fast: boolean) => {
    const found = selectedEnvelopePoints();
    if (!found) {
      onNotify('先在时间线上点选一个音量关键帧');
      return;
    }
    const { item } = found;
    const points = found.points.map((point) => ({ ...point }));
    const point = points[selectedEnvelope!.index];
    const duration = Math.max(0.001, itemOutputDuration(item));
    if (axis === 'db') {
      const step = fast ? 5 : 1;
      point.db = Math.round(clamp(point.db + direction * step, -60, 12) * 10) / 10;
    } else {
      const step = (fast ? Math.max(1, Math.round(fps)) : 1) / Math.max(1, fps);
      point.time = Math.round(clamp(point.time + direction * step, 0, duration) * fps) / fps;
    }
    updateVolumeKeyframes(
      item,
      normalizeVolumeKeyframes({ ...item, volumeKeyframes: points }),
      axis === 'db' ? '微调音量关键帧增益' : '微调音量关键帧时间',
    );
    onNotify(`音量关键帧：${point.time.toFixed(2)} 秒 ${point.db.toFixed(1)} dB`);
  };

  const removeSelectedEnvelopePoint = () => {
    const found = selectedEnvelopePoints();
    if (!found) return;
    const { item } = found;
    const points = found.points.filter((_, index) => index !== selectedEnvelope!.index);
    updateVolumeKeyframes(item, normalizeVolumeKeyframes({ ...item, volumeKeyframes: points }), '删除音量关键帧');
    onNotify('已删除音量关键帧');
    setSelectedEnvelope(null);
    setEnvelopeMenu(null);
  };

  const setEnvelopePointValue = (mode: 'zero' | 'playhead') => {
    const found = selectedEnvelopePoints();
    if (!found) return;
    const { item } = found;
    const points = found.points.map((point) => ({ ...point }));
    const point = points[selectedEnvelope!.index];
    if (mode === 'zero') {
      point.db = 0;
      updateVolumeKeyframes(item, normalizeVolumeKeyframes({ ...item, volumeKeyframes: points }), '音量关键帧归零');
      onNotify('音量关键帧已重置为 0 dB');
    } else {
      const duration = Math.max(0.001, itemOutputDuration(item));
      const starts = sequenceLayout(items, transitions, fps).starts;
      const offset = starts.get(item.id) ?? 0;
      point.time = Math.round(clamp(sequenceTime - offset, 0, duration) * fps) / fps;
      point.db = envelopeDbAt({ ...item, volumeKeyframes: points }, point.time);
      updateVolumeKeyframes(item, normalizeVolumeKeyframes({ ...item, volumeKeyframes: points }), '音量关键帧移到播放头');
      onNotify(`音量关键帧已移动到播放头（${point.time.toFixed(2)} 秒）`);
    }
    setEnvelopeMenu(null);
  };

  const envelopeDbToY = (db: number) => 100 - ((Math.min(12, Math.max(-60, db)) + 60) / 72) * 100;

  const envelopePointsFor = (item: TimelineItem): VolumeKeyframe[] => {
    if (envelopePreview && envelopePreview.itemId === item.id) return envelopePreview.points;
    return normalizeVolumeKeyframes(item);
  };

  const buildEnvelopePolyline = (points: VolumeKeyframe[], duration: number): string => {
    const sorted = [...points].sort((left, right) => left.time - right.time);
    if (sorted.length === 0) return '';
    const steps: Array<{ ratio: number; db: number }> = [{ ratio: 0, db: sorted[0].db }];
    for (const point of sorted) {
      steps.push({ ratio: Math.min(1, Math.max(0, point.time / Math.max(0.001, duration))), db: point.db });
    }
    steps.push({ ratio: 1, db: sorted[sorted.length - 1].db });
    return steps
      .map((step) => `${(step.ratio * 100).toFixed(2)},${envelopeDbToY(step.db).toFixed(2)}`)
      .join(' ');
  };

  const envelopeDbFromPointer = (clientY: number, rect: { top: number; height: number }) =>
    Math.round(
      Math.min(12, Math.max(-60, 12 - ((clientY - rect.top) / Math.max(1, rect.height)) * 72)) * 10,
    ) / 10;

  /** 片段上所有变换参数关键帧，按时间去重合并，供时间线菱形条渲染。 */
  const keyframeMarksFor = (
    item: TimelineItem,
  ): Array<{ time: number; properties: ParamKeyframeProperty[] }> => {
    const duration = Math.max(0.001, itemOutputDuration(item));
    const byTime = new Map<number, ParamKeyframeProperty[]>();
    for (const prop of PARAM_KEYFRAME_PROPS) {
      for (const point of paramKeyframesFor(item, prop)) {
        const time = Math.round(Math.min(duration, Math.max(0, point.time)) * 1000) / 1000;
        const list = byTime.get(time) ?? [];
        list.push(prop);
        byTime.set(time, list);
      }
    }
    return [...byTime.entries()]
      .map(([time, properties]) => ({ time, properties }))
      .sort((left, right) => left.time - right.time);
  };

  const finishKeyframeMarkDrag = (
    drag: {
      itemId: string;
      startTime: number;
      time: number;
      duration: number;
      offset: number;
      rectLeft: number;
      rectWidth: number;
      moved: boolean;
    },
    canceled = false,
  ) => {
    setSnapGuide(null);
    setKeyframeMarkDrag(null);
    if (canceled) return;
    if (!drag.moved) {
      onSeek(drag.offset + drag.startTime);
      return;
    }
    const item = items.find((entry) => entry.id === drag.itemId);
    if (!item) return;
    let touched = false;
    const keyframes: Partial<Record<ParamKeyframeProperty, Array<{ time: number; value: number }>>> = {
      ...(item.keyframes ?? {}),
    };
    for (const prop of PARAM_KEYFRAME_PROPS) {
      const points = paramKeyframesFor(item, prop);
      if (!points.some((point) => Math.abs(point.time - drag.startTime) <= 0.02)) continue;
      keyframes[prop] = points.map((point) =>
        Math.abs(point.time - drag.startTime) <= 0.02 ? { ...point, time: drag.time } : point,
      );
      touched = true;
    }
    if (!touched) return;
    onUpdateItems(
      items.map((entry) => (entry.id === item.id ? { ...entry, keyframes } : entry)),
      '移动参数关键帧',
    );
    onNotify(
      `关键帧 ${drag.startTime.toFixed(2)} 秒移到 ${drag.time.toFixed(2)} 秒（${drag.duration.toFixed(2)} 秒片段内）`,
    );
  };

  // FilmCraft 式「音频片段音量线」：直接在片段上上下拖线改音量（dB 提示、波形幅度跟随），
  // 一次手势一条 undo；有关键帧时整段音量整体平移（近似 FC 的两侧关键帧联动）。
  const beginGainDrag = (event: ReactPointerEvent<HTMLElement>, item: TimelineItem) => {
    if (event.button !== 0) return;
    if (readOnly) {
      onNotify('只读预览：不能调整片段音量');
      return;
    }
    const track = trackOf(item);
    if (lockedTracks.includes(track)) {
      onNotify(`${trackName(track)} 已锁定：先解锁轨道再调整音量`);
      return;
    }
    const clipEl = event.currentTarget.closest('.timeline-clip') as HTMLElement | null;
    if (!clipEl) return;
    event.preventDefault();
    event.stopPropagation();
    const rect = clipEl.getBoundingClientRect();
    const bandHeight = Math.max(6, rect.height - 22);
    const baseDb = clampGainDb(item.volumeDb ?? 0);
    const keys = normalizeVolumeKeyframes(item).length > 0;
    gainCleanupRef.current?.();
    setGainDrag({
      itemId: item.id,
      baseDb,
      startY: event.clientY,
      bandHeight,
      db: baseDb,
      clientX: event.clientX,
      clientY: event.clientY,
      keys,
    });
    const onMove = (moveEvent: PointerEvent) => {
      const current = gainDragRef.current;
      if (!current) return;
      const raw = current.baseDb + ((current.startY - moveEvent.clientY) / current.bandHeight) * GAIN_SPAN_DB;
      const snapped = Math.abs(raw) <= (4 / current.bandHeight) * GAIN_SPAN_DB ? 0 : raw;
      const db = clampGainDb(Math.round(snapped * 100) / 100);
      setGainDrag({ ...current, db, clientX: moveEvent.clientX, clientY: moveEvent.clientY });
    };
    const cleanup = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onCancel);
      if (gainCleanupRef.current === cleanup) gainCleanupRef.current = null;
    };
    const finish = (canceled: boolean) => {
      cleanup();
      const current = gainDragRef.current;
      gainDragRef.current = null;
      setGainDrag(null);
      if (!current || canceled) return;
      if (Math.abs(current.db - current.baseDb) < 0.05) {
        onNotify(`片段音量保持 ${formatGainDb(current.db)}`);
        return;
      }
      const delta = current.db - current.baseDb;
      onUpdateItems(
        items.map((entry) => {
          if (entry.id !== current.itemId) return entry;
          if (current.keys) {
            const points = normalizeVolumeKeyframes(entry).map((point) => ({
              ...point,
              db: clampGainDb(Math.round((point.db + delta) * 100) / 100),
            }));
            return { ...entry, volumeKeyframes: points };
          }
          return { ...entry, volumeDb: Math.round(current.db * 100) / 100 };
        }),
        current.keys ? '调整整段音量（关键帧整体平移）' : '调整片段音量',
      );
      onNotify(current.keys ? `整段音量 ${formatGainDb(current.baseDb)} → ${formatGainDb(current.db)}` : `片段音量 ${formatGainDb(current.db)}`);
    };
    function onUp() {
      finish(false);
    }
    function onCancel() {
      finish(true);
    }
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onCancel);
    gainCleanupRef.current = cleanup;
  };

  const beginKeyframeMarkDrag = (
    event: ReactPointerEvent<HTMLButtonElement>,
    itemId: string,
    markTime: number,
    offset: number,
  ) => {
    event.stopPropagation();
    event.preventDefault();
    const row = event.currentTarget.parentElement;
    const item = items.find((entry) => entry.id === itemId);
    if (!row || !item) return;
    const rect = row.getBoundingClientRect();
    const duration = Math.max(0.001, itemOutputDuration(item));
    const dragState = {
      itemId,
      startTime: markTime,
      time: markTime,
      duration,
      offset,
      rectLeft: rect.left,
      rectWidth: Math.max(1, rect.width),
      moved: false,
    };
    onSelectItem(itemId);
    setSnapGuide(null);
    setKeyframeMarkDrag(dragState);
    let latest = dragState;

    const onMove = (moveEvent: PointerEvent) => {
      const current = keyframeMarkDragRef.current;
      if (!current) return;
      const rawTime = ((moveEvent.clientX - current.rectLeft) / current.rectWidth) * current.duration;
      const baseTime = clamp(Math.round(rawTime * fps) / fps, 0, current.duration);
      const snapped = snapKeyframeSequence(current.offset + baseTime, current.rectWidth, current.duration);
      const time = clamp(snapped.value - current.offset, 0, current.duration);
      setSnapGuide(snapped.hit);
      const moved = current.moved || Math.abs(time - current.startTime) * (current.rectWidth / current.duration) > 2;
      latest = { ...current, time, moved };
      setKeyframeMarkDrag(latest);
    };
    const onUp = () => {
      const current = keyframeMarkDragRef.current;
      cleanup();
      if (current) finishKeyframeMarkDrag(latest.moved ? latest : { ...current, moved: latest.moved, time: latest.time });
    };
    const onCancel = () => {
      cleanup();
      finishKeyframeMarkDrag(latest, true);
    };
    const cleanup = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onCancel);
      if (envelopeCleanupRef.current === cleanup) envelopeCleanupRef.current = null;
    };
    envelopeCleanupRef.current?.();
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onCancel);
    envelopeCleanupRef.current = cleanup;
  };

  const addKeyframeAtPlayhead = () => {
    const item = selectedItem;
    if (!item) {
      onNotify('先在时间线上选中一个片段，再按 K 打关键帧');
      return;
    }
    const placement = placements.find((entry) => entry.item.id === item.id);
    const duration = Math.max(0.001, itemOutputDuration(item));
    const localTime = clamp(sequenceTime - (placement?.offset ?? 0), 0, duration);
    const animated = PARAM_KEYFRAME_PROPS.filter((prop) => paramKeyframesFor(item, prop).length > 0);
    if (animated.length === 0) {
      onNotify('该片段还没有启用关键帧的参数：先在效果控件点秒表启用缩放/旋转/位置/不透明度');
      return;
    }
    const keyframes: Partial<Record<ParamKeyframeProperty, Array<{ time: number; value: number }>>> = {
      ...(item.keyframes ?? {}),
    };
    for (const prop of animated) {
      const points = paramKeyframesFor(item, prop);
      const value = paramValueAtItem(item, prop, localTime);
      keyframes[prop] = normalizeParamKeyframes(
        prop,
        [...points.filter((point) => Math.abs(point.time - localTime) > 0.02), { time: localTime, value }],
        duration,
      );
    }
    onUpdateItems(
      items.map((entry) => (entry.id === item.id ? { ...entry, keyframes } : entry)),
      '在播放头位置添加参数关键帧',
    );
    onNotify(
      `已在 ${localTime.toFixed(2)} 秒处为 ${animated
        .map((prop) => PARAM_KEYFRAME_LABELS[prop])
        .join('、')} 添加关键帧`,
    );
  };

  const finishEnvelopePointDrag = (
    drag: {
      itemId: string;
      index: number;
      duration: number;
      offset: number;
      points: VolumeKeyframe[];
      rect: { left: number; top: number; width: number; height: number };
    },
    canceled = false,
  ) => {
    setSnapGuide(null);
    const item = items.find((entry) => entry.id === drag.itemId);
    const movedPoint = drag.points[drag.index];
    if (!canceled && item && movedPoint) {
      const next = normalizeVolumeKeyframes({ ...item, volumeKeyframes: drag.points });
      updateVolumeKeyframes(item, next, '调整音量包络');
      onNotify(`音量关键帧：${movedPoint.time.toFixed(2)} 秒 ${movedPoint.db.toFixed(1)} dB`);
    }
    setEnvelopeDrag(null);
    setEnvelopePreview(null);
  };

  const beginEnvelopePointDrag = (
    event: ReactPointerEvent<HTMLButtonElement>,
    placement: { item: TimelineItem; length: number; offset: number },
    index: number,
  ) => {
    event.stopPropagation();
    event.preventDefault();
    const overlay = event.currentTarget.closest('.timeline-envelope-editor') as HTMLElement | null;
    if (!overlay) return;
    const points = normalizeVolumeKeyframes(placement.item).map((point) => ({ ...point }));
    if (!points[index]) return;
    const rect = overlay.getBoundingClientRect();
    const dragState = {
      itemId: placement.item.id,
      index,
      duration: Math.max(0.001, itemOutputDuration(placement.item)),
      offset: placement.offset,
      points,
      rect: {
        left: rect.left,
        top: rect.top,
        width: Math.max(1, rect.width),
        height: Math.max(1, rect.height),
      },
    };
    onSelectItem(placement.item.id);
    setSelectedEnvelope({ itemId: placement.item.id, index });
    setSnapGuide(null);
    setEnvelopeDrag(dragState);
    setEnvelopePreview({ itemId: placement.item.id, points });

    const onMove = (moveEvent: PointerEvent) => {
      const current = envelopeDragRef.current;
      if (!current) return;
      const ratio = Math.min(1, Math.max(0, (moveEvent.clientX - current.rect.left) / current.rect.width));
      const rawTime = Math.round(ratio * current.duration * fps) / Math.max(1, fps);
      const snapped = snapKeyframeSequence(current.offset + rawTime, current.rect.width, current.duration);
      const time = clamp(snapped.value - current.offset, 0, current.duration);
      setSnapGuide(snapped.hit);
      const db = snapEnvelopeDb(envelopeDbFromPointer(moveEvent.clientY, current.rect), current.rect.height);
      const nextPoints = current.points.map((point, pointIndex) =>
        pointIndex === current.index ? { time, db } : point,
      );
      setEnvelopeDrag({ ...current, points: nextPoints });
      setEnvelopePreview({ itemId: current.itemId, points: nextPoints });
    };
    const onUp = () => {
      const current = envelopeDragRef.current;
      cleanup();
      if (current) finishEnvelopePointDrag(current);
    };
    const onCancel = () => {
      const current = envelopeDragRef.current;
      cleanup();
      if (current) finishEnvelopePointDrag(current, true);
    };
    const cleanup = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onCancel);
      if (envelopeCleanupRef.current === cleanup) envelopeCleanupRef.current = null;
    };
    envelopeCleanupRef.current?.();
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onCancel);
    envelopeCleanupRef.current = cleanup;
  };

  const finishEnvelopeLineDrag = (
    drag: {
      itemId: string;
      startY: number;
      basePoints: VolumeKeyframe[];
      rect: { top: number; height: number };
    },
    points: VolumeKeyframe[],
    canceled = false,
  ) => {
    const item = items.find((entry) => entry.id === drag.itemId);
    if (!canceled && item) {
      const next = normalizeVolumeKeyframes({ ...item, volumeKeyframes: points });
      updateVolumeKeyframes(item, next, '调整整条音量包络');
      const first = points[0];
      const delta = first ? first.db - (drag.basePoints[0]?.db ?? 0) : 0;
      onNotify(`整条包络 ${delta >= 0 ? '+' : ''}${delta.toFixed(1)} dB（共 ${next.length} 个关键帧）`);
    }
    setEnvelopeLineDrag(null);
    setEnvelopePreview(null);
  };

  const beginEnvelopeLineDrag = (
    event: ReactPointerEvent<SVGPolylineElement>,
    placement: { item: TimelineItem; length: number },
  ) => {
    event.stopPropagation();
    event.preventDefault();
    const points = normalizeVolumeKeyframes(placement.item).map((point) => ({ ...point }));
    if (points.length === 0) return;
    const overlay = event.currentTarget.closest('.timeline-envelope-editor') as HTMLElement | null;
    if (!overlay) return;
    const rect = overlay.getBoundingClientRect();
    const dragState = {
      itemId: placement.item.id,
      startY: event.clientY,
      basePoints: points,
      rect: { top: rect.top, height: Math.max(1, rect.height) },
    };
    onSelectItem(placement.item.id);
    setSelectedEnvelope(null);
    setEnvelopeLineDrag(dragState);
    setEnvelopePreview({ itemId: placement.item.id, points });
    let latest = points;

    const onMove = (moveEvent: PointerEvent) => {
      const current = envelopeLineDragRef.current;
      if (!current) return;
      const deltaDb = (-(moveEvent.clientY - current.startY) / current.rect.height) * 72;
      latest = current.basePoints.map((point) => ({
        time: point.time,
        db: Math.round(Math.min(12, Math.max(-60, point.db + deltaDb)) * 10) / 10,
      }));
      setEnvelopePreview({ itemId: current.itemId, points: latest });
    };
    const onUp = () => {
      const current = envelopeLineDragRef.current;
      cleanup();
      if (current) finishEnvelopeLineDrag(current, latest);
    };
    const onCancel = () => {
      const current = envelopeLineDragRef.current;
      cleanup();
      if (current) finishEnvelopeLineDrag(current, latest, true);
    };
    const cleanup = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onCancel);
      if (envelopeCleanupRef.current === cleanup) envelopeCleanupRef.current = null;
    };
    envelopeCleanupRef.current?.();
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onCancel);
    envelopeCleanupRef.current = cleanup;
  };

  const addEnvelopeKeyframeAtPoint = (
    event: ReactMouseEvent<Element>,
    placement: { item: TimelineItem; length: number },
    rectOverride?: DOMRect,
  ) => {
    if (envelopeDrag) return;
    const rect = rectOverride ?? event.currentTarget.getBoundingClientRect();
    const duration = Math.max(0.001, itemOutputDuration(placement.item));
    const ratio = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
    const db = envelopeDbFromPointer(event.clientY, rect);
    const points = normalizeVolumeKeyframes(placement.item);
    const next = normalizeVolumeKeyframes({
      ...placement.item,
      volumeKeyframes: [
        ...points.filter((point) => Math.abs(point.time - ratio * duration) > 0.02),
        { time: Math.round(ratio * duration * fps) / Math.max(1, fps), db },
      ],
    });
    updateVolumeKeyframes(placement.item, next, '添加音量关键帧');
    onNotify(`已在 ${(ratio * duration).toFixed(2)} 秒处添加音量关键帧（${db.toFixed(1)} dB）`);
  };

  const pasteSelected = (mode: 'insert' | 'overwrite' = 'insert') => {
    const source = clipboardRef.current;
    if (!source) {
      onNotify('剪贴板里还没有片段');
      return;
    }
    addItemToTarget(source, mode);
  };

  const slipSelected = (frames: number) => {
    if (!selectedItem) {
      onNotify('先选中一个片段，再滑移');
      return;
    }
    const length = selectedItem.end - selectedItem.start;
    const limit = Math.max(0, sourceDuration - length);
    const sourceDelta = (frames / Math.max(fps, 1)) * itemSpeed(selectedItem);
    const nextStart = clamp(selectedItem.start + sourceDelta, 0, limit);
    if (Math.abs(nextStart - selectedItem.start) < 0.0005) {
      onNotify(frames < 0 ? '已经到了素材入点' : '已经到了素材出点');
      return;
    }
    const nextEnd = nextStart + length;
    onUpdateItems(
      items.map((item) =>
        item.id === selectedItem.id ? { ...item, start: nextStart, end: nextEnd } : item,
      ),
      frames < 0 ? '向左滑移片段' : '向右滑移片段',
    );
    onNotify(`滑移 ${Math.abs(frames)} 帧，时间线位置不变`);
  };

  const rollSelected = (frames: number) => {
    if (!selectedItem || selectedIndex < 0) {
      onNotify('先选中一个片段，再滚动编辑');
      return;
    }
    const nextItem = items[selectedIndex + 1];
    if (!nextItem) {
      onNotify('当前片段后面没有相邻片段，无法滚动切点');
      return;
    }
    if (Math.abs(itemSpeed(selectedItem) - itemSpeed(nextItem)) > 0.001) {
      onNotify('不同速度的片段之间暂不支持滚动编辑');
      return;
    }
    const selectedLength = selectedItem.end - selectedItem.start;
    const nextLength = nextItem.end - nextItem.start;
    const lower = -Math.min(selectedLength - 0.1, nextItem.start);
    const upper = Math.min(nextLength - 0.1, sourceDuration - selectedItem.end);
    const delta = clamp(frames / Math.max(fps, 1), lower, upper);
    if (Math.abs(delta) < 0.0005) {
      onNotify('切点已到可移动范围的边缘');
      return;
    }
    onUpdateItems(
      items.map((item) => {
        if (item.id === selectedItem.id) return { ...item, end: item.end + delta };
        if (item.id === nextItem.id) return { ...item, start: item.start + delta };
        return item;
      }),
      delta < 0 ? '向左滚动切点' : '向右滚动切点',
    );
    onNotify(`切点移动 ${Math.round(Math.abs(delta) * fps)} 帧，总时长不变`);
  };

  const zoomBy = (delta: number) => {
    setZoom((current) => {
      const next = Math.round((current + delta) * 10) / 10;
      return Math.min(4, Math.max(1, next));
    });
  };

  // 缩放缓动：目标 zoom 变化时逐帧逼近，指针下的时间点保持不动（FilmCraft zoom_about 指针锚点）。
  useEffect(() => {
    let frame = 0;
    let last = performance.now();
    let display = zoomRenderRef.current;
    const step = (now: number) => {
      const dt = Math.min(0.05, (now - last) / 1000);
      last = now;
      const diff = zoom - display;
      display += diff * (1 - Math.exp(-dt * 20));
      if (Math.abs(zoom - display) < 0.002) display = zoom;
      setZoomRender(display);
      const anchor = zoomAnchorRef.current;
      const viewport = tracksViewportRef.current;
      if (anchor && viewport) {
        const contentWidth = viewport.clientWidth * display;
        const left = Math.max(
          0,
          (anchor.time / Math.max(0.001, total)) * contentWidth - anchor.offsetPx,
        );
        viewport.scrollLeft = left;
        if (rulerViewportRef.current) rulerViewportRef.current.scrollLeft = left;
        window.requestAnimationFrame(syncHScrollThumb);
      }
      if (display !== zoom) frame = window.requestAnimationFrame(step);
      else onZoomChange?.(Math.round(zoom * 100));
    };
    frame = window.requestAnimationFrame(step);
    return () => window.cancelAnimationFrame(frame);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [zoom, total]);

  // Ctrl/⌘ + 滚轮：以指针为锚点缩放，步进走同一缓动。
  useEffect(() => {
    const viewport = tracksViewportRef.current;
    if (!viewport) return undefined;
    const onWheel = (event: WheelEvent) => {
      if (!event.ctrlKey && !event.metaKey) return;
      event.preventDefault();
      const rect = viewport.getBoundingClientRect();
      const offsetPx = event.clientX - rect.left;
      const contentWidth = viewport.clientWidth * zoomRenderRef.current;
      const ratio = contentWidth > 0 ? (viewport.scrollLeft + offsetPx) / contentWidth : 0;
      zoomAnchorRef.current = { time: clamp(ratio, 0, 1) * total, offsetPx };
      setZoom((current) => Math.min(4, Math.max(1, current * Math.exp(-event.deltaY * 0.0015))));
    };
    viewport.addEventListener('wheel', onWheel, { passive: false });
    return () => viewport.removeEventListener('wheel', onWheel);
  }, [total]);

  useImperativeHandle(ref, () => ({
    copy: () => copySelected(),
    cut: () => copySelected(true),
    paste: pasteSelected,
    addItem: addItemToTarget,
    addAssetAt: addAssetToTrack,
    addAssetWithAudioAt,
    getWorkArea: () => inOutRef.current,
    setWorkArea: (edge, value) => {
      const limit = Math.max(0, totalDuration);
      const nextValue = value === null ? null : Math.min(limit, Math.max(0, value));
      const current = inOutRef.current;
      const next = { ...current, [edge]: nextValue };
      if (edge === 'in' && next.in !== null && next.out !== null && next.out <= next.in) next.out = null;
      if (edge === 'out' && next.in !== null && next.out !== null && next.in >= next.out) next.in = null;
      applyWorkArea(next);
      if (nextValue === null) {
        onNotify(edge === 'in' ? '已清除入点' : '已清除出点');
      } else {
        onNotify(`已设置${edge === 'in' ? '入点' : '出点'} ${formatTimecode(nextValue)}`);
      }
    },
    getTargetTracks: () => targetTracksRef.current,
    duplicate: duplicateSelected,
    deleteSelection: deleteSelected,
    slip: slipSelected,
    roll: rollSelected,
    toggleDisabled,
    zoomIn: () => zoomBy(0.25),
    zoomOut: () => zoomBy(-0.25),
    zoomReset: () => setZoom(1),
  }));

  useEffect(() => {
    const viewport = tracksViewportRef.current;
    if (!viewport) return;
    const update = () => {
      setHScrollable(viewport.scrollWidth > viewport.clientWidth + 1);
      requestAnimationFrame(syncHScrollThumb);
    };
    update();
    const observer = new ResizeObserver(update);
    observer.observe(viewport);
    return () => observer.disconnect();
  }, [zoom, totalDuration, hScrollable]);

  // 钢笔工具 = 多边形蒙版加点：把工具状态同步给监视器蒙版编辑器。
  useEffect(() => {
    maskEditor.setPenMode(activeTool === 'pen');
  }, [activeTool]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.isComposing || event.keyCode === 229) return;
      if (event.target instanceof Element && event.target.closest('#editor-refinement, .refinement-panel')) return;
      if (event.target instanceof HTMLElement && event.target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])')) return;
      const target = event.target as HTMLElement | null;
      if (
        target &&
        (target.tagName === 'INPUT' ||
          target.tagName === 'TEXTAREA' ||
          target.tagName === 'SELECT' ||
          target.isContentEditable)
      ) {
        return;
      }
      const key = event.key.toLowerCase();
      if (readOnly) {
        // 子序列只读预览：时间线上的一切写操作都拦截，只保留播放头拖动、工具切换与吸附。
        if (event.key === 'Delete' || event.key === 'Backspace' || key === 'k') {
          event.preventDefault();
          onNotify('只读预览：子序列片段不能删除或添加关键帧（v1 限制）');
          return;
        }
        if (event.ctrlKey || event.metaKey) {
          if (key === 'c' || key === 'x' || key === 'v' || (key === 'd' && event.altKey)) {
            event.preventDefault();
            onNotify('只读预览：子序列片段不能复制、粘贴或删除（v1 限制）');
            return;
          }
        }
        if (event.altKey || (event.shiftKey && key === 'c')) {
          event.preventDefault();
          onNotify('只读预览：子序列片段不能滑移或滚动编辑（v1 限制）');
          return;
        }
      }
      if (event.key === 'Escape' && transitionDialog) {
        event.preventDefault();
        setTransitionDialog(null);
        return;
      }
      if (event.key === 'Escape' && contextMenu) {
        event.preventDefault();
        closeClipMenu();
        return;
      }
      if (event.key === 'Escape' && drag) {
        // FilmCraft：拖动中按 Esc 取消本次拖拽（不改工程、不提交命令）。
        event.preventDefault();
        cancelActiveDrag();
        onNotify('已取消本次拖动');
        return;
      }
      if (event.key === 'Escape' && (marquee || marqueeIds.length > 0)) {
        event.preventDefault();
        setMarquee(null);
        setMarqueeIds([]);
        onNotify('已取消框选');
        return;
      }
      if (event.key === 'Escape' && activeTool !== 'select') {
        // Esc 只处理一层：阻止默认并标记已处理，避免 EditorPage 再关一个浮层。
        event.preventDefault();
        onToolChange('select');
        onNotify('已切回选择工具');
        return;
      }
      if (selectedEnvelope && !event.ctrlKey && !event.metaKey && !event.altKey) {
        if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
          event.preventDefault();
          nudgeEnvelopePoint('db', event.key === 'ArrowUp' ? 1 : -1, event.shiftKey);
          return;
        }
        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
          event.preventDefault();
          nudgeEnvelopePoint('time', event.key === 'ArrowLeft' ? -1 : 1, event.shiftKey);
          return;
        }
        if (event.key === 'Delete' || event.key === 'Backspace') {
          event.preventDefault();
          removeSelectedEnvelopePoint();
          return;
        }
        if (event.key === 'Escape') {
          event.preventDefault();
          setSelectedEnvelope(null);
          setEnvelopeMenu(null);
          return;
        }
      }
      if (event.altKey && (key === 'arrowleft' || key === 'arrowright')) {
        event.preventDefault();
        slipSelected((key === 'arrowleft' ? -1 : 1) * (event.shiftKey ? 5 : 1));
        return;
      }
      if (event.shiftKey && key === 't' && !event.altKey) {
        // FilmCraft：Shift+T 选中最近的编辑点并打开修剪监视器。
        event.preventDefault();
        const nearest = nearestEditPoint(items, sequenceTime, fps, transitions, sourceDurations);
        if (!nearest) {
          onNotify('当前序列没有可修剪的编辑点');
          return;
        }
        onEditPoint?.({ ...nearest, kind: editPoint?.kind ?? nearest.kind });
        onNotify('已选中最近编辑点：节目监视器切换为修剪监视器');
        return;
      }
      if (event.ctrlKey || event.metaKey) {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault();
          if (placements.length === 0) {
            onNotify('时间线还没有片段');
            return;
          }
          const direction = event.key === 'ArrowDown' ? 1 : -1;
          const current = selectedIndex >= 0 ? selectedIndex : direction > 0 ? -1 : placements.length;
          const nextIndex = clamp(current + direction, 0, placements.length - 1);
          const placement = placements[nextIndex];
          onSelectItem(placement.item.id);
          onSeek(placement.offset + 0.01);
          onNotify(`选中片段 ${String(nextIndex + 1).padStart(2, '0')}`);
          return;
        }
        if (key === 'a') {
          event.preventDefault();
          selectAllItems();
        } else if (key === 't') {
          // FilmCraft：Ctrl+T 在修剪监视器里循环 修剪→波纹→滚动。
          event.preventDefault();
          if (!editPoint) {
            onNotify('先选中一个编辑点：Shift+T 选最近切点，或拖动片段边缘');
            return;
          }
          const kind = nextEditPointKind(editPoint.kind);
          onEditPoint?.({ ...editPoint, kind });
          onNotify(`修剪类型：${EDIT_POINT_KIND_LABELS[kind]}`);
          return;
        } else if (key === 'c') {
          event.preventDefault();
          copySelected();
        } else if (key === 'x') {
          event.preventDefault();
          copySelected(true);
        } else if (key === 'v') {
          event.preventDefault();
          // 三点编辑接管了 `,`/`.`（FilmCraft 契约），剪贴板粘贴改为 Ctrl+V（覆盖为 Ctrl+Shift+V）。
          pasteSelected(event.shiftKey ? 'overwrite' : 'insert');
        } else if (key === 'd' && event.altKey) {
          event.preventDefault();
          duplicateSelected();
        }
        return;
      }
      if (selectedTransitionId) {
        // 转场选中时 Delete/Backspace 由编辑器层移除转场，这里不删除片段。
        return;
      }
      if (event.key === ',' || event.key === '.') {
        // 三点编辑（插入/覆盖）由 EditorPage 处理：这里只放行，不再劫持成粘贴。
        return;
      } else if (event.key === 'Delete' || event.key === 'Backspace') {
        event.preventDefault();
        if (selectedGap) {
          closeSelectedGap();
        } else {
          deleteSelected();
        }
      } else if (key === 'v') {
        onSelectItem(null);
        onToolChange('select');
      } else if (key === 'a') {
        onToolChange('track');
        onNotify('轨道选择工具：拖动任意片段整体平移该轨道');
      } else if (key === 'b') {
        onToolChange('ripple');
        onNotify('波纹编辑工具：拖边缘裁剪或拖片段主体，后续片段自动跟随');
      } else if (key === 'n') {
        onToolChange('rolling');
        onNotify('滚动编辑工具：拖动片段边缘移动相邻切点，序列总长不变');
      } else if (key === 'r') {
        onToolChange('rate');
        onNotify('比率拉伸工具：拖动片段左右边缘改变播放速率');
      } else if (key === 'u') {
        onToolChange('slide');
        onNotify('幻灯片工具：拖动片段主体，前后片段自动补偿');
      } else if (key === 'y') {
        onToolChange('slip');
        onNotify('滑移工具：左右拖动片段改变素材入出点');
      } else if (key === 'p') {
        onToolChange('pen');
        onNotify('钢笔工具：点击片段在指针处添加关键帧（v1 音量包络）');
      } else if (key === 'h') {
        onToolChange('hand');
        onNotify('手形工具：拖动平移时间线视图');
      } else if (key === 'z') {
        onToolChange('zoom');
        onNotify('缩放工具：点击放大时间线，Alt+点击缩小');
      } else if (event.shiftKey && key === 'c') {
        event.preventDefault();
        rollSelected(1);
      } else if (key === 'c') {
        onToolChange('razor');
        onNotify('刀片工具：点击片段在指针处拆分');
      } else if (key === 's') {
        onToggleSnap();
        onNotify(snapEnabled ? '已关闭时间线吸附' : '已开启时间线吸附');
      } else if (key === 'k') {
        event.preventDefault();
        addKeyframeAtPlayhead();
      } else if (key === 'm') {
        onAddMarker();
      }
      // I/O / Shift+I/O / Ctrl+Shift+I/O 与 ·/;·/' 等播放控制在 EditorPage 统一处理（FilmCraft 监视器播放控制条）。
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  });

  // FilmCraft：拖动中按 Esc 取消本次拖拽。挂在 window 捕获阶段，
  // 避免被 document 上的其它 Esc（面板/抽屉/菜单）先吃掉，也避免 pointer capture 下事件丢失。
  const dragStateRef = useRef<ClipDrag | null>(null);
  dragStateRef.current = drag;
  // T9：卸载时清掉遗留的 window 级手势监听。
  useEffect(() => () => {
    envelopeCleanupRef.current?.();
    gainCleanupRef.current?.();
  }, []);
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const popupOpen = document.querySelector('.timeline-more-menu');
      if (popupOpen) return;
      if (event.isComposing || event.keyCode === 229) return;
      if (event.target instanceof Element && event.target.closest('#editor-refinement, .refinement-panel')) return;
      if (event.target instanceof HTMLElement && event.target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])')) return;
      const target = event.target as HTMLElement | null;
      const typing = target ? /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.isContentEditable : false;
      // Shift+Z = 复位时间线缩放（与帮助面板一致，不依赖父级快捷键）。
      if (!typing && event.key.toLowerCase() === 'z' && event.shiftKey && !event.ctrlKey && !event.metaKey && !event.altKey) {
        event.preventDefault();
        setZoom(1);
        onNotify('已复位时间线缩放（100%）');
        return;
      }
      if (event.key !== 'Escape') return;
      // T9：Drag 中的关键帧/包络点/整条包络也支持 Esc 取消（手势不提交、不进 undo）。
      const activeEnvelopeDrag = envelopeDragRef.current;
      const activeLineDrag = envelopeLineDragRef.current;
      const activeMarkDrag = keyframeMarkDragRef.current;
      if (activeEnvelopeDrag || activeLineDrag || activeMarkDrag) {
        event.preventDefault();
        event.stopPropagation();
        envelopeCleanupRef.current?.();
        if (activeEnvelopeDrag) {
          finishEnvelopePointDrag(activeEnvelopeDrag, true);
        } else if (activeLineDrag) {
          setEnvelopeLineDrag(null);
          setEnvelopePreview(null);
        } else if (activeMarkDrag) {
          finishKeyframeMarkDrag(activeMarkDrag, true);
        }
        setSnapGuide(null);
        onNotify('已取消关键帧拖动');
        return;
      }
      // FilmCraft 音量线拖拽的 Esc 取消（不提交、不进 undo）。
      if (gainDragRef.current) {
        event.preventDefault();
        event.stopPropagation();
        gainCleanupRef.current?.();
        gainDragRef.current = null;
        setGainDrag(null);
        onNotify('已取消音量调整');
        return;
      }
      if (!dragStateRef.current) {
        // Esc 第二层：退出修剪监视器（FlmCraft trim.clear）。
        if (!editPoint) return;
        event.preventDefault();
        event.stopPropagation();
        onEditPoint?.(null);
        onNotify('已退出修剪监视器');
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      const auto = autoScrollRef.current;
      if (auto) {
        window.cancelAnimationFrame(auto.raf);
        autoScrollRef.current = null;
      }
      dragPointerRef.current = null;
      dragSourceElRef.current = null;
      setDrag(null);
      setSnapGuide(null);
      setDropPreview(null);
      onNotify('已取消本次拖动');
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  });

  return (
    <section className={`panel timeline-panel is-tool-${activeTool}`}>
      {gainDrag ? (
        <div
          className="timeline-volume-tip"
          data-testid="volume-tip"
          style={{ left: gainDrag.clientX + 14, top: gainDrag.clientY - 12 }}
        >
          {formatGainDb(gainDrag.db)}
          {gainDrag.keys ? <span className="timeline-volume-tip-note">整段</span> : null}
        </div>
      ) : null}
      <div className="panel-head timeline-head">
        <span className="panel-tab is-active">01 时间线</span>
        <button type="button" className="panel-tab" onClick={() => onOpenTab('transcript')}>
          02 字幕
        </button>
        <button type="button" className="panel-tab" onClick={() => onOpenTab('candidates')}>
          03 AI 建议
        </button>
        <span className="timeline-head-spacer" />
        <span className="sequence-tab">
          序列 01 <span className="sequence-number">{formatClock(totalDuration)}</span>
        </span>
      </div>

      {tabsBar}

      <TimelineToolbar
        actions={[
          {
            id: 'select',
            label: '选择',
            icon: <MousePointer2 size={11} />,
            keyHint: 'V',
            active: activeTool === 'select',
            idle: activeTool !== 'select' && Boolean(selectedItemId),
            title: '选择工具 (V)',
            onClick: () => {
              onToolChange('select');
              onSelectItem(null);
            },
          },
          {
            id: 'razor',
            label: '刀片',
            icon: <Scissors size={11} />,
            active: activeTool === 'razor',
            disabled: readOnly,
            dividerBefore: true,
            title: readOnly ? '只读预览：子序列片段不能拆分（v1 限制）' : '刀片工具：点击片段在指针处拆分 (C)',
            onClick: () => onToolChange(activeTool === 'razor' ? 'select' : 'razor'),
          },
          {
            id: 'marker',
            label: '加标记',
            icon: <Flag size={11} />,
            keyHint: 'M',
            disabled: readOnly,
            title: readOnly ? '只读预览：不能在子序列中添加标记（v1 限制）' : '在播放头添加标记 (M)',
            onClick: () => onAddMarker(),
          },
          {
            id: 'duplicate',
            label: '复制片段',
            icon: <Copy size={11} />,
            disabled: readOnly || !selectedItemId,
            dividerBefore: true,
            title: selectedItemId ? '复制当前片段到下一段 (Ctrl+Alt+D)' : '先选中一个片段',
            onClick: duplicateSelected,
          },
          {
            id: 'ripple-delete',
            label: '波纹删除',
            icon: <Trash2 size={11} />,
            disabled: readOnly || !selectedItemId,
            title: selectedItemId ? '删除当前片段并闭合空隙 (Delete)' : '先选中一个片段',
            onClick: deleteSelected,
          },
          {
            id: 'roll',
            label: '滚动切点',
            icon: <ArrowLeftRight size={11} />,
            disabled: readOnly || !selectedItemId || selectedIndex >= items.length - 1,
            title:
              selectedItemId && selectedIndex < items.length - 1
                ? '向左/右移动当前片段与下一段的切点 (Shift+C 向右一帧)'
                : '需要选中一个后面还有片段的片段',
            onClick: () => rollSelected(1),
          },
          {
            id: 'undo',
            label: '撤销',
            icon: <Undo2 size={11} />,
            disabled: readOnly || !canUndo,
            dividerBefore: true,
            title: undoLabel ? `撤销：${undoLabel}` : '没有可撤销的操作',
            onClick: onUndo,
          },
          {
            id: 'redo',
            label: '重做',
            icon: <Redo2 size={11} />,
            disabled: readOnly || !canRedo,
            title: redoLabel ? `重做：${redoLabel}` : '没有可重做的操作',
            onClick: onRedo,
          },
          {
            id: 'perception',
            label: '感知标记',
            icon: <AudioLines />,
            active: perception.enabled,
            dividerBefore: true,
            title: `静音段、镜头切点与响度包络（只读建议）；开启才请求，阈值 ${perception.params.threshold}，窗口 ${perception.params.window} s`,
            onClick: perception.onToggle,
          },
        ]}
        hint={
          marqueeIds.length > 0 ? (
            <span className="timeline-marquee-hint" role="status">
              已选 {marqueeIds.length} 段 · <kbd>Delete</kbd> 删除 · <kbd>Esc</kbd> 取消
            </span>
          ) : null
        }
      >
        <button
          type="button"
          className={`pr-tool pr-tool--compact timeline-snap${snapEnabled ? ' is-active' : ''}`}
          title="时间线吸附开关 (S)：拖拽与裁剪时吸附到片段边缘、标记与播放头"
          aria-label="时间线吸附"
          aria-pressed={snapEnabled}
          onClick={onToggleSnap}
        >
          <Magnet size={11} />
          <span>吸附</span>
          <span className="pr-tool-key">S</span>
        </button>
        <label className="timeline-zoom" title="时间线缩放">
          <ZoomIn size={10} />
          <input
            type="range"
            min={1}
            max={4}
            step={0.1}
            value={zoom}
            aria-label="时间线缩放"
            onChange={(event) => setZoom(Number(event.target.value))}
          />
          <span>{Math.round(zoom * 100)}%</span>
        </label>
        {editingTimecode ? (
          <input
            className="timeline-timecode is-editing"
            aria-label="时间码输入"
            autoFocus
            value={timecodeDraft}
            onChange={(event) => setTimecodeDraft(event.target.value)}
            onKeyDown={(event) => {
              event.stopPropagation();
              if (event.key === 'Enter') {
                event.preventDefault();
                commitTimecodeInput(timecodeDraft);
              } else if (event.key === 'Escape') {
                event.preventDefault();
                setEditingTimecode(false);
                onNotify('已取消时间码输入');
              }
            }}
            onBlur={() => {
              if (editingTimecode) commitTimecodeInput(timecodeDraft);
            }}
          />
        ) : (
          <button
            type="button"
            className="timeline-timecode"
            title="播放头时间码（点击输入 HH:MM:SS:FF 跳转）"
            onClick={() => {
              setTimecodeDraft(formatTimecode(sequenceTime));
              setEditingTimecode(true);
            }}
          >
            {formatTimecode(sequenceTime)}
          </button>
        )}
      </TimelineToolbar>
      <PerceptionStatusBar perception={perception} />

      <div className="timeline-body">
        <div className="ruler-row">
          <div className="ruler-head" />
          <div
            className="ruler-scroll"
            ref={rulerViewportRef}
            onScroll={(event) => {
              if (tracksViewportRef.current) tracksViewportRef.current.scrollLeft = event.currentTarget.scrollLeft;
              if (hScrollRef.current) hScrollRef.current.scrollLeft = event.currentTarget.scrollLeft;
            }}
          >
            <div
              className="ruler"
              ref={rulerRef}
              style={{ width: `${zoomRender * 100}%` }}
              onPointerDown={beginScrub}
              onPointerMove={moveScrub}
              onPointerUp={endScrub}
              onPointerCancel={endScrub}
              onContextMenu={(event) => {
                event.preventDefault();
                event.stopPropagation();
                setRulerMenu({
                  x: event.clientX,
                  y: event.clientY,
                  time: timeAt(event.clientX, rulerRef.current),
                });
              }}
              role="presentation"
            >
              {ruler.map((time, index) => {
                const major = index % rulerLabelEvery === 0;
                return (
                  <span
                    key={index}
                    className={`ruler-tick${major ? ' is-major' : ''}`}
                    style={{ left: `${(index / (ruler.length - 1)) * 100}%` }}
                  >
                    {major ? <span>{formatClock(time)}</span> : null}
                  </span>
                );
              })}
              {inOut.in !== null && inOut.out !== null && inOut.out > inOut.in ? (
                <span
                  className="timeline-work-range"
                  style={{
                    left: `${(inOut.in / total) * 100}%`,
                    width: `${Math.max(0, ((inOut.out - inOut.in) / total) * 100)}%`,
                  }}
                  aria-hidden="true"
                />
              ) : null}
              {inOut.in !== null ? (
                <span
                  className="timeline-inout-mark is-in"
                  style={{ left: `${(inOut.in / total) * 100}%` }}
                  aria-hidden="true"
                />
              ) : null}
              {inOut.out !== null ? (
                <span
                  className="timeline-inout-mark is-out"
                  style={{ left: `${(inOut.out / total) * 100}%` }}
                  aria-hidden="true"
                />
              ) : null}
              {snapGuide !== null ? (
                <span
                  className="timeline-snap-guide timeline-snap-guide--ruler"
                  style={{ left: `${(snapGuide / total) * 100}%` }}
                  aria-hidden="true"
                />
              ) : null}
              <span
                className="timeline-playhead-tag"
                style={{ left: `${Math.min(100, Math.max(0, (sequenceTime / total) * 100))}%` }}
                aria-hidden="true"
              >
                {formatTimecode(sequenceTime)}
              </span>
            </div>
          </div>
        </div>

        <div className="track-area">
          <div className="track-heads">
            {tracks.map((track) => (
              <div
                key={track.id}
                className={`track-head${(track.kind === 'video' ? targetTracks.video : targetTracks.audio) === track.number ? ' is-targeted' : ''}`}
                data-kind={track.kind}
                onContextMenu={(event) => {
                  event.preventDefault();
                  event.stopPropagation();
                  if (readOnly) {
                    onNotify('只读预览：子序列中不能修改轨道状态或属性（v1 限制）');
                    return;
                  }
                  setTrackMenu({ x: event.clientX, y: event.clientY, track: track.number });
                }}
              >
                <button
                  type="button"
                  className="track-id track-target"
                  disabled={readOnly}
                  aria-pressed={(track.kind === 'video' ? targetTracks.video : targetTracks.audio) === track.number}
                  aria-label={`${track.id} 目标轨`}
                  title={`设为${track.kind === 'video' ? '画面' : '音频'}目标轨；插入/覆盖/粘贴将落到 ${track.id}`}
                  onClick={() => chooseTargetTrack(track.number)}
                >
                  {track.id}
                </button>
                <span className="track-kind" aria-hidden="true">
                  {track.kind === 'video' ? <Film size={11} /> : <AudioLines size={11} />}
                </span>
                <span className="track-name" title={track.name}>
                  {track.name}
                </span>
                {track.number === 1 && (
                  <button
                    type="button"
                    disabled={readOnly || !onToggleBaseAudioMute}
                    className={`track-toggle${baseAudioMuted ? ' is-active' : ''}`}
                    aria-label="V1 内嵌音频静音"
                    aria-pressed={baseAudioMuted}
                    title={baseAudioMuted ? '取消 V1 内嵌音频静音' : '静音 V1 内嵌音频（不影响画面）'}
                    onClick={onToggleBaseAudioMute}
                  >
                    <span className="track-toggle-glyph" aria-hidden="true">M</span>
                  </button>
                )}
                {track.kind === 'video' ? (
                  <button
                    type="button"
                    disabled={readOnly}
                    className={`track-toggle track-eye${hiddenTracks.includes(track.number) ? ' is-active' : ''}`}
                    aria-label={`${track.name}轨道显示`}
                    aria-pressed={!hiddenTracks.includes(track.number)}
                    title={hiddenTracks.includes(track.number) ? '显示此视频轨' : '隐藏此视频轨'}
                    onClick={() => onToggleTrackHidden(track.number)}
                  >
                    <span className="track-toggle-glyph" aria-hidden="true">
                      {hiddenTracks.includes(track.number) ? <EyeOff size={11} /> : <Eye size={11} />}
                    </span>
                  </button>
                ) : (
                  <>
                    <button
                      type="button"
                      disabled={readOnly}
                      className={`track-toggle${mutedTracks.includes(track.number) ? ' is-active' : ''}`}
                      aria-label={`${track.name}静音`}
                      aria-pressed={mutedTracks.includes(track.number)}
                      title={mutedTracks.includes(track.number) ? '取消静音' : '静音此音轨'}
                      onClick={() => onToggleTrackMute(track.number)}
                    >
                      <span className="track-toggle-glyph" aria-hidden="true">M</span>
                    </button>
                    <button
                      type="button"
                      disabled={readOnly}
                      className={`track-toggle${soloedTracks.includes(track.number) ? ' is-solo' : ''}`}
                      aria-label={`${track.name}独奏`}
                      aria-pressed={soloedTracks.includes(track.number)}
                      title={soloedTracks.includes(track.number) ? '取消独奏' : '只播放独奏音轨'}
                      onClick={() => onToggleTrackSolo(track.number)}
                    >
                      <span className="track-toggle-glyph" aria-hidden="true">S</span>
                    </button>
                  </>
                )}
                <button
                  type="button"
                  disabled={readOnly}
                  className={`track-toggle${lockedTracks.includes(track.number) ? ' is-locked' : ''}`}
                  aria-label={`${track.name}轨道锁定`}
                  aria-pressed={lockedTracks.includes(track.number)}
                  title={lockedTracks.includes(track.number) ? '解锁此轨道' : '锁定此轨道，防止误编辑'}
                  onClick={() => onToggleTrackLocked(track.number)}
                >
                  <span className="track-toggle-glyph" aria-hidden="true">
                    {lockedTracks.includes(track.number) ? <Lock size={10} /> : <LockOpen size={10} />}
                  </span>
                </button>
              </div>
            ))}
          </div>
          <div
            className="tracks-scroll"
            ref={tracksViewportRef}
            onScroll={(event) => {
              if (rulerViewportRef.current) rulerViewportRef.current.scrollLeft = event.currentTarget.scrollLeft;
              if (hScrollRef.current) hScrollRef.current.scrollLeft = event.currentTarget.scrollLeft;
            }}
          >
            <div
              className="tracks"
              ref={tracksRef}
              style={{ width: `${zoomRender * 100}%` }}
              onPointerDown={beginMarquee}
              onPointerMove={(event) => {
                moveMarquee(event);
                if (activeTool === 'razor' && !drag) {
                  setRazorGuide(timeAt(event.clientX, tracksRef.current));
                }
              }}
              onPointerLeave={() => setRazorGuide(null)}
              onPointerUp={endMarquee}
              onPointerCancel={cancelMarquee}
              onDragOver={handleAssetDragOver}
              onDragLeave={handleAssetDragLeave}
              onDrop={handleAssetDrop}
            >
            {tracks.map((track) => {
              const trackNumber = track.number;
              const lane = placements.filter((placement) => placement.track === trackNumber);
              return (
                <div
                  key={track.id}
                  data-track={trackNumber}
                  className={`track track-${track.id.toLowerCase()}${(track.kind === 'video' ? targetTracks.video : targetTracks.audio) === trackNumber ? ' is-targeted' : ''}${dropPreview && dropPreview.track === trackNumber ? (dropPreview.invalid ? ' is-drop-invalid' : ' is-drop-target') : ''}${track.kind === 'audio' && mutedTracks.includes(trackNumber) ? ' is-muted' : ''}${track.kind === 'audio' && soloedTracks.includes(trackNumber) ? ' is-soloed' : ''}${track.kind === 'video' && hiddenTracks.includes(trackNumber) ? ' is-hidden' : ''}${lockedTracks.includes(trackNumber) ? ' is-locked' : ''}`}
                >
                  {lane
                    .filter((placement) => placement.gap > 0.001)
                    .map((placement) => (
                      <span
                        key={`gap-${placement.item.id}`}
                        className={`timeline-gap${selectedGap?.itemId === placement.item.id ? ' is-gap-selected' : ''}`}
                        style={{
                          left: `${(placement.gapStart / total) * 100}%`,
                          width: `${(placement.gap / total) * 100}%`,
                        }}
                        title={`V${trackNumber} 空隙 ${formatClock(placement.gap)}（点击选中，Delete 闭合）`}
                        onPointerDown={(event) => {
                          event.preventDefault();
                          event.stopPropagation();
                          setSelectedGap({ track: trackNumber, itemId: placement.item.id, gap: placement.gap });
                          onNotify(`已选 ${track.name} 空隙 ${formatClock(placement.gap)}，Delete 闭合`);
                        }}
                        onContextMenu={(event) => {
                          event.preventDefault();
                          event.stopPropagation();
                          setSelectedGap({ track: trackNumber, itemId: placement.item.id, gap: placement.gap });
                          setGapMenu({
                            x: event.clientX,
                            y: event.clientY,
                            track: trackNumber,
                            itemId: placement.item.id,
                            gap: placement.gap,
                          });
                        }}
                      >
                        {placement.gap >= 1 ? formatClock(placement.gap) : ''}
                      </span>
                    ))}
                  {lane.map((placement) => (
                    <Clip
                      key={placement.item.id}
                      item={placement.item}
                      index={placement.index}
                      left={(placement.offset / total) * 100}
                      width={(placement.length / total) * 100}
                      label={itemLabels[placement.item.id] ?? `片段 ${String(placement.index + 1).padStart(2, '0')}`}
                      selected={placement.item.id === selectedItemId}
                      marqueeSelected={marqueeIds.includes(placement.item.id)}
                      fps={fps}
                      drag={drag}
                      audio={track.kind === 'audio'}
                      readOnly={readOnly}
                      perception={perception}
                      gainDb={gainDrag?.itemId === placement.item.id ? gainDrag.db : clampGainDb(placement.item.volumeDb ?? 0)}
                      gainActive={gainDrag?.itemId === placement.item.id}
                      onGainPointerDown={(event) => beginGainDrag(event, placement.item)}
                      nestedName={
                        placement.item.nestedSequenceId
                          ? nestedNames?.[placement.item.nestedSequenceId] ??
                            `子序列 ${placement.item.nestedSequenceId.slice(0, 6)}`
                          : null
                      }
                      onPointerDown={(event) => beginClipDrag(event, placement)}
                      onPointerMove={moveClipDrag}
                      onPointerUp={(event) => endClipDrag(event, placement)}
                      onPointerCancel={cancelActiveDrag}
                      onContextMenu={(event) => {
                        if (readOnly) {
                          event.preventDefault();
                          event.stopPropagation();
                          onNotify('只读预览：子序列片段不能拖动、裁剪、删除或添加关键帧（v1 限制）');
                          return;
                        }
                        openClipMenu(event, placement);
                      }}
                      onDoubleClick={(event) => {
                        if (placement.item.nestedSequenceId && onEnterNested) {
                          event.preventDefault();
                          event.stopPropagation();
                          onEnterNested(placement.item.id);
                          return;
                        }
                        if (readOnly) {
                          onNotify('只读预览：子序列片段不能编辑（v1 限制）');
                          return;
                        }
                        addEnvelopeKeyframeAtPoint(event, placement);
                      }}
                    />
                  ))}
                  {dropPreview && dropPreview.track === trackNumber ? (
                    <span
                      className={`timeline-drag-ghost${dropPreview.invalid ? ' is-invalid' : ''}`}
                      style={{
                        left: `${(dropPreview.offset / total) * 100}%`,
                        width: `${Math.max((dropPreview.length / total) * 100, 0.6)}%`,
                      }}
                      aria-hidden="true"
                    />
                  ) : null}
                  {assetPreview && assetPreview.track === trackNumber ? (
                    <span
                      className={`timeline-drag-ghost is-asset${assetPreview.invalid ? ' is-invalid' : ''}`}
                      data-testid="timeline-asset-ghost"
                      title={assetPreview.title}
                      style={{
                        left: `${(assetPreview.offset / total) * 100}%`,
                        width: assetPreview.length > 0
                          ? `${Math.max((assetPreview.length / total) * 100, 0.6)}%`
                          : '120px',
                      }}
                      aria-hidden="true"
                    />
                  ) : null}
                  {lane.map((placement) => {
                    const points = envelopePointsFor(placement.item);
                    if (points.length === 0) return null;
                    const duration = Math.max(0.001, itemOutputDuration(placement.item));
                    const polyline = buildEnvelopePolyline(points, duration);
                    return (
                      <div
                        key={`envelope-${placement.item.id}`}
                        className="timeline-envelope-editor"
                        style={{
                          left: `${(placement.offset / total) * 100}%`,
                          width: `${(placement.length / total) * 100}%`,
                        }}
                        title="音量包络：上下拖整条线调增益，拖动圆点改时间与增益，双击片段线上添加关键帧"
                      >
                        <svg
                          className="timeline-envelope"
                          viewBox="0 0 100 100"
                          preserveAspectRatio="none"
                          aria-hidden="true"
                        >
                          <polyline
                            className="envelope-line-hit"
                            points={polyline}
                            onPointerDown={(event) => beginEnvelopeLineDrag(event, placement)}
                            onContextMenu={(event) => {
                              event.preventDefault();
                              event.stopPropagation();
                              onSelectItem(placement.item.id);
                              const overlay = event.currentTarget.closest(
                                '.timeline-envelope-editor',
                              ) as HTMLElement | null;
                              const overlayRect = overlay?.getBoundingClientRect();
                              const ratio =
                                overlayRect && overlayRect.width > 0
                                  ? clamp((event.clientX - overlayRect.left) / overlayRect.width, 0, 1)
                                  : 0.5;
                              setContextMenu({
                                x: Math.min(event.clientX, window.innerWidth - 230),
                                y: Math.min(event.clientY, window.innerHeight - 560),
                                itemId: placement.item.id,
                                sourceTime: placement.item.start + ratio * placement.sourceLength,
                              });
                            }}
                            onDoubleClick={(event) => {
                              const overlay = event.currentTarget.closest('.timeline-envelope-editor');
                              const overlayRect = overlay?.getBoundingClientRect();
                              if (!overlayRect) return;
                              addEnvelopeKeyframeAtPoint(event, placement, overlayRect);
                            }}
                          />
                          <polyline points={polyline} />
                        </svg>
                        {points.map((point, index) => {
                          const x = Math.min(100, Math.max(0, (point.time / duration) * 100));
                          const y = envelopeDbToY(point.db);
                          const dragging =
                            envelopeDrag?.itemId === placement.item.id && envelopeDrag.index === index;
                          const selected =
                            selectedEnvelope?.itemId === placement.item.id && selectedEnvelope.index === index;
                          return (
                            <button
                              key={`envelope-point-${index}`}
                              type="button"
                              data-testid="envelope-point"
                              data-envelope-index={index}
                              className={`envelope-point${dragging ? ' is-dragging' : ''}${selected ? ' is-selected' : ''}`}
                              style={{ left: `${x}%`, top: `${y}%` }}
                              title={`${point.time.toFixed(2)} 秒 ${point.db.toFixed(1)} dB：拖动调整，方向键微调，右键更多`}
                              onPointerDown={(event) => beginEnvelopePointDrag(event, placement, index)}
                              onContextMenu={(event) => {
                                event.preventDefault();
                                event.stopPropagation();
                                setSelectedEnvelope({ itemId: placement.item.id, index });
                                setEnvelopeMenu({
                                  x: event.clientX,
                                  y: event.clientY,
                                  itemId: placement.item.id,
                                  index,
                                });
                              }}
                              onDoubleClick={(event) => event.stopPropagation()}
                            />
                          );
                        })}
                      </div>
                    );
                  })}
                  {lane.map((placement) => {
                    const marks = keyframeMarksFor(placement.item);
                    if (marks.length === 0) return null;
                    const duration = Math.max(0.001, itemOutputDuration(placement.item));
                    return (
                      <div
                        key={`keyframes-${placement.item.id}`}
                        className="timeline-keyframe-row"
                        style={{
                          left: `${(placement.offset / total) * 100}%`,
                          width: `${(placement.length / total) * 100}%`,
                        }}
                      >
                        {marks.map((mark) => {
                          const dragging =
                            keyframeMarkDrag &&
                            keyframeMarkDrag.itemId === placement.item.id &&
                            Math.abs(keyframeMarkDrag.startTime - mark.time) <= 0.02;
                          const markTime = dragging ? keyframeMarkDrag!.time : mark.time;
                          return (
                            <button
                              key={`keyframe-mark-${mark.time}`}
                              type="button"
                              data-testid="keyframe-mark"
                              data-keyframe-time={mark.time}
                              className={`timeline-keyframe-mark${
                                placement.item.id === selectedItemId ? ' is-selected' : ''
                              }${dragging ? ' is-dragging' : ''}`}
                              style={{
                                left: `${Math.min(100, Math.max(0, (markTime / duration) * 100))}%`,
                              }}
                              title={`关键帧 ${mark.time.toFixed(2)}s：${mark.properties
                                .map((prop) => PARAM_KEYFRAME_LABELS[prop])
                                .join('、')}（点击定位，拖动改时间）`}
                              onPointerDown={(event) =>
                                beginKeyframeMarkDrag(event, placement.item.id, mark.time, placement.offset)
                              }
                            />
                          );
                        })}
                      </div>
                    );
                  })}
                  {cutPlacements
                    .filter((cut) => trackOf(cut.next) === trackNumber)
                    .map((cut) => (
                      <button
                        key={cut.transition.id}
                        type="button"
                        className={`timeline-transition${cut.transition.id === selectedTransitionId ? ' is-selected' : ''}`}
                        style={{
                          left: `${(cut.start / total) * 100}%`,
                          width: `${Math.max(
                            0.4,
                            (((transitionResize?.transitionId === cut.transition.id
                              ? transitionResizePreview
                              : null) ?? cut.effective) /
                              total) *
                              100,
                          )}%`,
                        }}
                        title={`${transitionKindLabel(cut.transition.kind)} · ${(
                          (transitionResize?.transitionId === cut.transition.id
                            ? transitionResizePreview
                            : null) ?? cut.effective
                        ).toFixed(2)}s · 拖两端改时长，双击精确输入`}
                        onPointerDown={(event) => beginTransitionResize(event, cut)}
                        onDoubleClick={(event) => {
                          event.stopPropagation();
                          onSelectTransition(cut.transition.id);
                          setTransitionDialog({
                            transitionId: cut.transition.id,
                            value: cut.effective.toFixed(2),
                          });
                        }}
                        onContextMenu={(event) => {
                          event.preventDefault();
                          event.stopPropagation();
                          onSelectTransition(cut.transition.id);
                          setTransitionMenu({
                            x: event.clientX,
                            y: event.clientY,
                            transitionId: cut.transition.id,
                          });
                        }}
                      >
                        <span className="timeline-transition-label">
                          {transitionResize?.transitionId === cut.transition.id &&
                          transitionResizePreview !== null
                            ? `${transitionResizePreview.toFixed(2)}s`
                            : transitionKindLabel(cut.transition.kind)}
                        </span>
                      </button>
                    ))}
                </div>
              );
            })}
            {markers.map((marker, index) => (
              <span
                key={`${marker}-${index}`}
                className="timeline-marker"
                style={{ left: `${(marker / total) * 100}%` }}
                title={`标记 ${index + 1} · ${formatClock(marker)}`}
              >
                <Flag size={9} />
              </span>
            ))}
            {snapGuide !== null ? (
              <span
                className="timeline-snap-guide"
                style={{ left: `${(snapGuide / total) * 100}%` }}
                aria-hidden="true"
              />
            ) : null}
            {editPoint
              ? (() => {
                  const editPlacement = placements.find((entry) => entry.item.id === editPoint.clipId);
                  if (!editPlacement) return null;
                  const at =
                    editPoint.edge === 'out'
                      ? editPlacement.offset + editPlacement.length
                      : editPlacement.offset;
                  return (
                    <span
                      className={`timeline-edit-point is-${editPoint.kind} is-${editPoint.edge}`}
                      style={{ left: `${(at / total) * 100}%` }}
                      title={`编辑点 · ${EDIT_POINT_KIND_LABELS[editPoint.kind]}（修剪监视器）`}
                      data-testid="timeline-edit-point"
                      aria-hidden="true"
                    />
                  );
                })()
              : null}
            {transitionPreview ? (
              <span
                className={`timeline-transition-drop${transitionPreview.invalid ? ' is-invalid' : ''}`}
                style={{ left: `${(transitionPreview.time / total) * 100}%` }}
                title={
                  transitionPreview.invalid
                    ? `${transitionPreview.label}：这里没有可落点的剪点`
                    : `${transitionPreview.existing ? '替换转场' : '应用转场'}：${transitionPreview.label}`
                }
                data-testid="timeline-transition-drop"
                aria-hidden="true"
              />
            ) : null}
            {razorGuide !== null && activeTool === 'razor' ? (
              <span
                className="timeline-razor-guide"
                style={{
                  position: 'absolute',
                  top: 0,
                  bottom: 0,
                  width: 1,
                  marginLeft: -0.5,
                  background: 'rgba(255, 255, 255, 0.85)',
                  pointerEvents: 'none',
                  zIndex: 6,
                  left: `${(razorGuide / total) * 100}%`,
                }}
                aria-hidden="true"
              />
            ) : null}
            {placements.length === 0 ? (
              <div className="timeline-empty">
                {emptyHint ?? '还没有工程时间线 · 从右侧应用候选后，片段会出现在 V1'}
              </div>
            ) : null}
            {marquee && marquee.moved ? (
              <span
                className="timeline-marquee"
                aria-hidden="true"
                style={{
                  left: Math.min(marquee.x0, marquee.x1) - marquee.left,
                  top: Math.min(marquee.y0, marquee.y1) - marquee.top,
                  width: Math.abs(marquee.x1 - marquee.x0),
                  height: Math.abs(marquee.y1 - marquee.y0),
                }}
              />
            ) : null}
            <span className="timeline-playhead" style={{ left: `${playhead}%` }} aria-hidden="true">
              <span className="playhead-cap" />
            </span>
            <button
              type="button"
              className="playhead-grab"
              style={{ left: `${playhead}%` }}
              aria-label="拖动播放头"
              title="拖动播放头定位"
              onPointerDown={beginPlayheadDrag}
            />
          </div>
          </div>
        </div>

        <div
          className="timeline-hscroll"
          ref={hScrollRef}
          hidden={!hScrollable}
          aria-hidden="true"
          onScroll={(event) => {
            const left = event.currentTarget.scrollLeft;
            if (tracksViewportRef.current && tracksViewportRef.current.scrollLeft !== left) {
              tracksViewportRef.current.scrollLeft = left;
            }
            if (rulerViewportRef.current && rulerViewportRef.current.scrollLeft !== left) {
              rulerViewportRef.current.scrollLeft = left;
            }
            syncHScrollThumb();
          }}
        >
          <div
            className="timeline-hscroll__track"
            ref={hScrollTrackRef}
            onPointerDown={(event) => {
              const bar = hScrollRef.current;
              if (!bar) return;
              const rect = event.currentTarget.getBoundingClientRect();
              const ratio = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
              const max = Math.max(0, bar.scrollWidth - bar.clientWidth);
              bar.scrollLeft = ratio * max;
              hScrollDragRef.current = { pointerId: event.pointerId, startX: event.clientX, startLeft: bar.scrollLeft };
              event.currentTarget.setPointerCapture(event.pointerId);
              syncHScrollThumb();
            }}
            onPointerMove={(event) => {
              const bar = hScrollRef.current;
              const drag = hScrollDragRef.current;
              const track = hScrollTrackRef.current;
              if (!bar || !drag || !track) return;
              const max = Math.max(0, bar.scrollWidth - bar.clientWidth);
              const delta = event.clientX - drag.startX;
              bar.scrollLeft = Math.min(max, Math.max(0, drag.startLeft + delta * (bar.scrollWidth / track.clientWidth)));
              syncHScrollThumb();
            }}
            onPointerUp={(event) => {
              if (hScrollDragRef.current?.pointerId === event.pointerId) {
                hScrollDragRef.current = null;
                event.currentTarget.releasePointerCapture?.(event.pointerId);
              }
            }}
            onPointerCancel={() => {
              hScrollDragRef.current = null;
            }}
          >
            <div className="timeline-hscroll__thumb" ref={hScrollThumbRef} />
          </div>
          <div className="timeline-hscroll__content" style={{ width: `${Math.max(100, zoomRender * 100)}%` }} />
        </div>

        <div className="timeline-status">
          <span>序列 01</span>
          <span>时长 {formatTimecode(totalDuration)}</span>
          <span>{fps || 30} fps</span>
          <span>源：单文件</span>
          <span className="timeline-tool-chip">{toolStatusLabel(activeTool)}</span>
          <span className="timeline-status-right">
            拖动排序 · Delete 波纹删除 · Ctrl+D 转场 · Shift+D 批量转场 · Alt+←/→ 滑移 · S 吸附
          </span>
        </div>
      </div>

      {contextMenu ? (
        <div
          className="timeline-context-layer"
          onPointerDown={closeClipMenu}
          onContextMenu={(event) => event.preventDefault()}
        >
          <div
            ref={clipMenuRef}
            className="timeline-context-menu"
            style={{ left: contextMenu.x, top: contextMenu.y }}
            role="menu"
            onPointerDown={(event) => event.stopPropagation()}
          >
            <div className="timeline-context-title">
              {itemLabels[contextMenu.itemId] ?? '片段'}
            </div>
            <button type="button" role="menuitem" onClick={() => runContextAction(() => copySelected(true))}>
              <span>剪切片段</span><kbd>Ctrl X</kbd>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(copySelected)}>
              <span>复制片段</span><kbd>Ctrl C</kbd>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(pasteSelected)}>
              <span>粘贴片段</span><kbd>Ctrl V</kbd>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(duplicateSelected)}>
              <span>复制到下一段</span><kbd>Ctrl D</kbd>
            </button>
            <span className="timeline-context-separator" />
            <button
              type="button"
              role="menuitem"
              disabled={menuCutNested}
              title={menuCutNested ? '嵌套序列暂不支持跨嵌套转场' : undefined}
              onClick={() => runContextAction(() => onApplyTransition(contextMenu.itemId))}
            >
              <span>在此剪点添加默认转场</span><kbd>Ctrl Shift D</kbd>
            </button>
            {menuCutNested ? (
              <div className="timeline-context-hint">嵌套序列暂不支持跨嵌套转场</div>
            ) : null}
            {findTransition(transitions, contextMenu.itemId) ? (
              <button
                type="button"
                role="menuitem"
                onClick={() => {
                  const transition = findTransition(transitions, contextMenu.itemId);
                  if (transition) runContextAction(() => onRemoveTransition(transition.id));
                }}
              >
                <span>移除该剪点转场</span>
              </button>
            ) : null}
            <button
              type="button"
              role="menuitem"
              disabled={Boolean(findTransition(transitions, contextMenu.itemId))}
              onClick={() => runContextAction(() => onCreateNested?.(contextMenu.itemId))}
            >
              <span>创建为嵌套序列</span>
            </button>
            <span className="timeline-context-separator" />
            <button
              type="button"
              role="menuitem"
              onClick={() =>
                runContextAction(() => {
                  const placement = placements.find((entry) => entry.item.id === contextMenu.itemId);
                  if (placement) splitItemAt(placement, contextMenu.sourceTime);
                })
              }
            >
              <span>在点击位置拆分</span><kbd>C</kbd>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(deleteSelected)}>
              <span>波纹删除</span><kbd>Delete</kbd>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(toggleDisabled)}>
              <span>{selectedItem?.disabled ? '启用片段' : '禁用片段'}</span>
            </button>
            <span className="timeline-context-separator" />
            <button type="button" role="menuitem" onClick={() => runContextAction(detachSelectedAudio)}>
              <span>分离音频到 A1</span>
            </button>
            {menuClip && trackOf(menuClip) <= 3 && !menuClip.audioDetached && !readOnly ? (
              <button type="button" role="menuitem" onClick={() => runContextAction(attachCompanionAudio)}>
                <span>匹配音频到对应 A 轨</span>
              </button>
            ) : null}
            <button type="button" role="menuitem" onClick={() => runContextAction(linkSelectedToCounterpart)}>
              <span>链接同源画面/音频</span>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(unlinkSelected)}>
              <span>解除链接</span>
            </button>
            <span className="timeline-context-separator" />
            <button
              type="button"
              role="menuitem"
              onClick={() => runContextAction(() => addVolumeKeyframeAtPlayhead(contextMenu.itemId))}
            >
              <span>在播放头添加音量关键帧</span>
            </button>
            {menuClip && normalizeVolumeKeyframes(menuClip).length > 0 ? (
              <button
                type="button"
                role="menuitem"
                onClick={() => runContextAction(() => clearVolumeEnvelope(contextMenu.itemId))}
              >
                <span>清除音量包络</span>
              </button>
            ) : null}
            <span className="timeline-context-separator" />
            <button type="button" role="menuitem" onClick={() => runContextAction(onToggleSnap)}>
              <span>{snapEnabled ? '关闭时间线吸附' : '开启时间线吸附'}</span><kbd>S</kbd>
            </button>
            <span className="timeline-context-separator" />
            <button type="button" role="menuitem" onClick={() => runContextAction(() => slipSelected(-1))}>
              <span>向左滑移一帧</span><kbd>Alt ←</kbd>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(() => slipSelected(1))}>
              <span>向右滑移一帧</span><kbd>Alt →</kbd>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(() => rollSelected(1))}>
              <span>滚动编辑切点</span><kbd>Shift C</kbd>
            </button>
            <span className="timeline-context-separator" />
            <button type="button" role="menuitem" onClick={() => runContextAction(() => adjustGap(1))}>
              <span>向前插入 1 秒空隙</span>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(() => adjustGap(-(contextMenu ? (placements.find((entry) => entry.item.id === contextMenu.itemId)?.gap ?? 0) : 0)))}>
              <span>清除前置空隙</span>
            </button>
            <span className="timeline-context-separator" />
            <button type="button" role="menuitem" onClick={() => runContextAction(() => moveSelectedToTrack(1))}>
              <span>移动到 V1 主画面</span>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(() => moveSelectedToTrack(2))}>
              <span>移动到 V2 叠加轨</span>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(() => moveSelectedToTrack(3))}>
              <span>移动到 V3 图形轨</span>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(() => moveSelectedToTrack(4))}>
              <span>移动到 A1 人声</span>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(() => moveSelectedToTrack(5))}>
              <span>移动到 A2 音乐</span>
            </button>
            <button type="button" role="menuitem" onClick={() => runContextAction(() => moveSelectedToTrack(6))}>
              <span>移动到 A3 音效</span>
            </button>
          </div>
        </div>
      ) : null}

      {trackMenu ? (
        <EditorContextMenu
          x={trackMenu.x}
          y={trackMenu.y}
          title={trackMenuTitle(trackMenu.track)}
          ariaLabel="轨道头操作菜单"
          items={buildTrackMenuItems(trackMenu.track)}
          onClose={() => setTrackMenu(null)}
        />
      ) : null}

      {gapMenu ? (
        <EditorContextMenu
          x={gapMenu.x}
          y={gapMenu.y}
          title={`空隙 · ${formatClock(gapMenu.gap)}`}
          ariaLabel="时间线空隙菜单"
          items={buildGapMenuItems(gapMenu)}
          onClose={() => setGapMenu(null)}
        />
      ) : null}

      {rulerMenu ? (
        <EditorContextMenu
          x={rulerMenu.x}
          y={rulerMenu.y}
          title={`标尺 · ${formatClock(rulerMenu.time)}`}
          ariaLabel="标尺操作菜单"
          items={buildRulerMenuItems(rulerMenu.time)}
          onClose={() => setRulerMenu(null)}
        />
      ) : null}

      {transitionMenu && menuTransition ? (
        <div
          className="timeline-context-layer"
          onPointerDown={() => setTransitionMenu(null)}
          onContextMenu={(event) => event.preventDefault()}
        >
          <div
            className="timeline-context-menu"
            style={{ left: transitionMenu.x, top: transitionMenu.y }}
            role="menu"
            onPointerDown={(event) => event.stopPropagation()}
          >
            <div className="timeline-context-title">
              转场 · {transitionKindLabel(menuTransition.kind)}
            </div>
            {(['cross-dissolve', 'dip-to-black', 'dip-to-white'] as const).map((kind) => (
              <button
                key={kind}
                type="button"
                role="menuitemradio"
                aria-checked={menuTransition.kind === kind}
                onClick={() => {
                  onUpdateTransition(menuTransition.id, { kind });
                  setTransitionMenu(null);
                }}
              >
                <span>{transitionKindLabel(kind)}</span>
                {menuTransition.kind === kind ? <span className="menu-check">✓</span> : null}
              </button>
            ))}
            <span className="timeline-context-separator" />
            {[0.3, 0.5, 1, 2].map((duration) => (
              <button
                key={duration}
                type="button"
                role="menuitemradio"
                aria-checked={Math.abs(menuTransition.duration - duration) < 0.01}
                onClick={() => {
                  onUpdateTransition(menuTransition.id, { duration });
                  setTransitionMenu(null);
                }}
              >
                <span>时长 {duration} 秒</span>
                {Math.abs(menuTransition.duration - duration) < 0.01 ? (
                  <span className="menu-check">✓</span>
                ) : null}
              </button>
            ))}
            <span className="timeline-context-separator" />
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                onRemoveTransition(menuTransition.id);
                setTransitionMenu(null);
              }}
            >
              <span>移除转场</span>
              <kbd>Delete</kbd>
            </button>
          </div>
        </div>
      ) : null}

      {envelopeMenu
        ? (() => {
            const menuItem = items.find((entry) => entry.id === envelopeMenu.itemId);
            const menuPoints = menuItem ? normalizeVolumeKeyframes(menuItem) : [];
            const menuPoint = menuPoints[envelopeMenu.index];
            if (!menuItem || !menuPoint) return null;
            return (
              <div
                className="timeline-context-layer"
                onPointerDown={() => setEnvelopeMenu(null)}
                onContextMenu={(event) => event.preventDefault()}
              >
                <div
                  className="timeline-context-menu"
                  style={{ left: envelopeMenu.x, top: envelopeMenu.y }}
                  role="menu"
                  onPointerDown={(event) => event.stopPropagation()}
                >
                  <div className="timeline-context-title">
                    音量关键帧 · {menuPoint.time.toFixed(2)} 秒 · {menuPoint.db.toFixed(1)} dB
                  </div>
                  <button type="button" role="menuitem" onClick={() => setEnvelopePointValue('zero')}>
                    <span>重置为 0 dB</span>
                  </button>
                  <button type="button" role="menuitem" onClick={() => setEnvelopePointValue('playhead')}>
                    <span>移动到播放头</span>
                  </button>
                  <span className="timeline-context-separator" />
                  <button type="button" role="menuitem" onClick={() => removeSelectedEnvelopePoint()}>
                    <span>删除关键帧</span>
                    <kbd>Delete</kbd>
                  </button>
                  <div className="timeline-context-hint">
                    方向键：↑↓ 调增益（Shift ±5 dB），←→ 移时间（Shift ±1 秒）
                  </div>
                </div>
              </div>
            );
          })()
        : null}

      {transitionDialog && dialogTransition ? (
        <div
          className="transition-duration-layer"
          onPointerDown={(event) => {
            if (event.target === event.currentTarget) setTransitionDialog(null);
          }}
        >
          <form
            className="transition-duration-dialog"
            noValidate
            onKeyDown={(event) => {
              if (event.key === 'Escape') {
                event.stopPropagation();
                setTransitionDialog(null);
              }
            }}
            onSubmit={(event) => {
              event.preventDefault();
              submitTransitionDialog();
            }}
          >
            <header>
              <h2>设置转场时长</h2>
              <small>{transitionKindLabel(dialogTransition.kind)}</small>
            </header>
            <label>
              时长（秒）
              <input
                autoFocus
                type="number"
                min={transitionFloor}
                max={dialogCeiling}
                step="any"
                value={transitionDialog.value}
                onChange={(event) =>
                  setTransitionDialog((current) =>
                    current ? { ...current, value: event.target.value } : current,
                  )
                }
              />
            </label>
            <p>
              允许 {transitionFloor.toFixed(2)}–{dialogCeiling.toFixed(2)} 秒；确定后写入工程，可用
              Ctrl+Z 撤销。
            </p>
            <footer>
              <button type="button" onClick={() => setTransitionDialog(null)}>
                取消
              </button>
              <button type="submit">确定</button>
            </footer>
          </form>
        </div>
      ) : null}
    </section>
  );
});
