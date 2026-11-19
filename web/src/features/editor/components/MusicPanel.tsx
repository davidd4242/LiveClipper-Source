import { useState } from 'react';
import { Music2, RefreshCw, LoaderCircle, Check, Volume2 } from 'lucide-react';
import {
  MUSIC_DURATIONS,
  currentMusicLabel,
  findMusicOption,
  formatMusicDuration,
  musicJobLine,
  volumePercent,
  type MusicTrackOption,
} from '../musicTrack';
import type { EditorProject } from '../../../api/types';
import type { MusicJobState } from '../../../api/music';

/**
 * 「配乐」卡片：整场 BGM 的生成 / 选择 / 音量 / 人声压低，全部落到
 * EditorProject.musicAssetId + musicVolume + duckMusic 三个工程字段上。
 * 时间线不加片段（后端渲染时按整片长度 atrim 循环 BGM），所以这里不碰 timeline。
 */
export function MusicPanel({
  project,
  options,
  busy,
  error,
  job,
  onGenerate,
  onRefresh,
  onSelect,
  onToggleDuck,
  onVolume,
}: {
  project: EditorProject | null;
  options: MusicTrackOption[];
  busy: boolean;
  error: string | null;
  job: MusicJobState | null;
  onGenerate: (durationSeconds: number) => void;
  onRefresh: () => void;
  onSelect: (assetId: string | null) => void;
  onToggleDuck: (value: boolean) => void;
  onVolume: (percent: number) => void;
}) {
  const [duration, setDuration] = useState<number>(MUSIC_DURATIONS[1]);
  const selectedId = project?.musicAssetId ?? null;
  const selected = findMusicOption(options, selectedId);
  const volume = volumePercent(project);
  const line = musicJobLine(job);

  return (
    <section className="ai-music" aria-label="配乐">
      <header className="ai-advice-head">
        <span>
          <Music2 size={11} /> 配乐
        </span>
        <button
          type="button"
          className="text-button"
          onClick={onRefresh}
          disabled={busy}
          title="刷新本地音乐素材列表"
        >
          <RefreshCw size={10} /> 刷新
        </button>
      </header>

      <p className="ai-music-current">
        当前：<b>{currentMusicLabel(project, options)}</b>
        {selected?.durationSeconds ? <span className="ai-music-duration">{formatMusicDuration(selected.durationSeconds)}</span> : null}
      </p>
      <p className="ai-music-scope">配乐会整场循环播放，不会新增时间线片段。</p>

      <div className="ai-music-generate">
        <label className="ai-music-field">
          <span>时长</span>
          <select
            value={duration}
            onChange={(event) => setDuration(Number(event.target.value))}
            disabled={busy}
          >
            {MUSIC_DURATIONS.map((seconds) => (
              <option key={seconds} value={seconds}>
                {seconds} 秒
              </option>
            ))}
          </select>
        </label>
        <button type="button" className="primary-button ai-music-cta" onClick={() => onGenerate(duration)} disabled={busy}>
          {busy ? <LoaderCircle size={10} className="spin" /> : <Music2 size={10} />}
          {busy ? '生成中…' : '生成配乐'}
        </button>
      </div>
      {line ? <p className={`ai-music-job${job?.status === 'failed' ? ' is-error' : ''}`}>{line}</p> : null}
      {error ? <p className="ai-error">{error}</p> : null}

      <p className="ai-music-or"><span>或选用已有音乐</span></p>

      <ul className="ai-music-list" aria-label="配乐素材">
        {options.length === 0 ? (
          <li className="ai-music-empty">素材库还没有配乐。点「生成配乐」出一段，或去「音乐」页导入。</li>
        ) : (
          options.slice(0, 8).map((option) => {
            const active = option.id === selectedId;
            return (
              <li key={option.id} className={active ? 'is-active' : undefined}>
                <span className="ai-music-name" title={option.name}>
                  {option.name}
                </span>
                <span className="ai-music-style" title={option.style || '未标注风格'}>
                  {option.durationSeconds ? formatMusicDuration(option.durationSeconds) : option.style || '时长未知'}
                </span>
                <button
                  type="button"
                  className={`ai-music-pick${active ? ' is-active' : ''}`}
                  onClick={() => onSelect(active ? null : option.id)}
                  aria-pressed={active}
                >
                  {active ? <><Check size={10} /> 使用中</> : '选用'}
                </button>
              </li>
            );
          })
        )}
      </ul>

      <div className={`ai-music-mix${selectedId ? '' : ' is-disabled'}`}>
        {selectedId ? null : <span className="ai-music-disabled">添加配乐后可调整</span>}
        <label className="ai-music-switch">
          <input
            type="checkbox"
            checked={Boolean(project?.duckMusic)}
            onChange={(event) => onToggleDuck(event.target.checked)}
            disabled={!selectedId}
          />
          <span>人声出现时压低配乐</span>
        </label>
      </div>

      <label className={`ai-music-volume${selectedId ? '' : ' is-disabled'}`}>
        <span>
          <Volume2 size={10} /> 配乐音量 {volume}%
        </span>
        <input
          type="range"
          min={0}
          max={100}
          step={1}
          value={volume}
          onChange={(event) => onVolume(Number(event.target.value))}
          disabled={!selectedId}
        />
      </label>
    </section>
  );
}
