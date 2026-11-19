import { useEffect, useLayoutEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { X } from 'lucide-react';
import type { EditorTransition, TimelineItem } from '../../../api/types';
import { formatTimecode, trackOf, type TransitionDefaults } from '../project';
import { AudioLevelPanel } from './workspace/AudioLevelPanel';
import { MetadataPanel } from './workspace/MetadataPanel';
import { TextPanel } from './workspace/TextPanel';
import { TransitionsPanel } from './workspace/TransitionsPanel';
import { EffectControlsPanel } from './workspace/EffectControlsPanel';
import { Field, Slider } from './workspace/ui';
import { EDITOR_TOOL_OPTIONS } from './workspace/tools';
import type { WorkspaceClip, WorkspaceProject } from './workspace/types';
import { WORKSPACES, type WorkspaceId } from '../workspaces';
import type { EditorTool } from './ToolRail';

export type WorkspacePanelId =
  | 'workspaces'
  | 'new-project'
  | 'project'
  | 'media'
  | 'library'
  | 'info'
  | 'effects'
  | 'transitions'
  | 'markers'
  | 'history'
  | 'source'
  | 'effect-controls'
  | 'audio-clip-mixer'
  | 'metadata'
  | 'program'
  | 'timeline'
  | 'tools'
  | 'audio-level'
  | 'audio-track-mixer'
  | 'lumetri-color'
  | 'lumetri-scopes'
  | 'graphics'
  | 'essential-sound'
  | 'tts'
  | 'properties'
  | 'text'
  | 'events'
  | 'progress'
  | 'reference'
  | 'timecode'
  | 'project-notes';

export const WORKSPACE_PANEL_LABELS: Record<WorkspacePanelId, string> = {
  workspaces: '工作区',
  'new-project': '新建项目面板',
  project: '项目',
  media: '媒体浏览器',
  library: '库',
  info: '信息',
  effects: '效果',
  transitions: '转场',
  markers: '标记',
  history: '历史记录',
  source: '源',
  'effect-controls': '效果控件',
  'audio-clip-mixer': '音频剪辑混音器',
  metadata: '元数据',
  program: '节目',
  timeline: '时间轴',
  tools: '工具',
  'audio-level': '音频电平表',
  'audio-track-mixer': '音频轨道混音器',
  'lumetri-color': 'Lumetri 颜色',
  'lumetri-scopes': 'Lumetri 示波器',
  graphics: '基本图形',
  'essential-sound': '基本声音',
  tts: '文字转语音',
  properties: '属性',
  text: '文本',
  events: '事件',
  progress: '进度',
  reference: '参考监视器',
  timecode: '时间码',
  'project-notes': '项目备注',
};

export const WORKSPACE_PANEL_IDS = Object.keys(WORKSPACE_PANEL_LABELS) as WorkspacePanelId[];

let workspaceWindowZ = 400;
function raiseWorkspaceWindow() {
  workspaceWindowZ += 1;
  return workspaceWindowZ;
}

export type { WorkspaceClip } from './workspace/types';

type WorkspacePanelWindowProps = {
  id: WorkspacePanelId;
  stackIndex?: number;
  suspended?: boolean;
  docked?: boolean;
  project: WorkspaceProject | null;
  selectedClip: WorkspaceClip | null;
  selectedItem: TimelineItem | null;
  items: TimelineItem[];
  clips: WorkspaceClip[];
  markers: number[];
  transitions: EditorTransition[];
  sourceDurations?: Record<string, number>;
  selectedTransitionId: string | null;
  transitionDefaults: TransitionDefaults;
  onChangeTransitionDefaults: (next: TransitionDefaults) => void;
  sourceTime: number;
  sequenceTime: number;
  sourceDuration: number;
  totalDuration: number;
  transcriptCount: number;
  onClose: () => void;
  onSeek: (time: number) => void;
  onSeekSource: (time: number) => void;
  onUpdateItem: (patch: Partial<TimelineItem>, label: string) => void;
  onSelectClip: (id: string) => void;
  onSelectTransition: (transitionId: string | null) => void;
  onApplyTransition: (itemId: string | null) => void;
  onRemoveTransition: (transitionId: string) => void;
  onUpdateTransition: (
    transitionId: string,
    patch: Partial<Pick<EditorTransition, 'kind' | 'duration' | 'align'>>,
  ) => void;
  onAddMarker: (time?: number) => void;
  onClearMarkers: () => void;
  onRemoveMarker: (index: number) => void;
  onOpenHistory: () => void;
  onOpenStudio: () => void;
  onOpenLive: () => void;
  onOpenAi: (tab: 'candidates' | 'transcript' | 'chapters') => void;
  onSelectTool: (tool: EditorTool) => void;
  activeTool?: EditorTool;
  onToggleTrack: (field: 'mutedTracks' | 'soloedTracks', track: number) => void;
  activeWorkspaceId: WorkspaceId;
  onApplyWorkspace: (id: WorkspaceId) => void;
  onResetWorkspace: () => void;
};

const FILTERS = [
  { value: 'none', label: '原片' },
  { value: 'warm', label: '暖色' },
  { value: 'cool', label: '冷色' },
  { value: 'mono', label: '黑白' },
  { value: 'vivid', label: '鲜艳' },
] as const;

/**
 * 预览版窗口：只展示已接入的数据，缺失能力明确说明，不做假采样/假进度。
 * 菜单入口同步带「（预览版）」后缀（MenuBar.tsx）。
 */
const PREVIEW_PANELS: Partial<Record<WorkspacePanelId, { badge: string; note: string }>> = {
  'lumetri-scopes': {
    badge: '预览版',
    note: '示波器真实采样尚未接入：这里只展示节目时间与面板结构，不会伪造波形/矢量/直方图数据。',
  },
  progress: {
    badge: '预览版',
    note: '窗口内展示当前工程摘要。',
  },
  reference: {
    badge: '预览版',
    note: '参考监视器为只读占位：独立监看窗与参考画面素材尚未接入，画幅以工程分辨率为准。',
  },
};

/* ── 浮动窗口位置：默认贴右上、按标尺让出底部空间，并记忆上次位置 ───── */

const WINDOW_POSITION_STORAGE_PREFIX = 'liveclipper.editor.window-position.v1';
const WINDOW_POSITION_MARGIN = 24;
const WINDOW_MIN_VISIBLE = 160;
const EFFECT_CONTROLS_ID: WorkspacePanelId = 'effect-controls';

function windowPositionKey(id: WorkspacePanelId): string {
  // key 带页面：同一面板在不同页面复用位置记忆时互不影响。
  const page = window.location.pathname || '/';
  return `${WINDOW_POSITION_STORAGE_PREFIX}:${page}:${id}`;
}

function readStoredWindowPosition(id: WorkspacePanelId): { x: number; y: number } | null {
  try {
    const raw = window.localStorage.getItem(windowPositionKey(id));
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return null;
    const { x, y } = parsed as { x?: unknown; y?: unknown };
    if (typeof x !== 'number' || typeof y !== 'number' || !Number.isFinite(x) || !Number.isFinite(y)) return null;
    return { x, y };
  } catch {
    return null;
  }
}

function persistWindowPosition(id: WorkspacePanelId, position: { x: number; y: number }): void {
  try {
    window.localStorage.setItem(windowPositionKey(id), JSON.stringify(position));
  } catch {
    /* 位置记忆属于增强能力：localStorage 不可用时静默降级，不影响窗口本身。 */
  }
}

function windowWidthForViewport(): number {
  const bounds = shellBounds();
  const available = Math.max(320, (bounds ? bounds.right - bounds.left : window.innerWidth) - 24);
  // 窄窗（安装版默认 1344 宽）用 460，别再让 560 宽的浮窗横跨整个节目监视器。
  const base = available <= 1400 ? 460 : 560;
  return Math.round(Math.min(base, Math.max(320, available)));
}

/**
 * 浮窗层的可见区：.window-layer 是 position:fixed，但它挂在 .editor-shell 里面，
 * 而 shell 自身 overflow:hidden 且左侧被 64px 工具栏让开，所以可用坐标是 [shell.left, shell.right]。
 */
function shellBounds(): { left: number; right: number; top: number } {
  const shell = document.querySelector<HTMLElement>('.editor-shell');
  // 测试里的 DOM 桩只实现 querySelector，没有 getBoundingClientRect，这里必须先探测。
  const rect = shell && typeof shell.getBoundingClientRect === 'function' ? shell.getBoundingClientRect() : null;
  if (!rect || rect.width <= 0) return { left: 0, right: window.innerWidth, top: 0 };
  return { left: Math.round(rect.left), right: Math.round(rect.right), top: Math.round(rect.top) };
}

function clampWindowPosition(position: { x: number; y: number }): { x: number; y: number } {
  const maxX = Math.max(8, window.innerWidth - WINDOW_MIN_VISIBLE);
  // 允许向左少量超出，但至少留 WINDOW_MIN_VISIBLE 宽可见，避免换尺寸后窗口跑丢。
  const minX = Math.min(-220, WINDOW_MIN_VISIBLE - windowWidthForViewport());
  const maxY = Math.max(8, window.innerHeight - WINDOW_MIN_VISIBLE);
  return {
    x: Math.round(Math.max(minX, Math.min(maxX, position.x))),
    y: Math.round(Math.max(0, Math.min(maxY, position.y))),
  };
}

/** 换窗口尺寸时把效果控件整体收进可见区：右/下边被裁切会让标尺与面板操作区一起看不见。 */
function fitWindowIntoViewport(position: { x: number; y: number }): { x: number; y: number } {
  const width = windowWidthForViewport();
  const bounds = shellBounds();
  const minX = bounds.left + 8;
  const maxX = Math.max(minX, bounds.right - width - 8);
  const maxY = Math.max(8, window.innerHeight - 220);
  return {
    x: Math.round(Math.min(maxX, Math.max(minX, position.x))),
    y: Math.round(Math.max(0, Math.min(maxY, position.y))),
  };
}

function measureTopbarBottom(): number {
  const rect = document.querySelector<HTMLElement>('.ed-topbar')?.getBoundingClientRect();
  return rect && rect.height > 0 ? Math.round(rect.bottom) : 64;
}

function measureRulerTop(): number | null {
  const rect = document.querySelector<HTMLElement>('.ruler')?.getBoundingClientRect();
  return rect && rect.height > 0 ? rect.top : null;
}

/** 属性面板（右侧抽屉）左边缘：效果控件默认贴右上角时要避开它，避免两窗口叠在一起。 */
function measureInspectorLeft(): number | null {
  const rect = document.querySelector<HTMLElement>('.inspector-drawer.is-open')?.getBoundingClientRect();
  return rect && rect.width > 0 ? rect.left : null;
}

/** 若效果控件与右侧属性抽屉相交，向左平移到抽屉左侧；换工程/换页面回来也走这条兜底。 */
function avoidInspectorOverlap(position: { x: number; y: number }): { x: number; y: number } {
  const inspectorLeft = measureInspectorLeft();
  if (inspectorLeft === null) return position;
  const width = windowWidthForViewport();
  const maxX = Math.max(16, Math.round(inspectorLeft - width - 8));
  if (position.x + width <= inspectorLeft - 8) return position;
  return { x: Math.min(position.x, maxX), y: position.y };
}

/**
 * 效果控件默认位置：顶栏下方、贴着工作区左内缘。
 * 两块监视器并排时浮窗总会压住点什么；这里让浮窗落在左侧素材列上方，
 * 保证节目监视器与时间线可见（左端 64px 工具条也会遮住部分浮窗，所以从 shell 左内缘起算）。
 */
function defaultEffectControlsPosition(): { x: number; y: number } {
  const width = windowWidthForViewport();
  const bounds = shellBounds();
  const inspectorLeft = measureInspectorLeft();
  const baseX = bounds.left + 8;
  const maxX = Math.max(baseX, bounds.right - width - 8);
  let x = Math.min(baseX, maxX);
  if (inspectorLeft !== null) x = Math.min(x, Math.max(bounds.left + 8, Math.round(inspectorLeft - width - 8)));
  const y = Math.max(16, Math.min(measureTopbarBottom() + 8, window.innerHeight - 460));
  return { x: Math.max(0, x), y };
}

/** 效果控件的最大高度：底边停在时间标尺上沿之上，保证标尺/播放头/时间线左端永不被默认浮窗遮挡。 */
function effectControlsMaxHeight(positionY: number): number {
  const cssCap = Math.min(window.innerHeight * 0.46, 430);
  // 时间线面板的顶边比标尺更高，用它能保证浮窗不压住时间线标签与工具条。
  const panelTop = document.querySelector<HTMLElement>('.timeline-toolbar')?.getBoundingClientRect().top ?? null;
  const limit = panelTop ?? measureRulerTop();
  if (limit === null) return Math.round(cssCap);
  return Math.round(Math.max(220, Math.min(cssCap, limit - 12 - positionY)));
}

export function WorkspacePanelWindow({
  id,
  stackIndex = 0,
  suspended = false,
  docked = false,
  project,
  selectedClip,
  selectedItem,
  items,
  clips,
  markers,
  transitions,
  sourceDurations,
  selectedTransitionId,
  transitionDefaults,
  onChangeTransitionDefaults,
  sourceTime,
  sequenceTime,
  sourceDuration,
  totalDuration,
  transcriptCount,
  onClose,
  onSeek,
  onSeekSource,
  onUpdateItem,
  onSelectClip,
  onSelectTransition,
  onApplyTransition,
  onRemoveTransition,
  onUpdateTransition,
  onAddMarker,
  onClearMarkers,
  onRemoveMarker,
  onOpenHistory,
  onOpenStudio,
  onOpenLive,
  onOpenAi,
  onSelectTool,
  activeTool = 'select',
  onToggleTrack,
  activeWorkspaceId,
  onApplyWorkspace,
  onResetWorkspace,
}: WorkspacePanelWindowProps) {
  const fps = project?.fps ?? 30;
  const label = WORKSPACE_PANEL_LABELS[id];
  const previewPanel = PREVIEW_PANELS[id];
  const [notice, setNotice] = useState('');
  const [notes, setNotes] = useState(() => {
    try {
      return window.localStorage.getItem('liveclipper.project-notes') ?? '';
    } catch {
      return '';
    }
  });
  const [speech, setSpeech] = useState('');
  const [graphicText, setGraphicText] = useState('LiveClipper 标题');
  // 关闭动画：先播 160ms 的退出过渡，再交给父级移除窗口。
  const [closing, setClosing] = useState(false);
  const closeTimerRef = useRef<number | null>(null);
  useEffect(
    () => () => {
      if (closeTimerRef.current !== null) window.clearTimeout(closeTimerRef.current);
    },
    [],
  );
  const requestClose = () => {
    if (closing || suspendedRef.current) return;
    setClosing(true);
    closeTimerRef.current = window.setTimeout(() => {
      closeTimerRef.current = null;
      onClose();
    }, 160);
  };
  const cascade = stackIndex % 7;
  const effectControls = id === EFFECT_CONTROLS_ID;
  // 窄窗（≤1400）时效果控件不再浮动，改由宿主放进右侧 dock 列；docked 时跳过所有定位逻辑。
  const dockedPanel = docked && effectControls;
  const [position, setPosition] = useState(() => {
    const stored = readStoredWindowPosition(id);
    if (stored) return clampWindowPosition(stored);
    if (effectControls) {
      // 默认贴右上角；真实 topbar/ruler 尺寸在 layout effect 里再校正一次。
      return clampWindowPosition({
        x: window.innerWidth - windowWidthForViewport() - WINDOW_POSITION_MARGIN,
        y: 72,
      });
    }
    return {
      x: Math.max(16, Math.min(window.innerWidth - 600, 88) + cascade * 28),
      // Windows stay in the upper band so they can never cover the timeline ruler/tracks
      // (the workspace layout puts the timeline at roughly y=540 at 1440x900).
      // 视口较矮时把窗口整体上移，保证标题栏与正文至少可见，不落到视口外。
      y: Math.max(10, Math.min(window.innerHeight - 460, 76)),
    };
  });
  const positionRef = useRef(position);
  positionRef.current = position;
  const [panelMaxHeight, setPanelMaxHeight] = useState<number | null>(null);
  const [zIndex, setZIndex] = useState(() => 400 + stackIndex);
  const dragRef = useRef<{ startX: number; startY: number; originX: number; originY: number } | null>(null);

  const layerRef = useRef<HTMLDivElement>(null);
  const bodyRef = useRef<HTMLDivElement>(null);
  const [hasMoreBody, setHasMoreBody] = useState(false);
  const suspendedRef = useRef(suspended);
  const initializedLayoutRef = useRef(false);
  const dragCleanupRef = useRef<(() => void) | null>(null);

  useLayoutEffect(() => {
    suspendedRef.current = suspended;
    if (suspended) {
      const active = document.activeElement;
      if (active instanceof HTMLElement && layerRef.current?.contains(active)) active.blur();
    }
    return () => dragCleanupRef.current?.();
  }, [suspended]);

  useLayoutEffect(() => {
    if (suspended || dockedPanel) return;
    // Resume the existing position, not the persisted/default first-mount layout.
    if (effectControls && !initializedLayoutRef.current) {
      // 首帧按真实布局校正：默认位置依赖顶栏高度与标尺位置，渲染阶段拿不到最终尺寸。
      const stored = readStoredWindowPosition(id);
      const next = avoidInspectorOverlap(clampWindowPosition(stored ?? defaultEffectControlsPosition()));
      positionRef.current = next;
      setPosition(next);
      setPanelMaxHeight(effectControlsMaxHeight(next.y));
      persistWindowPosition(id, next);
    }
    initializedLayoutRef.current = true;
    const onResize = () => {
      if (suspendedRef.current) return;
      const base = effectControls
        ? fitWindowIntoViewport(clampWindowPosition(positionRef.current))
        : clampWindowPosition(positionRef.current);
      const next = effectControls ? avoidInspectorOverlap(base) : base;
      positionRef.current = next;
      setPosition(next);
      if (effectControls) setPanelMaxHeight(effectControlsMaxHeight(next.y));
      persistWindowPosition(id, next);
    };
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, [effectControls, id, suspended]);

  // 属性面板开合会改变可用横向空间：效果控件若已打开，重新收进抽屉左侧（仅效果控件，避免打扰其它窗口）。
  useLayoutEffect(() => {
    if (!effectControls || suspended || dockedPanel) return undefined;
    const shell = document.querySelector('.editor-shell');
    if (!shell) return undefined;
    let frame = 0;
    const onInspectorChange = () => {
      if (frame || suspendedRef.current) return;
      frame = window.requestAnimationFrame(() => {
        frame = 0;
        if (suspendedRef.current) return;
        const next = avoidInspectorOverlap(positionRef.current);
        if (next.x === positionRef.current.x && next.y === positionRef.current.y) return;
        positionRef.current = next;
        setPosition(next);
        setPanelMaxHeight(effectControlsMaxHeight(next.y));
        persistWindowPosition(id, next);
      });
    };
    const observer = new MutationObserver(onInspectorChange);
    observer.observe(shell, { attributes: true, attributeFilter: ['class'], subtree: true, childList: true });
    return () => {
      observer.disconnect();
      if (frame) window.cancelAnimationFrame(frame);
    };
  }, [effectControls, id, suspended]);

  const raiseWindow = () => {
    if (!suspendedRef.current) setZIndex(raiseWorkspaceWindow());
  };

  const beginWindowDrag = (event: ReactPointerEvent<HTMLElement>) => {
    if (suspendedRef.current) return;
    dragCleanupRef.current?.();
    if ((event.target as HTMLElement).closest('button, input, textarea, select, a')) return;
    event.preventDefault();
    raiseWindow();
    dragRef.current = { startX: event.clientX, startY: event.clientY, originX: position.x, originY: position.y };
    const onMove = (moveEvent: PointerEvent) => {
      const drag = dragRef.current;
      if (!drag || suspendedRef.current) return;
      const maxX = Math.max(0, window.innerWidth - 360);
      // 至少留出标题栏 + 120px 正文在视口内，避免拖到只剩一条边。
      const maxY = Math.max(0, window.innerHeight - 200);
      const next = {
        x: Math.max(-220, Math.min(maxX, drag.originX + moveEvent.clientX - drag.startX)),
        y: Math.max(0, Math.min(maxY, drag.originY + moveEvent.clientY - drag.startY)),
      };
      positionRef.current = next;
      setPosition(next);
    };
    const cleanup = () => {
      dragRef.current = null;
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onUp);
      dragCleanupRef.current = null;
    };
    const onUp = () => {
      if (!suspendedRef.current) persistWindowPosition(id, positionRef.current);
      cleanup();
    };
    dragCleanupRef.current = cleanup;
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onUp);
  };

  // 预览版窗口不再运行假采样定时器；真实示波器接入后在这里订阅播放帧。

  // Escape is handled once in EditorPage: it dismisses only the newest panel window, so a
  // per-window document listener would close every open window at once.

  const safeNotes = useMemo(() => notes, [notes]);
  const updateNotes = (value: string) => {
    setNotes(value);
    try {
      window.localStorage.setItem('liveclipper.project-notes', value);
    } catch {
      // localStorage may be unavailable; keep editing in memory.
    }
  };

  const patch = (next: Partial<TimelineItem>, labelText: string) => {
    if (!selectedItem) {
      setNotice('先在时间线上选中一个片段');
      return;
    }
    onUpdateItem(next, labelText);
    setNotice(`${labelText}，已写入工程`);
  };

  const toggleTrack = (field: 'mutedTracks' | 'soloedTracks', track: number) => {
    onToggleTrack(field, track);
    setNotice(`${track <= 3 ? '视频' : '音频'}轨 ${track <= 3 ? `V${track}` : `A${track - 3}`} 状态已更新`);
  };

  const copyTime = async () => {
    const text = formatTimecode(sequenceTime, fps);
    try {
      await navigator.clipboard.writeText(text);
      setNotice(`已复制 ${text}`);
    } catch {
      setNotice(text);
    }
  };

  const speak = () => {
    if (!('speechSynthesis' in window)) {
      setNotice('当前浏览器不支持语音合成');
      return;
    }
    window.speechSynthesis.cancel();
    if (!speech.trim()) {
      setNotice('先输入要朗读的文字');
      return;
    }
    window.speechSynthesis.speak(new SpeechSynthesisUtterance(speech));
    setNotice('正在用系统语音朗读');
  };

  const sourceName = selectedClip?.title || selectedClip?.path || '未选择素材';
  const itemIndex = selectedItem ? items.findIndex((item) => item.id === selectedItem.id) : -1;
  const selectedTrack = selectedItem ? trackOf(selectedItem) : 1;
  const filter = selectedItem?.filter ?? 'none';
  const volume = selectedItem?.volumeDb ?? 0;
  const selectedTransition = transitions.find((entry) => entry.id === selectedTransitionId) ?? null;

  useLayoutEffect(() => {
    const el = bodyRef.current;
    if (!el || suspended) return undefined;
    const sync = () => setHasMoreBody(el.scrollHeight - el.scrollTop - el.clientHeight > 1);
    const resizeObserver = typeof ResizeObserver === 'function' ? new ResizeObserver(sync) : null;
    const observeContent = () => {
      resizeObserver?.disconnect();
      resizeObserver?.observe(el);
      for (const child of Array.from(el.children)) resizeObserver?.observe(child);
    };
    const mutationObserver = typeof MutationObserver === 'function' ? new MutationObserver(() => {
      observeContent();
      sync();
    }) : null;
    observeContent();
    mutationObserver?.observe(el, {
      childList: true,
      subtree: true,
      characterData: true,
      attributes: true,
      attributeFilter: ['open', 'hidden', 'class', 'style'],
    });
    sync();
    el.addEventListener('scroll', sync, { passive: true });
    return () => {
      el.removeEventListener('scroll', sync);
      resizeObserver?.disconnect();
      mutationObserver?.disconnect();
    };
  }, [id, dockedPanel, suspended]);

  const showMoreBody = () => {
    const el = bodyRef.current;
    if (el) el.scrollBy({ top: el.clientHeight, behavior: 'smooth' });
  };

  const body = (() => {
    switch (id) {
      case 'workspaces':
        return (
          <>
            <p className="window-muted">
              9 个 FilmCraft 对齐工作区：Alt+Shift+1..9 切换，Alt+Shift+0 重置当前布局。切换会记住每个工作区的面板尺寸与浮动窗口。
            </p>
            <Field label="当前工作区" value={WORKSPACES.find((workspace) => workspace.id === activeWorkspaceId)?.name ?? '剪辑'} />
            <div className="window-card-grid">
              {WORKSPACES.map((workspace) => (
                <button
                  key={workspace.id}
                  type="button"
                  className={`window-card${workspace.id === activeWorkspaceId ? ' is-active' : ''}`}
                  onClick={() => {
                    onApplyWorkspace(workspace.id);
                    setNotice(`已切换到「${workspace.name}」`);
                  }}
                >
                  <strong>
                    {workspace.name}
                    <span className="window-card-shortcut">Alt+Shift+{workspace.digit}</span>
                  </strong>
                  <span>{workspace.hint}</span>
                </button>
              ))}
            </div>
            <div className="window-actions">
              <button
                type="button"
                className="window-button"
                onClick={() => {
                  onResetWorkspace();
                  setNotice('已重置当前工作区布局');
                }}
              >
                重置当前工作区（Alt+Shift+0）
              </button>
            </div>
          </>
        );
      case 'new-project':
        return (
          <>
            <p className="window-muted">从素材库建立新的剪辑工程；当前工程不会被覆盖。</p>
            <Field label="可用素材" value={`${clips.length} 个`} />
            <div className="window-actions">
              <button type="button" className="primary-button" onClick={onOpenStudio}>
                打开项目库
              </button>
              <button type="button" className="window-button" onClick={onOpenLive}>
                打开直播控制台
              </button>
            </div>
          </>
        );
      case 'project':
        return (
          <>
            <Field label="工程" value={selectedClip?.title || '未选择素材'} />
            <Field label="时间线" value={`${items.length} 个片段 · ${formatTimecode(totalDuration, fps)}`} />
            <Field label="素材" value={sourceName} />
            <Field label="分辨率 / 帧率" value={`${selectedClip ? '当前素材' : '—'} · ${fps} fps`} />
            <div className="window-actions">
              <button type="button" className="primary-button" onClick={onOpenStudio}>
                项目库
              </button>
              <button type="button" className="window-button" onClick={onOpenLive}>
                直播控制台
              </button>
            </div>
          </>
        );
      case 'media':
        return (
          <div className="window-list">
            {clips.map((clip) => (
              <button
                key={clip.id}
                type="button"
                className={`window-list-item${clip.id === selectedClip?.id ? ' is-current' : ''}`}
                onClick={() => {
                  onSelectClip(clip.id);
                  setNotice(`已切换到「${clip.title || clip.id}」`);
                }}
              >
                <span>{clip.title || clip.id}</span>
                <small>{clip.createdAt ? new Date(clip.createdAt).toLocaleDateString('zh-CN') : '本地素材'}</small>
              </button>
            ))}
            {clips.length === 0 ? <p className="window-muted">素材库为空。</p> : null}
          </div>
        );
      case 'library':
        return (
          <>
            <p className="window-muted">库用来管理项目、序列、媒体和导出记录。当前打开的是剪辑工程内的媒体库。</p>
            <Field label="素材" value={`${clips.length} 个`} />
            <Field label="当前素材" value={sourceName} />
            <div className="window-actions">
              <button type="button" className="primary-button" onClick={onOpenStudio}>
                打开完整项目库
              </button>
            </div>
          </>
        );
      case 'info':
        return (
          <>
            <Field label="素材" value={sourceName} />
            <Field label="素材路径" value={selectedClip?.path || '—'} />
            <Field label="转写" value={`${transcriptCount} 段`} />
            <Field label="时间线片段" value={`${items.length} 个 · ${formatTimecode(totalDuration, fps)}`} />
          </>
        );
      case 'metadata':
        return (
          <MetadataPanel
            project={project}
            selectedClip={selectedClip}
            selectedItem={selectedItem}
            items={items}
            fps={fps}
            totalDuration={totalDuration}
            sequenceTime={sequenceTime}
            transcriptCount={transcriptCount}
            onSeek={onSeek}
            onNotice={setNotice}
          />
        );
      case 'transitions':
        return (
          <TransitionsPanel
            items={items}
            selectedItem={selectedItem}
            fps={fps}
            transitions={transitions}
            sourceDurations={sourceDurations}
            selectedTransitionId={selectedTransitionId}
            transitionDefaults={transitionDefaults}
            onChangeTransitionDefaults={onChangeTransitionDefaults}
            onSelectTransition={onSelectTransition}
            onApplyTransition={onApplyTransition}
            onRemoveTransition={onRemoveTransition}
            onUpdateTransition={onUpdateTransition}
            onPatch={patch}
            onSeek={onSeek}
            onNotice={setNotice}
          />
        );
      case 'effects':
      case 'lumetri-color':
        return (
          <>
            <p className="window-muted">把颜色滤镜应用到当前选中的片段；修改立即写入工程。</p>
            <div className="window-filter-row">
              {FILTERS.map((entry) => (
                <button
                  key={entry.value}
                  type="button"
                  className={`window-filter${filter === entry.value ? ' is-active' : ''}`}
                  onClick={() => patch({ filter: entry.value }, `片段滤镜：${entry.label}`)}
                >
                  {entry.label}
                </button>
              ))}
            </div>
            {selectedItem ? (
              <>
                <Slider
                  label="片段不透明度"
                  value={selectedItem.opacity ?? 1}
                  min={0}
                  max={1}
                  step={0.05}
                  suffix=""
                  onChange={(value) => patch({ opacity: value }, '修改片段不透明度')}
                />
                <div className="window-actions">
                  <button type="button" className="window-button" onClick={() => onOpenAi('candidates')}>
                    打开 AI 候选
                  </button>
                </div>
              </>
            ) : (
              <p className="window-muted">选中片段后才能应用颜色。</p>
            )}
          </>
        );
      case 'effect-controls':
        return (
          <EffectControlsPanel
            selectedItem={selectedItem}
            selectedTransition={selectedTransition}
            transitions={transitions}
            sourceDurations={sourceDurations}
            items={items}
            fps={fps}
            sequenceTime={sequenceTime}
            onUpdateItem={onUpdateItem}
            onSelectTransition={onSelectTransition}
            onUpdateTransition={onUpdateTransition}
            onRemoveTransition={onRemoveTransition}
            onApplyTransition={onApplyTransition}
            onSeek={onSeek}
            onNotice={setNotice}
          />
        );
      case 'properties':
        return selectedItem ? (
          <>
            <p className="window-muted">
              片段 {String(itemIndex + 1).padStart(2, '0')} · V{selectedTrack} 轨 ·{' '}
              {formatTimecode(selectedItem.end - selectedItem.start, fps)}
            </p>
            <Slider
              label="不透明度"
              value={selectedItem.opacity ?? 1}
              min={0}
              max={1}
              step={0.05}
              suffix=""
              onChange={(value) => patch({ opacity: value }, '修改片段不透明度')}
            />
            <Slider
              label="旋转"
              value={selectedItem.rotation ?? 0}
              min={-180}
              max={180}
              step={1}
              suffix="°"
              onChange={(value) => patch({ rotation: value }, '修改片段旋转')}
            />
            <Slider
              label="缩放"
              value={selectedItem.zoom ?? 1}
              min={0.25}
              max={3}
              step={0.05}
              suffix="×"
              onChange={(value) => patch({ zoom: value }, '修改片段缩放')}
            />
            <div className="window-two">
              <Slider
                label="水平位置"
                value={selectedItem.positionX ?? 0}
                min={-0.5}
                max={0.5}
                step={0.01}
                suffix=""
                onChange={(value) => patch({ positionX: value }, '修改片段水平位置')}
              />
              <Slider
                label="垂直位置"
                value={selectedItem.positionY ?? 0}
                min={-0.5}
                max={0.5}
                step={0.01}
                suffix=""
                onChange={(value) => patch({ positionY: value }, '修改片段垂直位置')}
              />
            </div>
          </>
        ) : (
          <p className="window-muted">先在时间线上选中一个片段。</p>
        );
      case 'audio-clip-mixer':
      case 'essential-sound':
        return selectedItem ? (
          <>
            <Field label="片段" value={`片段 ${String(itemIndex + 1).padStart(2, '0')} · V${selectedTrack}`} />
            <Slider
              label="片段音量"
              value={volume}
              min={-60}
              max={12}
              step={0.5}
              suffix=" dB"
              onChange={(value) => patch({ volumeDb: value }, '修改片段音量')}
            />
            <div className="window-actions">
              <button type="button" className="window-button" onClick={() => patch({ volumeDb: -60 }, '静音片段')}>
                静音片段
              </button>
              <button type="button" className="window-button" onClick={() => patch({ volumeDb: 0 }, '片段音量归零')}>
                恢复 0 dB
              </button>
            </div>
          </>
        ) : (
          <p className="window-muted">先选中一个片段。</p>
        );
      case 'audio-track-mixer':
        return (
          <>
            <p className="window-muted">A1–A3 轨道的静音和独奏会立即写入工程。</p>
            {[4, 5, 6].map((track) => {
              const muted = (project?.mutedTracks ?? []).includes(track);
              const solo = (project?.soloedTracks ?? []).includes(track);
              return (
                <div className="window-track-row" key={track}>
                  <strong>A{track - 3}</strong>
                  <button
                    type="button"
                    className={`window-button${muted ? ' is-active' : ''}`}
                    onClick={() => toggleTrack('mutedTracks', track)}
                  >
                    {muted ? '取消静音' : '静音'}
                  </button>
                  <button
                    type="button"
                    className={`window-button${solo ? ' is-active' : ''}`}
                    onClick={() => toggleTrack('soloedTracks', track)}
                  >
                    {solo ? '取消独奏' : '独奏'}
                  </button>
                </div>
              );
            })}
          </>
        );
      case 'audio-level':
        return (
          <AudioLevelPanel
            project={project}
            items={items}
            selectedItem={selectedItem}
            fps={fps}
            sequenceTime={sequenceTime}
            onPatch={patch}
            onToggleTrack={toggleTrack}
            onSeek={onSeek}
          />
        );
      case 'markers':
      case 'events':
        return (
          <>
            <div className="window-actions">
              <button type="button" className="primary-button" onClick={() => onAddMarker(sequenceTime)}>
                在播放头添加
              </button>
              <button type="button" className="window-button" onClick={onClearMarkers} disabled={markers.length === 0}>
                清除全部
              </button>
            </div>
            {markers.length === 0 ? (
              <p className="window-muted">还没有标记。</p>
            ) : (
              markers.map((marker, index) => (
                <div className="window-marker-row" key={`${marker}-${index}`}>
                  <button type="button" className="window-link" onClick={() => onSeek(marker)}>
                    {formatTimecode(marker, fps)}
                  </button>
                  <button type="button" className="window-button" onClick={() => onRemoveMarker(index)}>
                    删除
                  </button>
                </div>
              ))
            )}
          </>
        );
      case 'history':
        return (
          <>
            <p className="window-muted">每次覆盖工程前自动保存上一版，最多保留最近 30 个版本；恢复会进入当前撤销栈。</p>
            <Field label="本地快照" value="由后端工程备份提供，可在下方对话框查看最近版本" />
            <button type="button" className="primary-button" onClick={onOpenHistory}>
              打开工程历史
            </button>
          </>
        );
      case 'source':
        return (
          <>
            <Field label="素材" value={sourceName} />
            <Field label="源时间" value={formatTimecode(sourceTime, fps)} />
            <Field label="素材总长" value={formatTimecode(sourceDuration, fps)} />
            <div className="window-actions">
              <button type="button" className="window-button" onClick={() => onSeekSource(0)}>
                源开头
              </button>
              <button type="button" className="window-button" onClick={() => onSeekSource(sourceDuration)}>
                源末尾
              </button>
            </div>
          </>
        );
      case 'program':
        return (
          <>
            <Field label="节目时间" value={formatTimecode(sequenceTime, fps)} />
            <Field label="节目总长" value={formatTimecode(totalDuration, fps)} />
            <Field label="当前片段" value={selectedItem ? `片段 ${String(itemIndex + 1).padStart(2, '0')}` : '无'} />
            <div className="window-actions">
              <button type="button" className="window-button" onClick={() => onSeek(0)}>
                节目开头
              </button>
              <button type="button" className="window-button" onClick={() => onSeek(totalDuration)}>
                节目末尾
              </button>
            </div>
          </>
        );
      case 'timeline':
        return (
          <>
            <Field label="片段数" value={`${items.length}`} />
            <Field label="时间线时长" value={formatTimecode(totalDuration, fps)} />
            <Field label="轨道" value="V1–V3 / A1–A3" />
            <Field label="缩放" value="100%–400%，默认 100%" />
          </>
        );
      case 'tools':
        return (
          <>
            <p className="window-muted">与时间线左侧工具轨同步：点击即切换，快捷键在时间线聚焦时生效。</p>
            <div className="ws-tool-grid" role="radiogroup" aria-label="编辑器工具">
              {EDITOR_TOOL_OPTIONS.map((tool) => {
                const Icon = tool.icon;
                const isActive = activeTool === tool.id;
                return (
                  <button
                    key={tool.id}
                    type="button"
                    role="radio"
                    aria-checked={isActive}
                    className={`ws-tool-button${isActive ? ' is-active' : ''}`}
                    onClick={() => {
                      onSelectTool(tool.id);
                      setNotice(`已切换到「${tool.label}」（快捷键 ${tool.shortcut}）`);
                    }}
                  >
                    <span className="ws-tool-icon" aria-hidden="true">
                      <Icon size={14} strokeWidth={1.7} />
                    </span>
                    <span className="ws-tool-text">
                      <strong>{tool.label}</strong>
                      <span>{tool.hint}</span>
                    </span>
                    <kbd className="ws-tool-key">{tool.shortcut}</kbd>
                  </button>
                );
              })}
            </div>
          </>
        );
      case 'lumetri-scopes':
        return (
          <>
            <p className="window-muted">示波器真实采样尚未接入；以下是面板结构占位。</p>
            <div className="scope-grid">
              {['波形', '矢量', '直方图'].map((name) => (
                <div className="scope-card is-preview" key={name}>
                  <strong>{name}</strong>
                  <span>未接入</span>
                </div>
              ))}
            </div>
            <Field label="节目时间" value={formatTimecode(sequenceTime, fps)} />
          </>
        );
      case 'graphics':
        return (
          <>
            <p className="window-muted">
              {selectedItem && selectedTrack === 3
                ? '当前选中的是 V3 图形片段。标题会进入预览和成片。'
                : '先把一个片段拖到 V3 图形与标题轨，再编辑文字、颜色和位置。'}
            </p>
            <label className="window-notes">
              <span>标题文本</span>
              <input
                value={selectedItem?.titleText ?? graphicText}
                onChange={(event) => {
                  if (selectedItem && selectedTrack === 3) {
                    patch({ titleText: event.target.value || null }, '修改标题文本');
                  } else {
                    setGraphicText(event.target.value);
                  }
                }}
              />
            </label>
            {selectedItem && selectedTrack === 3 ? (
              <>
                <label className="window-notes window-color-row">
                  <span>文字颜色</span>
                  <input
                    type="color"
                    value={selectedItem.titleColor ?? '#FFFFFF'}
                    onChange={(event) => patch({ titleColor: event.target.value }, '修改标题颜色')}
                  />
                </label>
                <Slider
                  label="字号"
                  value={selectedItem.titleSize ?? 48}
                  min={12}
                  max={160}
                  step={1}
                  suffix=" px"
                  onChange={(value) => patch({ titleSize: value }, '修改标题字号')}
                />
                <div className="window-two">
                  <Slider
                    label="水平位置"
                    value={selectedItem.titleX ?? 0.5}
                    min={0}
                    max={1}
                    step={0.01}
                    suffix=""
                    onChange={(value) => patch({ titleX: value }, '修改标题水平位置')}
                  />
                  <Slider
                    label="垂直位置"
                    value={selectedItem.titleY ?? 0.82}
                    min={0}
                    max={1}
                    step={0.01}
                    suffix=""
                    onChange={(value) => patch({ titleY: value }, '修改标题垂直位置')}
                  />
                </div>
                <div className="graphics-preview" style={{ color: selectedItem.titleColor ?? '#FFFFFF' }}>
                  {selectedItem.titleText || '标题预览'}
                </div>
              </>
            ) : (
              <div className="graphics-preview">{graphicText || '标题预览'}</div>
            )}
          </>
        );
      case 'tts':
        return (
          <>
            <p className="window-muted">用浏览器系统语音试听文案；不会上传到服务器。</p>
            <label className="window-notes">
              <span>朗读文本</span>
              <textarea value={speech} onChange={(event) => setSpeech(event.target.value)} rows={4} />
            </label>
            <div className="window-actions">
              <button type="button" className="primary-button" onClick={speak}>
                朗读
              </button>
              <button type="button" className="window-button" onClick={() => window.speechSynthesis.cancel()}>
                停止
              </button>
            </div>
          </>
        );
      case 'text':
        return (
          <TextPanel
            project={project}
            selectedItem={selectedItem}
            transcriptCount={transcriptCount}
            onPatch={patch}
            onOpenAi={onOpenAi}
          />
        );
      case 'progress':
        return (
          <>
            <Field label="工程片段" value={`${items.length}`} />
            <Field label="时间线" value={formatTimecode(totalDuration, fps)} />
            <Field label="转写" value={`${transcriptCount} 段`} />
            <Field label="标记" value={`${markers.length}`} />
          </>
        );
      case 'reference':
        return (
          <>
            <Field label="参考时间" value={formatTimecode(sequenceTime, fps)} />
            <Field label="源参考" value={formatTimecode(sourceTime, fps)} />
            <Field
              label="工程画幅"
              value={project?.resolution ? `${project.resolution}p · ${fps} fps` : `未接入 · ${fps} fps`}
            />
            <p className="window-muted">参考监视器用于对照源画面与节目画面；独立监看窗将在后续版本拆分。</p>
          </>
        );
      case 'timecode':
        return (
          <>
            <div className="timecode-large">{formatTimecode(sequenceTime, fps)}</div>
            <Field label="帧" value={`${Math.floor(sequenceTime * fps)} / ${Math.floor(totalDuration * fps)}`} />
            <div className="window-actions">
              <button type="button" className="primary-button" onClick={() => void copyTime()}>
                复制时间码
              </button>
              <button type="button" className="window-button" onClick={() => onSeek(0)}>
                定位开头
              </button>
            </div>
          </>
        );
      case 'project-notes':
        return (
          <>
            <label className="window-notes">
              <span>项目备注（保存在本机浏览器）</span>
              <textarea value={safeNotes} onChange={(event) => updateNotes(event.target.value)} rows={9} />
            </label>
            <p className="window-muted">自动保存到 localStorage，不写入工程，不会上传。</p>
          </>
        );
      default:
        return (
          <p className="window-muted">
            未知的面板 ID「{id}」，可能是旧工作区配置残留；可在「工作区」菜单重置布局。
          </p>
        );
    }
  })();

  if (dockedPanel) {
    return (
      <section
        className={`workspace-window workspace-window--docked workspace-window--${id}${hasMoreBody ? ' has-more' : ''}`}
        aria-label={label}
      >
        <header className="workspace-window-head">
          <div>
            <div className="workspace-window-title-row">
              <h2>{label}</h2>
            </div>
          </div>
          <button type="button" className="workspace-window-close" aria-label={`关闭${label}`} onClick={onClose}>
            <X size={15} />
          </button>
        </header>
        <div className="workspace-window-body" ref={bodyRef}>{body}</div>
        {hasMoreBody ? (
          <button type="button" className="workspace-window-more" onClick={showMoreBody}>↓ 还有参数</button>
        ) : null}
        {notice ? <div className="workspace-window-notice">{notice}</div> : null}
      </section>
    );
  }

  return (
    <div
      ref={layerRef}
      className="window-layer"
      role="presentation"
      style={{ display: suspended ? 'none' : undefined }}
      inert={suspended}
      aria-hidden={suspended || undefined}
      onBlurCapture={(event) => {
        // Hiding a focused draft must not commit its blur handler into the project.
        if (suspended) event.stopPropagation();
      }}
      onKeyDownCapture={(event) => {
        if (!suspended) return;
        event.preventDefault();
        event.stopPropagation();
      }}
    >
      <section
        className={`workspace-window workspace-window--${id}${closing ? ' is-closing' : ''}${hasMoreBody ? ' has-more' : ''}`}
        role="dialog"
        aria-label={label}
        style={{
          left: position.x,
          top: position.y,
          zIndex,
          // 效果控件默认压着标尺会挡住播放头/时间线左端：底边限制在标尺上方（用户手动拖到更低时不强制）。
          ...(effectControls && panelMaxHeight !== null ? { maxHeight: `${panelMaxHeight}px` } : null),
        }}
        onPointerDownCapture={raiseWindow}
      >
        <header className="workspace-window-head" onPointerDown={beginWindowDrag}>
          <div>
            <span className="workspace-window-kicker">WINDOW · 拖动标题栏移动</span>
            <div className="workspace-window-title-row">
              <h2>{label}</h2>
              {previewPanel ? <span className="workspace-window-badge">{previewPanel.badge}</span> : null}
            </div>
          </div>
          <button type="button" className="workspace-window-close" aria-label={`关闭${label}`} onClick={requestClose}>
            <X size={15} />
          </button>
        </header>
        <div className="workspace-window-body" ref={bodyRef}>
          {previewPanel ? <p className="ws-preview-note">{previewPanel.note}</p> : null}
          {body}
        </div>
        {hasMoreBody ? (
          <button type="button" className="workspace-window-more" onClick={showMoreBody}>↓ 还有参数</button>
        ) : null}
        {notice ? <div className="workspace-window-notice">{notice}</div> : null}
      </section>
    </div>
  );
}
