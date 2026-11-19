/**
 * 编辑器菜单数据源（从 MenuBar.tsx 抽出，供 EditorTopbar 的溢出菜单复用）。
 *
 * - 只描述菜单结构与动作，不渲染任何东西；MenuBar（旧 34px 菜单条）与
 *   EditorTopbar（48px 新顶栏）共用同一份定义，保证菜单功能不丢。
 * - 菜单项的启用条件、快捷键提示、勾选态全部保留原实现。
 */
import type { EditorTransition } from '../../../api/types';
import { transitionKindLabel, type TransitionDefaults } from '../project';
import type { AiTab } from '../components/AiDrawer';
import { WORKSPACES } from '../workspaces';
import {
  WORKSPACE_PANEL_IDS,
  WORKSPACE_PANEL_LABELS,
  type WorkspacePanelId,
} from '../components/WorkspacePanelWindow';

export type TimelineMenuAction =
  | 'copy'
  | 'cut'
  | 'paste'
  | 'insert'
  | 'overwrite'
  | 'duplicate'
  | 'delete'
  | 'slip-left'
  | 'slip-right'
  | 'roll';

/** MenuBar 的完整 props：EditorTopbar 继承它，菜单数据与旧菜单条保持一致。 */
export interface MenuBarProps {
  onExport: () => void;
  exportDisabled?: boolean;
  onSave: () => void;
  saveDisabled?: boolean;
  onUndo: () => void;
  onRedo: () => void;
  canUndo: boolean;
  canRedo: boolean;
  onOpenAi: (tab: AiTab) => void;
  onToggleAi: () => void;
  onExtract: () => void;
  onShowHelp: () => void;
  onShowAbout: () => void;
  onSeekStart: () => void;
  onSeekEnd: () => void;
  onManageSequences: () => void;
  onSequenceSettings: () => void;
  onTrackProperties: () => void;
  onShowProjectHistory: () => void;
  hasTimelineSelection: boolean;
  onTimelineAction: (action: TimelineMenuAction) => void;
  selectedTransition: EditorTransition | null;
  transitionDefaults: TransitionDefaults;
  onApplyTransitionAtPlayhead: () => void;
  onApplyAudioTransition: () => void;
  onApplyTransitionsToAllCuts: () => void;
  onCreateNested: () => void;
  canCreateNested: boolean;
  onRemoveTransition: () => void;
  snapEnabled: boolean;
  onToggleSnap: () => void;
  onAddMarker: () => void;
  onClearMarkers: () => void;
  markerCount: number;
  onToggleCaptions: () => void;
  captionsVisible: boolean;
  onResetLayout: () => void;
  openWorkspaceWindows: WorkspacePanelId[];
  onToggleWorkspaceWindow: (id: WorkspacePanelId) => void;
  workspaceName: string;
  workspaceId: string;
  onApplyWorkspace: (id: string) => void;
  onResetWorkspace: () => void;
}

export interface MenuEntry {
  label: string;
  shortcut?: string;
  disabled?: boolean;
  checked?: boolean;
  /** 悬停说明：禁用项写清前置条件，普通项补充行为边界。 */
  title?: string;
  onSelect: () => void;
}

export interface MenuGroup {
  label: string;
  entries: MenuEntry[];
}

/** 只做数据展示/占位的窗口：菜单入口标注预览版，窗口内也会显示说明。 */
const PREVIEW_PANEL_IDS: WorkspacePanelId[] = ['lumetri-scopes', 'progress', 'reference'];

function toggleFullscreen(): void {
  if (document.fullscreenElement) void document.exitFullscreen();
  else void document.documentElement.requestFullscreen().catch(() => undefined);
}

/** 依据当前编辑器状态生成菜单树（纯数据，无副作用）。 */
export function buildEditorMenus(props: MenuBarProps): MenuGroup[] {
  const {
    onExport,
    exportDisabled = false,
    onSave,
    saveDisabled = false,
    onUndo,
    onRedo,
    canUndo,
    canRedo,
    onOpenAi,
    onToggleAi,
    onExtract,
    onShowHelp,
    onShowAbout,
    onSeekStart,
    onSeekEnd,
    onManageSequences,
    onSequenceSettings,
    onTrackProperties,
    onShowProjectHistory,
    hasTimelineSelection,
    onTimelineAction,
    selectedTransition,
    transitionDefaults,
    onApplyTransitionAtPlayhead,
    onApplyAudioTransition,
    onApplyTransitionsToAllCuts,
    onCreateNested,
    canCreateNested,
    onRemoveTransition,
    snapEnabled,
    onToggleSnap,
    onAddMarker,
    onClearMarkers,
    markerCount,
    onToggleCaptions,
    captionsVisible,
    onResetLayout,
    openWorkspaceWindows,
    onToggleWorkspaceWindow,
    workspaceId,
    onApplyWorkspace,
    onResetWorkspace,
  } = props;
  return [
    {
      label: '文件',
      entries: [
        {
          label: '保存工程',
          shortcut: 'Ctrl+S',
          disabled: saveDisabled,
          title: saveDisabled ? '工程没有未保存的改动，或正在保存' : '把当前工程保存到后端',
          onSelect: onSave,
        },
        { label: '工程历史…', onSelect: onShowProjectHistory },
        {
          label: '导出影片',
          shortcut: 'Ctrl+E',
          disabled: exportDisabled,
          title: exportDisabled ? '载入工程后才能导出' : '导出当前时间线为 MP4',
          onSelect: onExport,
        },
        { label: '重新载入页面', onSelect: () => window.location.reload() },
      ],
    },
    {
      label: '编辑',
      entries: [
        { label: '撤销', shortcut: 'Ctrl+Z', disabled: !canUndo, onSelect: onUndo },
        { label: '重做', shortcut: 'Ctrl+Shift+Z', disabled: !canRedo, onSelect: onRedo },
        { label: '剪切片段', shortcut: 'Ctrl+X', disabled: !hasTimelineSelection, onSelect: () => onTimelineAction('cut') },
        { label: '复制片段', shortcut: 'Ctrl+C', disabled: !hasTimelineSelection, onSelect: () => onTimelineAction('copy') },
        {
          label: '粘贴片段',
          shortcut: 'Ctrl+V',
          title: '在播放头插入剪贴板片段，目标轨由轨道头的 V1–V3/A1–A3 按钮决定',
          onSelect: () => onTimelineAction('paste'),
        },
        {
          label: '插入片段',
          shortcut: ',',
          title: '在播放头插入并后推目标轨后续片段；落在片段内部会先拆分',
          onSelect: () => onTimelineAction('insert'),
        },
        {
          label: '覆盖片段',
          shortcut: '.',
          title: '在播放头覆盖目标轨对应区间，不改变其他轨道',
          onSelect: () => onTimelineAction('overwrite'),
        },
        { label: '复制到下一段', shortcut: 'Ctrl+Alt+D', disabled: !hasTimelineSelection, onSelect: () => onTimelineAction('duplicate') },
        { label: '波纹删除片段', shortcut: 'Delete', disabled: !hasTimelineSelection, onSelect: () => onTimelineAction('delete') },
        { label: '向左滑移一帧', shortcut: 'Alt+←', disabled: !hasTimelineSelection, onSelect: () => onTimelineAction('slip-left') },
        { label: '向右滑移一帧', shortcut: 'Alt+→', disabled: !hasTimelineSelection, onSelect: () => onTimelineAction('slip-right') },
        { label: '滚动编辑切点', shortcut: 'Shift+C', disabled: !hasTimelineSelection, onSelect: () => onTimelineAction('roll') },
        { label: '重试保存', disabled: saveDisabled, onSelect: onSave },
      ],
    },
    {
      label: '剪辑',
      entries: [
        { label: '打开 AI 候选', onSelect: () => onOpenAi('candidates') },
        { label: '打开字幕', onSelect: () => onOpenAi('transcript') },
        { label: '从字幕提取候选', onSelect: onExtract },
      ],
    },
    {
      label: '序列',
      entries: [
        { label: '序列设置…', onSelect: onSequenceSettings },
        { label: '时间线吸附', shortcut: 'S', checked: snapEnabled, onSelect: onToggleSnap },
        {
          label: `在剪点添加默认转场：${transitionKindLabel(transitionDefaults.kind)} ${transitionDefaults.duration} 秒`,
          shortcut: 'Ctrl+D',
          onSelect: onApplyTransitionAtPlayhead,
        },
        {
          label: `在剪点添加默认音频转场：${transitionKindLabel(transitionDefaults.kind)} ${transitionDefaults.audioDuration} 秒`,
          shortcut: 'Ctrl+Shift+D',
          onSelect: onApplyAudioTransition,
        },
        {
          label: `为所有兼容剪点添加默认转场（${transitionDefaults.duration} 秒）`,
          shortcut: 'Shift+D',
          disabled: !hasTimelineSelection,
          onSelect: onApplyTransitionsToAllCuts,
        },
        {
          label: '移除选中转场',
          shortcut: 'Delete',
          disabled: !selectedTransition,
          onSelect: onRemoveTransition,
        },
        {
          label: '将选中片段创建为嵌套序列',
          disabled: !canCreateNested,
          onSelect: onCreateNested,
        },
        { label: '视频轨不透明度…', onSelect: onTrackProperties },
        { label: '管理序列…', onSelect: onManageSequences },
        { label: '回到序列开头', onSelect: onSeekStart },
        { label: '跳到序列末尾', onSelect: onSeekEnd },
      ],
    },
    {
      label: '标记',
      entries: [
        { label: '在播放头添加标记', shortcut: 'M', onSelect: onAddMarker },
        { label: '清除所有标记', disabled: markerCount === 0, onSelect: onClearMarkers },
        { label: '打开章节', onSelect: () => onOpenAi('chapters') },
      ],
    },
    {
      label: '图形',
      entries: [
        { label: captionsVisible ? '隐藏字幕' : '显示字幕', onSelect: onToggleCaptions },
        { label: '打开字幕与转写', onSelect: () => onOpenAi('transcript') },
      ],
    },
    {
      label: '视图',
      entries: [
        { label: '显示 / 隐藏 AI 助手', shortcut: 'Ctrl+J', onSelect: onToggleAi },
        { label: '还原面板布局', onSelect: onResetLayout },
        { label: '切换全屏', onSelect: toggleFullscreen },
      ],
    },
    {
      label: '窗口',
      entries: [
        ...WORKSPACE_PANEL_IDS.map((id) => ({
          label: PREVIEW_PANEL_IDS.includes(id)
            ? `${WORKSPACE_PANEL_LABELS[id]}（预览版）`
            : WORKSPACE_PANEL_LABELS[id],
          title: PREVIEW_PANEL_IDS.includes(id) ? '该窗口仅展示已接入的数据，缺失部分不会伪造' : undefined,
          checked: openWorkspaceWindows.includes(id),
          onSelect: () => onToggleWorkspaceWindow(id),
        })),
        { label: '打开项目库', onSelect: () => window.location.assign('/studio') },
        { label: '打开直播控制台', onSelect: () => window.location.assign('/live') },
      ],
    },
    {
      label: '工作区',
      entries: [
        ...WORKSPACES.map((workspace) => ({
          label: `${workspace.name}（${workspace.english}）`,
          shortcut: `Alt+Shift+${workspace.digit}`,
          checked: workspaceId === workspace.id,
          onSelect: () => onApplyWorkspace(workspace.id),
        })),
        { label: '重置当前工作区布局', shortcut: 'Alt+Shift+0', onSelect: onResetWorkspace },
      ],
    },
    {
      label: '帮助',
      entries: [
        { label: '快捷键', onSelect: onShowHelp },
        { label: '关于 LiveClipper', onSelect: onShowAbout },
      ],
    },
  ];
}
