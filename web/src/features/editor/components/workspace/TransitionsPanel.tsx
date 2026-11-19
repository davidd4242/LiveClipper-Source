import { useMemo, useState } from 'react';
import type { EditorTransition, TimelineItem } from '../../../../api/types';
import {
  formatTimecode,
  itemOutputDuration,
  itemSequenceStart,
  trackOf,
  TRANSITION_ALIGN_LABELS,
  transitionCuts,
  transitionKindLabel,
  type TransitionDefaults,
} from '../../project';
import { Field, Note, Section } from './ui';
import { fadePercent, fadeSeconds, trackLabel } from './types';

type TransitionKind = 'in' | 'out' | 'both';

const TRANSITIONS: Array<{ kind: TransitionKind; label: string; detail: string }> = [
  { kind: 'in', label: '淡入', detail: '从上黑场渐显画面与声音' },
  { kind: 'out', label: '淡出', detail: '画面与声音渐隐到下黑场' },
  { kind: 'both', label: '淡入淡出', detail: '首尾同时添加淡化' },
];

const CUT_KINDS: Array<{ kind: EditorTransition['kind']; label: string }> = [
  { kind: 'cross-dissolve', label: '交叉淡化' },
  { kind: 'dip-to-black', label: '闪黑' },
  { kind: 'dip-to-white', label: '闪白' },
];

const DURATIONS = [0.3, 0.5, 1, 2];
const KIND_LABELS: Record<TransitionKind, string> = { in: '淡入', out: '淡出', both: '淡入淡出' };

function fadeSummary(item: TimelineItem): string {
  const fadeIn = item.fadeIn ?? 0;
  const fadeOut = item.fadeOut ?? 0;
  if (fadeIn <= 0 && fadeOut <= 0) return '无';
  const parts: string[] = [];
  if (fadeIn > 0) parts.push(`入 ${fadeIn.toFixed(1)}s`);
  if (fadeOut > 0) parts.push(`出 ${fadeOut.toFixed(1)}s`);
  return parts.join(' · ');
}

type TransitionsPanelProps = {
  items: TimelineItem[];
  selectedItem: TimelineItem | null;
  fps: number;
  transitions: EditorTransition[];
  sourceDurations?: Record<string, number>;
  selectedTransitionId: string | null;
  transitionDefaults: TransitionDefaults;
  onChangeTransitionDefaults: (next: TransitionDefaults) => void;
  onSelectTransition: (transitionId: string | null) => void;
  onApplyTransition: (itemId: string | null) => void;
  onRemoveTransition: (transitionId: string) => void;
  onUpdateTransition: (
    transitionId: string,
    patch: Partial<Pick<EditorTransition, 'kind' | 'duration' | 'align'>>,
  ) => void;
  onPatch: (patch: Partial<TimelineItem>, label: string) => void;
  onSeek: (time: number) => void;
  onNotice: (message: string) => void;
};

/**
 * 转场窗口：上半部分编辑剪辑点转场（工程 transitions），下半部分编辑当前片段的首尾淡化。
 * 剪点转场会压缩相邻两段的重叠区，实际生效时长由 project.ts 的 sequenceLayout clamp。
 */
export function TransitionsPanel({
  items,
  selectedItem,
  fps,
  transitions,
  sourceDurations,
  selectedTransitionId,
  transitionDefaults,
  onChangeTransitionDefaults,
  onSelectTransition,
  onApplyTransition,
  onRemoveTransition,
  onUpdateTransition,
  onPatch,
  onSeek,
  onNotice,
}: TransitionsPanelProps) {
  const [duration, setDuration] = useState(0.5);
  const cuts = useMemo(() => transitionCuts(items, transitions, fps, sourceDurations), [items, transitions, fps]);

  const track = selectedItem ? trackOf(selectedItem) : 0;
  const clipLength = selectedItem ? itemOutputDuration(selectedItem) : 0;
  const { fadeIn, fadeOut } = fadeSeconds(selectedItem);
  const { inPct, outPct } = fadePercent(selectedItem);
  const sequenceStart = selectedItem ? itemSequenceStart(items, selectedItem.id, transitions, fps, sourceDurations) : 0;
  const itemIndex = selectedItem ? items.findIndex((item) => item.id === selectedItem.id) : -1;
  const trackItems = selectedItem ? items.filter((item) => trackOf(item) === track) : [];
  const trackPosition = selectedItem ? trackItems.findIndex((item) => item.id === selectedItem.id) : -1;
  const nextItem = trackPosition >= 0 ? trackItems[trackPosition + 1] ?? null : null;

  const applied = useMemo(
    () =>
      items
        .filter((item) => (item.fadeIn ?? 0) > 0 || (item.fadeOut ?? 0) > 0)
        .map((item) => ({ item, start: itemSequenceStart(items, item.id, transitions, fps, sourceDurations), track: trackOf(item) }))
        .sort((a, b) => a.start - b.start),
    [items, transitions, fps],
  );

  const applyTransition = (kind: TransitionKind) => {
    if (!selectedItem) {
      onNotice('先在时间线上选中一个片段，再应用转场');
      return;
    }
    const length = Math.max(0.1, clipLength);
    let nextIn = fadeIn;
    let nextOut = fadeOut;
    if (kind === 'in' || kind === 'both') nextIn = Math.min(duration, Math.max(0, length - nextOut));
    if (kind === 'out' || kind === 'both') nextOut = Math.min(duration, Math.max(0, length - nextIn));
    onPatch(
      { fadeIn: Math.round(nextIn * 100) / 100, fadeOut: Math.round(nextOut * 100) / 100 },
      `应用转场：${KIND_LABELS[kind]} ${duration.toFixed(1)} 秒`,
    );
  };

  const removeTransition = () => {
    if (!selectedItem) {
      onNotice('先在时间线上选中一个片段，再移除转场');
      return;
    }
    onPatch({ fadeIn: 0, fadeOut: 0 }, '移除片段转场');
  };

  return (
    <div className="wsx-stack">
      <Note>
        剪点转场挂在片段入点上，压缩相邻两段的重叠区；片段首尾淡化则是每段自己的淡入/淡出。两层可以同时存在，转场区间内以转场为准。
      </Note>

      <Section
        title="默认转场设置"
        hint={`Ctrl+D 视频 ${transitionDefaults.duration}s · Ctrl+Shift+D 音频 ${transitionDefaults.audioDuration}s`}
      >
        <Note>
          默认值只存一份（本机浏览器 localStorage），Ctrl+D / Ctrl+Shift+D / Shift+D 与「为选中片段添加默认剪点转场」共用它；不是工程数据，换工程不重置。
        </Note>
        <div className="wsx-toolbar">
          <span className="wsx-label">类型</span>
          <div className="wsx-chip-row">
            {CUT_KINDS.map((entry) => (
              <button
                key={entry.kind}
                type="button"
                className={`wsx-chip${transitionDefaults.kind === entry.kind ? ' is-active' : ''}`}
                aria-pressed={transitionDefaults.kind === entry.kind}
                onClick={() => onChangeTransitionDefaults({ ...transitionDefaults, kind: entry.kind })}
              >
                {entry.label}
              </button>
            ))}
          </div>
        </div>
        <div className="wsx-toolbar">
          <span className="wsx-label">视频时长</span>
          <div className="wsx-chip-row">
            {DURATIONS.map((value) => (
              <button
                key={value}
                type="button"
                className={`wsx-chip${Math.abs(transitionDefaults.duration - value) < 0.01 ? ' is-active' : ''}`}
                aria-pressed={Math.abs(transitionDefaults.duration - value) < 0.01}
                onClick={() => onChangeTransitionDefaults({ ...transitionDefaults, duration: value })}
              >
                {value.toFixed(1)}s
              </button>
            ))}
          </div>
        </div>
        <div className="wsx-toolbar">
          <span className="wsx-label">音频时长</span>
          <div className="wsx-chip-row">
            {DURATIONS.map((value) => (
              <button
                key={value}
                type="button"
                className={`wsx-chip${Math.abs(transitionDefaults.audioDuration - value) < 0.01 ? ' is-active' : ''}`}
                aria-pressed={Math.abs(transitionDefaults.audioDuration - value) < 0.01}
                onClick={() => onChangeTransitionDefaults({ ...transitionDefaults, audioDuration: value })}
              >
                {value.toFixed(1)}s
              </button>
            ))}
          </div>
        </div>
      </Section>

      <Section title="当前片段" hint={selectedItem ? `${trackLabel(track)} · 第 ${itemIndex + 1} 个片段` : '未选择'}>
        {selectedItem ? (
          <>
            <div className="wsx-fade-preview" aria-hidden="true">
              <span className="wsx-fade-ramp wsx-fade-ramp--in" style={{ width: `${inPct}%` }} />
              <span className="wsx-fade-ramp wsx-fade-ramp--out" style={{ width: `${outPct}%` }} />
              <span className="wsx-fade-caption">
                {trackLabel(track)} · {clipLength.toFixed(2)}s · 序列 {formatTimecode(sequenceStart, fps)}
              </span>
            </div>
            <Field label="已应用" value={fadeSummary(selectedItem)} />
            <Field
              label="下一段"
              value={
                nextItem
                  ? `淡入 ${(nextItem.fadeIn ?? 0).toFixed(1)}s · 淡出 ${(nextItem.fadeOut ?? 0).toFixed(1)}s`
                  : '本轨最后一段'
              }
            />
          </>
        ) : (
          <Note tone="warning">先在时间线上选中一个片段，再应用转场。视频和音频片段都支持首尾淡化。</Note>
        )}
      </Section>

      <Section title="转场预设" hint={`点击直接写入 · 当前时长 ${duration.toFixed(1)} 秒`}>
        <div className="wsx-preset-grid">
          {TRANSITIONS.map((transition) => (
            <button
              key={transition.kind}
              type="button"
              className="wsx-preset"
              disabled={!selectedItem}
              onClick={() => applyTransition(transition.kind)}
            >
              <strong>{transition.label}</strong>
              <span>{transition.detail}</span>
            </button>
          ))}
        </div>
        <div className="wsx-toolbar">
          <span className="wsx-label">时长</span>
          <div className="wsx-chip-row">
            {DURATIONS.map((value) => (
              <button
                key={value}
                type="button"
                className={`wsx-chip${duration === value ? ' is-active' : ''}`}
                aria-pressed={duration === value}
                onClick={() => setDuration(value)}
              >
                {value.toFixed(1)}s
              </button>
            ))}
          </div>
        </div>
        <div className="window-actions">
          <button
            type="button"
            className="window-button"
            disabled={!selectedItem || (fadeIn <= 0 && fadeOut <= 0)}
            onClick={removeTransition}
          >
            移除当前片段转场
          </button>
          <button type="button" className="window-button" disabled={!selectedItem} onClick={() => onSeek(sequenceStart)}>
            定位片段
          </button>
        </div>
      </Section>

      <Section title={`剪点转场 · ${cuts.length}`} hint="交叉淡化 / 闪黑 / 闪白会压缩重叠区">
        <Note>
          剪点转场挂在片段入点上，横跨相邻两段。选中一行后可切换类型与时长；其余入口：时间线转场条右键、序列菜单、Ctrl+Shift+D。
        </Note>
        {cuts.length === 0 ? (
          <p className="window-muted">还没有剪点转场。先在时间线上选中一个片段，再点下方按钮添加。</p>
        ) : (
          cuts.map((cut) => {
            const selected = cut.transition.id === selectedTransitionId;
            return (
              <div className={`wsx-cut-row${selected ? ' is-selected' : ''}`} key={cut.transition.id}>
                <div className="wsx-cut-head">
                  <button
                    type="button"
                    className="window-link"
                    onClick={() => onSelectTransition(selected ? null : cut.transition.id)}
                  >
                    {trackLabel(trackOf(cut.next))} · {transitionKindLabel(cut.transition.kind)} ·{' '}
                    {formatTimecode(cut.start, fps)} → {formatTimecode(cut.end, fps)}
                  </button>
                  <small>生效 {cut.effective.toFixed(2)}s</small>
                </div>
                {selected ? (
                  <div className="wsx-toolbar">
                    <div className="wsx-chip-row">
                      {CUT_KINDS.map((entry) => (
                        <button
                          key={entry.kind}
                          type="button"
                          className={`wsx-chip${cut.transition.kind === entry.kind ? ' is-active' : ''}`}
                          aria-pressed={cut.transition.kind === entry.kind}
                          onClick={() => onUpdateTransition(cut.transition.id, { kind: entry.kind })}
                        >
                          {entry.label}
                        </button>
                      ))}
                    </div>
                    <div className="wsx-chip-row">
                      {DURATIONS.map((value) => (
                        <button
                          key={value}
                          type="button"
                          className={`wsx-chip${Math.abs(cut.transition.duration - value) < 0.01 ? ' is-active' : ''}`}
                          aria-pressed={Math.abs(cut.transition.duration - value) < 0.01}
                          onClick={() => onUpdateTransition(cut.transition.id, { duration: value })}
                        >
                          {value.toFixed(1)}s
                        </button>
                      ))}
                    </div>
                    <div className="wsx-chip-row">
                      {(Object.keys(TRANSITION_ALIGN_LABELS) as Array<EditorTransition['align']>).map((align) => (
                        <button
                          key={align}
                          type="button"
                          className={`wsx-chip${cut.transition.align === align ? ' is-active' : ''}`}
                          aria-pressed={cut.transition.align === align}
                          onClick={() => onUpdateTransition(cut.transition.id, { align })}
                        >
                          {TRANSITION_ALIGN_LABELS[align]}
                        </button>
                      ))}
                    </div>
                    <div className="window-actions">
                      <button
                        type="button"
                        className="window-button"
                        onClick={() => onRemoveTransition(cut.transition.id)}
                      >
                        移除转场
                      </button>
                      <button type="button" className="window-button" onClick={() => onSeek(cut.start)}>
                        定位剪点
                      </button>
                    </div>
                  </div>
                ) : null}
              </div>
            );
          })
        )}
        <div className="window-actions">
          <button
            type="button"
            className="window-button"
            disabled={!selectedItem}
            onClick={() => onApplyTransition(selectedItem?.id ?? null)}
          >
            为选中片段添加默认剪点转场
          </button>
        </div>
      </Section>

      <Section title={`已应用转场 · ${applied.length}`} hint={applied.length ? '点击时间码定位' : undefined}>
        {applied.length === 0 ? (
          <p className="window-muted">时间线上还没有淡化转场。选中片段后在上方应用。</p>
        ) : (
          applied.map(({ item, start, track: rowTrack }) => (
            <div className="wsx-transition-row" key={item.id}>
              <button type="button" className="window-link" onClick={() => onSeek(start)}>
                {formatTimecode(start, fps)}
              </button>
              <span>
                {trackLabel(rowTrack)} · 片段 {items.findIndex((entry) => entry.id === item.id) + 1} · 时长{' '}
                {itemOutputDuration(item).toFixed(2)}s
              </span>
              <small>{fadeSummary(item)}</small>
            </div>
          ))
        )}
        <p className="wsx-hint">时长超过片段长度时会自动截短；交叉淡化不会改动相邻片段，需分别应用。</p>
      </Section>
    </div>
  );
}
