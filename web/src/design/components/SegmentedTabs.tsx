import { useId, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { cn } from '../cn';
import './SegmentedTabs.css';

export interface SegmentedTabItem<T extends string = string> {
  value: T;
  label: ReactNode;
  /** 可选计数徽标。 */
  count?: number;
  disabled?: boolean;
}

export interface SegmentedTabsProps<T extends string = string> {
  items: readonly SegmentedTabItem<T>[];
  value: T;
  onChange: (value: T) => void;
  /** tablist 的无障碍名称。 */
  ariaLabel: string;
  /**
   * 可选：关联的 tabpanel 元素 id。提供后每个 tab 会获得 `id` 与 `aria-controls`，
   * 调用方应渲染 `<SegmentedTabPanel id={panelId} tabId={...} />`（或同构的
   * `role="tabpanel"` 容器）；不传时仅通过 tablist 的 `aria-label` 建立语义。
   */
  panelId?: string;
  size?: 'sm' | 'md';
  className?: string;
}

/** tab 元素的 id，供 `<SegmentedTabPanel tabId={...} />` 的 aria-labelledby 使用。 */
export function segmentedTabId(panelId: string, value: string): string {
  return `${panelId}-tab-${value}`;
}

export interface SegmentedTabPanelProps {
  /** 与 `SegmentedTabs` 的 `panelId` 相同，tab 的 `aria-controls` 指向它。 */
  id: string;
  /** 当前选中 tab 的 id（`segmentedTabId(panelId, value)`）。 */
  tabId: string;
  children?: ReactNode;
  /** 由调用方按选中值隐藏未激活面板，例如 `hidden={value !== 'all'}`。 */
  hidden?: boolean;
  className?: string;
}

/**
 * 与 `SegmentedTabs` 配套的 tabpanel 容器；不强制使用，自定义同构容器也可。
 * 面板内含可聚焦元素时，浏览器会先聚焦内部元素，此处的 tabIndex 只是兜底。
 */
export function SegmentedTabPanel({
  id,
  tabId,
  children,
  hidden,
  className,
}: SegmentedTabPanelProps) {
  return (
    <div
      id={id}
      role="tabpanel"
      aria-labelledby={tabId}
      hidden={hidden}
      tabIndex={0}
      className={cn('lc-segmented-panel', className)}
    >
      {children}
    </div>
  );
}

/**
 * 分段控件；遵循 WAI-ARIA Tabs 模式：
 * - 只有一个 tab 进入 Tab 序列（roving tabindex），其余用 ←/→/Home/End 切换；
 * - 方向键跳过 disabled 项并直接激活（automatic activation）；
 * - 传 `panelId` 时补 `aria-controls`，面板用 `SegmentedTabPanel` 关联。
 */
export function SegmentedTabs<T extends string>({
  items,
  value,
  onChange,
  ariaLabel,
  panelId,
  size = 'md',
  className,
}: SegmentedTabsProps<T>) {
  const generatedId = useId().replace(/[^a-zA-Z0-9_-]/g, '');
  const buttonRefs = useRef<Array<HTMLButtonElement | null>>([]);

  const selectedIndex = items.findIndex((item) => item.value === value);
  const selectedIsDisabled = selectedIndex >= 0 && Boolean(items[selectedIndex]?.disabled);
  const firstEnabledIndex = items.findIndex((item) => !item.disabled);
  const rovingIndex = selectedIndex >= 0 && !selectedIsDisabled ? selectedIndex : firstEnabledIndex;

  const activate = (index: number) => {
    const target = items[index];
    if (!target || target.disabled) return;
    buttonRefs.current[index]?.focus();
    if (target.value !== value) onChange(target.value);
  };

  const moveFocus = (from: number, delta: 1 | -1) => {
    if (items.length === 0) return;
    let index = from;
    for (let step = 0; step < items.length; step += 1) {
      index = (index + delta + items.length) % items.length;
      if (!items[index]?.disabled) {
        activate(index);
        return;
      }
    }
  };

  const moveToEdge = (edge: 'start' | 'end') => {
    if (edge === 'start') {
      const index = items.findIndex((item) => !item.disabled);
      if (index >= 0) activate(index);
      return;
    }
    for (let index = items.length - 1; index >= 0; index -= 1) {
      if (!items[index]?.disabled) {
        activate(index);
        return;
      }
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    switch (event.key) {
      case 'ArrowRight':
        event.preventDefault();
        moveFocus(index, 1);
        return;
      case 'ArrowLeft':
        event.preventDefault();
        moveFocus(index, -1);
        return;
      case 'Home':
        event.preventDefault();
        moveToEdge('start');
        return;
      case 'End':
        event.preventDefault();
        moveToEdge('end');
        return;
      default:
        return;
    }
  };

  return (
    <div
      role="tablist"
      aria-label={ariaLabel}
      aria-orientation="horizontal"
      className={cn('lc-segmented', `lc-segmented--${size}`, className)}
    >
      {items.map((item, index) => {
        const selected = item.value === value;
        const tabId = panelId
          ? segmentedTabId(panelId, item.value)
          : `${generatedId}-tab-${item.value}`;
        return (
          <button
            key={item.value}
            ref={(node) => {
              buttonRefs.current[index] = node;
            }}
            type="button"
            role="tab"
            id={tabId}
            aria-selected={selected}
            aria-controls={panelId}
            tabIndex={index === rovingIndex ? 0 : -1}
            disabled={item.disabled}
            className={cn('lc-segmented__item', selected && 'is-active')}
            onClick={() => onChange(item.value)}
            onKeyDown={(event) => handleKeyDown(event, index)}
          >
            <span className="lc-segmented__label">{item.label}</span>
            {typeof item.count === 'number' ? (
              <span className="lc-segmented__count">{item.count}</span>
            ) : null}
          </button>
        );
      })}
    </div>
  );
}
