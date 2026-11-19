export interface ProjectNavigationBridge {
  onNewProjectRequest?(callback: () => void): () => void;
  onOpenProjectRequest?(callback: () => void): () => void;
  onCloseProjectRequest?(callback: () => void): () => void;
}
// The existing leave guard controls navigation. No project creation or window exit.
export function bindProjectNavigation(bridge: ProjectNavigationBridge | undefined,
  navigate: (href: string) => Promise<boolean>, keyboard?: { window: Window; document: Document }, createNew?: () => void) {
  let disposed = false, pending = false;
  const request = async () => {
    if (disposed || pending) return;
    pending = true;
    try { await navigate('/editor'); }
    finally { pending = false; }
  };
  const create = bridge?.onNewProjectRequest?.(() => { if (!disposed) createNew?.(); });
  const open = bridge?.onOpenProjectRequest?.(request);
  const close = bridge?.onCloseProjectRequest?.(request);
  const shortcut = (event: KeyboardEvent) => {
    if (event.defaultPrevented || event.repeat || event.isComposing || !(event.ctrlKey || event.metaKey)) return;
    const key = event.key.toLowerCase();
    const isNew = key === 'n' && event.altKey && !event.shiftKey && !!create;
    if (!isNew && event.altKey) return;
    if (!(isNew || (key === 'o' && !event.shiftKey && open) || (key === 'w' && close))) return;
    const target = event.target instanceof Element ? event.target : keyboard!.document.activeElement;
    if (target?.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="textbox"]')) return;
    if (keyboard!.document.querySelector('dialog[open], [role="dialog"][aria-modal="true"], .fxm-mask')) return;
    event.preventDefault(); event.stopImmediatePropagation();
    if (isNew) createNew?.(); else void request();
  };
  keyboard?.window.addEventListener('keydown', shortcut, true);
  return () => {
    disposed = true; create?.(); open?.(); close?.();
    keyboard?.window.removeEventListener('keydown', shortcut, true);
  };
}
