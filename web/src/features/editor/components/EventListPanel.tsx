import { useEffect, useMemo, useRef, useState } from 'react';
import { Ban, Check, Inbox, ListChecks, MapPin, Sparkles, X } from 'lucide-react';
import { EmptyState, StatusDot } from '../../../design';
import type { EditorProject, StudioEvent } from '../../../api/types';
import { formatClock, formatTimecode } from '../project';
import {
  buildEventViews,
  eventDecisionLabel,
  eventDotTone,
  eventSourceLabel,
  type EditorEventView,
} from '../events';
import './EventListPanel.css';

export interface EventListPanelProps {
  open: boolean;
  events: StudioEvent[];
  project: EditorProject | null;
  fps: number;
  /** 与 AI 抽屉共用的「采用」集合（EditorPage 的 adopted state）。 */
  adopted: Set<string>;
  /** 列表侧的「排除」集合（暂停/舍弃的候选）。 */
  excluded: Set<string>;
  /** 播放头当前命中的事件（由 EditorPage 计算）。 */
  activeEventId: string | null;
  eventsBusy: boolean;
  onClose: () => void;
  /** 定位播放头并选中对应片段。 */
  onLocateEvent: (event: EditorEventView) => void;
  onToggleAdopt: (eventId: string) => void;
  onToggleExclude: (eventId: string) => void;
  onApply: () => void;
  onExtract: () => void;
}

/**
 * B 端事件列表侧栏（编辑器右侧面板，约 320px）。
 *
 * 分工：AI 抽屉负责「生成/整理」候选，本面板负责「看得见、点得动、能应用」；
 * 两者通过 EditorPage 的 `events` / `adopted` 状态同步，面板内部不取数据、不写工程。
 */
export function EventListPanel({
  open,
  events,
  project,
  fps,
  adopted,
  excluded,
  activeEventId,
  eventsBusy,
  onClose,
  onLocateEvent,
  onToggleAdopt,
  onToggleExclude,
  onApply,
  onExtract,
}: EventListPanelProps) {
  const views = useMemo(
    () => buildEventViews(events, project, adopted, excluded),
    [events, project, adopted, excluded],
  );
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const rowRefs = useRef(new Map<string, HTMLButtonElement>());
  const listRef = useRef<HTMLUListElement>(null);

  // 视图变化时保持选中项有效：优先保留选中，其次跟随播放头命中，最后取第一条。
  useEffect(() => {
    setSelectedId((current) => {
      if (current && views.some((view) => view.id === current)) return current;
      if (activeEventId && views.some((view) => view.id === activeEventId)) return activeEventId;
      return views[0]?.id ?? null;
    });
  }, [views, activeEventId]);

  // 收起动画期间仍需渲染节点（CSS 把它移出视口），不能提前 return null。

  const selected = views.find((view) => view.id === selectedId) ?? null;
  const mockAdopted = views.some((view) => view.mock && adopted.has(view.id));
  const anyMock = views.some((view) => view.mock);
  const canApply = adopted.size > 0 && !mockAdopted;

  const focusRow = (index: number) => {
    const view = views[index];
    if (!view) return;
    setSelectedId(view.id);
    rowRefs.current.get(view.id)?.focus();
  };

  const handleListKeyDown = (event: React.KeyboardEvent<HTMLUListElement>) => {
    const index = views.findIndex((view) => view.id === selectedId);
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const next = event.key === 'ArrowDown' ? index + 1 : index - 1;
      focusRow(Math.max(0, Math.min(views.length - 1, next < 0 ? 0 : next)));
    } else if (event.key === 'Home') {
      event.preventDefault();
      focusRow(0);
    } else if (event.key === 'End') {
      event.preventDefault();
      focusRow(views.length - 1);
    }
  };

  return (
    <aside
      className={`event-panel${open ? ' is-open' : ''}`}
      role="complementary"
      aria-label="事件列表"
      aria-hidden={!open}
      inert={!open}
      data-canvas-theme="dark"
      onKeyDown={(event) => {
        if (event.key !== 'Escape') return;
        // 面板内 Escape 只收起面板，不再触发编辑器的逐层关闭。
        event.stopPropagation();
        onClose();
      }}
    >
      <header className="event-panel__head">
        <div className="event-panel__heading">
          <span className="event-panel__eyebrow">
            <ListChecks size={13} aria-hidden="true" />
            事件列表 · B 端
          </span>
          <h2 className="event-panel__title">
            {views.length} 个候选
            <span className="event-panel__counts">
              已选 {adopted.size}
              {excluded.size > 0 ? ` · 排除 ${excluded.size}` : ''}
            </span>
          </h2>
        </div>
        <button type="button" className="event-panel__icon-btn" aria-label="收起事件列表" title="收起事件列表（Esc）" onClick={onClose}>
          <X size={15} />
        </button>
      </header>

      {anyMock ? (
        <p className="event-panel__notice" role="note">
          当前为示例数据：后端 auto-plan 事件接口未接入，列表由时间线派生；示例事件不可应用到时间线。
        </p>
      ) : null}

      {views.length === 0 ? (
        <div className="event-panel__empty">
          <EmptyState
            compact
            icon={<Inbox size={22} />}
            title="还没有事件"
            description="在 AI 抽屉里从字幕提取候选，或等真实 auto-plan 事件接入后在这里查看、勾选并应用。"
            action={
              <button type="button" className="event-panel__btn" onClick={onExtract} disabled={eventsBusy}>
                <Sparkles size={13} aria-hidden="true" />
                {eventsBusy ? '提取中…' : '从字幕提取候选'}
              </button>
            }
          />
        </div>
      ) : (
        <>
          <ul className="event-panel__list" ref={listRef} aria-label="候选事件" onKeyDown={handleListKeyDown}>
            {views.map((view) => {
              const isSelected = view.id === selectedId;
              const isAdopted = view.decision === 'adopted';
              return (
                <li key={view.id} className={`event-panel__item${isSelected ? ' is-selected' : ''}`}>
                  <button
                    type="button"
                    ref={(node) => {
                      if (node) rowRefs.current.set(view.id, node);
                      else rowRefs.current.delete(view.id);
                    }}
                    className="event-panel__row"
                    aria-current={isSelected ? 'true' : undefined}
                    title="定位播放头并选中片段"
                    onClick={() => {
                      setSelectedId(view.id);
                      onLocateEvent(view);
                    }}
                  >
                    <StatusDot
                      tone={eventDotTone(view.decision)}
                      size="sm"
                      label={eventDecisionLabel(view.decision)}
                    />
                    <span className="event-panel__time">{formatTimecode(view.start, fps)}</span>
                    <span className="event-panel__title-text">{view.title || '未命名事件'}</span>
                    {view.mock ? <span className="event-panel__mock">示例</span> : null}
                    <span className="event-panel__summary">{view.summary || '—'}</span>
                  </button>
                  <button
                    type="button"
                    className={`event-panel__adopt${isAdopted ? ' is-adopted' : ''}`}
                    aria-pressed={isAdopted}
                    aria-label={`${isAdopted ? '取消采用' : '采用'}「${view.title || '未命名事件'}」`}
                    title={
                      view.mock
                        ? '示例事件不能写入时间线（真实事件接入后可采用）'
                        : isAdopted
                          ? '取消采用（应用前可撤销）'
                          : '采用（多选后一次性应用到时间线）'
                    }
                    disabled={view.mock}
                    onClick={() => onToggleAdopt(view.id)}
                  >
                    <Check size={13} />
                  </button>
                </li>
              );
            })}
          </ul>

          <section className="event-panel__detail" aria-label="事件详情">
            {selected ? (
              <>
                <h3 className="event-panel__detail-title">{selected.title || '未命名事件'}</h3>
                <dl className="event-panel__meta">
                  <div>
                    <dt>区间</dt>
                    <dd>
                      {formatTimecode(selected.start, fps)} → {formatTimecode(selected.end, fps)}
                      <span className="event-panel__duration">（{formatClock(Math.max(0, selected.end - selected.start))}）</span>
                    </dd>
                  </div>
                  <div>
                    <dt>来源</dt>
                    <dd>
                      {eventSourceLabel(selected.source)}
                      {selected.mock ? ' · 示例' : ''}
                    </dd>
                  </div>
                  <div>
                    <dt>弹幕热度</dt>
                    <dd title="等 auto-plan / 事件 API 返回 heat 后接入">{selected.heat === null ? '—（待接 auto-plan）' : selected.heat}</dd>
                  </div>
                  <div>
                    <dt>理由</dt>
                    <dd>{selected.reasons.length > 0 ? selected.reasons.join('；') : '—（待接 auto-plan）'}</dd>
                  </div>
                </dl>
                <p className="event-panel__summary-full">{selected.summary || '暂无摘要'}</p>
                <div className="event-panel__actions">
                  <button
                    type="button"
                    className={`event-panel__btn${selected.decision === 'adopted' ? ' is-active' : ''}`}
                    aria-pressed={selected.decision === 'adopted'}
                    title={selected.mock ? '示例事件不能写入时间线' : undefined}
                    disabled={selected.mock}
                    onClick={() => onToggleAdopt(selected.id)}
                  >
                    <Check size={13} aria-hidden="true" />
                    {selected.decision === 'adopted' ? '取消采用' : '采用'}
                  </button>
                  <button
                    type="button"
                    className={`event-panel__btn event-panel__btn--danger${selected.decision === 'excluded' ? ' is-active' : ''}`}
                    aria-pressed={selected.decision === 'excluded'}
                    onClick={() => onToggleExclude(selected.id)}
                  >
                    <Ban size={13} aria-hidden="true" />
                    {selected.decision === 'excluded' ? '恢复待定' : '排除'}
                  </button>
                  <button type="button" className="event-panel__btn" onClick={() => onLocateEvent(selected)}>
                    <MapPin size={13} aria-hidden="true" />
                    定位
                  </button>
                </div>
              </>
            ) : (
              <p className="event-panel__hint">在列表中选择一个事件查看摘要、理由与区间。</p>
            )}
          </section>

          <footer className="event-panel__foot">
            <button
              type="button"
              className="event-panel__apply"
              disabled={!canApply}
              title={
                mockAdopted
                  ? '示例事件不能写入时间线；接入真实事件后可用'
                  : adopted.size === 0
                    ? '先在列表里采用至少一个事件'
                    : '把采用的候选加入时间线（走现有候选采用逻辑）'
              }
              onClick={onApply}
            >
              应用到时间线{adopted.size > 0 ? `（${adopted.size}）` : ''}
            </button>
            <button type="button" className="event-panel__btn" onClick={onExtract} disabled={eventsBusy}>
              <Sparkles size={13} aria-hidden="true" />
              {eventsBusy ? '提取中…' : '从字幕提取'}
            </button>
          </footer>
        </>
      )}
    </aside>
  );
}
