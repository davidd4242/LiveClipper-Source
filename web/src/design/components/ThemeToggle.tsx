import { useEffect, useRef, useState } from 'react';
import { applyTheme, getStoredTheme, isTheme, THEME_STORAGE_KEY, type Theme } from '../theme';
import { cn } from '../cn';
import './ThemeToggle.css';

export interface ThemeToggleProps {
  /** 受控主题；省略时组件内部读取 localStorage、监听 storage 并自行应用。 */
  theme?: Theme;
  /**
   * 切换后回调。
   * - 受控模式：组件只回调，不写 `<html data-theme>` / localStorage，由父级应用；
   * - 非受控模式：主题已应用到 `<html data-theme>` 并持久化后再回调。
   */
  onChange?: (theme: Theme) => void;
  /** 禁用两枚按钮（用于只读/离线外壳）。 */
  disabled?: boolean;
  className?: string;
}

function SunIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <circle cx="12" cy="12" r="4.1" />
      <path d="M12 2.8v2.4M12 18.8v2.4M2.8 12h2.4M18.8 12h2.4M5.5 5.5l1.7 1.7M16.8 16.8l1.7 1.7M18.5 5.5l-1.7 1.7M7.2 16.8l-1.7 1.7" />
    </svg>
  );
}

function MoonIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M20 14.5A8 8 0 0 1 9.5 4a8.2 8.2 0 1 0 10.5 10.5Z" />
    </svg>
  );
}

export function ThemeToggle({
  theme: controlledTheme,
  onChange,
  disabled = false,
  className,
}: ThemeToggleProps) {
  const controlled = controlledTheme !== undefined;
  const [uncontrolledTheme, setUncontrolledTheme] = useState<Theme>(getStoredTheme);
  const theme = controlledTheme ?? uncontrolledTheme;

  // 回调存 ref：storage 同步 / 受控切换都不依赖父级每次 render 的新函数引用。
  const onChangeRef = useRef(onChange);
  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  // 非受控：挂载时把已存主题回填到 <html>（persist=false，不重复写 storage）；
  // 后续切换在 select() 里显式 applyTheme 持久化。
  useEffect(() => {
    if (controlled) return;
    applyTheme(uncontrolledTheme, { persist: false });
  }, [controlled, uncontrolledTheme]);

  // 非受控：跟随其他标签页的 storage 变更；受控模式由父级决定是否同步。
  useEffect(() => {
    if (controlled) return;
    const handleStorage = (event: StorageEvent) => {
      if (event.key !== THEME_STORAGE_KEY || !isTheme(event.newValue)) return;
      setUncontrolledTheme(event.newValue);
    };
    window.addEventListener('storage', handleStorage);
    return () => window.removeEventListener('storage', handleStorage);
  }, [controlled]);

  const select = (next: Theme) => {
    if (next === theme) return;
    if (!controlled) {
      // 非受控：立即应用 + 持久化；state 更新后上面的 effect 会再幂等应用一次。
      setUncontrolledTheme(next);
      applyTheme(next);
    }
    onChangeRef.current?.(next);
  };

  return (
    <div className={cn('lc-theme-toggle', className)} role="group" aria-label="主题切换">
      <button
        type="button"
        className={cn('lc-theme-toggle__btn', theme === 'light' && 'is-active')}
        aria-pressed={theme === 'light'}
        disabled={disabled}
        onClick={() => select('light')}
      >
        <SunIcon />
        <span>浅色</span>
      </button>
      <button
        type="button"
        className={cn('lc-theme-toggle__btn', theme === 'dark' && 'is-active')}
        aria-pressed={theme === 'dark'}
        disabled={disabled}
        onClick={() => select('dark')}
      >
        <MoonIcon />
        <span>深色</span>
      </button>
    </div>
  );
}
