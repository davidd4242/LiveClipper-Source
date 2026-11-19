import { useId } from 'react';
import type { HTMLAttributes, ReactNode } from 'react';
import { cn } from '../cn';
import './Panel.css';

export interface PanelProps extends Omit<HTMLAttributes<HTMLElement>, 'title'> {
  title: ReactNode;
  /** 标题上方的小字。 */
  eyebrow?: ReactNode;
  /** 右上角操作（通常是“查看全部”链接或 IconButton）。 */
  action?: ReactNode;
  footer?: ReactNode;
}

export function Panel({
  title,
  eyebrow,
  action,
  footer,
  className,
  children,
  ...rest
}: PanelProps) {
  const generatedId = useId();
  const titleId = `lc-panel-title-${generatedId.replace(/[^a-zA-Z0-9_-]/g, '')}`;
  const hasTitle = title !== null && title !== undefined && title !== false && title !== '';
  return (
    <section
      {...rest}
      className={cn('lc-panel', className)}
      aria-labelledby={hasTitle ? titleId : rest['aria-labelledby']}
    >
      <header className="lc-panel__head">
        <div className="lc-panel__heading">
          {eyebrow ? <div className="lc-panel__eyebrow">{eyebrow}</div> : null}
          <h2 id={hasTitle ? titleId : undefined} className="lc-panel__title">
            {title}
          </h2>
        </div>
        {action ? <div className="lc-panel__action">{action}</div> : null}
      </header>
      {children ? <div className="lc-panel__body">{children}</div> : null}
      {footer ? <footer className="lc-panel__foot">{footer}</footer> : null}
    </section>
  );
}
