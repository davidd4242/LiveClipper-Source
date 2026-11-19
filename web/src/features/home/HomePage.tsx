/**
 * 工作台首页（/）：外壳内第一屏。
 *
 * - 数据全部只读，来自既有后端接口：/api/status、/api/live/auto-clip、/api/clips、/api/jobs、/api/live/storage。
 * - 单项失败不影响其他卡片；失败时保留最近一次成功数据并给出提示与重试。
 * - 点击切片沿用编辑器深链 `/?clip=`，与素材页/音乐页打开剪辑的既有方式一致。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Activity,
  AlertTriangle,
  ArrowRight,
  Film,
  HardDrive,
  LoaderCircle,
  Radio,
  RefreshCw,
  Scissors,
  ServerCog,
  Timer,
} from 'lucide-react';
import { Badge, Button, Card, EmptyState, IconButton, Panel, StatusDot, Toast } from '../../design';
import { getClips, getJobs, getLiveStorage } from '../../api/client';
import type { ClipDto, JobDto, LiveStorageStatus } from '../../api/types';
import { getAutoClip, getLegacyStatus, type AutoClipPayload, type LegacyStatus } from '../live/liveApi';
import { formatBytes, formatNumber, formatTime } from '../live/format';
import { navigateApp } from '../../app/appNavigation';
import { reportHomeStartup } from '../../app/startupReadiness';
import './home.css';

const REFRESH_MS = 30_000;

type SourceKey = 'status' | 'autoClip' | 'clips' | 'jobs' | 'storage';

interface HomeData {
  status: LegacyStatus | null;
  autoClip: AutoClipPayload | null;
  clips: ClipDto[];
  jobs: JobDto[];
  storage: LiveStorageStatus | null;
}

const EMPTY_DATA: HomeData = {
  status: null,
  autoClip: null,
  clips: [],
  jobs: [],
  storage: null,
};

const SOURCE_LABELS: Record<SourceKey, string> = {
  status: '直播状态',
  autoClip: '自动切片',
  clips: '切片列表',
  jobs: '任务队列',
  storage: '存储状态',
};

const JOB_STATUS_LABELS: Record<string, string> = {
  running: '进行中',
  queued: '排队中',
  pending: '等待中',
  succeeded: '已完成',
  completed: '已完成',
  failed: '失败',
  cancelled: '已取消',
};

const JOB_TYPE_LABELS: Record<string, string> = {
  render: '渲染',
  transcribe: '转写',
  analyze: 'AI 分析',
  'auto-plan': '自动方案',
  'trim-silence': '静音裁剪',
  export: '导出',
};

function jobStatusLabel(status: string): string {
  return JOB_STATUS_LABELS[status] ?? status;
}

function jobTypeLabel(type: string): string {
  return JOB_TYPE_LABELS[type] ?? type;
}

function go(href: string) {
  void navigateApp(href);
}

/** 从路径里取文件名；Windows 反斜杠与 POSIX 都兼容。 */
function baseName(path: string): string {
  const normalized = path.replace(/\\/g, '/');
  const name = normalized.slice(normalized.lastIndexOf('/') + 1);
  return name || path;
}

function todayLabel(): string {
  return new Date().toLocaleDateString('zh-CN', { month: 'long', day: 'numeric', weekday: 'long' });
}

export function HomePage() {
  const [cacheScan,setCacheScan]=useState<{token:string;bytes:number;count:number;excluded:string[]}|null>(null);
  const [cacheBusy,setCacheBusy]=useState(false);
  const [cacheResult,setCacheResult]=useState('');
  async function cacheAction(clean=false) {
    setCacheBusy(true);setCacheResult('');
    try {
      const response=await fetch('/api/cache-cleanup',clean?{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({token:cacheScan?.token})}:undefined);
      const result=await response.json();if(!response.ok)throw new Error(result.error||'缓存扫描/清理失败');
      if(clean) {setCacheScan(null);setCacheResult(`已清理 ${result.deletedCount} 个波形缓存，释放 ${formatBytes(result.deletedBytes)}；跳过 ${result.skippedCount} 个变化或占用文件。`);}
      else setCacheScan(result);
    } catch(error) {setCacheResult(error instanceof Error?error.message:String(error));}
    finally {setCacheBusy(false);}
  }

  const [data, setData] = useState<HomeData>(EMPTY_DATA);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [failures, setFailures] = useState<SourceKey[]>([]);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  const mountedRef = useRef(true);
  const [toast, setToast] = useState<{ id: number; tone: 'success' | 'error'; text: string } | null>(null);
  const toastSeq = useRef(0);
  const manualRefreshRef = useRef(false);
  const notify = useCallback((tone: 'success' | 'error', text: string) => {
    toastSeq.current += 1;
    setToast({ id: toastSeq.current, tone, text });
  }, []);

  // 启动门槛：只有当前 effect 轮次发起的首次加载才有资格上报（严格模式重复 effect 时旧请求不抢发）。
  const startupRunRef = useRef(0);
  const [startupResult, setStartupResult] = useState<'ready' | 'error' | null>(null);
  useEffect(() => {
    if (startupResult) reportHomeStartup(startupResult);
  }, [startupResult]);

  const load = useCallback(async (mode: 'initial' | 'refresh', run = 0) => {
    if (mode === 'refresh') setRefreshing(true);
    const results = await Promise.allSettled([
      getLegacyStatus(),
      getAutoClip(),
      getClips(),
      getJobs(),
      getLiveStorage(),
    ]);
    if (!mountedRef.current) return;

    const [status, autoClip, clips, jobs, storage] = results;
    const failed: SourceKey[] = [];
    if (status.status === 'rejected') failed.push('status');
    if (autoClip.status === 'rejected') failed.push('autoClip');
    if (clips.status === 'rejected') failed.push('clips');
    if (jobs.status === 'rejected') failed.push('jobs');
    if (storage.status === 'rejected') failed.push('storage');

    setData((previous) => ({
      status: status.status === 'fulfilled' ? status.value : previous.status,
      autoClip: autoClip.status === 'fulfilled' ? autoClip.value : previous.autoClip,
      clips: clips.status === 'fulfilled' ? clips.value : previous.clips,
      jobs: jobs.status === 'fulfilled' ? jobs.value : previous.jobs,
      storage: storage.status === 'fulfilled' ? storage.value : previous.storage,
    }));
    setFailures(failed);
    if (mode === 'initial' && run === startupRunRef.current) {
      setStartupResult(failed.length === 0 ? 'ready' : 'error');
    }
    if (manualRefreshRef.current) {
      manualRefreshRef.current = false;
      notify(
        failed.length === 0 ? 'success' : 'error',
        failed.length === 0 ? '数据已刷新' : `刷新失败：${failed.length} 个数据源不可用，页面上是最近一次成功的数据`,
      );
    }
    setLoading(false);
    setRefreshing(false);
    setUpdatedAt(new Date());
  }, [notify]);

  const handleManualRefresh = useCallback(() => {
    manualRefreshRef.current = true;
    void load('refresh');
  }, [load]);

  useEffect(() => {
    mountedRef.current = true;
    startupRunRef.current += 1;
    void load('initial', startupRunRef.current);
    const timer = window.setInterval(() => {
      void load('refresh');
    }, REFRESH_MS);
    return () => {
      mountedRef.current = false;
      window.clearInterval(timer);
    };
  }, [load]);

  const rooms = data.status?.rooms ?? [];
  const liveCount = rooms.filter((room) => room.isLive).length;
  const monitoringCount = rooms.filter((room) => room.monitoring).length;
  const maxRooms = data.status?.maxRooms ?? 8;
  const autoClipEnabled = data.autoClip?.enabled ?? data.status?.autoHighlightEnabled ?? false;
  const autoClips = data.autoClip?.clips ?? [];
  const latestAutoClip = autoClips[0] ?? null;
  const runningJobs = data.jobs.filter(
    (job) => job.status === 'running' || job.status === 'queued' || job.status === 'pending',
  );
  const latestJob = data.jobs[0] ?? null;
  const recentClips = useMemo(() => data.clips.slice(0, 6), [data.clips]);

  const summary = useMemo(() => {
    if (loading) return '正在读取后端状态…';
    if (!data.status) {
      return '暂时无法读取直播状态；可以先打开已有切片继续剪辑，或到直播控制台重新连接后端。';
    }
    return `后台正在监控 ${monitoringCount} 个房间（${liveCount} 路在播）；自动切片${
      autoClipEnabled ? '已开启' : '已关闭'
    }，共记录 ${autoClips.length} 条高光。`;
  }, [autoClipEnabled, autoClips.length, data.status, liveCount, loading, monitoringCount]);

  const failedSourceText = failures.map((key) => SOURCE_LABELS[key]).join('、');

  return (
    <div className="lc-home">
      <section className="lc-home-hero" aria-labelledby="lc-home-greeting">
        <div className="lc-home-hero__text">
          <div className="lc-home-hero__eyebrow">{todayLabel()} · 本地优先工作台</div>
          <h2 id="lc-home-greeting" className="lc-home-hero__title">
            欢迎回来
          </h2>
          <p className="lc-home-hero__summary">{summary}</p>
        </div>
        <div className="lc-home-hero__actions">
          <Button variant="outline" disabled={cacheBusy} onClick={()=>void cacheAction()}>检查可清理缓存</Button>
          <Button variant="solid" leading={<Radio size={15} aria-hidden="true" />} onClick={() => go('/live')}>
            打开直播控制台
          </Button>
          <Button variant="outline" leading={<Scissors size={15} aria-hidden="true" />} onClick={() => go('/editor')}>
            进入剪辑
          </Button>
        </div>
      </section>

      {cacheScan && <section className="lc-home-notice" aria-label="缓存清理确认">
        <div><strong>波形图片缓存：{cacheScan.count} 个，{formatBytes(cacheScan.bytes)}</strong>
          <p>本次仅清理波形图片，不包含浏览器 WebView 及预览音频缓存。只清理一小时前生成且未被占用、仍与扫描清单一致的可重建波形，再次需要时自动生成。</p>
          <p>不清理：{cacheScan.excluded.join('；')}。</p>
          <Button variant="outline" disabled={cacheBusy||cacheScan.count===0} onClick={()=>void cacheAction(true)}>确认清理上述缓存</Button>
          <Button variant="outline" disabled={cacheBusy} onClick={()=>setCacheScan(null)}>取消</Button>
        </div>
      </section>}
      {cacheResult && <p role="status">{cacheResult}</p>}

      {failures.length > 0 && !loading ? (
        <div className="lc-home-notice" role="status">
          <AlertTriangle size={15} aria-hidden="true" />
          <span>
            {failures.length === 5 ? '后端暂时不可用：' : '部分数据读取失败：'}
            {failedSourceText}。页面显示的是最近一次成功读取的数据。
          </span>
          <Button variant="link" size="sm" onClick={handleManualRefresh}>
            重试
          </Button>
        </div>
      ) : null}

      <section className="lc-home-stats" aria-label="运行概览">
        <Card
          eyebrow="直播"
          title="在线房间"
          meta={`最多 ${maxRooms} 路并行监控`}
          actions={
            <StatusDot
              tone={liveCount > 0 ? 'green' : 'idle'}
              pulse={liveCount > 0}
              label={liveCount > 0 ? '有直播' : '无直播'}
            />
          }
          footer={
            <Button variant="link" size="sm" onClick={() => go('/live')}>
              查看直播
              <ArrowRight size={13} aria-hidden="true" />
            </Button>
          }
        >
          <div className="lc-home-stat">
            <strong>{data.status ? formatNumber(liveCount) : '—'}</strong>
            <span>/ {maxRooms}</span>
          </div>
          <p className="lc-home-stat__hint">
            {monitoringCount > 0 ? `${monitoringCount} 路录制监控中` : '当前没有录制中的房间'}
          </p>
        </Card>

        <Card
          eyebrow="自动切片"
          title="最近高光"
          meta={latestAutoClip ? `最新记录 ${formatTime(latestAutoClip.createdAt)}` : '弹幕热度命中的片段会自动出现'}
          actions={
            <Badge tone={autoClipEnabled ? 'green' : 'neutral'} dot>
              {autoClipEnabled ? '已开启' : '已关闭'}
            </Badge>
          }
          footer={
            <Button variant="link" size="sm" onClick={() => go('/live/monitor')}>
              打开监控屏
              <ArrowRight size={13} aria-hidden="true" />
            </Button>
          }
        >
          <div className="lc-home-stat">
            <strong>{formatNumber(autoClips.length)}</strong>
            <span>条</span>
          </div>
          <p className="lc-home-stat__hint">
            {latestAutoClip
              ? `最新高光 ${formatTime(latestAutoClip.createdAt)} · ${latestAutoClip.state ?? '处理中'}`
              : '还没有自动切片记录'}
          </p>
        </Card>

        <Card
          eyebrow="后台任务"
          title="任务队列"
          meta={latestJob ? `${jobTypeLabel(latestJob.type)} · ${jobStatusLabel(latestJob.status)}` : '渲染、转写与分析任务会显示在这里'}
          actions={
            <StatusDot
              tone={runningJobs.length > 0 ? 'blue' : 'idle'}
              pulse={runningJobs.length > 0}
              label={runningJobs.length > 0 ? '有任务进行中' : '空闲'}
            />
          }
          footer={
            <Button variant="link" size="sm" onClick={() => go('/studio')}>
              打开素材库
              <ArrowRight size={13} aria-hidden="true" />
            </Button>
          }
        >
          <div className="lc-home-stat">
            <strong>{formatNumber(runningJobs.length)}</strong>
            <span>进行中</span>
          </div>
          <p className="lc-home-stat__hint">
            {latestJob
              ? `最近任务 ${formatTime(latestJob.startedAt ?? null)}`
              : '当前没有排队或运行中的任务'}
          </p>
        </Card>

        <Card
          eyebrow="本地存储"
          title="存储占用"
          meta={data.storage ? `剩余 ${formatBytes(data.storage.freeBytes)}` : '存储配额由后端实时计算'}
          actions={
            <Badge tone={data.storage?.quotaExceeded ? 'amber' : 'neutral'} dot>
              {data.storage?.quotaExceeded ? '配额超额' : '配额正常'}
            </Badge>
          }
          footer={
            <Button variant="link" size="sm" onClick={() => go('/settings')}>
              存储与设置
              <ArrowRight size={13} aria-hidden="true" />
            </Button>
          }
        >
          <div className="lc-home-stat">
            <strong>{formatBytes(data.storage?.totalBytes)}</strong>
          </div>
          <p className="lc-home-stat__hint">
            {data.storage
              ? `${data.storage.sessionDirectories} 个会话目录 · 已清理 ${data.storage.deletedSessions} 个过期会话`
              : '存储信息读取中'}
          </p>
        </Card>
      </section>

      <div className="lc-home-main">
        <Panel
          eyebrow="素材 · SLICES"
          title="最近切片"
          action={
            <Button variant="link" size="sm" onClick={() => go('/studio')}>
              查看全部
            </Button>
          }
        >
          {loading && recentClips.length === 0 ? (
            <div className="lc-home-loading">
              <LoaderCircle size={15} className="lc-home-spin" aria-hidden="true" />
              正在读取切片…
            </div>
          ) : failures.includes('clips') && recentClips.length === 0 ? (
            <EmptyState
              icon={<AlertTriangle size={22} aria-hidden="true" />}
              title="切片列表读取失败"
              description="后端可能尚未启动完成；稍后重试，或先到直播页确认状态。"
              action={
                <Button variant="outline" size="sm" onClick={handleManualRefresh}>
                  重新读取
                </Button>
              }
            />
          ) : recentClips.length === 0 ? (
            <EmptyState
              icon={<Film size={22} aria-hidden="true" />}
              title="还没有可剪的切片"
              description="到直播控制台开启自动切片：弹幕热度命中的片段会自动出现在这里，点击即可进入编辑器。"
              action={
                <Button variant="solid" size="sm" leading={<Radio size={14} aria-hidden="true" />} onClick={() => go('/live')}>
                  去直播控制台
                </Button>
              }
            />
          ) : (
            <ul className="lc-home-clips">
              {recentClips.map((clip) => (
                <li key={clip.id}>
                  <button
                    type="button"
                    className="lc-home-clip"
                    onClick={() => go(`/?clip=${encodeURIComponent(clip.id)}`)}
                  >
                    <span className="lc-home-clip__icon" aria-hidden="true">
                      <Film size={16} />
                    </span>
                    <span className="lc-home-clip__main">
                      <span className="lc-home-clip__title">{clip.title?.trim() || baseName(clip.path)}</span>
                      <span className="lc-home-clip__meta">
                        {formatTime(clip.createdAt)} · {clip.eventId ? `事件 ${clip.eventId.slice(0, 8)}` : '手动切片'}
                      </span>
                    </span>
                    <Badge tone="green" dot>
                      就绪
                    </Badge>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel
          eyebrow="系统 · RUNTIME"
          title="运行状态"
          action={
            <IconButton
              label="刷新数据"
              size="sm"
              onClick={handleManualRefresh}
              disabled={refreshing}
            >
              {refreshing ? (
                <LoaderCircle size={15} className="lc-home-spin" aria-hidden="true" />
              ) : (
                <RefreshCw size={15} aria-hidden="true" />
              )}
            </IconButton>
          }
          footer={
            <span className="lc-home-kv__note">
              数据来自 /api/status、/api/live/auto-clip、/api/clips、/api/jobs 与 /api/live/storage，每 30 秒自动刷新。
            </span>
          }
        >
          <dl className="lc-home-kv">
            <div className="lc-home-kv__row">
              <dt>
                <ServerCog size={14} aria-hidden="true" />
                后端
              </dt>
              <dd>
                <StatusDot
                  tone={data.status ? 'green' : failures.length > 0 ? 'red' : 'idle'}
                  label={data.status ? '已连接' : '不可用'}
                />
                {data.status ? '已连接' : '不可用'}
              </dd>
            </div>
            <div className="lc-home-kv__row">
              <dt>
                <Activity size={14} aria-hidden="true" />
                版本
              </dt>
              <dd>{data.status?.appVersion ?? '—'}</dd>
            </div>
            <div className="lc-home-kv__row">
              <dt>
                <Timer size={14} aria-hidden="true" />
                自动切片
              </dt>
              <dd>{autoClipEnabled ? '已开启' : '已关闭'}</dd>
            </div>
            <div className="lc-home-kv__row">
              <dt>
                <Radio size={14} aria-hidden="true" />
                监控房间
              </dt>
              <dd>
                {monitoringCount} / {maxRooms}
              </dd>
            </div>
            <div className="lc-home-kv__row">
              <dt>
                <HardDrive size={14} aria-hidden="true" />
                存储状态
              </dt>
              <dd>{data.storage?.quotaExceeded ? '配额超额' : '正常'}</dd>
            </div>
            <div className="lc-home-kv__row">
              <dt>
                <RefreshCw size={14} aria-hidden="true" />
                最近刷新
              </dt>
              <dd>{updatedAt ? formatTime(updatedAt.toISOString()) : '—'}</dd>
            </div>
          </dl>
        </Panel>
      </div>

      <Toast
        open={toast !== null}
        id={toast?.id}
        tone={toast?.tone ?? 'neutral'}
        message={toast?.text ?? ''}
        onClose={() => setToast(null)}
      />
    </div>
  );
}
