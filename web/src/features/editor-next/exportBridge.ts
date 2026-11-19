import { independentRenderBlockReason } from './independentRenderEligibility';
import { controlRender, getJobs, getRenderFileUrl, getRenders, queueRender } from '../../api/client';
import type { EditorProject, ExportFormat, JobDto, RenderDto } from '../../api/types';
import type { ReplicaSaveWarning } from '../../engine/replicaSnapshotToProject';
import { EXPORT_FORMATS, FFMPEG_EXPORT_FORMATS } from '../editor/exportFormats';

export type ExportSaveState = { hasUnsaved: boolean; conflict: boolean; saving?: boolean; error?: string };
export interface ExportSnapshot {
  clipId: string; epoch: string; serverRevision: number; generation: number; engineRevision: number;
  project: EditorProject; document: unknown; archiveWarnings?: ReplicaSaveWarning[];
}
export interface ExportSaveSession {
  flush: () => Promise<boolean>;
  readState: () => ExportSaveState;
  snapshot: () => ExportSnapshot;
}
export type ExportColorSpace = null | 'bt709' | 'hdr10';
/** burnSubtitles 仅作用于本次提交的 clone；未给时沿用已保存工程值，不写回工程。 */
export interface ExportSelection { format: string; colorSpace: ExportColorSpace; burnSubtitles?: boolean }
export interface ExportPin {
  clipId: string; epoch: string; serverRevision: number; generation: number; engineRevision: number; documentHash: string;
}
export interface ExportState {
  open: boolean; preparing: boolean; enqueuing: boolean; cancelling: boolean;
  archiveWarnings: ReplicaSaveWarning[];
  error: string | null; pollError: string | null; render: RenderDto | null; job: JobDto | null; pin: ExportPin | null;
}
export interface ExportApi {
  queueRender: typeof queueRender; getRenders: typeof getRenders; getJobs: typeof getJobs;
  controlRender: typeof controlRender; getRenderFileUrl: typeof getRenderFileUrl;
}
const backendApi: ExportApi = { queueRender, getRenders, getJobs, controlRender, getRenderFileUrl };
const copy = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
const terminal = (status: string) => ['completed', 'failed', 'cancelled'].includes(status);
const messageOf = (error: unknown) => error instanceof Error ? error.message : String(error);
const exportFailure = (error: unknown) => {
  const conflict = error as { status?: number; code?: string; revision?: number } | null;
  if (conflict?.status === 409 && ['stale-revision', 'stale-epoch'].includes(conflict.code ?? '')) {
    const revision = Number.isSafeInteger(conflict.revision) ? `（当前 revision ${conflict.revision}）` : '';
    return `${conflict.code === 'stale-epoch' ? '服务端纪元已变化' : '工程保存修订已变化'}${revision}，请重新载入工程并重新保存后导出。`;
  }
  return messageOf(error);
};
const legacyLabels: Record<string, string> = {
  h264: 'H.264 · MP4 · 8-bit', hevc: 'HEVC (H.265) · MP4',
  prores: 'Apple ProRes 422 HQ · MOV · 10-bit 输出', dnxhr: 'Avid DNxHR · MOV · SQ 8-bit / HDR HQX 10-bit',
};
// 后端接受的格式 id：旧四格式（FilmCraft CLI 契约）+ R81 新增的 FFmpeg 原生格式。
// 新增条目直接来自 exportFormats.ts 的 FFMPEG_EXPORT_FORMATS，顺序与 C# EditorExportFormats.Ids 一致。
export const BACKEND_EXPORT_FORMATS = [
  ...EXPORT_FORMATS.map(spec => ({ ...spec, label: legacyLabels[spec.value] ?? spec.label })),
  ...FFMPEG_EXPORT_FORMATS.map(spec => ({
    value: spec.id as ExportFormat, label: `${spec.label} · ${spec.container}`, extension: spec.container, summary: spec.summary,
  })),
];
/** 未接入后端的通用原因（仅用于未知 id；每个已知不可用格式都有自己的具体原因）。 */
export const UNIMPLEMENTED_EXPORT_REASON = '未实现：后端没有该格式的 FFmpeg 编码路径。';
/**
 * 仍然不可用的复刻格式：只保留 FFmpeg 也没有可靠产物的那几项。
 * 键为复刻导出的原始 id（大小写敏感），统一转小写后参与判定。
 */
const UNIMPLEMENTED_REASONS: Record<string, string> = {
  av1: 'AV1 仅存在于复刻浏览器引擎（WebCodecs），后端 FFmpeg 路径未接入；请改用 WebM/VP9。',
  apv: 'APV 是后期交换编码，本机 FFmpeg 无 apv 编码器。',
  'mxf-op1a': 'MXF OP1a 需要专用封装与 essence 约束，现阶段无法保证第三方 NLE 可读。',
  'mxf-opatom': 'MXF OP-Atom 需要按轨拆分的 OP-Atom 布局，FFmpeg 无可靠路径。',
  aaf: 'AAF 是 NLE 工程交换格式（CFB/OLE2 结构），不是可编码的媒体流，FFmpeg 无法生成。',
  omf: 'OMF 是旧版 NLE 工程交换格式，不是媒体编码输出，FFmpeg 无法生成。',
};
export const UNIMPLEMENTED_EXPORT_FORMATS = Object.entries(UNIMPLEMENTED_REASONS).map(([value, reason]) => ({ value, label: unimplementedLabel(value), disabled: true as const, reason }));
function unimplementedLabel(value: string): string {
  return ({ av1: 'AV1', apv: 'APV', 'mxf-op1a': 'MXF OP1a', 'mxf-opatom': 'MXF OP-Atom', aaf: 'AAF 交换文件', omf: 'OMF (OMF2)' } as Record<string, string>)[value] ?? value;
}
const backendIds = new Set<string>(BACKEND_EXPORT_FORMATS.map(spec => String(spec.value)));
export function isBackendExportFormat(value: string): value is ExportFormat {
  return backendIds.has(value.trim().toLowerCase());
}
/** 复刻格式 id（可能是驼峰，如 wavLpcm）是否已经由后端接管。 */
export function isImplementedReplicaFormat(value: string): boolean {
  return backendIds.has(value.trim().toLowerCase());
}
export function exportSelectionBlockReason(selection: ExportSelection): string | null {
  if (!isBackendExportFormat(selection.format)) return UNIMPLEMENTED_EXPORT_REASON;
  if (![null, 'bt709', 'hdr10'].includes(selection.colorSpace)) return '色彩空间未实现。';
  if (selection.colorSpace !== null && selection.format !== 'hevc' && !(selection.format === 'dnxhr' && selection.colorSpace === 'hdr10')) {
    return '色彩标记仅适用于后端 HEVC，或 DNxHR 的 HDR10 编码。';
  }
  return null;
}
/** Read live state after flush; an acknowledgement for N cannot authorize N+1. */
export function exportSaveBlockReason(state: ExportSaveState): string | null {
  if (state.conflict) return '保存冲突：请先重新载入项目，导出已禁用。';
  if (state.error) return `保存失败：${state.error}`;
  if (state.saving) return '正在保存，请等待保存确认后导出。';
  if (state.hasUnsaved) return '项目仍有未保存的修改，导出已禁用。';
  return null;
}
export async function flushBeforeExport(flush: () => Promise<boolean>, readState: () => ExportSaveState): Promise<string | null> {
  try {
    const flushed = await flush();
    return exportSaveBlockReason(readState()) ?? (flushed ? null : '保存未确认，导出已禁用。');
  } catch (error) { return `保存失败：${messageOf(error)}`; }
}
export async function exportDocumentHash(document: unknown): Promise<string> {
  const bytes = new TextEncoder().encode(JSON.stringify(document));
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, '0')).join('');
}
export function exportBusy(state: ExportState): boolean {
  return state.preparing || state.enqueuing || state.cancelling || !!state.render && !terminal(state.render.status);
}
export const initialExportState = (): ExportState => ({ open: false, preparing: false, enqueuing: false, cancelling: false,
  error: null, pollError: null, render: null, job: null, pin: null, archiveWarnings: [] });

/** One project/load lifetime. API clients own transport; this controller owns stale-result rejection. */
export function createEditorNextExportController(options: {
  clipId: string; save: ExportSaveSession; independentProject?: boolean; onState?: (state: ExportState) => void; api?: ExportApi; pollMs?: number;
}) {
  const api = options.api ?? backendApi;
  let state = initialExportState(), disposed = false, operation = 0, openRequest = 0;
  let timer: ReturnType<typeof setTimeout> | null = null;
  const current = (id: number) => !disposed && operation === id;
  const sameOrigin = (item: { projectId?: string | null; clipId?: string | null; revision?: number | null; epoch?: string | null }) =>
    ((item.projectId ?? item.clipId) == null || (item.projectId ?? item.clipId) === options.clipId) &&
    (item.revision == null || item.revision === state.pin?.serverRevision) &&
    (item.epoch == null || item.epoch === state.pin?.epoch);
  const publish = (patch: Partial<ExportState>) => {
    if (disposed) return;
    state = { ...state, ...patch };
    options.onState?.(copy(state));
  };
  const clearPoll = () => { if (timer !== null) clearTimeout(timer); timer = null; };
  const cancelOrphan = (render: RenderDto) => {
    if ((render.projectId ?? render.clipId) === options.clipId && !terminal(render.status)) void api.controlRender(render.id, 'cancel').catch(() => {});
  };
  const checkSnapshot = () => {
    const blocked = exportSaveBlockReason(options.save.readState());
    if (blocked) throw new Error(blocked);
    const snapshot = copy(options.save.snapshot());
    if (snapshot.clipId !== options.clipId || !snapshot.epoch ||
        [snapshot.serverRevision, snapshot.generation, snapshot.engineRevision].some(value => !Number.isSafeInteger(value) || value < 0)) {
      throw new Error('工程身份或修订未确认，导出已禁用。');
    }
    const unsupported = options.independentProject ? independentRenderBlockReason(snapshot.project) : null;
    if (unsupported) throw new Error(unsupported);
    return snapshot;
  };
  const poll = async (id: number, renderId: string) => {
    try {
      const [renders, jobs] = await Promise.all([api.getRenders(), api.getJobs()]);
      if (!current(id)) return;
      const render = renders.find(item => item.id === renderId && (item.projectId ?? item.clipId) === options.clipId && sameOrigin(item));
      const job = jobs.find(item => item.id === renderId && item.type === 'edit-render' && sameOrigin(item)) ?? null;
      if (render) {
        publish({ render, job, pollError: null,
          cancelling: terminal(render.status) ? false : state.cancelling,
          error: render.status === 'failed' ? render.error || '后端渲染失败。' : terminal(render.status) ? null : state.error });
        if (terminal(render.status)) return;
      } else publish({ pollError: '后端暂未返回此工程的渲染任务；继续等待，不采纳其他任务结果。' });
    } catch (error) { if (current(id)) publish({ pollError: `进度读取失败：${messageOf(error)}；正在重试。` }); }
    if (current(id)) timer = setTimeout(() => { timer = null; void poll(id, renderId); }, options.pollMs ?? 1300);
  };
  const prepare = async () => {
    const blocked = await flushBeforeExport(options.save.flush, options.save.readState);
    if (blocked) throw new Error(blocked);
    return checkSnapshot();
  };
  return {
    getState: () => copy(state),
    async open() {
      if (disposed) return false;
      if (state.open) return true;
      // Reopening a submitted task must not invalidate its poll or pinned revision.
      if (state.pin || state.enqueuing || state.cancelling) { publish({ open: true }); return true; }
      if (state.preparing) return false;
      const id = ++operation, request = ++openRequest;
      publish({ preparing: true, error: null });
      try {
        const snapshot = await prepare();
        if (!current(id) || request !== openRequest) return false;
        publish({ open: true, archiveWarnings: copy(snapshot.archiveWarnings ?? []) });
        return true;
      } catch (error) { if (current(id)) publish({ error: messageOf(error) }); return false; }
      finally { if (current(id)) publish({ preparing: false }); }
    },
    // Hiding the panel never cancels a job or bypasses its save/queue barrier.
    close() { openRequest++; publish({ open: false }); },
    async start(selection: ExportSelection) {
      if (disposed || !state.open || exportBusy(state)) return false;
      const selected = { ...selection }, blocked = exportSelectionBlockReason(selected);
      if (blocked) { publish({ error: blocked }); return false; }
      if (options.independentProject && (selected.format !== 'h264' || selected.colorSpace !== null)) { publish({ error: '独立工程暂仅支持 H.264 MP4，不支持其他格式或 HDR 色彩空间。' }); return false; }
      const id = ++operation;
      clearPoll();
      publish({ preparing: true, error: null, pollError: null, render: null, job: null, pin: null });
      try {
        const snapshot = await prepare();
        if (!current(id)) return false;
        const documentHash = await exportDocumentHash(snapshot.document);
        if (!current(id)) return false;
        const latest = checkSnapshot();
        if (['clipId', 'epoch', 'serverRevision', 'generation', 'engineRevision'].some(key => latest[key as keyof ExportSnapshot] !== snapshot[key as keyof ExportSnapshot])) {
          throw new Error('保存后工程已变化，请重新确认导出。');
        }
        const { clipId, epoch, serverRevision, generation, engineRevision } = snapshot;
        const pin = { clipId, epoch, serverRevision, generation, engineRevision, documentHash };
        // snapshot is already a deep copy of the confirmed save; the override never touches the base project or captions.
        const project = { ...snapshot.project, format: selected.format as ExportFormat, colorSpace: selected.colorSpace,
          ...(typeof selected.burnSubtitles === 'boolean' ? { burnSubtitles: selected.burnSubtitles } : {}) };
        // Only the last confirmed save can authorize this render. A 409 is surfaced, never automatically retried.
        publish({ preparing: false, enqueuing: true, pin, archiveWarnings: copy(snapshot.archiveWarnings ?? []) });
        const receipt = await api.queueRender(clipId, project, serverRevision, epoch);
        // POST acknowledges a pending job; only GET /renders reports its execution state.
        const render: RenderDto = { ...receipt, status: 'pending', error: null };
        if (!current(id)) { cancelOrphan(render); return false; }
        if ((render.projectId ?? render.clipId) !== clipId) { cancelOrphan(render); throw new Error('后端返回其他工程的任务，已忽略。'); }
        if (!sameOrigin(render)) { cancelOrphan(render); throw new Error('后端返回其他修订的任务，已忽略，请重新保存后导出。'); }
        publish({ render, enqueuing: false, error: null });
        void poll(id, render.id);
        return true;
      } catch (error) { if (current(id)) publish({ error: exportFailure(error) }); return false; }
      finally { if (current(id)) publish({ preparing: false, enqueuing: false }); }
    },
    async cancel() {
      if (disposed || state.cancelling || !exportBusy(state)) return;
      const id = ++operation, render = state.render;
      clearPoll();
      publish({ preparing: false, enqueuing: false, cancelling: true, error: null });
      let awaitingBackend = false;
      try {
        if (render && !terminal(render.status)) {
          const result = await api.controlRender(render.id, 'cancel');
          if (!result.ok) throw new Error('后端未确认取消，继续查询任务状态。');
          // Backend only acknowledges cancelling; keep cancelling/busy until poll sees a real terminal status.
          if (current(id)) { awaitingBackend = true; void poll(id, render.id); }
        } else if (current(id)) publish({ job: null });
      } catch (error) {
        if (current(id)) {
          publish({ error: `取消失败：${messageOf(error)}` });
          if (render && !terminal(render.status)) void poll(id, render.id);
        }
      } finally { if (current(id) && !awaitingBackend) publish({ cancelling: false }); }
    },
    downloadUrl() {
      return !disposed && state.render != null && (state.render.projectId ?? state.render.clipId) === options.clipId && state.render.status === 'completed'
        ? api.getRenderFileUrl(state.render.id) : null;
    },
    dispose() {
      if (disposed) return;
      disposed = true; operation++; clearPoll();
      if (state.render) cancelOrphan(state.render);
    },
  };
}
export type EditorNextExportController = ReturnType<typeof createEditorNextExportController>;
