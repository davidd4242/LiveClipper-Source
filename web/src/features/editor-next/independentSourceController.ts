import type { RoughCutPlan, RoughCutRange } from './aiApi';
import type { AiConnectionBody } from '../editor/aiAdvice';

export interface IndependentSource { id: string; name: string; eventId: string; duration: number }
export interface IndependentBaseline { clipId: string; epoch: string; serverRevision: number; generation: number; engineRevision: number }
export interface IndependentSourceState {
  source: IndependentSource | null; range: RoughCutRange | null;
  status: 'idle' | 'reading' | 'generating' | 'ready' | 'applying' | 'applied' | 'error';
  transcript: { transcriptCacheKey: string; [key: string]: unknown } | null;
  plan: RoughCutPlan | null; message: string;
}
export interface IndependentSourcePorts {
  projectId: string;
  sources: () => readonly IndependentSource[];
  save: { flush: () => Promise<boolean>; snapshot: () => IndependentBaseline;
    readState: () => { hasUnsaved: boolean; conflict: boolean; saving?: boolean; error?: string } };
  native: { snapshot: () => { ok: boolean; generation?: number; engineRevision?: number };
    append: (input: { sourceId: string; ranges: (RoughCutRange & { name?: string })[];
      expected: { generation: number; engineRevision: number } }) => { ok: boolean; error?: string; keys?: string[] } };
  api: {
    transcript: (sourceId: string, signal: AbortSignal) => Promise<{ transcriptCacheKey: string; [key: string]: unknown }>;
    connection: () => Promise<AiConnectionBody>;
    generate: (sourceId: string, input: RoughCutRange & { connection: AiConnectionBody }, signal: AbortSignal) => Promise<RoughCutPlan>;
    project: (projectId: string, signal: AbortSignal) => Promise<{ projectId: string; epoch: string; revision: number }>;
  };
  onState?: (state: IndependentSourceState) => void;
}
const same = (a: IndependentBaseline, b: IndependentBaseline) => a.clipId === b.clipId && a.epoch === b.epoch
  && a.serverRevision === b.serverRevision && a.generation === b.generation && a.engineRevision === b.engineRevision;
const changed = () => new Error('工程、选中素材或字幕已变化，请重新生成。');

/** Source requests never use projectId; persistence and revision checks never use sourceId. */
export function createIndependentSourceController(p: IndependentSourcePorts) {
  let state: IndependentSourceState = { source: null, range: null, status: 'idle', transcript: null, plan: null, message: '请选择当前工程中的素材。' };
  let disposed = false, request: AbortController | null = null;
  let preview: { baseline: IndependentBaseline; key: string } | null = null;
  const emit = (patch: Partial<IndependentSourceState>) => { state = { ...state, ...patch }; p.onState?.(state); };
  const registered = () => state.source && p.sources().some(s => s.id === state.source!.id && s.eventId === state.source!.eventId && s.duration === state.source!.duration);
  const baseline = () => {
    const saved = p.save.readState(), b = p.save.snapshot(), n = p.native.snapshot();
    if (saved.hasUnsaved || saved.conflict || saved.saving || saved.error) throw new Error(saved.error || '请先保存当前工程。');
    if (!registered() || b.clipId !== p.projectId || !n.ok || n.generation !== b.generation || n.engineRevision !== b.engineRevision) throw changed();
    return { ...b };
  };
  const begin = () => {
    if (disposed || request || !registered() || !state.range || state.status === 'applied') return null;
    const token = new AbortController(); request = token;
    const source = { ...state.source! }, range = { ...state.range };
    return { token, source, range, live: () => !disposed && request === token && !token.signal.aborted && registered() && state.source?.id === source.id };
  };
  const failure = (e: unknown) => emit({ status: 'error', plan: null, message: e instanceof Error ? e.message : String(e) });
  return {
    getState: () => state,
    select(sourceId: string | null, range?: RoughCutRange) {
      if (disposed) return false;
      const source = sourceId === null ? null : p.sources().find(s => s.id === sourceId);
      if (sourceId !== null && !source) return false;
      const selectedRange = source ? range ?? { start: 0, end: source.duration } : null;
      if (selectedRange && (!Number.isFinite(selectedRange.start) || !Number.isFinite(selectedRange.end) || selectedRange.start < 0 || selectedRange.end <= selectedRange.start || selectedRange.end > source!.duration)) return false;
      request?.abort(); request = null; preview = null;
      emit({ source: source ? { ...source } : null, range: selectedRange ? { ...selectedRange } : null, status: 'idle', transcript: null, plan: null, message: source ? '已选择素材；生成结果只追加到当前工程。' : '请选择当前工程中的素材。' });
      return true;
    },
    async readTranscript() {
      const run = begin(); if (!run) return false;
      emit({ status: 'reading', message: '正在读取所选素材字幕。' });
      try { const transcript = await p.api.transcript(run.source.id, run.token.signal); if (!run.live()) return false;
        emit({ transcript, status: 'idle', message: '已读取素材字幕，工程未修改。' }); return true;
      } catch (e) { if (run.live()) failure(e); return false; }
      finally { if (request === run.token) request = null; }
    },
    async generate() {
      const run = begin(); if (!run) return false;
      preview = null; emit({ status: 'generating', plan: null, message: '正在生成所选素材的粗剪预览。' });
      try {
        if (!await p.save.flush()) throw new Error('当前工程保存失败，未请求模型。');
        if (!run.live()) return false;
        const b = baseline();
        const transcript = await p.api.transcript(run.source.id, run.token.signal); if (!run.live()) return false;
        const connection = await p.api.connection(); if (!run.live()) return false;
        const plan = await p.api.generate(run.source.id, { ...run.range, connection }, run.token.signal); if (!run.live()) return false;
        const latest = await p.api.transcript(run.source.id, run.token.signal); if (!run.live()) return false;
        if (!same(b, baseline()) || latest.transcriptCacheKey !== transcript.transcriptCacheKey || plan.transcriptCacheKey !== transcript.transcriptCacheKey) throw changed();
        if (plan.source !== 'ai' || !plan.ranges.length || plan.ranges.some(r => !Number.isFinite(r.start) || !Number.isFinite(r.end) || r.start < run.range.start || r.end > run.range.end || r.end <= r.start)) throw new Error('没有可应用的真实 AI 源区间。');
        preview = { baseline: b, key: transcript.transcriptCacheKey };
        emit({ status: 'ready', transcript: latest, plan, message: '建议已生成，尚未追加。' }); return true;
      } catch (e) { if (run.live()) failure(e); return false; }
      finally { if (request === run.token) request = null; }
    },
    async apply() {
      if (state.status !== 'ready' || !preview || !state.plan) return false;
      const run = begin(); if (!run) return false;
      const captured = preview, plan = state.plan; let appended = false;
      emit({ status: 'applying', message: '正在校验并追加到当前工程。' });
      try {
        if (!await p.save.flush()) throw new Error('当前工程保存失败，未追加。');
        if (!run.live()) return false;
        if (!same(captured.baseline, baseline())) throw changed();
        const source = await p.api.transcript(run.source.id, run.token.signal); if (!run.live()) return false;
        const project = await p.api.project(p.projectId, run.token.signal); if (!run.live()) return false;
        if (project.projectId !== p.projectId || project.epoch !== captured.baseline.epoch || project.revision !== captured.baseline.serverRevision || source.transcriptCacheKey !== captured.key || !same(captured.baseline, baseline())) throw changed();
        const result = p.native.append({ sourceId: run.source.id, ranges: plan.ranges.map(r => ({ ...r, name: plan.title })), expected: { generation: captured.baseline.generation, engineRevision: captured.baseline.engineRevision } });
        if (!result.ok) throw new Error(result.error || '追加失败。');
        appended = true; preview = null;
        const saved = await p.save.flush(); if (!run.live()) return false;
        emit({ status: 'applied', plan: null, message: saved ? '已追加并保存，可单步撤销。' : '已追加但保存未确认，请在保存栏重试，不要重复追加。' }); return true;
      } catch (e) { if (run.live()) { if (appended) emit({ status: 'applied', plan: null, message: '已追加但保存未确认，请核对保存栏，不要重复追加。' }); else failure(e); } return false; }
      finally { if (request === run.token) request = null; }
    },
    dispose() { disposed = true; request?.abort(); request = null; preview = null; },
  };
}
