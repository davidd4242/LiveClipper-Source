import { useEffect, useRef, useState } from 'react';
import Mpegts from 'mpegts.js';
import { Expand, LoaderCircle, MonitorPlay, Radio, RefreshCw, Volume2, VolumeX } from 'lucide-react';
import type { LiveAvailability } from '../../api/types';

export interface LivePreviewRoom {
  roomId: number;
  title: string;
  streamer: string;
  availability: LiveAvailability | null;
  running: boolean;
}

interface LivePreviewProps {
  room: LivePreviewRoom | null;
  onRefresh: () => void;
  /** 受控静音；不传时组件自己管理（直播页保持原有行为）。监控屏用它实现“声音跟随选中房间”。 */
  muted?: boolean;
  onMutedChange?: (muted: boolean) => void;
}

function statusLabel(room: LivePreviewRoom | null): string {
  if (!room) return '未选择房间';
  if (room.availability === 'Live') return '直播中';
  if (room.availability === 'Offline') return '未开播';
  return room.running ? '探测中' : '未监控';
}

function statusClass(room: LivePreviewRoom | null): string {
  if (!room) return 'live-badge live-badge--idle';
  if (room.availability === 'Live') return 'live-badge live-badge--live';
  if (room.availability === 'Offline') return 'live-badge live-badge--offline';
  return 'live-badge live-badge--probing';
}

export function LivePreview({ room, onRefresh, muted, onMutedChange }: LivePreviewProps) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const playerRef = useRef<ReturnType<typeof Mpegts.createPlayer> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [internalMuted, setInternalMuted] = useState(true);
  const [attempt, setAttempt] = useState(0);
  const mutedValue = muted ?? internalMuted;

  const roomId = room?.roomId ?? null;
  const offline = room?.availability === 'Offline';

  useEffect(() => {
    const video = videoRef.current;
    if (!video || roomId === null || offline) {
      setError(null);
      setLoading(false);
      return undefined;
    }
    if (!Mpegts.isSupported()) {
      setError('当前浏览器不支持 MSE，无法播放 FLV 直播预览。');
      setLoading(false);
      return undefined;
    }

    setError(null);
    setLoading(true);
    let cancelled = false;
    const player = Mpegts.createPlayer(
      {
        type: 'flv',
        isLive: true,
        url: `/api/live/rooms/${roomId}/preview.flv`,
      },
      {
        isLive: true,
        enableStashBuffer: false,
        stashInitialSize: 128,
        lazyLoad: false,
        liveBufferLatencyChasing: true,
      },
    );
    playerRef.current = player;
    player.attachMediaElement(video);
    player.on(Mpegts.Events.ERROR, (type, detail) => {
      if (cancelled) return;
      setLoading(false);
      setError(`直播预览连接失败（${type || 'network'} / ${detail || 'unknown'}）；可能房间当前未开播，或取流已被上游拒绝。`);
    });
    player.on(Mpegts.Events.MEDIA_INFO, () => {
      if (!cancelled) setLoading(false);
    });
    player.load();
    const playback = player.play();
    if (playback) {
      playback.catch((cause: unknown) => {
        if (cancelled) return;
        setLoading(false);
        setError(cause instanceof Error ? cause.message : '浏览器阻止了自动播放');
      });
    }

    return () => {
      cancelled = true;
      player.pause();
      player.unload();
      player.detachMediaElement();
      player.destroy();
      if (playerRef.current === player) playerRef.current = null;
    };
  }, [roomId, offline, attempt]);

  useEffect(() => {
    if (videoRef.current) videoRef.current.muted = mutedValue;
  }, [mutedValue, roomId]);

  const toggleMuted = () => {
    const next = !mutedValue;
    if (muted === undefined) setInternalMuted(next);
    onMutedChange?.(next);
  };

  const retry = () => {
    setAttempt((value) => value + 1);
    onRefresh();
  };

  const toggleFullscreen = () => {
    const stage = stageRef.current;
    if (!stage) return;
    if (document.fullscreenElement) {
      void document.exitFullscreen();
    } else {
      void stage.requestFullscreen().catch(() => setError('当前窗口不允许进入全屏。'));
    }
  };

  return (
    <section className="lw-panel lw-preview" aria-label="直播预览">
      <header className="live-preview__head lw-preview__head">
        <div>
          <h2>
            <MonitorPlay size={13} /> 直播预览
          </h2>
          <span>{room ? `点选下方房间卡片切换画面 · ROOM ${room.roomId}` : '点选下方房间卡片，预览画面会切换到这里'}</span>
        </div>
        <div className="lw-preview__status">
          {room ? <span className={statusClass(room)}>{statusLabel(room)}</span> : null}
          <span className="lw-preview__proxy">FLV 代理</span>
        </div>
      </header>

      <div className="live-preview__stage lw-preview__stage" ref={stageRef}>
        {room ? (
          <>
            <video
              ref={videoRef}
              className="live-preview__video"
              autoPlay
              muted={mutedValue}
              playsInline
              onPlaying={() => setLoading(false)}
              onWaiting={() => setLoading(true)}
            />
            <div className="live-preview__shade" aria-hidden="true" />
            <div className="live-preview__top">
              <span className="live-preview__kicker">LIVE PREVIEW</span>
              <strong>{room.title}</strong>
              <span>{room.streamer || `直播间 ${room.roomId}`}</span>
            </div>
            <div className="live-preview__bottom">
              <span className="live-preview__hint">
                {offline ? '房间未开播，开播后可在这里直接预览。' : 'FLV 实时预览 · 由本地后端代理真实直播流'}
              </span>
              <div className="live-preview__actions">
                <button type="button" aria-label={mutedValue ? '打开声音' : '静音'} onClick={toggleMuted}>
                  {mutedValue ? <VolumeX size={13} /> : <Volume2 size={13} />}
                </button>
                <button type="button" aria-label="全屏预览" onClick={toggleFullscreen}>
                  <Expand size={13} />
                </button>
              </div>
            </div>
          </>
        ) : (
          <div className="live-preview__empty">
            <Radio size={26} />
            <p>还没有选择房间</p>
            <span>在下方房间列表点选一个直播间，预览窗会自动连接并切换画面。</span>
          </div>
        )}

        {room && !offline && loading ? (
          <div className="live-preview__loading">
            <LoaderCircle size={18} className="live-spin" />
            <span>正在连接直播流…</span>
          </div>
        ) : null}

        {room && error ? (
          <div className="live-preview__error">
            <p>{error}</p>
            <div>
              <button type="button" onClick={retry}>
                <RefreshCw size={11} /> 重试
              </button>
              <button type="button" onClick={onRefresh}>
                刷新状态
              </button>
            </div>
          </div>
        ) : null}

        {room && offline && !error ? (
          <div className="live-preview__offline">
            <span className="live-preview__offline-dot" />
            <p>房间当前未开播</p>
            <span>直播开始后，画面会自动出现在这里；无需手动刷新页面。</span>
          </div>
        ) : null}
      </div>
    </section>
  );
}
