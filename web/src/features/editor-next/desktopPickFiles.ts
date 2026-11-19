// Desktop shell bridge: asks the native window to open a file picker seeded with a quick location.
// Only the restricted location enum crosses the bridge; the native side (MainWindow.xaml.cs) maps it
// to a Windows known folder, so the page can never seed the dialog with an arbitrary path.
// Picked absolute paths come back and are imported through /api/studio/import-paths — no synthetic File objects.
interface WebViewHost {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}
const host = (): WebViewHost | null => (window as unknown as { chrome?: { webview?: WebViewHost } }).chrome?.webview ?? null;

/**
 * 打开桌面文件选择器（多选；起始位置由受限枚举决定，见 MainWindow.xaml.cs 白名单）。
 * - 用户取消（原生回 paths=[]）→ 解析为 []，调用方零请求。
 * - 桥不可用（浏览器）→ reject，调用方需明示该快捷位置不可用，不得假成功。
 * 不设超时：原生对话框可能一直开着，定时器会误杀真实选择。
 */
export function pickVideoPathsViaDesktop(location: string): Promise<string[]> {
  const webview = host();
  if (!webview) return Promise.reject(new Error('该快捷位置仅在桌面版可用，请用「导入媒体文件…」选择文件。'));
  const requestId = `pick-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
  return new Promise<string[]>((resolve, reject) => {
    const finish = (action: () => void) => { webview.removeEventListener('message', onMessage); action(); };
    const onMessage = (event: { data: unknown }) => {
      const data = event.data as { type?: string; requestId?: string; paths?: unknown } | null;
      if (!data || data.type !== 'files-picked' || data.requestId !== requestId) return;
      const paths = Array.isArray(data.paths) && data.paths.every(p => typeof p === 'string') ? data.paths as string[] : null;
      if (paths === null) { finish(() => reject(new Error('桌面选择器返回了无效路径列表。'))); return; }
      finish(() => resolve(paths));
    };
    webview.addEventListener('message', onMessage);
    try { webview.postMessage({ type: 'pick-files', requestId, location }); }
    catch (error) { finish(() => reject(error instanceof Error ? error : new Error('无法发送文件选择请求。'))); }
  });
}
