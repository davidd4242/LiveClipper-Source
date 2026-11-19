import type { EditorPayload, EditorProject, ExportFormat } from '../../../api/types';

export type EditorFetch = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export interface EditorApiClientOptions {
  fetch?: EditorFetch;
  baseUrl?: string;
}

export interface EditorRequestOptions {
  signal?: AbortSignal;
}

export interface CommandRequestOptions extends EditorRequestOptions {
  /** The backend uses this header only when the batch has no sessionId. */
  sessionId?: string;
}

export interface EditorWriteOptions extends CommandRequestOptions {
  /** Null/omitted keeps the backend's legacy unchecked-write behavior. */
  baseRevision?: number | null;
}

export interface EditorErrorDetail {
  code?: string;
  message?: string;
  holder?: string | null;
}

export interface EditorErrorBody {
  error?: string | EditorErrorDetail;
  revision?: number;
  id?: string;
  threshold?: string | null;
  window?: string | null;
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
}

export class EditorApiError extends Error {
  readonly status: number;
  readonly code: string | null;
  readonly revision: number | null;
  readonly holder: string | null;
  readonly body: unknown;

  constructor(message: string, status = 0, body: unknown = null, code: string | null = null) {
    super(message);
    this.name = 'EditorApiError';
    this.status = status;
    this.body = body;
    const envelope = objectOf(body);
    const error = objectOf(envelope?.['error']);
    this.code = code ?? stringOf(error?.['code']);
    this.revision = numberOf(envelope?.['revision']);
    this.holder = stringOf(error?.['holder']);
  }
}

function objectOf(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

function stringOf(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null;
}

function numberOf(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function isAbort(error: unknown, signal: AbortSignal | null | undefined): boolean {
  return signal?.aborted === true || (error instanceof Error && error.name === 'AbortError');
}

function errorMessage(body: unknown, text: string, status: number): string {
  const envelope = objectOf(body);
  const error = envelope?.['error'];
  return stringOf(objectOf(error)?.['message'])
    ?? stringOf(error)
    ?? stringOf(envelope?.['detail'])
    ?? stringOf(envelope?.['title'])
    ?? stringOf(body)
    ?? (body === null && text.trim() ? text.trim().slice(0, 240) : `请求失败（HTTP ${status}）`);
}

export type EditorJsonRequest = <T>(path: string, init?: RequestInit) => Promise<T>;

/** Shared here because this batch cannot change the existing private HTTP helpers. */
export function createEditorJsonRequest(options: EditorApiClientOptions = {}): EditorJsonRequest {
  let baseUrl = options.baseUrl ?? import.meta.env?.VITE_API_BASE ?? '';
  while (baseUrl.endsWith('/')) baseUrl = baseUrl.slice(0, -1);
  const fetcher: EditorFetch = options.fetch ?? ((input, init) => globalThis.fetch(input, init));

  return async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const headers = new Headers(init.headers);
    if (!headers.has('Accept')) headers.set('Accept', 'application/json');
    if (init.body !== undefined && init.body !== null && !headers.has('Content-Type')) {
      headers.set('Content-Type', 'application/json');
    }
    let response: Response;
    try {
      response = await fetcher(`${baseUrl}${path}`, { ...init, headers });
    } catch (error) {
      if (isAbort(error, init.signal)) throw error;
      const detail = error instanceof Error ? error.message : String(error);
      throw new EditorApiError(`无法连接 LiveClipper 后端：${detail}`, 0, null, 'network-error');
    }

    let text: string;
    try {
      text = await response.text();
    } catch (error) {
      if (isAbort(error, init.signal)) throw error;
      const detail = error instanceof Error ? error.message : String(error);
      throw new EditorApiError(`无法读取 LiveClipper 后端响应：${detail}`, response.status, null, 'response-read-error');
    }
    let body: unknown = null;
    let parsed = false;
    if (text.trim()) {
      try {
        body = JSON.parse(text) as unknown;
        parsed = true;
      } catch {
        // Keep the original text for gateways that return non-JSON errors.
      }
    }
    if (!response.ok) throw new EditorApiError(errorMessage(body, text, response.status), response.status, body);
    if (!parsed) throw new EditorApiError('LiveClipper 后端返回了无效 JSON', response.status, null, 'invalid-response');
    return body as T;
  };
}

export interface MoveItemRequest {
  key: string;
  track?: number;
  start?: number;
}

export interface ItemPropsRequest {
  key: string;
  volumeDb?: number;
  opacity?: number;
  zoom?: number;
  rotation?: number;
  positionX?: number;
  positionY?: number;
  disabled?: boolean;
  fadeIn?: number;
  fadeOut?: number;
}

export interface TrackStateRequest {
  mute?: readonly number[];
  solo?: readonly number[];
  hide?: readonly number[];
  lock?: readonly number[];
  opacity?: readonly number[];
}

export interface RenderSettingsRequest {
  resolution?: number;
  fps?: number;
  bitrateKbps?: number;
  format?: ExportFormat;
}

export type EditorCommand =
  | { op: 'setRange'; args: { start: number; end: number } }
  | { op: 'moveItems'; args: { moves: readonly MoveItemRequest[] } }
  | { op: 'trimItem'; args: { key: string; start?: number; end?: number; duration?: number } }
  | { op: 'splitItem'; args: { key: string; at: number } }
  | { op: 'removeItems'; args: { keys: readonly string[] } }
  | { op: 'setTrackState'; args: TrackStateRequest }
  | { op: 'setRenderSettings'; args: RenderSettingsRequest }
  | { op: 'setItemProps'; args: { items: readonly ItemPropsRequest[] } }
  | { op: 'replaceProject'; args: { project: EditorProject } };

export interface CommandBatchRequest {
  baseRevision?: number | null;
  commands: readonly EditorCommand[];
  sessionId?: string | null;
}

export interface AdjustedField {
  key: string;
  field: string;
  value: number;
  why: string;
}

export interface CommittedRecord {
  opId: string;
  command: EditorCommand['op'];
  changed: string[];
  created: string[];
  removed: string[];
  adjusted: AdjustedField[];
}

export interface CommandBatchResponse {
  revision: number;
  results: CommittedRecord[];
}

export interface CommandSessionResponse {
  sessionId: string;
  checkpointRevision: number;
}

export interface EndCommandSessionResponse {
  ended: true;
  checkpointRevision: number;
  revision: number;
}

export interface SaveEditorResponse {
  saved: true;
  revision: number;
}

export interface RestoreEditorResponse {
  restored: true;
  revision: number;
}

export interface EditorRevisionPayload extends EditorPayload {
  revision: number;
}

export interface CommandSessionClient {
  getEditor(clipId: string, options?: EditorRequestOptions): Promise<EditorRevisionPayload>;
  commands(clipId: string, batch: CommandBatchRequest, options?: CommandRequestOptions): Promise<CommandBatchResponse>;
  beginSession(clipId: string, options?: EditorRequestOptions): Promise<CommandSessionResponse>;
  endSession(clipId: string, sessionId: string, options?: EditorRequestOptions): Promise<EndCommandSessionResponse>;
  saveEditor(clipId: string, project: EditorProject, options?: EditorWriteOptions): Promise<SaveEditorResponse>;
  undo(clipId: string, options?: EditorWriteOptions): Promise<RestoreEditorResponse>;
  redo(clipId: string, options?: EditorWriteOptions): Promise<RestoreEditorResponse>;
}

function clipPath(clipId: string): string {
  return `/api/studio/clips/${encodeURIComponent(clipId)}`;
}

function sessionHeaders(options: CommandRequestOptions): Headers {
  const headers = new Headers();
  if (options.sessionId !== undefined) headers.set('X-Session-Id', options.sessionId);
  return headers;
}

function writeHeaders(options: EditorWriteOptions): Headers {
  const headers = sessionHeaders(options);
  if (options.baseRevision !== undefined && options.baseRevision !== null) {
    headers.set('X-Base-Revision', String(options.baseRevision));
  }
  return headers;
}

export function createCommandSessionClient(options: EditorApiClientOptions = {}): CommandSessionClient {
  const request = createEditorJsonRequest(options);
  return {
    getEditor(clipId, requestOptions = {}) {
      return request<EditorRevisionPayload>(`${clipPath(clipId)}/editor`, {
        ...requestOptions,
        method: 'GET',
      });
    },
    commands(clipId, batch, requestOptions = {}) {
      return request<CommandBatchResponse>(`${clipPath(clipId)}/commands`, {
        ...(requestOptions.signal ? { signal: requestOptions.signal } : {}),
        method: 'POST',
        headers: sessionHeaders(requestOptions),
        body: JSON.stringify(batch),
      });
    },
    beginSession(clipId, requestOptions = {}) {
      return request<CommandSessionResponse>(`${clipPath(clipId)}/sessions`, {
        ...requestOptions,
        method: 'POST',
      });
    },
    endSession(clipId, sessionId, requestOptions = {}) {
      return request<EndCommandSessionResponse>(`${clipPath(clipId)}/sessions/${encodeURIComponent(sessionId)}/end`, {
        ...requestOptions,
        method: 'POST',
      });
    },
    saveEditor(clipId, project, requestOptions = {}) {
      return request<SaveEditorResponse>(`${clipPath(clipId)}/editor`, {
        ...(requestOptions.signal ? { signal: requestOptions.signal } : {}),
        method: 'PUT',
        headers: writeHeaders(requestOptions),
        body: JSON.stringify(project),
      });
    },
    undo(clipId, requestOptions = {}) {
      return request<RestoreEditorResponse>(`${clipPath(clipId)}/undo`, {
        ...(requestOptions.signal ? { signal: requestOptions.signal } : {}),
        method: 'POST',
        headers: writeHeaders(requestOptions),
      });
    },
    redo(clipId, requestOptions = {}) {
      return request<RestoreEditorResponse>(`${clipPath(clipId)}/redo`, {
        ...(requestOptions.signal ? { signal: requestOptions.signal } : {}),
        method: 'POST',
        headers: writeHeaders(requestOptions),
      });
    },
  };
}
