import { pickRecordingDirectory } from '../live/desktopPickRecordingDirectory';
import { MusicModelLicense } from './MusicModelLicense';
import { catalogModelFromLink } from './musicModelLink';
import { useState } from 'react';
import { AlertTriangle, CheckCircle2, LoaderCircle, Sparkles, Square } from 'lucide-react';
import {
  getAudioFileUrl,
  importMusicModelDirectory,
  isMusicDownloadBusy,
  isMusicModelBusy,
  type AudioAsset,
  type MusicGenerateInput,
  type MusicGenerateProvider,
  type MusicJobState,
  type MusicStatus,
} from '../../api/music';
import { navigateApp } from '../../app/appNavigation';
import { DownloadUninstallSwitch } from '../../components/DownloadUninstallSwitch';
import { MusicModelUninstallDialog } from './MusicModelUninstallDialog';
import { clamp, formatBytes, errorMessage } from './utils';

interface GeneratePanelProps {
  status: MusicStatus | null;
  assets: AudioAsset[];
  selectedModelId: string;
  job: MusicJobState | null;
  submitting: boolean;
  downloading: boolean;
  applying: boolean;
  onSelectModel: (modelId: string) => void;
  onToggleAllowDownload: (allowed: boolean) => void;
  onDownloadModel: (modelId?: string) => Promise<void>;
  /** 取消本地模型下载（与在线任务「停止等待」无关）。 */
  onCancelDownload: () => Promise<void>;
  /** 重新读取真实 status（卸载后复查用）；失败返回 null。 */
  onRefreshStatus: () => Promise<MusicStatus | null>;
  onGenerate: (input: MusicGenerateInput) => Promise<void>;
  onCancelJob: () => Promise<void>;
  onApplyMusic: (assetId: string) => Promise<void>;
}

const JOB_STATUS_LABELS: Record<string, string> = {
  queued: '排队中',
  running: '生成中',
  completed: '已完成',
  failed: '失败',
  cancelled: '已取消',
};

function ProgressBar({ value, label }: { value: number; label: string }) {
  const percent = Math.round(clamp(value, 0, 100));
  return (
    <div
      className="music-progress"
      role="progressbar"
      aria-label={label}
      aria-valuenow={percent}
      aria-valuemin={0}
      aria-valuemax={100}
    >
      <span className="music-progress-bar" style={{ width: `${percent}%` }} />
    </div>
  );
}

export function GeneratePanel({
  status,
  assets,
  selectedModelId,
  job,
  submitting,
  downloading,
  applying,
  onSelectModel,
  onToggleAllowDownload,
  onDownloadModel,
  onCancelDownload,
  onRefreshStatus,
  onGenerate,
  onCancelJob,
  onApplyMusic,
}: GeneratePanelProps) {
  const [genre, setGenre] = useState('氛围电子');
  const [style, setStyle] = useState('电影感、缓慢推进');
  const [content, setContent] = useState('夜晚城市的灯光与独白');
  const [theme, setTheme] = useState('深夜独白');
  const [duration, setDuration] = useState(15);
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [cancellingDownload, setCancellingDownload] = useState(false);
  /* 来源选择只是本页草稿：null = 跟随已保存的 status.generationProvider，不改本地 selectedModelId。 */
  const [providerChoice, setProviderChoice] = useState<MusicGenerateProvider | null>(null);
  const [submittedProvider, setSubmittedProvider] = useState<MusicGenerateProvider | null>(null);
  const [uninstallTarget, setUninstallTarget] = useState<{ id: string; name: string } | null>(null);
  /* 卸载 partial/unknown 后目录可能残留：未清除前不提供一键下载（会覆盖未知目录）。 */
  const [residual, setResidual] = useState<Record<string, string>>({});

  const provider: MusicGenerateProvider =
    providerChoice ?? (status?.generationProvider === 'custom-http' ? 'custom-http' : 'local');
  const online = provider === 'custom-http';
  const httpConfigured = status?.httpConfigured === true;
  const apiModel = status?.apiModel?.trim() ?? '';
  const jobOnline =
    (submittedProvider ?? (status?.generationProvider === 'custom-http' ? 'custom-http' : 'local')) ===
    'custom-http';

  const models = status?.models ?? [];
  const selected = models.find((model) => model.id === selectedModelId) ?? models[0] ?? null;
  const [modelDirectory, setModelDirectory] = useState('');
  const [directoryMessage, setDirectoryMessage] = useState('');
  const [directoryPending, setDirectoryPending] = useState(false);
  const [modelLink, setModelLink] = useState('');
  const linkedModel = catalogModelFromLink(modelLink, models);
  const download = status?.download ?? null;
  const downloadActive = download !== null && isMusicDownloadBusy(download.state);
  const downloadCancelling = download?.state === 'cancelling';
  /* 真实 busy = 新字段 downloadBusy/localModelBusy OR 旧 download.state；failed 后 worker 未退出时 downloadActive 为 false 但这里仍为 true。 */
  const modelBusy = isMusicModelBusy(status);
  const residualMessage = selected ? residual[selected.id] : undefined;
  const jobActive = job !== null && (job.status === 'queued' || job.status === 'running');
  const resultAsset = job?.assetId
    ? assets.find((asset) => asset.id === job.assetId) ?? null
    : null;

  const durationMax = !online && selected?.engine === 'musicgen' ? 30 : 180;

  const blockedReason = !status
    ? '正在读取音乐引擎状态…'
    : online
      ? httpConfigured
        ? null
        : '线上 API 尚未配置：请先到设置填写生成地址、模型名与 API key'
      : !selected
      ? '没有可用的音乐模型'
      : downloadActive
        ? downloadCancelling
          ? '正在取消模型下载，任务退出后才能生成'
          : '模型正在下载，完成后才能生成'
        : modelBusy
          ? '模型后台任务仍在运行或退出中，请稍候'
          : !selected.installed
          ? (selected.id === 'local/musicgen-directory' ? '外部模型目录已移动或不完整，请重新选择。' : '所选模型尚未下载，请先下载或切换到内置合成器')
          : status.pythonAvailable
            ? null
            : '未检测到 Python，音乐生成暂不可用';

  const busyReason = downloadActive
    ? downloadCancelling
      ? '正在取消下载，等待后台任务退出'
      : '已有下载任务在进行'
    : status?.localModelBusy || (jobActive && !jobOnline)
      ? '本地音乐正在生成，暂不能下载或卸载'
      : modelBusy
        ? '下载或生成的后台任务仍在运行或退出中，请稍候'
        : null;

  // 已安装时按钮是「卸载」：卸载不需要 Python / 允许下载，只看 busy。
  const downloadDisabledReason = !status
    ? '正在读取状态…'
    : !selected
      ? '没有可选的模型'
      : selected.builtIn
        ? '内置合成器无需下载或卸载'
        : selected.installed
          ? busyReason
          : busyReason
            ? busyReason
            : residualMessage !== undefined
              ? '上次卸载未完成，模型目录可能有残留，已阻止一键下载覆盖'
              : !status.pythonAvailable
                ? '未检测到 Python，无法准备模型环境'
                : !status.allowDownload
                  ? '需要先勾选「允许下载本地音乐模型」'
                  : null;

  async function handleSubmit() {
    if (online ? !httpConfigured : !selected) {
      setFormError(online ? '线上 API 尚未配置。' : '没有可用的音乐模型');
      return;
    }
    if (!genre.trim() || !style.trim() || !content.trim() || !theme.trim()) {
      setFormError('曲风、风格、内容、主题都需要填写。');
      return;
    }
    if (genre.length > 500 || style.length > 500 || content.length > 500 || theme.length > 500) {
      setFormError('每个描述字段最多 500 个字符。');
      return;
    }
    if (duration < 5 || duration > 180) {
      setFormError('音乐时长需在 5–180 秒之间。');
      return;
    }
    setFormError(null);
    setSubmittedProvider(provider);
    try {
      await onGenerate({
        // 线上：真实模型来自已保存配置，不传 URL/key；本地：保持旧字段，不带 provider。
        modelId: online ? '' : (selected?.id ?? ''),
        genre: genre.trim(),
        style: style.trim(),
        content: content.trim(),
        theme: theme.trim(),
        durationSeconds: duration,
        allowDownload: status?.allowDownload ?? false,
        ...(online ? { provider: 'custom-http' as const } : {}),
      });
    } catch (error) {
      setFormError(errorMessage(error));
    }
  }

  async function handleApply() {
    if (!job?.assetId) return;
    setSaving(true);
    try {
      await onApplyMusic(job.assetId);
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="music-panel music-generate" aria-label="音乐生成">
      <header className="music-panel-head">
        <div className="music-panel-title">
          <Sparkles size={14} aria-hidden="true" />
          <span>音乐生成</span>
        </div>
        <span className="music-panel-meta">
          {!status ? '读取中' : online ? '线上 API' : `${models.length} 个引擎`}
        </span>
      </header>

      <div className="music-panel-body">
        <div className="music-section">
          <div className="music-section-head">
            <h2>模型与引擎</h2>
            {status?.engineNote ? <span>{status.engineNote}</span> : null}
          </div>

          {status ? (
            <fieldset className="music-model-list" disabled={modelBusy || jobActive}>
              <legend className="music-hint">生成来源</legend>
              <label className={`music-model ${!online ? 'is-selected' : ''}`}>
                <input
                  type="radio"
                  name="music-provider"
                  checked={!online}
                  onChange={() => setProviderChoice('local')}
                />
                <span className="music-model-text">
                  <span className="music-model-name">本地 / 离线</span>
                  <span className="music-model-desc">使用本机内置合成器或已下载模型，需要 Python。</span>
                </span>
              </label>
              <label className={`music-model ${online ? 'is-selected' : ''}`}>
                <input
                  type="radio"
                  name="music-provider"
                  checked={online}
                  onChange={() => setProviderChoice('custom-http')}
                />
                <span className="music-model-text">
                  <span className="music-model-name">
                    线上 API
                    {httpConfigured ? (
                      <span className="music-badge music-badge--ok">已配置</span>
                    ) : (
                      <span className="music-badge music-badge--warn">未配置</span>
                    )}
                  </span>
                  <span className="music-model-desc">
                    使用设置里保存的自定义 HTTP 接口，无需 Python，也无需下载本地模型。
                  </span>
                </span>
              </label>
            </fieldset>
          ) : null}

          {online && status ? (
            <div className="music-online">
              {httpConfigured ? (
                <p className="music-hint">
                  当前线上模型：<span className="music-mono">{apiModel || '（未返回模型名）'}</span>
                </p>
              ) : (
                <p className="music-note music-note--warning">
                  <AlertTriangle size={13} aria-hidden="true" />
                  线上 API 尚未配置完整（需要生成地址、模型名与 API key）。
                </p>
              )}
              <div className="music-actions">
                <button
                  type="button"
                  className="music-button music-button--ghost"
                  onClick={() => void navigateApp('/settings')}
                >
                  去设置{httpConfigured ? '修改' : '配置'}
                </button>
              </div>
              <p className="music-hint">
                线上生成没有远端完成百分比；取消只会停止本地等待，远端服务可能仍在运行并继续计费。
              </p>
            </div>
          ) : null}

          {online && status ? null : !status ? (
            <p className="music-empty">正在读取音乐引擎状态…</p>
          ) : models.length === 0 ? (
            <p className="music-empty">后端没有返回可用的音乐模型。</p>
          ) : (
            <div className="music-model-list">
              {models.map((model) => (
                <label
                  key={model.id}
                  className={`music-model ${selectedModelId === model.id ? 'is-selected' : ''}`}
                >
                  <input
                    type="radio"
                    name="music-model"
                    value={model.id}
                    checked={selectedModelId === model.id}
                    onChange={() => onSelectModel(model.id)}
                    disabled={modelBusy || jobActive}
                  />
                  <span className="music-model-text">
                    <span className="music-model-name">
                      {model.name}
                      {model.builtIn ? <span className="music-badge">内置</span> : null}
                      {model.installed && !model.builtIn ? (
                        <span className="music-badge music-badge--ok">已安装</span>
                      ) : null}
                      {!model.installed && !model.builtIn ? (
                        <span className="music-badge music-badge--warn">
                          {model.id === 'local/musicgen-directory' ? '目录不可用' : `需下载 ${model.approxBytes > 0 ? formatBytes(model.approxBytes) : '大小未知（含依赖）'}`}
                        </span>
                      ) : null}
                    </span>
                    <span className="music-model-desc">{model.description}</span>
                    {model.license ? (
                      <span className="music-model-license">许可：<MusicModelLicense model={model} /></span>
                    ) : null}
                  </span>
                </label>
              ))}
            </div>
          )}

          {status && !online ? (
            <>
              <label className="music-check">
                <input
                  type="checkbox"
                  checked={status.allowDownload}
                  disabled={modelBusy}
                  onChange={(event) => onToggleAllowDownload(event.target.checked)}
                />
                <span>允许下载本地音乐模型（体积因型号和依赖而异，会写入本机运行时目录）</span>
              </label>

              <label>外部 MusicGen 目录
                <input value={modelDirectory} onChange={event=>setModelDirectory(event.target.value)} placeholder="输入本机完整目录路径" />
              </label>
              <button type="button" className="music-button" disabled={directoryPending || modelBusy}
                onClick={async()=>{setDirectoryPending(true);try{const path=await pickRecordingDirectory('music-model');if(path!==null){setModelDirectory(path);setDirectoryMessage('');}}catch(error){setDirectoryMessage(errorMessage(error));}finally{setDirectoryPending(false);}}}>浏览模型目录…</button>
              <p className="music-hint">支持 Transformers MusicGen 单文件 safetensors 模型及分词器。仅保存一个目录引用，新引用会替换旧引用；不移动或删除原文件。</p>
              <button type="button" className="music-button" disabled={!modelDirectory.trim() || modelBusy || directoryPending}
                onClick={async()=>{setDirectoryPending(true);try{await importMusicModelDirectory(modelDirectory.trim());await onRefreshStatus();setDirectoryMessage('目录引用已保存。请在模型列表中选择外部 MusicGen。');}catch(error){setDirectoryMessage(errorMessage(error));}finally{setDirectoryPending(false);}}}>保存目录引用</button>
              <p role="status">{directoryMessage}</p>
              <label>从模型链接下载
                <input type="url" value={modelLink} onChange={event => setModelLink(event.target.value)}
                  placeholder="https://huggingface.co/facebook/musicgen-small" />
              </label>
              {modelLink.trim() && (linkedModel ? <>
                <p className="music-hint">{linkedModel.name} · <MusicModelLicense model={linkedModel} /> · {linkedModel.installed ? '已安装' : '未安装'}。下载不会切换默认模型。</p>
                <button type="button" className="music-button" disabled={linkedModel.installed || !status.allowDownload || !status.pythonAvailable || downloading || modelBusy || residual[linkedModel.id] !== undefined}
                  onClick={() => void onDownloadModel(linkedModel.id)}>下载链接中的模型</button>
              </> : <p className="music-error">仅支持当前模型列表中的 Hugging Face 模型主页链接。</p>)}

              {!status.pythonAvailable ? (
                <p className="music-note music-note--warning">
                  <AlertTriangle size={13} aria-hidden="true" />
                  未检测到 Python，内置合成器与模型下载都不可用。请先配置 Python 运行时。
                </p>
              ) : null}

              {download && download.state !== 'idle' ? (
                <div className="music-download">
                  <div className="music-download-head">
                    <span>
                      下载状态：
                      {download.state === 'preparing'
                        ? '准备环境'
                        : download.state === 'downloading'
                          ? '下载中'
                          : download.state === 'cancelling'
                            ? '取消中'
                            : download.state === 'completed'
                            ? '已完成'
                            : download.state === 'cancelled'
                              ? '已取消'
                              : download.state === 'failed'
                                ? '失败'
                                : '空闲'}
                    </span>
                    <span className="music-mono">{Math.round(clamp(download.progress, 0, 100))}%</span>
                  </div>
                  <ProgressBar value={download.progress} label="模型下载进度" />
                  <p className="music-hint">{download.detail}</p>
                  {download.error ? <p className="music-error">{download.error}</p> : null}
                  {download.state === 'cancelled' ? (
                    <p className="music-hint">已取消；已下载的部分文件可能被保留，不会自动删除。</p>
                  ) : null}
                </div>
              ) : null}

              <div className="music-actions">
                {selected && !selected.builtIn && selected.id !== 'local/musicgen-directory' ? (
                  <DownloadUninstallSwitch
                    installed={selected.installed}
                    busy={
                      selected.installed
                        ? busyReason !== null || uninstallTarget !== null
                        : downloading || downloadDisabledReason !== null
                    }
                    disabledReason={downloadDisabledReason ?? undefined}
                    onDownload={() => void onDownloadModel()}
                    onUninstall={() => setUninstallTarget({ id: selected.id, name: selected.name })}
                  />
                ) : null}
                {residualMessage !== undefined && selected && !selected.installed && !selected.builtIn ? (
                  <button
                    type="button"
                    className="music-button music-button--ghost"
                    disabled={modelBusy}
                    onClick={() => setUninstallTarget({ id: selected.id, name: selected.name })}
                  >
                    重新预览卸载
                  </button>
                ) : null}
                {downloadActive ? (
                  <button
                    type="button"
                    className="music-button music-button--ghost"
                    disabled={downloadCancelling || cancellingDownload}
                    title={downloadCancelling ? '已请求取消，等待后台任务退出' : '取消本地模型下载'}
                    onClick={() => {
                      setCancellingDownload(true);
                      void onCancelDownload().finally(() => setCancellingDownload(false));
                    }}
                  >
                    <Square size={13} aria-hidden="true" />
                    {downloadCancelling ? '取消中…' : '取消下载'}
                  </button>
                ) : null}
                {downloadDisabledReason ? (
                  <span className="music-hint">{downloadDisabledReason}</span>
                ) : null}
              </div>
              {residualMessage !== undefined && selected && !selected.installed ? (
                <p className="music-error" role="alert">
                  待处理：{residualMessage}
                </p>
              ) : null}
            </>
          ) : null}
        </div>

        <div className="music-divider" />

        <div className="music-section">
          <div className="music-section-head">
            <h2>生成音乐</h2>
            <span>填写四个描述字段，引擎会按主题、曲风、风格合成一条{online ? '线上 API ' : selected?.engine === 'builtin' ? '内置合成' : ' AI '}音乐。</span>
          </div>

          <div className="music-form-grid">
            <label className="music-field">
              <span>主题</span>
              <input
                className="music-input"
                type="text"
                maxLength={500}
                value={theme}
                onChange={(event) => setTheme(event.target.value)}
                placeholder="例如：深夜独白"
              />
            </label>
            <label className="music-field">
              <span>曲风</span>
              <input
                className="music-input"
                type="text"
                maxLength={500}
                value={genre}
                onChange={(event) => setGenre(event.target.value)}
                placeholder="例如：氛围电子"
              />
            </label>
            <label className="music-field">
              <span>风格</span>
              <input
                className="music-input"
                type="text"
                maxLength={500}
                value={style}
                onChange={(event) => setStyle(event.target.value)}
                placeholder="例如：电影感、缓慢推进"
              />
            </label>
            <label className="music-field music-field--wide">
              <span>内容</span>
              <textarea
                className="music-textarea"
                maxLength={500}
                rows={2}
                value={content}
                onChange={(event) => setContent(event.target.value)}
                placeholder="描述画面与情绪，例如：夜晚城市的灯光与独白"
              />
            </label>
          </div>

          <div className="music-duration">
            <label htmlFor="music-duration">时长</label>
            <input
              id="music-duration"
              className="music-range"
              type="range"
              min={5}
              max={durationMax}
              step={5}
              value={Math.min(duration, durationMax)}
              onChange={(event) => setDuration(Number(event.target.value))}
            />
            <span className="music-mono">{Math.min(duration, durationMax)} 秒</span>
            {!online && selected?.engine === 'musicgen' ? (
              <span className="music-hint">MusicGen 单次上限 30 秒，实际时长会被截到 30 秒内。</span>
            ) : null}
          </div>

          {formError ? <p className="music-error">{formError}</p> : null}

          <div className="music-actions">
            <button
              type="button"
              className="music-button music-button--primary"
              disabled={submitting || jobActive || blockedReason !== null}
              onClick={() => void handleSubmit()}
            >
              {submitting ? (
                <LoaderCircle size={13} className="music-spin" aria-hidden="true" />
              ) : (
                <Sparkles size={13} aria-hidden="true" />
              )}
              开始生成音乐
            </button>
            {blockedReason ? <span className="music-hint">{blockedReason}</span> : null}
          </div>
        </div>

        {job ? (
          <>
            <div className="music-divider" />
            <div className="music-section">
              <div className="music-section-head">
                <h2>当前任务</h2>
                <span className={`music-status music-status--${job.status}`}>
                  {JOB_STATUS_LABELS[job.status] ?? job.status}
                </span>
              </div>

              {jobOnline && jobActive ? (
                <progress aria-label="线上生成等待中（阶段提示，非远端完成比例）" />
              ) : (
                <ProgressBar value={job.progress} label="音乐生成进度" />
              )}
              <div className="music-job-meta">
                <span>{job.detail || '等待引擎输出…'}</span>
                <span className="music-mono">{job.id.slice(0, 8)}</span>
              </div>

              {job.error ? <p className="music-error">{job.error}</p> : null}

              {jobActive ? (
                <div className="music-actions">
                  <button
                    type="button"
                    className="music-button music-button--danger"
                    onClick={() => void onCancelJob()}
                  >
                    <Square size={12} aria-hidden="true" />
                    {jobOnline ? '停止等待' : '取消任务'}
                  </button>
                  {jobOnline ? (
                    <span className="music-hint">只停止本地等待，不承诺远端取消或免费。</span>
                  ) : null}
                </div>
              ) : null}

              {job.status === 'completed' && job.assetId ? (
                <div className="music-result">
                  <div className="music-result-head">
                    <CheckCircle2 size={14} aria-hidden="true" />
                    <strong>{resultAsset ? resultAsset.name : '生成结果已导入音频库'}</strong>
                    {job.analysis?.bpm ? (
                      <span className="music-badge music-badge--bpm">BPM {job.analysis.bpm.toFixed(1)}</span>
                    ) : null}
                    {job.analysis?.measuredLufs != null ? (
                      <span className="music-badge music-badge--lufs">{job.analysis.measuredLufs.toFixed(1)} LUFS</span>
                    ) : null}
                    {resultAsset ? <span className="music-badge">音乐</span> : null}
                  </div>
                  <audio
                    className="music-audio-player"
                    controls
                    preload="none"
                    src={getAudioFileUrl(job.assetId)}
                  />
                  <div className="music-actions">
                    <button
                      type="button"
                      className="music-button music-button--primary"
                      disabled={saving || applying}
                      onClick={() => void handleApply()}
                    >
                      {saving || applying ? (
                        <LoaderCircle size={13} className="music-spin" aria-hidden="true" />
                      ) : null}
                      设为工程配乐
                    </button>
                    <span className="music-hint">
                      写入所选项目的工程文件；已在编辑器打开时需重新载入才会更新。
                    </span>
                  </div>
                </div>
              ) : null}
            </div>
          </>
        ) : null}
      </div>

      {uninstallTarget ? (
        <MusicModelUninstallDialog
          model={uninstallTarget}
          onRefreshStatus={onRefreshStatus}
          onResidualChange={(modelId, message) =>
            setResidual((prev) => {
              if (message === null) {
                if (!(modelId in prev)) return prev;
                const { [modelId]: _removed, ...rest } = prev;
                return rest;
              }
              return { ...prev, [modelId]: message };
            })
          }
          onClose={() => setUninstallTarget(null)}
        />
      ) : null}
    </section>
  );
}
