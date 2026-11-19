import { useEffect, useRef } from 'react';
import type { ReactNode } from 'react';

export function HostDrawer({ id, title, open, onClose, children, className = '', restoreFocus = true, describedBy }: {
  id: string;
  title: string;
  open: boolean;
  onClose: () => void;
  children: ReactNode;
  className?: string;
  restoreFocus?: boolean;
  describedBy?: string;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const close = useRef(onClose);
  close.current = onClose;
  useEffect(() => {
    const element = dialog.current;
    if (!open || !element) return;
    const trigger = document.activeElement;
    element.show();
    closeButton.current?.focus();
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing) return;
      // A nested apply barrier or an engine modal owns its own Escape.
      if (document.querySelector('dialog[open]:not(.editor-next-drawer), [role="dialog"][aria-modal="true"]')) return;
      event.preventDefault(); event.stopImmediatePropagation(); closeButton.current?.focus(); close.current();
    };
    window.addEventListener('keydown', escape, true);
    return () => {
      window.removeEventListener('keydown', escape, true);
      const ownedFocus = element.contains(document.activeElement);
      // Export's lifecycle restores the pre-save trigger, not show()'s later active element.
      if (!restoreFocus && ownedFocus && document.activeElement instanceof HTMLElement) document.activeElement.blur();
      if (element.open) element.close();
      if (restoreFocus && ownedFocus && trigger instanceof HTMLElement && trigger.isConnected) trigger.focus();
    };
  }, [open, restoreFocus]);
  return <dialog ref={dialog} id={id} className={`editor-next-drawer ${className}`} aria-labelledby={`${id}-title`} aria-describedby={describedBy} aria-modal="false"
    onCancel={event => {
      if (event.target !== event.currentTarget) return;
      event.preventDefault(); event.stopPropagation(); onClose();
    }}
    onKeyDown={event => event.stopPropagation()}>
    <header className="editor-next-drawer-heading">
      <h2 id={`${id}-title`} tabIndex={0}>{title}</h2>
      <button type="button" ref={closeButton} onClick={onClose} aria-label={`关闭${title}`}>关闭</button>
    </header>
    {children}
  </dialog>;
}
