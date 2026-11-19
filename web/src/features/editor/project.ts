import type {
  ClipDto,
  EditorPayload,
  EditorProject,
  EditorTransition,
  EditorTransitionAlign,
  EditorTransitionKind,
  MaskInstance,
  NestedSequence,
  ParamKeyframe,
  ParamKeyframeProperty,
  StudioEvent,
  TimelineItem,
  TimelineItemKind,
  TranscriptSegment,
  VolumeKeyframe,
} from '../../api/types';

export const defaultTimelineItem = (
  clipId: string,
  start: number,
  end: number,
  kind: TimelineItemKind = 'video',
): TimelineItem => ({
  id: crypto.randomUUID(),
  clipId,
  start,
  end,
  kind,
  zoom: 1,
  filter: 'none',
  motion: 'none',
  stickerAssetId: null,
  stickerX: 0.72,
  stickerY: 0.12,
  stickerWidth: 0.22,
  soundAssetId: null,
  stickerRotation: 0,
  gapBefore: 0,
  speed: 1,
  track: 1,
  rotation: 0,
  opacity: 1,
  positionX: 0,
  positionY: 0,
  disabled: false,
  volumeDb: 0,
  titleText: null,
  titleSize: 48,
  titleColor: '#FFFFFF',
  titleX: 0.5,
  titleY: 0.82,
  fadeIn: 0,
  fadeOut: 0,
  volumeKeyframes: null,
});

export const itemSpeed = (item: TimelineItem): number =>
  Math.min(4, Math.max(0.25, Number.isFinite(item.speed) && item.speed ? item.speed : 1));

/**
 * 把一段视频片段配上「匹配的音频片段」：视频标 audioDetached（预览不再重复放它自带音频），
 * 音频放到指定音频轨、与视频共享 linkGroup，于是拖动/修剪联动，且音频轨上会出现波形与音量线。
 * FilmCraft 把带音频的片段放进时间线时会自动建这条配对音轨，这里保持一致。
 */
export const linkAudioCompanion = (
  video: TimelineItem,
  audioTrack: number,
): { video: TimelineItem; audio: TimelineItem } => {
  const group = video.linkGroup ?? `link-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`;
  return {
    video: { ...video, audioDetached: true, linkGroup: group },
    audio: {
      ...video,
      id: `${video.id}-audio`,
      track: audioTrack,
      gapBefore: 0,
      kind: 'video',
      audioDetached: false,
      volumeKeyframes: null,
      linkGroup: group,
    },
  };
};

/** 视频轨（1–3）对应的匹配音频轨（A1–A3 = 4–6）。 */
export const audioTrackForVideoTrack = (videoTrack: number): number =>
  Math.min(6, Math.max(4, videoTrack + 3));

export function clipFadeFactor(item: TimelineItem, localTime: number): number {
  const duration = Math.max(0.001, itemOutputDuration(item));
  const local = Math.min(duration, Math.max(0, localTime));
  const fadeIn = Math.min(duration, Math.max(0, item.fadeIn ?? 0));
  const fadeOut = Math.min(duration, Math.max(0, item.fadeOut ?? 0));
  const inFactor = fadeIn > 0 ? Math.min(1, local / fadeIn) : 1;
  const outFactor = fadeOut > 0 ? Math.min(1, (duration - local) / fadeOut) : 1;
  return Math.max(0, Math.min(1, Math.min(inFactor, outFactor)));
}

export const trackOf = (item: TimelineItem): number =>
  Number.isInteger(item.track) && item.track! >= 1 && item.track! <= 6 ? item.track! : 1;

export const VOLUME_MIN_DB = -60;
export const VOLUME_MAX_DB = 12;
export const MAX_VOLUME_KEYFRAMES = 64;

/** 归一化音量包络：clamp 时间到输出时长、clamp dB、按时间升序、同一时间保留最后、最多 64 点。 */
export function normalizeVolumeKeyframes(item: TimelineItem): VolumeKeyframe[] {
  const span = Math.max(0, itemOutputDuration(item));
  const byTime = new Map<number, number>();
  for (const raw of item.volumeKeyframes ?? []) {
    if (!raw || !Number.isFinite(raw.time) || !Number.isFinite(raw.db)) continue;
    const time = Math.round(Math.min(span, Math.max(0, raw.time)) * 1000) / 1000;
    const db = Math.round(Math.min(VOLUME_MAX_DB, Math.max(VOLUME_MIN_DB, raw.db)) * 100) / 100;
    byTime.set(time, db);
  }
  return [...byTime.entries()]
    .map(([time, db]) => ({ time, db }))
    .sort((a, b) => a.time - b.time)
    .slice(0, MAX_VOLUME_KEYFRAMES);
}

/** 片段音频包络在输出时间轴 localTime 处的 dB 偏移；无包络返回 0。 */
export function envelopeDbAt(item: TimelineItem, localTime: number): number {
  const points = normalizeVolumeKeyframes(item);
  if (points.length === 0) return 0;
  if (localTime <= points[0].time) return points[0].db;
  const last = points[points.length - 1];
  if (localTime >= last.time) return last.db;
  for (let index = 1; index < points.length; index += 1) {
    const right = points[index];
    if (localTime <= right.time) {
      const left = points[index - 1];
      const span = right.time - left.time;
      const ratio = span <= 0 ? 1 : (localTime - left.time) / span;
      return left.db + (right.db - left.db) * ratio;
    }
  }
  return last.db;
}

/** dB → 线性增益。 */
export const dbToGain = (db: number): number => Math.pow(10, Math.min(24, Math.max(-60, db)) / 20);

/** 可关键帧的变换参数白名单（冻结顺序）。 */
export const PARAM_KEYFRAME_PROPS: ParamKeyframeProperty[] = [
  'scale',
  'rotation',
  'positionX',
  'positionY',
  'opacity',
];

export const PARAM_KEYFRAME_LABELS: Record<ParamKeyframeProperty, string> = {
  scale: '缩放',
  rotation: '旋转',
  positionX: '水平位置',
  positionY: '垂直位置',
  opacity: '不透明度',
};

/** 参数业务范围（写回工程前 clamp，后端渲染端按同一范围再 clamp 一次）。 */
export const PARAM_KEYFRAME_RANGES: Record<ParamKeyframeProperty, { min: number; max: number }> = {
  scale: { min: 0.5, max: 3 },
  rotation: { min: -180, max: 180 },
  positionX: { min: -0.5, max: 0.5 },
  positionY: { min: -0.5, max: 0.5 },
  opacity: { min: 0, max: 1 },
};

export const MAX_PARAM_KEYFRAMES = 64;
export const PARAM_KEYFRAME_MIN_GAP = 0.02;

export function clampParamValue(prop: ParamKeyframeProperty, value: number): number {
  const range = PARAM_KEYFRAME_RANGES[prop];
  const fallback = prop === 'scale' || prop === 'opacity' ? 1 : 0;
  if (!Number.isFinite(value)) return fallback;
  return Math.min(range.max, Math.max(range.min, value));
}

/** 参数在片段上的静态基准值（无关键帧时的值）。 */
export function baseParamValue(item: TimelineItem, prop: ParamKeyframeProperty): number {
  if (prop === 'scale') return Number.isFinite(item.zoom) && item.zoom > 0 ? item.zoom : 1;
  if (prop === 'rotation') return Number.isFinite(item.rotation) ? item.rotation ?? 0 : 0;
  if (prop === 'positionX') return Number.isFinite(item.positionX) ? item.positionX ?? 0 : 0;
  if (prop === 'positionY') return Number.isFinite(item.positionY) ? item.positionY ?? 0 : 0;
  return Number.isFinite(item.opacity) ? item.opacity ?? 1 : 1;
}

/** 归一化参数关键帧：clamp 时间到片段输出时长、clamp 值、按时间升序、0.02 秒内去重保留最后、最多 64 点。 */
export function normalizeParamKeyframes(
  prop: ParamKeyframeProperty,
  points: ParamKeyframe[] | null | undefined,
  span: number,
): ParamKeyframe[] {
  const maxTime = Math.max(0, Number.isFinite(span) ? span : 0);
  const sorted = (points ?? [])
    .filter((point) => point && Number.isFinite(point.time) && Number.isFinite(point.value))
    .map((point) => ({
      time: Math.round(Math.min(maxTime, Math.max(0, point.time)) * 1000) / 1000,
      value: Math.round(clampParamValue(prop, point.value) * 1000) / 1000,
    }))
    .sort((a, b) => a.time - b.time);
  const kept: ParamKeyframe[] = [];
  for (const point of sorted) {
    const previous = kept[kept.length - 1];
    if (previous && point.time - previous.time < PARAM_KEYFRAME_MIN_GAP) {
      kept[kept.length - 1] = point;
      continue;
    }
    kept.push(point);
  }
  return kept.slice(0, MAX_PARAM_KEYFRAMES);
}

export function paramKeyframesFor(item: TimelineItem, prop: ParamKeyframeProperty): ParamKeyframe[] {
  return normalizeParamKeyframes(prop, item.keyframes?.[prop] ?? null, itemOutputDuration(item));
}

/** 参数在片段输出时间轴 localTime 处的值；无关键帧返回 fallback。 */
export function paramValueAt(
  points: ParamKeyframe[],
  localTime: number,
  fallback: number,
): number {
  if (points.length === 0) return fallback;
  if (localTime <= points[0].time) return points[0].value;
  const last = points[points.length - 1];
  if (localTime >= last.time) return last.value;
  for (let index = 1; index < points.length; index += 1) {
    const right = points[index];
    if (localTime <= right.time) {
      const left = points[index - 1];
      const gap = right.time - left.time;
      const ratio = gap <= 0 ? 1 : (localTime - left.time) / gap;
      return left.value + (right.value - left.value) * ratio;
    }
  }
  return last.value;
}

/** 片段某参数在 localTime 处的实际值：有关键帧走插值，否则用静态值。 */
export function paramValueAtItem(
  item: TimelineItem,
  prop: ParamKeyframeProperty,
  localTime: number,
): number {
  const points = paramKeyframesFor(item, prop);
  return paramValueAt(points, localTime, baseParamValue(item, prop));
}

/** 片段是否有任何关键帧。 */
export function hasParamKeyframes(item: TimelineItem): boolean {
  return PARAM_KEYFRAME_PROPS.some((prop) => paramKeyframesFor(item, prop).length > 0);
}

/** 只保留至少一条关键帧的参数，返回可 JSON 化的对象；全空返回 null。 */
export function normalizeTimelineItemKeyframes(
  item: TimelineItem,
): Partial<Record<ParamKeyframeProperty, ParamKeyframe[]>> | null {
  const span = itemOutputDuration(item);
  const result: Partial<Record<ParamKeyframeProperty, ParamKeyframe[]>> = {};
  let count = 0;
  for (const prop of PARAM_KEYFRAME_PROPS) {
    const points = normalizeParamKeyframes(prop, item.keyframes?.[prop] ?? null, span);
    if (points.length === 0) continue;
    result[prop] = points;
    count += 1;
  }
  return count > 0 ? result : null;
}

export const MAX_MASKS = 4;
/** 多边形蒙版最少/最多顶点数。 */
export const MIN_MASK_POINTS = 3;
export const MAX_MASK_POINTS = 24;

export const DEFAULT_MASK: MaskInstance = {
  id: 'mask-1',
  type: 'rect',
  enabled: true,
  x: 0,
  y: 0,
  w: 0.6,
  h: 0.6,
  rotation: 0,
  feather: 0,
  opacity: 1,
};

/** 归一化多边形顶点：0..1 坐标、去非法点、至少 3 点才有效，最多 24 点。 */
export function normalizeMaskPoints(
  points: Array<{ x: number; y: number }> | null | undefined,
): Array<{ x: number; y: number }> | null {
  if (!Array.isArray(points)) return null;
  const result: Array<{ x: number; y: number }> = [];
  for (const point of points.slice(0, MAX_MASK_POINTS)) {
    if (!point || typeof point !== 'object') continue;
    const x = Number(point.x);
    const y = Number(point.y);
    if (!Number.isFinite(x) || !Number.isFinite(y)) continue;
    result.push({
      x: Math.round(Math.min(1, Math.max(0, x)) * 1000) / 1000,
      y: Math.round(Math.min(1, Math.max(0, y)) * 1000) / 1000,
    });
  }
  return result.length >= MIN_MASK_POINTS ? result : null;
}

const clampNumber = (value: unknown, min: number, max: number, fallback: number): number =>
  Number.isFinite(value) ? Math.min(max, Math.max(min, value as number)) : fallback;

/** 归一化静态蒙版：只保留合法项，clamp 全部数值字段，最多 4 条。 */
export function normalizeMasks(item: TimelineItem): MaskInstance[] | null {
  const raw = item.masks ?? [];
  const result: MaskInstance[] = [];
  for (const mask of raw.slice(0, MAX_MASKS)) {
    if (!mask || typeof mask !== 'object') continue;
    const type: MaskInstance['type'] =
      mask.type === 'ellipse' ? 'ellipse' : mask.type === 'polygon' ? 'polygon' : 'rect';
    const points = type === 'polygon' ? normalizeMaskPoints(mask.points) : null;
    // 多边形没有 3 个合法顶点时整条丢弃（不能渲染出不确定形状）。
    if (type === 'polygon' && !points) continue;
    result.push({
      id: typeof mask.id === 'string' && mask.id ? mask.id : `mask-${result.length + 1}`,
      type,
      enabled: Boolean(mask.enabled),
      x: clampNumber(mask.x, -0.5, 0.5, 0),
      y: clampNumber(mask.y, -0.5, 0.5, 0),
      w: clampNumber(mask.w, 0.01, 2, 0.6),
      h: clampNumber(mask.h, 0.01, 2, 0.6),
      rotation: clampNumber(mask.rotation, -180, 180, 0),
      feather: clampNumber(mask.feather, 0, 100, 0),
      opacity: clampNumber(mask.opacity, 0, 1, 1),
      ...(points ? { points } : {}),
    });
  }
  return result.length > 0 ? result : null;
}

/** 归一化时间线片段的嵌套序列引用 id（后端 TimelineItem.NestedSequenceId）：去空白、截断，空串回退 null。 */
export function normalizeNestedSequenceId(item: TimelineItem): string | null {
  const raw = typeof item.nestedSequenceId === 'string' ? item.nestedSequenceId.trim() : '';
  return raw ? raw.slice(0, 80) : null;
}

export const itemOutputDuration = (item: TimelineItem): number =>
  Math.max(0, item.end - item.start) / itemSpeed(item);

const TRANSITION_KINDS: EditorTransitionKind[] = [
  'cross-dissolve', 'dip-to-black', 'dip-to-white', 'wipe-left', 'wipe-right', 'wipe-up', 'wipe-down',
  'catalog-wipe-up', 'catalog-wipe-right', 'catalog-wipe-down', 'catalog-wipe-left', 'linear-wipe-up', 'linear-wipe-right', 'linear-wipe-down', 'linear-wipe-left',
  'additive-dissolve', 'non-additive-dissolve', 'iris-box', 'iris-cross', 'iris-diamond', 'iris-round',
  'barn-doors-vertical', 'barn-doors-horizontal',
  'soft-wipe-up', 'soft-wipe-right', 'soft-wipe-down', 'soft-wipe-left',
  'gradient-wipe-up', 'gradient-wipe-right', 'gradient-wipe-down', 'gradient-wipe-left',
  'panel-wipe', 'vr-iris-wipe', 'vr-gradient-wipe', 'flash', 'ray', 'vr-light-rays', 'solarize',
  'flare', 'light-sweep', 'plateau-wipe', 'shape-flow',
];
const TRANSITION_ALIGNS: EditorTransitionAlign[] = ['center', 'start', 'end'];

export function normalizeTransitionKind(kind: unknown): EditorTransitionKind {
  if (typeof kind !== 'string' || !TRANSITION_KINDS.includes(kind as EditorTransitionKind)) {
    throw new Error(`unsupported_transition:${String(kind)}`);
  }
  return kind as EditorTransitionKind;
}

/**
 * 不支持的 kind 明确拒绝；其余非法剪点过滤，并把 align/duration 归一化。
 * 每个剪点最多保留一条转场；同一 itemId 出现多条时以最后一条为准。
 */
export function normalizeTransitions(
  items: TimelineItem[],
  transitions: EditorTransition[] | null,
  fps = 30,
): EditorTransition[] {
  const byItem = new Map<string, EditorTransition>();
  for (const raw of transitions ?? []) {
    if (!raw || typeof raw !== 'object') continue;
    const kind = normalizeTransitionKind(raw.kind);
    const itemId = typeof raw.itemId === 'string' ? raw.itemId : '';
    const item = items.find((candidate) => candidate.id === itemId);
    const custom = !['cross-dissolve', 'dip-to-black', 'dip-to-white', 'wipe-left', 'wipe-right', 'wipe-up', 'wipe-down'].includes(kind);
    if (custom && (raw.track !== 1 || (item && trackOf(item) !== 1))) throw new Error(`unsupported_transition:${kind}`);
    if (!item) continue;
    const track = trackOf(item);
    if (Number.isInteger(raw.track) && raw.track !== track) continue;
    const align: EditorTransitionAlign = TRANSITION_ALIGNS.includes(raw.align) ? raw.align : 'center';
    const duration = Number.isFinite(raw.duration) ? Math.max(0, raw.duration) : DEFAULT_TRANSITION_SECONDS;
    if (raw.layout != null && !['overlap', 'preserve'].includes(raw.layout)) throw new Error('invalid_transition_layout');
    const candidate: EditorTransition = {
      id: typeof raw.id === 'string' && raw.id ? raw.id : transitionIdForItem(itemId),
      itemId,
      track,
      kind,
      ...(raw.layout ? { layout: raw.layout } : {}),
      duration,
      align,
    };
    if (transitionEffectiveDuration(items, candidate, fps) === null) continue;
    byItem.set(itemId, candidate);
  }
  return [...byItem.values()];
}

/**
 * 字幕覆盖归一化：只做形状修复与深拷贝，不改写正在输入的文本、不过滤空行、不重排。
 * undefined → 保持旧工程 JSON 不变（不新增字段）；null/非对象 → null。
 */
export function normalizeCaptionOverrides(
  raw: EditorProject['captionOverrides'],
): Record<string, TranscriptSegment[]> | null | undefined {
  if (raw === undefined) return undefined;
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const result: Record<string, TranscriptSegment[]> = {};
  for (const [clipId, value] of Object.entries(raw as Record<string, unknown>)) {
    if (!Array.isArray(value)) continue;
    result[clipId] = value.map((segment) => {
      const item = segment as { start?: unknown; end?: unknown; text?: unknown } | null;
      return {
        start: typeof item?.start === 'number' ? item.start : 0,
        end: typeof item?.end === 'number' ? item.end : 0,
        text: typeof item?.text === 'string' ? item.text : '',
      };
    });
  }
  return result;
}

export function normalizeProject(project: EditorProject, duration: number): EditorProject {
  const start = Number.isFinite(project.start) ? project.start : 0;
  const rawEnd = Number.isFinite(project.end) ? project.end : duration;
  const fps = project.fps || 30;
  const timeline = project.timeline?.map(normalizeTimelineItem) ?? null;
  const nestedSequences = normalizeNestedSequences(project.nestedSequences, fps);
  const captionOverrides = normalizeCaptionOverrides(project.captionOverrides);
  return {
    ...project,
    start,
    end: rawEnd > start ? rawEnd : Math.max(duration, start + 1),
    masterVolume: Number.isFinite(project.masterVolume) ? project.masterVolume : 1,
    ...(project.baseAudioMuted === undefined ? {} : { baseAudioMuted: project.baseAudioMuted === true }),
    mutedTracks: (project.mutedTracks ?? []).filter((track) => track >= 4 && track <= 6),
    soloedTracks: (project.soloedTracks ?? []).filter((track) => track >= 4 && track <= 6),
    hiddenTracks: (project.hiddenTracks ?? []).filter((track) => track >= 1 && track <= 3),
    lockedTracks: (project.lockedTracks ?? []).filter((track) => track >= 1 && track <= 6),
    trackOpacities:
      project.trackOpacities?.length === 3
        ? project.trackOpacities.map((value) =>
            Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 1,
          )
        : null,
    markers: [
      ...new Set(
        (project.markers ?? []).filter(
          (marker) => Number.isFinite(marker) && marker >= 0 && marker <= Math.max(duration, rawEnd),
        ),
      ),
    ].sort((a, b) => a - b),
    transitions: normalizeTransitions(timeline ?? [], project.transitions ?? null, fps),
    timeline: timeline?.map((item) => alignNestedItemDuration(item, nestedSequences, fps)) ?? null,
    nestedSequences,
    ...(captionOverrides === undefined ? {} : { captionOverrides }),
  };
}

/** 片段字段归一化（主时间线与嵌套子序列共用；不改写数据所有权）。 */
export function normalizeTimelineItem(item: TimelineItem): TimelineItem {
  // Graphic geometry/previewOnly stay lossless via the spread; bridge and projection validate the shared TimelineItem fields.
  return {
    ...item,
    kind: item.kind ?? 'video',
    zoom: item.zoom ?? 1,
    filter: item.filter ?? 'none',
    motion: item.motion ?? 'none',
    stickerAssetId: item.stickerAssetId ?? null,
    stickerX: item.stickerX ?? 0.72,
    stickerY: item.stickerY ?? 0.12,
    stickerWidth: item.stickerWidth ?? 0.22,
    soundAssetId: item.soundAssetId ?? null,
    stickerRotation: item.stickerRotation ?? 0,
    gapBefore: Math.max(0, item.gapBefore ?? 0),
    speed: Math.min(4, Math.max(0.25, Number.isFinite(item.speed) && item.speed ? item.speed : 1)),
    track: Number.isInteger(item.track) && item.track! >= 1 && item.track! <= 6 ? item.track! : 1,
    rotation: Number.isFinite(item.rotation) ? Math.min(180, Math.max(-180, item.rotation ?? 0)) : 0,
    opacity: Number.isFinite(item.opacity) ? Math.min(1, Math.max(0, item.opacity ?? 1)) : 1,
    positionX: Number.isFinite(item.positionX) ? Math.min(0.5, Math.max(-0.5, item.positionX ?? 0)) : 0,
    positionY: Number.isFinite(item.positionY) ? Math.min(0.5, Math.max(-0.5, item.positionY ?? 0)) : 0,
    disabled: Boolean(item.disabled),
    volumeDb: Number.isFinite(item.volumeDb) ? Math.min(12, Math.max(-60, item.volumeDb ?? 0)) : 0,
    linkGroup: item.linkGroup ?? null,
    audioDetached: Boolean(item.audioDetached),
    titleText: item.titleText?.slice(0, 120) ?? null,
    titleSize: Number.isFinite(item.titleSize) ? Math.min(160, Math.max(12, item.titleSize ?? 48)) : 48,
    titleColor: /^#[A-Fa-f0-9]{6}$/.test(item.titleColor ?? '') ? item.titleColor! : '#FFFFFF',
    titleX: Number.isFinite(item.titleX) ? Math.min(1, Math.max(0, item.titleX ?? 0.5)) : 0.5,
    titleY: Number.isFinite(item.titleY) ? Math.min(1, Math.max(0, item.titleY ?? 0.82)) : 0.82,
    fadeIn: Number.isFinite(item.fadeIn) ? Math.min(10, Math.max(0, item.fadeIn ?? 0)) : 0,
    fadeOut: Number.isFinite(item.fadeOut) ? Math.min(10, Math.max(0, item.fadeOut ?? 0)) : 0,
    volumeKeyframes: (() => {
      const points = normalizeVolumeKeyframes(item);
      return points.length > 0 ? points : null;
    })(),
    keyframes: normalizeTimelineItemKeyframes(item),
    masks: normalizeMasks(item),
    nestedSequenceId: normalizeNestedSequenceId(item),
  };
}

/**
 * 归一化嵌套序列字典（后端契约：Dictionary&lt;string, NestedSequence&gt;，键=序列 id）。
 * - 键与 seq.id 都可用，优先 seq.id；name 为空回退「子序列 N」；
 * - timeline 逐条走 normalizeTimelineItem（v1 只读，不递归展开多级嵌套）；
 * - fps 非法回退主序列 fps（再退 30），resolution clamp 0..7680；
 * - 没有有效序列时返回 null，旧工程 JSON 保持不带 nestedSequences。
 */
export function normalizeNestedSequences(
  raw: EditorProject['nestedSequences'],
  fallbackFps = 30,
): Record<string, NestedSequence> | null {
  if (!raw || typeof raw !== 'object') return null;
  const result: Record<string, NestedSequence> = {};
  for (const [key, value] of Object.entries(raw)) {
    if (!value || typeof value !== 'object') continue;
    const rawId = typeof value.id === 'string' && value.id.trim() ? value.id.trim() : key.trim();
    const id = rawId.slice(0, 80);
    if (!id || result[id]) continue;
    const timelineRaw = Array.isArray(value.timeline) ? value.timeline : [];
    const timeline = timelineRaw
      .filter((item): item is TimelineItem => Boolean(item) && typeof item === 'object')
      .map(normalizeTimelineItem);
    const fps = Number.isFinite(value.fps) && value.fps > 0 ? value.fps : fallbackFps;
    result[id] = {
      id,
      name: (
        typeof value.name === 'string' && value.name.trim()
          ? value.name.trim()
          : `子序列 ${Object.keys(result).length + 1}`
      ).slice(0, 80),
      timeline,
      transitions: normalizeTransitions(timeline, value.transitions ?? null, fps > 0 ? fps : 30),
      fps: fps > 0 ? fps : 30,
      resolution: Number.isInteger(value.resolution)
        ? Math.min(7680, Math.max(0, value.resolution))
        : 0,
    };
  }
  return Object.keys(result).length > 0 ? result : null;
}

/**
 * 嵌套片段显示时长以子序列计算值为准（后端 v1 契约没有 duration 字段）：
 * 加载时把片段 end 对齐到 start + 子序列时长并固定 speed=1，
 * 让时间线占位、节目监视器总时长与渲染 flatten 结果一致。
 */
export function alignNestedItemDuration(
  item: TimelineItem,
  nestedSequences: Record<string, NestedSequence> | null,
  fallbackFps = 30,
): TimelineItem {
  const sequenceId = item.nestedSequenceId;
  if (!sequenceId || !nestedSequences) return item;
  const sequence = nestedSequences[sequenceId];
  if (!sequence) return item;
  const duration = sequenceDuration(
    sequence.timeline,
    sequence.transitions ?? [],
    sequence.fps || fallbackFps,
  );
  if (!(duration > 0)) return item;
  return { ...item, speed: 1, end: item.start + duration };
}

export function clipTitle(clip: ClipDto): string {
  if (clip.title?.trim()) return clip.title.trim();
  const filename = clip.path.split(/[\\/]/).pop() ?? clip.id;
  return filename.replace(/\.[^.]+$/, '');
}

export function clipFileName(clip: ClipDto): string {
  return clip.path.split(/[\\/]/).pop() ?? clip.path;
}

export function effectiveTimeline(project: EditorProject | null, clip: ClipDto | null, duration: number): TimelineItem[] {
  if (project?.timeline?.length) return project.timeline;
  if (!clip || !Number.isFinite(duration) || duration <= 0) return [];
  return [
    {
      ...defaultTimelineItem(clip.id, 0, duration),
      id: `source-${clip.id}`,
    },
  ];
}

export type SequenceTransitionCut = {
  transition: EditorTransition;
  previous: TimelineItem;
  next: TimelineItem;
  /** 生效时长：已按两侧片段长度、对齐三态与最小 1 帧 clamp。 */
  effective: number;
  /** 压缩后的转场带起点（序列秒）；带区间是 [start, start + effective]。 */
  start: number;
  end: number;
  /** 对齐三态（已归一化，与 C# TransitionCut.Align 一致）。 */
  align: EditorTransitionAlign;
  /** 该剪点让序列缩短的秒数 Δ：center=effective、start=effective/2、end=1.5×effective。 */
  shift: number;
};

export type SequenceLayout = {
  starts: Map<string, number>;
  overlaps: Map<string, number>;
  cuts: SequenceTransitionCut[];
  trackDurations: Map<number, number>;
  duration: number;
};

export const DEFAULT_TRANSITION_SECONDS = 1;

export const transitionMinDuration = (fps: number): number =>
  1 / (Number.isFinite(fps) && fps > 0 ? fps : 30);

export const TRANSITION_KIND_LABELS: Record<EditorTransitionKind, string> = {
  'cross-dissolve': '交叉淡化',
  'dip-to-black': '闪黑',
  'dip-to-white': '闪白',
  'catalog-wipe-up': '目录擦除 · 向上',
  'catalog-wipe-right': '目录擦除 · 向右',
  'catalog-wipe-down': '目录擦除 · 向下',
  'catalog-wipe-left': '目录擦除 · 向左',
  'linear-wipe-up': '线性擦除 · 向上',
  'linear-wipe-right': '线性擦除 · 向右',
  'linear-wipe-down': '线性擦除 · 向下',
  'linear-wipe-left': '线性擦除 · 向左',
  'wipe-left': '从右向左擦除',
  'wipe-right': '从左向右擦除',
  'wipe-up': '从下向上擦除',
  'wipe-down': '从上向下擦除',
  'additive-dissolve': '叠加溶解', 'non-additive-dissolve': '非叠加溶解',
  'iris-box': '矩形光圈', 'iris-cross': '十字光圈', 'iris-diamond': '菱形光圈', 'iris-round': '圆形光圈',
  'barn-doors-vertical': '垂直对开门', 'barn-doors-horizontal': '水平对开门',
  'soft-wipe-up': '向上柔和擦除', 'soft-wipe-right': '向右柔和擦除', 'soft-wipe-down': '向下柔和擦除', 'soft-wipe-left': '向左柔和擦除',
  'gradient-wipe-up': '向上渐变擦除', 'gradient-wipe-right': '向右渐变擦除', 'gradient-wipe-down': '向下渐变擦除', 'gradient-wipe-left': '向左渐变擦除',
  'panel-wipe': '面板擦除', 'vr-iris-wipe': '全景光圈擦除', 'vr-gradient-wipe': '全景渐变擦除',
  'flash': '闪光', 'ray': '光线', 'vr-light-rays': '全景光线', 'solarize': '曝光反转',
  'flare': '耀斑', 'light-sweep': '扫光', 'plateau-wipe': '阶梯擦除', 'shape-flow': '形状流动',
};

export const transitionKindLabel = (kind: EditorTransitionKind): string =>
  TRANSITION_KIND_LABELS[kind] ?? TRANSITION_KIND_LABELS['cross-dissolve'];

export const TRANSITION_ALIGN_LABELS: Record<EditorTransitionAlign, string> = {
  center: '居中',
  start: '起点对齐',
  end: '终点对齐',
};

export type TransitionDefaults = {
  kind: EditorTransitionKind;
  /** 默认视频转场时长（秒）。 */
  duration: number;
  /** 默认音频转场时长（秒）。 */
  audioDuration: number;
};

export const DEFAULT_TRANSITION_DEFAULTS: TransitionDefaults = {
  kind: 'cross-dissolve',
  duration: DEFAULT_TRANSITION_SECONDS,
  audioDuration: 1,
};

const TRANSITION_DEFAULT_DURATIONS = [0.3, 0.5, 1, 2];

/** 只接受已知 kind 与有限正时长；损坏的本地设置回退默认值，避免出现 NaN 转场。 */
export function normalizeTransitionDefaults(raw: unknown): TransitionDefaults {
  const source = (raw ?? {}) as Partial<Record<keyof TransitionDefaults, unknown>>;
  const kind =
    typeof source.kind === 'string' && source.kind in TRANSITION_KIND_LABELS
      ? (source.kind as EditorTransitionKind)
      : DEFAULT_TRANSITION_DEFAULTS.kind;
  const pickDuration = (value: unknown): number => {
    if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) {
      return DEFAULT_TRANSITION_DEFAULTS.duration;
    }
    const closest = TRANSITION_DEFAULT_DURATIONS.reduce((best, choice) =>
      Math.abs(choice - value) < Math.abs(best - value) ? choice : best,
    );
    return Math.abs(closest - value) < 0.001 ? value : closest;
  };
  return {
    kind,
    duration: pickDuration(source.duration),
    audioDuration: pickDuration(source.audioDuration),
  };
}

export const transitionAlignLabel = (align: EditorTransitionAlign): string =>
  TRANSITION_ALIGN_LABELS[align] ?? TRANSITION_ALIGN_LABELS.center;

export const transitionIdForItem = (itemId: string): string => `transition-${itemId}`;

export const trackItems = (items: TimelineItem[], track: number): TimelineItem[] =>
  items.filter((item) => trackOf(item) === track);

export function transitionPreviousItem(
  items: TimelineItem[],
  transition: EditorTransition,
): TimelineItem | null {
  const ordered = trackItems(items, transition.track);
  const index = ordered.findIndex((item) => item.id === transition.itemId);
  return index > 0 ? ordered[index - 1] : null;
}

const transitionAlignOf = (transition: EditorTransition): EditorTransitionAlign =>
  TRANSITION_ALIGNS.includes(transition.align) ? transition.align : 'center';

/**
 * 对齐三态对前一片段可见尾部的调整（秒，规格 §10）：
 * start 需要多取 d/2 的尾部 handle，end 会裁掉 d/2，center 不改变。
 */
export const transitionTailAdjust = (
  align: EditorTransitionAlign,
  effective: number,
): number => (align === 'start' ? effective / 2 : align === 'end' ? -effective / 2 : 0);

/** 对齐三态让序列缩短的秒数 Δ=d−tailAdjust：center=d、start=d/2、end=3d/2（规格 §10）。 */
export const transitionShift = (align: EditorTransitionAlign, effective: number): number =>
  effective - transitionTailAdjust(align, effective);

export type TransitionSpan = {
  /** 转场带相对剪点 cut 的起点（秒，负值表示在 cut 之前）。 */
  start: number;
  /** 转场带相对剪点 cut 的终点（秒）。 */
  end: number;
  /** 转场带宽度，等于生效时长。 */
  overlap: number;
  /** 前段尾部需要裁掉的时长（终点对齐 d/2，其余 0）。 */
  trimPrevious: number;
  /** 后段头部需要裁掉的时长（三态均为 0，保留字段与 C# 对齐）。 */
  trimNext: number;
  /** 前段尾部需要额外素材的时长（起点对齐 d/2，其余 0）。 */
  extendPrevious: number;
  /** 该剪点让序列缩短的秒数 Δ。 */
  sequenceShorten: number;
};

/**
 * 返回转场带相对剪点 cut 的区间与对两侧片段的裁剪/延长量（规格 §10），非法时返回 null。
 * center：[-d/2,+d/2]；start（起点落在 cut）：[0,+d]；end（终点落在 cut）：[-d,0]。
 */
export function transitionSpan(
  transition: EditorTransition,
  previous: TimelineItem,
  next: TimelineItem,
  fps = 30,
  sourceDurations?: Record<string, number>,
): TransitionSpan | null {
  const effective = transitionEffectiveDuration([previous, next], transition, fps, sourceDurations);
  if (effective === null) return null;
  const align = transitionAlignOf(transition);
  const half = transition.layout === 'preserve' ? Math.floor(Math.round(effective * fps) / 2) / fps : effective / 2;
  const start = align === 'start' ? 0 : align === 'end' ? -effective : -half;
  const end = align === 'start' ? effective : align === 'end' ? 0 : effective - half;
  return {
    start,
    end,
    overlap: effective,
    trimPrevious: transition.layout !== 'preserve' && align === 'end' ? half : 0,
    trimNext: 0,
    extendPrevious: transition.layout !== 'preserve' && align === 'start' ? half : 0,
    sequenceShorten: transition.layout === 'preserve' ? 0 : transitionShift(align, effective),
  };
}

/**
 * 转场合法时返回 clamp 后的生效时长（秒），否则返回 null。
 * 合法条件见 docs/transitions-spec.md §2；对齐三态的内容约束见 §10（end 需前段 2/3 时长，
 * start 需前段尾部 d/2 handle，只有传入 sourceDurations 时才能按真实源时长 clamp）。
 */
export function transitionEffectiveDuration(
  items: TimelineItem[],
  transition: EditorTransition,
  fps: number,
  sourceDurations?: Record<string, number>,
): number | null {
  const next = items.find((item) => item.id === transition.itemId);
  if (!next || trackOf(next) !== transition.track || next.disabled) return null;
  const previous = transitionPreviousItem(items, transition);
  if (!previous || previous.disabled) return null;
  if (Math.max(0, next.gapBefore ?? 0) > 0.001) return null;
  let maxDuration = Math.min(itemOutputDuration(previous), itemOutputDuration(next));
  if (transition.layout === 'preserve') {
    if (transition.track !== 1) throw new Error('preserve_transition_requires_v1');
    const rate = fps > 0 ? fps : 30;
    const maxFrames = Math.floor(maxDuration * rate + 1e-6);
    if (maxFrames < 1) return null;
    return Math.min(maxFrames, Math.max(1, Math.round(transition.duration * rate))) / rate;
  }
  const align = transitionAlignOf(transition);
  if (align === 'end') {
    maxDuration = Math.min(maxDuration, (itemOutputDuration(previous) * 2) / 3);
  } else if (align === 'start') {
    const sourceDuration = sourceDurations?.[previous.clipId];
    if (Number.isFinite(sourceDuration)) {
      const handle = Math.max(0, (sourceDuration! - previous.end) / itemSpeed(previous));
      maxDuration = Math.min(maxDuration, handle * 2);
    }
  }
  const minDuration = transitionMinDuration(fps);
  if (!(maxDuration >= minDuration)) return null;
  const requested = Number.isFinite(transition.duration) ? transition.duration : 0;
  return Math.min(maxDuration, Math.max(minDuration, requested));
}

export function findTransition(
  transitions: EditorTransition[] | null | undefined,
  itemId: string | null | undefined,
): EditorTransition | null {
  if (!itemId) return null;
  return transitions?.find((transition) => transition.itemId === itemId) ?? null;
}

/** 返回该轨道某剪点上能否创建转场，以及不能创建时的原因。 */
export function cutAvailability(
  items: TimelineItem[],
  itemId: string,
  fps: number,
  transition?: EditorTransition | null,
  sourceDurations?: Record<string, number>,
): { available: boolean; reason: string | null; previous: TimelineItem | null; next: TimelineItem | null } {
  const next = items.find((item) => item.id === itemId) ?? null;
  if (!next) return { available: false, reason: '找不到该片段', previous: null, next: null };
  if (next.disabled) return { available: false, reason: '片段已禁用', previous: null, next };
  const ordered = trackItems(items, trackOf(next));
  const index = ordered.findIndex((item) => item.id === itemId);
  const previous = index > 0 ? ordered[index - 1] : null;
  if (!previous) return { available: false, reason: '这是轨道上的第一个片段', previous: null, next };
  if (previous.disabled) return { available: false, reason: '前一个片段已禁用', previous, next };
  if (Math.max(0, next.gapBefore ?? 0) > 0.001) {
    return { available: false, reason: '剪点前有空隙，先清除空隙再加转场', previous, next };
  }
  const minDuration = transitionMinDuration(fps);
  if (Math.min(itemOutputDuration(previous), itemOutputDuration(next)) < minDuration) {
    return { available: false, reason: '片段太短，放不下 1 帧转场', previous, next };
  }
  // 对齐三态的内容约束（规格 §10）：只有在调用方给了转场时才检查，保持旧的 3 参数调用行为不变。
  if (transition) {
    const align = transitionAlignOf(transition);
    if (align === 'end' && (itemOutputDuration(previous) * 2) / 3 < minDuration) {
      return { available: false, reason: '前一个片段太短，终点对齐放不下转场', previous, next };
    }
    if (align === 'start') {
      const sourceDuration = sourceDurations?.[previous.clipId];
      if (Number.isFinite(sourceDuration)) {
        const handle = Math.max(0, (sourceDuration! - previous.end) / itemSpeed(previous));
        if (handle * 2 < minDuration) {
          return { available: false, reason: '前一个片段尾部没有足够素材用于起点对齐', previous, next };
        }
      }
    }
  }
  return { available: true, reason: null, previous, next };
}

export function sequenceLayout(
  items: TimelineItem[],
  transitions?: EditorTransition[] | null,
  fps = 30,
  sourceDurations?: Record<string, number>,
): SequenceLayout {
  const starts = new Map<string, number>();
  const overlaps = new Map<string, number>();
  const cuts: SequenceTransitionCut[] = [];
  const trackDurations = new Map<number, number>();
  const byItem = new Map((transitions ?? []).map((transition) => [transition.itemId, transition]));
  let duration = 0;
  for (const track of [1, 2, 3, 4, 5, 6]) {
    const ordered = trackItems(items, track);
    if (ordered.some(item => item.sequenceStart != null)) {
      if (ordered.some(item => item.sequenceStart == null || !Number.isFinite(item.sequenceStart) || item.sequenceStart! < 0 || (item.gapBefore ?? 0) !== 0)) throw new Error('invalid_absolute_sequence_layout');
      if ((transitions ?? []).some(t => t.track === track && t.layout !== 'preserve')) throw new Error('absolute_sequence_transition_unsupported');
      let end = 0;
      for (const item of ordered) {
        starts.set(item.id, item.sequenceStart!);
        end = Math.max(end, item.sequenceStart! + itemOutputDuration(item));
      }
      for (const transition of (transitions ?? []).filter(t => t.track === track)) {
        const next = ordered.find(item => item.id === transition.itemId);
        const previous = next && ordered[ordered.indexOf(next) - 1];
        if (!next || !previous || Math.abs(previous.sequenceStart! + itemOutputDuration(previous) - next.sequenceStart!) > 1e-8) throw new Error('preserve_transition_requires_adjacent_v1');
        const effective = transitionEffectiveDuration(items, transition, fps, sourceDurations);
        if (effective === null) throw new Error('preserve_transition_not_effective');
        const align = transitionAlignOf(transition);
        const before = align === 'start' ? 0 : align === 'end' ? effective : Math.floor(Math.round(effective * fps) / 2) / fps;
        const start = next.sequenceStart! - before;
        overlaps.set(next.id, effective);
        cuts.push({ transition, previous, next, align, effective, start, end: start + effective, shift: 0 });
      }
      trackDurations.set(track, end);
      duration = Math.max(duration, end);
      continue;
    }
    let raw = 0;
    let shift = 0;
    for (const item of ordered) {
      raw += Math.max(0, item.gapBefore ?? 0);
      const transition = byItem.get(item.id);
      if (transition && transition.track === track) {
        const previous = transitionPreviousItem(items, transition);
        const candidate = transitionEffectiveDuration(items, transition, fps, sourceDurations);
        if (previous && candidate !== null) {
          const align = transitionAlignOf(transition);
          const preserve = transition.layout === 'preserve';
          const shiftOfCut = preserve ? 0 : transitionShift(align, candidate);
          const before = align === 'start' ? 0 : align === 'end' ? candidate : Math.floor(Math.round(candidate * fps) / 2) / fps;
          const start = raw - shift - (preserve ? before : shiftOfCut);
          overlaps.set(item.id, candidate);
          shift += shiftOfCut;
          cuts.push({
            transition,
            previous,
            next: item,
            align,
            effective: candidate,
            start,
            end: start + candidate,
            shift: shiftOfCut,
          });
        }
      }
      starts.set(item.id, raw - shift);
      raw += itemOutputDuration(item);
    }
    trackDurations.set(track, raw - shift);
    duration = Math.max(duration, raw - shift);
  }
  return { starts, overlaps, cuts, trackDurations, duration };
}

export function transitionCuts(
  items: TimelineItem[],
  transitions?: EditorTransition[] | null,
  fps = 30,
  sourceDurations?: Record<string, number>,
): SequenceTransitionCut[] {
  return sequenceLayout(items, transitions, fps, sourceDurations).cuts;
}

export function trackDuration(
  items: TimelineItem[],
  track: number,
  transitions?: EditorTransition[] | null,
  fps = 30,
  sourceDurations?: Record<string, number>,
): number {
  return sequenceLayout(items, transitions, fps, sourceDurations).trackDurations.get(track) ?? 0;
}

export function sequenceDuration(
  items: TimelineItem[],
  transitions?: EditorTransition[] | null,
  fps = 30,
  sourceDurations?: Record<string, number>,
): number {
  return sequenceLayout(items, transitions, fps, sourceDurations).duration;
}

export function itemSequenceStart(
  items: TimelineItem[],
  itemId: string,
  transitions?: EditorTransition[] | null,
  fps = 30,
  sourceDurations?: Record<string, number>,
): number {
  return sequenceLayout(items, transitions, fps, sourceDurations).starts.get(itemId) ?? 0;
}

export function projectSequenceDuration(
  project: EditorProject | null,
  clip: ClipDto | null,
  sourceDuration: number,
): number {
  const items = effectiveTimeline(project, clip, sourceDuration);
  const total = sequenceDuration(items, project?.transitions, project?.fps || 30);
  return total > 0 ? total : Math.max(0, project?.end ?? sourceDuration);
}

export function itemForSequence(
  items: TimelineItem[],
  sequenceTime: number,
  transitions?: EditorTransition[] | null,
  fps = 30,
): {
  item: TimelineItem;
  index: number;
  local: number;
  offset: number;
} | null {
  if (items.length === 0) return null;
  const layout = sequenceLayout(items, transitions, fps);
  const clamped = Math.max(0, sequenceTime);
  for (const track of [3, 2, 1]) {
    const ordered = trackItems(items, track);
    let chosen: { item: TimelineItem; start: number } | null = null;
    if (ordered.some(item => item.sequenceStart != null)) {
      const first = ordered.find(item => !item.disabled && clamped >= (layout.starts.get(item.id) ?? 0) && clamped < (layout.starts.get(item.id) ?? 0) + itemOutputDuration(item));
      if (first) {
        const start = layout.starts.get(first.id)!;
        return { item: first, index: items.indexOf(first), local: clamped - start, offset: start };
      }
      continue;
    }
    for (const item of ordered) {
      const start = layout.starts.get(item.id) ?? 0;
      if (clamped + 1e-6 >= start) chosen = { item, start };
      else break;
    }
    if (!chosen) continue;
    const length = itemOutputDuration(chosen.item);
    // 播放头落在空隙里（片段尾部之后、下一片段之前）：该轨此刻没有画面，
    // 返回 null 让调用方显示黑场，而不是把上一片段最后一帧当成“当前片段”。
    if (clamped > chosen.start + length - 1e-6) continue;
    return {
      item: chosen.item,
      index: items.indexOf(chosen.item),
      local: Math.min(length, Math.max(0, clamped - chosen.start)),
      offset: chosen.start,
    };
  }
  return null;
}

export function sequenceTimeForSource(
  items: TimelineItem[],
  sourceTime: number,
  transitions?: EditorTransition[] | null,
  fps = 30,
): number | null {
  const layout = sequenceLayout(items, transitions, fps);
  for (const item of trackItems(items, 1)) {
    if (sourceTime >= item.start - 0.02 && sourceTime <= item.end + 0.02) {
      const speed = itemSpeed(item);
      const length = itemOutputDuration(item);
      const start = layout.starts.get(item.id) ?? 0;
      return start + Math.min(length, Math.max(0, sourceTime - item.start) / speed);
    }
  }
  return null;
}

export function formatClock(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '00:00';
  const whole = Math.floor(seconds);
  const hours = Math.floor(whole / 3600);
  const minutes = Math.floor((whole % 3600) / 60);
  const rest = whole % 60;
  return hours > 0
    ? `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}`
    : `${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}`;
}

export function formatTimecode(seconds: number, fps = 30): string {
  const safe = Math.max(0, Number.isFinite(seconds) ? seconds : 0);
  const safeFps = Number.isFinite(fps) && fps > 0 ? Math.round(fps) : 30;
  // 先按「总帧数四舍五入」再拆秒/帧：直接 floor((safe - whole) * fps) 会被浮点误差
  // 吃掉一帧（1.0666… * 30 = 31.999999999999996），逐帧步进时时间码会出现 01,01,03,03 的跳变。
  const totalFrames = Math.round(safe * safeFps);
  const whole = Math.floor(totalFrames / safeFps);
  const frames = totalFrames % safeFps;
  return `${formatClock(whole)}:${String(frames).padStart(2, '0')}`;
}

export function eventToCandidate(event: StudioEvent): {
  id: string;
  start: string;
  end: string;
  startSeconds: number;
  endSeconds: number;
  title: string;
  rationale: string;
  quote: string;
  source: string;
} {
  return {
    id: event.id,
    start: formatClock(event.start),
    end: formatClock(event.end),
    startSeconds: event.start,
    endSeconds: event.end,
    title: event.title || '未命名候选',
    rationale: event.summary || '来自当前切片的真实候选事件。',
    quote: event.hook || event.summary || '（此候选没有额外说明）',
    source: event.source,
  };
}

export function buildProjectAfterApplying(
  project: EditorProject,
  clip: ClipDto,
  chosen: StudioEvent[],
): EditorProject {
  const freshItems = chosen
    .slice()
    .sort((a, b) => a.start - b.start)
    .map((event) => defaultTimelineItem(clip.id, event.start, event.end));
  if (freshItems.length === 0) return project;

  if (!project.timeline?.length) {
    const start = Math.min(...freshItems.map((item) => item.start));
    const end = Math.max(...freshItems.map((item) => item.end));
    return { ...project, start, end, timeline: freshItems };
  }

  const existing = project.timeline;
  const merged = [...existing];
  for (const item of freshItems) {
    const overlap = existing.some(
      (current) => Math.abs(current.start - item.start) < 0.02 && Math.abs(current.end - item.end) < 0.02,
    );
    if (!overlap) merged.push(item);
  }
  return {
    ...project,
    start: Math.min(project.start, ...merged.map((item) => item.start)),
    end: Math.max(project.end, ...merged.map((item) => item.end)),
    timeline: merged,
  };
}

export function editorSummary(payload: EditorPayload): {
  width: number;
  height: number;
  duration: number;
  transcriptSegments: number;
} {
  return {
    width: payload.width,
    height: payload.height,
    duration: payload.duration,
    transcriptSegments: payload.transcript?.segments.length ?? 0,
  };
}
