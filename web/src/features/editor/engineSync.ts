import { useEffect, useMemo, useSyncExternalStore } from 'react';
import type { EditorProject, EditorTransition, TimelineItem } from '../../api/types';
import { getVideoUrl } from '../../api/client';
import type { EngineAdapter, EngineEvent, EngineEventMap, EngineResult } from '../../engine/types';
import { baseParamValue, itemOutputDuration, itemSpeed, normalizeMasks, PARAM_KEYFRAME_PROPS, paramKeyframesFor, sequenceLayout, trackOf } from './project';
import { DEFAULT_GRADE, normalizeGrade } from './colorGrade';
import { singleOperatorEffect } from './engineColor';
import { captionsFor } from './captionOverrides';
import { buildReplicaCaptions } from './replicaProject';
import { includeBaseAudio, includeTrackAudio } from './trackAudioRule';

/** 引擎上限 MAX_CAPTIONS = 2000（fc-captions.js），超出的 cue 丢弃并告警。 */
const ENGINE_CAPTION_LIMIT = 2000;

export interface HostEngineState {
  project: EditorProject | null;
  items: TimelineItem[];
  transitions: EditorTransition[];
  fps: number;
  duration: number;
  sourceDurations?: Record<string, number>;
  sequenceTime: number;
  isPlaying: boolean;
  selectedItemId: string | null;
}

// Crop is not yet part of TimelineItem; imported documents may still carry it.
type CropItem = TimelineItem & {
  crop?: Partial<Record<'left' | 'top' | 'right' | 'bottom' | 'x' | 'y' | 'width' | 'height', number>> & { enabled?: boolean } | null;
};
function hasCrop(item: CropItem): boolean {
  const crop = item.crop;
  return Boolean(crop && crop.enabled !== false && (
    (crop.left ?? 0) !== 0 || (crop.top ?? 0) !== 0 || (crop.right ?? 0) !== 0 || (crop.bottom ?? 0) !== 0 ||
    (crop.x ?? 0) !== 0 || (crop.y ?? 0) !== 0 || (crop.width ?? 1) !== 1 || (crop.height ?? 1) !== 1
  ));
}

/** Gate the entire sequence, including future keyframes, so playback never drops host-only visuals. */
export function needsHostRender(state: HostEngineState, mediaUnbound: readonly string[] = []): boolean {
  return mediaUnbound.length > 0 || state.items.some((item) => {
    const track = trackOf(item);
    return hasHostOnlyColor(item) ||
      PARAM_KEYFRAME_PROPS.some((prop) => {
        const fallback = prop === 'scale' || prop === 'opacity' ? 1 : 0;
        return baseParamValue(item, prop) !== fallback || paramKeyframesFor(item, prop).some((point) => point.value !== fallback);
      }) ||
      Boolean(normalizeMasks(item)?.some((mask) => mask.enabled)) || hasCrop(item) ||
      (track <= 3 && (state.project?.trackOpacities?.[track - 1] ?? 1) !== 1);
  });
}

/**
 * 调色只在两种情况下需要宿主渲染（D54/D65）：
 *   1. 链里有 ≥2 个算子——引擎逐效果 `clamp01`，压成一个矩阵的偏差实测 maxError 16；
 *   2. 链里有引擎表达不了的算子（`invert` 之类）。
 * 单算子（maxError 1）交给引擎的 `channel_mix`，画面不再是"引擎画、宿主调色"两张皮。
 */
function hasHostOnlyColor(item: TimelineItem): boolean {
  const grade = normalizeGrade(item.grade);
  const filter = item.filter ?? 'none';
  const dirty = grade.brightness !== DEFAULT_GRADE.brightness || grade.contrast !== DEFAULT_GRADE.contrast ||
    grade.saturation !== DEFAULT_GRADE.saturation || grade.temperature !== DEFAULT_GRADE.temperature ||
    filter !== 'none';
  if (!dirty) return false;
  return singleOperatorEffect({ filter, grade }) === null;
}

/** 单算子调色 → 一个 `channel_mix` 实例；其余情况不写效果（由宿主渲染兜底）。 */
function engineColorEffects(item: TimelineItem): { effect: string; enabled: boolean; params: Record<string, number> }[] {
  const params = singleOperatorEffect({ filter: item.filter ?? 'none', grade: normalizeGrade(item.grade) });
  return params ? [{ effect: 'channel_mix', enabled: true, params }] : [];
}

function unboundKeys(result: EngineResult): string[] {
  const data = result.data as { mediaUnbound?: unknown } | undefined;
  const top = result as EngineResult & { mediaUnbound?: unknown };
  const keys = [data?.mediaUnbound, top.mediaUnbound].flatMap((value) => Array.isArray(value) ? value : []);
  return [...new Set(keys.filter((key): key is string => typeof key === 'string' && key.length > 0))];
}

/** Persisted __fcproj documents store frames; adapter transport methods take seconds (D31). */
export function engineDocument(state: HostEngineState) {
  const { project, items, transitions, fps, duration, sourceDurations } = state;
  const frame = (seconds: number) => Math.max(0, Math.round(seconds * fps));
  const layout = sequenceLayout(items, transitions, fps, sourceDurations);
  const mapped = new Map(items.map((item) => {
    const start = frame(layout.starts.get(item.id) ?? 0);
    return [item.id, {
      key: item.id, name: item.titleText || item.id,
      kind: trackOf(item) > 3 ? 'audio' : 'video',
      start, end: Math.max(start + 1, frame((layout.starts.get(item.id) ?? 0) + itemOutputDuration(item))),
      sourceIn: frame(item.start), speed: itemSpeed(item), enabled: !item.disabled,
      mediaPath: getVideoUrl(item.clipId), mediaKind: trackOf(item) > 3 ? 'audio' : 'video',
      effects: engineColorEffects(item), fadeIn: frame(item.fadeIn ?? 0), fadeOut: frame(item.fadeOut ?? 0),
      transition: undefined as { id: string; params: { duration: number; align: string } } | undefined,
    }];
  }));
  for (const cut of layout.cuts) {
    const previous = mapped.get(cut.previous.id);
    if (previous) previous.transition = {
      id: cut.transition.kind, params: { duration: cut.effective, align: cut.align },
    };
  }
  const track = (index: number) => ({
    id: index <= 3 ? `V${index}` : `A${index - 3}`,
    name: index <= 3 ? `Video ${index}` : `Audio ${index - 3}`,
    hidden: project?.hiddenTracks?.includes(index) ?? false,
    muted: index === 1
      ? !includeBaseAudio(project?.baseAudioMuted, project?.mutedTracks, project?.soloedTracks)
      : !includeTrackAudio(index, project?.mutedTracks, project?.soloedTracks),
    clips: items.filter((item) => trackOf(item) === index).map((item) => mapped.get(item.id)!),
  });
  return {
    __fcproj: 1,
    sequence: {
      name: 'LiveClipper sequence', fps, duration: frame(duration),
      width: 1920, height: 1080,
      videoTracks: [1, 2, 3].map(track), audioTracks: [4, 5, 6].map(track), markers: [],
      captions: engineCaptions(project, items),
    },
  };
}

/**
 * D45：宿主是字幕的唯一真相源。override → sourceTranscripts 取每源字幕，
 * 再按片段入点/速度换算成序列绝对帧（复用 buildReplicaCaptions 的裁切规则）。
 * 空文本与非有限时间由 buildReplicaCaptions 自己丢弃。
 */
function engineCaptions(project: EditorProject | null, items: TimelineItem[]) {
  if (!project) return [];
  const byClip = new Map<string, { start: number; end: number; text: string }[]>();
  for (const item of items) {
    if (byClip.has(item.clipId)) continue;
    const cues = captionsFor(project, item.clipId, null);
    if (cues.length) byClip.set(item.clipId, cues);
  }
  if (!byClip.size) return [];
  const captions = buildReplicaCaptions(project, byClip);
  if (captions.length <= ENGINE_CAPTION_LIMIT) return captions;
  console.warn(`[engineSync] 字幕 ${captions.length} 条超过引擎上限 ${ENGINE_CAPTION_LIMIT}，已截断`);
  return captions.slice(0, ENGINE_CAPTION_LIMIT);
}

type RecordedEvent = { type: EngineEvent; payload: EngineEventMap[EngineEvent] };

const PROJECT_RETRY_DELAYS = [400, 800, 1600];

/** No host setters enter this controller: engine events can only become diagnostic records. */
export function createEngineSync() {
  let adapter: EngineAdapter | null = null;
  let state: HostEngineState | null = null;
  let projectState: HostEngineState | null = null;
  let fingerprint = '';
  let attemptedFingerprint = '';
  let retries = 0;
  let generation = 0;
  let opening = false;
  let writing = false;
  let lastWrite = -Infinity;
  let lastTime: number | null = null;
  let selected: string | null | undefined;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let retryTimer: ReturnType<typeof setTimeout> | undefined;
  let unsubscribe: (() => void)[] = [];
  let hostOnlyVisuals = false;
  let snapshot = { needsHostRender: false, firstSyncDone: false };
  const listeners = new Set<() => void>();
  const diagnostics = {
    projectPushes: 0, playheadWrites: 0, events: [] as RecordedEvent[], errors: [] as string[], mediaUnbound: [] as string[],
  };
  const publish = (firstSyncDone = snapshot.firstSyncDone) => {
    const fallback = hostOnlyVisuals || diagnostics.mediaUnbound.length > 0;
    if (snapshot.needsHostRender === fallback && snapshot.firstSyncDone === firstSyncDone) return;
    snapshot = { needsHostRender: fallback, firstSyncDone };
    for (const listener of listeners) listener();
  };
  const record = <E extends EngineEvent>(type: E, payload: EngineEventMap[E]) => {
    diagnostics.events.push({ type, payload: structuredClone(payload) });
    if (diagnostics.events.length > 128) diagnostics.events.shift();
  };
  const error = (message: string) => {
    diagnostics.errors.push(message);
    if (diagnostics.errors.length > 20) diagnostics.errors.splice(0, diagnostics.errors.length - 20);
  };
  const report = (result: { ok: boolean; error?: string }, action: string) => {
    if (!result.ok) error(`${action}:${result.error ?? 'failed'}`);
    return result.ok;
  };
  const clearTimer = () => { if (timer !== undefined) clearTimeout(timer); timer = undefined; };
  const clearRetry = () => { if (retryTimer !== undefined) clearTimeout(retryTimer); retryTimer = undefined; };
  const select = () => {
    if (!adapter || !state || !snapshot.firstSyncDone || opening || retryTimer !== undefined || selected === state.selectedItemId) return;
    selected = state.selectedItemId;
    const keys = selected && state.items.some((item) => item.id === selected) ? [selected] : [];
    void adapter.selectClips(keys).then((result) => report(result, 'selection')).catch((reason) => error(`selection:${String(reason)}`));
  };
  const schedule = () => {
    if (!adapter || !state || !snapshot.firstSyncDone || opening || writing || retryTimer !== undefined || timer !== undefined || state.sequenceTime === lastTime) return;
    timer = setTimeout(() => { timer = undefined; void flush(); }, Math.ceil(Math.max(0, 1000 / 30 - (performance.now() - lastWrite))));
  };
  const flush = async () => {
    const current = adapter, stamp = generation;
    if (!current || !state || opening || writing) return;
    writing = true;
    try {
      // Never start an engine clock. If engine transport was used, stop it before following the host.
      if (current.snapshot().playing) await current.pause();
      if (adapter !== current || generation !== stamp || !state) return;
      const seconds = Math.max(0, Math.min(state.duration, state.sequenceTime));
      lastWrite = performance.now();
      lastTime = state.sequenceTime;
      diagnostics.playheadWrites++;
      if (report(await current.setPlayhead(seconds), 'playhead')) await current.requestRender();
    } catch (reason) {
      error(`playhead:${String(reason)}`);
    } finally {
      writing = false;
      schedule();
    }
  };
  const syncProject = (retry = false) => {
    if (!adapter || !state || !state.project || opening || retryTimer !== undefined) return;
    const previous = projectState;
    projectState = state;
    if (!retry && previous && previous.project === state.project && previous.items === state.items &&
      previous.transitions === state.transitions && previous.fps === state.fps &&
      previous.duration === state.duration && previous.sourceDurations === state.sourceDurations) return;
    const document = engineDocument(state);
    const next = JSON.stringify(document);
    if (next === fingerprint) return;
    if (next !== attemptedFingerprint) {
      attemptedFingerprint = next;
      retries = 0;
    } else if (!retry && retries >= PROJECT_RETRY_DELAYS.length) return;
    const current = adapter, stamp = ++generation;
    opening = true;
    clearTimer();
    diagnostics.projectPushes++;
    void (async () => {
      let succeeded = false;
      try {
        const result = await current.openProject(document);
        if (adapter !== current || stamp !== generation) return;
        succeeded = report(result, 'project');
        const keys = unboundKeys(result);
        if (succeeded || keys.length) diagnostics.mediaUnbound = keys;
        if (succeeded) {
          fingerprint = next;
          retries = 0;
          clearRetry();
        }
        publish(snapshot.firstSyncDone || succeeded);
        // Host playback remains the only clock, including while media decoding is pending.
        if (current.snapshot().playing) await current.pause();
      } catch (reason) {
        if (adapter === current && stamp === generation) error(`${succeeded ? 'pause' : 'project'}:${String(reason)}`);
      } finally {
        if (adapter !== current || stamp !== generation) return;
        opening = false;
        if (!succeeded) {
          // A failed open can partially replace engine state; an undo must reopen the old document too.
          fingerprint = '';
          const delay = PROJECT_RETRY_DELAYS[retries];
          if (delay !== undefined) {
            retries++;
            retryTimer = setTimeout(() => {
              retryTimer = undefined;
              // Rebuild from the latest host state; updates during an open/backoff are coalesced.
              syncProject(true);
            }, delay);
          } else {
            syncProject();
          }
          return;
        }
        syncProject();
        if (opening) return;
        lastTime = null;
        selected = undefined;
        select();
        schedule();
      }
    })();
  };
  const dispose = () => {
    generation++;
    clearTimer();
    clearRetry();
    unsubscribe.forEach((off) => off());
    unsubscribe = [];
    adapter = null;
    fingerprint = '';
    attemptedFingerprint = '';
    retries = 0;
    projectState = null;
    opening = false;
    selected = undefined;
    lastTime = null;
    diagnostics.mediaUnbound = [];
    publish(false);
  };
  return {
    diagnostics,
    get needsHostRender() { return snapshot.needsHostRender; },
    get firstSyncDone() { return snapshot.firstSyncDone; },
    getSnapshot: () => snapshot,
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    attach(current: EngineAdapter) {
      if (adapter === current) return;
      dispose();
      adapter = current;
      const events: EngineEvent[] = ['playhead', 'playstate', 'selection', 'project-dirty', 'render-progress', 'media/added'];
      unsubscribe = events.map((event) => current.on(event, (payload) => record(event, payload)));
      syncProject();
      select();
      schedule();
    },
    update(next: HostEngineState) {
      if (!state || state.items !== next.items || state.project !== next.project) hostOnlyVisuals = needsHostRender(next);
      state = next;
      publish();
      syncProject();
      select();
      schedule();
    },
    dispose,
  };
}

export function useEngineSync() {
  const sync = useMemo(createEngineSync, []);
  useSyncExternalStore(sync.subscribe, sync.getSnapshot, sync.getSnapshot);
  useEffect(() => () => sync.dispose(), [sync]);
  return sync;
}
