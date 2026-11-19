import { MusicModelLicense } from '../music/MusicModelLicense';
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { RefreshCw, Save } from 'lucide-react';
import { Button } from '../../design';
import {
  getMusicGenerationSettings,
  saveMusicGenerationSettings,
  type MusicGenerationAuthMode,
  type MusicGenerationProvider,
  type MusicGenerationSettings,
  type MusicGenerationSettingsInput,
} from '../../api/musicGenerationSettings';
import {
  getMusicStatus,
  startModelDownload,
  cancelModelDownload,
  isMusicDownloadBusy,
  isMusicModelBusy,
  type MusicModelStatus,
  type MusicStatus,
} from '../../api/music';
import { DownloadUninstallSwitch } from '../../components/DownloadUninstallSwitch';
import { MusicModelUninstallDialog } from '../music/MusicModelUninstallDialog';
import './musicGenerationSettings.css';

function sizeText(model: MusicModelStatus): string {
  if (model.approxBytes > 0) return `约 ${(model.approxBytes / 1e9).toFixed(1)} GB（估算）`;
  return '大小未知（含依赖）';
}

function ModalDialog({
  title,
  onClose,
  children,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
  }, []);
  // portal 到 body：对话框不在设置 <form> 内，回车/按钮不会触发保存。
  return createPortal(
    <dialog ref={ref} className="music-gen__dialog" aria-label={title} onClose={onClose} onCancel={onClose}>
      <h4 className="music-gen__dialog-title">{title}</h4>
      {children}
    </dialog>,
    document.body,
  );
}

function ImportDialog({ onClose }: { onClose: () => void }) {
  const [path, setPath] = useState('');
  return (
    <ModalDialog title="导入本地模型" onClose={onClose}>
      <p className="music-gen__note">
        目标：把磁盘上已有的 MusicGen 模型目录导入为本地音乐模型。当前 worker 以 transformers 的
        <code> MusicgenForConditionalGeneration </code>加载，目录需含 <code>config.json</code>、
        <code>preprocessor_config.json</code>、tokenizer 文件和权重（<code>.safetensors</code> 或 <code>.bin</code>）。
      </p>
      <label className="music-gen__field">
        <span>模型目录路径</span>
        <input
          type="text"
          value={path}
          spellCheck={false}
          autoComplete="off"
          placeholder="例如 D:\\models\\musicgen-medium"
          onChange={(event) => setPath(event.target.value)}
        />
      </label>
      <p className="music-gen__error" role="status">
        尚未接入：后端目前没有接收本地模型目录的接口，也没有权重校验。此处输入不会保存、读取或发送，提交已禁用。
        （音频文件导入不能当作模型导入。）
      </p>
      <div className="music-gen__actions">
        <Button size="sm" variant="solid" disabled title="后端尚未接入本地模型导入">
          导入（尚未接入）
        </Button>
        <Button size="sm" variant="ghost" onClick={onClose}>
          关闭
        </Button>
      </div>
    </ModalDialog>
  );
}

function LinkDialog({ onClose }: { onClose: () => void }) {
  const [url, setUrl] = useState('');
  return (
    <ModalDialog title="从链接下载模型" onClose={onClose}>
      <p className="music-gen__note">
        目标：从 https 链接（如 Hugging Face 仓库地址）下载 MusicGen 模型。支持格式与导入相同：transformers
        MusicGen 目录（<code>config.json</code> + 权重 <code>.safetensors</code>/<code>.bin</code>）。
      </p>
      <label className="music-gen__field">
        <span>模型链接</span>
        <input
          type="url"
          value={url}
          spellCheck={false}
          autoComplete="off"
          placeholder="https://huggingface.co/facebook/musicgen-medium"
          onChange={(event) => setUrl(event.target.value)}
        />
      </label>
      <p className="music-gen__error" role="status">
        尚未接入：后端只能下载目录内预置的模型，不接受任意链接，也没有来源与权重校验。此处输入不会保存或发送，提交已禁用。
      </p>
      <div className="music-gen__actions">
        <Button size="sm" variant="solid" disabled title="后端尚未接入链接下载">
          下载（尚未接入）
        </Button>
        <Button size="sm" variant="ghost" onClick={onClose}>
          关闭
        </Button>
      </div>
    </ModalDialog>
  );
}

/** 本地模型列表：只读 GET /api/music/status；下载需用户点击并确认，绝不自动 POST、不改任何设置。 */
function LocalMusicModels() {
  const [status, setStatus] = useState<MusicStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [startError, setStartError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<MusicModelStatus | null>(null);
  const [starting, setStarting] = useState(false);
  const [dialog, setDialog] = useState<'import' | 'link' | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const [cancelError, setCancelError] = useState<string | null>(null);
  const [uninstallTarget, setUninstallTarget] = useState<MusicModelStatus | null>(null);
  /** 卸载 partial/unknown 后目录可能残留：按模型记录，未清除前不提供一键下载（会覆盖未知目录）。 */
  const [residual, setResidual] = useState<Record<string, string>>({});
  const aliveRef = useRef(true);

  const refresh = useCallback(async (): Promise<MusicStatus | null> => {
    try {
      const next = await getMusicStatus();
      if (!aliveRef.current) return null;
      setStatus(next);
      setError(null);
      return next;
    } catch (err) {
      if (aliveRef.current) setError(errorText(err));
      return null;
    }
  }, []);

  const onResidualChange = useCallback((modelId: string, message: string | null) => {
    setResidual((prev) => {
      if (message === null) {
        if (!(modelId in prev)) return prev;
        const { [modelId]: _removed, ...rest } = prev;
        return rest;
      }
      return { ...prev, [modelId]: message };
    });
  }, []);

  useEffect(() => {
    aliveRef.current = true;
    void refresh();
    return () => {
      aliveRef.current = false;
    };
  }, [refresh]);

  const activeState = status?.download.state;
  // 含 cancelling：取消中仍占用 worker，继续 2s 轮询直到后端报告 cancelled / failed。
  const downloading = isMusicDownloadBusy(activeState);
  // 真实 busy（含 failed 后 worker 仍在退出、本地生成中）也继续轮询，直到后端不再占用。
  const anyBusy = isMusicModelBusy(status);
  useEffect(() => {
    if (!anyBusy) return;
    const timer = window.setInterval(() => void refresh(), 2000);
    return () => window.clearInterval(timer);
  }, [anyBusy, refresh]);

  const onConfirmDownload = async () => {
    if (!confirm || starting) return;
    const target = confirm;
    setStarting(true);
    setStartError(null);
    try {
      // allowDownload 仅随本次请求声明用户已确认，后端不会据此保存设置。
      const state = await startModelDownload(target.id, true);
      if (aliveRef.current) setStatus((prev) => (prev ? { ...prev, download: state } : prev));
      setConfirm(null);
      void refresh();
    } catch (err) {
      setStartError(errorText(err));
    } finally {
      if (aliveRef.current) setStarting(false);
    }
  };

  const onCancelDownload = async () => {
    if (cancelling) return;
    setCancelling(true);
    setCancelError(null);
    try {
      // 202 只表示已请求取消（state=cancelling），不是已取消；以后续轮询为准。
      const res = await cancelModelDownload();
      if (aliveRef.current) setStatus((prev) => (prev ? { ...prev, download: res.download } : prev));
    } catch (err) {
      if (aliveRef.current) setCancelError(errorText(err));
    } finally {
      if (aliveRef.current) setCancelling(false);
      void refresh();
    }
  };

  return (
    <div className="music-gen__local">
      <p className="music-gen__note">
        本地 / 离线模型。内置合成器无需下载；AI 模型需要你明确点击下载并确认。这里不会修改默认模型或「允许下载」设置。
      </p>
      {error ? (
        <div className="music-gen__banner" role="alert">
          <span>未能读取模型状态：{error}</span>
          <Button size="sm" type="button" leading={<RefreshCw size={14} />} onClick={() => void refresh()}>
            重试
          </Button>
        </div>
      ) : null}
      {!status && !error ? <p className="music-gen__note">读取模型列表中…</p> : null}

      {status ? (
        <ul className="music-gen__models">
          {status.models.map((model) => {
            const dl = status.download;
            const mine = dl.modelId === model.id;
            const busyHere = downloading && mine;
            const pending = residual[model.id];
            // 不能用「未安装」假设目录干净：卸载未完成的残留目录不允许被一键下载覆盖。
            const residualBlocks = !model.installed && pending !== undefined;
            const reason = busyHere
              ? dl.state === 'cancelling'
                ? '正在取消下载，等待后台任务退出'
                : `正在下载 ${dl.progress}%`
              : anyBusy
                ? downloading
                  ? '另一个模型正在下载，请等待完成'
                  : status.localModelBusy
                    ? '本地音乐正在生成，暂不能下载或卸载'
                    : '下载或生成的后台任务仍在运行或退出中，请稍候'
                : residualBlocks
                  ? '上次卸载未完成，模型目录可能有残留；请先重新预览处理，不会一键覆盖'
                  : !model.installed && !status.pythonAvailable
                    ? '未配置可用的 Python 路径，无法安装运行依赖'
                    : undefined;
            // 卸载不需要 Python：已安装时只看是否 busy。
            const blocked = anyBusy || (!model.installed && (!status.pythonAvailable || residualBlocks));
            return (
              <li key={model.id} className="music-gen__model">
                <div className="music-gen__model-head">
                  <strong>{model.name}</strong>
                  <code className="music-gen__muted">{model.id}</code>
                </div>
                <p className="music-gen__note">{model.description}</p>
                {model.builtIn ? (
                  <p className="music-gen__status">内置，始终可用，无需下载或卸载。</p>
                ) : (
                  <>
                    <p className="music-gen__note">
                      {sizeText(model)} · 许可：<MusicModelLicense model={model} /> ·{' '}
                      {model.compatibilityVerified ? '兼容性已验证' : '兼容性未验证（仅核对了模型元数据）'}
                    </p>
                    {model.id === 'local/musicgen-directory' ? <p className="music-gen__note">外部目录只读引用，不提供下载或卸载。</p> : <DownloadUninstallSwitch
                      installed={model.installed}
                      busy={blocked}
                      disabledReason={reason}
                      onDownload={() => {
                        setStartError(null);
                        setConfirm(model);
                      }}
                      onUninstall={() => setUninstallTarget(model)}
                    />}
                    {residualBlocks ? (
                      <p className="music-gen__error" role="alert">
                        待处理：{pending}
                        （模型目录可能残留，已阻止一键下载；点击下方按钮重新预览。）
                      </p>
                    ) : null}
                    {residualBlocks ? (
                      <div className="music-gen__actions">
                        <Button
                          size="sm"
                          type="button"
                          variant="ghost"
                          disabled={anyBusy}
                          onClick={() => setUninstallTarget(model)}
                        >
                          重新预览卸载
                        </Button>
                      </div>
                    ) : null}
                    {busyHere ? (
                      <>
                        <p className="music-gen__status" role="status">
                          {dl.state === 'cancelling'
                            ? `正在取消…${dl.detail}（后台任务退出后才会显示已取消）`
                            : `${dl.detail}（${dl.progress}%）`}
                        </p>
                        {dl.state !== 'cancelling' ? (
                          <div className="music-gen__actions">
                            <Button
                              size="sm"
                              type="button"
                              variant="ghost"
                              disabled={cancelling}
                              onClick={() => void onCancelDownload()}
                            >
                              {cancelling ? '提交中…' : '取消下载'}
                            </Button>
                          </div>
                        ) : null}
                      </>
                    ) : null}
                    {cancelError && mine ? <p className="music-gen__error">取消失败：{cancelError}</p> : null}
                    {mine && !downloading && dl.state === 'cancelled' ? (
                      <p className="music-gen__muted">
                        下载已取消。已下载的部分文件可能被保留，不会自动删除；再次下载将重新开始。
                      </p>
                    ) : null}
                    {mine && !downloading && dl.state === 'failed' ? (
                      <p className="music-gen__error">下载失败：{dl.error ?? dl.detail}</p>
                    ) : null}
                    {mine && !downloading && dl.state === 'completed' && !model.installed ? (
                      <p className="music-gen__error">
                        后端报告完成，但未确认已安装，仍显示为未下载。
                      </p>
                    ) : null}
                    {model.installed ? (
                      <p className="music-gen__muted">
                        已安装仅表示配置文件与标记存在，尚无权重完整性校验。
                      </p>
                    ) : null}
                  </>
                )}
              </li>
            );
          })}
        </ul>
      ) : null}

      <div className="music-gen__actions">
        <Button size="sm" type="button" onClick={() => setDialog('import')}>
          导入本地模型
        </Button>
        <Button size="sm" type="button" onClick={() => setDialog('link')}>
          从链接下载
        </Button>
        <Button size="sm" type="button" variant="ghost" leading={<RefreshCw size={14} />} onClick={() => void refresh()}>
          刷新模型状态
        </Button>
      </div>

      {confirm ? (
        <ModalDialog title={`确认下载 ${confirm.name}`} onClose={() => setConfirm(null)}>
          <p className="music-gen__note">
            将从 Hugging Face 下载 <code>{confirm.id}</code> 的整个仓库，并在 LiveClipper 本地运行目录创建 Python
            虚拟环境、安装 torch / transformers 等依赖。{sizeText(confirm)}；许可：<MusicModelLicense model={confirm} />
            {confirm.compatibilityVerified ? '' : '兼容性与资源需求未在本机验证。'}
            下载中可点击「取消下载」，取消后已下载的部分文件可能保留。已安装的模型可在此处点「卸载」，会先预览再确认。
          </p>
          {startError ? <p className="music-gen__error" role="alert">{startError}</p> : null}
          <div className="music-gen__actions">
            <Button size="sm" variant="solid" disabled={starting} onClick={() => void onConfirmDownload()}>
              {starting ? '提交中…' : '确认并开始下载'}
            </Button>
            <Button size="sm" variant="ghost" disabled={starting} onClick={() => setConfirm(null)}>
              取消
            </Button>
          </div>
        </ModalDialog>
      ) : null}
      {uninstallTarget ? (
        <MusicModelUninstallDialog
          model={uninstallTarget}
          onRefreshStatus={refresh}
          onResidualChange={onResidualChange}
          onClose={() => setUninstallTarget(null)}
        />
      ) : null}
      {dialog === 'import' ? <ImportDialog onClose={() => setDialog(null)} /> : null}
      {dialog === 'link' ? <LinkDialog onClose={() => setDialog(null)} /> : null}
    </div>
  );
}

const EXAMPLE = `POST <完整地址>
Content-Type: application/json
Authorization: Bearer <你的 key>   // 仅选择 Bearer 时

{ "protocol": "liveclipper.music.v1", "model": "your-music-model", "genre": "...", "style": "...", "content": "...", "theme": "...", "duration_seconds": 30 }

→ 200  Content-Type: audio/wav（响应体即 WAV 数据）
→ 非 2xx：JSON { "error": "…" }`;

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function isAbort(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

/**
 * 音乐生成 · 线上 API 设置表单。独立于页面其它设置：挂载只 GET，
 * 只有点击「保存」才 POST；key 仅存在于本地输入框，保存成功后立刻清空。
 */
export function MusicGenerationSettingsPanel() {
  const [saved, setSaved] = useState<MusicGenerationSettings | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const [provider, setProvider] = useState<MusicGenerationProvider>('local');
  const [generationUrl, setGenerationUrl] = useState('');
  const [model, setModel] = useState('');
  const [authMode, setAuthMode] = useState<MusicGenerationAuthMode>('none');
  const [apiKey, setApiKey] = useState('');
  const [clearKey, setClearKey] = useState(false);

  const loadControllerRef = useRef<AbortController | null>(null);
  const saveControllerRef = useRef<AbortController | null>(null);
  const loadSeqRef = useRef(0);
  const savingRef = useRef(false);

  const applyServer = useCallback((next: MusicGenerationSettings) => {
    setSaved(next);
    setProvider(next.provider);
    setGenerationUrl(next.generationUrl);
    setModel(next.model);
    setAuthMode(next.authMode);
    setApiKey('');
    setClearKey(false);
  }, []);

  const load = useCallback(async () => {
    if (savingRef.current) return;
    loadControllerRef.current?.abort();
    const controller = new AbortController();
    loadControllerRef.current = controller;
    const seq = ++loadSeqRef.current;
    setLoading(true);
    try {
      const next = await getMusicGenerationSettings(controller.signal);
      if (seq !== loadSeqRef.current) return;
      applyServer(next);
      setLoadError(null);
    } catch (err) {
      if (isAbort(err) || seq !== loadSeqRef.current) return;
      setLoadError(errorText(err));
    } finally {
      if (seq === loadSeqRef.current) setLoading(false);
    }
  }, [applyServer]);

  useEffect(() => {
    void load();
    return () => {
      loadSeqRef.current += 1;
      loadControllerRef.current?.abort();
      saveControllerRef.current?.abort();
    };
  }, [load]);

  const online = provider === 'custom-http';
  const bearer = online && authMode === 'bearer';
  const trimmedUrl = generationUrl.trim();
  const trimmedModel = model.trim();
  const trimmedKey = apiKey.trim();
  const hasStoredKey = saved?.hasApiKey ?? false;
  const urlChanged = !!saved && trimmedUrl !== saved.generationUrl;
  // clear 只在不再使用 Bearer（本地或 none）时才有意义；后端会拒绝「清除后仍 Bearer」。
  const canClear = hasStoredKey && !bearer;

  const dirty =
    !!saved &&
    (provider !== saved.provider ||
      trimmedUrl !== saved.generationUrl ||
      trimmedModel !== saved.model ||
      authMode !== saved.authMode ||
      trimmedKey !== '' ||
      clearKey);

  const problem = useMemo<string | null>(() => {
    if (!saved || !online) return null;
    if (!trimmedUrl) return '请填写完整的生成接口地址。';
    if (!/^https?:\/\//i.test(trimmedUrl)) return '地址需以 http:// 或 https:// 开头。';
    if (bearer && !trimmedKey) {
      if (!hasStoredKey) return '选择 Bearer 需要填写 API key。';
      if (urlChanged) return '地址已更改：为避免把已保存的 key 发给新地址，请重新输入 API key。';
    }
    return null;
  }, [saved, online, trimmedUrl, bearer, trimmedKey, hasStoredKey, urlChanged]);

  const canSave = !!saved && !saving && dirty && !problem;

  const onSave = async () => {
    if (!canSave || savingRef.current) return;
    const body: MusicGenerationSettingsInput = {
      provider,
      generationUrl: trimmedUrl,
      model: trimmedModel,
      authMode,
    };
    if (bearer && trimmedKey) body.apiKey = trimmedKey;
    else if (canClear && clearKey) body.clearApiKey = true;

    savingRef.current = true;
    const controller = new AbortController();
    saveControllerRef.current = controller;
    // 保存期间让进行中的读取失效，避免旧响应覆盖表单。
    loadControllerRef.current?.abort();
    loadSeqRef.current += 1;
    setLoading(false);
    setSaving(true);
    setSaveError(null);
    setNotice(null);
    try {
      const result = await saveMusicGenerationSettings(body, controller.signal);
      applyServer(result.music);
      setLoadError(null);
      setNotice('已保存。');
    } catch (err) {
      if (isAbort(err)) return;
      setSaveError(errorText(err));
      setApiKey('');
    } finally {
      savingRef.current = false;
      setSaving(false);
    }
  };

  const disabled = !saved || saving;

  return (
    <section className="music-gen" aria-labelledby="music-gen-title">
      <h3 id="music-gen-title" className="music-gen__title">
        音乐生成 · 线上 API
      </h3>
      <p className="music-gen__note">
        可选：把音乐生成交给自己的 HTTP 服务。选线上接口时不需要 Python，也不需要下载本地模型。
        这里只保存连接设置，不会自动切换任何本地或 AI 模型。
      </p>

      {loadError ? (
        <div className="music-gen__banner" role="alert">
          <span>未能读取音乐生成设置：{loadError}</span>
          <Button size="sm" leading={<RefreshCw size={14} />} disabled={loading} onClick={() => void load()}>
            重试
          </Button>
        </div>
      ) : null}
      {loading && !saved ? <p className="music-gen__note">读取中…</p> : null}

      {saved ? (
        <form
          className="music-gen__form"
          onSubmit={(event) => {
            event.preventDefault();
            void onSave();
          }}
        >
          <fieldset className="music-gen__choice" disabled={disabled}>
            <legend>生成方式</legend>
            <label>
              <input
                type="radio"
                name="music-gen-provider"
                checked={provider === 'local'}
                onChange={() => setProvider('local')}
              />
              <span>本地 / 离线</span>
            </label>
            <label>
              <input
                type="radio"
                name="music-gen-provider"
                checked={provider === 'custom-http'}
                onChange={() => setProvider('custom-http')}
              />
              <span>自定义音乐 HTTP 接口</span>
            </label>
          </fieldset>

          {online ? (
            <>
              <div className="music-gen__fields">
                <label className="music-gen__field music-gen__field--wide">
                  <span>完整生成地址</span>
                  <input
                    type="url"
                    value={generationUrl}
                    spellCheck={false}
                    autoComplete="off"
                    placeholder="https://api.example.com/v1/music/generate"
                    disabled={disabled}
                    onChange={(event) => setGenerationUrl(event.target.value)}
                  />
                </label>
                <label className="music-gen__field">
                  <span>模型名</span>
                  <input
                    type="text"
                    value={model}
                    spellCheck={false}
                    autoComplete="off"
                    placeholder="服务商提供的模型标识"
                    disabled={disabled}
                    onChange={(event) => setModel(event.target.value)}
                  />
                </label>
                <label className="music-gen__field">
                  <span>鉴权</span>
                  <select
                    value={authMode}
                    disabled={disabled}
                    onChange={(event) => {
                      setAuthMode(event.target.value as MusicGenerationAuthMode);
                      setApiKey('');
                      setClearKey(false);
                    }}
                  >
                    <option value="none">无需鉴权</option>
                    <option value="bearer">Bearer API key</option>
                  </select>
                </label>
                {bearer ? (
                  <label className="music-gen__field music-gen__field--wide">
                    <span>API key</span>
                    <input
                      type="password"
                      value={apiKey}
                      autoComplete="new-password"
                      spellCheck={false}
                      placeholder={hasStoredKey ? '留空则保留已保存的 key' : '粘贴 API key'}
                      disabled={disabled}
                      onChange={(event) => setApiKey(event.target.value)}
                    />
                  </label>
                ) : null}
              </div>
            </>
          ) : null}

          <p className="music-gen__status" role="status">
            API key：{hasStoredKey ? '已配置' : '未配置'}
            <span className="music-gen__muted">（只显示是否已配置，不回显内容）</span>
          </p>

          {canClear ? (
            <label className="music-gen__check">
              <input
                type="checkbox"
                checked={clearKey}
                disabled={disabled}
                onChange={(event) => setClearKey(event.target.checked)}
              />
              <span>保存时清除已保存的 API key</span>
            </label>
          ) : null}

          {online ? (
            <>
              <div className="music-gen__protocol">
                <p>
                  仅支持协议 <code>liveclipper.music.v1</code>：POST 音乐描述与时长，成功时返回 200
                  的 WAV。它不是聊天或 TTS 接口，也不保证兼容所有厂商；返回 202 的异步任务暂不支持。
                  不是填一个聊天 URL 就能用。
                </p>
                <p>
                  取消生成只会停止本地等待，远端服务可能仍在运行并继续计费。
                </p>
                <details>
                  <summary>请求示例（不含凭据）</summary>
                  <pre>{EXAMPLE}</pre>
                </details>
              </div>
            </>
          ) : (
            <LocalMusicModels />
          )}

          {problem ? <p className="music-gen__error">{problem}</p> : null}

          <div className="music-gen__actions">
            <Button
              size="sm"
              variant="solid"
              type="submit"
              leading={<Save size={14} />}
              disabled={!canSave}
            >
              {saving ? '保存中…' : '保存'}
            </Button>
            <Button
              size="sm"
              variant="ghost"
              leading={<RefreshCw size={14} />}
              disabled={saving || loading}
              onClick={() => void load()}
            >
              {dirty ? '放弃修改并重新读取' : '刷新'}
            </Button>
          </div>
        </form>
      ) : null}

      <p className="music-gen__error" role="alert">
        {saveError ?? ''}
      </p>
      <p className="music-gen__ok" role="status">
        {notice ?? ''}
      </p>
    </section>
  );
}
