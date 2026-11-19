import { useEffect, useRef, useState } from 'react';
import { RotateCcw, SlidersHorizontal, X } from 'lucide-react';
import type { TimelineFilter, TimelineItem, TimelineItemKind, TimelineMotion } from '../../../api/types';
import type { InspectorClipView, InspectorDrawerProps, InspectorItemPatch, InspectorItemView } from './inspectorTypes';
export type { InspectorClipView, InspectorDrawerProps } from './inspectorTypes';
import { formatClock, itemOutputDuration, itemSpeed } from '../project';
import { gradeIsDefault, gradeToSliders, slidersToGrade } from '../colorGrade';
import {
  GRAPHIC_TEMPLATES,
  TITLE_TRACK,
  needsTitleTrack,
  templateMatches,
  templatePatch,
} from '../graphicsTemplates';

const FILTERS: Array<{ value: TimelineFilter; label: string }> = [
  { value: 'none', label: '原片' },
  { value: 'warm', label: '暖色' },
  { value: 'cool', label: '冷色' },
  { value: 'mono', label: '黑白' },
  { value: 'vivid', label: '鲜艳' },
];

const GRADE_CONTROLS = [
  { key: 'brightness', label: '亮度', min: -100, max: 100, unit: '' },
  { key: 'contrast', label: '对比度', min: 0, max: 300, unit: '%' },
  { key: 'saturation', label: '饱和度', min: 0, max: 300, unit: '%' },
  { key: 'temperature', label: '色温', min: -100, max: 100, unit: '' },
] as const;

const MOTIONS: Array<{ value: TimelineMotion; label: string }> = [
  { value: 'none', label: '无' },
  { value: 'push', label: '推近' },
  { value: 'shake', label: '震动' },
];

const KIND_LABEL: Record<TimelineItemKind, string> = {
  video: '视频',
  hook: '钩子',
  insert: '插入',
  graphic: '图形',
};

const SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2, 4];

/**
 * 标题文字编辑器：输入过程只改本地草稿，失焦/回车才落一次 undo 记录，
 * 避免每敲一个字符都生成一条「设置标题」历史。
 */
function TitleEditor({
  item,
  clipKey,
  revision,
  onUpdateItem,
}: {
  item: InspectorItemView;
  clipKey: string;
  revision: number | null;
  onUpdateItem: (patch: InspectorItemPatch, label: string) => void;
}) {
  const [draft, setDraft] = useState(item.titleText ?? '');
  const cancelBlur = useRef(false);
  useEffect(() => {
    setDraft(item.titleText ?? '');
    cancelBlur.current = false;
  }, [clipKey, revision, item.id, item.titleText]);
  const commit = () => {
    const text = draft.trim();
    setDraft(item.titleText ?? '');
    if (cancelBlur.current) {
      cancelBlur.current = false;
      return;
    }
    if (text === (item.titleText ?? '')) return;
    onUpdateItem({ titleText: text || null }, text ? `设置标题：${text}` : '清空标题文字');
  };
  return (
    <input
      className="inspector-title-input"
      type="text"
      value={draft}
      placeholder="输入标题文字，回车确认"
      aria-label="标题文字"
      maxLength={120}
      onChange={(event) => setDraft(event.target.value)}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === 'Enter') event.currentTarget.blur();
        if (event.key === 'Escape') {
          cancelBlur.current = true;
          setDraft(item.titleText ?? '');
          event.currentTarget.blur();
        }
      }}
    />
  );
}

function describeParams(params: unknown): string {
  if (params == null) return '（无参数）';
  if (typeof params !== 'object' || Array.isArray(params)) return `params=${JSON.stringify(params)}`;
  const values = Object.entries(params);
  return values.length ? values.map(([key, value]) => `${key}=${JSON.stringify(value)}`).join('，') : '（无参数）';
}

function unsupportedWarnings(clip: InspectorClipView | null | undefined, item: TimelineItem | null): string[] {
  const warnings: string[] = [];
  if (clip) {
    if (!Array.isArray(clip.effects)) {
      warnings.push('effects 形状未支持：应为效果实例数组；原值未作转换或写入。');
    } else {
      clip.effects.forEach((effect, slot) => {
        warnings.push(`效果 #${slot + 1} ${effect.effect}：此抽屉尚未支持效果及参数编辑；原值保留。${describeParams(effect.params)}`);
      });
    }
    if (clip.transition != null) {
      const transition = clip.transition;
      warnings.push(`转场 ${transition.id ?? transition.type ?? transition.name ?? '（未知标识）'}：此抽屉尚未支持转场参数编辑；原值保留。${describeParams(transition.params)}`);
    }
  } else {
    item?.effects?.forEach((effect, slot) => {
      warnings.push(`宿主音效 #${slot + 1} ${effect.type}：此抽屉尚未支持音效参数编辑；原值保留。${describeParams(effect.params)}`);
    });
  }
  if (item) {
    if (!FILTERS.some(({ value }) => value === item.filter)) warnings.push(`未支持的滤镜：${item.filter}；原值保留。`);
    if (!MOTIONS.some(({ value }) => value === item.motion)) warnings.push(`未支持的运动：${item.motion}；原值保留。`);
    const extraGrade = Object.keys(item.grade ?? {}).filter((key) => !GRADE_CONTROLS.some((control) => control.key === key));
    if (extraGrade.length) warnings.push(`未支持的调色参数：${extraGrade.join('、')}；调整已有滑杆时保留这些值。`);
  }
  return warnings;
}

export function InspectorDrawer({
  open = false,
  clip,
  item: legacyItem = null,
  revision: legacyRevision = null,
  label = null,
  index = 0,
  total = 0,
  sequenceStart = 0,
  clipTitle = '',
  sourceDuration = 0,
  fps = 30,
  onPatch,
  warnings: hostWarnings = [],
  onUpdateItem: legacyOnUpdateItem,
  onLocateSequence,
  onClose,
}: InspectorDrawerProps) {
  const controlled = clip !== undefined;
  const item = controlled ? clip?.item ?? null : legacyItem;
  const clipKey = controlled ? clip?.key ?? '' : item?.id ?? '';
  const revision = controlled ? clip?.revision ?? null : legacyRevision;
  const canEdit = Boolean(item && (controlled ? clipKey && revision !== null && onPatch : legacyOnUpdateItem));
  const warnings = [...hostWarnings, ...unsupportedWarnings(clip, item)];
  if (item && !canEdit) {
    warnings.push(controlled && revision === null
      ? '尚无回读种子（revision=null），属性暂为只读。'
      : '未接入属性提交回调或片段 key，属性暂为只读。');
  }
  const onUpdateItem = (patch: InspectorItemPatch, historyLabel: string) => {
    if (!canEdit) return;
    if (controlled) onPatch?.(clipKey, patch);
    else legacyOnUpdateItem?.(patch, historyLabel);
  };
  const gradeSliders = gradeToSliders(item?.grade ?? null);
  const formatSourceTime = (seconds: number) => {
    const safe = Math.max(0, Number.isFinite(seconds) ? seconds : 0);
    const whole = Math.floor(safe);
    const frame = Math.min(Math.max(1, fps) - 1, Math.floor((safe - whole) * Math.max(1, fps)));
    return `${formatClock(whole)}:${String(frame).padStart(2, '0')}`;
  };
  const sourceLimit = Math.max(0.1, sourceDuration || item?.end || 0.1);
  const applyRange = (nextStart: number, nextEnd: number, label: string) => {
    if (!item) return;
    const start = Math.min(Math.max(0, nextStart), sourceLimit - 0.1);
    const end = Math.min(Math.max(start + 0.1, nextEnd), sourceLimit);
    onUpdateItem(
      {
        start: Number(start.toFixed(3)),
        end: Number(end.toFixed(3)),
      },
      label,
    );
  };
  const zoomPercent = Math.round((item?.zoom ?? 1) * 100);
  const rotation = item?.rotation ?? 0;
  const opacity = item?.opacity ?? 1;
  const positionX = item?.positionX ?? 0;
  const positionY = item?.positionY ?? 0;
  const gap = Math.max(0, item?.gapBefore ?? 0);
  const speed = item ? itemSpeed(item) : 1;
  const volumeDb = item ? Math.min(12, Math.max(-60, item.volumeDb ?? 0)) : 0;
  const fadeIn = item ? Math.min(10, Math.max(0, item.fadeIn ?? 0)) : 0;
  const fadeOut = item ? Math.min(10, Math.max(0, item.fadeOut ?? 0)) : 0;
  const clipLength = item ? itemOutputDuration(item) : 0;
  const outputDuration = item ? itemOutputDuration(item) : 0;
  const isDefault =
    !item ||
    (item.zoom === 1 &&
      item.filter === 'none' &&
      item.motion === 'none' &&
      rotation === 0 &&
      opacity === 1 &&
      positionX === 0 &&
      positionY === 0 &&
      speed === 1);

  return (
    <aside
      className={`ai-drawer inspector-drawer${open ? ' is-open' : ''}`}
      aria-label="片段属性"
      aria-hidden={!open}
      inert={!open}
    >
      <div className="ai-drawer-head">
        <div className="ai-drawer-title">
          <span className="ai-brand-dot">
            <SlidersHorizontal size={12} />
          </span>
          <strong>属性</strong>
          <span className="ai-version">
            {item ? `${index + 1} / ${total}` : '未选择'}
          </span>
        </div>
        <div className="ai-drawer-head-actions">
          <kbd>Esc</kbd>
          <button type="button" className="icon-button" onClick={onClose} aria-label="收起属性面板">
            <X size={12} />
          </button>
        </div>
      </div>

      <div className="inspector-body">
        {warnings.length > 0 && (
          <div
            className="inspector-warning"
            role="status"
            aria-label="属性警告"
            style={{
              color: 'var(--lc-warning)',
              background: 'var(--lc-warning-surface)',
              border: '1px solid var(--lc-line)',
              borderRadius: 'var(--lc-radius-sm)',
              padding: 'var(--lc-space-3)',
              marginBottom: 'var(--lc-space-3)',
              overflowWrap: 'anywhere',
            }}
          >
            <strong>属性警告</strong>
            <ul style={{ margin: 0, paddingInlineStart: 'var(--lc-space-4)' }}>
              {warnings.map((warning, index) => <li key={`${index}:${warning}`}>{warning}</li>)}
            </ul>
          </div>
        )}
        {!item ? (
          <div className="inspector-empty">
            <SlidersHorizontal size={20} />
            <strong>未选择片段</strong>
            <p>在时间线上选中一个片段后，这里会显示源入点、滤镜、运动和缩放属性。</p>
          </div>
        ) : (
          <>
            <div className="inspector-source">
              <span className="inspector-eyebrow">SELECTED CLIP</span>
              <strong title={label ?? undefined}>{label ?? '未命名片段'}</strong>
              <span>{clipTitle}</span>
            </div>

            <section className="inspector-section" aria-label="片段信息">
              <h3>片段</h3>
              <dl className="inspector-kv">
                <div><dt>类型</dt><dd>{KIND_LABEL[item.kind]}</dd></div>
                <div><dt>入点</dt><dd>{formatSourceTime(item.start)}</dd></div>
                <div><dt>出点</dt><dd>{formatSourceTime(item.end)}</dd></div>
                <div><dt>素材时长</dt><dd>{formatClock(item.end - item.start)}</dd></div>
                <div><dt>输出时长</dt><dd>{formatClock(outputDuration)}</dd></div>
                <div><dt>前置空隙</dt><dd>{gap > 0 ? `${formatClock(gap)}` : '无'}</dd></div>
                <div><dt>时间线位置</dt><dd>{formatSourceTime(sequenceStart)}</dd></div>
              </dl>
              <button
                type="button"
                className="secondary-button inspector-locate"
                onClick={() => onLocateSequence?.(sequenceStart)}
              >
                在时间线定位
              </button>
            </section>

            <fieldset disabled={!canEdit} style={{ display: 'contents' }} aria-label="片段属性编辑">
            <section className="inspector-section" aria-label="片段启用">
              <div className="inspector-section-head">
                <h3>片段状态</h3>
                <output>{item.disabled ? '已禁用' : '启用'}</output>
              </div>
              <button
                type="button"
                className={`secondary-button${item.disabled ? ' is-warning' : ''}`}
                aria-pressed={Boolean(item.disabled)}
                onClick={() =>
                  onUpdateItem(
                    { disabled: !item.disabled },
                    item.disabled ? '启用片段' : '禁用片段',
                  )
                }
              >
                {item.disabled ? '启用这个片段' : '禁用这个片段'}
              </button>
              <p className="inspector-footnote">
                禁用后保留时间线长度，成片输出黑场静音；V2/V3 不参与叠加。
              </p>
            </section>

            <section className="inspector-section" aria-label="片段裁剪">
              <div className="inspector-section-head">
                <h3>裁剪</h3>
                <output>{formatSourceTime(sourceLimit)}</output>
              </div>
              <div className="inspector-range-grid">
                <label>
                  入点
                  <input
                    type="number"
                    min={0}
                    max={Math.max(0, sourceLimit - 0.1)}
                    step={0.1}
                    value={Number(item.start.toFixed(3))}
                    aria-label="片段入点"
                    onChange={(event) => {
                      const value = Number(event.target.value);
                      if (Number.isFinite(value)) applyRange(value, item.end, '调整片段入点');
                    }}
                  />
                </label>
                <label>
                  出点
                  <input
                    type="number"
                    min={0.1}
                    max={sourceLimit}
                    step={0.1}
                    value={Number(item.end.toFixed(3))}
                    aria-label="片段出点"
                    onChange={(event) => {
                      const value = Number(event.target.value);
                      if (Number.isFinite(value)) applyRange(item.start, value, '调整片段出点');
                    }}
                  />
                </label>
              </div>
              <button
                type="button"
                className="secondary-button"
                disabled={item.start <= 0.001 && item.end >= sourceLimit - 0.001}
                onClick={() => applyRange(0, sourceLimit, '恢复片段整段素材')}
              >
                恢复整段
              </button>
              <p className="inspector-footnote">
                入点 / 出点限制在当前素材 0–{formatSourceTime(sourceLimit)}；输出时长与后续片段自动顺延。
              </p>
            </section>

            <section className="inspector-section" aria-label="时间线空隙">
              <div className="inspector-section-head">
                <h3>空隙</h3>
                <output>{gap > 0 ? `${gap.toFixed(1)} 秒` : '无'}</output>
              </div>
              <div className="inspector-chip-row">
                <button
                  type="button"
                  className="inspector-chip"
                  disabled={gap <= 0}
                  onClick={() => onUpdateItem({ gapBefore: 0 }, '移除片段前置空隙')}
                >
                  移除
                </button>
                <button
                  type="button"
                  className="inspector-chip"
                  onClick={() => onUpdateItem({ gapBefore: gap + 0.5 }, '增加 0.5 秒空隙')}
                >
                  +0.5 秒
                </button>
                <button
                  type="button"
                  className="inspector-chip"
                  onClick={() => onUpdateItem({ gapBefore: gap + 1 }, '增加 1 秒空隙')}
                >
                  +1 秒
                </button>
              </div>
              <p className="inspector-footnote">空隙在成片中渲染为黑场静音，字幕和后续片段顺延。</p>
            </section>

            <section className="inspector-section" aria-label="片段速度">
              <div className="inspector-section-head">
                <h3>速度</h3>
                <output>{speed}×</output>
              </div>
              <div className="inspector-chip-row">
                {SPEEDS.map((option) => (
                  <button
                    key={option}
                    type="button"
                    className={`inspector-chip${Math.abs(speed - option) < 0.001 ? ' is-active' : ''}`}
                    aria-pressed={Math.abs(speed - option) < 0.001}
                    onClick={() => onUpdateItem({ speed: option }, `设置速度：${option}×`)}
                  >
                    {option}×
                  </button>
                ))}
              </div>
              <p className="inspector-footnote">速度改变输出时长；字幕、空隙和后续片段自动顺延。</p>
            </section>

            <section className="inspector-section" aria-label="片段音量">
              <div className="inspector-section-head">
                <h3>音量</h3>
                <output>{volumeDb > 0 ? `+${volumeDb}` : volumeDb} dB</output>
              </div>
              <div className="inspector-zoom-controls">
                <button
                  type="button"
                  className="secondary-button"
                  aria-label="静音片段"
                  disabled={volumeDb <= -60}
                  onClick={() => onUpdateItem({ volumeDb: -60 }, '静音片段')}
                >
                  静音
                </button>
                <input
                  type="range"
                  min={-60}
                  max={12}
                  step={1}
                  value={volumeDb}
                  aria-label="片段音量"
                  onChange={(event) => {
                    const next = Number(event.target.value);
                    onUpdateItem({ volumeDb: next }, `设置片段音量：${next > 0 ? '+' : ''}${next} dB`);
                  }}
                />
                <button
                  type="button"
                  className="secondary-button"
                  aria-label="还原片段音量"
                  disabled={Math.abs(volumeDb) < 0.5}
                  onClick={() => onUpdateItem({ volumeDb: 0 }, '还原片段音量')}
                >
                  0 dB
                </button>
              </div>
              <p className="inspector-footnote">音量只影响片段自带音频；导出成片时生效。</p>
            </section>

            <section className="inspector-section" aria-label="片段淡入淡出">
              <div className="inspector-section-head">
                <h3>淡入 / 淡出</h3>
                <output>{fadeIn.toFixed(1)}s / {fadeOut.toFixed(1)}s</output>
              </div>
              <div className="inspector-fade-row">
                <label>
                  <span>淡入</span>
                  <input
                    type="range"
                    min={0}
                    max={2}
                    step={0.1}
                    value={fadeIn}
                    aria-label="片段淡入"
                    onChange={(event) => {
                      const next = Math.min(Number(event.target.value), Math.max(0, clipLength - fadeOut));
                      onUpdateItem({ fadeIn: Math.round(next * 10) / 10 }, `设置淡入：${next.toFixed(1)} 秒`);
                    }}
                  />
                  <output>{fadeIn.toFixed(1)}s</output>
                </label>
                <label>
                  <span>淡出</span>
                  <input
                    type="range"
                    min={0}
                    max={2}
                    step={0.1}
                    value={fadeOut}
                    aria-label="片段淡出"
                    onChange={(event) => {
                      const next = Math.min(Number(event.target.value), Math.max(0, clipLength - fadeIn));
                      onUpdateItem({ fadeOut: Math.round(next * 10) / 10 }, `设置淡出：${next.toFixed(1)} 秒`);
                    }}
                  />
                  <output>{fadeOut.toFixed(1)}s</output>
                </label>
              </div>
              <div className="inspector-actions">
                <button
                  type="button"
                  className="secondary-button"
                  aria-label="清除片段淡入淡出"
                  disabled={fadeIn <= 0 && fadeOut <= 0}
                  onClick={() => onUpdateItem({ fadeIn: 0, fadeOut: 0 }, '清除片段淡入淡出')}
                >
                  清除
                </button>
              </div>
              <p className="inspector-footnote">淡入淡出同时作用于画面和声音；导出成片时生效。</p>
            </section>

            <section className="inspector-section" aria-label="画面滤镜">
              <h3>滤镜</h3>
              <div className="inspector-chip-row">
                {FILTERS.map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    className={`inspector-chip${item.filter === option.value ? ' is-active' : ''}`}
                    aria-pressed={item.filter === option.value}
                    onClick={() => onUpdateItem({ filter: option.value }, `设置滤镜：${option.label}`)}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </section>

            <section className="inspector-section" aria-label="画面调色">
              <div className="inspector-section-head">
                <h3>调色</h3>
                <output>{gradeIsDefault(item.grade) ? '原片' : '已调色'}</output>
              </div>
              <div className="inspector-range-grid">
                {GRADE_CONTROLS.map((control) => (
                  <label key={control.key}>
                    <span>
                      {control.label}
                      {control.key === 'temperature'
                        ? gradeSliders.temperature > 0
                          ? ' 偏暖'
                          : gradeSliders.temperature < 0
                            ? ' 偏冷'
                            : ''
                        : ` ${gradeSliders[control.key]}${control.unit}`}
                    </span>
                    <input
                      type="range"
                      min={control.min}
                      max={control.max}
                      step={1}
                      value={gradeSliders[control.key]}
                      aria-label={`调色${control.label}`}
                      onChange={(event) => {
                        const next = Number(event.target.value);
                        onUpdateItem(
                          { grade: slidersToGrade({ ...gradeSliders, [control.key]: next }) },
                          `调色：${control.label} ${next}${control.unit}`,
                        );
                      }}
                    />
                  </label>
                ))}
              </div>
              <button
                type="button"
                className="secondary-button"
                disabled={gradeIsDefault(item.grade)}
                onClick={() => onUpdateItem({ grade: null }, '还原调色')}
              >
                还原调色
              </button>
              <p className="inspector-footnote">调色叠加在滤镜之上；预览与导出用同一组参数。</p>
            </section>

            <section className="inspector-section" aria-label="标题与图形">
              <div className="inspector-section-head">
                <h3>标题 / 图形</h3>
                <output>{(item.track ?? 1) === TITLE_TRACK ? 'V3 标题层' : '非标题层'}</output>
              </div>
              <TitleEditor item={item} clipKey={clipKey} revision={revision} onUpdateItem={onUpdateItem} />
              <div className="inspector-chip-row">
                {GRAPHIC_TEMPLATES.map((template) => {
                  const active = templateMatches(item, template);
                  return (
                    <button
                      key={template.id}
                      type="button"
                      title={template.description}
                      className={`inspector-chip${active ? ' is-active' : ''}`}
                      aria-pressed={active}
                      onClick={() => onUpdateItem(templatePatch(item, template), `套用图形模板：${template.label}`)}
                    >
                      {active ? `${template.label} ✓` : template.label}
                    </button>
                  );
                })}
              </div>
              {needsTitleTrack(item) ? (
                <button
                  type="button"
                  className="primary-button"
                  onClick={() => onUpdateItem({ track: TITLE_TRACK }, '移到标题层 V3')}
                >
                  移到标题层 V3（标题只在 V3 渲染）
                </button>
              ) : null}
              <p className="inspector-footnote">标题只在 V3 轨道渲染；套用模板不会覆盖已经写好的标题文字。</p>
            </section>

            <section className="inspector-section" aria-label="画面运动">
              <h3>运动</h3>
              <div className="inspector-chip-row">
                {MOTIONS.map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    className={`inspector-chip${item.motion === option.value ? ' is-active' : ''}`}
                    aria-pressed={item.motion === option.value}
                    onClick={() => onUpdateItem({ motion: option.value }, `设置运动：${option.label}`)}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </section>

            <section className="inspector-section" aria-label="画面缩放">
              <div className="inspector-section-head">
                <h3>缩放</h3>
                <output>{zoomPercent}%</output>
              </div>
              <div className="inspector-zoom-controls">
                <button
                  type="button"
                  className="secondary-button"
                  aria-label="缩小画面"
                  disabled={item.zoom <= 1}
                  onClick={() => onUpdateItem({ zoom: Math.max(1, Number((item.zoom - 0.1).toFixed(2))) }, '缩小画面')}
                >
                  −
                </button>
                <input
                  type="range"
                  min={1}
                  max={2}
                  step={0.05}
                  value={item.zoom}
                  aria-label="画面缩放"
                  onChange={(event) => {
                    const zoom = Number(event.target.value);
                    onUpdateItem({ zoom }, `缩放画面至 ${Math.round(zoom * 100)}%`);
                  }}
                />
                <button
                  type="button"
                  className="secondary-button"
                  aria-label="放大画面"
                  disabled={item.zoom >= 2}
                  onClick={() => onUpdateItem({ zoom: Math.min(2, Number((item.zoom + 0.1).toFixed(2))) }, '放大画面')}
                >
                  +
                </button>
              </div>
            </section>

            <section className="inspector-section" aria-label="画面位置">
              <div className="inspector-section-head">
                <h3>画面位置</h3>
                <output>X {Math.round(positionX * 100)}% · Y {Math.round(positionY * 100)}%</output>
              </div>
              <div className="inspector-position-grid">
                <label>
                  X
                  <input
                    type="range"
                    min={-0.5}
                    max={0.5}
                    step={0.01}
                    value={positionX}
                    aria-label="画面水平位置"
                    onChange={(event) =>
                      onUpdateItem(
                        { positionX: Number(event.target.value) },
                        `水平移动画面至 ${Math.round(Number(event.target.value) * 100)}%`,
                      )
                    }
                  />
                </label>
                <label>
                  Y
                  <input
                    type="range"
                    min={-0.5}
                    max={0.5}
                    step={0.01}
                    value={positionY}
                    aria-label="画面垂直位置"
                    onChange={(event) =>
                      onUpdateItem(
                        { positionY: Number(event.target.value) },
                        `垂直移动画面至 ${Math.round(Number(event.target.value) * 100)}%`,
                      )
                    }
                  />
                </label>
              </div>
            </section>

            <section className="inspector-section" aria-label="画面旋转">
              <div className="inspector-section-head">
                <h3>旋转</h3>
                <output>{rotation}°</output>
              </div>
              <div className="inspector-zoom-controls inspector-slider-only">
                <input
                  type="range"
                  min={-180}
                  max={180}
                  step={1}
                  value={rotation}
                  aria-label="画面旋转角度"
                  onChange={(event) =>
                    onUpdateItem(
                      { rotation: Number(event.target.value) },
                      `旋转画面至 ${event.target.value}°`,
                    )
                  }
                />
              </div>
            </section>

            <section className="inspector-section" aria-label="画面不透明度">
              <div className="inspector-section-head">
                <h3>不透明度</h3>
                <output>{Math.round(opacity * 100)}%</output>
              </div>
              <div className="inspector-zoom-controls inspector-slider-only">
                <input
                  type="range"
                  min={0}
                  max={1}
                  step={0.01}
                  value={opacity}
                  aria-label="画面不透明度"
                  onChange={(event) =>
                    onUpdateItem(
                      { opacity: Number(event.target.value) },
                      `设置不透明度 ${Math.round(Number(event.target.value) * 100)}%`,
                    )
                  }
                />
              </div>
            </section>

            <button
              type="button"
              className="secondary-button inspector-reset"
              disabled={isDefault}
              onClick={() =>
                onUpdateItem(
                  { zoom: 1, filter: 'none', motion: 'none', rotation: 0, opacity: 1, positionX: 0, positionY: 0, speed: 1 },
                  '重置片段画面属性',
                )
              }
            >
              <RotateCcw size={12} /> 重置画面属性
            </button>
            <p className="inspector-footnote">修改立即写入工程并进入撤销栈。</p>
            </fieldset>
          </>
        )}
      </div>
    </aside>
  );
}
