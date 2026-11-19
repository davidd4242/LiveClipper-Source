// Desktop shell bridge: asks the native window to show a finished render in Explorer.
// Only the job id crosses the bridge; the native side resolves the file from its own render records.
interface WebViewHost {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}
const host = (): WebViewHost | null => (window as unknown as { chrome?: { webview?: WebViewHost } }).chrome?.webview ?? null;

export const canRevealExport = (): boolean => host() !== null;

const REVEAL_TIMEOUT_MS = 15000;

/** Resolves on success; rejects with a user-visible message. */
export function revealExportInFolder(jobId: string): Promise<void> {
  const webview = host();
  if (!webview) return Promise.reject(new Error('当前环境不支持打开文件夹。'));
  const requestId = `reveal-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
  return new Promise<void>((resolve, reject) => {
    const finish = (action: () => void) => { window.clearTimeout(timer); webview.removeEventListener('message', onMessage); action(); };
    const onMessage = (event: { data: unknown }) => {
      const data = event.data as { type?: string; requestId?: string; ok?: boolean; error?: string | null } | null;
      if (!data || data.type !== 'reveal-export-result' || data.requestId !== requestId) return;
      finish(() => data.ok ? resolve() : reject(new Error(data.error || '无法打开所在文件夹。')));
    };
    const timer = window.setTimeout(() => finish(() => reject(new Error('打开文件夹超时，请稍后重试。'))), REVEAL_TIMEOUT_MS);
    webview.addEventListener('message', onMessage);
    try { webview.postMessage({ type: 'reveal-export', requestId, jobId }); }
    catch (error) { finish(() => reject(error instanceof Error ? error : new Error('无法发送打开文件夹请求。'))); }
  });
}
