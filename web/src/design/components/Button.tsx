import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { cn } from '../cn';
import './Button.css';

export type ButtonVariant = 'solid' | 'outline' | 'ghost' | 'link';
export type ButtonSize = 'sm' | 'md' | 'lg';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** 视觉变体；默认 outline（次按钮）。solid 为实心主按钮。 */
  variant?: ButtonVariant;
  /** 高度：sm 34px / md 38px / lg 40px。 */
  size?: ButtonSize;
  /** 加载中：显示 spinner 并自动禁用。 */
  loading?: boolean;
  /** 撑满父容器宽度。 */
  block?: boolean;
  /** 文字前置图标节点（任意 ReactNode，如 lucide 图标）。 */
  leading?: ReactNode;
}

export function Button({
  variant = 'outline',
  size = 'md',
  loading = false,
  block = false,
  leading,
  className,
  children,
  disabled,
  type = 'button',
  ...rest
}: ButtonProps) {
  return (
    <button
      {...rest}
      type={type}
      className={cn(
        'lc-btn',
        `lc-btn--${variant}`,
        `lc-btn--${size}`,
        block && 'lc-btn--block',
        loading && 'lc-btn--loading',
        className,
      )}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
    >
      {loading ? (
        <span className="lc-btn__spinner" aria-hidden="true" />
      ) : leading ? (
        <span className="lc-btn__icon" aria-hidden="true">
          {leading}
        </span>
      ) : null}
      <span className="lc-btn__label">{children}</span>
    </button>
  );
}
