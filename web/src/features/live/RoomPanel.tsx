import {RoomHeatDetails} from './RoomHeatDetails';
import {RecordingDirectory,RoomRecordingControl} from './RecordingControls';
import {manualHighlightAvailability} from './manualHighlight';
import {RoomEntry} from './RoomEntry';
import { useMemo } from 'react';
import {
  AlertTriangle,
  Eye,
  Flame,
  LoaderCircle,
  Play,
  Radio,
  Scissors,
  Square,
  Trash2,
} from 'lucide-react';
import { Button, EmptyState } from '../../design';
import type {
  LegacyRoomStatus,
  LegacyStatus,
  LiveRecoveryState,
  LiveRoomRegistration,
  LiveRoomsPayload,
} from './liveApi';
import {
  formatClock,
  formatDuration,
  formatNumber,
  recoveryDescription,
  recoveryTone,
  shortId,
} from './format';

const recordingStateLabels: Record<string, string> = {
  Offline: '未开播',
  Connecting: '连接中',
  Recording: '录制中',
  Reconnecting: '重连中',
  Finalizing: '收尾中',
  Ended: '已结束',
};

function recordingStateLabel(state: string | null | undefined): string {
  if (!state) return '—';
  return recordingStateLabels[state] ?? state;
}

function recordingStateTone(state: string | null | undefined): 'ok' | 'warn' | 'bad' | 'idle' {
  switch (state) {
    case 'Recording':
    case 'Finalizing':
      return 'ok';
    case 'Connecting':
    case 'Reconnecting':
      return 'warn';
    case 'Ended':
      return 'bad';
    default:
      return 'idle';
  }
}

function busyFor(busy: string | null, roomId: number): boolean {
  return busy?.endsWith(`:${roomId}`) ?? false;
}

function roomHint(room: LegacyRoomStatus): string {
  if (room.monitoring) {
    if(room.captureMode==='monitor-only')return '纯预览，不写媒体文件；高光缓存和整场留存均关闭';
    if(room.captureMode==='highlight-cache')return room.cacheActive?'高光临时缓存采集中，不保留整场':'高光缓存已启用，等待采集';
    if (room.state === 'Recording') return '直播中：录制、弹幕采集与自动切片同时运行';
    if (room.state === 'Reconnecting') return '重连中：等待流恢复后继续录制';
    return '监控中：等待开播或正在建立录制会话';
  }
  return '已停止监控；可重新开始，旧录制管线会先探测开播状态';
}

function RoomCard({
  room,
  manualAvailability,
  busy,
  selected,
  previewable,
  onSelect,
  onStart,
  onStop,
  onMark,
  onRecordingChanged,
}: {
  room: LegacyRoomStatus;
  manualAvailability:{enabled:boolean;description:string};
  busy: string | null;
  selected: boolean;
  previewable: boolean;
  onSelect: (roomId: number) => void;
  onStart: (roomId: number,autoRecord?:boolean,cacheEnabled?:boolean) => void;
  onStop: (roomId: number) => void;
  onMark: (roomId: number) => void;
  onRecordingChanged:()=>void|Promise<void>;
}) {
  const roomBusy = busyFor(busy, room.roomId);
  const startBusy = busy === `start:${room.roomId}`;
  const stopBusy = busy === `stop:${room.roomId}`;
  const markBusy = busy === `mark:${room.roomId}`;
  const selectCard = () => onSelect(room.roomId);

  return (
    <article
      className={`lw-room${room.monitoring ? '' : ' lw-room--idle'}${selected ? ' is-selected' : ''}`}
      onClick={selectCard}
    >
      <div className="lw-room__head">
        <div className="lw-room__title">
          <p className="lw-mono">ROOM {room.roomId}</p>
          <h3 title={room.title || undefined}>{room.title || `直播间 ${room.roomId}`}</h3>
          <span className="lw-room__hint">
            {room.streamer ? `${room.streamer} · ` : ''}
            {roomHint(room)}
          </span>
        </div>
        <div className="lw-room__badges">
          <button
            type="button"
            className={`lw-room__preview${selected ? ' is-active' : ''}`}
            aria-pressed={selected}
            onClick={(event) => {
              event.stopPropagation();
              selectCard();
            }}
          >
            <Eye size={9} aria-hidden="true" />
            {selected ? '预览中' : '预览'}
          </button>
          <span className={`live-badge ${room.monitoring ? 'live-badge--live' : 'live-badge--idle'}`}>
            {room.monitoring ? '监控中' : '未运行'}
          </span>
          <span className={`live-badge ${room.isLive ? 'live-badge--live' : 'live-badge--offline'}`}>
            {room.isLive ? '直播中' : '未开播'}
          </span>
          {room.state ? (
            <span className={`lw-badge lw-badge--${recordingStateTone(room.state)}`}>
              {recordingStateLabel(room.state)}
            </span>
          ) : null}
          {!room.danmakuAuthorized ? (
            <span className="lw-badge lw-badge--warn" title={room.danmakuError ?? '未配置 B 站 Cookie，弹幕采集不可用'}>
              弹幕未授权
            </span>
          ) : null}
        </div>
      </div>

      <div className="lw-room__metrics">
        <div>
          <span>消息速率</span>
          <strong>{`${room.messageRate.toFixed(2)}/s`}</strong>
        </div>
        <div>
          <span>热度基准</span>
          <strong>{room.baseline.toFixed(2)}</strong>
        </div>
        <div>
          <span>当前热度</span>
          <strong className={room.monitoring ? undefined : 'is-warn'}>{room.heatScore.toFixed(2)}</strong>
        </div>
        <div>
          <span>弹幕包</span>
          <strong>{formatNumber(room.danmakuPackets)}</strong>
        </div>
        <div>
          <span>缓冲</span>
          <strong>{formatDuration(room.bufferSeconds)}</strong>
        </div>
      </div>

      {room.sessionId ? (
        <p className="lw-room__session lw-mono">
          会话 {shortId(room.sessionId)}
          {room.startedAt ? ` · 开始 ${formatClock(room.startedAt)}` : ''}
        </p>
      ) : null}
      {room.heatReason ? (
        <p className="lw-room__hint">
          <Flame size={10} /> {room.heatReason}
        </p>
      ) : null}
      {room.error ? (
        <p className="lw-room-error">
          <AlertTriangle size={11} />
          {room.error}
        </p>
      ) : null}
      {room.danmakuError ? (
        <p className="lw-room-error">
          <AlertTriangle size={11} />
          {room.danmakuError}
        </p>
      ) : null}

      <details><summary>动态弹幕基准</summary><RoomHeatDetails room={room} comparisonSeconds={null}/></details>
      <RoomRecordingControl room={room} onChanged={onRecordingChanged}/>
      <div className="lw-room__actions">
        {room.monitoring ? (
          <>
            <button
              type="button"
              className="live-btn"
              disabled={roomBusy||!manualAvailability.enabled}
              title={manualAvailability.description}
              onClick={(event) => {
                event.stopPropagation();
                if(manualAvailability.enabled)onMark(room.roomId);
              }}
            >
              {markBusy ? <LoaderCircle size={11} className="live-spin" /> : <Scissors size={10} />}
              手动高光录制
            </button>
            <button
              type="button"
              className="live-btn live-btn--danger"
              disabled={roomBusy}
              onClick={(event) => {
                event.stopPropagation();
                onStop(room.roomId);
              }}
            >
              {stopBusy ? <LoaderCircle size={11} className="live-spin" /> : <Square size={10} />}
              停止监控
            </button>
          </>
        ) : (
          <button
            type="button"
            className="live-btn live-btn--primary"
            disabled={roomBusy}
            onClick={(event) => {
              event.stopPropagation();
              onStart(room.roomId,room.autoRecord,room.cacheEnabled);
            }}
          >
            {startBusy ? <LoaderCircle size={11} className="live-spin" /> : <Play size={10} />}
            开始监控
          </button>
        )}
        <span className="lw-room__update" title={manualAvailability.description}>
          {previewable ? '预览可用' : '没有 live-core 预览'} · {manualAvailability.description}
        </span>
      </div>
    </article>
  );
}

interface RegistrationRow {
  registration: LiveRoomRegistration;
  recovery: LiveRecoveryState | null;
}

interface RoomPanelProps {
  status: LegacyStatus | null;
  live: LiveRoomsPayload | null;
  loading: boolean;
  error: string | null;
  busy: string | null;
  input: string;
  onInputChange: (value: string) => void;
  onStart: (roomId: number,autoRecord?:boolean,cacheEnabled?:boolean) => void;
  onStop: (roomId: number) => void;
  onMark: (roomId: number) => void;
  onRemove: (roomId: number) => void;
  onRetry: () => void;
  selectedRoomId: number | null;
  onSelectRoom: (roomId: number) => void;
}

export function RoomPanel({
  status,
  live,
  loading,
  error,
  busy,
  input,
  onInputChange,
  onStart,
  onStop,
  onMark,
  onRemove,
  onRetry,
  selectedRoomId,
  onSelectRoom,
}: RoomPanelProps) {
  const rooms = useMemo<LegacyRoomStatus[]>(() => status?.rooms ?? [], [status]);
  const registrations = useMemo<RegistrationRow[]>(() => {
    if (!live) return [];
    const running = new Set(live.rooms.map((room) => room.roomId));
    const recoveries = new Map(live.recovery.map((item) => [item.roomId, item]));
    return live.registrations
      .filter((item) => !running.has(item.roomId))
      .map((item) => ({ registration: item, recovery: recoveries.get(item.roomId) ?? null }));
  }, [live]);
  const monitoringCount = rooms.filter((room) => room.monitoring).length;
  const recordingCount = rooms.filter((room) => room.state === 'Recording').length;
  const manualPre = status?.manualPreRollSeconds;
  const manualPost = status?.manualPostRollSeconds;
  const manualPolicyLabel=manualPre==null||manualPost==null?'读取高光时长中':`手动高光 ${manualPre}s + ${manualPost}s`;
  const previewable = useMemo(() => new Set((live?.rooms ?? []).map((room) => room.roomId)), [live]);

  return (
    <section className="lw-panel" aria-label="房间面板">
      <header className="lw-panel__head lw-room-entry-head">
        <div>
          <h2>
            <Radio size={13} /> 房间
          </h2>
          <span>
            {status
              ? `${rooms.length} 个房间 · ${monitoringCount} 个监控中 · ${recordingCount} 个录制中 · 上限 ${status.maxRooms} · ${manualPolicyLabel}`
              : '等待房间状态'}
          </span>
        </div>
        <RecordingDirectory/>
        <RoomEntry busy={busy!==null} input={input} onInputChange={onInputChange} onStart={onStart}/>
      </header>

      <div className="lw-panel__body">
        {rooms.length===0&&<p role="status" className="lw-manual-empty">手动高光录制暂不可用：先选择正在录制且已有缓存的房间。</p>}
        <p className="lw-room__hint">
          数据源 <span className="lw-mono">/api/status</span>（旧录制管线）；开始/停止/保存高光直接驱动它。下方 live-core 预览与登记保持只读。
        </p>

        {error ? (
          <EmptyState
            compact
            icon={<AlertTriangle size={22} aria-hidden="true" />}
            title="房间状态读取失败"
            description={error}
            action={
              <Button variant="outline" size="sm" onClick={onRetry}>
                重试
              </Button>
            }
          />
        ) : null}

        {!error && loading && rooms.length === 0 ? (
          <EmptyState compact icon={<LoaderCircle size={22} className="live-spin" aria-hidden="true" />} title="正在读取房间状态…" />
        ) : null}

        {!error && !loading && rooms.length === 0 ? (
          <EmptyState
            icon={<Radio size={22} aria-hidden="true" />}
            title="还没有监控的房间"
            description="输入房间号、UID 或链接识别直播间，再选择高光临时缓存、整场留存或纯预览；只有可用采集缓存才能生成高光。"
          />
        ) : null}

        {rooms.length > 0 ? (
          <div className="lw-room-list" role="region" aria-label="监控房间卡片列表" tabIndex={0}>
            {rooms.map((room) => (
              <RoomCard
                key={room.roomId}
                room={room}
                manualAvailability={manualHighlightAvailability(room,status)}
                busy={busy}
                selected={selectedRoomId === room.roomId}
                previewable={previewable.has(room.roomId)}
                onSelect={onSelectRoom}
                onStart={onStart}
                onStop={onStop}
                onRecordingChanged={onRetry}
                onMark={onMark}
              />
            ))}
          </div>
        ) : null}

        {registrations.length > 0 ? (
          <div className="lw-recovery-list">
            <p className="lw-room__hint">旧版 live-core 登记（未运行，可移除）</p>
            {registrations.map(({ registration, recovery }) => (
              <div
                className={`lw-recovery${recovery && recoveryTone(recovery) !== 'ok' ? ' lw-recovery--warn' : ' lw-recovery--idle'}`}
                key={`registration-${registration.roomId}`}
              >
                <span className="lw-mono">ROOM {registration.roomId}</span>
                <span className="lw-room__hint">
                  {registration.active ? '自动恢复已启用' : '已停用'}
                  {recovery ? ` · ${recoveryDescription(recovery)}` : ''}
                </span>
                {recovery ? (
                  <span className={`lw-badge lw-badge--${recoveryTone(recovery)}`}>
                    {recoveryTone(recovery) === 'ok' ? '正常' : '恢复关注'}
                  </span>
                ) : null}
                <button
                  type="button"
                  className="live-btn live-btn--ghost"
                  onClick={(event) => {
                    event.stopPropagation();
                    onRemove(registration.roomId);
                  }}
                >
                  <Trash2 size={10} /> 移除登记
                </button>
              </div>
            ))}
          </div>
        ) : null}
      </div>
    </section>
  );
}
