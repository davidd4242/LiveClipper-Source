import {
  createContext,
  useCallback,
  useContext,
  useLayoutEffect,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from 'react';
import './NavSidebarMotion.css';

/*
 * 宽文字侧栏的持久弹簧（m1 c30 k300，解析解，与 PageSlideMotion 同一公式）。
 * 状态 open ∈ [0,1]（1 = 完全展开）住在 App 层的 Provider，路由重挂时 x / v / RAF 不丢；
 * 路由里的 NavSidebarSlot 只绑定 DOM 节点：外层裁切槽宽度 = 自然宽度 × open，
 * 内层随之向右平移（向右收起），外层裁切宽度同步缩小，槽自身占布局，工作区跟随真实宽度，没有覆盖浮层。
 */
const MASS = 1;
const DAMPING = 30;
const STIFFNESS = 300;
const ALPHA = DAMPING / (2 * MASS);
const OMEGA = Math.sqrt(STIFFNESS / MASS - ALPHA * ALPHA);
const REST_THRESHOLD = 0.0005;

class NavSidebarController {
  private slot: HTMLElement | null = null;
  private body: HTMLElement | null = null;
  private observer: ResizeObserver | null = null;
  private width = 0;
  private position: number;
  private velocity = 0;
  private target: number;
  private lastTime = 0;
  private frame: number | null = null;
  private reducedMotion: MediaQueryList | null = null;
  private shownValue: boolean;
  private listeners = new Set<() => void>();

  constructor(open: boolean) {
    this.position = open ? 1 : 0;
    this.target = this.position;
    this.shownValue = this.computeShown();
  }

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
    this.observer?.disconnect();
    this.observer = null;
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };

  getShown = () => this.shownValue;

  /** 目标跟随实际 URL；调用时先推进到当前时刻，位置与速度原样保留（中途反向）。 */
  setOpen(open: boolean) {
    const now = performance.now();
    if (this.reducedMotion?.matches) this.lastTime = now;
    else this.advance(now);
    this.target = open ? 1 : 0;
    this.continueMotion();
  }

  bindSlot = (node: HTMLElement | null) => {
    this.slot = node;
    this.paint();
  };

  bindBody = (node: HTMLElement | null) => {
    this.observer?.disconnect();
    this.observer = null;
    this.body = node;
    if (node) {
      this.width = node.offsetWidth;
      this.observer = new ResizeObserver(() => {
        this.width = this.body?.offsetWidth ?? 0;
        this.paint();
      });
      this.observer.observe(node);
    }
    this.paint();
  };

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
      this.position = this.target;
      this.velocity = 0;
      this.lastTime = performance.now();
    } else if (this.frame === null && (this.position !== this.target || this.velocity !== 0)) {
      this.frame = requestAnimationFrame(this.animate);
    }
    this.paint();
  }

  /** 完全收起且不在动时不挂内容（初始进入剪辑页不闪宽栏，也不空转 /api/status 轮询）。 */
  private computeShown() {
    return this.target > 0 || this.position > REST_THRESHOLD || this.frame !== null;
  }

  private paint() {
    const slot = this.slot;
    if (slot) {
      const open = Math.min(1, Math.max(0, this.position));
      const settledOpen = this.frame === null && this.target === 1 && this.position === 1;
      if (settledOpen) {
        slot.style.width = '';
        if (this.body) this.body.style.transform = '';
      } else {
        slot.style.width = `${this.width * open}px`;
        if (this.body) this.body.style.transform = `translateX(${(1 - open) * this.width}px)`;
      }
      // 正在收起或已收起：不可聚焦、不可点击、对读屏隐藏。
      const hidden = this.target === 0;
      if (hidden) {
        slot.setAttribute('inert', '');
        slot.setAttribute('aria-hidden', 'true');
      } else {
        slot.removeAttribute('inert');
        slot.removeAttribute('aria-hidden');
      }
      slot.style.willChange = this.frame === null ? '' : 'width';
    }
    const shown = this.computeShown();
    if (shown !== this.shownValue) {
      this.shownValue = shown;
      this.listeners.forEach((listener) => listener());
    }
  }
}

const NavSidebarContext = createContext<NavSidebarController | null>(null);

/** 挂在 App 层、keyed 路由边界之外；`open` 由实际 URL 推出（有宽侧栏的页面为 true）。 */
export function NavSidebarMotionProvider({ open, children }: { open: boolean; children: ReactNode }) {
  const [controller] = useState(() => new NavSidebarController(open));

  useLayoutEffect(() => {
    controller.start();
    return () => controller.dispose();
  }, [controller]);

  useLayoutEffect(() => {
    controller.setOpen(open);
  }, [controller, open]);

  return <NavSidebarContext.Provider value={controller}>{children}</NavSidebarContext.Provider>;
}

/** 布局槽：放在窄 rail 与工作区之间；只在可见期间挂载 children。 */
export function NavSidebarSlot({ children }: { children: ReactNode }) {
  const controller = useContext(NavSidebarContext);
  if (!controller) throw new Error('NavSidebarSlot requires NavSidebarMotionProvider');
  const shown = useSyncExternalStore(controller.subscribe, controller.getShown, controller.getShown);
  const bindSlot = useCallback((node: HTMLDivElement | null) => controller.bindSlot(node), [controller]);
  const bindBody = useCallback((node: HTMLDivElement | null) => controller.bindBody(node), [controller]);

  return (
    <div className="lc-nav-slot" ref={bindSlot}>
      <div className="lc-nav-slot__body" ref={bindBody}>
        {shown ? children : null}
      </div>
    </div>
  );
}
