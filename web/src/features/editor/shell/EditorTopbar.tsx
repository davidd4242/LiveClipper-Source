import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import {
  ChevronLeft,
  Download,
  Film,
  ListChecks,
  Menu as MenuIcon,
  Redo2,
  Settings2,
  Undo2,
} from 'lucide-react';
import { savedLabel, saveStateTitle, type EditorSaveState } from './editorChrome';
import { buildEditorMenus, type MenuBarProps, type MenuEntry } from './editorMenus';
import './EditorTopbar.css';

export interface EditorTopbarProps extends MenuBarProps {
  /** 项目标题（当前素材名，或「未选择素材」）。 */
  projectName: string;
  /** 序列名（嵌套时显示子序列名）。 */
  sequenceName: string;
  saveState: EditorSaveState;
  lastSavedAt: Date | null;
  /** 返回工作台（/studio）。 */
  onBack: () => void;
  /** 事件列表侧栏是否展开。 */
  eventsOpen?: boolean;
  /** 切换事件列表侧栏（Alt+E）。 */
  onToggleEvents?: () => void;
  onToggleRefinement?: () => void;
  refinementOpen?: boolean;
  refinementDisabled?: boolean;
}

/**
 * 编辑器外壳顶栏（P0）：48px 单行。
 *
 * - 原来的 34px 菜单条被替换：撤销/重做/序列设置/导出外露，
 *   其余 10 组菜单收进右侧「菜单」溢出面板（数据来自 buildEditorMenus，功能不丢）。
 * - 外壳与画布统一使用暗色。
 */
export function EditorTopbar(props: EditorTopbarProps) {
  const {
    projectName,
    sequenceName,
    saveState,
    lastSavedAt,
    onBack,
    onExport,
    exportDisabled = false,
    onUndo,
    onRedo,
    canUndo,
    canRedo,
    onSequenceSettings,
    eventsOpen = false,
    onToggleEvents,
    onToggleRefinement,
    refinementOpen = false,
    refinementDisabled = false,
  } = props;

  const menus = buildEditorMenus(props);
  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const entryRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const [focusIndex, setFocusIndex] = useState(0);
  const flatEntries = menus.flatMap((group) => group.entries);

  // 溢出菜单：外部点击关闭、Escape 关闭并回焦触发按钮、Alt+M 切换。
  useEffect(() => {
    const close = (event: PointerEvent) => {
      if (!menuOpen) return;
      const target = event.target as Node;
      if (menuRef.current?.contains(target) || triggerRef.current?.contains(target)) return;
      setMenuOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.isComposing || event.keyCode === 229) return;
      if (event.target instanceof HTMLElement && event.target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])')) return;
      if (event.altKey && event.key.toLowerCase() === 'm') {
        event.preventDefault();
        setMenuOpen((open) => !open);
        return;
      }
      if (event.key !== 'Escape' || !menuOpen) return;
      // 菜单打开时 Escape 只关菜单，不再触发编辑器「逐层关闭面板」的快捷键。
      event.stopPropagation();
      setMenuOpen(false);
      triggerRef.current?.focus();
    };
    document.addEventListener('pointerdown', close);
    document.addEventListener('keydown', onKeyDown, true);
    return () => {
      document.removeEventListener('pointerdown', close);
      document.removeEventListener('keydown', onKeyDown, true);
    };
  }, [menuOpen]);

  // 菜单打开后焦点落到第一个可用项；↑↓/Home/End 在菜单项间循环（Enter/Space 由按钮原生触发）。
  useEffect(() => {
    if (!menuOpen) return undefined;
    const first = entryRefs.current.findIndex((element) => element && !element.disabled);
    if (first < 0) return undefined;
    setFocusIndex(first);
    const frame = window.requestAnimationFrame(() => entryRefs.current[first]?.focus());
    return () => window.cancelAnimationFrame(frame);
  }, [menuOpen]);

  const focusEntryAt = (index: number) => {
    const target = entryRefs.current[index];
    if (!target || target.disabled) return;
    target.focus();
    setFocusIndex(index);
  };

  const handleMenuKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') return; // capture 层统一关闭菜单并还原到触发按钮
    const enabled = flatEntries.reduce<number[]>((indexes, entry, index) => {
      if (!entry.disabled) indexes.push(index);
      return indexes;
    }, []);
    if (enabled.length === 0) return;
    const activeIndex = entryRefs.current.findIndex((element) => element === document.activeElement);
    const position = activeIndex >= 0 ? enabled.indexOf(activeIndex) : enabled.indexOf(focusIndex);
    const safePosition = position >= 0 ? position : 0;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        focusEntryAt(enabled[(safePosition + 1) % enabled.length]);
        break;
      case 'ArrowUp':
        event.preventDefault();
        focusEntryAt(enabled[(safePosition - 1 + enabled.length) % enabled.length]);
        break;
      case 'Home':
        event.preventDefault();
        focusEntryAt(enabled[0]);
        break;
      case 'End':
        event.preventDefault();
        focusEntryAt(enabled[enabled.length - 1]);
        break;
      default:
        break;
    }
  };

  // Alt+E 开关事件列表侧栏（与 Alt+M 菜单同一层快捷键）。
  useEffect(() => {
    if (!onToggleEvents) return undefined;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.isComposing || event.keyCode === 229) return;
      if (event.target instanceof HTMLElement && event.target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])')) return;
      if (!event.altKey || event.key.toLowerCase() !== 'e') return;
      event.preventDefault();
      onToggleEvents();
    };
    document.addEventListener('keydown', onKeyDown, true);
    return () => document.removeEventListener('keydown', onKeyDown, true);
  }, [onToggleEvents]);

  const runEntry = (entry: MenuEntry) => {
    if (entry.disabled) return;
    setMenuOpen(false);
    entry.onSelect();
  };

  const saveTitle = `${saveStateTitle(saveState)}${lastSavedAt ? `（${lastSavedAt.toLocaleTimeString('zh-CN', { hour12: false })}）` : ''}`;

  return (
    <header className="ed-topbar">
      {/* 页面主标题：视觉隐藏，补全全页 0 heading 的结构缺口（P1-7）。 */}
      <h1 className="ed-sr-only">剪辑 · {projectName} · {sequenceName}</h1>
      <div className="ed-topbar__left">
        <button
          type="button"
          className="ed-icon-btn"
          aria-label="返回工作台"
          title="返回工作台"
          onClick={onBack}
        >
          <ChevronLeft size={15} />
        </button>
        <nav className="ed-crumbs" aria-label="当前位置">
          <button type="button" className="ed-crumbs__link" onClick={onBack}>
            剪辑
          </button>
          <span className="ed-crumbs__sep" aria-hidden="true">
            ›
          </span>
          <span className="ed-crumbs__item" title={projectName}>
            {projectName}
          </span>
          <span className="ed-crumbs__sep" aria-hidden="true">
            ›
          </span>
          <span
            className="ed-crumbs__item ed-crumbs__item--current"
            aria-current="page"
            title={sequenceName}
          >
            {sequenceName}
          </span>
        </nav>
        <span className={`ed-save ed-save--${saveState}`} title={saveTitle} aria-live="polite">
          <span className="ed-save__dot" aria-hidden="true" />
          {savedLabel(saveState, lastSavedAt)}
        </span>
      </div>

      <div className="ed-topbar__spacer" />

      <div className="ed-topbar__actions">
        <button
          type="button"
          className="ed-icon-btn"
          aria-label="撤销"
          title={canUndo ? '撤销（Ctrl+Z）' : '没有可撤销的操作'}
          disabled={!canUndo}
          onClick={onUndo}
        >
          <Undo2 size={15} />
        </button>
        <button
          type="button"
          className="ed-icon-btn"
          aria-label="重做"
          title={canRedo ? '重做（Ctrl+Shift+Z）' : '没有可重做的操作'}
          disabled={!canRedo}
          onClick={onRedo}
        >
          <Redo2 size={15} />
        </button>
        <span className="ed-topbar__divider" aria-hidden="true" />
        <button
          type="button"
          className="ed-btn ed-btn--quiet"
          onClick={onSequenceSettings}
          title="序列设置"
        >
          <Settings2 size={14} />
          序列设置
        </button>
        <button
          type="button"
          className="ed-btn ed-btn--quiet"
          id="refinement-toggle"
          onClick={onToggleRefinement}
          disabled={refinementDisabled || !onToggleRefinement}
          aria-expanded={refinementOpen}
          aria-controls="editor-refinement"
          title="打开或关闭原生字幕精修栏"
        >
          <Film size={14} />
          字幕精修
        </button>
        <button
          type="button"
          className="ed-btn ed-btn--primary"
          onClick={onExport}
          disabled={exportDisabled}
          title="导出当前时间线为 MP4"
        >
          <Download size={14} />
          导出
        </button>
        <span className="ed-topbar__divider" aria-hidden="true" />
        <button
          type="button"
          className={`ed-icon-btn${eventsOpen ? ' is-open' : ''}`}
          aria-label="事件列表"
          aria-pressed={eventsOpen}
          aria-keyshortcuts="Alt+E"
          title="事件列表（Alt+E）"
          disabled={!onToggleEvents}
          onClick={onToggleEvents}
        >
          <ListChecks size={15} />
        </button>
        <button
          ref={triggerRef}
          type="button"
          className={`ed-icon-btn ed-menu-trigger${menuOpen ? ' is-open' : ''}`}
          aria-label="菜单"
          aria-haspopup="menu"
          aria-keyshortcuts="Alt+M"
          aria-expanded={menuOpen}
          title="全部菜单（Alt+M；↑↓/Home/End 选择）"
          onClick={() => setMenuOpen((open) => !open)}
        >
          <MenuIcon size={15} />
        </button>
      </div>

      {menuOpen ? (
        <div
          className="ed-menu"
          ref={menuRef}
          role="menu"
          aria-label="编辑器菜单"
          onKeyDown={handleMenuKeyDown}
        >
          {menus.map((group, groupIndex) => {
            const firstIndex = menus
              .slice(0, groupIndex)
              .reduce((total, item) => total + item.entries.length, 0);
            return (
              <section className="ed-menu__group" key={group.label}>
                <h3 className="ed-menu__label">{group.label}</h3>
                <div className="ed-menu__entries">
                  {group.entries.map((entry, entryIndex) => {
                    const flatIndex = firstIndex + entryIndex;
                    return (
                      <button
                        key={entry.label}
                        ref={(element) => {
                          entryRefs.current[flatIndex] = element;
                        }}
                        type="button"
                        role="menuitem"
                        className="ed-menu__entry"
                        tabIndex={flatIndex === focusIndex ? 0 : -1}
                        disabled={entry.disabled}
                        title={entry.title}
                        onClick={() => runEntry(entry)}
                      >
                        <span className="ed-menu__entry-label">
                          <span>{entry.label}</span>
                          {entry.checked ? (
                            <span className="ed-menu__check" aria-hidden="true">
                              ✓
                            </span>
                          ) : null}
                        </span>
                        {entry.shortcut ? <kbd>{entry.shortcut}</kbd> : null}
                      </button>
                    );
                  })}
                </div>
              </section>
            );
          })}
        </div>
      ) : null}
    </header>
  );
}
