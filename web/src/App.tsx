import { Suspense, useEffect, useRef } from 'react';
import { useHomeStartupPhase } from './app/startupReadiness';
import { AppShell, isAppShellRoute } from './app/AppShell';
import { RailFrame } from './app/RailFrame';
import { ErrorBoundary } from './app/ErrorBoundary';
import { RouteFallback } from './app/RouteFallback';
import { lazyWithRetry } from './app/lazyWithRetry';
import { PageTitle } from './app/PageTitle';
import { PageSlideMotionProvider } from './app/PageSlideMotion';
import { NavSidebarMotionProvider } from './app/NavSidebarMotion';
import { PersistentAppLayout, type PersistentLayoutMode } from './app/PersistentAppLayout';
import { AppRouteCommitted, useAppLocation, useAppNavigationEvents } from './app/appNavigation';

export { isAppShellRoute };

// 桌面壳首屏合同（消费方 src/LiveClipper.Desktop/MainWindow.xaml.cs 的 WebMessageReceived）：
// - 首次 location.ready → lc-first-route-ready，壳收到才把启动 Logo 推到 100%（SplashStage(5)）；
// - ErrorBoundary 首屏报错且 ready 尚未发过 → lc-first-route-error，壳放弃等待并显示启动错误。
// 只在 WebView2 里有 chrome.webview（写法同 theme.ts notifyHostTheme），浏览器环境静默跳过；
// 每次页面加载各最多一条，先到者结束本次首屏合同。
let hostFirstRouteReadySent = false;
let hostFirstRouteErrorSent = false;
function notifyHostFirstRoute(kind: 'ready' | 'error'): void {
  if (kind === 'ready' ? hostFirstRouteReadySent : hostFirstRouteErrorSent) return;
  if (kind === 'ready') hostFirstRouteReadySent = true; else hostFirstRouteErrorSent = true;
  try {
    const host = (window as unknown as { chrome?: { webview?: { postMessage?: (m: unknown) => void } } }).chrome?.webview;
    host?.postMessage?.({ type: kind === 'ready' ? 'lc-first-route-ready' : 'lc-first-route-error' });
  } catch {
    // 通知失败不影响页面（同 theme.ts notifyHostTheme）。
  }
}

// 页面级懒加载：import 失败会重试一次（chunk 404/网络抖动），仍失败交给 ErrorBoundary。
const EditorPage = lazyWithRetry(() =>
  import('./features/editor/EditorPage').then((module) => ({ default: module.EditorPage })),
);
const EditorNextPage = lazyWithRetry(() =>
  import('./features/editor-next/EditorNextRoute').then((module) => ({ default: module.EditorNextRoute })),
);
const HomePage = lazyWithRetry(() =>
  import('./features/home/HomePage').then((module) => ({ default: module.HomePage })),
);
const LivePage = lazyWithRetry(() =>
  import('./features/live/LivePage').then((module) => ({ default: module.LivePage })),
);
const MonitorScreen = lazyWithRetry(() =>
  import('./features/live/MonitorScreen').then((module) => ({ default: module.MonitorScreen })),
);
const MusicStudioPage = lazyWithRetry(() =>
  import('./features/music/MusicStudioPage').then((module) => ({ default: module.MusicStudioPage })),
);
const SettingsPage = lazyWithRetry(() =>
  import('./features/settings/SettingsPage').then((module) => ({ default: module.SettingsPage })),
);
const StudioPage = lazyWithRetry(() =>
  import('./features/studio/StudioPage').then((module) => ({ default: module.StudioPage })),
);

/**
 * 无路由库的入口分派：按 pathname 前缀选择页面。
 *
 * 迁移进度（design/ui/migration-inventory.md）：
 * - `/` 工作台已迁到新外壳（AppShell + HomePage）；
 * - `/settings` 设置页已迁到新外壳（AppShell + SettingsPage），旧界面仍由 /legacy.html 兜底；
 * - `/editor` 与 `/editor-next` 使用复刻版编辑器，旧页保留在 `/editor-legacy`；
 * - `/?clip=`、`/?tab=` 深链使用复刻版编辑器，保留原查询参数；
 * - 未登记路径保持 EditorPage 兜底，与迁移前行为一致。
 *
 * 七个页面均按需加载（lazyWithRetry），Suspense 骨架由 RouteFallback 提供；
 * 页面加载/渲染失败由 ErrorBoundary 兜底（按路由 key 重置）。
 */
/** 路由 → document.title 的最终分派表（外壳页与全屏页统一在此兜底）。 */
function resolveDocumentTitle(pathname: string, search: string): string {
  if (pathname.startsWith('/live/monitor')) return '监控台 · LiveClipper';
  if (pathname.startsWith('/live')) return '直播 · LiveClipper';
  if (pathname.startsWith('/studio')) return '素材 · LiveClipper';
  if (pathname.startsWith('/music')) return '音乐 · LiveClipper';
  if (pathname.startsWith('/settings')) return '设置 · LiveClipper';
  if (pathname.startsWith('/editor')) return '剪辑 · LiveClipper';
  const params = new URLSearchParams(search);
  // `/` 带 clip/tab 深链时标题跟随编辑器。
  if (pathname === '/' && !params.has('clip') && !params.has('tab')) return '工作台 · LiveClipper';
  return '剪辑 · LiveClipper';
}

function RouteContent({ pathname, search }: { pathname: string; search: string }) {
  if (pathname === '/editor' || pathname === '/editor-next' ||
    (pathname === '/' && !isAppShellRoute(pathname, search))) return <EditorNextPage />;
  if (pathname.startsWith('/music')) return <MusicStudioPage />;
  if (pathname.startsWith('/studio')) return <StudioPage />;
  if (pathname.startsWith('/settings')) {
    return (
      <AppShell>
        <SettingsPage />
      </AppShell>
    );
  }
  // 全屏页面不在 AppShell 内，标题在这里按路由分派；外壳页面的标题由 AppShell 负责。
  if (pathname.startsWith('/live/monitor')) {
    return (
      <>
        <PageTitle title="监控台 · LiveClipper" />
        <MonitorScreen />
      </>
    );
  }
  if (pathname.startsWith('/live')) return <LivePage />;
  if (isAppShellRoute(pathname, search)) {
    return (
      <AppShell>
        <HomePage />
      </AppShell>
    );
  }
  return (
    <RailFrame>
      <PageTitle title="剪辑 · LiveClipper" />
      <EditorPage />
    </RailFrame>
  );
}

export function App() {
  const location = useAppLocation();
  useAppNavigationEvents();
  const { pathname, search, key, navId } = location;
  const editor = pathname === '/editor' || pathname === '/editor-next' ||
    (pathname === '/' && !isAppShellRoute(pathname, search));

  useEffect(() => {
    document.title = resolveDocumentTitle(pathname, search);
  }, [pathname, search]);

  // 首次 location.ready（AppRouteCommitted / EditorNextPage 标记）→ 通知桌面壳首屏已挂载，Logo 才到 100%。
  // 默认首页（首次路由是 '/' 工作台）改为等首批 5 个数据请求 commit（startupReadiness）；
  // 其余首次路由（深链、/settings 等）本步保留原 location.ready 逻辑，数据门槛待接。
  // 首页数据仍 pending 时用户已离开首页：退回原 location.ready，避免永久等待。
  const initialHomeRef = useRef(pathname === '/' && isAppShellRoute(pathname, search));
  const homeStartup = useHomeStartupPhase();
  const onHome = pathname === '/' && isAppShellRoute(pathname, search);
  useEffect(() => {
    if (initialHomeRef.current && (onHome || homeStartup !== 'pending')) {
      if (homeStartup === 'ready') notifyHostFirstRoute('ready');
      else if (homeStartup === 'error') notifyHostFirstRoute('error');
      return;
    }
    if (location.ready) notifyHostFirstRoute('ready');
  }, [location.ready, onHome, homeStartup]);

  // 宽文字侧栏目标只跟随已接受的实际 URL（守卫取消时 URL 不变，目标不变）。
  const sidebarOpen = !editor && !pathname.startsWith('/live/monitor') && (
    pathname.startsWith('/music') || pathname.startsWith('/studio') || pathname.startsWith('/live') ||
    isAppShellRoute(pathname, search));

  // 明确传 mode：pathname-only 的 resolvePersistentLayoutMode 认不出 `/?clip=`/`/?tab=` 编辑器深链。
  const layoutMode: PersistentLayoutMode = pathname.startsWith('/live/monitor')
    ? 'monitor'
    : editor ? 'editor' : 'regular';

  return (
    <NavSidebarMotionProvider open={sidebarOpen}>
      <PageSlideMotionProvider>
        <PersistentAppLayout mode={layoutMode}>
          <ErrorBoundary key={navId} onError={() => { if (!hostFirstRouteReadySent) notifyHostFirstRoute('error'); }}>
            <Suspense fallback={<RouteFallback />}>
              <RouteContent pathname={pathname} search={search} />
              <AppRouteCommitted routeKey={key} editor={editor} />
            </Suspense>
          </ErrorBoundary>
        </PersistentAppLayout>
      </PageSlideMotionProvider>
    </NavSidebarMotionProvider>
  );
}
