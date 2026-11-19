import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { cn } from '../cn';
import './Toast.css';

export type ToastTone = 'neutral' | 'success' | 'info' | 'error';

export interface ToastProps {
  open: boolean;
  message: ReactNode;
  /** 补充说明（第二行小字）。 */
  description?: ReactNode;
  tone?: ToastTone;
  /** 自动关闭延时（ms）；<= 0 表示不自动关闭。默认 4000。 */
  duration?: number;
  /**
   * 可选标识（如递增序号）。提示复用同一实例时，`id` 变化会重新计时；
   * 不传则只在 `open` 变为 true / `duration` 变化时开始计时。
   */
  id?: string | number;
  /** 可选的操作按钮（通常是 Button variant="link"）。 */
  action?: ReactNode;
  onClose: () => void;
  className?: string;
}

/* ── v1.1：ToastStack（最多 3 条堆叠） ─────────────────────── */

export interface ToastItem {
  id: string | number;
  message: ReactNode;
  description?: ReactNode;
  tone?: ToastTone;
  /** 自动关闭延时（ms）；<= 0 常驻。默认 4000。 */
  duration?: number;
  action?: ReactNode;
}

export interface ToastStackProps {
  /** 按到达顺序排列（新条目在末尾）；组件只渲染最新 max 条。 */
  items: ToastItem[];
  /** 最多同时显示的条数（默认 3）。超出时最旧的立即出队（不渲染）。 */
  max?: number;
  /** 自动计时或点 × 时回调；调用方负责从 items 移除该 id。 */
  onDismiss: (id: ToastItem['id']) => void;
  className?: string;
}

export interface UseToastQueueResult {
  items: ToastItem[];
  /** 入队一条提示，返回生成的 id（同 id 先替换再入队）。 */
  push: (toast: Omit<ToastItem, 'id'> & { id?: ToastItem['id'] }) => ToastItem['id'];
  dismiss: (id: ToastItem['id']) => void;
  clear: () => void;
}

/** v1.1：最多 max 条、超限自动丢弃最旧的简单队列；配合 <ToastStack /> 使用。 */
export function useToastQueue(max = 3): UseToastQueueResult {
  const [items, setItems] = useState<ToastItem[]>([]);
  const seq = useRef(0);
  const limit = Math.max(1, max);

  const push = useCallback<UseToastQueueResult['push']>(
    (toast) => {
      seq.current += 1;
      const id = toast.id ?? `toast-${seq.current}`;
      setItems((prev) => [...prev.filter((item) => item.id !== id), { ...toast, id }].slice(-limit));
      return id;
    },
    [limit],
  );

  const dismiss = useCallback<UseToastQueueResult['dismiss']>((id) => {
    setItems((prev) => prev.filter((item) => item.id !== id));
  }, []);

  const clear = useCallback(() => setItems([]), []);

  return { items, push, dismiss, clear };
}

function CloseIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M6 6l12 12M18 6 6 18" />
    </svg>
  );
}

function ToastRow({ item, onDismiss }: { item: ToastItem; onDismiss: (id: ToastItem['id']) => void }) {
  const onDismissRef = useRef(onDismiss);
  useEffect(() => {
    onDismissRef.current = onDismiss;
  }, [onDismiss]);

  const duration = item.duration ?? 4000;
  useEffect(() => {
    if (duration <= 0) return;
    const timer = window.setTimeout(() => onDismissRef.current(item.id), duration);
    return () => window.clearTimeout(timer);
  }, [item.id, duration]);

  const tone = item.tone ?? 'neutral';

  return (
    <div className={cn('lc-toast', 'lc-toast--stacked')} data-tone={tone}>
      <span className={cn('lc-toast__dot', `lc-toast__dot--${tone}`)} aria-hidden="true" />
      <div className="lc-toast__body">
        <div className="lc-toast__message">{item.message}</div>
        {item.description ? <div className="lc-toast__description">{item.description}</div> : null}
      </div>
      {item.action ? <div className="lc-toast__action">{item.action}</div> : null}
      <button
        type="button"
        className="lc-toast__close"
        onClick={() => onDismiss(item.id)}
        aria-label="关闭提示"
      >
        <CloseIcon />
      </button>
    </div>
  );
}

/**
 * v1.1：最多 max 条同时显示的 Toast 堆叠。
 *
 * - 单一 live region（`role="status"` + max 3 条内只播报新增条目），
 *   条目本身不再各自带 role/aria-live，避免读屏重复播报；
 * - 有 error 条目时整个 region 切到 `aria-live="assertive"`；
 * - 单条场景继续用 <Toast />，行为与 v1.0 完全一致。
 */
export function ToastStack({ items, max = 3, onDismiss, className }: ToastStackProps) {
  const limit = Math.max(1, max);
  const visible = items.slice(-limit);
  const hasError = visible.some((item) => (item.tone ?? 'neutral') === 'error');

  if (visible.length === 0) return null;

  return createPortal(
    <div
      className={cn('lc-toast-stack', className)}
      role="status"
      aria-live={hasError ? 'assertive' : 'polite'}
      aria-atomic="false"
      aria-relevant="additions"
    >
      {visible.map((item) => (
        <ToastRow key={item.id} item={item} onDismiss={onDismiss} />
      ))}
    </div>,
    document.body,
  );
}

export function Toast({
  open,
  message,
  description,
  tone = 'neutral',
  duration = 4000,
  id,
  action,
  onClose,
  className,
}: ToastProps) {
  // onClose 常以内联箭头函数传入；放进 ref 避免父组件每次 render 都重置计时器。
  const onCloseRef = useRef(onClose);
  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    if (!open || duration <= 0) return;
    const timer = window.setTimeout(() => onCloseRef.current(), duration);
    return () => window.clearTimeout(timer);
  }, [open, duration, id]);

  if (!open) return null;

  const assertive = tone === 'error';

  return createPortal(
    <div
      className={cn('lc-toast', className)}
      role={assertive ? 'alert' : 'status'}
      aria-live={assertive ? 'assertive' : 'polite'}
      aria-atomic="true"
    >
      <span className={cn('lc-toast__dot', `lc-toast__dot--${tone}`)} aria-hidden="true" />
      <div className="lc-toast__body">
        <div className="lc-toast__message">{message}</div>
        {description ? <div className="lc-toast__description">{description}</div> : null}
      </div>
      {action ? <div className="lc-toast__action">{action}</div> : null}
      <button type="button" className="lc-toast__close" onClick={onClose} aria-label="关闭提示">
        <CloseIcon />
      </button>
    </div>,
    document.body,
  );
}
