import { useEffect, useId, useMemo, useState } from 'react';
import { PARAMETRIC_EQ_SPECS, ParametricEqControls, type ParametricEqAudio } from './Effects';
import type {
  AudioEffectInstance,
  EditorTransition,
  MaskInstance,
  ParamKeyframe,
  ParamKeyframeProperty,
  TimelineItem,
} from '../../../../api/types';
import {
  baseParamValue,
  clipFadeFactor,
  envelopeDbAt,
  itemOutputDuration,
  itemSequenceStart,
  normalizeMasks,
  normalizeVolumeKeyframes,
  MAX_MASKS,
  DEFAULT_MASK,
  paramKeyframesFor,
  paramValueAt,
  paramValueAtItem,
  trackOf,
  transitionEffectiveDuration,
  transitionKindLabel,
  TRANSITION_ALIGN_LABELS,
  transitionMinDuration,
  findTransition,
  cutAvailability,
} from '../../project';
import {
  AUDIO_EFFECT_SPECS,
  AUDIO_EFFECT_TYPES,
  MAX_AUDIO_EFFECTS,
  clampAudioParam,
  createAudioEffect,
  defaultAudioParams,
  normalizeAudioEffects,
  type AudioEffectParamSpec,
} from '../../audioEffects';
import { Field, Note, Section, Slider } from './ui';
import { maskEditor, useMaskEditorState } from '../Monitor';
import './effect-controls.css';

const KINDS: Array<EditorTransition['kind']> = ['cross-dissolve', 'dip-to-black', 'dip-to-white'];
const ALIGNS: Array<EditorTransition['align']> = ['center', 'start', 'end'];
const DURATIONS = [0.3, 0.5, 1, 2];

function formatSeconds(value: number): string {
  return `${Math.round(value * 100) / 100}s`;
}

function formatClock(seconds: number): string {
  const safe = Math.max(0, seconds);
  const minutes = Math.floor(safe / 60);
  const rest = safe - minutes * 60;
  return `${String(minutes).padStart(2, '0')}:${rest.toFixed(2).padStart(5, '0')}`;
}

const KEYFRAME_EPSILON = 1 / 60;

const PROP_PATCH_KEYS: Record<ParamKeyframeProperty, 'zoom' | 'rotation' | 'positionX' | 'positionY' | 'opacity'> = {
  scale: 'zoom',
  rotation: 'rotation',
  positionX: 'positionX',
  positionY: 'positionY',
  opacity: 'opacity',
};

/**
 * 变换参数滑杆 + 关键帧控制（秒表启用、菱形在播放头增删点）。
 * 启用关键帧后，滑杆值来自播放头位置的插值，修改会写在播放头处而不是静态字段。
 */
function ParamKeyframeSlider({
  item,
  prop,
  label,
  min,
  max,
  step,
  suffix,
  playhead,
  patch,
  seekLocal,
}: {
  item: TimelineItem;
  prop: ParamKeyframeProperty;
  label: string;
  min: number;
  max: number;
  step: number;
  suffix: string;
  playhead: number;
  patch: (next: Partial<TimelineItem>, label: string) => void;
  seekLocal: (localTime: number) => void;
}) {
  const points = paramKeyframesFor(item, prop);
  const enabled = points.length > 0;
  const clampedPlayhead = Math.max(0, Math.round(playhead * 1000) / 1000);
  const baseValue = baseParamValue(item, prop);
  const currentValue = enabled ? paramValueAt(points, clampedPlayhead, baseValue) : baseValue;
  const atPlayhead = points.some((point) => Math.abs(point.time - clampedPlayhead) < KEYFRAME_EPSILON);
  const [expanded, setExpanded] = useState(false);
  const previousPoint = [...points].reverse().find((point) => point.time < clampedPlayhead - KEYFRAME_EPSILON) ?? null;
  const nextPoint = points.find((point) => point.time > clampedPlayhead + KEYFRAME_EPSILON) ?? null;

  const writePoints = (next: ParamKeyframe[], action: string) => {
    const keyframes = { ...(item.keyframes ?? {}) };
    if (next.length > 0) keyframes[prop] = next;
    else delete keyframes[prop];
    const hasAny = Object.keys(keyframes).length > 0;
    patch({ keyframes: hasAny ? keyframes : null }, action);
  };

  const roundValue = (value: number) => Math.round(value * 1000) / 1000;

  const upsertAtPlayhead = (value: number, action: string) => {
    const next = points
      .filter((point) => Math.abs(point.time - clampedPlayhead) >= KEYFRAME_EPSILON)
      .concat({ time: clampedPlayhead, value: roundValue(value) })
      .sort((left, right) => left.time - right.time);
    writePoints(next, action);
  };

  const toggleWatch = () => {
    if (enabled) {
      writePoints([], `关闭${label}关键帧`);
      return;
    }
    writePoints([{ time: clampedPlayhead, value: roundValue(baseValue) }], `启用${label}关键帧`);
  };

  const toggleDiamond = () => {
    if (!enabled) {
      writePoints([{ time: clampedPlayhead, value: roundValue(baseValue) }], `启用${label}关键帧`);
      return;
    }
    if (atPlayhead) {
      writePoints(
        points.filter((point) => Math.abs(point.time - clampedPlayhead) >= KEYFRAME_EPSILON),
        `删除${label}关键帧`,
      );
      return;
    }
    upsertAtPlayhead(currentValue, `添加${label}关键帧`);
  };

  const handleChange = (value: number) => {
    if (enabled) {
      upsertAtPlayhead(value, `${label}关键帧 ${roundValue(value)}`);
      return;
    }
    patch({ [PROP_PATCH_KEYS[prop]]: value }, `修改片段${label}`);
  };

  return (
    <div className="ec-keyframe-row">
      <Slider
        label={label}
        value={currentValue}
        min={min}
        max={max}
        step={step}
        suffix={suffix}
        onChange={handleChange}
        action={
          <span className="ec-keyframe-actions">
            <button
              type="button"
              className={`ec-keyframe-watch${enabled ? ' is-on' : ''}`}
              title={enabled ? `关闭${label}关键帧（恢复静态值）` : `启用${label}关键帧`}
              aria-pressed={enabled}
              onClick={toggleWatch}
            >
              <svg viewBox="0 0 16 16" aria-hidden="true">
                <circle cx="8" cy="8.5" r="5.2" />
                <path d="M8 5.6V8.5l2 1.2" />
                <path d="M6.2 1.8h3.6" />
              </svg>
            </button>
            <button
              type="button"
              className={`ec-keyframe-diamond${atPlayhead ? ' is-on' : ''}`}
              title={atPlayhead ? `删除播放头处的${label}关键帧` : `在播放头添加${label}关键帧`}
              aria-pressed={atPlayhead}
              onClick={toggleDiamond}
            >
              <svg viewBox="0 0 16 16" aria-hidden="true">
                <path d="M8 2.4 13.6 8 8 13.6 2.4 8Z" />
              </svg>
            </button>
          </span>
        }
      />
      {enabled ? (
        <div className="ec-keyframe-nav">
          <button
            type="button"
            className="ec-keyframe-nav-button"
            title="跳到上一个关键帧"
            disabled={!previousPoint}
            onClick={() => {
              if (!previousPoint) return;
              seekLocal(previousPoint.time);
            }}
          >
            <svg viewBox="0 0 16 16" aria-hidden="true">
              <path d="M10.5 3.5 5.5 8l5 4.5" />
            </svg>
          </button>
          <button
            type="button"
            className="ec-keyframe-nav-button"
            title="跳到下一个关键帧"
            disabled={!nextPoint}
            onClick={() => {
              if (!nextPoint) return;
              seekLocal(nextPoint.time);
            }}
          >
            <svg viewBox="0 0 16 16" aria-hidden="true">
              <path d="M5.5 3.5 10.5 8l-5 4.5" />
            </svg>
          </button>
          <button
            type="button"
            className={`ec-keyframe-expand${expanded ? ' is-on' : ''}`}
            title={expanded ? '收起关键帧列表' : '展开关键帧列表'}
            aria-expanded={expanded}
            onClick={() => setExpanded((current) => !current)}
          >
            {points.length} 个关键帧
            <svg viewBox="0 0 16 16" aria-hidden="true">
              <path d="M4 6.2 8 10.2l4-4" />
            </svg>
          </button>
          <button
            type="button"
            className="ec-keyframe-clear"
            title="清除该参数全部关键帧"
            onClick={() => writePoints([], `清除${label}关键帧`)}
          >
            清除
          </button>
        </div>
      ) : null}
      {enabled && expanded ? (
        <ul className="ec-keyframe-list">
          {points.map((point) => {
            const active = Math.abs(point.time - clampedPlayhead) < KEYFRAME_EPSILON;
            return (
              <li key={point.time} className={`ec-keyframe-item${active ? ' is-active' : ''}`}>
                <button type="button" className="ec-keyframe-item-seek" onClick={() => seekLocal(point.time)}>
                  <span>{point.time.toFixed(2)}s</span>
                  <span className="ec-keyframe-item-value">
                    {roundValue(point.value)}
                    {suffix}
                  </span>
                </button>
                <button
                  type="button"
                  className="ec-keyframe-item-remove"
                  title="删除该关键帧"
                  onClick={() => {
                    writePoints(
                      points.filter((candidate) => Math.abs(candidate.time - point.time) >= KEYFRAME_EPSILON),
                      `删除${label}关键帧`,
                    );
                  }}
                >
                  ×
                </button>
              </li>
            );
          })}
        </ul>
      ) : null}
      <p className={`ec-keyframe-hint${enabled ? ' is-on' : ''}`}>
        {enabled
          ? `播放头 ${clampedPlayhead.toFixed(2)}s · ${atPlayhead ? '当前帧已有关键帧' : '当前帧无关键帧'}`
          : '未启用关键帧（点秒表启用）'}
      </p>
    </div>
  );
}

export function EffectControlsPanel({
  selectedItem,
  selectedTransition,
  transitions,
  sourceDurations,
  items,
  fps,
  sequenceTime,
  onUpdateItem,
  onSelectTransition,
  onUpdateTransition,
  onRemoveTransition,
  onApplyTransition,
  onSeek,
  onNotice,
}: {
  selectedItem: TimelineItem | null;
  selectedTransition: EditorTransition | null;
  transitions: EditorTransition[];
  sourceDurations?: Record<string, number>;
  items: TimelineItem[];
  fps: number;
  sequenceTime: number;
  onUpdateItem: (patch: Partial<TimelineItem>, label: string) => void;
  onSelectTransition: (transitionId: string | null) => void;
  onUpdateTransition: (
    transitionId: string,
    patch: Partial<Pick<EditorTransition, 'kind' | 'duration' | 'align'>>,
  ) => void;
  onRemoveTransition: (transitionId: string) => void;
  onApplyTransition: (itemId: string | null) => void;
  onSeek: (time: number) => void;
  onNotice: (message: string) => void;
}) {
  const [speedDraft, setSpeedDraft] = useState('');
  const [fadeInDraft, setFadeInDraft] = useState('');
  const [fadeOutDraft, setFadeOutDraft] = useState('');
  const [addEffectOpen, setAddEffectOpen] = useState(false);
  const [expandedEffects, setExpandedEffects] = useState<Record<string, boolean>>({});
  const [effectDrafts, setEffectDrafts] = useState<Record<string, string>>({});
  const speedInputId = useId();
  const speedHintId = useId();

  useEffect(() => {
    setSpeedDraft(selectedItem ? String(selectedItem.speed ?? 1) : '');
    setFadeInDraft(selectedItem ? String(selectedItem.fadeIn ?? 0) : '');
    setFadeOutDraft(selectedItem ? String(selectedItem.fadeOut ?? 0) : '');
    setAddEffectOpen(false);
    setEffectDrafts({});
  }, [selectedItem?.id, selectedItem?.speed, selectedItem?.fadeIn, selectedItem?.fadeOut]);

  const [selectedMaskId, setSelectedMaskId] = useState<string | null>(null);
  const maskEditorState = useMaskEditorState();


  // 切换片段 / 删除蒙版后，把选择收敛到仍存在的第一个蒙版。
  useEffect(() => {
    if (!selectedItem) {
      setSelectedMaskId(null);
      return;
    }
    const currentMasks = normalizeMasks(selectedItem) ?? [];
    setSelectedMaskId((previous) =>
      previous && currentMasks.some((mask) => mask.id === previous) ? previous : currentMasks[0]?.id ?? null,
    );
  }, [selectedItem]);

  // 把当前片段 + 蒙版选择发布给节目监视器；只有 V1 主画面允许拖拽编辑。
  useEffect(() => {
    if (!selectedItem) {
      maskEditor.setTarget(null);
      return;
    }
    const currentMasks = normalizeMasks(selectedItem) ?? [];
    if (currentMasks.length === 0) {
      maskEditor.setTarget(null);
      return;
    }
    const maskId =
      selectedMaskId && currentMasks.some((mask) => mask.id === selectedMaskId)
        ? selectedMaskId
        : currentMasks[0]?.id ?? null;
    const editable = trackOf(selectedItem) === 1;
    maskEditor.setTarget({
      itemId: selectedItem.id,
      maskId,
      editable,
      reason: editable ? undefined : 'v1 只支持主画面 V1 蒙版在监视器中编辑',
    });
  }, [selectedItem, selectedMaskId]);

  // 监视器里点选其它蒙版时，回同步到效果控件卡片高亮。
  // 这里必须读 store 的最新值：effect 353 会在同一批 effect 中先写入 target，
  // 若用渲染快照 maskEditorState.target，就会把刚选中的蒙版改回旧值，
  // 与 effect 353 形成无限互推（添加蒙版后出现 frame 级死循环）。
  useEffect(() => {
    const target = maskEditor.getState().target;
    if (!target || !selectedItem || target.itemId !== selectedItem.id) return;
    if (target.maskId && target.maskId !== selectedMaskId) setSelectedMaskId(target.maskId);
  }, [maskEditorState.target, selectedItem, selectedMaskId]);

  useEffect(
    () => () => {
      maskEditor.setTarget(null);
    },
    [],
  );

  const commitNumber = (draft: string, fallback: number, min: number, max: number) => {
    const parsed = Number(draft);
    if (!Number.isFinite(parsed)) return fallback;
    return Math.min(max, Math.max(min, parsed));
  };

  const trigger = (label: string, patch: Partial<TimelineItem>) => {
    if (!selectedItem) {
      onNotice('先在时间线上选中一个片段');
      return;
    }
    onUpdateItem(patch, label);
    onNotice(`${label}，已写入工程`);
  };

  // 注意：这个 useMemo 必须**排在提前 return 之前**，否则"选中片段"会多跑一个 hook，
  // React 直接抛 #310（Rendered more hooks than during the previous render）整页崩。
  // EQ 的 35 项规格优先问引擎（FXA.specs('parametric_eq_full')），引擎没装时用静态副本，
  // 两者在 Effects.tsx 里由 parametricEqSpecs() 逐字段对账，不会各说各话。
  const eqAudio = useMemo<ParametricEqAudio>(() => ({
    specs(id: string) {
      const registry = (globalThis as { FXA?: { specs?: (effectId: string) => readonly typeof PARAMETRIC_EQ_SPECS[number][] } }).FXA;
      const live = registry?.specs ? registry.specs(id) : null;
      return live && live.length === PARAMETRIC_EQ_SPECS.length ? live : PARAMETRIC_EQ_SPECS;
    },
  }), []);

  if (!selectedItem && !selectedTransition) {
    return (
      <>
        <Note>效果控件显示当前选中的片段或转场。先在时间线上点选片段，或点选 17px 斜纹转场条。</Note>
        <Section title="效果栈" hint="无选择">
          <p className="ec-empty">没有选中的片段或转场，因此没有可编辑的参数。</p>
        </Section>
      </>
    );
  }

  const item = selectedTransition
    ? items.find((entry) => entry.id === selectedTransition.itemId) ?? selectedItem
    : selectedItem;
  const sequenceStart = item ? itemSequenceStart(items, item.id, transitions, fps, sourceDurations) : 0;
  const speedFactor = item && item.speed && item.speed > 0 ? item.speed : 1;
  const outputDuration = item ? itemOutputDuration(item) : 0;
  const track = item ? trackOf(item) : 1;
  const maskEditable = track === 1;
  const itemIndex = item ? items.findIndex((entry) => entry.id === item.id) : -1;
  const localTime = item ? sequenceTime - sequenceStart : 0;
  const fadeFactor = item && localTime > 0 && localTime < outputDuration ? clipFadeFactor(item, localTime) : 1;
  const cut = item ? cutAvailability(items, item.id, fps, undefined, sourceDurations) : null;
  const existingAtCut = item ? findTransition(transitions, item.id) : null;
  const transitionMin = transitionMinDuration(fps);
  const envelope = item ? normalizeVolumeKeyframes(item) : [];
  const envelopePlayhead = Math.min(outputDuration, Math.max(0, localTime));
  const audioEffects = item ? normalizeAudioEffects(item) : [];
  const masks = item ? normalizeMasks(item) ?? [] : [];

  const writeMasks = (next: MaskInstance[], label: string) => {
    trigger(label, { masks: next.length > 0 ? next : null });
  };

  const addMask = (type: MaskInstance['type']) => {
    if (!item) return;
    if (masks.length >= MAX_MASKS) {
      onNotice(`最多 ${MAX_MASKS} 个蒙版`);
      return;
    }
    const used = new Set(masks.map((mask) => mask.id));
    let index = 1;
    while (used.has(`mask-${index}`)) index += 1;
    const next: MaskInstance =
      type === 'polygon'
        ? {
            ...DEFAULT_MASK,
            id: `mask-${index}`,
            type,
            enabled: true,
            points: [
              { x: 0.3, y: 0.28 },
              { x: 0.72, y: 0.32 },
              { x: 0.68, y: 0.72 },
              { x: 0.28, y: 0.66 },
            ],
          }
        : { ...DEFAULT_MASK, id: `mask-${index}`, type, enabled: true };
    setSelectedMaskId(next.id);
    writeMasks(
      [...masks, next],
      `添加${type === 'ellipse' ? '椭圆' : type === 'polygon' ? '多边形' : '矩形'}蒙版`,
    );
  };

  const updateMask = (maskId: string, patch: Partial<MaskInstance>, label: string) => {
    writeMasks(
      masks.map((mask) => (mask.id === maskId ? { ...mask, ...patch } : mask)),
      label,
    );
  };

  const removeMask = (maskId: string) => {
    writeMasks(masks.filter((mask) => mask.id !== maskId), '删除蒙版');
  };

  const writeEffects = (next: AudioEffectInstance[], label: string) => {
    // 空效果链写 null，保持旧工程（无 effects 字段）的逐字节行为。
    trigger(label, { effects: next.length > 0 ? next : null });
  };

  const updateEffect = (
    effectId: string,
    updater: (effect: AudioEffectInstance) => AudioEffectInstance,
    label: string,
  ) => {
    writeEffects(
      audioEffects.map((effect) => (effect.id === effectId ? updater(effect) : effect)),
      label,
    );
  };

  const setEffectParam = (effect: AudioEffectInstance, spec: AudioEffectParamSpec, value: number) => {
    const clamped = clampAudioParam(spec, value);
    updateEffect(
      effect.id,
      (current) => ({ ...current, params: { ...current.params, [spec.key]: clamped } }),
      `调整${AUDIO_EFFECT_SPECS[effect.type].label} · ${spec.label} ${clamped.toFixed(spec.digits)}${
        spec.unit === ':1' ? '' : spec.unit
      }`,
    );
  };

  const effectExpanded = (effect: AudioEffectInstance) => expandedEffects[effect.id] !== false;

  const toggleEffectExpanded = (effect: AudioEffectInstance) => {
    setExpandedEffects((previous) => ({ ...previous, [effect.id]: previous[effect.id] === false }));
  };

  return (
    <>
      <div className="ec-summary">
        <div>
          <strong>{item ? `片段 ${String(itemIndex + 1).padStart(2, '0')}` : '未选片段'}</strong>
          <span>
            {item ? `V${Math.min(3, track)} · 序列 ${formatClock(sequenceStart)} · 输出 ${outputDuration.toFixed(2)}s` : '—'}
          </span>
        </div>
        <span className={`ec-badge${item?.disabled ? ' is-off' : ''}`}>{item?.disabled ? '片段已禁用' : '片段启用'}</span>
      </div>

      {selectedTransition ? (
        <Section
          title={`转场 · ${transitionKindLabel(selectedTransition.kind)}`}
          hint={`生效 ${formatSeconds(
            transitionEffectiveDuration(items, selectedTransition, fps, sourceDurations) ?? selectedTransition.duration,
          )}`}
        >
          <div className="ec-row">
            <span className="ec-row-label">类型</span>
            <div className="ec-chips">
              {KINDS.map((kind) => (
                <button
                  key={kind}
                  type="button"
                  className={`ec-chip${selectedTransition.kind === kind ? ' is-active' : ''}`}
                  onClick={() => onUpdateTransition(selectedTransition.id, { kind })}
                >
                  {transitionKindLabel(kind)}
                </button>
              ))}
            </div>
          </div>
          <div className="ec-row">
            <span className="ec-row-label">对齐</span>
            <div className="ec-chips">
              {ALIGNS.map((align) => (
                <button
                  key={align}
                  type="button"
                  className={`ec-chip${selectedTransition.align === align ? ' is-active' : ''}`}
                  onClick={() => onUpdateTransition(selectedTransition.id, { align })}
                >
                  {TRANSITION_ALIGN_LABELS[align]}
                </button>
              ))}
            </div>
          </div>
          <div className="ec-row">
            <span className="ec-row-label">时长</span>
            <div className="ec-chips">
              {DURATIONS.map((duration) => (
                <button
                  key={duration}
                  type="button"
                  className={`ec-chip${Math.abs(selectedTransition.duration - duration) < 0.001 ? ' is-active' : ''}`}
                  onClick={() => onUpdateTransition(selectedTransition.id, { duration })}
                >
                  {duration}s
                </button>
              ))}
            </div>
          </div>
          <div className="ec-row">
            <span className="ec-row-label">精调</span>
            <div className="ec-stepper">
              <button
                type="button"
                onClick={() =>
                  onUpdateTransition(selectedTransition.id, {
                    duration: Math.max(transitionMin, selectedTransition.duration - 0.1),
                  })
                }
              >
                −0.1
              </button>
              <input
                type="number"
                min={transitionMin}
                max={10}
                step={0.1}
                value={Math.round(selectedTransition.duration * 100) / 100}
                onChange={(event) => {
                  const next = Number(event.target.value);
                  if (Number.isFinite(next)) onUpdateTransition(selectedTransition.id, { duration: next });
                }}
              />
              <button
                type="button"
                onClick={() =>
                  onUpdateTransition(selectedTransition.id, {
                    duration: Math.min(10, selectedTransition.duration + 0.1),
                  })
                }
              >
                +0.1
              </button>
            </div>
            <span className="ec-hint">最小 {formatSeconds(transitionMin)}</span>
          </div>
          <div className="ec-actions">
            <button
              type="button"
              className="window-button"
              onClick={() => {
                const cutStart = sequenceStart;
                onSeek(Math.max(0, cutStart));
                onNotice('已定位到转场起点');
              }}
            >
              定位剪点
            </button>
            <button
              type="button"
              className="window-button"
              onClick={() => {
                onRemoveTransition(selectedTransition.id);
                onNotice('已移除转场');
              }}
            >
              移除转场
            </button>
          </div>
          <Note>{`剪点处的交叉淡化会压缩序列：生效 ${formatSeconds(
            transitionEffectiveDuration(items, selectedTransition, fps, sourceDurations) ?? selectedTransition.duration,
          )}，对齐语义 v1 仅保存数据，渲染按居中重叠执行。`}</Note>
        </Section>
      ) : null}

      {item ? (
        <>
          <Section title="运动" hint="Transform">
            <ParamKeyframeSlider
              item={item}
              prop="scale"
              label="缩放"
              min={0.5}
              max={3}
              step={0.05}
              suffix="×"
              playhead={envelopePlayhead}
              patch={(next, action) => trigger(action, next)}
              seekLocal={(localTime) => onSeek(sequenceStart + localTime * speedFactor)}
            />
            <ParamKeyframeSlider
              item={item}
              prop="rotation"
              label="旋转"
              min={-180}
              max={180}
              step={1}
              suffix="°"
              playhead={envelopePlayhead}
              patch={(next, action) => trigger(action, next)}
              seekLocal={(localTime) => onSeek(sequenceStart + localTime * speedFactor)}
            />
            <div className="ec-stack">
              <ParamKeyframeSlider
                item={item}
                prop="positionX"
                label="水平位置"
                min={-0.5}
                max={0.5}
                step={0.01}
                suffix=""
                playhead={envelopePlayhead}
                patch={(next, action) => trigger(action, next)}
                seekLocal={(localTime) => onSeek(sequenceStart + localTime * speedFactor)}
              />
              <ParamKeyframeSlider
                item={item}
                prop="positionY"
                label="垂直位置"
                min={-0.5}
                max={0.5}
                step={0.01}
                suffix=""
                playhead={envelopePlayhead}
                patch={(next, action) => trigger(action, next)}
                seekLocal={(localTime) => onSeek(sequenceStart + localTime * speedFactor)}
              />
            </div>
          </Section>

          <Section title="不透明度" hint={`当前 ${Math.round(paramValueAtItem(item, 'opacity', envelopePlayhead) * 100)}%`}>
            <ParamKeyframeSlider
              item={item}
              prop="opacity"
              label="不透明度"
              min={0}
              max={1}
              step={0.05}
              suffix=""
              playhead={envelopePlayhead}
              patch={(next, action) => trigger(action, next)}
              seekLocal={(localTime) => onSeek(sequenceStart + localTime * speedFactor)}
            />
            <div className="ec-actions">
              <button
                type="button"
                className="window-button"
                onClick={() => trigger('恢复片段不透明度', { opacity: 1 })}
              >
                恢复 100%
              </button>
            </div>
          </Section>

          <Section title="蒙版" hint={`${masks.length}/${MAX_MASKS} · Masks`}>
            {masks.length === 0 ? (
              <p className="ec-hint">
                暂无蒙版。添加矩形、椭圆或多边形后，可在节目监视器中直接拖拽编辑：矩形/椭圆用 8 个手柄 + 侧边圆点调羽化；多边形拖顶点、右键删顶点、钢笔工具（P）点击加点。
              </p>
            ) : null}
            <div className="ec-mask-list">
              {masks.map((mask, index) => (
                <div
                  key={mask.id}
                  className={`ec-mask-card${selectedMaskId === mask.id ? ' is-selected' : ''}`}
                  role="group"
                  aria-label={`${mask.type === 'ellipse' ? '椭圆' : mask.type === 'polygon' ? '多边形' : '矩形'}蒙版 ${index + 1}`}
                  onClick={() => {
                    setSelectedMaskId(mask.id);
                    // 同步告诉监视器：避免「监视器→面板」的回同步 effect 把这次点击改回去。
                    maskEditor.selectMask(mask.id);
                  }}
                >
                  <div className="ec-mask-head">
                    <label className="ec-mask-enable">
                      <input
                        type="checkbox"
                        checked={mask.enabled}
                        onChange={(event) =>
                          updateMask(mask.id, { enabled: event.target.checked }, `${event.target.checked ? '启用' : '关闭'}蒙版 ${index + 1}`)
                        }
                      />
                      <span>
                        {mask.type === 'ellipse' ? '椭圆蒙版' : mask.type === 'polygon' ? '多边形蒙版' : '矩形蒙版'} {index + 1}
                      </span>
                    </label>
                    <button type="button" className="ec-mask-remove" title="删除蒙版" onClick={() => removeMask(mask.id)}>
                      ×
                    </button>
                  </div>
                  <div className="ec-mask-view-row">
                    <button
                      type="button"
                      className="ec-mask-focus"
                      disabled={!maskEditable}
                      title={
                        maskEditable
                          ? '在节目监视器中拖拽蒙版、8 个缩放手柄与旋转手柄；Esc 取消当前拖动'
                          : 'v1 只支持主画面 V1 的蒙版在监视器中编辑'
                      }
                      onClick={(event) => {
                        event.stopPropagation();
                        setSelectedMaskId(mask.id);
                        maskEditor.selectMask(mask.id);
                        maskEditor.focus(mask.id);
                        if (maskEditable) {
                          onSeek(Math.min(sequenceStart + 0.05, sequenceStart + Math.max(0.05, outputDuration / 2)));
                          onNotice('已聚焦节目监视器：拖动蒙版或手柄编辑，Esc 取消');
                        }
                      }}
                    >
                      在监视器中编辑
                    </button>
                    <span className="ec-mask-view-hint">
                      {maskEditable ? '监视器：8 手柄缩放 · 圆点旋转' : '仅 V1 视频轨可编辑'}
                    </span>
                  </div>
                  <div className="ec-two">
                    <Slider
                      label="位置 X"
                      value={mask.x}
                      min={-0.5}
                      max={0.5}
                      step={0.01}
                      suffix=""
                      onChange={(value) => updateMask(mask.id, { x: value }, '调整蒙版位置')}
                    />
                    <Slider
                      label="位置 Y"
                      value={mask.y}
                      min={-0.5}
                      max={0.5}
                      step={0.01}
                      suffix=""
                      onChange={(value) => updateMask(mask.id, { y: value }, '调整蒙版位置')}
                    />
                  </div>
                  <div className="ec-two">
                    <Slider
                      label="宽度"
                      value={mask.w}
                      min={0.01}
                      max={2}
                      step={0.01}
                      suffix=""
                      onChange={(value) => updateMask(mask.id, { w: value }, '调整蒙版大小')}
                    />
                    <Slider
                      label="高度"
                      value={mask.h}
                      min={0.01}
                      max={2}
                      step={0.01}
                      suffix=""
                      onChange={(value) => updateMask(mask.id, { h: value }, '调整蒙版大小')}
                    />
                  </div>
                  <div className="ec-two">
                    <Slider
                      label="旋转"
                      value={mask.rotation}
                      min={-180}
                      max={180}
                      step={1}
                      suffix="°"
                      onChange={(value) => updateMask(mask.id, { rotation: value }, '调整蒙版旋转')}
                    />
                    <Slider
                      label="羽化"
                      value={mask.feather}
                      min={0}
                      max={100}
                      step={1}
                      suffix="%"
                      onChange={(value) => updateMask(mask.id, { feather: value }, '调整蒙版羽化')}
                    />
                  </div>
                  <Slider
                    label="不透明度"
                    value={mask.opacity}
                    min={0}
                    max={1}
                    step={0.05}
                    suffix=""
                    onChange={(value) => updateMask(mask.id, { opacity: value }, '调整蒙版不透明度')}
                  />
                </div>
              ))}
            </div>
            <div className="ec-actions">
              <button type="button" className="window-button" disabled={masks.length >= MAX_MASKS} onClick={() => addMask('rect')}>
                添加矩形
              </button>
              <button type="button" className="window-button" disabled={masks.length >= MAX_MASKS} onClick={() => addMask('ellipse')}>
                添加椭圆
              </button>
              <button
                type="button"
                className="window-button"
                disabled={masks.length >= MAX_MASKS}
                onClick={() => addMask('polygon')}
                title="添加多边形蒙版：在节目监视器里拖顶点，或用钢笔工具（P）点击加点"
              >
                添加多边形
              </button>
            </div>
          </Section>

          <Section title="时间重映射" hint="Time Remapping">
            <div className="ec-row">
              <label className="ec-row-label" htmlFor={speedInputId}>
                速度
              </label>
              <input
                id={speedInputId}
                aria-describedby={speedHintId}
                className="ec-input"
                type="number"
                min={0.25}
                max={4}
                step={0.05}
                value={speedDraft}
                onChange={(event) => setSpeedDraft(event.target.value)}
                onBlur={() => {
                  const next = commitNumber(speedDraft, item.speed ?? 1, 0.25, 4);
                  setSpeedDraft(String(next));
                  if (Math.abs(next - (item.speed ?? 1)) > 0.001) trigger(`片段速度 ${next.toFixed(2)}×`, { speed: next });
                }}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') (event.target as HTMLInputElement).blur();
                }}
              />
              <span className="ec-hint" id={speedHintId}>
                0.25×–4× · 当前输出 {outputDuration.toFixed(2)}s
              </span>
            </div>
            <div className="ec-chips">
              {[0.5, 1, 1.5, 2].map((speed) => (
                <button
                  key={speed}
                  type="button"
                  className={`ec-chip${Math.abs((item.speed ?? 1) - speed) < 0.001 ? ' is-active' : ''}`}
                  onClick={() => trigger(`片段速度 ${speed}×`, { speed })}
                >
                  {speed}×
                </button>
              ))}
            </div>
          </Section>

          <Section title="淡化" hint="Fade In / Fade Out">
            <div className="ec-two">
              <label className="ec-field">
                <span>淡入</span>
                <input
                  className="ec-input"
                  type="number"
                  min={0}
                  max={outputDuration}
                  step={0.1}
                  value={fadeInDraft}
                  onChange={(event) => setFadeInDraft(event.target.value)}
                  onBlur={() => {
                    const next = commitNumber(fadeInDraft, item.fadeIn ?? 0, 0, Math.max(0.1, outputDuration));
                    setFadeInDraft(String(next));
                    if (Math.abs(next - (item.fadeIn ?? 0)) > 0.001) trigger(`淡入 ${next.toFixed(1)}s`, { fadeIn: next });
                  }}
                />
              </label>
              <label className="ec-field">
                <span>淡出</span>
                <input
                  className="ec-input"
                  type="number"
                  min={0}
                  max={outputDuration}
                  step={0.1}
                  value={fadeOutDraft}
                  onChange={(event) => setFadeOutDraft(event.target.value)}
                  onBlur={() => {
                    const next = commitNumber(fadeOutDraft, item.fadeOut ?? 0, 0, Math.max(0.1, outputDuration));
                    setFadeOutDraft(String(next));
                    if (Math.abs(next - (item.fadeOut ?? 0)) > 0.001)
                      trigger(`淡出 ${next.toFixed(1)}s`, { fadeOut: next });
                  }}
                />
              </label>
            </div>
            <div className="ec-chips">
              {[0.3, 0.5, 1].map((duration) => (
                <button
                  key={duration}
                  type="button"
                  className="ec-chip"
                  onClick={() => {
                    const nextIn = Math.min(duration, outputDuration);
                    setFadeInDraft(String(nextIn));
                    setFadeOutDraft(String(nextIn));
                    trigger(`淡入淡出 ${nextIn.toFixed(1)}s`, { fadeIn: nextIn, fadeOut: nextIn });
                  }}
                >
                  首尾 {duration}s
                </button>
              ))}
            </div>
            <Field label="播放头处淡化系数" value={fadeFactor.toFixed(2)} />
          </Section>

          <Section title="音频" hint="Volume">
            <Slider
              label="片段音量"
              value={item.volumeDb ?? 0}
              min={-60}
              max={12}
              step={0.5}
              suffix=" dB"
              onChange={(value) => trigger('修改片段音量', { volumeDb: value })}
            />
            <div className="ec-actions">
              <button type="button" className="window-button" onClick={() => trigger('片段音量归零', { volumeDb: 0 })}>
                恢复 0 dB
              </button>
              <button type="button" className="window-button" onClick={() => trigger('静音片段', { volumeDb: -60 })}>
                静音
              </button>
            </div>
          </Section>

          <Section title="音频效果" hint={`${audioEffects.length}/${MAX_AUDIO_EFFECTS} · Effects`}>
            {audioEffects.length === 0 ? (
              <p className="ec-empty">
                暂无音频效果。添加压缩器或噪声门后，渲染会按列表顺序把效果串到片段音量与包络之间。
              </p>
            ) : (
              <div className="ec-fx-list">
                {audioEffects.map((effect, index) => {
                  const effectSpec = AUDIO_EFFECT_SPECS[effect.type];
                  const expanded = effectExpanded(effect);
                  return (
                    <div className={`ec-fx-card${effect.enabled ? '' : ' is-bypassed'}`} key={effect.id}>
                      <div className="ec-fx-head">
                        <button
                          type="button"
                          className="ec-fx-arrow"
                          aria-label={expanded ? `收起${effectSpec.label}` : `展开${effectSpec.label}`}
                          aria-expanded={expanded}
                          onClick={() => toggleEffectExpanded(effect)}
                        >
                          {expanded ? '▾' : '▸'}
                        </button>
                        <span className="ec-fx-name">
                          {effectSpec.label}
                          <i>{index + 1}</i>
                        </span>
                        <button
                          type="button"
                          role="switch"
                          aria-checked={effect.enabled}
                          aria-label={`${effectSpec.label} 启用`}
                          title={effect.enabled ? '旁路此效果（保留参数）' : '启用此效果'}
                          className={`ec-fx-toggle${effect.enabled ? ' is-on' : ''}`}
                          onClick={() =>
                            updateEffect(
                              effect.id,
                              (current) => ({ ...current, enabled: !current.enabled }),
                              `${effect.enabled ? '旁路' : '启用'}${effectSpec.label}`,
                            )
                          }
                        >
                          <span />
                        </button>
                        <button
                          type="button"
                          className="ec-fx-btn"
                          aria-label={`重置${effectSpec.label}`}
                          title="重置参数并启用（保留顺序）"
                          onClick={() =>
                            updateEffect(
                              effect.id,
                              () => ({ id: effect.id, type: effect.type, enabled: true, params: defaultAudioParams(effect.type) }),
                              `重置${effectSpec.label}`,
                            )
                          }
                        >
                          ↺
                        </button>
                        <button
                          type="button"
                          className="ec-fx-btn ec-fx-remove"
                          aria-label={`移除${effectSpec.label}`}
                          title="移除效果"
                          onClick={() => writeEffects(audioEffects.filter((entry) => entry.id !== effect.id), `移除${effectSpec.label}`)}
                        >
                          ✕
                        </button>
                      </div>
                      {expanded ? effect.type === 'parametric_eq' ? (
                        <div className="ec-fx-params">
                          <ParametricEqControls
                            audio={eqAudio}
                            params={effect.params}
                            onChange={(next) => writeEffects(audioEffects.map((entry) => (
                              entry.id === effect.id ? { ...entry, params: next } : entry
                            )), `调整${effectSpec.label}`)}
                          />
                        </div>
                      ) : (
                        <div className="ec-fx-params">
                          {effectSpec.params.map((param) => {
                            const draftKey = `${effect.id}:${param.key}`;
                            const value = effect.params[param.key] ?? param.defaultValue;
                            const draft = effectDrafts[draftKey];
                            return (
                              <div className="ec-fx-param" key={param.key}>
                                <span className="ec-fx-param-label">{param.label}</span>
                                <input
                                  className="ec-fx-range"
                                  type="range"
                                  min={param.min}
                                  max={param.max}
                                  step={param.step}
                                  value={value}
                                  aria-label={`${effectSpec.label} ${param.label}`}
                                  onChange={(event) => setEffectParam(effect, param, Number(event.target.value))}
                                />
                                <input
                                  className="ec-input ec-fx-value"
                                  type="number"
                                  min={param.min}
                                  max={param.max}
                                  step={param.step}
                                  value={draft ?? value.toFixed(param.digits)}
                                  aria-label={`${effectSpec.label} ${param.label} 数值`}
                                  onChange={(event) =>
                                    setEffectDrafts((previous) => ({ ...previous, [draftKey]: event.target.value }))
                                  }
                                  onBlur={() => {
                                    const parsed = Number(effectDrafts[draftKey] ?? value);
                                    const next = clampAudioParam(param, parsed);
                                    setEffectDrafts((previous) => {
                                      const copy = { ...previous };
                                      delete copy[draftKey];
                                      return copy;
                                    });
                                    if (Math.abs(next - value) > 1e-6) setEffectParam(effect, param, next);
                                  }}
                                  onKeyDown={(event) => {
                                    if (event.key === 'Enter') (event.target as HTMLInputElement).blur();
                                  }}
                                />
                                <span className="ec-fx-unit">{param.unit === ':1' ? ':1' : param.unit}</span>
                              </div>
                            );
                          })}
                        </div>
                      ) : null}
                    </div>
                  );
                })}
              </div>
            )}
            <div className="ec-fx-add-wrap">
              <button
                type="button"
                className="ec-fx-add"
                disabled={audioEffects.length >= MAX_AUDIO_EFFECTS}
                onClick={() => setAddEffectOpen((previous) => !previous)}
              >
                {audioEffects.length >= MAX_AUDIO_EFFECTS ? `v1 每片段最多 ${MAX_AUDIO_EFFECTS} 个效果` : '+ 添加音频效果'}
              </button>
              {addEffectOpen && audioEffects.length < MAX_AUDIO_EFFECTS ? (
                <div className="ec-fx-menu">
                  {AUDIO_EFFECT_TYPES.map((type) => (
                    <button
                      key={type}
                      type="button"
                      onClick={() => {
                        const effect = createAudioEffect(type, audioEffects);
                        writeEffects([...audioEffects, effect], `添加${AUDIO_EFFECT_SPECS[type].label}`);
                        setExpandedEffects((previous) => ({ ...previous, [effect.id]: true }));
                        setAddEffectOpen(false);
                      }}
                    >
                      {AUDIO_EFFECT_SPECS[type].label}
                      <i>{AUDIO_EFFECT_SPECS[type].filter}</i>
                    </button>
                  ))}
                </div>
              ) : null}
            </div>
            <Note>
              效果按列表顺序串联：片段音量 → 变速 → 效果链 → 音量包络 → 淡入淡出。旁路保留参数但渲染跳过；空效果链不写入工程字段。
            </Note>
          </Section>

          <Section title="音量包络" hint={envelope.length > 0 ? `${envelope.length} 个关键帧` : 'Rubber Band'}>
            {envelope.length === 0 ? (
              <p className="ec-empty">
                暂无包络。在播放头处添加关键帧后，时间线片段上会显示一条可读的音量曲线；右键时间线片段也能添加或清除。
              </p>
            ) : (
              <div className="ec-envelope-list">
                {envelope.map((point, index) => (
                  <div className="ec-envelope-row" key={`${index}-${point.time.toFixed(2)}`}>
                    <span className="ec-envelope-time">{point.time.toFixed(2)}s</span>
                    <input
                      className="ec-input"
                      type="number"
                      min={-60}
                      max={12}
                      step={0.5}
                      defaultValue={point.db.toFixed(1)}
                      aria-label={`关键帧 ${index + 1} 音量 dB`}
                      onBlur={(event) => {
                        const parsed = Number(event.target.value);
                        const clamped = Number.isFinite(parsed) ? Math.min(12, Math.max(-60, parsed)) : point.db;
                        event.target.value = clamped.toFixed(1);
                        if (Math.abs(clamped - point.db) < 0.05) return;
                        trigger(`音量关键帧 ${index + 1} 改为 ${clamped.toFixed(1)} dB`, {
                          volumeKeyframes: envelope.map((entry, entryIndex) =>
                            entryIndex === index ? { ...entry, db: clamped } : entry,
                          ),
                        });
                      }}
                    />
                    <span className="ec-envelope-unit">dB</span>
                    <button
                      type="button"
                      className="window-button"
                      onClick={() =>
                        trigger(`删除音量关键帧 ${index + 1}`, {
                          volumeKeyframes: envelope.filter((_, entryIndex) => entryIndex !== index),
                        })
                      }
                    >
                      删除
                    </button>
                  </div>
                ))}
              </div>
            )}
            <div className="ec-actions">
              <button
                type="button"
                className="window-button"
                onClick={() => {
                  const time = envelopePlayhead;
                  const points = [
                    ...envelope.filter((point) => Math.abs(point.time - time) > 0.02),
                    { time, db: envelopeDbAt(item, time) },
                  ];
                  trigger(`在 ${time.toFixed(2)}s 添加音量关键帧`, { volumeKeyframes: points });
                }}
              >
                在播放头添加关键帧
              </button>
              <button
                type="button"
                className="window-button"
                disabled={envelope.length === 0}
                onClick={() => trigger('清除音量包络', { volumeKeyframes: null })}
              >
                清除包络
              </button>
            </div>
            <Field
              label="播放头处包络增益"
              value={`${envelopeDbAt(item, envelopePlayhead).toFixed(1)} dB`}
            />
          </Section>

          <Section title="剪点转场" hint={existingAtCut ? '该剪点已有转场' : '在此剪点添加'}>
            {existingAtCut ? (
              <div className="ec-actions">
                <button
                  type="button"
                  className="window-button"
                  onClick={() => {
                    onSelectTransition(existingAtCut.id);
                    onNotice('已选中该剪点转场，可在上方编辑');
                  }}
                >
                  选中转场
                </button>
                <button
                  type="button"
                  className="window-button"
                  onClick={() => {
                    onRemoveTransition(existingAtCut.id);
                    onNotice('已移除转场');
                  }}
                >
                  移除转场
                </button>
              </div>
            ) : (
              <>
                <Note tone={cut?.available ? 'info' : 'warning'}>
                  {cut?.available ? '该片段与前一片段相邻，可添加默认转场。' : cut?.reason ?? '无法添加转场。'}
                </Note>
                <div className="ec-actions">
                  <button
                    type="button"
                    className="window-button"
                    disabled={!cut?.available}
                    onClick={() => {
                      onApplyTransition(item.id);
                      onNotice('已添加默认转场');
                    }}
                  >
                    添加默认转场
                  </button>
                </div>
              </>
            )}
          </Section>

          <Section title="效果栈" hint="Applied effects">
            <div className="ec-stack">
              <div className="ec-stack-row is-active">
                <span>运动</span>
                <b>{`${Math.round((item.zoom ?? 1) * 100)}% · ${item.rotation ?? 0}°`}</b>
              </div>
              <div className="ec-stack-row is-active">
                <span>不透明度</span>
                <b>{`${Math.round((item.opacity ?? 1) * 100)}%`}</b>
              </div>
              <div className={`ec-stack-row${(item.speed ?? 1) !== 1 ? ' is-active' : ''}`}>
                <span>时间重映射</span>
                <b>{(item.speed ?? 1) === 1 ? '未启用' : `${(item.speed ?? 1).toFixed(2)}×`}</b>
              </div>
              <div className={`ec-stack-row${(item.fadeIn ?? 0) > 0 || (item.fadeOut ?? 0) > 0 ? ' is-active' : ''}`}>
                <span>淡化</span>
                <b>{`${(item.fadeIn ?? 0).toFixed(1)}s / ${(item.fadeOut ?? 0).toFixed(1)}s`}</b>
              </div>
              <div className={`ec-stack-row${(item.volumeDb ?? 0) !== 0 ? ' is-active' : ''}`}>
                <span>音量</span>
                <b>{`${(item.volumeDb ?? 0).toFixed(1)} dB`}</b>
              </div>
              <div className={`ec-stack-row${audioEffects.length > 0 ? ' is-active' : ''}`}>
                <span>音频效果</span>
                <b>
                  {audioEffects.length === 0
                    ? '未添加'
                    : `${audioEffects.length} 个${audioEffects.some((entry) => !entry.enabled) ? ' · 含旁路' : ''}`}
                </b>
              </div>
              <div className={`ec-stack-row${(item.filter ?? 'none') !== 'none' ? ' is-active' : ''}`}>
                <span>颜色滤镜</span>
                <b>{item.filter ?? 'none'}</b>
              </div>
            </div>
          </Section>
        </>
      ) : null}
    </>
  );
}
