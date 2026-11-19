/**
 * 设置 document.title 的轻量组件。
 *
 * 外壳页面（/、/live、/studio、/music、/settings）由 AppShell 按页面元数据设置标题；
 * 不在 AppShell 内的全屏页面（/editor、/live/monitor）由 App.tsx 渲染本组件兜底。
 * 用 effect 而不是组件内直接赋值，保证 StrictMode 下重复挂载/卸载后标题稳定，
 * 也保证导航到该页时标题一定会被重新写回。
 */
import { useEffect } from 'react';

export interface PageTitleProps {
  title: string;
}

export function PageTitle({ title }: PageTitleProps) {
  useEffect(() => {
    document.title = title;
  }, [title]);

  return null;
}
