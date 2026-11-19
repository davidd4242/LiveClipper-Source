/**
 * 直播工作台的展示格式化工具；只放纯函数，便于在真实页面和验证脚本里复用同一套口径。
 */
import type {
  LiveAvailability,
  LiveRecoveryState,
  LiveSessionState,
} from './liveApi';

const availabilityLabels: Record<LiveAvailability, string> = {
  Unknown: '探测中',
  Offline: '未开播',
  Live: '直播中',
};

const sessionStateLabels: Record<LiveSessionState, string> = {
  Starting: '准备录制',
  Recording: '录制中',
  Reconnecting: '重连中',
  Finalizing: '收尾中',
  Ended: '已结束',
  Interrupted: '已中断',
  Failed: '失败',
};

export function availabilityLabel(value: LiveAvailability | null | undefined): string {
  return value ? availabilityLabels[value] : '未知';
}

export function sessionStateLabel(value: string | null | undefined): string {
  if (!value) return '—';
  return (sessionStateLabels as Record<string, string>)[value] ?? value;
}

export function sessionStateTone(value: string | null | undefined): 'live' | 'warn' | 'bad' | 'idle' {
  switch (value) {
    case 'Recording':
    case 'Finalizing':
      return 'live';
    case 'Reconnecting':
    case 'Starting':
      return 'warn';
    case 'Interrupted':
    case 'Failed':
      return 'bad';
    default:
      return 'idle';
  }
}

export function formatBytes(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value) || value <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

export function formatNumber(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return '—';
  return value.toLocaleString('zh-CN');
}

export function formatClock(value: string | null | undefined): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString('zh-CN', { hour12: false });
}

export function formatTime(value: string | null | undefined): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleTimeString('zh-CN', { hour12: false });
}

export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds <= 0) return '0:00';
  const total = Math.round(seconds);
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  return `${minutes}:${String(rest).padStart(2, '0')}`;
}

export function formatSeconds(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds)) return '—';
  return `${seconds.toFixed(1)}s`;
}

export function shortId(value: string | null | undefined): string {
  if (!value) return '—';
  return value.length > 12 ? value.slice(0, 12) : value;
}

export function registryLoadedLabel(loaded: boolean | null | undefined): string {
  if (loaded == null) return '未知';
  return loaded ? '已加载' : '未加载';
}

/** 恢复状态：成功显示已恢复，失败显示失败次数与下次重试时间。 */
export function recoveryDescription(state: LiveRecoveryState | null | undefined): string {
  if (!state) return '未登记恢复状态';
  if (state.succeeded && state.failures === 0) return '自动恢复成功';
  if (state.failures > 0) {
    const next = state.nextAttemptAt ? `，下次重试 ${formatTime(state.nextAttemptAt)}` : '';
    return `恢复失败 ${state.failures} 次${next}`;
  }
  return '等待恢复扫描';
}

export function recoveryTone(state: LiveRecoveryState | null | undefined): 'ok' | 'warn' | 'idle' {
  if (!state) return 'idle';
  if (state.succeeded && state.failures === 0) return 'ok';
  if (state.failures > 0) return 'warn';
  return 'idle';
}
