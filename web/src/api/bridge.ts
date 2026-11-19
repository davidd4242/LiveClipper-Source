/** C1：FilmCraft 精修往返的最小前端契约（与 `BridgeEndpoints` 对齐）。 */
import type { EditorProject } from './types';


export interface BridgeFcprojResult {
  fcprojPath: string;
  workDir: string;
  editorProjectPath: string;
  clipId: string;
  warnings: string[];
}

export interface BridgeOpenResult {
  launched: boolean;
  pid: number;
  guiPath: string;
  fcprojPath: string;
  mode: 'gui' | 'control';
  message: string;
}

/** 结构化错误（404 FilmCraftNotFound / 409 VersionMismatch / 422 MappingError …）。 */
export class BridgeApiError extends Error {
  readonly status: number;
  readonly errorClass: string;
  readonly detail: string | null;

  constructor(status: number, errorClass: string, message: string, detail: string | null) {
    super(message);
    this.name = 'BridgeApiError';
    this.status = status;
    this.errorClass = errorClass;
    this.detail = detail;
  }
}

async function bridgeRequest<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(`${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const text = await response.text();
  let payload: unknown = null;
  if (text) {
    try {
      payload = JSON.parse(text) as unknown;
    } catch {
      payload = null;
    }
  }
  if (!response.ok) {
    const error = (payload as { error?: { class?: string; message?: string; detail?: string } } | null)?.error;
    throw new BridgeApiError(
      response.status,
      error?.class ?? 'HttpError',
      error?.message ?? `请求失败（HTTP ${response.status}）`,
      error?.detail ?? null,
    );
  }
  return payload as T;
}

/**
 * 生成临时 FilmCraft 工程（写入 `{storage}/bridge-roundtrip/{clipId}/{ts}/`）。
 * `project` 为编辑器当前即时工程：服务端优先用它（先校验再快照），不回落到防抖前的旧工程。
 */
export function generateFcproj(
  clipId: string,
  variant?: string,
  project?: EditorProject | null,
): Promise<BridgeFcprojResult> {
  return bridgeRequest<BridgeFcprojResult>('/api/bridge/fcproj', {
    clipId,
    variant: variant ?? null,
    project: project ?? null,
  });
}

/** 在 FilmCraft 中打开刚生成的临时工程（只有 bridge-roundtrip 下的路径允许）。 */
export function openFcproj(fcprojPath: string): Promise<BridgeOpenResult> {
  return bridgeRequest<BridgeOpenResult>('/api/bridge/open', { fcprojPath });
}
