import type { LoudnessEnvelopePoint } from './api/perceptionClient.ts';

export type LoudnessPoint = Readonly<Pick<LoudnessEnvelopePoint, 't' | 'lufs'> & { end?: number }>;
export type ShotTime = number | Readonly<{ t: number }>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function isTime(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}

/** Sorted unique seconds relative to video start; never substitute rawT or guess an fps. */
export function normalizeShots(response: unknown): number[] {
  if (!isRecord(response) || !Array.isArray(response.cuts)) return [];
  const times = new Set<number>();
  for (const cut of response.cuts) {
    if (isRecord(cut) && isTime(cut.t)) times.add(cut.t);
  }
  return [...times].sort((a, b) => a - b);
}

/** Returns a time, not a timeline edit. Equal-distance ties prefer the earlier cut. */
export function snapToShots(
  timeSec: number,
  cuts: readonly ShotTime[],
  toleranceSec: number,
): number | null {
  if (!isTime(timeSec) || !Number.isFinite(toleranceSec) || toleranceSec < 0 || !Array.isArray(cuts)) return null;
  let nearest: number | null = null;
  let distance = toleranceSec;
  for (const cut of cuts) {
    const time = typeof cut === 'number' ? cut : isRecord(cut) ? cut.t : undefined;
    if (!isTime(time)) continue;
    const delta = Math.abs(time - timeSec);
    if (delta <= toleranceSec && (nearest === null || delta < distance || (delta === distance && time < nearest))) {
      nearest = time;
      distance = delta;
    }
  }
  return nearest;
}

/** Linear LUFS interpolation at t, without bridging silence or uncovered intervals. */
export function loudnessAt(timeSec: number, points: readonly LoudnessPoint[]): number | null {
  if (!isTime(timeSec) || !Array.isArray(points)) return null;
  const ordered = new Map<number, { t: number; end: number | null; lufs: number | null }>();
  for (const point of points) {
    if (!isRecord(point) || !isTime(point.t)) continue;
    const lufs = typeof point.lufs === 'number' && Number.isFinite(point.lufs) ? point.lufs : null;
    const end = isTime(point.end) && point.end > point.t ? point.end : null;
    // Conflicting duplicate samples are unknown, not a guessed audible value.
    if (ordered.has(point.t)) ordered.set(point.t, { t: point.t, end: null, lufs: null });
    else ordered.set(point.t, { t: point.t, end, lufs });
  }
  const samples = [...ordered.values()].sort((a, b) => a.t - b.t);
  for (let index = 0; index < samples.length; index++) {
    const left = samples[index];
    if (timeSec === left.t) return left.lufs;
    if (timeSec < left.t) return null;
    const right = samples[index + 1];
    if (!right) return left.end !== null && timeSec < left.end ? left.lufs : null;
    if (timeSec >= right.t) continue;
    if (left.lufs === null || right.lufs === null || (left.end !== null && left.end < right.t)) return null;
    const ratio = (timeSec - left.t) / (right.t - left.t);
    const value = (1 - ratio) * left.lufs + ratio * right.lufs;
    return Number.isFinite(value) ? value : null;
  }
  return null;
}
