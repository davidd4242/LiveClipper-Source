export const EDITOR_NEXT_DRAFT_NOTICE = '未保存修改会节流写入本机草稿；再次打开同一工程时可恢复或丢弃，版本或纪元变化的草稿不会应用。离开页面会尝试带版本校验保存，失败时保留草稿；浏览器存储限制或强制终止仍可能导致丢失，请优先手动保存。';

export function isEditorNextExportShortcut(event: KeyboardEvent, document: Document): boolean {
  if (event.defaultPrevented || event.repeat || event.isComposing || event.altKey || event.shiftKey
    || !(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'e') return false;
  const target = event.target instanceof Element ? event.target : document.activeElement;
  if (target?.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="textbox"]')) return false;
  return !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]');
}

export function createEditorNextExportLifecycle(options: { document: Document; open: () => Promise<boolean> }) {
  let trigger: HTMLElement | null = null;
  let disposed = false;
  let pending = false;
  let open = false;
  let generation = 0;
  let lastFocus: HTMLElement | null = options.document.activeElement instanceof HTMLElement ? options.document.activeElement : null;
  const restore = () => {
    const node = trigger;
    trigger = null;
    const active = options.document.activeElement;
    if (active instanceof HTMLElement && active !== node && active.closest('.editor-next-bar, .editor-next-drawer:not(.editor-next-export)')) return;
    if (node?.isConnected && !node.closest('[inert]')) node.focus({ preventScroll: true });
  };
  return {
    async open(triggerElement?: HTMLElement | null): Promise<boolean> {
      if (disposed || pending || open) return false;
      const active = options.document.activeElement;
      trigger = triggerElement ?? (active instanceof HTMLElement ? active : null);
      pending = true;
      const current = ++generation;
      try {
        const accepted = await options.open();
        if (disposed || current !== generation) return false;
        open = accepted;
        if (!accepted) trigger = null;
        return accepted;
      } catch { if (current === generation) trigger = null; return false; }
      finally { if (current === generation) pending = false; }
    },
    rememberFocus(target: EventTarget | null) {
      if (!pending && !open && target instanceof HTMLElement && !target.closest('dialog, [role="dialog"]')) lastFocus = target;
    },
    shortcut(event: KeyboardEvent) {
      if (!isEditorNextExportShortcut(event, options.document)) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      void this.open();
    },
    syncOpen(value: boolean) {
      if (disposed) return;
      if (value && !open && !trigger) trigger = lastFocus;
      if (open && !value) restore();
      open = value;
    },
    dispose() { disposed = true; generation++; trigger = null; },
  };
}
