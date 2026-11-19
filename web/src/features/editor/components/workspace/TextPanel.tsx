import type { TimelineItem } from '../../../../api/types';
import { trackOf } from '../../project';
import { Field, Note, Section, Slider } from './ui';
import { clamp, type WorkspaceProject } from './types';

const POSITION_LABELS: Record<'top' | 'middle' | 'bottom', string> = { top: '顶部', middle: '中部', bottom: '底部' };

type TitlePreset = {
  id: string;
  label: string;
  detail: string;
  sample: string;
  patch: { titleSize: number; titleColor: string; titleX: number; titleY: number };
};

const TITLE_PRESETS: TitlePreset[] = [
  {
    id: 'center',
    label: '居中主标题',
    detail: '64 px · 白色 · 画面中央',
    sample: '主标题',
    patch: { titleSize: 64, titleColor: '#FFFFFF', titleX: 0.5, titleY: 0.5 },
  },
  {
    id: 'lower',
    label: '底部字幕条',
    detail: '36 px · 白色 · 底部居中',
    sample: '一句字幕',
    patch: { titleSize: 36, titleColor: '#FFFFFF', titleX: 0.5, titleY: 0.86 },
  },
  {
    id: 'hook',
    label: '开场钩子',
    detail: '72 px · 暖黄 · 偏上',
    sample: '开场标题',
    patch: { titleSize: 72, titleColor: '#FFC24D', titleX: 0.5, titleY: 0.38 },
  },
  {
    id: 'corner',
    label: '角落标注',
    detail: '28 px · 浅蓝 · 右上角',
    sample: '标注',
    patch: { titleSize: 28, titleColor: '#A8D6FF', titleX: 0.86, titleY: 0.1 },
  },
];

type TextPanelProps = {
  project: WorkspaceProject | null;
  selectedItem: TimelineItem | null;
  transcriptCount: number;
  onPatch: (patch: Partial<TimelineItem>, label: string) => void;
  onOpenAi: (tab: 'candidates' | 'transcript' | 'chapters') => void;
};

/**
 * 文本窗口：标题预设与文字/颜色/字号/位置写入 V3 图形片段；字幕样式只读展示并可跳转转写。
 */
export function TextPanel({ project, selectedItem, transcriptCount, onPatch, onOpenAi }: TextPanelProps) {
  const isTitleClip = Boolean(selectedItem && trackOf(selectedItem) === 3);
  const titleText = selectedItem?.titleText ?? '';
  const titleSize = clamp(selectedItem?.titleSize ?? 48, 12, 160);
  const titleColor = selectedItem?.titleColor ?? '#FFFFFF';
  const titleX = clamp(selectedItem?.titleX ?? 0.5, 0, 1);
  const titleY = clamp(selectedItem?.titleY ?? 0.82, 0, 1);
  const subtitlePosition = project?.subtitlePosition ?? 'bottom';

  const applyPreset = (preset: TitlePreset) => {
    if (!isTitleClip) return;
    onPatch(
      { ...preset.patch, titleText: titleText.trim() ? titleText : preset.sample },
      `标题预设：${preset.label}`,
    );
  };

  return (
    <div className="wsx-stack">
      <Note>
        标题写在 V3 图形片段上，会叠加到预览与成片。文字、颜色、字号与位置会立即写入工程；字幕样式来自序列设置，这里只读。
      </Note>

      <Section title="标题" hint={isTitleClip ? 'V3 图形轨' : '需要选中 V3 片段'}>
        {!isTitleClip ? (
          <Note tone="warning">
            当前没有选中 V3 图形片段。先在时间线上把片段放到 V3（或选中 V3 片段），再编辑标题。
          </Note>
        ) : null}
        <label className="window-notes">
          <span>标题文本（最多 120 字）</span>
          <textarea
            rows={2}
            maxLength={120}
            value={titleText}
            disabled={!isTitleClip}
            placeholder={isTitleClip ? '输入标题文字…' : '先在 V3 选中标题片段'}
            onChange={(event) => onPatch({ titleText: event.target.value || null }, '修改标题文本')}
          />
        </label>
        <div className="wsx-toolbar">
          <span className="wsx-label">预设</span>
          <div className="wsx-preset-grid wsx-preset-grid--two">
            {TITLE_PRESETS.map((preset) => (
              <button
                key={preset.id}
                type="button"
                className="wsx-preset"
                disabled={!isTitleClip}
                onClick={() => applyPreset(preset)}
              >
                <strong>{preset.label}</strong>
                <span>{preset.detail}</span>
              </button>
            ))}
          </div>
        </div>
        <label className="window-notes window-color-row">
          <span>文字颜色</span>
          <input
            type="color"
            value={titleColor}
            disabled={!isTitleClip}
            onChange={(event) => onPatch({ titleColor: event.target.value.toUpperCase() }, '修改标题颜色')}
          />
        </label>
        <Slider
          label="字号"
          value={titleSize}
          min={12}
          max={160}
          step={1}
          suffix=" px"
          disabled={!isTitleClip}
          onChange={(value) => onPatch({ titleSize: value }, '修改标题字号')}
        />
        <div className="window-two">
          <Slider
            label="水平位置"
            value={titleX}
            min={0}
            max={1}
            step={0.01}
            suffix=""
            disabled={!isTitleClip}
            onChange={(value) => onPatch({ titleX: value }, '修改标题水平位置')}
          />
          <Slider
            label="垂直位置"
            value={titleY}
            min={0}
            max={1}
            step={0.01}
            suffix=""
            disabled={!isTitleClip}
            onChange={(value) => onPatch({ titleY: value }, '修改标题垂直位置')}
          />
        </div>
        <div className="wsx-title-preview" aria-hidden="true">
          <span
            className="wsx-title-preview-text"
            style={{
              color: titleColor,
              left: `${titleX * 100}%`,
              top: `${titleY * 100}%`,
              fontSize: `${Math.max(14, Math.min(40, titleSize * 0.5))}px`,
            }}
          >
            {titleText || '标题预览'}
          </span>
        </div>
      </Section>

      <Section title="字幕样式" hint={transcriptCount ? `${transcriptCount} 段转写` : '还没有转写'}>
        <Field label="烧录字幕" value={project?.burnSubtitles ? '是' : '否'} />
        <Field label="字体" value={project?.subtitleFont || '默认'} />
        <Field label="字号" value={`${project?.subtitleSize ?? 34}`} />
        <Field label="颜色 / 位置" value={`${project?.subtitleColor ?? '#FFFFFF'} · ${POSITION_LABELS[subtitlePosition]}`} />
        <Field label="描边 / 边距" value={`${project?.subtitleOutline ?? 0} / ${project?.subtitleMargin ?? 0}`} />
        <div className="window-actions">
          <button type="button" className="primary-button" onClick={() => onOpenAi('transcript')}>
            打开字幕与转写
          </button>
        </div>
        <p className="wsx-hint">字幕字体、字号与位置在「序列设置」中修改，此面板只读展示。</p>
      </Section>
    </div>
  );
}
