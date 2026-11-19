import type { TimelineItem } from '../../../../api/types';
import { formatTimecode, itemOutputDuration, trackDuration, trackOf } from '../../project';
import { Field, Note, Section, Slider } from './ui';
import { AUDIO_TRACKS, clamp, formatDb, gainToDbLabel, trackLabel, type WorkspaceProject } from './types';

type ActiveClip = { item: TimelineItem; start: number };

/** 找出播放头在指定音轨上命中的片段；start 为该片段在序列中的起始时间。 */
function activeClipOnTrack(items: TimelineItem[], track: number, sequenceTime: number): ActiveClip | null {
  let offset = 0;
  for (const item of items) {
    if (trackOf(item) !== track) continue;
    offset += Math.max(0, item.gapBefore ?? 0);
    const length = itemOutputDuration(item);
    if (sequenceTime >= offset && sequenceTime < offset + length) return { item, start: offset };
    offset += length;
  }
  return null;
}

type AudioLevelPanelProps = {
  project: WorkspaceProject | null;
  items: TimelineItem[];
  selectedItem: TimelineItem | null;
  fps: number;
  sequenceTime: number;
  onPatch: (patch: Partial<TimelineItem>, label: string) => void;
  onToggleTrack: (field: 'mutedTracks' | 'soloedTracks', track: number) => void;
  onSeek: (time: number) => void;
};

/**
 * 音频电平表窗口：显示 A1–A3 的真实片段构成、播放头命中的片段、静音/独奏状态与推子位置。
 * 真实峰值读数在主窗口右侧音频面板（AudioMixer），这里不伪造采样。
 */
export function AudioLevelPanel({
  project,
  items,
  selectedItem,
  fps,
  sequenceTime,
  onPatch,
  onToggleTrack,
  onSeek,
}: AudioLevelPanelProps) {
  const mutedTracks = project?.mutedTracks ?? [];
  const soloedTracks = project?.soloedTracks ?? [];
  const anySolo = soloedTracks.length > 0;

  const tracks = AUDIO_TRACKS.map((track) => {
    const clips = items.filter((item) => trackOf(item) === track);
    const averageDb = clips.length
      ? clips.reduce((sum, item) => sum + clamp(item.volumeDb ?? 0, -60, 12), 0) / clips.length
      : null;
    const active = activeClipOnTrack(items, track, sequenceTime);
    const muted = mutedTracks.includes(track);
    const solo = soloedTracks.includes(track);
    const audible = !muted && (!anySolo || solo);
    const activeDb = active ? clamp(active.item.volumeDb ?? 0, -60, 12) : averageDb;
    const meterDb = audible ? activeDb : -60;
    const level = meterDb === null ? 0 : Math.round(((clamp(meterDb, -60, 0) + 60) / 60) * 100);
    const status = muted
      ? '已静音'
      : clips.length === 0
        ? '没有片段'
        : !audible
          ? '独奏模式下静音'
          : active
            ? '播放头在此轨'
            : '等待播放头进入';
    return {
      track,
      clips,
      duration: trackDuration(items, track, project?.transitions, project?.fps ?? fps),
      averageDb,
      active,
      muted,
      solo,
      audible,
      activeDb,
      level,
      status,
    };
  });

  const audioClipCount = tracks.reduce((sum, row) => sum + row.clips.length, 0);
  const masterGain = project?.masterVolume ?? 1;
  const selectedVolume = selectedItem ? clamp(selectedItem.volumeDb ?? 0, -60, 12) : 0;

  return (
    <div className="wsx-stack">
      <Note>
        本面板显示 A1–A3 的片段构成与推子位置，不是实时信号。真实电平表在主窗口右侧音频面板：播放节目时刷新 L/R
        峰值与 M/S/I/TP（dBFS，-60–0）。
      </Note>

      {audioClipCount === 0 ? (
        <Note tone="warning">A1–A3 还没有音频片段。可以把素材放到音频轨，或先选中片段在下方直接调音量。</Note>
      ) : null}

      {tracks.map((row) => {
        const activeStart = row.active ? row.active.start : 0;
        const activeLabel = row.active
          ? `片段 ${items.findIndex((item) => item.id === row.active?.item.id) + 1} · ${formatTimecode(activeStart, fps)}`
          : `${trackLabel(row.track)} 无播放头片段`;
        return (
          <div className={`wsx-audio-row${row.audible ? '' : ' is-silent'}`} key={row.track}>
            <div className="wsx-audio-top">
              <div className="wsx-audio-id">
                <strong>{trackLabel(row.track)}</strong>
                <span>
                  {row.clips.length} 段 · {formatTimecode(row.duration, fps)}
                </span>
              </div>
              <div className="wsx-audio-buttons">
                <button
                  type="button"
                  className={`window-button${row.muted ? ' is-active' : ''}`}
                  aria-pressed={row.muted}
                  onClick={() => onToggleTrack('mutedTracks', row.track)}
                >
                  {row.muted ? '取消静音' : '静音'}
                </button>
                <button
                  type="button"
                  className={`window-button${row.solo ? ' is-active' : ''}`}
                  aria-pressed={row.solo}
                  onClick={() => onToggleTrack('soloedTracks', row.track)}
                >
                  {row.solo ? '取消独奏' : '独奏'}
                </button>
              </div>
            </div>
            <div
              className="wsx-audio-meter"
              role="img"
              aria-label={`${trackLabel(row.track)} 推子 ${formatDb(row.activeDb)}`}
            >
              <span
                className={row.muted || !row.audible ? 'is-muted' : row.solo ? 'is-solo' : 'is-live'}
                style={{ width: `${row.level}%` }}
              />
            </div>
            <div className="wsx-audio-readout">
              <span>推子 {formatDb(row.activeDb)}</span>
              <span>{row.status}</span>
            </div>
            <div className="wsx-audio-readout">
              <span>平均 {row.averageDb === null ? '—' : formatDb(row.averageDb)}</span>
              {row.active ? (
                <button type="button" className="window-link" onClick={() => onSeek(activeStart)}>
                  {activeLabel}
                </button>
              ) : (
                <span>{activeLabel}</span>
              )}
            </div>
          </div>
        );
      })}

      <Section title="选中片段音量" hint={selectedItem ? trackLabel(trackOf(selectedItem)) : '未选择'}>
        {selectedItem ? (
          <>
            <Slider
              label="音量"
              value={selectedVolume}
              min={-60}
              max={12}
              step={0.5}
              suffix=" dB"
              onChange={(value) => onPatch({ volumeDb: value }, '修改片段音量')}
            />
            <div className="window-actions">
              <button type="button" className="window-button" onClick={() => onPatch({ volumeDb: 0 }, '片段音量归零')}>
                恢复 0 dB
              </button>
              <button
                type="button"
                className="window-button"
                onClick={() => onPatch({ volumeDb: -60 }, '静音选中片段')}
              >
                片段静音
              </button>
            </div>
          </>
        ) : (
          <p className="window-muted">在时间线或属性面板选中片段后，可以在这里直接调音量。</p>
        )}
      </Section>

      <Section title="主输出" hint="0 dB = 100%">
        <Field label="主推子" value={`${Math.round(masterGain * 100)}% · ${gainToDbLabel(masterGain)}`} />
        <Field label="音频片段" value={`${audioClipCount} 个`} />
        <Field label="播放头" value={formatTimecode(sequenceTime, fps)} />
        <p className="wsx-hint">静音/独奏会立即写入工程，影响预览与导出；播放时以右侧电平表读数为准。</p>
      </Section>
    </div>
  );
}
