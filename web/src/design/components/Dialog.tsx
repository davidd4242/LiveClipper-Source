import { useEffect, useId, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { cn } from '../cn';
import './Dialog.css';

export type DialogSize = 'sm' | 'md' | 'lg';

export interface DialogProps {
  open: boolean;
  title: ReactNode;
  /** 标题下方的说明文字。 */
  description?: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
  onClose: () => void;
  /** sm 420 / md 560 / lg 760（px）。 */
  size?: DialogSize;
  /** 点击遮罩是否关闭，默认 true。 */
  closeOnOverlay?: boolean;
  className?: string;
}

const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'textarea:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

/**
 * 当前打开的 Dialog 栈（同一时刻可能有嵌套：Dialog 里再开确认框）。
 * 只有栈顶实例处理 Esc / Tab 循环，避免一次 Esc 关掉两层；
 * 每个实例的 inert / overflow / 焦点还原仍由自己的 effect 独立清理。
 */
const dialogStack: symbol[] = [];

function CloseIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M6 6l12 12M18 6 6 18" />
    </svg>
  );
}

export function Dialog({
  open,
  title,
  description,
  children,
  footer,
  onClose,
  size = 'md',
  closeOnOverlay = true,
  className,
}: DialogProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const stackToken = useRef(Symbol('lc-dialog'));
  const onCloseRef = useRef(onClose);
  const titleId = useId();
  const descriptionId = useId();

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    if (!open) return;
    const panel = panelRef.current;
    const container = containerRef.current;
    const token = stackToken.current;
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const previousOverflow = document.body.style.overflow;

    dialogStack.push(token);
    document.body.style.overflow = 'hidden';

    // 背景隔离：对话框经 portal 挂在 body 上，对容器以外的 body 子节点设 `inert`，
    // 键盘 Tab 与屏幕阅读器都无法进入背景内容（无需再叠加 aria-hidden）。
    const backgroundSiblings = container?.parentElement
      ? Array.from(container.parentElement.children).filter(
          (node): node is HTMLElement => node instanceof HTMLElement && node !== container,
        )
      : [];
    const alreadyInert = new Set(backgroundSiblings.filter((node) => node.hasAttribute('inert')));
    for (const node of backgroundSiblings) node.setAttribute('inert', '');

    const getFocusables = () => {
      if (!panel) return [] as HTMLElement[];
      return Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
    };

    const firstFocusable = getFocusables()[0] ?? panel;
    firstFocusable?.focus();

    const handleKeyDown = (event: KeyboardEvent) => {
      // 嵌套 Dialog：只有栈顶实例响应键盘，底层保持静止。
      if (dialogStack[dialogStack.length - 1] !== token) return;
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      if (event.key !== 'Tab') return;

      const focusables = getFocusables();
      if (focusables.length === 0) {
        event.preventDefault();
        panel?.focus();
        return;
      }

      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      const active = document.activeElement;

      // 极端情况下焦点跑到对话框外：拉回面板内，而不是让 Tab 逃逸。
      if (!panel || !panel.contains(active)) {
        event.preventDefault();
        first.focus();
        return;
      }

      if (event.shiftKey && (active === first || active === panel)) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (active === last || active === panel)) {
        event.preventDefault();
        first.focus();
      }
    };

    document.addEventListener('keydown', handleKeyDown, true);

    return () => {
      document.removeEventListener('keydown', handleKeyDown, true);
      const index = dialogStack.lastIndexOf(token);
      if (index >= 0) dialogStack.splice(index, 1);
      for (const node of backgroundSiblings) {
        if (!alreadyInert.has(node)) node.removeAttribute('inert');
      }
      document.body.style.overflow = previousOverflow;
      if (previouslyFocused?.isConnected) previouslyFocused.focus();
    };
  }, [open]);

  if (!open) return null;

  return createPortal(
    <div ref={containerRef} className={cn('lc-dialog', className)}>
      <div
        className="lc-dialog__overlay"
        aria-hidden="true"
        onMouseDown={(event) => {
          if (closeOnOverlay && event.target === event.currentTarget) onClose();
        }}
      />
      <div
        ref={panelRef}
        className={cn('lc-dialog__panel', `lc-dialog__panel--${size}`)}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={description ? descriptionId : undefined}
        tabIndex={-1}
      >
        <header className="lc-dialog__head">
          <div className="lc-dialog__heading">
            <h2 className="lc-dialog__title" id={titleId}>
              {title}
            </h2>
            {description ? (
              <p className="lc-dialog__description" id={descriptionId}>
                {description}
              </p>
            ) : null}
          </div>
          <button type="button" className="lc-dialog__close" onClick={onClose} aria-label="关闭对话框">
            <CloseIcon />
          </button>
        </header>
        {children ? <div className="lc-dialog__body">{children}</div> : null}
        {footer ? <footer className="lc-dialog__foot">{footer}</footer> : null}
      </div>
    </div>,
    document.body,
  );
}
