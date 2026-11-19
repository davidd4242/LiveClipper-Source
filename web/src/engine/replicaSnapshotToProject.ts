import type { EditorProject, EditorTransition, TimelineCaption, TimelineEventPlacement, TimelineEventSelectionClear, TimelineItem } from '../api/types';
import { itemOutputDuration, sequenceLayout } from '../features/editor/project';
import type { FullEngineDocument } from './fullEngineSession';

type JsonObject = Record<string, unknown>;
/** 独立字幕 cue 的可选字段（与 C# TimelineCaption 一致）；manual 单独处理。 */
const CAPTION_STYLE_KEYS = ['font', 'size', 'color', 'positionX', 'positionY', 'outline', 'margin',
  'sourceClipId', 'sequenceItemId', 'sourceCueIndex', 'sourceStart', 'sourceEnd'] as const;
export type ReplicaSaveWarning = { path: string; code: string; message: string; clipIds?: string[] };
export interface ReplicaExtensionV1 {
  version: 1;
  codecVersion: 1;
  document: JsonObject;
  opaque: JsonObject;
  exactTimeMap: JsonObject;
  compatibility: JsonObject;
}
export interface CatalogVideoFxInstance {
  id: 'transform' | 'offset' | 'rotate_3d' | 'horizontal_flip' | 'vertical_flip' | 'gaussian_blur' | 'sharpen' | 'tint'
    | 'proc_amp' | 'black_white' | 'invert' | 'gamma_correction' | 'vignette' | 'channel_mix' | 'color_pass'
    | 'directional_blur' | 'channel_blur' | 'reduce_interlace_flicker' | 'extract' | 'asc_cdl' | 'video_limiter'
    | 'color_replace' | 'rounded_crop' | 'levels' | 'posterize' | 'color_balance' | 'cineon_converter'
    | 'mosaic' | 'find_edges' | 'emboss' | 'color_emboss' | 'leave_color' | 'change_to_color' | 'three_way_color' | 'median'
    | 'lighting_effects' | 'lumetri'
    | 'four_color_gradient' | 'gradient' | 'rgb_split' | 'clone' | 'lens_flare'
    | 'compound_blur' | 'focus_blur' | 'strobe' | 'bokeh_blur'
    | 'circle' | 'grid' | 'checkerboard' | 'ellipse' | 'paint_bucket' | 'alpha_adjust' | 'track_matte' | 'color_key' | 'luma_key' | 'logo_cutout'
    | 'mirror' | 'lens_distortion' | 'twirl' | 'wave_warp' | 'spherize' | 'warp_stabilizer' | 'spacer' | 'magnify'
    | 'ultra_key' | 'drop_shadow' | 'long_shadow' | 'grow' | 'shrink' | 'move'
    | 'noise' | 'basic_3d' | 'turbulent_displace' | 'spin' | 'camera_shake' | 'wiggle' | 'wonder_glow' | 'stroke';
  enabled: boolean;
  params: Record<string, number>;
}
export interface CatalogAudioFxInstance {
  id: 'amplify' | 'highpass' | 'lowpass' | 'bass' | 'treble' | 'delay' | 'analog_delay' | 'simple_notch' | 'notch' | 'bandpass'
    | 'simple_eq' | 'parametric_eq' | 'graphic_eq' | 'graphic_eq_20' | 'graphic_eq_30'
    | 'dynamics' | 'single_band_compressor' | 'dynamics_rack' | 'tube_compressor' | 'deesser' | 'distortion'
    | 'fill_left' | 'fill_right' | 'invert_a' | 'swap_channels' | 'stereo_width' | 'stereo_expander' | 'panner_ambisonics'
    | 'channel_mixer_a' | 'channel_volume_a' | 'loudness_radar'
    | 'scientific_filter' | 'dehummer' | 'vocal_enhancer' | 'multitap_delay' | 'speech_enhance' | 'mastering' | 'multiband_compressor';
  enabled: boolean;
  params: Record<string, number>;
}
export type CatalogFxTimelineItem = TimelineItem & {
  catalogFxVersion?: 1; catalogFx?: CatalogVideoFxInstance[];
  catalogAudioFxVersion?: 1; catalogAudioFx?: CatalogAudioFxInstance[];
};
export type ReplicaEditorProject = EditorProject & { timeline: CatalogFxTimelineItem[]; replicaExtension: ReplicaExtensionV1 };
export interface ReplicaProjectionBase {
  /** Independent catalog identity, never inferred from an empty source ID. */
  independentProject?: boolean;
  /** Immutable archive from GET or a confirmed PUT, never a second editable store. */
  project: EditorProject;
  document: unknown;
  registeredSourceIds: readonly string[];
}
export type ReplicaReverseResult =
  | { ok: true; project: ReplicaEditorProject; warnings: ReplicaSaveWarning[] }
  | { ok: false; warnings: ReplicaSaveWarning[] };

function object(value: unknown, path: string): JsonObject {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(`expected_object:${path}`);
  return value as JsonObject;
}
function list(value: unknown, path: string): unknown[] {
  if (!Array.isArray(value)) throw new Error(`expected_array:${path}`);
  return value;
}
function number(value: unknown, path: string, min = 0, max = Number.MAX_SAFE_INTEGER): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < min || value > max) throw new Error(`invalid_number:${path}`);
  return value;
}
function frame(value: unknown, path: string): number {
  const result = number(value, path);
  if (!Number.isSafeInteger(result)) throw new Error(`invalid_frame:${path}`);
  return result;
}
function text(value: unknown, path: string): string {
  if (typeof value !== 'string' || !value.trim()) throw new Error(`missing_stable_id:${path}`);
  return value;
}
function boolean(value: unknown, path: string): boolean {
  if (typeof value !== 'boolean') throw new Error(`invalid_boolean:${path}`);
  return value;
}
function same(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((value, index) => same(value, b[index]));
  if (a && b && typeof a === 'object' && typeof b === 'object') {
    const left = Object.keys(a), right = Object.keys(b);
    return left.length === right.length && left.every(key => Object.hasOwn(b, key) && same((a as JsonObject)[key], (b as JsonObject)[key]));
  }
  return false;
}

/** Reject values JSON.stringify would silently erase; shared values are not cycles. */
export function cloneReplicaJson<T>(value: T, path = '$', ancestors = new Set<object>()): T {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return value;
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value !== 'object') throw new Error(`invalid_json:${path}`);
  if (ancestors.has(value as object)) throw new Error(`cyclic_json:${path}`);
  const prototype = Object.getPrototypeOf(value);
  if (!Array.isArray(value) && prototype !== null && Object.getPrototypeOf(prototype) !== null) throw new Error(`non_plain_json:${path}`);
  ancestors.add(value as object);
  try {
    if (Array.isArray(value)) {
      if (Object.keys(value).length !== value.length) throw new Error(`sparse_json:${path}`);
      return value.map((item, index) => cloneReplicaJson(item, `${path}[${index}]`, ancestors)) as T;
    }
    const copy: JsonObject = {};
    for (const [key, item] of Object.entries(value as JsonObject)) {
      Object.defineProperty(copy, key, { value: cloneReplicaJson(item, `${path}.${key}`, ancestors), enumerable: true, writable: true, configurable: true });
    }
    return copy as T;
  } finally { ancestors.delete(value as object); }
}

/** Matches ReplicaExtension.cs: unknown optional metadata survives; required* fails closed. */
export function validateReplicaExtension(value: unknown): ReplicaExtensionV1 {
  const extension = object(cloneReplicaJson(value), '$.replicaExtension');
  const unique = (value: JsonObject, path: string) => {
    const keys = Object.keys(value).map(key => key.toLowerCase());
    if (new Set(keys).size !== keys.length) throw new Error(`duplicate_extension_field:${path}`);
  };
  unique(extension, '$.replicaExtension');
  for (const key of ['version', 'codecVersion']) {
    if (extension[key] !== 1) throw new Error(`unsupported_extension_version:$.replicaExtension.${key}`);
  }
  object(extension.document, '$.replicaExtension.document');
  object(extension.exactTimeMap, '$.replicaExtension.exactTimeMap');
  if (extension.opaque !== undefined) object(extension.opaque, '$.replicaExtension.opaque');
  const compatibility = object(extension.compatibility, '$.replicaExtension.compatibility');
  unique(compatibility, '$.replicaExtension.compatibility');
  for (const [key, requirement] of Object.entries(compatibility)) {
    if (key === 'requiredVersion' || key === 'requiredCodecVersion') {
      if (requirement !== 1) throw new Error(`unsupported_extension_requirement:${key}`);
    } else if (/^required/i.test(key)) throw new Error(`unknown_required_compatibility:${key}`);
  }
  return extension as unknown as ReplicaExtensionV1;
}

const CLEARED_EVENT_SELECTION_KEYS = ['version', 'sourceId'];

/**
 * 受管理事件显式清空标记（v1）：[{version:1, sourceId}] 或 undefined。严格形状闸——
 * 未知字段、version!=1、空 sourceId、重复 sourceId 全部 fail-closed；undefined 直接放行
 * （无标记的非空 timeline / forward 文档是常态）。
 */
export function validateClearedEventSelections(value: unknown, path: string): TimelineEventSelectionClear[] | undefined {
  if (value === undefined) return undefined;
  const entries = list(value, path);
  const seen = new Set<string>();
  return entries.map((entry, index) => {
    const item = object(entry, `${path}[${index}]`);
    if (Object.keys(item).some(key => !CLEARED_EVENT_SELECTION_KEYS.includes(key))) throw new Error(`unknown_cleared_event_selection_field:${path}[${index}]`);
    if (item.version !== 1) throw new Error(`unsupported_cleared_event_selection_version:${path}[${index}]`);
    const sourceId = text(item.sourceId, `${path}[${index}].sourceId`);
    if (seen.has(sourceId)) throw new Error(`duplicate_cleared_event_selection:${path}[${index}]`);
    seen.add(sourceId);
    return { version: 1, sourceId };
  });
}

/** Add exact host preserve windows to matching native clips, without rewriting native edits. */
export function applyHostPreserveWindows(project: EditorProject, forward: FullEngineDocument, value: FullEngineDocument): void {
  const document = value as unknown as JsonObject;
  const sequence = object(document.sequence, 'sequence');
  // Only derived playback metadata is added; requested values and draft edits remain untouched.
    // Restore the host's effective preserve window only for the same adjacent native clips.
    const preserveCuts = project.transitions?.some(transition => transition.layout === 'preserve')
      ? sequenceLayout(project.timeline ?? [], project.transitions, project.fps).cuts.filter(cut => cut.transition.layout === 'preserve') : [];
    const forwardSequence = object((forward as unknown as JsonObject).sequence, 'forward.sequence');
    const forwardTracks = tracksOf(forwardSequence);
    const applyPreserveWindows = (target: JsonObject): void => {
      if (target.fps !== forwardSequence.fps || target.fps !== project.fps) return;
      for (const cut of preserveCuts) {
        const clips = tracksOf(target).find(track => track.track === cut.transition.track)?.clips ?? [];
        const index = clips.findIndex(clip => clip.key === cut.previous.id);
        const left = clips[index], right = clips[index + 1];
        const forwardClips = forwardTracks.find(track => track.track === cut.transition.track)?.clips ?? [];
        const forwardLeft = forwardClips.find(clip => clip.key === cut.previous.id), forwardRight = forwardClips.find(clip => clip.key === cut.next.id);
        if (!left || right?.key !== cut.next.id || !forwardLeft || !forwardRight || left.transition == null) continue;
        const transition = object(left.transition, 'transition');
        if (transition.layout !== 'preserve') continue;
        const limitFor = (clip: JsonObject, original: JsonObject, item: TimelineItem) =>
          Number(clip.end) - Number(clip.start) === Number(original.end) - Number(original.start) && ['sourceIn', 'speed', 'sourceId'].every(key => same(clip[key], original[key]))
            ? { ...Object.fromEntries(['key','sourceIn','speed','sourceId'].map(key => [key,clip[key]])), lengthFrames: Number(clip.end) - Number(clip.start), maxFrames: Math.floor(itemOutputDuration(item) * project.fps + 1e-6) } : undefined;
        transition.hostEffectiveWindow = { fps: project.fps,
          left: limitFor(left, forwardLeft, cut.previous), right: limitFor(right, forwardRight, cut.next) };

      }
    };
    applyPreserveWindows(sequence);
    if (document.projectState !== undefined) applyPreserveWindows(object(object(document.projectState, 'projectState').sequence, 'projectState.sequence'));
}

/** Restore the archive rather than rebuilding and losing replica-only fields on a reload. */
export function replicaDocumentForLoad(project: EditorProject, forward: FullEngineDocument): FullEngineDocument {
  const extension = (project as unknown as JsonObject).replicaExtension;
  const document = cloneReplicaJson(extension === undefined || extension === null ? forward : validateReplicaExtension(extension).document) as unknown as JsonObject;
  const sequence = object(document.sequence, 'sequence');
  // Existing archives predate hostMusic: materialise only that registered field, retaining all other JSON.
  if (sequence.hostMusic === undefined) {
    sequence.hostMusic = { musicAssetId: project.musicAssetId ?? null,
      musicVolume: project.musicVolume === undefined ? 0.25 : project.musicVolume,
      duckMusic: project.duckMusic === undefined ? false : project.duckMusic };
    // The native snapshot duplicates the active sequence in projectState; keep that identity check valid.
    if (document.projectState !== undefined) {
      const stored = object(object(document.projectState, 'projectState').sequence, 'projectState.sequence');
      if (stored.hostMusic === undefined) stored.hostMusic = cloneReplicaJson(sequence.hostMusic);
    }
  }
  if (sequence.hostEffect === undefined) {
    sequence.hostEffect = { effectAssetId: project.effectAssetId ?? null, effectAt: project.effectAt ?? 0, effectVolume: project.effectVolume ?? 0.5 };
    if (document.projectState !== undefined) {
      const stored = object(object(document.projectState, 'projectState').sequence, 'projectState.sequence');
      if (stored.hostEffect === undefined) stored.hostEffect = cloneReplicaJson(sequence.hostEffect);
    }
  }
  // The current payload's host caption snapshot always wins over the archive: after an archive
  // restore, re-inject it (or clear the stale key when the snapshot is empty), so archived
  // captions can never mask what the payload resolves right now. Same projectState duplication
  // rule as hostMusic above — the reverse identity check requires both copies to match.
  if (extension !== undefined && extension !== null) {
    const forwardSequence = object((forward as unknown as JsonObject).sequence, 'forward.sequence');
    const snapshot = forwardSequence.hostCaptions === undefined
      ? undefined
      : cloneReplicaJson(forwardSequence.hostCaptions);
    const applySnapshot = (target: JsonObject): void => {
      if (snapshot === undefined) delete target.hostCaptions;
      else target.hostCaptions = cloneReplicaJson(snapshot);
    };
    applySnapshot(sequence);
    if (document.projectState !== undefined) {
      applySnapshot(object(object(document.projectState, 'projectState').sequence, 'projectState.sequence'));
    }
    // hostEventOriginals is host-owned read-only metadata with the same precedence/duplication rules.
    const forwardOriginals = forwardSequence.hostEventOriginals === undefined ? undefined : cloneReplicaJson(forwardSequence.hostEventOriginals);
    const applyOriginals = (target: JsonObject): void => {
      if (forwardOriginals === undefined) delete target.hostEventOriginals;
      else target.hostEventOriginals = cloneReplicaJson(forwardOriginals);
    };
    applyOriginals(sequence);
    if (document.projectState !== undefined) {
      applyOriginals(object(object(document.projectState, 'projectState').sequence, 'projectState.sequence'));
    }
    // 权威 timelineCaptions 决定 captionMode/captions：independent 时用 forward 的 cue 覆盖旧扩展；
    // forward 为 source 时把旧扩展里残留的 independent 模式/cue 退回 forward 值，避免旧 extension 胜出。
    const forwardMode = forwardSequence.captionMode === 'independent' ? 'independent' : 'source';
    const applyCaptions = (target: JsonObject): void => {
      if (forwardMode === 'independent') {
        target.captionMode = 'independent';
        target.captions = cloneReplicaJson(forwardSequence.captions ?? []);
      } else {
        if (target.captionMode === 'independent') {
          if (forwardSequence.captions === undefined) delete target.captions;
          else target.captions = cloneReplicaJson(forwardSequence.captions);
        }
        target.captionMode = 'source';
      }
    };
    applyCaptions(sequence);
    if (document.projectState !== undefined) {
      applyCaptions(object(object(document.projectState, 'projectState').sequence, 'projectState.sequence'));
    }
  }
  applyHostPreserveWindows(project, forward, document as unknown as FullEngineDocument);
  // 受管理事件清空标记是 project 侧权威字段（服务器 raw+typed 双闸校验过）——project-wins 重注入。
  // 与 replicaExtension 无关的共用路径：无扩展的合法 empty+flag 也要在加载后能逆投影，
  // 不误 empty_timeline_unsupported。forward/editorNextDocument 不携带该字段：
  // project 缺失（含 null）即从归档删除，防陈旧标记复活；sequence 与 projectState.sequence 双份同步。
  const forwardCleared = validateClearedEventSelections((project as unknown as JsonObject).clearedEventSelections ?? undefined, 'project.clearedEventSelections');
  const applyCleared = (target: JsonObject): void => {
    if (forwardCleared === undefined) delete target.hostClearedEventSelections;
    else target.hostClearedEventSelections = cloneReplicaJson(forwardCleared);
  };
  if (project.emptySequenceDuration != null && (!forwardCleared?.length || project.timeline?.length !== 0)) throw new Error('invalid_empty_sequence_duration_owner');
  const retainedDuration = project.emptySequenceDuration == null ? undefined : number(project.emptySequenceDuration, 'project.emptySequenceDuration', 0, 21600);
  const applyDuration = (target: JsonObject): void => {
    if (retainedDuration !== undefined) target.duration = Math.round(retainedDuration * number(target.fps, 'sequence.fps', 1, 120));
  };
  applyDuration(sequence);
  if (document.projectState !== undefined) applyDuration(object(object(document.projectState, 'projectState').sequence, 'projectState.sequence'));
  applyCleared(sequence);
  if (document.projectState !== undefined) {
    applyCleared(object(object(document.projectState, 'projectState').sequence, 'projectState.sequence'));
  }
  return document as unknown as FullEngineDocument;
}

interface Track { value: JsonObject; id: string; track: number; kind: 'video' | 'audio'; clips: JsonObject[] }
function tracksOf(sequence: JsonObject): Track[] {
  const ids = new Set<string>(), keys = new Set<string>();
  return (['video', 'audio'] as const).flatMap(kind => list(sequence[`${kind}Tracks`], `sequence.${kind}Tracks`).map((raw, index) => {
    const value = object(raw, `sequence.${kind}Tracks[${index}]`);
    const id = text(value.id, 'track.id');
    if (ids.has(id)) throw new Error(`duplicate_track_id:${id}`);
    ids.add(id);
    const match = /^(V|A)([1-3])$/.exec(id);
    if (!match || match[1] !== (kind === 'video' ? 'V' : 'A')) throw new Error(`unsupported_track_projection:${id}`);
    if (value.kind !== undefined && value.kind !== kind) throw new Error(`track_kind_mismatch:${id}`);
    const clips = list(value.clips, `${id}.clips`).map(rawClip => {
      const clip = object(rawClip, `${id}.clip`), key = text(clip.key, 'clip.key');
      if (keys.has(key)) throw new Error(`duplicate_clip_id:${key}`);
      keys.add(key);
      if (clip.track !== undefined && clip.track !== id || clip.trackId !== undefined && clip.trackId !== id) throw new Error(`clip_track_mismatch:${key}`);
      return clip;
    });
    return { value, id, kind, clips, track: Number(match[2]) + (kind === 'audio' ? 3 : 0) };
  }));
}
const transitionKinds: Record<string, EditorTransition['kind']> = {
  cross_dissolve: 'cross-dissolve', dip_to_black: 'dip-to-black', dip_to_white: 'dip-to-white',
};
const blendTransitionKinds: Record<string, EditorTransition['kind']> = {
  additive_dissolve: 'additive-dissolve', additive_dissolve_legacy: 'additive-dissolve',
  non_additive_dissolve: 'non-additive-dissolve', iris_box: 'iris-box', iris_cross: 'iris-cross',
  iris_diamond: 'iris-diamond', iris_round: 'iris-round',
};
const fixedTransitionKernels: Record<string, { kind: EditorTransition['kind']; controls: Record<string, number | boolean> }> = {
  panel_wipe: { kind: 'panel-wipe', controls: { panels: 6 } },
  vr_iris_wipe: { kind: 'vr-iris-wipe', controls: {} },
  vr_gradient_wipe: { kind: 'vr-gradient-wipe', controls: { softness: 20, invert: false } },
  flash: { kind: 'flash', controls: { color: 1, intensity: 100 } },
  ray: { kind: 'ray', controls: { length: 50 } },
  vr_light_rays: { kind: 'vr-light-rays', controls: {} },
  solarize: { kind: 'solarize', controls: {} },
  flare: { kind: 'flare', controls: { color: 1, angle: 20 } },
  light_sweep: { kind: 'light-sweep', controls: { angle: 70, width: 160, color: 1 } },
  plateau_wipe: { kind: 'plateau-wipe', controls: { angle: 90, plateaus: 5 } },
  shape_flow: { kind: 'shape-flow', controls: { shape: 0, softness: 4 } },
};
function catalogTransitionKind(id: string, params: JsonObject): EditorTransition['kind'] | undefined {
  if (Object.hasOwn(fixedTransitionKernels, id)) {
    const fixed = fixedTransitionKernels[id];
    if (Object.keys(params).some(key => !['duration', 'align', ...Object.keys(fixed.controls)].includes(key))) return undefined;
    const { duration, align } = params;
    if (duration !== undefined && (typeof duration !== 'number' || !Number.isFinite(duration) || duration < .001 || duration > 10)) return undefined;
    if (align !== undefined && align !== 'start' && align !== 'center' && align !== 'end') return undefined;
    if (Object.entries(fixed.controls).some(([key, value]) => params[key] !== undefined && params[key] !== value)) return undefined;
    return fixed.kind;
  }
  if (Object.hasOwn(blendTransitionKinds, id) || id === 'barn_doors' || id === 'soft_wipe' || id === 'gradient_wipe') {
    // The host kinds encode only these proven fixed controls. Validate before original reuse.
    const controls = id === 'barn_doors' ? ['orientation'] : id === 'soft_wipe' ? ['angle'] : id === 'gradient_wipe' ? ['angle', 'softness'] : [];
    if (Object.keys(params).some(key => !['duration', 'align', ...controls].includes(key))) return undefined;
    const { duration, align } = params;
    if (duration !== undefined && (typeof duration !== 'number' || !Number.isFinite(duration) || duration < .001 || duration > 10)) return undefined;
    if (align !== undefined && align !== 'start' && align !== 'center' && align !== 'end') return undefined;
    if (Object.hasOwn(blendTransitionKinds, id)) return blendTransitionKinds[id];
    if (id === 'barn_doors') {
      const orientation = params.orientation === undefined ? 0 : params.orientation;
      return orientation === 0 ? 'barn-doors-vertical' : orientation === 1 ? 'barn-doors-horizontal' : undefined;
    }
    const angle = params.angle === undefined ? 90 : params.angle;
    if (typeof angle !== 'number' || ![0, 90, 180, 270].includes(angle)) return undefined;
    if (id === 'gradient_wipe' && params.softness !== undefined && params.softness !== 10) return undefined;
    const kinds: EditorTransition['kind'][] = id === 'soft_wipe'
      ? ['soft-wipe-up', 'soft-wipe-right', 'soft-wipe-down', 'soft-wipe-left']
      : ['gradient-wipe-up', 'gradient-wipe-right', 'gradient-wipe-down', 'gradient-wipe-left'];
    return kinds[angle / 90];
  }
  if (id === 'cross_dissolve_legacy') {
    if (Object.keys(params).some(key => !['duration', 'align'].includes(key))) return undefined;
    // Validate before unchanged-original reuse, which does not re-project these controls.
    if (params.duration !== undefined && (typeof params.duration !== 'number' || !Number.isFinite(params.duration) || params.duration < .001 || params.duration > 10)) return undefined;
    if (params.align !== undefined && params.align !== 'start' && params.align !== 'center' && params.align !== 'end') return undefined;
    return 'cross-dissolve';
  }
  if (id === 'wipe' || id === 'linear_wipe') {
    if (params.duration !== undefined && (typeof params.duration !== 'number' || !Number.isFinite(params.duration) || params.duration < .001 || params.duration > 10)) return undefined;
    if (params.align !== undefined && params.align !== 'start' && params.align !== 'center' && params.align !== 'end') return undefined;
  }
  if (id === 'wipe') {
    if (Object.keys(params).some(key => !['duration', 'align', 'direction'].includes(key))) return undefined;
    const direction = params.direction === undefined ? 3 : params.direction;
    if (typeof direction !== 'number' || !Number.isInteger(direction) || direction < 0 || direction > 3) return undefined;
    // Preview: 0/1/2/3 enters from top/right/bottom/left. xfade names the opposite edge.
    const kinds: EditorTransition['kind'][] = ['catalog-wipe-down', 'catalog-wipe-left', 'catalog-wipe-up', 'catalog-wipe-right'];
    return kinds[direction];
  }
  if (id === 'linear_wipe') {
    if (Object.keys(params).some(key => !['duration', 'align', 'angle'].includes(key))) return undefined;
    const angle = params.angle === undefined ? 90 : params.angle;
    if (typeof angle !== 'number') return undefined;
    switch (angle) {
      case 0: return 'linear-wipe-up';
      case 90: return 'linear-wipe-right';
      case 180: return 'linear-wipe-down';
      case 270: return 'linear-wipe-left';
      default: return undefined;
    }
  }
  return Object.hasOwn(transitionKinds, id) ? transitionKinds[id] : undefined;
}
function withoutExtension(project: EditorProject): JsonObject {
  const value = cloneReplicaJson(project) as unknown as JsonObject;
  delete value.replicaExtension;
  return value;
}
function defaultItem(id: string, clipId: string, track: number): TimelineItem {
  return { id, clipId, track, start: 0, end: 1, kind: 'video', zoom: 1, filter: 'none', motion: 'none',
    stickerAssetId: null, stickerX: 0, stickerY: 0, stickerWidth: 0.2, stickerRotation: 0, soundAssetId: null };
}
const graphicFields = ['titleText', 'titleFont', 'titleSize', 'titleColor', 'titleX', 'titleY', 'graphicKind', 'graphicBoxColor', 'graphicBoxOpacity', 'graphicBoxX', 'graphicBoxY', 'graphicBoxW', 'graphicBoxH', 'graphicLineThickness', 'previewOnly'] as const;
// 仅预览图形的中文模板名，与 filmcraft-preview/wwwroot/js/panels.js 的 TEMPLATES 保持一致（r81 测试逐条比对）。
const previewGraphicLabels: Record<string, string> = { 'callout-pointer': '标注-指示', 'end-credits': '片尾字幕-滚动' };
function graphicsOf(clip: JsonObject): JsonObject {
  const result: JsonObject = {};
  for (const key of graphicFields) {
    const value = clip[key];
    if (value === undefined || value === null && key === 'titleText') continue;
    if (key === 'titleText') {
      if (typeof value !== 'string' || value.length > 120 || /[\r\n]/.test(value)) throw new Error(`invalid_graphic:${key}`);
    } else if (key === 'titleFont') {
      if (value !== null && (typeof value !== 'string' || !['Microsoft YaHei', 'Segoe UI', 'Arial'].includes(value))) throw new Error(`invalid_graphic:${key}`);
    } else if (key === 'graphicKind') {
      if (!['title', 'box', 'lowerThirdBlock', 'line', 'callout-pointer', 'end-credits'].includes(String(value))) throw new Error(`invalid_graphic:${key}`);
    } else if (key === 'previewOnly') boolean(value, key);
    else if (key.endsWith('Color')) {
      if (typeof value !== 'string' || !/^#[0-9a-f]{6}$/i.test(value)) throw new Error(`invalid_graphic:${key}`);
    } else number(value, key, key === 'titleSize' ? 12 : 0, key === 'titleSize' ? 160 : key === 'graphicLineThickness' ? 40 : 1);
    result[key] = value;
  }
  if (Number(result.graphicBoxX ?? 0) + Number(result.graphicBoxW ?? 0) > 1 || Number(result.graphicBoxY ?? 0) + Number(result.graphicBoxH ?? 0) > 1) throw new Error('invalid_graphic:bounds');
  if (result.previewOnly === true && !result.graphicKind || result.graphicKind && Boolean(result.previewOnly) !== ['callout-pointer', 'end-credits'].includes(String(result.graphicKind))) throw new Error('invalid_graphic:previewOnly');
  return result;
}
function trackDb(value: JsonObject, id: string): number {
  const field = value.gain !== undefined ? 'gain' : 'volume';
  return number(value[field] === undefined ? 0 : value[field], `${id}.${field}`, -96, 24);
}
function mixerFlag(value: JsonObject, field: string, path: string): boolean {
  return value[field] == null ? false : boolean(value[field], `${path}.${field}`);
}

type Rgb = [number, number, number];
type RuleTuple = [fallback: number, min: number, max: number, marker?: Record<string, number> | 'toggle'];
type RuleChoices = Record<string, Record<string, number>>;

// Catalog colours arrive as a 0..1 triplet, a grey scalar, or a #rgb/#rrggbb string.
function rgbOf(rawValue: unknown, fallback: Rgb | number): Rgb | null {
  const raw = rawValue === undefined ? fallback : rawValue;
  let color: unknown = raw;
  if (typeof raw === 'number') color = [raw, raw, raw];
  else if (typeof raw === 'string' && /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i.test(raw)) {
    const hex = raw.length === 4 ? raw.slice(1).split('').map(channel => channel + channel).join('') : raw.slice(1);
    color = [0, 2, 4].map(offset => parseInt(hex.slice(offset, offset + 2), 16) / 255);
  }
  return Array.isArray(color) && color.length === 3
    && color.every(channel => typeof channel === 'number' && Number.isFinite(channel) && channel >= 0 && channel <= 1)
    ? color as Rgb : null;
}

function putRgb(projected: Record<string, number>, prefix: string, rgb: Rgb): void {
  projected[`${prefix}_r`] = rgb[0]; projected[`${prefix}_g`] = rgb[1]; projected[`${prefix}_b`] = rgb[2];
}

// Catalog points arrive as a scalar, an [x, y] pair, or an { x, y } object.
function pointOf(rawPoint: unknown): [number, number] | null {
  let pair: unknown[];
  if (typeof rawPoint === 'number') pair = [rawPoint, rawPoint];
  else if (Array.isArray(rawPoint) && rawPoint.length === 2) pair = rawPoint;
  else if (rawPoint && typeof rawPoint === 'object' && !Array.isArray(rawPoint)
    && Object.keys(rawPoint).every(name => name === 'x' || name === 'y')) pair = [(rawPoint as JsonObject).x, (rawPoint as JsonObject).y];
  else return null;
  return pair.every(value => typeof value === 'number' && Number.isFinite(value) && Math.abs(value) <= 1_000_000)
    ? pair as [number, number] : null;
}

function putPoint(projected: Record<string, number>, key: string, pair: [number, number]): void {
  projected[`${key}_x`] = pair[0]; projected[`${key}_y`] = pair[1];
}

function projectPoints(keys: string[], values: JsonObject, projected: Record<string, number>, required?: string): boolean {
  for (const key of keys) {
    const rawPoint = values[key];
    if (rawPoint === undefined) { if (key === required) return false; continue; }
    const pair = pointOf(rawPoint);
    if (!pair) return false;
    putPoint(projected, key, pair);
  }
  return true;
}

function projectColors(colors: Record<string, [prefix: string | undefined, fallback: Rgb | number]>, values: JsonObject, projected: Record<string, number>): boolean {
  for (const [key, [prefix, fallback]] of Object.entries(colors)) {
    const rgb = rgbOf(values[key], fallback);
    if (!rgb) return false;
    putRgb(projected, prefix ?? key, rgb);
  }
  return true;
}

function projectColorKeys(colors: Record<string, Rgb | number>, values: JsonObject, projected: Record<string, number>): boolean {
  for (const [key, fallback] of Object.entries(colors)) {
    const rgb = rgbOf(values[key], fallback);
    if (!rgb) return false;
    putRgb(projected, key, rgb);
  }
  return true;
}

interface RuleOptions {
  choices?: RuleChoices; // per-key label → number maps
  nullKey?: string; // this key's explicit null means "use the default"
  integer?: boolean; // a mapped key must land on an integer
  members?: boolean; // ... and on one of that map's values
}

// Shared numeric projection: default, range, label mapping, and the per-family integrality rules.
function projectRules(rules: Record<string, RuleTuple>, values: JsonObject, projected: Record<string, number>, options: RuleOptions = {}): boolean {
  const { choices, nullKey, integer, members } = options;
  for (const [key, [fallback, min, max, marker]] of Object.entries(rules)) {
    const map = marker === 'toggle' ? undefined : marker ?? choices?.[key];
    const missing = values[key] === undefined || key === nullKey && values[key] === null;
    const rawValue = missing ? (marker === 'toggle' ? fallback === 1 : fallback) : values[key];
    const value = marker === 'toggle' ? (typeof rawValue === 'boolean' ? Number(rawValue) : null)
      : map && typeof rawValue === 'string' && Object.hasOwn(map, rawValue) ? map[rawValue] : rawValue;
    if (typeof value !== 'number' || !Number.isFinite(value) || value < min || value > max) return false;
    if (map && integer && !Number.isInteger(value)) return false;
    if (map && members && !Object.values(map).includes(value)) return false;
    projected[key] = value;
  }
  return true;
}

function projectToggles(toggles: Record<string, boolean>, values: JsonObject, projected: Record<string, number>, reject?: string): boolean {
  for (const [key, fallback] of Object.entries(toggles)) {
    if (values[key] !== undefined && typeof values[key] !== 'boolean' || key === reject && values[key] === true) return false;
    projected[key] = (values[key] === undefined ? fallback : values[key]) ? 1 : 0;
  }
  return true;
}

type VideoControlSpec = [rules: Record<string, RuleTuple>, toggles?: Record<string, boolean>, colors?: Record<string, [string | undefined, Rgb | number]>, points?: string[], options?: RuleOptions];

// Static control schemas keep the renderer's exact bounds and insertion order in one place.
const videoControlSpecs: Partial<Record<CatalogVideoFxInstance['id'], VideoControlSpec>> = {
  directional_blur: [{ length: [0, 0, 8], direction: [0, -360, 360] }, {  }, {  }, [], { integer: true, members: true }],
  channel_blur: [{ red: [0, 0, 8], green: [0, 0, 8], blue: [0, 0, 8], alpha: [0, 0, 8], dimensions: [0, 0, 2, { "Horizontal and Vertical": 0, Horizontal: 1, Vertical: 2 }], repeat_edge: [0, 0, 1, "toggle"] }, {  }, {  }, [], { integer: true, members: true }],
  reduce_interlace_flicker: [{ softness: [0, 0, 8] }, {  }, {  }, [], { integer: true, members: true }],
  extract: [{ black: [0, 0, 255], white: [255, 0, 255], softness: [0, 0, 100], invert: [0, 0, 1, "toggle"] }, {  }, {  }, [], { integer: true, members: true }],
  video_limiter: [{ clip_level: [100, 90, 109, { "100 IRE": 100, "103 IRE": 103, "109 IRE": 109, "95 IRE": 95, "90 IRE": 90 }], compression: [0, 0, 3, { None: 0, Low: 1, Medium: 2, High: 3 }], axis: [0, 0, 3, { Luma: 0, Chroma: 1, "Luma and Chroma": 2, RGB: 3 }], gamut_warning: [0, 0, 1, "toggle"] }, {  }, { warning_color: ["warning", [1, 1, 1]] }, [], { integer: true, members: true }],
  color_replace: [{ similarity: [30, 0, 100], solid: [0, 0, 1, "toggle"] }, {  }, { target: ["target", [0, 0, 1]], replace: ["replace", [1, 0, 0]] }, [], { integer: true, members: true }],
  rounded_crop: [{ left: [0, 0, 100], right: [0, 0, 100], top: [0, 0, 100], bottom: [0, 0, 100], radius: [40, 0, 1024], feather: [0, 0, 1024], border: [0, 0, 1024] }, {  }, { border_color: ["border", [1, 1, 1]] }, [], { integer: true, members: true }],
  levels: [{ in_black: [0, 0, 255], in_white: [255, 0, 255], out_black: [0, 0, 255], out_white: [255, 0, 255], gamma: [100, 1, 500] }, {  }, {  }, [], { integer: true, members: true }],
  posterize: [{ levels: [8, 2, 256] }, {  }, {  }, [], { integer: true, members: true }],
  cineon_converter: [{ conversion: [0, 0, 2, { "Log to Linear": 0, "Linear to Log": 1, "Log to Log": 2 }], black10: [95, 0, 1023], white10: [685, 0, 1023], black_internal: [0, 0, 255], white_internal: [255, 0, 255], gamma: [1.7, 0.01, 10], rolloff: [20, 0, 100] }, {  }, {  }, [], { integer: true, members: true }],
  mosaic: [{ horizontal: [10, 1, 4096], vertical: [10, 1, 4096] }, { sharp: false }],
  find_edges: [{ blend: [0, 0, 100] }, { invert: false }],
  emboss: [{ direction: [45, -360, 360], relief: [1.8, 0, 100], contrast: [100, 0, 500], blend: [0, 0, 100] }],
  color_emboss: [{ direction: [45, -360, 360], relief: [1, 0, 100], contrast: [100, 0, 500] }],
  leave_color: [{ amount: [100, 0, 100], tolerance: [30, 0, 100], softness: [30, 0, 100] }, {  }, { color: ["color", [0, 0, 1]] }],
  change_to_color: [{ hue_tol: [30, 0, 100], softness: [30, 0, 100] }, {  }, { from: ["from", [0, 0, 1]], to: ["to", [0, 1, 0]] }],
  median: [{ radius: [0, 0, 4096] }],
  gradient: [{ shape: [0, 0, 1, { Linear: 0, Radial: 1 }], scatter: [0, 0, 0], midpoint: [50, 0, 100] }, {  }, { start_color: ["start", [0, 0, 0]], end_color: ["end", [1, 1, 1]] }, ["start", "end"], { integer: true, members: true }],
  rgb_split: [{ amount: [10, -1024, 1024], angle: [0, -360, 360], mode: [0, 0, 1, { Linear: 0, Radial: 1 }] }, {  }, {  }, ["center"], { integer: true, members: true }],
  clone: [{ columns: [2, 1, 128], rows: [1, 1, 128], gap: [0, 0, 4096] }, { mirror: false }, {  }, [], { integer: true, members: true }],
  lens_flare: [{ brightness: [100, 0, 300], blend: [0, 0, 100] }, {  }, {  }, ["center"], { integer: true, members: true }],
  compound_blur: [{ max: [20, 0, 1024], layer: [0, 0, 2, { None: 0, Luma: 1, Alpha: 2 }] }, { stretch: true, invert: false }, {  }, [], { nullKey: "layer", integer: true }],
  focus_blur: [{ amount: [20, 0, 1024], size: [200, 0, 1000000], feather: [200, 0, 1000000], angle: [0, -360, 360], shape: [0, 0, 1, { Radial: 0, Linear: 1 }] }, { show: false }, {  }, ["center"], { nullKey: "layer", integer: true }],
  circle: [{ radius: [75, 0, 1000000], opacity: [100, 0, 100] }, {  }, { color: [undefined, [1, 1, 1]] }, ["center"], { integer: true, members: true }],
  grid: [{ size: [50, 0, 1000000], border: [2, 0, 1000000], opacity: [100, 0, 100] }, {  }, { color: [undefined, [1, 1, 1]] }, [], { integer: true, members: true }],
  checkerboard: [{ width: [64, 0, 1000000], height: [64, 0, 1000000], feather: [0, 0, 1000000], opacity: [100, 0, 100], mode: [0, 0, 4, { None: 0, Normal: 0, Add: 1, Screen: 2, Multiply: 3, Overlay: 4 }] }, { square: true }, { color: [undefined, [1, 1, 1]] }, ["anchor"], { integer: true, members: true }],
  ellipse: [{ width: [200, -1000000, 1000000], height: [200, -1000000, 1000000], thickness: [8, 0, 1000000], softness: [15, 0, 100] }, { composite: false }, { inside: [undefined, [1, 1, 1]], outside: [undefined, [0, 0, 0]] }, ["center"], { integer: true, members: true }],
  paint_bucket: [{ selector: [0, 0, 3, { "Color & Alpha": 0, Color: 1, Alpha: 2, Luminance: 3 }], tolerance: [10, 100, 100], opacity: [100, 0, 100], mode: [0, 0, 4, { None: 0, Normal: 0, Add: 1, Screen: 2, Multiply: 3, Overlay: 4 }] }, { invert: false }, { color: [undefined, [1, 1, 1]] }, ["point"], { integer: true, members: true }],
  alpha_adjust: [{ opacity: [100, 0, 100] }, { ignore: false, invert: false, mask_only: false }, {  }, [], { integer: true, members: true }],
  track_matte: [{ matte: [0, 0, 2, { None: 0, Alpha: 1, Luma: 2 }], composite: [0, 0, 1, { "Matte Alpha": 0, "Matte Luma": 1 }] }, { reverse: false }, {  }, [], { integer: true, members: true }],
  color_key: [{ tolerance: [0, 0, 0], thin: [0, 0, 0], feather: [0, 0, 0] }, {  }, { color: [undefined, [0, 0, 0]] }, [], { integer: true, members: true }],
  luma_key: [{ threshold: [50, 0, 100], cutoff: [0, 0, 100] }, {  }, {  }, [], { integer: true, members: true }],
  logo_cutout: [{ background: [0, 0, 2, { White: 0, Black: 1, Color: 2, Custom: 2 }], threshold: [5, 0, 100], softness: [10, 0, 100] }, { unmultiply: true, invert: false }, { color: [undefined, [1, 1, 1]] }, [], { integer: true, members: true }],
  mirror: [{ angle: [0, -360, 360] }, {  }, {  }, ["center"], { nullKey: "mode", integer: true, members: true }],
  lens_distortion: [{ curvature: [0, -100, 100], h_decentering: [0, -100, 100], v_decentering: [0, -100, 100] }, {  }, {  }, [], { nullKey: "mode", integer: true, members: true }],
  twirl: [{ angle: [50, -360, 360], radius: [30, 0, 100] }, {  }, {  }, ["center"], { nullKey: "mode", integer: true, members: true }],
  wave_warp: [{ height: [10, -1000000, 1000000], width: [40, -1000000, 1000000], direction: [90, -360, 360], speed: [1, 0, 0] }, {  }, {  }, [], { nullKey: "mode", integer: true, members: true }],
  spherize: [{ radius: [0, 0, 1000000] }, {  }, {  }, ["center"], { nullKey: "mode", integer: true, members: true }],
  warp_stabilizer: [{ result: [0, 0, 1, { "Smooth Motion": 0, "No Motion": 1 }], method: [0, 0, 3, { Position: 0, "Position, Scale, Rotation": 1, Perspective: 2, "Subspace Warp": 3 }], framing: [0, 0, 3, { "Stabilize Only": 0, "Stabilize, Crop": 1, "Stabilize, Crop, Auto-scale": 2, "Stabilize, Synthesize Edges": 3 }], smoothness: [50, 0, 100], action_safe: [0, 0, 100], crop_less: [50, 0, 100], max_scale: [150, 0, 1000], additional_scale: [100, 0, 1000], motion_scale: [100, 0, 1000], motion_rotation: [0, -360, 360], motion_shear: [0, -100, 100] }, { preserve_scale: false, detailed: false }, {  }, ["motion"], { nullKey: "mode", integer: true, members: true }],
  spacer: [{ left: [40, 0, 1000000], top: [40, 0, 1000000], right: [40, 0, 1000000], bottom: [40, 0, 1000000], radius: [0, 0, 1000000] }, { uniform: true, fill: false }, { color: [undefined, 0] }, [], { nullKey: "mode", integer: true, members: true }],
  magnify: [{ shape: [0, 0, 1, { Circle: 0, Square: 1, Rectangle: 1 }], mode: [0, 0, 3, { Normal: 0, Screen: 1, Add: 2, Multiply: 3 }], magnification: [200, 0, 1000], size: [100, 0, 1000000], feather: [0, 0, 1000000], opacity: [100, 0, 100] }, {  }, {  }, ["center"], { nullKey: "mode", integer: true, members: true }],
  ultra_key: [{ output: [0, 0, 2, { Composite: 0, "Alpha Channel": 1, "Color Channel": 2 }], setting: [0, 0, 3, { Default: 0, Relaxed: 1, Aggressive: 2, Custom: 3 }], tolerance: [50, 0, 100], transparency: [45, 0, 100], highlight: [10, 0, 100], shadow: [50, 0, 100], pedestal: [10, 0, 100], spill: [50, 0, 100], choke: [0, 0, 0], soften: [0, 0, 0] }, {  }, { key_color: [undefined, 0] }, [], { nullKey: "easing", integer: true }],
  drop_shadow: [{ opacity: [50, 0, 100], direction: [135, -360, 360], distance: [5, -1000000, 1000000], softness: [0, 0, 0] }, { only: false }, { color: [undefined, 0] }, [], { nullKey: "easing", integer: true }],
  long_shadow: [{ opacity: [50, 0, 100], angle: [135, -360, 360], length: [100, 0, 1] }, { only: false, fade: true }, { color: [undefined, 0] }, [], { nullKey: "easing", integer: true }],
  grow: [{ easing: [0, 0, 3, { Linear: 0, "Ease In": 1, "Ease Out": 2, "Ease In Out": 3, "Ease In/Out": 3 }], from: [100, 0, 1000], to: [120, 0, 1000] }, {  }, {  }, ["center"], { nullKey: "easing", integer: true }],
  shrink: [{ easing: [0, 0, 3, { Linear: 0, "Ease In": 1, "Ease Out": 2, "Ease In Out": 3, "Ease In/Out": 3 }], from: [120, 0, 1000], to: [100, 0, 1000] }, {  }, {  }, ["center"], { nullKey: "easing", integer: true }],
  move: [{ easing: [0, 0, 3, { Linear: 0, "Ease In": 1, "Ease Out": 2, "Ease In Out": 3, "Ease In/Out": 3 }], motion_blur: [0, 0, 0] }, {  }, {  }, ["from", "to"], { nullKey: "easing", integer: true }],
  noise: [{ amount: [0, 0, 0] }, { color: true, clip: true }, {  }, [], { nullKey: "easing", integer: true }],
  basic_3d: [{ swivel: [0, 0, 0], tilt: [0, 0, 0], distance: [0, 0, 0] }, {  }, {  }, [], { nullKey: "easing", integer: true }],
  turbulent_displace: [{ amount: [50, 0, 0], size: [100, 0, 1000000], complexity: [1, 1, 5], evolution: [0, -360, 360], cycle_revs: [1, 0, 1000000], seed: [0, -1000000, 1000000], displacement: [0, 0, 5, { Turbulent: 0, "Turbulent Smoother": 1, Bulge: 2, Twist: 3, "Horizontal Displacement": 4, "Vertical Displacement": 5 }], pinning: [0, 0, 3, { None: 0, "All Edges": 1, "Horizontal Edges": 2, "Vertical Edges": 3 }], antialias: [0, 0, 1, { Low: 0, High: 1 }] }, { cycle: false }, {  }, ["offset"], { nullKey: "easing", integer: true }],
  spin: [{ amount: [360, 0, 0], scale: [100, 0, 1000], easing: [0, 0, 3, { Linear: 0, "Ease In": 1, "Ease Out": 2, "Ease In Out": 3, "Ease In/Out": 3 }] }, {  }, {  }, ["center"], { nullKey: "easing", integer: true }],
  camera_shake: [{ amount: [20, 0, 0], rotation: [1, 0, 0], frequency: [4, 0, 1000000], seed: [0, -1000000, 1000000], zoom: [5, 0, 0], motion_blur: [0, 0, 0], complexity: [3, 1, 6] }, {  }, {  }, [], { nullKey: "easing", integer: true }],
  wiggle: [{ amount: [20, 0, 0], rotation: [0, 0, 0], frequency: [2, 0, 1000000], seed: [0, -1000000, 1000000], scale: [0, 0, 0], dimensions: [0, 0, 2, { "Horizontal and Vertical": 0, Horizontal: 1, Vertical: 2 }] }, {  }, {  }, [], { nullKey: "easing", integer: true }],
  wonder_glow: [{ intensity: [100, 0, 0], threshold: [60, 0, 100], radius: [25, 0, 1000000], saturation: [100, 0, 200], mode: [1, 0, 4, { None: 0, Normal: 0, Add: 1, Screen: 2, Multiply: 3, Overlay: 4 }] }, { use_color: false }, { color: [undefined, [1, 1, 1]] }, [], { nullKey: "easing", integer: true }],
  stroke: [{ width: [4, 0, 1000000], opacity: [100, 0, 100], position: [0, 0, 2, { Outside: 0, Inside: 1, Center: 2 }] }, { only: false }, { color: [undefined, [1, 1, 1]] }, [], { nullKey: "easing", integer: true }],
};

function projectCatalogVideoFx(raw: unknown, track = 1): CatalogVideoFxInstance | null {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const effect = raw as JsonObject;
  if (effect.enabled !== undefined && typeof effect.enabled !== 'boolean' || effect.enabled === false) return null;
  // Unknown instance metadata can encode animation/mixing; don't silently drop its semantics.
  if (Object.keys(effect).some(key => !['id', 'effect', 'enabled', 'params'].includes(key))) return null;
  const id = effect.effect ?? effect.id;
  if (effect.effect !== undefined && typeof effect.effect !== 'string') return null;
  if (effect.id !== undefined && typeof effect.id !== 'string') return null;
  const params = effect.params === undefined ? {} : effect.params;
  if (!params || typeof params !== 'object' || Array.isArray(params)) return null;
  const values = params as JsonObject;
  if (id === 'horizontal_flip' || id === 'vertical_flip') {
    return Object.keys(values).length ? null : { id, enabled: true, params: {} };
  }
  const only = (keys: string[]) => Object.keys(values).every(key => keys.includes(key));
  const numeric = (key: string, fallback: number, min: number, max: number): number | null => {
    const value = values[key] === undefined ? fallback : values[key];
    return typeof value === 'number' && Number.isFinite(value) && value >= min && value <= max ? value : null;
  };
  if (id === 'transform' || id === 'offset' || id === 'rotate_3d') {
    // V2/V3 intermediate H.264 has no alpha; transformed holes would hide the lower track.
    if (track !== 1 && id !== 'offset') return null;
    const projected: Record<string, number> = {};
    const point = (key: string): boolean => {
      if (values[key] === undefined) return true; // Absent points stay frame-relative, not catalog scalar64.
      const pair = pointOf(values[key]);
      if (!pair) return false;
      putPoint(projected, key, pair);
      return true;
    };
    if (id === 'offset') {
      const blend = numeric('blend', 0, 0, 100); // fx-more-b overrides the old engine's default100.
      if (!only(['shift', 'blend']) || !point('shift') || blend === null) return null;
      return { id, enabled: true, params: { ...projected, blend } };
    }
    if (id === 'rotate_3d') {
      const rotZ = numeric('rot_z', 0, -360, 360), perspective = numeric('perspective', 50, 0, 100);
      if (!only(['rot_x', 'rot_y', 'rot_z', 'perspective', 'z', 'hide_back']) || numeric('rot_x', 0, 0, 0) !== 0
        || numeric('rot_y', 0, 0, 0) !== 0 || numeric('z', 0, 0, 0) !== 0 || rotZ === null || perspective === null
        || values.hide_back !== undefined && typeof values.hide_back !== 'boolean') return null;
      // With X/Y/Z-position zero, perspective and back-face flags cannot change this 2D map.
      return { id, enabled: true, params: { rot_z: rotZ } };
    }
    const scaleY = numeric('scale_height', 100, 1, 1000), width = numeric('scale_width', 100, 1, 1000);
    const rotation = numeric('rotation', 0, -360, 360), skewAxis = numeric('skew_axis', 0, -360, 360);
    const shutterAngle = numeric('shutter_angle', 180, 0, 360);
    if (!only(['anchor', 'position', 'uniform_scale', 'scale_height', 'scale_width', 'rotation', 'skew', 'skew_axis', 'opacity', 'shutter_override', 'shutter_angle'])
      || !point('anchor') || !point('position') || scaleY === null || width === null || rotation === null || skewAxis === null
      || shutterAngle === null || numeric('skew', 0, 0, 0) !== 0 || numeric('opacity', 100, 100, 100) !== 100
      || values.uniform_scale !== undefined && typeof values.uniform_scale !== 'boolean'
      || values.shutter_override !== undefined && values.shutter_override !== false) return null;
    // Non-100 opacity, nonzero skew and temporal shutter history stay outside the static 2D subset.
    return { id, enabled: true, params: { ...projected, scale_x: values.uniform_scale === false ? width : scaleY, scale_y: scaleY, rotation } };
  }
  if (id === 'sharpen') {
    const amount = numeric('amount', 100, 0, 500);
    return only(['amount']) && amount !== null ? { id, enabled: true, params: { amount } } : null;
  }
  // AUDIT0: unsharp's linear-light preview and brightness/contrast's per-RGB preview
  // are not equivalent to encoded-YUV unsharp/eq. No maxRGB<=1/alphaMax=0 proof:
  // archive the entire IDs (including identity/default params) via the usual null + warning path.
  if (id === 'unsharp_mask' || id === 'brightness_contrast') return null;
  if (id === 'tint') {
    const color = (value: unknown, fallback: number): number[] | null => {
      if (value === undefined) return [fallback, fallback, fallback];
      if (typeof value === 'number') return Number.isFinite(value) && value >= 0 && value <= 1 ? [value, value, value] : null;
      if (Array.isArray(value)) return value.length === 3 && value.every(channel => typeof channel === 'number' && Number.isFinite(channel) && channel >= 0 && channel <= 1) ? value : null;
      if (typeof value !== 'string' || !/^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i.test(value)) return null;
      const hex = value.length === 4 ? value.slice(1).split('').map(channel => channel + channel).join('') : value.slice(1);
      return [0, 2, 4].map(offset => parseInt(hex.slice(offset, offset + 2), 16) / 255);
    };
    const amount = numeric('amount', 100, 0, 100), black = color(values.black, 0), white = color(values.white, 1);
    if (!only(['black', 'white', 'amount']) || amount === null || black === null || white === null) return null;
    return { id, enabled: true, params: { black_r: black[0], black_g: black[1], black_b: black[2], white_r: white[0], white_g: white[1], white_b: white[2], amount } };
  }
  if (id === 'black_white') return only([]) ? { id, enabled: true, params: {} } : null;
  if (id === 'proc_amp') {
    const brightness = numeric('brightness', 0, -100, 100), contrast = numeric('contrast', 0, 0, 200);
    const hue = numeric('hue', 0, -360, 360), saturation = numeric('saturation', 100, 0, 200);
    if (!only(['brightness', 'contrast', 'hue', 'saturation']) || brightness === null || contrast === null || hue === null || saturation === null) return null;
    return { id, enabled: true, params: { brightness, contrast, hue, saturation } };
  }
  if (id === 'gamma_correction') {
    const gamma = numeric('gamma', 100, 0, 100);
    return only(['gamma']) && gamma !== null ? { id, enabled: true, params: { gamma } } : null;
  }
  if (id === 'invert') {
    const channels: Record<string, number> = { RGB: 0, Red: 1, Green: 2, Blue: 3, Alpha: 4 };
    const rawChannel = values.channel === undefined ? 0 : values.channel;
    const channel = typeof rawChannel === 'string' && Object.hasOwn(channels, rawChannel) ? channels[rawChannel] : rawChannel;
    const blend = numeric('blend', 0, 0, 100);
    // Alpha=4 changes coverage, which the current alpha-less export path cannot composite faithfully.
    if (!only(['channel', 'blend']) || typeof channel !== 'number' || !Number.isInteger(channel) || channel < 0 || channel > 3 || blend === null) return null;
    return { id, enabled: true, params: { channel, blend } };
  }
  if (id === 'channel_mix') {
    const keys = ['rr', 'rg', 'rb', 'rc', 'gr', 'gg', 'gb', 'gc', 'br', 'bg', 'bb', 'bc'];
    if (!only([...keys, 'monochrome']) || values.monochrome !== undefined && typeof values.monochrome !== 'boolean') return null;
    const projected: Record<string, number> = {};
    for (const key of keys) {
      const value = numeric(key, key[0] === key[1] ? 100 : 0, -200, 200);
      if (value === null) return null;
      projected[key] = value;
    }
    projected.monochrome = values.monochrome === true ? 1 : 0;
    return { id, enabled: true, params: projected };
  }
  if (id === 'vignette' || id === 'color_pass') {
    const rawColor = values.color === undefined ? (id === 'vignette' ? [0, 0, 0] : [0, 0, 1]) : values.color;
    let color: unknown = rawColor;
    if (typeof rawColor === 'number') color = [rawColor, rawColor, rawColor];
    else if (typeof rawColor === 'string' && /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i.test(rawColor)) {
      const hex = rawColor.length === 4 ? rawColor.slice(1).split('').map(channel => channel + channel).join('') : rawColor.slice(1);
      color = [0, 2, 4].map(offset => parseInt(hex.slice(offset, offset + 2), 16) / 255);
    }
    if (!Array.isArray(color) || color.length !== 3 || !color.every(channel => typeof channel === 'number' && Number.isFinite(channel) && channel >= 0 && channel <= 1)) return null;
    const projected: Record<string, number> = { color_r: color[0], color_g: color[1], color_b: color[2] };
    if (id === 'color_pass') {
      const similarity = numeric('similarity', 30, 0, 100);
      if (!only(['color', 'similarity', 'reverse']) || similarity === null || values.reverse !== undefined && typeof values.reverse !== 'boolean') return null;
      return { id, enabled: true, params: { ...projected, similarity, reverse: values.reverse === true ? 1 : 0 } };
    }
    const amount = numeric('amount', -50, -100, 100), midpoint = numeric('midpoint', 50, 0, 100);
    const roundness = numeric('roundness', 0, -100, 100), feather = numeric('feather', 50, 0, 100);
    if (!only(['amount', 'midpoint', 'roundness', 'feather', 'color']) || amount === null || midpoint === null || roundness === null || feather === null) return null;
    return { id, enabled: true, params: { ...projected, amount, midpoint, roundness, feather } };
  }
  let spec: VideoControlSpec | undefined;
  // Repeated channel/range families stay generated, not expanded into redundant table rows.
  if (id === 'asc_cdl' || id === 'color_balance' || id === 'three_way_color' || id === 'four_color_gradient') {
    const rules: Record<string, RuleTuple> = {}, toggles: Record<string, boolean> = {}, colors: Record<string, [string | undefined, Rgb | number]> = {};
    const add = (key: string, fallback: number, min: number, max: number) => { rules[key] = [fallback, min, max]; };
    if (id === 'asc_cdl') {
      for (const c of ['r', 'g', 'b']) { add(`${c}_slope`, 1, 0, 4); add(`${c}_offset`, 0, -1, 1); add(`${c}_power`, 1, 0, 4); }
      add('saturation', 1, 0, 4);
    } else if (id === 'color_balance') {
      for (const range of ['shadow', 'mid', 'hi']) for (const c of ['r', 'g', 'b']) add(`${range}_${c}`, 0, -100, 100);
      add('preserve', 0, 0, 1); rules.preserve[3] = 'toggle';
    } else if (id === 'three_way_color') {
      for (const range of ['shadow', 'mid', 'hi']) { add(`${range}_hue`, 0, -360, 360); add(`${range}_balance`, 0, 0, 100); add(`${range}_level`, 0, -100, 100); }
      for (const range of ['shadow', 'hi']) { add(`${range}_threshold`, 50, 0, 100); add(`${range}_softness`, 0, 0, 100); }
      add('saturation', 100, 0, 200);
    } else {
      add('opacity', 100, 0, 100); add('blend', 100, 0, 100);
      for (let i = 1; i <= 4; i++) colors[`c${i}`] = [`c${i}`, i % 2 === 1 ? [1, 1, 1] : [0, 0, 0]];
    }
    spec = [rules, toggles, colors];
  } else if (typeof id === 'string' && Object.hasOwn(videoControlSpecs, id)) spec = videoControlSpecs[id as CatalogVideoFxInstance['id']];
  const projectControls = (): Record<string, number> | null => {
    if (!spec) return null;
    const [rules, toggles = {}, colors = {}, points = [], options = {}] = spec;
    if (!only([...Object.keys(rules), ...Object.keys(toggles), ...Object.keys(colors), ...points])) return null;
    const projected: Record<string, number> = {};
    if (!projectRules(rules, values, projected, options)) return null;
    if (id === 'checkerboard') projected.replace = values.mode === undefined || values.mode === 'None' || values.mode === 0 ? 1 : 0;
    if (id === 'stroke' && projected.width !== 0 && projected.opacity !== 0) return null;
    if (!projectToggles(toggles, values, projected, id === 'focus_blur' ? 'show' : id === 'spacer' ? 'fill' : undefined)) return null;
    if (!projectPoints(points, values, projected, id === 'move' ? 'from' : undefined)) return null;
    if ((id === 'grow' || id === 'shrink') && projected.from !== projected.to) return null;
    if (id === 'move' && (projected.from_x !== projected.to_x || projected.from_y !== projected.to_y)) return null;
    if (!projectColors(colors, values, projected)) return null;
    return projected;
  };
  // Keep family dispatch and alpha gates explicit: their revert-negative contracts exercise live code.
  if (id === 'directional_blur' || id === 'channel_blur' || id === 'reduce_interlace_flicker' || id === 'extract'
    || id === 'asc_cdl' || id === 'video_limiter' || id === 'color_replace' || id === 'rounded_crop' || id === 'levels'
    || id === 'posterize' || id === 'color_balance' || id === 'cineon_converter') {
    const projected = projectControls();
    if (!projected) return null;
    if (track !== 1 && (id === 'rounded_crop' || id === 'channel_blur' && projected.alpha > 0)) return null;
    return { id, enabled: true, params: projected };
  }
  if (id === 'mosaic' || id === 'find_edges' || id === 'emboss' || id === 'color_emboss'
    || id === 'leave_color' || id === 'change_to_color' || id === 'three_way_color' || id === 'median') {
    const projected = projectControls();
    if (!projected) return null;
    if (id === 'mosaic' && track !== 1) return null;
    return { id, enabled: true, params: projected };
  }
  if (id === 'four_color_gradient' || id === 'gradient' || id === 'rgb_split' || id === 'clone' || id === 'lens_flare') {
    const projected = projectControls();
    if (!projected) return null;
    if (track !== 1 && id !== 'gradient') return null;
    return { id, enabled: true, params: projected };
  }
  if (id === 'compound_blur' || id === 'focus_blur' || id === 'noise' || id === 'basic_3d'
    || id === 'turbulent_displace' || id === 'spin' || id === 'camera_shake' || id === 'wiggle' || id === 'wonder_glow' || id === 'stroke'
    || id === 'ultra_key' || id === 'drop_shadow' || id === 'long_shadow' || id === 'grow' || id === 'shrink' || id === 'move'
    || id === 'mirror' || id === 'lens_distortion' || id === 'twirl' || id === 'wave_warp' || id === 'spherize' || id === 'warp_stabilizer' || id === 'spacer' || id === 'magnify'
    || id === 'circle' || id === 'grid' || id === 'checkerboard' || id === 'ellipse' || id === 'paint_bucket' || id === 'alpha_adjust'
    || id === 'track_matte' || id === 'color_key' || id === 'luma_key' || id === 'logo_cutout') {
    if (track !== 1 && id !== 'compound_blur' && id !== 'focus_blur') return null;
    const projected = projectControls();
    return projected ? { id, enabled: true, params: projected } : null;
  }
  if (id === 'strobe' || id === 'bokeh_blur') {
    const shapes: Record<string, number> = { Circle: 0, Triangle: 3, Square: 4, Pentagon: 5, Hexagon: 6, Octagon: 8 };
    const rules: Record<string, RuleTuple> = id === 'strobe'
      ? { period: [1, 0, 1_000_000], duration: [.1, 0, 1_000_000], blend: [100, 0, 100] }
      : { amount: [10, 0, 0], shape: [0, 0, 8, shapes], rotation: [0, -360, 360], highlight: [50, 0, 100], threshold: [80, 0, 100] };
    if (!only([...Object.keys(rules), ...(id === 'strobe' ? ['color'] : [])])) return null;
    const projected: Record<string, number> = {};
    if (!projectRules(rules, values, projected, { members: true })) return null;
    if (id === 'strobe') {
      // Intermittent strobes still need a proven clip-local time/PTS mapping.
      if (projected.duration < Math.max(projected.period, .001)) return null;
      if (!projectColorKeys({ color: [1, 1, 1] }, values, projected)) return null;
    }
    return { id, enabled: true, params: projected };
  }
  if (id === 'lumetri') {
    // Any Creative/Curves/Look/HDR/Wheels/HSL/Vignette key archives the whole instance.
    const rules: Record<string, [number, number, number]> = {
      temperature: [0, -100, 100], tint: [0, -100, 100], exposure: [0, -5, 5], contrast: [0, -100, 100],
      highlights: [0, -100, 100], shadows: [0, -100, 100], whites: [0, -100, 100], blacks: [0, -100, 100], saturation: [100, 0, 200],
    };
    if (!only(['basic_on', ...Object.keys(rules)]) || (values.basic_on !== undefined && typeof values.basic_on !== 'boolean')) return null;
    const projected: Record<string, number> = { basic_on: values.basic_on === false ? 0 : 1 };
    if (!projectRules(rules, values, projected)) return null;
    return { id, enabled: true, params: projected };
  }
  if (id === 'lighting_effects') {
    if (!only(['position', 'radius', 'intensity', 'ambient', 'color'])) return null;
    const intensity = numeric('intensity', 70, 0, 100), ambient = numeric('ambient', 55, 0, 100);
    if (intensity === null || ambient === null) return null;
    const projected: Record<string, number> = { intensity, ambient };
    // Missing position/radius remain frame-relative defaults in the renderer.
    if (values.radius !== undefined) {
      const radius = numeric('radius', 0, 0, 1_000_000);
      if (radius === null) return null;
      projected.radius = radius;
    }
    if (values.position !== undefined) {
      const pair = pointOf(values.position);
      if (!pair) return null;
      putPoint(projected, 'position', pair);
    }
    const rgb = rgbOf(values.color, [1, .94, .82]);
    if (!rgb) return null;
    putRgb(projected, 'color', rgb);
    return { id, enabled: true, params: projected };
  }
  if (id !== 'gaussian_blur' || Object.keys(values).some(key => !['blurriness', 'dimensions', 'repeat_edge'].includes(key))) return null;
  if (values.dimensions !== undefined && values.dimensions !== 'Horizontal and Vertical') return null;
  if (values.repeat_edge !== undefined && values.repeat_edge !== false) return null;
  const radius = values.blurriness === undefined ? 0 : values.blurriness;
  if (typeof radius !== 'number' || !Number.isFinite(radius) || radius < 0 || radius > 1024) return null;
  // The renderer matches the preview's dimension-capped, linear three-box Gaussian.
  return { id, enabled: true, params: { radius } };
}

function projectCatalogAudioFx(raw: unknown): CatalogAudioFxInstance | null {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const effect = raw as JsonObject;
  if (effect.enabled !== undefined && typeof effect.enabled !== 'boolean' || effect.enabled === false) return null;
  if (Object.keys(effect).some(key => !['id', 'effect', 'enabled', 'params'].includes(key))) return null;
  if (effect.effect !== undefined && typeof effect.effect !== 'string') return null;
  if (effect.id !== undefined && typeof effect.id !== 'string') return null;
  const id = effect.effect ?? effect.id;
  const params = effect.params === undefined ? {} : effect.params;
  if (!params || typeof params !== 'object' || Array.isArray(params)) return null;
  const values = params as JsonObject;
  type Rule = [fallback: number, min: number, max: number, choices?: readonly string[] | 'toggle'];
  const rules: Record<string, Rule> = {};
  const add = (key: string, fallback: number, min: number, max: number, choices?: readonly string[] | 'toggle') => { rules[key] = [fallback, min, max, choices]; };
  if (id === 'amplify') add('gain', 0, -96, 24);
  else if (id === 'highpass' || id === 'lowpass') add('cutoff', id === 'highpass' ? 80 : 8000, 20, 20000);
  else if (id === 'bass' || id === 'treble') add('boost', 0, -30, 30);
  else if (id === 'bandpass' || id === 'simple_notch' || id === 'simple_eq') {
    add('center', 30, 20, 20000); add('q', .707, .1, 30);
    if (id === 'simple_eq') add('boost', 0, -30, 30);
  } else if (id === 'delay') {
    // Project seconds -> DSP milliseconds; feedback and mix are percentages, not fractions.
    add('delay', .001, .001, 2); add('feedback', 30, 0, 95); add('mix', 30, 0, 100);
  } else if (id === 'analog_delay') {
    add('mode', 0, 0, 2, ['Tape', 'Tape/Tube', 'Analog']); add('dry', 100, 0, 100); add('wet', 40, 0, 100);
    add('delay', 250, 5, 8000); add('feedback', 40, 0, 200); add('trash', 0, 0, 200); add('spread', 0, 0, 200);
  } else if (id === 'notch') {
    add('width', 0, 0, 2, ['Narrow', 'Very Narrow', 'Super Narrow']);
    for (let i = 1; i <= 6; i++) {
      add(`n${i}_on`, 0, 0, 1, 'toggle'); add(`n${i}_freq`, 60 * i, 20, 20000); add(`n${i}_gain`, -30, -90, 0);
    }
  } else if (id === 'parametric_eq') {
    add('master_gain', 0, -30, 30);
    const slopes = ['12 dB/oct', '24 dB/oct', '36 dB/oct', '48 dB/oct'];
    add('hp_on', 0, 0, 1, 'toggle'); add('hp_freq', 30, 10, 20000); add('hp_slope', 0, 0, 3, slopes);
    const bands = [['low', 100, .707], ['b1', 200, 1], ['b2', 500, 1], ['mid', 1000, 1], ['b4', 2000, 1], ['b5', 5000, 1], ['high', 8000, .707]] as const;
    for (const [prefix, frequency, q] of bands) {
      add(`${prefix}_on`, 1, 0, 1, 'toggle'); add(`${prefix}_freq`, frequency, 20, 20000);
      add(`${prefix}_gain`, 0, -30, 30); add(`${prefix}_q`, q, .1, 30);
    }
    add('lp_on', 0, 0, 1, 'toggle'); add('lp_freq', 18000, 20, 22000); add('lp_slope', 0, 0, 3, slopes);
  } else if (id === 'graphic_eq' || id === 'graphic_eq_20' || id === 'graphic_eq_30') {
    add('gain', 0, -24, 24);
    for (let i = 1; i <= (id === 'graphic_eq' ? 10 : id === 'graphic_eq_20' ? 20 : 30); i++) add(`b${i}`, 0, -24, 24);
  } else if (id === 'dynamics' || id === 'single_band_compressor') {
    add('threshold', -20, -60, 0); add('ratio', 4, 1, 30); add('attack', 10, .01, 300); add('release', 100, 1, 3000);
    if (id === 'single_band_compressor') add('output', 0, -40, 40);
  } else if (id === 'multiband_compressor') {
    add('xo1', 120, 20, 1000); add('xo2', 2000, 100, 8000); add('xo3', 10000, 1000, 20000);
    for (let i = 1; i <= 4; i++) {
      add(`b${i}_threshold`, -18, -60, 0); add(`b${i}_ratio`, 3, 1, 30);
      add(`b${i}_attack`, 10, .1, 500); add(`b${i}_release`, 100, 1, 5000); add(`b${i}_gain`, 0, -18, 18);
      add(`b${i}_solo`, 0, 0, 1, 'toggle'); add(`b${i}_bypass`, 0, 0, 1, 'toggle');
    }
    add('output', 0, -18, 18); add('lim_on', 0, 0, 1, 'toggle'); add('lim_threshold', -.1, -30, 0);
    add('lim_release', 50, 1, 1000); add('link', 1, 0, 1, 'toggle');
  } else if (id === 'dynamics_rack') {
    add('gate_on', 0, 0, 1, 'toggle'); add('gate_threshold', -50, -80, 0); add('gate_attack', 2, .1, 100);
    add('gate_release', 100, 1, 3000); add('gate_hold', 50, 0, 1000);
    add('comp_on', 1, 0, 1, 'toggle'); add('comp_threshold', -20, -60, 0); add('comp_ratio', 2, 1, 30);
    add('comp_attack', 10, .1, 300); add('comp_release', 100, 1, 3000); add('comp_auto', 0, 0, 1, 'toggle'); add('comp_makeup', 0, 0, 30);
    add('exp_on', 0, 0, 1, 'toggle'); add('exp_threshold', -60, -80, 0); add('exp_ratio', 2, 1, 30);
    add('lim_on', 0, 0, 1, 'toggle'); add('lim_threshold', -1, -30, 0); add('lim_release', 50, 1, 1000);
    add('soft_clip', 0, 0, 1, 'toggle'); add('output', 0, -30, 30);
  } else if (id === 'tube_compressor') {
    add('threshold', -20, -60, 0); add('ratio', 4, 1, 30); add('attack', 10, .1, 500);
    add('release', 100, 1, 5000); add('output', 0, -30, 30);
  } else if (id === 'deesser') {
    add('frequency', 6000, 2000, 12000); add('threshold', -12, -40, 0); add('reduction', 8, 0, 24);
  } else if (id === 'distortion') {
    add('drive', 12, 0, 48); add('curve', 0, 0, 3, ['Soft Clip', 'Hard Clip', 'Tube', 'Foldback']);
    add('symmetry', 0, -100, 100); add('tone', 20000, 500, 20000); add('output', 0, -48, 12); add('mix', 100, 0, 100);
  } else if (id === 'fill_left' || id === 'fill_right' || id === 'invert_a' || id === 'swap_channels') {
    // Routing has no parameters. Unknown keys still fail below.
  } else if (id === 'stereo_width') add('width', 100, 0, 200);
  else if (id === 'stereo_expander') { add('center_pan', 0, -100, 100); add('expand', 100, 0, 300); }
  else if (id === 'panner_ambisonics') { add('pan', 0, -180, 180); add('tilt', 0, -90, 90); add('roll', 0, -180, 180); }
  else if (id === 'channel_mixer_a') {
    add('l_from_l', 100, -100, 100); add('l_from_r', 0, -100, 100);
    add('r_from_l', 0, -100, 100); add('r_from_r', 100, -100, 100);
    add('invert_l', 0, 0, 1, 'toggle'); add('invert_r', 0, 0, 1, 'toggle');
  } else if (id === 'channel_volume_a') {
    for (const channel of ['left', 'right', 'center', 'lfe', 'left_surround', 'right_surround']) add(channel, 0, -96, 24);
  } else if (id === 'loudness_radar') add('target', -23, -36, -5); // PCM pass-through only; not loudness normalization or exported metering.
  else if (id === 'scientific_filter') {
    add('type', 1, 0, 3, ['Bessel', 'Butterworth', 'Chebyshev', 'Elliptical']);
    add('mode', 0, 0, 3, ['Low Pass', 'High Pass', 'Band Pass', 'Band Stop']);
    add('order', 6, 1, 12); add('cutoff', 1000, 20, 20000); add('high_cutoff', 4000, 20, 20000);
    add('ripple', 1, .01, 6); add('stop_atten', 60, 20, 120); add('gain', 0, -30, 30);
  } else if (id === 'dehummer') {
    // Match project mapping, not the catalogue's display defaults (50Hz/-40dB).
    add('freq', 1, 0, 1, ['50 Hz', '60 Hz']); add('gain', 0, -96, 0); add('harmonics', 5, 1, 10); add('q', 30, 1, 100);
  } else if (id === 'speech_enhance') {
    add('mix', 100, 0, 100); add('tone', 0, 0, 1, ['Low Tone', 'High Tone']);
  } else if (id === 'mastering') {
    add('eq_low', 0, -12, 12); add('eq_mid_freq', 1000, 100, 10000); add('eq_mid', 0, -12, 12); add('eq_high', 0, -12, 12);
    add('reverb', 0, 0, 100); add('exciter', 0, 0, 100); add('exciter_mode', 1, 0, 2, ['Retro', 'Tape', 'Tube']);
    add('widener', 100, 0, 200); add('loudness', 0, 0, 100); add('output', 0, -24, 12);
  } else if (id === 'vocal_enhancer') add('mode', 0, 0, 2, ['Male', 'Female', 'Music']);
  else if (id === 'multitap_delay') {
    for (let i = 1; i <= 4; i++) { add(`delay${i}`, i * 250, 1, 4000); add(`feedback${i}`, 0, 0, 95); add(`level${i}`, -3 * (i + 1), -96, 0); }
    add('mix', 50, 0, 100);
  } else return null; // Minimum-statistics denoise and true-peak limiter still have no faithful export mapping.
  const ignored = new Set<string>();
  const ignore = (key: string, fallback: number, min: number, max: number, choices?: readonly string[] | 'toggle') => {
    add(key, fallback, min, max, choices);
    ignored.add(key);
  };
  // Project mappings replace these DSP controls rather than forwarding the UI's full defaults.
  if (id === 'highpass' || id === 'lowpass' || id === 'bass' || id === 'treble' || id === 'bandpass' || id === 'simple_notch') {
    const types = ['Peaking', 'Low Shelf', 'High Shelf', 'Low Pass', 'High Pass', 'Notch', 'Band Pass'];
    const bands = [[0, 4, 30, .707], [1, 1, 100, .707], [1, 0, 250, 1], [1, 0, 700, 1],
      [1, 0, 2000, 1], [1, 0, 5000, 1], [1, 2, 10000, .707], [0, 3, 18000, .707]];
    for (const [index, [on, type, frequency, q]] of bands.entries()) {
      const prefix = `b${index + 1}`;
      ignore(`${prefix}.on`, on, 0, 1, 'toggle'); ignore(`${prefix}.type`, type, 0, 6, types);
      ignore(`${prefix}.freq`, frequency, 20, 20000); ignore(`${prefix}.gain`, 0, -30, 30); ignore(`${prefix}.q`, q, .1, 30);
    }
    ignore('output', 0, -30, 30);
  } else if (id === 'simple_eq') {
    ignore('low', 0, -24, 24); ignore('low_freq', 200, 20, 1000);
    ignore('mid', 0, -24, 24); ignore('mid_freq', 1000, 100, 10000); ignore('mid_q', .7, .1, 10);
    ignore('high', 0, -24, 24); ignore('high_freq', 5000, 1000, 20000); ignore('output', 0, -24, 24);
  }
  if (id === 'delay') {
    ignore('mode', 0, 0, 1, ['Time', 'Tempo']); ignore('time', 250, 1, 2000); ignore('bpm', 120, 20, 300);
    ignore('division', 3, 0, 8, ['1/1', '1/2', '1/4', '1/8', '1/16', '1/4 dotted', '1/8 dotted', '1/4 triplet', '1/8 triplet']);
  } else if (id === 'dynamics' || id === 'single_band_compressor') {
    ignore('knee', 6, 0, 24); ignore('makeup', 0, -40, 40); ignore('detector', 0, 0, 1, ['Peak', 'RMS']);
  }
  if (id === 'dehummer') {
    ignore('frequency', 0, 0, 1, ['50 Hz', '60 Hz']); ignore('amount', 100, 0, 100);
  }
  if (Object.keys(values).some(key => !Object.hasOwn(rules, key))) return null;
  const projected: Record<string, number> = {};
  for (const [key, [fallback, min, max, choices]] of Object.entries(rules)) {
    let value = values[key] === undefined ? fallback : values[key];
    if (choices === 'toggle' && typeof value === 'boolean') value = value ? 1 : 0;
    else if (Array.isArray(choices) && typeof value === 'string') value = choices.findIndex(choice => choice.toLowerCase() === (value as string).toLowerCase());
    if (typeof value !== 'number' || !Number.isFinite(value) || value < min || value > max || choices && !Number.isInteger(value)) return null;
    if (!ignored.has(key)) projected[key] = value;
  }
  if (id === 'delay' || id === 'analog_delay') {
    const wet = id === 'delay' ? projected.mix : projected.wet;
    const samples = projected.delay * (id === 'delay' ? 48000 : 48);
    // No finite-tail or fractional-tap approximation of the recursive preview delay.
    if (wet !== 0 && (projected.feedback !== 0 || id === 'analog_delay' && projected.spread !== 0 || Math.abs(samples - Math.round(samples)) > 1e-7)) return null;
  }
  // Dynamics and distortion use exact per-sample state machines, not similarly named FFmpeg aliases.
  if (id === 'mastering' && projected.reverb !== 0) return null;
  // The adaptive deesser shelf still has no faithful active mapping.
  if (id === 'deesser' && projected.reduction !== 0) return null;
  if (id === 'scientific_filter' && (projected.type !== 1 || projected.mode > 1 || !Number.isInteger(projected.order) || projected.order > 6)) return null;
  if (id === 'dehummer' && !Number.isInteger(projected.harmonics) || id === 'vocal_enhancer' && projected.mode !== 2) return null;
  if (id === 'multitap_delay' && projected.mix !== 0) {
    for (let i = 1; i <= 4; i++) {
      const samples = Math.fround(projected[`delay${i}`] * .001 * 48000);
      if (projected[`feedback${i}`] !== 0 || !Number.isInteger(samples)) return null;
    }
  }
  return { id, enabled: true, params: projected };
}

export function replicaSnapshotToProject(document: unknown, base: ReplicaProjectionBase): ReplicaReverseResult {
  const warnings: ReplicaSaveWarning[] = [];
  const warn = (path: string, code: string, message: string, clipIds?: string[]) =>
    warnings.push(clipIds ? { path, code, message, clipIds: [...clipIds] } : { path, code, message });
  try {
    const archived = object(cloneReplicaJson(document), '$');
    const prior = object(cloneReplicaJson(base.document), '$base');
    if (!(archived.__fcproj === 1 || archived.format === 'filmcraft-project' && archived.version === 1)
      || archived.__fcproj !== undefined && archived.__fcproj !== 1
      || archived.version !== undefined && archived.version !== 1
      || archived.format !== undefined && (archived.format !== 'filmcraft-project' || archived.version !== 1)) throw new Error('unsupported_document_version:$');
    if (archived.bridgeSchema !== undefined && archived.bridgeSchema !== 2) throw new Error('unsupported_document_schema:bridgeSchema');
    const sequence = object(archived.sequence, '$.sequence'), oldSequence = object(prior.sequence, '$base.sequence');
    const fps = number(sequence.fps, 'sequence.fps', 1, 120);
    if (!Number.isInteger(fps) || ![24, 25, 30, 50, 60].includes(fps)) throw new Error('unsupported_fps_projection:sequence.fps');
    frame(sequence.duration, 'sequence.duration');
    const project = cloneReplicaJson(base.project);
    const rawProject = project as unknown as JsonObject;
    if (sequence.hostEffect !== undefined) {
      const effect = object(sequence.hostEffect, 'sequence.hostEffect');
      if (Object.keys(effect).some(key => !['effectAssetId', 'effectAt', 'effectVolume'].includes(key))) throw new Error('unknown_host_effect_field');
      project.effectAssetId = effect.effectAssetId === null ? null : text(effect.effectAssetId, 'sequence.hostEffect.effectAssetId');
      project.effectAt = number(effect.effectAt, 'sequence.hostEffect.effectAt', 0, 21600);
      project.effectVolume = number(effect.effectVolume, 'sequence.hostEffect.effectVolume', 0, 2);
    } else if (oldSequence.hostEffect !== undefined) {
      project.effectAssetId = null; project.effectAt = 0; project.effectVolume = 0.5;
    }
    // Registered native global-music metadata. Never copy mixer flags or any whole editor payload.
    if (sequence.hostMusic !== undefined) {
      const mix = object(sequence.hostMusic, 'sequence.hostMusic');
      if (Object.keys(mix).some(key => !['musicAssetId', 'musicVolume', 'duckMusic'].includes(key))) throw new Error('unknown_host_music_field');
      const assetId = mix.musicAssetId === null ? null : text(mix.musicAssetId, 'sequence.hostMusic.musicAssetId');
      const volume = number(mix.musicVolume, 'sequence.hostMusic.musicVolume', 0, 1);
      const duck = boolean(mix.duckMusic, 'sequence.hostMusic.duckMusic');
      project.musicAssetId = assetId; project.musicVolume = volume; project.duckMusic = duck;
    } else if (oldSequence.hostMusic !== undefined) {
      // Undo to an older absent metadata snapshot means no global BGM, not stale base-project selection.
      project.musicAssetId = null; project.musicVolume = 0.25; project.duckMusic = false;
    }
    // 受管理事件清空标记（v1）：引擎把它持久化在 sequence.hostClearedEventSelections（原生 TL 历史随 undo 恢复），
    // 这里严格校验后投影成 project.clearedEventSelections；undo 回无标记快照 → 显式 null。
    const clearedSelections = validateClearedEventSelections(sequence.hostClearedEventSelections, 'sequence.hostClearedEventSelections');
    if (clearedSelections !== undefined) project.clearedEventSelections = clearedSelections;
    else if (oldSequence.hostClearedEventSelections !== undefined) project.clearedEventSelections = null;
    const oldExtension = rawProject.replicaExtension == null ? null : validateReplicaExtension(rawProject.replicaExtension);
    const tracks = tracksOf(sequence), oldTracks = tracksOf(oldSequence);
    const oldClips = new Map(oldTracks.flatMap(track => track.clips.map(clip => [String(clip.key), clip] as const)));
    const oldTrackByClip = new Map(oldTracks.flatMap(track => track.clips.map(clip => [String(clip.key), track] as const)));
    const rejectMixer = (path: string, reason: string): never => {
      warn(path, 'mixer_projection_unrepresentable', reason);
      throw new Error(`unrepresentable_mixer:${path}:${reason}`);
    };
    const masterDb = number(sequence.masterGain ?? 0, 'sequence.masterGain', -96, 24);
    if (!same(sequence.masterGain, oldSequence.masterGain)) {
      const amplitude = 10 ** (masterDb / 20);
      if (amplitude > 2) rejectMixer('sequence.masterGain', '主总线 dB 换算后超过后端 masterVolume 0..2；未截断或仅存扩展。');
      project.masterVolume = amplitude;
    }
    for (const track of tracks) {
      trackDb(track.value, track.id);
      for (const flag of ['muted', 'solo']) mixerFlag(track.value, flag, track.id);
      const old = oldTracks.find(entry => entry.id === track.id);
      for (const flag of ['enabled', 'hidden']) {
        if (!same(track.value[flag], old?.value[flag]) && (flag === 'enabled' || track.kind === 'audio')) {
          rejectMixer(`${track.id}.${flag}`, '该轨音频门控没有独立的后端投影，不能借 disabled 连带改变画面。');
        }
      }
      for (const flag of ['muted', 'solo']) if (track.kind === 'video' && !same(track.value[flag], old?.value[flag])) {
        if (track.track === 1 && flag === 'muted' && !mixerFlag(track.value, 'solo', track.id)) {
          project.baseAudioMuted = mixerFlag(track.value, flag, track.id);
        } else {
          rejectMixer(`${track.id}.${flag}`, '后端仅支持 V1 独立静音与 A1–A3 M/S；该视频音轨门控无法无损表达。');
        }
      }
    }
    if (oldTracks.some(track => track.track === 1) && !tracks.some(track => track.track === 1)) project.baseAudioMuted = false;
    const audioSoloChanged = tracks.some(track => track.kind === 'audio' && !same(track.value.solo, oldTracks.find(old => old.id === track.id)?.value.solo));
    const hasSolo = tracks.some(track => mixerFlag(track.value, 'solo', track.id));
    if (audioSoloChanged && hasSolo && (project.musicAssetId || project.effectAssetId || (project.timeline ?? []).some(item => item.soundAssetId))) {
      rejectMixer('sequence.solo', '后端全局 BGM/SFX 不受轨道 solo 门控；此组合不能只导出独奏通道。');
    }
    const originals = new Map((base.project.timeline ?? []).map(item => [item.id, item]));
    if (originals.size !== (base.project.timeline ?? []).length) throw new Error('duplicate_original_clip_id');
    for (const item of originals.values()) if (!oldClips.has(item.id)) throw new Error(`original_projection_missing:${item.id}`);
    const registered = new Set(base.registeredSourceIds);
    const mapped = new Map<string, TimelineItem>();
    const sourceOf = (clip: JsonObject, original?: TimelineItem) => {
      const source = typeof clip.source === 'string' ? clip.source : clip.source && typeof clip.source === 'object' ? (clip.source as JsonObject).sourceId ?? (clip.source as JsonObject).id : undefined;
      const id = clip.sourceId ?? source ?? (original && oldClips.has(original.id) ? original.clipId : undefined);
      if (typeof id !== 'string' || !registered.has(id)) throw new Error(`unregistered_media:${clip.key}`);
      if (clip.sourceId !== undefined && source !== undefined && source !== clip.sourceId) throw new Error(`ambiguous_media:${clip.key}`);
      return id;
    };
    // Effects use the replica schema, not the backend audio-effect chain; never overwrite that chain.
    const archivedClipIds = { effects: new Set<string>(), gpuFx: new Set<string>() };
    // Backend-renderable candidates still need a proven inverse before accepting edits.
    const rejectChanges = ['motion', 'transform', 'graph', 'mask', 'nestedSeq', 'nested', 'multicam', 'in', 'volume', 'muted', 'scene'];
    for (const track of tracks) for (const clip of track.clips) {
      const id = String(clip.key), original = originals.get(id), old = oldClips.get(id);
      const graphic = clip.kind === 'title' && track.track === 3;
      if (!graphic && clip.kind !== track.kind) throw new Error(`unsupported_clip_kind:${id}:${clip.kind}`);
      const graphics = graphicsOf(clip);
      if ((graphic || clip.graphicKind) && track.track !== 3) throw new Error(`graphics_require_v3:${id}`);
      if (graphic && !clip.graphicKind) throw new Error(`invalid_graphic:graphicKind:${id}`);
      if (graphic && (clip.mediaPath || clip.assetId || clip.sourceId || clip.source)) throw new Error(`invalid_graphic:media:${id}`);
      if (clip.previewOnly === true && !graphic) throw new Error(`invalid_graphic:preview_only_media:${id}`);
      if (clip.previewOnly === true) {
        // 一个仅预览图形一条警告：模板中文名 + 种类 + 片段 id + 轨道，否则多条警告无法区分。
        const label = previewGraphicLabels[String(clip.graphicKind)] ?? String(clip.graphicKind);
        const where = `片段 ${id} · 轨道 ${track.id}`;
        warn(`clip.${id}`, `graphics_preview_only:${clip.graphicKind}`,
          `仅预览图形「${label}」（${clip.graphicKind}，${where}）已保存在工程中，导出成片不会渲染它。`, [id]);
        warn(`clip.${id}`, 'graphics_preview_only_archived_not_rendered',
          `本次导出不渲染“仅预览”图形「${label}」（${clip.graphicKind}，${where}）；工程中仍保留它。`, [id]);
        continue;
      }
      for (const field of rejectChanges) {
        if (!(graphic && field === 'scene' && clip.scene == null) && !same(clip[field], old?.[field])) throw new Error(`unimplemented_edit:${id}.${field}`);
      }
      if (clip.lut != null) throw new Error(`unregistered_lut:${id}`);
      const replicaEffects = list(clip.effects ?? [], `${id}.effects`);
      const projected = replicaEffects.map(effect => track.kind === 'video' ? projectCatalogVideoFx(effect, track.track) : null);
      // V1 already renders embedded audio through AudioFilter; overlay V2/V3 stay archive-only in this slice.
      const projectedAudio = replicaEffects.map(effect => track.kind === 'audio' || track.track === 1 ? projectCatalogAudioFx(effect) : null);
      if (projected.some((effect, index) => effect === null && projectedAudio[index] === null)) archivedClipIds.effects.add(id);
      if (!same(clip.gpuFx, old?.gpuFx) || clip.gpuFx != null) archivedClipIds.gpuFx.add(id);
      if (clip.gpuFx != null) warn(`clip.${id}.gpuFx`, 'projection_unimplemented', 'GPU 效果按原 JSON 存扩展；宿主渲染未实现，不能视为可导出等价。');
      const clipId = graphic ? '' : sourceOf(clip, original), item: CatalogFxTimelineItem = original ? cloneReplicaJson(original) : defaultItem(id, clipId, track.track);
      if (graphic) item.kind = 'graphic';
      // eventPlacement is host provenance. Existing managed identity is immutable; a brand-new clip (no original
      // by key) may carry bridge-minted metadata, strictly validated. Never inferred from title/time for manual clips.
      const placed = item as CatalogFxTimelineItem & { eventPlacement?: TimelineEventPlacement | null };
      const originalPlacement = original?.eventPlacement ?? undefined;
      const incomingPlacement = clip.eventPlacement;
      if (original) {
        if (incomingPlacement !== undefined && !same(incomingPlacement, originalPlacement ?? null) && !(incomingPlacement === null && originalPlacement === undefined)) {
          throw new Error(`event_placement_mismatch:${id}`);
        }
        if (originalPlacement !== undefined) {
          const p = object(originalPlacement, `${id}.eventPlacement`);
          if (p.version !== 1 || p.sourceId !== clipId) delete placed.eventPlacement; // source changed: provenance no longer valid
        } else delete placed.eventPlacement;
      } else {
        delete placed.eventPlacement;
        if (incomingPlacement !== undefined && incomingPlacement !== null) {
          const p = object(incomingPlacement, `${id}.eventPlacement`);
          if (p.version !== 1 || typeof p.sourceId !== 'string' || typeof p.eventId !== 'string' || p.eventId === '' || p.sourceId !== clipId
            || Object.keys(p).some(key => !['version', 'sourceId', 'eventId'].includes(key))) {
            throw new Error(`invalid_event_placement:${id}`);
          }
          placed.eventPlacement = { version: 1, sourceId: p.sourceId, eventId: p.eventId };
        }
      }
      for (const field of graphicFields) {
        if (field === 'titleFont' && graphics[field] == null && original?.titleFont == null) continue;
        if (graphics[field] !== undefined) (item as unknown as JsonObject)[field] = graphics[field];
        else if (old?.[field] !== undefined) delete (item as unknown as JsonObject)[field];
      }
      const catalogFx = projected.filter((effect): effect is CatalogVideoFxInstance => effect !== null);
      if (catalogFx.length) {
        item.catalogFxVersion = 1;
        item.catalogFx = catalogFx;
      } else {
        delete item.catalogFxVersion;
        delete item.catalogFx;
      }
      const catalogAudioFx = projectedAudio.filter((effect): effect is CatalogAudioFxInstance => effect !== null);
      if (catalogAudioFx.length) {
        item.catalogAudioFxVersion = 1;
        item.catalogAudioFx = catalogAudioFx;
      } else {
        delete item.catalogAudioFxVersion;
        delete item.catalogAudioFx;
      }
      if (clip.offline === true || String(clip.assetId ?? '').startsWith('host-offline:')) warn(`clip.${id}`, 'media_offline', '媒体当前离线；保留已注册素材身份，不使用演示素材。');
      if (old && ['mediaPath', 'assetId', 'assetSource', 'sourceUrl', 'src'].some(field => !same(clip[field], old[field])) && clipId === original?.clipId) throw new Error(`media_relink_unregistered:${id}`);
      const startFrame = frame(clip.start, `${id}.start`), endFrame = frame(clip.end, `${id}.end`);
      if (endFrame <= startFrame) throw new Error(`invalid_clip_duration:${id}`);
      const sourceIn = frame(clip.sourceIn, `${id}.sourceIn`), speed = number(clip.speed ?? 1, `${id}.speed`, 0.25, 4);
      const timingChanged = !original || !old || fps !== oldSequence.fps || ['sourceIn', 'speed'].some(key => !same(clip[key], old[key])) || endFrame - startFrame !== Number(old.end) - Number(old.start);
      if (timingChanged) {
        item.start = sourceIn / fps;
        item.end = item.start + (endFrame - startFrame) / fps * speed;
        item.speed = speed;
      }
      if (!original || item.clipId !== clipId) item.clipId = clipId;
      if (!original || (item.track ?? 1) !== track.track) item.track = track.track;
      const assignChanged = (field: keyof TimelineItem, value: unknown, replicaField: string) => {
        if (!old || !same(clip[replicaField], old[replicaField])) (item as unknown as JsonObject)[field] = value;
      };
      assignChanged('disabled', !boolean(clip.enabled ?? true, `${id}.enabled`), 'enabled');
      const gainDb = number(clip.gain === undefined ? 0 : clip.gain, `${id}.gain`, -96, 24), gain = trackDb(track.value, track.id);
      const oldTrack = oldTrackByClip.get(id), oldGain = oldTrack ? trackDb(oldTrack.value, oldTrack.id) : 0;
      const gainChanged = !old || !same(clip.gain, old.gain) || gain !== oldGain;
      const overlay = (track: number | undefined) => track === 2 || track === 3;
      if (oldTrack && overlay(oldTrack.track) !== overlay(track.track) && (original?.volumeDb ?? 0) !== 0) {
        rejectMixer(`clip.${id}.gain`, '后端 V2/V3 同时在片段与合成阶段应用 volumeDb，非零增益不能无损跨越该边界。');
      }
      if (gainChanged) {
        // Confirmed projects already contain the old track/clip factors; apply only their delta.
        const volumeDb = original && old ? (original.volumeDb ?? 0) + gainDb - number(old.gain ?? 0, `${id}.oldGain`, -96, 24) + gain - oldGain : gainDb + gain;
        if (volumeDb < -60 || volumeDb > 12) rejectMixer(`clip.${id}.gain`, '轨道与片段 dB 叠加超出后端 volumeDb -60..12；未截断增益。');
        if (gain !== 0 && (item.effects?.some(effect => effect.enabled && !['parametric_eq', 'parametric-eq', 'parametriceq'].includes(effect.type.toLowerCase())) || list(clip.effects ?? [], `${id}.effects`).some(raw => object(raw, `${id}.effect`).enabled !== false))) {
          rejectMixer(`clip.${id}.gain`, '后推子轨增益不能移到非线性/未验证效果链之前，避免改变处理顺序。');
        }
        if (!graphic && (track.track === 2 || track.track === 3)) rejectMixer(`clip.${id}.gain`, '后端 V2/V3 同时在片段与合成阶段应用 volumeDb，无法无损投影单次轨/片段增益。');
        item.volumeDb = volumeDb;
      }
      if (clip.opacity !== undefined || old?.opacity !== undefined) assignChanged('opacity', number(clip.opacity ?? 1, `${id}.opacity`, 0, 1), 'opacity');
      if (clip.link !== undefined || old?.link !== undefined) {
        if (clip.link != null && typeof clip.link !== 'string') throw new Error(`invalid_link:${id}`);
        assignChanged('linkGroup', clip.link ?? null, 'link');
      }
      for (const field of ['fadeIn', 'fadeOut'] as const) {
        const seconds = frame(clip[field] ?? 0, `${id}.${field}`) / fps;
        if (!old || fps !== oldSequence.fps || !same(clip[field], old[field])) item[field] = seconds;
        if (seconds > 10) throw new Error(`fade_out_of_range:${id}.${field}`);
      }
      if ((item.fadeIn ?? 0) + (item.fadeOut ?? 0) > (item.end - item.start) / (item.speed ?? 1) + 1e-9) throw new Error(`fade_exceeds_duration:${id}`);
      mapped.set(id, item);
      if (original) {
        const preserved = Object.keys(original).filter(field => !['id', 'clipId', 'start', 'end', 'speed', 'track', 'gapBefore', 'sequenceStart', 'disabled', 'volumeDb', 'fadeIn', 'fadeOut', 'opacity', 'linkGroup', 'catalogFx', 'catalogFxVersion', 'catalogAudioFx', 'catalogAudioFxVersion'].includes(field));
        if (preserved.length) warn(`timeline.${id}`, 'lc_fields_preserved', `未投影字段按稳定 ID 原样保留：${preserved.join('、')}。`);
      }
    }
    const oldTransitionIds = new Set<string>();
    for (const track of oldTracks) track.clips.forEach((clip, index) => {
      if (clip.transition != null && track.clips[index + 1]) {
        const rightId = track.clips[index + 1].key;
        for (const entry of base.project.transitions ?? []) if (entry.itemId === rightId && entry.track === track.track) oldTransitionIds.add(entry.id);
      }
    });
    const transitionCandidates: EditorTransition[] = [];
    for (const track of tracks) for (let index = 0; index < track.clips.length; index++) {
      const left = track.clips[index], next = track.clips[index + 1], old = oldClips.get(String(left.key));
      if (left.transition == null) continue;
      if (!next) throw new Error(`transition_without_right_clip:${left.key}`);
      const value = object(left.transition, `${left.key}.transition`), params = object(value.params ?? {}, 'transition.params');
      if (value.layout !== undefined && value.layout !== 'overlap' && value.layout !== 'preserve') throw new Error('invalid_transition_layout');
      if (value.layout === 'preserve' && (track.track !== 1 || left.end !== next.start)) throw new Error('preserve_transition_requires_adjacent_v1');
      let kind = catalogTransitionKind(String(value.id), params);
      if (!kind) throw new Error(`unsupported_transition:${value.id}`);
      if (Object.hasOwn(fixedTransitionKernels, String(value.id)) && (sequence.width !== undefined || sequence.height !== undefined)
        && (sequence.width !== 1280 || sequence.height !== 720)) {
        const reason = `unsupported_transition:${value.id}:output_size:${sequence.width}x${sequence.height}:requires_1280x720`;
        warn(`${left.key}.transition`, 'unsupported_transition', reason);
        throw new Error(reason);
      }
      const original = (base.project.transitions ?? []).find(entry => entry.itemId === next.key && entry.track === track.track);
      const legacyWipe = !!original && ['wipe-up', 'wipe-right', 'wipe-down', 'wipe-left'].includes(original.kind);
      const previousTransition = old?.transition == null ? null : object(old.transition, 'base.transition');
      const previousParams = previousTransition ? object(previousTransition.params ?? {}, 'base.transition.params') : {};
      const identityChanged = previousTransition?.id !== value.id || (value.id === 'wipe'
        ? (previousParams.direction ?? 3) !== (params.direction ?? 3)
        : value.id === 'linear_wipe' && (previousParams.angle ?? 90) !== (params.angle ?? 90));
      // Legacy forward loading synthesizes `wipe`; that ID alone is not catalog provenance.
      // Only an archived linear_wipe is unambiguous, or a current explicit ID/direction edit.
      const archivedLeft = oldExtension ? tracksOf(object(oldExtension.document.sequence, 'extension.sequence'))
        .find(t => t.track === track.track)?.clips.find(c => c.key === left.key) : undefined;
      const archivedLinear = legacyWipe && value.id === 'linear_wipe' && archivedLeft?.transition != null
        && object(archivedLeft.transition, 'archived.transition').id === 'linear_wipe'
        && same(object(object(archivedLeft.transition, 'archived.transition').params ?? {}, 'archived.params').angle ?? 90, previousParams.angle ?? 90);
      const upgradeLegacyWipe = legacyWipe && (identityChanged || archivedLinear || value.catalogOrigin === 'explicit');
      if (legacyWipe && !upgradeLegacyWipe && (value.id === 'wipe' || value.id === 'linear_wipe')) kind = original!.kind;
      const custom = !['cross-dissolve', 'dip-to-black', 'dip-to-white', 'wipe-left', 'wipe-right', 'wipe-up', 'wipe-down'].includes(kind);
      if (custom && track.track !== 1) throw new Error(`unsupported_transition:${value.id}`);
      if (original && !upgradeLegacyWipe && same(left.transition, old?.transition) && fps === oldSequence.fps) transitionCandidates.push(cloneReplicaJson(original));
      else {
        const align = params.align ?? 'center';
        if (!['center', 'start', 'end'].includes(String(align))) throw new Error('unsupported_transition_align');
        transitionCandidates.push({ id: original?.id ?? `replica:${track.id}:${left.key}:${next.key}`, itemId: String(next.key), track: track.track,
          kind, duration: number(params.duration ?? 0.5, 'transition.duration', 0.001, 10), align: align as EditorTransition['align'], ...(value.layout ? { layout: value.layout as 'overlap' | 'preserve' } : {}) });
      }
    }
    const replacements = new Map(transitionCandidates.map(entry => [entry.id, entry]));
    const transitions: EditorTransition[] = [];
    for (const original of base.project.transitions ?? []) {
      if (oldTransitionIds.has(original.id)) {
        const replacement = replacements.get(original.id);
        if (replacement) transitions.push(replacement);
        replacements.delete(original.id);
      } else {
        transitions.push(cloneReplicaJson(original));
        warn(`transitions.${original.id}`, 'unprojected_transition_preserved', '未进入复刻视图的宿主转场保留原值；本批未实现编辑。');
      }
    }
    transitions.push(...replacements.values());
    const allItems = tracks.flatMap(track => track.clips.flatMap(clip => mapped.has(String(clip.key)) ? [mapped.get(String(clip.key))!] : []));
    // Legacy media projects require the managed-clear contract; independent catalog projects allow explicit [].
    // Never turn an empty independent sequence into null (legacy whole-source fallback).
    // 受管理事件清空标记是唯一例外：显式取消勾选到空随 project.clearedEventSelections 持久化（服务器豁免）。
    const emptySequence = allItems.length === 0;
    if (emptySequence) project.timeline = [];
    // Use the production transition layout, not a negative-gap approximation.
    const zeroGaps = allItems.map(item => ({ ...item, sequenceStart: undefined, gapBefore: 0 }));
    const cutShifts = new Map(sequenceLayout(zeroGaps, transitions, fps).cuts.map(cut => [cut.next.id, cut.shift]));
    const preserveWindows = transitionCandidates.map(t=>{
      const row=tracks.find(track=>track.track===t.track)!;
      const right=row.clips.find(c=>String(c.key)===t.itemId)!;
      const cut=sequenceLayout(zeroGaps,[t],fps).cuts[0];
      if(!cut) throw new Error('preserve_transition_not_effective');
      const span=Math.round(cut.effective*fps),preserve=t.layout==='preserve',before=!preserve||t.align==='start'?0:t.align==='end'?span:Math.floor(span/2);
      return {start:Number(right.start)-before,end:Number(right.start)-before+span,preserve};
    }).sort((a,b)=>a.start-b.start);
    for(let i=1;i<preserveWindows.length;i++)if((preserveWindows[i].preserve||preserveWindows[i-1].preserve)&&preserveWindows[i].start<preserveWindows[i-1].end)throw new Error('preserve_transition_windows_overlap');
    for (const track of tracks) {
      const mappedClips = track.clips.filter(clip => mapped.has(String(clip.key)));
      const explicit = mappedClips.some(clip => originals.get(String(clip.key))?.sequenceStart != null);
      const pureOverlap = mappedClips.some((clip, index) => index > 0 && Number(clip.start) < Number(mappedClips[index - 1].end) && !cutShifts.has(String(clip.key)));
      if (explicit || pureOverlap) {
        if (track.track !== 1) throw new Error('absolute_sequence_requires_v1');
        if (transitions.some(t => t.track === track.track && t.layout !== 'preserve')) throw new Error('absolute_sequence_transition_unsupported');
        for (const clip of mappedClips) {
          const item = mapped.get(String(clip.key))!;
          const original = (base.project.timeline ?? []).find(entry => entry.id === item.id);
          const old = oldClips.get(String(clip.key));
          const translated = original?.sequenceStart != null && old && fps === oldSequence.fps
            && Number(clip.end) - Number(clip.start) === Number(old.end) - Number(old.start)
            && ['sourceIn', 'speed'].every(key => same(clip[key], old[key]));
          item.sequenceStart = translated
            ? original!.sequenceStart! + (Number(clip.start) - Number(old!.start)) / fps
            : Number(clip.start) / fps;
          item.gapBefore = 0;
        }
        continue;
      }
      let previousEnd = 0;
      for (const clip of track.clips) {
        const id = String(clip.key), item = mapped.get(id), old = oldClips.get(id);
        if (!item) continue;
        const gap = Number(clip.start) / fps - previousEnd + (cutShifts.get(id) ?? 0);
        const index = track.clips.indexOf(clip), oldTrack = oldTracks.find(entry => entry.clips.some(value => value.key === id));
        const samePredecessor = index === 0 ? oldTrack?.clips[0]?.key === id : oldTrack?.clips[oldTrack.clips.findIndex(value => value.key === id) - 1]?.key === track.clips[index - 1].key;
        const oldIndex = oldTrack?.clips.findIndex(value => value.key === id) ?? -1;
        const oldPreviousEnd = oldIndex > 0 ? Number(oldTrack!.clips[oldIndex - 1].end) / Number(oldSequence.fps) : 0;
        const layoutUnchanged = originals.has(id) && !!old && samePredecessor && fps === oldSequence.fps && same(clip.start, old.start)
          && previousEnd === oldPreviousEnd && same(transitions, base.project.transitions ?? []);
        // Only the unchanged forward baseline may retain a subframe rounding residue.
        if (gap < -1e-8 && (!layoutUnchanged || gap < -1 / fps - 1e-8)) throw new Error(`unrepresentable_overlap:${id}`);
        if (!layoutUnchanged) item.gapBefore = Math.max(0, gap);
        previousEnd = Number(clip.end) / fps;
      }
    }
    // Keep original global interleaving when every track's stable-ID order is unchanged.
    const orderUnchanged = oldTracks.length === tracks.length && tracks.every(track => same(track.clips.map(clip => clip.key), oldTracks.find(old => old.id === track.id)?.clips.map(clip => clip.key)));
    if (!same(sequence.videoTracks, oldSequence.videoTracks) || !same(sequence.audioTracks, oldSequence.audioTracks) || fps !== oldSequence.fps
      || [...mapped].some(([id, item]) => !same((item as CatalogFxTimelineItem).catalogFx, (originals.get(id) as CatalogFxTimelineItem | undefined)?.catalogFx)
        || (item as CatalogFxTimelineItem).catalogFxVersion !== (originals.get(id) as CatalogFxTimelineItem | undefined)?.catalogFxVersion
        || !same((item as CatalogFxTimelineItem).catalogAudioFx, (originals.get(id) as CatalogFxTimelineItem | undefined)?.catalogAudioFx)
        || (item as CatalogFxTimelineItem).catalogAudioFxVersion !== (originals.get(id) as CatalogFxTimelineItem | undefined)?.catalogAudioFxVersion)) {
      project.timeline = orderUnchanged && mapped.size === originals.size ? (base.project.timeline ?? []).map(item => mapped.get(item.id)!).filter(Boolean) : allItems;
    }
    if (!same(transitions, base.project.transitions ?? [])) project.transitions = transitions;
    if (fps !== oldSequence.fps) project.fps = fps;
    // Canvas edits belong to the same native history/save transaction as the timeline.
    if (sequence.width !== oldSequence.width || sequence.height !== oldSequence.height ||
        fps !== oldSequence.fps && sequence.width !== undefined && sequence.height !== undefined) {
      const width = number(sequence.width, 'sequence.width', 16, 16384);
      const height = number(sequence.height, 'sequence.height', 16, 16384);
      if (!Number.isInteger(width) || !Number.isInteger(height) || width % 2 || height % 2) throw new Error('invalid_canvas_dimensions');
      project.canvasWidth = width;
      project.canvasHeight = height;
    }
    // A height enum cannot encode arbitrary aspect ratios. Preserve unchanged dimensions only.
    for (const [field, flag, allowed] of [
      ['mutedTracks', 'muted', [4, 5, 6]], ['soloedTracks', 'solo', [4, 5, 6]],
      ['hiddenTracks', 'hidden', [1, 2, 3]], ['lockedTracks', 'locked', [1, 2, 3, 4, 5, 6]],
    ] as const) {
      const result = new Set(base.project[field] ?? []);
      let changed = false;
      if (field === 'mutedTracks' || field === 'soloedTracks') {
        for (const old of oldTracks) if (old.kind === 'audio' && !tracks.some(track => track.id === old.id)) {
          if (result.delete(old.track)) changed = true;
        }
      }
      for (const track of tracks) {
        const before = oldTracks.find(entry => entry.id === track.id)?.value[flag];
        if (!same(track.value[flag], before)) {
          if (track.track === 1 && flag === 'muted') continue;
          if (!(allowed as readonly number[]).includes(track.track)) throw new Error(`unsupported_track_flag:${track.id}.${flag}`);
          const enabled = boolean(track.value[flag] ?? false, `${track.id}.${flag}`);
          if (enabled) result.add(track.track); else result.delete(track.track);
          changed = true;
        }
      }
      if (changed) project[field] = [...result].sort((a, b) => a - b);
    }
    // Only explicit managed-empty state may retain a standalone time domain.
    // No source clip is synthesized; regular caption and marker projection remains unchanged.
    if (emptySequence && clearedSelections?.length) project.emptySequenceDuration = number(sequence.duration, 'sequence.duration', 0, 21600 * fps) / fps;
    else if (base.project.emptySequenceDuration != null) project.emptySequenceDuration = null;
    if (!same(sequence.markers, oldSequence.markers) || fps !== oldSequence.fps) {
      project.markers = list(sequence.markers, 'sequence.markers').map((raw, index) => {
        const marker = object(raw, 'marker'), old = (oldSequence.markers as JsonObject[] | undefined)?.[index];
        return old && fps === oldSequence.fps && same(marker, old) && base.project.markers?.[index] !== undefined ? base.project.markers[index] : frame(marker.frame, 'marker.frame') / fps;
      });
    }
    const captionModeOf = (value: unknown, path: string): 'independent' | 'source' => {
      if (value === undefined || value === 'source') return 'source';
      if (value === 'independent') return 'independent';
      throw new Error(`invalid_caption_mode:${path}`);
    };
    const mode = captionModeOf(sequence.captionMode, 'sequence.captionMode');
    const oldMode = captionModeOf(oldSequence.captionMode, 'base.sequence.captionMode');
    const emptyCaptions = (value: unknown): boolean => value === undefined || value === null || Array.isArray(value) && value.length === 0;
    if (mode === 'independent') {
      const baseCues = base.project.timelineCaptions;
      if (oldMode === 'independent' && baseCues != null && same(sequence.captions, oldSequence.captions)) {
        // 未编辑：保留 base 的秒值，避免帧↔秒往返漂移。
      } else {
        const cues = list(sequence.captions, 'sequence.captions');
        if (cues.length > 5000) throw new Error('invalid_caption:count');
        const ids = new Set<string>();
        const known = new Set(['id', 'start', 'end', 'text', 'manual', ...CAPTION_STYLE_KEYS]);
        const duration = Number(sequence.duration);
        project.timelineCaptions = cues.map((raw, index) => {
          const cue = object(raw, `sequence.captions[${index}]`);
          const where = `sequence.captions[${index}]`;
          for (const key of Object.keys(cue)) if (!known.has(key)) throw new Error(`invalid_caption_field:${where}.${key}`);
          const id = text(cue.id, `${where}.id`);
          if (id.length > 120 || ids.has(id)) throw new Error(`invalid_caption_id:${where}.id`);
          ids.add(id);
          const start = frame(cue.start, `${where}.start`), end = frame(cue.end, `${where}.end`);
          if (end <= start || end > duration) throw new Error(`invalid_caption_range:${where}`);
          if (typeof cue.text !== 'string' || !cue.text.trim() || cue.text.length > 2000) throw new Error(`invalid_caption_text:${where}.text`);
          const out: JsonObject = { id, start: start / fps, end: end / fps, text: cue.text };
          const opt = (key: string): unknown => cue[key] === null ? undefined : cue[key];
          const set = (key: string, value: unknown): void => { if (value !== undefined) out[key] = value; };
          const font = opt('font');
          if (font !== undefined && (typeof font !== 'string' || !font.trim() || font.length > 80)) throw new Error(`invalid_caption_style:${where}.font`);
          const size = opt('size');
          if (size !== undefined && (typeof size !== 'number' || !Number.isInteger(size) || size < 10 || size > 80)) throw new Error(`invalid_caption_style:${where}.size`);
          const color = opt('color');
          if (color !== undefined && (typeof color !== 'string' || !/^#[A-Fa-f0-9]{6}$/.test(color))) throw new Error(`invalid_caption_style:${where}.color`);
          const px = opt('positionX'), py = opt('positionY');
          if ((px === undefined) !== (py === undefined)) throw new Error(`invalid_caption_style:${where}.position`);
          for (const [key, value] of [['positionX', px], ['positionY', py]] as const) {
            if (value !== undefined && (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 1)) throw new Error(`invalid_caption_style:${where}.${key}`);
          }
          const outline = opt('outline');
          if (outline !== undefined && (typeof outline !== 'number' || !Number.isFinite(outline) || outline < 0 || outline > 8)) throw new Error(`invalid_caption_style:${where}.outline`);
          const margin = opt('margin');
          if (margin !== undefined && (typeof margin !== 'number' || !Number.isInteger(margin) || margin < 0 || margin > 200)) throw new Error(`invalid_caption_style:${where}.margin`);
          for (const key of ['sourceClipId', 'sequenceItemId']) {
            const v = opt(key);
            if (v !== undefined && typeof v !== 'string') throw new Error(`invalid_caption_style:${where}.${key}`);
          }
          const cueIndex = opt('sourceCueIndex');
          if (cueIndex !== undefined && (typeof cueIndex !== 'number' || !Number.isInteger(cueIndex))) throw new Error(`invalid_caption_style:${where}.sourceCueIndex`);
          for (const key of ['sourceStart', 'sourceEnd']) {
            const v = opt(key);
            if (v !== undefined && (typeof v !== 'number' || !Number.isFinite(v))) throw new Error(`invalid_caption_style:${where}.${key}`);
          }
          for (const key of CAPTION_STYLE_KEYS) set(key, opt(key));
          if (cue.manual !== undefined && cue.manual !== null) {
            if (typeof cue.manual !== 'boolean') throw new Error(`invalid_caption_style:${where}.manual`);
            if (cue.manual) out.manual = true;
          }
          return out as unknown as TimelineCaption;
        });
      }
    } else {
      // source 模式不能隐藏非空/显式独立 cue；native 初始化的 []/缺省不得变成显式清空。
      const wasIndependent = oldMode === 'independent' || base.project.timelineCaptions != null;
      // 用户 undo 首次放置会回到 mode source + 空 cue 列表：这是明确回源动态模式，清掉旧独立数组；非空仍拒。
      if (wasIndependent && !emptyCaptions(sequence.captions)) throw new Error('invalid_caption_mode:independent_to_source');
      if (wasIndependent) project.timelineCaptions = null;
      else if (!same(sequence.captions, oldSequence.captions) && !(emptyCaptions(sequence.captions) && emptyCaptions(oldSequence.captions))) {
        throw new Error('unimplemented_caption_identity:sequence.captions');
      }
    }
    // An ordinary explicit empty timeline has a zero-second domain. Match ValidateForSave;
    // only an event-owned clear can retain its previous time domain.
    if (emptySequence && !clearedSelections?.length && ((project.effectAt ?? 0) > 0 ||
        project.markers?.some(at => at > 0.001) || project.timelineCaptions?.some(c => c.end > 0.001))) {
      throw new Error('empty_timeline_has_annotations');
    }
    // hostCaptions 是只读宿主快照：引擎只按帧派生、从不改写；base 为注入后文档，被改写即拒绝保存。
    if (!same(sequence.hostCaptions, oldSequence.hostCaptions)) throw new Error('unimplemented_caption_identity:sequence.hostCaptions');
    if (!same(sequence.hostEventOriginals, oldSequence.hostEventOriginals)) throw new Error('unimplemented_event_originals:sequence.hostEventOriginals');
    for (const field of ['id', 'mogrt', 'mogrtActive', 'tracks']) if (!same(sequence[field], oldSequence[field])) throw new Error(`unimplemented_sequence_edit:${field}`);
    for (const field of ['sequences', 'activeSequence']) if (!same(archived[field], prior[field])) throw new Error(`unimplemented_sequence_graph_edit:${field}`);
    const shell = archived.projectState === undefined ? null : object(archived.projectState, '$.projectState');
    const oldShell = prior.projectState === undefined ? null : object(prior.projectState, '$base.projectState');
    if (!same(shell?.activeSequence, oldShell?.activeSequence)) throw new Error('unimplemented_sequence_graph_edit:projectState.activeSequence');
    if (shell && (!same(shell.sequence, sequence) || !same(shell.sequences, archived.sequences))) throw new Error('inconsistent_sequence_shell');
    const inspect = (value: unknown, path: string): void => {
      if (typeof value === 'string' && /^(blob:|data:)/i.test(value)) throw new Error(`ephemeral_media_url:${path}`);
      if (!value || typeof value !== 'object') return;
      if (!Array.isArray(value)) {
        const entry = value as JsonObject;
        // An empty/whitespace sourceId means "no media", not a missing registration.
        const entrySourceId = typeof entry.sourceId === 'string' ? entry.sourceId.trim() : entry.sourceId;
        if (entrySourceId !== undefined && entrySourceId !== '' && !registered.has(String(entrySourceId))) throw new Error(`unregistered_media:${path}.sourceId`);
      }
      for (const [key, child] of Object.entries(value)) inspect(child, `${path}.${key}`);
    };
    inspect(archived, '$');
    for (const [field, ids] of Object.entries(archivedClipIds)) if (ids.size) warnings.push({
      path: `replicaExtension.document.clip.${field}`, code: `${field}_archived_not_rendered`,
      message: field === 'effects' ? '效果已保存到工程，但当前导出尚不渲染这些效果' : 'GPU 效果已保存到工程，但当前导出尚不渲染这些效果',
      clipIds: [...ids],
    });
    warn('replicaExtension.document', 'replica_fields_archived', '复刻版完整快照与未知 JSON 已存扩展；扩展保存不代表后端渲染这些字段。');
    if ((project.nestedSequences && Object.keys(project.nestedSequences).length) || project.silenceTrimBackup?.length) warn('project', 'inactive_graph_preserved', '未激活子序列与静音裁剪备份保留原值；本批不支持编辑这些图。');
    for (const issue of list(sequence.unsupported ?? [], 'sequence.unsupported')) warn('sequence.unsupported', 'projection_unimplemented', String(issue));
    const exactTimeMap: JsonObject = { ...(oldExtension?.exactTimeMap ?? {}), fps: project.fps,
      byClipId: Object.fromEntries((project.timeline ?? []).map(item => [item.id, { start: item.start, end: item.end,
        ...(item.gapBefore !== undefined ? { gapBefore: item.gapBefore } : {}), ...(item.fadeIn !== undefined ? { fadeIn: item.fadeIn } : {}),
        ...(item.fadeOut !== undefined ? { fadeOut: item.fadeOut } : {}), frames: oldClips.has(item.id) ? Object.fromEntries(['start', 'end', 'sourceIn'].map(field => [field, tracks.flatMap(track => track.clips).find(clip => clip.key === item.id)![field]])) : {} }])),
    };
    for (const active of [archived.sequence, (archived.projectState as JsonObject | undefined)?.sequence]) {
      if (!active) continue;
      for (const track of tracksOf(object(active, 'sequence'))) for (const clip of track.clips)
        if (clip.transition != null) delete object(clip.transition, 'transition').hostEffectiveWindow;
    }
    const extension: ReplicaExtensionV1 = { ...(oldExtension ?? {}), version: 1, codecVersion: 1, document: archived,
      opaque: { ...(oldExtension?.opaque ?? {}), lcProject: withoutExtension(project) }, exactTimeMap,
      compatibility: { ...(oldExtension?.compatibility ?? {}), requiredVersion: 1, requiredCodecVersion: 1,
        projection: 'lc-editor-next-active-sequence-v1', warnings: warnings.map(warning => `${warning.code}:${warning.path}`) } };
    return { ok: true, project: Object.assign(project, { replicaExtension: extension }) as ReplicaEditorProject, warnings };
  } catch (error) {
    warn('$', 'save_rejected', error instanceof Error ? error.message : String(error));
    return { ok: false, warnings };
  }
}
