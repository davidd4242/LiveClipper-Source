import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { cn } from '../cn';
import './IconButton.css';

export type IconButtonVariant = 'outline' | 'ghost' | 'solid';
export type IconButtonSize = 'sm' | 'md';

export interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** 必填：按钮的无障碍名称（同时作为 title）。 */
  label: string;
  variant?: IconButtonVariant;
  /** sm 30×30 / md 38×38。 */
  size?: IconButtonSize;
  /** 选中态（反色块），用于 rail / 工具栏切换。 */
  active?: boolean;
  /** 图标节点，建议 18×18 viewBox 的 SVG。 */
  children: ReactNode;
}

export function IconButton({
  label,
  variant = 'ghost',
  size = 'md',
  active = false,
  className,
  children,
  type = 'button',
  ...rest
}: IconButtonProps) {
  return (
    <button
      {...rest}
      type={type}
      title={rest.title ?? label}
      aria-label={label}
      aria-pressed={active || undefined}
      className={cn(
        'lc-icon-btn',
        `lc-icon-btn--${variant}`,
        `lc-icon-btn--${size}`,
        active && 'is-active',
        className,
      )}
    >
      {children}
    </button>
  );
}
