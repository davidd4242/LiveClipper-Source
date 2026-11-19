import { useEffect, useState } from 'react';
import { Layers3 } from 'lucide-react';
import type { EditorProject } from '../../../api/types';

interface TrackPropertiesDialogProps {
  open: boolean;
  project: EditorProject | null;
  onClose: () => void;
  onSave: (next: EditorProject) => void;
}

const VIDEO_TRACKS = [
  { index: 0, label: 'V1 主画面', note: '节目主画面，影响成片底色' },
  { index: 1, label: 'V2 叠加层', note: '画面叠加与画中画' },
  { index: 2, label: 'V3 图形与标题', note: '图形、标题和贴纸层' },
] as const;

export function TrackPropertiesDialog({ open, project, onClose, onSave }: TrackPropertiesDialogProps) {
  const [opacities, setOpacities] = useState([1, 1, 1]);

  useEffect(() => {
    if (!open || !project) return;
    const values = project.trackOpacities ?? [];
    setOpacities(
      Array.from({ length: 3 }, (_, index) =>
        Number.isFinite(values[index]) ? Math.min(1, Math.max(0, Number(values[index]))) : 1,
      ),
    );
  }, [open, project]);

  if (!open || !project) return null;

  const update = (index: number, value: number) => {
    setOpacities((current) => current.map((item, itemIndex) => (itemIndex === index ? value : item)));
  };

  const save = () => {
    onSave({ ...project, trackOpacities: opacities.map((value) => Math.min(1, Math.max(0, value))) });
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
      <section className="help-dialog track-properties-dialog" role="dialog" aria-modal="true" aria-label="视频轨属性">
        <header>
          <div>
            <span className="help-kicker">TRACK PROPERTIES</span>
            <h2>视频轨属性</h2>
          </div>
          <button type="button" className="help-close" onClick={onClose} aria-label="关闭视频轨属性">×</button>
        </header>
        <div className="track-opacity-list">
          {VIDEO_TRACKS.map((track) => (
            <div className="track-opacity-row" key={track.index}>
              <div className="track-opacity-meta">
                <strong>{track.label}</strong>
                <span>{track.note}</span>
              </div>
              <input
                type="range"
                min={0}
                max={100}
                step={1}
                value={Math.round(opacities[track.index] * 100)}
                aria-label={`${track.label}不透明度`}
                onChange={(event) => update(track.index, Number(event.target.value) / 100)}
              />
              <output>{Math.round(opacities[track.index] * 100)}%</output>
            </div>
          ))}
        </div>
        <p className="sequence-settings-hint">
          <Layers3 size={12} /> 轨道不透明度同时作用于预览和成片；单个片段的不透明度仍可在属性检查器中叠加。
        </p>
        <footer>
          <button type="button" className="secondary-button" onClick={() => setOpacities([1, 1, 1])}>
            恢复全部
          </button>
          <button type="button" className="primary-button" onClick={save}>保存轨道属性</button>
        </footer>
      </section>
    </div>
  );
}
