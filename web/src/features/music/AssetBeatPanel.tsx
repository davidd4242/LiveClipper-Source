import { useEffect, useState } from 'react';
import { AlertTriangle, LoaderCircle, RefreshCw } from 'lucide-react';
import { getMusicBeats, type MusicBeats, type MusicLibraryAsset } from '../../api/music';
import { clamp, errorMessage } from './utils';

interface AssetBeatPanelProps {
  asset: MusicLibraryAsset;
  projectMusicAssetId: string | null;
  applying: boolean;
  onAnalyze: () => Promise<void>;
  onApplyMusic: (assetId: string) => void;
}

/** 把后端 /beats 的逐拍时间画成网格，并给出按节拍裁切/循环建议。 */
export function AssetBeatPanel({ asset, projectMusicAssetId, applying, onAnalyze, onApplyMusic }: AssetBeatPanelProps) {
  const [beats, setBeats] = useState<MusicBeats | null>(null);
  const [beatsLoading, setBeatsLoading] = useState(false);
  const [beatsError, setBeatsError] = useState<string | null>(null);
  const [targetSeconds, setTargetSeconds] = useState(30);
  const [analyzing, setAnalyzing] = useState(false);
  const [analyzeError, setAnalyzeError] = useState<string | null>(null);

  const hasBpm = typeof asset.analysis?.bpm === 'number';
  const current = asset.id === projectMusicAssetId;

  useEffect(() => {
    let cancelled = false;
    setBeats(null);
    setBeatsError(null);
    if (!hasBpm) {
      return () => {
        cancelled = true;
      };
    }
    setBeatsLoading(true);
    getMusicBeats(asset.id, 30)
      .then((result) => {
        if (!cancelled) {
          setBeats(result);
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setBeatsError(errorMessage(error));
        }
      })
      .finally(() => {
        if (!cancelled) {
          setBeatsLoading(false);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [asset.id, hasBpm]);

  async function loadBeats(seconds: number) {
    setBeatsLoading(true);
    setBeatsError(null);
    try {
      setBeats(await getMusicBeats(asset.id, clamp(Math.round(seconds), 5, 180)));
    } catch (error) {
      setBeatsError(errorMessage(error));
    } finally {
      setBeatsLoading(false);
    }
  }

  async function handleAnalyze() {
    setAnalyzing(true);
    setAnalyzeError(null);
    try {
      await onAnalyze();
    } catch (error) {
      setAnalyzeError(errorMessage(error));
    } finally {
      setAnalyzing(false);
    }
  }

  if (!asset.analysis) {
    return (
      <div className="music-beat-panel">
        <p className="music-empty">这首音乐还没有节拍分析。分析后可以看到 BPM、逐拍网格和循环建议。</p>
        <div className="music-actions">
          <button type="button" className="music-button music-button--primary" disabled={analyzing} onClick={() => void handleAnalyze()}>
            {analyzing ? <LoaderCircle size={13} className="music-spin" aria-hidden="true" /> : null}
            {analyzing ? '分析中…' : '分析节拍'}
          </button>
          {analyzeError ? <span className="music-error">{analyzeError}</span> : null}
        </div>
      </div>
    );
  }

  if (!hasBpm) {
    return (
      <div className="music-beat-panel">
        <p className="music-note music-note--warning">
          <AlertTriangle size={13} aria-hidden="true" />
          分析已完成，但解码样本不足，后端没有估计出 BPM（降级为未知），因此没有节拍网格和循环建议。
        </p>
      </div>
    );
  }

  const duration = beats?.durationSeconds ?? asset.analysis.durationSeconds ?? 0;
  const beatTimes = beats?.beats ?? [];
  const bars = beats?.bars ?? 0;
  const beatsPerBar = bars > 0 && beatTimes.length > 0 ? Math.max(1, Math.round(beatTimes.length / bars)) : 4;
  const loop = beats?.loop ?? null;
  const alignedCut = beats?.alignedCut ?? null;
  const degraded = beats?.degraded === true;

  return (
    <div className="music-beat-panel">
      <div className="music-beat-caption">
        <span>
          BPM {beats?.bpm?.toFixed(2) ?? asset.analysis.bpm?.toFixed(2) ?? '—'}
          {beats?.beatInterval ? ` · 每拍 ${beats.beatInterval.toFixed(2)}s` : ''}
          {bars > 0 ? ` · ${bars} 小节` : ''}
        </span>
        <span>{beats?.beatCount ?? asset.analysis.beats.length} 拍 · {duration.toFixed(1)}s</span>
      </div>

      <div className="music-field-row music-field-row--wrap">
        <label className="music-inline-label">
          目标时长
          <input
            className="music-input music-input--short"
            type="number"
            min={5}
            max={180}
            step={5}
            value={targetSeconds}
            aria-label="循环目标时长（秒）"
            onChange={(event) => setTargetSeconds(clamp(Number(event.target.value) || 5, 5, 180))}
          />
        </label>
        {[15, 30, 60].map((seconds) => (
          <button
            key={seconds}
            type="button"
            className="music-mini-button"
            onClick={() => {
              setTargetSeconds(seconds);
              void loadBeats(seconds);
            }}
          >
            {seconds}s
          </button>
        ))}
        <button type="button" className="music-mini-button" disabled={beatsLoading} onClick={() => void loadBeats(targetSeconds)}>
          {beatsLoading ? '计算中…' : '计算循环'}
        </button>
      </div>

      {beatsError ? <p className="music-error">{beatsError}</p> : null}
      {degraded ? <p className="music-note music-note--warning">{beats?.reason ?? '后端对这首音乐的节拍检测降级了，网格与循环建议仅供参考。'}</p> : null}

      {duration > 0 && beatTimes.length > 0 ? (
        <>
          <div className="music-beat-grid" role="img" aria-label={`${beatTimes.length} 拍节拍网格`}>
            {beatTimes.map((time, index) => (
              <span
                key={`beat-${index}`}
                className={index % beatsPerBar === 0 ? 'music-beat-line is-bar' : 'music-beat-line'}
                style={{ left: `${clamp(time / duration, 0, 1) * 100}%` }}
              />
            ))}
            {loop ? (
              <span
                className="music-beat-loop"
                style={{
                  left: `${clamp(loop.start / duration, 0, 1) * 100}%`,
                  width: `${clamp(loop.seconds / duration, 0, 1) * 100}%`,
                }}
              />
            ) : null}
            {alignedCut && alignedCut.beatSeconds > 0.01 ? (
              <span
                className="music-beat-cut"
                title={`对齐切点 ${alignedCut.beatSeconds.toFixed(2)}s`}
                style={{ left: `${clamp(alignedCut.relativeSeconds / duration, 0, 1) * 100}%` }}
              />
            ) : null}
          </div>
          <div className="music-beat-legend">
            <span>蓝框 = 循环区间</span>
            <span>黄线 = 对齐切点</span>
            <span>每 {beatsPerBar} 拍一小节</span>
          </div>
        </>
      ) : beatsLoading ? (
        <p className="music-empty">正在计算逐拍网格…</p>
      ) : null}

      <div className="music-loop-card">
        <div className="music-loop-head">
          <strong>按节拍裁 / 循环建议</strong>
          <span className="music-hint">{loop?.method ?? '—'}</span>
        </div>
        {loop ? (
          <p className="music-loop-detail">
            循环 {loop.start.toFixed(2)}s → {loop.end.toFixed(2)}s · 共 {loop.seconds.toFixed(2)}s / {loop.barCount} 小节 / {loop.beatCount} 拍
            {alignedCut && alignedCut.beatSeconds > 0 ? ` · 对齐切点 ${alignedCut.beatSeconds.toFixed(2)}s` : ''}
          </p>
        ) : (
          <p className="music-empty">当前目标时长下没有可用的循环建议。</p>
        )}
        <div className="music-actions">
          <button
            type="button"
            className="music-button music-button--primary"
            disabled={applying || current}
            onClick={() => onApplyMusic(asset.id)}
          >
            {current ? '当前已是工程配乐' : applying ? '写入中…' : '应用为工程配乐'}
          </button>
          <button type="button" className="music-button" disabled={beatsLoading} onClick={() => void loadBeats(targetSeconds)}>
            <RefreshCw size={12} aria-hidden="true" />
            重新计算
          </button>
        </div>
        <p className="music-hint">
          v1 限制：工程配乐只保存全局音乐素材 ID，循环/裁切点不会写进时间线；请把上面的建议值带到编辑器手动裁剪。
        </p>
      </div>
    </div>
  );
}
