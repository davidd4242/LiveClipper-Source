import {RoomHeatDetails} from './RoomHeatDetails';
import {roomHeatDisplay} from './roomHeatDisplay';
import {autoClipAvailability} from './autoClipAvailability';
import {danmakuSnapshotBadge,noRecordingSessionMessage,type DanmakuStatus} from './monitorDanmakuStatus';
import {RecordingDirectory,RoomRecordingControl,monitorStartedMessage} from './RecordingControls';
import {manualHighlightAvailability,manualHighlightReceipt} from './manualHighlight';
import {markLegacyHighlight} from './liveApi';
import {RoomEntry} from './RoomEntry';
import {startLegacyMonitor} from './liveApi';
/**
 * A4 独立监控屏（/live/monitor）：
 * - 左侧房间列表来自 GET /api/status.rooms（旧录制管线多路监控），支持键盘 1-9 / ↑↓ / [ ] 切换；
 * - 点选房间切换 FLV 预览（/api/live/rooms/{id}/preview.flv）与对应弹幕窗口；
 * - 弹幕走轻量 SSE（GET /api/live/danmaku/stream?roomId=），只读现有弹幕缓冲；
 * - 声音只跟随选中房间：同一时刻只挂一个预览播放器，切换时旧画面立即卸载；
 * - 不做宫格；旁边展示该房热度条与切片状态。
 * - 收尾：48px 轻工具栏、SSE 状态徽标、主题切换、全屏、快捷键浮层、房间列表折叠。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Activity,
  AlertTriangle,
  ArrowLeft,
  ChevronLeft,
  ChevronRight,
  ClipboardList,
  Keyboard,
  LoaderCircle,
  Maximize2,
  MessagesSquare,
  Minimize2,
  Radio,
  RefreshCw,
  Scissors,
  Volume2,
  VolumeX,
  X,
} from 'lucide-react';
import {
  Badge,
  Button,
  Dialog,
  EmptyState,
  IconButton,
  ToastStack,
  useToastQueue,
} from '../../design';
import { navigateApp, replaceAppLocation } from '../../app/appNavigation';
import { LivePreview, type LivePreviewRoom } from './LivePreview';
import {
  getAutoClip,
  getLegacyHighlights,
  getLegacyStatus,
  getLegacyDanmaku,
  type AutoClipItem,
  type AutoClipPayload,
  type DanmakuStreamEvent,
  type DanmakuStreamHello,
  type LegacyHighlightEvent,
  type LegacyRoomStatus,
  type LegacyStatus,
} from './liveApi';
import { formatDuration, formatNumber, formatTime } from './format';
import './monitor.css';
import '../../styles/monitor.css';

const ROOM_POLL_MS = 2000;
const SLICE_POLL_MS = 5000;
const MAX_DANMAKU = 200;
const ROOM_NOTICE_MS = 8000;

type LoadState = 'loading' | 'ready' | 'error';

function errorMessage(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}

function readRoomFromUrl(): number | null {
  const raw = new URLSearchParams(window.location.search).get('room');
  if (!raw) return null;
  const value = Number(raw);
  return Number.isInteger(value) && value > 0 ? value : null;
}

function writeRoomToUrl(roomId: number): void {
  const url = new URL(window.location.href);
  url.searchParams.set('room', String(roomId));
  replaceAppLocation(url);
}

function previewRoomOf(room: LegacyRoomStatus): LivePreviewRoom {
  return {
    roomId: room.roomId,
    title: room.title || `直播间 ${room.roomId}`,
    streamer: room.streamer || '',
    availability: room.isLive ? 'Live' : 'Offline',
    running: room.monitoring,
  };
}

function recordingLabel(room: LegacyRoomStatus): string {
  if (!room.monitoring) return room.isLive ? '直播中 · 未监控' : '未监控';
  if(room.captureMode==='monitor-only')return '纯预览 · 不写媒体文件';
  if(room.captureMode==='highlight-cache')return room.cacheActive?'高光临时缓存中':'等待高光缓存';
  switch (room.state) {
    case 'Recording':
      return '监控中 · 录制中';
    case 'Connecting':
      return '监控中 · 连接中';
    case 'Reconnecting':
      return '监控中 · 重连中';
    case 'Finalizing':
      return '监控中 · 收尾中';
    default:
      return '监控中';
  }
}

function danmakuKindLabel(kind: string): string {
  switch (kind) {
    case 'danmaku':
      return '弹幕';
    case 'gift':
      return '礼物';
    case 'superchat':
    case 'super_chat':
      return 'SC';
    case 'guard':
      return '舰长';
    case 'enter':
      return '进入';
    default:
      return kind;
  }
}

function highlightStateLabel(state: string): string {
  switch (state) {
    case 'Collecting':
      return '收集中';
    case 'Materializing':
      return '生成中';
    case 'Ready':
      return '已就绪';
    case 'Failed':
      return '失败';
    default:
      return state || '—';
  }
}

function latestOf<T>(items: T[], pick: (item: T) => number): T | null {
  let best: T | null = null;
  let bestValue = Number.NEGATIVE_INFINITY;
  for (const item of items) {
    const value = pick(item);
    if (Number.isFinite(value) && value > bestValue) {
      bestValue = value;
      best = item;
    }
  }
  return best;
}



function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  const tag = target.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable;
}

export function MonitorScreen() {
  const [roomInput,setRoomInput]=useState('');
  const [roomStartBusy,setRoomStartBusy]=useState(false);
  const roomStartLock=useRef(false);
  const [roomStartError,setRoomStartError]=useState('');
  const [markBusy,setMarkBusy]=useState(false);
  const markLock=useRef(false);
  const [markNotice,setMarkNotice]=useState<{roomId:number;text:string;error:boolean}|null>(null);
  const [status, setStatus] = useState<LegacyStatus | null>(null);
  const [statusState, setStatusState] = useState<LoadState>('loading');
  const [statusError, setStatusError] = useState<string | null>(null);
  const [updatedAt, setUpdatedAt] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const [autoClip, setAutoClip] = useState<AutoClipPayload | null>(null);
  const [highlights, setHighlights] = useState<LegacyHighlightEvent[]>([]);
  const [sliceError, setSliceError] = useState<string | null>(null);

  const [selectedRoomId, setSelectedRoomId] = useState<number | null>(() => readRoomFromUrl());
  const [soundOn, setSoundOn] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [roomsCollapsed, setRoomsCollapsed] = useState(false);
  const [roomNotice, setRoomNotice] = useState<string | null>(null);

  const [danmaku, setDanmaku] = useState<DanmakuStreamEvent[]>([]);
  const [danmakuHello, setDanmakuHello] = useState<DanmakuStreamHello | null>(null);
  const [danmakuStatus, setDanmakuStatus] = useState<DanmakuStatus>('idle');

  // ToastStack：监控屏的连续失败（状态轮询 / 切片读取 / 弹幕流断开）共用一条 3 条堆叠队列。
  const { items: toastItems, push: pushToast, dismiss: dismissToast } = useToastQueue(3);
  const pushToastRef = useRef(pushToast);
  const toastDedupeRef = useRef<Record<string, string>>({});

  const mountedRef = useRef(false);
  const listRef = useRef<HTMLDivElement | null>(null);
  const stickRef = useRef(true);
  const sessionRef = useRef<string | null>(null);
  const previousRoomRef = useRef<number | null>(selectedRoomId);
  const noticeTimerRef = useRef<number | null>(null);

  useEffect(() => {
    pushToastRef.current = pushToast;
  }, [pushToast]);

  /** 同一 key 的相同错误只提示一次，恢复后重新允许提示，避免轮询刷屏。 */
  const notifyFailure = useCallback((key: string, message: string, description?: string) => {
    if (toastDedupeRef.current[key] === message) return;
    toastDedupeRef.current[key] = message;
    pushToastRef.current({ message, description, tone: 'error', duration: 6000 });
  }, []);

  const clearFailure = useCallback((key: string) => {
    delete toastDedupeRef.current[key];
  }, []);

  const loadStatus = useCallback(async () => {
    try {
      const payload = await getLegacyStatus();
      if (!mountedRef.current) return true;
      setStatus(payload);
      setStatusState('ready');
      setStatusError(null);
      setUpdatedAt(new Date().toISOString());
      clearFailure('status');
      return true;
    } catch (cause) {
      const message = errorMessage(cause);
      if (!mountedRef.current) return false;
      setStatusState('error');
      setStatusError(message);
      notifyFailure('status', '后端状态读取失败', `${message}。请确认 LiveClipper 后端在运行。`);
      return false;
    }
  }, [clearFailure, notifyFailure]);

  const loadSlices = useCallback(async () => {
    try {
      const [auto, list] = await Promise.all([getAutoClip(), getLegacyHighlights()]);
      if (!mountedRef.current) return true;
      setAutoClip(auto);
      setHighlights(Array.isArray(list) ? list : []);
      setSliceError(null);
      clearFailure('slices');
      return true;
    } catch (cause) {
      const message = errorMessage(cause);
      if (!mountedRef.current) return false;
      setSliceError(message);
      notifyFailure('slices', '切片状态读取失败', message);
      return false;
    }
  }, [clearFailure, notifyFailure]);

  const refreshAll = useCallback(
    async (manual = false) => {
      setRefreshing(true);
      const [statusOk, slicesOk] = await Promise.all([loadStatus(), loadSlices()]);
      if (mountedRef.current) setRefreshing(false);
      if (!manual) return;
      const failed = [statusOk, slicesOk].filter((ok) => !ok).length;
      pushToastRef.current(
        failed === 0
          ? { message: '状态已刷新', description: '房间与切片数据已更新。', tone: 'success', duration: 3000 }
          : {
              message: '刷新未完成',
              description: `${failed} 项数据读取失败，已保留上一次结果。`,
              tone: 'error',
              duration: 6000,
            },
      );
    },
    [loadStatus, loadSlices],
  );

  useEffect(() => {
    mountedRef.current = true;
    void refreshAll();
    const statusTimer = window.setInterval(() => void loadStatus(), ROOM_POLL_MS);
    const sliceTimer = window.setInterval(() => void loadSlices(), SLICE_POLL_MS);
    return () => {
      mountedRef.current = false;
      window.clearInterval(statusTimer);
      window.clearInterval(sliceTimer);
    };
  }, [refreshAll, loadStatus, loadSlices]);

  // 全屏状态跟随浏览器 fullscreenchange（Esc 由浏览器先处理退出全屏）。
  useEffect(() => {
    const sync = () => setFullscreen(Boolean(document.fullscreenElement));
    sync();
    document.addEventListener('fullscreenchange', sync);
    return () => document.removeEventListener('fullscreenchange', sync);
  }, []);

  const rooms = useMemo<LegacyRoomStatus[]>(
    () => (Array.isArray(status?.rooms) ? status.rooms : []),
    [status],
  );

  const monitoringCount = useMemo(
    () => rooms.filter((room) => room.monitoring).length,
    [rooms],
  );

  // 首屏/URL 里的房间不在列表时，自动落到第一个正在监控（优先直播中）的房间。
  useEffect(() => {
    if (rooms.length === 0) return;
    if (selectedRoomId != null && rooms.some((room) => room.roomId === selectedRoomId)) return;
    const fallback =
      rooms.find((room) => room.monitoring && room.isLive) ??
      rooms.find((room) => room.monitoring) ??
      rooms.find((room) => room.isLive) ??
      rooms[0];
    setSelectedRoomId(fallback.roomId);
  }, [rooms, selectedRoomId]);

  // 选中房间消失时给出提示（自动落到第一个由上面的 effect 完成）。
  useEffect(() => {
    const previous = previousRoomRef.current;
    previousRoomRef.current = selectedRoomId;
    if (previous == null || previous === selectedRoomId) return;
    if (rooms.some((room) => room.roomId === previous)) return;
    const fallback = rooms.find((room) => room.roomId === selectedRoomId);
    setRoomNotice(
      fallback
        ? `房间 ${previous} 已从监控列表移除，已自动切换到「${fallback.title || `直播间 ${fallback.roomId}`}」。`
        : `房间 ${previous} 已从监控列表移除。`,
    );
  }, [rooms, selectedRoomId]);

  useEffect(() => {
    if (!roomNotice) return undefined;
    if (noticeTimerRef.current != null) window.clearTimeout(noticeTimerRef.current);
    noticeTimerRef.current = window.setTimeout(() => setRoomNotice(null), ROOM_NOTICE_MS);
    return () => {
      if (noticeTimerRef.current != null) window.clearTimeout(noticeTimerRef.current);
    };
  }, [roomNotice]);

  const selectRoom = useCallback((roomId: number) => {
    setSelectedRoomId(roomId);
    writeRoomToUrl(roomId);
  }, []);

  const selectedRoom = useMemo(
    () => rooms.find((room) => room.roomId === selectedRoomId) ?? null,
    [rooms, selectedRoomId],
  );

  // 状态轮询会不断替换 rooms 数组；弹幕轮询只跟随房间/会话，不跟随数组身份，
  // 否则每次状态刷新都会清空已收到的弹幕。
  const selectedRoomRef = useRef<LegacyRoomStatus | null>(null);
  useEffect(() => {
    selectedRoomRef.current = selectedRoom;
  }, [selectedRoom]);

  const cycleRoom = useCallback(
    (delta: number) => {
      if (rooms.length === 0) return;
      const index = rooms.findIndex((room) => room.roomId === selectedRoomId);
      const nextIndex = index < 0 ? 0 : (index + delta + rooms.length) % rooms.length;
      selectRoom(rooms[nextIndex].roomId);
    },
    [rooms, selectedRoomId, selectRoom],
  );

  const toggleFullscreen = useCallback(() => {
    if (document.fullscreenElement) {
      void document.exitFullscreen().catch(() => {});
      return;
    }
    void document.documentElement.requestFullscreen().catch(() => {});
  }, []);

  // 键盘操作：1..9 / ↑↓ / [ ] 切房间，M 静音，Esc 返回，? 快捷键。
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return;
      if (isEditableTarget(event.target)) return;

      switch (event.key) {
        case '?': {
          event.preventDefault();
          setHelpOpen((open) => !open);
          return;
        }
        case 'Escape': {
          if (helpOpen) {
            setHelpOpen(false);
            event.preventDefault();
          } else {
            void navigateApp('/live');
          }
          return;
        }
        case 'm':
        case 'M': {
          if (!selectedRoom) return;
          event.preventDefault();
          setSoundOn((value) => !value);
          return;
        }
        case 'ArrowUp':
        case '[': {
          event.preventDefault();
          cycleRoom(-1);
          return;
        }
        case 'ArrowDown':
        case ']': {
          event.preventDefault();
          cycleRoom(1);
          return;
        }
        default: {
          if (!/^[1-9]$/.test(event.key)) return;
          const target = rooms[Number(event.key) - 1];
          if (!target) return;
          event.preventDefault();
          selectRoom(target.roomId);
        }
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [cycleRoom, helpOpen, rooms, selectRoom, selectedRoom]);

  // 弹幕：WebView2 的单窗口宿主里，WebResourceRequested 自定义响应会缓冲到 EOF，
  // 无限 SSE 不能增量交付；这里改为 1s 只读快照轮询，按 id 去重。
  useEffect(() => {
    if (selectedRoomId == null) {
      setDanmaku([]);
      setDanmakuHello(null);
      setDanmakuStatus('idle');
      sessionRef.current = null;
      return undefined;
    }

    sessionRef.current = null;
    stickRef.current = true;
    setDanmaku([]);
    setDanmakuHello(null);
    setDanmakuStatus('connecting');

    let cancelled = false;
    let failed = false;

    let inFlight = false;
    const poll = async () => {
      if (inFlight) return;
      inFlight = true;
      try {
      const room = selectedRoomRef.current;
      const sessionId = room?.sessionId ?? null;
      const hello: DanmakuStreamHello = {
        roomId: selectedRoomId,
        sessionId,
        monitoring: room?.monitoring ?? false,
        title: room?.title ?? '',
        streamer: room?.streamer ?? '',
      };
      if (cancelled) return;
      setDanmakuHello(hello);
      if (!sessionId) {setDanmakuStatus('waiting');return;}
      if (sessionId !== sessionRef.current) {
        sessionRef.current = sessionId;
        setDanmaku([]);
      }
      try {
        const items = (await getLegacyDanmaku(sessionId))
          .slice()
          .sort((a, b) => a.timestamp.localeCompare(b.timestamp));
        if (cancelled) return;
        clearFailure('sse');
        failed = false;
        setDanmakuStatus('streaming');
        setDanmaku((current) => {
          const seen = new Set(current.map((item) => item.id));
          const next = [...current];
          for (const item of items) {
            if (seen.has(item.id)) continue;
            seen.add(item.id);
            next.push(item);
          }
          return next.slice(-MAX_DANMAKU);
        });
      } catch {
        if (cancelled || failed) return;
        failed = true;
        setDanmakuStatus('error');
        notifyFailure('sse', '弹幕快照读取失败', '监控屏会继续轮询，已收到的消息保留。');
      }
      } finally {
        inFlight = false;
      }
    };

    void poll();
    const timer = window.setInterval(() => void poll(), 1000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [clearFailure, notifyFailure, selectedRoom?.sessionId, selectedRoomId]);

  // 新弹幕到达时，只有原本贴着底部才自动滚动，避免打断向上翻看。
  useEffect(() => {
    const element = listRef.current;
    if (element && stickRef.current) element.scrollTop = element.scrollHeight;
  }, [danmaku]);

  const handleListScroll = () => {
    const element = listRef.current;
    if (!element) return;
    stickRef.current = element.scrollHeight - element.scrollTop - element.clientHeight < 48;
  };

  const highlightsBySession = useMemo(() => {
    const map = new Map<string, LegacyHighlightEvent[]>();
    for (const item of highlights) {
      const list = map.get(item.sessionId);
      if (list) list.push(item);
      else map.set(item.sessionId, [item]);
    }
    return map;
  }, [highlights]);

  const clipSessionByEvent = useMemo(() => {
    const map = new Map<string, string>();
    for (const item of highlights) map.set(item.id, item.sessionId);
    return map;
  }, [highlights]);

  const selectedSessionHighlights = selectedRoom?.sessionId
    ? highlightsBySession.get(selectedRoom.sessionId) ?? []
    : [];
  const selectedSessionClips = useMemo(() => {
    if (!selectedRoom?.sessionId || !autoClip) return [] as AutoClipItem[];
    return autoClip.clips.filter(
      (clip) => clipSessionByEvent.get(clip.eventId) === selectedRoom.sessionId,
    );
  }, [autoClip, clipSessionByEvent, selectedRoom?.sessionId]);

  const readySlices = selectedSessionHighlights.filter((item) => item.state === 'Ready').length;
  const latestHighlight = latestOf(selectedSessionHighlights, (item) => item.triggerTime);
  const latestClip = latestOf(selectedSessionClips, (item) =>
    item.createdAt ? new Date(item.createdAt).getTime() : Number.NEGATIVE_INFINITY,
  );
  const triggerRatio = status?.autoHighlightBaselineRatio ?? 1.4;
  const triggerLabel = `${(triggerRatio * 100).toFixed(0)}%`;

  const sseBadge = danmakuSnapshotBadge(selectedRoom ? danmakuStatus : 'idle');

  const startEnteredRoom=async(roomId:number,autoRecord=false,cacheEnabled=true)=>{
    if(roomStartLock.current)return;
    roomStartLock.current=true;setRoomStartBusy(true);setRoomStartError('');
    try{await startLegacyMonitor(roomId,autoRecord,cacheEnabled);if(!mountedRef.current)return;await loadStatus();if(!mountedRef.current)return;selectRoom(roomId);setRoomInput('');setRoomNotice(monitorStartedMessage(roomId,autoRecord,cacheEnabled));}
    catch(e){if(mountedRef.current)setRoomStartError(errorMessage(e));}
    finally{roomStartLock.current=false;if(mountedRef.current)setRoomStartBusy(false);}
  };
  const manualAvailability=manualHighlightAvailability(selectedRoom,status);
  const autoAvailability=autoClipAvailability(autoClip?.enabled??null,status?(selectedRoom?[selectedRoom]:[]):null);
  const markSelectedRoom=async()=>{
    if(markLock.current||!selectedRoom||!manualAvailability.enabled)return;
    const roomId=selectedRoom.roomId;markLock.current=true;setMarkBusy(true);setMarkNotice(null);
    try{const event=await markLegacyHighlight(roomId);if(!mountedRef.current)return;setMarkNotice({roomId,text:manualHighlightReceipt(event),error:false});await loadSlices();}
    catch(e){if(mountedRef.current)setMarkNotice({roomId,text:errorMessage(e),error:true});}
    finally{markLock.current=false;if(mountedRef.current)setMarkBusy(false);}
  };
  const roomListBody = (() => {
    if (statusState === 'loading' && rooms.length === 0) {
      return (
        <EmptyState
          density="compact"
          icon={<LoaderCircle size={18} className="live-spin" />}
          title="正在读取房间…"
        />
      );
    }
    if (rooms.length === 0) {
      return (
        <EmptyState
          density="compact"
          icon={<Radio size={18} />}
          title="等待后台开启直播间监控"
          description={
            <>
              去 <a href="/live">直播页</a> 启动房间监控后，这里会出现可切换的独立画面。
            </>
          }
          action={
            <Button
              variant="outline"
              size="sm"
              leading={<RefreshCw size={12} />}
              onClick={() => void refreshAll(true)}
            >
              刷新状态
            </Button>
          }
        />
      );
    }
    return (
      <div className="lw-monitor-room-list" role="region" aria-label="可切换监控房间列表" tabIndex={0}>
        {rooms.map((room, index) => {
          const roomHighlights = room.sessionId
            ? highlightsBySession.get(room.sessionId) ?? []
            : [];
          const liveBadge = room.isLive ? 'live-badge live-badge--live' : 'live-badge live-badge--offline';
          const isSelected = room.roomId === selectedRoomId;
          return (
            <button
              key={room.roomId}
              type="button"
              className={`lw-monitor-room${isSelected ? ' is-selected' : ''}`}
              onClick={() => selectRoom(room.roomId)}
              aria-pressed={isSelected}
            >
              <span className="lw-monitor-room__top">
                <span className="lw-monitor-room__top-left">
                  {index < 9 ? <span className="lw-monitor-room__key">{index + 1}</span> : null}
                  <span className="lw-monitor-room__id">ROOM {room.roomId}</span>
                </span>
                <span className={liveBadge}>{room.isLive ? '直播中' : '未开播'}</span>
              </span>
              <strong className="lw-monitor-room__title">{room.title || `直播间 ${room.roomId}`}</strong>
              <span className="lw-monitor-room__streamer">{room.streamer || '未知主播'}</span>
              <span className="lw-monitor-room__heat-row">
                <span className={`lw-bar lw-monitor-room__bar ${roomHeatDisplay(room).tone}`}>
                  <i style={{ width: `${roomHeatDisplay(room).width}%` }} />
                </span>
                <small className="lw-monitor-room__heat-label" title={`当前基准=100%，140%显示满格；${roomHeatDisplay(room).label}。达到显示线不代表已触发自动高光。`}>弹幕热度</small>
              </span>
              <span className="lw-monitor-room__meta">
                <span>{recordingLabel(room)}</span>
                <span className="lw-mono">{formatNumber(room.danmakuPackets)} 弹幕</span>
                {roomHighlights.length > 0 ? (
                  <span className="lw-monitor-room__slice">{roomHighlights.length} 切片</span>
                ) : null}
              </span>
            </button>
          );
        })}
      </div>
    );
  })();

  return (
    <div className="lc-monitor-page">
      <a className="monitor-skip-link" href="#monitor-main">
        跳到主内容
      </a>
      <h1 className="lc-monitor-sr-only">直播监控屏</h1>
      <header className="lc-monitor-toolbar">
        <a
          className="lc-monitor-toolbar__back"
          href="/live"
          aria-label="返回直播页（Esc）"
          title="返回直播页（Esc）"
        >
          <ArrowLeft size={15} />
        </a>
        <div className="lc-monitor-toolbar__room">
          <strong>
            {selectedRoom ? selectedRoom.title || `直播间 ${selectedRoom.roomId}` : '独立监控屏'}
          </strong>
          <span>
            {selectedRoom
              ? `ROOM ${selectedRoom.roomId} · ${recordingLabel(selectedRoom)} · 声音${soundOn ? '开' : '静音'}`
              : '等待后台开启直播间监控'}
          </span>
        </div>
        <div className="lc-monitor-toolbar__meta">
          {selectedRoom ? (
            <Badge tone={selectedRoom.isLive ? 'red' : 'idle'} dot>
              {selectedRoom.isLive ? '直播中' : '未开播'}
            </Badge>
          ) : null}
          <span className="lc-monitor-toolbar__time">
            {updatedAt ? `更新 ${formatTime(updatedAt)}` : '更新 —'}
          </span>
          <Badge tone={sseBadge.tone} dot title={`弹幕快照（轮询） ${sseBadge.label}`}>
            {sseBadge.label}
          </Badge>
        </div>
        <div className="lc-monitor-toolbar__actions">
          <IconButton
            label={soundOn ? '静音（M）' : '开启声音（M）'}
            variant="ghost"
            size="sm"
            active={soundOn}
            disabled={!selectedRoom}
            onClick={() => setSoundOn((value) => !value)}
          >
            {soundOn ? <Volume2 size={15} /> : <VolumeX size={15} />}
          </IconButton>
          <IconButton
            label="刷新状态"
            variant="ghost"
            size="sm"
            disabled={refreshing}
            onClick={() => void refreshAll(true)}
          >
            {refreshing ? <LoaderCircle size={15} className="live-spin" /> : <RefreshCw size={15} />}
          </IconButton>
          <IconButton
            label={fullscreen ? '退出全屏' : '全屏'}
            variant="ghost"
            size="sm"
            onClick={toggleFullscreen}
          >
            {fullscreen ? <Minimize2 size={15} /> : <Maximize2 size={15} />}
          </IconButton>
          <IconButton
            label="键盘快捷键（?）"
            variant="ghost"
            size="sm"
            active={helpOpen}
            onClick={() => setHelpOpen((value) => !value)}
          >
            <Keyboard size={15} />
          </IconButton>
        </div>
      </header>

      <main className="lc-monitor-main" id="monitor-main" tabIndex={-1}>
        {roomNotice ? (
          <div className="lc-monitor-banner" role="status">
            <AlertTriangle size={13} />
            <span className="lc-monitor-banner__text">{roomNotice}</span>
            <span className="lc-monitor-banner__action">
              <IconButton label="关闭提示" variant="ghost" size="sm" onClick={() => setRoomNotice(null)}>
                <X size={13} />
              </IconButton>
            </span>
          </div>
        ) : null}

        {statusState === 'error' && statusError ? (
          <div className="lc-monitor-banner lc-monitor-banner--error" role="alert">
            <AlertTriangle size={13} />
            <span className="lc-monitor-banner__text">
              后端不可达：{statusError}。请确认 LiveClipper 后端在运行。
            </span>
            <span className="lc-monitor-banner__action">
              <Button variant="outline" size="sm" onClick={() => void refreshAll(true)}>
                重试
              </Button>
            </span>
          </div>
        ) : null}

        {sliceError ? (
          <div className="lc-monitor-banner lc-monitor-banner--warn" role="status">
            <AlertTriangle size={13} />
            <span className="lc-monitor-banner__text">切片状态读取失败：{sliceError}</span>
            <span className="lc-monitor-banner__action">
              <Button variant="ghost" size="sm" onClick={() => void loadSlices()}>
                重试
              </Button>
            </span>
          </div>
        ) : null}

        <div className={`lw-monitor-grid${roomsCollapsed ? ' is-rooms-collapsed' : ''}`}>
          <nav className="lw-panel lw-monitor-rooms" aria-label="监控导航">
            <header className="lw-panel__head">
              <div>
                <h2>
                  <Radio size={13} /> 房间列表
                </h2>
                <span>
                  后台监控 {formatNumber(monitoringCount)} / {formatNumber(status?.maxRooms ?? 0)}
                </span>
              </div>
              <div className="lw-panel__head-actions">
                <IconButton
                  label={roomsCollapsed ? '展开房间列表' : '收起房间列表'}
                  variant="ghost"
                  size="sm"
                  onClick={() => setRoomsCollapsed((value) => !value)}
                >
                  {roomsCollapsed ? <ChevronRight size={14} /> : <ChevronLeft size={14} />}
                </IconButton>
              </div>
            </header>
            <div className="lw-panel__body lw-monitor-rooms__body">
              <RecordingDirectory/>
              <RoomEntry busy={roomStartBusy} input={roomInput} onInputChange={setRoomInput} onStart={(id,autoRecord,cacheEnabled)=>void startEnteredRoom(id,autoRecord,cacheEnabled)}/>
              {roomStartError&&<p role="alert" className="lw-room-error">{roomStartError}</p>}
              {!selectedRoom&&<p role="status" className="lw-manual-empty">手动高光录制暂不可用：先选择正在录制且已有缓存的房间。</p>}
              {roomListBody}
            </div>
          </nav>

          <section className="lw-monitor-center" aria-label="选中房间画面">
            {rooms.length === 0 ? (
              <div className="lc-monitor-empty">
                <span className="lc-monitor-empty__icon">
                  <Radio size={20} />
                </span>
                <strong>等待后台开启直播间监控</strong>
                <span>
                  在直播页启动房间监控后，这里会自动出现可切换的独立画面；若后台已开启，点「刷新状态」重新读取。
                </span>
                <Button
                  variant="solid"
                  size="sm"
                  leading={<RefreshCw size={13} />}
                  disabled={refreshing}
                  onClick={() => void refreshAll(true)}
                >
                  刷新状态
                </Button>
              </div>
            ) : (
              <>
                {selectedRoom?.isLive ? (
                  <LivePreview
                    key={selectedRoom.roomId}
                    room={previewRoomOf(selectedRoom)}
                    onRefresh={() => void loadStatus()}
                    muted={!soundOn}
                    onMutedChange={(next) => setSoundOn(!next)}
                  />
                ) : (
                  <div className="lw-panel lw-preview lc-monitor-preview-placeholder">
                    <header className="lw-panel__head">
                      <div>
                        <h2>
                          <Radio size={13} /> 直播预览
                        </h2>
                        <span>FLV · 代理</span>
                      </div>
                    </header>
                    <div className="lc-monitor-preview-placeholder__stage" role="status">
                      <Radio size={26} />
                      <strong>
                        {selectedRoom
                          ? `「${selectedRoom.title || `直播间 ${selectedRoom.roomId}`}」未开播`
                          : '请选择房间'}
                      </strong>
                      <span>
                        该房间当前没有可用的直播画面；主播开播（或开始后台监控）后，这里会自动切换到 FLV
                        预览。
                      </span>
                    </div>
                  </div>
                )}

                <div className="lw-panel lw-monitor-status" aria-label="热度与切片状态">
                  <header className="lw-panel__head">
                    <div>
                      <h2>
                        <Activity size={13} /> 热度与切片
                      </h2>
                      <span>
                        {selectedRoom
                          ? autoAvailability.ready ? `本轮热度到达 ${triggerLabel} 触发线后自动切片` : `${autoAvailability.label}，当前不会自动产生高光`
                          : '选择房间后显示该房热度与切片状态'}
                      </span>
                    </div>
                    <div className="lw-monitor-status__badges">
                      {selectedRoom&&<RoomRecordingControl compact key={selectedRoom.roomId} room={selectedRoom} onChanged={loadStatus}/>}
                      <button type="button" className="live-btn live-btn--primary" disabled={markBusy||!manualAvailability.enabled} title={manualAvailability.description} onClick={()=>void markSelectedRoom()}>
                        <Scissors size={11}/>{markBusy?'正在标记…':'手动高光录制'}
                      </button>
                      <span className={`lw-badge ${autoAvailability.ready ? 'lw-badge--ok' : 'lw-badge--idle'}`}>
                        {autoAvailability.label}
                      </span>
                      <span className="lw-badge lw-badge--idle">
                        冷却 {formatNumber(autoClip?.policy.cooldownSeconds ?? status?.autoHighlightCooldownSeconds ?? 60)}s
                      </span>
                    </div>
                  </header>
                  <div className="lw-panel__body lw-monitor-status__body">
                    <p className="lw-room__hint">{manualAvailability.description}</p>

                    {markNotice&&<p role={markNotice.error?'alert':'status'}>房间 {markNotice.roomId}：{markNotice.text}</p>}
                    {selectedRoom ? (
                      <>
                        <div className="lw-monitor-status__heat">
                          <div className="lw-monitor-status__heat-head">
                            <span>弹幕热度</span>
                            <strong className={roomHeatDisplay(selectedRoom).tone}>
                              {roomHeatDisplay(selectedRoom).label}
                            </strong>
                            <span className="lw-mono">
                              触发线 {triggerLabel} · 基准 {selectedRoom.baseline.toFixed(2)}/s · 当前{' '}
                              {selectedRoom.messageRate.toFixed(2)}/s
                            </span>
                          </div>
                          <div className={`lw-bar lw-monitor-status__bar ${roomHeatDisplay(selectedRoom).tone}`}>
                            <i style={{ width: `${roomHeatDisplay(selectedRoom).width}%` }} />
                          </div>
                          <RoomHeatDetails room={selectedRoom} comparisonSeconds={status?.autoHighlightComparisonSeconds??null}/>
                          <p className="lw-room__hint">基准 = 100%（绿色），140% 显示满格；达到显示线不代表自动高光已触发，实际状态如下。</p>
                          <p className="lw-monitor-status__reason">
                            {selectedRoom.heatReason || '等待弹幕热度数据…'}
                          </p>
                        </div>

                        <div className="lw-monitor-status__slices">
                          <div>
                            <span>本场切片</span>
                            <strong>
                              {formatNumber(selectedSessionHighlights.length)}
                              <small> / 已就绪 {formatNumber(readySlices)}</small>
                            </strong>
                          </div>
                          <div>
                            <span>自动切片记录</span>
                            <strong>{formatNumber(selectedSessionClips.length)}</strong>
                          </div>
                          <div>
                            <span>最近一条</span>
                            <strong>
                              {latestHighlight
                                ? `${highlightStateLabel(latestHighlight.state)}${latestHighlight.partial ? '（部分）' : ''} · ${formatDuration(latestHighlight.triggerTime)}`
                                : '—'}
                            </strong>
                          </div>
                          <div>
                            <span>会话</span>
                            <strong className="lw-mono">
                              {selectedRoom.sessionId ? selectedRoom.sessionId.slice(0, 8) : selectedRoom.monitoring ? '监控中 · 无录制会话' : '未监控 · 无录制会话'}
                            </strong>
                          </div>
                        </div>

                        {latestClip ? (
                          <p className="lw-monitor-status__clip lw-ellipsis" title={latestClip.path}>
                            <Scissors size={10} /> 最新切片 {formatTime(latestClip.createdAt)} ·{' '}
                            {latestClip.path}
                          </p>
                        ) : null}
                      </>
                    ) : (
                      <EmptyState
                        density="compact"
                        icon={<ClipboardList size={18} />}
                        title="从左侧房间列表选择一个直播间"
                      />
                    )}
                  </div>
                </div>
              </>
            )}
          </section>

          <aside className="lw-panel lw-monitor-danmaku" aria-label="弹幕窗口">
            <header className="lw-panel__head">
              <div>
                <h2>
                  <MessagesSquare size={13} /> 弹幕
                </h2>
                <span>
                  {selectedRoom
                    ? danmakuHello?.sessionId
                      ? `会话 ${danmakuHello.sessionId.slice(0, 8)} · 已收 ${formatNumber(danmaku.length)} 条`
                      : noRecordingSessionMessage(selectedRoom)
                    : '选择房间后显示对应弹幕窗口'}
                </span>
              </div>
            </header>
            <div
              className="lw-panel__body lw-monitor-danmaku__body"
              ref={listRef}
              onScroll={handleListScroll}
            >
              {selectedRoom && danmakuStatus === 'error' ? (
                <div className="lc-monitor-danmaku__reconnect" role="status">
                  <LoaderCircle size={12} className="live-spin" />
                  <span>弹幕快照读取失败，正在自动重试；已收到的消息保留。</span>
                </div>
              ) : selectedRoom && danmakuStatus === 'connecting' ? (
                <div className="lc-monitor-danmaku__reconnect" role="status">
                  <LoaderCircle size={12} className="live-spin" />
                  <span>正在读取该房间的弹幕快照…</span>
                </div>
              ) : null}

              {!selectedRoom ? (
                <EmptyState
                  density="compact"
                  icon={<MessagesSquare size={18} />}
                  title="点选房间后读取该房录制会话的弹幕快照"
                />
              ) : danmaku.length === 0 ? (
                <EmptyState
                  density="compact"
                  icon={
                    danmakuStatus === 'connecting' || danmakuStatus === 'error' ? (
                      <LoaderCircle size={18} className="live-spin" />
                    ) : (
                      <MessagesSquare size={18} />
                    )
                  }
                  title={
                    danmakuStatus === 'error'
                      ? '弹幕快照读取失败，正在自动重试…'
                      : danmakuStatus === 'connecting'
                        ? '正在读取弹幕快照…'
                        : danmakuStatus === 'waiting'
                          ? noRecordingSessionMessage(selectedRoom)
                          : '当前会话弹幕快照为空，等待新消息…'
                  }
                />
              ) : (
                <div
                  className="lw-danmaku__list"
                  role="region"
                  aria-label="实时弹幕列表"
                  tabIndex={0}
                >
                  {danmaku.map((item) => (
                    <div className="lw-danmaku__row" key={item.id}>
                      <span className="lw-danmaku__offset">{formatDuration(item.sessionTime)}</span>
                      {item.type && item.type !== 'danmaku' ? (
                        <span className="lw-danmaku__kind">{danmakuKindLabel(item.type)}</span>
                      ) : null}
                      <span className="lw-danmaku__user">{item.userName || '匿名'}</span>
                      <span className="lw-danmaku__text">{item.text}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </aside>
        </div>
      </main>

      <footer className="lc-monitor-footer">
        <button
          type="button"
          className="lc-monitor-hint"
          onClick={() => setHelpOpen(true)}
          title="查看键盘快捷键"
        >
          <kbd>?</kbd> 快捷键
        </button>
      </footer>

      <ToastStack items={toastItems} onDismiss={dismissToast} />

      <Dialog
        open={helpOpen}
        title={
          <>
            <Keyboard size={15} aria-hidden="true" /> 键盘快捷键
          </>
        }
        description="按 Esc 或点击遮罩关闭；没有浮层时 Esc 返回直播页。"
        size="md"
        onClose={() => setHelpOpen(false)}
        footer={
          <Button variant="outline" size="sm" onClick={() => setHelpOpen(false)}>
            关闭
          </Button>
        }
      >
            <dl className="lc-monitor-help__grid">
              <dt>
                <kbd>1</kbd>–<kbd>9</kbd>
              </dt>
              <dd>切换到左侧第 N 个房间</dd>
              <dt>
                <kbd>↑</kbd>
                <kbd>↓</kbd> / <kbd>[</kbd>
                <kbd>]</kbd>
              </dt>
              <dd>上一个 / 下一个房间</dd>
              <dt>
                <kbd>M</kbd>
              </dt>
              <dd>静音 / 取消静音（只作用于选中房间）</dd>
              <dt>
                <kbd>Esc</kbd>
              </dt>
              <dd>关闭本浮层；没有浮层时返回直播页</dd>
              <dt>
                <kbd>?</kbd>
              </dt>
              <dd>打开 / 关闭本快捷键浮层</dd>
            </dl>
            <p className="lc-monitor-help__note">
              声音只跟随选中房间；切换房间时上一个画面会立即静音并卸载，不会在后台继续播放。全屏由工具栏按钮进入，Esc
              会先退出全屏。
            </p>
      </Dialog>
    </div>
  );
}
