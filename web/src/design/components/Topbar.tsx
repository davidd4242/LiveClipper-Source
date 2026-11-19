import type { HTMLAttributes, ReactNode } from 'react';
import { cn } from '../cn';
import './Topbar.css';

export interface TopbarProps extends Omit<HTMLAttributes<HTMLElement>, 'title'> {
  /** 面包屑：字符串数组或任意节点。 */
  breadcrumbs?: ReactNode | string[];
  /** 标题上方的小字（如「工作台 · 任务中心」）。 */
  eyebrow?: ReactNode;
  title?: ReactNode;
  subtitle?: ReactNode;
  /** 右侧操作区（按钮组等）。 */
  actions?: ReactNode;
  /** 右侧主题切换位（通常放 <ThemeToggle />），在 actions 左侧。 */
  themeSlot?: ReactNode;
  /** 最左侧节点（如移动端菜单按钮）。 */
  leading?: ReactNode;
  /** 吸顶（长内容滚动时保持可见）。 */
  sticky?: boolean;
}

export function Topbar({
  breadcrumbs,
  eyebrow,
  title,
  subtitle,
  actions,
  themeSlot,
  leading,
  sticky = false,
  className,
  ...rest
}: TopbarProps) {
  const crumbs = Array.isArray(breadcrumbs) ? breadcrumbs : null;

  return (
    <header {...rest} className={cn('lc-topbar', sticky && 'lc-topbar--sticky', className)}>
      {leading ? <div className="lc-topbar__leading">{leading}</div> : null}
      <div className="lc-topbar__head">
        {crumbs ? (
          <nav className="lc-topbar__crumbs" aria-label="面包屑">
            {crumbs.map((crumb, index) => (
              <span key={`${crumb}-${index}`} className="lc-topbar__crumb">
                <span>{crumb}</span>
                {index < crumbs.length - 1 ? (
                  <span className="lc-topbar__separator" aria-hidden="true">
                    /
                  </span>
                ) : null}
              </span>
            ))}
          </nav>
        ) : breadcrumbs ? (
          <div className="lc-topbar__crumbs">{breadcrumbs}</div>
        ) : null}
        {eyebrow ? <div className="lc-topbar__eyebrow">{eyebrow}</div> : null}
        {title ? <h1 className="lc-topbar__title">{title}</h1> : null}
        {subtitle ? <div className="lc-topbar__sub">{subtitle}</div> : null}
      </div>
      {actions || themeSlot ? (
        <div className="lc-topbar__actions">
          {themeSlot}
          {actions}
        </div>
      ) : null}
    </header>
  );
}
