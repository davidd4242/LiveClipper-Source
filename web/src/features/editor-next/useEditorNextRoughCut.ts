import { useEffect, useRef, useState } from 'react';
import { getEditor } from '../../api/client';
import { getAiSettings } from '../../api/settings';
import type { FullEngineBridge } from '../../engine/fullEngineSession';
import { adviceConnection } from '../editor/aiAdvice';
import { generateRoughCutPlan, readRoughCutTranscriptState } from './aiApi';
import type { RoughCutPlan, RoughCutRange } from './aiApi';
import type { AiSaveSession } from './useEditorNextAi';

interface RoughCutBaseline {
  clipId: string;
  epoch: string;
  serverRevision: number;
  generation: number;
  engineRevision: number;
}
interface RoughCutSaveSession extends AiSaveSession {
  snapshot: () => RoughCutBaseline;
}
export interface NativeRoughCutBridge extends Pick<FullEngineBridge, 'hostSnapshot'> {
  hostPlaceSourceRanges?: (input: { sourceId: string; ranges: (RoughCutRange & { name?: string })[];
    expected: { generation: number; engineRevision: number } }) =>
    | { ok: true; keys: string[]; generation: number; engineRevision: number }
    | { ok: false; error?: string };
}
export interface EditorNextRoughCutState {
  status: 'idle' | 'generating' | 'ready' | 'applying' | 'applied' | 'stale' | 'error';
  plan: RoughCutPlan | null;
  message: string;
}
const initialState = (): EditorNextRoughCutState => ({ status: 'idle', plan: null, message: '先生成源区间预览，确认后追加到工程末尾；现有多轨内容保持不变。' });
export const roughCutIsBusy = (state: EditorNextRoughCutState) => state.status === 'generating' || state.status === 'applying';
const sameBaseline = (a: RoughCutBaseline, b: RoughCutBaseline) => a.clipId === b.clipId && a.epoch === b.epoch
  && a.serverRevision === b.serverRevision && a.generation === b.generation && a.engineRevision === b.engineRevision;
const stale = () => Object.assign(new Error('工程、原生修订或生效字幕已变化，请重新生成粗剪预览；未追加片段。'), { code: 'stale-rough-cut' });

export function useEditorNextRoughCut(options: {
  clipId: string | null;
  identity: object | null;
  ready: boolean;
  generation: number | undefined;
  sourceRange: RoughCutRange | null;
  blocked: boolean;
  getSaveSession: () => RoughCutSaveSession | null;
  getBridge: () => NativeRoughCutBridge | null;
}) {
  const latest = useRef(options); latest.current = options;
  const pending = useRef<AbortController | null>(null);
  const preview = useRef<{ plan: RoughCutPlan; baseline: RoughCutBaseline; identity: object } | null>(null);
  const [state, setState] = useState(initialState);
  const invalidate = () => { pending.current?.abort(); pending.current = null; preview.current = null; setState(initialState()); };
  useEffect(() => {
    invalidate();
    return () => { pending.current?.abort(); pending.current = null; preview.current = null; };
  }, [options.clipId, options.identity, options.ready, options.generation, options.sourceRange?.start, options.sourceRange?.end]);

  const begin = () => {
    const context = latest.current;
    if (!context.ready || !context.clipId || !context.identity || !context.sourceRange || context.blocked || pending.current) return null;
    const request = new AbortController(); pending.current = request;
    const current = () => pending.current === request && !request.signal.aborted && latest.current.ready
      && latest.current.identity === context.identity && latest.current.clipId === context.clipId;
    return { context, request, current };
  };
  const readBaseline = (context: typeof options, save: RoughCutSaveSession): RoughCutBaseline => {
    const saved = save.readState();
    if (saved.hasUnsaved || saved.saving || saved.conflict || saved.error) throw new Error(saved.error || '请先保存工程并处理保存冲突，再生成或追加粗剪。');
    const baseline = save.snapshot();
    const native = context.getBridge()?.hostSnapshot();
    if (!native?.ok || baseline.clipId !== context.clipId || !baseline.epoch || baseline.generation !== context.generation
      || native.generation !== baseline.generation || native.engineRevision !== baseline.engineRevision) throw stale();
    return baseline;
  };
  const fail = (cause: unknown) => setState(previous => ({ ...previous,
    status: (cause as { code?: string } | null)?.code === 'stale-rough-cut' ? 'stale' : 'error',
    message: cause instanceof Error ? cause.message : String(cause) }));

  const generate = async () => {
    const run = begin();
    if (!run) return;
    const { context, request, current } = run;
    preview.current = null;
    setState({ status: 'generating', plan: null, message: '正在保存工程并请求真实 LLM 粗剪源区间；尚未修改时间线…' });
    try {
      const save = context.getSaveSession();
      if (!save || !await save.flush()) throw new Error('工程尚未保存成功，未请求粗剪。请处理保存状态后重试。');
      if (!current()) return;
      const baseline = readBaseline(context, save);
      const transcript = await readRoughCutTranscriptState(context.clipId!);
      if (!current()) return;
      const connection = adviceConnection(await getAiSettings());
      if (!current()) return;
      if (!connection) throw new Error('请先在设置里选择 AI 模型，再生成真实 AI 粗剪。');
      const plan = await generateRoughCutPlan(context.clipId!, { ...context.sourceRange!, connection }, request.signal);
      if (!current()) return;
      if (!sameBaseline(baseline, readBaseline(context, save)) || plan.transcriptCacheKey !== transcript.transcriptCacheKey) throw stale();
      preview.current = { plan, baseline, identity: context.identity! };
      setState({ status: 'ready', plan, message: plan.source === 'ai'
        ? 'AI 粗剪预览已生成，尚未追加。请审阅源区间和理由。'
        : `返回来源为 ${plan.source}，不是 AI 粗剪成功；未启用 AI 追加，请重新生成。` });
    } catch (cause) { if (current()) fail(cause); }
    finally { if (pending.current === request) pending.current = null; }
  };

  const apply = async () => {
    const selected = preview.current;
    if (state.status !== 'ready' || !selected || selected.plan.source !== 'ai' || selected.plan.ranges.length === 0) return;
    const run = begin();
    if (!run) return;
    const { context, request, current } = run;
    setState(previous => ({ ...previous, status: 'applying', message: '正在保存并核对工程与字幕基线；确认后一次批量追加…' }));
    let appended = false;
    try {
      const save = context.getSaveSession();
      if (!save || !await save.flush()) throw new Error('工程尚未保存成功，未追加粗剪。请处理保存状态后重试。');
      if (!current()) return;
      if (selected.identity !== context.identity || !sameBaseline(selected.baseline, readBaseline(context, save))) throw stale();
      const transcript = await readRoughCutTranscriptState(context.clipId!);
      if (!current()) return;
      const server = await getEditor(context.clipId!);
      if (!current()) return;
      if (server.clip?.id !== selected.baseline.clipId || server.epoch !== selected.baseline.epoch
        || server.revision !== selected.baseline.serverRevision || transcript.transcriptCacheKey !== selected.plan.transcriptCacheKey
        || !sameBaseline(selected.baseline, readBaseline(context, save))) throw stale();
      const bridge = context.getBridge();
      if (!bridge?.hostPlaceSourceRanges) throw new Error('原生批量追加桥 hostPlaceSourceRanges 尚未就绪；未追加，不会改用逐条放置。');
      const result = bridge.hostPlaceSourceRanges({ sourceId: context.clipId!, ranges: selected.plan.ranges.map(range => ({ ...range, name: selected.plan.title })),
        expected: { generation: selected.baseline.generation, engineRevision: selected.baseline.engineRevision } });
      if (!result.ok) throw new Error(result.error || '原生批量追加失败，工程未确认追加。');
      appended = true;
      preview.current = null;
      setState({ status: 'applying', plan: null, message: `AI 粗剪已一次追加 ${result.keys.length} 个源区间到工程末尾，可单步撤销；正在保存。` });
      const saved = await save.flush();
      if (!current()) return;
      setState({ status: 'applied', plan: null, message: saved
        ? `AI 粗剪已一次追加 ${result.keys.length} 个源区间到工程末尾并保存；现有多轨保留，可单步撤销。`
        : 'AI 粗剪已追加，可单步撤销；保存尚未成功，请在原保存栏重试。不要重复追加。' });
    } catch (cause) {
      if (current()) {
        if (appended) setState({ status: 'applied', plan: null, message: `AI 粗剪已追加，可单步撤销；保存未确认，请在原保存栏核对。${cause instanceof Error ? cause.message : String(cause)}` });
        else fail(cause);
      }
    } finally { if (pending.current === request) pending.current = null; }
  };
  return { state, generate, apply, invalidate };
}
