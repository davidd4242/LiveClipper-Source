import { useCallback, useEffect, useRef, useState } from 'react';
import { HardDrive, RefreshCw, Server } from 'lucide-react';
import { Button, Card, Dialog } from '../../design';
import { DownloadUninstallSwitch } from '../../components/DownloadUninstallSwitch';
import {
  cancelManagedOllamaInstallation,
  manageOllamaDaemon,
  saveOllamaRuntimeLocation,
  type OllamaDaemonAction,
  getOllamaManagementStatus,
  installManagedOllama,
  isOllamaInstallationActive,
  OLLAMA_PACKAGE_SIZE,
  OLLAMA_PACKAGE_VERSION,
  OllamaManagementRequestError,
  type OllamaInstallation,
  type OllamaManagementStatus,
} from '../../api/ollamaManagement';
import './OllamaManagementPanel.css';

const STATE_LABELS: Record<string, string> = {
  idle: '当前没有安装任务',
  downloading: '正在下载程序包',
  installing: '正在安装程序文件',
  verifying: '正在校验文件',
  cancelling: '正在取消，等待任务结束',
  completed: '安装任务已完成',
  cancelled: '安装任务已取消',
  failed: '安装任务失败',
};
const STAGE_LABELS: Record<string, string> = {
  idle: '等待操作',
  'inspecting-existing': '检查已有私有文件',
  'already-installed': '已有有效的私有程序文件',
  'inspection-refused': '已有目录未通过检查，已停止安装',
  'preparing-download': '准备下载',
  'checking-cache': '校验已有程序包',
  downloading: '接收程序包',
  verifying: '校验程序包完整性',
  verified: '程序包校验通过',
  'installing-package': '解压并安装程序文件',
  'installation-failed': '安装程序文件失败',
  'inspecting-installed': '核对已安装的程序文件',
  'installation-inspected': '程序文件核对结束',
};
const OUTCOME_LABELS: Record<string, string> = {
  'private-root-changed': '私有目录已变化，请刷新后重新确认。',
  'confirmation-required': '安装需要明确确认。',
  'version-mismatch': '程序包版本已变化，请刷新后重新确认。',
  'application-stopping': '应用正在退出，暂不能安装。',
  'installation-in-progress': '已有安装任务正在进行，请等待其结束。',
  'private-root-invalid': '私有目录不可用，请人工核对存储位置。',
  'existing-program-refused': '已有程序目录无法确认身份，已拒绝覆盖。请人工核对，不会自动删除或修复。',
  'no-active-installation': '没有可取消的安装任务，请刷新查看最新结果。',
  'installation-cancelled': '本次安装已取消，请查看任务状态。',
  'installation-refused': '本次安装未开始，请查看任务状态。',
};

function errorText(error: unknown): string {
  if (error instanceof OllamaManagementRequestError) {
    return OUTCOME_LABELS[error.result?.outcome ?? ''] ?? error.message;
  }
  return error instanceof Error ? (OUTCOME_LABELS[error.message] ?? error.message) : '请求失败，请手动刷新状态';
}

function bytesText(value: number): string {
  return `${(value / 1e9).toFixed(2)} GB`;
}

function isKnownState(state: string | undefined): boolean {
  return state !== undefined && Object.hasOwn(STATE_LABELS, state);
}

export function OllamaManagementPanel({ onConnectionChanged }: { onConnectionChanged: () => void }) {
  const [status, setStatus] = useState<OllamaManagementStatus | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [readError, setReadError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [confirmationRoot, setConfirmationRoot] = useState<string | null>(null);
  const [daemonAction, setDaemonAction] = useState<OllamaDaemonAction | null>(null);
  const [daemonBusy, setDaemonBusy] = useState(false);
  const [locationDraft, setLocationDraft] = useState<string | null>(null);
  const [locationBusy, setLocationBusy] = useState(false);
  const [locationError, setLocationError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState<'install' | 'cancel' | null>(null);
  const [needsRefresh, setNeedsRefresh] = useState(false);
  const [cancelAccepted, setCancelAccepted] = useState(false);
  const alive = useRef(false);
  const snapshot = useRef<OllamaManagementStatus | null>(null);
  const readController = useRef<AbortController | null>(null);
  const timer = useRef<number | null>(null);
  const reading = useRef(false);
  const mutation = useRef(false);
  const readSequence = useRef(0);
  const lastSelected = useRef<boolean | undefined>(undefined);
  const observeSelection = useCallback((selected: boolean) => {
    const previous = lastSelected.current;
    lastSelected.current = selected;
    if (previous !== undefined && previous !== selected) onConnectionChanged();
  }, [onConnectionChanged]);
  const selected = status?.daemon?.selected;
  useEffect(() => {
    if (selected !== undefined) observeSelection(selected);
  }, [selected, observeSelection]);

  const clearPoll = useCallback(() => {
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = null;
  }, []);

  const refresh = useCallback(async function readStatus(): Promise<void> {
    if (!alive.current || reading.current || mutation.current) return;
    clearPoll();
    reading.current = true;
    setRefreshing(true);
    const controller = new AbortController();
    readController.current = controller;
    const sequence = ++readSequence.current;
    try {
      const next = await getOllamaManagementStatus(controller.signal);
      if (!alive.current || sequence !== readSequence.current) return;
      snapshot.current = next;
      setStatus(next);
      setReadError(null);
      setNeedsRefresh(false);
      if (!isOllamaInstallationActive(next.installation.state) && isKnownState(next.installation.state)) {
        setCancelAccepted(false);
      }
      if (isOllamaInstallationActive(next.installation.state)) {
        // Schedule only after this GET settles: never overlap polling requests.
        timer.current = window.setTimeout(() => void readStatus(), 2000);
      }
    } catch (error) {
      if (alive.current && sequence === readSequence.current && !controller.signal.aborted) {
        setReadError(errorText(error));
        // A failed read stops polling; manual refresh is the only retry.
      }
    } finally {
      if (sequence === readSequence.current) {
        reading.current = false;
        readController.current = null;
        if (alive.current) setRefreshing(false);
      }
    }
  }, [clearPoll]);

  useEffect(() => {
    alive.current = true;
    void refresh();
    return () => {
      alive.current = false;
      clearPoll();
      readSequence.current += 1;
      reading.current = false;
      readController.current?.abort();
      readController.current = null;
      // Leaving the page stops observation, not the server-side installation.
    };
  }, [clearPoll, refresh]);

  const applyInstallation = (installation: OllamaInstallation) => {
    const previous = snapshot.current;
    if (!previous) return;
    const next = { ...previous, installation };
    snapshot.current = next;
    setStatus(next);
  };

  const captureConfirmationRoot = () => setConfirmationRoot(snapshot.current?.paths.privateRoot ?? null);
  const openDaemonConfirmation = (action: OllamaDaemonAction) => {
    captureConfirmationRoot();
    setDaemonAction(action);
  };

  const runAction = async (kind: 'install' | 'cancel') => {
    if (mutation.current || !alive.current) return;
    const current = snapshot.current;
    if (!current || readError || needsRefresh) return;
    if (kind === 'install' && (!confirmOpen || !confirmationRoot || current.paths.privateRoot !== confirmationRoot || current.managed.status !== 'not-installed'
      || !current.paths.privateRoot || !current.installationActionsImplemented
      || current.package.version !== OLLAMA_PACKAGE_VERSION || current.package.size !== OLLAMA_PACKAGE_SIZE
      || !isKnownState(current.installation.state) || isOllamaInstallationActive(current.installation.state)
      || current.installation.reason === 'existing-program-not-managed-valid')) return;
    if (kind === 'cancel' && (!isOllamaInstallationActive(current.installation.state)
      || current.installation.cancellationRequested || cancelAccepted)) return;

    mutation.current = true;
    clearPoll();
    readSequence.current += 1;
    readController.current?.abort();
    readController.current = null;
    reading.current = false;
    setRefreshing(false);
    setSubmitting(kind);
    setActionError(null);
    setConfirmOpen(false);
    setNeedsRefresh(true);
    try {
      const result = kind === 'install'
        ? await installManagedOllama(confirmationRoot!)
        : await cancelManagedOllamaInstallation();
      if (alive.current) {
        applyInstallation(result.installation);
        if (kind === 'cancel' && result.statusCode === 202) setCancelAccepted(true);
      }
    } catch (error) {
      if (alive.current) {
        if (error instanceof OllamaManagementRequestError && error.result) {
          applyInstallation(error.result.installation);
        }
        setActionError(`${errorText(error)} 若请求已送达，后台任务可能仍在进行；以下次状态读取为准，请勿重复提交。`);
      }
    } finally {
      mutation.current = false;
      if (alive.current) {
        setSubmitting(null);
        void refresh();
      }
    }
  };

  const runDaemonAction = async () => {
    if (!daemonAction || mutation.current || (daemonNeedsRoot && confirmationRootChanged)) return;
    mutation.current = true;
    clearPoll();
    readSequence.current += 1;
    readController.current?.abort();
    reading.current = false;
    setRefreshing(false);
    setDaemonBusy(true);
    setActionError(null);
    setNeedsRefresh(true);
    try {
      await manageOllamaDaemon(daemonAction, confirmationRoot ?? undefined);
      if (alive.current && (daemonAction === 'private' || daemonAction === 'original')) observeSelection(daemonAction === 'private');
      setDaemonAction(null);
    } catch (error) {
      setActionError(errorText(error));
      setDaemonAction(null);
    } finally {
      mutation.current = false;
      if (alive.current) {
        setDaemonBusy(false);
        void refresh();
      }
    }
  };

  const saveLocation = async () => {
    if (locationDraft === null || locationBlocked || mutation.current || confirmationRootChanged) return;
    mutation.current = true;
    setLocationBusy(true);
    setLocationError(null);
    setNeedsRefresh(true);
    try {
      await saveOllamaRuntimeLocation(locationDraft.trim() || null, confirmationRoot!);
      if (alive.current) setLocationDraft(null);
    } catch (error) {
      if (alive.current) setLocationError(errorText(error));
    } finally {
      mutation.current = false;
      if (alive.current) {
        setLocationBusy(false);
        void refresh();
      }
    }
  };

  const installation = status?.installation;
  const active = isOllamaInstallationActive(installation?.state);
  const connectionKnown = status?.connectionKnown === true && status.connection !== null;
  const connectionReady = connectionKnown && status?.connection?.ready === true;
  const managed = status?.managed;
  const valid = managed?.status === 'managed-valid';
  const invalid = managed?.status === 'managed-invalid';
  const missing = managed?.status === 'not-installed';
  const programRefused = installation?.reason === 'existing-program-not-managed-valid';
  const packageMatches = status?.package.version === OLLAMA_PACKAGE_VERSION && status.package.size === OLLAMA_PACKAGE_SIZE;
  const installed = valid ? true : missing && !programRefused ? false : null;
  const blockReason = readError ? '状态读取失败，请先手动刷新'
    : needsRefresh ? '请求已发送，需读取最新状态后才能继续'
      : submitting || daemonBusy || locationBusy ? '正在提交请求'
        : active ? '安装任务尚未结束'
          : !status || !isKnownState(installation?.state) ? '安装状态未知，请刷新确认'
            : invalid || programRefused ? '已有程序目录需人工核对，禁止覆盖'
              : !missing && !valid ? '私有文件状态未知，请刷新确认'
                : !status.paths.privateRoot ? '尚未确认私有目录'
                  : !status.installationActionsImplemented ? '安装操作尚未接入'
                    : !packageMatches ? '程序包信息不匹配，请刷新后核对'
                      : undefined;
  const daemon = status?.daemon;
  const daemonBlocked = !status?.daemonActionsImplemented || !daemon || !!readError
    || needsRefresh || refreshing || submitting !== null || daemonBusy || locationBusy || active;
  const confirmationRootChanged = !confirmationRoot || confirmationRoot !== status?.paths.privateRoot;
  const daemonNeedsRoot = daemonAction === 'start' || daemonAction === 'uninstall';
  const staleLocationMessage = '私有目录已变化，请关闭并重新确认。';
  const locationBlocked = daemonBlocked || daemon?.owned === true || !status?.paths.defaultBaseDirectory;
  const daemonLabels: Record<OllamaDaemonAction, string> = {
    start: '启动私有服务', stop: '停止私有服务', private: '本次使用私有服务',
    original: '恢复原连接', uninstall: '移出私有程序（可恢复）',
  };
  const received = installation?.bytesReceived;
  const total = installation?.totalBytes;
  const hasByteProgress = typeof received === 'number' && Number.isFinite(received) && received >= 0
    && typeof total === 'number' && Number.isFinite(total) && total > 0;
  const showProgress = installation?.state === 'downloading' && installation.stage === 'downloading' && hasByteProgress;
  const terminalProblem = installation?.state === 'cancelled' || installation?.state === 'failed';

  return (
    <Card
      title="Ollama 私有程序"
      meta="管理程序文件，不改变当前 AI 连接或推理选择"
      className="settings-ollama-span ollama-management"
      actions={(
        <Button leading={<RefreshCw size={15} />} loading={refreshing} disabled={submitting !== null || daemonBusy || locationBusy}
          onClick={() => void refresh()}>刷新状态</Button>
      )}
    >
      {readError ? (
        <p className="ollama-management__error" role="alert">
          状态读取失败：{readError}。自动刷新已暂停，请手动刷新。{status ? '下方保留的是上次读取结果，不代表当前状态。' : ''}
        </p>
      ) : null}
      <div className="ollama-management__overview">
        <section className="ollama-management__state">
          <h3><Server size={17} aria-hidden="true" />当前 Ollama 连接</h3>
          <strong>{!connectionKnown ? '连接状态未知' : connectionReady ? '连接可用' : '连接不可用'}</strong>
          <p>{connectionReady
            ? `${status?.daemon?.selected ? '本次使用私有连接' : '继续使用原连接'}${status?.connection?.version ? ` · ${status.connection.version}` : ''}。私有安装不会切换或替换它。`
            : connectionKnown ? '尚未连接到可用服务；这不等于本机没有安装 Ollama。'
              : '尚未取得可确认的连接结果，不据此判断是否安装。'}</p>
        </section>
        <section className="ollama-management__state">
          <h3><HardDrive size={17} aria-hidden="true" />应用私有文件</h3>
          <strong>{valid ? `程序文件已安装 · ${managed.version ?? OLLAMA_PACKAGE_VERSION}`
            : invalid || programRefused ? '已有文件未通过核对' : missing ? '未安装私有程序文件' : '私有文件状态未知'}</strong>
          <p>{valid ? '文件身份已核对；不代表服务已启动或正在用于推理。'
            : invalid || programRefused ? '已有目录需人工核对。不会覆盖、自动删除或修复。'
              : missing ? '与现有外部 Ollama 分开存放，不是升级或替代现有版本。'
                : '读取状态后才能决定是否安装。'}</p>
        </section>
      </div>
      <div className="ollama-management__install">
        <div>
          <strong>固定版本 {OLLAMA_PACKAGE_VERSION}</strong>
          <p>程序包约 1.46 GB（1,461,196,158 字节），不含模型。</p>
        </div>
        <div className="ollama-management__controls">
          <DownloadUninstallSwitch installed={installed} canUninstall={!daemonBlocked && !daemon?.owned && !daemon?.selected}
            busy={blockReason !== undefined} disabledReason={blockReason}
            onDownload={() => { setActionError(null); captureConfirmationRoot(); setConfirmOpen(true); }} onUninstall={() => openDaemonConfirmation('uninstall')} />
          {active ? (
            <Button variant="ghost" disabled={!!readError || needsRefresh || submitting !== null
              || installation?.cancellationRequested || cancelAccepted}
              onClick={() => void runAction('cancel')}>
              {submitting === 'cancel' ? '正在请求取消…' : installation?.cancellationRequested || cancelAccepted ? '已请求取消，等待终态' : '请求取消安装'}
            </Button>
          ) : null}
        </div>
      </div>
      {blockReason && !valid ? <p className="ollama-management__note">{blockReason}</p> : null}
      {actionError ? <p className="ollama-management__error" role="alert">{actionError}</p> : null}
      {installation ? (
        <section className="ollama-management__task" aria-label="安装任务状态" aria-busy={active || undefined}>
          <p role="status" aria-live="polite">
            <strong>{STATE_LABELS[installation.state] ?? `未识别的任务状态：${installation.state}`}</strong>
            {' · '}{STAGE_LABELS[installation.stage] ?? installation.stage}
          </p>
          {installation.state === 'completed' ? (
            <p>{valid ? '程序文件已安装。服务未由此启动，当前推理选择未改变。'
              : '任务报告完成，但尚未确认私有文件有效。请刷新核对，暂不能视为已安装。'}</p>
          ) : null}
          {installation.state === 'cancelling' || cancelAccepted ? (
            <p>取消请求仅已受理，后台仍可能在退出或核对文件。将继续读取状态，直到实际结束。</p>
          ) : null}
          {showProgress ? (
            <div className="ollama-management__progress">
              <progress value={Math.min(received, total)} max={total} aria-label="程序包下载字节进度" />
              <span>{bytesText(received)} / {bytesText(total)} · {Math.min(100, received / total * 100).toFixed(1)}%</span>
            </div>
          ) : active ? <p>等待后台报告当前阶段结果；校验和安装不显示估算进度。</p> : null}
          {terminalProblem ? <p>任务已结束。已下载的部分文件或安装暂存目录可能保留，不会自动删除，也不保证下次可以续传。</p> : null}
          {terminalProblem && hasByteProgress ? <p>已接收 {bytesText(received)} / {bytesText(total)}，不代表已安装。</p> : null}
          {terminalProblem || invalid || programRefused ? (
            <details className="ollama-management__details">
              <summary>查看核对信息与保留位置</summary>
              <dl>
                <div><dt>阶段</dt><dd>{installation.stage}</dd></div>
                <div><dt>原因</dt><dd>{installation.reason ?? managed?.reason ?? '未提供'}</dd></div>
                {installation.packagePath ? <div><dt>程序包 / 部分文件</dt><dd>{installation.packagePath}</dd></div> : null}
                {installation.stagingDirectory ? <div><dt>安装暂存目录</dt><dd>{installation.stagingDirectory}</dd></div> : null}
                {installation.taskId ? <div><dt>任务编号</dt><dd>{installation.taskId}</dd></div> : null}
              </dl>
            </details>
          ) : null}
        </section>
      ) : !readError ? <p className="ollama-management__note">正在读取连接与私有文件状态…</p> : null}
      <dl className="ollama-management__paths">
        <div><dt>私有目录</dt><dd>{status?.paths.privateRoot ?? '尚未确认'}</dd></div>
        <div><dt>程序目录</dt><dd>{status?.paths.programDirectory ?? '尚未确认'}</dd></div>
      </dl>
      <p>私有运行位置：{status?.paths.runtimeBaseDirectory ?? '使用默认存储目录'}</p>
      <p>默认：{status?.paths.defaultBaseDirectory ?? '尚未确认'}</p>
      <Button disabled={locationBlocked} onClick={() => { setLocationError(null); captureConfirmationRoot(); setLocationDraft(status?.paths.runtimeBaseDirectory ?? ''); }}>
        更改私有运行目录
      </Button>
      {daemon?.owned ? <p>请先恢复原连接并停止私有服务，再更改目录。</p> : null}
      <Dialog open={locationDraft !== null} title="更改私有运行目录"
        description="只更改 LiveClipper 私有 Ollama 的位置。"
        onClose={() => { if (!locationBusy) setLocationDraft(null); }}
        footer={<>
          <Button disabled={locationBusy} onClick={() => setLocationDraft(null)}>取消</Button>
          <Button variant="solid" disabled={locationBlocked || confirmationRootChanged ||
            (locationDraft?.trim() || null) === status?.paths.runtimeBaseDirectory}
            onClick={() => void saveLocation()}>确认保存目录</Button>
        </>}>
        <label htmlFor="ollama-runtime-base">父目录（留空恢复默认）</label>
        <input id="ollama-runtime-base" type="text" placeholder="例如 F:/LiveClipper-Runtime"
          value={locationDraft ?? ''} disabled={locationBusy}
          onChange={(event) => setLocationDraft(event.target.value)} />
        <p>填写完整的子目录路径，不要填写盘符根目录。程序、包缓存和私有模型将使用该目录下的 runtime/ollama-managed。</p>
        <p>不会迁移或删除旧文件，也不会下载、安装或切换外部 Ollama；不更改数据库、项目或导出位置。</p>
        <p>当前私有目录：{confirmationRoot}</p>
        {confirmationRootChanged ? <p role="alert">{staleLocationMessage}</p> : null}
        {locationError ? <p role="alert">{locationError}</p> : null}
      </Dialog>
      <section className="ollama-management__task" aria-label="私有服务管理">
        <p role="status">私有服务：{daemon?.owned ? (daemon.ready ? '运行且就绪' : '正在运行，尚未就绪') : '未运行'}
          {' · '}{daemon?.selected ? '本次会话正在使用私有连接' : '正在使用原连接'}</p>
        <div className="ollama-management__controls">
          <Button disabled={daemonBlocked || !valid || daemon?.owned}
            onClick={() => openDaemonConfirmation('start')}>启动私有服务</Button>
          <Button disabled={daemonBlocked || !daemon?.owned || daemon?.selected}
            onClick={() => openDaemonConfirmation('stop')}>停止私有服务</Button>
          <Button disabled={daemonBlocked || !daemon?.ready || !daemon?.owned || daemon?.selected}
            onClick={() => openDaemonConfirmation('private')}>本次使用私有服务</Button>
          <Button disabled={daemonBlocked || !daemon?.selected}
            onClick={() => openDaemonConfirmation('original')}>恢复原连接</Button>
          <Button disabled={daemonBlocked || !valid || daemon?.owned || daemon?.selected}
            onClick={() => openDaemonConfirmation('uninstall')}>移出私有程序（可恢复）</Button>
        </div>
        <p>仅管理本应用启动的私有进程，不停止或替换外部 Ollama。切换只对本次会话生效，重启恢复原连接。使用中请先恢复原连接，再停止服务。</p>
        {daemon?.reason ? <p>{daemon.reason}</p> : null}
        {daemon?.retiredDirectory ? <p>已移出程序保留于：{daemon.retiredDirectory}</p> : null}
      </section>
      <Dialog open={daemonAction !== null} title={daemonAction ? daemonLabels[daemonAction] : ''}
        description="请核对本次操作范围" onClose={() => { if (!daemonBusy) setDaemonAction(null); }}
        footer={<>
          <Button disabled={daemonBusy} onClick={() => setDaemonAction(null)}>取消</Button>
          <Button variant="solid" disabled={daemonBusy || daemonBlocked || (daemonNeedsRoot && confirmationRootChanged)} onClick={() => void runDaemonAction()}>确认操作</Button>
        </>}>
        <p>{daemonAction === 'uninstall'
          ? '仅将已核对的私有 program 目录移动到同目录内的保留位置，不永久删除，不释放磁盘空间。模型与下载缓存保留。'
          : daemonAction === 'start'
            ? '启动已核对的私有程序，使用独立本机端口。不下载模型，不改变当前推理连接。'
            : daemonAction === 'stop'
              ? '只停止本应用启动的私有进程，不影响外部 Ollama。'
              : '只切换本次会话的本地推理连接，对下一次请求生效，已执行的请求不会迁移。不改变在线服务设置，不迁移模型，不修改外部服务。重启软件后恢复原连接。'}</p>
        <p>私有目录：{confirmationRoot}</p>
        {daemonNeedsRoot && confirmationRootChanged ? <p role="alert">{staleLocationMessage}</p> : null}
      </Dialog>
      <Dialog open={confirmOpen} title="下载并安装私有 Ollama？" className="ollama-management-dialog"
        description="只有点击确认后才会发起下载和安装。" onClose={() => setConfirmOpen(false)}
        footer={(
          <>
            <Button onClick={() => setConfirmOpen(false)}>暂不安装</Button>
            <Button variant="solid" disabled={blockReason !== undefined || refreshing || confirmationRootChanged} onClick={() => void runAction('install')}>
              确认下载并安装
            </Button>
          </>
        )}>
        <div className="ollama-management__confirmation">
          <p>固定版本 <strong>{OLLAMA_PACKAGE_VERSION}</strong>，程序包约 <strong>1.46 GB</strong>（1,461,196,158 字节）。解压安装还需额外磁盘空间。</p>
          <dl><div><dt>写入应用私有目录</dt><dd>{confirmationRoot ?? '尚未确认，暂不能安装'}</dd></div></dl>
          <p>不会下载模型，不会停止、升级或替换外部 Ollama 服务，也不会自动启动服务、切换连接或改变推理选择。</p>
          {connectionReady ? <p>当前已有可用连接，安装后仍使用现有连接。</p> : null}
          <p>已有程序目录若无法核对身份，将拒绝覆盖。请求取消后需等待后台任务真实结束；部分文件可能保留，不保证续传。</p>
          {confirmationRootChanged ? <p role="alert">{staleLocationMessage}</p> : null}
          {blockReason ? <p className="ollama-management__error">{blockReason}</p> : null}
        </div>
      </Dialog>
    </Card>
  );
}
