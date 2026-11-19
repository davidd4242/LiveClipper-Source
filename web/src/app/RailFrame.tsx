/**
 * 编辑器页的图标侧边栏外壳。
 *
 * 背景：全局 64px IconRail 由 AppShell（工作台/设置）以及 /studio、/live、/music
 * 各自的外壳渲染，唯独 /editor 是裸画布 —— 切到剪辑页后图标栏就消失了。
 * 这里复用同一份导航数据（PAGES / PAGE_ORDER）把 IconRail 放到编辑器画布左侧，
 * 不再复制第二套图标与跳转表。
 *
 * 布局：`RailFrame = IconRail + 画布`。画布用 flex:1 吃掉剩余宽度，
 * min-width:0 防止 `.editor-shell` 的 min-width 把整页撑出横向滚动。
 *
 * 配色：IconRail 使用应用外壳的语义令牌（浅主题下是白底），编辑器画布固定深色；
 * 这里只在栏的作用域内切换到深色令牌（等同 dark 主题），不改设计层组件，
 * 也不影响画布内部颜色。
 */
import { useCallback, useRef, type ReactNode } from 'react';
import { EditorNavigationRegistration } from './PersistentAppLayout';
import './RailFrame.css';

export function RailFrame({ children, beforeNavigate }: {
  children: ReactNode;
  /** 可选：跳转前守卫，返回 false 取消跳转（R81 编辑器外壳用它先落盘未保存改动）。 */
  beforeNavigate?: (href: string) => boolean | Promise<boolean>;
}) {
  // IconRail / 侧栏 / 滑入槽已由 PersistentAppLayout 持有；这里只保留内容槽，并把旧的
  // beforeNavigate(bool) 转成全应用唯一离页守卫（false → 阻止并给出原因）。
  // href 为真实导航目标（绝对 URL，来自 appNavigation 的 guard 调用）。
  const beforeRef = useRef(beforeNavigate);
  beforeRef.current = beforeNavigate;
  const guard = useCallback(async (targetHref: string): Promise<string | null> => {
    const check = beforeRef.current;
    if (!check) return null;
    const allowed = await check(targetHref);
    return allowed ? null : '当前页面尚未确认保存，已留在当前页面。';
  }, []);

  return (
    <div className="lc-rail-frame__body">
      {beforeNavigate ? <EditorNavigationRegistration beforeNavigate={guard} /> : null}
      {children}
    </div>
  );
}
