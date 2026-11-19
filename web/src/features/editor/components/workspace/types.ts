import type { EditorTransition, TimelineItem } from '../../../../api/types';
import { itemOutputDuration } from '../../project';

/**
 * 窗口内读取的工程字段。
 * EditorPage 传入完整 EditorProject，这里只声明二级窗口实际用到的部分，
 * 避免修改 EditorPage 的 props 接口。
 */
export type WorkspaceProject = {
  timeline?: TimelineItem[] | null;
  transitions?: EditorTransition[] | null;
  markers?: number[] | null;
  mutedTracks?: number[] | null;
  soloedTracks?: number[] | null;
  hiddenTracks?: number[] | null;
  lockedTracks?: number[] | null;
  fps?: number | null;
  resolution?: number | null;
  bitrateKbps?: number | null;
  masterVolume?: number | null;
  voiceOnly?: boolean | null;
  duckMusic?: boolean | null;
  sentenceMode?: boolean | null;
  burnSubtitles?: boolean | null;
  subtitleFont?: string | null;
  subtitleSize?: number | null;
  subtitleColor?: string | null;
  subtitlePosition?: 'top' | 'middle' | 'bottom' | null;
  subtitleOutline?: number | null;
  subtitleMargin?: number | null;
  start?: number | null;
  end?: number | null;
  trackOpacities?: number[] | null;
  voiceVolume?: number | null;
  musicVolume?: number | null;
  effectVolume?: number | null;
  musicAssetId?: string | null;
  effectAssetId?: string | null;
  silenceTrimmed?: boolean | null;
  silenceTrimmedSeconds?: number | null;
};

export type WorkspaceClip = {
  id: string;
  title?: string | null;
  path?: string | null;
  createdAt?: string | null;
};

/** 时间线上的音频轨：4/5/6 对应 A1/A2/A3。 */
export const AUDIO_TRACKS = [4, 5, 6] as const;

export function trackLabel(track: number): string {
  if (track >= 4 && track <= 6) return `A${track - 3}`;
  if (track === 1) return 'V1';
  if (track === 2) return 'V2';
  if (track === 3) return 'V3';
  return `T${track}`;
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

/** 线性增益（1 ≈ 0 dB）转 dBFS 字符串。 */
export function gainToDbLabel(gain: number | null | undefined): string {
  if (gain === null || gain === undefined || !Number.isFinite(gain) || gain <= 0) return '-∞ dB';
  return formatDb(20 * Math.log10(gain));
}

export function formatDb(db: number | null | undefined): string {
  if (db === null || db === undefined || !Number.isFinite(db)) return '—';
  if (db <= -60.01) return '-∞ dB';
  return `${db >= 0 ? '+' : ''}${db.toFixed(1)} dB`;
}

/** 片段首尾淡化时长（秒），与工程里 0..10s 的约束一致。 */
export function fadeSeconds(item: TimelineItem | null): { fadeIn: number; fadeOut: number } {
  if (!item) return { fadeIn: 0, fadeOut: 0 };
  return { fadeIn: clamp(item.fadeIn ?? 0, 0, 10), fadeOut: clamp(item.fadeOut ?? 0, 0, 10) };
}

/** 淡入/淡出占片段输出时长的百分比，用于预览条。 */
export function fadePercent(item: TimelineItem | null): { inPct: number; outPct: number } {
  if (!item) return { inPct: 0, outPct: 0 };
  const length = Math.max(0.1, itemOutputDuration(item));
  const { fadeIn, fadeOut } = fadeSeconds(item);
  return {
    inPct: clamp((fadeIn / length) * 100, 0, 100),
    outPct: clamp((fadeOut / length) * 100, 0, 100),
  };
}
