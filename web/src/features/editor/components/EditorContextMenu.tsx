import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

export type ContextMenuItem =
  | { kind: 'separator' }
  | { kind: 'heading'; label: string }
  | {
      kind: 'item';
      label: string;
      shortcut?: string;
      disabled?: boolean;
      /** 禁用原因，作为 title 提示，避免只有灰掉的不可用状态。 */
      disabledReason?: string;
      checked?: boolean;
      danger?: boolean;
      onSelect?: () => void;
    };

interface EditorContextMenuProps {
  x: number;
  y: number;
  title?: string;
  ariaLabel?: string;
  items: ContextMenuItem[];
  onClose: () => void;
}

/**
 * 统一的编辑器右键菜单：复用时间线片段菜单的视觉类，点击外部/Esc 关闭，
 * 菜单项越界时自动收进视口。所有调用方只需要维护「菜单坐标」这一份局部状态。
 */
export function EditorContextMenu({ x, y, title, ariaLabel, items, onClose }: EditorContextMenuProps) {
  const menuRef = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState({ left: x, top: y });

  useLayoutEffect(() => {
    const node = menuRef.current;
    if (!node) return;
    const rect = node.getBoundingClientRect();
    const margin = 8;
    setPosition({
      left: Math.max(margin, Math.min(x, window.innerWidth - rect.width - margin)),
      top: Math.max(margin, Math.min(y, window.innerHeight - rect.height - margin)),
    });
  }, [x, y, items]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      event.stopPropagation();
      onClose();
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [onClose]);

  const runItem = (action: () => void) => {
    onClose();
    action();
  };

  return createPortal(
    <div
      className="timeline-context-layer"
      onPointerDown={onClose}
      onContextMenu={(event) => event.preventDefault()}
    >
      <div
        ref={menuRef}
        className="timeline-context-menu"
        role="menu"
        aria-label={ariaLabel ?? title ?? '右键菜单'}
        style={{ left: position.left, top: position.top }}
        onPointerDown={(event) => event.stopPropagation()}
      >
        {title ? <div className="timeline-context-title">{title}</div> : null}
        {items.map((item, index) => {
          if (item.kind === 'separator') {
            return <span key={`separator-${index}`} className="timeline-context-separator" />;
          }
          if (item.kind === 'heading') {
            return (
              <div key={`heading-${index}`} className="timeline-context-heading">
                {item.label}
              </div>
            );
          }
          return (
            <button
              key={`item-${index}-${item.label}`}
              type="button"
              role="menuitem"
              className={item.danger ? 'is-danger' : undefined}
              disabled={item.disabled}
              title={item.disabled ? item.disabledReason ?? '当前不可用' : undefined}
              aria-checked={item.checked}
              onClick={() => {
                if (item.disabled) return;
                runItem(item.onSelect ?? (() => undefined));
              }}
            >
              <span>{item.label}</span>
              {item.checked ? <span className="menu-check">✓</span> : null}
              {!item.checked && item.shortcut ? <kbd>{item.shortcut}</kbd> : null}
            </button>
          );
        })}
      </div>
    </div>,
    document.body,
  );
}
