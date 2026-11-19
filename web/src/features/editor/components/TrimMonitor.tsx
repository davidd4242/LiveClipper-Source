import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, Link2, Scissors, X } from 'lucide-react';
import type { EditorTransition, TimelineItem } from '../../../api/types';
import { formatTimecode as formatTimecodeAt } from '../project';
import {
  applyEditPointTrim,
  EDIT_POINT_KIND_LABELS,
  editPointSides,
  largeTrimFrames,
  type EditPoint,
  type EditPointKind,
} from '../trimOps';
import './TrimMonitor.css';

export type TrimMonitorProps = {
  editPoint: EditPoint;
  items: TimelineItem[];
  fps: number;
  width: number;
  height: number;
  sourceDurations?: Record<string, number>;
  transitions?: EditorTransition[];
  getVideoUrl: (clipId: string) => string;
  onExit: () => void;
  onNudge: (frames: number) => void;
  onCommit: (items: TimelineItem[], label: string) => void;
  onApplyTransition: (incomingItemId: string | null) => void;
  onToggleKind: () => void;
  onNotify: (message: string) => void;
};

const HOT_COLORS: Record<EditPointKind, string> = {
  trim: '#f2c43d',
  ripple: '#e53e3e',
  roll: '#f2c43d',
};

/** 单个画面：把 <video> 定在指定素材帧上（暂停态，trim monitor 不需要播放）。 */
function TrimFrame({
  side,
  label,
  url,
  sourceTime,
  fps,
  hot,
  gap,
}: {
  side: 'out' | 'in';
  label: string;
  url: string | null;
  sourceTime: number;
  fps: number;
  hot: boolean;
  gap: boolean;
}) {
  const videoRef = useRef<HTMLVideoElement | null>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !url) return;
    const seek = () => {
      const target = Math.max(0, sourceTime);
      if (Number.isFinite(video.duration) && video.duration > 0) {
        video.currentTime = Math.min(Math.max(0, target), Math.max(0, video.duration - 0.001));
      } else {
        video.currentTime = target;
      }
    };
    if (video.readyState >= 1) {
      seek();
    } else {
      video.addEventListener('loadedmetadata', seek);
      return () => video.removeEventListener('loadedmetadata', seek);
    }
    return undefined;
  }, [url, sourceTime]);

  return (
    <div
      className={`trim-monitor__frame is-${side}${hot ? ' is-hot' : ''}${gap ? ' is-gap' : ''}`}
      style={hot ? { boxShadow: `inset 0 0 0 2px ${HOT_COLORS.trim}` } : undefined}
      data-testid={`trim-monitor-${side}`}
    >
      {gap || !url ? (
        <div className="trim-monitor__gap" data-testid={`trim-monitor-${side}-gap`}>
          <span>(gap)</span>
        </div>
      ) : (
        <video ref={videoRef} className="trim-monitor__video" src={url} muted playsInline preload="auto" />
      )}
      <span className="trim-monitor__badge">{label}</span>
      <span className="trim-monitor__timecode" data-testid={`trim-monitor-${side}-timecode`}>
        {formatTimecodeAt(Math.max(0, sourceTime), fps)}
      </span>
    </div>
  );
}

/**
 * FilmCraft「修剪监视器」（Trim Monitor）v1。
 *
 * 有编辑点时整个节目监视器被本组件替换（fc-spec §3.1）；左 = 出点所属片最后一帧，
 * 右 = 入点所属片第一帧；J/L 动态穿梭、K 提交为一步 undo、Esc 取消；Alt+←/→ 逐帧微调。
 */
export function TrimMonitor({
  editPoint,
  items,
  fps,
  width,
  height,
  sourceDurations,
  transitions,
  getVideoUrl,
  onExit,
  onNudge,
  onCommit,
  onApplyTransition,
  onToggleKind,
  onNotify,
}: TrimMonitorProps) {
  const [offsetFrames, setOffsetFrames] = useState(0);
  const [shuttleSpeed, setShuttleSpeed] = useState(0);
  const offsetRef = useRef(0);
  const itemsRef = useRef(items);
  const editPointRef = useRef(editPoint);
  const fpsRef = useRef(fps);
  const shuttleRef = useRef(0);
  const pointerRef = useRef<{ x: number; base: number } | null>(null);
  itemsRef.current = items;
  editPointRef.current = editPoint;
  fpsRef.current = fps;

  const safeFps = fps > 0 ? fps : 30;
  const sourceDuration = editPoint
    ? sourceDurations?.[items.find((item) => item.id === editPoint.clipId)?.clipId ?? '']
    : undefined;

  // 滚动编辑要求切点两侧都有相邻片段；序列首尾的「(gap)」侧自动降级为普通修剪，
  // 避免 Shift+T 选中边界编辑点后 J/K 静默无事发生。
  const rawSides = useMemo(
    () => editPointSides(items, editPoint, safeFps, transitions, sourceDurations),
    [editPoint, items, safeFps, transitions, sourceDurations],
  );
  const normalizedEditPoint = useMemo<EditPoint>(() => {
    if (editPoint.kind !== 'roll') return editPoint;
    if (!rawSides || rawSides.out.gap || rawSides.in.gap) return { ...editPoint, kind: 'trim' };
    return editPoint;
  }, [editPoint, rawSides]);
  editPointRef.current = normalizedEditPoint;


  const resetOffset = useCallback(() => {
    offsetRef.current = 0;
    setOffsetFrames(0);
  }, []);

  // 编辑点切换时清掉未提交的偏移，避免把上一个切点的偏移带到新切点。
  useEffect(() => {
    resetOffset();
    shuttleRef.current = 0;
    setShuttleSpeed(0);
  }, [editPoint.clipId, editPoint.edge, editPoint.kind, resetOffset]);

  const preview = useMemo(() => {
    const frames = Math.round(offsetFrames);
    if (frames === 0) return { items, label: '', changed: false };
    const result = applyEditPointTrim(items, normalizedEditPoint, frames, { fps: safeFps, sourceDuration });
    if (!result.ok) return { items, label: '', changed: false };
    return { items: result.items, label: result.label, changed: true };
  }, [normalizedEditPoint, items, offsetFrames, safeFps, sourceDuration]);

  const sides = useMemo(
    () => editPointSides(preview.items, normalizedEditPoint, safeFps, transitions, sourceDurations),
    [preview.items, normalizedEditPoint, safeFps, transitions, sourceDurations],
  );

  const commit = useCallback(
    (frames: number) => {
      if (Math.abs(Math.round(frames)) < 1) {
        onNotify('动态修剪没有变化，未写入工程');
        return;
      }
      const result = applyEditPointTrim(itemsRef.current, editPointRef.current, Math.round(frames), {
        fps: fpsRef.current,
        sourceDuration,
      });
      if (!result.ok) {
        onNotify(result.reason);
        resetOffset();
        return;
      }
      onCommit(result.items, result.label);
      resetOffset();
      onNotify(`${result.label}已提交：${Math.abs(Math.round(frames))} 帧（Ctrl+Z 可撤销）`);
    },
    [onCommit, onNotify, resetOffset, sourceDuration],
  );

  const commitRef = useRef(commit);
  commitRef.current = commit;
  const exitRef = useRef(onExit);
  exitRef.current = onExit;
  const nudgeRef = useRef(onNudge);
  nudgeRef.current = onNudge;
  const notifyRef = useRef(onNotify);
  notifyRef.current = onNotify;
  const toggleKindRef = useRef(onToggleKind);
  toggleKindRef.current = onToggleKind;

  // 动态穿梭：J/L 反向/正向，同向再按翻倍（上限 ±8×），反向按重置为 ±1×。
  const shuttle = useCallback((direction: 1 | -1) => {
    const current = shuttleRef.current;
    if (Math.sign(current) === direction) {
      const magnitude = Math.min(8, Math.abs(current) * 2);
      shuttleRef.current = direction * magnitude;
    } else {
      shuttleRef.current = direction;
    }
    setShuttleSpeed(shuttleRef.current);
  }, []);

  useEffect(() => {
    if (shuttleSpeed === 0) return undefined;
    let raf = 0;
    let last = performance.now();
    const tick = (now: number) => {
      const dt = Math.min(0.05, Math.max(0, (now - last) / 1000));
      last = now;
      const framesPerSecond = fpsRef.current;
      const candidate = offsetRef.current + shuttleSpeed * framesPerSecond * dt;
      const rounded = Math.round(candidate);
      // 前几帧 round 到 0 是正常的：继续累积，别把它当成「无法提交」而停表。
      if (rounded === 0) {
        offsetRef.current = candidate;
        setOffsetFrames(candidate);
        raf = requestAnimationFrame(tick);
        return;
      }
      const result = applyEditPointTrim(itemsRef.current, editPointRef.current, rounded, {
        fps: fpsRef.current,
        sourceDuration,
      });
      if (result.ok) {
        offsetRef.current = candidate;
        setOffsetFrames(candidate);
        raf = requestAnimationFrame(tick);
      } else {
        shuttleRef.current = 0;
        setShuttleSpeed(0);
        notifyRef.current(`动态修剪到达边缘：${result.reason}`);
      }
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [shuttleSpeed, sourceDuration]);

  // trim mode 键盘：J/L 穿梭、K 提交、Esc 取消/退出、Alt+方向 nudge。
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const typing = target ? /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.isContentEditable : false;
      if (typing) return;
      const key = event.key.toLowerCase();

      if (event.altKey && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
        event.preventDefault();
        event.stopPropagation();
        const direction = event.key === 'ArrowRight' ? 1 : -1;
        const step = event.shiftKey
          ? largeTrimFrames(sides?.out.track ?? 1)
          : 1;
        nudgeRef.current(direction * step);
        return;
      }
      if (event.ctrlKey || event.metaKey) {
        if (key === 't') {
          event.preventDefault();
          event.stopPropagation();
          toggleKindRef.current();
        }
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        if (Math.abs(Math.round(offsetRef.current)) > 0) {
          resetOffset();
          shuttleRef.current = 0;
          setShuttleSpeed(0);
          notifyRef.current('已取消动态修剪（未写入工程）');
          return;
        }
        exitRef.current();
        return;
      }
      if (key === 'j' || key === 'l') {
        event.preventDefault();
        event.stopPropagation();
        shuttle(key === 'j' ? -1 : 1);
        return;
      }
      if (key === 'k') {
        event.preventDefault();
        event.stopPropagation();
        shuttleRef.current = 0;
        setShuttleSpeed(0);
        commitRef.current(offsetRef.current);
        return;
      }
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [resetOffset, shuttle, sides?.out.track]);

  const beginDrag = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    event.preventDefault();
    (event.currentTarget as HTMLDivElement).setPointerCapture?.(event.pointerId);
    pointerRef.current = { x: event.clientX, base: offsetRef.current };
  };

  const moveDrag = (event: ReactPointerEvent<HTMLDivElement>) => {
    const pointer = pointerRef.current;
    if (!pointer) return;
    const frames = pointer.base + (event.clientX - pointer.x) / 6;
    const rounded = Math.round(frames);
    const result = applyEditPointTrim(itemsRef.current, editPointRef.current, rounded, {
      fps: fpsRef.current,
      sourceDuration,
    });
    if (!result.ok) return;
    offsetRef.current = frames;
    setOffsetFrames(frames);
  };

  const endDrag = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (!pointerRef.current) return;
    pointerRef.current = null;
    (event.currentTarget as HTMLDivElement).releasePointerCapture?.(event.pointerId);
    commitRef.current(offsetRef.current);
  };

  if (!sides) {
    return (
      <section className="panel monitor trim-monitor" data-testid="trim-monitor">
        <div className="panel-head">
          <span className="panel-tab is-active">修剪监视器</span>
          <button type="button" className="monitor-mode-button" onClick={onExit} aria-label="退出修剪监视器">
            退出
          </button>
        </div>
        <div className="trim-monitor__body trim-monitor__body--empty">
          编辑点对应的片段已不存在，按 Esc 退出修剪模式
        </div>
      </section>
    );
  }

  const kindLabel = EDIT_POINT_KIND_LABELS[normalizedEditPoint.kind];
  const hotSide = editPoint.edge;
  const large = largeTrimFrames(sides.out.track ?? sides.in.track ?? 1);
  const shiftFrames = Math.round(offsetFrames);
  const incomingId = sides.in.item?.id ?? null;
  const bothSides = Boolean(sides.out.item && sides.in.item);

  return (
    <section className="panel monitor trim-monitor" data-testid="trim-monitor">
      <div className="panel-head trim-monitor__head">
        <span className="panel-tab is-active">修剪监视器</span>
        <span className="trim-monitor__kind" data-testid="trim-monitor-kind" title="点击或 Ctrl+T 切换修剪类型">
          {kindLabel}
        </span>
        <span className="panel-tab">
          序列 01 · {formatTimecodeAt(sides.cutTime, safeFps)}
        </span>
        <button
          type="button"
          className="monitor-mode-button trim-monitor__exit"
          onClick={onExit}
          aria-label="退出修剪监视器"
          title="退出修剪监视器（Esc）"
          data-testid="trim-monitor-exit"
        >
          <X size={14} />
        </button>
      </div>
      <div className="trim-monitor__stage">
        <div
          className="trim-monitor__pointer"
          onPointerDown={beginDrag}
          onPointerMove={moveDrag}
          onPointerUp={endDrag}
          onPointerCancel={endDrag}
        >
          <TrimFrame
            side="out"
            label={`Out: ${sides.out.item ? `片段 ${sides.out.track}` : 'gap'}`}
            url={sides.out.item ? getVideoUrl(sides.out.item.clipId) : null}
            sourceTime={sides.out.frameSourceTime}
            fps={safeFps}
            hot={hotSide === 'out'}
            gap={sides.out.gap}
          />
          <TrimFrame
            side="in"
            label={`In: ${sides.in.item ? `片段 ${sides.in.track}` : 'gap'}`}
            url={sides.in.item ? getVideoUrl(sides.in.item.clipId) : null}
            sourceTime={sides.in.frameSourceTime}
            fps={safeFps}
            hot={hotSide === 'in'}
            gap={sides.in.gap}
          />
        </div>
      </div>
      <div className="trim-monitor__controls">
        <button
          type="button"
          className="trim-monitor__button"
          onClick={() => onNudge(-large)}
          title={`向左修剪 ${large} 帧（Alt+Shift+←）`}
          aria-label={`向左修剪 ${large} 帧`}
          data-testid="trim-monitor-backward-many"
        >
          <ChevronsLeft size={14} />
        </button>
        <button
          type="button"
          className="trim-monitor__button"
          onClick={() => onNudge(-1)}
          title="向左修剪 1 帧（Alt+←）"
          aria-label="向左修剪 1 帧"
          data-testid="trim-monitor-backward"
        >
          <ChevronLeft size={14} />
        </button>
        <span className="trim-monitor__shift" data-testid="trim-monitor-shift">
          {shiftFrames === 0 ? '0 帧' : `${shiftFrames > 0 ? '+' : ''}${shiftFrames} 帧`}
        </span>
        <button
          type="button"
          className="trim-monitor__button"
          onClick={() => onNudge(1)}
          title="向右修剪 1 帧（Alt+→）"
          aria-label="向右修剪 1 帧"
          data-testid="trim-monitor-forward"
        >
          <ChevronRight size={14} />
        </button>
        <button
          type="button"
          className="trim-monitor__button"
          onClick={() => onNudge(large)}
          title={`向右修剪 ${large} 帧（Alt+Shift+→）`}
          aria-label={`向右修剪 ${large} 帧`}
          data-testid="trim-monitor-forward-many"
        >
          <ChevronsRight size={14} />
        </button>
        <button
          type="button"
          className="trim-monitor__button trim-monitor__button--wide"
          onClick={() => onApplyTransition(incomingId)}
          disabled={!bothSides}
          title={bothSides ? '在此切点应用默认转场' : '切点两侧都有片段才能加转场'}
          data-testid="trim-monitor-apply-transition"
        >
          <Scissors size={14} />
          应用默认转场
        </button>
        <button
          type="button"
          className="trim-monitor__button"
          onClick={onToggleKind}
          title="切换修剪类型（Ctrl+T）"
          aria-label="切换修剪类型"
          data-testid="trim-monitor-toggle-kind"
        >
          <Link2 size={14} />
        </button>
      </div>
      <div className="trim-monitor__hint">
        拖动画面或 <kbd>J</kbd>/<kbd>L</kbd> 动态穿梭 · <kbd>K</kbd> 提交（一步撤销）· <kbd>Esc</kbd> 取消/退出 ·{' '}
        <kbd>Alt</kbd>+<kbd>←</kbd>/<kbd>→</kbd> 逐帧 · 6px = 1 帧
      </div>
      <span className="trim-monitor__size" aria-hidden="true">
        {width}×{height}
      </span>
    </section>
  );
}
