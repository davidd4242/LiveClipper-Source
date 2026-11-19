import { useEffect, useRef, useState } from 'react';
import { Download } from 'lucide-react';
import {
  buildEditorMenus,
  type MenuBarProps,
  type MenuEntry,
  type TimelineMenuAction,
} from '../shell/editorMenus';

export type { MenuBarProps, TimelineMenuAction };

function clockLabel(): string {
  return new Date().toLocaleTimeString('zh-CN', { hour12: false });
}

/**
 * 旧版 34px 菜单条（兼容保留）。
 *
 * P0 之后编辑器默认渲染 EditorTopbar（48px），本组件不再出现在页面里；
 * 仍保留旧 JSX 结构，便于回退与样式对照。菜单数据与 EditorTopbar 共用
 * `buildEditorMenus`，保证两边功能定义一致。
 */
export function MenuBar(props: MenuBarProps) {
  const {
    onExport,
    exportDisabled = false,
    workspaceName,
  } = props;
  const menus = buildEditorMenus(props);
  const [clock, setClock] = useState(clockLabel);
  const [openMenu, setOpenMenu] = useState<string | null>(null);
  const navRef = useRef<HTMLElement>(null);

  useEffect(() => {
    const timer = window.setInterval(() => setClock(clockLabel()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (!openMenu) return undefined;
    const close = (event: PointerEvent) => {
      if (navRef.current?.contains(event.target as Node)) return;
      setOpenMenu(null);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      // Keep Escape from also reaching the editor shortcut handler (which closes the
      // newest panel window) while the menu itself is the thing being dismissed.
      event.stopPropagation();
      setOpenMenu(null);
    };
    document.addEventListener('pointerdown', close);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', close);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [openMenu]);

  const runEntry = (entry: MenuEntry) => {
    if (entry.disabled) return;
    setOpenMenu(null);
    entry.onSelect();
  };

  return (
    <header className={`menu-bar${openMenu ? ' is-menu-open' : ''}`}>
      <div className="brand">
        <span className="brand-mark" aria-hidden="true" />
        <strong>LiveClipper</strong>
        <span className="brand-suffix">EDIT</span>
      </div>
      <nav className="menu-items" aria-label="主菜单" ref={navRef}>
        {menus.map((menu) => (
          <div className="menu-group" key={menu.label}>
            <button
              type="button"
              className={`menu-item${openMenu === menu.label ? ' is-open' : ''}`}
              aria-haspopup="menu"
              aria-expanded={openMenu === menu.label}
              onClick={() => setOpenMenu((current) => (current === menu.label ? null : menu.label))}
              onPointerEnter={() => {
                if (openMenu) setOpenMenu(menu.label);
              }}
            >
              {menu.label}
            </button>
            {openMenu === menu.label ? (
              <div className="menu-dropdown" role="menu" aria-label={menu.label}>
                {menu.entries.map((entry) => (
                  <button
                    key={entry.label}
                    type="button"
                    role="menuitem"
                    className="menu-entry"
                    disabled={entry.disabled}
                    title={entry.title}
                    onClick={() => runEntry(entry)}
                  >
                    <span className="menu-entry-label">
                      <span>{entry.label}</span>
                      {entry.checked ? (
                        <span className="menu-check" aria-hidden="true">
                          ✓
                        </span>
                      ) : null}
                    </span>
                    {entry.shortcut ? <kbd>{entry.shortcut}</kbd> : null}
                  </button>
                ))}
              </div>
            ) : null}
          </div>
        ))}
      </nav>
      <span className="menu-spacer" />
      <button
        type="button"
        className="export-button"
        onClick={onExport}
        disabled={exportDisabled}
        title="导出当前时间线为 MP4"
      >
        <Download size={11} />
        导出影片
      </button>
      <a className="workspace-picker workspace-picker--link" href="/live" title="打开直播监控控制台">
        直播控制台
      </a>
      <span className="workspace-picker" title="工作区（Alt+Shift+1..9 切换，Alt+Shift+0 重置）">
        工作区：{workspaceName}
      </span>
      <span className="clock">{clock}</span>
    </header>
  );
}
