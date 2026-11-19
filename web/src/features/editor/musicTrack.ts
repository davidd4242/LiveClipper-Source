import type { EditorProject } from '../../api/types';
import type { MusicJobState, MusicLibraryAsset } from '../../api/music';

/**
 * 剪辑页「配乐」卡片的纯数据层。
 *
 * 工程级的配乐是 EditorProject.musicAssetId（整场 BGM），渲染时后端用
 * `Find(plan.MusicAssetId, "music")` 取文件并配 sidechaincompress 做 ducking；
 * 这里只负责把素材列表/开关值整理成 UI 需要的形状，并把改动落成可交给
 * updateProject 的新工程对象（保持不可变，方便撤销栈记录）。
 */

export interface MusicTrackOption {
  id: string;
  name: string;
  style: string;
  durationSeconds: number | null;
  builtIn: boolean;
}

/** 只保留 kind=music 的素材；duration 取分析结果，没有就是 null（UI 显示“时长未知”）。 */
export function musicOptions(assets: MusicLibraryAsset[] | null | undefined): MusicTrackOption[] {
  if (!assets || assets.length === 0) return [];
  return assets
    .filter((asset) => asset && asset.kind === 'music' && typeof asset.id === 'string' && asset.id)
    .map((asset) => ({
      id: asset.id,
      name: (asset.name ?? '').trim() || '未命名配乐',
      style: (asset.style ?? '').trim(),
      durationSeconds:
        typeof asset.analysis?.durationSeconds === 'number' && Number.isFinite(asset.analysis.durationSeconds)
          ? asset.analysis.durationSeconds
          : null,
      builtIn: Boolean(asset.builtIn),
    }));
}

export function findMusicOption(options: MusicTrackOption[], assetId: string | null | undefined): MusicTrackOption | null {
  if (!assetId) return null;
  return options.find((option) => option.id === assetId) ?? null;
}

/** 卡片标题右侧的当前配乐名；工程里挂了但素材列表还没加载时退化成 id 片段。 */
export function currentMusicLabel(project: EditorProject | null, options: MusicTrackOption[]): string {
  const id = project?.musicAssetId ?? null;
  if (!id) return '未选择配乐';
  return findMusicOption(options, id)?.name ?? `已选素材 ${id.slice(0, 8)}`;
}

export function selectMusicPatch(project: EditorProject, assetId: string | null): EditorProject {
  return { ...project, musicAssetId: assetId };
}

export function duckingPatch(project: EditorProject, duck: boolean): EditorProject {
  return { ...project, duckMusic: duck };
}

/** 滑杆是 0..100 的百分比，工程里存 0..1 的增益系数。 */
export function volumePercent(project: EditorProject | null): number {
  const value = project?.musicVolume;
  if (typeof value !== 'number' || !Number.isFinite(value)) return 0;
  return Math.round(Math.min(1, Math.max(0, value)) * 100);
}

export function volumePatch(project: EditorProject, percent: number): EditorProject {
  const clamped = Math.min(100, Math.max(0, Number.isFinite(percent) ? percent : 0));
  return { ...project, musicVolume: Math.round((clamped / 100) * 100) / 100 };
}

/** 生成任务的单行状态文案；完成/失败/进行中各一句，避免 UI 里散落判断。 */
export function musicJobLine(job: MusicJobState | null): string {
  if (!job) return '';
  if (job.status === 'completed') return job.detail?.trim() || '配乐已生成';
  if (job.status === 'failed') return job.error?.trim() || job.detail?.trim() || '生成失败';
  if (job.status === 'cancelled') return '已取消生成';
  const progress = Number.isFinite(job.progress) ? Math.min(100, Math.max(0, Math.round(job.progress))) : 0;
  return `${job.status === 'queued' ? '排队中' : '生成中'} ${progress}%${job.detail?.trim() ? ` · ${job.detail.trim()}` : ''}`;
}

/** 生成请求的固定默认值：剪辑页不做音乐风格编辑器，只给「氛围 + 时长」。 */
export const MUSIC_DURATIONS = [15, 30, 60] as const;

export function musicGenerateBody(modelId: string, allowDownload: boolean, durationSeconds: number) {
  return {
    modelId,
    genre: 'cinematic',
    style: 'warm',
    content: '温暖舒缓的纯音乐，为视频画面和人声提供自然的背景衬托',
    theme: '背景配乐',
    durationSeconds: Math.round(durationSeconds),
    allowDownload,
  };
}

export function formatMusicDuration(seconds: number | null): string {
  if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds <= 0) return '时长未知';
  const total = Math.round(seconds);
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  return minutes > 0 ? `${minutes}:${String(rest).padStart(2, '0')}` : `0:${String(rest).padStart(2, '0')}`;
}
