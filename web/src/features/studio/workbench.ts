import type { CSSProperties } from 'react';
import type { ClipDto, RenderStatus } from '../../api/types';

/** Studio 工作台里复用的展示格式化与状态映射；只做展示，不产生任何数据。 */

export type StudioTab = 'projects' | 'library' | 'exports' | 'settings';
export type DateFilter = 'all' | 'today' | 'week' | 'month';
export type SortMode = 'date' | 'name';

export interface StatusMeta {
  label: string;
  tone: 'ok' | 'warn' | 'danger' | 'muted' | 'active';
}

const RENDER_STATUS_META: Record<RenderStatus, StatusMeta> = {
  pending: { label: '排队中', tone: 'muted' },
  running: { label: '渲染中', tone: 'active' },
  paused: { label: '已暂停', tone: 'warn' },
  completed: { label: '已完成', tone: 'ok' },
  failed: { label: '失败', tone: 'danger' },
  cancelled: { label: '已取消', tone: 'muted' },
  unknown: { label: '未知', tone: 'muted' },
};

export function renderStatusMeta(status: RenderStatus): StatusMeta {
  return RENDER_STATUS_META[status] ?? RENDER_STATUS_META.unknown;
}

export function outputStatusMeta(status: string | null | undefined): StatusMeta {
  switch (status) {
    case 'completed':
      return { label: '已完成', tone: 'ok' };
    case 'running':
      return { label: '打包中', tone: 'active' };
    case 'pending':
      return { label: '排队中', tone: 'muted' };
    case 'failed':
      return { label: '失败', tone: 'danger' };
    case 'cancelled':
      return { label: '已取消', tone: 'muted' };
    default:
      return { label: status?.trim() || '未知', tone: 'muted' };
  }
}

export function outputKindLabel(kind: 'package' | 'draft'): string {
  return kind === 'draft' ? '旧 V1 烘焙草稿（非 v3）' : '素材包';
}

export function clipTitle(clip: ClipDto): string {
  return clip.title?.trim() || clip.path.split(/[\\/]/).pop()?.replace(/\.[^.]+$/, '') || clip.id.slice(0, 8);
}

export function fileName(path: string): string {
  return path.split(/[\\/]/).pop() || path;
}

export function dateLabel(value: string | null | undefined): string {
  if (!value) return '未接入';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '日期未知';
  return date.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  });
}

export function relativeDay(value: string): string {
  const time = new Date(value).getTime();
  if (Number.isNaN(time)) return '日期未知';
  const days = Math.floor((Date.now() - time) / 86_400_000);
  if (days <= 0) return '今天';
  if (days === 1) return '昨天';
  if (days < 30) return `${days} 天前`;
  return dateLabel(value);
}

export function formatSeconds(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds)) return '未接入';
  const total = Math.max(0, seconds);
  const minutes = Math.floor(total / 60);
  const rest = total - minutes * 60;
  const secondText = rest.toFixed(2).padStart(5, '0');
  return minutes > 0 ? `${minutes}:${secondText}` : `${rest.toFixed(2)} s`;
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes)) return '未接入';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(value >= 10 ? 1 : 2)} ${units[unit]}`;
}

export function formatPercent(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return '—';
  return `${Math.round(Math.max(0, Math.min(1, value)) * 100)}%`;
}

export function trackLabel(track: number | null | undefined): string {
  if (!track) return 'V1';
  if (track >= 4 && track <= 6) return `A${track - 3}`;
  return `V${track}`;
}

export function tracksInUse(tracks: Array<number | undefined>): string {
  const unique = [...new Set(tracks.filter((track): track is number => typeof track === 'number' && track > 0))].sort(
    (left, right) => left - right,
  );
  return unique.length > 0 ? unique.map((track) => trackLabel(track)).join(' / ') : '—';
}

export function withinFilter(value: string, filter: DateFilter): boolean {
  if (filter === 'all') return true;
  const time = new Date(value).getTime();
  if (Number.isNaN(time)) return false;
  const days = (Date.now() - time) / 86_400_000;
  if (filter === 'today') return days < 1;
  if (filter === 'week') return days <= 7;
  return days <= 30;
}

export const dateFilters: Array<{ id: DateFilter; label: string }> = [
  { id: 'all', label: '全部' },
  { id: 'today', label: '今天' },
  { id: 'week', label: '7 天内' },
  { id: 'month', label: '30 天内' },
];

const thumbPalettes = [
  ['#404f67', '#17202f'],
  ['#705646', '#2d1f1b'],
  ['#45685e', '#152825'],
  ['#63506f', '#241b2b'],
  ['#6d5a35', '#2c2415'],
];

export function thumbStyle(index: number): CSSProperties {
  const colors = thumbPalettes[index % thumbPalettes.length];
  return {
    '--studio-thumb-a': colors[0],
    '--studio-thumb-b': colors[1],
  } as CSSProperties;
}

export function errorMessage(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
