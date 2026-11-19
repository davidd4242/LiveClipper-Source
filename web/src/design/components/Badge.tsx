import type { HTMLAttributes, ReactNode } from 'react';
import type { StatusTone } from './StatusDot';
import { cn } from '../cn';
import './Badge.css';

export type BadgeTone = 'neutral' | StatusTone;

export interface BadgeProps extends HTMLAttributes<HTMLSpanElement> {
  /** 状态色；neutral 为中性徽标。 */
  tone?: BadgeTone;
  /** 前置小圆点（跟随文字色）。 */
  dot?: boolean;
  children: ReactNode;
}

export function Badge({ tone = 'neutral', dot = false, className, children, ...rest }: BadgeProps) {
  return (
    <span {...rest} className={cn('lc-badge', `lc-badge--${tone}`, className)}>
      {dot ? <span className="lc-badge__dot" aria-hidden="true" /> : null}
      {children}
    </span>
  );
}
