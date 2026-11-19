import {
  createContext,
  useCallback,
  useContext,
  useLayoutEffect,
  useState,
  type ReactNode,
} from 'react';
import './PageSlideMotion.css';

export interface PageSlideMotionProps {
  children: ReactNode;
  routeKey: string;
  ready: boolean;
  className?: string;
}

const ENTRY_OFFSET = 24;
const MASS = 1;
const DAMPING = 30;
const STIFFNESS = 300;
const ALPHA = DAMPING / (2 * MASS);
const OMEGA = Math.sqrt(STIFFNESS / MASS - ALPHA * ALPHA);
const REST_THRESHOLD = 0.01;

/**
 * The single spring (x / v / RAF) for the right-hand page slot. It lives in PageSlideMotionProvider,
 * outside the keyed route subtree, so it survives route remounts; route nodes only bind/unbind to it.
 */
class SlideController {
  private node: HTMLElement | null = null;
  private routeKey: string | null = null;
  private position = ENTRY_OFFSET;
  private velocity = 0;
  private target = ENTRY_OFFSET;
  private lastTime = 0;
  private frame: number | null = null;
  private reducedMotion: MediaQueryList | null = null;

  start() {
    this.reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    this.reducedMotion.addEventListener('change', this.onMotionChange);
    this.lastTime = performance.now();
  }

  dispose() {
    if (this.frame !== null) cancelAnimationFrame(this.frame);
    this.frame = null;
    this.reducedMotion?.removeEventListener('change', this.onMotionChange);
    this.reducedMotion = null;
  }

  /** Callback-ref target: binds the current route's content node; unbinding keeps the spring state. */
  bind = (node: HTMLElement | null) => {
    this.node = node;
    this.paint();
  };

  update(routeKey: string, ready: boolean) {
    const reduced = this.reducedMotion?.matches ?? false;
    const now = performance.now();
    if (!reduced) this.advance(now);
    else this.lastTime = now;

    if (routeKey !== this.routeKey) {
      this.routeKey = routeKey;
      // Rearm settled entries only; an interrupted spring keeps both position and velocity.
      if (!reduced && this.frame === null && this.position === 0 && this.velocity === 0) {
        this.position = ENTRY_OFFSET;
      }
    }
    this.target = ready ? 0 : ENTRY_OFFSET;
    this.continueMotion();
  }

  private onMotionChange = () => {
    this.lastTime = performance.now();
    this.continueMotion();
  };

  private advance(now: number) {
    const dt = Math.max(0, (now - this.lastTime) / 1000);
    this.lastTime = now;
    const offset = this.position - this.target;
    const decay = Math.exp(-ALPHA * dt);
    const cosine = Math.cos(OMEGA * dt);
    const sine = Math.sin(OMEGA * dt);
    this.position = this.target + decay * (offset * cosine + (this.velocity + ALPHA * offset) / OMEGA * sine);
    this.velocity = decay * (this.velocity * cosine - (ALPHA * this.velocity + STIFFNESS / MASS * offset) / OMEGA * sine);
  }

  private animate = (now: number) => {
    this.frame = null;
    this.advance(now);
    if (Math.abs(this.position - this.target) < REST_THRESHOLD && Math.abs(this.velocity) < REST_THRESHOLD) {
      this.position = this.target;
      this.velocity = 0;
    } else {
      this.frame = requestAnimationFrame(this.animate);
    }
    this.paint();
  };

  private continueMotion() {
    if (this.reducedMotion?.matches) {
      if (this.frame !== null) cancelAnimationFrame(this.frame);
      this.frame = null;
      this.position = 0;
      this.velocity = 0;
      this.lastTime = performance.now();
    } else if (this.frame === null && (this.position !== this.target || this.velocity !== 0)) {
      this.frame = requestAnimationFrame(this.animate);
    }
    this.paint();
  }

  private paint() {
    const node = this.node;
    if (!node) return;
    // Release the transform at rest so fixed-position descendants regain their usual containing block.
    node.style.transform = this.position === 0 && this.velocity === 0 ? 'none' : `translateX(${this.position}px)`;
    // Forward offset shortens the box by the same amount (used by opt-in host CSS) so its right edge never passes the clip.
    node.style.setProperty('--lc-slide-shrink', `${Math.max(this.position, 0)}px`);
    node.style.willChange = this.frame === null ? '' : 'transform';
  }
}

const SlideContext = createContext<SlideController | null>(null);

/** Mount once in App, outside the keyed route boundary. */
export function PageSlideMotionProvider({ children }: { children: ReactNode }) {
  const [controller] = useState(() => new SlideController());

  useLayoutEffect(() => {
    controller.start();
    return () => controller.dispose();
  }, [controller]);

  return <SlideContext.Provider value={controller}>{children}</SlideContext.Provider>;
}

/** Right-hand DOM slot of a page; it only registers its content node with the persistent controller. */
export function PageSlideMotion({ children, routeKey, ready, className }: PageSlideMotionProps) {
  const controller = useContext(SlideContext);
  if (!controller) throw new Error('PageSlideMotion requires PageSlideMotionProvider');
  const bind = useCallback((node: HTMLDivElement | null) => controller.bind(node), [controller]);

  useLayoutEffect(() => {
    controller.update(routeKey, ready);
  }, [controller, routeKey, ready]);

  return (
    <div className={['lc-page-slide-motion', className].filter(Boolean).join(' ')} aria-busy={!ready}>
      <div className="lc-page-slide-motion__content" ref={bind}>
        {children}
      </div>
    </div>
  );
}
