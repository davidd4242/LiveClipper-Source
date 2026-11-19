import type { TimelineItem } from '../../api/types';
import { itemSpeed } from './project.ts';

export interface ResolvedProgramSource {
  clipId: string;
  url: string;
  time: number;
  speed: number;
}

/** Uses the resolved timeline item, never the independently selected source monitor. */
export function resolveProgramSource(
  resolved: { item: TimelineItem; local: number } | null,
  urlForClip: (clipId: string) => string,
): ResolvedProgramSource | null {
  if (!resolved) return null;
  const speed = itemSpeed(resolved.item);
  return {
    clipId: resolved.item.clipId,
    url: urlForClip(resolved.item.clipId),
    time: resolved.item.start + resolved.local * speed,
    speed,
  };
}

/** URL arguments must use the same absolute URL basis. Metadata from an old source is not seekable. */
export function canSeekProgramSource(readyState: number, currentUrl: string, requestedUrl: string): boolean {
  return readyState >= 1 && currentUrl === requestedUrl;
}
