import { clamp } from './utils';

export interface EnvelopeKeyframe {
  time: number;
  db: number;
}

export interface EnvelopeVoiceInterval {
  start: number;
  end: number;
}

interface EnvelopeCurveProps {
  keyframes: EnvelopeKeyframe[];
  voiceIntervals: EnvelopeVoiceInterval[];
  targetDb: number;
  durationSeconds: number;
}

const VIEW_WIDTH = 100;
const VIEW_HEIGHT = 100;
const DB_FLOOR = -60;

/**
 * Ducking 包络曲线预览。
 *
 * 横轴是工程时间（秒），纵轴是增益（0 dB 顶部 → -60 dB 底部）；虚线是目标
 * 阈值，绿色折线是后端返回的关键帧，浅蓝色块是检测到的人声区间。这里只做
 * 可视化，不参与任何写操作。
 */
export function EnvelopeCurve({ keyframes, voiceIntervals, targetDb, durationSeconds }: EnvelopeCurveProps) {
  const lastTime = keyframes.length > 0 ? keyframes[keyframes.length - 1].time : 0;
  const duration = Math.max(durationSeconds, lastTime, 1);

  const x = (time: number) => clamp(time / duration, 0, 1) * VIEW_WIDTH;
  const y = (db: number) => clamp((0 - db) / Math.abs(DB_FLOOR), 0, 1) * VIEW_HEIGHT;

  const points = keyframes
    .map((keyframe) => `${x(keyframe.time).toFixed(2)},${y(keyframe.db).toFixed(2)}`)
    .join(' ');

  return (
    <div className="music-envelope" data-surface="canvas">
      <svg viewBox={`0 0 ${VIEW_WIDTH} ${VIEW_HEIGHT}`} preserveAspectRatio="none" role="img" aria-label={`ducking 包络：${keyframes.length} 个关键帧，目标 ${targetDb} dB`}>
        {voiceIntervals.map((interval, index) => (
          <rect
            key={`voice-${index}`}
            className="music-envelope-voice"
            x={x(interval.start)}
            y={0}
            width={Math.max(0, x(interval.end) - x(interval.start))}
            height={VIEW_HEIGHT}
          />
        ))}
        <line
          className="music-envelope-target"
          x1={0}
          y1={y(targetDb)}
          x2={VIEW_WIDTH}
          y2={y(targetDb)}
          vectorEffect="non-scaling-stroke"
        />
        <polyline className="music-envelope-line" points={points} vectorEffect="non-scaling-stroke" />
      </svg>
      <span className="music-envelope-axis">0 dB</span>
      <span className="music-envelope-axis music-envelope-axis--bottom">{DB_FLOOR} dB</span>
    </div>
  );
}
