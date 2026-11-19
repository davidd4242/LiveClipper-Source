import { useState } from 'react';
import { usePerception, type UsePerceptionResult } from '../editor/usePerception';
import { perceptionStripModel, perceptionTimeLabel } from './perceptionStripModel';
import './r67-editor-next-perception.css';

interface PerceptionMarkersProps {
  clipId: string | null;
  duration: number;
  sourceName?: string | null;
}

const params = { threshold: 0.3, window: 1 };

export function PerceptionMarkers({ clipId, duration, sourceName }: PerceptionMarkersProps) {
  const [enabled, setEnabled] = useState(false);
  const perception = usePerception(enabled ? clipId : null, params);
  return (
    <section className="editor-next-perception" aria-label="只读感知标记">
      <div className="editor-next-perception-controls">
        <button type="button" aria-pressed={enabled} aria-controls="editor-next-perception-content"
          onClick={() => setEnabled(value => !value)}>感知标记</button>
        <span>只读建议 · 默认关闭 · 不修改工程</span>
      </div>
      <div id="editor-next-perception-content">
        {enabled ? <PerceptionReadout perception={perception} clipId={clipId} duration={duration} sourceName={sourceName} /> :
          <p role="status" aria-live="polite" aria-atomic="true">感知标记已关闭</p>}
      </div>
    </section>
  );
}

export function PerceptionReadout({ perception, clipId, duration, sourceName }: PerceptionMarkersProps & { perception: UsePerceptionResult }) {
  const model = perception.status === 'ready' ? perceptionStripModel(duration, perception.shots, perception.loudness) : null;
  const message = !clipId ? '没有可分析的主素材。' : perception.status === 'error'
    ? `感知分析失败：${perception.error?.message ?? '未知错误'}`
    : perception.status !== 'ready' ? '正在分析主素材…'
      : !model ? '分析完成，但源素材时长无效，无法绘制参考刻度。'
        : model.cuts.length === 0 && model.windows === 0 ? '分析完成：没有镜头切点或可用响度窗口。'
          : `分析完成：${model.cuts.length} 个镜头切点，${model.windows} 个响度窗口。`;
  return (
    <div className="editor-next-perception-readout" data-perception-state={perception.status}>
      <p role="status" aria-live="polite" aria-atomic="true">{message}</p>
      {perception.status === 'error' && clipId && <button type="button" onClick={perception.refresh}>重试感知分析</button>}
      <p>主素材：{sourceName || clipId || '未选择'} · 阈值 {params.threshold} · 窗口 {params.window} 秒</p>
      <p>独立源时间参考条；不随复刻时间线缩放或滚动，不代表剪辑后的位置。</p>
      {model && <figure className="editor-next-perception-figure">
        <figcaption>镜头切点 · 响度包络（−70～0 LUFS）· 静音窗口；仅供参考</figcaption>
        <div className="editor-next-perception-scale" aria-label="源素材秒刻度">
          <span>0 秒</span><span>{perceptionTimeLabel(model.duration / 2)}</span><span>{perceptionTimeLabel(model.duration)}</span>
        </div>
        <div className="editor-next-perception-plot" role="img"
          aria-label={`主素材源时间 0 至 ${perceptionTimeLabel(model.duration)}：${model.cuts.length} 个镜头切点，${model.windows} 个响度窗口，${model.silence.length} 个静音窗口`}>
          {model.silence.map((window, index) => <span key={index} className="editor-next-perception-silence"
            style={{ left: `${window.left}%`, width: `${window.width}%` }} />)}
          {model.cuts.map(cut => <span key={cut.time} className="editor-next-perception-cut" style={{ left: `${cut.left}%` }} />)}
          {model.envelope && <svg className="editor-next-perception-envelope" viewBox="0 0 100 100" preserveAspectRatio="none" focusable="false">
            <title>响度包络，静音和缺口处断开</title>
            <path d={model.envelope} vectorEffect="non-scaling-stroke" />
          </svg>}
        </div>
      </figure>}
    </div>
  );
}
