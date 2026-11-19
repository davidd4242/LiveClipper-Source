import type { HealthResponse, LiveStorageStatus } from '../../api/types';
import type { FfmpegDownloadState, StudioSettings } from '../../api/studio';
import { dateLabel, formatBytes, formatPercent } from './workbench';

interface SettingsTabProps {
  settings: StudioSettings | null;
  storage: LiveStorageStatus | null;
  version: { appVersion: string } | null;
  health: HealthResponse | null;
  ffmpeg: FfmpegDownloadState | null;
  loading: boolean;
  error: string | null;
  onRefresh: () => void;
}

function Kv({ label, value, title }: { label: string; value: string; title?: string }) {
  return (
    <div className="wb-kv" title={title}>
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

const ffmpegStateLabels: Record<string, string> = {
  idle: '空闲（未下载）',
  downloading: '下载中',
  completed: '已就绪',
  failed: '失败',
  cancelled: '已取消',
};

export function SettingsTab({
  settings,
  storage,
  version,
  health,
  ffmpeg,
  loading,
  error,
  onRefresh,
}: SettingsTabProps) {
  return (
    <section className="wb-tab" aria-label="设置">
      <header className="wb-detail-head">
        <div className="wb-detail-title">
          <span className="wb-kicker">SETTINGS</span>
          <h2>设置</h2>
          <p className="wb-detail-path">
            只读展示后端真实配置：/api/settings、/api/live/storage、/api/version、/api/health、/api/ffmpeg/download。
            修改接口为 POST /api/settings，本页暂不提供写入。
          </p>
        </div>
        <div className="wb-actions">
          <button type="button" className="wb-button wb-button--ghost" onClick={onRefresh}>
            重新读取设置
          </button>
        </div>
      </header>

      {error ? (
        <div className="wb-error">
          <strong>设置读取失败</strong>
          <span>{error}</span>
          <button type="button" className="wb-button" onClick={onRefresh}>
            重试
          </button>
        </div>
      ) : null}

      {loading && !settings && !health ? (
        <div className="wb-empty">
          <strong>正在读取设置…</strong>
        </div>
      ) : (
        <>
          <div className="wb-section">
            <header className="wb-section-head">
              <h3>后端与数据</h3>
              <span>来源 /api/version、/api/health</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="应用版本" value={version?.appVersion ?? '未接入'} />
              <Kv label="后端状态" value={health?.ok ? '已连接' : '未连接'} />
              <Kv label="应用名称" value={health?.app ?? '未接入'} />
              <Kv label="数据目录" value={health?.dataRoot ?? '未接入'} title={health?.dataRoot} />
              <Kv label="ffmpeg 下载状态" value={ffmpeg ? ffmpegStateLabels[ffmpeg.state] ?? ffmpeg.state : '未接入'} />
              <Kv label="ffmpeg 进度" value={ffmpeg ? formatPercent(ffmpeg.progress / 100) : '未接入'} />
              <Kv label="ffmpeg 就绪路径" value={ffmpeg?.path ?? '未接入'} title={ffmpeg?.path ?? undefined} />
            </div>
          </div>

          <div className="wb-section">
            <header className="wb-section-head">
              <h3>媒体工具</h3>
              <span>来源 /api/settings</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="ffmpeg 路径" value={settings?.ffmpegPath ?? '未接入'} title={settings?.ffmpegPath} />
              <Kv label="Python 路径" value={settings?.pythonPath ?? '未接入'} title={settings?.pythonPath} />
              <Kv label="Whisper 模型" value={settings?.whisperModel ?? '未接入'} />
            </div>
          </div>

          <div className="wb-section">
            <header className="wb-section-head">
              <h3>目录</h3>
              <span>来源 /api/settings</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="数据根目录" value={settings?.storageRoot ?? '未接入'} title={settings?.storageRoot} />
              <Kv label="本地输出目录" value={settings?.localOutputRoot ?? '未接入'} title={settings?.localOutputRoot} />
            </div>
          </div>

          <div className="wb-section">
            <header className="wb-section-head">
              <h3>直播存储</h3>
              <span>来源 GET /api/live/storage · 检查于 {storage ? dateLabel(storage.checkedAt) : '未接入'}</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="已用空间" value={formatBytes(storage?.totalBytes)} />
              <Kv label="元数据占用" value={formatBytes(storage?.metadataBytes)} />
              <Kv label="剩余空间" value={formatBytes(storage?.freeBytes)} />
              <Kv label="总配额" value={storage ? `${formatBytes(storage.totalBytes)} / ${formatBytes(storage.maxTotalBytes)}` : '未接入'} />
              <Kv label="最小保留" value={formatBytes(storage?.minFreeBytes)} />
              <Kv label="录制目录" value={storage ? `${storage.sessionDirectories} 个` : '未接入'} />
              <Kv label="受保护会话" value={storage ? `${storage.protectedSessions} 个` : '未接入'} />
              <Kv label="配额状态" value={storage?.quotaExceeded ? '超限' : '正常'} />
              <Kv label="自动清理" value={storage?.enforced ? '开启' : '关闭'} />
              <Kv label="已清理" value={storage ? `${storage.deletedSessions} 个 · ${formatBytes(storage.deletedBytes)}` : '未接入'} />
            </div>
            {storage && storage.warnings.length > 0 ? (
              <div className="wb-note wb-note--warning">
                <strong>存储告警</strong>
                <ul>
                  {storage.warnings.map((warning) => (
                    <li key={warning}>{warning}</li>
                  ))}
                </ul>
              </div>
            ) : null}
          </div>

          <div className="wb-section">
            <header className="wb-section-head">
              <h3>AI 与分析</h3>
              <span>来源 /api/settings</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="AI 提供方" value={settings?.aiProvider ?? '未接入'} />
              <Kv label="AI 端点" value={settings?.aiEndpoint ?? '未接入'} title={settings?.aiEndpoint} />
              <Kv label="AI 模型" value={settings?.aiModel ?? '未接入'} />
            </div>
          </div>

          <div className="wb-section">
            <header className="wb-section-head">
              <h3>自动化</h3>
              <span>来源 /api/settings</span>
            </header>
            <div className="wb-kv-grid">
              <Kv label="自动转写" value={settings ? (settings.autoTranscribe ? '开' : '关') : '未接入'} />
              <Kv label="自动高光" value={settings ? (settings.autoHighlight ? '开' : '关') : '未接入'} />
              <Kv label="自动分析" value={settings ? (settings.autoAnalyze ? '开' : '关') : '未接入'} />
              <Kv label="自动裁剪静音" value={settings ? (settings.autoTrimSilence ? '开' : '关') : '未接入'} />
              <Kv label="退出时清理" value={settings ? (settings.cleanupOnExit ? '开' : '关') : '未接入'} />
              <Kv label="高光前后余量" value={settings ? `${settings.highlightPreRollSeconds}s / ${settings.highlightPostRollSeconds}s` : '未接入'} />
              <Kv label="手动前后余量" value={settings ? `${settings.manualPreRollSeconds}s / ${settings.manualPostRollSeconds}s` : '未接入'} />
              <Kv label="弹幕记录上限" value={settings ? `${settings.danmakuRecordLimit} 条` : '未接入'} />
              <Kv label="音乐模型下载" value={settings ? (settings.musicModelDownloadAllowed ? '允许' : '未允许') : '未接入'} />
            </div>
          </div>
        </>
      )}
    </section>
  );
}
