import { formatClock } from '../project';
import {
  connectionLabel,
  connectionTone,
  editorSaveStatus,
  orDash,
  type EditorSaveStatusInput,
} from './editorChrome';
import './EditorStatusbar.css';

export interface EditorStatusbarProps extends EditorSaveStatusInput {
  projectName: string;
  sequenceLabel: string;
  /** 工程帧率；未知传 null（显示「—」）。 */
  fps: number | null;
  /** 画面宽度；未知传 null。 */
  width: number | null;
  /** 画面高度；未知传 null。 */
  height: number | null;
  /** 序列总时长（秒）；未知传 null。 */
  duration: number | null;
  /** 当前帧号；未知传 null。 */
  frame: number | null;
  itemCount: number;
  zoom: number;
  message: string;
  sourcePreview: boolean;
}

const DASH = '—';

/**
 * 编辑器外壳状态栏（P0）：28px 单行，只展示 EditorPage 已有状态。
 *
 * 连接态由 loadState 映射；帧率/分辨率/时长/帧号拿不到时统一显示「—」，不伪造。
 */
export function EditorStatusbar({
  loadState,
  projectName,
  sequenceLabel,
  fps,
  width,
  height,
  duration,
  frame,
  itemCount,
  zoom,
  message,
  sourcePreview,
  saveState,
  lastSavedAt,
  saveError,
  saveConflict,
  recoveredDraft,
  hasUnsavedChanges,
}: EditorStatusbarProps) {
  const saveStatus = editorSaveStatus({
    loadState,
    saveState,
    lastSavedAt,
    saveError,
    saveConflict,
    recoveredDraft,
    hasUnsavedChanges,
  });
  const resolution = width !== null && height !== null ? `${width}×${height}` : DASH;
  const fpsLabel = fps !== null ? `${fps} fps` : DASH;
  const durationLabel = duration !== null ? formatClock(duration) : DASH;
  const frameLabel = frame !== null ? `帧 ${frame}` : `帧 ${DASH}`;

  return (
    <footer className="ed-statusbar" role="contentinfo" aria-label="编辑器状态栏">
      <div className="ed-statusbar__group ed-statusbar__group--left">
        <span className="ed-statusbar__brand">
          <span className="ed-statusbar__mark" aria-hidden="true" />
          LIVECLIPPER
        </span>
        <span className="ed-statusbar__item ed-statusbar__item--project" title={projectName}>
          {projectName}
        </span>
        <span className="ed-statusbar__item">{sequenceLabel}</span>
        <span className="ed-statusbar__item">{orDash(itemCount)} 段</span>
      </div>

      <div className="ed-statusbar__group ed-statusbar__group--center">
        <span
          className={`ed-statusbar__conn ed-statusbar__conn--${connectionTone(loadState)}`}
          title={`后端连接状态：${connectionLabel(loadState)}`}
        >
          <span className="ed-statusbar__dot" aria-hidden="true" />
          {connectionLabel(loadState)}
        </span>
        <span className="ed-statusbar__item" title="工程帧率">
          {fpsLabel}
        </span>
        <span className="ed-statusbar__item" title="画面分辨率">
          {resolution}
        </span>
        <span className="ed-statusbar__item" title="序列总时长">
          {durationLabel}
        </span>
        <span className="ed-statusbar__item" title="播放头所在帧">
          {frameLabel}
        </span>
      </div>

      <div className="ed-statusbar__group ed-statusbar__group--right">
        <span className="ed-statusbar__message" title={message} aria-live="polite">
          {message}
        </span>
        <span className="ed-statusbar__item">{sourcePreview ? '源素材' : '工程时间线'}</span>
        <span className="ed-statusbar__item">缩放 {zoom}%</span>
        <span
          className={`ed-statusbar__save ed-statusbar__save--${saveStatus.state}`}
          title={saveStatus.title}
          aria-live="polite"
          aria-atomic="true"
        >
          <span className="ed-statusbar__save-dot" aria-hidden="true" />
          {saveStatus.label}
        </span>
      </div>
    </footer>
  );
}
