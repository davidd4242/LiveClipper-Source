/**
 * LiveClipper Studio — 设计基础层统一出口。
 *
 * 用法：`import { Button, Card, StatusDot } from '../design';`
 * 从本文件导入会同时加载 tokens.css 与各组件的 CSS（Vite/TS 支持 CSS 副作用导入）。
 * 主题：入口调用 `initTheme()`（或自行设置 `<html data-theme="light|dark">`）后，
 * 本层令牌与组件才会生效；未设置 data-theme 时不产生任何全局样式副作用。
 */
import './tokens.css';

export { Button } from './components/Button';
export type { ButtonProps, ButtonSize, ButtonVariant } from './components/Button';

export { IconButton } from './components/IconButton';
export type { IconButtonProps, IconButtonSize, IconButtonVariant } from './components/IconButton';

export { Card } from './components/Card';
export type { CardProps } from './components/Card';

export { StatusDot } from './components/StatusDot';
export type { StatusDotProps, StatusTone } from './components/StatusDot';

export { Badge } from './components/Badge';
export type { BadgeProps, BadgeTone } from './components/Badge';

export { SegmentedTabPanel, SegmentedTabs, segmentedTabId } from './components/SegmentedTabs';
export type {
  SegmentedTabItem,
  SegmentedTabPanelProps,
  SegmentedTabsProps,
} from './components/SegmentedTabs';

export { TextField } from './components/TextField';
export type { TextFieldProps } from './components/TextField';

export { SelectField } from './components/SelectField';
export type { SelectFieldProps } from './components/SelectField';

export { Panel } from './components/Panel';
export type { PanelProps } from './components/Panel';

export { EmptyState } from './components/EmptyState';
export type { EmptyStateProps } from './components/EmptyState';

/* ── Shell 组件 ─────────────────────────────────────────────── */

export { IconRail } from './components/IconRail';
export type { IconRailItem, IconRailProps } from './components/IconRail';

export { Sidebar } from './components/Sidebar';
export type { SidebarItem, SidebarProps, SidebarSection } from './components/Sidebar';

export { Topbar } from './components/Topbar';
export type { TopbarProps } from './components/Topbar';

export { ThemeToggle } from './components/ThemeToggle';
export type { ThemeToggleProps } from './components/ThemeToggle';

export { Toast, ToastStack, useToastQueue } from './components/Toast';
export type {
  ToastItem,
  ToastProps,
  ToastStackProps,
  ToastTone,
  UseToastQueueResult,
} from './components/Toast';

export { Dialog } from './components/Dialog';
export type { DialogProps, DialogSize } from './components/Dialog';

/* ── 主题状态 ───────────────────────────────────────────────── */

export {
  applyTheme,
  getStoredTheme,
  initTheme,
  isTheme,
  toggleTheme,
  THEME_STORAGE_KEY,
} from './theme';
export type { ApplyThemeOptions, Theme } from './theme';
