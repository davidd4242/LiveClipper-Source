import type { EditorProject, StudioEvent } from '../../api/types';
import { itemSpeed, sequenceLayout, trackOf } from '../editor/project';
import './r82-editor-next-music.css';

export type ChapterLocation =
  | { ok: true; itemId: string; sourceSeconds: number; seconds: number; frame: number; trimmed: boolean }
  | { ok: false; reason: string };

export function locateChapter(event: StudioEvent, project: EditorProject): ChapterLocation {
  if (!event.clipId?.trim() || !Number.isFinite(event.start) || !Number.isFinite(event.end) || event.start < 0 || event.end <= event.start) {
    return { ok: false, reason: '源事件时间或素材标识无效，无法定位。' };
  }
  const items = project.timeline ?? [];
  const fps = project.fps;
  if (!Number.isFinite(fps) || fps <= 0) return { ok: false, reason: '当前序列帧率无效，无法定位。' };
  const layout = sequenceLayout(items, project.transitions, fps);
  const candidates = items.flatMap(item => {
    if (item.clipId !== event.clipId || item.disabled || item.kind === 'graphic' || trackOf(item) > 3 ||
      project.hiddenTracks?.includes(trackOf(item)) || item.nestedSequenceId || !Number.isFinite(item.start) || !Number.isFinite(item.end)) return [];
    const sourceSeconds = Math.max(event.start, item.start);
    if (sourceSeconds >= Math.min(event.end, item.end)) return [];
    const sequenceStart = layout.starts.get(item.id);
    if (sequenceStart === undefined || !Number.isFinite(sequenceStart)) return [];
    const seconds = sequenceStart + (sourceSeconds - item.start) / itemSpeed(item);
    return [{ ok: true as const, itemId: item.id, sourceSeconds, seconds,
      frame: Math.round(seconds * fps), trimmed: sourceSeconds !== event.start }];
  }).sort((a, b) => Number(a.trimmed) - Number(b.trimmed) || a.seconds - b.seconds);
  return candidates[0] ?? { ok: false, reason: '此事件没有保留在当前可见序列中的源片段，无法定位。' };
}

export function chapterClock(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '时间无效';
  const whole = Math.floor(seconds);
  return `${String(Math.floor(whole / 60)).padStart(2, '0')}:${String(whole % 60).padStart(2, '0')}`;
}

export interface ChapterPanelProps {
  events: readonly StudioEvent[];
  project: EditorProject | null;
  unavailableReason?: string | null;
  onLocate: (location: Extract<ChapterLocation, { ok: true }>, event: StudioEvent) => void;
}

export function ChapterPanel({ events, project, unavailableReason, onLocate }: ChapterPanelProps) {
  const sorted = [...events].sort((a, b) => (Number.isFinite(a.start) ? a.start : Infinity) -
    (Number.isFinite(b.start) ? b.start : Infinity) || a.id.localeCompare(b.id));
  return <section className="editor-next-chapters" aria-label="已有章节事件">
    <p>来自已载入视频的独立事件，只读展示；定位会按当前剪辑的裁剪和速度换算。</p>
    {!sorted.length && <p role="status">还没有章节事件。可在 AI 候选提取中创建；本面板不会自动提取或写入工程。</p>}
    <ol className="editor-next-chapters__list">{sorted.map(event => {
      const location: ChapterLocation = unavailableReason ? { ok: false, reason: unavailableReason } : project ? locateChapter(event, project) :
        { ok: false, reason: '当前工程尚未就绪，无法定位。' };
      return <li key={event.id} className="editor-next-chapters__item">
        <h3>{event.title || '未命名事件'}</h3>
        <p className="editor-next-music__timing">{chapterClock(event.start)} – {chapterClock(event.end)} · {chapterClock(event.end - event.start)}</p>
        <p>{event.summary || '没有摘要'}</p>
        <button type="button" disabled={!location.ok} onClick={() => { if (location.ok) onLocate(location, event); }}
          aria-describedby={`chapter-location-${event.id}`}>定位章节</button>
        <p id={`chapter-location-${event.id}`} className="editor-next-music__hint">{location.ok ?
          `${location.trimmed ? '事件开头已裁剪，定位首个保留片段。' : ''}序列 ${chapterClock(location.seconds)}` : location.reason}</p>
      </li>;
    })}</ol>
  </section>;
}
