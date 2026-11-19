/**
 * LiveClipper Studio — 应用外壳（AppShell）。
 *
 * 结构与设计稿 design/ui/app-shell-prototype.html 对齐：
 *   IconRail（64px 图标栏） + Sidebar（268px 导航/直播频道）+ 主区（Topbar + 内容）。
 *
 * 迁移约定（见 design/ui/migration-inventory.md）：
 * - 外壳只负责导航、主题切换与布局；页面内容仍然各自渲染。
 * - `/` 工作台与 `/settings` 设置页已迁入外壳；/live、/studio、/editor、/music
 *   仍保留各自现有外壳，待后续任务逐页迁入。PAGES 里已登记它们的顶栏文案，
 *   迁入时直接 `<AppShell>` 包一层即可。
 * - 没有路由库：导航统一用 window.location.assign，保持刷新后的深链行为与旧页面一致。
 */
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  Badge,
  Button,
  Sidebar,
  StatusDot,
  TextField,
  Topbar,
  type SidebarSection,
  type StatusTone,
} from '../design';
import {
  Clapperboard,
  FolderOpen,
  LayoutDashboard,
  Music2,
  Radio,
  Search,
  Settings2,
} from 'lucide-react';
import { navigateApp, useAppLocation } from './appNavigation';
import { usePersistentChannelBinding } from './PersistentAppLayout';
import './AppShell.css';

/** 外壳导航目标；新增页面迁入外壳时在这里登记。 */
export type ShellPageId = 'home' | 'live' | 'studio' | 'editor' | 'music' | 'settings';

interface ShellPageMeta {
  label: string;
  href: string;
  icon: ReactNode;
  breadcrumbs: string[];
  eyebrow: string;
  title: string;
  subtitle: string;
  actionLabel: string;
  actionHref: string;
}

const PAGE_ORDER: ShellPageId[] = ['home', 'live', 'studio', 'editor', 'music', 'settings'];

const PAGES: Record<ShellPageId, ShellPageMeta> = {
  home: {
    label: '工作台',
    href: '/',
    icon: <LayoutDashboard size={19} aria-hidden="true" />,
    breadcrumbs: ['LiveClipper', '工作台'],
    eyebrow: '工作台 · 概览',
    title: '工作台',
    subtitle: '直播监控、自动切片、素材与剪辑的统一入口',
    actionLabel: '打开直播控制台',
    actionHref: '/live',
  },
  live: {
    label: '直播',
    href: '/live',
    icon: <Radio size={19} aria-hidden="true" />,
    breadcrumbs: ['LiveClipper', '直播'],
    eyebrow: '直播 · 监控',
    title: '直播控制台',
    subtitle: '房间监控、多路预览与自动切片策略',
    actionLabel: '打开独立监控屏',
    actionHref: '/live/monitor',
  },
  studio: {
    label: '素材',
    href: '/studio',
    icon: <FolderOpen size={19} aria-hidden="true" />,
    breadcrumbs: ['LiveClipper', '素材'],
    eyebrow: '素材 · 项目库',
    title: '素材库',
    subtitle: '切片、项目与导出记录',
    actionLabel: '回到工作台',
    actionHref: '/',
  },
  editor: {
    label: '剪辑',
    href: '/editor',
    icon: <Clapperboard size={19} aria-hidden="true" />,
    breadcrumbs: ['LiveClipper', '剪辑'],
    eyebrow: '剪辑 · 时间线',
    title: '剪辑工作台',
    subtitle: '时间线、字幕与渲染输出',
    actionLabel: '打开素材库',
    actionHref: '/studio',
  },
  music: {
    label: '音乐',
    href: '/music',
    icon: <Music2 size={19} aria-hidden="true" />,
    breadcrumbs: ['LiveClipper', '音乐'],
    eyebrow: '音乐 · 音频工坊',
    title: '音乐工作台',
    subtitle: '节拍、包络与音频素材',
    actionLabel: '打开素材库',
    actionHref: '/studio',
  },
  settings: {
    label: '设置',
    href: '/settings',
    icon: <Settings2 size={19} aria-hidden="true" />,
    breadcrumbs: ['LiveClipper', '设置'],
    eyebrow: '设置 · AI 与本地环境',
    title: '设置',
    subtitle: 'AI 连接、存储与转写模型',
    actionLabel: '回到工作台',
    actionHref: '/',
  },
};

/**
 * 编辑器页的 RailFrame 复用同一份导航表，避免第二套图标/文案漂移。
 * 只读导出：新增页面仍在外壳内部 via PAGES 登记。
 */
export { PAGE_ORDER, PAGES };

/** 把 pathname 映射到外壳页面；未登记前缀返回 null（继续走旧页面外壳）。 */
export function resolveShellPage(pathname: string): ShellPageId | null {
  if (!pathname || pathname === '/') return 'home';
  if (pathname.startsWith('/live')) return 'live';
  if (pathname.startsWith('/studio')) return 'studio';
  if (pathname.startsWith('/editor')) return 'editor';
  if (pathname.startsWith('/music')) return 'music';
  if (pathname.startsWith('/settings')) return 'settings';
  return null;
}

/**
 * 是否使用新外壳渲染：
 * - 工作台 `/` 使用外壳，但带编辑器深链 ?clip=/?tab= 时必须继续落到 EditorPage，
 *   否则从素材页打开切片会丢参数；
 * - `/settings` 设置页已迁入外壳（无深链约定）。
 */
export function isAppShellRoute(pathname: string, search: string): boolean {
  const page = resolveShellPage(pathname);
  if (page === 'settings') return true;
  if (page !== 'home') return false;
  const params = new URLSearchParams(search);
  return !params.has('clip') && !params.has('tab');
}

/** GET /api/status 里外壳需要的房间字段。 */
interface ShellRoom {
  roomId: number;
  title?: string;
  streamer?: string;
  isLive?: boolean;
  monitoring?: boolean;
}

const ROOM_POLL_MS = 30_000;

function navigate(href: string) {
  const current = `${window.location.pathname}${window.location.search}`;
  if (current === href) return;
  void navigateApp(href);
}

function roomTone(room: ShellRoom): StatusTone {
  if (room.monitoring && room.isLive) return 'green';
  if (room.isLive) return 'blue';
  if (room.monitoring) return 'amber';
  return 'idle';
}

function roomLabel(room: ShellRoom): string {
  const name = room.title?.trim() || room.streamer?.trim();
  return name ? `${name} · ${room.roomId}` : `${room.roomId} 号直播间`;
}

/**
 * 宽文字侧栏（268px）。与 AppShell 分离，使编辑页在滑入/滑出期间也能渲染同一份导航；
 * 位置与宽度由 NavSidebarMotion 的持久弹簧控制，本组件只负责内容与跳转。
 */
export function AppNavSidebar({ activePage, onChannelSelect, channelRoomId }: {
  activePage: ShellPageId;
  /**
   * 侧边栏直播频道的点击回调；提供后点击频道不再跳转独立监控屏，
   * 由当前页面自行切换选中房间（/live 页面使用）。
   */
  onChannelSelect?: (roomId: number) => void;
  /** 当前选中的频道房间号，仅用于在侧边栏频道名后标记「当前」。 */
  channelRoomId?: number | null;
}) {
  const [rooms, setRooms] = useState<ShellRoom[]>([]);

  // 侧边栏直播频道：只读 /api/status.rooms，不启动/停止任何录制。
  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        const response = await fetch('/api/status', { headers: { Accept: 'application/json' } });
        if (!response.ok) return;
        const payload = (await response.json()) as { rooms?: ShellRoom[] };
        if (!cancelled) setRooms(Array.isArray(payload.rooms) ? payload.rooms : []);
      } catch {
        // 后端未启动时保持上一次列表，侧边栏其余导航仍可用。
      }
    };
    void load();
    const timer = window.setInterval(() => void load(), ROOM_POLL_MS);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, []);

  const liveRoomCount = rooms.filter((room) => room.isLive).length;

  const sidebarSections = useMemo<SidebarSection[]>(() => {
    const navigation: SidebarSection = {
      id: 'workspace',
      label: '工作区',
      items: PAGE_ORDER.map((id) => ({
        id,
        label: PAGES[id].label,
        icon: PAGES[id].icon,
      })),
    };

    const channelItems = rooms.map((room) => ({
      id: `channel:${room.roomId}`,
      label:
        channelRoomId === room.roomId ? `${roomLabel(room)} · 当前` : roomLabel(room),
      icon: (
        <StatusDot
          tone={roomTone(room)}
          size="sm"
          pulse={Boolean(room.monitoring && room.isLive)}
          label={room.monitoring ? '监控中' : '未监控'}
        />
      ),
    }));

    const channels: SidebarSection = {
      id: 'channels',
      label: '直播频道',
      action:
        liveRoomCount > 0 ? (
          <Badge tone="green" dot>
            {liveRoomCount} 在播
          </Badge>
        ) : undefined,
      items:
        channelItems.length > 0
          ? channelItems
          : [{ id: 'no-channel', label: '暂无监控房间', disabled: true }],
    };

    return [navigation, channels];
  }, [channelRoomId, liveRoomCount, rooms]);

  const handleSelect = (id: string) => {
    if (id.startsWith('channel:')) {
      const roomId = Number(id.slice('channel:'.length));
      if (onChannelSelect && Number.isInteger(roomId) && roomId > 0) {
        onChannelSelect(roomId);
        return;
      }
      navigate(`/live/monitor?room=${encodeURIComponent(id.slice('channel:'.length))}`);
      return;
    }
    const page = PAGES[id as ShellPageId];
    if (id === 'editor' && window.location.pathname.startsWith('/studio')) {
      // 素材页已明确选中的工程（URL ?clip=）带入剪辑；无选中则进剪辑选择提示，不默认首个。
      const clip = new URLSearchParams(window.location.search).get('clip')?.trim();
      if (clip) { navigate(`${PAGES.editor.href}?clip=${encodeURIComponent(clip)}`); return; }
    }
    if (page) navigate(page.href);
  };

  return (
    <Sidebar
      sections={sidebarSections}
      activeId={activePage}
      onSelect={handleSelect}
      footer={
        <div className="lc-app-shell__user">
          <span className="lc-app-shell__avatar" aria-hidden="true">
            L
          </span>
          <span className="lc-app-shell__user-text">
            <strong>本地用户</strong>
            <span>本地工作区</span>
          </span>
          <span className="lc-app-shell__user-badge">LOCAL</span>
        </div>
      }
    />
  );
}

export interface AppShellProps {
  children: ReactNode;
  /** 覆盖顶栏标题信息；不传时使用 PAGES[activePage]。 */
  breadcrumbs?: string[];
  eyebrow?: string;
  title?: string;
  subtitle?: string;
  /**
   * 覆盖顶栏右侧操作区（默认是搜索占位 + 主按钮）。
   * 迁入外壳的页面可以放自己的刷新/跳转按钮，主题切换与搜索占位不再重复。
   */
  topbarActions?: ReactNode;
  /** 内容区附加类名；例如页面需要整屏布局时传 `lc-app-shell__content--flush`。 */
  contentClassName?: string;
  /**
   * 侧边栏直播频道的点击回调；提供后点击频道不再跳转独立监控屏，
   * 由当前页面自行切换选中房间（/live 页面使用）。
   */
  onChannelSelect?: (roomId: number) => void;
  /** 当前选中的频道房间号，仅用于在侧边栏频道名后标记「当前」。 */
  channelRoomId?: number | null;
}

export function AppShell({
  children,
  breadcrumbs,
  eyebrow,
  title,
  subtitle,
  topbarActions,
  contentClassName,
  onChannelSelect,
  channelRoomId,
}: AppShellProps) {
  const location = useAppLocation();
  const pathname = window.location.pathname;
  const activePage: ShellPageId = resolveShellPage(pathname) ?? 'home';
  const meta = PAGES[activePage];

  // 页面标题：按外壳页元数据统一，例如「工作台 · LiveClipper」「直播 · LiveClipper」。
  // 用页面 label 而不是顶栏 title（顶栏是「素材库/音乐工作台」这类产品内标题）；
  // 主题切换不会重跑本 effect，因此不会覆盖或抖动标题。
  // 部分历史页在自身 mount effect 里仍会设置旧 title（StudioPage/MusicStudioPage），
  // 子组件 effect 先于外壳 effect 执行，故同步写完后用宏任务再校正一次，覆盖同一次提交里的旧标题；
  // 这样直达、刷新、外壳内导航三条路径最终都以外壳页 label 为准。
  useEffect(() => {
    const title = `${meta.label} · LiveClipper`;
    document.title = title;
    const timer = window.setTimeout(() => {
      document.title = title;
    }, 0);
    return () => window.clearTimeout(timer);
  }, [meta.label]);

  // 导航 rail / 侧栏 / 页面滑入槽由 PersistentAppLayout 唯一持有；这里只把频道绑定注册给它。
  usePersistentChannelBinding(location.navId, onChannelSelect, channelRoomId);

  return (
    <>
        <div className="lc-app-shell__topbar">
          <TopbarContent
            breadcrumbs={breadcrumbs ?? meta.breadcrumbs}
            eyebrow={eyebrow ?? meta.eyebrow}
            title={title ?? meta.title}
            subtitle={subtitle ?? meta.subtitle}
            actionLabel={meta.actionLabel}
            onAction={() => navigate(meta.actionHref)}
            actions={topbarActions}
          />
        </div>
        <main
          id="lc-main-content"
          tabIndex={-1}
          className={`lc-app-shell__content${contentClassName ? ` ${contentClassName}` : ''}`}
        >
          {children}
        </main>
        <footer
          className="lc-app-shell__contentinfo"
          style={{
            position: 'absolute',
            width: 1,
            height: 1,
            padding: 0,
            margin: -1,
            overflow: 'hidden',
            clip: 'rect(0 0 0 0)',
            whiteSpace: 'nowrap',
            border: 0,
          }}
        >
          LiveClipper · 本地工作区
        </footer>
    </>
  );
}

interface TopbarContentProps {
  breadcrumbs: string[];
  eyebrow: string;
  title: string;
  subtitle: string;
  actionLabel: string;
  onAction: () => void;
  /** 页面自带操作区；不传时使用「搜索占位 + 主按钮」。 */
  actions?: ReactNode;
}

/**
 * 顶栏内容拆成独立组件，只为了让 AppShell 的 body 保持可读。
 * 使用设计层 Topbar + ThemeToggle，后续页面迁入外壳时无需重复这段结构。
 */
function TopbarContent({
  breadcrumbs,
  eyebrow,
  title,
  subtitle,
  actionLabel,
  onAction,
  actions,
}: TopbarContentProps) {
  return (
    <Topbar
      breadcrumbs={breadcrumbs}
      eyebrow={eyebrow}
      title={title}
      subtitle={subtitle}
      actions={
        actions ?? (
          <>
            {/*
              全局搜索尚未接线：用设计层 TextField 渲染，明确 disabled + aria-disabled + tooltip，
              不做「能点但无反应」的假交互；素材索引迁移后在此接入真实搜索。
            */}
            <div
              className="lc-app-shell__searchbox"
              title="即将支持：素材索引迁移后接入全局搜索"
            >
              <TextField
                className="lc-app-shell__search"
                type="search"
                placeholder="搜索项目、切片…（即将支持）"
                aria-label="搜索项目、切片（即将支持）"
                aria-disabled="true"
                leading={<Search size={14} aria-hidden="true" />}
                disabled
              />
            </div>
            <Button variant="solid" size="sm" leading={<Radio size={14} aria-hidden="true" />} onClick={onAction}>
              {actionLabel}
            </Button>
          </>
        )
      }
    />
  );
}
