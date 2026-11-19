import { useId, useRef, useState, type KeyboardEvent } from 'react';
import { Plus, Trash2, X } from 'lucide-react';
import type { TranscriptSegment } from '../../../api/types';
import './RefinementPanel.css';

export interface RefinementPanelProps {
  clipLabel: string;
  segments: TranscriptSegment[];
  activeTime: number;
  disabled?: boolean;
  onChange: (segments: TranscriptSegment[]) => void;
  onSeek: (sourceTime: number) => void;
  onClose: () => void;
}

/** Host-owned source seconds and subtitles; this panel owns only interaction state. */
export function RefinementPanel(props: RefinementPanelProps) {
  const { clipLabel, segments, activeTime, disabled = false, onChange, onSeek, onClose } = props;
  const id = useId();
  const composing = useRef(false);
  const [validationMessage, setValidationMessage] = useState('');

  const handleKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    // Preserve native text editing and Tab navigation without firing bubbling host shortcuts.
    event.stopPropagation();
    if (event.key === 'Escape' && !composing.current && !event.nativeEvent.isComposing && event.keyCode !== 229) {
      event.preventDefault();
      onClose();
    }
  };

  const changeText = (index: number, text: string) => {
    if (disabled) return;
    onChange(segments.map((segment, position) => position === index ? { ...segment, text } : segment));
  };

  const changeTime = (index: number, field: 'start' | 'end', input: HTMLInputElement) => {
    if (disabled) return;
    const segment = segments[index];
    const value = input.valueAsNumber;
    const next = { ...segment, [field]: value };
    if (!Number.isFinite(next.start) || !Number.isFinite(next.end) || next.start < 0 || next.end <= next.start) {
      input.value = String(segment[field]);
      setValidationMessage(`第 ${index + 1} 条：时间须为非负秒数，且结束时间大于开始时间。已保留原值。`);
      return;
    }
    setValidationMessage('');
    onChange(segments.map((current, position) => position === index ? next : current));
  };

  const addSegment = () => {
    if (disabled) return;
    const start = segments.reduce(
      (latest, segment) => Number.isFinite(segment.end) ? Math.max(latest, segment.end) : latest,
      Number.isFinite(activeTime) ? Math.max(0, activeTime) : 0,
    );
    const end = start + 2;
    if (!Number.isFinite(end) || end <= start) {
      setValidationMessage('当前时间超出有效范围，请先在宿主调整素材定位。');
      return;
    }
    setValidationMessage('');
    onChange([...segments, { start, end, text: '' }]);
  };

  const removeSegment = (index: number) => {
    if (disabled) return;
    setValidationMessage('');
    onChange(segments.filter((_, position) => position !== index));
  };

  return (
    <aside
      className="refinement-panel"
      aria-labelledby={`${id}-title`}
      onKeyDown={handleKeyDown}
      onKeyUp={(event) => event.stopPropagation()}
      onKeyPress={(event) => event.stopPropagation()}
      onCompositionStartCapture={() => { composing.current = true; }}
      onCompositionEndCapture={() => { composing.current = false; }}
    >
      <header className="refinement-panel__header">
        <div>
          <h2 id={`${id}-title`}>精修</h2>
          <p className="refinement-panel__meta">编辑当前素材的字幕</p>
        </div>
        <button type="button" className="ed-icon-btn refinement-panel__close" aria-label="关闭精修面板" onClick={onClose}>
          <X size={16} aria-hidden="true" />
        </button>
      </header>

      <div className="refinement-panel__context">
        <p className="refinement-panel__clip" title={clipLabel}>{clipLabel || '未选择素材'}</p>
        <div className="refinement-panel__section-heading">
          <h3 id={`${id}-subtitles`}>字幕</h3>
          <span className="refinement-panel__meta">{disabled ? '未就绪' : `${segments.length} 条`}</span>
        </div>
        <p id={`${id}-time-help`} className="refinement-panel__meta">时间按源素材的秒数填写，结束要晚于开始。</p>
      </div>

      <div className="refinement-panel__scroll" aria-labelledby={`${id}-subtitles`}>
        {disabled ? (
          <p className="refinement-panel__empty" role="status">暂无可编辑素材，请先在宿主选择素材。字幕编辑与定位已停用。</p>
        ) : segments.length === 0 ? (
          <div className="refinement-panel__empty-block">
            <p className="refinement-panel__empty">当前素材还没有字幕。点下面的按钮添加一条，按时间码填文本即可。</p>
            <button type="button" className="ed-btn refinement-panel__add" onClick={addSegment}>
              <Plus size={14} aria-hidden="true" /> 添加字幕
            </button>
          </div>
        ) : (
          <ol className="refinement-panel__segments">
            {segments.map((segment, index) => {
              const isActive = Number.isFinite(activeTime) && activeTime >= segment.start && activeTime < segment.end;
              const rowId = `${id}-segment-${index}`;
              return (
                <li key={index} className={`refinement-panel__segment${isActive ? ' is-active' : ''}`} aria-current={isActive ? 'true' : undefined}>
                  <div className="refinement-panel__row-heading">
                    <label htmlFor={`${rowId}-text`}>字幕 {index + 1}</label>
                    {isActive && <span className="refinement-panel__meta">当前播放</span>}
                    <button type="button" className="ed-icon-btn refinement-panel__delete" aria-label={`删除第 ${index + 1} 条字幕`} onClick={() => removeSegment(index)}>
                      <Trash2 size={14} aria-hidden="true" />
                    </button>
                  </div>
                  <textarea id={`${rowId}-text`} rows={3} value={segment.text} placeholder="输入字幕文本" onChange={(event) => changeText(index, event.currentTarget.value)} />
                  <div className="refinement-panel__times">
                    <label htmlFor={`${rowId}-start`}>
                      开始（秒）
                      <input id={`${rowId}-start`} type="number" min={0} step="any" value={segment.start} aria-describedby={`${id}-time-help ${id}-validation`} onChange={(event) => changeTime(index, 'start', event.currentTarget)} />
                    </label>
                    <label htmlFor={`${rowId}-end`}>
                      结束（秒）
                      <input id={`${rowId}-end`} type="number" min={0} step="any" value={segment.end} aria-describedby={`${id}-time-help ${id}-validation`} onChange={(event) => changeTime(index, 'end', event.currentTarget)} />
                    </label>
                  </div>
                  <button type="button" className="ed-btn refinement-panel__seek" disabled={!Number.isFinite(segment.start) || segment.start < 0} onClick={() => onSeek(segment.start)}>
                    跳到这一句开始
                  </button>
                </li>
              );
            })}
          </ol>
        )}
      </div>

      <footer className="refinement-panel__footer">
        <p id={`${id}-validation`} className="refinement-panel__validation" role="status" aria-live="polite" aria-atomic="true">{disabled ? '' : validationMessage}</p>
        <button type="button" className="ed-btn refinement-panel__add" disabled={disabled} onClick={addSegment}>
          <Plus size={14} aria-hidden="true" /> 添加字幕
        </button>
      </footer>
    </aside>
  );
}
