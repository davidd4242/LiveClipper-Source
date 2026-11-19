import {
  AlertTriangle,
  ChevronLeft,
  ChevronRight,
  Film,
  ListVideo,
  LoaderCircle,
  MessageSquareText,
  RefreshCw,
} from 'lucide-react';
import type {
  LiveDanmakuPayload,
  LiveSessionDetailPayload,
  LiveSessionsPayload,
  LiveSessionSummary,
} from './liveApi';
import {
  formatBytes,
  formatClock,
  formatDuration,
  formatNumber,
  formatSeconds,
  sessionStateLabel,
  sessionStateTone,
  shortId,
} from './format';
import { Button, EmptyState } from '../../design';

interface SessionPanelProps {
  data: LiveSessionsPayload | null;
  loading: boolean;
  error: string | null;
  detail: LiveSessionDetailPayload | null;
  detailLoading: boolean;
  detailError: string | null;
  danmaku: LiveDanmakuPayload | null;
  danmakuLoading: boolean;
  danmakuError: string | null;
  selectedSessionId: string | null;
  roomFilter: number | null;
  roomOptions: number[];
  onSelect: (sessionId: string) => void;
  onPage: (offset: number) => void;
  onRoomFilter: (roomId: number | null) => void;
  onRetry: () => void;
}

function sessionMeta(session: LiveSessionSummary): string {
  const ended = session.endedAt ? `结束 ${formatClock(session.endedAt)}` : '未结束';
  return `${formatClock(session.startedAt)} · ${ended} · 时长 ${formatDuration(session.durationSeconds)}`;
}

export function SessionPanel({
  data,
  loading,
  error,
  detail,
  detailLoading,
  detailError,
  danmaku,
  danmakuLoading,
  danmakuError,
  selectedSessionId,
  roomFilter,
  roomOptions,
  onSelect,
  onPage,
  onRoomFilter,
  onRetry,
}: SessionPanelProps) {
  const sessions = data?.sessions ?? [];
  const limit = data?.limit ?? 10;
  const offset = data?.offset ?? 0;
  const total = data?.total ?? 0;
  const pageStart = total === 0 ? 0 : offset + 1;
  const pageEnd = Math.min(offset + sessions.length, total);
  const canPrev = offset > 0;
  const canNext = offset + limit < total;
  const selected = detail?.session ?? null;
  const segments = detail?.segments ?? [];

  return (
    <section className="lw-panel" aria-label="会话与分片面板">
      <p className="lw-room__hint">此处来自 live-core 独立会话库，不含当前主录制管线的 F 盘录播；正在采集的主会话 ID、状态和缓存时长见房间卡片。</p>
      <header className="lw-panel__head">
        <div>
          <h2>
            <ListVideo size={13} /> 独立会话库与分片
          </h2>
          <span>
            {data ? `${formatNumber(total)} 场会话 · ${pageStart}-${pageEnd} · 每页 ${limit}` : '等待会话数据'}
          </span>
        </div>
        <div className="lw-session-tools">
          <label className="lw-select">
            <span>房间</span>
            <select
              value={roomFilter ?? ''}
              onChange={(event) => onRoomFilter(event.target.value ? Number(event.target.value) : null)}
            >
              <option value="">全部</option>
              {roomOptions.map((roomId) => (
                <option key={roomId} value={roomId}>
                  {roomId}
                </option>
              ))}
            </select>
          </label>
          <button
            type="button"
            className="live-btn"
            disabled={!canPrev}
            onClick={() => onPage(Math.max(0, offset - limit))}
            aria-label="上一页"
          >
            <ChevronLeft size={11} />
          </button>
          <button
            type="button"
            className="live-btn"
            disabled={!canNext}
            onClick={() => onPage(offset + limit)}
            aria-label="下一页"
          >
            <ChevronRight size={11} />
          </button>
        </div>
      </header>

      <div className="lw-panel__body">
        {error ? (
          <EmptyState
            compact
            icon={<AlertTriangle size={22} aria-hidden="true" />}
            title="会话数据读取失败"
            description={error}
            action={
              <Button variant="outline" size="sm" onClick={onRetry}>
                重试
              </Button>
            }
          />
        ) : null}

        {!error && loading && sessions.length === 0 ? (
          <EmptyState compact icon={<LoaderCircle size={22} className="live-spin" aria-hidden="true" />} title="正在读取会话历史…" />
        ) : null}

        {!error && !loading && sessions.length === 0 ? (
          <EmptyState
            icon={<ListVideo size={22} aria-hidden="true" />}
            title="还没有可查询的直播会话"
            description="这里只显示 live-core 管线会话，不代表当前主录播是否成功；点开一行可查看该库分片。"
          />
        ) : null}

        {sessions.length > 0 ? (
          <div className="lw-session-list" role="list">
            {sessions.map((session) => (
              <button
                key={session.id}
                type="button"
                role="listitem"
                className={`lw-session${selectedSessionId === session.id ? ' is-selected' : ''}`}
                onClick={() => onSelect(session.id)}
              >
                <div className="lw-session__main">
                  <span className={`lw-badge lw-badge--${sessionStateTone(session.state)}`}>
                    {sessionStateLabel(session.state)}
                  </span>
                  <strong className="lw-mono">{shortId(session.id)}</strong>
                  <span className="lw-mono">房间 {session.roomId}</span>
                  <span>分片 {formatNumber(session.segments)}</span>
                  <span>恢复 {formatNumber(session.recoveredSegments)}</span>
                  <span>弹幕 {formatNumber(session.danmakuCount)}</span>
                  {session.restartCount > 0 ? <span className="is-warn">重连 {session.restartCount}</span> : null}
                </div>
                <span className="lw-session__meta">{sessionMeta(session)}</span>
              </button>
            ))}
          </div>
        ) : null}

        {selectedSessionId ? (
          <div className="lw-segments">
            <div className="lw-segments__head">
              <h3>
                <Film size={12} /> 分片清单
              </h3>
              {detailError ? (
                <button type="button" className="live-btn" onClick={onRetry}>
                  <RefreshCw size={10} /> 重试
                </button>
              ) : null}
            </div>

            {detailLoading ? (
              <div className="lw-empty lw-empty--compact">
                <LoaderCircle size={16} className="live-spin" />
                <p>正在读取会话分片…</p>
              </div>
            ) : null}

            {detailError ? (
              <div className="lw-error lw-error--inline">
                <AlertTriangle size={11} />
                <div>
                  <strong>分片读取失败</strong>
                  <span>{detailError}</span>
                </div>
              </div>
            ) : null}

            {!detailLoading && !detailError && selected ? (
              <>
                <p className="lw-segments__meta lw-mono">
                  会话 {selected.id} · 开始 {formatClock(selected.startedAt)} · 分片时长合计{' '}
                  {formatSeconds(selected.segmentDurationSeconds)} · 恢复{' '}
                  {formatNumber(selected.recoveredSegments)} 段
                  {selected.resumedFrom ? ` · 承接 ${shortId(selected.resumedFrom)}` : ''}
                </p>
                {segments.length === 0 ? (
                  <div className="lw-empty lw-empty--compact">
                    <Film size={16} />
                    <p>该会话暂无可读分片</p>
                    <span>会话可能仍在录制，或分片索引尚未落盘。</span>
                  </div>
                ) : (
                  <div className="lw-table-wrap">
                    <table className="lw-table">
                      <thead>
                        <tr>
                          <th>序号</th>
                          <th>偏移</th>
                          <th>时长</th>
                          <th>大小</th>
                          <th>文件</th>
                          <th>完成时间</th>
                        </tr>
                      </thead>
                      <tbody>
                        {segments.map((segment) => (
                          <tr key={`${segment.sequence}-${segment.file}`}>
                            <td className="lw-mono">{segment.sequence}</td>
                            <td className="lw-mono">{formatSeconds(segment.offsetSeconds)}</td>
                            <td className="lw-mono">{formatSeconds(segment.durationSeconds)}</td>
                            <td className="lw-mono">{formatBytes(segment.bytes)}</td>
                            <td className="lw-mono lw-ellipsis" title={segment.file}>
                              {segment.file}
                            </td>
                            <td className="lw-mono">{formatClock(segment.finalizedAt)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </>
            ) : null}

            {!selectedSessionId ? <p className="lw-muted">选择上方会话查看分片</p> : null}
          </div>
        ) : null}

        {selectedSessionId ? (
          <div className="lw-danmaku">
            <div className="lw-danmaku__head">
              <h3>
                <MessageSquareText size={12} /> 弹幕记录
              </h3>
              <span className="lw-mono">
                {danmaku ? `回看 ${formatNumber(danmaku.events.length)} 条` : '读取中…'}
              </span>
            </div>

            {danmakuLoading ? (
              <div className="lw-empty lw-empty--compact">
                <LoaderCircle size={16} className="live-spin" />
                <p>正在读取会话弹幕…</p>
              </div>
            ) : null}

            {danmakuError ? (
              <div className="lw-error lw-error--inline">
                <AlertTriangle size={11} />
                <div>
                  <strong>弹幕读取失败</strong>
                  <span>{danmakuError}</span>
                </div>
              </div>
            ) : null}

            {!danmakuLoading && !danmakuError && danmaku && danmaku.events.length === 0 ? (
              <div className="lw-empty lw-empty--compact">
                <MessageSquareText size={16} />
                <p>该会话暂无可读弹幕</p>
                <span>抓取时弹幕队列为空，或直播间当时没有弹幕。</span>
              </div>
            ) : null}

            {!danmakuLoading && !danmakuError && danmaku && danmaku.events.length > 0 ? (
              <div className="lw-danmaku__list" role="list">
                {danmaku.events.map((event) => (
                  <div key={event.id} className="lw-danmaku__row" role="listitem">
                    <span className="lw-danmaku__offset lw-mono">
                      {formatSeconds(event.offsetSeconds)}
                    </span>
                    <span className={`lw-danmaku__kind lw-mono`}>{event.kind}</span>
                    <span className="lw-danmaku__user">{event.user ?? '匿名'}</span>
                    <span className="lw-danmaku__text" title={event.text}>
                      {event.text}
                    </span>
                  </div>
                ))}
              </div>
            ) : null}
          </div>
        ) : null}
      </div>
    </section>
  );
}
