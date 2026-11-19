import type { LoudnessEnvelopePoint } from '../api/perceptionClient';
import type { PerceptionParams, UsePerceptionResult } from '../usePerception';
import './PerceptionMarkers.css';

export interface TimelinePerception extends UsePerceptionResult {
  enabled: boolean;
  clipId: string | null;
  params: PerceptionParams;
  onToggle: () => void;
}

export function PerceptionStatusBar({ perception }: { perception: TimelinePerception }) {
  if (!perception.enabled) return null;
  const failed = perception.status === 'error';
  const message = failed
    ? `感知分析失败：${perception.error?.message ?? '未知错误'}`
    : !perception.clipId
      ? '选择一个素材片段以分析'
      : perception.status === 'loading' || perception.status === 'idle'
        ? '正在分析所选素材…'
        : `分析完成 · ${perception.shots.length} 个切点 · ${perception.loudness.length} 个响度窗口`;
  return (
    <div className={`perception-status${failed ? ' is-error' : ''}`} data-testid="perception-status">
      <span role={failed ? 'alert' : 'status'}>{message}</span>
      <span className="perception-legend" aria-label="感知标记图例">
        <span className="perception-legend-silence">静音段</span>
        <span className="perception-legend-shot">镜头切点</span>
        <span className="perception-legend-loudness">响度包络（−70～0 LUFS）</span>
      </span>
      <span className="perception-params">
        阈值 {perception.params.threshold} · 窗口 {perception.params.window} s · 只读建议
      </span>
      {failed ? <button type="button" className="perception-retry" onClick={perception.refresh}>重试感知分析</button> : null}
    </div>
  );
}

interface PerceptionMarkersProps {
  sourceStart: number;
  sourceEnd: number;
  shots: readonly number[];
  loudness: readonly LoudnessEnvelopePoint[];
}

const LOUDNESS_FLOOR_LUFS = -70;
const LOUDNESS_CEILING_LUFS = 0;

/** Source-relative percentages follow the clip's trim, placement and playback speed. */
export function PerceptionMarkers({ sourceStart, sourceEnd, shots, loudness }: PerceptionMarkersProps) {
  const duration = sourceEnd - sourceStart;
  if (!Number.isFinite(sourceStart) || !Number.isFinite(sourceEnd) || duration <= 0) return null;
  const x = (time: number) => ((time - sourceStart) / duration) * 100;
  const cuts = shots.filter(time => Number.isFinite(time) && time >= sourceStart && time < sourceEnd);
  const buckets = loudness.filter(point =>
    Number.isFinite(point.t) && Number.isFinite(point.end) && point.end > point.t &&
    point.end > sourceStart && point.t < sourceEnd,
  );
  const silence = buckets.filter(point => point.lufs === null);
  let envelope = '';
  let previousEnd: number | null = null;
  for (const point of buckets) {
    if (point.lufs === null || !Number.isFinite(point.lufs)) {
      previousEnd = null;
      continue;
    }
    const start = Math.max(sourceStart, point.t);
    const end = Math.min(sourceEnd, point.end);
    const level = Math.min(LOUDNESS_CEILING_LUFS, Math.max(LOUDNESS_FLOOR_LUFS, point.lufs));
    const y = ((LOUDNESS_CEILING_LUFS - level) / (LOUDNESS_CEILING_LUFS - LOUDNESS_FLOOR_LUFS)) * 100;
    // Each API bucket is an average over its window; silence and gaps break the path.
    envelope += `${previousEnd === start ? 'L' : 'M'}${x(start)},${y} H${x(end)} `;
    previousEnd = end;
  }
  if (cuts.length === 0 && buckets.length === 0) return null;
  const summary = `感知标记：${silence.length} 个静音窗口，${cuts.length} 个镜头切点；响度包络 −70～0 LUFS，仅供参考`;
  return (
    <span className="perception-markers" role="img" aria-label={summary} title={summary}>
      {silence.map((point, index) => (
        <span
          key={`${point.t}-${index}`}
          className="perception-silence"
          style={{ left: `${x(Math.max(sourceStart, point.t))}%`, width: `${(Math.min(sourceEnd, point.end) - Math.max(sourceStart, point.t)) / duration * 100}%` }}
        />
      ))}
      {cuts.map((time, index) => (
        <span key={`${time}-${index}`} className="perception-shot" style={{ left: `${x(time)}%` }} />
      ))}
      {envelope ? (
        <svg className="perception-loudness" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
          <path d={envelope.trim()} vectorEffect="non-scaling-stroke" />
        </svg>
      ) : null}
    </span>
  );
}
