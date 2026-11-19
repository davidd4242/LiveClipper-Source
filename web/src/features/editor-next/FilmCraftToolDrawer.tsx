import { useLayoutEffect, useRef, type ReactNode } from 'react';

export interface FilmCraftToolDrawerProps {
  open: boolean;
  title: string;
  id: string;
  onClose: () => void;
  children: ReactNode;
  width?: number;
}

// Place directly beside the workspace in a flex row; workspace: flex: 1, minWidth: 0.
export function FilmCraftToolDrawer({
  open,
  title,
  id,
  onClose,
  children,
  width = 360,
}: FilmCraftToolDrawerProps) {
  const rootRef = useRef<HTMLDivElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const updateRef = useRef<((expanded: boolean, requestedWidth: number) => void) | null>(null);

  useLayoutEffect(() => {
    const root = rootRef.current;
    const panel = panelRef.current;
    const parent = root?.parentElement;
    if (!root || !panel || !parent) return;

    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const m = 1, c = 30, k = 300;
    const alpha = c / (2 * m);
    const omega = Math.sqrt(k / m - alpha * alpha);
    let expanded = false;
    let requestedWidth = 360;
    let limit = 0;
    let position = 0;
    let velocity = 0;
    let target = 0;
    let lastTime = performance.now();
    let frame: number | null = null;

    function advance(now: number) {
      const dt = Math.max(0, (now - lastTime) / 1000);
      lastTime = now;
      const offset = position - target;
      const decay = Math.exp(-alpha * dt);
      const cosine = Math.cos(omega * dt);
      const sine = Math.sin(omega * dt);
      position = target + decay * (offset * cosine + (velocity + alpha * offset) / omega * sine);
      velocity = decay * (velocity * cosine - (alpha * velocity + k / m * offset) / omega * sine);
    }

    function paint() {
      // Clamp only the visible width, preserving physical state when reversing.
      root!.style.width = `${Math.max(0, Math.min(limit, position))}px`;
      panel!.style.width = `${limit}px`;
    }

    function animate(now: number) {
      frame = null;
      advance(now);
      if (Math.abs(position - target) < 0.01 && Math.abs(velocity) < 0.01) {
        position = target;
        velocity = 0;
      } else {
        frame = requestAnimationFrame(animate);
      }
      paint();
    }

    function continueMotion() {
      if (reducedMotion.matches) {
        if (frame !== null) cancelAnimationFrame(frame);
        frame = null;
        position = target;
        velocity = 0;
        lastTime = performance.now();
        paint();
      } else if (frame === null) {
        frame = requestAnimationFrame(animate);
      }
    }

    function update(nextExpanded: boolean, nextWidth: number) {
      advance(performance.now());
      expanded = nextExpanded;
      requestedWidth = Number.isFinite(nextWidth) ? Math.max(0, nextWidth) : 360;
      limit = Math.min(requestedWidth, parent!.clientWidth * 0.4);
      target = expanded ? limit : 0;
      if (expanded) root!.removeAttribute('inert');
      else root!.setAttribute('inert', '');
      paint();
      continueMotion();
    }

    const observer = new ResizeObserver(() => update(expanded, requestedWidth));
    const onMotionChange = () => {
      advance(performance.now());
      continueMotion();
    };
    updateRef.current = update;
    observer.observe(parent);
    reducedMotion.addEventListener('change', onMotionChange);
    paint();

    return () => {
      if (frame !== null) cancelAnimationFrame(frame);
      observer.disconnect();
      reducedMotion.removeEventListener('change', onMotionChange);
      updateRef.current = null;
    };
  }, []);

  useLayoutEffect(() => {
    updateRef.current?.(open, width);
  }, [open, width]);

  return (
    <div
      ref={rootRef}
      id={id}
      role="complementary"
      aria-labelledby={`${id}-title`}
      aria-hidden={!open}
      style={{
        flex: '0 0 auto',
        width: 0,
        maxWidth: '40%',
        minWidth: 0,
        minHeight: 0,
        alignSelf: 'stretch',
        overflow: 'hidden',
        pointerEvents: open ? 'auto' : 'none',
      }}
    >
      <div
        ref={panelRef}
        style={{
          display: 'flex',
          flexDirection: 'column',
          height: '100%',
          minHeight: 0,
          boxSizing: 'border-box',
          borderLeft: '1px solid rgba(128, 128, 128, 0.25)',
          background: 'var(--panel-background, #17191f)',
          color: 'var(--text-primary, #f4f4f5)',
        }}
      >
        <header style={{ display: 'flex', alignItems: 'center', gap: 12, padding: 16, flexShrink: 0 }}>
          <h2 id={`${id}-title`} style={{ margin: 0, flex: 1, minWidth: 0, fontSize: 16, overflowWrap: 'anywhere' }}>
            {title}
          </h2>
          <button
            type="button"
            aria-label={`关闭${title}`}
            onClick={onClose}
            style={{
              flexShrink: 0,
              width: 32,
              height: 32,
              border: '1px solid rgba(128, 128, 128, 0.4)',
              borderRadius: 6,
              background: 'transparent',
              color: 'inherit',
              cursor: 'pointer',
              fontSize: 20,
            }}
          >
            ×
          </button>
        </header>
        <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: 16 }}>
          {children}
        </div>
      </div>
    </div>
  );
}

export default FilmCraftToolDrawer;
