/**
 * 直播工作台 v1 的只读/操作 API 层。
 *
 * 该文件只属于 features/live，避免污染共享的 api/client.ts；端点形状与
 * src/LiveClipper.App/LiveEndpoints.cs、src/LiveClipper.Live/LiveDiagnosticsService.cs
 * 保持一一对应，字段均为后端 camelCase JSON。
 */
import type { LiveStorageStatus } from '../../api/types';

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

export class LiveApiError extends Error {
  readonly status: number;

  constructor(message: string, status = 0) {
    super(message);
    this.name = 'LiveApiError';
    this.status = status;
  }
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${apiBase}${path}`, {
      ...init,
      headers: {
        Accept: 'application/json',
        ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
        ...init?.headers,
      },
    });
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new LiveApiError(`无法连接 LiveClipper 后端：${detail}`);
  }

  if (!response.ok) {
    let message = `请求失败（HTTP ${response.status}）`;
    try {
      const body = (await response.json()) as { error?: string | { fields?: string[] } };
      if (typeof body.error === 'string') message = body.error;
      else if (body.error?.fields?.length) message = `设置无效：${body.error.fields.join('、')}`;
    } catch {
      const text = await response.text().catch(() => '');
      if (text.trim()) message = text.trim().slice(0, 240);
    }
    throw new LiveApiError(message, response.status);
  }

  return (await response.json()) as T;
}

export type LiveAvailability = 'Unknown' | 'Offline' | 'Live';

export type LiveSessionState =
  | 'Starting'
  | 'Recording'
  | 'Reconnecting'
  | 'Finalizing'
  | 'Ended'
  | 'Interrupted'
  | 'Failed';

export interface LiveRoomProbeInfo {
  roomId: number;
  availability: LiveAvailability;
  title: string;
  streamer: string;
  checkedAt: string;
}

export interface LiveSessionInfo {
  id: string;
  roomId: number;
  state: LiveSessionState;
  startedAt: string;
  endedAt: string | null;
  resumedFrom: string | null;
  restartCount: number;
  lastSequence: number;
  lastError: string | null;
  isOpen: boolean;
}

/** GET /api/live/rooms 中的房间快照；G7 字段在旧二进制下可能缺失。 */
export interface LiveRoomStatus {
  roomId: number;
  availability: LiveAvailability;
  running: boolean;
  capturing: boolean;
  room: LiveRoomProbeInfo | null;
  session: LiveSessionInfo | null;
  lastError: string | null;
  segmentCount: number;
  danmakuCount: number;
  restartCount: number;
  recoveredSessions: number;
  updatedAt: string;
  danmakuQueueCapacity?: number;
  danmakuQueueDropped?: number;
}

export interface LiveRoomRegistration {
  roomId: number;
  active: boolean;
  addedAt: string;
  updatedAt: string;
  lastRecoveryAttemptAt?: string | null;
  recoveryFailures?: number;
  nextRecoveryAt?: string | null;
  lastRecoveryError?: string | null;
}

export interface LiveRecoveryState {
  roomId: number;
  failures: number;
  lastAttemptAt: string | null;
  lastError: string | null;
  nextAttemptAt: string | null;
  succeeded: boolean;
}

export interface LiveRoomsPayload {
  rooms: LiveRoomStatus[];
  registrations: LiveRoomRegistration[];
  recovery: LiveRecoveryState[];
  maxRooms: number;
}

export interface LiveResolution {
  input: number;
  roomId: number;
  resolvedFrom: 'room' | 'uid';
  title: string;
  streamer: string;
  availability: LiveAvailability;
}

export interface StopLiveRoomResponse {
  stopped: boolean;
  roomId: number;
  alreadyStopped?: boolean;
}

export interface LiveSessionSummary {
  id: string;
  roomId: number;
  state: string;
  startedAt: string;
  endedAt: string | null;
  lastSequence: number;
  recoveredSegments: number;
  segments: number;
  danmakuCount: number;
  resumedFrom: string | null;
  restartCount: number;
  durationSeconds: number;
  segmentDurationSeconds: number;
}

export interface LiveSessionsPayload {
  sessions: LiveSessionSummary[];
  total: number;
  limit: number;
  offset: number;
}

export interface LiveSegmentView {
  sequence: number;
  offsetSeconds: number;
  durationSeconds: number;
  bytes: number;
  file: string;
  finalizedAt: string;
}

export interface LiveSessionDetailPayload {
  session: LiveSessionSummary;
  segments: LiveSegmentView[];
}

/** GET /api/live/sessions/{id}/danmaku 中的一条弹幕；offset 相对录制起点。 */
export interface LiveDanmakuEvent {
  id: string;
  offsetSeconds: number;
  sentAt: string;
  uid: number | null;
  user: string | null;
  text: string;
  kind: string;
}

export interface LiveDanmakuPayload {
  sessionId: string;
  events: LiveDanmakuEvent[];
  limit: number;
}

export interface LiveStoreIssue {
  kind: string;
  path: string;
  line: number | null;
  sizeBytes: number | null;
  message: string;
}

export interface LiveStoreDiagnostics {
  root: string;
  sessionFiles: number;
  openSessions: number;
  corruptFiles: number;
  quarantinedFiles: number;
  quarantineFiles: string[];
  issues: LiveStoreIssue[];
}

export interface LiveRegistryDiagnostics {
  path: string;
  loaded: boolean;
  lastError: string | null;
  lastErrorAt: string | null;
  corruptBackups: string[];
}

export interface LiveOrphanMediaDirectory {
  relativePath: string;
  files: number;
  bytes: number;
}

export interface LiveDanmakuDrop {
  roomId: number;
  queueDropped: number;
  queueCapacity: number;
}

export interface LiveDiagnosticsReport {
  checkedAt: string;
  root: string;
  store: LiveStoreDiagnostics;
  registry: LiveRegistryDiagnostics;
  storage: LiveStorageStatus | null;
  recovery: LiveRecoveryState[];
  orphanMediaDirectories: LiveOrphanMediaDirectory[];
  warnings: string[];
  danmakuDropped: LiveDanmakuDrop[];
}

export interface LiveSessionsQuery {
  roomId?: number | null;
  limit: number;
  offset: number;
}

function sessionsPath(query: LiveSessionsQuery): string {
  const params = new URLSearchParams();
  if (query.roomId != null) params.set('roomId', String(query.roomId));
  params.set('limit', String(query.limit));
  params.set('offset', String(query.offset));
  return `/api/live/sessions?${params.toString()}`;
}

export function getLiveRooms(): Promise<LiveRoomsPayload> {
  return requestJson<LiveRoomsPayload>('/api/live/rooms');
}

export function getLiveStorage(): Promise<LiveStorageStatus> {
  return requestJson<LiveStorageStatus>('/api/live/storage');
}

export function getLiveSessions(query: LiveSessionsQuery): Promise<LiveSessionsPayload> {
  return requestJson<LiveSessionsPayload>(sessionsPath(query));
}

export function getLiveSessionDetail(sessionId: string): Promise<LiveSessionDetailPayload> {
  return requestJson<LiveSessionDetailPayload>(
    `/api/live/sessions/${encodeURIComponent(sessionId)}/segments`,
  );
}

export function getLiveSessionDanmaku(
  sessionId: string,
  limit = 200,
): Promise<LiveDanmakuPayload> {
  return requestJson<LiveDanmakuPayload>(
    `/api/live/sessions/${encodeURIComponent(sessionId)}/danmaku?limit=${encodeURIComponent(String(limit))}`,
  );
}

export function getLiveDiagnostics(): Promise<LiveDiagnosticsReport> {
  return requestJson<LiveDiagnosticsReport>('/api/live/diagnostics');
}

export function resolveLiveInput(input: string, signal?:AbortSignal): Promise<LiveResolution> {
  return requestJson<LiveResolution>(`/api/live/resolve?input=${encodeURIComponent(input)}`, {signal});
}

export function startLiveRoom(roomId: number): Promise<LiveRoomStatus> {
  return requestJson<LiveRoomStatus>(`/api/live/rooms/${roomId}/start`, { method: 'POST' });
}

export function stopLiveRoom(roomId: number): Promise<StopLiveRoomResponse> {
  return requestJson<StopLiveRoomResponse>(`/api/live/rooms/${roomId}/stop`, { method: 'POST' });
}

export function removeLiveRoom(roomId: number): Promise<{ removed: boolean; stopped: boolean }> {
  return requestJson<{ removed: boolean; stopped: boolean }>(`/api/live/rooms/${roomId}`, {
    method: 'DELETE',
  });
}

export function cleanupLiveStorage(): Promise<LiveStorageStatus> {
  return requestJson<LiveStorageStatus>('/api/live/storage/cleanup', { method: 'POST' });
}

export interface LegacySession {
  id: string;
  roomId: number;
  startedAt: string;
  endedAt: string | null;
  state: string;
}

export interface LegacyRoomStatus {
  autoRecord: boolean;
  cacheEnabled: boolean;
  cacheActive: boolean;
  captureMode: 'monitor-only'|'highlight-cache'|'full-recording';
  heatBaselinePercent?:number;
  heatRebaseReason?:string;
  heatRebaseAt?:number|null;
  heatRecentTriggers?:number;
  roomId: number;
  title: string;
  streamer: string;
  isLive: boolean;
  monitoring: boolean;
  sessionId: string | null;
  state: string | null;
  startedAt: string | null;
  messageRate: number;
  baseline: number;
  heatScore: number;
  danmakuPackets: number;
  danmakuAuthorized: boolean;
  heatReason: string;
  error: string | null;
  danmakuError: string | null;
  bufferSeconds: number;
}

/** GET /api/status：旧录制管线（/api/start|stop|mark）的状态与设置口径。 */
export interface LegacyStatus {
  monitoring: boolean;
  room: { roomId: number; canonicalId: number; isLive: boolean; title: string; streamer: string } | null;
  session: LegacySession | null;
  error: string | null;
  danmakuError: string | null;
  danmakuPackets: number;
  lastDanmakuCommand: number;
  danmakuAuthorized: boolean;
  messageRate: number;
  baseline: number;
  heatScore: number;
  rooms: LegacyRoomStatus[];
  maxRooms: number;
  heatReason: string;
  autoHighlightCooldownSeconds: number;
  autoHighlightBaselineSeconds: number;
  autoHighlightComparisonSeconds: number;
  autoHighlightBaselineRatio: number;
  autoHighlightEnabled: boolean;
  autoHighlightReleaseRatio: number;
  autoHighlightMaxExtensionSeconds: number;
  highlightPreRollSeconds: number;
  highlightPostRollSeconds: number;
  heatTriggerLine: number;
  heatReleaseLine: number;
  heatActive: boolean;
  heatInBaseline: boolean;
  ffmpegAvailable: boolean;
  segmentCount: number;
  bufferSeconds: number;
  bufferUnlimited: boolean;
  bufferLimitSeconds: number | null;
  danmakuRecordLimit: number;
  manualPreRollSeconds: number;
  manualPostRollSeconds: number;
  appVersion: string;
}

/** POST /api/mark 返回的高光事件；时间均为相对录制起点的秒数。 */
export interface LegacyHighlightEvent {
  id: string;
  sessionId: string;
  source: string;
  triggerTime: number;
  start: number;
  end: number;
  state: string;
  score: number;
  closed?: boolean;
  /** 崩溃/重启恢复时只覆盖到可用分片末尾，切片不是完整的后录长度。 */
  partial?: boolean;
}

export interface LegacyStartResponse {
  started: boolean;
}

export interface LegacyStopResponse {
  stopped: boolean;
  rooms: number[];
}

export function getLegacyStatus(): Promise<LegacyStatus> {
  return requestJson<LegacyStatus>('/api/status');
}

/** 启动旧录制管线（coordinator）的直播间监控；body 只带 roomId，UID 解析由旧管线自己处理。 */
export function startLegacyMonitor(roomId: number, autoRecord = false, cacheEnabled = true): Promise<LegacyStartResponse> {
  return requestJson<LegacyStartResponse>('/api/start', {
    method: 'POST',
    body: JSON.stringify({ roomId, autoRecord, cacheEnabled }),
  });
}

export function stopLegacyMonitor(roomId: number | null = null): Promise<LegacyStopResponse> {
  const query = roomId != null ? `?roomId=${encodeURIComponent(String(roomId))}` : '';
  return requestJson<LegacyStopResponse>(`/api/stop${query}`, { method: 'POST' });
}

/** 手动保存高光：不传 roomId 时旧管线会选第一个正在录制的房间。 */
export function markLegacyHighlight(roomId: number | null = null): Promise<LegacyHighlightEvent> {
  const query = roomId != null ? `?roomId=${encodeURIComponent(String(roomId))}` : '';
  return requestJson<LegacyHighlightEvent>(`/api/mark${query}`, { method: 'POST' });
}

/** 自动切片策略（弹幕热度）。用户口径：前 10 分钟基准、140% 触发、持续后推、120% 释放、前 200s + 后 200s。 */
export interface AutoClipPolicy {
  enabled: boolean;
  cooldownSeconds: number;
  baselineSeconds: number;
  comparisonSeconds: number;
  triggerRatio: number;
  releaseRatio: number;
  preRollSeconds: number;
  postRollSeconds: number;
  maxExtensionSeconds: number;
  /** A2：速率跌破释放线后需连续保持的秒数。 */
  releaseSeconds: number;
  /** A3：基准重锚间隔秒数。 */
  rebaseSeconds: number;
  manualPreRollSeconds: number;
  manualPostRollSeconds: number;
  danmakuRecordLimit: number;
}

export interface AutoClipHeat {
  messageRate: number;
  baseline: number;
  triggerLine: number;
  releaseLine: number;
  active: boolean;
  inBaseline: boolean;
  reason: string;
}

export interface AutoClipItem {
  id: string;
  eventId: string;
  path: string;
  createdAt: string;
  source?: string | null;
  triggerTime?: number | null;
  start?: number | null;
  end?: number | null;
  state?: string | null;
  closed?: boolean;
  partial?: boolean;
  score?: number;
}

export interface AutoClipPayload {
  enabled: boolean;
  policy: AutoClipPolicy;
  heat: AutoClipHeat;
  clips: AutoClipItem[];
}

export function getAutoClip(): Promise<AutoClipPayload> {
  return requestJson<AutoClipPayload>('/api/live/auto-clip');
}

export function saveAutoClipPolicy(
  patch: Partial<Omit<AutoClipPolicy, 'enabled'>> & { enabled?: boolean },
): Promise<{ saved: boolean; policy: AutoClipPolicy }> {
  return requestJson<{ saved: boolean; policy: AutoClipPolicy }>('/api/live/auto-clip', {
    method: 'POST',
    body: JSON.stringify(patch),
  });
}

/** 监控屏弹幕 SSE（/api/live/danmaku/stream）里的一条旧管线弹幕。 */
export interface DanmakuStreamEvent {
  id: string;
  sessionId: string;
  /** 相对录制起点的秒数。 */
  sessionTime: number;
  timestamp: string;
  type: string;
  uid: number;
  userName: string;
  text: string;
  weight: number;
}

/** SSE 的 hello 事件：房间当前监控状态与会话切换信号。 */
export interface DanmakuStreamHello {
  roomId: number;
  sessionId: string | null;
  monitoring: boolean;
  title: string;
  streamer: string;
}

/**
 * 旧管线弹幕流地址；用 EventSource 订阅，事件名是 `hello` 与 `danmaku`。
 * 该端点只读现有弹幕缓冲，房间未在监控时只会收到 hello 与心跳注释。
 */
export function legacyDanmakuStreamUrl(roomId: number): string {
  return `${apiBase}/api/live/danmaku/stream?roomId=${encodeURIComponent(String(roomId))}`;
}

/**
 * WebView2 的单窗口宿主里，`WebResourceRequested` 自定义响应会缓冲到 EOF，
 * 无限 SSE 不能增量交付；监控屏改用这个只读快照轮询，按 id 去重。
 */
export function getLegacyDanmaku(sessionId: string): Promise<DanmakuStreamEvent[]> {
  return requestJson<DanmakuStreamEvent[]>(
    `/api/danmaku?sessionId=${encodeURIComponent(sessionId)}`,
  );
}

/** GET /api/highlights：旧管线已产生的高光（切片）事件，用于监控屏统计每房切片数。 */
export function getLegacyHighlights(): Promise<LegacyHighlightEvent[]> {
  return requestJson<LegacyHighlightEvent[]>('/api/highlights');
}

export interface ResolvedLiveUser { uid:number; userName:string; roomId:number; canonicalRoomId:number; isLive:boolean; title:string }
/** Lookup only: this endpoint does not register a room or start recording. */
export function resolveBilibiliUid(uid:string,signal?:AbortSignal):Promise<ResolvedLiveUser>{
  return requestJson<ResolvedLiveUser>(`/api/resolve-uid/${encodeURIComponent(uid)}`,{signal});
}

export function setRoomRecording(roomId:number,enabled:boolean):Promise<unknown>{
  return requestJson(`/api/rooms/${roomId}/recording`,{method:'POST',body:JSON.stringify({enabled})});
}
export function getRecordingDirectory():Promise<{recordingDirectory:string}>{
  return requestJson('/api/settings');
}
export function saveRecordingDirectory(recordingDirectory:string):Promise<unknown>{
  return requestJson('/api/settings',{method:'PATCH',body:JSON.stringify({recordingDirectory})});
}

export function setRoomCache(roomId:number,enabled:boolean):Promise<unknown>{
 return requestJson(`/api/rooms/${roomId}/cache`,{method:'POST',body:JSON.stringify({enabled})});
}
