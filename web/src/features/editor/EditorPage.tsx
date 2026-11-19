import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent as ReactKeyboardEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import { AlertTriangle, LoaderCircle, RefreshCw } from 'lucide-react';
import { EngineStage, type EngineAdapter } from '../../engine';
import { useEngineSync } from './engineSync';
import { applyInspectorEdit } from './engineWrite';
import { AiDrawer, type AiTab } from './components/AiDrawer';
import { AiRail } from './components/AiRail';
import { InspectorDrawer } from './components/InspectorDrawer';
import { AudioMixer } from './components/AudioMixer';
import { EventListPanel } from './components/EventListPanel';
import type { EditorEventView } from './events';
import type { TimelineMenuAction } from './shell/editorMenus';
import { EditorShellFrame } from './shell/EditorShellFrame';
import { EditorStatusbar } from './shell/EditorStatusbar';
import { EditorTopbar } from './shell/EditorTopbar';
import { Monitor, type ProgramAudioLayer, type ProgramOverlay } from './components/Monitor';
import { TrimMonitor } from './components/TrimMonitor';
import { NestedTabsBar, type SequenceCrumb, type SequenceTab } from './components/NestedTabsBar';
import { ProjectColumn } from './components/ProjectColumn';
import { RenderDialog } from './components/RenderDialog';
import { RefinementPanel } from './components/RefinementPanel';
import { captionsFor, hasCaptionOverride, setClipCaptions } from './captionOverrides';
import { SequenceDialog } from './components/SequenceDialog';
import { ProjectHistoryDialog } from './components/ProjectHistoryDialog';
import { SequenceSettingsDialog } from './components/SequenceSettingsDialog';
import { TrackPropertiesDialog } from './components/TrackPropertiesDialog';
import {
  Timeline,
  type AssetDropPayload,
  type TimelineHandle,
  type TransitionDropPayload,
} from './components/Timeline';
import { ToolRail, type EditorTool } from './components/ToolRail';
import './helpDialog.css';
import { WorkspacePanelWindow, type WorkspacePanelId } from './components/WorkspacePanelWindow';
import { useSlideMount } from './useSlideMount';
import { getEditor, getEditorSequence, getVideoUrl } from '../../api/client';
import { importStudioFiles } from '../../api/studio';
import {
  AUDIO_EFFECT_SPECS,
  MAX_AUDIO_EFFECTS,
  createAudioEffect,
  normalizeAudioEffects,
} from './audioEffects';
import type { AudioEffectType, EditorProject, EditorTransition, NestedSequence, TimelineItem, TranscriptSegment } from '../../api/types';
import { useEditorSession } from './useEditorSession';
import { usePerception } from './usePerception';
import { installCloseHandshake } from './closeHandshake';
import { SaveStatusBars } from './components/SaveStatusBars';
import { useSequencePlayer } from './useSequencePlayer';
import { resolveProgramSource } from './programSource';
import { includeBaseAudio, includeTrackAudio } from './trackAudioRule';
import { overlayAudible } from './trackAudioRule';
import { createSequencePlayheadState, prepareSequencePlayhead, readySequenceProjectId, rememberSequencePlayhead } from './sequencePlayhead';
import {
  audioTrackForVideoTrack,
  buildProjectAfterApplying,
  clipFadeFactor,
  clipTitle,
  cutAvailability,
  DEFAULT_TRANSITION_DEFAULTS,
  defaultTimelineItem,
  effectiveTimeline,
  findTransition,
  formatClock,
  formatTimecode,
  itemForSequence,
  itemOutputDuration,
  itemSequenceStart,
  linkAudioCompanion,
  normalizeProject,
  normalizeTransitionDefaults,
  sequenceDuration,
  trackOf,
  transitionCuts,
  transitionIdForItem,
  transitionKindLabel,
  transitionMinDuration,
  type TransitionDefaults,
} from './project';
import { isRangeUsable, spliceRange } from './rangeEdit';
import {
  applyEditPointTrim,
  EDIT_POINT_KIND_LABELS,
  editPointSides,
  nextEditPointKind,
  type EditPoint,
  type EditPointKind,
} from './trimOps';
import {
  MAX_NESTED_DEPTH,
  buildNestedPreviewProject,
  createNestedSequence,
  nestedItemLabels,
  nestedSequenceFor,
} from './nested';
import {
  LAYOUT_KEY,
  limit,
  readWorkspaceStore,
  resolveWorkspace,
  saveWorkspaceSnapshot,
  WORKSPACES,
  workspaceById,
  writeWorkspaceStore,
  type EditorLayout,
  type WorkspaceId,
} from './workspaces';

const tabs: AiTab[] = ['candidates', 'transcript', 'chapters', 'music'];

function parseInitialTab(): AiTab | null {
  const value = new URLSearchParams(window.location.search).get('ai');
  return value && tabs.includes(value as AiTab) ? (value as AiTab) : null;
}


type HelpDialogKind = 'shortcuts' | 'about';

type ResizeKind = 'project' | 'viewers' | 'viewerSplit' | 'audio';

const TRANSITION_DEFAULTS_KEY = 'liveclipper.editor.transitionDefaults.v1';

function readTransitionDefaults(): TransitionDefaults {
  try {
    const raw = window.localStorage.getItem(TRANSITION_DEFAULTS_KEY);
    if (!raw) return DEFAULT_TRANSITION_DEFAULTS;
    return normalizeTransitionDefaults(JSON.parse(raw) as unknown);
  } catch {
    return DEFAULT_TRANSITION_DEFAULTS;
  }
}

function HelpDialog({
  kind,
  onClose,
  returnFocus,
}: {
  kind: HelpDialogKind;
  onClose: () => void;
  returnFocus?: HTMLElement | null;
}) {
  const dialogRef = useRef<HTMLElement | null>(null);
  const returnFocusRef = useRef<HTMLElement | null>(null);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    // 生产构建下菜单项与弹窗在同一次提交内切换，effect 运行时 activeElement 可能已是 body；
    // 只接受真实可聚焦元素，避免把 body/documentElement 当作返回目标（dev StrictMode 双 effect 会掩盖）。
    const active = document.activeElement;
    const fallbackActive =
      active instanceof HTMLElement && active !== document.body && active !== document.documentElement ? active : null;
    const preferred = returnFocus && returnFocus.isConnected ? returnFocus : fallbackActive;
    returnFocusRef.current = preferred instanceof HTMLElement ? preferred : null;

    const focusable = () =>
      Array.from(
        dialog.querySelectorAll<HTMLElement>(
          'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
        ),
      ).filter((element) => element.offsetParent !== null || element === document.activeElement);

    // 打开时聚焦第一个可操作项（关闭按钮），Tab 在弹窗内循环，关闭后还原到触发点。
    (focusable()[0] ?? dialog).focus();

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      if (event.key !== 'Tab') return;
      const items = focusable();
      if (items.length === 0) {
        event.preventDefault();
        dialog.focus();
        return;
      }
      const firstItem = items[0];
      const lastItem = items[items.length - 1];
      const current = document.activeElement;
      if (event.shiftKey && (current === firstItem || current === dialog)) {
        event.preventDefault();
        lastItem.focus();
      } else if (!event.shiftKey && current === lastItem) {
        event.preventDefault();
        firstItem.focus();
      }
    };

    dialog.addEventListener('keydown', handleKeyDown);
    return () => {
      dialog.removeEventListener('keydown', handleKeyDown);
      // 还原优先级：记录的真实触发元素 → 菜单入口 → 顶栏第一个可聚焦元素；body 永远不是有效目标。
      const target = returnFocusRef.current;
      if (target?.isConnected && target !== document.body && target !== document.documentElement) {
        target.focus();
        if (document.activeElement === target) return;
      }
      // 触发点（菜单项）随菜单关闭被卸载时，还原到菜单入口，避免焦点掉到 body。
      const candidates = [
        document.querySelector<HTMLElement>('button[title*="全部菜单"]'),
        document.querySelector<HTMLElement>('.ed-topbar .ed-menu-trigger'),
        document.querySelector<HTMLElement>('.ed-topbar button, .ed-topbar a[href], .ed-topbar [tabindex]:not([tabindex="-1"])'),
      ];
      for (const candidate of candidates) {
        if (!candidate || candidate === document.body) continue;
        candidate.focus();
        if (document.activeElement === candidate) return;
      }
    };
  }, [returnFocus]);

  return (
    <div className="help-backdrop" role="presentation" onPointerDown={(event) => {
      if (event.target === event.currentTarget) onClose();
    }}>
      <section
        ref={dialogRef}
        className="help-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="help-dialog-title"
        tabIndex={-1}
      >
        <header>
          <div>
            <span className="help-kicker">LIVECLIPPER EDIT</span>
            <h2 id="help-dialog-title">{kind === 'shortcuts' ? '快捷键' : '关于 LiveClipper'}</h2>
          </div>
          <button type="button" className="help-close" onClick={onClose} aria-label="关闭">×</button>
        </header>
        {kind === 'shortcuts' ? (
          <>
          <div className="shortcut-list">
            <div><span>撤销</span><kbd>Ctrl Z</kbd></div>
            <div><span>重做</span><kbd>Ctrl Shift Z</kbd></div>
            <div><span>显示 / 隐藏 AI 助手</span><kbd>Ctrl J</kbd></div>
            <div><span>保存工程</span><kbd>Ctrl S</kbd></div>
            <div><span>导出影片</span><kbd>Ctrl E</kbd></div>
            <div><span>在播放头添加标记</span><kbd>M</kbd></div>
            <div><span>选择 / 轨道选择 / 波纹 / 滚动</span><kbd>V / A / B / N</kbd></div>
            <div><span>比率拉伸 / 刀片 / 滑移 / 幻灯片</span><kbd>R / C / Y / U</kbd></div>
            <div><span>钢笔 / 手形 / 缩放</span><kbd>P / H / Z</kbd></div>
            <div><span>设置入点 / 出点（Shift 清除）</span><kbd>I / O</kbd></div>
            <div><span>选中最近剪点并打开修剪监视器</span><kbd>Shift T</kbd></div>
            <div><span>切换修剪类型（修剪监视器内）</span><kbd>Ctrl T</kbd></div>
            <div><span>三点编辑：插入 / 覆盖</span><kbd>, / .</kbd></div>
            <div><span>源监视器设入点 / 出点</span><kbd>Alt I / O</kbd></div>
            <div><span>复位时间线缩放</span><kbd>Shift Z</kbd></div>
            <div><span>复制 / 粘贴片段</span><kbd>Ctrl C / V</kbd></div>
            <div><span>复制片段到下一段</span><kbd>Ctrl Alt D</kbd></div>
            <div><span>波纹删除片段</span><kbd>Delete</kbd></div>
            <div><span>选择上一个 / 下一个片段</span><kbd>Ctrl ↑ / ↓</kbd></div>
            <div><span>在播放头添加关键帧</span><kbd>K</kbd></div>
            <div><span>滑移片段内容</span><kbd>Alt ← / →</kbd></div>
            <div><span>滚动相邻切点</span><kbd>Shift C</kbd></div>
            <div><span>时间线吸附开关</span><kbd>S</kbd></div>
            <div><span>在剪点添加默认视频转场</span><kbd>Ctrl D</kbd></div>
            <div><span>在剪点添加默认音频转场</span><kbd>Ctrl Shift D</kbd></div>
            <div><span>为所有兼容剪点批量加转场</span><kbd>Shift D</kbd></div>
            <div><span>移除选中转场</span><kbd>Delete</kbd></div>
            <div><span>切换工作区（剪辑 / 装配 / 调色 / 效果 / 音频 / 字幕 / 学习 / 审阅 / 全部）</span><kbd>Alt Shift 1..9</kbd></div>
            <div><span>重置当前工作区布局</span><kbd>Alt Shift 0</kbd></div>
            <div><span>播放 / 暂停</span><kbd>Space</kbd></div>
            <div><span>播放头后退 / 前进 1 秒（Shift 5 秒）</span><kbd>J / L</kbd></div>
            <div><span>播放头逐帧（Shift 1 秒）</span><kbd>← / →</kbd></div>
            <div><span>跳到序列开头 / 末尾</span><kbd>Home / End</kbd></div>
            <div><span>时间线放大 / 缩小</span><kbd>+ / -</kbd></div>
            <div><span>复位时间线缩放</span><kbd>Shift Z</kbd></div>
            <div><span>调整面板尺寸</span><kbd>拖动分隔条 / ←→ / Home End</kbd></div>
            <div><span>关闭弹窗 / 抽屉</span><kbd>Esc</kbd></div>
          </div>
          <p
            style={{
              margin: '2px 0 0',
              color: 'var(--lc-text-secondary, #a5a5a0)',
              fontSize: 11,
              lineHeight: 1.6,
            }}
          >
            提示：分隔条聚焦后可用方向键或 Home / End 调整；删除留空隙（Shift+Delete）暂未支持。
          </p>
          </>
        ) : (
          <div className="about-copy">
            <p>LiveClipper 是一个本地优先的直播高光剪辑台。</p>
            <p>素材、转写和导出都留在本机；AI 结果只作为候选，写入时间线前仍需要你确认。</p>
            <p className="about-version">EDIT · 本地工作区 · v0.1</p>
          </div>
        )}
        <footer>
          <button type="button" className="primary-button" onClick={onClose}>知道了</button>
        </footer>
      </section>
    </div>
  );
}

/** 序列标签条的固定主序列标签。 */
const PRIMARY_SEQUENCE_TAB: SequenceTab = { id: 'primary', sequenceId: null, name: '主序列', parentId: '' };
/** 标签列表持久化 key：只存标签（不含子序列内容），刷新后按规格只恢复主序列。 */
const TABS_STORAGE_KEY = 'liveclipper.editor.tabs.v1';

export function EditorPage() {
  const initialTab = parseInitialTab();
  const [aiOpen, setAiOpen] = useState(initialTab !== null);
  const [inspectorOpen, setInspectorOpen] = useState(false);
  const [aiTab, setAiTab] = useState<AiTab>(initialTab ?? 'candidates');
  const [adopted, setAdopted] = useState<Set<string>>(new Set());
  const [excluded, setExcluded] = useState<Set<string>>(new Set());
  const [eventsOpen, setEventsOpen] = useState(
    () => new URLSearchParams(window.location.search).get('events') === '1',
  );
  // 抽屉收起动画：先去掉 is-open 播过渡，等过渡结束再卸载（见 useSlideMount）。
  const aiPanel = useSlideMount(aiOpen);
  const inspectorPanel = useSlideMount(inspectorOpen);
  const [selectedItemId, setSelectedItemId] = useState<string | null>(null);
  /** 空白点击显式取消选择时，抑制 currentItem 兜底选中（否则时间线永远高亮播放头所在片段）。 */
  const [selectionCleared, setSelectionCleared] = useState(false);
  const [selectedTransitionId, setSelectedTransitionId] = useState<string | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const [renderOpen, setRenderOpen] = useState(false);
  const [refinementOpen, setRefinementOpen] = useState(false);
  // 窄窗把效果控件从浮窗改成右侧停靠，避免它压住整行单列的节目监视器。
  const [narrowLayout, setNarrowLayout] = useState(() => (
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia('(max-width: 1400px)').matches
      : false
  ));
  useEffect(() => {
    // 无 matchMedia 的环境（jsdom / 旧测试桩）直接当作宽屏，不要抛。
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return undefined;
    const query = window.matchMedia('(max-width: 1400px)');
    const sync = () => setNarrowLayout(query.matches);
    query.addEventListener('change', sync);
    return () => query.removeEventListener('change', sync);
  }, []);

  const [transcriptCache, setTranscriptCache] = useState<Record<string, TranscriptSegment[]>>({});
  const [transcriptErrors, setTranscriptErrors] = useState<Record<string, string>>({});
  const [transcriptLoading, setTranscriptLoading] = useState<Record<string, boolean>>({});
  const transcriptRequests = useRef(new Map<string, object>());
  const transcriptAttempts = useRef(new Set<string>());
  const transcriptLifetime = useRef<object | null>(null);
  useEffect(() => {
    transcriptLifetime.current = {};
    return () => {
      transcriptLifetime.current = null;
      transcriptRequests.current.clear();
      transcriptAttempts.current.clear();
    };
  }, []);
  const [sequenceOpen, setSequenceOpen] = useState(false);
  const [projectHistoryOpen, setProjectHistoryOpen] = useState(false);
  const [helpDialog, setHelpDialog] = useState<HelpDialogKind | null>(null);
  const helpReturnFocusRef = useRef<HTMLElement | null>(null);
  const [sequenceSettingsOpen, setSequenceSettingsOpen] = useState(false);
  const [trackPropertiesOpen, setTrackPropertiesOpen] = useState(false);
  // 嵌套序列 v2：序列标签（主序列 + 已打开的子序列）+ 当前标签。
  const [sequenceTabs, setSequenceTabs] = useState<SequenceTab[]>(() => [PRIMARY_SEQUENCE_TAB]);
  const [activeTabId, setActiveTabId] = useState<string>(PRIMARY_SEQUENCE_TAB.id);
  /** 每个标签的播放头（内存即可；刷新按规格只恢复主序列标签）。 */
  const tabPlayheadsRef = useRef(createSequencePlayheadState());
  const activeTabIdRef = useRef(activeTabId);
  activeTabIdRef.current = activeTabId;
  /** 序列库（API）载入的子序列：只读预览，编辑不写回工程。 */
  const [externalSequences, setExternalSequences] = useState<Record<string, NestedSequence>>({});
  const [workspaceStore] = useState(readWorkspaceStore);
  const [workspaceId, setWorkspaceId] = useState<WorkspaceId>(workspaceStore.current);
  const [workspaceWindows, setWorkspaceWindows] = useState<WorkspacePanelId[]>(
    () => resolveWorkspace(workspaceStore.current, workspaceStore).windows,
  );
  const [editorTool, setEditorTool] = useState<EditorTool>('select');
  // FilmCraft Trim Monitor：编辑点只存会话态（新增工程字段会被后端 DTO round-trip 丢弃）。
  const [editPoint, setEditPoint] = useState<EditPoint | null>(null);
  // 三点编辑：源 In/Out 也存会话态（按素材 id 记，切换素材不丢；刷新即清空，v1 限制）。
  const [sourceMarks, setSourceMarks] = useState<Record<string, { in: number | null; out: number | null }>>({});
  const [snapEnabled, setSnapEnabled] = useState(true);
  const [captionsVisible, setCaptionsVisible] = useState(true);
  const [perceptionEnabled, setPerceptionEnabled] = useState(false);
  const [statusMessage, setStatusMessage] = useState('等待后端数据');
  const [layout, setLayout] = useState<EditorLayout>(
    () => resolveWorkspace(workspaceStore.current, workspaceStore).layout,
  );
  const [transitionDefaults, setTransitionDefaults] = useState<TransitionDefaults>(readTransitionDefaults);
  const programRef = useRef<HTMLVideoElement>(null);
  const sourcePreviewRef = useRef<HTMLVideoElement>(null);
  const timelineRef = useRef<TimelineHandle>(null);
  const resizeRef = useRef<{
    kind: ResizeKind;
    x: number;
    y: number;
    layout: EditorLayout;
    viewerWidth: number;
  } | null>(null);

  const session = useEditorSession();

  // 打开「配乐」tab 时才拉本地音乐素材，避免每次进编辑器都探一遍音乐服务。
  useEffect(() => {
    if (aiOpen && aiTab === 'music') void session.loadMusicLibrary();
  }, [aiOpen, aiTab, session.loadMusicLibrary]);
  const { selectedClip, payload, project, events } = session;
  // D52：引擎揭幕（revealed）的唯一决策点在父侧。`signal` 由 EngineStage 在引擎装载完成后交出，
  // 宿主拿到第一份同步成功的工程、且没有绑不上的素材，才调用它——否则引擎会先露出它自带的演示画面。
  const engineReveal = useRef<null | (() => void)>(null);
  const [engineArmed, setEngineArmed] = useState(false);
  // 右栏写入用（W3-wire）：只借引擎的写入口，不在这里缓存任何片段状态。
  const engineAdapter = useRef<EngineAdapter | null>(null);
  const requestTranscript = useCallback((id: string) => {
    const lifetime = transcriptLifetime.current;
    if (!lifetime) return;
    const request = {};
    transcriptRequests.current.set(id, request);
    transcriptAttempts.current.add(id);
    setTranscriptLoading((loading) => ({ ...loading, [id]: true }));
    setTranscriptErrors((errors) => ({ ...errors, [id]: '' }));
    const current = () => transcriptLifetime.current === lifetime && transcriptRequests.current.get(id) === request;
    void getEditor(id).then((data) => {
      if (!current()) return;
      session.mergeTranscript(id, data.transcript);
      setTranscriptCache((cache) => ({ ...cache, [id]: data.transcript?.segments ?? [] }));
    }).catch((error: unknown) => {
      if (!current()) return;
      const message = error instanceof Error ? error.message : String(error);
      setTranscriptErrors((errors) => ({ ...errors, [id]: message }));
      setToast(`转写读取失败：${message}`);
    }).finally(() => {
      if (!current()) return;
      transcriptRequests.current.delete(id);
      setTranscriptLoading((loading) => ({ ...loading, [id]: false }));
    });
  }, [session.mergeTranscript]);
  const playheadProjectId = readySequenceProjectId(selectedClip?.id ?? null, payload?.clip?.id ?? null, session.loadState, project !== null);
  const sourceUrl = useMemo(
    () => (selectedClip ? getVideoUrl(selectedClip.id) : null),
    [selectedClip],
  );
  const markers = useMemo(() => project?.markers ?? [], [project?.markers]);
  // FilmCraft 监视器：序列 In/Out（I/O 标记；提升/提取与「转到入/出点」用它）。
  const [workArea, setWorkArea] = useState<{ in: number | null; out: number | null }>({ in: null, out: null });
  const items = useMemo(
    () => effectiveTimeline(project, selectedClip, payload?.duration ?? 0),
    [project, selectedClip, payload?.duration],
  );
  const transitions = useMemo<EditorTransition[]>(
    () => project?.transitions ?? [],
    [project?.transitions],
  );
  const fps = project?.fps || 30;
  const sourceDurations = useMemo(
    () => (selectedClip && payload?.duration ? { [selectedClip.id]: payload.duration } : undefined),
    [selectedClip, payload?.duration],
  );
  const totalDuration = useMemo(
    () => sequenceDuration(items, transitions, fps, sourceDurations),
    [items, transitions, fps, sourceDurations],
  );

  // 编辑点失效清理：片段被删/工程还原后自动退出修剪监视器。
  useEffect(() => {
    if (!editPoint) return;
    if (!items.some((item) => item.id === editPoint.clipId)) setEditPoint(null);
  }, [editPoint, items]);

  // ── 嵌套序列 v2：标签栈（主序列 + 已打开子序列），子序列可编辑并写回 nestedSequences ──
  const tabChain = useMemo<SequenceTab[]>(() => {
    const chain: SequenceTab[] = [];
    const seen = new Set<string>();
    let cursor: SequenceTab | undefined =
      sequenceTabs.find((tab) => tab.id === activeTabId) ?? sequenceTabs[0];
    while (cursor && !seen.has(cursor.id)) {
      seen.add(cursor.id);
      chain.unshift(cursor);
      if (!cursor.parentId) break;
      const parent = sequenceTabs.find((tab) => tab.id === cursor?.parentId);
      cursor = parent;
    }
    return chain.length > 0 ? chain : [PRIMARY_SEQUENCE_TAB];
  }, [sequenceTabs, activeTabId]);
  const activeTab = tabChain[tabChain.length - 1] ?? PRIMARY_SEQUENCE_TAB;
  const activeSequenceId = activeTab.sequenceId;
  const nestedView = Boolean(activeSequenceId);
  const nestedDepth = Math.max(0, tabChain.length - 1);
  const nestedSequence = useMemo(
    () =>
      activeSequenceId
        ? nestedSequenceFor(project, activeSequenceId) ?? externalSequences[activeSequenceId] ?? null
        : null,
    [activeSequenceId, project, externalSequences],
  );
  /** 子序列引用缺失（工程里没有、序列库也取不到）：时间线显示空占位。 */
  const nestedMissing = nestedView && !nestedSequence;
  const nestedProject = useMemo(
    () => (nestedSequence ? buildNestedPreviewProject(project, nestedSequence) : null),
    [nestedSequence, project],
  );
  const nestedItems = useMemo(() => nestedProject?.timeline ?? [], [nestedProject]);
  const viewProject = nestedView ? nestedProject : project;
  const viewItems = nestedView ? nestedItems : items;
  const viewTransitions = nestedView ? nestedProject?.transitions ?? [] : transitions;
  const viewFps = nestedView ? nestedSequence?.fps || fps : fps;
  const viewSourceDurations = nestedView ? undefined : sourceDurations;
  const viewTotalDuration = useMemo(
    () => sequenceDuration(viewItems, viewTransitions, viewFps, viewSourceDurations),
    [viewItems, viewTransitions, viewFps, viewSourceDurations],
  );
  // 子序列视图里不允许修剪父序列（v1）：进入时退出修剪监视器。
  useEffect(() => {
    if (nestedView) setEditPoint(null);
  }, [nestedView]);
  const nestedNames = useMemo(() => {
    const names: Record<string, string> = {};
    Object.values(project?.nestedSequences ?? {}).forEach((sequence) => {
      names[sequence.id] = sequence.name;
    });
    Object.values(externalSequences).forEach((sequence) => {
      if (!names[sequence.id]) names[sequence.id] = sequence.name;
    });
    return names;
  }, [project?.nestedSequences, externalSequences]);
  /** 面包屑：主序列 › 嵌套:名称 › …（点击任意层切回去）。 */
  const sequenceBreadcrumbs = useMemo<SequenceCrumb[]>(
    () =>
      tabChain.map((tab) => ({
        tabId: tab.id,
        label: tab.id === PRIMARY_SEQUENCE_TAB.id ? '主序列' : `嵌套:${tab.name}`,
      })),
    [tabChain],
  );
  const legalCuts = useMemo(() => {
    const cuts: Array<{ itemId: string; track: number; time: number }> = [];
    const existingNextIds = new Set(transitionCuts(items, transitions, fps, sourceDurations).map((cut) => cut.next.id));
    for (const item of items) {
      if (existingNextIds.has(item.id)) continue;
      const availability = cutAvailability(items, item.id, fps, undefined, sourceDurations);
      if (!availability.available) continue;
      cuts.push({
        itemId: item.id,
        track: trackOf(item),
        time: itemSequenceStart(items, item.id, transitions, fps, sourceDurations),
      });
    }
    return cuts.sort((left, right) => left.time - right.time);
  }, [items, transitions, fps, sourceDurations]);
  const baseItems = useMemo(() => viewItems.filter((item) => trackOf(item) === 1), [viewItems]);
  const previewVideoItems = baseItems;
  const player = useSequencePlayer(
    programRef,
    previewVideoItems,
    activeTabId,
    viewTransitions,
    viewFps,
    viewSourceDurations,
    getVideoUrl,
    editPoint === null || nestedView,
    viewTotalDuration,
  );
  // 键盘/按钮回调是在 effect 里注册的，闭包里的 player.sequenceTime 会滞后一帧；
  // 用 ref 读取最新的播放头，避免连按方向键时一半按键被算成同一个旧位置。
  const sequenceTimeRef = useRef(player.sequenceTime);
  sequenceTimeRef.current = player.sequenceTime;
  const previewOverlays = useMemo<ProgramOverlay[]>(() => {
    if (!viewProject) return [];
    const hiddenTracks = viewProject.hiddenTracks ?? [];
    const mutedTracks = viewProject.mutedTracks ?? [];
    const soloedTracks = viewProject.soloedTracks ?? [];
    return viewItems
      .filter((item) => {
        const track = trackOf(item);
        return track >= 2 && track <= 3 && !hiddenTracks.includes(track) && !item.disabled;
      })
      .map((item) => {
        const sequenceStart = itemSequenceStart(
          viewItems,
          item.id,
          viewTransitions,
          viewFps,
          viewSourceDurations,
        );
        const duration = (item.end - item.start) / Math.max(0.01, item.speed ?? 1);
        return { item, sequenceStart, duration };
      })
      .filter(
        ({ sequenceStart, duration }) =>
          player.sequenceTime >= sequenceStart - 0.01 &&
          player.sequenceTime < sequenceStart + duration - 0.01,
      )
      .sort((left, right) => trackOf(left.item) - trackOf(right.item))
      .map(({ item, sequenceStart, duration }) => {
        const track = trackOf(item);
        return {
          id: item.id,
          url: getVideoUrl(item.clipId),
          sequenceStart,
          duration,
          item,
          opacity: (item.opacity ?? 1) * (viewProject.trackOpacities?.[track - 1] ?? 1),
          audible: overlayAudible(track, mutedTracks, soloedTracks, {
            audioDetached: item.audioDetached,
            disabled: item.disabled,
            hidden: hiddenTracks.includes(track),
          }),
          volume: Math.pow(10, (item.volumeDb ?? 0) / 20),
        };
      });
  }, [viewItems, player.sequenceTime, viewProject, viewTransitions, viewFps, viewSourceDurations]);

  const currentItem = useMemo(
    () => itemForSequence(viewItems, player.sequenceTime, viewTransitions, viewFps)?.item ?? null,
    [viewItems, player.sequenceTime, viewTransitions, viewFps],
  );
  const engineSync = useEngineSync();
  // D52/D53：引擎揭幕的唯一决策点。`signal` 由 EngineStage 在装载完成后交出；只有第一份工程同步成功、
  // 且没有绑不上的素材，才允许揭幕——否则用户先看到的是引擎自带的演示画面。
  useEffect(() => {
    const signal = engineReveal.current;
    if (!engineArmed || !signal) return;
    if (!engineSync.firstSyncDone || engineSync.needsHostRender) return;
    engineReveal.current = null;
    signal();
  }, [engineArmed, engineSync, engineSync.firstSyncDone, engineSync.needsHostRender]);
  useEffect(() => {
    engineSync.update({
      project: viewProject, items: viewItems, transitions: viewTransitions, fps: viewFps,
      duration: viewTotalDuration, sourceDurations: viewSourceDurations,
      sequenceTime: player.sequenceTime, isPlaying: player.isPlaying,
      selectedItemId: selectionCleared ? null : selectedItemId ?? currentItem?.id ?? null,
    });
  }, [engineSync, viewProject, viewItems, viewTransitions, viewFps, viewTotalDuration,
    viewSourceDurations, player.sequenceTime, player.isPlaying, selectedItemId, selectionCleared, currentItem?.id]);
  /** 只有普通片段（非嵌套、非只读视图）可以创建为嵌套序列。 */
  const nestedTargetItem = useMemo(() => {
    const targetId = selectedItemId ?? currentItem?.id ?? null;
    if (!targetId) return null;
    return items.find((item) => item.id === targetId) ?? null;
  }, [items, selectedItemId, currentItem?.id]);
  const canCreateNested = Boolean(
    nestedTargetItem && !nestedTargetItem.nestedSequenceId && !nestedView,
  );
  /** 效果库作用目标：优先选中片段，否则播放头所在片段（与嵌套创建同一语义）。 */
  const libraryTargetLabel = nestedTargetItem
    ? `${trackOf(nestedTargetItem) <= 3 ? `V${trackOf(nestedTargetItem)}` : `A${trackOf(nestedTargetItem) - 3}`} · ${formatClock(
        itemSequenceStart(items, nestedTargetItem.id, transitions, fps),
      )}`
    : null;
  const programPreview = useMemo(
    () => itemForSequence(previewVideoItems, player.sequenceTime, viewTransitions, viewFps),
    [previewVideoItems, player.sequenceTime, viewTransitions, viewFps],
  );
  const programPreviewItem = programPreview?.item ?? null;
  const programSource = resolveProgramSource(programPreview, getVideoUrl);
  /** 播放头落在空隙里（V1 没有片段、也没有叠加层）：节目监视器显示纯黑，和 FilmCraft 的 gap 表现一致。 */
  const programGapBlank = Boolean(
    programPreviewItem === null
      && previewOverlays.length === 0
      && !(viewProject?.hiddenTracks ?? []).includes(1),
  );
  const programBaseFade = programPreview ? clipFadeFactor(programPreview.item, programPreview.local) : 1;
  const previewAudioLayers = useMemo<ProgramAudioLayer[]>(() => {
    if (!viewProject) return [];
    const mutedTracks = viewProject.mutedTracks ?? [];
    const soloedTracks = viewProject.soloedTracks ?? [];
    return viewItems
      .filter((item) => {
        const track = trackOf(item);
        if (track < 4 || track > 6 || item.disabled) return false;
        return includeTrackAudio(track, mutedTracks, soloedTracks);
      })
      .map((item) => {
        const sequenceStart = itemSequenceStart(
          viewItems,
          item.id,
          viewTransitions,
          viewFps,
          viewSourceDurations,
        );
        const duration = (item.end - item.start) / Math.max(0.01, item.speed ?? 1);
        return { item, sequenceStart, duration };
      })
      .filter(
        ({ sequenceStart, duration }) =>
          player.sequenceTime >= sequenceStart - 0.01 &&
          player.sequenceTime < sequenceStart + duration - 0.01,
      )
      .map(({ item, sequenceStart, duration }) => ({
        id: item.id,
        url: getVideoUrl(item.clipId),
        sequenceStart,
        duration,
        item,
        volume: Math.pow(10, (item.volumeDb ?? 0) / 20),
        muted: false,
      }));
  }, [viewItems, player.sequenceTime, viewProject, viewTransitions, viewFps, viewSourceDurations]);
  const baseAudioMuted = useMemo(() => {
    if (!viewProject) return true;
    const mutedTracks = viewProject.mutedTracks ?? [];
    const soloedTracks = viewProject.soloedTracks ?? [];
    if (programPreviewItem?.disabled || programPreviewItem?.audioDetached) return true;
    return !includeBaseAudio(viewProject.baseAudioMuted === true, mutedTracks, soloedTracks);
  }, [programPreviewItem, viewProject]);
  const activeEvent = useMemo(
    () =>
      nestedView
        ? null
        : events.find(
            (event) => player.sourceTime >= event.start - 0.02 && player.sourceTime <= event.end + 0.02,
          ) ?? null,
    [events, player.sourceTime, nestedView],
  );
  const perceptionItem = selectionCleared ? null : viewItems.find((item) => item.id === selectedItemId);
  const perceptionClipId = perceptionItem?.nestedSequenceId ? null : perceptionItem?.clipId ?? selectedClip?.id ?? null;
  const perceptionParams = { threshold: 0.3, window: 1 };
  const perception = usePerception(perceptionEnabled ? perceptionClipId : null, perceptionParams);
  const refinementItem = viewItems.find((item) => item.id === selectedItemId) ?? programPreviewItem;
  const refinementClipId = refinementItem?.clipId ?? selectedClip?.id ?? null;
  const transcriptClipIds = [...new Set([
    ...items.map((item) => item.clipId),
    refinementClipId,
    programPreviewItem?.clipId,
  ].filter((id): id is string => Boolean(id)))].sort().join('|');
  useEffect(() => {
    for (const id of transcriptClipIds.split('|').filter(Boolean)) {
      if (id === selectedClip?.id || transcriptAttempts.current.has(id)) continue;
      requestTranscript(id);
    }
  }, [transcriptClipIds, selectedClip?.id, requestTranscript]);
  const originalCaptions = (id: string) => id === selectedClip?.id
    ? payload?.transcript?.segments ?? []
    : transcriptCache[id] ?? [];
  const effectiveCaptions = (id: string) => {
    const segments = captionsFor(project, id, originalCaptions(id));
    if (id === selectedClip?.id && !(project != null && hasCaptionOverride(project, id)) && project?.selectedSegments != null) {
      return segments.filter((_, index) => project.selectedSegments!.includes(index));
    }
    return segments;
  };
  const refinementSegments = refinementClipId ? effectiveCaptions(refinementClipId) : [];
  const refinementLoading = Boolean(refinementClipId && transcriptLoading[refinementClipId]);
  const programCaptionTime = programPreview
    ? programPreview.item.start + programPreview.local * Math.max(0.01, programPreview.item.speed ?? 1)
    : player.sourceTime;
  const caption = programPreviewItem
    ? effectiveCaptions(programPreviewItem.clipId).find((segment) =>
      programCaptionTime >= segment.start - 0.02 && programCaptionTime <= segment.end + 0.02)?.text ?? null
    : null;
  const closeRefinement = () => {
    setRefinementOpen(false);
    document.getElementById('refinement-toggle')?.focus();
  };
  const openRefinement = () => {
    setAiOpen(false);
    setInspectorOpen(false);
    setRefinementOpen(true);
  };
  const seekRefinement = (sourceTime: number) => {
    if (!refinementItem) {
      if (refinementClipId === selectedClip?.id) player.seekSource(sourceTime);
      return;
    }
    const offset = (Math.min(refinementItem.end, Math.max(refinementItem.start, sourceTime)) - refinementItem.start)
      / Math.max(0.01, refinementItem.speed ?? 1);
    player.seekSequence(itemSequenceStart(viewItems, refinementItem.id, viewTransitions, viewFps, viewSourceDurations) + offset);
  };

  const itemLabels = useMemo(() => {
    if (nestedView) return nestedItemLabels(viewItems);
    const labels: Record<string, string> = {};
    items.forEach((item: TimelineItem, index) => {
      const event = events.find(
        (candidate) => Math.abs(candidate.start - item.start) < 0.05 && Math.abs(candidate.end - item.end) < 0.05,
      );
      labels[item.id] = event?.title || `片段 ${String(index + 1).padStart(2, '0')}`;
    });
    return labels;
  }, [events, items, nestedView, viewItems]);

  const inspectorItem = useMemo(
    () => (selectedItemId ? items.find((item) => item.id === selectedItemId) : currentItem) ?? null,
    [currentItem, items, selectedItemId],
  );
  const inspectorIndex = inspectorItem ? items.findIndex((item) => item.id === inspectorItem.id) : -1;
  const inspectorSequenceStart = useMemo(
    () => (inspectorItem ? itemSequenceStart(items, inspectorItem.id, transitions, fps, sourceDurations) : 0),
    [inspectorItem, items, transitions, fps, sourceDurations],
  );
  const selectedTransition = useMemo(
    () => transitions.find((transition) => transition.id === selectedTransitionId) ?? null,
    [transitions, selectedTransitionId],
  );

  const sourcePreview = !project?.timeline?.length;

  useEffect(() => {
    setAdopted(new Set());
    setExcluded(new Set());
    setSelectedItemId(null);
    setStatusMessage(selectedClip ? `已载入「${clipTitle(selectedClip)}」` : '等待素材');
  }, [selectedClip?.id]);


  useEffect(() => {
    if (!toast) return undefined;
    const timer = window.setTimeout(() => setToast(null), 2600);
    return () => window.clearTimeout(timer);
  }, [toast]);

  // R51-EP2：保存状态只由顶栏保存标记显示一次（「已保存 · HH:MM:SS」原先在状态栏消息、状态栏保存标记、
  // 顶栏标记各出现一次）。状态栏消息只在失败时带上具体原因（状态栏保存标记的 title 也读它）。
  useEffect(() => {
    if (session.saveState === 'error') setStatusMessage(`保存失败：${session.saveError ?? '未知错误'}`);
  }, [session.saveError, session.saveState]);

  const hasUnsavedRef = useRef(false);
  hasUnsavedRef.current =
    session.hasUnsavedChanges || session.saveState === 'dirty' || session.saveState === 'saving';
  const leavingRef = useRef(false);
  const flushSaveRef = useRef(session.flushSave);
  flushSaveRef.current = session.flushSave;
  const getHasUnsavedRef = useRef(session.getHasUnsavedChanges);
  getHasUnsavedRef.current = session.getHasUnsavedChanges;

  useEffect(() => {
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      if (hasUnsavedRef.current && !leavingRef.current) {
        event.preventDefault();
        event.returnValue = '';
      }
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, []);

  // R51-EP2：握手直接读取保存队列；flush 后的 React 状态可能尚未提交。
  useEffect(
    () =>
      installCloseHandshake(() => ({
        dirty: getHasUnsavedRef.current(),
        flush: () => flushSaveRef.current(),
      })),
    [],
  );

  /** 先把排队的修改写出去；写不出去（冲突 / 失败）时提示并返回 false，调用方就此停下。 */
  const ensureSaved = async (action: string): Promise<boolean> => {
    if (await session.flushSave()) return true;
    const message = `${action}前保存未完成，请先处理保存问题`;
    setToast(message);
    setStatusMessage(message);
    return false;
  };

  const handleOpenExport = () => {
    void ensureSaved('导出').then((saved) => {
      if (saved) setRenderOpen(true);
    });
  };

  /** 关闭工程（离开编辑器）：保存成功直接走；没保存成功就让用户确认是否放弃。 */
  const leaveEditor = (url: string) => {
    void session.flushSave().then((saved) => {
      if (!saved && !window.confirm('还有修改没有保存成功，仍要离开编辑器吗？')) return;
      // 用户已确认，beforeunload 不再二次拦截（离开时 hook 的 pagehide 兜底照旧写草稿）。
      leavingRef.current = true;
      window.location.assign(url);
    });
  };

  const openAi = (tab: AiTab = 'candidates') => {
    setAiTab(tab);
    setInspectorOpen(false);
    setRefinementOpen(false);
    setAiOpen(true);
  };

  const toggleInspector = () => {
    setRefinementOpen(false);
    setInspectorOpen((open) => {
      if (!open) setAiOpen(false);
      return !open;
    });
  };

  const handleSelectClip = (clipId: string) => {
    if (clipId === selectedClip?.id) return;
    session.selectClip(clipId);
    const url = new URL(window.location.href);
    url.searchParams.set('clip', clipId);
    window.history.replaceState({}, '', url);
    setStatusMessage('切换素材…');
  };

  const handleAdoptEvent = (eventId: string) => {
    const event = events.find((candidate) => candidate.id === eventId);
    if (!event) return;
    setAdopted((current) => {
      const next = new Set(current);
      if (next.has(eventId)) {
        next.delete(eventId);
        setStatusMessage(`已撤销采用「${event.title || '候选'}」`);
      } else {
        next.add(eventId);
        setStatusMessage(`已采用「${event.title || '候选'}」，尚未写入时间线`);
      }
      return next;
    });
  };

  const handleApply = () => {
    if (!project || !selectedClip || adopted.size === 0) return;
    const chosen = events.filter((event) => adopted.has(event.id));
    if (chosen.length === 0) return;
    const next = buildProjectAfterApplying(project, selectedClip, chosen);
    if (next === project || !next.timeline || next.timeline.length === 0) {
      setToast('没有可加入的候选');
      return;
    }
    session.updateProject(next, `应用 ${chosen.length} 个候选到时间线`);
    setSelectedItemId(next.timeline[0]?.id ?? null);
    setAdopted(new Set());
    setToast(`已加入时间线：${chosen.length} 个片段，等待保存`);
    setStatusMessage(`已应用 ${chosen.length} 个候选，正在保存`);
  };

  const handleToggleExcludeEvent = (eventId: string) => {
    const event = events.find((candidate) => candidate.id === eventId);
    const wasExcluded = excluded.has(eventId);
    setExcluded((current) => {
      const next = new Set(current);
      if (next.has(eventId)) next.delete(eventId);
      else next.add(eventId);
      return next;
    });
    if (!wasExcluded) {
      setAdopted((current) => {
        if (!current.has(eventId)) return current;
        const next = new Set(current);
        next.delete(eventId);
        return next;
      });
    }
    setStatusMessage(
      wasExcluded
        ? `已恢复「${event?.title || '候选'}」为待定`
        : `已排除「${event?.title || '候选'}」，不会写入时间线`,
    );
  };

  const handleLocateEvent = (event: EditorEventView) => {
    player.seekSequence(event.start);
    const matched = items.find(
      (item) => Math.abs(item.start - event.start) < 0.05 && Math.abs(item.end - event.end) < 0.05,
    );
    setSelectedItemId(matched?.id ?? null);
    setSelectedTransitionId(null);
    setStatusMessage(`已定位「${event.title || '候选'}」· ${formatClock(event.start)}`);
  };

  const toggleEvents = () => {
    const next = !eventsOpen;
    setEventsOpen(next);
    if (next) {
      setAiOpen(false);
      setInspectorOpen(false);
      setStatusMessage('事件列表：点选定位，勾选后应用到时间线');
    }
  };

  const handleUndo = () => {
    session.undo();
    setStatusMessage('已撤销上一处修改');
  };
  const handleRedo = () => {
    session.redo();
    setStatusMessage('已重做上一处修改');
  };

  const handleSeek = (time: number) => {
    player.seekSequence(time);
  };

  const handleSelectItem = (itemId: string | null) => {
    setSelectedItemId(itemId);
    setSelectionCleared(itemId === null);
    setSelectedTransitionId(null);
    if (!itemId) return;
    const item = items.find((candidate) => candidate.id === itemId);
    if (item) {
      setStatusMessage(`已选择「${itemLabels[item.id] ?? '片段'}」`);
    }
  };

  const handleSelectTransition = (transitionId: string | null) => {
    setSelectedTransitionId(transitionId);
    if (!transitionId) return;
    setSelectedItemId(null);
    const transition = transitions.find((entry) => entry.id === transitionId);
    if (transition) {
      setStatusMessage(
        `已选择转场：${transitionKindLabel(transition.kind)} · ${transition.duration.toFixed(1)} 秒`,
      );
    }
  };

  const handleAddMarker = (timeOverride?: number) => {
    if (nestedView) {
      handleTimelineNotice('子序列里不能添加标记：标记属于主序列');
      return;
    }
    if (totalDuration <= 0 || !project) {
      setToast('时间线还没有内容，无法添加标记');
      return;
    }
    const time = Math.min(totalDuration, Math.max(0, timeOverride ?? sequenceTimeRef.current));
    if (markers.some((marker) => Math.abs(marker - time) < 0.25)) return;
    const nextMarkers = [...markers, time].sort((a, b) => a - b);
    session.updateProject({ ...project, markers: nextMarkers }, '添加时间线标记');
    setStatusMessage(`已在 ${formatClock(time)} 添加标记`);
  };

  const handleClearMarkers = () => {
    if (!project) return;
    session.updateProject({ ...project, markers: [] }, '清除时间线标记');
    setStatusMessage('已清除全部标记');
  };

  const handleRemoveMarker = (index: number) => {
    if (!project) return;
    const nextMarkers = markers.filter((_, markerIndex) => markerIndex !== index);
    session.updateProject({ ...project, markers: nextMarkers }, '删除时间线标记');
    setStatusMessage('已删除标记');
  };

  // ── FilmCraft 监视器播放控制条（transport） ─────────────────────────────
  const handleMarkWorkArea = (edge: 'in' | 'out') => {
    timelineRef.current?.setWorkArea(edge, sequenceTimeRef.current);
  };

  const handleClearWorkArea = (edge?: 'in' | 'out') => {
    if (!timelineRef.current) return;
    if (edge) {
      timelineRef.current.setWorkArea(edge, null);
      setStatusMessage(edge === 'in' ? '已清除入点' : '已清除出点');
      return;
    }
    timelineRef.current.setWorkArea('in', null);
    timelineRef.current.setWorkArea('out', null);
    setStatusMessage('已清除入点与出点');
  };

  const handleGoToWorkAreaEdge = (edge: 'in' | 'out') => {
    const value = edge === 'in' ? workArea.in : workArea.out;
    const target = edge === 'in' ? value ?? 0 : value ?? totalDuration;
    player.seekSequence(target);
    setStatusMessage(`${edge === 'in' ? '跳转到入点' : '跳转到出点'} ${formatClock(target)}`);
  };

  const handleStepFrames = (frames: number) => {
    const safeFps = fps > 0 ? fps : 30;
    const raw = sequenceTimeRef.current + frames / safeFps;
    // 吸附到帧栅格，避免浮点误差让时间码少一帧（3 段格式按帧向下取整显示）。
    const snapped = Math.round(Math.max(0, Math.min(totalDuration, raw)) * safeFps) / safeFps;
    player.seekSequence(snapped);
  };

  const editWorkAreaRange = (mode: 'lift' | 'extract') => {
    if (!project || !timelineRef.current) return;
    if (nestedView) {
      setToast('子序列里不能提升/提取（请回到主序列）');
      return;
    }
    if (!isRangeUsable(workArea.in, workArea.out, fps)) {
      setToast('先设置入点和出点（I / O）再提升或提取');
      return;
    }
    const rangeIn = workArea.in as number;
    const rangeOut = workArea.out as number;
    const targets = timelineRef.current.getTargetTracks();
    const tracks = Array.from(new Set([targets.video, targets.audio]));
    const result = spliceRange(
      project.timeline ?? [],
      transitions,
      tracks,
      rangeIn,
      rangeOut,
      fps,
      mode,
      sourceDurations,
    );
    if (!result) {
      setToast('区间太短，没有可提升/提取的内容');
      return;
    }
    session.updateProject(
      { ...project, timeline: result.items, transitions: result.transitions },
      mode === 'lift' ? '提升（留下空隙）' : '提取（合拢下游）',
    );
    setStatusMessage(
      mode === 'lift'
        ? `已提升 ${formatClock(result.removedDuration)}：区间变成空隙，下游位置不变`
        : `已提取 ${formatClock(result.removedDuration)}：下游已合拢`,
    );
    if (mode === 'extract') {
      timelineRef.current.setWorkArea('in', null);
      timelineRef.current.setWorkArea('out', null);
      player.seekSequence(rangeIn);
    }
  };

  const handleExportFrame = () => {
    const source = programRef.current;
    if (!source || !source.videoWidth) {
      setToast('节目监视器还没有可导出的画面');
      return;
    }
    const canvas = document.createElement('canvas');
    canvas.width = source.videoWidth;
    canvas.height = source.videoHeight;
    const context = canvas.getContext('2d');
    if (!context) {
      setToast('导出静帧失败：无法创建画布');
      return;
    }
    context.drawImage(source, 0, 0, canvas.width, canvas.height);
    const stamp = formatTimecode(player.sequenceTime, fps).replaceAll(':', '-');
    canvas.toBlob((blob) => {
      if (!blob) {
        setToast('导出静帧失败：无法编码 PNG');
        return;
      }
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `still_${stamp}.png`;
      anchor.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 4000);
      setStatusMessage(`已导出静帧 ${formatTimecode(player.sequenceTime, fps)}`);
    }, 'image/png');
  };

  const handleToggleProxies = () => {
    setToast('代理文件暂未接入：LiveClipper 目前直接用原片剪辑');
  };

  const toggleWorkspaceWindow = (id: WorkspacePanelId) => {
    setWorkspaceWindows((current) =>
      current.includes(id) ? current.filter((entry) => entry !== id) : [...current, id],
    );
  };

  const closeWorkspaceWindow = (id: WorkspacePanelId) => {
    setWorkspaceWindows((current) => current.filter((entry) => entry !== id));
  };

  const applyWorkspace = (id: WorkspaceId) => {
    const store = readWorkspaceStore();
    store.saved[store.current] = { layout, windows: workspaceWindows };
    store.current = id;
    writeWorkspaceStore(store);
    const resolved = resolveWorkspace(id, store);
    setLayout(resolved.layout);
    setWorkspaceWindows(resolved.windows);
    setWorkspaceId(id);
    const preset = workspaceById(id);
    setToast(`已切换工作区：${preset.name}（${preset.english}）`);
    setStatusMessage(`工作区：${preset.name} · ${preset.hint}`);
  };

  const resetWorkspace = () => {
    const store = readWorkspaceStore();
    delete store.saved[workspaceId];
    store.current = workspaceId;
    writeWorkspaceStore(store);
    const resolved = resolveWorkspace(workspaceId, store);
    setLayout(resolved.layout);
    setWorkspaceWindows(resolved.windows);
    const preset = workspaceById(workspaceId);
    setToast(`已重置工作区：${preset.name}`);
    setStatusMessage(`工作区已重置：${preset.name}`);
  };

  const handleSeekStart = () => {
    player.seekSequence(0);
    setStatusMessage('已回到序列开头');
  };

  const handleSeekEnd = () => {
    player.seekSequence(totalDuration);
    setStatusMessage('已跳到序列末尾');
  };

  const handleToggleCaptions = () => {
    setCaptionsVisible((visible) => {
      setStatusMessage(visible ? '已隐藏节目字幕' : '已显示节目字幕');
      return !visible;
    });
  };

  const handleTimelineNotice = (message: string) => {
    setToast(message);
    setStatusMessage(message);
  };

  const handleLoadSequence = (nextProject: typeof project, name: string) => {
    if (!nextProject) return;
    void session.flushSave();
    session.loadProjectSnapshot(nextProject, `载入序列「${name}」`);
    setSequenceOpen(false);
    setSelectedItemId(null);
    setToast(`已载入序列「${name}」，正在保存为当前工程`);
    setStatusMessage(`已载入序列「${name}」`);
  };

  const handleRestoreProjectHistory = (restored: typeof project) => {
    if (!restored || !payload) return;
    const normalized = normalizeProject(restored, payload.duration);
    void session.flushSave();
    session.loadProjectSnapshot(normalized, '恢复工程历史');
    setProjectHistoryOpen(false);
    setSelectedItemId(null);
    setToast('已恢复工程历史，可在保存前继续撤销');
    setStatusMessage('已恢复工程历史，正在保存为当前工程');
  };

  const handleUpdateTimelineItems = (
    nextItems: TimelineItem[],
    label: string,
    coalesceKey?: string,
  ) => {
    if (!project) return;
    // 嵌套视图：编辑的是子序列自己的时间线，写回 project.nestedSequences[seqId]（父序列 items 不变）。
    if (nestedView && activeSequenceId) {
      if (externalSequences[activeSequenceId]) {
        handleTimelineNotice('序列库载入的子序列是只读预览：请先保存到工程再编辑');
        return;
      }
      const base = nestedSequenceFor(project, activeSequenceId);
      if (!base) {
        handleTimelineNotice('子序列内容未加载：当前是空占位，不能编辑');
        return;
      }
      session.updateProject(
        {
          ...project,
          nestedSequences: {
            ...(project.nestedSequences ?? {}),
            [activeSequenceId]: { ...base, timeline: nextItems },
          },
        },
        `编辑嵌套序列：${base.name}`,
        coalesceKey,
      );
      setStatusMessage(`编辑嵌套序列：${base.name}，正在保存`);
      return;
    }
    session.updateProject({ ...project, timeline: nextItems }, label, coalesceKey);
    setStatusMessage(`${label}，正在保存`);
  };

  // ── Trim Monitor（FilmCraft 修剪监视器）──────────────────────────────
  const trimSourceDuration = selectedClip && payload?.duration ? payload.duration : undefined;

  /** 修剪监视器逐帧微调（按钮 / Alt+←→）：连点合并成一步撤销。 */
  const handleTrimNudge = (frames: number) => {
    if (!editPoint || !project) return;
    // 序列首尾的「(gap)」侧不能滚动编辑：与 TrimMonitor 一致自动降级为普通修剪。
    const resolvedKind: EditPointKind =
      editPoint.kind === 'roll'
        ? (() => {
            const sides = editPointSides(items, editPoint, fps, viewTransitions, viewSourceDurations);
            return !sides || sides.out.gap || sides.in.gap ? 'trim' : 'roll';
          })()
        : editPoint.kind;
    const effectiveEditPoint: EditPoint = { ...editPoint, kind: resolvedKind };
    const result = applyEditPointTrim(items, effectiveEditPoint, frames, {
      fps,
      sourceDuration: trimSourceDuration,
    });
    if (!result.ok) {
      setToast(result.reason);
      return;
    }
    handleUpdateTimelineItems(
      result.items,
      result.label,
      `trim:${editPoint.clipId}:${editPoint.edge}:${resolvedKind}`,
    );
    setToast(`${result.label}：${frames > 0 ? '+' : ''}${frames} 帧`);
  };

  /** 动态穿梭提交（K / 拖拽松手）：一次手势 = 一步撤销。 */
  const handleTrimCommit = (nextItems: TimelineItem[], label: string) => {
    handleUpdateTimelineItems(nextItems, label);
  };

  const handleTrimToggleKind = () => {
    if (!editPoint) return;
    const kind = nextEditPointKind(editPoint.kind);
    setEditPoint({ ...editPoint, kind });
    setToast(`修剪类型：${EDIT_POINT_KIND_LABELS[kind]}`);
  };

  const handleExitTrim = () => {
    setEditPoint(null);
    setToast('已退出修剪监视器');
  };

  const handleEditPoint = (next: EditPoint | null) => setEditPoint(next);

  const handleAddClipToTimeline = (clipId: string) => {
    const target = session.clips.find((clip) => clip.id === clipId) ?? null;
    if (!target) {
      handleTimelineNotice('找不到这个素材，请刷新素材库后重试');
      return;
    }
    if (!project) {
      handleTimelineNotice('工程还没有载入，无法加入时间线');
      return;
    }
    const label = clipTitle(target);
    void getEditor(clipId)
      .then((nextPayload) => {
        const duration = nextPayload.duration ?? 0;
        if (!(duration > 0)) {
          handleTimelineNotice(`素材「${label}」时长为 0，无法加入时间线`);
          return;
        }
        const item = defaultTimelineItem(clipId, 0, duration);
        if (nextPayload.hasAudio === false) {
          const added = timelineRef.current?.addItem(item, 'insert') ?? false;
          if (!added) handleTimelineNotice('素材未加入：请检查目标轨是否锁定，或退出只读预览');
          else handleTimelineNotice(`素材「${label}」没有音轨：只加入画面`);
          return;
        }
        const pair = linkAudioCompanion(item, 4);
        const added =
          timelineRef.current?.addAssetWithAudioAt(pair.video, pair.audio, 1, 4, totalDuration, 'insert') ?? false;
        if (!added) handleTimelineNotice('素材未加入：请检查目标轨是否锁定，或退出只读预览');
      })
      .catch(() => {
        handleTimelineNotice(`读取素材「${label}」失败，未能加入时间线`);
      });
  };

  /**
   * 素材库拖放到时间线：落点轨道/时间由 Timeline 算出（含空白区与空隙区）。
   * 时长未知时先取一次 editor payload 补齐，再按 overwrite 语义放入（不推动下游）。
   */
  const handleDropAssetOnTimeline = ({ clipId, title, track, time, duration }: AssetDropPayload) => {
    if (!project) {
      handleTimelineNotice('工程还没有载入，无法放入素材');
      return;
    }
    if (nestedView) {
      handleTimelineNotice('子序列里暂不支持放入新素材（v1）：请回主序列放入');
      return;
    }
    const place = (seconds: number, hasAudio: boolean | undefined) => {
      if (!(seconds > 0)) {
        handleTimelineNotice(`素材「${title}」时长为 0，无法放入时间线`);
        return;
      }
      const item = defaultTimelineItem(clipId, 0, seconds);
      if (hasAudio === false) {
        // 源素材没有音轨：只放画面，不建空的配对音频片段。
        const placed = timelineRef.current?.addAssetAt(item, track, time, 'insert') ?? false;
        if (!placed) handleTimelineNotice('素材未能放入：请检查轨道是否锁定');
        else handleTimelineNotice(`素材「${title}」没有音轨：只放入画面`);
        return;
      }
      const audioTrack = audioTrackForVideoTrack(track);
      const pair = linkAudioCompanion(item, audioTrack);
      const placed =
        timelineRef.current?.addAssetWithAudioAt(pair.video, pair.audio, track, audioTrack, time, 'insert') ?? false;
      if (!placed) handleTimelineNotice('素材未能放入：请检查轨道是否锁定');
    };
    if (duration && duration > 0) {
      place(duration, payload?.hasAudio);
      return;
    }
    void getEditor(clipId)
      .then((nextPayload) => place(nextPayload.duration ?? 0, nextPayload.hasAudio))
      .catch(() => handleTimelineNotice(`读取素材「${title}」失败，未能放入时间线`));
  };

  // 三点编辑：源 In/Out 标记（按素材 id 记会话态）+ 插入 / 覆盖命令。
  const sourceMark = selectedClip ? sourceMarks[selectedClip.id] ?? { in: null, out: null } : { in: null, out: null };

  const setSourceMark = (edge: 'in' | 'out', value: number | null) => {
    if (!selectedClip) {
      handleTimelineNotice('先在项目面板选中一个素材，再设源 In/Out');
      return;
    }
    const duration = payload?.duration ?? 0;
    if (!(duration > 0)) {
      handleTimelineNotice('素材时长未知，暂时不能设源 In/Out');
      return;
    }
    const clamped = value === null ? null : Math.max(0, Math.min(duration, value));
    const current = sourceMarks[selectedClip.id] ?? { in: null, out: null };
    const next = { ...current, [edge]: clamped };
    if (edge === 'in' && next.out !== null && clamped !== null && next.out <= clamped) next.out = null;
    if (edge === 'out' && next.in !== null && clamped !== null && next.in >= clamped) next.in = null;
    setSourceMarks((marks) => ({ ...marks, [selectedClip.id]: next }));
    if (clamped === null) {
      handleTimelineNotice(`已清除源${edge === 'in' ? '入点' : '出点'}`);
    } else {
      handleTimelineNotice(`源${edge === 'in' ? '入点' : '出点'}：${formatTimecode(clamped, fps)}`);
    }
  };

  const handleThreePointEdit = (mode: 'insert' | 'overwrite') => {
    if (!project || !selectedClip) {
      handleTimelineNotice('三点编辑需要一个源素材与已载入的工程');
      return;
    }
    if (nestedView) {
      handleTimelineNotice('子序列里暂不支持三点编辑（v1）：请回主序列');
      return;
    }
    const mediaDuration = payload?.duration ?? 0;
    if (!(mediaDuration > 0)) {
      handleTimelineNotice('素材时长未知，无法三点编辑');
      return;
    }
    const markIn = sourceMark.in ?? 0;
    const markOut = sourceMark.out ?? mediaDuration;
    const rangeStart = Math.max(0, Math.min(mediaDuration, markIn));
    const rangeEnd = Math.max(0, Math.min(mediaDuration, markOut));
    const rangeDuration = rangeEnd - rangeStart;
    if (!(rangeDuration > 1 / Math.max(fps, 1))) {
      handleTimelineNotice('源范围为空：先设源 In/Out（Alt+I / Alt+O）或换一段素材');
      return;
    }
    // FilmCraft 契约：序列 In 优先于播放头（"Three-point editing: sequence In wins over the playhead."）。
    const safeFps = Math.max(fps, 1);
    const workArea = timelineRef.current?.getWorkArea();
    const at = Math.max(0, Math.round((workArea?.in ?? player.sequenceTime) * safeFps) / safeFps);
    const target = timelineRef.current?.getTargetTracks();
    const track = target?.video ?? 1;
    if (track !== 1) {
      handleTimelineNotice('三点编辑 v1 只落在 V1：先把目标轨切回 V1');
      return;
    }
    const item = defaultTimelineItem(selectedClip.id, rangeStart, rangeEnd);
    const placed = timelineRef.current?.addAssetAt(
      item,
      track,
      at,
      mode,
      mode === 'insert' ? '三点编辑：插入' : '三点编辑：覆盖',
    ) ?? false;
    if (!placed) {
      handleTimelineNotice('三点编辑未能落位：请检查轨道是否锁定');
      return;
    }
    const end = at + rangeDuration;
    handleTimelineNotice(
      `${mode === 'insert' ? '插入' : '覆盖'}完成：${formatTimecode(rangeDuration, fps)} @ ${formatTimecode(at, fps)}`,
    );
    window.requestAnimationFrame(() => player.seekSequence(Math.min(totalDuration, end)));
  };

  /** 效果面板的转场卡拖到剪点：Timeline 已解析出剪点左侧片段 id 与类型。 */
  const handleDropTransitionOnTimeline = ({ itemId, kind }: TransitionDropPayload) => {
    if (!project) {
      handleTimelineNotice('工程还没有载入，无法应用转场');
      return;
    }
    if (nestedView) {
      handleTimelineNotice('子序列里暂不支持转场（v1）：转场属于主序列');
      return;
    }
    applyTransitionAtItem(itemId, { kind, duration: transitionDefaults.duration });
  };

  const handleUpdateTransitions = (nextTransitions: EditorTransition[], label: string) => {
    if (!project) return;
    if (nestedView) {
      handleTimelineNotice('子序列里暂不支持转场（v1）：转场属于主序列');
      return;
    }
    session.updateProject({ ...project, transitions: nextTransitions }, label);
    setStatusMessage(`${label}，正在保存`);
  };

  /** 激活某个标签：切换前把播放头记在旧标签上（内存，切换回来时恢复）。 */
  const handleActivateTab = (tabId: string) => {
    if (tabId === activeTabIdRef.current) return;
    rememberSequencePlayhead(tabPlayheadsRef.current, playheadProjectId, activeTabIdRef.current, sequenceTimeRef.current);
    setActiveTabId(tabId);
    setSelectedItemId(null);
    setSelectedTransitionId(null);
  };

  /** 打开（或复用）子序列标签并激活。 */
  const openSequenceTab = (sequenceId: string, name: string) => {
    if (playheadProjectId === null || tabPlayheadsRef.current.projectId !== playheadProjectId) return;
    rememberSequencePlayhead(tabPlayheadsRef.current, playheadProjectId, activeTabIdRef.current, sequenceTimeRef.current);
    const parentId = activeTabIdRef.current;
    setSequenceTabs((tabs) =>
      tabs.some((tab) => tab.id === sequenceId)
        ? tabs
        : [...tabs, { id: sequenceId, sequenceId, name, parentId }],
    );
    setActiveTabId(sequenceId);
    setSelectedItemId(null);
    setSelectedTransitionId(null);
  };

  /** 双击嵌套片段：已打开则激活，否则加载子序列并追加「嵌套:名称」标签（最多 4 层）。 */
  const handleEnterNested = (itemId: string) => {
    if (!project) return;
    const item = viewItems.find((candidate) => candidate.id === itemId);
    const sequenceId = item?.nestedSequenceId?.trim();
    if (!sequenceId) {
      handleTimelineNotice('这个片段不是嵌套片段');
      return;
    }
    const existing = sequenceTabs.find((tab) => tab.id === sequenceId);
    if (existing) {
      handleActivateTab(existing.id);
      return;
    }
    if (nestedDepth >= MAX_NESTED_DEPTH) {
      setToast('嵌套层级不能超过 4 层');
      setStatusMessage('嵌套层级不能超过 4 层');
      return;
    }
    const sequence = nestedSequenceFor(project, sequenceId);
    if (sequence) {
      openSequenceTab(sequence.id, sequence.name);
      setToast(`已进入子序列「${sequence.name}」`);
      setStatusMessage(`子序列「${sequence.name}」· 可编辑，改动写回工程`);
      return;
    }
    const external = externalSequences[sequenceId];
    if (external) {
      openSequenceTab(external.id, external.name);
      return;
    }
    // 工程里没有该子序列：按规格先尝试序列库 API，失败再开空占位标签（「子序列内容未加载」）。
    setToast(`子序列「${sequenceId}」未在工程里找到，正在从序列库加载…`);
    void getEditorSequence(sequenceId)
      .then((payload) => {
        const loaded: NestedSequence = {
          id: sequenceId,
          name: payload.name || `子序列 ${nestedDepth + 1}`,
          timeline: payload.project?.timeline ?? [],
          transitions: payload.project?.transitions ?? [],
          fps: payload.project?.fps || fps,
          resolution: payload.project?.resolution || project.resolution,
        };
        setExternalSequences((current) => ({ ...current, [sequenceId]: loaded }));
        openSequenceTab(loaded.id, loaded.name);
        setToast(`已从序列库载入「${loaded.name}」（只读预览，不写回工程）`);
      })
      .catch(() => {
        openSequenceTab(sequenceId, `缺失 ${sequenceId}`);
        setToast(`子序列「${sequenceId}」内容未加载`);
      });
  };

  /** 将选中片段封装成一级嵌套序列（v1.5 创建）。 */
  const handleCreateNested = () => {
    if (!project) return;
    const targetId = selectedItemId ?? currentItem?.id ?? null;
    if (!targetId) {
      setToast('先选中一个片段，再创建嵌套序列');
      return;
    }
    if (nestedView) {
      setToast('只读子序列视图里不能创建嵌套序列，请先返回主序列');
      return;
    }
    const result = createNestedSequence(project, targetId);
    if (!result) {
      setToast('这个片段不能创建嵌套序列（可能已经是嵌套片段）');
      return;
    }
    session.updateProject(result.project, '创建嵌套序列');
    setSelectedItemId(null);
    setSelectedTransitionId(null);
    setToast(`已创建「${result.sequenceName}」；双击片段进入只读预览，导出时后端会自动展开`);
    setStatusMessage(`嵌套序列「${result.sequenceName}」已创建`);
  };

  /** 返回上一层（Esc / Alt+Left / 面包屑）。 */
  const handleTabBack = () => {
    if (nestedDepth <= 0) return;
    const parentId = activeTab.parentId || PRIMARY_SEQUENCE_TAB.id;
    handleActivateTab(parentId);
    const message = parentId === PRIMARY_SEQUENCE_TAB.id ? '已返回主序列' : '已返回上一层';
    setToast(message);
    setStatusMessage(message);
  };

  /** 关闭标签：父标签关闭时连带关闭其子孙；关闭当前标签则回到父层（不改工程内容）。 */
  const handleCloseTab = (tabId: string) => {
    if (sequenceTabs.length <= 1) return;
    const closing = sequenceTabs.find((tab) => tab.id === tabId);
    if (!closing || tabId === PRIMARY_SEQUENCE_TAB.id) return;
    const doomed = new Set<string>([tabId]);
    let grew = true;
    while (grew) {
      grew = false;
      for (const tab of sequenceTabs) {
        if (tab.parentId && doomed.has(tab.parentId) && !doomed.has(tab.id)) {
          doomed.add(tab.id);
          grew = true;
        }
      }
    }
    const nextTabs = sequenceTabs.filter((tab) => !doomed.has(tab.id));
    const parentId = closing.parentId && !doomed.has(closing.parentId) ? closing.parentId : PRIMARY_SEQUENCE_TAB.id;
    if (doomed.has(activeTabIdRef.current)) {
      rememberSequencePlayhead(tabPlayheadsRef.current, playheadProjectId, activeTabIdRef.current, sequenceTimeRef.current);
      setActiveTabId(parentId);
      setSelectedItemId(null);
      setSelectedTransitionId(null);
    }
    setSequenceTabs(nextTabs.length > 0 ? nextTabs : [PRIMARY_SEQUENCE_TAB]);
    setToast(`已关闭标签「${closing.name}」`);
    setStatusMessage('关闭序列标签不修改工程内容');
  };

  /** 关闭其他标签：保留目标标签及其祖先链（否则面包屑断链）。 */
  const handleCloseOtherTabs = (tabId: string) => {
    const target = sequenceTabs.find((tab) => tab.id === tabId);
    if (!target) return;
    const keep = new Set<string>([target.id]);
    let cursor: SequenceTab | undefined = target;
    while (cursor?.parentId) {
      const parent = sequenceTabs.find((tab) => tab.id === cursor?.parentId);
      if (!parent) break;
      keep.add(parent.id);
      cursor = parent;
    }
    if (tabId !== activeTabIdRef.current) handleActivateTab(tabId);
    setSequenceTabs(sequenceTabs.filter((tab) => keep.has(tab.id)));
    setToast(`已关闭其他 ${sequenceTabs.length - keep.size} 个标签`);
  };

  /** 重命名标签：主序列不可改名；子序列名写回工程（可撤销的一次编辑）。 */
  const handleRenameTab = (tabId: string, name: string) => {
    const trimmed = name.trim();
    if (!trimmed) return;
    const tab = sequenceTabs.find((entry) => entry.id === tabId);
    if (!tab) return;
    if (tab.id === PRIMARY_SEQUENCE_TAB.id) {
      setToast('主序列标签名称固定');
      return;
    }
    setSequenceTabs((tabs) => tabs.map((entry) => (entry.id === tabId ? { ...entry, name: trimmed } : entry)));
    if (project && tab.sequenceId) {
      const sequence = nestedSequenceFor(project, tab.sequenceId);
      if (sequence) {
        session.updateProject(
          {
            ...project,
            nestedSequences: {
              ...(project.nestedSequences ?? {}),
              [tab.sequenceId]: { ...sequence, name: trimmed },
            },
          },
          '重命名嵌套序列',
        );
        setStatusMessage(`嵌套序列已重命名为「${trimmed}」`);
        return;
      }
    }
    setStatusMessage(`标签已重命名为「${trimmed}」（仅本次会话）`);
  };

  const applyTransitionAtItem = (
    itemId: string | null | undefined,
    options?: { duration?: number; audio?: boolean; kind?: EditorTransition['kind'] },
  ) => {
    if (!project) return;
    if (nestedView) {
      handleTimelineNotice('子序列里暂不支持转场（v1）：转场属于主序列');
      return;
    }
    if (!itemId) {
      setToast('播放头附近没有兼容剪点；先选中一段或把播放头移到剪点上');
      return;
    }
    const availability = cutAvailability(items, itemId, fps);
    if (!availability.available || !availability.next || !availability.previous) {
      setToast(availability.reason ?? '该剪点不能添加转场');
      return;
    }
    const existing = findTransition(project.transitions, itemId);
    if (existing) {
      setSelectedTransitionId(existing.id);
      setSelectedItemId(null);
      if (options?.kind && options.kind !== existing.kind) {
        handleUpdateTransitions(
          (project.transitions ?? []).map((transition) =>
            transition.id === existing.id ? { ...transition, kind: options.kind! } : transition,
          ),
          '替换转场类型',
        );
        setToast(`已把转场换成：${transitionKindLabel(options.kind)}`);
      } else {
        setToast('该剪点已有转场，已选中它');
      }
      return;
    }
    const requested = options?.duration ?? transitionDefaults.duration;
    const span = Math.min(
      itemOutputDuration(availability.previous),
      itemOutputDuration(availability.next),
    );
    const duration = Math.max(transitionMinDuration(fps), Math.min(requested, span));
    const next: EditorTransition = {
      id: transitionIdForItem(itemId),
      itemId,
      track: trackOf(availability.next),
      kind: options?.kind ?? transitionDefaults.kind,
      duration,
      align: 'center',
    };
    handleUpdateTransitions(
      [...(project.transitions ?? []), next],
      options?.audio ? '添加音频转场' : '添加转场',
    );
    setSelectedTransitionId(next.id);
    setSelectedItemId(null);
    const kindLabel = transitionKindLabel(options?.kind ?? transitionDefaults.kind);
    const clamped = Math.abs(duration - requested) > 0.001 ? '（已按片段长度截短）' : '';
    setToast(`已添加${options?.audio ? '音频' : ''}转场：${kindLabel} ${duration.toFixed(1)} 秒${clamped}`);
  };

  const handleApplyTransition = (itemId: string | null | undefined) => applyTransitionAtItem(itemId);

  const cutNearPlayhead = () => {
    if (legalCuts.length === 0) return null;
    let best = legalCuts[0];
    let bestDistance = Math.abs(best.time - player.sequenceTime);
    for (const cut of legalCuts) {
      const distance = Math.abs(cut.time - player.sequenceTime);
      if (distance < bestDistance) {
        best = cut;
        bestDistance = distance;
      }
    }
    return { cut: best, distance: bestDistance };
  };

  const handleApplyTransitionAtPlayhead = (audio = false) => {
    const selectedCutReady =
      selectedItemId !== null && cutAvailability(items, selectedItemId, fps).available;
    const near = selectedCutReady ? null : cutNearPlayhead();
    const target = selectedCutReady
      ? selectedItemId
      : near && near.distance <= 1.5
        ? near.cut.itemId
        : null;
    applyTransitionAtItem(target, {
      duration: audio ? transitionDefaults.audioDuration : transitionDefaults.duration,
      audio,
    });
  };

  const handleApplyTransitionsToAllCuts = () => {
    if (!project) return;
    const created: EditorTransition[] = [];
    for (const cut of legalCuts) {
      const availability = cutAvailability(items, cut.itemId, fps);
      if (!availability.available || !availability.next || !availability.previous) continue;
      const span = Math.min(
        itemOutputDuration(availability.previous),
        itemOutputDuration(availability.next),
      );
      created.push({
        id: transitionIdForItem(cut.itemId),
        itemId: cut.itemId,
        track: trackOf(availability.next),
        kind: transitionDefaults.kind,
        duration: Math.max(transitionMinDuration(fps), Math.min(transitionDefaults.duration, span)),
        align: 'center',
      });
    }
    if (created.length === 0) {
      setToast('所有兼容剪点都已有转场');
      return;
    }
    handleUpdateTransitions([...(project.transitions ?? []), ...created], '批量添加转场');
    setSelectedTransitionId(created[0].id);
    setSelectedItemId(null);
    setToast(`已为 ${created.length} 个剪点添加默认转场（V1 真实交叉淡化，其余轨道仅压缩时间线）`);
  };

  /** 效果库：应用指定类型的转场到选中剪点或播放头附近剪点。 */
  const handleApplyLibraryTransition = (kind: EditorTransition['kind']) => {
    if (nestedView) {
      setToast('只读子序列视图里不能添加转场，请先返回主序列');
      return;
    }
    // 转场的 itemId 指向「剪点之后」的片段；选中片段时先找它右侧的剪点，
    // 没有则退回到它左侧剪点（把选中片段作为后一段），再退到播放头附近。
    const selectedCutTarget = (() => {
      if (!nestedTargetItem) return null;
      let startCut: string | null = null;
      for (const candidate of items) {
        if (candidate.id === nestedTargetItem.id) continue;
        const availability = cutAvailability(items, candidate.id, fps);
        if (!availability.available) continue;
        if (availability.previous?.id === nestedTargetItem.id) return candidate.id;
        if (availability.next?.id === nestedTargetItem.id && !startCut) startCut = candidate.id;
      }
      return startCut;
    })();
    const near = selectedCutTarget === null ? cutNearPlayhead() : null;
    const target =
      selectedCutTarget ?? (near && near.distance <= 1.5 ? near.cut.itemId : null);
    applyTransitionAtItem(target, { kind });
  };

  /** 效果库：给目标片段追加音频效果（v1 上限 3 个）。 */
  const handleApplyAudioEffect = (type: AudioEffectType) => {
    if (!project) return;
    if (nestedView) {
      setToast('只读子序列视图里不能编辑效果，请先返回主序列');
      return;
    }
    const target = nestedTargetItem;
    if (!target) {
      setToast('先在时间线选中一个片段，再应用音频效果');
      return;
    }
    const existing = normalizeAudioEffects(target);
    if (existing.length >= MAX_AUDIO_EFFECTS) {
      setToast(`这个片段已有 ${MAX_AUDIO_EFFECTS} 个音频效果（v1 上限）；先移除一个再添加`);
      return;
    }
    const effect = createAudioEffect(type, existing);
    const label = AUDIO_EFFECT_SPECS[type].label;
    handleUpdateTimelineItems(
      items.map((item) =>
        item.id === target.id ? { ...item, effects: [...existing, effect] } : item,
      ),
      `添加音频效果：${label}`,
    );
    setToast(`已添加${label}；在效果控件里微调参数`);
  };

  /** 效果库：给目标片段加淡入/淡出/淡入淡出（0.5 秒，超长时按片段一半截短）。 */
  const handleApplyLibraryFade = (kind: 'in' | 'out' | 'both') => {
    if (!project) return;
    if (nestedView) {
      setToast('只读子序列视图里不能编辑淡化，请先返回主序列');
      return;
    }
    const target = nestedTargetItem;
    if (!target) {
      setToast('先在时间线选中一个片段，再应用淡化');
      return;
    }
    const duration = Math.max(0.1, Math.min(0.5, itemOutputDuration(target) / 2));
    const patch: Partial<TimelineItem> = {};
    if (kind === 'in' || kind === 'both') patch.fadeIn = duration;
    if (kind === 'out' || kind === 'both') patch.fadeOut = duration;
    const label = kind === 'both' ? '添加淡入淡出' : kind === 'in' ? '添加淡入' : '添加淡出';
    handleUpdateTimelineItems(
      items.map((item) => (item.id === target.id ? { ...item, ...patch } : item)),
      label,
    );
    setToast(`${label} ${duration.toFixed(1)} 秒`);
  };

  const handleRemoveTransition = (transitionId: string) => {
    if (!project) return;
    const next = (project.transitions ?? []).filter((entry) => entry.id !== transitionId);
    if (next.length === (project.transitions ?? []).length) return;
    handleUpdateTransitions(next, '移除转场');
    setSelectedTransitionId((current) => (current === transitionId ? null : current));
    setToast('已移除转场');
  };

  const handleUpdateTransition = (
    transitionId: string,
    patch: Partial<Pick<EditorTransition, 'duration' | 'kind' | 'align'>>,
  ) => {
    if (!project) return;
    const next = (project.transitions ?? []).map((entry) => {
      if (entry.id !== transitionId) return entry;
      const merged = { ...entry, ...patch };
      if (patch.duration !== undefined) {
        merged.duration = Math.max(transitionMinDuration(fps), patch.duration);
      }
      return merged;
    });
    handleUpdateTransitions(next, '修改转场');
  };

  const toggleBaseAudioMute = () => {
    if (!project) return;
    const next = project.baseAudioMuted !== true;
    session.updateProject({ ...project, baseAudioMuted: next }, next ? '静音 V1 内嵌音频' : '取消静音 V1 内嵌音频');
  };

  const toggleTrackState = (
    field: 'mutedTracks' | 'soloedTracks' | 'hiddenTracks' | 'lockedTracks',
    track: number,
  ) => {
    if (!project) return;
    const current = new Set(project[field] ?? []);
    const wasEnabled = current.has(track);
    if (wasEnabled) current.delete(track);
    else current.add(track);
    const next = [...current].sort((left, right) => left - right);
    const trackLabel = track <= 3 ? `V${track}` : `A${track - 3}`;
    const label =
      field === 'hiddenTracks'
        ? `${wasEnabled ? '显示' : '隐藏'} ${trackLabel}`
        : field === 'lockedTracks'
          ? `${wasEnabled ? '解锁' : '锁定'} ${trackLabel}`
          : field === 'mutedTracks'
            ? `${wasEnabled ? '取消静音' : '静音'} ${trackLabel}`
            : `${wasEnabled ? '取消独奏' : '独奏'} ${trackLabel}`;
    session.updateProject({ ...project, [field]: next }, label);
    if (
      field === 'lockedTracks' &&
      wasEnabled &&
      inspectorItem &&
      trackOf(inspectorItem) === track
    ) {
      setSelectedItemId(null);
    }
    setStatusMessage(`${label}，正在保存`);
  };

  const handleUpdateInspectorItem = (patch: Partial<TimelineItem>, label: string) => {
    if (!inspectorItem) return;
    setSelectedItemId(inspectorItem.id);
    handleUpdateTimelineItems(
      items.map((item) => (item.id === inspectorItem.id ? { ...item, ...patch } : item)),
      label,
      'inspector-property',
    );
    // 同一次编辑也推给引擎，免得等下一轮整包同步；引擎没写入口的字段由 engineWrite 列出并丢弃。
    const adapter = engineAdapter.current;
    if (adapter) {
      void applyInspectorEdit(adapter, inspectorItem.id, patch as Record<string, unknown>).then((result) => {
        if (!result.ok) setStatusMessage(`引擎未写入：${result.message ?? result.code}`);
      });
    }
  };

  /** 节目监视器蒙版编辑：一次拖拽结束只写一次工程，因此只占一步撤销。 */
  const handleUpdateItemMasks = (itemId: string, masks: TimelineItem['masks'], label: string) => {
    if (!project) return;
    if (nestedView) return;
    const next = items.map((item) => (item.id === itemId ? { ...item, masks } : item));
    session.updateProject({ ...project, timeline: next }, label);
    setStatusMessage(`${label}，正在保存`);
  };

  const handleLocateSource = (time: number) => {
    player.seekSource(time);
    setStatusMessage(`已在源监看定位到 ${formatClock(time)}`);
  };

  const handleExtract = () => {
    void session.extractCandidates().then(() => {
      setToast('已从字幕提取候选');
      setStatusMessage('候选已从字幕更新');
    });
  };

  const handleAutoPlan = () => {
    void (async () => {
      // 自动方案会整份替换工程：先确认本地修改已写出去，写不出去就不生成。
      if (!(await ensureSaved('生成自动方案'))) return null;
      return session.generateAutoPlan();
    })().then((report) => {
      if (!report) return;
      setAiTab('candidates');
      setRefinementOpen(false);
      setInspectorOpen(false);
      setAiOpen(true);
      const generated = report.status === 'ready' || report.status === 'partial';
      setToast(generated ? (report.status === 'partial' ? '已生成部分方案' : '已生成自动方案') : '自动方案未生成');
      setStatusMessage(
        generated
          ? `自动方案：${report.keepCount} 段 · ${report.source || '本地'}`
          : `自动方案失败：${report.error || report.status}`,
      );
    });
  };

  const layoutStyle = {
    '--lc-project-width': `${layout.projectWidth}px`,
    '--lc-audio-width': `${layout.audioWidth}px`,
    // 监视器区高度：persisted 值可拖动，但上界受 34vh / 380px 约束（P1b 时间线增高）。
    '--lc-viewers-height': `clamp(240px, min(${layout.viewersHeight}px, 34vh), 380px)`,
  } as CSSProperties;

  const resetLayout = () => {
    resetWorkspace();
  };

  const beginResize = (kind: ResizeKind) => (event: ReactPointerEvent<HTMLDivElement>) => {
    event.preventDefault();
    const viewerWidth = document.querySelector<HTMLElement>('.viewers')?.getBoundingClientRect().width ?? 800;
    resizeRef.current = {
      kind,
      x: event.clientX,
      y: event.clientY,
      layout,
      viewerWidth,
    };
    document.body.classList.add('is-panel-resizing');
    setStatusMessage('拖动分隔条调整面板大小，双击还原');
    event.currentTarget.focus();
  };

  const nudgeLayout = (kind: ResizeKind, delta: number) => {
    setLayout((current) => {
      if (kind === 'project') return { ...current, projectWidth: limit(current.projectWidth + delta, 240, 460) };
      if (kind === 'audio') return { ...current, audioWidth: limit(current.audioWidth - delta, 180, 340) };
      if (kind === 'viewers') {
        const max = Math.min(620, window.innerHeight - 280);
        return { ...current, viewersHeight: limit(current.viewersHeight + delta, 300, max) };
      }
      return { ...current, viewerSplit: limit(current.viewerSplit + delta / 10, 25, 75) };
    });
  };

  const handleSplitterKey = (kind: ResizeKind) => (event: ReactKeyboardEvent<HTMLDivElement>) => {
    // APG separator：Home/End 复位（与双击一致），方向键按 aria-orientation 调整。
    if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      resetLayout();
      setStatusMessage('面板布局已重置');
      return;
    }
    const horizontal = kind === 'viewers';
    const positive = event.key === (horizontal ? 'ArrowDown' : 'ArrowRight');
    const negative = event.key === (horizontal ? 'ArrowUp' : 'ArrowLeft');
    if (!positive && !negative) return;
    event.preventDefault();
    const magnitude = kind === 'viewerSplit' ? 2 : 12;
    nudgeLayout(kind, positive ? magnitude : -magnitude);
    setStatusMessage('面板尺寸已调整');
  };

  useEffect(() => {
    const onPointerMove = (event: PointerEvent) => {
      const current = resizeRef.current;
      if (!current) return;
      const dx = event.clientX - current.x;
      const dy = event.clientY - current.y;
      if (current.kind === 'project') {
        setLayout((previous) => ({ ...previous, projectWidth: limit(current.layout.projectWidth + dx, 240, 460) }));
      } else if (current.kind === 'audio') {
        setLayout((previous) => ({ ...previous, audioWidth: limit(current.layout.audioWidth - dx, 180, 340) }));
      } else if (current.kind === 'viewers') {
        const max = Math.min(620, window.innerHeight - 280);
        setLayout((previous) => ({
          ...previous,
          viewersHeight: limit(current.layout.viewersHeight + dy, 300, max),
        }));
      } else {
        setLayout((previous) => ({
          ...previous,
          viewerSplit: limit(current.layout.viewerSplit + (dx / Math.max(1, current.viewerWidth)) * 100, 25, 75),
        }));
      }
    };
    const onPointerUp = () => {
      if (!resizeRef.current) return;
      resizeRef.current = null;
      document.body.classList.remove('is-panel-resizing');
      setStatusMessage('面板布局已调整');
    };
    window.addEventListener('pointermove', onPointerMove);
    window.addEventListener('pointerup', onPointerUp);
    window.addEventListener('pointercancel', onPointerUp);
    return () => {
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerup', onPointerUp);
      window.removeEventListener('pointercancel', onPointerUp);
      document.body.classList.remove('is-panel-resizing');
    };
  }, []);

  useEffect(() => {
    try {
      window.localStorage.setItem(LAYOUT_KEY, JSON.stringify(layout));
    } catch {
      // Local storage can be unavailable in a locked-down webview; layout still works for this session.
    }
    saveWorkspaceSnapshot(workspaceId, layout, workspaceWindows);
  }, [layout, workspaceId, workspaceWindows]);

  useEffect(() => {
    try {
      window.localStorage.setItem(TRANSITION_DEFAULTS_KEY, JSON.stringify(transitionDefaults));
    } catch {
      // 默认转场设置仅作为首选项，本地不可写时退回默认值，不回写工程。
    }
  }, [transitionDefaults]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.isComposing || event.keyCode === 229) return;
      if (event.target instanceof Element && event.target.closest('#editor-refinement, .refinement-panel')) return;
      const target = event.target as HTMLElement | null;
      const typing =
        target?.tagName === 'INPUT' ||
        target?.tagName === 'TEXTAREA' ||
        target?.tagName === 'SELECT' ||
        target?.isContentEditable === true;
      const modifier = event.ctrlKey || event.metaKey;
      const key = event.key.toLowerCase();

      if (event.altKey && event.shiftKey && !modifier && !typing) {
        const digit = event.code?.startsWith('Digit') ? event.code.slice(5) : event.key;
        if (digit === '0') {
          event.preventDefault();
          resetWorkspace();
          return;
        }
        const preset = WORKSPACES.find((workspace) => workspace.digit === digit);
        if (preset) {
          event.preventDefault();
          applyWorkspace(preset.id);
          return;
        }
      }

      if (event.key === 'Escape') {
        // 子级（Timeline 等）已经处理过 Esc 时不再叠加第二动作（关浮层 / 切回选择工具各只做一次）。
        if (event.defaultPrevented) return;
        if (refinementOpen) {
          event.preventDefault();
          closeRefinement();
          return;
        }
        if (nestedDepth > 0) {
          event.preventDefault();
          handleTabBack();
          return;
        }
        if (workspaceWindows.length > 0) {
          event.preventDefault();
          setWorkspaceWindows((current) => current.slice(0, -1));
          return;
        }
        if (trackPropertiesOpen) {
          event.preventDefault();
          setTrackPropertiesOpen(false);
          return;
        }
        if (sequenceSettingsOpen) {
          event.preventDefault();
          setSequenceSettingsOpen(false);
          return;
        }
        if (helpDialog) {
          event.preventDefault();
          setHelpDialog(null);
          return;
        }
        if (eventsOpen) {
          event.preventDefault();
          setEventsOpen(false);
          return;
        }
        if (aiOpen || inspectorOpen) {
          event.preventDefault();
          if (aiOpen) setAiOpen(false);
          if (inspectorOpen) setInspectorOpen(false);
        }
        return;
      }

      if (typing) return;

      // 时间线子组件可能已处理并阻止默认行为（包络微调、轨道移动等），避免二次响应。
      if (event.defaultPrevented) return;

      // 交互控件上的 Space 交给原生激活（按钮/链接/选项卡等），不劫持成播放/暂停。
      const interactiveTarget =
        target instanceof HTMLElement
          ? target.closest(
              'button, a[href], input, textarea, select, summary, [contenteditable="true"], [role="button"], [role="link"], [role="checkbox"], [role="switch"], [role="tab"], [role="menuitem"], [role="option"], [role="slider"]',
            )
          : null;
      if (event.code === 'Space' && interactiveTarget) return;

      // 三点编辑：Alt+I / Alt+O 设源 In/Out（Alt+Shift+I/O 清除），`,` 插入、`.` 覆盖。
      if (event.altKey && !modifier && !event.shiftKey && (key === 'i' || key === 'o')) {
        event.preventDefault();
        setSourceMark(key === 'i' ? 'in' : 'out', player.sourceTime);
        return;
      }
      if (event.altKey && event.shiftKey && !modifier && (key === 'i' || key === 'o')) {
        event.preventDefault();
        setSourceMark(key === 'i' ? 'in' : 'out', null);
        return;
      }
      if (!modifier && !event.altKey && !event.shiftKey && (event.key === ',' || event.key === '.')) {
        event.preventDefault();
        handleThreePointEdit(event.key === ',' ? 'insert' : 'overwrite');
        return;
      }

      // 播放与播放头导航（FilmCraft / Premiere 风格）
      if (event.code === 'Space' && !modifier && !event.altKey) {
        event.preventDefault();
        player.playPause();
        return;
      }
      if (!modifier && !event.altKey && (key === 'j' || key === 'l')) {
        event.preventDefault();
        const step = event.shiftKey ? 5 : 1;
        const target = Math.max(
          0,
          Math.min(totalDuration, player.sequenceTime + (key === 'j' ? -step : step)),
        );
        player.seekSequence(target);
        return;
      }
      if (!modifier && !event.altKey && (key === 'arrowleft' || key === 'arrowright')) {
        event.preventDefault();
        // FilmCraft：方向键逐帧，Shift+方向键 5 帧。
        const safeFps = fps > 0 ? fps : 30;
        const step = event.shiftKey ? 5 / safeFps : 1 / safeFps;
        const raw = sequenceTimeRef.current + (key === 'arrowleft' ? -step : step);
        // 吸附到帧栅格，避免浮点误差让时间码少一帧。
        const target = Math.round(Math.max(0, Math.min(totalDuration, raw)) * safeFps) / safeFps;
        player.seekSequence(target);
        return;
      }
      if (!modifier && !event.altKey && (key === 'home' || key === 'end')) {
        event.preventDefault();
        player.seekSequence(key === 'home' ? 0 : totalDuration);
        return;
      }

      // FilmCraft 监视器播放控制条键位：I/O 设工作区、Shift+I/O 跳转、Ctrl+Shift+I/O 清除。
      if (!modifier && event.shiftKey && !event.altKey && (key === 'i' || key === 'o')) {
        event.preventDefault();
        handleGoToWorkAreaEdge(key === 'i' ? 'in' : 'out');
        return;
      }
      if (!modifier && !event.shiftKey && !event.altKey && (key === 'i' || key === 'o')) {
        event.preventDefault();
        handleMarkWorkArea(key === 'i' ? 'in' : 'out');
        return;
      }
      if (modifier && event.shiftKey && !event.altKey && (key === 'i' || key === 'o')) {
        event.preventDefault();
        handleClearWorkArea(key === 'i' ? 'in' : 'out');
        return;
      }
      if (modifier && event.shiftKey && !event.altKey && key === 'x') {
        event.preventDefault();
        handleClearWorkArea();
        return;
      }
      if (!modifier && !event.altKey && !event.shiftKey && (event.key === ';' || event.key === "'")) {
        event.preventDefault();
        editWorkAreaRange(event.key === ';' ? 'lift' : 'extract');
        return;
      }
      if (!modifier && !event.altKey && event.shiftKey && key === 'e') {
        event.preventDefault();
        handleExportFrame();
        return;
      }
      if (!modifier && !event.altKey && (key === '=' || key === '+')) {
        event.preventDefault();
        timelineRef.current?.zoomIn();
        return;
      }
      if (!modifier && !event.altKey && key === '-') {
        event.preventDefault();
        timelineRef.current?.zoomOut();
        return;
      }
      if (!modifier && !event.altKey && event.shiftKey && key === 'z') {
        event.preventDefault();
        timelineRef.current?.zoomReset();
        return;
      }

      // 子序列视图现在是可编辑的（编辑写回 project.nestedSequences），
      // 父序列级的转场/标记操作由各自 handler 的 nestedView 守卫拒绝。

      if (!modifier && (key === 'delete' || key === 'backspace') && selectedTransitionId) {
        event.preventDefault();
        handleRemoveTransition(selectedTransitionId);
        return;
      }
      if (!modifier && !event.altKey && event.shiftKey && key === 'd') {
        event.preventDefault();
        handleApplyTransitionsToAllCuts();
        return;
      }
      if (modifier && event.shiftKey && key === 'd') {
        event.preventDefault();
        handleApplyTransitionAtPlayhead(true);
        return;
      }
      if (modifier && !event.shiftKey && !event.altKey && key === 'd') {
        event.preventDefault();
        handleApplyTransitionAtPlayhead(false);
        return;
      }

      if (modifier && key === 'z') {
        event.preventDefault();
        if (event.shiftKey) handleRedo();
        else handleUndo();
        return;
      }
      if (modifier && key === 'j') {
        event.preventDefault();
        setInspectorOpen(false);
        setAiOpen((open) => !open);
        return;
      }
      if (modifier && key === 's') {
        event.preventDefault();
        session.retrySave();
        return;
      }
      // 导出只需要工程：不再因为「没选中片段」而误禁用（§5 错误禁用 #2）。
      if (modifier && key === 'e' && project) {
        event.preventDefault();
        handleOpenExport();
        return;
      }
      if (!modifier && !event.altKey && key === 'm') {
        event.preventDefault();
        handleAddMarker();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [refinementOpen, aiOpen, inspectorOpen, helpDialog, sequenceSettingsOpen, trackPropertiesOpen, workspaceWindows, workspaceId, layout, project, selectedClip, session.retrySave, handleOpenExport, handleUndo, handleRedo, handleAddMarker, selectedTransitionId, selectedItemId, currentItem, handleRemoveTransition, handleApplyTransitionAtPlayhead, handleApplyTransitionsToAllCuts, player, totalDuration, fps, nestedDepth, handleTabBack, sourceMark, handleThreePointEdit]);

  // 标签持久化：只存标签清单（不含子序列内容）；刷新后按规格只恢复主序列标签。
  useEffect(() => {
    try {
      window.localStorage.setItem(
        TABS_STORAGE_KEY,
        JSON.stringify({
          version: 2,
          activeId: activeTabId,
          tabs: sequenceTabs.map(({ id, name, parentId }) => ({ id, name, parentId })),
        }),
      );
    } catch {
      // localStorage 不可用（隐私模式 / 配额）时只保留内存标签。
    }
  }, [sequenceTabs, activeTabId]);

  // Payload identity must match selection before a new project can own tab memory.
  const seekSequenceRef = useRef(player.seekSequence);
  seekSequenceRef.current = player.seekSequence;
  useEffect(() => {
    const { resetTabs, time } = prepareSequencePlayhead(tabPlayheadsRef.current, playheadProjectId, activeTabId, viewTotalDuration);
    if (resetTabs) {
      setSequenceTabs([PRIMARY_SEQUENCE_TAB]);
      setActiveTabId(PRIMARY_SEQUENCE_TAB.id);
      setExternalSequences({});
      setSelectedItemId(null);
      setSelectedTransitionId(null);
    }
    if (time === null) return;
    const frame = window.requestAnimationFrame(() => {
      seekSequenceRef.current(time);
      tabPlayheadsRef.current.activeTabId = activeTabId;
    });
    return () => window.cancelAnimationFrame(frame);
  }, [playheadProjectId, activeTabId, viewTotalDuration]);

  // 标签快捷键（捕获阶段，先于 Timeline 的 Alt+← 滑移）：
  // Ctrl+Alt+W 关闭当前、Ctrl+PageUp/PageDown 切换、Alt+← 返回上一层。
  const tabShortcutRef = useRef({
    tabs: sequenceTabs,
    depth: nestedDepth,
    close: handleCloseTab,
    activate: handleActivateTab,
    back: handleTabBack,
  });
  tabShortcutRef.current = {
    tabs: sequenceTabs,
    depth: nestedDepth,
    close: handleCloseTab,
    activate: handleActivateTab,
    back: handleTabBack,
  };
  useEffect(() => {
    const onCapture = (event: KeyboardEvent) => {
      if (event.isComposing || event.keyCode === 229) return;
      if (event.target instanceof Element && event.target.closest('#editor-refinement, .refinement-panel')) return;
      const target = event.target as HTMLElement | null;
      if (target && (target.isContentEditable || target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])'))) return;
      const state = tabShortcutRef.current;
      if (event.ctrlKey && event.altKey && (event.key === 'w' || event.key === 'W')) {
        event.preventDefault();
        event.stopPropagation();
        state.close(activeTabIdRef.current);
        return;
      }
      if (event.ctrlKey && !event.altKey && (event.key === 'PageDown' || event.key === 'PageUp')) {
        event.preventDefault();
        event.stopPropagation();
        const index = state.tabs.findIndex((tab) => tab.id === activeTabIdRef.current);
        if (index < 0) return;
        const delta = event.key === 'PageDown' ? 1 : -1;
        const next = state.tabs[(index + delta + state.tabs.length) % state.tabs.length];
        if (next) state.activate(next.id);
        return;
      }
      if (event.altKey && !event.ctrlKey && event.key === 'ArrowLeft' && state.depth > 0) {
        event.preventDefault();
        event.stopPropagation();
        state.back();
      }
    };
    window.addEventListener('keydown', onCapture, true);
    return () => window.removeEventListener('keydown', onCapture, true);
  }, []);

  const handleSaveSequenceSettings = (next: EditorProject) => {
    session.updateProject(next, '修改序列设置');
    setToast('序列设置已保存');
  };

  const handleSaveTrackProperties = (next: EditorProject) => {
    session.updateProject(next, '修改视频轨不透明度');
    setToast('视频轨属性已保存');
  };

  const handleSetVideoTrackOpacity = (trackIndex: number, value: number) => {
    if (!project) return;
    const current = project.trackOpacities;
    const next = [0, 1, 2].map((index) => {
      if (index === trackIndex) return value;
      const existing = current?.[index];
      return typeof existing === 'number' && Number.isFinite(existing) ? existing : 1;
    });
    const label = ['V1', 'V2', 'V3'][trackIndex] ?? `V${trackIndex + 1}`;
    session.updateProject({ ...project, trackOpacities: next }, `调整 ${label} 轨道不透明度`);
    setToast(`${label} 轨道不透明度已设为 ${Math.round(value * 100)}%`);
  };

  const projectTitle = selectedClip ? clipTitle(selectedClip) : '未选择素材';
  // 状态栏显示时间线真实缩放（原先硬编码 52，是假反馈）。
  const [timelineZoomPercent, setTimelineZoomPercent] = useState(100);

  const handleTimelineAction = (action: TimelineMenuAction) => {
    const handle = timelineRef.current;
    if (!handle) return;
    switch (action) {
      case 'copy':
        handle.copy();
        break;
      case 'cut':
        handle.cut();
        break;
      case 'paste':
        handle.paste('insert');
        break;
      case 'insert':
        handle.paste('insert');
        break;
      case 'overwrite':
        handle.paste('overwrite');
        break;
      case 'duplicate':
        handle.duplicate();
        break;
      case 'delete':
        handle.deleteSelection();
        break;
      case 'slip-left':
        handle.slip(-1);
        break;
      case 'slip-right':
        handle.slip(1);
        break;
      case 'roll':
        handle.roll(1);
        break;
    }
  };

  const dockedEffectControls = narrowLayout && !refinementOpen && workspaceWindows.includes('effect-controls');

  return (
    <EditorShellFrame
      className={[
        aiOpen ? 'ai-open' : '',
        inspectorOpen ? 'inspector-open' : '',
        refinementOpen ? 'refinement-open' : '',
        dockedEffectControls ? 'effect-docked' : '',
      ]
        .filter(Boolean)
        .join(' ')}
    >
      <EditorTopbar
        onExport={handleOpenExport}
        exportDisabled={!project}
        onSave={session.retrySave}
        saveDisabled={!project || session.saveState === 'clean' || session.saveState === 'saving'}
        onUndo={handleUndo}
        onRedo={handleRedo}
        canUndo={session.canUndo}
        canRedo={session.canRedo}
        onOpenAi={openAi}
        onToggleAi={() => {
          setRefinementOpen(false);
          setInspectorOpen(false);
          setAiOpen((open) => !open);
        }}
        onExtract={handleExtract}
        onShowHelp={() => {
          const active = document.activeElement;
          helpReturnFocusRef.current = active instanceof HTMLElement ? active : null;
          setHelpDialog('shortcuts');
        }}
        onShowAbout={() => {
          const active = document.activeElement;
          helpReturnFocusRef.current = active instanceof HTMLElement ? active : null;
          setHelpDialog('about');
        }}
        onSeekStart={handleSeekStart}
        onSeekEnd={handleSeekEnd}
        onManageSequences={() => setSequenceOpen(true)}
        onSequenceSettings={() => setSequenceSettingsOpen(true)}
        onTrackProperties={() => setTrackPropertiesOpen(true)}
        onShowProjectHistory={() => setProjectHistoryOpen(true)}
        hasTimelineSelection={Boolean(selectedItemId ?? currentItem?.id)}
        onTimelineAction={handleTimelineAction}
        selectedTransition={selectedTransition}
        transitionDefaults={transitionDefaults}
        onApplyTransitionAtPlayhead={() => handleApplyTransitionAtPlayhead(false)}
        onApplyAudioTransition={() => handleApplyTransitionAtPlayhead(true)}
        onApplyTransitionsToAllCuts={handleApplyTransitionsToAllCuts}
        onCreateNested={handleCreateNested}
        canCreateNested={canCreateNested}
        onRemoveTransition={() => {
          if (selectedTransition) handleRemoveTransition(selectedTransition.id);
        }}
        snapEnabled={snapEnabled}
        onToggleSnap={() => setSnapEnabled((enabled) => !enabled)}
        onAddMarker={handleAddMarker}
        onClearMarkers={handleClearMarkers}
        markerCount={markers.length}
        onToggleCaptions={handleToggleCaptions}
        captionsVisible={captionsVisible}
        onResetLayout={resetLayout}
        openWorkspaceWindows={workspaceWindows}
        onToggleWorkspaceWindow={toggleWorkspaceWindow}
        workspaceName={workspaceById(workspaceId).name}
        workspaceId={workspaceId}
        onApplyWorkspace={(id) => applyWorkspace(id as WorkspaceId)}
        onResetWorkspace={resetWorkspace}
        projectName={projectTitle}
        sequenceName={nestedView ? nestedSequence?.name ?? '子序列' : '序列 01'}
        saveState={session.saveState}
        lastSavedAt={session.lastSavedAt}
        onBack={() => leaveEditor('/studio')}
        eventsOpen={eventsOpen}
        onToggleEvents={toggleEvents}
        onToggleRefinement={() => refinementOpen ? closeRefinement() : openRefinement()}
        refinementOpen={refinementOpen}
        refinementDisabled={!project}
      />
      <SaveStatusBars
        conflict={session.saveConflict}
        onResolveConflict={session.resolveConflict}
        recoveredDraft={session.recoveredDraft}
        onApplyDraft={session.applyRecoveredDraft}
        onDiscardDraft={session.discardRecoveredDraft}
        hasUnsavedChanges={session.hasUnsavedChanges}
        saveStateShown={session.saveState !== 'clean'}
      />
      <div className="editor-body" style={layoutStyle}>
        <div
          className="panel-splitter panel-splitter--project"
          role="separator"
          aria-label="调整项目栏宽度"
          aria-orientation="vertical"
          aria-keyshortcuts="ArrowLeft ArrowRight Home End"
          aria-valuemin={200}
          aria-valuemax={380}
          aria-valuenow={Math.round(layout.projectWidth)}
          tabIndex={0}
          onPointerDown={beginResize('project')}
          onKeyDown={handleSplitterKey('project')}
          onDoubleClick={resetLayout}
          title="拖动调整项目栏宽度，双击还原；聚焦后 ←/→ 或 Home/End 调整"
        />
        <ProjectColumn
          clips={session.clips}
          selectedClipId={selectedClip?.id ?? null}
          transcriptSegments={payload?.transcript?.segments.length ?? 0}
          duration={payload?.duration ?? 0}
          width={payload?.width ?? 0}
          height={payload?.height ?? 0}
          fps={project?.fps ?? 30}
          onSelectClip={handleSelectClip}
          onOpenAi={openAi}
          onExport={handleOpenExport}
          onLocateSource={handleLocateSource}
          onAddToTimeline={handleAddClipToTimeline}
          onNotify={handleTimelineNotice}
          hasTimelineSelection={Boolean(nestedTargetItem)}
          selectedTimelineLabel={libraryTargetLabel}
          onApplyAudioEffect={handleApplyAudioEffect}
          onApplyLibraryFade={handleApplyLibraryFade}
          onApplyLibraryTransition={handleApplyLibraryTransition}
          onOpenEffectControls={() =>
            setWorkspaceWindows((current) =>
              current.includes('effect-controls') ? current : [...current, 'effect-controls'],
            )
          }
          onImportFiles={async (files) => {
            try {
              const result = await importStudioFiles(files);
              await session.refreshClips();
              handleTimelineNotice(
                `导入完成：新增 ${result.added}，已存在 ${result.existing}，匹配字幕 ${result.matchedSubtitles}`,
              );
            } catch (cause) {
              handleTimelineNotice(`导入失败：${cause instanceof Error ? cause.message : String(cause)}`);
            }
          }}
        />
        <main
          id="editor-main"
          tabIndex={-1}
          className="editor-center"
          data-surface="canvas"
        >
          <div
            className="viewers"
            data-canvas-theme="dark"
            style={{ gridTemplateColumns: `${layout.viewerSplit}% minmax(0, 1fr)` }}
          >
            <Monitor
              variant="source"
              title={projectTitle}
              sourceUrl={sourceUrl}
              sourceClipId={selectedClip?.id ?? null}
              videoRef={programRef}
              sourcePreviewRef={sourcePreviewRef}
              sourceTime={player.sourceTime}
              sequenceTime={player.sequenceTime}
              totalDuration={totalDuration}
              isPlaying={player.isPlaying}
              playbackError={player.error}
              width={payload?.width ?? 0}
              height={payload?.height ?? 0}
              fps={project?.fps ?? 30}
              caption={caption}
              sourceClips={session.clips}
              selectedClipId={selectedClip?.id ?? null}
              sourceMarkIn={sourceMark.in}
              sourceMarkOut={sourceMark.out}
              onSelectClip={handleSelectClip}
              onTogglePlay={player.playPause}
              onSeek={handleSeek}
            />
            {editPoint && !nestedView ? (
              <TrimMonitor
                editPoint={editPoint}
                items={viewItems}
                fps={viewFps}
                width={payload?.width ?? 0}
                height={payload?.height ?? 0}
                sourceDurations={viewSourceDurations}
                transitions={viewTransitions}
                getVideoUrl={getVideoUrl}
                onExit={handleExitTrim}
                onNudge={handleTrimNudge}
                onCommit={handleTrimCommit}
                onApplyTransition={handleApplyTransition}
                onToggleKind={handleTrimToggleKind}
                onNotify={handleTimelineNotice}
              />
            ) : (
            <Monitor
              variant="program"
              title={nestedView ? `${nestedSequence?.name ?? activeTab.name ?? '子序列'} · 子序列编辑` : projectTitle}
              sourceUrl={programSource?.url ?? null}
              sourceClipId={programSource?.clipId ?? null}
              videoRef={programRef}
              sourceTime={player.sourceTime}
              sequenceTime={player.sequenceTime}
              totalDuration={viewTotalDuration}
              isPlaying={player.isPlaying}
              playbackError={player.error}
              width={payload?.width ?? 0}
              height={payload?.height ?? 0}
              fps={viewFps}
              caption={captionsVisible ? caption : null}
              hidden={(viewProject?.hiddenTracks ?? []).includes(1) && previewOverlays.length === 0}
              gapBlank={programGapBlank}
              previewItem={(viewProject?.hiddenTracks ?? []).includes(1) ? null : programPreviewItem}
              previewLocalTime={programPreview?.local ?? 0}
              baseOpacity={programBaseFade}
              overlays={previewOverlays}
              audioLayers={previewAudioLayers}
              baseAudioMuted={baseAudioMuted}
              onTogglePlay={player.playPause}
              onSeek={handleSeek}
              onNotify={handleTimelineNotice}
              onUpdateMasks={nestedView ? undefined : handleUpdateItemMasks}
              onAddMarker={() => handleAddMarker(player.sequenceTime)}
              onMarkIn={() => handleMarkWorkArea('in')}
              onMarkOut={() => handleMarkWorkArea('out')}
              onGoToIn={() => handleGoToWorkAreaEdge('in')}
              onGoToOut={() => handleGoToWorkAreaEdge('out')}
              onStepFrames={handleStepFrames}
              onLift={() => editWorkAreaRange('lift')}
              onExtract={() => editWorkAreaRange('extract')}
              onExportFrame={handleExportFrame}
              onToggleProxies={handleToggleProxies}
              workAreaIn={workArea.in}
              workAreaOut={workArea.out}
              markers={markers}
            />
            )}
            {/* 引擎画面是剪辑页默认画面；`lc.engineStage = '0'` 是唯一逃生舱（见 D50）。 */}
            {localStorage.getItem('lc.engineStage') !== '0' ? (
              <EngineStage
                fps={viewFps}
                // 宿主有引擎画不出来的画面属性（调色/变换/蒙版…）时让位宿主渲染，见 D40/D53。
                suppressed={engineSync.needsHostRender}
                onReady={(adapter, signal) => {
                  engineAdapter.current = adapter;
                  engineSync.attach(adapter);
                  engineReveal.current = signal;
                  setEngineArmed(true);
                }}
                mountSelector={'[data-testid="program-monitor"] .screen'}
                hidden={Boolean(editPoint && !nestedView)}
              />
            ) : null}
            <div
              className="panel-splitter panel-splitter--viewer"
              style={{ left: `calc(${layout.viewerSplit}% - 3px)` }}
              role="separator"
              aria-label="调整源监视器与节目监视器比例"
              aria-orientation="vertical"
              aria-keyshortcuts="ArrowLeft ArrowRight Home End"
              aria-valuemin={25}
              aria-valuemax={75}
              aria-valuenow={Math.round(layout.viewerSplit)}
              tabIndex={0}
              onPointerDown={beginResize('viewerSplit')}
              onKeyDown={handleSplitterKey('viewerSplit')}
              onDoubleClick={resetLayout}
              title="拖动调整双监视器比例，双击还原；聚焦后 ←/→ 或 Home/End 调整"
            />
          </div>
          <div
            className="panel-splitter panel-splitter--viewers"
            role="separator"
            aria-label="调整监视器区域高度"
            aria-orientation="horizontal"
            aria-keyshortcuts="ArrowUp ArrowDown Home End"
            aria-valuemin={300}
            aria-valuemax={620}
            aria-valuenow={Math.round(layout.viewersHeight)}
            tabIndex={0}
            onPointerDown={beginResize('viewers')}
            onKeyDown={handleSplitterKey('viewers')}
            onDoubleClick={resetLayout}
            title="拖动调整监视器与时间线高度，双击还原；聚焦后 ↑/↓ 或 Home/End 调整"
          />
          <div className="lower-workspace" data-canvas-theme="dark">
            <ToolRail tool={editorTool} onChange={setEditorTool} />
            <Timeline
              ref={timelineRef}
              items={viewItems}
              totalDuration={viewTotalDuration}
              sourceDuration={nestedView ? 0 : payload?.duration ?? 0}
              fps={viewFps}
              sequenceTime={player.sequenceTime}
              selectedItemId={selectionCleared ? null : selectedItemId ?? currentItem?.id ?? null}
              selectedTransitionId={nestedView ? null : selectedTransitionId}
              transitions={viewTransitions}
              sourceDurations={viewSourceDurations}
              onSelectTransition={handleSelectTransition}
              onApplyTransition={handleApplyTransition}
              onRemoveTransition={handleRemoveTransition}
              onUpdateTransition={handleUpdateTransition}
              itemLabels={itemLabels}
              canUndo={session.canUndo}
              canRedo={session.canRedo}
              undoLabel={session.undoLabel}
              redoLabel={session.redoLabel}
              onSeek={handleSeek}
              onSelectItem={handleSelectItem}
              onUpdateItems={handleUpdateTimelineItems}
              onUndo={handleUndo}
              onRedo={handleRedo}
              onOpenTab={(tab) => tab === 'transcript' ? openRefinement() : openAi(tab)}
              onZoomChange={setTimelineZoomPercent}
              onWorkAreaChange={setWorkArea}
              markers={nestedView ? [] : markers}
              onAddMarker={
                nestedView
                  ? () => handleTimelineNotice('子序列里不能添加标记：标记属于主序列')
                  : handleAddMarker
              }
              activeTool={editorTool}
              onToolChange={setEditorTool}
              snapEnabled={snapEnabled}
              onToggleSnap={() => setSnapEnabled((enabled) => !enabled)}
              perception={{
                ...perception,
                enabled: perceptionEnabled,
                clipId: perceptionClipId,
                params: perceptionParams,
                onToggle: () => setPerceptionEnabled((enabled) => !enabled),
              }}
              baseAudioMuted={viewProject?.baseAudioMuted === true}
              onToggleBaseAudioMute={nestedView ? undefined : toggleBaseAudioMute}
              mutedTracks={viewProject?.mutedTracks ?? []}
              soloedTracks={viewProject?.soloedTracks ?? []}
              hiddenTracks={viewProject?.hiddenTracks ?? []}
              lockedTracks={viewProject?.lockedTracks ?? []}
              onToggleTrackMute={(track) => toggleTrackState('mutedTracks', track)}
              onToggleTrackSolo={(track) => toggleTrackState('soloedTracks', track)}
              onToggleTrackHidden={(track) => toggleTrackState('hiddenTracks', track)}
              onToggleTrackLocked={(track) => toggleTrackState('lockedTracks', track)}
              onClearMarkers={handleClearMarkers}
              trackOpacities={viewProject?.trackOpacities ?? undefined}
              onSetTrackOpacity={handleSetVideoTrackOpacity}
              onOpenTrackProperties={() => setTrackPropertiesOpen(true)}
              nestedNames={nestedNames}
              onEnterNested={handleEnterNested}
              onCreateNested={handleCreateNested}
              onNotify={handleTimelineNotice}
              editPoint={nestedView ? null : editPoint}
              onEditPoint={handleEditPoint}
              onDropAsset={handleDropAssetOnTimeline}
              onDropTransition={handleDropTransitionOnTimeline}
              tabsBar={
                <NestedTabsBar
                  tabs={sequenceTabs}
                  activeId={activeTab.id}
                  breadcrumbs={sequenceBreadcrumbs}
                  dirty={nestedView && session.saveState !== 'clean'}
                  missing={nestedMissing}
                  onActivate={handleActivateTab}
                  onClose={handleCloseTab}
                  onCloseOthers={handleCloseOtherTabs}
                  onRename={handleRenameTab}
                  onBack={handleTabBack}
                  onCreate={() => setSequenceOpen(true)}
                  onOpenLibrary={() => setSequenceOpen(true)}
                  onNotify={handleTimelineNotice}
                />
              }
              emptyHint={
                nestedMissing
                  ? `子序列内容未加载 · ${activeSequenceId}`
                  : nestedView
                    ? '这个子序列还没有片段 · 可以把素材库内容拖进来'
                    : undefined
              }
            />
            <div
              className="panel-splitter panel-splitter--audio"
              role="separator"
              aria-label="调整音频混合器宽度"
              aria-orientation="vertical"
              aria-keyshortcuts="ArrowLeft ArrowRight Home End"
              aria-valuemin={180}
              aria-valuemax={340}
              aria-valuenow={Math.round(layout.audioWidth)}
              tabIndex={0}
              onPointerDown={beginResize('audio')}
              onKeyDown={handleSplitterKey('audio')}
              onDoubleClick={resetLayout}
              title="拖动调整音频混合器宽度，双击还原；聚焦后 ←/→ 或 Home/End 调整"
            />
            <AudioMixer
              voiceVolume={project?.voiceVolume ?? 1}
              musicVolume={project?.musicVolume ?? 0.25}
              effectVolume={project?.effectVolume ?? 0.5}
              masterVolume={project?.masterVolume ?? 1}
              videoRef={programRef}
              isPlaying={player.isPlaying}
              onChange={(channel, volume) => {
                if (!project) return;
                const key =
                  channel === 'voice'
                    ? 'voiceVolume'
                    : channel === 'music'
                      ? 'musicVolume'
                      : channel === 'effect'
                        ? 'effectVolume'
                        : 'masterVolume';
                const label =
                  channel === 'voice' ? '对白' : channel === 'music' ? '音乐' : channel === 'effect' ? '音效' : '主输出';
                const updated = { ...project, [key]: volume };
                session.updateProject(updated, `调整${label}音量`, `${channel}-volume`);
              }}
            />
          </div>
        </main>
        {refinementOpen ? (
          <div id="editor-refinement" className="editor-refinement-dock" onKeyDown={(event) => {
            event.stopPropagation();
            if (event.key === 'Escape' && !event.nativeEvent.isComposing && event.keyCode !== 229) {
              event.preventDefault();
              closeRefinement();
            }
          }}>
            {refinementClipId && (project != null && hasCaptionOverride(project, refinementClipId)) ? (
              <button type="button" className="secondary-button" onClick={() => {
                if (project) session.updateProject(setClipCaptions(project, refinementClipId, null), '恢复原转写');
              }}>恢复原转写</button>
            ) : null}
            {refinementClipId && transcriptErrors[refinementClipId] ? <div role="alert">
              <p>{transcriptErrors[refinementClipId]}</p>
              <button type="button" className="secondary-button" data-testid="refinement-transcript-retry"
                data-source-clip-id={refinementClipId} disabled={refinementLoading}
                onClick={() => requestTranscript(refinementClipId)}>重试</button>
            </div>
              : refinementLoading ? <p role="status">正在读取当前素材转写…</p> : null}
            <RefinementPanel
              key={refinementClipId}
              clipLabel={session.clips.find((clip) => clip.id === refinementClipId)?.title || refinementClipId || '未选择素材'}
              segments={refinementSegments}
              activeTime={programPreviewItem?.clipId === refinementClipId ? programCaptionTime : refinementItem?.start ?? 0}
              disabled={!project || !refinementClipId || refinementLoading}
              onChange={(segments) => {
                if (project && refinementClipId) session.updateProject(
                  setClipCaptions(project, refinementClipId, segments), '字幕精修', 'captions:' + refinementClipId,
                );
              }}
              onSeek={seekRefinement}
              onClose={closeRefinement}
            />
          </div>
        ) : null}
        {dockedEffectControls ? (
          <div className="editor-effect-dock">
            <WorkspacePanelWindow
              key="effect-controls"
              id="effect-controls"
              stackIndex={0}
              suspended={false}
              project={project}
              selectedClip={selectedClip}
              selectedItem={inspectorItem}
              items={items}
              clips={session.clips}
              markers={markers}
              transitions={transitions}
              sourceDurations={sourceDurations}
              selectedTransitionId={selectedTransitionId}
              transitionDefaults={transitionDefaults}
              onChangeTransitionDefaults={setTransitionDefaults}
              sourceTime={player.sourceTime}
              sequenceTime={player.sequenceTime}
              sourceDuration={payload?.duration ?? 0}
              totalDuration={totalDuration}
              transcriptCount={payload?.transcript?.segments.length ?? 0}
              onClose={() => closeWorkspaceWindow('effect-controls')}
              onSeek={handleSeek}
              onSeekSource={handleLocateSource}
              onUpdateItem={handleUpdateInspectorItem}
              onSelectClip={handleSelectClip}
              onSelectTransition={handleSelectTransition}
              onApplyTransition={handleApplyTransition}
              onRemoveTransition={handleRemoveTransition}
              onUpdateTransition={handleUpdateTransition}
              onAddMarker={handleAddMarker}
              onClearMarkers={handleClearMarkers}
              onRemoveMarker={handleRemoveMarker}
              onOpenHistory={() => setProjectHistoryOpen(true)}
              onOpenStudio={() => leaveEditor('/studio')}
              onOpenLive={() => leaveEditor('/live')}
              onOpenAi={openAi}
              onSelectTool={setEditorTool}
              activeTool={editorTool}
              onToggleTrack={toggleTrackState}
              activeWorkspaceId={workspaceId}
              onApplyWorkspace={applyWorkspace}
              onResetWorkspace={resetWorkspace}
              docked
            />
          </div>
        ) : null}
        {!refinementOpen && aiPanel.mounted ? (
          <AiDrawer
            open={aiPanel.shown}
            tab={aiTab}
            transcript={payload?.transcript ?? null}
            transcriptError={selectedClip?.id ? transcriptErrors[selectedClip.id] || null : null}
            transcriptBusy={selectedClip?.id ? Boolean(transcriptLoading[selectedClip.id]) : false}
            events={events}
            adopted={adopted}
            activeEventId={activeEvent?.id ?? null}
            eventsBusy={session.eventsBusy}
            eventError={session.actionError}
            advice={session.advice}
            adviceBusy={session.adviceBusy}
            adviceError={session.adviceError}
            musicAssets={session.musicAssets}
            musicBusy={session.musicBusy}
            musicError={session.musicError}
            musicJob={session.musicJob}
            musicAssetId={project?.musicAssetId ?? null}
            project={project}
            duckMusic={Boolean(project?.duckMusic)}
            musicVolume={project?.musicVolume ?? 0}
            fps={project?.fps ?? 30}
            onTabChange={setAiTab}
            onClose={() => setAiOpen(false)}
            onLocateSource={handleLocateSource}
            onAdoptEvent={(event) => handleAdoptEvent(event.id)}
            onApply={handleApply}
            onExtract={handleExtract}
            onAutoPlan={handleAutoPlan}
            onAnalyze={session.analyzeAdvice}
            onRetryTranscript={() => { if (selectedClip?.id) requestTranscript(selectedClip.id); }}
            clipId={selectedClip?.id ?? null}
            onTranscriptReady={session.mergeTranscript}
            onGenerateMusic={session.generateMusicTrack}
            onRefreshMusic={session.loadMusicLibrary}
            onSelectMusic={session.selectMusic}
            onToggleDuckMusic={session.setDuckMusic}
            onMusicVolume={session.setMusicVolume}
          />
        ) : null}
        {!refinementOpen && !nestedView && inspectorPanel.mounted ? (
          <InspectorDrawer
            open={inspectorPanel.shown}
            item={inspectorItem}
            label={inspectorItem ? itemLabels[inspectorItem.id] ?? null : null}
            index={Math.max(0, inspectorIndex)}
            total={items.length}
            sequenceStart={inspectorSequenceStart}
            clipTitle={selectedClip ? clipTitle(selectedClip) : '本地素材'}
            sourceDuration={payload?.duration ?? 0}
            fps={project?.fps ?? 30}
            onUpdateItem={handleUpdateInspectorItem}
            onLocateSequence={handleSeek}
            onClose={() => setInspectorOpen(false)}
          />
        ) : null}
        <AiRail
          open={aiOpen}
          activeTab={aiTab}
          counts={{
            candidates: events.length,
            transcript: payload?.transcript?.segments.length ?? 0,
            chapters: events.length,
            music: session.musicAssets.length,
          }}
          inspectorOpen={inspectorOpen}
          onToggle={() => {
            setRefinementOpen(false);
            setInspectorOpen(false);
            setAiOpen((open) => !open);
          }}
          onSelectTab={openAi}
          onToggleInspector={toggleInspector}
        />
        <EventListPanel
          open={eventsOpen}
          events={events}
          project={project}
          fps={fps}
          adopted={adopted}
          excluded={excluded}
          activeEventId={activeEvent?.id ?? null}
          eventsBusy={session.eventsBusy}
          onClose={() => setEventsOpen(false)}
          onLocateEvent={handleLocateEvent}
          onToggleAdopt={handleAdoptEvent}
          onToggleExclude={handleToggleExcludeEvent}
          onApply={handleApply}
          onExtract={handleExtract}
        />
      </div>
      <EditorStatusbar
        loadState={session.loadState}
        projectName={projectTitle}
        sequenceLabel={nestedView ? nestedSequence?.name ?? '子序列' : '序列 01'}
        fps={project ? project.fps : null}
        width={payload?.width ?? null}
        height={payload?.height ?? null}
        duration={totalDuration > 0 ? totalDuration : null}
        frame={project ? Math.floor(player.sequenceTime * fps) : null}
        itemCount={items.length}
        zoom={timelineZoomPercent}
        message={statusMessage}
        sourcePreview={sourcePreview}
        saveState={session.saveState}
        saveError={session.saveError}
        saveConflict={session.saveConflict}
        recoveredDraft={session.recoveredDraft}
        hasUnsavedChanges={session.hasUnsavedChanges}
        // R51-EP2：带时刻的「已保存 · HH:MM:SS」只在顶栏出现一次，状态栏只留状态点 + 「已保存」。
        lastSavedAt={null}
      />
      {session.loadState === 'error' ? (
        <div className="editor-blocking" role="alert">
          <AlertTriangle size={18} />
          <strong>无法载入编辑器</strong>
          <p>{session.loadError ?? '未知错误'}</p>
          <button type="button" className="primary-button" onClick={() => window.location.reload()}>
            <RefreshCw size={11} /> 重试
          </button>
        </div>
      ) : null}
      {session.loadState === 'loading' ? (
        <div className="editor-loading" role="status">
          <LoaderCircle size={14} className="spin" />
          正在连接 LiveClipper 后端…
        </div>
      ) : null}
      {toast ? (
        <div className="toast" role="status">
          {toast}
        </div>
      ) : null}
      {workspaceWindows.map((workspaceWindowId, workspaceWindowIndex) => (
        dockedEffectControls && workspaceWindowId === 'effect-controls' ? null : (
            <WorkspacePanelWindow
              key={workspaceWindowId}
              id={workspaceWindowId}
              stackIndex={workspaceWindowIndex}
              suspended={refinementOpen}
            project={project}
            selectedClip={selectedClip}
            selectedItem={inspectorItem}
            items={items}
            clips={session.clips}
            markers={markers}
            transitions={transitions}
            sourceDurations={sourceDurations}
            selectedTransitionId={selectedTransitionId}
            transitionDefaults={transitionDefaults}
            onChangeTransitionDefaults={setTransitionDefaults}
            sourceTime={player.sourceTime}
            sequenceTime={player.sequenceTime}
            sourceDuration={payload?.duration ?? 0}
            totalDuration={totalDuration}
            transcriptCount={payload?.transcript?.segments.length ?? 0}
            onClose={() => closeWorkspaceWindow(workspaceWindowId)}
            onSeek={handleSeek}
            onSeekSource={handleLocateSource}
            onUpdateItem={handleUpdateInspectorItem}
            onSelectClip={handleSelectClip}
            onSelectTransition={handleSelectTransition}
            onApplyTransition={handleApplyTransition}
            onRemoveTransition={handleRemoveTransition}
            onUpdateTransition={handleUpdateTransition}
            onAddMarker={handleAddMarker}
            onClearMarkers={handleClearMarkers}
            onRemoveMarker={handleRemoveMarker}
            onOpenHistory={() => setProjectHistoryOpen(true)}
            onOpenStudio={() => leaveEditor('/studio')}
            onOpenLive={() => leaveEditor('/live')}
            onOpenAi={openAi}
            onSelectTool={setEditorTool}
            activeTool={editorTool}
            onToggleTrack={toggleTrackState}
            activeWorkspaceId={workspaceId}
            onApplyWorkspace={applyWorkspace}
            onResetWorkspace={resetWorkspace}
          />
        )
      ))}
      {helpDialog ? (
        <HelpDialog
          kind={helpDialog}
          returnFocus={helpReturnFocusRef.current}
          onClose={() => setHelpDialog(null)}
        />
      ) : null}
      <SequenceSettingsDialog
        open={sequenceSettingsOpen}
        project={project}
        hasTranscript={[...new Set([...items.map((item) => item.clipId), selectedClip?.id].filter((id): id is string => Boolean(id)))].some((id) => effectiveCaptions(id).length > 0)}
        onClose={() => setSequenceSettingsOpen(false)}
        onSave={handleSaveSequenceSettings}
      />
      <TrackPropertiesDialog
        open={trackPropertiesOpen}
        project={project}
        onClose={() => setTrackPropertiesOpen(false)}
        onSave={handleSaveTrackProperties}
      />
      <SequenceDialog
        open={sequenceOpen}
        clipId={selectedClip?.id ?? null}
        project={project}
        onClose={() => setSequenceOpen(false)}
        onLoad={handleLoadSequence}
      />
      {projectHistoryOpen && selectedClip ? (
        <ProjectHistoryDialog
          clipId={selectedClip.id}
          onRestore={handleRestoreProjectHistory}
          onClose={() => setProjectHistoryOpen(false)}
        />
      ) : null}
      <RenderDialog
        open={renderOpen}
        clipId={selectedClip?.id ?? null}
        clipTitle={projectTitle}
        project={project}
        duration={totalDuration}
        hasTranscript={[...new Set([...items.map((item) => item.clipId), selectedClip?.id].filter((id): id is string => Boolean(id)))].some((id) => effectiveCaptions(id).length > 0)}
        sourceHeight={payload?.height ?? 0}
        onClose={() => setRenderOpen(false)}
        onApplySettings={(next) => session.updateProject(next, '导出设置')}
      />
    </EditorShellFrame>
  );
}
