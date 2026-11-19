import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
  type RefObject,
} from 'react';
import {
  ArrowUpFromLine,
  ArrowUpToLine,
  Camera,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Flag,
  Maximize2,
  Pause,
  Play,
  Plus,
  Replace,
  SkipBack,
  SkipForward,
  StepBack,
  StepForward,
  Volume2,
  VolumeX,
  Wrench,
} from 'lucide-react';
import type { ClipDto, MaskInstance, TimelineItem } from '../../../api/types';
import {
  baseParamValue,
  normalizeMasks,
  MAX_MASK_POINTS,
  clipFadeFactor,
  clipTitle,
  formatTimecode as formatTimecodeAt,
  paramValueAtItem,
} from '../project';
import { copyTextToClipboard } from '../clipboard';
import { combineFilters, gradeCss } from '../colorGrade';
import { normalizeAudioEffects } from '../audioEffects';
import { browserAudioEnv, createEqAudition, type EqAudition } from '../eqAudition';
import { EditorContextMenu } from './EditorContextMenu';

export type ProgramOverlay = {
  id: string;
  url: string;
  sequenceStart: number;
  duration: number;
  item: TimelineItem;
  opacity: number;
  audible: boolean;
  volume: number;
};

export type ProgramAudioLayer = {
  id: string;
  url: string;
  sequenceStart: number;
  duration: number;
  item: TimelineItem;
  volume: number;
  muted: boolean;
};

/**
 * 节目监视器蒙版编辑目标：效果控件（EffectControlsPanel）发布当前选中片段的
 * 蒙版选择，节目监视器消费它绘制可拖拽手柄，并把结果经由 EditorPage 写回工程。
 * 共享状态放在模块级 store，避免把 Monitor 与效果控件耦合到同一层 props。
 */
export type MaskEditorTarget = {
  itemId: string;
  maskId: string | null;
  editable: boolean;
  reason?: string;
};

type MaskEditorState = {
  target: MaskEditorTarget | null;
  focus: { maskId: string; nonce: number } | null;
  /** 钢笔工具提示：true 时在选中的多边形蒙版上点击 = 追加顶点。 */
  penMode: boolean;
};

const maskEditorListeners = new Set<() => void>();
let maskEditorState: MaskEditorState = { target: null, focus: null, penMode: false };

function emitMaskEditor() {
  maskEditorState = { ...maskEditorState };
  for (const listener of maskEditorListeners) listener();
}

export const maskEditor = {
  getState: () => maskEditorState,
  subscribe(listener: () => void) {
    maskEditorListeners.add(listener);
    return () => {
      maskEditorListeners.delete(listener);
    };
  },
  /** 效果控件每次选择片段/蒙版时更新；内容相同则不触发订阅者。 */
  setTarget(next: MaskEditorTarget | null) {
    const current = maskEditorState.target;
    if (
      current === next ||
      (current &&
        next &&
        current.itemId === next.itemId &&
        current.maskId === next.maskId &&
        current.editable === next.editable &&
        (current.reason ?? '') === (next.reason ?? ''))
    ) {
      return;
    }
    maskEditorState = { ...maskEditorState, target: next };
    emitMaskEditor();
  },
  /** 在监视器里点选另一个蒙版时回写选择，效果控件据此高亮卡片。 */
  selectMask(maskId: string | null) {
    const current = maskEditorState.target;
    if (!current || current.maskId === maskId) return;
    maskEditorState = { ...maskEditorState, target: { ...current, maskId } };
    emitMaskEditor();
  },
  /** 时间线切到钢笔工具时由 Timeline 调用；离开钢笔工具关闭。 */
  setPenMode(penMode: boolean) {
    if (maskEditorState.penMode === penMode) return;
    maskEditorState = { ...maskEditorState, penMode };
    emitMaskEditor();
  },
  /** 请求监视器以脉冲高亮当前蒙版（效果控件「在监视器中编辑」按钮）。 */
  focus(maskId: string) {
    maskEditorState = {
      ...maskEditorState,
      focus: { maskId, nonce: (maskEditorState.focus?.nonce ?? 0) + 1 },
    };
    emitMaskEditor();
  },
};

/** 订阅蒙版编辑状态；只用于渲染与选择同步，不触发工程写入。 */
export function useMaskEditorState(): MaskEditorState {
  const [state, setState] = useState(maskEditorState);
  useEffect(() => {
    const unsubscribe = maskEditor.subscribe(() => setState(maskEditorState));
    setState(maskEditorState);
    return unsubscribe;
  }, []);
  return state;
}

/** FilmCraft 节目监视器缩放档位：适配 + 百分比（相对「适配」尺寸）。 */
type MonitorZoom = 'fit' | number;
const MONITOR_ZOOM_OPTIONS: MonitorZoom[] = ['fit', 10, 25, 50, 75, 100, 150, 200, 400, 800, 1600];
/** 播放分辨率（预览降采样分母）：完整 / 1/2 / 1/4 / 1/8 / 1/16。 */
const MONITOR_RESOLUTION_OPTIONS = [1, 2, 4, 8, 16];
/** 标记三角配色（FilmCraft 用多彩标记；LC 的 markers 是 number[]，按顺序取色）。 */
const MONITOR_MARKER_COLORS = ['#9ac25e', '#e5534b', '#e8a33d', '#5a8dee', '#59b6e6', '#b98bdb'];
const MONITOR_HIDDEN_STORAGE_KEY = 'liveclipper.monitor.hiddenTransport';

const monitorZoomLabel = (zoom: MonitorZoom) => (zoom === 'fit' ? '适配' : `${zoom}%`);
const monitorResolutionLabel = (resolution: number) => (resolution === 1 ? '完整' : `1/${resolution}`);

interface MonitorProps {
  variant: 'source' | 'program';
  title: string;
  sourceUrl: string | null;
  sourceClipId?: string | null;
  videoRef: RefObject<HTMLVideoElement | null>;
  sourcePreviewRef?: RefObject<HTMLVideoElement | null>;
  sourceTime: number;
  sequenceTime: number;
  totalDuration: number;
  isPlaying: boolean;
  playbackError: string | null;
  width: number;
  height: number;
  fps: number;
  caption?: string | null;
  hidden?: boolean;
  /** 播放头停在空隙/没有片段的位置：画面应为纯黑（FilmCraft 的 gap 表现）。 */
  gapBlank?: boolean;
  previewItem?: TimelineItem | null;
  /** 当前节目预览片段在其自身输出时间轴上的位置（秒），用于参数关键帧插值。 */
  previewLocalTime?: number;
  baseOpacity?: number;
  overlays?: ProgramOverlay[];
  audioLayers?: ProgramAudioLayer[];
  baseAudioMuted?: boolean;
  sourceClips?: ClipDto[];
  selectedClipId?: string | null;
  /** 源监视器的源 In/Out 标记（三点编辑用；null = 未设）。 */
  sourceMarkIn?: number | null;
  sourceMarkOut?: number | null;
  /** FilmCraft 节目监视器播放控制条（transport）回调；不传则不渲染控制条。 */
  onAddMarker?: () => void;
  onMarkIn?: () => void;
  onMarkOut?: () => void;
  onGoToIn?: () => void;
  onGoToOut?: () => void;
  onStepFrames?: (frames: number) => void;
  onLift?: () => void;
  onExtract?: () => void;
  onExportFrame?: () => void;
  onToggleProxies?: () => void;
  /** 序列 In/Out（提升/提取与转到入/出点的可用状态）。 */
  workAreaIn?: number | null;
  workAreaOut?: number | null;
  /** 时间线标记（秒）；节目监视器在滑轨上方画彩色三角，对标 FilmCraft。 */
  markers?: number[];
  onSelectClip?: (clipId: string) => void;
  onTogglePlay: () => void;
  onSeek: (time: number) => void;
  onNotify?: (message: string) => void;
  /** 节目监视器蒙版编辑：指针抬起时一次性写回整组蒙版（单步撤销）。 */
  onUpdateMasks?: (itemId: string, masks: MaskInstance[] | null, label: string) => void;
}

function SourceScene() {
  return (
    <>
      <div className="source-scene" aria-hidden="true" />
      <svg className="monitor-art" viewBox="0 0 760 430" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
        <defs>
          <radialGradient id="sourceSunGlow" cx="50%" cy="50%" r="50%">
            <stop offset="0" stopColor="#ffe0a6" stopOpacity=".7" />
            <stop offset="1" stopColor="#ffe0a6" stopOpacity="0" />
          </radialGradient>
        </defs>
        <circle cx="585" cy="123" r="78" fill="url(#sourceSunGlow)" />
        <circle cx="585" cy="123" r="23" fill="#ffd69b" opacity=".75" />
        <g fill="#111a23" opacity=".88">
          <rect x="18" y="234" width="34" height="78" />
          <rect x="56" y="252" width="48" height="60" />
          <rect x="110" y="222" width="24" height="90" />
          <rect x="139" y="246" width="62" height="66" />
          <rect x="206" y="260" width="36" height="52" />
          <rect x="650" y="235" width="34" height="77" />
          <rect x="688" y="257" width="64" height="55" />
          <rect x="620" y="266" width="25" height="46" />
        </g>
        <path d="M0 307 L760 278 L760 298 L0 328 Z" fill="#171a1c" />
        <path d="M0 294 L760 265" stroke="#b18f68" strokeWidth="1" opacity=".35" />
        <g stroke="#b69470" strokeWidth="1.2" fill="none" opacity=".38">
          <path d="M20 281 L175 246 M175 246 L360 296 M360 296 L545 261 M545 261 L760 303" />
          <path d="M48 283 L175 252 M205 270 L360 302 M390 296 L545 267 M580 280 L748 305" />
        </g>
        <path d="M0 338 Q180 327 360 335 T760 326 L760 430 L0 430 Z" fill="#0b1117" opacity=".55" />
      </svg>
      <div className="scene-shade" aria-hidden="true" />
    </>
  );
}

function presetFilter(item: TimelineItem): string | undefined {
  if (item.filter === 'warm') return 'sepia(.22) saturate(1.12)';
  if (item.filter === 'cool') return 'hue-rotate(-12deg) saturate(.92)';
  if (item.filter === 'mono') return 'grayscale(1)';
  if (item.filter === 'vivid') return 'saturate(1.35) contrast(1.06)';
  return undefined;
}

function itemFilter(item: TimelineItem): string | undefined {
  // 预设滤镜先定基调，数值调色再叠加（顺序与后端 GradeFilter 一致）。
  return combineFilters(presetFilter(item), gradeCss(item.grade));
}

function itemTransform(item: TimelineItem, localTime = 0): string {
  const x = paramValueAtItem(item, 'positionX', localTime);
  const y = paramValueAtItem(item, 'positionY', localTime);
  const scale = paramValueAtItem(item, 'scale', localTime);
  const rotation = paramValueAtItem(item, 'rotation', localTime);
  return `translate(${x * 100}%, ${y * 100}%) scale(${scale}) rotate(${rotation}deg)`;
}

/** 参数关键帧相对片段静态值的不透明度倍率；无关键帧时为 1。 */
function paramOpacityRatio(item: TimelineItem, localTime: number): number {
  const base = baseParamValue(item, 'opacity');
  const value = paramValueAtItem(item, 'opacity', localTime);
  return base > 0 ? value / base : value;
}

/**
 * 节目监视器蒙版叠层 v1：仅绘制静态矩形/椭圆，用于对齐位置与大小；
 * 预览按帧等比拉伸（viewBox 100x100），不做像素级羽化模拟。
 * 可编辑模式下选中蒙版由 HTML 手柄层绘制，这里只画其余蒙版。
 */
function MaskOverlay({
  item,
  hiddenMaskId = null,
  onSelectMask,
}: {
  item: TimelineItem;
  hiddenMaskId?: string | null;
  onSelectMask?: (maskId: string) => void;
}) {
  const masks = (normalizeMasks(item) ?? []).filter((mask) => mask.enabled && mask.id !== hiddenMaskId);
  if (masks.length === 0) return null;
  return (
    <svg className="monitor-mask-overlay" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
      {masks.map((mask) => {
        const cx = (0.5 + mask.x) * 100;
        const cy = (0.5 + mask.y) * 100;
        const rx = Math.max(0.5, mask.w * 50);
        const ry = Math.max(0.5, mask.h * 50);
        const transform = `rotate(${mask.rotation} ${cx} ${cy})`;
        const strokeOpacity = Math.max(0.18, Math.min(0.85, mask.opacity * (1 - mask.feather / 160)));
        const common = {
          fill: 'rgba(255, 255, 255, 0.05)',
          stroke: 'rgba(255, 255, 255, 0.85)',
          strokeOpacity,
          strokeWidth: 0.22,
          strokeDasharray: '1.8 1.3',
          vectorEffect: 'non-scaling-stroke' as const,
          transform,
          style: onSelectMask ? { pointerEvents: 'stroke' as const, cursor: 'pointer' } : { pointerEvents: 'none' as const },
          onPointerDown: onSelectMask
            ? (event: ReactPointerEvent<SVGElement>) => {
                event.preventDefault();
                event.stopPropagation();
                onSelectMask(mask.id);
              }
            : undefined,
        };
        if (mask.type === 'polygon' && mask.points && mask.points.length >= 3) {
          const polygonPoints = mask.points
            .map((point) => `${point.x * 100},${point.y * 100}`)
            .join(' ');
          return <polygon key={mask.id} points={polygonPoints} {...common} transform={undefined} />;
        }
        return mask.type === 'ellipse' ? (
          <ellipse key={mask.id} cx={cx} cy={cy} rx={rx} ry={ry} {...common} />
        ) : (
          <rect key={mask.id} x={cx - rx} y={cy - ry} width={rx * 2} height={ry * 2} {...common} />
        );
      })}
    </svg>
  );
}

const MASK_MIN_SIZE = 0.02;
const MASK_HANDLES = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'] as const;
type MaskHandle = (typeof MASK_HANDLES)[number];

type MaskDragState = {
  mode: 'move' | 'resize' | 'rotate' | 'feather' | 'vertex' | 'poly-move';
  handle: string;
  startMask: MaskInstance;
  startClientX: number;
  startClientY: number;
  rect: DOMRect;
};

function clampNumber(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

function normalizeRotation(value: number) {
  return ((((value + 180) % 360) + 360) % 360) - 180;
}

function roundMask(mask: MaskInstance): MaskInstance {
  const round = (value: number) => Math.round(value * 1000) / 1000;
  return {
    ...mask,
    x: round(mask.x),
    y: round(mask.y),
    w: round(mask.w),
    h: round(mask.h),
    rotation: round(mask.rotation),
    feather: Math.round(clampNumber(mask.feather, 0, 100)),
    ...(mask.points
      ? {
          points: mask.points.map((point) => ({
            x: Math.round(clampNumber(point.x, 0, 1) * 1000) / 1000,
            y: Math.round(clampNumber(point.y, 0, 1) * 1000) / 1000,
          })),
        }
      : {}),
  };
}

function sameMask(a: MaskInstance, b: MaskInstance) {
  return (
    Math.abs(a.x - b.x) < 0.0005 &&
    Math.abs(a.y - b.y) < 0.0005 &&
    Math.abs(a.w - b.w) < 0.0005 &&
    Math.abs(a.h - b.h) < 0.0005 &&
    Math.abs(a.rotation - b.rotation) < 0.05 &&
    Math.abs((a.feather ?? 0) - (b.feather ?? 0)) < 0.05 &&
    (a.points ?? []).length === (b.points ?? []).length &&
    (a.points ?? []).every((point, index) => {
      const other = (b.points ?? [])[index];
      return other
        ? Math.abs(point.x - other.x) < 0.0005 && Math.abs(point.y - other.y) < 0.0005
        : false;
    })
  );
}

/** 按拖拽状态计算下一帧蒙版值；所有几何在“画面比例空间”计算（x/y 中心偏移，w/h 占比）。 */
function computeDragMask(drag: MaskDragState, clientX: number, clientY: number, shift: boolean): MaskInstance {
  const start = drag.startMask;
  const dx = (clientX - drag.startClientX) / Math.max(1, drag.rect.width);
  const dy = (clientY - drag.startClientY) / Math.max(1, drag.rect.height);

  if (drag.mode === 'vertex' || drag.mode === 'poly-move') {
    const points = start.points ?? [];
    if (drag.mode === 'vertex') {
      const index = Number(drag.handle);
      return {
        ...start,
        points: points.map((point, i) =>
          i === index
            ? { x: clampNumber(point.x + dx, 0, 1), y: clampNumber(point.y + dy, 0, 1) }
            : point,
        ),
      };
    }
    return {
      ...start,
      points: points.map((point) => ({
        x: clampNumber(point.x + dx, 0, 1),
        y: clampNumber(point.y + dy, 0, 1),
      })),
    };
  }

  if (drag.mode === 'feather') {
    // 羽化手柄：水平拖动 0–100%。拖到框外（向右/向下）变软，向左变硬。
    const delta = ((clientX - drag.startClientX) / Math.max(1, drag.rect.width)) * 100;
    return { ...start, feather: clampNumber(Math.round((start.feather ?? 0) + delta), 0, 100) };
  }

  if (drag.mode === 'move') {
    return {
      ...start,
      x: clampNumber(start.x + dx, -0.5, 0.5),
      y: clampNumber(start.y + dy, -0.5, 0.5),
    };
  }

  if (drag.mode === 'rotate') {
    const centerX = drag.rect.left + (0.5 + start.x) * drag.rect.width;
    const centerY = drag.rect.top + (0.5 + start.y) * drag.rect.height;
    const startAngle = Math.atan2(drag.startClientY - centerY, drag.startClientX - centerX);
    const angle = Math.atan2(clientY - centerY, clientX - centerX);
    let rotation = start.rotation + ((angle - startAngle) * 180) / Math.PI;
    if (shift) rotation = Math.round(rotation / 15) * 15;
    return { ...start, rotation: clampNumber(normalizeRotation(rotation), -180, 180) };
  }

  const radians = (start.rotation * Math.PI) / 180;
  const cos = Math.cos(radians);
  const sin = Math.sin(radians);
  const localDx = dx * cos + dy * sin;
  const localDy = -dx * sin + dy * cos;

  let left = start.x - start.w / 2;
  let right = start.x + start.w / 2;
  let top = start.y - start.h / 2;
  let bottom = start.y + start.h / 2;
  const handle = drag.handle as MaskHandle;
  if (handle.includes('e')) right += localDx;
  if (handle.includes('w')) left += localDx;
  if (handle.includes('s')) bottom += localDy;
  if (handle.includes('n')) top += localDy;

  if (right - left < MASK_MIN_SIZE) {
    if (handle.includes('e')) right = left + MASK_MIN_SIZE;
    else left = right - MASK_MIN_SIZE;
  }
  if (bottom - top < MASK_MIN_SIZE) {
    if (handle.includes('s')) bottom = top + MASK_MIN_SIZE;
    else top = bottom - MASK_MIN_SIZE;
  }

  let w = clampNumber(right - left, MASK_MIN_SIZE, 2);
  let h = clampNumber(bottom - top, MASK_MIN_SIZE, 2);
  if (shift && handle.length === 2) {
    const ratio = start.w / start.h;
    if (w / ratio >= h) h = w / ratio;
    else w = h * ratio;
    w = clampNumber(w, MASK_MIN_SIZE, 2);
    h = clampNumber(h, MASK_MIN_SIZE, 2);
    if (handle.includes('e')) left = right - w;
    else right = left + w;
    if (handle.includes('s')) top = bottom - h;
    else bottom = top + h;
  }

  return {
    ...start,
    // mask.x / mask.y 表示相对画面中心的偏移比例（0 = 居中）；left/right/top/bottom
    // 本身就是以中心为 0 的坐标，因此这里不能再减 0.5。
    x: clampNumber((left + right) / 2, -0.5, 0.5),
    y: clampNumber((top + bottom) / 2, -0.5, 0.5),
    w,
    h,
  };
}

/**
 * 可编辑蒙版叠层：拖动本体移动、8 个手柄缩放、顶部圆点旋转。
 * 拖动过程只更新本地 draft（实时预览），pointerup 时一次性提交整组蒙版，
 * 因此一次拖动只产生一步撤销；Esc 取消当次拖动且不写回。
 */
function MaskEditorLayer({
  mask,
  masks,
  isPlaying,
  focusNonce,
  onTogglePlay,
  onSelectMask,
  onCommit,
  onNotify,
}: {
  mask: MaskInstance;
  masks: MaskInstance[];
  isPlaying: boolean;
  focusNonce: number;
  onTogglePlay: () => void;
  onSelectMask: (maskId: string) => void;
  onCommit: (masks: MaskInstance[], label: string) => void;
  onNotify?: (message: string) => void;
}) {
  const layerRef = useRef<HTMLDivElement>(null);
  const dragRef = useRef<MaskDragState | null>(null);
  const draftRef = useRef<MaskInstance | null>(null);
  const [dragActive, setDragActive] = useState(false);
  const [draft, setDraft] = useState<MaskInstance | null>(null);
  const [focused, setFocused] = useState(false);

  const active = draft ?? mask;
  const maskEditorState = useMaskEditorState();
  const penMode = maskEditorState.penMode;
  const isPolygon = mask.type === 'polygon';

  useEffect(() => {
    if (!focusNonce) return;
    setFocused(true);
    const timer = window.setTimeout(() => setFocused(false), 1100);
    return () => window.clearTimeout(timer);
  }, [focusNonce]);

  useEffect(() => {
    const drag = dragRef.current;
    if (!drag) return;
    const handleMove = (event: PointerEvent) => {
      const next = computeDragMask(drag, event.clientX, event.clientY, event.shiftKey);
      draftRef.current = next;
      setDraft(next);
    };
    const cleanupListeners = () => {
      window.removeEventListener('pointermove', handleMove);
      window.removeEventListener('pointerup', handleUp);
      window.removeEventListener('pointercancel', handleCancel);
      document.removeEventListener('keydown', handleKey, true);
    };
    const handleUp = () => {
      cleanupListeners();
      const next = draftRef.current;
      dragRef.current = null;
      draftRef.current = null;
      setDragActive(false);
      setDraft(null);
      if (!next) return;
      const rounded = roundMask(next);
      if (sameMask(drag.startMask, rounded)) return;
      onCommit(
        masks.map((entry) => (entry.id === rounded.id ? rounded : entry)),
        drag.mode === 'move'
          ? '调整蒙版位置'
          : drag.mode === 'rotate'
            ? '调整蒙版旋转'
            : drag.mode === 'feather'
              ? '调整蒙版羽化'
              : drag.mode === 'vertex'
                ? '调整蒙版顶点'
                : drag.mode === 'poly-move'
                  ? '调整蒙版位置'
                  : '调整蒙版大小',
      );
    };
    const handleCancel = () => {
      cleanupListeners();
      dragRef.current = null;
      draftRef.current = null;
      setDragActive(false);
      setDraft(null);
      onNotify?.('已取消蒙版拖动，未写入工程');
    };
    const handleKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      event.stopPropagation();
      handleCancel();
    };
    window.addEventListener('pointermove', handleMove);
    window.addEventListener('pointerup', handleUp);
    window.addEventListener('pointercancel', handleCancel);
    document.addEventListener('keydown', handleKey, true);
    return cleanupListeners;
  }, [dragActive]); // eslint-disable-line react-hooks/exhaustive-deps

  const beginDrag = (event: ReactPointerEvent<Element>, mode: MaskDragState['mode'], handle = '') => {
    if (event.button !== 0) return;
    const layer = layerRef.current;
    if (!layer) return;
    event.preventDefault();
    event.stopPropagation();
    if (isPlaying) onTogglePlay();
    onSelectMask(mask.id);
    dragRef.current = {
      mode,
      handle,
      startMask: active,
      startClientX: event.clientX,
      startClientY: event.clientY,
      rect: layer.getBoundingClientRect(),
    };
    draftRef.current = active;
    setDraft(active);
    setDragActive(true);
  };

  const appendVertex = (clientX: number, clientY: number) => {
    const layer = layerRef.current;
    if (!layer) return;
    const rect = layer.getBoundingClientRect();
    const x = clampNumber((clientX - rect.left) / Math.max(1, rect.width), 0, 1);
    const y = clampNumber((clientY - rect.top) / Math.max(1, rect.height), 0, 1);
    const points = [...(active.points ?? [])];
    if (points.length >= MAX_MASK_POINTS) {
      onNotify?.(`多边形顶点最多 ${MAX_MASK_POINTS} 个`);
      return;
    }
    // 插到最近一条边的中点之后，避免新点总加在末尾造成自交。
    let insertAt = points.length;
    let best = Number.POSITIVE_INFINITY;
    for (let index = 0; index < points.length; index += 1) {
      const from = points[index];
      const to = points[(index + 1) % points.length];
      const midX = (from.x + to.x) / 2;
      const midY = (from.y + to.y) / 2;
      const distance = (midX - x) ** 2 + (midY - y) ** 2;
      if (distance < best) {
        best = distance;
        insertAt = index + 1;
      }
    }
    points.splice(insertAt, 0, {
      x: Math.round(x * 1000) / 1000,
      y: Math.round(y * 1000) / 1000,
    });
    onCommit(
      masks.map((entry) => (entry.id === mask.id ? roundMask({ ...active, points }) : entry)),
      '添加多边形顶点',
    );
    onNotify?.(`已添加多边形顶点（${points.length} 点）`);
  };

  const removeVertex = (index: number) => {
    const points = [...(active.points ?? [])];
    if (points.length <= 3) {
      onNotify?.('多边形至少保留 3 个顶点');
      return;
    }
    points.splice(index, 1);
    onCommit(
      masks.map((entry) => (entry.id === mask.id ? roundMask({ ...active, points }) : entry)),
      '删除多边形顶点',
    );
    onNotify?.(`已删除多边形顶点（剩 ${points.length} 点）`);
  };

  const boxStyle: CSSProperties = {
    left: `${(0.5 + active.x - active.w / 2) * 100}%`,
    top: `${(0.5 + active.y - active.h / 2) * 100}%`,
    width: `${active.w * 100}%`,
    height: `${active.h * 100}%`,
    transform: `rotate(${active.rotation}deg)`,
  };
  const shapeLabel = mask.type === 'ellipse' ? '椭圆' : mask.type === 'polygon' ? '多边形' : '矩形';
  const polyPoints = active.points ?? [];
  const polyBox = polyPoints.reduce(
    (acc, point) => ({
      minX: Math.min(acc.minX, point.x),
      maxX: Math.max(acc.maxX, point.x),
      minY: Math.min(acc.minY, point.y),
      maxY: Math.max(acc.maxY, point.y),
    }),
    { minX: 1, maxX: 0, minY: 1, maxY: 0 },
  );
  const polyFeatherRight = {
    left: `${polyBox.maxX * 100}%`,
    top: `${((polyBox.minY + polyBox.maxY) / 2) * 100}%`,
  };
  const polyFeatherBottom = {
    left: `${((polyBox.minX + polyBox.maxX) / 2) * 100}%`,
    top: `${polyBox.maxY * 100}%`,
  };

  return (
    <div ref={layerRef} className="monitor-mask-editor" data-mask-id={mask.id}>
      {isPolygon ? (
        <div className="mask-edit-poly">
          <svg
            className="mask-edit-poly-svg"
            viewBox="0 0 100 100"
            preserveAspectRatio="none"
            aria-hidden="true"
          >
            <polygon
              className="mask-edit-poly-path"
              data-testid="mask-edit-polygon"
              points={polyPoints.map((point) => `${point.x * 100},${point.y * 100}`).join(' ')}
              onPointerDown={(event) => beginDrag(event, 'poly-move')}
            />
          </svg>
          {polyPoints.map((point, index) => (
            <span
              key={`vertex-${index}`}
              className="mask-edit-vertex"
              data-testid="mask-vertex"
              data-vertex-index={index}
              style={{ left: `${point.x * 100}%`, top: `${point.y * 100}%` }}
              role="presentation"
              title="拖动移动顶点；右键删除顶点"
              onPointerDown={(event) => beginDrag(event, 'vertex', String(index))}
              onContextMenu={(event) => {
                event.preventDefault();
                event.stopPropagation();
                removeVertex(index);
              }}
            />
          ))}
          <span
            className="mask-edit-feather mask-edit-feather--poly"
            data-testid="mask-feather-handle"
            style={polyFeatherRight}
            role="presentation"
            title="拖动调整羽化（0–100%）"
            onPointerDown={(event) => beginDrag(event, 'feather', 'e')}
          />
          <span
            className="mask-edit-feather mask-edit-feather--poly"
            data-testid="mask-feather-handle"
            style={polyFeatherBottom}
            role="presentation"
            title="拖动调整羽化（0–100%）"
            onPointerDown={(event) => beginDrag(event, 'feather', 's')}
          />
          {penMode ? (
            <div
              className="mask-edit-pen-layer"
              data-testid="mask-pen-layer"
              title="钢笔：点击添加多边形顶点"
              onPointerDown={(event) => {
                if (event.button !== 0) return;
                event.preventDefault();
                event.stopPropagation();
                appendVertex(event.clientX, event.clientY);
              }}
            />
          ) : null}
          {focused ? <span className="mask-edit-poly-label">{shapeLabel}蒙版 · 拖顶点编辑</span> : null}
          {dragActive && dragRef.current?.mode === 'feather' ? (
            <span
              className="mask-edit-feather-badge"
              data-testid="mask-feather-badge"
              style={polyFeatherRight}
            >
              {Math.round(active.feather ?? 0)}%
            </span>
          ) : null}
        </div>
      ) : (
        <div
          className={`mask-edit-box${mask.type === 'ellipse' ? ' is-ellipse' : ''}${
            focused ? ' is-focused' : ''
          }${dragActive ? ' is-dragging' : ''}`}
          style={boxStyle}
          role="presentation"
          title="拖动蒙版移动；拖 8 个手柄缩放（Shift 等比）；拖顶部圆点旋转（Shift 15° 吸附）；Esc 取消"
          onPointerDown={(event) => beginDrag(event, 'move')}
        >
          {focused ? <span className="mask-edit-label">{shapeLabel}蒙版 · 拖动编辑</span> : null}
          {MASK_HANDLES.map((handle) => (
            <span
              key={handle}
              className={`mask-edit-handle mask-edit-handle--${handle}`}
              role="presentation"
              onPointerDown={(event) => beginDrag(event, 'resize', handle)}
            />
          ))}
          <span className="mask-edit-rotate-line" aria-hidden="true" />
          <span
            className="mask-edit-rotate"
            role="presentation"
            title="拖动旋转蒙版（Shift 15° 吸附）"
            onPointerDown={(event) => beginDrag(event, 'rotate')}
          />
          <span
            className="mask-edit-feather mask-edit-feather--e"
            role="presentation"
            data-testid="mask-feather-handle"
            title="拖动调整羽化（0–100%）"
            onPointerDown={(event) => beginDrag(event, 'feather', 'e')}
          />
          <span
            className="mask-edit-feather mask-edit-feather--s"
            role="presentation"
            data-testid="mask-feather-handle"
            title="拖动调整羽化（0–100%）"
            onPointerDown={(event) => beginDrag(event, 'feather', 's')}
          />
          {dragActive && dragRef.current?.mode === 'feather' ? (
            <span className="mask-edit-feather-badge" data-testid="mask-feather-badge">
              {Math.round(active.feather ?? 0)}%
            </span>
          ) : null}
        </div>
      )}
      <span className="mask-edit-hint">
        {isPolygon
          ? '多边形蒙版 · 拖顶点移动 · 右键删顶点 · 钢笔点击加顶点 · Esc 取消'
          : '蒙版编辑 · 8 手柄缩放 · 顶部圆点旋转 · 侧边圆点羽化 · Esc 取消'}
      </span>
    </div>
  );
}

function OverlayVideo({
  overlay,
  sequenceTime,
  isPlaying,
  audible,
}: {
  overlay: ProgramOverlay;
  sequenceTime: number;
  isPlaying: boolean;
  audible: boolean;
}) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [ready, setReady] = useState(false);
  const speed = Math.max(0.01, overlay.item.speed ?? 1);
  const localTime = Math.max(0, Math.min(overlay.duration, sequenceTime - overlay.sequenceStart));
  const sourceStart = overlay.item.start + localTime * speed;
  const style: CSSProperties = {
    transform: itemTransform(overlay.item, localTime),
    opacity: overlay.item.disabled
      ? 0
      : overlay.opacity *
        clipFadeFactor(overlay.item, localTime) *
        paramOpacityRatio(overlay.item, localTime),
    filter: itemFilter(overlay.item),
  };

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !ready) return;
    video.volume = Math.max(0, Math.min(1, overlay.volume));
    video.muted = !audible;
  }, [audible, overlay.volume, ready]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !ready) return;
    video.playbackRate = speed;
    const target = overlay.item.start + Math.max(0, Math.min(overlay.duration, sequenceTime - overlay.sequenceStart)) * speed;
    if (Math.abs(video.currentTime - target) > 0.22) video.currentTime = target;
  }, [overlay, ready, sequenceTime, speed]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !ready) return undefined;
    if (isPlaying) {
      let cancelled = false;
      void video.play().catch((reason: unknown) => {
        if (!cancelled && audible && reason instanceof DOMException && reason.name === 'NotAllowedError') {
          video.muted = true;
          void video.play().catch(() => undefined);
        }
      });
      const timer = window.setInterval(() => {
        const target = overlay.item.start + Math.max(0, Math.min(overlay.duration, sequenceTime - overlay.sequenceStart)) * speed;
        if (Math.abs(video.currentTime - target) > 0.35) video.currentTime = target;
      }, 500);
      return () => {
        cancelled = true;
        window.clearInterval(timer);
      };
    }
    video.pause();
    return undefined;
  }, [audible, isPlaying, overlay, ready, sequenceTime, speed]);

  return (
    <video
      ref={videoRef}
      className="monitor-overlay-video"
      src={overlay.url}
      style={style}
      muted={!audible}
      playsInline
      preload="auto"
      aria-label={`叠加轨 ${overlay.item.id}`}
      onLoadedMetadata={() => {
        if (videoRef.current) {
          videoRef.current.volume = Math.max(0, Math.min(1, overlay.volume));
          videoRef.current.muted = !audible;
          videoRef.current.playbackRate = speed;
          videoRef.current.currentTime = sourceStart;
          setReady(true);
        }
      }}
      onLoadedData={() => setReady(true)}
      onError={() => setReady(false)}
    />
  );
}

function ProgramTitle({ item }: { item: TimelineItem }) {
  if (!item.titleText?.trim()) return null;
  const style: CSSProperties = {
    left: `${(item.titleX ?? 0.5) * 100}%`,
    top: `${(item.titleY ?? 0.82) * 100}%`,
    color: item.titleColor ?? '#FFFFFF',
    fontSize: `${Math.max(12, Math.min(64, (item.titleSize ?? 48) * 0.6))}px`,
  };
  return (
    <div className="monitor-title" style={style}>
      {item.titleText}
    </div>
  );
}

function OverlayAudio({
  layer,
  sequenceTime,
  isPlaying,
}: {
  layer: ProgramAudioLayer;
  sequenceTime: number;
  isPlaying: boolean;
}) {
  const audioRef = useRef<HTMLAudioElement>(null);
  const [ready, setReady] = useState(false);
  const speed = Math.max(0.01, layer.item.speed ?? 1);
  const targetTime =
    layer.item.start + Math.max(0, Math.min(layer.duration, sequenceTime - layer.sequenceStart)) * speed;

  useEffect(() => {
    const audio = audioRef.current;
    if (!audio || !ready) return;
    audio.volume = Math.max(0, Math.min(1, layer.volume));
    audio.playbackRate = speed;
    if (Math.abs(audio.currentTime - targetTime) > 0.22) audio.currentTime = targetTime;
  }, [layer.volume, ready, speed, targetTime]);

  useEffect(() => {
    const audio = audioRef.current;
    if (!audio || !ready) return undefined;
    if (isPlaying) {
      void audio.play().catch(() => undefined);
      return undefined;
    }
    audio.pause();
    return undefined;
  }, [isPlaying, ready]);

  return (
    <audio
      ref={audioRef}
      className="monitor-audio-layer"
      src={layer.url}
      muted={layer.muted}
      preload="auto"
      aria-hidden="true"
      onLoadedMetadata={() => {
        if (audioRef.current) {
          audioRef.current.volume = Math.max(0, Math.min(1, layer.volume));
          audioRef.current.currentTime = targetTime;
          setReady(true);
        }
      }}
      onLoadedData={() => setReady(true)}
      onError={() => setReady(false)}
    />
  );
}

export function Monitor({
  variant,
  title,
  sourceUrl,
  sourceClipId = null,
  videoRef,
  sourcePreviewRef,
  sourceTime,
  sequenceTime,
  totalDuration,
  isPlaying,
  playbackError,
  width,
  height,
  fps,
  caption,
  hidden = false,
  gapBlank = false,
  previewItem = null,
  previewLocalTime = 0,
  baseOpacity = 1,
  overlays = [],
  audioLayers = [],
  baseAudioMuted = false,
  sourceClips = [],
  selectedClipId = null,
  sourceMarkIn = null,
  sourceMarkOut = null,
  onAddMarker,
  onMarkIn,
  onMarkOut,
  onGoToIn,
  onGoToOut,
  onStepFrames,
  onLift,
  onExtract,
  onExportFrame,
  onToggleProxies,
  workAreaIn = null,
  workAreaOut = null,
  markers = [],
  onSelectClip,
  onTogglePlay,
  onSeek,
  onNotify,
  onUpdateMasks,
}: MonitorProps) {
  const source = variant === 'source';

  const formatTimecode = (value: number) => formatTimecodeAt(value, fps);
  const [ready, setReady] = useState(false);
  const [videoError, setVideoError] = useState<string | null>(null);

  /**
   * 参数均衡试听（D95/D96）：只有当前节目片段**真的带了启用的 parametric_eq** 才创建 AudioContext；
   * 没有 EQ 时传 null 给运行时，它保证一次都不碰音频路由（测试 `eqAudition.test.ts` 第一条）。
   */
  const eqParams = useMemo(() => {
    if (source) return null;
    const effects = previewItem ? normalizeAudioEffects(previewItem) : [];
    const eq = effects.find((effect) => effect.type === 'parametric_eq' && effect.enabled !== false);
    return eq ? { ...(eq.params ?? {}) } : null;
  }, [previewItem, source]);
  const eqAuditionRef = useRef<EqAudition | null>(null);

  useEffect(() => {
    if (source) return undefined;
    const video = videoRef.current;
    if (!video) return undefined;
    if (!eqParams) {
      if (eqAuditionRef.current) {
        eqAuditionRef.current.apply(null);
      } else {
        // 没有 EQ 时连 audition 都不建：这里只留一个"确实没接管音频"的标记，方便排查"听不到 EQ"。
        (globalThis as { __lcEqAudition?: unknown }).__lcEqAudition = { active: false, nodes: 0, contextState: null, error: null };
      }
      return undefined;
    }
    if (!eqAuditionRef.current) {
      const env = browserAudioEnv();
      if (!env) return undefined;
      eqAuditionRef.current = createEqAudition(env, video);
    }
    eqAuditionRef.current.apply(eqParams);
    return undefined;
    // `ready` 也要进依赖：首次渲染时 videoRef 可能还没挂上，等到 metadata 到了再跑一次。
    // `isPlaying` 也进：浏览器要求“用户手势之后”才能启动 AudioContext，播放动作正好是这个手势，
    // 所以每次开播重放一次 apply，把挂起的 context resume 起来（否则用户会听到 EQ 之前的声音）。
  }, [eqParams, source, ready, isPlaying]);

  useEffect(() => () => { eqAuditionRef.current?.dispose(); eqAuditionRef.current = null; }, []);

  const [muted, setMuted] = useState(false);
  const overlayAudioEnabled = isPlaying && !muted && navigator.userActivation?.hasBeenActive === true;
  const [fit, setFit] = useState<'contain' | 'cover'>('contain');
  // FilmCraft 节目监视器底部信息条：缩放 / 播放分辨率 / 标记 / 按钮编辑器。
  const [zoom, setZoom] = useState<MonitorZoom>('fit');
  const [resolution, setResolution] = useState(1);
  const [zoomMenu, setZoomMenu] = useState(false);
  const [resolutionMenu, setResolutionMenu] = useState(false);
  const [editorMenu, setEditorMenu] = useState(false);
  const [settingsMenu, setSettingsMenu] = useState(false);
  const [showMarkers, setShowMarkers] = useState(true);
  const [hiddenTransport, setHiddenTransport] = useState<string[]>(() => {
    try {
      const raw = window.localStorage.getItem(MONITOR_HIDDEN_STORAGE_KEY);
      const parsed = raw ? (JSON.parse(raw) as unknown) : null;
      return Array.isArray(parsed) ? parsed.filter((entry): entry is string => typeof entry === 'string') : [];
    } catch {
      return [];
    }
  });
  const resolutionCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const [monitorMenu, setMonitorMenu] = useState<{ x: number; y: number } | null>(null);
  const panelRef = useRef<HTMLElement>(null);
  const maskEditorState = useMaskEditorState();
  const previewMasks = previewItem ? normalizeMasks(previewItem) ?? [] : [];
  const editingTarget =
    !source &&
    previewItem &&
    onUpdateMasks &&
    !isPlaying &&
    maskEditorState.target &&
    maskEditorState.target.itemId === previewItem.id &&
    maskEditorState.target.editable
      ? maskEditorState.target
      : null;
  const editingMask = editingTarget
    ? previewMasks.find((mask) => mask.id === editingTarget.maskId) ?? previewMasks[0] ?? null
    : null;
  const editingMaskId = editingMask?.id ?? null;


  const toggleFullscreen = () => {
    const node = panelRef.current;
    if (!node) return;
    if (document.fullscreenElement) {
      void document.exitFullscreen();
      return;
    }
    void node.requestFullscreen().catch(() => undefined);
  };

  useEffect(() => {
    setReady(false);
    setVideoError(null);
  }, [sourceUrl]);

  useEffect(() => {
    try {
      window.localStorage.setItem(MONITOR_HIDDEN_STORAGE_KEY, JSON.stringify(hiddenTransport));
    } catch {
      /* ignore */
    }
  }, [hiddenTransport]);

  useEffect(() => {
    if (!zoomMenu && !resolutionMenu && !editorMenu && !settingsMenu) return;
    const onPointerDown = (event: PointerEvent) => {
      const target = event.target as HTMLElement | null;
      if (target?.closest('.monitor-menu, .monitor-transport-button.is-editor')) return;
      setZoomMenu(false);
      setResolutionMenu(false);
      setEditorMenu(false);
      setSettingsMenu(false);
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    return () => document.removeEventListener('pointerdown', onPointerDown, true);
  }, [zoomMenu, resolutionMenu, editorMenu, settingsMenu]);

  // 播放分辨率：把 <video> 画进低分辨率 canvas，CSS 放大时用 pixelated，复刻 FilmCraft 的预览降采样观感。
  useEffect(() => {
    if (source || resolution <= 1 || !isPlaying) return;
    const canvas = resolutionCanvasRef.current;
    const video = videoRef.current;
    if (!canvas || !video) return;
    const context = canvas.getContext('2d');
    if (!context) return;
    let frame = 0;
    const loop = () => {
      if (video.readyState >= 2) {
        try {
          context.drawImage(video, 0, 0, canvas.width, canvas.height);
        } catch {
          /* ignore */
        }
      }
      frame = window.requestAnimationFrame(loop);
    };
    frame = window.requestAnimationFrame(loop);
    return () => window.cancelAnimationFrame(frame);
  }, [source, resolution, isPlaying, videoRef]);

  useEffect(() => {
    if (source || resolution <= 1 || isPlaying) return;
    const canvas = resolutionCanvasRef.current;
    const video = videoRef.current;
    if (!canvas || !video || video.readyState < 2) return;
    try {
      canvas.getContext('2d')?.drawImage(video, 0, 0, canvas.width, canvas.height);
    } catch {
      /* ignore */
    }
  }, [source, resolution, isPlaying, sequenceTime, videoRef]);

  useEffect(() => {
    if (!source || !sourcePreviewRef?.current) return;
    const video = sourcePreviewRef.current;
    if (video.readyState < 1) return;
    if (Math.abs(video.currentTime - sourceTime) > 0.2) {
      video.currentTime = Math.min(sourceTime, video.duration || sourceTime);
    }
  }, [source, sourcePreviewRef, sourceTime]);

  const progress = totalDuration > 0 ? Math.min(100, (sequenceTime / totalDuration) * 100) : 0;
  const rangeStyle = { '--range-progress': `${progress}%` } as CSSProperties;
  const previewStyle: CSSProperties | undefined = !source
    ? previewItem
      ? {
          transform: itemTransform(previewItem, previewLocalTime),
          opacity: previewItem.disabled
            ? 0
            : (previewItem.opacity ?? 1) * baseOpacity * paramOpacityRatio(previewItem, previewLocalTime),
          filter: itemFilter(previewItem),
        }
      : { opacity: 0 }
    : undefined;
  const zoomFactor = zoom === 'fit' ? null : zoom / 100;
  const programVideoStyle: CSSProperties | undefined =
    source || zoomFactor === null
      ? previewStyle
      : {
          ...previewStyle,
          inset: 'auto',
          left: '50%',
          top: '50%',
          width: Math.max(1, Math.round((width || 1280) * zoomFactor)),
          height: Math.max(1, Math.round((height || 720) * zoomFactor)),
          objectFit: 'contain',
          transform: `${previewStyle?.transform ? `${previewStyle.transform} ` : ''}translate(-50%, -50%)`,
        };
  const workAreaInvalid = !workAreaIn || !workAreaOut || workAreaOut <= workAreaIn;
  type TransportButton = {
    id: string;
    testId: string;
    title: string;
    label: string;
    icon: ReactNode;
    onClick?: () => void;
    disabled?: boolean;
  };
  const transportButtons: TransportButton[] = [
    { id: 'marker', testId: 'transport-marker', title: '添加标记（M）', label: '添加标记', icon: <Flag size={13} />, onClick: onAddMarker },
    { id: 'mark-in', testId: 'transport-mark-in', title: '标记入点（I）', label: '标记入点', icon: <span className="monitor-transport-glyph">&#123;</span>, onClick: onMarkIn },
    { id: 'mark-out', testId: 'transport-mark-out', title: '标记出点（O）', label: '标记出点', icon: <span className="monitor-transport-glyph">&#125;</span>, onClick: onMarkOut },
    { id: 'go-in', testId: 'transport-go-in', title: '转到入点（Shift+I）', label: '转到入点', icon: <ChevronLeft size={13} />, onClick: onGoToIn },
    { id: 'step-back', testId: 'transport-step-back', title: '后退 1 帧（左方向键）', label: '后退 1 帧', icon: <StepBack size={13} />, onClick: onStepFrames ? () => onStepFrames(-1) : undefined },
    { id: 'play', testId: 'transport-play', title: '播放/停止切换（空格）', label: '播放/停止', icon: isPlaying ? <Pause size={13} fill="currentColor" /> : <Play size={13} fill="currentColor" />, onClick: onTogglePlay },
    { id: 'step-forward', testId: 'transport-step-forward', title: '前进 1 帧（右方向键）', label: '前进 1 帧', icon: <StepForward size={13} />, onClick: onStepFrames ? () => onStepFrames(1) : undefined },
    { id: 'go-out', testId: 'transport-go-out', title: '转到出点（Shift+O）', label: '转到出点', icon: <ChevronRight size={13} />, onClick: onGoToOut },
    { id: 'lift', testId: 'transport-lift', title: '提升（;）', label: '提升', icon: <ArrowUpFromLine size={13} />, onClick: onLift, disabled: workAreaInvalid },
    { id: 'extract', testId: 'transport-extract', title: '提取（\'）', label: '提取', icon: <ArrowUpToLine size={13} />, onClick: onExtract, disabled: workAreaInvalid },
    { id: 'export-frame', testId: 'transport-export-frame', title: '导出静帧', label: '导出静帧', icon: <Camera size={13} />, onClick: onExportFrame },
    { id: 'proxies', testId: 'transport-proxies', title: '切换代理文件', label: '切换代理文件', icon: <Replace size={13} />, onClick: onToggleProxies },
  ];
  const error = playbackError ?? videoError;

  return (
    <section
      className="panel monitor"
      data-testid={source ? "source-monitor" : "program-monitor"}
      data-source-clip-id={sourceClipId ?? undefined}
      ref={panelRef}
      onContextMenu={
        source
          ? undefined
          : (event) => {
              event.preventDefault();
              setMonitorMenu({ x: event.clientX, y: event.clientY });
            }
      }
    >
      <div className="panel-head">
        <span className="panel-tab is-active">
          {source ? `源：${title}` : `节目：${title} · 序列 01`}
        </span>
        {source && sourceClips.length > 0 && onSelectClip ? (
          <select
            className="monitor-source-select"
            value={selectedClipId ?? ''}
            onChange={(event) => onSelectClip(event.target.value)}
            aria-label="切换源素材"
            title="切换源素材"
          >
            {sourceClips.map((clip) => (
              <option key={clip.id} value={clip.id}>{clipTitle(clip)}</option>
            ))}
          </select>
        ) : null}
        <span className="panel-tab">{source ? '源监视器' : '节目监视器'}</span>
      </div>
      <div className="monitor-body">
        <div className={`screen${ready ? ' is-ready' : ''}${!source && fit === 'cover' ? ' is-cover' : ''}`}>
          {source ? <SourceScene /> : null}
          {(!source || sourceUrl) ? (
            <video
              ref={source ? sourcePreviewRef : videoRef}
              className={`monitor-video${ready ? ' is-ready' : ''}${!source && fit === 'cover' ? ' is-cover' : ''}`}
              style={source ? previewStyle : programVideoStyle}
              src={sourceUrl ?? undefined}
              muted={source || muted || (!source && baseAudioMuted)}
              playsInline
              preload="auto"
              aria-label={source ? '源素材预览' : '节目播放器'}
              onLoadedMetadata={() => setReady(true)}
              onLoadedData={() => setReady(true)}
              onCanPlay={() => setReady(true)}
              onPlaying={() => setReady(true)}
              onError={() => setVideoError('视频无法加载')}
              onClick={source ? undefined : onTogglePlay}
            />
          ) : null}
          {!source && resolution > 1 ? (
            <canvas
              ref={resolutionCanvasRef}
              className="monitor-resolution-canvas"
              data-testid="monitor-resolution-canvas"
              width={Math.max(1, Math.round((width || 1280) / resolution))}
              height={Math.max(1, Math.round((height || 720) / resolution))}
              aria-hidden="true"
            />
          ) : null}
          {!source && gapBlank ? <div className="monitor-gap-blank" aria-hidden="true" /> : null}
          {!source && previewItem ? (
            <MaskOverlay
              item={previewItem}
              hiddenMaskId={editingMaskId}
              onSelectMask={editingTarget ? (maskId) => maskEditor.selectMask(maskId) : undefined}
            />
          ) : null}
          {!source && previewItem && editingMask && onUpdateMasks ? (
            <MaskEditorLayer
              mask={editingMask}
              masks={previewMasks}
              isPlaying={isPlaying}
              focusNonce={maskEditorState.focus?.maskId === editingMask.id ? maskEditorState.focus.nonce : 0}
              onTogglePlay={onTogglePlay}
              onSelectMask={(maskId) => maskEditor.selectMask(maskId)}
              onCommit={(nextMasks, label) => onUpdateMasks(previewItem.id, nextMasks, label)}
              onNotify={onNotify}
            />
          ) : null}
          {!source
            ? overlays.map((overlay) => (
                <OverlayVideo
                  key={overlay.id}
                  overlay={overlay}
                  sequenceTime={sequenceTime}
                  isPlaying={isPlaying}
                  audible={overlay.audible && overlayAudioEnabled}
                />
              ))
            : null}
          {!source
            ? overlays.filter((overlay) => overlay.item.titleText?.trim()).map((overlay) => (
                <ProgramTitle key={`title-${overlay.id}`} item={overlay.item} />
              ))
            : null}
          {!source
            ? audioLayers.map((layer) => (
                <OverlayAudio
                  key={layer.id}
                  layer={layer}
                  sequenceTime={sequenceTime}
                  isPlaying={isPlaying}
                />
              ))
            : null}
          {!source && previewItem?.disabled ? (
            <div className="monitor-hidden-shade" role="status">
              <span>片段已禁用</span>
            </div>
          ) : null}
          {!source && hidden ? (
            <div className="monitor-hidden-shade" role="status">
              <span>V1 已隐藏</span>
            </div>
          ) : null}
          {!source && caption ? (
            <div className="program-caption">
              <span>{caption}</span>
            </div>
          ) : null}
          <span className="monitor-tag">
            {source ? `V1 · ${formatTimecode(sourceTime)}` : `序列 01 · ${formatTimecode(sequenceTime)}`}
          </span>
          <span className="monitor-slug">
            {source ? '原始画质' : '节目预览'} · {width}×{height} · {fps}p
          </span>
          <span className="monitor-time">{formatTimecode(source ? sourceTime : sequenceTime)}</span>
          {!sourceUrl ? <div className="monitor-empty">还没有可播放的视频</div> : null}
          {error ? <div className="monitor-empty is-error">{error}</div> : null}
          {!source ? <div className="program-frame" aria-hidden="true" /> : null}
        </div>

        {source ? (
          <div className="monitor-toolbar">            <span className="timecode">{formatTimecode(sourceTime)}</span>
            <span className="toolbar-separator" />
            <span className="monitor-readonly">源预监 · 跟随节目</span>
            <span className="monitor-marks" aria-label="源 In/Out 标记">
              <span
                className={`monitor-mark${typeof sourceMarkIn === 'number' ? ' is-set' : ''}`}
                data-testid="source-mark-in"
              >
                In {typeof sourceMarkIn === 'number' ? formatTimecode(sourceMarkIn) : '—'}
              </span>
              <span
                className={`monitor-mark${typeof sourceMarkOut === 'number' ? ' is-set' : ''}`}
                data-testid="source-mark-out"
              >
                Out {typeof sourceMarkOut === 'number' ? formatTimecode(sourceMarkOut) : '—'}
              </span>
            </span>
            <span className="monitor-toolbar-right">
              <span>原片</span>
              <button type="button" className="icon-button monitor-control" onClick={toggleFullscreen} aria-label="全屏源监视器" title="全屏源监视器">
                <Maximize2 size={11} />
              </button>
              <span>{formatTimecode(totalDuration)}</span>
            </span>
          </div>
        ) : (
          <>
            <div className="monitor-footer-bar">
              <div className="monitor-transport" role="toolbar" aria-label="播放控制条">
                {transportButtons
                  .filter((button) => button.onClick && !hiddenTransport.includes(button.id))
                  .map((button) => (
                    <button
                      key={button.id}
                      type="button"
                      className="monitor-transport-button"
                      data-testid={button.testId}
                      onClick={button.onClick}
                      disabled={button.disabled}
                      title={button.title}
                      aria-label={button.title}
                    >
                      {button.icon}
                    </button>
                  ))}
                <div className="monitor-menu monitor-transport-editor-wrap">
                  <button
                    type="button"
                    className="monitor-transport-button is-editor"
                    data-testid="transport-editor"
                    title="编辑按钮（显示/隐藏）"
                    aria-label="编辑播放控制条按钮"
                    aria-haspopup="dialog"
                    aria-expanded={editorMenu}
                    onClick={() => {
                      setEditorMenu((current) => !current);
                      setSettingsMenu(false);
                    }}
                  >
                    <Plus size={13} />
                  </button>
                  {editorMenu ? (
                    <div className="monitor-popover is-editor" role="dialog" aria-label="播放控制条按钮">
                      <span className="monitor-popover-head">播放控制条按钮</span>
                      {transportButtons.map((button) => (
                        <label key={button.id} className="monitor-popover-check">
                          <input
                            type="checkbox"
                            checked={!hiddenTransport.includes(button.id)}
                            onChange={() =>
                              setHiddenTransport((current) =>
                                current.includes(button.id)
                                  ? current.filter((entry) => entry !== button.id)
                                  : [...current, button.id],
                              )
                            }
                          />
                          {button.label}
                        </label>
                      ))}
                    </div>
                  ) : null}
                </div>
                <span className="monitor-footer-spacer" />
                <div className="monitor-menu">
                  <button
                    type="button"
                    className="monitor-dropdown"
                    data-testid="monitor-zoom"
                    title="监视器缩放"
                    aria-label={`${monitorZoomLabel(zoom)} 监视器缩放`}
                    aria-haspopup="listbox"
                    aria-expanded={zoomMenu}
                    onClick={() => {
                      setZoomMenu((current) => !current);
                      setResolutionMenu(false);
                      setEditorMenu(false);
                      setSettingsMenu(false);
                    }}
                  >
                    {monitorZoomLabel(zoom)}
                    <ChevronDown size={11} />
                  </button>
                  {zoomMenu ? (
                    <div className="monitor-popover" role="listbox" aria-label="监视器缩放选项">
                      <span className="monitor-popover-head">缩放</span>
                      {MONITOR_ZOOM_OPTIONS.map((option) => (
                        <button
                          key={String(option)}
                          type="button"
                          role="option"
                          aria-selected={option === zoom}
                          className={`monitor-popover-item${option === zoom ? ' is-active' : ''}`}
                          data-testid={`monitor-zoom-${option === 'fit' ? 'fit' : option}`}
                          onClick={() => {
                            setZoom(option);
                            setZoomMenu(false);
                          }}
                        >
                          {monitorZoomLabel(option)}
                        </button>
                      ))}
                    </div>
                  ) : null}
                </div>
                <div className="monitor-menu">
                  <button
                    type="button"
                    className="monitor-dropdown"
                    data-testid="monitor-resolution"
                    title="播放分辨率"
                    aria-label={`${monitorResolutionLabel(resolution)} 播放分辨率`}
                    aria-haspopup="listbox"
                    aria-expanded={resolutionMenu}
                    onClick={() => {
                      setResolutionMenu((current) => !current);
                      setZoomMenu(false);
                      setEditorMenu(false);
                      setSettingsMenu(false);
                    }}
                  >
                    {monitorResolutionLabel(resolution)}
                    <ChevronDown size={11} />
                  </button>
                  {resolutionMenu ? (
                    <div className="monitor-popover" role="listbox" aria-label="播放分辨率选项">
                      <span className="monitor-popover-head">播放分辨率</span>
                      {MONITOR_RESOLUTION_OPTIONS.map((option) => (
                        <button
                          key={option}
                          type="button"
                          role="option"
                          aria-selected={option === resolution}
                          className={`monitor-popover-item${option === resolution ? ' is-active' : ''}`}
                          data-testid={`monitor-resolution-${option}`}
                          onClick={() => {
                            setResolution(option);
                            setResolutionMenu(false);
                          }}
                        >
                          {monitorResolutionLabel(option)}
                        </button>
                      ))}
                    </div>
                  ) : null}
                </div>
                <div className="monitor-menu">
                  <button
                    type="button"
                    className="monitor-footer-icon"
                    data-testid="monitor-settings"
                    title="监视器设置"
                    aria-label="监视器设置"
                    aria-haspopup="dialog"
                    aria-expanded={settingsMenu}
                    onClick={() => {
                      setSettingsMenu((current) => !current);
                      setZoomMenu(false);
                      setResolutionMenu(false);
                      setEditorMenu(false);
                    }}
                  >
                    <Wrench size={12} />
                  </button>
                  {settingsMenu ? (
                    <div className="monitor-popover is-settings" role="dialog" aria-label="监视器设置选项">
                      <span className="monitor-popover-head">监视器设置</span>
                      <label className="monitor-popover-check">
                        <input type="checkbox" checked={showMarkers} onChange={(event) => setShowMarkers(event.target.checked)} />
                        显示标记
                      </label>
                      <button
                        type="button"
                        className="monitor-popover-item"
                        onClick={() => {
                          setZoom('fit');
                          setSettingsMenu(false);
                        }}
                      >
                        复位缩放（适配）
                      </button>
                      <span className="monitor-popover-hint">播放分辨率只影响预览观感，导出不受影响。</span>
                    </div>
                  ) : null}
                </div>
                <span className="monitor-toolbar-right monitor-transport-legacy">
                  <button type="button" className="icon-button monitor-control" onClick={onTogglePlay} aria-label={isPlaying ? '暂停' : '播放'}>
                    {isPlaying ? <Pause size={11} fill="currentColor" /> : <Play size={11} fill="currentColor" />}
                  </button>
                  <button type="button" className="icon-button monitor-control" onClick={() => onSeek(sequenceTime - 5)} aria-label="后退五秒">
                    <SkipBack size={11} />
                  </button>
                  <button type="button" className="icon-button monitor-control" onClick={() => onSeek(sequenceTime + 5)} aria-label="前进五秒">
                    <SkipForward size={11} />
                  </button>
                  <button
                    type="button"
                    className="icon-button monitor-control"
                    onClick={() => setMuted((current) => !current)}
                    aria-label={muted ? '取消静音' : '静音节目'}
                    title={muted ? '取消静音' : '静音节目'}
                  >
                    {muted ? <VolumeX size={11} /> : <Volume2 size={11} />}
                  </button>
                  <button
                    type="button"
                    className="monitor-mode-button"
                    onClick={() => setFit((current) => (current === 'contain' ? 'cover' : 'contain'))}
                    aria-label={fit === 'contain' ? '适合（填充画面）' : '填充（裁切画面）'}
                    title={fit === 'contain' ? '填充画面' : '适合画面'}
                  >
                    {fit === 'contain' ? '适合' : '填充'}
                  </button>
                  <button type="button" className="icon-button monitor-control" onClick={toggleFullscreen} aria-label="全屏节目监视器" title="全屏节目监视器">
                    <Maximize2 size={11} />
                  </button>
                  <span>{fps} fps</span>
                </span>
              </div>
              <div className="monitor-scrubber-wrap monitor-scrubber-wrap--fc">
                <span className="monitor-footer-time" data-testid="monitor-current-time">{formatTimecode(sequenceTime)}</span>
                <div className="monitor-scrubber-track" data-testid="monitor-scrubber-track">
                  {showMarkers
                    ? markers.map((marker, index) => (
                        <span
                          key={`${marker}-${index}`}
                          className="monitor-scrubber-marker"
                          data-testid="monitor-scrubber-marker"
                          data-marker-time={marker}
                          title={`标记 ${formatTimecode(marker)}`}
                          style={{
                            left: `${totalDuration > 0 ? Math.min(100, Math.max(0, (marker / totalDuration) * 100)) : 0}%`,
                            background: MONITOR_MARKER_COLORS[index % MONITOR_MARKER_COLORS.length],
                          }}
                        />
                      ))
                    : null}
                  {typeof workAreaIn === 'number' ? (
                    <span
                      className="monitor-scrubber-io is-in"
                      data-testid="monitor-scrubber-in"
                      style={{ left: `${(workAreaIn / Math.max(totalDuration, 0.01)) * 100}%` }}
                    >
                      &#123;
                    </span>
                  ) : null}
                  {typeof workAreaOut === 'number' ? (
                    <span
                      className="monitor-scrubber-io is-out"
                      data-testid="monitor-scrubber-out"
                      style={{ left: `${(workAreaOut / Math.max(totalDuration, 0.01)) * 100}%` }}
                    >
                      &#125;
                    </span>
                  ) : null}
                  <input
                    className="monitor-scrubber"
                    style={rangeStyle}
                    type="range"
                    min={0}
                    max={Math.max(totalDuration, 0.01)}
                    step={0.01}
                    value={Math.min(sequenceTime, totalDuration)}
                    onChange={(event) => onSeek(Number(event.target.value))}
                    aria-label="节目播放位置"
                  />
                  <span className="monitor-scrubber-handle" style={{ left: `${progress}%` }} aria-hidden="true" />
                </div>
                <span className="monitor-footer-duration" data-testid="monitor-duration">{formatTimecode(totalDuration)}</span>
              </div>
            </div>


          </>
        )}
      </div>

      {monitorMenu ? (
        <EditorContextMenu
          x={monitorMenu.x}
          y={monitorMenu.y}
          title={`节目监视器 · ${formatTimecode(sequenceTime)}`}
          ariaLabel="节目监视器操作菜单"
          onClose={() => setMonitorMenu(null)}
          items={[
            {
              kind: 'item',
              label: '复制当前时间码',
              onSelect: () => {
                void copyTextToClipboard(formatTimecode(sequenceTime)).then((copied) =>
                  onNotify?.(
                    copied
                      ? `已复制时间码 ${formatTimecode(sequenceTime)}`
                      : '复制时间码失败，请检查剪贴板权限',
                  ),
                );
              },
            },
            { kind: 'item', label: '跳转到序列起点', onSelect: () => onSeek(0) },
            { kind: 'item', label: '跳转到序列终点', onSelect: () => onSeek(totalDuration) },
            { kind: 'separator' },
            {
              kind: 'item',
              label: '适合画面',
              checked: fit === 'contain',
              onSelect: () => setFit('contain'),
            },
            {
              kind: 'item',
              label: '填充画面',
              checked: fit === 'cover',
              onSelect: () => setFit('cover'),
            },
            { kind: 'item', label: '全屏监视器', onSelect: toggleFullscreen },
            { kind: 'separator' },
            {
              kind: 'item',
              label: '安全框 / 参考线',
              disabled: true,
              disabledReason: '节目监视器暂不支持安全框叠加',
            },
          ]}
        />
      ) : null}
    </section>
  );
}
