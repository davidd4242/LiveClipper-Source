import type { CSSProperties, ReactNode } from 'react';
import { cn } from '../cn';
import './EmptyState.css';

/** 空态密度：default = 页面级空态；compact = 侧栏/列表列内小占位（不撑高列）。 */
export type EmptyStateDensity = 'default' | 'compact';

export interface EmptyStateProps {
  /** 顶部图标节点（建议 24×24 SVG，compact 时建议 18×18）。 */
  icon?: ReactNode;
  /** 一句引导语，避免“暂无数据”式空话。 */
  title: ReactNode;
  description?: ReactNode;
  /** 可选行动入口（Button 等）。 */
  action?: ReactNode;
  /** v1.1：密度，默认 default；compact 用于列内紧凑占位。 */
  density?: EmptyStateDensity;
  /**
   * @deprecated 使用 `density="compact"`；保留旧布尔 API 兼容已接线页面。
   */
  compact?: boolean;
  /**
   * 覆盖尺寸的 CSS 变量（高度/内边距/图标尺寸），例如
   * `style={{ '--lc-empty-min-height': '64px' } as CSSProperties}`。
   */
  style?: CSSProperties;
  className?: string;
}

export function EmptyState({
  icon,
  title,
  description,
  action,
  density = 'default',
  compact = false,
  style,
  className,
}: EmptyStateProps) {
  const isCompact = density === 'compact' || compact;
  return (
    <div
      className={cn('lc-empty', isCompact && 'lc-empty--compact', className)}
      style={style}
      data-density={isCompact ? 'compact' : 'default'}
    >
      {icon ? (
        <div className="lc-empty__icon" aria-hidden="true">
          {icon}
        </div>
      ) : null}
      <div className="lc-empty__title">{title}</div>
      {description ? <p className="lc-empty__desc">{description}</p> : null}
      {action ? <div className="lc-empty__action">{action}</div> : null}
    </div>
  );
}
