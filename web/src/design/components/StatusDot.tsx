import { cn } from '../cn';
import './StatusDot.css';

/** 状态语义：绿=完成/在线，蓝=进行中/信息，紫=待开始，红=错误，琥珀=警告，idle=中性离线。 */
export type StatusTone = 'green' | 'blue' | 'purple' | 'red' | 'amber' | 'idle';

export interface StatusDotProps {
  tone?: StatusTone;
  /** 无障碍名称；提供时作为 role="img"，否则视为装饰。 */
  label?: string;
  /** sm 7px / md 9px。 */
  size?: 'sm' | 'md';
  /** 呼吸动画（仅用于“进行中/在线”等高关注状态）。 */
  pulse?: boolean;
  className?: string;
}

export function StatusDot({
  tone = 'idle',
  label,
  size = 'md',
  pulse = false,
  className,
}: StatusDotProps) {
  return (
    <span
      role={label ? 'img' : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
      title={label}
      className={cn('lc-dot', `lc-dot--${tone}`, `lc-dot--${size}`, pulse && 'lc-dot--pulse', className)}
    />
  );
}
