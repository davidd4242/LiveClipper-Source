import type {
  AiAnalyzeResponse,
  AutoPlanReport,
  ClipDto,
  EditorPayload,
  EditorProject,
  EditorSequencePayload,
  EditorSequenceSummary,
  HealthResponse,
  ProjectRevisionInfo,
  JobDto,
  LiveResolution,
  LiveRoomStatus,
  LiveRoomsResponse,
  LiveStorageStatus,
  RenderDto,
  StudioEvent,
} from './types';

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

/** ApiError 的可选结构化字段；旧调用 `new ApiError(message[, status])` 不受影响。 */
export interface ApiErrorDetails {
  requestSent?: boolean | null;
  /** 服务端错误码，例如 409 的 stale-revision。 */
  code?: string | null;
  /** 409 冲突时服务器当前 revision。 */
  revision?: number | null;
  /** 会话占用冲突时的持有者。 */
  holder?: string | null;
}

export class ApiError extends Error {
  readonly status: number;
  readonly requestSent: boolean | null;
  readonly code: string | null;
  readonly revision: number | null;
  readonly holder: string | null;

  constructor(message: string, status = 0, details: ApiErrorDetails = {}) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.requestSent = details.requestSent ?? null;
    this.code = details.code ?? null;
    this.revision = details.revision ?? null;
    this.holder = details.holder ?? null;
  }
}

function apiErrorText(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null;
}

function apiErrorNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function apiErrorRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

/**
 * 把非 2xx 响应转成 ApiError。错误体可能是 `{ error: "文本" }`，也可能是信封
 * `{ error: { code, message, holder }, revision }`（409 冲突）；后者以前会被拼成 "[object Object]"。
 * 正文只读一次：先 json() 失败再 text() 时正文已被消费，拿不到纯文本错误。
 */
async function apiErrorFromResponse(response: Response): Promise<ApiError> {
  const fallback = `请求失败（HTTP ${response.status}）`;
  const text = await response.text().catch(() => '');
  let body: unknown = null;
  if (text.trim()) {
    try {
      body = JSON.parse(text);
    } catch {
      body = null;
    }
  }
  const record = apiErrorRecord(body);
  const rawError = record ? record['error'] : undefined;
  const envelope = apiErrorRecord(rawError);
  const message =
    apiErrorText(rawError)
    ?? apiErrorText(envelope ? envelope['message'] : undefined)
    ?? (response.status === 422 && record?.['code'] === 'editor_render_unsupported' ? apiErrorText(record['message']) : null)
    ?? (record === null && text.trim() ? text.trim().slice(0, 240) : null)
    ?? fallback;
  return new ApiError(message, response.status, {
    requestSent: typeof record?.['requestSent'] === 'boolean' ? record['requestSent'] as boolean : null,
    code: apiErrorText(envelope ? envelope['code'] : undefined) ?? apiErrorText(record ? record['code'] : undefined),
    revision: apiErrorNumber(record ? record['revision'] : undefined)
      ?? apiErrorNumber(envelope ? envelope['revision'] : undefined),
    holder: apiErrorText(envelope ? envelope['holder'] : undefined) ?? apiErrorText(record ? record['holder'] : undefined),
  });
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
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
    throw new ApiError(`无法连接 LiveClipper 后端：${detail}`);
  }

  if (!response.ok) throw await apiErrorFromResponse(response);

  return (await response.json()) as T;
}

export function getHealth(): Promise<HealthResponse> {
  return request<HealthResponse>('/api/health');
}

export function getClips(): Promise<ClipDto[]> {
  return request<ClipDto[]>('/api/clips');
}

export function getEditor(clipId: string): Promise<EditorPayload> {
  return request<EditorPayload>(`/api/studio/clips/${encodeURIComponent(clipId)}/editor`);
}

export interface SaveEditorResult {
  saved: boolean;
  /** 本次写入后服务器的 revision，作为下一次 PUT 的 X-Base-Revision。 */
  revision?: number;
}

/**
 * 保存编辑工程。传入 baseRevision 时附带 `X-Base-Revision`：服务器 revision 已前进（其他写入）时
 * 返回 409，抛出的 ApiError 带 code（如 'stale-revision'）与服务器当前 revision。
 * 不传 baseRevision 保持旧行为（不做过期检查），兼容既有调用方。
 */
export function saveEditor(
  clipId: string,
  project: EditorProject,
  baseRevision?: number | null,
  epoch?: string,
): Promise<SaveEditorResult> {
  const init: RequestInit = { method: 'PUT', body: JSON.stringify(project) };
  if (typeof baseRevision === 'number' && Number.isFinite(baseRevision)) {
    init.headers = { 'X-Base-Revision': String(baseRevision) };
  }
  if (epoch) init.headers = { ...init.headers, 'X-Editor-Epoch': epoch };
  return request<SaveEditorResult>(`/api/studio/clips/${encodeURIComponent(clipId)}/editor`, init);
}

export function getEditorSequences(clipId: string): Promise<EditorSequenceSummary[]> {
  return request<EditorSequenceSummary[]>(`/api/studio/clips/${encodeURIComponent(clipId)}/variants`);
}

export function createEditorSequence(
  clipId: string,
  name: string,
  project: EditorProject,
): Promise<{ id: string }> {
  return request<{ id: string }>(`/api/studio/clips/${encodeURIComponent(clipId)}/variants`, {
    method: 'POST',
    body: JSON.stringify({ name, project }),
  });
}

export function getEditorSequence(variantId: string): Promise<EditorSequencePayload> {
  return request<EditorSequencePayload>(`/api/studio/variants/${encodeURIComponent(variantId)}`);
}

export function deleteEditorSequence(variantId: string): Promise<{ deleted: boolean }> {
  return request<{ deleted: boolean }>(`/api/studio/variants/${encodeURIComponent(variantId)}`, {
    method: 'DELETE',
  });
}

export function getProjectRevisions(clipId: string): Promise<ProjectRevisionInfo[]> {
  return request<ProjectRevisionInfo[]>(`/api/studio/clips/${encodeURIComponent(clipId)}/project-backups`);
}

export function getProjectRevision(clipId: string, fileName: string): Promise<EditorProject> {
  return request<EditorProject>(
    `/api/studio/clips/${encodeURIComponent(clipId)}/project-backups/${encodeURIComponent(fileName)}`,
  );
}

export function analyzeClip(
  clipId: string,
  connection: { provider: 'ollama' | 'compatible'; endpoint: string; model: string },
): Promise<AiAnalyzeResponse> {
  return request<AiAnalyzeResponse>('/api/ai/analyze', {
    method: 'POST',
    body: JSON.stringify({ clipId, connection }),
  });
}

export function extractEvents(clipId: string): Promise<StudioEvent[]> {
  return request<StudioEvent[]>('/api/studio/events/extract', {
    method: 'POST',
    body: JSON.stringify({ clipId }),
  });
}

export function generateAutoPlan(clipId: string): Promise<AutoPlanReport> {
  return request<AutoPlanReport>(`/api/studio/clips/${encodeURIComponent(clipId)}/auto-plan`, {
    method: 'POST',
  });
}

export function getVideoUrl(clipId: string): string {
  return `${apiBase}/api/clips/${encodeURIComponent(clipId)}/video`;
}

export function getPreviewAudioUrl(clipId: string): string {
  return `${apiBase}/api/studio/clips/${encodeURIComponent(clipId)}/preview-audio.wav`;
}

export function getWaveformUrl(clipId: string): string {
  return `${apiBase}/api/studio/clips/${encodeURIComponent(clipId)}/waveform?width=900&height=96`;
}

type RenderQueueReceipt = Omit<RenderDto, 'status' | 'error'>;

/** Omitted preconditions retain legacy render behavior; each supplied header is enforced by the server. */
export function queueRender(
  clipId: string,
  project: EditorProject,
  baseRevision?: number,
  epoch?: string,
): Promise<RenderQueueReceipt> {
  return request<RenderQueueReceipt>(`/api/studio/clips/${encodeURIComponent(clipId)}/render`, {
    method: 'POST',
    body: JSON.stringify(project),
    headers: {
      ...(baseRevision !== undefined ? { 'X-Base-Revision': String(baseRevision) } : {}),
      ...(epoch !== undefined ? { 'X-Editor-Epoch': epoch } : {}),
    },
  });
}

export function getRenders(): Promise<RenderDto[]> {
  return request<RenderDto[]>('/api/studio/renders');
}

export function getJobs(): Promise<JobDto[]> {
  return request<JobDto[]>('/api/jobs');
}

export function controlRender(id: string, action: 'cancel' | 'pause' | 'resume' | 'retry'): Promise<{ ok: boolean }> {
  return request<{ ok: boolean }>(`/api/studio/renders/${encodeURIComponent(id)}/${action}`, {
    method: 'POST',
  });
}

export function getRenderFileUrl(id: string): string {
  return `${apiBase}/api/studio/renders/${encodeURIComponent(id)}/file`;
}

export function getLiveRooms(): Promise<LiveRoomsResponse> {
  return request<LiveRoomsResponse>('/api/live/rooms');
}

export function resolveLiveInput(input: string): Promise<LiveResolution> {
  return request<LiveResolution>(`/api/live/resolve?input=${encodeURIComponent(input)}`);
}

export function startLiveRoom(roomId: number): Promise<LiveRoomStatus> {
  return request<LiveRoomStatus>(`/api/live/rooms/${roomId}/start`, { method: 'POST' });
}

export function stopLiveRoom(roomId: number): Promise<{ stopped: boolean; roomId: number }> {
  return request<{ stopped: boolean; roomId: number }>(`/api/live/rooms/${roomId}/stop`, {
    method: 'POST',
  });
}

export function removeLiveRoom(roomId: number): Promise<{ removed: boolean; stopped: boolean }> {
  return request<{ removed: boolean; stopped: boolean }>(`/api/live/rooms/${roomId}`, {
    method: 'DELETE',
  });
}

export function getLiveStorage(): Promise<LiveStorageStatus> {
  return request<LiveStorageStatus>('/api/live/storage');
}

export function cleanupLiveStorage(): Promise<LiveStorageStatus> {
  return request<LiveStorageStatus>('/api/live/storage/cleanup', { method: 'POST' });
}

export interface CopyEditorResult { clipId: string; name: string; revision: number }
export function copyEditor(clipId: string, name: string, project: EditorProject, baseRevision: number, epoch: string) {
  return request<CopyEditorResult>(`/api/studio/clips/${encodeURIComponent(clipId)}/copy`, {
    method: 'POST', headers: { 'X-Base-Revision': String(baseRevision), 'X-Editor-Epoch': epoch },
    body: JSON.stringify({ name, project }),
  });
}

export interface EditorPackageResult { downloadUrl: string; fileName: string }
export function packageEditor(clipId: string, name: string, project: EditorProject, baseRevision: number, epoch: string) {
  return request<EditorPackageResult>(`/api/studio/clips/${encodeURIComponent(clipId)}/project-package`, {
    method: 'POST', headers: { 'X-Base-Revision': String(baseRevision), 'X-Editor-Epoch': epoch },
    body: JSON.stringify({ name, project }),
  });
}
export interface PackageUploadProgress { uploaded: number; total: number; phase: 'uploading' | 'importing' }
export async function importEditorPackage(file: File, options: {
  signal?: AbortSignal; onProgress?: (progress: PackageUploadProgress) => void;
} = {}): Promise<CopyEditorResult> {
  const root = `${apiBase}/api/studio/project-packages/uploads`;
  const send = async <T,>(url: string, init: RequestInit): Promise<T> => {
    const response = await fetch(url, init);
    if (!response.ok) throw await apiErrorFromResponse(response);
    return await response.json() as T;
  };
  options.signal?.throwIfAborted();
  const upload = await send<{ uploadId: string; offset: number; chunkBytes: number }>(root, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ totalBytes: file.size }), signal: options.signal,
  });
  const url = `${root}/${encodeURIComponent(upload.uploadId)}`;
  let complete = false;
  try {
    let offset = upload.offset;
    options.onProgress?.({ uploaded: offset, total: file.size, phase: 'uploading' });
    while (offset < file.size) {
      options.signal?.throwIfAborted();
      const end = Math.min(offset + 4 * 1024 * 1024, file.size);
      // Materialize only one bounded slice. WebView2 loses disk-backed File multipart parts.
      const body = await file.slice(offset, end).arrayBuffer();
      options.signal?.throwIfAborted();
      const next = await send<{ offset: number }>(`${url}/chunks?offset=${offset}`, {
        method: 'POST', headers: { 'Content-Type': 'application/octet-stream' }, body, signal: options.signal,
      });
      if (next.offset !== end) throw new Error('工程包上传偏移不一致，已停止。');
      offset = next.offset;
      options.onProgress?.({ uploaded: offset, total: file.size, phase: 'uploading' });
    }
    options.signal?.throwIfAborted();
    options.onProgress?.({ uploaded: offset, total: file.size, phase: 'importing' });
    const result = await send<CopyEditorResult>(`${url}/complete`, { method: 'POST', signal: options.signal });
    complete = true;
    return result;
  } finally {
    // Cleanup is best effort and does not replace the upload/import error. Never retry data or completion.
    if (!complete) {
      try { await fetch(url, { method: 'DELETE' }); } catch { /* Original failure remains the result. */ }
    }
  }
}

export interface PremiereExportResult { directory: string; xmlPath: string; srtPath: string; warnings: string[] }
export function exportPremiere(clipId: string, name: string, project: EditorProject, baseRevision: number, epoch: string, normalizeVariableFrameRate = false) {
  return request<PremiereExportResult>(`/api/studio/clips/${encodeURIComponent(clipId)}/premiere-export`, {
    method: 'POST', headers: { 'X-Base-Revision': String(baseRevision), 'X-Editor-Epoch': epoch },
    body: JSON.stringify({ name, project, normalizeVariableFrameRate }),
  });
}

export interface JianyingExportResult { directory: string; draftPath: string; manifestPath: string; warnings: string[] }
export function exportJianying(clipId: string, name: string, project: EditorProject, baseRevision: number, epoch: string) {
  return request<JianyingExportResult>(`/api/studio/clips/${encodeURIComponent(clipId)}/jianying-export`, {
    method: 'POST', headers: { 'X-Base-Revision': String(baseRevision), 'X-Editor-Epoch': epoch },
    body: JSON.stringify({ name, project }),
  });
}

export function detectSilencePreview(clipId: string, body: {start:number;end:number;noiseDb:number;minSeconds:number}, signal: AbortSignal) {
  return request<import('../features/editor-next/silencePreviewController').SilencePreviewResult>(`/api/studio/clips/${encodeURIComponent(clipId)}/silence-preview`, {method:'POST',body:JSON.stringify(body),signal});
}
export function verifySilencePreview(clipId: string, sourceSignature: string, signal: AbortSignal) {
  return request<unknown>(`/api/studio/clips/${encodeURIComponent(clipId)}/silence-preview/verify`, {method:'POST',body:JSON.stringify({sourceSignature}),signal});
}

export function readMultiClipPreview(clipId:string,project:EditorProject,signal:AbortSignal) {
 return request<import('../features/editor-next/multiClipController').MultiClipPreview>(`/api/studio/clips/${encodeURIComponent(clipId)}/roughcut-preview`,{method:'POST',body:JSON.stringify({project}),signal});
}
export function verifyMultiClipPreview(clipId:string,project:EditorProject,data:{sourceSignature:string;candidateHash:string;transcriptCacheKey:string},signal:AbortSignal) {
 return request<{valid:boolean}>(`/api/studio/clips/${encodeURIComponent(clipId)}/roughcut-preview/verify`,{method:'POST',body:JSON.stringify({project,sourceSignature:data.sourceSignature,candidateHash:data.candidateHash,transcriptCacheKey:data.transcriptCacheKey}),signal});
}

export function requestSemanticReview(clipId:string,project:EditorProject,state:import('../features/editor-next/multiClipController').MultiClipState,connection:import('../features/editor/aiAdvice').AiConnectionBody,signal:AbortSignal) {
 return request<import('../features/editor-next/multiClipController').SemanticOnlineResult>(`/api/studio/clips/${encodeURIComponent(clipId)}/semantic-review`,{method:'POST',signal,body:JSON.stringify({project,selectedCandidates:state.selectedIds.map(id=>{const c=state.items.find(item=>item.id===id)!;return {id:c.id,start:c.start,end:c.end};}),connection,sourceSignature:state.data!.sourceSignature,candidateHash:state.data!.candidateHash,transcriptCacheKey:state.data!.transcriptCacheKey,confirm:true})});
}

export function createAutomaticRoughcut(clipId:string,name:string,project:EditorProject,data:{sourceSignature:string;candidateHash:string;transcriptCacheKey:string},ranges:{start:number;end:number}[],revision:number,epoch:string,beforeRanges:{start:number;end:number}[],sceneBoundaryIndexes:number[]=[],transitionDurationSeconds=.8){
 return request<import('../features/editor-next/multiClipController').AutomaticCopyResult>(`/api/studio/clips/${encodeURIComponent(clipId)}/roughcut-copy`,{method:'POST',headers:{'X-Base-Revision':String(revision),'X-Editor-Epoch':epoch},body:JSON.stringify({name,project,sceneBoundaryIndexes,transitionDurationSeconds,sourceSignature:data.sourceSignature,candidateHash:data.candidateHash,transcriptCacheKey:data.transcriptCacheKey,ranges:ranges.map(({start,end})=>({start,end})),beforeRanges:beforeRanges.map(({start,end})=>({start,end}))})});
}

export function getAutomaticRestore(clipId:string){return request<{available:boolean}>(`/api/studio/clips/${encodeURIComponent(clipId)}/roughcut-restore`);}
export function restoreAutomaticRoughcut(clipId:string,revision:number,epoch:string){return request<{revision:number;project:EditorProject}>(`/api/studio/clips/${encodeURIComponent(clipId)}/roughcut-restore`,{method:'POST',headers:{'X-Base-Revision':String(revision),'X-Editor-Epoch':epoch}});}



const independentRoughcutPath=(projectId:string,sourceId:string)=>`/api/studio/projects/${encodeURIComponent(projectId)}/sources/${encodeURIComponent(sourceId)}`;
export function readIndependentMultiClipPreview(projectId:string,sourceId:string,project:EditorProject,signal:AbortSignal){
 return request<import('../features/editor-next/multiClipController').MultiClipPreview>(`${independentRoughcutPath(projectId,sourceId)}/roughcut-preview`,{method:'POST',body:JSON.stringify({project}),signal});
}
export function verifyIndependentMultiClipPreview(projectId:string,sourceId:string,project:EditorProject,data:{sourceSignature:string;candidateHash:string;transcriptCacheKey:string},signal:AbortSignal){
 return request<{valid:boolean}>(`${independentRoughcutPath(projectId,sourceId)}/roughcut-preview/verify`,{method:'POST',body:JSON.stringify({project,sourceSignature:data.sourceSignature,candidateHash:data.candidateHash,transcriptCacheKey:data.transcriptCacheKey}),signal});
}
export function requestIndependentSemanticReview(projectId:string,sourceId:string,project:EditorProject,state:import('../features/editor-next/multiClipController').MultiClipState,connection:import('../features/editor/aiAdvice').AiConnectionBody,signal:AbortSignal){
 return request<import('../features/editor-next/multiClipController').SemanticOnlineResult>(`${independentRoughcutPath(projectId,sourceId)}/semantic-review`,{method:'POST',signal,body:JSON.stringify({project,selectedCandidates:state.selectedIds.map(id=>{const c=state.items.find(item=>item.id===id)!;return {id:c.id,start:c.start,end:c.end};}),connection,sourceSignature:state.data!.sourceSignature,candidateHash:state.data!.candidateHash,transcriptCacheKey:state.data!.transcriptCacheKey,confirm:true})});
}
export function createIndependentAutomaticRoughcut(projectId:string,sourceId:string,name:string,project:EditorProject,data:{sourceSignature:string;candidateHash:string;transcriptCacheKey:string},ranges:{start:number;end:number}[],revision:number,epoch:string,beforeRanges:{start:number;end:number}[],sceneBoundaryIndexes:number[]=[],transitionDurationSeconds=.8){
 return request<import('../features/editor-next/multiClipController').AutomaticCopyResult>(`${independentRoughcutPath(projectId,sourceId)}/roughcut-copy`,{method:'POST',headers:{'X-Base-Revision':String(revision),'X-Editor-Epoch':epoch},body:JSON.stringify({name,project,sceneBoundaryIndexes,transitionDurationSeconds,sourceSignature:data.sourceSignature,candidateHash:data.candidateHash,transcriptCacheKey:data.transcriptCacheKey,ranges:ranges.map(({start,end})=>({start,end})),beforeRanges:beforeRanges.map(({start,end})=>({start,end}))})});
}

export function analyzeIndependentSource(projectId:string,sourceId:string,project:EditorProject,connection:import('../features/editor/aiAdvice').AiConnectionBody,revision:number,epoch:string){
 return request<AiAnalyzeResponse>(`${independentRoughcutPath(projectId,sourceId)}/analyze`,{method:'POST',headers:{'X-Base-Revision':String(revision),'X-Editor-Epoch':epoch},body:JSON.stringify({project,connection})});
}

export function detectReviewPauses(sourceId:string,project:EditorProject,ranges:{start:number;end:number}[],parameters:import('../features/editor-next/pauseReviewPlanner').PauseParameters,signal:AbortSignal,projectId?:string){
 const root=projectId?independentRoughcutPath(projectId,sourceId):`/api/studio/clips/${encodeURIComponent(sourceId)}`;
 return request<import('../features/editor-next/pauseReviewPlanner').PausePreview>(`${root}/silence-preview`,{method:'POST',body:JSON.stringify({start:Math.min(...ranges.map(r=>r.start)),end:Math.max(...ranges.map(r=>r.end)),...parameters,project}),signal});
}
export function verifyReviewPauses(sourceId:string,project:EditorProject,preview:import('../features/editor-next/pauseReviewPlanner').PausePreview,signal:AbortSignal,projectId?:string){
 const root=projectId?independentRoughcutPath(projectId,sourceId):`/api/studio/clips/${encodeURIComponent(sourceId)}`;
 return request<unknown>(`${root}/silence-preview/verify`,{method:'POST',body:JSON.stringify({sourceSignature:preview.sourceSignature,transcriptCacheKey:preview.transcriptCacheKey,project}),signal});
}
