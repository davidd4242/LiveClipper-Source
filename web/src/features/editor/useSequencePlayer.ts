import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import type { EditorTransition, TimelineItem } from '../../api/types';
import { itemForSequence, sequenceDuration, sequenceTimeForSource } from './project';
import { canSeekProgramSource, resolveProgramSource } from './programSource';

export interface SequencePlayer {
  sequenceTime: number;
  sourceTime: number;
  isPlaying: boolean;
  videoDuration: number;
  totalDuration: number;
  error: string | null;
  playPause: () => void;
  seekSequence: (time: number) => void;
  seekSource: (time: number) => void;
}

/**
 * Plays resolved sources through a sequence of source ranges.
 *
 * 播放时钟以**序列时间**为主：rAF 每帧推进 sequenceTime，<video> 跟随序列时间
 * 定位到对应素材帧。这样片段交界、空隙（gap）都能连续播放——空隙里视频暂停、
 * 由节目监视器显示黑场；到达序列末尾才停。之前的实现以 video.currentTime 反推
 * 序列时间，素材区间重叠时映射会卡在剪点不动（docs/…/playback 记录的问题）。
 */
export function useSequencePlayer(
  videoRef: RefObject<HTMLVideoElement | null>,
  items: TimelineItem[],
  mediaKey: string | number | null = null,
  transitions: EditorTransition[] | null = null,
  fps = 30,
  sourceDurations: Record<string, number> | undefined = undefined,
  urlForClip: (clipId: string) => string,
  enabled = true,
  fullSequenceDuration?: number,
): SequencePlayer {
  const [sequenceTime, setSequenceTime] = useState(0);
  const [sourceTime, setSourceTime] = useState(0);
  const [isPlaying, setIsPlaying] = useState(false);
  const [videoDuration, setVideoDuration] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const itemsRef = useRef(items);
  const transitionsRef = useRef(transitions);
  const fpsRef = useRef(fps);
  const sourceDurationsRef = useRef(sourceDurations);
  const fullSequenceDurationRef = useRef(fullSequenceDuration);
  const lastSequenceRef = useRef(0);
  const clockRef = useRef(0);
  const playingRef = useRef(false);
  const rafRef = useRef<number | null>(null);
  const lastTickRef = useRef(0);
  const atEndRef = useRef(false);
  const urlForClipRef = useRef(urlForClip);
  urlForClipRef.current = urlForClip;

  itemsRef.current = items;
  transitionsRef.current = transitions;
  fpsRef.current = fps;
  sourceDurationsRef.current = sourceDurations;
  fullSequenceDurationRef.current = fullSequenceDuration;

  const totalSequence = useCallback(
    () => fullSequenceDurationRef.current ?? sequenceDuration(itemsRef.current, transitionsRef.current, fpsRef.current, sourceDurationsRef.current),
    [],
  );

  const applySequenceTime = useCallback((next: number) => {
    clockRef.current = next;
    lastSequenceRef.current = next;
    setSequenceTime(next);
  }, []);

  /** 把 <video> 对准序列时间；空隙里返回 false 并暂停视频（节目监视器负责黑场）。 */
  const syncVideoToSequence = useCallback(
    (time: number, allowPlay: boolean, forceSeek = false): boolean => {
      const video = videoRef.current;
      const resolved = itemForSequence(itemsRef.current, time, transitionsRef.current, fpsRef.current);
      const source = resolveProgramSource(resolved, urlForClipRef.current);
      if (!video || !source) {
        if (video && !video.paused) video.pause();
        return false;
      }
      const requestedUrl = new URL(source.url, document.baseURI).href;
      setSourceTime(source.time);
      if (video.src !== requestedUrl) {
        video.pause();
        video.src = source.url;
        video.load();
        setError(null);
      }
      if (!canSeekProgramSource(video.readyState, video.currentSrc, requestedUrl)) return false;
      const speed = source.speed;
      const targetSource = source.time;
      if (Math.abs(video.playbackRate - speed) > 0.01) video.playbackRate = speed;
      if (forceSeek || video.ended || Math.abs(video.currentTime - targetSource) > (allowPlay ? 0.14 : 0.001)) {
        try {
          video.currentTime = targetSource;
        } catch {
          // 元数据还没就绪时 seek 会被忽略，下一帧会重试。
        }
      }
      setSourceTime(targetSource);
      if (allowPlay && video.paused) {
        void video.play().catch((reason: unknown) => {
          const latest = resolveProgramSource(itemForSequence(itemsRef.current, clockRef.current, transitionsRef.current, fpsRef.current), urlForClipRef.current);
          if (!latest || new URL(latest.url, document.baseURI).href !== requestedUrl || video.currentSrc !== requestedUrl) return;
          if (reason instanceof DOMException && reason.name === 'AbortError') return;
          if (reason instanceof Error && /interrupted|aborted/i.test(reason.message)) return;
          setError(reason instanceof Error ? reason.message : '浏览器未能开始播放');
        });
      }
      return true;
    },
    [videoRef],
  );

  const stopClock = useCallback(() => {
    playingRef.current = false;
    if (rafRef.current !== null) {
      cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    }
    setIsPlaying(false);
    const video = videoRef.current;
    if (video && !video.paused) video.pause();
  }, [videoRef]);

  const tick = useCallback(
    (now: number) => {
      if (!playingRef.current) return;
      const dt = Math.min(0.25, Math.max(0, (now - lastTickRef.current) / 1000));
      lastTickRef.current = now;
      const total = totalSequence();
      const next = clockRef.current + dt;
      if (total <= 0 || next >= total - 1e-4) {
        // 播放到序列末尾：把播放头钉在末尾。video 的 pause/seeked 事件随后会带着
        // 旧素材帧回来，不能再用它反推序列时间（否则播放头会回退两帧）。
        atEndRef.current = true;
        applySequenceTime(Math.max(0, total));
        stopClock();
        return;
      }
      applySequenceTime(next);
      syncVideoToSequence(next, true);
      rafRef.current = requestAnimationFrame(tick);
    },
    [applySequenceTime, stopClock, syncVideoToSequence, totalSequence],
  );

  const startClock = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    const total = totalSequence();
    if (total <= 0) return;
    if (clockRef.current >= total - 0.05) applySequenceTime(0);
    atEndRef.current = false;
    playingRef.current = true;
    setIsPlaying(true);
    lastTickRef.current = performance.now();
    syncVideoToSequence(clockRef.current, true);
    rafRef.current = requestAnimationFrame(tick);
  }, [applySequenceTime, syncVideoToSequence, tick, totalSequence, videoRef]);

  const seekSequence = useCallback(
    (time: number) => {
      const total = totalSequence();
      const target = Math.min(Math.max(0, time), Math.max(0, total - 0.001));
      atEndRef.current = false;
      applySequenceTime(target);
      if (playingRef.current) lastTickRef.current = performance.now();
      syncVideoToSequence(target, playingRef.current);
    },
    [applySequenceTime, syncVideoToSequence, totalSequence],
  );

  const seekSource = useCallback(
    (time: number) => {
      const video = videoRef.current;
      const currentItems = itemsRef.current;
      if (!video || currentItems.length === 0) return;
      const clamped = Math.max(currentItems[0].start, Math.min(time, currentItems[currentItems.length - 1].end));
      atEndRef.current = false;
      const mapped = sequenceTimeForSource(currentItems, clamped, transitionsRef.current, fpsRef.current);
      if (mapped !== null) seekSequence(mapped);
    },
    [seekSequence, videoRef],
  );

  const playPause = useCallback(() => {
    if (playingRef.current) stopClock();
    else startClock();
  }, [startClock, stopClock]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    const matchesCurrentSource = () => {
      const source = resolveProgramSource(itemForSequence(itemsRef.current, clockRef.current, transitionsRef.current, fpsRef.current), urlForClipRef.current);
      return source !== null && canSeekProgramSource(video.readyState, video.currentSrc, new URL(source.url, document.baseURI).href);
    };
    const onLoadedMetadata = () => {
      if (!matchesCurrentSource()) return;
      setVideoDuration(Number.isFinite(video.duration) ? video.duration : 0);
      syncVideoToSequence(clockRef.current, playingRef.current, true);
    };
    const onTimeUpdate = () => {
      // Sequence time owns position even while paused; late media events never move the clock.
      if (matchesCurrentSource()) setSourceTime(video.currentTime);
    };

    const onPlay = () => {
      // 只在时钟驱动时同步播放状态；空隙里 video 被暂停不应影响时钟。
      if (playingRef.current) setIsPlaying(true);
      setError(null);
    };
    const onPause = () => {
      if (!playingRef.current) setIsPlaying(false);
    };
    const onEnded = () => {
      if (!playingRef.current) return;
      // 时钟到末尾时 stopClock 会停；这里只处理 video 提前结束（例如素材比片段短）。
      syncVideoToSequence(clockRef.current, true);
    };
    const onError = () => {
      setError(video.error?.message || '视频无法播放');
      if (playingRef.current) stopClock();
    };

    video.addEventListener('loadedmetadata', onLoadedMetadata);
    video.addEventListener('timeupdate', onTimeUpdate);
    video.addEventListener('seeked', onTimeUpdate);
    video.addEventListener('play', onPlay);
    video.addEventListener('pause', onPause);
    video.addEventListener('ended', onEnded);
    video.addEventListener('error', onError);
    syncVideoToSequence(clockRef.current, playingRef.current);
    return () => {
      video.removeEventListener('loadedmetadata', onLoadedMetadata);
      video.removeEventListener('timeupdate', onTimeUpdate);
      video.removeEventListener('seeked', onTimeUpdate);
      video.removeEventListener('play', onPlay);
      video.removeEventListener('pause', onPause);
      video.removeEventListener('ended', onEnded);
      video.removeEventListener('error', onError);
    };
  }, [mediaKey, enabled, stopClock, syncVideoToSequence, videoRef]);

  useEffect(() => {
    syncVideoToSequence(clockRef.current, playingRef.current);
  }, [items, transitions, fps, enabled, syncVideoToSequence]);

  // 卸载时停掉时钟，避免 rAF 泄漏。
  useEffect(() => () => stopClock(), [stopClock]);

  return {
    sequenceTime,
    sourceTime,
    isPlaying,
    videoDuration,
    totalDuration: totalSequence(),
    error,
    playPause,
    seekSequence,
    seekSource,
  };
}
