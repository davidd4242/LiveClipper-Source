import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Ellipsis } from 'lucide-react';
import { planToolbarLayout, type ToolbarLayout } from './timelineToolbarLayout';

export interface TimelineToolbarAction {
  id: string;
  label: string;
  icon: ReactNode;
  /** 标签模式下按钮右侧的快捷键角标（与原工具栏一致）。 */
  keyHint?: string;
  active?: boolean;
  /** 选择工具未激活但有选中片段时的弱化态（原 is-idle）。 */
  idle?: boolean;
  disabled?: boolean;
  title?: string;
  /** 该动作之前插入分隔线（仅标签模式显示）。 */
  dividerBefore?: boolean;
  onClick: () => void;
}

const SLOT_WIDTH = 30;
const MIN_SPACER = 8;

interface TimelineToolbarProps {
  actions: TimelineToolbarAction[];
  /** 中间提示（如框选提示）；空间不足时省略号截断，不属于可操作项。 */
  hint?: ReactNode;
  /** 右侧固定区：吸附 / 缩放 / 时间码，永不被裁切或隐藏。 */
  children: ReactNode;
}

function ActionButton({ action, iconOnly }: { action: TimelineToolbarAction; iconOnly: boolean }) {
  return (
    <button
      type="button"
      className={`pr-tool timeline-toolbar-action${action.active ? ' is-active' : action.idle ? ' is-idle' : ''}`}
      data-toolbar-action={action.id}
      disabled={action.disabled}
      title={action.title}
      aria-label={action.label}
      aria-pressed={action.active === undefined ? undefined : action.active}
      onClick={action.onClick}
    >
      {action.icon}
      {iconOnly ? null : <span>{action.label}</span>}
      {!iconOnly && action.keyHint ? <span className="pr-tool-key" aria-hidden="true">{action.keyHint}</span> : null}
    </button>
  );
}

export function TimelineToolbar({ actions, hint, children }: TimelineToolbarProps) {
  const rootRef = useRef<HTMLDivElement>(null);
  const mainRef = useRef<HTMLDivElement>(null);
  const rightRef = useRef<HTMLDivElement>(null);
  const moreRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const focusedActionRef = useRef<string | null>(null);
  const menuId = useId();
  const [layout, setLayout] = useState<ToolbarLayout>({ mode: 'labels', visible: actions.length });
  const [stacked, setStacked] = useState(false);
  const [open, setOpen] = useState(false);
  const [menuPos, setMenuPos] = useState<{ left: number; top: number; maxHeight: number } | null>(null);
  const widths = useRef<{ labelsMain: number | null; labelsRight: number | null; iconsRight: number | null }>({
    labelsMain: null,
    labelsRight: null,
    iconsRight: null,
  });

  const total = actions.length;
  const visible = Math.min(layout.visible, total);
  const inline = actions.slice(0, visible);
  const overflow = actions.slice(visible);
  const iconOnly = layout.mode === 'icons';

  const recompute = useCallback(() => {
    const root = rootRef.current;
    const main = mainRef.current;
    const right = rightRef.current;
    if (!root || !main || !right || root.clientWidth === 0) return;
    const cs = getComputedStyle(root);
    const padding = (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0);
    const gap = parseFloat(cs.columnGap) || 0;
    const mainGap = parseFloat(getComputedStyle(main).columnGap) || 0;
    const w = widths.current;
    // 只在对应模式、且全部动作内联时测得"自然宽度"。
    if (root.dataset.mode === 'labels' && root.dataset.overflow === '0') {
      w.labelsMain = main.scrollWidth;
      w.labelsRight = right.offsetWidth;
    } else if (root.dataset.mode === 'icons') {
      w.iconsRight = right.offsetWidth;
    }
    if (w.labelsMain === null || w.labelsRight === null) return;
    const next = planToolbarLayout({
      contentWidth: root.clientWidth - padding,
      labelsMainWidth: w.labelsMain,
      labelsRightWidth: w.labelsRight,
      iconsRightWidth: w.iconsRight,
      gap,
      mainGap,
      minSpacer: MIN_SPACER,
      slotWidth: SLOT_WIDTH,
      total,
    });
    const pinned = Math.min(3, total);
    setStacked(next.mode === 'icons' && next.visible < pinned);
    next.visible = Math.max(pinned, next.visible);
    setLayout((prev) => (prev.mode === next.mode && prev.visible === next.visible ? prev : next));
  }, [total]);

  // 每次渲染后都校准（幂等：结果相同不触发 setState），保证模式切换后补测到图标模式右组宽度。
  useLayoutEffect(() => {
    recompute();
  });

  useEffect(() => {
    const root = rootRef.current;
    if (!root || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() => recompute());
    observer.observe(root);
    if (rightRef.current) observer.observe(rightRef.current);
    return () => observer.disconnect();
  }, [recompute]);

  const closeMenu = useCallback((restoreFocus: boolean) => {
    setOpen(false);
    if (restoreFocus) {
      const trigger = moreRef.current;
      moreRef.current?.focus();
      if (!trigger) {
        const buttons = Array.from(rootRef.current?.querySelectorAll<HTMLButtonElement>('.timeline-toolbar-action:not(:disabled)') ?? []);
        const action = buttons.find((button) => button.dataset.toolbarAction === focusedActionRef.current) ?? buttons[0];
        action?.focus();
      }
    }
    focusedActionRef.current = null;
  }, []);

  // 扩宽后更多入口会卸载，回焦到对应内联工具。
  useLayoutEffect(() => {
    if (open && overflow.length === 0) closeMenu(focusedActionRef.current !== null);
  }, [open, overflow.length, closeMenu]);

  // 定位：落在视口内，下方放不下时翻到上方。
  useLayoutEffect(() => {
    if (!open) {
      setMenuPos(null);
      return;
    }
    const trigger = moreRef.current;
    const menu = menuRef.current;
    if (!trigger || !menu) return;
    const margin = 8;
    const rect = trigger.getBoundingClientRect();
    const menuRect = menu.getBoundingClientRect();
    const below = window.innerHeight - rect.bottom - margin - 4;
    const above = rect.top - margin - 4;
    const placeAbove = menuRect.height > below && above > below;
    const room = Math.max(96, placeAbove ? above : below);
    const height = Math.min(menuRect.height, room);
    setMenuPos({
      left: Math.max(margin, Math.min(rect.left, window.innerWidth - menuRect.width - margin)),
      top: placeAbove ? rect.top - 4 - height : rect.bottom + 4,
      maxHeight: room,
    });
  }, [open, overflow.length]);

  // 打开后聚焦第一项。
  useEffect(() => {
    if (!open || !menuPos) return;
    menuRef.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
  }, [open, menuPos]);

  const onMenuKeyDown = useCallback((event: KeyboardEvent) => {
    const items = Array.from(menuRef.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? []);
    if (items.length === 0) return;
    const index = items.indexOf(document.activeElement as HTMLButtonElement);
    let target: HTMLButtonElement | undefined;
    if (event.key === 'ArrowDown') target = items[(index + 1) % items.length];
    else if (event.key === 'ArrowUp') target = items[(index - 1 + items.length) % items.length];
    else if (event.key === 'Home') target = items[0];
    else if (event.key === 'End') target = items[items.length - 1];
    else if (event.key === 'Tab') {
      closeMenu(true);
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      items[index]?.click();
    }
    if (target) {
      event.preventDefault();
      target.focus();
    }
  }, [closeMenu]);

  // Esc / 外部点击 / 窗口变化：只关闭本菜单，Esc 不再向其他面板冒泡。
  useEffect(() => {
    if (!open) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation();
        closeMenu(true);
        return;
      }
      const target = event.target as Node | null;
      if (target && menuRef.current?.contains(target)) {
        event.stopPropagation();
        event.stopImmediatePropagation();
        onMenuKeyDown(event);
      }
    };
    const onPointerDown = (event: PointerEvent) => {
      const target = event.target as Node | null;
      if (target && (menuRef.current?.contains(target) || moreRef.current?.contains(target))) return;
      closeMenu(false);
    };
    const onResize = () => closeMenu(Boolean(menuRef.current?.contains(document.activeElement)));
    window.addEventListener('keydown', onKeyDown, true);
    document.addEventListener('pointerdown', onPointerDown, true);
    window.addEventListener('resize', onResize);
    return () => {
      window.removeEventListener('keydown', onKeyDown, true);
      document.removeEventListener('pointerdown', onPointerDown, true);
      window.removeEventListener('resize', onResize);
    };
  }, [open, closeMenu, onMenuKeyDown]);

  const portalHost = (rootRef.current?.closest('.editor-shell') as HTMLElement | null) ?? document.body;

  return (
    <div
      ref={rootRef}
      className={`timeline-toolbar is-${layout.mode}${stacked ? ' is-stacked' : ''}`}
      data-mode={layout.mode}
      data-overflow={overflow.length > 0 ? '1' : '0'}
    >
      <div ref={mainRef} className="timeline-toolbar-main">
        {inline.map((action) => (
          <span key={action.id} className="timeline-toolbar-slot">
            {action.dividerBefore ? <span className="timeline-tool-divider" aria-hidden="true" /> : null}
            <ActionButton action={action} iconOnly={iconOnly} />
          </span>
        ))}
        {overflow.length > 0 ? (
          <button
            ref={moreRef}
            type="button"
            className={`pr-tool timeline-toolbar-more${open ? ' is-active' : ''}`}
            title="更多工具"
            aria-label="更多工具"
            aria-haspopup="menu"
            aria-expanded={open}
            aria-controls={open ? menuId : undefined}
            onClick={() => setOpen((value) => !value)}
            onKeyDown={(event) => {
              if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') event.stopPropagation();
              if (event.key === 'ArrowDown' && !open) {
                event.preventDefault();
                setOpen(true);
              }
            }}
          >
            <Ellipsis size={13} />
          </button>
        ) : null}
      </div>
      <span className="timeline-toolbar-spacer">{hint}</span>
      <div ref={rightRef} className="timeline-toolbar-right">
        {children}
      </div>
      {open && overflow.length > 0
        ? createPortal(
            <div
              ref={menuRef}
              id={menuId}
              className="timeline-more-menu"
              role="menu"
              aria-label="更多时间线工具"
              onFocusCapture={(event) => {
                focusedActionRef.current = (event.target as HTMLElement).closest<HTMLElement>('[data-toolbar-action]')?.dataset.toolbarAction ?? null;
              }}
              onBlurCapture={(event) => {
                if (!event.currentTarget.contains(event.relatedTarget)) focusedActionRef.current = null;
              }}
              style={{
                left: menuPos?.left ?? 0,
                top: menuPos?.top ?? 0,
                maxHeight: menuPos?.maxHeight,
                visibility: menuPos ? 'visible' : 'hidden',
              }}
            >
              {overflow.map((action) => (
                <button
                  key={action.id}
                  type="button"
                  role="menuitem"
                  data-toolbar-action={action.id}
                  className={action.active ? 'is-active' : undefined}
                  aria-disabled={action.disabled || undefined}
                  title={action.title}
                  tabIndex={-1}
                  onClick={() => {
                    if (action.disabled) return;
                    closeMenu(true);
                    action.onClick();
                  }}
                >
                  {action.icon}
                  <span>{action.label}</span>
                  {action.keyHint ? <kbd>{action.keyHint}</kbd> : null}
                </button>
              ))}
            </div>,
            portalHost,
          )
        : null}
    </div>
  );
}
