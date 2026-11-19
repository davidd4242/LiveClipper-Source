/**
 * LiveClipper Studio — 应用外壳主题状态。
 *
 * 约定：
 * - 主题写在 <html data-theme="light" | "dark"> 上；设计层令牌只在该属性存在时生效。
 * - 持久化 key 固定为 `lc-studio-theme`，值为 'light' | 'dark'。
 * - 应用只使用暗色；忽略历史存储值与系统偏好。
 * - 本文件不依赖 React，可在入口、AppShell 或任何组件中调用。
 */

export type Theme = 'light' | 'dark';

/** localStorage 持久化 key。 */
export const THEME_STORAGE_KEY = 'lc-studio-theme';

const DEFAULT_THEME: Theme = 'dark';

/** 类型守卫：判断任意值是否为合法主题（供 storage 事件等外部输入使用）。 */
export function isTheme(value: unknown): value is Theme {
  return value === 'light' || value === 'dark';
}

/**
 * 忽略历史主题，包括已存储的 light。
 */
export function getStoredTheme(): Theme {
  return DEFAULT_THEME;
}

export interface ApplyThemeOptions {
  /**
   * 是否同步写入 localStorage，默认 true。
   * `storage` 事件同步、首屏回填等「DOM 跟随外部主题」的场景传 false，
   * 避免把别的标签页的写入再写回去。
   */
  persist?: boolean;
}

/** 桌面壳（WebView2）同步原生标题栏；浏览器里没有 chrome.webview，静默跳过。 */
function notifyHostTheme(theme: Theme): void {
  try {
    const host = (window as unknown as { chrome?: { webview?: { postMessage?: (m: unknown) => void } } }).chrome?.webview;
    host?.postMessage?.({ type: 'lc-theme', theme });
  } catch {
    // 通知失败不影响页面主题。
  }
}

/**
 * 应用固定暗色，默认同时覆盖历史持久化值。
 * 幂等，可在每次主题变化时调用。
 */
export function applyTheme(theme: Theme, options: ApplyThemeOptions = {}): void {
  theme = DEFAULT_THEME;
  if (typeof document === 'undefined') return;
  document.documentElement.dataset.theme = theme;
  notifyHostTheme(theme);
  if (options.persist === false) return;
  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, theme);
  } catch {
    // 写入失败不影响当前会话的暗色界面。
  }
}

/**
 * 保留旧调用接口，始终应用暗色。
 * @param current 旧调用方传入的主题，不再参与选择。
 * @returns 固定暗色主题
 */
export function toggleTheme(current: Theme = getStoredTheme()): Theme {
  current = DEFAULT_THEME;
  applyTheme(current);
  return current;
}

/**
 * 应用启动时调用一次：固定暗色并覆盖历史存储值。
 * 返回最终主题，便于入口记录 / 调试。
 */
export function initTheme(): Theme {
  const theme = getStoredTheme();
  applyTheme(theme);
  return theme;
}
