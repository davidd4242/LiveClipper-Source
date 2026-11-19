import { Fragment } from 'react';
import { ArrowLeft, ChevronRight } from 'lucide-react';
import { formatTimecode } from '../project';
import type { NestedBreadcrumbItem } from '../nested';

export interface NestedSequenceBarProps {
  /** 父序列标题（如「序列 01」）。 */
  rootLabel: string;
  /** 从主序列进入的嵌套路径（v1 只读预览只有一层）。 */
  breadcrumbs: NestedBreadcrumbItem[];
  duration: number;
  fps: number;
  itemCount: number;
  onBack: () => void;
}

/**
 * 嵌套序列只读预览的面包屑条。
 * v1 边界：一级嵌套、子序列片段不可编辑；此组件只用 props，不持有状态。
 */
export function NestedSequenceBar({
  rootLabel,
  breadcrumbs,
  duration,
  fps,
  itemCount,
  onBack,
}: NestedSequenceBarProps) {
  const current = breadcrumbs[breadcrumbs.length - 1];
  return (
    <div className="nested-sequence-bar" role="navigation" aria-label="嵌套序列导航">
      <div className="nested-sequence-breadcrumb">
        <span className="nested-sequence-crumb">{rootLabel}</span>
        {breadcrumbs.map((crumb) => (
          <Fragment key={crumb.id}>
            <ChevronRight className="nested-sequence-sep" size={12} aria-hidden="true" />
            <span className="nested-sequence-crumb is-current" aria-current="true">
              {crumb.name}
            </span>
          </Fragment>
        ))}
        <span className="nested-sequence-readonly">只读预览</span>
      </div>
      <span className="nested-sequence-meta">
        {current ? `${current.name} · ` : ''}
        {itemCount} 个片段 · {formatTimecode(duration, fps > 0 ? fps : 30)}
      </span>
      <button
        type="button"
        className="nested-sequence-back"
        onClick={onBack}
        title="返回主序列（Esc）"
      >
        <ArrowLeft size={13} aria-hidden="true" />
        返回主序列
        <kbd>Esc</kbd>
      </button>
    </div>
  );
}
