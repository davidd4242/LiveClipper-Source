import { useEffect, useState } from 'react';

interface VideoThumbnailProps {
  src: string;
  alt: string;
  className?: string;
  seekRatio?: number;
}

const cache = new Map<string, string>();
const queue: Array<() => void> = [];
let active = 0;
const MAX_ACTIVE = 3;

function schedule(task: () => Promise<void>) {
  const run = () => {
    active += 1;
    void task().finally(() => {
      active -= 1;
      const next = queue.shift();
      if (next) next();
    });
  };

  if (active < MAX_ACTIVE) run();
  else queue.push(run);
}

function captureFrame(src: string, ratio: number): Promise<string> {
  return new Promise((resolve, reject) => {
    const video = document.createElement('video');
    video.muted = true;
    video.playsInline = true;
    video.preload = 'metadata';
    video.crossOrigin = 'anonymous';
    video.style.cssText = 'position:fixed;left:-9999px;top:0;width:1px;height:1px;opacity:0;pointer-events:none';
    document.body.appendChild(video);

    let settled = false;
    const timeout = window.setTimeout(() => finish(new Error('thumbnail timeout')), 12_000);

    const finish = (error?: Error, data?: string) => {
      if (settled) return;
      settled = true;
      window.clearTimeout(timeout);
      video.pause();
      video.removeAttribute('src');
      video.load();
      video.remove();
      if (error) reject(error);
      else resolve(data ?? '');
    };

    const draw = () => {
      try {
        const sourceWidth = video.videoWidth || 320;
        const sourceHeight = video.videoHeight || 180;
        const width = Math.min(420, sourceWidth);
        const height = Math.max(1, Math.round((sourceHeight / sourceWidth) * width));
        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        const context = canvas.getContext('2d');
        if (!context) {
          finish(new Error('canvas unavailable'));
          return;
        }
        context.drawImage(video, 0, 0, width, height);
        finish(undefined, canvas.toDataURL('image/jpeg', 0.7));
      } catch (error) {
        finish(error instanceof Error ? error : new Error('thumbnail failed'));
      }
    };

    video.onloadedmetadata = () => {
      const duration = Number.isFinite(video.duration) ? video.duration : 0;
      if (duration <= 0) return;
      const target = Math.min(Math.max(0.1, duration * ratio), Math.max(0.1, duration - 0.1));
      try {
        video.currentTime = target;
      } catch {
        draw();
      }
    };
    video.onseeked = draw;
    video.onerror = () => finish(new Error('video load failed'));
    video.src = src;
    video.load();
  });
}

export function VideoThumbnail({ src, alt, className, seekRatio = 0.18 }: VideoThumbnailProps) {
  const key = `${src}#${seekRatio}`;
  const [thumbnail, setThumbnail] = useState(() => cache.get(key) ?? '');

  useEffect(() => {
    if (cache.has(key)) {
      setThumbnail(cache.get(key) ?? '');
      return;
    }

    let cancelled = false;
    setThumbnail('');
    schedule(async () => {
      if (cancelled) return;
      try {
        const data = await captureFrame(src, seekRatio);
        cache.set(key, data);
        if (!cancelled) setThumbnail(data);
      } catch {
        // Keep the gradient fallback; a failed probe should not break the list.
      }
    });

    return () => {
      cancelled = true;
    };
  }, [key, seekRatio, src]);

  if (!thumbnail) return null;
  return <img className={`video-thumbnail ${className ?? ''}`.trim()} src={thumbnail} alt={alt} draggable={false} />;
}
