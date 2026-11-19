import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Activity, Download, FolderOpen, RefreshCw, Search, Upload } from 'lucide-react';
import {
  controlRender,
  getClips,
  getEditor,
  getEditorSequences,
  getHealth,
  getLiveRooms,
  getLiveStorage,
  getProjectRevisions,
  getRenders,
  getRenderFileUrl,
  getVideoUrl,
  queueRender,
} from '../../api/client';
import type {
  ClipDto,
  EditorSequenceSummary,
  HealthResponse,
  LiveRoomStatus,
  LiveStorageStatus,
  ProjectRevisionInfo,
  RenderDto,
} from '../../api/types';
import { getAudioAssets, type AudioAsset } from '../../api/music';
import {
  getFfmpegDownloadState,
  getStudioOutputs,
  getStudioSettings,
  getStudioVersion,
  getTranscriptState,
  importStudioFiles,
  queueStudioOutput,
  type FfmpegDownloadState,
  type StudioOutput,
  type StudioSettings,
} from '../../api/studio';
import { AppShell } from '../../app/AppShell';
import { navigateApp, replaceAppLocation } from '../../app/appNavigation';
import { VideoThumbnail } from '../../components/VideoThumbnail';
import { Button, EmptyState, SegmentedTabs, Toast } from '../../design';
import { ExportsTab } from './ExportsTab';
import { MaterialsTab } from './MaterialsTab';
import { ProjectDetailTab } from './ProjectDetailTab';
import { SettingsTab } from './SettingsTab';
import type { ExportAction, StudioDetail } from './types';
import {
  clipTitle,
  dateFilters,
  errorMessage,
  fileName,
  formatSeconds,
  outputKindLabel,
  outputStatusMeta,
  relativeDay,
  renderStatusMeta,
  thumbStyle,
  withinFilter,
  type DateFilter,
  type SortMode,
  type StudioTab,
} from './workbench';
import './studio.css';
import './workbench.css';
import '../../styles/studio.css';

type RenderControlAction = 'cancel' | 'pause' | 'resume' | 'retry';
type ToastTone = 'ok' | 'error' | 'info';

interface Toast {
  id: number;
  tone: ToastTone;
  text: string;
}

const emptyDetail: StudioDetail = {
  editor: null,
  transcriptState: null,
  backups: [],
  variants: [],
  loading: false,
  error: null,
};

const tabs: Array<{ id: StudioTab; label: string }> = [
  { id: 'projects', label: '项目' },
  { id: 'library', label: '素材库' },
  { id: 'exports', label: '导出队列' },
  { id: 'settings', label: '设置' },
];

function applySettled<T>(
  result: PromiseSettledResult<T>,
  label: string,
  failures: string[],
  apply: (value: T) => void,
): void {
  if (result.status === 'fulfilled') {
    apply(result.value);
  } else {
    failures.push(`${label}：${errorMessage(result.reason)}`);
  }
}

export function StudioPage() {
  const [clips, setClips] = useState<ClipDto[]>([]);
  const [renders, setRenders] = useState<RenderDto[]>([]);
  const [outputs, setOutputs] = useState<StudioOutput[]>([]);
  const [rooms, setRooms] = useState<LiveRoomStatus[]>([]);
  const [storage, setStorage] = useState<LiveStorageStatus | null>(null);
  const [settings, setSettings] = useState<StudioSettings | null>(null);
  const [version, setVersion] = useState<{ appVersion: string } | null>(null);
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [assets, setAssets] = useState<AudioAsset[]>([]);
  const [ffmpeg, setFfmpeg] = useState<FfmpegDownloadState | null>(null);

  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [clipsError, setClipsError] = useState<string | null>(null);
  const [queueError, setQueueError] = useState<string | null>(null);
  const [settingsError, setSettingsError] = useState<string | null>(null);

  const [query, setQuery] = useState('');
  const [dateFilter, setDateFilter] = useState<DateFilter>('all');
  const [sort, setSort] = useState<SortMode>('date');
  const [tab, setTab] = useState<StudioTab>('projects');
  const [selectedClipId, setSelectedClipId] = useState<string | null>(null);
  const [detail, setDetail] = useState<StudioDetail>(emptyDetail);
  const [exportBusy, setExportBusy] = useState<ExportAction | null>(null);
  const [controlBusy, setControlBusy] = useState<string | null>(null);
  const [importBusy, setImportBusy] = useState(false);
  const [toast, setToast] = useState<Toast | null>(null);

  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const toastSeq = useRef(0);

  const notify = useCallback((tone: ToastTone, text: string) => {
    toastSeq.current += 1;
    setToast({ id: toastSeq.current, tone, text });
  }, []);

  useEffect(() => {
    document.title = 'Studio 工作台 · LiveClipper';
  }, []);

  const loadAll = useCallback(async (mode: 'initial' | 'refresh') => {
    if (mode === 'initial') setLoading(true);
    else setRefreshing(true);
    const [
      clipsResult,
      rendersResult,
      outputsResult,
      roomsResult,
      storageResult,
      settingsResult,
      versionResult,
      healthResult,
      ffmpegResult,
      assetsResult,
    ] = await Promise.allSettled([
      getClips(),
      getRenders(),
      getStudioOutputs(),
      getLiveRooms(),
      getLiveStorage(),
      getStudioSettings(),
      getStudioVersion(),
      getHealth(),
      getFfmpegDownloadState(),
      getAudioAssets(),
    ]);

    const failures: string[] = [];
    const clipsFailures: string[] = [];
    const queueFailures: string[] = [];
    const settingsFailures: string[] = [];

    applySettled(clipsResult, '项目列表', clipsFailures, (value) => {
      setClips(value);
      setClipsError(null);
    });
    applySettled(rendersResult, '渲染队列', queueFailures, setRenders);
    applySettled(outputsResult, '导出任务', queueFailures, setOutputs);
    applySettled(roomsResult, '直播房间', failures, (value) => setRooms(value.rooms ?? []));
    applySettled(storageResult, '直播存储', settingsFailures, setStorage);
    applySettled(settingsResult, '设置', settingsFailures, setSettings);
    applySettled(versionResult, '版本', settingsFailures, setVersion);
    applySettled(healthResult, '健康检查', failures, setHealth);
    applySettled(ffmpegResult, 'ffmpeg 状态', settingsFailures, setFfmpeg);
    applySettled(assetsResult, '音频素材', failures, setAssets);

    setClipsError(clipsFailures.length > 0 ? clipsFailures.join('；') : null);
    setQueueError(queueFailures.length > 0 ? queueFailures.join('；') : null);
    setSettingsError(settingsFailures.length > 0 ? settingsFailures.join('；') : null);
    const combined = [...clipsFailures, ...queueFailures, ...settingsFailures, ...failures];
    setLoadError(combined.length > 0 ? combined.join('；') : null);

    if (clipsResult.status === 'fulfilled') {
      const available = clipsResult.value;
      setSelectedClipId((current) => {
        if (current && available.some((clip) => clip.id === current)) return current;
        const fromUrl = new URLSearchParams(window.location.search).get('clip');
        if (fromUrl && available.some((clip) => clip.id === fromUrl)) return fromUrl;
        return null; // 无有效当前/URL 工程时不默认首个素材，等用户明确选择。
      });
    }

    if (mode === 'initial') setLoading(false);
    else setRefreshing(false);
  }, []);

  const loadDetail = useCallback(async (clipId: string) => {
    setDetail((previous) => ({ ...previous, loading: true, error: null }));
    const [editorResult, transcriptResult, backupsResult, variantsResult] = await Promise.allSettled([
      getEditor(clipId),
      getTranscriptState(clipId),
      getProjectRevisions(clipId),
      getEditorSequences(clipId),
    ]);

    const transcriptState = transcriptResult.status === 'fulfilled' ? transcriptResult.value : null;
    const backups = backupsResult.status === 'fulfilled' ? (backupsResult.value as ProjectRevisionInfo[]) : [];
    const variants = variantsResult.status === 'fulfilled' ? (variantsResult.value as EditorSequenceSummary[]) : [];

    if (editorResult.status === 'rejected') {
      setDetail({
        editor: null,
        transcriptState,
        backups,
        variants,
        loading: false,
        error: errorMessage(editorResult.reason),
      });
      return;
    }

    setDetail({
      editor: editorResult.value,
      transcriptState,
      backups,
      variants,
      loading: false,
      error: null,
    });
  }, []);

  useEffect(() => {
    void loadAll('initial');
  }, [loadAll]);

  useEffect(() => {
    if (!selectedClipId) {
      setDetail(emptyDetail);
      return;
    }
    void loadDetail(selectedClipId);
    const nextUrl = `${window.location.pathname}?clip=${encodeURIComponent(selectedClipId)}`;
    replaceAppLocation(nextUrl);
  }, [selectedClipId, loadDetail]);

  useEffect(() => {
    const timer = window.setInterval(() => {
      if (document.visibilityState !== 'visible') return;
      void Promise.allSettled([getRenders(), getStudioOutputs(), getLiveRooms()]).then(
        ([renderResult, outputResult, roomResult]) => {
          if (renderResult.status === 'fulfilled') setRenders(renderResult.value);
          if (outputResult.status === 'fulfilled') setOutputs(outputResult.value);
          if (roomResult.status === 'fulfilled') setRooms(roomResult.value.rooms ?? []);
        },
      );
    }, 4000);
    return () => window.clearInterval(timer);
  }, []);

  const selectedClip = useMemo(
    () => clips.find((clip) => clip.id === selectedClipId) ?? null,
    [clips, selectedClipId],
  );

  const projects = useMemo(() => {
    const keyword = query.trim().toLowerCase();
    return clips
      .filter((clip) => {
        if (keyword) {
          const haystack = `${clipTitle(clip)} ${clip.path}`.toLowerCase();
          if (!haystack.includes(keyword)) return false;
        }
        return withinFilter(clip.createdAt, dateFilter);
      })
      .sort((left, right) => {
        if (sort === 'name') return clipTitle(left).localeCompare(clipTitle(right), 'zh-CN');
        return new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime();
      });
  }, [clips, query, dateFilter, sort]);

  const recentRenders = useMemo(
    () =>
      [...renders]
        .sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime())
        .slice(0, 4),
    [renders],
  );
  const recentOutputs = useMemo(
    () =>
      [...outputs]
        .sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime())
        .slice(0, 4),
    [outputs],
  );
  const activeRooms = rooms.filter((room) => room.running || room.capturing).length;

  const reloadQueues = useCallback(async () => {
    const [renderResult, outputResult] = await Promise.allSettled([getRenders(), getStudioOutputs()]);
    if (renderResult.status === 'fulfilled') setRenders(renderResult.value);
    if (outputResult.status === 'fulfilled') setOutputs(outputResult.value);
  }, []);

  const handleRefresh = useCallback(async () => {
    await loadAll('refresh');
    if (selectedClipId) await loadDetail(selectedClipId);
    await reloadQueues();
  }, [loadAll, loadDetail, reloadQueues, selectedClipId]);

  const handleSelectClip = useCallback((clipId: string) => {
    setSelectedClipId(clipId);
  }, []);

  const handleOpenEditor = useCallback((clipId: string) => {
    void navigateApp(`/editor?clip=${encodeURIComponent(clipId)}`);
  }, []);

  const handleExport = useCallback(
    async (action: ExportAction) => {
      if (!selectedClipId || !detail.editor?.project) {
        notify('error', '请先在左栏选择项目并等待工程载入完成');
        return;
      }
      setExportBusy(action);
      try {
        if (action === 'render') {
          await queueRender(selectedClipId, detail.editor.project);
          notify('ok', '已提交渲染成片任务，进度见「导出队列」');
        } else {
          await queueStudioOutput(selectedClipId, detail.editor.project, action);
          notify('ok', `已提交${outputKindLabel(action)}导出任务`);
        }
        setTab('exports');
        await reloadQueues();
      } catch (cause) {
        notify('error', errorMessage(cause));
      } finally {
        setExportBusy(null);
      }
    },
    [selectedClipId, detail.editor, notify, reloadQueues],
  );

  const handleControlRender = useCallback(
    async (id: string, action: RenderControlAction) => {
      setControlBusy(id);
      try {
        await controlRender(id, action);
        const label =
          action === 'cancel' ? '取消' : action === 'pause' ? '暂停' : action === 'resume' ? '继续' : '重试';
        notify('ok', `渲染任务已${label}`);
        await reloadQueues();
      } catch (cause) {
        notify('error', errorMessage(cause));
      } finally {
        setControlBusy(null);
      }
    },
    [notify, reloadQueues],
  );

  const handleImport = useCallback(
    async (files: File[]) => {
      if (files.length === 0) return;
      setImportBusy(true);
      try {
        const result = await importStudioFiles(files);
        notify('ok', `导入完成：新增 ${result.added}，已存在 ${result.existing}，匹配字幕 ${result.matchedSubtitles}`);
        const clipsResult = await getClips();
        setClips(clipsResult);
        if (result.clipIds && result.clipIds.length > 0) setSelectedClipId(result.clipIds[0]);
      } catch (cause) {
        notify('error', errorMessage(cause));
      } finally {
        setImportBusy(false);
        if (fileInputRef.current) fileInputRef.current.value = '';
      }
    },
    [notify],
  );

  return (
    <AppShell
      eyebrow="素材 · 切片库"
      title="素材"
      breadcrumbs={['LiveClipper', '素材']}
      subtitle="切片库、素材导入与导出渲染；缩略图预览保持深色"
      contentClassName="lc-app-shell__content--flush"
      topbarActions={
        <>
          <span className={`lc-app-shell__status${health?.ok ? ' is-ok' : ''}`}>
            <Activity size={12} aria-hidden="true" />
            后端 {health?.ok ? '已连接' : '未连接'} · v{version?.appVersion ?? '未接入'}
          </span>
          <input
            ref={fileInputRef}
            className="wb-hidden-input"
            type="file"
            multiple
            accept=".mp4,.mkv,.mov,.flv,.webm,.m4v,.ts,.srt,.vtt,.ass,.ssa"
            onChange={(event) => void handleImport(Array.from(event.target.files ?? []))}
          />
          <Button
            variant="outline"
            size="sm"
            leading={<Upload size={13} aria-hidden="true" />}
            disabled={importBusy}
            onClick={() => fileInputRef.current?.click()}
          >
            {importBusy ? '导入中…' : '导入素材'}
          </Button>
          {selectedClip ? (
            <Button
              variant="solid"
              size="sm"
              leading={<FolderOpen size={13} aria-hidden="true" />}
              onClick={() => handleOpenEditor(selectedClip.id)}
            >
              在编辑器中打开
            </Button>
          ) : null}
          <Button
            variant="ghost"
            size="sm"
            leading={<RefreshCw size={13} aria-hidden="true" />}
            disabled={refreshing}
            onClick={() => void handleRefresh()}
          >
            {refreshing ? '刷新中…' : '刷新'}
          </Button>
        </>
      }
    >
      <div className="lc-studio-page">
        <div className="studio-workbench">
          <div className="studio-actionbar">
            <SegmentedTabs
              ariaLabel="工作台分区"
              value={tab}
              onChange={setTab}
              items={tabs.map((item) => ({ value: item.id, label: item.label }))}
            />
            <label className="lc-studio-search">
              <Search size={13} aria-hidden="true" />
              <input
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="搜索项目 / 路径"
                aria-label="搜索项目"
              />
            </label>
          </div>

          {loadError ? (
            <div className="wb-banner" role="status">
              <span className="wb-ellipsis" title={loadError}>
                部分数据未加载：{loadError}
              </span>
              <button type="button" className="wb-button wb-button--ghost" onClick={() => void handleRefresh()}>
                重试
              </button>
            </div>
          ) : null}

          <div className="wb-layout">
            <aside className="wb-projects" aria-label="项目列表">
              <header className="wb-panel-head">
                <span>项目</span>
                <span className="wb-count">
                  {projects.length} / {clips.length}
                </span>
              </header>
              <div className="wb-projects-filter">
                <div className="studio-filters" role="group" aria-label="日期过滤">
                  {dateFilters.map((filter) => (
                    <button
                      key={filter.id}
                      type="button"
                      className={dateFilter === filter.id ? 'is-active' : ''}
                      onClick={() => setDateFilter(filter.id)}
                    >
                      {filter.label}
                    </button>
                  ))}
                </div>
                <label className="wb-sort">
                  排序
                  <select value={sort} onChange={(event) => setSort(event.target.value as SortMode)} aria-label="项目排序">
                    <option value="date">按日期</option>
                    <option value="name">按名称</option>
                  </select>
                </label>
              </div>
              <div className="wb-project-list">
                {loading && clips.length === 0 ? (
                  <EmptyState compact title="正在读取项目…" description="GET /api/clips" />
                ) : clipsError ? (
                  <EmptyState
                    compact
                    title="项目列表读取失败"
                    description={clipsError}
                    action={
                      <Button variant="outline" size="sm" onClick={() => void handleRefresh()}>
                        重试
                      </Button>
                    }
                  />
                ) : projects.length === 0 ? (
                  <EmptyState
                    compact
                    title="没有匹配的项目"
                    description={clips.length === 0 ? '先通过「导入素材」导入视频。' : '换个关键词或日期过滤试试。'}
                  />
                ) : (
                  projects.map((clip, index) => (
                    <button
                      key={clip.id}
                      type="button"
                      className={`wb-project${clip.id === selectedClipId ? ' is-selected' : ''}`}
                      onClick={() => handleSelectClip(clip.id)}
                      title={clip.path}
                    >
                      <span className="wb-project-thumb" style={thumbStyle(index)}>
                        <VideoThumbnail src={getVideoUrl(clip.id)} alt={`${clipTitle(clip)} 缩略图`} seekRatio={0.18} />
                      </span>
                      <span className="wb-project-text">
                        <strong className="wb-ellipsis">{clipTitle(clip)}</strong>
                        <span>{relativeDay(clip.createdAt)}</span>
                      </span>
                    </button>
                  ))
                )}
              </div>
            </aside>

            <section className="wb-center" aria-label="素材工作区">
              {tab === 'projects' ? (
                <ProjectDetailTab
                  clip={selectedClip}
                  detail={detail}
                  assets={assets}
                  exportBusy={exportBusy}
                  onExport={(action) => void handleExport(action)}
                  onOpenEditor={handleOpenEditor}
                  onRefreshDetail={() => {
                    if (selectedClipId) void loadDetail(selectedClipId);
                  }}
                />
              ) : null}
              {tab === 'library' ? (
                <MaterialsTab
                  clips={clips}
                  assets={assets}
                  selectedClipId={selectedClipId}
                  loading={loading}
                  error={clipsError}
                  onSelectClip={(clipId) => {
                    handleSelectClip(clipId);
                    setTab('projects');
                  }}
                  onOpenEditor={handleOpenEditor}
                  onRefresh={() => void handleRefresh()}
                />
              ) : null}
              {tab === 'exports' ? (
                <ExportsTab
                  renders={renders}
                  outputs={outputs}
                  clips={clips}
                  selectedClip={selectedClip}
                  detail={detail}
                  loading={loading}
                  error={queueError}
                  exportBusy={exportBusy}
                  controlBusy={controlBusy}
                  onExport={(action) => void handleExport(action)}
                  onControlRender={(id, action) => void handleControlRender(id, action)}
                  onRefresh={() => void handleRefresh()}
                />
              ) : null}
              {tab === 'settings' ? (
                <SettingsTab
                  settings={settings}
                  storage={storage}
                  version={version}
                  health={health}
                  ffmpeg={ffmpeg}
                  loading={loading}
                  error={settingsError}
                  onRefresh={() => void handleRefresh()}
                />
              ) : null}
            </section>

            <aside className="wb-overview" aria-label="概览">
              <header className="wb-panel-head">
                <span>概览</span>
                <span className="wb-count">实时</span>
              </header>
              <div className="wb-overview-body">
                <div className="wb-overview-stats">
                  <div>
                    <span>项目</span>
                    <strong>{clips.length}</strong>
                  </div>
                  <div>
                    <span>渲染</span>
                    <strong>{renders.length}</strong>
                  </div>
                  <div>
                    <span>导出</span>
                    <strong>{outputs.length}</strong>
                  </div>
                  <div>
                    <span>直播中</span>
                    <strong>{activeRooms}</strong>
                  </div>
                </div>

                <section className="wb-card">
                  <header className="wb-card-head">
                    <span>当前项目</span>
                    <span className="wb-count">{selectedClip ? '已选中' : '未选中'}</span>
                  </header>
                  {selectedClip ? (
                    <div className="wb-card-body">
                      <strong className="wb-ellipsis" title={clipTitle(selectedClip)}>
                        {clipTitle(selectedClip)}
                      </strong>
                      <span className="wb-media-meta">
                        {detail.editor
                          ? `${formatSeconds(detail.editor.duration)} · ${detail.editor.width}×${detail.editor.height}`
                          : '工程读取中…'}
                      </span>
                      <span className="wb-media-meta">
                        {detail.editor ? `${detail.editor.project.timeline?.length ?? 0} 个片段` : ''}
                        {detail.backups[0] ? ` · ${relativeDay(detail.backups[0].createdAt)}` : ''}
                      </span>
                      <button type="button" className="wb-button wb-button--ghost" onClick={() => setTab('projects')}>
                        查看详情
                      </button>
                    </div>
                  ) : (
                    <div className="wb-card-empty">选择左侧项目查看工程信息</div>
                  )}
                </section>

                <section className="wb-card">
                  <header className="wb-card-head">
                    <span>最近的渲染</span>
                    <button type="button" className="wb-link" onClick={() => setTab('exports')}>
                      全部
                    </button>
                  </header>
                  {recentRenders.length === 0 ? (
                    <div className="wb-card-empty">还没有渲染任务</div>
                  ) : (
                    <ul className="wb-mini-list">
                      {recentRenders.map((render) => {
                        const meta = renderStatusMeta(render.status);
                        return (
                          <li key={render.id}>
                            <span className={`wb-badge wb-badge--${meta.tone}`}>{meta.label}</span>
                            <span className="wb-mini-text wb-ellipsis" title={render.outputPath}>
                              {fileName(render.outputPath)}
                            </span>
                            {render.status === 'completed' ? (
                              <a
                                className="wb-icon-link"
                                href={getRenderFileUrl(render.id)}
                                download
                                aria-label="下载渲染文件"
                                title="下载渲染文件"
                              >
                                <Download size={13} aria-hidden="true" />
                              </a>
                            ) : (
                              <span className="wb-mini-time">{relativeDay(render.createdAt)}</span>
                            )}
                          </li>
                        );
                      })}
                    </ul>
                  )}
                </section>

                <section className="wb-card">
                  <header className="wb-card-head">
                    <span>导出任务</span>
                    <button type="button" className="wb-link" onClick={() => setTab('exports')}>
                      全部
                    </button>
                  </header>
                  {recentOutputs.length === 0 ? (
                    <div className="wb-card-empty">还没有素材包 / 草稿导出</div>
                  ) : (
                    <ul className="wb-mini-list">
                      {recentOutputs.map((output) => {
                        const meta = outputStatusMeta(output.status);
                        return (
                          <li key={output.id}>
                            <span className="wb-mini-kind">{outputKindLabel(output.kind)}</span>
                            <span className={`wb-badge wb-badge--${meta.tone}`}>{meta.label}</span>
                            <span className="wb-mini-time">{relativeDay(output.createdAt)}</span>
                          </li>
                        );
                      })}
                    </ul>
                  )}
                </section>

                <section className="wb-card">
                  <header className="wb-card-head">
                    <span>直播房间</span>
                    <a className="wb-link" href="/live">
                      打开
                    </a>
                  </header>
                  {rooms.length === 0 ? (
                    <div className="wb-card-empty">还没有监控中的直播房间</div>
                  ) : (
                    <ul className="wb-mini-list">
                      {rooms.map((room) => (
                        <li key={room.roomId}>
                          <span className="wb-mini-kind">{room.roomId}</span>
                          <span className="wb-mini-text wb-ellipsis">
                            {room.room?.title || room.room?.streamer || (room.running ? '直播中' : '未开播')}
                          </span>
                          <span className={`wb-badge wb-badge--${room.running || room.capturing ? 'ok' : 'muted'}`}>
                            {room.running || room.capturing ? '录制中' : '空闲'}
                          </span>
                        </li>
                      ))}
                    </ul>
                  )}
                </section>
              </div>
            </aside>
          </div>
        </div>

        <Toast
          open={toast !== null}
          id={toast?.id}
          tone={toast?.tone === 'ok' ? 'success' : (toast?.tone ?? 'neutral')}
          message={toast?.text ?? ''}
          duration={4500}
          onClose={() => setToast(null)}
        />
      </div>
    </AppShell>
  );
}
