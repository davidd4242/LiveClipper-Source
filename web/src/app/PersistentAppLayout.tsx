/**
 * 持久应用外壳：IconRail + 宽侧栏槽 + 右侧页面滑入槽。
 *
 * 目的：lazy 页面首次切换时，旧页面的 AppShell/RailFrame 随 ErrorBoundary key(navId) 一起卸载，
 * Suspense fallback 出现时整个导航闪空。本组件放在 App 的 ErrorBoundary key(navId) / Suspense 之外，
 * 唯一渲染 IconRail、NavSidebarSlot(AppNavSidebar) 与 PageSlideMotion；页面只渲染 children（自己的 Topbar/main）。
 *
 * 依赖（均在 App 外层，本文件不创建）：NavSidebarMotionProvider、PageSlideMotionProvider。
 *
 * 模式只切 class，不重挂导航 DOM：
 * - regular：IconRail + 侧栏槽 + 右侧槽（类名 lc-app-shell__main，页面自己放 topbar/main/footer）；
 * - editor：同上，右侧槽带 fc-editor-motion，保留 PageSlideMotion.css 的 shrink 裁切修正；
 *   侧栏槽目标 0 由 NavSidebarMotionProvider 的 open 决定（速度连续），这里不干预；
 * - monitor：无导航，导航 DOM 仍在但 display:none。
 *
 * 离页守卫：全应用唯一 registerAppLeaveGuard 由页面渲染 <EditorNavigationRegistration beforeNavigate=… />，
 * 语义与旧 FilmCraftAppNav 一致（pending / busy / error / mounted），busy/error 显示在持久 rail 的状态出口。
 * 直播频道：页面调用 usePersistentChannelBinding 注册 onChannelSelect / roomId。
 */
import {
  createContext,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type KeyboardEvent,
  type ReactNode,
} from 'react';
import { MonitorPlay } from 'lucide-react';
import { IconButton, IconRail, type IconRailItem } from '../design';
import { AppNavSidebar, PAGE_ORDER, PAGES, isAppShellRoute, resolveShellPage, type ShellPageId } from './AppShell';
import { navigateApp, registerAppLeaveGuard, useAppLocation } from './appNavigation';
import { NavSidebarSlot } from './NavSidebarMotion';
import { PageSlideMotion } from './PageSlideMotion';
import './PersistentAppLayout.css';

export type PersistentLayoutMode = 'regular' | 'editor' | 'monitor';

/** pathname → 布局模式；App 可直接用它，也可自己传 mode。 */
export function resolvePersistentLayoutMode(pathname: string): PersistentLayoutMode {
  if (pathname === '/live/monitor' || pathname.startsWith('/live/monitor/')) return 'monitor';
  if (pathname.startsWith('/editor')) return 'editor';
  return 'regular';
}

/* ───────────────────────── store ───────────────────────── */

interface LayoutSnapshot {
  /** 当前拥有侧栏频道绑定的页面 navId；null = 无页面绑定（点击频道走独立监控屏）。 */
  channelOwner: number | null;
  channelRoomId: number | null;
  /** 编辑器离页保存：进行中 / 错误提示。 */
  busy: boolean;
  error: string;
  /** 仅标记「字幕草稿取消导航」提示，供草稿结束时精准清除；其它错误为 ''。 */
  errorKind: '' | 'transcript-draft';
}

class LayoutStore {
  private snapshot: LayoutSnapshot = { channelOwner: null, channelRoomId: null, busy: false, error: '', errorKind: '' };
  private listeners = new Set<() => void>();
  private handler: ((roomId: number) => void) | null = null;

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };
  getSnapshot = () => this.snapshot;

  private set(patch: Partial<LayoutSnapshot>) {
    const next = { ...this.snapshot, ...patch };
    const changed = (Object.keys(next) as (keyof LayoutSnapshot)[]).some((key) => next[key] !== this.snapshot[key]);
    if (!changed) return;
    this.snapshot = next;
    this.listeners.forEach((listener) => listener());
  }

  claimChannel(owner: number) {
    this.set({ channelOwner: owner, channelRoomId: null });
  }
  releaseChannel(owner: number) {
    if (this.snapshot.channelOwner !== owner) return; // 新页面已接管，旧页面清理不得误清。
    this.handler = null;
    this.set({ channelOwner: null, channelRoomId: null });
  }
  setChannelHandler(owner: number, handler: ((roomId: number) => void) | undefined) {
    if (this.snapshot.channelOwner !== owner) return;
    this.handler = handler ?? null;
  }
  setChannelRoom(owner: number, roomId: number | null) {
    if (this.snapshot.channelOwner !== owner) return;
    this.set({ channelRoomId: roomId });
  }
  /** 稳定代理：始终转发给当前 owner 的最新回调。 */
  dispatchChannel = (roomId: number) => {
    if (this.snapshot.channelOwner !== null) this.handler?.(roomId);
  };

  setNavStatus(patch: { busy?: boolean; error?: string; errorKind?: '' | 'transcript-draft' }) {
    // 写入 error 时若未显式给 kind，则 kind 复位，避免旧 kind 粘到新的真实错误上。
    this.set('error' in patch && !('errorKind' in patch) ? { ...patch, errorKind: '' } : patch);
  }
  /** 草稿结束后只清「字幕草稿取消导航」类型提示；其它错误原样保留。 */
  clearTranscriptDraftError() {
    if (this.snapshot.errorKind === 'transcript-draft') this.set({ error: '', errorKind: '' });
  }
}

const LayoutContext = createContext<LayoutStore | null>(null);

/* ───────────────────────── hooks / registration ───────────────────────── */

/**
 * 页面（AppShell 内部）注册直播频道绑定。回调经 ref 稳定代理，不需要 useCallback，
 * 也不会因 inline 回调的 setState 引起循环；只在 navId 归属匹配时生效，离页自动清理。
 * `onChannelSelect` 为 undefined 时不接管（侧栏频道回落为跳转独立监控屏）。
 */
export function usePersistentChannelBinding(
  navId: number,
  onChannelSelect: ((roomId: number) => void) | undefined,
  channelRoomId: number | null | undefined,
): void {
  const store = useContext(LayoutContext);
  const enabled = Boolean(onChannelSelect);

  useLayoutEffect(() => {
    if (!store || !enabled) return undefined;
    store.claimChannel(navId);
    return () => store.releaseChannel(navId);
  }, [store, navId, enabled]);

  // 每次渲染同步最新回调（只写 ref，不触发 setState）。
  useLayoutEffect(() => {
    store?.setChannelHandler(navId, onChannelSelect);
  });

  const room = channelRoomId ?? null;
  useLayoutEffect(() => {
    if (!store || !enabled) return;
    store.setChannelRoom(navId, room);
  }, [store, navId, enabled, room]);
}

export interface EditorNavigationRegistrationProps {
  /** 返回 null 允许离开；返回字符串则阻止并显示该原因。由编辑器页面提供，本组件不改其语义。 */
  beforeNavigate: (targetHref: string) => Promise<string | { message: string; kind: 'transcript-draft' } | null>;
  /** 字幕草稿当前是否仍 dirty（可观察信号；页面仍用 ref 做即时判断）。变为 false 时清除 transcript-draft 类提示。 */
  transcriptDraft?: boolean;
}

/**
 * 编辑器页渲染它以注册全应用唯一的离页守卫（迁自 FilmCraftAppNav）。
 * 渲染 null；busy/error 写入持久 rail 的状态出口。
 */
export function EditorNavigationRegistration({ beforeNavigate, transcriptDraft = false }: EditorNavigationRegistrationProps): null {
  const store = useContext(LayoutContext);
  const pending = useRef(false);
  const mounted = useRef(true);
  const draftRef = useRef(transcriptDraft);
  draftRef.current = transcriptDraft;

  // draft 变 false：只清 kind==='transcript-draft' 的提示；pending 期间不动（异步结果落地时再按 draftRef 判定）。
  useEffect(() => {
    if (!transcriptDraft && !pending.current) store?.clearTranscriptDraftError();
  }, [transcriptDraft, store]);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      store?.setNavStatus({ busy: false, error: '', errorKind: '' });
    };
  }, [store]);

  useEffect(() => registerAppLeaveGuard(async (targetHref: string) => {
    if (pending.current) return '正在确认保存，请稍候。';
    pending.current = true;
    store?.setNavStatus({ busy: true, error: '' });
    try {
      const result = await beforeNavigate(targetHref);
      if (!mounted.current) return '当前编辑器已离开。';
      const reason = typeof result === 'string' || result === null ? result : result.message;
      if (typeof result === 'object' && result !== null) {
        // 异步期间草稿已结束：该提示已过期，不显示（导航仍被本次结果拦截，用户重试即可）。
        if (draftRef.current) store?.setNavStatus({ error: result.message, errorKind: result.kind });
      } else if (reason) store?.setNavStatus({ error: reason });
      return reason;
    } catch (cause) {
      const reason = `离开前保存失败：${cause instanceof Error ? cause.message : String(cause)}。当前工程已保留，请重试保存。`;
      if (mounted.current) store?.setNavStatus({ error: reason });
      return reason;
    } finally {
      pending.current = false;
      if (mounted.current) store?.setNavStatus({ busy: false });
    }
  }), [beforeNavigate, store]);

  return null;
}

/* ───────────────────────── navigation ───────────────────────── */

function currentClip(search: string): string | null {
  const params = new URLSearchParams(search);
  return (params.get('clipId') ?? params.get('clip'))?.trim() || null;
}

/** 所有导航按钮的唯一入口：经 navigateApp（含离页守卫）；studio↔editor 保留当前 clip。 */
function navigateRail(id: string) {
  const page = PAGES[id as ShellPageId];
  if (!page) return;
  const { pathname, search } = window.location;
  const clip = currentClip(search);
  // 与 App.tsx 的 editor 判定同一合同：/editor*、以及 `/` 带 ?clip=/?tab= 深链。
  const inEditor = pathname.startsWith('/editor') || (pathname === '/' && !isAppShellRoute(pathname, search));
  let href = page.href;
  if (id === 'editor') {
    if (inEditor) return; // 已在剪辑页：重载会丢会话态。
    if (pathname.startsWith('/studio') && clip) href = `${page.href}?clip=${encodeURIComponent(clip)}`;
  } else if (id === 'studio' && inEditor && clip) {
    href = `${page.href}?clip=${encodeURIComponent(clip)}`;
  }
  if (`${pathname}${search}` === href) return;
  void navigateApp(href);
}

function navigateMonitor() {
  if (window.location.pathname.startsWith('/live/monitor')) return;
  void navigateApp('/live/monitor');
}

/* ───────────────────────── layout ───────────────────────── */

export interface PersistentAppLayoutProps {
  /** 页面内容（含各自 Topbar/main）。App 把 ErrorBoundary key(navId) + Suspense 放在这里面。 */
  children: ReactNode;
  /** 不传则由当前 URL 推出（resolvePersistentLayoutMode）。 */
  mode?: PersistentLayoutMode;
}

function PersistentNavSidebar({ store, activePage, navId }: { store: LayoutStore; activePage: ShellPageId; navId: number }) {
  const snap = useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
  // 只有当前 navId 页面拥有绑定才接管；fallback 期间旧页 owner 不匹配时回落为独立监控屏跳转。
  const owned = snap.channelOwner === navId;
  return (
    <AppNavSidebar
      activePage={activePage}
      onChannelSelect={owned ? store.dispatchChannel : undefined}
      channelRoomId={owned ? snap.channelRoomId : null}
    />
  );
}

export function PersistentAppLayout({ children, mode: modeProp }: PersistentAppLayoutProps) {
  const location = useAppLocation();
  const [store] = useState(() => new LayoutStore());
  const snap = useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
  const mode = modeProp ?? resolvePersistentLayoutMode(location.pathname);
  const editor = mode === 'editor';
  // `/?clip=`/`/?tab=` 深链 pathname 为 '/'，resolveShellPage 会给 home；editor 模式以 mode 为准。
  const activePage: ShellPageId = editor ? 'editor' : resolveShellPage(location.pathname) ?? 'home';
  const busy = editor && snap.busy;

  const railItems = useMemo<IconRailItem[]>(
    () =>
      PAGE_ORDER.map((id) => ({
        id,
        label: editor && id === 'music' ? '音乐工作室' : PAGES[id].label,
        icon: PAGES[id].icon,
        active: id === activePage,
        disabled: editor && snap.busy && id !== activePage,
      })),
    [activePage, editor, snap.busy],
  );

  const onRailKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (!editor) return;
    event.stopPropagation(); // 编辑器全局快捷键不吃导航栏上的按键。
    if (event.key === 'Escape' && !store.getSnapshot().busy) {
      store.setNavStatus({ error: '', errorKind: '' });
      if (event.target instanceof HTMLElement) event.target.blur();
    }
  };

  return (
    <LayoutContext.Provider value={store}>
      <div className={`lc-app-shell lc-persistent-layout lc-persistent-layout--${mode}`} data-layout-mode={mode}>
        {mode === 'regular' ? (
          <a className="lc-skip-link" href="#lc-main-content">
            跳到主要内容
          </a>
        ) : null}

        <div
          className="lc-persistent-layout__rail"
          aria-hidden={mode === 'monitor' ? true : undefined}
          onKeyDown={onRailKeyDown}
        >
          <IconRail
            items={railItems}
            activeId={activePage}
            onSelect={navigateRail}
            ariaLabel="主导航"
            footer={
              <IconButton label="独立监控屏" onClick={navigateMonitor}>
                <MonitorPlay size={18} aria-hidden="true" />
              </IconButton>
            }
          />
          {editor ? (
            <p className="lc-persistent-layout__hint" role="status" data-busy={busy}>
              {busy ? '正在确认保存…' : '离开剪辑前会先保存工程'}
            </p>
          ) : null}
          {editor && snap.error ? (
            <p className="lc-persistent-layout__error" role="alert">
              {snap.error}
            </p>
          ) : null}
        </div>

        <NavSidebarSlot>
          <PersistentNavSidebar store={store} activePage={activePage} navId={location.navId} />
        </NavSidebarSlot>

        <PageSlideMotion
          className={editor ? 'lc-persistent-layout__main fc-editor-motion' : 'lc-persistent-layout__main lc-app-shell__main'}
          routeKey={String(location.navId)}
          ready={location.ready}
        >
          {children}
        </PageSlideMotion>
      </div>
    </LayoutContext.Provider>
  );
}

export default PersistentAppLayout;
