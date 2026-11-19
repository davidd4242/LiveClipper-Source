import type { HTMLAttributes, ReactNode } from 'react';
import { cn } from '../cn';
import './Card.css';

export interface CardProps extends Omit<HTMLAttributes<HTMLElement>, 'title'> {
  /** 卡片标题。 */
  title?: ReactNode;
  /** 标题上方的小字（如任务编号、分组）。 */
  eyebrow?: ReactNode;
  /** 标题下方的元信息行。 */
  meta?: ReactNode;
  /** 右上角操作区（通常是 StatusDot 或 IconButton）。 */
  actions?: ReactNode;
  /** 底部操作区，自动贴底。 */
  footer?: ReactNode;
  /** 可交互卡片（hover 描边加深）；点击语义由使用方包裹按钮处理。 */
  interactive?: boolean;
  /**
   * 标题语义层级，默认 2（h2）。页面结构里卡片需要嵌在 h2 小节下时传 3，
   * 避免标题层级跳级；无 `title` 时不渲染标题元素。
   */
  headingLevel?: 1 | 2 | 3 | 4;
}

export function Card({
  title,
  eyebrow,
  meta,
  actions,
  footer,
  interactive = false,
  headingLevel = 2,
  className,
  children,
  ...rest
}: CardProps) {
  const hasHead = Boolean(title || eyebrow || meta || actions);
  const HeadingTag = `h${headingLevel}` as 'h1' | 'h2' | 'h3' | 'h4';
  return (
    <article
      {...rest}
      className={cn('lc-card', interactive && 'lc-card--interactive', className)}
    >
      {hasHead ? (
        <header className="lc-card__head">
          <div className="lc-card__heading">
            {eyebrow ? <div className="lc-card__eyebrow">{eyebrow}</div> : null}
            {title ? <HeadingTag className="lc-card__title">{title}</HeadingTag> : null}
            {meta ? <div className="lc-card__meta">{meta}</div> : null}
          </div>
          {actions ? <div className="lc-card__actions">{actions}</div> : null}
        </header>
      ) : null}
      {children ? <div className="lc-card__body">{children}</div> : null}
      {footer ? <footer className="lc-card__foot">{footer}</footer> : null}
    </article>
  );
}
