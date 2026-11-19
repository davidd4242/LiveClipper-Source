import type { WorkspacePanelId } from './components/WorkspacePanelWindow';

export interface EditorLayout {
  projectWidth: number;
  audioWidth: number;
  viewersHeight: number;
  viewerSplit: number;
}

export type WorkspaceId =
  | 'editing'
  | 'assembly'
  | 'color'
  | 'effects'
  | 'audio'
  | 'captions'
  | 'learning'
  | 'review'
  | 'all-panels';

export interface EditorWorkspace {
  id: WorkspaceId;
  digit: string;
  name: string;
  english: string;
  layout: Partial<EditorLayout>;
  windows: WorkspacePanelId[];
  hint: string;
}

export interface SavedWorkspace {
  layout: EditorLayout;
  windows: WorkspacePanelId[];
}

export interface WorkspaceStore {
  current: WorkspaceId;
  saved: Partial<Record<WorkspaceId, SavedWorkspace>>;
}

export const LAYOUT_KEY = 'liveclipper.editor.layout.v2';
export const WORKSPACE_KEY = 'liveclipper.editor.workspaces.v2';

export const limit = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

export function defaultLayout(): EditorLayout {
  const narrow = window.innerWidth <= 1280;
  return {
    // §4 P0：项目面板默认宽统一到 design/ui/tokens.css 的 --lc-project-width（260px），
    // 不再出现 340/280 与 tokens 口径不一致。
    projectWidth: 260,
    audioWidth: narrow ? 190 : 220,
    viewersHeight: limit(Math.round(window.innerHeight * 0.6), 360, 580),
    viewerSplit: 44,
  };
}

export function clampLayout(layout: EditorLayout): EditorLayout {
  return {
    projectWidth: limit(layout.projectWidth, 240, 460),
    audioWidth: limit(layout.audioWidth, 180, 340),
    viewersHeight: limit(layout.viewersHeight, 300, Math.min(620, window.innerHeight - 280)),
    viewerSplit: limit(layout.viewerSplit, 25, 75),
  };
}

export function readLegacyLayout(): EditorLayout {
  const fallback = defaultLayout();
  try {
    const raw = window.localStorage.getItem(LAYOUT_KEY);
    if (!raw) return fallback;
    const saved = JSON.parse(raw) as Partial<EditorLayout>;
    const values = [saved.projectWidth, saved.audioWidth, saved.viewersHeight, saved.viewerSplit];
    if (values.some((value) => typeof value !== 'number' || !Number.isFinite(value))) return fallback;
    return clampLayout({
      projectWidth: saved.projectWidth!,
      audioWidth: saved.audioWidth!,
      viewersHeight: saved.viewersHeight!,
      viewerSplit: saved.viewerSplit!,
    });
  } catch {
    return fallback;
  }
}

const PANEL_IDS: WorkspacePanelId[] = [
  'workspaces',
  'new-project',
  'project',
  'media',
  'library',
  'info',
  'effects',
  'transitions',
  'markers',
  'history',
  'source',
  'effect-controls',
  'audio-clip-mixer',
  'metadata',
  'program',
  'timeline',
  'tools',
  'audio-level',
  'audio-track-mixer',
  'lumetri-color',
  'lumetri-scopes',
  'graphics',
  'essential-sound',
  'tts',
  'properties',
  'text',
  'events',
  'progress',
  'reference',
  'timecode',
  'project-notes',
];

export const WORKSPACES: EditorWorkspace[] = [
  {
    id: 'editing',
    digit: '1',
    name: '剪辑',
    english: 'Editing',
    layout: { projectWidth: 260, audioWidth: 220, viewerSplit: 50 },
    windows: ['effect-controls'],
    hint: '默认工作区：项目/源/节目 + 效果控件 + 时间线',
  },
  {
    id: 'assembly',
    digit: '2',
    name: '装配',
    english: 'Assembly',
    layout: { projectWidth: 380, audioWidth: 200, viewerSplit: 45 },
    windows: ['media', 'info'],
    hint: '素材优先：项目面板加宽，源与节目并排',
  },
  {
    id: 'color',
    digit: '3',
    name: '调色',
    english: 'Color',
    layout: { projectWidth: 240, audioWidth: 210, viewerSplit: 58 },
    windows: ['lumetri-color', 'lumetri-scopes'],
    hint: '监看优先：项目收窄，打开调色与示波器面板',
  },
  {
    id: 'effects',
    digit: '4',
    name: '效果',
    english: 'Effects',
    layout: { projectWidth: 260, audioWidth: 200, viewerSplit: 50 },
    windows: ['effects', 'effect-controls'],
    hint: '效果与效果控件并排，适合调参',
  },
  {
    id: 'audio',
    digit: '5',
    name: '音频',
    english: 'Audio',
    layout: { projectWidth: 280, audioWidth: 320, viewerSplit: 48 },
    windows: ['audio-track-mixer', 'essential-sound'],
    hint: '音频栏加宽，打开轨道混音器与基本声音',
  },
  {
    id: 'captions',
    digit: '6',
    name: '字幕与图形',
    english: 'Captions and Graphics',
    layout: { projectWidth: 300, audioWidth: 200, viewerSplit: 52 },
    windows: ['text', 'graphics'],
    hint: '字幕与图形工具展开，适合做标题和文字',
  },
  {
    id: 'learning',
    digit: '7',
    name: '学习',
    english: 'Learning',
    layout: { projectWidth: 280, audioWidth: 200, viewerSplit: 48 },
    windows: ['reference', 'progress'],
    hint: '参考与进度文档常驻，适合边看边做',
  },
  {
    id: 'review',
    digit: '8',
    name: '审阅',
    english: 'Review',
    layout: { projectWidth: 320, audioWidth: 200, viewerSplit: 50 },
    windows: ['markers', 'history'],
    hint: '标记与历史常驻，适合逐条检查和回退',
  },
  {
    id: 'all-panels',
    digit: '9',
    name: '全部面板',
    english: 'All Panels',
    layout: { projectWidth: 360, audioWidth: 240, viewerSplit: 44 },
    windows: ['project', 'media', 'metadata', 'markers', 'history', 'audio-level', 'progress', 'timecode'],
    hint: '一次铺开主要浮动面板（最多 8 个），适合核对信息',
  },
];

const WORKSPACE_IDS = WORKSPACES.map((workspace) => workspace.id);

export function workspaceById(id: WorkspaceId | string): EditorWorkspace {
  return WORKSPACES.find((workspace) => workspace.id === id) ?? WORKSPACES[0];
}

export function resolveWorkspace(id: WorkspaceId, store: WorkspaceStore): SavedWorkspace & { preset: EditorWorkspace } {
  const preset = workspaceById(id);
  const saved = store.saved[id];
  const base = defaultLayout();
  const layout = saved ? clampLayout(saved.layout) : clampLayout({ ...base, ...preset.layout });
  const windows = saved
    ? saved.windows.filter((panel) => PANEL_IDS.includes(panel))
    : preset.windows.slice();
  return { layout, windows, preset };
}

export function readWorkspaceStore(): WorkspaceStore {
  const fallback: WorkspaceStore = {
    current: 'editing',
    saved: {},
  };
  try {
    const raw = window.localStorage.getItem(WORKSPACE_KEY);
    if (!raw) {
      const legacy = readLegacyLayout();
      // 旧版只保存过布局宽度；迁移时保留宽度，但窗口沿用「剪辑」预设，避免空窗口列表覆盖默认布局。
      fallback.saved.editing = { layout: legacy, windows: workspaceById('editing').windows.slice() };
      return fallback;
    }
    const parsed = JSON.parse(raw) as Partial<WorkspaceStore>;
    const current = WORKSPACE_IDS.includes(parsed.current as WorkspaceId) ? (parsed.current as WorkspaceId) : 'editing';
    const saved: WorkspaceStore['saved'] = {};
    if (parsed.saved && typeof parsed.saved === 'object') {
      for (const [key, value] of Object.entries(parsed.saved)) {
        if (!WORKSPACE_IDS.includes(key as WorkspaceId)) continue;
        if (!value || typeof value !== 'object') continue;
        const entry = value as Partial<SavedWorkspace>;
        if (!entry.layout) continue;
        const numbers = [entry.layout.projectWidth, entry.layout.audioWidth, entry.layout.viewersHeight, entry.layout.viewerSplit];
        if (numbers.some((item) => typeof item !== 'number' || !Number.isFinite(item))) continue;
        saved[key as WorkspaceId] = {
          layout: clampLayout(entry.layout as EditorLayout),
          windows: Array.isArray(entry.windows)
            ? entry.windows.filter((panel): panel is WorkspacePanelId => PANEL_IDS.includes(panel as WorkspacePanelId))
            : [],
        };
      }
    }
    return { current, saved };
  } catch {
    return fallback;
  }
}

export function writeWorkspaceStore(store: WorkspaceStore): void {
  try {
    window.localStorage.setItem(WORKSPACE_KEY, JSON.stringify(store));
  } catch {
    // 存储不可用时静默降级，工作区仍可在本次会话内切换。
  }
}

export function saveWorkspaceSnapshot(
  id: WorkspaceId,
  layout: EditorLayout,
  windows: WorkspacePanelId[],
): WorkspaceStore {
  const store = readWorkspaceStore();
  store.saved[id] = { layout: clampLayout(layout), windows: windows.filter((panel) => PANEL_IDS.includes(panel)) };
  writeWorkspaceStore(store);
  return store;
}
