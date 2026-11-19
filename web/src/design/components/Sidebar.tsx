import { useState, type ReactNode } from 'react';
import { cn } from '../cn';
import './Sidebar.css';

export interface SidebarItem {
  id: string;
  label: string;
  icon?: ReactNode;
  /** 选中态；也支持在 Sidebar 上传 activeId 统一控制。 */
  active?: boolean;
  /** 右侧数量。 */
  count?: number;
  disabled?: boolean;
}

export interface SidebarSection {
  id: string;
  /** 分组标题；省略时整组无标题。 */
  label?: ReactNode;
  /** 分组标题右侧的操作按钮。 */
  action?: ReactNode;
  items: SidebarItem[];
}

export interface SidebarProps {
  /** 顶部品牌区节点，默认「LiveClipper」。 */
  brand?: ReactNode;
  sections: SidebarSection[];
  /** 受控的选中项 id，优先于 item.active。 */
  activeId?: string;
  onSelect?: (id: string) => void;
  /** 底部固定区（用户信息等）。 */
  footer?: ReactNode;
  /** 是否显示折叠按钮，默认 true。 */
  collapsible?: boolean;
  /** 受控折叠状态。 */
  collapsed?: boolean;
  /** 非受控初始折叠状态，默认 false。 */
  defaultCollapsed?: boolean;
  onCollapsedChange?: (collapsed: boolean) => void;
  ariaLabel?: string;
  className?: string;
}

function ChevronIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M14.5 6 8.5 12l6 6" />
    </svg>
  );
}

export function Sidebar({
  brand,
  sections,
  activeId,
  onSelect,
  footer,
  collapsible = true,
  collapsed: collapsedProp,
  defaultCollapsed = false,
  onCollapsedChange,
  ariaLabel = '侧边导航',
  className,
}: SidebarProps) {
  const [uncontrolledCollapsed, setUncontrolledCollapsed] = useState(defaultCollapsed);
  const collapsed = collapsedProp ?? uncontrolledCollapsed;

  const toggle = () => {
    const next = !collapsed;
    if (collapsedProp === undefined) setUncontrolledCollapsed(next);
    onCollapsedChange?.(next);
  };

  return (
    <aside
      className={cn('lc-sidebar', collapsed && 'lc-sidebar--collapsed', className)}
      aria-label={ariaLabel}
    >
      <div className="lc-sidebar__head">
        <span className="lc-sidebar__brand">
          <i className="lc-sidebar__brand-dot" aria-hidden="true" />
          <span className="lc-sidebar__brand-name">{brand ?? 'LiveClipper'}</span>
        </span>
        {collapsible ? (
          <button
            type="button"
            className="lc-sidebar__collapse"
            onClick={toggle}
            aria-label={collapsed ? '展开侧边栏' : '折叠侧边栏'}
            aria-expanded={!collapsed}
            title={collapsed ? '展开侧边栏' : '折叠侧边栏'}
          >
            <span className={cn('lc-sidebar__collapse-icon', collapsed && 'is-collapsed')} aria-hidden="true">
              <ChevronIcon />
            </span>
          </button>
        ) : null}
      </div>

      <nav className="lc-sidebar__nav" aria-label="页面导航">
        {sections.map((section) => (
          <div key={section.id} className="lc-sidebar__section">
            {section.label || section.action ? (
              <div className="lc-sidebar__section-head">
                {section.label ? <span className="lc-sidebar__section-label">{section.label}</span> : null}
                {section.action ? <span className="lc-sidebar__section-action">{section.action}</span> : null}
              </div>
            ) : null}
            {section.items.map((item) => {
              const active = activeId ? item.id === activeId : Boolean(item.active);
              return (
                <button
                  key={item.id}
                  type="button"
                  className={cn('lc-sidebar__item', active && 'is-active')}
                  aria-current={active ? 'page' : undefined}
                  aria-label={collapsed ? item.label : undefined}
                  title={collapsed ? item.label : undefined}
                  disabled={item.disabled}
                  onClick={onSelect ? () => onSelect(item.id) : undefined}
                >
                  {item.icon ? (
                    <span className="lc-sidebar__icon" aria-hidden="true">
                      {item.icon}
                    </span>
                  ) : null}
                  <span className="lc-sidebar__label">{item.label}</span>
                  {item.count !== undefined ? <span className="lc-sidebar__count">{item.count}</span> : null}
                </button>
              );
            })}
          </div>
        ))}
      </nav>

      {footer ? <div className="lc-sidebar__foot">{footer}</div> : null}
    </aside>
  );
}
