import type { LoudnessEnvelopePoint } from '../editor/api/perceptionClient';

export interface PerceptionStripModel {
  duration: number;
  cuts: { time: number; left: number }[];
  silence: { left: number; width: number }[];
  envelope: string;
  windows: number;
}

/** This scale is source-relative; it is not the replica's zoomed sequence viewport. */
export function perceptionStripModel(
  duration: number,
  shots: readonly number[],
  loudness: readonly LoudnessEnvelopePoint[],
): PerceptionStripModel | null {
  if (!Number.isFinite(duration) || duration <= 0) return null;
  const x = (time: number) => Math.min(100, Math.max(0, time / duration * 100));
  const cuts = [...new Set(shots.filter(time => Number.isFinite(time) && time >= 0 && time < duration))]
    .sort((a, b) => a - b).map(time => ({ time, left: x(time) }));
  const buckets = loudness.filter(point => Number.isFinite(point.t) && Number.isFinite(point.end) &&
    point.end > point.t && point.end > 0 && point.t < duration &&
    (point.lufs === null || Number.isFinite(point.lufs))).slice().sort((a, b) => a.t - b.t);
  const silence: PerceptionStripModel['silence'] = [];
  let envelope = '';
  let previousEnd: number | null = null;
  for (const point of buckets) {
    const start = Math.max(0, point.t);
    const end = Math.min(duration, point.end);
    if (point.lufs === null) {
      silence.push({ left: x(start), width: x(end) - x(start) });
      previousEnd = null;
      continue;
    }
    const y = -Math.min(0, Math.max(-70, point.lufs)) / 70 * 100;
    // API buckets are averages; gaps and silent windows must not be connected.
    envelope += `${previousEnd === start ? 'L' : 'M'}${x(start)},${y} H${x(end)} `;
    previousEnd = end;
  }
  return { duration, cuts, silence, envelope: envelope.trim(), windows: buckets.length };
}

export function perceptionTimeLabel(seconds: number): string {
  return `${Number(seconds.toFixed(2))} 秒`;
}
