import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, LoaderCircle, RefreshCw } from 'lucide-react';
import { getClips, getEditor, saveEditor } from '../../api/client';
import type { ClipDto, EditorProject } from '../../api/types';
import {
  cancelMusicJob,
  generateMusicDucking,
  getMusicAssets,
  getMusicDucking,
  getMusicJob,
  getMusicStatus,
  setMusicPreferences,
  startModelDownload,
  cancelModelDownload,
  isMusicDownloadBusy,
  isMusicModelBusy,
  startMusicGeneration,
  type MusicDuckingEnvelope,
  type MusicGenerateInput,
  type MusicJobState,
  type MusicLibraryAsset,
  type MusicStatus,
} from '../../api/music';
import { AppShell } from '../../app/AppShell';
import { navigateApp, replaceAppLocation } from '../../app/appNavigation';
import { Badge, Button, Toast } from '../../design';
import { GeneratePanel } from './GeneratePanel';
import { LibraryPanel, type ToastTone } from './LibraryPanel';
import { ProjectPanel } from './ProjectPanel';
import { errorMessage } from './utils';
import './music.css';
import '../../styles/music.css';

const JOB_STORAGE_KEY = 'liveclipper.music-job-id';

interface Toast {
  id: number;
  tone: ToastTone;
  text: string;
}

export function MusicStudioPage() {
  const [status, setStatus] = useState<MusicStatus | null>(null);
  const [assets, setAssets] = useState<MusicLibraryAsset[]>([]);
  const [assetsLoading, setAssetsLoading] = useState(true);
  const [clips, setClips] = useState<ClipDto[]>([]);
  const [fatalError, setFatalError] = useState<string | null>(null);

  const [clipId, setClipId] = useState('');
  const [project, setProject] = useState<EditorProject | null>(null);
  const [projectLoading, setProjectLoading] = useState(false);
  const [projectError, setProjectError] = useState<string | null>(null);

  const [selectedModelId, setSelectedModelId] = useState('');
  const [job, setJob] = useState<MusicJobState | null>(null);
  const [submittingGeneration, setSubmittingGeneration] = useState(false);
  const [downloadBusy, setDownloadBusy] = useState(false);
  const [applying, setApplying] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [ducking, setDucking] = useState<MusicDuckingEnvelope | null>(null);
  const [duckingLoading, setDuckingLoading] = useState(false);

  const [toast, setToast] = useState<Toast | null>(null);
  const toastSeq = useRef(0);
  const previousDownloadState = useRef<string | null>(null);

  const showToast = useCallback((tone: ToastTone, text: string) => {
    toastSeq.current += 1;
    setToast({ id: toastSeq.current, tone, text });
  }, []);

  useEffect(() => {
    if (!toast) return undefined;
    const timer = window.setTimeout(() => {
      setToast((current) => (current && current.id === toast.id ? null : current));
    }, 4500);
    return () => window.clearTimeout(timer);
  }, [toast]);

  useEffect(() => {
    document.title = '音乐工作室 · LiveClipper';
    return () => {
      document.title = 'LiveClipper Studio';
    };
  }, []);

  const applyStatus = useCallback((next: MusicStatus) => {
    setStatus(next);
    setSelectedModelId((current) =>
      next.models.some((model) => model.id === current)
        ? current
        : ((next.models.find((model) => model.installed) ?? next.models[0])?.id ?? ''),
    );
  }, []);

  const loadStatus = useCallback(async (): Promise<MusicStatus | null> => {
    try {
      const next = await getMusicStatus();
      applyStatus(next);
      return next;
    } catch {
      return null;
    }
  }, [applyStatus]);

  const loadAssets = useCallback(async (): Promise<void> => {
    setAssetsLoading(true);
    try {
      setAssets(await getMusicAssets());
    } catch {
      /* 保留旧数据；刷新错误由调用方提示 */
    } finally {
      setAssetsLoading(false);
    }
  }, []);

  const loadProject = useCallback(async (id: string): Promise<void> => {
    if (!id) {
      setProject(null);
      return;
    }
    setProjectLoading(true);
    setProjectError(null);
    try {
      const payload = await getEditor(id);
      setProject(payload.project);
    } catch (error) {
      setProject(null);
      setProjectError(errorMessage(error));
    } finally {
      setProjectLoading(false);
    }
  }, []);

  const loadDucking = useCallback(async (id: string): Promise<void> => {
    if (!id) {
      setDucking(null);
      return;
    }
    try {
      setDucking(await getMusicDucking(id));
    } catch {
      /* 404 表示还没有生成过 ducking 包络 */
      setDucking(null);
    }
  }, []);

  /* 首次加载：状态、素材库、项目列表并行读取。 */
  useEffect(() => {
    let cancelled = false;
    setAssetsLoading(true);
    void Promise.allSettled([getMusicStatus(), getMusicAssets(), getClips()]).then(
      ([statusResult, assetsResult, clipsResult]) => {
        if (cancelled) return;
        if (statusResult.status === 'fulfilled') {
          applyStatus(statusResult.value);
        }
        if (assetsResult.status === 'fulfilled') {
          setAssets(assetsResult.value);
        }
        if (clipsResult.status === 'fulfilled') {
          setClips(clipsResult.value);
        }
        setAssetsLoading(false);
        if (
          statusResult.status === 'rejected' &&
          assetsResult.status === 'rejected' &&
          clipsResult.status === 'rejected'
        ) {
          setFatalError(errorMessage(statusResult.reason));
        } else {
          setFatalError(null);
        }
      },
    );
    return () => {
      cancelled = true;
    };
  }, [applyStatus]);

  /* 默认项目：URL ?clip= 优先，其次第一个项目。 */
  useEffect(() => {
    if (clips.length === 0) return;
    setClipId((current) => {
      if (current && clips.some((clip) => clip.id === current)) return current;
      const requested = new URLSearchParams(window.location.search).get('clip');
      const matched = requested ? clips.find((clip) => clip.id === requested) : undefined;
      return matched?.id ?? clips[0].id;
    });
  }, [clips]);

  /* 选中项目变化时读取工程配乐配置。 */
  useEffect(() => {
    void loadProject(clipId);
  }, [clipId, loadProject]);

  /* 配乐或项目变化时读取已保存的 ducking 包络。 */
  useEffect(() => {
    void loadDucking(clipId);
  }, [clipId, project?.musicAssetId, loadDucking]);

  /* 刷新后恢复未完成的生成任务。 */
  useEffect(() => {
    const savedJobId = window.sessionStorage.getItem(JOB_STORAGE_KEY);
    if (!savedJobId) return undefined;
    let cancelled = false;
    void getMusicJob(savedJobId)
      .then((next) => {
        if (!cancelled) setJob(next);
      })
      .catch(() => {
        window.sessionStorage.removeItem(JOB_STORAGE_KEY);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!job) return;
    const active = job.status === 'queued' || job.status === 'running';
    if (active) {
      window.sessionStorage.setItem(JOB_STORAGE_KEY, job.id);
    } else {
      window.sessionStorage.removeItem(JOB_STORAGE_KEY);
    }
  }, [job]);

  const jobId = job?.id ?? null;
  const jobStatusValue = job?.status ?? null;
  const activeJob = jobStatusValue === 'queued' || jobStatusValue === 'running';

  /* 生成任务轮询。 */
  useEffect(() => {
    if (!jobId || !activeJob) return undefined;
    let cancelled = false;
    const timer = window.setInterval(() => {
      void getMusicJob(jobId)
        .then((next) => {
          if (cancelled) return;
          setJob(next);
          if (next.status === 'completed') {
            showToast(
              'success',
              next.analysis?.bpm
                ? `音乐生成完成（BPM ${next.analysis.bpm.toFixed(1)}），结果已导入音频库`
                : '音乐生成完成，结果已导入音频库',
            );
            void loadAssets();
          } else if (next.status === 'failed') {
            showToast('error', next.error || '音乐生成失败');
          }
        })
        .catch((error: unknown) => {
          if (!cancelled) showToast('error', errorMessage(error));
        });
    }, 1000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [jobId, activeJob, showToast, loadAssets]);

  // 含 cancelling 与后端真实 busy（failed 后 worker 仍在退出、本地生成中）：继续轮询直到后端不再占用。
  const downloadActive = isMusicDownloadBusy(status?.download.state) || isMusicModelBusy(status);

  /* 模型下载轮询 + 状态切换提示。 */
  useEffect(() => {
    if (!downloadActive) return undefined;
    const timer = window.setInterval(() => {
      void loadStatus();
    }, 1600);
    return () => window.clearInterval(timer);
  }, [downloadActive, loadStatus]);

  useEffect(() => {
    const state = status?.download.state ?? null;
    if (previousDownloadState.current !== null && previousDownloadState.current !== state) {
      if (state === 'completed') {
        showToast('success', '音乐模型下载完成，可以开始生成了');
      } else if (state === 'failed') {
        showToast('error', status?.download.error || '音乐模型下载失败');
      } else if (state === 'cancelled') {
        showToast('info', '音乐模型下载已取消（已下载的部分文件可能保留）');
      }
    }
    previousDownloadState.current = state;
  }, [status?.download.state, status?.download.error, showToast]);

  const handleRefresh = useCallback(async () => {
    setRefreshing(true);
    try {
      const [statusNext, assetsNext, clipsNext] = await Promise.all([
        getMusicStatus(),
        getMusicAssets(),
        getClips(),
      ]);
      applyStatus(statusNext);
      setAssets(assetsNext);
      setClips(clipsNext);
      setFatalError(null);
      if (clipId) await loadProject(clipId);
      showToast('info', '已刷新音乐工作室数据');
    } catch (error) {
      showToast('error', errorMessage(error));
    } finally {
      setRefreshing(false);
    }
  }, [applyStatus, clipId, loadProject, showToast]);

  const handleRefreshAssets = useCallback(async () => {
    await loadAssets();
  }, [loadAssets]);

  const handleSelectClip = useCallback((nextClipId: string) => {
    setClipId(nextClipId);
    const url = new URL(window.location.href);
    url.searchParams.set('clip', nextClipId);
    replaceAppLocation(url);
  }, []);

  const handleSelectModel = useCallback(
    (modelId: string) => {
      setSelectedModelId(modelId);
      if (!status) return;
      void setMusicPreferences(status.allowDownload, modelId)
        .then((next) => applyStatus(next))
        .catch((error: unknown) => {
          showToast('error', errorMessage(error));
          void loadStatus();
        });
    },
    [status, applyStatus, showToast, loadStatus],
  );

  const handleToggleAllowDownload = useCallback(
    (allowed: boolean) => {
      if (!status) return;
      void setMusicPreferences(allowed, selectedModelId)
        .then((next) => applyStatus(next))
        .catch((error: unknown) => {
          showToast('error', errorMessage(error));
          void loadStatus();
        });
    },
    [status, selectedModelId, applyStatus, showToast, loadStatus],
  );

  const handleDownloadModel = useCallback(async (modelId = selectedModelId) => {
    if (!status) return;
    setDownloadBusy(true);
    try {
      const state = await startModelDownload(modelId, status.allowDownload);
      setStatus((current) => (current ? { ...current, download: state } : current));
      showToast('info', state.detail || '模型下载任务已启动');
    } catch (error) {
      showToast('error', errorMessage(error));
    } finally {
      setDownloadBusy(false);
      void loadStatus();
    }
  }, [status, selectedModelId, showToast, loadStatus]);

  const handleCancelDownload = useCallback(async () => {
    try {
      // 202 = 已请求取消（cancelling），不是已取消；最终状态由轮询得到。
      const res = await cancelModelDownload();
      setStatus((current) => (current ? { ...current, download: res.download } : current));
      showToast('info', '已请求取消下载，等待后台任务退出');
    } catch (error) {
      showToast('error', errorMessage(error));
    } finally {
      void loadStatus();
    }
  }, [showToast, loadStatus]);

  const handleGenerate = useCallback(
    async (input: MusicGenerateInput) => {
      setSubmittingGeneration(true);
      try {
        const next = await startMusicGeneration(input);
        setJob(next);
        showToast(
          'info',
          input.provider === 'custom-http'
            ? '生成任务已提交，正在等待线上接口返回音乐'
            : '生成任务已提交，正在合成音乐',
        );
      } catch (error) {
        showToast('error', errorMessage(error));
        throw error;
      } finally {
        setSubmittingGeneration(false);
      }
    },
    [showToast],
  );

  const handleCancelJob = useCallback(async () => {
    if (!job) return;
    try {
      const response = await cancelMusicJob(job.id);
      const next = await getMusicJob(job.id);
      setJob(next);
      showToast(
        response.cancelled ? 'info' : 'error',
        response.cancelled ? '已请求取消当前生成任务' : '任务已经结束',
      );
    } catch (error) {
      showToast('error', errorMessage(error));
    }
  }, [job, showToast]);

  const applyProjectAsset = useCallback(
    async (field: 'music' | 'effect', assetId: string | null) => {
      if (!clipId) {
        showToast('error', '请先在右侧选择一个项目');
        return;
      }
      setApplying(true);
      try {
        /* 先拿最新工程，避免把其它字段的旧值写回去。 */
        const fresh = await getEditor(clipId);
        const nextProject: EditorProject =
          field === 'music'
            ? { ...fresh.project, musicAssetId: assetId }
            : { ...fresh.project, effectAssetId: assetId };
        await saveEditor(clipId, nextProject, fresh.revision);
        setProject(nextProject);
        showToast('success', assetId ? '已写入工程配乐配置' : '已清除工程配乐配置');
      } catch (error) {
        showToast('error', errorMessage(error));
      } finally {
        setApplying(false);
      }
    },
    [clipId, showToast],
  );

  const handleApplyMusic = useCallback(
    (assetId: string | null) => applyProjectAsset('music', assetId),
    [applyProjectAsset],
  );

  const handleApplyEffect = useCallback(
    (assetId: string | null) => applyProjectAsset('effect', assetId),
    [applyProjectAsset],
  );

  const handleGenerateDucking = useCallback(async () => {
    if (!clipId) {
      showToast('error', '请先在右侧选择一个项目');
      return;
    }
    setDuckingLoading(true);
    try {
      const response = await generateMusicDucking(clipId);
      if (response.envelope) setDucking(response.envelope);
      if (response.degraded) {
        showToast('info', response.reason ?? '当前没有可用人声区间，ducking 已降级');
      } else if (response.saved) {
        showToast(
          'success',
          `已生成 ducking 包络：${response.voiceIntervals ?? 0} 段人声 / ${response.keyframeCount ?? 0} 个关键帧`,
        );
      } else {
        showToast('info', response.reason ?? 'ducking 包络未写入工程');
      }
    } catch (error) {
      showToast('error', errorMessage(error));
    } finally {
      setDuckingLoading(false);
    }
  }, [clipId, showToast]);

  const currentMusicAnalysis = useMemo(() => {
    const musicAssetId = project?.musicAssetId;
    if (!musicAssetId) return null;
    return assets.find((asset) => asset.id === musicAssetId)?.analysis ?? null;
  }, [assets, project?.musicAssetId]);

  const editorHref = clipId ? `/?clip=${encodeURIComponent(clipId)}` : '/';

  return (
    <AppShell
      eyebrow="音乐 · 工作室"
      title="音乐"
      breadcrumbs={['LiveClipper', '音乐']}
      subtitle="AI 音乐生成、素材库与工程配乐；波形预览保持深色"
      contentClassName="lc-app-shell__content--flush"
      topbarActions={
        <>
          <Badge tone={status ? 'green' : 'neutral'} dot>
            后端 {status ? '已连接' : '读取中'}
          </Badge>
          <Badge tone={status ? (status.pythonAvailable ? 'green' : 'amber') : 'neutral'} dot>
            Python {status ? (status.pythonAvailable ? '可用' : '不可用') : '—'}
          </Badge>
          <Button variant="outline" size="sm" onClick={() => void navigateApp(editorHref)}>
            剪辑工作区
          </Button>
          <Button
            variant="ghost"
            size="sm"
            leading={
              refreshing ? (
                <LoaderCircle size={13} className="music-spin" aria-hidden="true" />
              ) : (
                <RefreshCw size={13} aria-hidden="true" />
              )
            }
            disabled={refreshing}
            onClick={() => void handleRefresh()}
          >
            刷新
          </Button>
        </>
      }
    >
      <div className="lc-music-page">
      <section className="music-main" aria-label="音乐工作台">
        {fatalError ? (
          <div className="music-fatal" role="alert">
            <AlertTriangle size={28} aria-hidden="true" />
            <h2>无法连接音乐后端</h2>
            <p>{fatalError}</p>
            <p className="music-hint">
              确认 LiveClipper 后端（127.0.0.1:5179）正在运行，然后点下面的按钮重试。
            </p>
            <button
              type="button"
              className="music-button music-button--primary"
              onClick={() => void handleRefresh()}
            >
              重试
            </button>
          </div>
        ) : (
          <div className="music-layout">
            <LibraryPanel
              assets={assets}
              loading={assetsLoading}
              onRefresh={handleRefreshAssets}
              onToast={showToast}
              projectMusicAssetId={project?.musicAssetId ?? null}
              applying={applying}
              onApplyMusic={handleApplyMusic}
            />
            <GeneratePanel
              status={status}
              assets={assets}
              selectedModelId={selectedModelId}
              job={job}
              submitting={submittingGeneration}
              downloading={downloadBusy}
              applying={applying}
              onSelectModel={handleSelectModel}
              onToggleAllowDownload={handleToggleAllowDownload}
              onDownloadModel={handleDownloadModel}
              onCancelDownload={handleCancelDownload}
              onRefreshStatus={loadStatus}
              onGenerate={handleGenerate}
              onCancelJob={handleCancelJob}
              onApplyMusic={handleApplyMusic}
            />
            <ProjectPanel
              clips={clips}
              clipId={clipId}
              project={project}
              projectLoading={projectLoading}
              projectError={projectError}
              assets={assets}
              applying={applying}
              musicAnalysis={currentMusicAnalysis}
              onSelectClip={handleSelectClip}
              onReloadProject={() => loadProject(clipId)}
              onApplyMusic={handleApplyMusic}
              onApplyEffect={handleApplyEffect}
              ducking={ducking}
              duckingLoading={duckingLoading}
              onGenerateDucking={handleGenerateDucking}
            />
          </div>
        )}
      </section>

      <Toast
        open={toast !== null}
        id={toast?.id}
        tone={toast?.tone ?? 'neutral'}
        message={toast?.text ?? ''}
        duration={4500}
        onClose={() => setToast(null)}
      />
      </div>
    </AppShell>
  );
}
