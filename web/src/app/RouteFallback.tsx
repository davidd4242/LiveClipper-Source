import './RouteFallback.css';

/**
 * 路由级 Suspense 骨架。
 *
 * - 挂载即可见（Suspense 真正挂起期间才会渲染本组件），无延迟、无淡入；
 * - 3 行 shimmer 使用令牌色，`prefers-reduced-motion` 下为静态占位；
 * - `role=status` + `aria-live=polite`，读屏会播报「页面加载中」。
 */
export function RouteFallback() {
  return (
    <div
      className="lc-route-fallback"
      role="status"
      aria-live="polite"
      aria-busy="true"
      aria-label="页面加载中"
    >
      <div className="lc-route-fallback__panel" aria-hidden="true">
        <div className="lc-route-fallback__bar lc-route-fallback__bar--title" />
        <div className="lc-route-fallback__bar" />
        <div className="lc-route-fallback__bar" />
        <div className="lc-route-fallback__bar lc-route-fallback__bar--short" />
        <span className="lc-route-fallback__hint">页面加载中…</span>
      </div>
    </div>
  );
}
