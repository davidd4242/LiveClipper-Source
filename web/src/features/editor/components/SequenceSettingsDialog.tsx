import { useEffect, useState } from 'react';
import { Settings2 } from 'lucide-react';
import type { EditorProject } from '../../../api/types';

interface SequenceSettingsDialogProps {
  open: boolean;
  project: EditorProject | null;
  hasTranscript: boolean;
  onClose: () => void;
  onSave: (next: EditorProject) => void;
}

const RESOLUTIONS = [480, 720, 1080, 2160];
const FRAME_RATES = [24, 25, 30, 50, 60];

export function SequenceSettingsDialog({
  open,
  project,
  hasTranscript,
  onClose,
  onSave,
}: SequenceSettingsDialogProps) {
  const [resolution, setResolution] = useState(720);
  const [fps, setFps] = useState(30);
  const [bitrateKbps, setBitrateKbps] = useState(0);
  const [burnSubtitles, setBurnSubtitles] = useState(false);
  const [subtitleSize, setSubtitleSize] = useState(34);
  const [subtitleColor, setSubtitleColor] = useState('#FFFFFF');
  const [subtitlePosition, setSubtitlePosition] = useState<'top' | 'middle' | 'bottom'>('bottom');

  useEffect(() => {
    if (!open || !project) return;
    setResolution(project.resolution || 720);
    setFps(project.fps || 30);
    setBitrateKbps(project.bitrateKbps || 0);
    setBurnSubtitles(Boolean(project.burnSubtitles));
    setSubtitleSize(project.subtitleSize || 34);
    setSubtitleColor(project.subtitleColor || '#FFFFFF');
    setSubtitlePosition(project.subtitlePosition || 'bottom');
  }, [open, project]);

  if (!open || !project) return null;

  const save = () => {
    onSave({
      ...project,
      resolution,
      fps,
      bitrateKbps: Math.min(100000, Math.max(0, Math.round(bitrateKbps) || 0)),
      burnSubtitles: hasTranscript && burnSubtitles,
      subtitleSize: Math.min(80, Math.max(10, Math.round(subtitleSize) || 34)),
      subtitleColor,
      subtitlePosition,
    });
    onClose();
  };

  return (
    <div
      className="help-backdrop"
      role="presentation"
      onPointerDown={(event) => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <section className="help-dialog sequence-settings-dialog" role="dialog" aria-modal="true" aria-label="序列设置">
        <header>
          <div>
            <span className="help-kicker">SEQUENCE SETTINGS</span>
            <h2>序列设置</h2>
          </div>
          <button type="button" className="help-close" onClick={onClose} aria-label="关闭序列设置">×</button>
        </header>
        <div className="sequence-settings-grid">
          <label>
            分辨率
            <select value={resolution} onChange={(event) => setResolution(Number(event.target.value))}>
              {RESOLUTIONS.map((value) => <option key={value} value={value}>{value}p</option>)}
            </select>
          </label>
          <label>
            帧率
            <select value={fps} onChange={(event) => setFps(Number(event.target.value))}>
              {FRAME_RATES.map((value) => <option key={value} value={value}>{value} fps</option>)}
            </select>
          </label>
          <label>
            目标码率
            <input
              type="number"
              min={0}
              max={100000}
              step={500}
              value={bitrateKbps}
              onChange={(event) => setBitrateKbps(Number(event.target.value))}
            />
            <small>0 表示自动</small>
          </label>
          <label className="sequence-settings-check">
            <input
              type="checkbox"
              checked={burnSubtitles}
              disabled={!hasTranscript}
              onChange={(event) => setBurnSubtitles(event.target.checked)}
            />
            <span>导出时烧录字幕</span>
          </label>
          <label>
            字幕字号
            <input
              type="number"
              min={10}
              max={80}
              value={subtitleSize}
              onChange={(event) => setSubtitleSize(Number(event.target.value))}
            />
          </label>
          <label>
            字幕颜色
            <input
              type="color"
              value={subtitleColor}
              onChange={(event) => setSubtitleColor(event.target.value.toUpperCase())}
            />
          </label>
          <label>
            字幕位置
            <select
              value={subtitlePosition}
              onChange={(event) => setSubtitlePosition(event.target.value as 'top' | 'middle' | 'bottom')}
            >
              <option value="top">顶部</option>
              <option value="middle">居中</option>
              <option value="bottom">底部</option>
            </select>
          </label>
        </div>
        <p className="sequence-settings-hint">
          <Settings2 size={12} /> 这些设置随工程保存，导出弹窗仍可临时覆盖分辨率与帧率。
        </p>
        <footer>
          <button type="button" className="secondary-button" onClick={onClose}>取消</button>
          <button type="button" className="primary-button" onClick={save}>保存设置</button>
        </footer>
      </section>
    </div>
  );
}
