import { useEffect, useRef, useState } from 'react';
import {
  EMPTY_NATIVE_STATUS,
  nativePanelEntryState,
  openNativePanel,
  readNativeStatus,
  swapNativeStatus,
  statusCanvas,
  statusDirty,
  statusSeconds,
  statusValue,
} from './editorNextNativePanels';
import type { NativeEngineStatus } from './editorNextNativePanels';

/**
 * R82 第三批：底部真实引擎状态 + 右侧「属性」原生面板入口。
 *
 * 数据全部来自真实 bridge：
 * - 帧：playhead 事件的 frame（没有事件时用 seconds×fps 按 bridge 自己的取整换算，frameSource 标 derived）
 * - 秒：getPlayhead()（bridge 返回秒）
 * - fps / 画布：getProjectSummary()
 * - 选择：只显示数量（selection 里是 bridge 的 clip.key，不和原生 name@start 混用）
 * - 保存：直接用 EditorNextPage 的 save 状态文案（不新建 history / 第二份 CAS 状态）
 * - zoom：没有公开 API，不显示（不写假的 100%）
 *
 * 缺失字段一律「—」。epoch 变化（重新载入/换工程）时先注销旧监听再订阅新的，
 * 迟到的回调在 unsubscribe 之后被丢弃，不会把上一代引擎的读数写进当前状态。
 */

const REASON_TEXT: Record<string, string> = {
  bridge_missing: '缺少 FCBridge（引擎未载入）',
  open_panel_unavailable: 'bridge 未包含 openPanel（缺少 fc-host-bridge-panels.js）',
  panel_not_whitelisted: '不是白名单面板',
  dock_unavailable: '原生 Dock 未就绪',
  panel_not_registered: '原生 Dock 未注册该面板',
  panel_not_in_layout: '面板不在当前布局中',
  panel_render_failed: '面板渲染失败',
  panel_not_visible: '面板未真正显示',
  panel_activation_failed: '面板激活抛错',
  panel_not_confirmed: 'bridge 未确认激活',
  panel_mismatch: 'bridge 激活了别的面板',
  engine_not_ready: '引擎未就绪',
  engine_disposed: '引擎已释放',
  busy: '引擎忙',
};

export function reasonText(reason: string | null): string {
  if (reason === null || reason === '') return '';
  return Object.hasOwn(REASON_TEXT, reason) ? REASON_TEXT[reason] : reason;
}

export interface NativeStatusState extends NativeEngineStatus {
  /** 当前订阅所属的 epoch 标记，仅用于调试展示，不参与读数。 */
  epoch: string | null;
}

/** 读一次快照 + 订阅增量；active=false（未就绪/卸载中）时保持「—」。 */
export function useNativeStatus(epoch: string | null, active: boolean): NativeStatusState {
  const [state, setState] = useState<NativeStatusState>(() => ({ ...EMPTY_NATIVE_STATUS, epoch: null }));
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    if (!active) {
      setState({ ...EMPTY_NATIVE_STATUS, reason: 'engine_not_ready', epoch });
      return () => {
        alive.current = false;
      };
    }
    const snapshot = readNativeStatus();
    setState({ ...snapshot, epoch });
    const subscription = swapNativeStatus(null, {
      onPatch: patch => {
        if (!alive.current) return;
        setState(current => ({ ...current, ...patch }));
      },
      onUnavailable: reason => {
        if (!alive.current) return;
        setState(current => ({ ...current, phase: reason === 'bridge_missing' ? 'unavailable' : 'not_ready', reason }));
      },
    });
    return () => {
      alive.current = false;
      subscription.stop();
    };
  }, [epoch, active]);
  return state;
}

export interface EditorNextStatusProps {
  /** 当前引擎 epoch 标记（EditorNextPage: `${clipId}:${attempt}`），变化即重新订阅。 */
  epoch: string | null;
  /** 引擎已就绪才读取；false 时全部显示「—」。 */
  active: boolean;
  /** 直接复用 EditorNextPage 的保存状态文案（含修订号），不新建保存状态源。 */
  saveLabel: string;
}

const barStyle = {
  display: 'flex',
  flexWrap: 'wrap' as const,
  alignItems: 'center',
  gap: '14px',
  padding: '4px 12px',
  borderTop: 'var(--lc-border-width) solid var(--lc-line)',
  fontSize: '12px',
  lineHeight: 1.5,
  opacity: 0.92,
};

const itemStyle = { whiteSpace: 'nowrap' as const };

export function EditorNextStatus({ epoch, active, saveLabel }: EditorNextStatusProps) {
  const status = useNativeStatus(epoch, active);
  return (
    <footer className="editor-next-status" data-status-phase={status.phase} data-status-reason={status.reason ?? ''}
      aria-label="引擎状态" aria-live="off" style={barStyle}>
      <span data-field="time" style={itemStyle}>时间 {statusSeconds(status.seconds)}</span>
      <span data-field="frame" style={itemStyle}>帧 {statusValue(status.frame)}</span>
      <span data-field="fps" style={itemStyle}>fps {statusValue(status.fps, 2)}</span>
      <span data-field="canvas" style={itemStyle}>画布 {statusCanvas(status.width, status.height)}</span>
      <span data-field="selection" style={itemStyle}>
        选择 {status.selectionCount === null ? '—' : `${status.selectionCount} 段`}
      </span>
      <span data-field="dirty" style={itemStyle}>工程 {statusDirty(status.dirty)}</span>
      <span data-field="save" style={itemStyle}>保存 {saveLabel}</span>
      {status.phase !== 'ok' && <span data-field="reason" role="status" style={itemStyle}>
        {reasonText(status.reason) || '引擎读数不可用'}
      </span>}
    </footer>
  );
}

export interface EditorNextPropsButtonProps {
  epoch: string | null;
  /** 引擎就绪才允许打开原生属性面板。 */
  ready: boolean;
}

/**
 * 右侧 rail 的「属性」按钮：只调用原生 Dock 面板（FCBridge.openPanel('properties')）。
 * 不渲染自带 inspector，避免与宿主 InspectorDrawer 重复；失败时显示真实原因而不是盲 toast。
 */
export function EditorNextPropsButton({ epoch, ready }: EditorNextPropsButtonProps) {
  const [entry, setEntry] = useState(() => nativePanelEntryState());
  const [failure, setFailure] = useState<string | null>(null);
  useEffect(() => {
    setEntry(nativePanelEntryState());
    setFailure(null);
  }, [epoch, ready]);
  const disabled = !ready || !entry.available;
  // 失败原因走 title / aria-label / data-reason（rail 很窄，塞一行长文本会挤坏布局），不是盲 toast。
  const reason = !entry.available ? entry.reason : failure;
  const hint = reason === null ? '属性（原生面板）' : `属性（原生面板）· ${reasonText(reason)}`;
  return (
    <button
      type="button"
      className="editor-next-rail__item"
      data-panel-entry="properties"
      data-reason={reason ?? ''}
      aria-label={hint}
      title={hint}
      disabled={disabled}
      onClick={() => {
        const result = openNativePanel('properties');
        setFailure(result.ok ? null : result.error ?? 'panel_not_confirmed');
        if (!result.ok) setEntry(nativePanelEntryState());
      }}
    >
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M4 7h9M17 7h3M4 12h3M11 12h9M4 17h9M17 17h3" />
      </svg>
      <span className="editor-next-rail__caption" aria-hidden="true">属性</span>
      {hint !== null && <span className="editor-next-rail__hint" aria-live="polite">{hint}</span>}
    </button>
  );
}
