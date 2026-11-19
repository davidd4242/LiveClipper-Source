import { getVideoUrl, getPreviewAudioUrl } from '../../api/client';
import type { EditorPayload, TimelineItem, TranscriptSegment } from '../../api/types';
import type { FullEngineDocument, FullEngineLoadOptions } from '../../engine/fullEngineSession';
import { captionsFor } from '../editor/captionOverrides';
import { buildReplicaSequence } from '../editor/replicaProject';
import type { ReplicaTrack } from '../editor/replicaProject';

export function editorNextClipId(search: string): string | null {
  const params = new URLSearchParams(search);
  return (params.get('clipId') ?? params.get('clip'))?.trim() || null;
}

/**
 * 只读宿主源字幕快照：源 clipId → 源媒体秒 TranscriptSegment[]。
 * 键集至少覆盖主源（含尚未入轨、AI 粗剪桥随后才追加的情形）与当前时间线媒体源。
 * 解析优先级与后端 CaptionsFor/Snapshot 一致（captionOverrides → sourceTranscripts →
 * fallback，own-key `[]` 是显式清空）；主源 fallback 用 payload.transcript。
 * 快照只作逐片段推导字幕的只读基准，不随 trim/移动预换算；空快照省略整个字段。
 * 键用与 engine 文档 clip.sourceId 相同的 trim 归一，保证两处对得上。
 */
export function editorNextHostCaptions(payload: EditorPayload): Record<string, TranscriptSegment[]> | undefined {
  if ((!payload?.clip?.id && !payload?.projectIdentity?.id) || !payload.project) throw new Error('editor_project_payload_missing');
  const primaryClipId = payload.clip?.id;
  const snapshot: Record<string, TranscriptSegment[]> = {};
  const sourceIds: string[] = primaryClipId ? [primaryClipId] : [];
  for (const item of payload.project.timeline ?? []) {
    const clipId = (item.clipId ?? '').trim();
    if (item.kind === 'graphic' || !clipId || sourceIds.includes(clipId)) continue;
    sourceIds.push(clipId);
  }
  for (const clipId of sourceIds) {
    const fallback = clipId === primaryClipId ? payload.transcript?.segments ?? [] : null;
    const cues = captionsFor(payload.project, clipId, fallback);
    if (cues.length) snapshot[clipId] = cues;
  }
  return Object.keys(snapshot).length ? snapshot : undefined;
}

export interface EditorNextEventPlacement { version: 1; sourceId: string; eventId: string }
type PlacedItem = TimelineItem & { eventPlacement?: EditorNextEventPlacement | null };

/** Valid wire shape only; a placement whose sourceId differs from the item's source is dropped, never rewritten. */
function placementOf(item: PlacedItem | undefined): EditorNextEventPlacement | undefined {
  const p = item?.eventPlacement;
  if (!p || p.version !== 1 || typeof p.sourceId !== 'string' || typeof p.eventId !== 'string' || !p.eventId.trim()) return undefined;
  if (p.sourceId !== (item.clipId ?? '').trim()) return undefined;
  return { version: 1, sourceId: p.sourceId, eventId: p.eventId };
}

const isUnset = (v: unknown, d: unknown): boolean => v === undefined || v === null || v === d;
const emptyList = (v: unknown): boolean => v === undefined || v === null || (Array.isArray(v) && v.length === 0);
const emptyMap = (v: unknown): boolean => v === undefined || v === null || (typeof v === 'object' && Object.keys(v as object).length === 0);

/**
 * Candidate keys of untouched whole-source primary clips. No time inference: bridge compares the
 * actual initial clip/track baseline at hostLoadDocument. captionOverrides are deliberately ignored
 * (caption refinement does not change the video). Any field outside the known-default set => ineligible.
 */
export function editorNextHostEventOriginals(payload: EditorPayload): { key: string; sourceId: string }[] {
  if (!payload.clip) return [];
  const primary = payload.clip.id;
  const timeline = (payload.project.timeline ?? []) as PlacedItem[];
  if ((payload.project.lockedTracks ?? []).includes(1)) return [];
  const seen = new Set<string>();
  for (const item of timeline) {
    if (seen.has(item.id)) return [];
    seen.add(item.id);
  }
  const sourceItems = timeline.filter((item) => item.id === 'source');
  if (sourceItems.length !== 1 || sourceItems[0].eventPlacement != null) return [];
  const it = sourceItems[0];
  const duration = payload.duration;
  const v1First = buildReplicaSequence(payload.project, payload.clip).videoTracks
    .find((track) => track.id === 'V1')?.clips[0];
  const eligible = it.clipId === primary && it.kind === 'video'
    && isUnset(it.track, 1) && it.start === 0 && Number.isFinite(duration) && duration > 0 && it.end === duration
    && isUnset(it.speed, 1) && isUnset(it.gapBefore, 0) && it.zoom === 1 && it.filter === 'none' && it.motion === 'none'
    && isUnset(it.rotation, 0) && isUnset(it.opacity, 1) && isUnset(it.positionX, 0) && isUnset(it.positionY, 0)
    && isUnset(it.disabled, false) && isUnset(it.volumeDb, 0) && isUnset(it.linkGroup, null) && isUnset(it.audioDetached, false)
    && isUnset(it.fadeIn, 0) && isUnset(it.fadeOut, 0) && isUnset(it.titleText, null)
    && isUnset(it.titleSize, 48) && isUnset(it.titleColor, '#FFFFFF') && isUnset(it.titleX, 0.5) && isUnset(it.titleY, 0.82)
    && it.titleFont == null
    && !it.stickerAssetId && !it.soundAssetId && !it.graphicKind && !it.previewOnly && !it.nestedSequenceId
    && !it.grade && emptyList(it.masks) && emptyList(it.effects) && emptyList(it.volumeKeyframes) && emptyMap(it.keyframes)
    && emptyList((it as unknown as { catalogFx?: unknown }).catalogFx) && emptyList((it as unknown as { catalogAudioFx?: unknown }).catalogAudioFx)
    && v1First != null && v1First.key === it.id && v1First.kind === 'video' && v1First.enabled
    && v1First.start === 0 && !v1First.transition;
  return eligible ? [{ key: it.id, sourceId: primary }] : [];
}

const CAPTION_OPTIONAL = ['font', 'size', 'color', 'positionX', 'positionY', 'outline', 'margin',
  'sourceClipId', 'sequenceItemId', 'sourceCueIndex', 'sourceStart', 'sourceEnd'] as const;

/**
 * 权威 project.timelineCaptions（非 null，含 []）→ native 帧 captions；null/缺省返回 undefined（沿用 source 模式）。
 * start/end 为 round(秒*fps) 整数帧并夹在 0..duration、保证 end>start；sourceStart/End 保持源秒。
 */
export function editorNextTimelineCaptions(
  payload: EditorPayload, fps: number, duration: number,
): Record<string, unknown>[] | undefined {
  const cues = payload.project.timelineCaptions;
  if (cues === undefined || cues === null) return undefined;
  return cues.map((cue) => {
    let start = Math.min(Math.max(0, Math.round(cue.start * fps)), Math.max(0, duration - 1));
    const end = Math.min(Math.max(start + 1, Math.round(cue.end * fps)), duration);
    if (end <= start) start = Math.max(0, end - 1);
    const out: Record<string, unknown> = { id: cue.id, start, end, text: cue.text };
    for (const key of CAPTION_OPTIONAL) {
      const value = (cue as unknown as Record<string, unknown>)[key];
      if (value !== undefined && value !== null) out[key] = value;
    }
    if (cue.manual) out.manual = true;
    return out;
  });
}

export function editorNextDocument(payload: EditorPayload): FullEngineDocument {
  if ((!payload?.clip?.id && !payload?.projectIdentity?.id) || !payload.project) throw new Error('editor_project_payload_missing');
  const sequence = buildReplicaSequence(payload.project, payload.clip);
  if (payload.projectIdentity) {
    sequence.name = payload.projectIdentity.name;
    sequence.fps = payload.project.fps;
  }
  if (!sequence.name?.trim() || !Number.isFinite(sequence.fps) || sequence.fps <= 0 ||
    !Number.isSafeInteger(sequence.duration) || sequence.duration < 0) {
    throw new Error('editor_project_sequence_invalid');
  }
  // An empty clipId means "no media" (graphic/text items are saved with clipId ''); copying '' into the
  // engine document made the save projection reject the snapshot (unregistered_media), so normalise it away.
  const sourceIds = new Map((payload.project.timeline ?? []).map((item) => [item.id, (item.clipId ?? '').trim()]));
  const itemsById = new Map(((payload.project.timeline ?? []) as PlacedItem[]).map((item) => [item.id, item]));
  const sourceOf = (key: string): string | undefined => sourceIds.get(key) || undefined;
  const withSourceIds = (tracks: ReplicaTrack[]) => tracks.map((track) => ({
    ...track,
    clips: track.clips.map((clip) => {
      const eventPlacement = placementOf(itemsById.get(clip.key));
      return { ...clip, sourceId: sourceOf(clip.key), ...(eventPlacement ? { eventPlacement } : {}) };
    }),
  }));
  // The existing mapper has optional undefined fields; C2a accepts JSON values only.
  // hostCaptions 与 hostMusic 同级、同为只读宿主字段：ReplicaSequence 类型不含它，
  // 经 JSON 往返后由 validateHostDocument/applySequence 原样带进引擎（fc-captions 按帧派生）。
  const hostCaptions = editorNextHostCaptions(payload);
  // 独立字幕投影：timelineCaptions 非 null → independent + cue 帧；否则 source 且保原映射。
  // hostCaptions 不受影响，始终是 native transcript 读取的源文稿。
  const independent = editorNextTimelineCaptions(payload, sequence.fps, sequence.duration);
  return JSON.parse(JSON.stringify({
    __fcproj: 1,
    sequence: {
      ...sequence, width: payload.project.canvasWidth ?? payload.width, height: payload.project.canvasHeight ?? payload.height,
      captionMode: independent ? 'independent' : 'source',
      ...(independent ? { captions: independent } : {}),
      hostEffect: { effectAssetId: payload.project.effectAssetId ?? null, effectAt: payload.project.effectAt ?? 0, effectVolume: payload.project.effectVolume ?? 0.5 },
      hostMusic: { musicAssetId: payload.project.musicAssetId ?? null,
        musicVolume: payload.project.musicVolume === undefined ? 0.25 : payload.project.musicVolume,
        duckMusic: payload.project.duckMusic === undefined ? false : payload.project.duckMusic },
      ...(hostCaptions ? { hostCaptions } : {}),
      hostEventOriginals: editorNextHostEventOriginals(payload),
      videoTracks: withSourceIds(sequence.videoTracks), audioTracks: withSourceIds(sequence.audioTracks),
    },
    playhead: 0,
  })) as FullEngineDocument;
}

export function editorNextLoadOptions(payload: EditorPayload, bin: { sourceId: string; name: string }[] = []): FullEngineLoadOptions {
  if ((!payload?.clip?.id && !payload?.projectIdentity?.id) || !payload.project) throw new Error('editor_project_payload_missing');
  const primary = payload.clip;
  const names = new Map(bin.map(item => [item.sourceId, item.name]));
  const sourceIds = new Set([...names.keys(), ...(primary ? [primary.id] : []), ...(payload.project.timeline ?? []).filter(item => item.kind !== 'graphic').map((item) => item.clipId).filter((id) => id?.trim())]);
  return {
    clipVolumeDb: Object.fromEntries((payload.project.timeline ?? []).map(item=>[item.id,item.volumeDb??0])),
    media: Object.fromEntries(Array.from(sourceIds, (sourceId) => [sourceId, {
      url: getVideoUrl(sourceId), audioUrl: getPreviewAudioUrl(sourceId), name: sourceId === primary?.id ? primary.title || sourceId : names.get(sourceId) || sourceId,
      ...(sourceId === primary?.id && typeof payload.hasAudio === 'boolean' ? { hasAudio: payload.hasAudio } : {}),
    }])),
  };
}
