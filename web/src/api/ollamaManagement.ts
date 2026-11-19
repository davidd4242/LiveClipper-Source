import { ApiError } from './client';

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');
export const OLLAMA_PACKAGE_VERSION = '0.35.0';
export const OLLAMA_PACKAGE_SIZE = 1461196158;

export interface OllamaConnection {
  ready: boolean;
  installed: boolean;
  executable: string | null;
  version: string | null;
  detail: string;
}

export interface OllamaManagedIdentity {
  status: string;
  reason: string;
  version: string | null;
  executable: string | null;
  uninstallPreviewDirectory: string | null;
  verifiedFiles: string[];
}

export interface OllamaInstallation {
  state: string;
  stage: string;
  version: string;
  taskId: string | null;
  privateRoot: string | null;
  bytesReceived: number | null;
  totalBytes: number | null;
  packagePath: string | null;
  stagingDirectory: string | null;
  reason: string | null;
  cancellationRequested: boolean;
  managedStatus: string | null;
}

export type OllamaDaemonAction = 'start' | 'stop' | 'private' | 'original' | 'uninstall';

export interface OllamaDaemon {
  state: string;
  ready: boolean;
  owned: boolean;
  selected: boolean;
  endpoint: string | null;
  processId: number | null;
  privateRoot: string | null;
  retiredDirectory: string | null;
  reason: string | null;
}

export interface OllamaManagementStatus {
  daemon?: OllamaDaemon;
  connection: OllamaConnection | null;
  connectionKnown: boolean;
  connectionError: string | null;
  managed: OllamaManagedIdentity;
  installation: OllamaInstallation;
  paths: {
    runtimeBaseDirectory: string | null;
    defaultBaseDirectory: string;
    privateRoot: string | null;
    programDirectory: string | null;
    modelsDirectory: string | null;
  };
  package: { version: string; url: string; size: number; sha256: string };
  managementActionsImplemented: boolean;
  installationActionsImplemented: boolean;
  daemonActionsImplemented: boolean;
  daemonOwnershipVerified: boolean;
  recursiveDeleteAllowed: boolean;
}

export interface OllamaInstallationResult {
  statusCode: number;
  outcome: string;
  installation: OllamaInstallation;
}

export class OllamaManagementRequestError extends ApiError {
  readonly result: OllamaInstallationResult | null;

  constructor(status: number, result: OllamaInstallationResult | null) {
    super(`请求未被接受（HTTP ${status}）`, status, { code: result?.outcome });
    this.result = result;
  }
}

function record(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

function nullableString(value: unknown): boolean {
  return value === null || typeof value === 'string';
}

function nullableBytes(value: unknown): boolean {
  return value === null || (typeof value === 'number' && Number.isFinite(value) && value >= 0);
}

function isInstallation(value: unknown): value is OllamaInstallation {
  const body = record(value);
  return body !== null && typeof body.state === 'string' && typeof body.stage === 'string'
    && typeof body.version === 'string' && typeof body.cancellationRequested === 'boolean'
    && ['taskId', 'privateRoot', 'packagePath', 'stagingDirectory', 'reason', 'managedStatus']
      .every((key) => nullableString(body[key]))
    && nullableBytes(body.bytesReceived) && nullableBytes(body.totalBytes);
}

function isResult(value: unknown): value is OllamaInstallationResult {
  const body = record(value);
  return body !== null && typeof body.statusCode === 'number' && typeof body.outcome === 'string'
    && isInstallation(body.installation);
}

async function request(path: string, init?: RequestInit): Promise<{ response: Response; body: unknown }> {
  const response = await fetch(`${apiBase}/api/ollama/runtime${path}`, {
    ...init,
    headers: { Accept: 'application/json', ...(init?.body ? { 'Content-Type': 'application/json' } : {}) },
  });
  const body: unknown = await response.json().catch(() => null);
  return { response, body };
}

export async function getOllamaManagementStatus(signal?: AbortSignal): Promise<OllamaManagementStatus> {
  const { response, body } = await request('', { signal, cache: 'no-store' });
  if (!response.ok) throw new ApiError(`状态读取失败（HTTP ${response.status}）`, response.status);
  const value = record(body);
  const managed = record(value?.managed);
  const paths = record(value?.paths);
  const packageInfo = record(value?.package);
  const connection = record(value?.connection);
  if (!value || !managed || !paths || !packageInfo || !isInstallation(value.installation)
    || typeof value.connectionKnown !== 'boolean' || !nullableString(value.connectionError)
    || (value.connection !== null && (!connection || typeof connection.ready !== 'boolean'
      || typeof connection.installed !== 'boolean' || !nullableString(connection.version)
      || !nullableString(connection.executable) || typeof connection.detail !== 'string'))
    || typeof managed.status !== 'string' || typeof managed.reason !== 'string'
    || !['version', 'executable', 'uninstallPreviewDirectory'].every((key) => nullableString(managed[key]))
    || !Array.isArray(managed.verifiedFiles) || !managed.verifiedFiles.every((file) => typeof file === 'string')
    || !['runtimeBaseDirectory', 'privateRoot', 'programDirectory', 'modelsDirectory'].every((key) => nullableString(paths[key]))
    || typeof paths.defaultBaseDirectory !== 'string'
    || typeof packageInfo.version !== 'string' || typeof packageInfo.size !== 'number'
    || !Number.isFinite(packageInfo.size) || typeof packageInfo.url !== 'string' || typeof packageInfo.sha256 !== 'string'
    || !['managementActionsImplemented', 'installationActionsImplemented', 'daemonActionsImplemented',
      'daemonOwnershipVerified', 'recursiveDeleteAllowed'].every((key) => typeof value[key] === 'boolean')) {
    throw new ApiError('状态响应无法识别，请刷新后重试');
  }
  return body as OllamaManagementStatus;
}

async function submit(path: string, body?: object): Promise<OllamaInstallationResult> {
  // Mutation requests deliberately do not share the refresh AbortController.
  const result = await request(path, { method: 'POST', ...(body ? { body: JSON.stringify(body) } : {}) });
  if (!result.response.ok) {
    throw new OllamaManagementRequestError(result.response.status, isResult(result.body) ? result.body : null);
  }
  if (!isResult(result.body)) throw new ApiError('请求已发送，但响应无法识别；请刷新状态后再操作');
  return result.body;
}

export function installManagedOllama(expectedPrivateRoot: string): Promise<OllamaInstallationResult> {
  return submit('/install', { confirmed: true, expectedVersion: OLLAMA_PACKAGE_VERSION, expectedPrivateRoot });
}

export function cancelManagedOllamaInstallation(): Promise<OllamaInstallationResult> {
  return submit('/install/cancel');
}

export function isOllamaInstallationActive(state: string | undefined): boolean {
  return state === 'downloading' || state === 'installing' || state === 'verifying' || state === 'cancelling';
}

export async function manageOllamaDaemon(action: OllamaDaemonAction, expectedPrivateRoot?: string): Promise<void> {
  const selection = action === 'private' || action === 'original';
  const { response, body } = await request(selection ? '/select' : `/${action}`, {
    method: 'POST',
    body: JSON.stringify({ confirmed: true,
      ...(selection ? { target: action } : {}),
      ...(action === 'uninstall' ? { expectedVersion: OLLAMA_PACKAGE_VERSION } : {}),
      ...(action === 'start' || action === 'uninstall' ? { expectedPrivateRoot } : {}),
    }),
  });
  if (!response.ok) {
    const outcome = record(body)?.outcome;
    throw new ApiError(typeof outcome === 'string' ? outcome : `HTTP ${response.status}`, response.status);
  }
}

export async function saveOllamaRuntimeLocation(baseDirectory: string | null, expectedPrivateRoot: string): Promise<void> {
  const { response, body } = await request('/location', {
    method: 'POST', body: JSON.stringify({ confirmed: true, baseDirectory, expectedPrivateRoot }),
  });
  const outcome = record(body)?.outcome;
  if (!response.ok || outcome !== 'location-saved') {
    throw new ApiError(typeof outcome === 'string' ? outcome : `HTTP ${response.status}`, response.status);
  }
}
