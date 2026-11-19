interface StatusBarProps {
  saveState: 'clean' | 'dirty' | 'saving' | 'error';
  lastSavedAt: Date | null;
  message: string;
  projectName: string;
  fps: number;
  itemCount: number;
  zoom: number;
  sourcePreview: boolean;
}

function savedLabel(state: StatusBarProps['saveState'], date: Date | null): string {
  if (state === 'saving') return '保存中…';
  if (state === 'dirty') return '未保存的修改';
  if (state === 'error') return '保存失败';
  if (!date) return '已保存';
  return `已保存 ${date.toLocaleTimeString('zh-CN', { hour12: false })}`;
}

export function StatusBar({
  saveState,
  lastSavedAt,
  message,
  projectName,
  fps,
  itemCount,
  zoom,
  sourcePreview,
}: StatusBarProps) {
  return (
    <footer className="status-bar">
      <div className="status-brand">
        <span className="status-mark" aria-hidden="true" />
        LIVECLIPPER
        <span className="status-muted">
          项目：{projectName} · {sourcePreview ? '源预览' : `序列 01 · ${itemCount} 段`} · {fps || 30}p
        </span>
      </div>
      <div className="status-right">
        <span
          className={`status-${saveState === 'clean' ? 'saved' : saveState}`}
          title={saveState === 'error' ? message : undefined}
        >
          <span className="status-dot" aria-hidden="true" />
          {savedLabel(saveState, lastSavedAt)}
        </span>
        <span className="status-message" title={message}>
          {message}
        </span>
        <span className="status-muted">{sourcePreview ? '源素材' : '工程时间线'}</span>
        <span className="status-muted">缩放 {zoom}%</span>
      </div>
    </footer>
  );
}
