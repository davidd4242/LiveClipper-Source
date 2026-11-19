import { useEffect, useMemo, useSyncExternalStore } from 'react';
import { Button, Dialog } from '../../design';
import { cancelOllamaModelDownload, getOllamaModelDownload, startOllamaModelDownload } from '../../api/settings';
import { createOllamaModelDownloadController, installedOllama7B, OLLAMA_7B_MODEL } from './ollamaModelDownloadController';

const LABELS = { idle: '未开始下载', downloading: '正在下载', completed: '下载完成', cancelled: '已取消', failed: '下载失败' };
export function OllamaModelDownloadPanel({ installedModels = null }: { installedModels?: readonly string[] | null }) {
  const installedModel = installedOllama7B(installedModels);
  const modelsKnown = installedModels !== null;
  const controller = useMemo(() => createOllamaModelDownloadController({ read: getOllamaModelDownload, start: startOllamaModelDownload, cancel: cancelOllamaModelDownload }), []);
  const state = useSyncExternalStore(controller.subscribe, controller.getState, controller.getState);
  const downloading = state.download?.state === 'downloading';
  useEffect(() => {
    if (!modelsKnown || installedModel) controller.dismiss();
  }, [controller, modelsKnown, installedModel]);
  useEffect(() => {
    controller.activate();
    void controller.refresh();
    return () => controller.dispose();
  }, [controller]);
  useEffect(() => {
    if (!downloading || state.busy || state.error) return;
    const timer = window.setTimeout(() => void controller.refresh(), 1500);
    return () => window.clearTimeout(timer);
  }, [controller, downloading, state.busy, state.error, state.download]);
  return <section className="settings-field" aria-label="可选本地模型下载">
    <span className="settings-field__label">可选本地模型下载</span>
    <p className="settings-hint">7B：{OLLAMA_7B_MODEL}。按需下载，不会自动下载、删除模型或改变已保存的模型选择。</p>
    <div className="settings-inline">
      {installedModel ? <span className="settings-hint">7B 已安装，可使用：{installedModel}。无需重复下载。</span> : <Button disabled={!modelsKnown || !state.download || state.busy || !!state.error || downloading} onClick={() => controller.prepare()}>下载 7B 模型…</Button>}
      <Button disabled>4B 下载（型号尚未配置）</Button>
      <Button variant="outline" disabled={state.busy} onClick={() => void controller.refresh()}>刷新下载状态</Button>
      {downloading ? <Button variant="outline" disabled={state.busy || state.cancelRequested} onClick={() => void controller.cancel()}>取消下载</Button> : null}
    </div>
    {!modelsKnown ? <p className="settings-hint">请先刷新上方已安装模型列表，确认本机状态后再下载。</p> : null}
    <p className="settings-hint">4B 下载入口已预留，具体型号确定后再启用。下载完成后请刷新上方已安装模型列表，选择并保存才会切换。</p>
    <div role="status" aria-live="polite">{state.download ? `${LABELS[state.download.state]}${state.download.model ? `：${state.download.model}` : ''}` : '正在读取下载状态…'}{downloading ? ` ${state.download!.progress}%` : ''}{state.cancelRequested ? '；已请求取消，等待服务确认' : ''}</div>
    {downloading ? <progress max={100} value={state.download!.progress} aria-label="模型下载进度" /> : null}
    {state.download?.detail ? <p className="settings-hint">{state.download.detail}</p> : null}
    {state.error ? <p role="alert">{state.error}。请刷新状态后再操作，不会自动重试下载。</p> : null}
    <Dialog open={state.confirmOpen && modelsKnown && !installedModel} title="下载 7B 本地模型？" onClose={() => controller.dismiss()} footer={<><Button variant="outline" onClick={() => controller.dismiss()}>暂不下载</Button><Button disabled={!modelsKnown || !!installedModel} onClick={() => { if (modelsKnown && !installedModel) void controller.confirm(); }}>确认下载</Button></>}>
      <p>将通过当前本机 Ollama 下载 {OLLAMA_7B_MODEL}，需要网络和数 GB 磁盘空间，存储位置沿用当前 Ollama 配置。</p>
      <p>这是可选下载。不会删除已有模型，也不会自动选择或保存此模型。取消会停止本次请求，Ollama 已下载的缓存可能保留。</p>
    </Dialog>
  </section>;
}
