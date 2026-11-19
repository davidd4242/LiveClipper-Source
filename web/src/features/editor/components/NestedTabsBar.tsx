import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent as ReactMouseEvent } from 'react';
import { Clapperboard, CornerDownRight, LayoutGrid, Plus } from 'lucide-react';

/**
 * 序列标签（嵌套序列 v2）。
 *
 * 标签条固定渲染在「01 时间线」面板 tab 行的下一行（28px），由 EditorPage 通过
 * Timeline 的 tabsBar 插槽挂进去：只有 Timeline 知道 panel-head 的确切位置。
 *
 * 标签 = 主序列（primary，始终存在）+ 已打开的嵌套序列（`嵌套:名称`）。
 * 打开/关闭标签只改编辑器会话态，不写工程（冒烟断言 §2.4 #10）。
 */
export interface SequenceTab {
  /** 标签 id：主序列固定为 'primary'，嵌套序列直接用 sequenceId（一个序列只开一个标签）。 */
  id: string;
  /** 嵌套序列在 `project.nestedSequences` 里的 key；主序列为 null。 */
  sequenceId: string | null;
  /** 序列名（不含「嵌套:」前缀）。 */
  name: string;
  /** 父标签 id（主序列为 ''）。 */
  parentId: string;
}

export interface SequenceCrumb {
  tabId: string;
  label: string;
}

export interface NestedTabsBarProps {
  tabs: SequenceTab[];
  activeId: string;
  /** 主序列 → 当前层的面包屑（含当前层）。 */
  breadcrumbs: SequenceCrumb[];
  /** 当前标签有未保存的写入（保存中/失败）。 */
  dirty?: boolean;
  /** 当前嵌套序列在工程里找不到引用（时间线显示空占位）。 */
  missing?: boolean;
  onActivate: (id: string) => void;
  onClose: (id: string) => void;
  onCloseOthers: (id: string) => void;
  onRename: (id: string, name: string) => void;
  onBack: () => void;
  onCreate: () => void;
  onOpenLibrary: () => void;
  onNotify: (message: string) => void;
}

const PRIMARY_ID = 'primary';

const tabLabel = (tab: SequenceTab): string => (tab.id === PRIMARY_ID ? '主序列' : `嵌套:${tab.name}`);

const sameId = (left: string, right: string) => left === right;

export function NestedTabsBar({
  tabs,
  activeId,
  breadcrumbs,
  dirty = false,
  missing = false,
  onActivate,
  onClose,
  onCloseOthers,
  onRename,
  onBack,
  onCreate,
  onOpenLibrary,
  onNotify,
}: NestedTabsBarProps) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const renameInputRef = useRef<HTMLInputElement>(null);
  const [menu, setMenu] = useState<{ id: string; x: number; y: number } | null>(null);
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');

  const singleTab = tabs.length <= 1;

  // 激活标签滚入视野：标签溢出时不让当前序列跑到滚动区外。
  useEffect(() => {
    const container = scrollRef.current;
    if (!container) return;
    const tab = container.querySelector<HTMLElement>(`[data-tab-id="${activeId}"]`);
    if (!tab) return;
    const left = tab.offsetLeft;
    const right = left + tab.offsetWidth;
    if (left < container.scrollLeft) container.scrollLeft = Math.max(0, left - 6);
    else if (right > container.scrollLeft + container.clientWidth) {
      container.scrollLeft = right - container.clientWidth + 6;
    }
  }, [activeId, tabs.length]);

  // 右键菜单：点击别处/Esc 关闭。用冒泡阶段 + 菜单自身 stopPropagation，
  // 避免捕获阶段先卸载菜单、导致菜单项 click 永远不触发。
  useEffect(() => {
    if (!menu) return undefined;
    const close = () => setMenu(null);
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      event.stopPropagation();
      close();
    };
    window.addEventListener('pointerdown', close);
    window.addEventListener('keydown', onKeyDown, true);
    return () => {
      window.removeEventListener('pointerdown', close);
      window.removeEventListener('keydown', onKeyDown, true);
    };
  }, [menu]);

  useEffect(() => {
    if (!renamingId) return;
    const input = renameInputRef.current;
    input?.focus();
    input?.select();
  }, [renamingId]);

  const beginRename = (tab: SequenceTab) => {
    // 主序列是固定名称：只允许改嵌套序列标签。
    if (tab.id === PRIMARY_ID) {
      onNotify('主序列标签名不可修改');
      return;
    }
    setRenamingId(tab.id);
    setRenameValue(tab.name);
  };

  const commitRename = () => {
    const id = renamingId;
    if (!id) return;
    const value = renameValue.trim();
    setRenamingId(null);
    if (value) onRename(id, value);
  };

  const copyName = (tab: SequenceTab) => {
    const label = tabLabel(tab);
    const clipboard = typeof navigator !== 'undefined' ? navigator.clipboard : undefined;
    if (!clipboard?.writeText) {
      onNotify(`无法访问剪贴板，标签名：${label}`);
      return;
    }
    void clipboard
      .writeText(label)
      .then(() => onNotify(`已复制「${label}」`))
      .catch(() => onNotify(`复制失败，标签名：${label}`));
  };

  const onTabKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>, tab: SequenceTab) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onActivate(tab.id);
    }
  };

  const onTabContextMenu = (event: ReactMouseEvent<HTMLDivElement>, tab: SequenceTab) => {
    event.preventDefault();
    event.stopPropagation();
    setMenu({ id: tab.id, x: event.clientX, y: event.clientY });
  };

  const menuTab = menu ? tabs.find((tab) => sameId(tab.id, menu.id)) ?? null : null;

  return (
    <div className="nested-tabs-bar" data-testid="nested-tabs-bar">
      <div
        className="nested-tabs-scroll"
        ref={scrollRef}
        role="tablist"
        aria-label="序列标签"
        data-testid="nested-tabs-scroll"
        onWheel={(event) => {
          const container = event.currentTarget;
          if (container.scrollWidth <= container.clientWidth) return;
          container.scrollLeft += event.deltaY + event.deltaX;
        }}
      >
        {tabs.map((tab) => {
          const active = sameId(tab.id, activeId);
          const primary = sameId(tab.id, PRIMARY_ID);
          const label = tabLabel(tab);
          return (
            <div
              key={tab.id}
              className={`nested-tab${primary ? ' is-primary' : ' is-nested'}${active ? ' is-active' : ''}${
                tab.parentId ? ' is-child' : ''
              }`}
              role="tab"
              aria-selected={active}
              tabIndex={active ? 0 : -1}
              data-tab-id={tab.id}
              data-tab-kind={primary ? 'primary' : 'nested'}
              title={primary ? '主序列' : `嵌套序列：${tab.name}（双击标签可重命名）`}
              onClick={() => {
                if (!active) onActivate(tab.id);
              }}
              onDoubleClick={(event) => {
                if (event.target instanceof HTMLElement && event.target.closest('.nested-tab-close')) return;
                beginRename(tab);
              }}
              onAuxClick={(event) => {
                if (event.button !== 1 || singleTab) return;
                event.preventDefault();
                onClose(tab.id);
              }}
              onContextMenu={(event) => onTabContextMenu(event, tab)}
              onKeyDown={(event) => onTabKeyDown(event, tab)}
            >
              <span className="nested-tab-icon" aria-hidden="true">
                {primary ? <Clapperboard size={12} /> : <CornerDownRight size={12} />}
              </span>
              {sameId(renamingId ?? '', tab.id) ? (
                <input
                  ref={renameInputRef}
                  className="nested-tab-rename"
                  value={renameValue}
                  aria-label="标签名称"
                  onChange={(event) => setRenameValue(event.target.value)}
                  onBlur={commitRename}
                  onPointerDown={(event) => event.stopPropagation()}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter') {
                      event.preventDefault();
                      event.stopPropagation();
                      commitRename();
                      return;
                    }
                    if (event.key === 'Escape') {
                      event.preventDefault();
                      event.stopPropagation();
                      setRenamingId(null);
                    }
                  }}
                />
              ) : (
                <span className="nested-tab-name">{label}</span>
              )}
              {dirty && active ? (
                <span className="nested-tab-dirty" title="有未保存的更改" aria-label="有未保存的更改" />
              ) : null}
              <button
                type="button"
                className="nested-tab-close"
                aria-label={`关闭 ${label}`}
                title={singleTab ? '主序列标签不能关闭' : `关闭 ${label}`}
                disabled={singleTab}
                onClick={(event) => {
                  event.stopPropagation();
                  onClose(tab.id);
                }}
                onPointerDown={(event) => event.stopPropagation()}
              >
                ×
              </button>
            </div>
          );
        })}
      </div>

      <div className="nested-tabs-meta">
        {missing ? (
          <span className="nested-tabs-missing" role="status" title="工程里找不到这个子序列的引用">
            子序列内容未加载
          </span>
        ) : null}
        <nav className="nested-tabs-breadcrumb" aria-label="嵌套层级" data-testid="nested-breadcrumb">
          {breadcrumbs.map((crumb, index) => (
            <span className="nested-crumb-item" key={crumb.tabId}>
              {index > 0 ? (
                <span className="nested-tabs-sep" aria-hidden="true">
                  ›
                </span>
              ) : null}
              <button
                type="button"
                className={`nested-crumb${sameId(crumb.tabId, activeId) ? ' is-current' : ''}`}
                aria-current={sameId(crumb.tabId, activeId) ? 'page' : undefined}
                onClick={() => {
                  if (!sameId(crumb.tabId, activeId)) onActivate(crumb.tabId);
                }}
              >
                {crumb.label}
              </button>
            </span>
          ))}
        </nav>
        <button
          type="button"
          className="nested-tabs-add"
          title="新建序列（打开序列库）"
          aria-label="新建序列"
          onClick={onCreate}
        >
          <Plus size={12} />
        </button>
        <button
          type="button"
          className="nested-tabs-all"
          title="全部序列（打开序列库）"
          onClick={onOpenLibrary}
        >
          <LayoutGrid size={11} />
          <span>全部序列</span>
        </button>
      </div>

      {menu && menuTab ? (
        <div
          className="nested-tabs-menu"
          role="menu"
          style={{ left: menu.x, top: menu.y }}
          onPointerDown={(event) => event.stopPropagation()}
          onContextMenu={(event) => event.preventDefault()}
        >
          <div className="nested-tabs-menu-title">{tabLabel(menuTab)}</div>
          <button
            type="button"
            role="menuitem"
            onClick={() => {
              const tab = menuTab;
              setMenu(null);
              beginRename(tab);
            }}
          >
            重命名
          </button>
          <button
            type="button"
            role="menuitem"
            disabled={singleTab}
            onClick={() => {
              setMenu(null);
              onClose(menuTab.id);
            }}
          >
            关闭
          </button>
          <button
            type="button"
            role="menuitem"
            disabled={menuTab.id === PRIMARY_ID && singleTab}
            onClick={() => {
              setMenu(null);
              onCloseOthers(menuTab.id);
            }}
          >
            关闭其他
          </button>
          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setMenu(null);
              copyName(menuTab);
            }}
          >
            复制名称
          </button>
        </div>
      ) : null}

      {/* onBack 保留给键盘/辅助技术：Esc 与 Alt+← 在 EditorPage 的 window 监听里处理。 */}
      <button type="button" className="nested-tabs-back-proxy" tabIndex={-1} aria-hidden="true" onClick={onBack} />
    </div>
  );
}
