import type { ClipDto, EditorProject, MaskInstance, TimelineItem } from '../../api/types';
import { itemOutputDuration, sequenceLayout } from './project';

export interface ReplicaTransition {
  id: string;
  layout?: 'overlap' | 'preserve';
  hostEffectiveWindow?: unknown;
  params?: {
    duration?: number; align?: 'center' | 'start' | 'end'; direction?: number;
    panels?: number; softness?: number; invert?: boolean; color?: number; intensity?: number;
    length?: number; angle?: number; width?: number; plateaus?: number; shape?: number;
  };
}

export interface ReplicaClip extends Pick<TimelineItem, 'titleText' | 'titleFont' | 'titleSize' | 'titleColor' | 'titleX' | 'titleY' | 'graphicKind' | 'graphicBoxColor' | 'graphicBoxOpacity' | 'graphicBoxX' | 'graphicBoxY' | 'graphicBoxW' | 'graphicBoxH' | 'graphicLineThickness' | 'previewOnly'> {
  key: string;
  name: string;
  kind: 'video' | 'audio' | 'title';
  start: number;
  end: number;
  sourceIn: number;
  mediaPath: string;
  mediaFrames: number;
  speed: number;
  enabled: boolean;
  gain: number;
  fadeIn: number;
  fadeOut: number;
  transition?: ReplicaTransition;
  mask?: ReplicaMask;
  assetId?: string;
}

/** FilmCraft 复刻版 clip.mask 的字段（fc-mask.js normalize）。 */
export interface ReplicaMask {
  shape: 'ellipse' | 'rect' | 'linear';
  x: number;
  y: number;
  w: number;
  h: number;
  angle: number;
  feather: number;
  invert: boolean;
  opacity: number;
}

export interface ReplicaTrack {
  id: string;
  name: string;
  clips: ReplicaClip[];
}

export interface ReplicaSequence {
  hostBinSources?: { sourceId: string; name: string }[];
  name: string;
  fps: number;
  duration: number;
  videoTracks: ReplicaTrack[];
  audioTracks: ReplicaTrack[];
  markers: { frame: number; color: string }[];
  captions?: ReplicaCaption[];
  /** Fields the replica cannot represent yet; surfaced instead of silently dropped. */
  unsupported?: string[];
}

/** FilmCraft 复刻版 sequence.captions 的一项；start/end 为序列绝对帧。 */
export interface ReplicaCaption {
  start: number;
  end: number;
  text: string;
}

export interface ReplicaAsset {
  duration?: number;
  fps?: number;
  _ok?: boolean;
  failed?: string | null;
  el?: HTMLVideoElement | null;
}

export interface ReplicaAssetWindow {
  Media: {
    fromUrl: (url: string, name: string, kind: string) => Promise<string>;
    asset: (assetId: string) => ReplicaAsset | null;
    bind: (clip: ReplicaClip, assetId: string) => void;
  };
  App: {
    seq?: { sequence?: ReplicaSequence };
    playhead?: number;
    refreshPanels?: () => void;
    setMode?: (mode: 'edit' | 'import' | 'export') => void;
  };
  TL?: { render?: () => void };
  Monitor?: { invalidate?: () => void; draw?: () => void };
}

const VIDEO_TRACKS = 3;
const TRANSITION_IDS: Record<string, string> = {
  'catalog-wipe-up': 'wipe',
  'catalog-wipe-right': 'wipe',
  'catalog-wipe-down': 'wipe',
  'catalog-wipe-left': 'wipe',
  'linear-wipe-up': 'linear_wipe',
  'linear-wipe-right': 'linear_wipe',
  'linear-wipe-down': 'linear_wipe',
  'linear-wipe-left': 'linear_wipe',

  'cross-dissolve': 'cross_dissolve',
  'dip-to-black': 'dip_to_black',
  'dip-to-white': 'dip_to_white',
  'wipe-down': 'wipe', 'wipe-left': 'wipe', 'wipe-up': 'wipe', 'wipe-right': 'wipe',
  'additive-dissolve': 'additive_dissolve',
  'non-additive-dissolve': 'non_additive_dissolve', 'iris-box': 'iris_box', 'iris-cross': 'iris_cross',
  'iris-diamond': 'iris_diamond', 'iris-round': 'iris_round',
  'barn-doors-vertical': 'barn_doors', 'barn-doors-horizontal': 'barn_doors',
  'soft-wipe-up': 'soft_wipe', 'soft-wipe-right': 'soft_wipe', 'soft-wipe-down': 'soft_wipe', 'soft-wipe-left': 'soft_wipe',
  'gradient-wipe-up': 'gradient_wipe', 'gradient-wipe-right': 'gradient_wipe', 'gradient-wipe-down': 'gradient_wipe', 'gradient-wipe-left': 'gradient_wipe',
  'panel-wipe': 'panel_wipe', 'vr-iris-wipe': 'vr_iris_wipe', 'vr-gradient-wipe': 'vr_gradient_wipe',
  'flash': 'flash', 'ray': 'ray', 'vr-light-rays': 'vr_light_rays', 'solarize': 'solarize',
  'flare': 'flare', 'light-sweep': 'light_sweep', 'plateau-wipe': 'plateau_wipe', 'shape-flow': 'shape_flow',
};
const BLEND_TRANSITION_PARAMS: Record<string, Record<string, number | boolean>> = {
  'linear-wipe-up': { angle: 0 },
  'linear-wipe-right': { angle: 90 },
  'linear-wipe-down': { angle: 180 },
  'linear-wipe-left': { angle: 270 },

  'barn-doors-vertical': { orientation: 0 }, 'barn-doors-horizontal': { orientation: 1 },
  'soft-wipe-up': { angle: 0 }, 'soft-wipe-right': { angle: 90 }, 'soft-wipe-down': { angle: 180 }, 'soft-wipe-left': { angle: 270 },
  'gradient-wipe-up': { angle: 0, softness: 10 }, 'gradient-wipe-right': { angle: 90, softness: 10 },
  'gradient-wipe-down': { angle: 180, softness: 10 }, 'gradient-wipe-left': { angle: 270, softness: 10 },
  'panel-wipe': { panels: 6 }, 'vr-gradient-wipe': { softness: 20, invert: false },
  'flash': { color: 1, intensity: 100 }, 'ray': { length: 50 },
  'flare': { color: 1, angle: 20 }, 'light-sweep': { angle: 70, width: 160, color: 1 },
  'plateau-wipe': { angle: 90, plateaus: 5 }, 'shape-flow': { shape: 0, softness: 4 },
};
const WIPE_DIRECTIONS: Partial<Record<string, number>> = {
  'catalog-wipe-down': 0,
  'catalog-wipe-left': 1,
  'catalog-wipe-up': 2,
  'catalog-wipe-right': 3,

  'wipe-down': 0, 'wipe-left': 1, 'wipe-up': 2, 'wipe-right': 3,
};

/**
 * LiveClipper 的 EditorTransition 挂在剪点右侧片段上；复刻版 TL.transitionProgress
 * 读的是当前片段（左片段）的 transition。这里把类型映射后挂到前一个片段，
 * 让序列交接后的转场能被复刻版监视器/导出直接消费。
 */
function outgoingTransitions(project: EditorProject): Map<string, ReplicaTransition> {
  const result = new Map<string, ReplicaTransition>();
  const items = project.timeline ?? [];
  for (const transition of project.transitions ?? []) {
    if (!Object.hasOwn(TRANSITION_IDS, transition.kind)) throw new Error(`unsupported_transition:${transition.kind}`);
    const custom = !['cross-dissolve', 'dip-to-black', 'dip-to-white', 'wipe-left', 'wipe-right', 'wipe-up', 'wipe-down'].includes(transition.kind);
    const target = items.find(item => item.id === transition.itemId);
    if (custom && (transition.track !== 1 || (target && (target.track ?? 1) !== 1))) throw new Error(`unsupported_transition:${transition.kind}`);
    const ordered = items.filter((item) => (item.track ?? 1) === transition.track);
    const index = ordered.findIndex((item) => item.id === transition.itemId);
    if (index <= 0) continue;
    const effective = transition.layout === 'preserve'
      ? sequenceLayout(items, project.transitions, project.fps).cuts.find(cut => cut.transition.id === transition.id)?.effective : undefined;
    if (transition.layout === 'preserve' && effective === undefined) throw new Error('invalid_preserve_transition_window');
    result.set(ordered[index - 1].id, {
      id: TRANSITION_IDS[transition.kind],
      ...(transition.layout ? { layout: transition.layout } : {}),
      params: {
        duration: transition.layout === 'preserve' ? transition.duration : Math.max(0.05, transition.duration || 0.5),
        align: transition.align || 'center',
        ...(WIPE_DIRECTIONS[transition.kind] === undefined ? {} : { direction: WIPE_DIRECTIONS[transition.kind] }),
        ...(Object.hasOwn(BLEND_TRANSITION_PARAMS, transition.kind) ? BLEND_TRANSITION_PARAMS[transition.kind] : {}),
      },
    });
  }
  return result;
}

const clamp = (value: number, min: number, max: number): number =>
  Math.min(max, Math.max(min, Number.isFinite(value) ? value : min));

/** 复刻版当前没有对应结构的字段；必须显式暴露，不能静默丢掉。 */
function unsupportedFor(item: TimelineItem): string[] {
  const out: string[] = [];
  if (item.keyframes && Object.values(item.keyframes).some((cues) => (cues?.length ?? 0) > 0)) {
    out.push(`keyframes:${item.id}`);
  }
  if ((item.effects?.length ?? 0) > 0) out.push(`effects:${item.id}`);
  if ((item.masks ?? []).some((mask) => mask.enabled && mask.type === 'polygon')) {
    out.push(`polygonMask:${item.id}`);
  }
  const activeMask = (item.masks ?? []).find((mask) => mask.enabled);
  if (activeMask && activeMask.type !== 'polygon' && Math.abs(activeMask.rotation) > 0.01) {
    // fc-mask only uses angle for linear masks; a rotated rect/ellipse would be
    // silently wrong, so surface it instead of pretending it was mapped.
    out.push(`rotatedMask:${item.id}`);
  }
  if (item.nestedSequenceId) out.push(`nestedSequence:${item.id}`);
  return out;
}

/**
 * LiveClipper 的 mask 坐标是“相对画面中心的 -0.5..0.5 偏移 + 0..2 宽高比例”；
 * FilmCraft 复刻版 fc-mask 是“0..1 中心 + 半径”。只映射第一条启用的
 * rect/ellipse；polygon 在复刻版没有对应形状，显式跳过（不伪造形状）。
 */
function toReplicaMask(masks: MaskInstance[] | null | undefined): ReplicaMask | undefined {
  const mask = (masks ?? []).find((candidate) => candidate.enabled);
  if (!mask || mask.type === 'polygon') return undefined;
  return {
    shape: mask.type,
    x: clamp(0.5 + mask.x, -0.5, 1.5),
    y: clamp(0.5 + mask.y, -0.5, 1.5),
    w: clamp(mask.w / 2, 0.01, 1.5),
    h: clamp(mask.h / 2, 0.01, 1.5),
    angle: clamp(mask.rotation, -360, 360),
    feather: clamp(mask.feather, 0, 0.5),
    invert: false,
    opacity: clamp(mask.opacity, 0, 1),
  };
}

interface SrtCue {
  start: number;
  end: number;
  text: string;
}

const SRT_TIME = /(\d{1,2}):(\d{2}):(\d{2})[,.](\d{3})/;

function srtSeconds(hours: string, minutes: string, seconds: string, millis: string): number {
  return Number(hours) * 3600 + Number(minutes) * 60 + Number(seconds) + Number(millis) / 1000;
}

/** 解析 SRT；只保留有文本、起止合法的 cue，时间单位秒。 */
export function parseSrt(source: string): SrtCue[] {
  const cues: SrtCue[] = [];
  const blocks = source.replace(/^\uFEFF/, '').split(/\r?\n\r?\n/);
  for (const block of blocks) {
    const lines = block.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
    const timing = lines.find((line) => line.includes('-->'));
    if (!timing) continue;
    const [left, right] = timing.split('-->').map((part) => part.trim());
    const a = SRT_TIME.exec(left);
    const b = SRT_TIME.exec(right);
    if (!a || !b) continue;
    const start = srtSeconds(a[1], a[2], a[3], a[4]);
    const end = srtSeconds(b[1], b[2], b[3], b[4]);
    const text = lines.slice(lines.indexOf(timing) + 1).join('\n').trim();
    if (!text || !Number.isFinite(start) || !Number.isFinite(end) || end <= start) continue;
    cues.push({ start, end, text });
  }
  return cues;
}

/**
 * 把每个 clip 的 SRT 按 TimelineItem 的源区间裁切，再换算成序列绝对帧。
 * 复刻版 captions 是序列级帧时间，所以在片段入点之外的字幕必须被裁掉。
 */
export function buildReplicaCaptions(
  project: EditorProject,
  srtByClipId: Map<string, SrtCue[]>,
): ReplicaCaption[] {
  const fps = project.fps || 30;
  const items = project.timeline ?? [];
  const layout = sequenceLayout(items, project.transitions ?? [], fps);
  const total = Math.max(1, Math.ceil(layout.duration * fps));
  const captions: ReplicaCaption[] = [];
  const seen = new Set<string>();
  for (const item of items) {
    if ((item.track ?? 1) > VIDEO_TRACKS || item.disabled) continue;
    const cues = srtByClipId.get(item.clipId);
    if (!cues?.length) continue;
    const base = layout.starts.get(item.id) ?? 0;
    const speed = Math.max(0.01, item.speed ?? 1);
    for (const cue of cues) {
      const start = Math.max(cue.start, item.start);
      const end = Math.min(cue.end, item.end);
      if (end - start < 0.001) continue;
      const startFrame = Math.round((base + (start - item.start) / speed) * fps);
      const endFrame = Math.round((base + (end - item.start) / speed) * fps);
      const text = cue.text.trim();
      if (!text || endFrame <= startFrame) continue;
      const key = `${item.clipId}:${startFrame}:${endFrame}:${text}`;
      if (seen.has(key)) continue;
      seen.add(key);
      captions.push({
        start: clamp(startFrame, 0, total - 1),
        end: clamp(endFrame, 1, total),
        text,
      });
    }
  }
  captions.sort((a, b) => a.start - b.start || a.end - b.end);
  return captions;
}

function trackId(track: number): string {
  return track <= VIDEO_TRACKS ? `V${track}` : `A${track - VIDEO_TRACKS}`;
}

/**
 * LiveClipper 的 EditorProject 是“源素材秒 + 轨道内 gapBefore”的顺序模型；
 * 复刻版是“序列帧 + 绝对 start/end”的模型。这里用 LiveClipper 自己的
 * sequenceLayout 算出绝对起点，再换算成帧，避免另写一套布局规则。
 */
export function buildReplicaSequence(project: EditorProject, clip: ClipDto | null): ReplicaSequence {
  const fps = project.fps || 30;
  const items = project.timeline ?? [];
  const layout = sequenceLayout(items, project.transitions ?? [], fps);
  const tracks = new Map<string, ReplicaTrack>();
  const outgoing = outgoingTransitions(project);
  const title = clip?.title || 'LiveClipper 序列';

  for (const item of items) {
    const trackNo = item.track ?? 1;
    const id = trackId(trackNo);
    const kind: 'video' | 'audio' = trackNo <= VIDEO_TRACKS ? 'video' : 'audio';
    let track = tracks.get(id);
    if (!track) {
      track = { id, name: id, clips: [] };
      tracks.set(id, track);
    }
    track.clips.push(toReplicaClip(item, kind, fps, layout.starts.get(item.id) ?? 0, title, outgoing.get(item.id)));
  }

  const videoTracks = Array.from({ length: VIDEO_TRACKS }, (_, index) => {
    const id = `V${index + 1}`;
    return tracks.get(id) ?? { id, name: id, clips: [] };
  });
  const audioTracks = Array.from({ length: VIDEO_TRACKS }, (_, index) => {
    const id = `A${index + 1}`;
    return tracks.get(id) ?? { id, name: id, clips: [] };
  });

  // Absolute video uses nearest-frame endpoints, including the native minimum-one-frame clip.
  // Source/audio durations stay in the host project as exact seconds.
  const absoluteVideoFrames = items.some(item => item.sequenceStart != null)
    ? Math.max(1, Math.round(layout.duration * fps), ...Array.from(tracks.values()).flatMap(track => track.clips.map(clip => clip.end)))
    : null;
  return {
    name: title,
    fps,
    duration: absoluteVideoFrames ?? Math.max(1, Math.ceil((items.length === 0 && project.clearedEventSelections?.length ? project.emptySequenceDuration ?? layout.duration : layout.duration) * fps)),
    videoTracks,
    audioTracks,
    markers: (project.markers ?? []).map((second) => ({ frame: Math.round(second * fps), color: '#f2b84b' })),
    unsupported: items.flatMap(unsupportedFor),
  };
}

function toReplicaClip(
  item: TimelineItem,
  kind: 'video' | 'audio',
  fps: number,
  startSeconds: number,
  fallbackName: string,
  transition?: ReplicaTransition,
): ReplicaClip {
  const start = Math.max(0, Math.round(startSeconds * fps));
  const output = itemOutputDuration(item);
  const end = Math.max(start + 1, Math.round((startSeconds + output) * fps));
  const sourceIn = Math.max(0, Math.round(item.start * fps));
  const mask = toReplicaMask(item.masks);
  return {
    key: item.id,
    name: `${fallbackName} · ${item.clipId.slice(0, 8)}`,
    kind: item.kind === 'graphic' ? 'title' : kind,
    start,
    end,
    sourceIn,
    mediaPath: item.kind === 'graphic' ? '' : `/api/clips/${encodeURIComponent(item.clipId)}/video`,
    ...Object.fromEntries(['titleText', 'titleFont', 'titleSize', 'titleColor', 'titleX', 'titleY', 'graphicKind', 'graphicBoxColor', 'graphicBoxOpacity', 'graphicBoxX', 'graphicBoxY', 'graphicBoxW', 'graphicBoxH', 'graphicLineThickness', 'previewOnly']
      .filter(field => item[field as keyof TimelineItem] !== undefined && item[field as keyof TimelineItem] !== null)
      .map(field => [field, item[field as keyof TimelineItem]])),
    mediaFrames: Math.max(1, Math.round((item.end - item.start) * fps)),
    speed: item.speed ?? 1,
    enabled: !item.disabled,
    gain: item.volumeDb ?? 0,
    fadeIn: Math.max(0, Math.round((item.fadeIn ?? 0) * fps)),
    fadeOut: Math.max(0, Math.round((item.fadeOut ?? 0) * fps)),
    ...(mask ? { mask } : {}),
    ...(transition ? { transition } : {}),
  };
}

export async function hydrateReplica(
  win: ReplicaAssetWindow,
  project: EditorProject,
  clip: ClipDto | null,
  isCancelled: () => boolean = () => false,
): Promise<ReplicaSequence> {
  const sequence = buildReplicaSequence(project, clip);
  const assetIds = new Map<string, string>();
  const srtByClipId = new Map<string, SrtCue[]>();
  const clipIds = new Set((project.timeline ?? []).filter(item => item.kind !== 'graphic').map((item) => item.clipId));
  await Promise.all(Array.from(clipIds, async (clipId) => {
    if (isCancelled()) return;
    try {
      const response = await fetch(`/api/clips/${encodeURIComponent(clipId)}/srt`);
      if (response.ok) srtByClipId.set(clipId, parseSrt(await response.text()));
    } catch {
      // 字幕是可选能力；SRT 缺失不能阻断视频交接。
    }
  }));
  if (isCancelled()) return sequence;
  sequence.captions = buildReplicaCaptions(project, srtByClipId);

  for (const track of [...sequence.videoTracks, ...sequence.audioTracks]) {
    for (const item of track.clips) {
      if (isCancelled()) return sequence;
      if (item.kind === 'title') continue;
      const assetKey = `${item.kind}|${item.mediaPath}`;
      let assetId = assetIds.get(assetKey);
      if (!assetId) {
        assetId = await win.Media.fromUrl(item.mediaPath, item.name, item.kind);
        if (isCancelled()) return sequence;
        assetIds.set(assetKey, assetId);
      }
      const asset = win.Media.asset(assetId);
      if (!asset?._ok) {
        const el = asset?.el;
        throw new Error(
          `媒体未就绪：${asset?.failed || 'unknown'}；error=${el?.error?.code ?? '-'}；ready=${el?.readyState ?? '-'}；network=${el?.networkState ?? '-'}`,
        );
      }
      if (asset?.duration && asset.duration > 0) {
        item.mediaFrames = Math.max(1, Math.round(asset.duration * sequence.fps));
        // 源素材不够长时，按入点与速度把复刻版片段截到可用范围，避免
        // sourceIn + 输出长度越过 mediaFrames。
        const speed = Math.max(0.01, item.speed || 1);
        const maxFrames = Math.max(1, Math.floor((item.mediaFrames - item.sourceIn) / speed));
        if (item.end - item.start > maxFrames) item.end = item.start + maxFrames;
      }
      if (asset) {
        asset.fps = sequence.fps;
        win.Media.bind(item, assetId);
      } else {
        win.Media.bind(item, assetId);
      }
    }
  }

  if (isCancelled()) return sequence;
  const app = win.App;
  if (app.seq && typeof app.seq === 'object') {
    app.seq.sequence = sequence;
  } else {
    app.seq = { sequence };
  }
  app.playhead = 0;
  if (typeof app.refreshPanels === 'function') app.refreshPanels();
  if (typeof win.TL?.render === 'function') win.TL.render();
  if (typeof win.Monitor?.invalidate === 'function') win.Monitor.invalidate();
  else if (typeof win.Monitor?.draw === 'function') win.Monitor.draw();
  return sequence;
}
