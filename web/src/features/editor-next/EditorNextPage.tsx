import {SequenceSettingsDialog} from './SequenceSettingsDialog';
import {readSequenceSettings} from './sequenceSettings';
import type {SequenceSettingsSnapshot} from './sequenceSettings';
import { useIndependentAutomaticReview } from './useIndependentAutomaticReview';
import type { IndependentSource } from './independentSourceController';
import { IndependentSourcePanel } from './IndependentSourcePanel';
import { independentRenderBlockReason } from './independentRenderEligibility';
import { canExportIndependentTimeline } from './exchangeExportAvailability';
import { bindProjectNavigation } from './projectNavigation';
import type { ProjectNavigationBridge } from './projectNavigation';
import { useMultiClipReview } from './useMultiClipReview';
import { MultiClipReviewPanel } from './MultiClipReviewPanel';
import { useSilencePreview } from './useSilencePreview';
import { SilencePreviewPanel } from './SilencePreviewPanel';
import { GlobalEffectPanel } from './GlobalEffectPanel';
import type { NativeEffectBridge } from './GlobalEffectPanel';
import { SourceSrtImport, ReplaceSourceCaptions } from './SourceSrtImport';
import { eventClearEffectAtBlock } from './eventClearProtection';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import './r65-editor-next-layout.css';
import './r72-editor-next-layout.css';
import './r79-editor-next-draft.css';
import './filmcraft-host-actions.css';
import { HostDrawer } from './HostDrawer';
import { NewProjectDialog } from './NewProjectDialog';
import { EditorStartPanel } from './EditorStartPanel';
import { TechnicalDetails } from './TechnicalDetails';
import { editorNextNoticeText } from './editorNextNotices';
import { getClips, getEditor } from '../../api/client';
import type { EditorPayload, StudioEvent } from '../../api/types';
import { FullEngineHost } from '../../engine/FullEngineHost';
import { createFullEngineGeneration } from '../../engine/fullEngineGeneration';
import type { FullEngineDocument, FullEngineLoadOptions } from '../../engine/fullEngineSession';
import { replicaDocumentForLoad, replicaSnapshotToProject } from '../../engine/replicaSnapshotToProject';
import { editorNextClipId, editorNextDocument, editorNextLoadOptions } from './editorNextDocument';
import { useEditorNextSave } from './useEditorNextSave';
import { IMPORT_MEDIA_LOCATIONS, restoredBinNames, importAndProbe, importPathsAndProbe, importRejectReason, importRejectReasonPaths, pickVideoFiles } from './editorNextImport';
import type { ImportMediaLocation } from './editorNextImport';
import { pickVideoPathsViaDesktop } from './desktopPickFiles';
import { useEditorNextExport } from './useEditorNextExport';
import { useEditorNextJianying } from './useEditorNextJianying';
import { useEditorNextPremiere } from './useEditorNextPremiere';
import { useEditorNextCopy } from './useEditorNextCopy';
import { useEditorNextPackage } from './useEditorNextPackage';
import { useEditorNextLifecycle } from './useEditorNextLifecycle';
import { exportSaveBlockReason } from './exportBridge';
import { ExportPanel } from './ExportPanel';
import { aiIsBusy, useEditorNextAi } from './useEditorNextAi';
import { useEditorNextRoughCut } from './useEditorNextRoughCut';
import { AiPanel } from './AiPanel';
import type { EventPlacementResult, PlacedEvent } from './AiPanel';
import { FilmCraftHostActions } from './FilmCraftHostActions';
import { markAppRouteReady, navigateApp, useAppLocation } from '../../app/appNavigation';
import { EditorNavigationRegistration } from '../../app/PersistentAppLayout';
import { FilmCraftRightTools } from './FilmCraftRightTools';
import { FilmCraftToolDrawer } from './FilmCraftToolDrawer';
import { TranscriptPanel } from './TranscriptPanel';
import { useEditorNextTranscript } from './useEditorNextTranscript';
import { PerceptionMarkers } from './PerceptionMarkers';
import { ChapterPanel, locateChapter } from './ChapterPanel';
import { MusicPanel } from './MusicPanel';
import { EditorNextStatus } from './EditorNextStatus';
import { createNativeMusicHost, useEditorNextMusic } from './useEditorNextMusic';
import type { MusicHostPort, NativeMusicBridge } from './useEditorNextMusic';
import type { FullEngineBridge, FullEngineConnection } from '../../engine/fullEngineSession';
type ChapterMusicBridge = FullEngineBridge & NativeMusicBridge & {
  setPlayhead: (seconds: number) => void;
  hostSyncEventClips?: (data: { sourceId: string; selectedEvents: PlacedEvent[]; originalKey: string | null;
    expected: { generation: number; engineRevision: number } }) =>
    | { ok: true; unchanged: boolean; addedKeys: string[]; removedKeys: string[]; eventClips: { eventId: string; key: string }[]; generation: number; engineRevision: number }
    | { ok: false; error?: string; keys?: string[] };
  hostPlaceCaptions?: (data: { sourceId: string; expected: { generation: number; engineRevision: number }; replaceExisting?: boolean }) =>
    | { ok: true; count: number; generation: number; engineRevision: number }
    | { ok: false; error?: string };
};
const eventObject = (value: unknown): Record<string, unknown> | null => value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null;
function readEventPlacement(document: unknown, sourceId: string) {
  const sequence = eventObject(eventObject(document)?.sequence);
  if (!sequence || !Array.isArray(sequence.videoTracks) || !Array.isArray(sequence.audioTracks) || typeof sequence.fps !== 'number' || !Number.isFinite(sequence.fps) || sequence.fps <= 0) throw new Error('invalid_event_snapshot');
  const fps = sequence.fps;
  const clips: Record<string, unknown>[] = [];
  const placed = new Map<string, PlacedEvent>();
  for (const track of [...sequence.videoTracks, ...sequence.audioTracks]) {
    const items = eventObject(track)?.clips;
    if (!Array.isArray(items)) throw new Error('invalid_event_snapshot');
    for (const item of items) {
      const clip = eventObject(item);
      if (!clip) throw new Error('invalid_event_snapshot');
      clips.push(clip);
      const placement = eventObject(clip.eventPlacement);
      if (placement?.version !== 1 || placement.sourceId !== sourceId) continue;
      if (typeof placement.eventId !== 'string' || !placement.eventId.trim() || clip.sourceId !== sourceId ||
        typeof clip.sourceIn !== 'number' || typeof clip.start !== 'number' || typeof clip.end !== 'number' ||
        typeof clip.speed !== 'number' || !Number.isFinite(clip.speed) || clip.speed <= 0) throw new Error('invalid_event_placement');
      const start = clip.sourceIn / fps, end = start + (clip.end - clip.start) * clip.speed / fps;
      if (!Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end <= start) throw new Error('invalid_event_placement');
      if (!placed.has(placement.eventId)) placed.set(placement.eventId, { eventId: placement.eventId, start, end, ...(typeof clip.name === 'string' ? { name: clip.name } : {}) });
    }
  }
  return { placedEvents: [...placed.values()], clips };
}
const chapterMusicBridge = () => (window as unknown as { FCBridge?: ChapterMusicBridge }).FCBridge ?? null;
// 只取类型：外壳由 App.tsx 以 prop 注入，页面不在运行时 import 它（多个 merge 测试单独编译本文件）。
import type { EditorNextShellComponent } from './EditorNextShell';

type FilmCraftAction = 'transcript' | 'analysis' | 'events' | 'rough-cut' | 'music' | 'export' | 'save' | 'notices';
type PageState =
  | { clipId: string | null; status: 'loading' }
  | { clipId: string | null; status: 'error'; error: string }
  | { clipId: string; status: 'loaded'; document: FullEngineDocument; options: FullEngineLoadOptions; payload: EditorPayload };
const saveLabels = { saved: '已保存', saving: '保存中', unsaved: '未保存', conflict: '冲突', error: '失败' };
/** 外壳里保存态的解释文案（title），不参与播报。 */
const saveTitles: Record<keyof typeof saveLabels, string> = {
  saved: '工程修改已落盘',
  saving: '正在保存工程',
  unsaved: '有修改尚未保存，Ctrl+S 立即保存',
  conflict: '服务器版本已变化：可重新载入或放弃本地修改',
  error: '保存失败，可在宿主栏重试',
};

export function EditorNextPage({ shell: Shell }: { shell?: EditorNextShellComponent } = {}) {
  const route = useAppLocation();
  const clipId = editorNextClipId(route.search);
  const generation = useRef(createFullEngineGeneration());
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<PageState>({ clipId, status: 'loading' });
  const [engineReady, setEngineReady] = useState(false);
  const [engineError, setEngineError] = useState<string | null>(null);
  const [recoveryError, setRecoveryError] = useState('');
  const [recoveryBusy, setRecoveryBusy] = useState(false);
  const recoveryPending = useRef(false);
  const [engineWarningCodes, setEngineWarnings] = useState<string[]>([]);
  const [actionsHost, setActionsHost] = useState<HTMLElement | null>(null);
  const engineWarnings = engineWarningCodes.map(editorNextNoticeText);
  const [drawer, setDrawer] = useState<'ai' | 'transcript' | 'chapters' | 'music' | 'perception' | 'independent' | 'notices' | null>(null);
  const [roughCutTool,setRoughCutTool]=useState<'semantic'|'voice'>('semantic');
  const [aiTarget, setAiTarget] = useState<'candidates' | 'analysis' | 'rough-cut' | null>(null);
  const [nativeStamp, setNativeStamp] = useState<{ generation: number; engineRevision: number } | null>(null);
  const [chapterSeekError, setChapterSeekError] = useState('');
  const [candidateBatch, setCandidateBatch] = useState<{ owner: EditorPayload; events: StudioEvent[] } | null>(null);
  const eventHost = useRef<{ owner: EditorPayload; connection: FullEngineConnection; bridge: ChapterMusicBridge | null } | null>(null);
  const [, setPlacementRefresh] = useState(0);
  const [importBusy, setImportBusy] = useState(false);
  const [sourceSrtBusy, setSourceSrtBusy] = useState(false);
  const [effectBusy, setEffectBusy] = useState(false);
  const [independentApplying, setIndependentApplying] = useState(false);
  const [automaticSource,setAutomaticSource]=useState<{projectId:string;source:IndependentSource}|null>(null);
  const [importMessage, setImportMessage] = useState<{ tone: 'status' | 'alert'; text: string } | null>(null);
  const importLock = useRef(false);
  const [newProjectOpen, setNewProjectOpen] = useState(false);
  const [sequenceSettings,setSequenceSettings]=useState<{initial:SequenceSettingsSnapshot;bridge:FullEngineBridge}|null>(null);
  const [sequenceSettingsError,setSequenceSettingsError]=useState('');

  // 手动重载保持原有「重载即收起右侧面板」；字幕完成自动同步的那次 reload 带保留标记，面板不无故关闭。
  const keepDrawerOnReload = useRef(false);
  useEffect(() => {
    const keep = keepDrawerOnReload.current;
    keepDrawerOnReload.current = false;
    if (!keep) { setDrawer(null); setAiTarget(null); }
  }, [clipId, attempt]);

  useEffect(() => {
    const current = generation.current.begin();
    setEngineReady(false);
    setNativeStamp(null);
    setSequenceSettings(null);setSequenceSettingsError('');
    eventHost.current = null;
    setActionsHost(null);
    setChapterSeekError('');
    setEngineError(null);
    setRecoveryError('');
    setImportMessage(null);
    setEngineWarnings([]);
    setState({ clipId, status: 'loading' });
    void (async () => {
      if (!clipId) {
        // 未指定工程：不替用户挑素材，回素材页由用户明确选择。
        setState({ clipId, status: 'error', error: '未选择工程，请先在「素材」页选择要剪辑的工程。' });
        return;
      }
      const payload = await getEditor(clipId);
      // Older independent documents kept canvas dimensions only in the catalog.
      // Carry that original canvas into the save/undo baseline before any native edit.
      if(payload.projectIdentity)payload.project={...payload.project,
        canvasWidth:payload.project.canvasWidth??payload.projectIdentity.width,
        canvasHeight:payload.project.canvasHeight??payload.projectIdentity.height};
      if (!generation.current.isCurrent(current)) return;
      const document = replicaDocumentForLoad(payload.project, editorNextDocument(payload));
      const bin = document.sequence.hostBinSources ?? [];
      const entries = bin.some(item => item.name === item.sourceId) ? restoredBinNames(bin, await getClips()) : bin;
      if (!generation.current.isCurrent(current)) return;
      const options = editorNextLoadOptions(payload, entries);
      setState({ clipId, status: 'loaded', document, options, payload });
    })().catch((cause: unknown) => {
      if (!generation.current.isCurrent(current)) return;
      setState({ clipId, status: 'error', error: cause instanceof Error ? cause.message : String(cause) });
    });
    return () => generation.current.invalidate();
  }, [clipId, attempt]);

  const current = state.clipId === clipId;
  const loaded = current && state.status === 'loaded' ? state : null;
  const candidateEvents = candidateBatch && candidateBatch.owner === loaded?.payload ? candidateBatch.events : loaded?.payload.events ?? [];
  // 已登记源 id：每次载入一份稳定的可变数组；增量导入成功后原地 push，保存 controller 持同一引用，因此新源片段可保存。
  const registeredIds = useMemo(() => loaded ? Object.keys(loaded.options.media ?? {}) : [], [loaded]);
  const save = useEditorNextSave(loaded ? { clipId: loaded.clipId, payload: loaded.payload, sourceIds: registeredIds } : null);
  // 无 clip 参数是空选择态（不是载入失败）：不展示失败/重试，页面给可见提示。
  const noClip = !clipId;
  const rawFailure = noClip ? null : current && state.status === 'error' ? state.error : loaded ? engineError : null;
  const failure = rawFailure === null ? null : editorNextNoticeText(rawFailure);
  const status = noClip ? 'empty' : failure !== null ? 'error' : loaded && engineReady ? 'ready' : 'loading';
  const unsupported = loaded?.document.sequence.unsupported ?? [];
  const draftPending = !!save.state.draft;
  const primaryClip = loaded?.payload.clip ?? null;
  const sourceUnavailable = '独立工程可导入素材编辑、保存，并将非空时间线导出为 Premiere 交换包或剪映草稿；支持空工程另存为和工程包，以及 V1 视频＋字幕的 H.264 MP4 渲染；可通过“单源人工审核”明确选源生成粗剪；支持明确选源的已有候选人工审核／全自动独立结果；多源和重转写暂未接入。';
  const projectName = loaded?.payload.projectIdentity?.name || primaryClip?.title || '未命名工程';
  const editingReady = status === 'ready' && !draftPending;
  // 空选择态本身就是最终呈现；有 clip 时仍须引擎 ready 才标记。
  // Route readiness reveals the draft decision UI; edit readiness stays blocked until that decision.
  useEffect(() => { if (status === 'ready' || (noClip && !save.state.draft) || failure !== null) markAppRouteReady(route.key); }, [status, noClip, save.state.draft, failure, route.key]);
  const exchangeReady = editingReady && (!!primaryClip || (!!loaded?.payload.projectIdentity && canExportIndependentTimeline(save.exportSession(), nativeStamp)));
  const renderBlockReason = (() => {
    if (!loaded || !editingReady) return '工程尚未就绪。';
    if (primaryClip) return null;
    const identity = loaded.payload.projectIdentity;
    if (!identity || !nativeStamp) return '独立工程尚未就绪。';
    try {
      const projection = save.exportSession()?.projectCurrentSnapshot(nativeStamp);
      if(projection?.ok&&((projection.project.canvasWidth??identity.width)%2||(projection.project.canvasHeight??identity.height)%2))return '独立工程 MP4 要求画布宽高为偶数。';
      return projection?.ok ? independentRenderBlockReason(projection.project) : '当前时间线无法完整映射，暂不能渲染。';
    } catch { return '当前时间线尚未稳定，请稍后重试。'; }
  })();
  const packager = useEditorNextPackage({ clipId: loaded?.clipId ?? null, identity: loaded?.payload ?? null, ready: editingReady,
    epoch: String(loaded?.payload.epoch ?? ''), name: projectName, getSaveSession: save.exportSession });
  const jianying = useEditorNextJianying({ clipId: loaded?.clipId ?? null, identity: loaded?.payload ?? null, ready: exchangeReady,
    epoch: String(loaded?.payload.epoch ?? ''), name: projectName, getSaveSession: save.exportSession });
  const premiere = useEditorNextPremiere({ clipId: loaded?.clipId ?? null, identity: loaded?.payload ?? null, ready: exchangeReady,
    epoch: String(loaded?.payload.epoch ?? ''), name: projectName, getSaveSession: save.exportSession });
  const copier = useEditorNextCopy({ clipId: loaded?.clipId ?? null, identity: loaded?.payload ?? null, ready: editingReady,
    epoch: String(loaded?.payload.epoch ?? ''), name: projectName, getSaveSession: save.exportSession,
    switchTo: async (result, hold) => {
      const target = new URL(`/editor?clip=${encodeURIComponent(result.clipId)}`, window.location.origin).href;
      hold.permit(target);
      return navigateApp(target);
    } });
  useEffect(() => bindProjectNavigation(
    (window as unknown as { FCBridge?: ProjectNavigationBridge }).FCBridge, navigateApp, { window, document }, () => setNewProjectOpen(true)),
    [loaded?.payload, editingReady]);
  const exporter = useEditorNextExport({ clipId: loaded?.clipId ?? null, identity: loaded?.payload ?? null, ready: editingReady && !renderBlockReason, independentProject: !!loaded?.payload.projectIdentity, getSaveSession: save.exportSession });
  const exportBlockReason = exportSaveBlockReason({ hasUnsaved: save.state.status === 'unsaved', conflict: save.state.status === 'conflict',
    saving: save.state.status === 'saving', error: save.state.status === 'error' ? save.state.message : undefined });
  const lifecycle = useEditorNextLifecycle({ identity: loaded?.payload ?? null, ready: editingReady,
    exportOpen: exporter.state.open, openExport: async () => { if (renderBlockReason) { setDrawer('notices'); return false; } return exporter.open(); }, mediaWarnings: engineWarningCodes,
    onMediaRecovered: sourceIds => setEngineWarnings(codes => codes.filter(code =>
      !sourceIds.some(id => code.startsWith(`media_unbound:${id}:`)))) });
  const ai = useEditorNextAi({ clipId: primaryClip?.id ?? null, identity: loaded?.payload ?? null, ready: editingReady && !!primaryClip,
    getSaveSession: save.exportSession, reload: () => reload() });
  const roughCutRange = useMemo(() => loaded && Number.isFinite(loaded.payload.duration) && loaded.payload.duration > 0
    ? { start: 0, end: loaded.payload.duration } : null, [loaded]);
  const multiClipReview = useMultiClipReview({clipId:primaryClip?.id??null,epoch:loaded?.payload.epoch??'',reload:()=>reload(),restoreBlocked:()=>!!save.state.draft||save.state.status!=='saved',identity:loaded?.payload??null,ready:editingReady&&!!primaryClip,getSaveSession:save.exportSession});
  const silencePreview = useSilencePreview({clipId:primaryClip?.id??null,identity:loaded?.payload??null,ready:editingReady&&!!primaryClip,duration:loaded?.payload.duration??0,getSaveSession:save.exportSession,onOpen:()=>{setRoughCutTool('voice');setAiTarget('rough-cut');setDrawer('ai');}});
  const roughCut = useEditorNextRoughCut({ clipId: primaryClip?.id ?? null, identity: loaded?.payload ?? null, ready: editingReady && !!primaryClip,
    generation: nativeStamp?.generation, sourceRange: roughCutRange, blocked: aiIsBusy(ai.state),
    getSaveSession: save.exportSession, getBridge: chapterMusicBridge });
  const musicGate = useRef({ loaded, editingReady, saveState: save.state, getSaveSession: save.exportSession });
  musicGate.current = { loaded, editingReady, saveState: save.state, getSaveSession: save.exportSession };
  const nativeGeneration = nativeStamp?.generation;
  const musicHost = useMemo<MusicHostPort>(() => {
    if (!loaded || nativeGeneration === undefined) return { read: () => null, blockReason: () => '当前工程原生配乐桥尚未就绪。',
      setGlobalMusic: () => ({ ok: false, error: '原生配乐桥尚未就绪。' }), flushSave: async () => false };
    return createNativeMusicHost({ context: { clipId: loaded.clipId, epoch: String(loaded.payload.epoch ?? ''), generation: nativeGeneration },
      getBridge: chapterMusicBridge, isCurrent: () => musicGate.current.loaded?.payload === loaded.payload && musicGate.current.editingReady,
      getSaveSession: () => musicGate.current.getSaveSession(), blockReason: () => {
        const current = musicGate.current;
        return !current.editingReady ? '当前工程尚未就绪或正在处理草稿。' : current.saveState.status === 'conflict' || current.saveState.status === 'error' ?
          '请先在保存栏处理冲突或保存错误。' : null;
      } });
  }, [loaded, nativeGeneration]);
  const music = useEditorNextMusic({ context: musicHost.read()?.context ?? null, host: musicHost });
  const readChapterProject = () => {
    if (!loaded || !editingReady || nativeGeneration === undefined) return { project: null, reason: '当前剪辑尚未就绪，暂不能定位。' };
    const snapshot = chapterMusicBridge()?.hostSnapshot();
    if (!snapshot?.ok || !snapshot.document || snapshot.generation !== nativeGeneration) return { project: null, reason: snapshot?.error || '原生快照所属工程已变化，暂不能定位。' };
    const mapped = replicaSnapshotToProject(snapshot.document, { project: loaded.payload.project, document: loaded.document,
      registeredSourceIds: registeredIds, independentProject: !!loaded.payload.projectIdentity });
    return mapped.ok ? { project: mapped.project, reason: null } : { project: null, reason: mapped.warnings.map(w => w.message).join('；') || '当前原生时间线不能映射，暂不能定位。' };
  };
  const chapterView = readChapterProject();
  const locateEvent = (event: StudioEvent) => {
    const fresh = readChapterProject(), bridge = chapterMusicBridge();
    if (!fresh.project || !bridge) { setChapterSeekError(fresh.reason || '原生定位桥不可用。'); return; }
    const location = locateChapter(event, fresh.project);
    if (!location.ok) { setChapterSeekError(location.reason); return; }
    try { bridge.setPlayhead(location.seconds); setChapterSeekError(''); }
    catch (error) { setChapterSeekError(error instanceof Error ? error.message : String(error)); }
  };
  const readOwnedEvents = () => {
    const host = eventHost.current;
    if (!loaded?.payload.clip || !editingReady || !host || host.owner !== loaded.payload || nativeGeneration === undefined) throw new Error('event_host_not_ready');
    const snapshot = host.connection.snapshot();
    if (!snapshot.ok || snapshot.generation !== nativeGeneration || !Number.isSafeInteger(snapshot.engineRevision)) throw new Error(snapshot.error || 'stale_engine_revision');
    return { host, snapshot, ...readEventPlacement(snapshot.document, loaded.payload.clip.id) };
  };
  const readEventView = (): { placedEvents: PlacedEvent[]; error: string | null } => {
    try { return { placedEvents: readOwnedEvents().placedEvents, error: null }; }
    catch (error) { return { placedEvents: [], error: error instanceof Error ? error.message : String(error) }; }
  };
  const eventView = readEventView();
  const syncEvents = (selectedIds: readonly string[]): EventPlacementResult => {
    const fail = (error: string): EventPlacementResult => ({ ok: false, message: `事件同步失败：${error}`, selectedIds: readEventView().placedEvents.map(event => event.eventId) });
    try {
      if (!loaded?.payload.clip || !editingReady || save.state.status === 'conflict' || save.state.status === 'error') return fail('当前工程不能放置事件。');
      const session = save.exportSession(), saved = session?.readState();
      if (!session || !saved || saved.conflict || saved.error) return fail(saved?.error || '当前工程保存会话不可用或有冲突。');
      const current = readOwnedEvents();
      if (!current.host.bridge?.hostSyncEventClips) return fail('hostSyncEventClips 尚未就绪；未改用逐条追加。');
      const sourceId = loaded.payload.clip.id;
      const selectedEvents: PlacedEvent[] = [];
      for (const id of new Set(selectedIds)) {
        const placed = current.placedEvents.find(event => event.eventId === id);
        if (placed) { selectedEvents.push(placed); continue; }
        const candidate = candidateEvents.find(event => event.id === id && event.clipId === sourceId);
        if (!candidate) return fail(`event_selection_not_found:${id}`);
        selectedEvents.push({ eventId: candidate.id, start: candidate.start, end: candidate.end, name: candidate.title });
      }
      let originalKey: string | null = null;
      if (!current.placedEvents.length && selectedEvents.length) {
        const declared = eventObject(loaded.document.sequence)?.hostEventOriginals;
        const originals = Array.isArray(declared) ? declared.map(eventObject).filter(item => item?.sourceId === sourceId) : [];
        if (originals.length > 1) return fail('original_selection_required');
        const key = originals[0]?.key;
        if (typeof key === 'string' && key.trim() && current.clips.some(clip => clip.key === key)) originalKey = key;
      }
      const clearBlock = eventClearEffectAtBlock(() => session.projectCurrentSnapshot({
        generation: nativeGeneration!, engineRevision: current.snapshot.engineRevision!,
      }), sourceId, selectedEvents.length, current.clips);
      if (clearBlock) return fail(clearBlock);
      const result = current.host.bridge.hostSyncEventClips({ sourceId, selectedEvents, originalKey,
        expected: { generation: nativeGeneration!, engineRevision: current.snapshot.engineRevision! } });
      setPlacementRefresh(value => value + 1);
      if (!result.ok) return fail(`${result.error || 'unknown_event_sync_error'}${result.keys?.length ? `（片段：${result.keys.join('、')}）` : ''}`);
      const actual = readOwnedEvents();
      const ids = actual.placedEvents.map(event => event.eventId);
      if (result.generation !== actual.snapshot.generation || result.engineRevision !== actual.snapshot.engineRevision ||
        result.eventClips.some(event => !ids.includes(event.eventId)) || ids.some(id => !result.eventClips.some(event => event.eventId === id))) return fail('event_sync_snapshot_mismatch');
      return { ok: true, selectedIds: ids, message: result.unchanged ? '选择与实际已放置事件一致，时间线未修改。' :
        `事件已一次同步：新增 ${result.addedKeys.length} 个片段，移除 ${result.removedKeys.length} 个片段。勾选已按当前快照保留；可撤销，保存沿用工程保存栏。` };
    } catch (error) {
      setPlacementRefresh(value => value + 1);
      return fail(error instanceof Error ? error.message : String(error));
    }
  };
  const reload = () => { ai.invalidate(); roughCut.invalidate(); music.invalidate(); exporter.invalidate(); save.abandonForReload(); setEngineReady(false); setNativeStamp(null); setAttempt(value => value + 1); };
  const independentAutomatic=useIndependentAutomaticReview({
    projectId:loaded?.payload.projectIdentity?.id??null,sourceId:automaticSource && automaticSource.projectId===loaded?.clipId?automaticSource.source.id:null,
    epoch:loaded?.payload.epoch??'',generation:nativeStamp?.generation,identity:loaded?.payload??null,ready:editingReady&&!primaryClip,
    registered:()=>!!loaded&&automaticSource?.projectId===loaded.clipId&&Object.hasOwn(loaded.options.media??{},automaticSource.source.id),
    getSaveSession:save.exportSession,reload:()=>reload(),restoreBlocked:()=>!!save.state.draft||save.state.status!=='saved',onBusy:setIndependentApplying,
  });
  const transcript = useEditorNextTranscript({
    clipId: primaryClip?.id ?? null,
    eventId: primaryClip?.eventId ?? null,
    epoch: loaded?.payload.epoch ?? null,
    ready: editingReady && !!primaryClip,
    getSaveSession: save.exportSession,
    reload: () => reload(),
  });
  const rawNotices = [
    ...(loaded && !primaryClip ? [sourceUnavailable, ...(renderBlockReason ? [renderBlockReason] : [])] : []),
    ...(rawFailure !== null ? [rawFailure || '整页编辑器载入失败。'] : []),
    ...engineWarningCodes,
    ...(save.state.message ? [save.state.message] : []),
    ...save.state.archiveWarnings.map(issue => `${issue.message} · 片段：${issue.clipIds?.join('、') ?? ''}`),
    ...save.state.warnings,
    ...(exportBlockReason ? [exportBlockReason] : []),
    ...(exporter.bridgeWarning ? [exporter.bridgeWarning] : []),
    ...(exporter.state.error ? [exporter.state.error] : []),
    ...(exporter.state.pollError ? [exporter.state.pollError] : []),
    ...unsupported.map(message => `工程映射暂不支持：${message}。未实现字段保留原值；相应编辑会明确拒绝保存。`),
  ];
  const notices = rawNotices.map(editorNextNoticeText);
  const exportViewState = { ...exporter.state,
    error: exporter.state.error ? editorNextNoticeText(exporter.state.error) : null,
    pollError: exporter.state.pollError ? editorNextNoticeText(exporter.state.pollError) : null };
  const activeDrawer = exporter.state.open ? null : drawer;
  useEffect(()=>{if(drawer!=='ai'||aiTarget!=='rough-cut'||roughCutTool!=='voice'||exporter.state.open)silencePreview.close();},[drawer,aiTarget,roughCutTool,exporter.state.open]);
  useEffect(() => { if (exporter.state.open) setDrawer(null); }, [exporter.state.open]);
  const toggleDrawer = (value: 'ai' | 'transcript' | 'chapters' | 'music' | 'perception' | 'independent' | 'notices') => {
    exporter.close();
    setDrawer(current => current === value ? null : value);
  };
  const openAiPanel = (target: 'candidates' | 'analysis' | 'rough-cut') => {
    exporter.close();
    if (drawer === 'ai' && aiTarget === target) { setDrawer(null); setAiTarget(null); return; }
    setAiTarget(target);
    if(target==='rough-cut'&&roughCutTool==='voice')silencePreview.open();
    setDrawer('ai');
  };
  const handleAction = (action: FilmCraftAction) => {
    if (!primaryClip && ['transcript', 'analysis', 'events', 'rough-cut'].includes(action)) { setDrawer('notices'); return; }
    switch (action) {
      case 'transcript': toggleDrawer('transcript'); return;
      case 'analysis': if (editingReady) openAiPanel('analysis'); return;
      case 'events': if (editingReady) openAiPanel('candidates'); return;
      case 'rough-cut': if (editingReady) openAiPanel('rough-cut'); return;
      case 'music': toggleDrawer('music'); return;
      case 'export':
        if (renderBlockReason) { setDrawer('notices'); return; }
        if (exporter.state.open) { exporter.close(); return; }
        if (exporter.state.preparing || save.state.status === 'conflict' || save.state.status === 'error') return;
        setDrawer(null);
        void lifecycle.openExport(null);
        return;
      case 'save':
        // 冲突时保留本地修改：保存按钮只展示已有冲突提示，销毁性重载是提示栏里的独立入口。
        if (save.state.status === 'conflict') { exporter.close(); setDrawer('notices'); return; }
        if (save.state.status === 'saving') return;
        void save.save();
        return;
      case 'notices': toggleDrawer('notices'); return;
    }
  };
  // 原生顶栏「通知」按钮（稳定 data-fc-action，不依赖 title）：在 actionsHost 上委托，转到既有 notices 抽屉。
  const handleActionRef = useRef(handleAction);
  handleActionRef.current = handleAction;
  useEffect(() => {
    if (!actionsHost) return;
    const onClick = (event: MouseEvent) => {
      const target = event.target instanceof Element ? event.target.closest('[data-fc-action="notices"]') : null;
      if (!target || !actionsHost.contains(target)) return;
      event.preventDefault(); // 告知原生预览脚本：宿主已接管，不再提示「通知不可用」
      handleActionRef.current('notices');
    };
    actionsHost.addEventListener('click', onClick);
    return () => actionsHost.removeEventListener('click', onClick);
  }, [actionsHost]);
  const transcriptDraft = useRef(false);
  const [transcriptDraftDirty, setTranscriptDraftDirty] = useState(false);
  const onTranscriptDraftChange = useCallback((dirty: boolean) => { transcriptDraft.current = dirty; setTranscriptDraftDirty(dirty); }, []);
  // 转写字幕 → 独立字幕轨：只走 native hostPlaceCaptions 单事务（TL.replaceCaptions），dirty/保存沿用 onDirty 既有链路。
  const [placeBusy, setPlaceBusy] = useState(false);
  const [placeError, setPlaceError] = useState<string | null>(null);
  const placeLock = useRef(false);
  const readPlaceState = (): { reason: string | null; ownCount: number; host: NonNullable<typeof eventHost.current> | null; stamp: { generation: number; engineRevision: number } | null } => {
    const none = (reason: string) => ({ reason, ownCount: 0, host: null, stamp: null });
    if (!loaded?.payload.clip || !editingReady) return none('当前工程尚未就绪或正在处理草稿。');
    if (save.state.status === 'conflict' || save.state.status === 'error') return none('请先在保存栏处理冲突或保存错误。');
    if (transcriptDraftDirty) return none('请先保存或放弃字幕文本草稿。');
    if (transcript.saveState.busy || roughCut.state.status === 'applying') return none('请等待当前保存或粗剪应用完成。');
    const host = eventHost.current;
    if (!host || host.owner !== loaded.payload || nativeGeneration === undefined) return none('原生时间线尚未就绪。');
    if (typeof host.bridge?.hostPlaceCaptions !== 'function') return none('hostPlaceCaptions 尚未就绪。');
    const snapshot = host.connection.snapshot();
    if (!snapshot.ok || snapshot.generation !== nativeGeneration || !Number.isSafeInteger(snapshot.engineRevision)) return none(snapshot.error || '原生快照已变化，请稍后重试。');
    const sequence = eventObject(eventObject(snapshot.document)?.sequence);
    if (!sequence) return none('原生快照无效。');
    const sourceId = loaded.payload.clip.id;
    const cues = eventObject(sequence.hostCaptions)?.[sourceId];
    if (!Array.isArray(cues) || !cues.length) return none('当前转写没有可放置的字幕。');
    const own = Array.isArray(sequence.captions) ? sequence.captions.filter(c => eventObject(c)?.sourceClipId === sourceId) : [];
    return { reason: null, ownCount: own.length, host, stamp: { generation: snapshot.generation!, engineRevision: snapshot.engineRevision! } };
  };
  const placeView = readPlaceState();
  // 字幕完成后自动同步（最小修）：轮询发现 transcriptVersion 变化（updated）且工程完全干净时，
  // 走既有受保护 reload 重新 GET 工程一次，让新字幕进入原生 hostCaptions/文稿与放置按钮；
  // 任一条件不满足则不动，保留 TranscriptPanel 既有提示与受保护手动同步，绝不静默丢弃内容。
  const autoSyncSeen = useRef<{ identity: string; version: string } | null>(null);
  const autoSyncIdentity = useRef<string | null>(null);
  useEffect(() => {
    const identity = `${clipId ?? ''}|${loaded?.payload.clip?.eventId ?? ''}|${loaded?.payload.epoch ?? ''}`;
    if (autoSyncIdentity.current !== identity) {
      // 身份切换：清掉旧身份的一次版本记录，新工程自己的版本变化才能再次自动同步；
      // 本 commit 里 updated/transcriptVersion 仍是切换前 hook 的遗留快照（hook 的身份 reset 在下一 commit 才提交），直接跳过。
      autoSyncIdentity.current = identity;
      autoSyncSeen.current = null;
      return;
    }
    if (!transcript.state.updated) return;
    const version = transcript.state.transcriptVersion ?? '';
    if (!version) return; // NULL/空 transcriptVersion 不可当作成功的新版本
    if (autoSyncSeen.current?.version === version) return; // 同一版本只自动同步一次
    if (failure !== null || recoveryBusy || !editingReady) return;
    if (save.state.status !== 'saved' || save.state.draft || save.state.draftError) return; // 草稿/未保存/保存中/冲突/错误不自动重载
    if (transcriptDraft.current || transcript.saveState.busy || sourceSrtBusy || roughCut.state.status === 'applying' || placeBusy) return;
    if (exporter.state.open || exporter.state.preparing) return;
    // React 的 saved 标签不足以证明实时干净：用当前 controller 的 readState 核实，再以 getExportSnapshot
    // 确认引擎代次/修订与服务器确认一致。任一失败：保留面板提示，不 reload、不 ack、不记 seen（避免伪成功）。
    const session = save.exportSession();
    const live = session?.readState();
    if (!session || !live || live.hasUnsaved || live.saving || live.conflict || live.error) return;
    try { session.snapshot(); } catch { return; }
    autoSyncSeen.current = { identity, version };
    keepDrawerOnReload.current = true;
    transcript.acknowledgeUpdate();
    reload(); // 上方检查与 ack/reload 之间保持同步、不插 await，避免 check→commit 竞态
  }, [transcript.state.updated, transcript.state.transcriptVersion, transcript.acknowledgeUpdate, transcript.saveState.busy,
    clipId, loaded, failure, recoveryBusy, editingReady, save.state.status, save.state.draft, save.state.draftError, save.exportSession,
    roughCut.state.status, placeBusy, sourceSrtBusy, exporter.state.open, exporter.state.preparing, reload]);
  const placeCaptions = () => {
    if (placeLock.current) return;
    placeLock.current = true; setPlaceBusy(true); setPlaceError(null);
    try {
      const fresh = readPlaceState();
      if (fresh.reason || !fresh.host || !fresh.stamp || !loaded) { setPlaceError(fresh.reason || '当前不能放置字幕。'); return; }
      const owner = loaded.payload;
      if (!owner.clip) { setPlaceError(sourceUnavailable); return; }
      const replace = fresh.ownCount > 0;
      if (replace && !window.confirm('替换自动放置字幕，保留手工修改字幕？\n\n同源自动放置的字幕会按当前转写重新放置；手工修改过的字幕与其他来源字幕保持不变。')) return;
      const result = fresh.host.bridge!.hostPlaceCaptions!({ sourceId: owner.clip.id, expected: fresh.stamp, ...(replace ? { replaceExisting: true } : {}) });
      if (eventHost.current?.owner !== owner) { setPlaceError('工程已变化，字幕放置结果未采用，请重新确认。'); return; }
      if (!result.ok) { setPlaceError(`字幕放置失败：${result.error || 'unknown_caption_place_error'}`); return; }
      setPlacementRefresh(value => value + 1);
      const actual = fresh.host.connection.snapshot();
      if (!actual.ok || actual.generation !== result.generation || actual.engineRevision !== result.engineRevision) setPlaceError('字幕已放置，但原生快照与返回值不一致，请重新载入核对。');
    } catch (error) {
      setPlacementRefresh(value => value + 1);
      setPlaceError(`字幕放置失败：${error instanceof Error ? error.message : String(error)}`);
    } finally { placeLock.current = false; setPlaceBusy(false); }
  };
  // 真实媒体导入：显式 picker → POST 既有 import-files → 探测元数据 → 原生增量登记源（不 hostLoadDocument，不清撤销）。
  const importGate = useRef({ loaded, generation: nativeGeneration, editingReady });
  importGate.current = { loaded, generation: nativeGeneration, editingReady };
  const importBlockReason = (): string | null => {
    if (independentApplying) return '请等待当前粗剪追加完成。';
    if (importLock.current) return '正在导入，请等待完成。';
    if (!loaded || !editingReady) return '当前工程尚未就绪或正在处理草稿，暂不能导入。';
    if (save.state.status === 'conflict' || save.state.status === 'error') return '请先在保存栏处理冲突或保存错误，再导入。';
    if (transcriptDraftDirty) return '请先保存或放弃字幕文本草稿，再导入。';
    if (transcript.saveState.busy || roughCut.state.status === 'applying') return '请等待当前保存或粗剪应用完成，再导入。';
    const host = eventHost.current;
    if (!host || host.owner !== loaded.payload || nativeGeneration === undefined) return '原生时间线尚未就绪，暂不能导入。';
    if (typeof host.bridge?.hostRegisterSources !== 'function') return '原生增量登记接口 hostRegisterSources 不可用，未导入。';
    return null;
  };
  const importMedia = async (location?: ImportMediaLocation) => {
    const blocked = importBlockReason();
    if (blocked) { setImportMessage({ tone: 'alert', text: blocked }); return; }
    const owner = loaded!, payload = owner.payload, gen = nativeGeneration!, ids = registeredIds, media = owner.options.media;
    const live = () => importGate.current.loaded?.payload === payload && importGate.current.generation === gen;
    let files: File[] | null = null;
    let paths: string[] | null = null;
    if (location) {
      // 快捷位置：桌面选择器桥（受限枚举 → 已知文件夹起始位置），返回本机选中绝对路径；复用真实 import-paths，不伪造浏览器 File。
      try { paths = await pickVideoPathsViaDesktop(location); }
      catch (error) { setImportMessage({ tone: 'alert', text: `未导入：${error instanceof Error ? error.message : String(error)}` }); return; }
      if (!paths.length) return; // 用户取消：零请求
    } else {
      files = await pickVideoFiles(); // 取消 → []：不发任何请求
      if (!files.length) return;
    }
    if (!live()) { setImportMessage({ tone: 'alert', text: '选择文件期间工程已变化，未导入。' }); return; }
    const again = importBlockReason();
    if (again) { setImportMessage({ tone: 'alert', text: again }); return; }
    const reject = files ? importRejectReason(files) : importRejectReasonPaths(paths!);
    if (reject) { setImportMessage({ tone: 'alert', text: `未导入：${reject}` }); return; }
    importLock.current = true; setImportBusy(true);
    setImportMessage({ tone: 'status', text: `正在导入 ${(files ?? paths)!.length} 个文件…` });
    const stale = '素材已导入全局素材库，但当前工程已变化，未加入当前工程；请在新工程中重新导入以加入。';
    try {
      let probe;
      try { probe = files ? await importAndProbe(files, new Set(ids), live) : await importPathsAndProbe(paths!, new Set(ids), live); }
      catch (error) { if (live()) setImportMessage({ tone: 'alert', text: `导入失败：${error instanceof Error ? error.message : String(error)}（未自动重试；全局素材库是否已收入请到素材页核对）` }); return; }
      if (!probe || !live()) { setImportMessage({ tone: 'alert', text: stale }); return; }
      const failures = probe.failures.map(item => `${item.sourceId}：${item.message}`);
      const wanted = Object.keys(probe.sources);
      let added: string[] = [];
      let already: string[] = [];
      let fatal = '';
      if (wanted.length) {
        const bridge = chapterMusicBridge();
        if (!bridge?.hostRegisterSources || eventHost.current?.owner !== payload) fatal = '原生增量登记接口不可用或工程已变化，素材已进全局素材库但未加入当前工程。';
        else {
          let result;
          try { result = await bridge.hostRegisterSources({ expected: { generation: gen }, sources: probe.sources }); }
          catch (error) { result = { ok: false, error: error instanceof Error ? error.message : String(error) }; }
          if (!live()) { setImportMessage({ tone: 'alert', text: stale }); return; }
          if (!result.ok) fatal = `加入当前工程失败：${result.error || 'register_failed'}`;
          else {
            added = result.registered ?? []; already = result.already ?? [];
            for (const id of added) { if (!ids.includes(id)) ids.push(id); if (media) media[id] = probe.sources[id]; }
            for (const item of result.failed ?? []) failures.push(`${item.sourceId}：${item.error}`);
          }
        }
      }
      const lib = `素材库新增 ${probe.library.added}、已存在 ${probe.library.existing}`;
      const parts = [lib, `加入当前工程 ${added.length} 个${already.length ? `（${already.length} 个本就在工程内）` : ''}`];
      if (!probe.library.clipIds?.length) parts.push('后端没有返回视频素材');
      if (fatal) parts.push(fatal);
      if (failures.length) parts.push(`失败 ${failures.length} 个：${failures.join('；')}。已成功的不回滚，失败项未自动重传`);
      if (added.length) parts.push('可在素材面板里对其「追加到 V1 末尾」（可撤销）；未放入时间线的素材也随工程清单保存');
      setImportMessage({ tone: fatal || failures.length || !added.length && !already.length ? 'alert' : 'status', text: parts.join('；') + '。' });
    } finally { importLock.current = false; setImportBusy(false); }
  };
  const importRef = useRef(importMedia);
  importRef.current = importMedia;
  useEffect(() => {
    if (!loaded) return;
    const onRequest = (event: Event) => {
      const detail = (event as CustomEvent<{ location?: unknown }>).detail;
      const location = typeof detail?.location === 'string' && (IMPORT_MEDIA_LOCATIONS as readonly string[]).includes(detail.location)
        ? detail.location as ImportMediaLocation : undefined;
      event.preventDefault(); // 宿主接管：引擎不落回内置提示
      void importRef.current(location);
    };
    window.addEventListener('fc-host-import-request', onRequest);
    return () => window.removeEventListener('fc-host-import-request', onRequest);
  }, [loaded]);
  // 空选择态，或终态载入失败且从未载入（无 loaded 即无保存会话、无可丢内容）；有草稿/草稿错误一律不放行。
  const emptySelection = !loaded && !save.state.draft && !save.state.draftError && (!clipId || (current && state.status === 'error'));
  const navigationGate = useRef({ loaded, editingReady, busy: false, emptySelection, failed: false, draft: save.state.draft, draftError: save.state.draftError, getSession: save.exportSession });
  navigationGate.current = { loaded, editingReady, busy: independentApplying || effectBusy || transcript.saveState.busy || sourceSrtBusy || roughCut.state.status === 'applying' || importBusy, emptySelection,
    failed: !!loaded && failure !== null, draft: save.state.draft, draftError: save.state.draftError, getSession: save.exportSession };
  const checkRecovery = async (): Promise<string | null> => {
    const owner = navigationGate.current;
    if (!owner.loaded || !owner.failed || owner.busy) return '工程状态已变化或仍有操作进行，请稍后重试。';
    if (owner.draft || owner.draftError) return '本地草稿尚待处理或写入失败，不能确认全部内容已保全。';
    const session = owner.getSession();
    if (!session) return '引擎保存会话尚未就绪，不能确认当前引擎内容。';
    try {
      const before = session.readState();
      if ((before.hasUnsaved || before.saving || before.conflict || before.error) && !await session.flush()) {
        const saved = session.readState();
        return saved.error || (saved.conflict ? '保存冲突，本地修改仍保留。' : '未能确认保存，本地修改仍保留。');
      }
      if (navigationGate.current.loaded !== owner.loaded || !navigationGate.current.failed || navigationGate.current.busy) return '工程状态已变化，已取消恢复操作。';
      // saved 标签不足以证明安全；此合同还核对实时快照与服务器确认的代次、修订。
      session.snapshot();
      if (transcriptDraft.current) return '字幕面板中仍有未保存的文字草稿。';
      return null;
    } catch (cause) {
      return `无法确认已保存快照：${cause instanceof Error ? cause.message : String(cause)}`;
    }
  };
  const recoveryLossWarning = '当前引擎中尚未保存到服务器、也未写入本地草稿的时间线、配乐等工程修改，以及字幕面板中未保存的文字将丢失。已存在的本地工程草稿会保留，重新进入后仍需选择恢复。';
  const retryEngine = async (discardUnprotected = false) => {
    if (recoveryPending.current) return;
    const owner = navigationGate.current;
    if (!owner.failed || owner.busy || owner.draft?.status === 'restoring') { setRecoveryError('仍有操作进行，暂不能重新载入。'); return; }
    recoveryPending.current = true; setRecoveryBusy(true); setRecoveryError('');
    try {
      if (discardUnprotected) {
        if (!window.confirm(`${recoveryLossWarning}\n确定放弃这些未保全内容并重新载入吗？`)) return;
      } else {
        const reason = await checkRecovery();
        if (reason) { setRecoveryError(reason); return; }
      }
      if (navigationGate.current.loaded !== owner.loaded || !navigationGate.current.failed || navigationGate.current.busy) { setRecoveryError('工程状态已变化，已取消重新载入。'); return; }
      ai.invalidate(); roughCut.invalidate(); music.invalidate(); exporter.invalidate();
      // 不调用 abandonForReload：旧会话按现有 dispose 合同尽力落草稿，不删除已有草稿。
      setEngineReady(false); setNativeStamp(null); setAttempt(value => value + 1);
    } finally { recoveryPending.current = false; setRecoveryBusy(false); }
  };
  const beforeNavigate = async (targetHref: string): Promise<string | { message: string; kind: 'transcript-draft' } | null> => {
    const owner = navigationGate.current;
    if (recoveryPending.current) return '正在确认恢复，请稍候。';
    // 未选择工程的空选择态没有可保存内容；仍有操作进行或字幕草稿时走原有拦截。
    if (owner.emptySelection && !owner.busy && !transcriptDraft.current) return null;
    if (owner.failed) {
      if (owner.busy || owner.draft?.status === 'restoring') return '仍有操作进行，暂不能离开。';
      recoveryPending.current = true; setRecoveryBusy(true);
      try {
        const reason = await checkRecovery();
        if (navigationGate.current.loaded !== owner.loaded || !navigationGate.current.failed || navigationGate.current.busy) return '工程状态已变化，已留在当前页面。';
        if (!reason) return null;
        setRecoveryError(reason);
        return window.confirm(`${reason}\n${recoveryLossWarning}\n确定放弃这些未保全内容并离开当前页面吗？`) ? null : '未确认放弃内容，已留在当前页面。';
      } finally { recoveryPending.current = false; setRecoveryBusy(false); }
    }
    if (!owner.editingReady || owner.busy) return '请先处理本地草稿或等待当前操作完成，再切换页面。';
    const session = owner.getSession();
    if (!session) return '当前工程保存会话尚未就绪，请稍后重试。';
    if (session.saveAsPending()) {
      if (transcriptDraft.current) return '\u8bf7\u5148\u5904\u7406\u672a\u4fdd\u5b58\u7684\u5b57\u5e55\u8349\u7a3f\uff0c\u526f\u672c\u5df2\u4fdd\u7559\uff0c\u6682\u4e0d\u5207\u6362\u3002';
      return session.canLeaveForSaveAs(targetHref) ? null : '\u53e6\u5b58\u4e3a\u5904\u7406\u4e2d\uff0c\u539f\u5de5\u7a0b\u672a\u5199\u56de\u3002';
    }
    const before = session.readState();
    const alreadyClean = !before.hasUnsaved && !before.saving && !before.conflict && !before.error;
    const clean = alreadyClean || await session.flush();
    const current = navigationGate.current;
    if (current.loaded !== owner.loaded || !current.editingReady || current.busy) return '工程状态已变化，已留在当前页面，请稍后重试。';
    const saved = session.readState();
    if (!clean || saved.hasUnsaved || saved.saving || saved.conflict || saved.error) {
      return editorNextNoticeText(saved.error || (saved.conflict ? '保存冲突：本地修改已保留，请先处理保存提示。' : '工程尚未确认保存，已留在当前页面；请重试保存后再切换。'));
    }
    if (transcriptDraft.current && !window.confirm('字幕草稿尚未保存，离开会丢弃这些修改。确定离开当前页面吗？')) {
      return { message: '字幕草稿尚未保存，已留在当前页面。请先保存或取消字幕编辑。', kind: 'transcript-draft' };
    }
    return null;
  };
  // R64 preview stated 本批未接入保存; the scoped hook now owns the host save path.
  const sequenceSettingsReady=editingReady&&!independentApplying&&!recoveryBusy&&!importBusy&&!sourceSrtBusy&&!effectBusy&&!transcript.saveState.busy&&roughCut.state.status!=='applying'&&save.state.status!=='conflict';
  const openSequenceSettings=()=>{
    if(!sequenceSettingsReady)return;
    const bridge=(window as unknown as {FCBridge:FullEngineBridge}).FCBridge;
    try{setSequenceSettings({initial:readSequenceSettings(bridge),bridge});setSequenceSettingsError('');}
    catch(e){setSequenceSettingsError(e instanceof Error?e.message:String(e));}
  };
  useEffect(()=>{
    if(!sequenceSettingsReady)return;
    const bridge=(window as unknown as {FCBridge?:FullEngineBridge}).FCBridge;
    return bridge?.onSequenceSettingsRequest?.(openSequenceSettings);
  },[sequenceSettingsReady,nativeStamp?.generation,loaded]);
  const saveLabel = saveLabels[save.state.status] + (save.state.revision !== null ? ` · 修订 ${save.state.revision}` : '');
  const activeAction: FilmCraftAction | null =
    drawer === 'transcript' ? 'transcript' : drawer === 'music' ? 'music' : drawer === 'notices' ? 'notices'
      : drawer === 'ai' && editingReady ? (aiTarget === 'candidates' ? 'events' : aiTarget === 'rough-cut' ? 'rough-cut' : 'analysis') : null;
  const engineEpoch = `${clipId ?? 'none'}:${attempt}`;
  const content = (
    <>
      <EditorNavigationRegistration beforeNavigate={beforeNavigate} transcriptDraft={transcriptDraftDirty} />
    <main className="editor-next-page" id="editor-next-main" aria-label="整页复刻编辑器预览" aria-busy={status === 'loading'}>
      {save.state.draft && <section className="editor-next-draft-banner" aria-label="本地草稿恢复" data-draft-state={save.state.draft.status}>
        <p role="status" aria-live="polite" aria-atomic="true">{save.state.draft.message}</p>
        {save.state.draft.status === 'available' && <button type="button" disabled={!engineReady}
          onClick={() => { void save.restoreDraft(); }}>恢复草稿</button>}
        {save.state.draft.status === 'available' && <label>从备份文件恢复并保存（同工程、同基础修订）
          <input type="file" accept=".json,application/json" disabled={!engineReady || recoveryBusy} onChange={async e=>{
            const input=e.currentTarget,file=input.files?.[0]; input.value=''; if(!file)return;
            try { await save.restoreDraft(await file.text()); } catch(error) { setRecoveryError(error instanceof Error?error.message:'备份文件读取失败'); }
          }}/></label>}
        {save.state.draft.status === 'blocked' ? <button type="button" disabled={recoveryBusy}
          onClick={() => { if (failure !== null) void retryEngine(true); else reload(); }}>重新载入工程</button>
          : <button type="button" disabled={save.state.draft.status === 'restoring'} onClick={save.discardDraft}>丢弃草稿</button>}
      </section>}
      {save.state.draftError && <div className="editor-next-draft-banner"><p role="alert">{save.state.draftError}</p></div>}
      {lifecycle.mediaIssue && <div className="editor-next-media-alert" data-media-issue={lifecycle.mediaIssue.kind}>
        <p role="alert" aria-atomic="true">{lifecycle.mediaIssue.message}</p>
        <button type="button" disabled={lifecycle.mediaRetrying || !editingReady} aria-busy={lifecycle.mediaRetrying}
          onClick={() => { void lifecycle.retryMedia(); }}>{lifecycle.mediaRetrying ? '正在重试加载媒体…' : '重试加载媒体'}</button>
      </div>}
      <div className="editor-next-announcements">
        <p role="status" aria-live="polite" aria-atomic="true" aria-label="编辑器载入状态">{status === 'empty' ? '未选择工程：请先选择要剪辑的工程。' : status === 'error' ? '错误：整页编辑器载入失败。' : status === 'ready' ? '就绪：整页编辑器已载入。' : loaded ? '加载中：正在启动整页编辑器…' : '加载中：正在读取工程…'}</p>
        {failure !== null && !loaded && <p role="status">{failure}</p>}
        {failure !== null && loaded && <p role="alert">{failure}</p>}
        {importMessage && <p role={importMessage.tone === 'alert' ? 'alert' : 'status'} aria-busy={importBusy}>媒体导入：{importMessage.text}</p>}
        {engineWarnings.length > 0 && <p role="alert">媒体或工程载入提示：{engineWarnings.join('；')}</p>}
        {save.state.message && <p role={save.state.status === 'error' || save.state.status === 'conflict' ? 'alert' : 'status'}>{editorNextNoticeText(save.state.message)}</p>}
        {save.state.archiveWarnings.map(issue => <p key={`${issue.code}:${issue.clipIds?.[0] ?? issue.path}`} role="status" aria-live="polite">
          {issue.message} · 片段：{issue.clipIds?.join('、')}
        </p>)}
        {exportBlockReason && <p id="editor-next-export-reason">{editorNextNoticeText(exportBlockReason)}</p>}
        {exporter.bridgeWarning && <p role="status">{editorNextNoticeText(exporter.bridgeWarning)}</p>}
        {!exporter.state.open && exporter.state.error && <p role="alert">{exportViewState.error}</p>}
        <p role="status" aria-live="polite" aria-atomic="true">{save.state.warnings.map(editorNextNoticeText).join('；')}</p>
        {unsupported.length > 0 && <p role="alert">工程映射暂不支持：{unsupported.map(editorNextNoticeText).join('；')}。未实现字段保留原值；相应编辑会明确拒绝保存。</p>}
      </div>
      {failure !== null && <section className={`editor-next-terminal-error${loaded ? ' editor-next-terminal-error--loaded' : ''}`} aria-label="编辑器错误">
        <p role="alert" className="editor-next-terminal-error__text">{failure}</p>
        {loaded ? <>
          <p className="editor-next-terminal-error__hint">先保存并核对当前引擎快照，再重新载入；无法确认时会留在本页，已有本地草稿不会自动删除。也可通过左侧导航离开，未保全内容须先确认放弃。</p>
          {recoveryError && <p role="alert">{editorNextNoticeText(recoveryError)}</p>}
          <button type="button" disabled={recoveryBusy || transcript.saveState.busy || roughCut.state.status === 'applying' || save.state.draft?.status === 'restoring'}
            onClick={() => { void retryEngine(); }}>{recoveryBusy ? '正在确认保存…' : '保存确认后重试'}</button>
          <button type="button" disabled={recoveryBusy || transcript.saveState.busy || roughCut.state.status === 'applying' || save.state.draft?.status === 'restoring'}
            onClick={() => { void retryEngine(true); }}>放弃未保全内容并重试…</button>
        </> : <button type="button" className="editor-next-empty-selection__action" onClick={() => setAttempt((value) => value + 1)}>重试</button>}
      </section>}
      {loaded&&<button type="button" disabled={!sequenceSettingsReady} onClick={openSequenceSettings}>序列设置…</button>}
      {sequenceSettingsError&&<p role="alert">{sequenceSettingsError}</p>}
      {sequenceSettings&&<SequenceSettingsDialog initial={sequenceSettings.initial} bridge={sequenceSettings.bridge} save={save.save} onClose={()=>setSequenceSettings(null)}/>}
      {newProjectOpen && <NewProjectDialog onClose={() => setNewProjectOpen(false)} onCreated={async project => {
        const opened = await navigateApp(`/editor?clip=${encodeURIComponent(project.id)}`);
        if (opened) setNewProjectOpen(false);
        return opened;
      }} />}
      {loaded?.payload.projectIdentity && <button type="button" disabled={!editingReady||independentApplying} onClick={() => toggleDrawer('independent')}>单源人工审核…</button>}
      <HostDrawer id="editor-next-independent-source" title="独立工程 · 单源人工审核" open={activeDrawer === 'independent'} onClose={() => setDrawer(null)}>
        {loaded?.payload.projectIdentity && editingReady && <IndependentSourcePanel key={`${loaded.clipId}:${attempt}`} projectId={loaded.clipId}
          media={loaded.options.media ?? {}} ready={editingReady&&!independentApplying} getSaveSession={save.exportSession} getBridge={chapterMusicBridge} onApplying={setIndependentApplying} onOpenAutomatic={source=>{if(automaticSource?.projectId===loaded.clipId&&automaticSource.source.id===source.id)independentAutomatic.open();setAutomaticSource({projectId:loaded.clipId,source});setDrawer(null);}} />}
      </HostDrawer>
      {noClip && !loaded && <><EditorStartPanel onNew={() => setNewProjectOpen(true)} /><button type="button" onClick={packager.openImport}>打开工程包…</button></>}
      <HostDrawer id="editor-next-chapters-drawer" title="章节事件" open={activeDrawer === 'chapters'} onClose={() => setDrawer(null)}>
        {loaded && <ChapterPanel events={candidateEvents} project={chapterView.project} unavailableReason={chapterView.reason}
          onLocate={(_location, event) => locateEvent(event)} />}
        {chapterSeekError && <p role="alert">{chapterSeekError}</p>}
      </HostDrawer>
      <HostDrawer id="editor-next-perception-drawer" title="感知标记" open={activeDrawer === 'perception' && editingReady} onClose={() => setDrawer(null)}>
        {loaded?.payload.clip && editingReady && <PerceptionMarkers key={`${clipId}:${attempt}`} clipId={loaded.payload.clip.id}
          duration={loaded.payload.duration} sourceName={loaded.payload.clip.title} />}
      </HostDrawer>
      <HostDrawer id="editor-next-notices-drawer" title={`保存与兼容提示（${notices.length}）`} open={activeDrawer === 'notices'} onClose={() => setDrawer(null)}>
        <p className="editor-next-draft-policy">{lifecycle.draftNotice}</p>
        <p>导出快捷键：Ctrl+E / Cmd+E。未保存的修改会先保存确认，保存冲突或失败时不会打开导出。</p>
        {save.state.status === 'conflict' && <p role="alert">保存冲突：本地修改已保留。
          <button type="button" disabled={recoveryBusy}
            onClick={() => { if (failure !== null) void retryEngine(true); else reload(); }}>重新载入并放弃本地修改</button>
        </p>}
        <p>更多面板入口：
          <button type="button" disabled={!primaryClip} title={!primaryClip ? sourceUnavailable : undefined} onClick={() => { if (editingReady) toggleDrawer('perception'); }}>感知标记</button>
          <button type="button" onClick={() => toggleDrawer('chapters')}>章节事件</button>
        </p>
        {lifecycle.mediaIssue && <p>{lifecycle.mediaIssue.message}</p>}
        {notices.length > 0 ? <ul>{notices.map((message, index) => <li key={`${index}:${message}`}>{message}</li>)}</ul> : <p>暂无提示。</p>}
        <TechnicalDetails messages={rawNotices} />
      </HostDrawer>
      {loaded && <FullEngineHost
        key={`${clipId}:${attempt}`}
        className="editor-next-engine"
        inert={independentApplying || failure !== null || recoveryBusy || draftPending || sourceSrtBusy || transcript.saveState.busy || roughCut.state.status === 'applying'}
        document={loaded.document}
        options={loaded.options}
        onReady={(connection) => { save.onReady(connection);
          eventHost.current = { owner: loaded.payload, connection, bridge: chapterMusicBridge() };
          const snapshot = connection.snapshot();
          setNativeStamp(snapshot.ok && Number.isSafeInteger(snapshot.generation) && Number.isSafeInteger(snapshot.engineRevision) ?
            { generation: snapshot.generation!, engineRevision: snapshot.engineRevision! } : null);
          setEngineReady(true); setEngineError(null);
          const bar = document.getElementById('topbar-right');
          if (bar) setActionsHost(bar); }}
        onWarnings={setEngineWarnings}
        onError={(error) => { setEngineReady(false); setEngineError(error.message); }}
        onDocumentLoaded={snapshot => setNativeStamp(snapshot.ok && Number.isSafeInteger(snapshot.generation) && Number.isSafeInteger(snapshot.engineRevision) ?
          { generation: snapshot.generation!, engineRevision: snapshot.engineRevision! } : null)}
        onDirty={event => { save.onDirty(event); setNativeStamp({ generation: event.generation, engineRevision: event.engineRevision }); }}
        onSaveRequest={save.onSaveRequest}
        onCloseRequest={() => { void save.onCloseRequest(); }}
      />}
      {actionsHost && createPortal(<FilmCraftHostActions onAction={handleAction} active={activeAction} saveLabel={saveLabel} exportUnavailable={renderBlockReason ?? undefined} />, actionsHost)}
      <HostDrawer id="editor-next-package-drawer" title={packager.state.mode === 'save' ? '保存工程包' : '打开工程包'} open={packager.state.open} onClose={packager.close}>
        <p>.lcproj 是包含工程清单与媒体的 ZIP 工程包。媒体完整打包或复制可能耗时并占用额外空间；创建和导入包都不覆盖当前工程。</p>
        {packager.state.mode === 'save' ? <>
          <label>工程包名称 <input aria-label="工程包名称" value={packager.state.name} disabled={packager.state.busy} onChange={event => packager.setName(event.target.value)} /></label>
          <button type="button" disabled={packager.state.busy || !!packager.state.download} onClick={() => { void packager.save(); }}>生成工程包</button>
        </> : <>
          <p>普通 ZIP 必须包含有效工程清单。文件须小于 16 GiB；暂不支持 RAR。导入后将创建新的独立工程。</p>
          <input type="file" aria-label="选择工程包" accept=".lcproj,.zip" disabled={packager.state.busy} onChange={event => {
            const file = event.target.files?.[0]; event.target.value = ''; if (file) void packager.importFile(file);
          }} />
        </>}
        {packager.state.busy && <p role="status">{packager.state.importProgress
          ? packager.state.importProgress.phase === 'importing' ? '上传完成，正在验证并导入工程包…'
            : `正在上传工程包：${Math.floor(packager.state.importProgress.uploaded / Math.max(1, packager.state.importProgress.total) * 100)}%`
          : '正在处理工程包，请等待完成…'}</p>}
        {packager.state.busy && packager.state.importProgress?.phase === 'uploading' &&
          <button type="button" onClick={packager.cancelImport}>取消上传</button>}
        {packager.state.error && <p role="alert">{packager.state.error}</p>}
        {packager.state.download && <p role="status">工程包已生成。<a href={packager.state.download.downloadUrl} download={packager.state.download.fileName}>下载工程包</a></p>}
        {packager.state.imported && <p role="status">已导入“{packager.state.imported.name}”。
          <button type="button" onClick={() => { void navigateApp(`/editor?clip=${encodeURIComponent(packager.state.imported!.clipId)}`); }}>打开导入工程</button>
          <span>打开时执行现有离页保护，可能先保存当前工程；仅导入工程包不会触发此保存。</span></p>}
      </HostDrawer>
      <MultiClipReviewPanel model={multiClipReview} clipId={clipId} />
      <MultiClipReviewPanel model={independentAutomatic} clipId={automaticSource?.source.id??null} drawerId="editor-next-independent-automatic" blocked={['preparing','running'].includes(independentAutomatic.analysis.phase)} title="独立工程 · 单源候选审核／全自动"
        introduction={<section className="independent-automatic-intro"><p>仅处理明确选定源的已有候选：{automaticSource?.source.name}。全自动结果仅携本次单源时间线、媒体、字幕及恢复快照，不继承原候选、分析缓存或版本；原工程与源数据保留。支持明确标记跨场景过渡；不进行多源分析或重转写。</p>
          <button type="button" disabled={independentApplying} onClick={()=>void independentAutomatic.prepareCandidates()}>生成 AI 事件候选…</button>
          <p role={independentAutomatic.analysis.phase==='error'?'alert':'status'}>{independentAutomatic.analysis.message}</p>
          {independentAutomatic.analysis.phase==='confirm'&&<div><p>接收服务：{independentAutomatic.analysis.connection?.provider} · {independentAutomatic.analysis.connection?.endpoint} · 模型 {independentAutomatic.analysis.connection?.model}；仅发送所选源在当前工程的全部有效字幕（{independentAutomatic.analysis.cueCount} 条，{independentAutomatic.analysis.range?.start.toFixed(2)}–{independentAutomatic.analysis.range?.end.toFixed(2)} 秒）。不采用 cut-plan 开始/结束范围。{independentAutomatic.analysis.connection?.provider==='compatible'?'在线服务可能收费。':'使用所选本机 Ollama 服务。'}</p><button type="button" disabled={independentApplying} onClick={()=>void independentAutomatic.confirmCandidates()}>确认生成该素材的 AI 事件候选</button><button type="button" disabled={independentApplying} onClick={independentAutomatic.cancelCandidates}>取消</button></div>}
        </section>} />
      <HostDrawer id="editor-next-jianying-drawer" title="剪映 v3 候选草稿" open={jianying.state.open} onClose={jianying.close}>
        <p>以当前未保存内容生成独立草稿，不保存或覆盖原工程。复制原素材，保留 V1–V3 / A1–A3 基础轨道、源剪点、空隙、字幕和静态音量，不烘焙整条时间线；复杂效果将明确拒绝。</p>
        <p>候选草稿采用 pyJianYingDraft 0.3 格式，尚未在最新版剪映实机验收。生成成功不代表剪映版本兼容。</p>
        <p>使用设置中的导出目录（请配置 F 盘），复制媒体需要时间和磁盘空间。不会自动写入剪映草稿目录，也不会安装依赖。请保留整个输出目录，按生成的说明手动导入。</p>
        <label>草稿名称<input value={jianying.state.name} disabled={jianying.state.busy} onChange={event => jianying.setName(event.target.value)} /></label>
        <button type="button" disabled={jianying.state.busy} onClick={() => void jianying.create()}>{jianying.state.busy ? '正在复制素材并生成候选草稿…' : '生成剪映 v3 候选草稿'}</button>
        {jianying.state.error && <p role="alert">{jianying.state.error}</p>}
        {jianying.state.result && <div role="status">
          <p>候选草稿已生成，仍需剪映实机验收：{jianying.state.result.directory}</p>
          <p>草稿：{jianying.state.result.draftPath}</p><p>清单：{jianying.state.result.manifestPath}</p>
          {jianying.state.result.warnings.map((warning, index) => <p key={index}>{warning}</p>)}
        </div>}
      </HostDrawer>
      <HostDrawer id="editor-next-premiere-drawer" title="Premiere 交换包" open={premiere.state.open} onClose={premiere.close}>
        <p>生成 FCP7 XML、时间线 SRT 和独立复制的媒体，不生成 .prproj，不保存或覆盖当前工程。</p>
        <p>输出到设置的导出目录（F 盘请在设置中配置）。复制媒体可能耗时并占用空间。移动目录后可能需要重新连接 media。</p>
        <p>支持基本多轨剪切、空隙及独立字幕文件。变速、转场、嵌套和未映射效果不支持，将明确拒绝。已验证 Windows Premiere 基础剪切导入；不代表复杂效果兼容。</p>
        <label><input type="checkbox" checked={premiere.state.normalizeVariableFrameRate} disabled={premiere.state.busy} onChange={event => premiere.setNormalizeVariableFrameRate(event.target.checked)} />将可变帧率素材转换为恒定帧率（额外耗时、占用导出目录空间，保留原素材）</label>
        <p>转换按素材进行，不烘焙整条时间线，剪点仍可编辑；未经转换的可变帧率素材会明确拒绝。</p>
        <label>交换包名称<input value={premiere.state.name} disabled={premiere.state.busy} onChange={event => premiere.setName(event.target.value)} /></label>
        <button type="button" disabled={premiere.state.busy} onClick={() => void premiere.create()}>{premiere.state.busy ? '正在复制媒体并生成…' : '生成 Premiere 交换包'}</button>
        {premiere.state.error && <p role="alert">{premiere.state.error}</p>}
        {premiere.state.result && <div role="status">
          <p>已生成交换包：{premiere.state.result.directory}</p>
          <p>XML：{premiere.state.result.xmlPath}</p><p>SRT：{premiere.state.result.srtPath}</p>
          {premiere.state.result.warnings.map((warning, index) => <p key={index}>{warning}</p>)}
          <p>请在 Premiere 中导入 XML，再单独导入 SRT；生成成功不代表已完成 Premiere 实测。</p>
        </div>}
      </HostDrawer>
      <HostDrawer id="editor-next-copy-drawer" title={copier.state.mode === 'saveAs' ? '另存为新工程' : '保存独立副本'} open={copier.state.open} onClose={copier.close}>
        {copier.state.mode === 'saveAs' && <p>{'\u53e6\u5b58\u4e3a\u6210\u529f\u540e\u5207\u6362\u5230\u65b0\u5de5\u7a0b\uff0c\u4e0d\u5148\u4fdd\u5b58\u539f\u5de5\u7a0b\u3002\u82e5\u526f\u672c\u5df2\u521b\u5efa\u4f46\u5207\u6362\u5931\u8d25\uff0c\u4f1a\u4fdd\u7559\u526f\u672c\u4fe1\u606f\uff0c\u4e0d\u91cd\u590d\u521b\u5efa\u3002'}</p>}
        <p>将当前时间线保存为新的独立工程，不先保存或覆盖原工程。媒体文件也会完整复制，可能耗时并占用额外磁盘空间。</p>
        <label>副本名称 <input aria-label="副本名称" value={copier.state.name} disabled={copier.state.busy}
          onChange={event => copier.setName(event.target.value)} /></label>
        <button type="button" disabled={copier.state.busy || !!copier.state.result} onClick={() => { void copier.create(); }}>
          {copier.state.busy ? '正在创建副本…' : '创建副本'}</button>
        {copier.state.error && <p role="alert">{copier.state.error}</p>}
        {copier.state.result && <p role="status">已创建“{copier.state.result.name}”，原工程未被此操作覆盖。
          <button type="button" onClick={() => { void navigateApp(`/editor?clip=${encodeURIComponent(copier.state.result!.clipId)}`); }}>打开副本</button>
          <span>打开副本会离开当前工程，并执行现有离页保护；若原工程有未保存修改，会先尝试保存原工程，保存失败时不会切换。仅创建副本不会触发此保存。</span></p>}
      </HostDrawer>
      {loaded && editingReady && <ExportPanel key={loaded.clipId} independentProject={!!loaded.payload.projectIdentity} state={exportViewState} project={loaded.payload.project}
        blockReason={exportBlockReason ? editorNextNoticeText(exportBlockReason) : null}
        bridgeWarning={exporter.bridgeWarning ? editorNextNoticeText(exporter.bridgeWarning) : null} downloadUrl={exporter.downloadUrl}
        onClose={exporter.close} onStart={selection => { void exporter.start(selection); }} onCancel={() => { void exporter.cancel(); }} />}
      <EditorNextStatus epoch={engineEpoch} active={status === 'ready'} saveLabel={saveLabel} />
    </main>
      <FilmCraftToolDrawer id="editor-next-tool-drawer"
        title={activeDrawer === 'transcript' ? '转写字幕' : activeDrawer === 'music' ? '整场配乐'
          : aiTarget === 'candidates' ? '选择事件' : aiTarget === 'rough-cut' ? 'AI 粗剪' : 'AI 分析'}
        open={activeDrawer === 'transcript' || activeDrawer === 'music' || (activeDrawer === 'ai' && editingReady)}
        onClose={() => {silencePreview.close();setDrawer(null);}}>
        <div hidden={activeDrawer !== 'ai' || !editingReady}>
          {primaryClip && editingReady && aiTarget==='rough-cut' && <div className="rough-cut-tools" role="group" aria-label="粗剪工具">
            <button type="button" aria-pressed={roughCutTool==='semantic'} disabled={silencePreview.state.phase==='applying'} onClick={()=>{silencePreview.close();setRoughCutTool('semantic');}}>语义粗剪</button>
            <button type="button" aria-pressed={roughCutTool==='voice'} disabled={['generating','applying'].includes(roughCut.state.status)||aiIsBusy(ai.state)} onClick={()=>{setRoughCutTool('voice');silencePreview.open();}}>人声剪停顿（Silero）</button>
          </div>}
          {activeDrawer==='ai' && primaryClip && editingReady && aiTarget==='rough-cut' && roughCutTool==='voice' && silencePreview.state.open && <SilencePreviewPanel model={silencePreview} clipId={primaryClip.id} duration={loaded?.payload.duration??0} ready={editingReady} />}
          {primaryClip && editingReady && (aiTarget!=='rough-cut'||roughCutTool==='semantic') && <AiPanel view={aiTarget === 'candidates' ? 'events' : aiTarget === 'rough-cut' ? 'rough-cut' : 'analysis'}
            state={ai.state} onGenerate={() => { void ai.generate(); }} onApply={() => { void ai.apply(); }}
            roughCut={{ state: roughCut.state, sourceRange: roughCutRange,
              onGenerate: () => { void roughCut.generate(); }, onApply: () => { void roughCut.apply(); } }}
            candidates={loaded ? { clipId: loaded.clipId, events: candidateEvents, placedEvents: eventView.placedEvents,
              placementError: eventView.error, hasTranscript: !!loaded.payload.transcript?.segments.length,
              onEvents: events => setCandidateBatch({ owner: loaded.payload, events }),
              placeEnabled: editingReady && eventView.error === null && save.state.status !== 'conflict' && save.state.status !== 'error',
              onPlace: syncEvents } : undefined} />}
        </div>
        <div hidden={activeDrawer !== 'transcript'}>
          {loaded?.payload.clip && <SourceSrtImport key={`source-srt:${clipId}:${attempt}`} clipId={loaded.clipId}
            disabled={!transcript.canEdit || !!transcript.state.transcript || transcriptDraftDirty || importBusy || placeBusy || save.state.status !== 'saved'}
            onBusy={setSourceSrtBusy} onImported={transcript.refresh} />}

          {loaded?.payload.clip && <ReplaceSourceCaptions key={`replace-source:${clipId}:${attempt}`} clipId={loaded.clipId} epoch={loaded.payload.epoch??''}
            disabled={!editingReady||!transcript.canEdit||transcript.state.waiting||['pending','running'].includes(transcript.state.task?.status??'')||!loaded.payload.transcript?.segments.length||transcriptDraftDirty||transcript.saveState.busy||sourceSrtBusy||placeBusy||save.state.status!=='saved'||!!save.state.draft}
            getSession={save.exportSession} onBusy={setSourceSrtBusy} onReload={reload} />}
          {loaded?.payload.clip && <TranscriptPanel key={`${clipId}:${attempt}`} state={transcript.state} eventId={loaded.payload.clip.eventId || null}
            canEdit={transcript.canEdit && !sourceSrtBusy} saveBusy={transcript.saveState.busy || sourceSrtBusy} saveError={transcript.saveState.error}
            onSaveCaptions={transcript.saveCaptions} onDraftChange={onTranscriptDraftChange} srtUrl={transcript.srtUrl}
            canStart={transcript.canStart && !sourceSrtBusy} canRestart={transcript.canRestart && !sourceSrtBusy}
            onStart={() => { void transcript.start(); }} onRestart={() => { void transcript.restart(); }}
            onRefresh={transcript.refresh} onReloadProject={reload}
            reloadBlockReason={save.state.draft || save.state.status !== 'saved' ? '请先保存改动并处理草稿或保存错误，再重新载入工程。' : null}
            onPlaceCaptions={placeCaptions} canPlaceCaptions={placeView.reason === null && !placeBusy}
            placeBusy={placeBusy} placeError={placeError} placeBlockReason={placeView.reason}
            onAcknowledgeUpdate={transcript.acknowledgeUpdate} />}
        </div>
        <div hidden={activeDrawer !== 'music'}>
          {loaded && nativeGeneration !== undefined && <GlobalEffectPanel key={`effect:${clipId}:${attempt}:${nativeGeneration}`}
            ownerKey={`${loaded.clipId}:${loaded.payload.epoch}:${nativeGeneration}`} generation={nativeGeneration}
            disabled={!editingReady || save.state.status === 'conflict' || save.state.status === 'error'}
            getBridge={() => chapterMusicBridge() as NativeEffectBridge | null} getSaveSession={save.exportSession} onBusy={setEffectBusy} />}

          {loaded && <MusicPanel key={clipId} state={music.state} mix={musicHost.read()?.mix ?? null} applyBlockReason={musicHost.blockReason()}
            onRefresh={() => { void music.refresh(); }} onPreview={id => { void music.preview(id); }} onStopPreview={music.stopPreview}
            onPreviewError={music.previewFailed} onApply={patch => { void music.apply(patch); }} onGenerate={input => { void music.generate(input); }} />}
        </div>
      </FilmCraftToolDrawer>
      <FilmCraftRightTools
        active={activeAction === 'transcript' || activeAction === 'analysis' || activeAction === 'events' || activeAction === 'rough-cut' || activeAction === 'music' ? activeAction : null}
        onSelect={handleAction}
        sourceUnavailable={!primaryClip ? sourceUnavailable : undefined}
      />
    </>
  );
  // 未注入旧外壳时，也保留完整原生工作区（rail / 侧栏 / 滑入槽由 PersistentAppLayout 持有）。
  if (!Shell) return content;
  return (
    <Shell
      projectName={projectName}
      sequenceName="序列"
      saveStatus={save.state.status}
      saveLabel={saveLabel}
      saveTitle={saveTitles[save.state.status]}
      flush={save.save}
    >{content}</Shell>
  );
}
