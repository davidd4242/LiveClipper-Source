import type { ReactNode } from 'react';
import { cn } from '../cn';
import './IconRail.css';

export interface IconRailItem {
  id: string;
  /** 无障碍名称，同时作为 hover/focus 时显示的 tooltip。 */
  label: string;
  icon: ReactNode;
  /** 选中态；也支持在 IconRail 上传 activeId 统一控制。 */
  active?: boolean;
  /** 右上角数量/状态标记（数字或短文本）。 */
  badge?: number | string;
  disabled?: boolean;
}

export interface IconRailProps {
  items: IconRailItem[];
  /** 受控的选中项 id，优先于 item.active。 */
  activeId?: string;
  onSelect?: (id: string) => void;
  /** 顶部品牌区节点；默认是 LiveClipper 的圆点标记。 */
  brand?: ReactNode;
  /** 底部固定区（设置、头像等）。 */
  footer?: ReactNode;
  ariaLabel?: string;
  className?: string;
}

export function IconRail({
  items,
  activeId,
  onSelect,
  brand,
  footer,
  ariaLabel = '主导航',
  className,
}: IconRailProps) {
  return (
    <nav className={cn('lc-icon-rail', className)} aria-label={ariaLabel}>
      {brand ? (
        <div className="lc-icon-rail__brand">{brand}</div>
      ) : (
        <div className="lc-icon-rail__brand" aria-hidden="true">
          <i className="lc-icon-rail__brand-dot" />
        </div>
      )}
      {items.map((item) => {
        const active = activeId ? item.id === activeId : Boolean(item.active);
        const hasBadge = item.badge !== undefined && item.badge !== null && item.badge !== '';
        return (
          <button
            key={item.id}
            type="button"
            className={cn('lc-icon-rail__item', active && 'is-active')}
            data-tooltip={item.label}
            aria-label={item.label}
            aria-current={active ? 'page' : undefined}
            disabled={item.disabled}
            onClick={onSelect ? () => onSelect(item.id) : undefined}
          >
            <span className="lc-icon-rail__glyph" aria-hidden="true">
              {item.icon}
            </span>
            {hasBadge ? <span className="lc-icon-rail__count">{item.badge}</span> : null}
          </button>
        );
      })}
      <span className="lc-icon-rail__spacer" aria-hidden="true" />
      {footer ? <div className="lc-icon-rail__footer">{footer}</div> : null}
    </nav>
  );
}
