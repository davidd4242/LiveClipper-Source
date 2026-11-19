import {
  useRef,
  useState,
  type CSSProperties,
  type DragEvent as ReactDragEvent,
  type MouseEvent as ReactMouseEvent,
} from 'react';
import {
  ArrowLeftRight,
  ChevronDown,
  FileText,
  FileVideo,
  Film,
  Folder,
  Plus,
  Search,
  Sparkles,
  Volume2,
} from 'lucide-react';
import type { AudioEffectType, ClipDto, EditorTransition } from '../../../api/types';
import { getVideoUrl } from '../../../api/client';
import { VideoThumbnail } from '../../../components/VideoThumbnail';
import { clipFileName, clipTitle, formatClock } from '../project';
import { AUDIO_EFFECT_SPECS, AUDIO_EFFECT_TYPES } from '../audioEffects';
import { copyTextToClipboard } from '../clipboard';
import {
  ASSET_DRAG_MIME,
  TRANSITION_DRAG_MIME,
  beginAssetDrag,
  beginTransitionDrag,
  endAssetDrag,
  endTransitionDrag,
} from '../assetDrag';
import { EditorContextMenu, type ContextMenuItem } from './EditorContextMenu';

/** P1b：左栏收敛为单一侧栏，内容区按 section 切换（项目 / 素材 / 效果）。 */
type SideView = 'project' | 'assets' | 'effects';

interface ProjectColumnProps {
  clips: ClipDto[];
  selectedClipId: string | null;
  transcriptSegments: number;
  duration: number;
  width: number;
  height: number;
  fps: number;
  onSelectClip: (clipId: string) => void;
  onOpenAi: (tab: 'transcript' | 'candidates' | 'chapters') => void;
  onExport: () => void;
  onLocateSource: (time: number) => void;
  onAddToTimeline: (clipId: string) => void;
  onNotify: (message: string) => void;
  /** 时间线是否有可作用的选中片段（效果库据此决定音频/淡化是否可点）。 */
  hasTimelineSelection: boolean;
  /** 效果库顶部显示的作用目标（轨道 + 序列时间），无选中时为 null。 */
  selectedTimelineLabel: string | null;
  onApplyAudioEffect: (type: AudioEffectType) => void;
  onApplyLibraryFade: (kind: 'in' | 'out' | 'both') => void;
  onApplyLibraryTransition: (kind: EditorTransition['kind']) => void;
  onOpenEffectControls: () => void;
  /** P1.7：剪辑页导入素材（POST /api/studio/import-files），成功后由父侧刷新素材列表。 */
  onImportFiles: (files: File[]) => void | Promise<void>;
}

const thumbPairs = [
  ['#7e8b9b', '#263746'],
  ['#8e7b68', '#3f2c29'],
  ['#668b84', '#1f3b3e'],
  ['#8b7290', '#36243d'],
  ['#aa8a60', '#47311d'],
];

function dateLabel(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return `${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

function thumbStyle(index: number): CSSProperties {
  const colors = thumbPairs[index % thumbPairs.length];
  return {
    '--thumb-a': colors[0],
    '--thumb-b': colors[1],
  } as CSSProperties;
}

export function ProjectColumn({
  clips,
  selectedClipId,
  transcriptSegments,
  duration,
  width,
  height,
  fps,
  onSelectClip,
  onOpenAi,
  onExport,
  onLocateSource,
  onAddToTimeline,
  onNotify,
  hasTimelineSelection,
  selectedTimelineLabel,
  onApplyAudioEffect,
  onApplyLibraryFade,
  onApplyLibraryTransition,
  onOpenEffectControls,
  onImportFiles,
}: ProjectColumnProps) {
  const [query, setQuery] = useState('');
  const [folders, setFolders] = useState({ main: true, subtitle: true });
  const [mediaLayout, setMediaLayout] = useState<'grid' | 'list'>('grid');
  const [sortBy, setSortBy] = useState<'date' | 'name'>('date');
  const [sideView, setSideView] = useState<SideView>('project');
  const importInputRef = useRef<HTMLInputElement | null>(null);
  const [infoOpen, setInfoOpen] = useState(false);
  const [assetMenu, setAssetMenu] = useState<{ x: number; y: number; clipId: string } | null>(null);
  const [effectsQuery, setEffectsQuery] = useState('');
  const [effectsCategory, setEffectsCategory] = useState<'all' | 'video' | 'audio' | 'transition'>('all');

  const selected = clips.find((clip) => clip.id === selectedClipId) ?? null;
  const projectName = selected ? clipTitle(selected) : '未选择素材';
  const filtered = clips.filter((clip) => {
    const needle = query.trim().toLowerCase();
    if (!needle) return true;
    return clipTitle(clip).toLowerCase().includes(needle) || clipFileName(clip).toLowerCase().includes(needle);
  });
  const visible = [...filtered].sort((left, right) => {
    if (sortBy === 'name') return clipTitle(left).localeCompare(clipTitle(right), 'zh-CN');
    return new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime();
  });
  const toggleFolder = (folder: 'main' | 'subtitle') => {
    setFolders((current) => ({ ...current, [folder]: !current[folder] }));
  };

  const menuClip = assetMenu ? clips.find((clip) => clip.id === assetMenu.clipId) ?? null : null;

  const effectsCategories: Array<{ id: 'all' | 'video' | 'audio' | 'transition'; label: string }> = [
    { id: 'all', label: '全部' },
    { id: 'video', label: '视频' },
    { id: 'audio', label: '音频' },
    { id: 'transition', label: '转场' },
  ];

  const effectLibrary: Array<{
    id: string;
    category: 'video' | 'audio' | 'transition';
    label: string;
    description: string;
    disabled: boolean;
    /** 转场卡：可以拖到时间线剪点上落点。 */
    transitionKind?: EditorTransition['kind'];
    apply: () => void;
  }> = [
    {
      id: 'fade-in',
      category: 'video',
      label: '淡入',
      description: '片段首 0.5 秒从黑场淡入',
      disabled: !hasTimelineSelection,
      apply: () => onApplyLibraryFade('in'),
    },
    {
      id: 'fade-out',
      category: 'video',
      label: '淡出',
      description: '片段尾 0.5 秒淡出到黑场',
      disabled: !hasTimelineSelection,
      apply: () => onApplyLibraryFade('out'),
    },
    {
      id: 'fade-both',
      category: 'video',
      label: '淡入淡出',
      description: '首尾各 0.5 秒，适合单段素材',
      disabled: !hasTimelineSelection,
      apply: () => onApplyLibraryFade('both'),
    },
    ...AUDIO_EFFECT_TYPES.map((type) => ({
      id: `audio-${type}`,
      category: 'audio' as const,
      label: AUDIO_EFFECT_SPECS[type].label,
      description: `${AUDIO_EFFECT_SPECS[type].filter}：${AUDIO_EFFECT_SPECS[type].summary}`,
      disabled: !hasTimelineSelection,
      apply: () => onApplyAudioEffect(type),
    })),
    ...(['cross-dissolve', 'dip-to-black', 'dip-to-white'] as Array<EditorTransition['kind']>).map((kind) => ({
      id: `transition-${kind}`,
      transitionKind: kind,
      category: 'transition' as const,
      label: kind === 'cross-dissolve' ? '交叉淡化' : kind === 'dip-to-black' ? '闪黑' : '闪白',
      description:
        kind === 'cross-dissolve'
          ? '相邻两段重叠溶解，最常用的剪点转场'
          : kind === 'dip-to-black'
            ? '经过黑场再切到下一段，用于时间跳跃'
            : '经过白场再切到下一段，用于闪光或回忆',
      disabled: false,
      apply: () => onApplyLibraryTransition(kind),
    })),
  ];

  const visibleEffects = effectLibrary.filter((item) => {
    if (effectsCategory !== 'all' && item.category !== effectsCategory) return false;
    const needle = effectsQuery.trim().toLowerCase();
    if (!needle) return true;
    return (
      item.label.toLowerCase().includes(needle) || item.description.toLowerCase().includes(needle)
    );
  });

  const openAssetMenu = (event: ReactMouseEvent<HTMLButtonElement>, clip: ClipDto) => {
    event.preventDefault();
    event.stopPropagation();
    setAssetMenu({ x: event.clientX, y: event.clientY, clipId: clip.id });
  };

  /** 素材拖到时间线：只带 clipId/标题/已知时长，真正落点由时间线决定。 */
  const handleAssetDragStart = (event: ReactDragEvent<HTMLButtonElement>, clip: ClipDto) => {
    const knownDuration = clip.id === selectedClipId && duration > 0 ? duration : null;
    const payload = beginAssetDrag({ clipId: clip.id, title: clipTitle(clip), duration: knownDuration });
    event.dataTransfer.setData(ASSET_DRAG_MIME, JSON.stringify(payload));
    event.dataTransfer.setData('text/plain', payload.title);
    event.dataTransfer.effectAllowed = 'copy';
  };

  const handleAssetDragEnd = () => {
    endAssetDrag();
  };

  /** 转场卡拖到时间线剪点：载荷只带 kind/label，具体落点由 Timeline 算。 */
  const handleTransitionDragStart = (
    event: ReactDragEvent<HTMLButtonElement>,
    kind: EditorTransition['kind'],
    label: string,
  ) => {
    const payload = beginTransitionDrag({ kind, label });
    event.dataTransfer.setData(TRANSITION_DRAG_MIME, JSON.stringify(payload));
    event.dataTransfer.setData('text/plain', label);
    event.dataTransfer.effectAllowed = 'copy';
  };

  const handleTransitionDragEnd = () => {
    endTransitionDrag();
  };

  const buildAssetMenuItems = (clip: ClipDto): ContextMenuItem[] => [
    { kind: 'item', label: '在编辑器中打开', onSelect: () => onSelectClip(clip.id) },
    { kind: 'item', label: '加入时间线末尾', onSelect: () => onAddToTimeline(clip.id) },
    {
      kind: 'item',
      label: '显示素材信息',
      onSelect: () => {
        onSelectClip(clip.id);
        setInfoOpen(true);
      },
    },
    { kind: 'separator' },
    {
      kind: 'item',
      label: '复制素材路径',
      onSelect: () => {
        void copyTextToClipboard(clip.path).then((copied) =>
          onNotify(
            copied
              ? `已复制素材路径：${clipFileName(clip)}`
              : '复制素材路径失败，请检查剪贴板权限',
          ),
        );
      },
    },
  ];

  const projectTree = (
    <>
      <label className="searchbar">
        <Search size={11} aria-hidden="true" />
        <input
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="搜索素材"
          aria-label="搜索素材"
        />
      </label>
      <div className="project-meta">
        <b>素材箱</b>
        <span>·</span>
        <span>{clips.length} 个视频</span>
        <span>·</span>
        <span>显示 {filtered.length}</span>
      </div>
      <div className="project-tree">
        <button
          type="button"
          className="tree-row depth-0 tree-folder"
          aria-expanded={folders.main}
          onClick={() => toggleFolder('main')}
        >
          <span className="tree-icon folder-icon" aria-hidden="true">
            <Folder size={11} />
            <ChevronDown size={8} className={`tree-chevron${folders.main ? '' : ' is-collapsed'}`} />
          </span>
          <span className="tree-label">01_主素材</span>
          <span className="tree-duration">{clips.length}</span>
        </button>
        {folders.main
          ? filtered.map((clip) => (
              <button
                key={clip.id}
                type="button"
                className={`tree-row depth-1${clip.id === selectedClipId ? ' is-selected' : ''}`}
                onClick={() => onSelectClip(clip.id)}
                onContextMenu={(event) => openAssetMenu(event, clip)}
                draggable
                onDragStart={(event) => handleAssetDragStart(event, clip)}
                onDragEnd={handleAssetDragEnd}
                title={clip.path}
              >
                <FileVideo size={11} className="tree-file video" aria-hidden="true" />
                <span className="tree-label">{clipTitle(clip)}</span>
                <span className="tree-duration">
                  {clip.id === selectedClipId ? formatClock(duration) : dateLabel(clip.createdAt)}
                </span>
              </button>
            ))
          : null}
        {folders.main && filtered.length === 0 ? <p className="empty-state">没有匹配的素材。</p> : null}
        {transcriptSegments > 0 ? (
          <>
            <button
              type="button"
              className="tree-row depth-0 tree-folder"
              aria-expanded={folders.subtitle}
              onClick={() => toggleFolder('subtitle')}
            >
              <span className="tree-icon folder-icon" aria-hidden="true">
                <Folder size={11} />
                <ChevronDown size={8} className={`tree-chevron${folders.subtitle ? '' : ' is-collapsed'}`} />
              </span>
              <span className="tree-label">02_字幕</span>
            </button>
            {folders.subtitle ? (
              <button
                type="button"
                className="tree-row depth-1 tree-file-row"
                onClick={() => onOpenAi('transcript')}
                title="打开转写"
              >
                <FileText size={11} className="tree-file subtitle" aria-hidden="true" />
                <span className="tree-label">内嵌转写 · 中文</span>
                <span className="tree-duration">{transcriptSegments} 段</span>
              </button>
            ) : null}
          </>
        ) : null}
      </div>
      <div className="ed-side-hint">
        {clips.length} 个视频 · 显示 {filtered.length} · {transcriptSegments} 段字幕
      </div>
    </>
  );

  const assetsView = (
    <div className="ed-side-view">
      <label className="ed-side-search">
        <Search size={12} aria-hidden="true" />
        <input
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="搜索素材"
          aria-label="搜索素材"
        />
      </label>
      <div className="ed-side-toolbar">
        <button
          type="button"
          className={mediaLayout === 'grid' ? 'is-active' : ''}
          aria-pressed={mediaLayout === 'grid'}
          onClick={() => setMediaLayout('grid')}
        >
          缩略图
        </button>
        <button
          type="button"
          className={mediaLayout === 'list' ? 'is-active' : ''}
          aria-pressed={mediaLayout === 'list'}
          onClick={() => setMediaLayout('list')}
        >
          列表
        </button>
        <button
          type="button"
          className="ed-side-sort"
          title="切换排序方式"
          onClick={() => setSortBy((current) => (current === 'date' ? 'name' : 'date'))}
        >
          {sortBy === 'date' ? '按时间' : '按名称'}
        </button>
      </div>
      <div className={`media-grid is-${mediaLayout}`}>
        {visible.map((clip, index) => (
          <button
            key={clip.id}
            type="button"
            className={`media-card${clip.id === selectedClipId ? ' is-selected' : ''}`}
            onClick={() => onSelectClip(clip.id)}
            onContextMenu={(event) => openAssetMenu(event, clip)}
            draggable
            onDragStart={(event) => handleAssetDragStart(event, clip)}
            onDragEnd={handleAssetDragEnd}
            title={clip.path}
          >
            <span className="media-thumb" style={thumbStyle(index)}>
              <VideoThumbnail src={getVideoUrl(clip.id)} alt="" seekRatio={0.18} />
              <span className="media-duration">
                {clip.id === selectedClipId ? formatClock(duration) : dateLabel(clip.createdAt)}
              </span>
            </span>
            <span className="media-caption">
              <FileVideo size={9} aria-hidden="true" />
              <span>{clipTitle(clip)}</span>
            </span>
          </button>
        ))}
      </div>
      {visible.length === 0 ? <p className="empty-state">没有匹配的素材。</p> : null}
      <div className="ed-side-hint">
        {clips.length} 项 · 显示 {filtered.length}
      </div>
    </div>
  );

  const effectsView = (
    <div className="ed-side-view">
      <label className="ed-side-search">
        <Search size={12} aria-hidden="true" />
        <input
          value={effectsQuery}
          onChange={(event) => setEffectsQuery(event.target.value)}
          placeholder="搜索效果、转场"
          aria-label="搜索效果"
        />
      </label>
      <div className="fx-filters" role="tablist" aria-label="效果分类">
        {effectsCategories.map((category) => (
          <button
            key={category.id}
            type="button"
            role="tab"
            aria-selected={effectsCategory === category.id}
            className={`fx-filter${effectsCategory === category.id ? ' is-active' : ''}`}
            onClick={() => setEffectsCategory(category.id)}
          >
            {category.label}
          </button>
        ))}
      </div>
      <div className="fx-list">
        {visibleEffects.map((item) => (
          <button
            key={item.id}
            type="button"
            className="fx-card"
            data-fx-id={item.id}
            disabled={item.disabled}
            draggable={Boolean(item.transitionKind) && !item.disabled}
            title={
              item.disabled
                ? '先在时间线选中一个片段'
                : item.transitionKind
                  ? `${item.label}：${item.description}（拖到剪点可直接应用）`
                  : `${item.label}：${item.description}`
            }
            onDragStart={(event) => {
              if (item.transitionKind) handleTransitionDragStart(event, item.transitionKind, item.label);
            }}
            onDragEnd={item.transitionKind ? handleTransitionDragEnd : undefined}
            onClick={item.apply}
          >
            <span className="fx-icon" aria-hidden="true">
              {item.category === 'audio' ? (
                <Volume2 size={12} />
              ) : item.category === 'transition' ? (
                <ArrowLeftRight size={12} />
              ) : (
                <Sparkles size={12} />
              )}
            </span>
            <span className="fx-body">
              <b>{item.label}</b>
              <small>{item.description}</small>
            </span>
            <span className="fx-add" aria-hidden="true">
              <Plus size={11} />
            </span>
          </button>
        ))}
        {visibleEffects.length === 0 ? <p className="empty-state">没有匹配的效果。</p> : null}
      </div>
      <div className="fx-target">
        {selectedTimelineLabel ? (
          <>
            作用目标：<b>{selectedTimelineLabel}</b>
          </>
        ) : (
          '未选中片段：转场会作用到播放头附近的剪点；音频效果与淡化需先选中片段。'
        )}
      </div>
      <div className="ed-side-hint">
        <button type="button" className="ed-side-link" onClick={onOpenEffectControls}>
          效果控件
        </button>
        <button type="button" className="ed-side-link" onClick={onExport}>
          导出设置
        </button>
        <span>点击卡片应用</span>
      </div>
    </div>
  );

  return (
    <aside className="project-column ed-side" aria-label="项目侧栏">
      <nav className="ed-side-nav" aria-label="项目结构">
        <div className="ed-side-section">
          <span>项目</span>
          <button type="button" className="ed-side-add" aria-label="导入素材" title="导入素材" onClick={() => importInputRef.current?.click()}>
            <Plus size={12} />
          </button>
          <input
            ref={importInputRef}
            type="file"
            accept="video/*,audio/*,.srt,.vtt"
            multiple
            hidden
            onChange={(event) => {
              const files = Array.from(event.target.files ?? []);
              event.target.value = '';
              if (files.length > 0) void onImportFiles(files);
            }}
          />
        </div>
        <button
          type="button"
          className={`ed-side-row${sideView === 'project' ? ' is-active' : ''}`}
          aria-current={sideView === 'project' ? 'true' : undefined}
          onClick={() => setSideView('project')}
          title={projectName}
        >
          <span className="ed-side-dot" aria-hidden="true" />
          <span className="ed-side-row-label">{projectName}</span>
          <span className="ed-side-row-meta">{clips.length}</span>
        </button>

        <div className="ed-side-section"><span>序列</span></div>
        <button
          type="button"
          className={`ed-side-row${sideView === 'assets' ? ' is-active' : ''}`}
          aria-current={sideView === 'assets' ? 'true' : undefined}
          onClick={() => setSideView('assets')}
        >
          <Film size={12} className="ed-side-row-icon" aria-hidden="true" />
          <span className="ed-side-row-label">{`序列 01 · ${height || '—'}p${fps || '—'}`}</span>
          <span className="ed-side-row-meta">{formatClock(duration)}</span>
        </button>

        <div className="ed-side-section"><span>素材</span></div>
        <button
          type="button"
          className={`ed-side-row${sideView === 'assets' ? ' is-active' : ''}`}
          aria-current={sideView === 'assets' ? 'true' : undefined}
          onClick={() => setSideView('assets')}
        >
          <FileVideo size={12} className="ed-side-row-icon" aria-hidden="true" />
          <span className="ed-side-row-label">视频</span>
          <span className="ed-side-row-meta">{clips.length}</span>
        </button>
        <button
          type="button"
          className={`ed-side-row${sideView === 'effects' && effectsCategory === 'audio' ? ' is-active' : ''}`}
          onClick={() => {
            setEffectsCategory('audio');
            setSideView('effects');
          }}
        >
          <Volume2 size={12} className="ed-side-row-icon" aria-hidden="true" />
          <span className="ed-side-row-label">音频效果</span>
          <span className="ed-side-row-meta">{formatClock(duration)}</span>
        </button>
        <button
          type="button"
          className={`ed-side-row${sideView === 'effects' ? ' is-active' : ''}`}
          aria-current={sideView === 'effects' ? 'true' : undefined}
          onClick={() => setSideView('effects')}
        >
          <Sparkles size={12} className="ed-side-row-icon" aria-hidden="true" />
          <span className="ed-side-row-label">效果</span>
          <span className="ed-side-row-meta">{visibleEffects.length}</span>
        </button>
        <button type="button" className="ed-side-row" onClick={() => onOpenAi('transcript')}>
          <FileText size={12} className="ed-side-row-icon" aria-hidden="true" />
          <span className="ed-side-row-label">字幕</span>
          <span className="ed-side-row-meta">{transcriptSegments}</span>
        </button>
      </nav>

      <div className="ed-side-content">
        {sideView === 'project' ? <div className="ed-side-view">{projectTree}</div> : null}
        {sideView === 'assets' ? assetsView : null}
        {sideView === 'effects' ? effectsView : null}
      </div>

      <footer className="ed-side-foot">
        <button type="button" className="ed-side-foot-btn" onClick={() => setInfoOpen(true)} title="查看当前素材信息">
          信息
        </button>
        <span>{width || '—'}×{height || '—'}</span>
        <span>{fps || '—'} fps</span>
        <span className="ed-side-foot-end">API 在线</span>
      </footer>

      {infoOpen ? (
        <div
          className="help-backdrop"
          role="presentation"
          onPointerDown={(event) => {
            if (event.target === event.currentTarget) setInfoOpen(false);
          }}
        >
          <section className="help-dialog info-dialog" role="dialog" aria-modal="true" aria-label="素材信息">
            <header>
              <div>
                <span className="help-kicker">MEDIA INFO</span>
                <h2>素材信息</h2>
              </div>
              <button type="button" className="help-close" onClick={() => setInfoOpen(false)} aria-label="关闭">
                ×
              </button>
            </header>
            {selected ? (
              <dl className="info-list">
                <div><dt>名称</dt><dd>{projectName}</dd></div>
                <div><dt>文件</dt><dd title={selected.path}>{clipFileName(selected)}</dd></div>
                <div><dt>路径</dt><dd className="info-path" title={selected.path}>{selected.path}</dd></div>
                <div><dt>分辨率</dt><dd>{width || '—'} × {height || '—'}</dd></div>
                <div><dt>帧率 / 时长</dt><dd>{fps || '—'} fps · {formatClock(duration)}</dd></div>
                <div><dt>转写</dt><dd>{transcriptSegments} 段</dd></div>
              </dl>
            ) : (
              <p className="empty-state">请选择一个素材。</p>
            )}
            <footer>
              <button type="button" className="secondary-button" onClick={() => onLocateSource(0)}>
                定位素材开头
              </button>
              <button type="button" className="primary-button" onClick={() => setInfoOpen(false)}>
                关闭
              </button>
            </footer>
          </section>
        </div>
      ) : null}

      {assetMenu && menuClip ? (
        <EditorContextMenu
          x={assetMenu.x}
          y={assetMenu.y}
          title={`素材 · ${clipTitle(menuClip)}`}
          ariaLabel="素材操作菜单"
          items={buildAssetMenuItems(menuClip)}
          onClose={() => setAssetMenu(null)}
        />
      ) : null}
    </aside>
  );
}
