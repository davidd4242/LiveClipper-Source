import {monitorStartedMessage} from './RecordingControls';
import {manualHighlightAvailability,manualHighlightReceipt} from './manualHighlight';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Activity,
  AlertTriangle,
  Info,
  LoaderCircle,
  MonitorPlay,
  RefreshCw,
  Settings,
  Wifi,
} from 'lucide-react';
import type { LiveStorageStatus } from '../../api/types';
import { AppShell } from '../../app/AppShell';
import { navigateApp, replaceAppLocation } from '../../app/appNavigation';
import { Button, Dialog } from '../../design';
import { AutoClipPanel } from './AutoClipPanel';
import { DiagnosticsPanel } from './DiagnosticsPanel';
import { LivePreview, type LivePreviewRoom } from './LivePreview';
import { RoomPanel } from './RoomPanel';
import { SessionPanel } from './SessionPanel';
import {
  cleanupLiveStorage,
  getAutoClip,
  getLegacyStatus,
  getLiveDiagnostics,
  getLiveRooms,
  getLiveSessions,
  getLiveSessionDetail,
  getLiveSessionDanmaku,
  getLiveStorage,
  markLegacyHighlight,
  removeLiveRoom,
  saveAutoClipPolicy,
  startLegacyMonitor,
  stopLegacyMonitor,
  type AutoClipPayload,
  type LegacyStatus,
  type LiveDanmakuPayload,
  type LiveDiagnosticsReport,
  type LiveRoomsPayload,
  type LiveSessionDetailPayload,
  type LiveSessionsPayload,
} from './liveApi';
import { formatBytes, formatNumber, formatTime } from './format';

const POLL_MS = 3000;
const PAGE_SIZE = 10;

type LoadState = 'loading' | 'ready' | 'error';
type Banner = { tone: 'info' | 'error'; text: string };

function errorMessage(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}

/** 解析输入框：纯直播间号，或 https://live.bilibili.com/<号> 链接。 */

/** `/live?room=<号>` 深链：刷新或从监控屏跳回时保持上次选中的预览房间。 */
function readRoomFromUrl(): number | null {
  const raw = new URLSearchParams(window.location.search).get('room');
  if (!raw) return null;
  const value = Number(raw);
  return Number.isInteger(value) && value > 0 ? value : null;
}

export function LivePage() {
  const [rooms, setRooms] = useState<LiveRoomsPayload | null>(null);
  const [roomsState, setRoomsState] = useState<LoadState>('loading');
  const [roomsError, setRoomsError] = useState<string | null>(null);
  const [storage, setStorage] = useState<LiveStorageStatus | null>(null);

  // 旧录制管线（/api/start|stop|mark + /api/status）：录制、弹幕热度与自动切片都来自这里。
  const [legacy, setLegacy] = useState<LegacyStatus | null>(null);
  const [legacyState, setLegacyState] = useState<LoadState>('loading');
  const [legacyError, setLegacyError] = useState<string | null>(null);

  const [sessions, setSessions] = useState<LiveSessionsPayload | null>(null);
  const [sessionsState, setSessionsState] = useState<LoadState>('loading');
  const [sessionsError, setSessionsError] = useState<string | null>(null);
  const [offset, setOffset] = useState(0);
  const [roomFilter, setRoomFilter] = useState<number | null>(null);

  const [detail, setDetail] = useState<LiveSessionDetailPayload | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [danmaku, setDanmaku] = useState<LiveDanmakuPayload | null>(null);
  const [danmakuLoading, setDanmakuLoading] = useState(false);
  const [danmakuError, setDanmakuError] = useState<string | null>(null);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [previewRoomId, setPreviewRoomId] = useState<number | null>(() => readRoomFromUrl());

  const [diagnostics, setDiagnostics] = useState<LiveDiagnosticsReport | null>(null);
  const [diagnosticsError, setDiagnosticsError] = useState<string | null>(null);
  const [diagnosticsLoading, setDiagnosticsLoading] = useState(true);

  const [autoClip, setAutoClip] = useState<AutoClipPayload | null>(null);
  const [autoClipLoading, setAutoClipLoading] = useState(true);
  const [autoClipError, setAutoClipError] = useState<string | null>(null);
  const [autoClipBusy, setAutoClipBusy] = useState(false);

  const [input, setInput] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const manualMarkLock=useRef(false);
  const [banner, setBanner] = useState<Banner | null>(null);
  const [confirmRemove, setConfirmRemove] = useState<number | null>(null);
  const [lastUpdated, setLastUpdated] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const mountedRef = useRef(false);
  const pollRef = useRef<{ offset: number; roomFilter: number | null }>({ offset: 0, roomFilter: null });
  pollRef.current = { offset, roomFilter };

  const loadLegacy = useCallback(async () => {
    try {
      const payload = await getLegacyStatus();
      if (!mountedRef.current) return;
      setLegacy(payload);
      setLegacyState('ready');
      setLegacyError(null);
    } catch (cause) {
      if (!mountedRef.current) return;
      setLegacyState('error');
      setLegacyError(errorMessage(cause));
    }
  }, []);

  const loadRooms = useCallback(async () => {
    try {
      const [roomsPayload, storagePayload] = await Promise.all([getLiveRooms(), getLiveStorage()]);
      if (!mountedRef.current) return;
      setRooms(roomsPayload);
      setStorage(storagePayload);
      setRoomsState('ready');
      setRoomsError(null);
    } catch (cause) {
      if (!mountedRef.current) return;
      setRoomsState('error');
      setRoomsError(errorMessage(cause));
    }
  }, []);

  const loadSessions = useCallback(async (queryOffset: number, queryRoom: number | null) => {
    try {
      const payload = await getLiveSessions({ limit: PAGE_SIZE, offset: queryOffset, roomId: queryRoom });
      if (!mountedRef.current) return;
      setSessions(payload);
      setSessionsState('ready');
      setSessionsError(null);
    } catch (cause) {
      if (!mountedRef.current) return;
      setSessionsState('error');
      setSessionsError(errorMessage(cause));
    }
  }, []);

  const loadDiagnostics = useCallback(async () => {
    setDiagnosticsLoading(true);
    try {
      const report = await getLiveDiagnostics();
      if (!mountedRef.current) return;
      setDiagnostics(report);
      setDiagnosticsError(null);
    } catch (cause) {
      if (!mountedRef.current) return;
      setDiagnosticsError(errorMessage(cause));
    } finally {
      if (mountedRef.current) setDiagnosticsLoading(false);
    }
  }, []);

  const loadAutoClip = useCallback(async () => {
    setAutoClipLoading(true);
    try {
      const payload = await getAutoClip();
      if (!mountedRef.current) return;
      setAutoClip(payload);
      setAutoClipError(null);
    } catch (cause) {
      if (!mountedRef.current) return;
      setAutoClipError(errorMessage(cause));
    } finally {
      if (mountedRef.current) setAutoClipLoading(false);
    }
  }, []);

  const loadDetail = useCallback(async (sessionId: string) => {
    setDetailLoading(true);
    setDetailError(null);
    try {
      const payload = await getLiveSessionDetail(sessionId);
      if (!mountedRef.current) return;
      setDetail(payload);
    } catch (cause) {
      if (!mountedRef.current) return;
      setDetail(null);
      setDetailError(errorMessage(cause));
    } finally {
      if (mountedRef.current) setDetailLoading(false);
    }
  }, []);

  const loadDanmaku = useCallback(async (sessionId: string) => {
    setDanmakuLoading(true);
    setDanmakuError(null);
    try {
      const payload = await getLiveSessionDanmaku(sessionId);
      if (!mountedRef.current) return;
      setDanmaku(payload);
    } catch (cause) {
      if (!mountedRef.current) return;
      setDanmaku(null);
      setDanmakuError(errorMessage(cause));
    } finally {
      if (mountedRef.current) setDanmakuLoading(false);
    }
  }, []);

  const refreshAll = useCallback(async () => {
    setRefreshing(true);
    await Promise.allSettled([
      loadLegacy(),
      loadRooms(),
      loadSessions(pollRef.current.offset, pollRef.current.roomFilter),
      loadDiagnostics(),
      loadAutoClip(),
    ]);
    if (mountedRef.current) {
      setLastUpdated(new Date().toISOString());
      setRefreshing(false);
    }
  }, [loadLegacy, loadRooms, loadSessions, loadDiagnostics, loadAutoClip]);

  useEffect(() => {
    mountedRef.current = true;
    void refreshAll();
    const timer = window.setInterval(() => void refreshAll(), POLL_MS);
    return () => {
      mountedRef.current = false;
      window.clearInterval(timer);
    };
  }, [refreshAll]);

  useEffect(() => {
    if (!mountedRef.current) return;
    void loadSessions(offset, roomFilter);
  }, [offset, roomFilter, loadSessions]);

  useEffect(() => {
    if (!selectedSessionId) {
      setDetail(null);
      setDetailError(null);
      setDanmaku(null);
      setDanmakuError(null);
      return;
    }
    void loadDetail(selectedSessionId);
    void loadDanmaku(selectedSessionId);
  }, [selectedSessionId, loadDetail, loadDanmaku]);

  // 选中房间同步到 `/live?room=`：刷新、从监控屏跳回时保持同一路预览；其它页面不改 URL。
  useEffect(() => {
    if (window.location.pathname !== '/live') return;
    const next = previewRoomId === null ? '/live' : `/live?room=${previewRoomId}`;
    if (`${window.location.pathname}${window.location.search}` === next) return;
    replaceAppLocation(next);
  }, [previewRoomId]);

  const cards = useMemo(() => {
    const ids = new Set<number>();
    for (const room of legacy?.rooms ?? []) ids.add(room.roomId);
    if (!rooms) return [...ids].sort((left, right) => left - right);
    for (const room of rooms.rooms) ids.add(room.roomId);
    for (const registration of rooms.registrations) ids.add(registration.roomId);
    return [...ids].sort((left, right) => left - right);
  }, [legacy, rooms]);

  const roomOptions = useMemo(() => {
    const ids = new Set<number>(cards);
    for (const session of sessions?.sessions ?? []) ids.add(session.roomId);
    return [...ids].sort((left, right) => left - right);
  }, [cards, sessions]);

  // 预览默认跟随第一个 live-core 房间；用户点选卡片后保持其选择（房间被移除才回退）。
  // 旧管线的房间也在选择集合里：选中没有 live-core 会话的房间时预览显示为空，但选择不会被轮询重置。
  useEffect(() => {
    const known = new Set<number>(cards);
    const previewable = new Set<number>();
    for (const status of rooms?.rooms ?? []) previewable.add(status.roomId);
    for (const registration of rooms?.registrations ?? []) previewable.add(registration.roomId);
    setPreviewRoomId((current) => {
      if (current !== null && known.has(current)) return current;
      return [...previewable].sort((left, right) => left - right)[0] ?? null;
    });
  }, [cards, rooms]);

  const previewRoom = useMemo<LivePreviewRoom | null>(() => {
    if (!rooms || previewRoomId === null) return null;
    const status = rooms.rooms.find((item) => item.roomId === previewRoomId) ?? null;
    const registration = rooms.registrations.find((item) => item.roomId === previewRoomId) ?? null;
    if (!status && !registration) return null;
    return {
      roomId: previewRoomId,
      title: status?.room?.title?.trim() || `直播间 ${previewRoomId}`,
      streamer: status?.room?.streamer ?? '',
      availability: status?.availability ?? null,
      running: status?.running ?? false,
    };
  }, [rooms, previewRoomId]);

  const monitoringCount = legacy?.rooms.filter((room) => room.monitoring).length ?? 0;
  const liveCount = legacy?.rooms.filter((room) => room.isLive).length ?? 0;
  const danmakuPackets = legacy?.rooms.reduce((total, room) => total + room.danmakuPackets, 0) ?? 0;

  async function handleStart(roomId: number,autoRecord=false,cacheEnabled=true) {
    setBusy(`start:${roomId}`);
    setBanner(null);
    try {
      await startLegacyMonitor(roomId,autoRecord,cacheEnabled);
      setBanner({ tone: 'info', text: monitorStartedMessage(roomId,autoRecord,cacheEnabled) });
      await Promise.all([loadLegacy(), loadAutoClip()]);
    } catch (cause) {
      setBanner({ tone: 'error', text: errorMessage(cause) });
    } finally {
      setBusy(null);
    }
  }

  async function handleStop(roomId: number) {
    setBusy(`stop:${roomId}`);
    setBanner(null);
    try {
      const result = await stopLegacyMonitor(roomId);
      setBanner({
        tone: 'info',
        text: `房间 ${roomId} 已停止监控；服务端当前仍有 ${result.rooms.length} 个房间在监控列表中。`,
      });
      await Promise.all([loadLegacy(), loadSessions(offset, roomFilter), loadAutoClip()]);
    } catch (cause) {
      setBanner({ tone: 'error', text: errorMessage(cause) });
    } finally {
      setBusy(null);
    }
  }

  async function handleMark(roomId: number) {
    if(manualMarkLock.current)return;
    const availability=manualHighlightAvailability(legacy?.rooms.find(room=>room.roomId===roomId)??null,legacy);
    if(!availability.enabled){setBanner({tone:'error',text:availability.description});return;}
    manualMarkLock.current=true;
    setBusy(`mark:${roomId}`);
    setBanner(null);
    try {
      const event = await markLegacyHighlight(roomId);
      setBanner({
        tone: 'info',
        text: `房间 ${roomId}：${manualHighlightReceipt(event)}`,
      });
      await Promise.all([loadLegacy(), loadAutoClip()]);
    } catch (cause) {
      setBanner({ tone: 'error', text: errorMessage(cause) });
    } finally {
      manualMarkLock.current=false;
      setBusy(null);
    }
  }

  async function doRemoveRoom(roomId: number) {
    setBusy(`remove:${roomId}`);
    setBanner(null);
    try {
      const result = await removeLiveRoom(roomId);
      setBanner({ tone: 'info', text: `房间 ${roomId} 登记已移除（停止=${result.stopped}）。` });
      await Promise.all([loadRooms(), loadSessions(offset, roomFilter)]);
    } catch (cause) {
      setBanner({ tone: 'error', text: errorMessage(cause) });
    } finally {
      setBusy(null);
    }
  }

  function handleRemove(roomId: number) {
    setConfirmRemove(roomId);
  }

  async function handleCleanup() {
    setBusy('cleanup');
    setBanner(null);
    try {
      const result = await cleanupLiveStorage();
      setStorage(result);
      await loadDiagnostics();
      setBanner({
        tone: 'info',
        text: `清理完成：删除 ${formatNumber(result.deletedSessions)} 个过期会话（${formatBytes(result.deletedBytes)}）。`,
      });
    } catch (cause) {
      setBanner({ tone: 'error', text: errorMessage(cause) });
    } finally {
      setBusy(null);
    }
  }

  async function handleToggleAutoClip(next: boolean) {
    setAutoClipBusy(true);
    setBanner(null);
    try {
      const result = await saveAutoClipPolicy({ enabled: next });
      setAutoClip((current) => (current ? { ...current, enabled: result.policy.enabled } : current));
      setBanner({
        tone: 'info',
        text: next ? '自动切片已开启：前 10 分钟弹幕热度作为基准。' : '自动切片已关闭（已有切片保留）。',
      });
      await loadAutoClip();
    } catch (cause) {
      setBanner({ tone: 'error', text: errorMessage(cause) });
    } finally {
      setAutoClipBusy(false);
    }
  }

  return (
    <AppShell
      title="直播"
      eyebrow="直播 · 监控"
      subtitle="房间监控、单路预览与自动切片策略"
      contentClassName="lc-app-shell__content--flush"
      onChannelSelect={(roomId) => setPreviewRoomId(roomId)}
      channelRoomId={previewRoomId}
      topbarActions={
        <>
          <span className={`lc-app-shell__status${legacyState === 'ready' ? ' is-ok' : ''}`}>
            <Wifi size={12} aria-hidden="true" />
            {legacyState === 'error' ? '后端不可达' : `自动刷新 ${POLL_MS / 1000}s`}
          </span>
          <span className="lc-app-shell__updated">最近读取 {lastUpdated ? formatTime(lastUpdated) : '—'}</span>
          <Button
            variant="outline"
            size="sm"
            leading={<RefreshCw size={13} aria-hidden="true" />}
            onClick={() => void refreshAll()}
            disabled={refreshing}
          >
            刷新
          </Button>
          <Button
            variant="solid"
            size="sm"
            leading={<MonitorPlay size={14} aria-hidden="true" />}
            onClick={() => void navigateApp('/live/monitor')}
          >
            打开监控台
          </Button>
          <Button
            variant="outline"
            size="sm"
            leading={<Settings size={14} aria-hidden="true" />}
            onClick={() => void navigateApp('/settings')}
          >
            设置
          </Button>
        </>
      }
    >
      <div className="lc-live-page">
        <section className="lw-stats" aria-label="直播概览">
          <div>
            <span>监控房间</span>
            <strong>
              {formatNumber(monitoringCount)}
              <small> / {legacy?.maxRooms ?? '—'}</small>
            </strong>
          </div>
          <div>
            <span>正在直播</span>
            <strong>{formatNumber(liveCount)}</strong>
          </div>
          <div>
            <span>弹幕包</span>
            <strong>{formatNumber(danmakuPackets)}</strong>
          </div>
          <div>
            <span title="来自 live-core 独立会话库，不含当前主录制目录">独立会话库占用</span>
            <strong>{storage ? formatBytes(storage.totalBytes) : '—'}</strong>
          </div>
          <div>
            <span title="live-core 工作区所在盘余量，不是当前主录制目录所在盘">工作区盘剩余</span>
            <strong>{storage ? formatBytes(storage.freeBytes) : '—'}</strong>
          </div>
          <div>
            <span title="只统计 live-core 独立会话库，不含主录播">独立库会话数</span>
            <strong>{sessions ? formatNumber(sessions.total) : '—'}</strong>
          </div>
        </section>

        {banner ? (
          <div className={`lw-banner lw-banner--${banner.tone}`} role="status">
            {banner.tone === 'error' ? <AlertTriangle size={12} /> : <Info size={12} />}
            <span>{banner.text}</span>
            <button type="button" onClick={() => setBanner(null)}>
              关闭
            </button>
          </div>
        ) : null}

        {(legacyState === 'error' && legacyError) || (roomsState === 'error' && roomsError) ? (
          <div className="lw-banner lw-banner--error" role="alert">
            <AlertTriangle size={12} />
            <span>
              后端不可达：{legacyError ?? roomsError}。请确认 LiveClipper 后端在 127.0.0.1:5179 运行。
            </span>
            <button type="button" onClick={() => void refreshAll()}>
              重试
            </button>
          </div>
        ) : null}

        {rooms !== null && previewRoomId !== null && previewRoom === null && legacyState !== 'loading' ? (
          <div className="lw-banner lw-banner--info" role="status">
            <Info size={12} />
            <span>
              房间 {previewRoomId} 暂无 live-core 预览会话（旧录制管线房间）：预览画面需要 live-core
              会话，弹幕与热度数据仍显示在下方面板；独立监控屏可直接查看该直播间画面。
            </span>
          </div>
        ) : null}

        <div className="lw-grid">
          <div className="lw-col lw-col--rooms">
            <LivePreview room={previewRoom} onRefresh={() => void loadRooms()} />
            <RoomPanel
              status={legacy}
              live={rooms}
              loading={legacyState === 'loading'}
              error={legacyState === 'error' ? legacyError : null}
              busy={busy}
              input={input}
              onInputChange={setInput}
              onStart={(roomId,autoRecord,cacheEnabled) => void handleStart(roomId,autoRecord,cacheEnabled)}
              onStop={(roomId) => void handleStop(roomId)}
              onMark={(roomId) => void handleMark(roomId)}
              onRemove={(roomId) => void handleRemove(roomId)}
              onRetry={() => void loadLegacy()}
              selectedRoomId={previewRoomId}
              onSelectRoom={setPreviewRoomId}
            />
            <AutoClipPanel
              rooms={legacy?.rooms??null}
              payload={autoClip}
              loading={autoClipLoading}
              error={autoClipError}
              busy={autoClipBusy}
              onToggle={(enabled) => void handleToggleAutoClip(enabled)}
              onRefresh={() => void loadAutoClip()}
            />
          </div>
          <SessionPanel
            data={sessions}
            loading={sessionsState === 'loading'}
            error={sessionsState === 'error' ? sessionsError : null}
            detail={detail}
            detailLoading={detailLoading}
            detailError={detailError}
            danmaku={danmaku}
            danmakuLoading={danmakuLoading}
            danmakuError={danmakuError}
            selectedSessionId={selectedSessionId}
            roomFilter={roomFilter}
            roomOptions={roomOptions}
            onSelect={setSelectedSessionId}
            onPage={(nextOffset) => setOffset(Math.max(0, nextOffset))}
            onRoomFilter={(nextRoom) => {
              setOffset(0);
              setRoomFilter(nextRoom);
            }}
            onRetry={() => {
              void loadSessions(offset, roomFilter);
              if (selectedSessionId) {
                void loadDetail(selectedSessionId);
                void loadDanmaku(selectedSessionId);
              }
            }}
          />
          <DiagnosticsPanel
            report={diagnostics}
            loading={diagnosticsLoading}
            error={diagnosticsError}
            onRefresh={() => void Promise.allSettled([loadDiagnostics(), loadRooms(), loadLegacy()])}
          />
        </div>

        <footer className="lw-foot">
          <span className="lw-mono">
            GET /api/status · /api/live/auto-clip · /api/live/rooms · /sessions · /diagnostics · POST /api/start · /stop · /mark
          </span>
          <span>
            <Activity size={9} /> 自动刷新 {POLL_MS / 1000}s · 诊断面板可一键刷新
          </span>
          <button
            type="button"
            className="live-btn live-btn--ghost"
            disabled={busy === 'cleanup' || storage?.expiredSessions === 0}
            onClick={() => void handleCleanup()}
          >
            {busy === 'cleanup' ? <LoaderCircle size={10} className="live-spin" /> : null}
            清理独立会话库过期缓冲（{storage ? formatNumber(storage.expiredSessions) : '—'}）
          </button>
        </footer>
      </div>

      <Dialog
        open={confirmRemove !== null}
        title="移除房间登记"
        description={
          confirmRemove !== null
            ? `移除房间 ${confirmRemove} 的登记？停止后下次启动不会再自动恢复它。`
            : undefined
        }
        size="sm"
        onClose={() => setConfirmRemove(null)}
        footer={
          <>
            <Button variant="outline" size="sm" onClick={() => setConfirmRemove(null)}>
              取消
            </Button>
            <Button
              variant="solid"
              size="sm"
              onClick={() => {
                const target = confirmRemove;
                setConfirmRemove(null);
                if (target !== null) void doRemoveRoom(target);
              }}
            >
              移除
            </Button>
          </>
        }
      />
    </AppShell>
  );
}
