import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import './DownloadUninstallSwitch.css';

export interface DownloadUninstallSwitchProps {
  /** true 已安装；false 未安装；null 状态未知（两按钮禁用，不当作缺失）。 */
  installed: boolean | null;
  busy?: boolean;
  canUninstall?: boolean;
  /** 仅发出意图；确认与真实删除由父层负责。 */
  onDownload: () => void;
  onUninstall: () => void;
  disabledReason?: string;
  className?: string;
}

const ROW_GAP = 8;

export function DownloadUninstallSwitch({
  installed,
  busy = false,
  canUninstall = true,
  onDownload,
  onUninstall,
  disabledReason,
  className,
}: DownloadUninstallSwitchProps) {
  const rootRef = useRef<HTMLDivElement>(null);
  const downloadRef = useRef<HTMLButtonElement>(null);
  const uninstallRef = useRef<HTMLButtonElement>(null);
  const spacerRef = useRef<HTMLSpanElement>(null);
  const setTargetRef = useRef<((installed: boolean) => void) | null>(null);
  const focusFromRef = useRef<'download' | 'uninstall' | null>(null);
  const [moving, setMoving] = useState(false);
  // 已确认落位的状态：仅决定哪个按钮在原位、哪个被藏起来。
  const [shown, setShown] = useState<boolean | null>(installed);
  const movingRef = useRef(false);
  const setMovingBoth = (value: boolean) => {
    if (movingRef.current === value) return;
    movingRef.current = value;
    setMoving(value);
  };

  useLayoutEffect(() => {
    const root = rootRef.current;
    const dl = downloadRef.current;
    const un = uninstallRef.current;
    const spacer = spacerRef.current;
    if (!root || !dl || !un || !spacer) return;

    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const m = 1, c = 30, k = 300;
    const alpha = c / (2 * m);
    const omega = Math.sqrt(k / m - alpha * alpha);
    let position = 0; // 0 未安装，1 已安装
    let velocity = 0;
    let target = 0;
    let pitchX = 0;
    let pitchY = 0;
    let lastTime = performance.now();
    let frame: number | null = null;

    function measure() {
      pitchX = spacer!.offsetLeft - dl!.offsetLeft;
      pitchY = Math.max(dl!.offsetHeight, un!.offsetHeight) + ROW_GAP;
    }

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
      dl!.style.transform = `translate3d(0, ${position * pitchY}px, 0)`;
      un!.style.transform = `translate3d(${(1 - position) * pitchX}px, 0, 0)`;
    }

    function settle() {
      position = target;
      velocity = 0;
      paint();
      setShown(target === 1);
      setMovingBoth(false);
    }

    function animate(now: number) {
      frame = null;
      advance(now);
      if (Math.abs(position - target) < 0.001 && Math.abs(velocity) < 0.001) {
        settle();
      } else {
        paint();
        frame = requestAnimationFrame(animate);
      }
    }

    function run() {
      if (reducedMotion.matches) {
        if (frame !== null) cancelAnimationFrame(frame);
        frame = null;
        settle();
      } else if (frame === null) {
        lastTime = performance.now();
        frame = requestAnimationFrame(animate);
      }
    }

    function setTarget(next: boolean, immediate: boolean) {
      const nextTarget = next ? 1 : 0;
      advance(performance.now());
      if (nextTarget === target && !immediate) return;
      target = nextTarget;
      measure();
      if (immediate) {
        if (frame !== null) cancelAnimationFrame(frame);
        frame = null;
        settle();
        return;
      }
      const active = document.activeElement;
      if (active === dl) focusFromRef.current = 'download';
      else if (active === un) focusFromRef.current = 'uninstall';
      setMovingBoth(true);
      run();
    }

    let initialized = false;
    setTargetRef.current = (next: boolean) => {
      setTarget(next, !initialized);
      initialized = true;
    };
    // 首次确定的状态在外部 effect 里调用；这里只负责布局测量。
    measure();
    paint();

    const observer = new ResizeObserver(() => {
      advance(performance.now());
      measure();
      paint();
    });
    observer.observe(root);
    observer.observe(dl);
    observer.observe(un);
    const onMotionChange = () => {
      if (reducedMotion.matches && movingBoth()) run();
    };
    function movingBoth() {
      return movingRef.current;
    }
    reducedMotion.addEventListener('change', onMotionChange);

    return () => {
      if (frame !== null) cancelAnimationFrame(frame);
      observer.disconnect();
      reducedMotion.removeEventListener('change', onMotionChange);
      setTargetRef.current = null;
    };
  }, []);

  // 仅在确定状态出现时驱动；null 保持现状。首次确定值直接定位，不播动画。
  useLayoutEffect(() => {
    if (installed === null) return;
    setTargetRef.current?.(installed);
  }, [installed]);

  // 动画落位后：若原焦点按钮已飞走，把焦点交给新的可操作按钮。
  useEffect(() => {
    if (moving) return;
    const from = focusFromRef.current;
    if (!from) return;
    focusFromRef.current = null;
    const active = document.activeElement;
    if (active && active !== document.body) return;
    const next = from === 'download' ? uninstallRef.current : downloadRef.current;
    if (next && !next.disabled) next.focus();
  }, [moving]);

  useEffect(() => {
    const dl = downloadRef.current;
    if (!dl) return;
    if (shown === true) dl.setAttribute('inert', '');
    else dl.removeAttribute('inert');
  }, [shown]);

  const unknown = installed === null;
  const isInstalled = shown === true;
  const locked = busy || moving || unknown;
  const downloadDisabled = locked || isInstalled;
  const uninstallDisabled = locked || !isInstalled || !canUninstall;

  const downloadTitle = unknown
    ? '正在读取安装状态'
    : busy
      ? disabledReason || '正在处理，请稍候'
      : '下载离线模型';
  const uninstallTitle = unknown
    ? '正在读取安装状态'
    : busy
      ? disabledReason || '正在处理，请稍候'
      : !isInstalled
        ? '尚未安装，无法卸载'
        : !canUninstall
          ? disabledReason || '当前无法卸载'
          : '卸载离线模型';

  return (
    <div
      ref={rootRef}
      className={['dus', className].filter(Boolean).join(' ')}
      role="group"
      aria-label="离线模型下载与卸载"
      aria-busy={unknown || busy || moving || undefined}
      data-installed={unknown ? 'unknown' : String(isInstalled)}
      data-moving={moving ? 'true' : 'false'}
      style={{ ['--dus-gap' as string]: `${ROW_GAP}px` }}
    >
      <button
        ref={downloadRef}
        type="button"
        className="dus__btn dus__btn--download"
        disabled={downloadDisabled}
        title={downloadTitle}
        aria-label={unknown ? '下载（正在读取安装状态）' : '下载'}
        aria-hidden={isInstalled ? true : undefined}
        tabIndex={isInstalled ? -1 : undefined}
        onClick={() => {
          if (!downloadDisabled) onDownload();
        }}
      >
        下载
      </button>
      <button
        ref={uninstallRef}
        type="button"
        className="dus__btn dus__btn--uninstall"
        disabled={uninstallDisabled}
        title={uninstallTitle}
        aria-label={unknown ? '卸载（正在读取安装状态）' : '卸载'}
        onClick={() => {
          if (!uninstallDisabled) onUninstall();
        }}
      >
        卸载
      </button>
      <span ref={spacerRef} className="dus__spacer" aria-hidden="true" />
    </div>
  );
}
